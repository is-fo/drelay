package networking.log;

import networking.RingBuffer;
import networking.util.Json;

import java.io.BufferedWriter;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.atomic.AtomicLong;

/**
 * The relay's observability sink: one ring for the dashboard, one JSONL file per run, and the
 * legacy text line on stdout so the existing Python tools keep working unchanged.
 *
 * <h2>Why a structured log at all</h2>
 *
 * <p>A play session cannot be watched live and cannot be reproduced. Anything the relay does not
 * record is gone: the interesting moment is discovered <em>after</em> the session, from the log, and
 * it is usually one specific question - "what was my HP in the 400 ms before the escape went out,
 * and did the server acknowledge it". Answering that needs the packets as data, not as text, so this
 * class keeps the payload bytes and the decoded fields side by side.
 *
 * <h2>What is written where</h2>
 *
 * <ul>
 *   <li>{@code work/logs/events-<run>.jsonl} - <strong>every</strong> event, complete, one JSON
 *       object per line. This is the primary artifact and the one an analysis should read.</li>
 *   <li>{@code work/logs/nexus-<run>.jsonl} - the subset worth reading first when something went
 *       wrong: health readings, decisions, injections and the server's acknowledgements, in
 *       sequence order. Small enough to read in full after a session.</li>
 *   <li>{@code work/logs/relay-<run>.log} - the human-readable text stream, with the original
 *       {@code [tag dir] time len= id= Name hex} format the Python tools parse.</li>
 * </ul>
 *
 * <p>Rotation is by size with a fixed generation count, because the alternative is a session that
 * fills the disk or a log that has to be truncated by hand. {@code events-*.jsonl} keeps the newest
 * {@code ROTATIONS} generations; the nexus log is small and is kept whole per run.
 */
public final class EventLog {

    /** Payload bytes per logged packet. A longer payload is truncated and marked in {@code data}. */
    private static final int HEX_LIMIT = 4096;

    private static final int ROTATIONS = 4;

    private final Path directory;
    private final String runId;
    private final boolean jsonlEnabled;
    private final boolean textEnabled;
    private final long maxBytes;

    private final RingBuffer<Event> ring;
    private final AtomicLong sequence = new AtomicLong();
    private final AtomicLong dropped = new AtomicLong();
    private final Map<String, Long> kindCounts = new ConcurrentHashMap<>();
    private final Map<Integer, Long> gamePacketCounts = new ConcurrentHashMap<>();
    private final Map<Integer, Long> queuePacketCounts = new ConcurrentHashMap<>();

    private final Object writeLock = new Object();
    private BufferedWriter events;
    private BufferedWriter nexus;
    private BufferedWriter text;
    private long eventsBytes;
    private int eventsGeneration;
    private boolean closed;
    private String lastError;

    public EventLog(Path directory, String runId, int ringCapacity, long maxBytes) {
        this.directory = directory;
        this.runId = runId;
        this.ring = new RingBuffer<>(ringCapacity);
        this.maxBytes = Math.max(1 << 20, maxBytes);
        this.jsonlEnabled = directory != null;
        this.textEnabled = true;
        if (this.jsonlEnabled) {
            openAll();
        }
    }

    /** A log that only feeds the dashboard and stdout; used by the offline tests. */
    public static EventLog memoryOnly(int capacity) {
        return new EventLog(null, "memory", capacity, 1L << 20);
    }

    private void openAll() {
        try {
            Files.createDirectories(directory);
            events = openRotating("events", 0);
            nexus = open("nexus-" + runId + ".jsonl");
            text = open("relay-" + runId + ".log");
        } catch (IOException e) {
            lastError = "could not open the log files: " + e.getMessage();
            System.err.println("[log] " + lastError);
        }
    }

    private BufferedWriter openRotating(String prefix, int generation) throws IOException {
        this.eventsGeneration = generation;
        Path path = directory.resolve(generation == 0
                ? prefix + "-" + runId + ".jsonl"
                : prefix + "-" + runId + "." + generation + ".jsonl");
        BufferedWriter writer = open(path);
        eventsBytes = Files.exists(path) ? Files.size(path) : 0;
        return writer;
    }

    private BufferedWriter open(String name) throws IOException {
        return open(directory.resolve(name));
    }

    private BufferedWriter open(Path path) throws IOException {
        return Files.newBufferedWriter(path, StandardCharsets.UTF_8,
                StandardOpenOption.CREATE, StandardOpenOption.APPEND);
    }

    /**
     * Records one event.
     *
     * <p>The sequence number is assigned here, not by the caller, so it is globally ordered across
     * every virtual thread in the relay. That is what lets an analysis reconstruct "the health packet
     * arrived, then the decision was made, then the bytes went out" without trusting timestamps,
     * which on Windows have a coarse granularity relative to a packet's flight time.
     *
     * <p><strong>The sequence number is assigned inside the write lock, not before it.</strong> Both
     * pump threads emit concurrently, and the JSONL file is the primary artefact an analysis reads -
     * so the file's append order has to be the sequence order. Assigning the number outside the lock
     * and then writing inside it lets thread A take a lower number than thread B and still reach the
     * file after it, which produces a log whose own lines contradict its ordering field. That is
     * exactly what it did: {@code tools/test_relay_stress.py} drives both directions at once and caught
     * {@code ...seq=40, seq=41, seq=39...} in the written file.
     *
     * <p>Everything ordered by sequence therefore happens under the same monitor: the ring (which a
     * live reader queries by sequence), the counters, and the file appends. The counters are merged
     * inside the lock too - they are the numbers the dashboard shows, and a count that disagrees with
     * the log it summarises is worse than a slightly slower counter.
     */
    public Event emit(Event.Builder builder) {
        if (!jsonlEnabled) {
            // No file to order against, so the atomic counter alone is enough.
            Event event = builder.seq(sequence.incrementAndGet()).build();
            record(event);
            return event;
        }
        synchronized (writeLock) {
            Event event = builder.seq(sequence.incrementAndGet()).build();
            record(event);
            if (closed) {
                dropped.incrementAndGet();
            } else {
                writeEvents(event);
                if (isNexusInteresting(event)) {
                    try {
                        writeLine(nexus, event.toJson());
                    } catch (IOException e) {
                        lastError = "nexus log write failed: " + e.getMessage();
                    }
                }
                // The human-readable line travels with its event, under the same lock, so the text log
                // stays in the same order as the sequence numbers. It is written after the JSONL so a
                // failure to narrate can never cost the record.
                if (event.textLine != null) {
                    IO.println(event.textLine);
                    if (textEnabled) {
                        try {
                            writeLine(text, event.textLine);
                        } catch (IOException e) {
                            lastError = "text log write failed: " + e.getMessage();
                        }
                    }
                }
            }
            return event;
        }
    }

    /** The ring and the per-kind counters; both are read by sequence, so both belong to the lock. */
    private void record(Event event) {
        ring.add(event, event.seq);
        kindCounts.merge(event.kind, 1L, Long::sum);
        if (event.pktId != null && event.isPacket()) {
            String sessionName = event.session == null ? "" : event.session;
            if (sessionName.startsWith("Queue")) {
                queuePacketCounts.merge(event.pktId, 1L, Long::sum);
            } else if (sessionName.startsWith("Game")) {
                gamePacketCounts.merge(event.pktId, 1L, Long::sum);
            }
        }
    }

    /**
     * The subset that answers "why did (or didn't) it nexus".
     *
     * <p>Verbose packets are excluded on purpose. The whole game stream is in {@code events-*.jsonl};
     * this file exists so that after a session the escape story can be read top to bottom in a minute,
     * and a 30 KB {@code Update} payload would destroy that. So the filter is by packet, and it keeps
     * exactly the packets that participate in a nexus: the health reading, the client's own escape
     * attempts, the server's verdict, and the safe-area flag that decides whether an escape is even
     * meaningful.
     */
    private boolean isNexusInteresting(Event event) {
        return switch (event.kind) {
            case Event.KIND_INJECT, Event.KIND_NEXUS, Event.KIND_WORLD -> true;
            case Event.KIND_ERROR -> true;
            case Event.KIND_SESSION -> true;
            case Event.KIND_HEALTH -> true;
            case Event.KIND_PACKET -> switch (event.pkt == null ? "" : event.pkt) {
                case "HealthUpdate", "Escape", "EscapeAck", "ForcedEscape", "EscapeCastState",
                     "SafeAreaState", "MapInfo", "Reconnect" -> true;
                default -> false;
            };
            default -> false;
        };
    }

    private void writeEvents(Event event) {
        String line = event.toJson();
        try {
            writeLine(events, line);
            eventsBytes += line.length() + 1L;
            if (eventsBytes >= maxBytes) {
                rotate();
            }
        } catch (IOException e) {
            lastError = "event log write failed: " + e.getMessage();
        }
    }

    /** Closes the current generation and reopens as the next one, deleting the oldest. */
    private void rotate() {
        try {
            closeQuietly(events);
            int next = (eventsGeneration + 1) % ROTATIONS;
            Files.deleteIfExists(rotatedPath(next));
            events = openRotating("events", next);
        } catch (IOException e) {
            lastError = "event log rotation failed: " + e.getMessage();
            try {
                events = openRotating("events", eventsGeneration);
            } catch (IOException fatal) {
                jsonlEnabledError(fatal);
            }
        }
    }

    private Path rotatedPath(int generation) {
        return directory.resolve(generation == 0
                ? "events-" + runId + ".jsonl"
                : "events-" + runId + "." + generation + ".jsonl");
    }

    private void jsonlEnabledError(IOException e) {
        lastError = "event log unusable: " + e.getMessage();
    }

    /**
     * Writes a relay-level narration line - one that is not a packet, so it has no event to ride on -
     * to the human-readable text log <em>and</em> to standard output.
     *
     * <p>Public because two very different things use it: the per-packet line (which needs the packet
     * registry's description) and relay-level narration such as a retarget, which is not a packet and
     * would otherwise have nowhere to go but the console.
     *
     * <p>Both destinations are required. The console is where the operator watches a session and where
     * the verification scripts capture the relay's output from; the file is what survives a run that
     * was launched with its console closed, which is how the relay is actually used for a play
     * session. Writing only one of them would silently break one of those two workflows.
     *
     * <p>Per-packet lines do <em>not</em> come through here; they are attached to their event as
     * {@link Event#textLine} so that they are written under the sequence lock. See {@link #emit}.
     */
    public void writeTextLine(String line) {
        IO.println(line);
        if (!textEnabled) {
            return;
        }
        synchronized (writeLock) {
            if (closed) {
                return;
            }
            try {
                writeLine(text, line);
            } catch (IOException e) {
                lastError = "text log write failed: " + e.getMessage();
            }
        }
    }

    private void writeLine(BufferedWriter writer, String line) throws IOException {
        if (writer == null) {
            return;
        }
        writer.write(line);
        writer.write('\n');
        writer.flush();
    }

    public Event emitNote(String session, String note, Map<String, ?> data) {
        return emit(Event.builder(Event.KIND_NOTE)
                .session(session == null ? "relay" : session)
                .note(note)
                .putAll(data));
    }

    public Event emitError(String session, String note) {
        System.err.println("[" + session + "] " + note);
        return emit(Event.builder(Event.KIND_ERROR).session(session).note(note));
    }

    public RingBuffer<Event> ring() {
        return ring;
    }

    public long lastSequence() {
        return sequence.get();
    }

    public long dropped() {
        return dropped.get();
    }

    public Map<String, Long> kindCounts() {
        return new LinkedHashMap<>(kindCounts);
    }

    public Map<Integer, Long> gamePacketCounts() {
        return new LinkedHashMap<>(gamePacketCounts);
    }

    public Map<Integer, Long> queuePacketCounts() {
        return new LinkedHashMap<>(queuePacketCounts);
    }

    public String lastError() {
        return lastError;
    }

    public Path directory() {
        return directory;
    }

    public String runId() {
        return runId;
    }

    /** Closes every sink; safe to call twice, and called from the relay's shutdown hook. */
    public void close() {
        synchronized (writeLock) {
            if (closed) {
                return;
            }
            closed = true;
            closeQuietly(events);
            closeQuietly(nexus);
            closeQuietly(text);
        }
    }

    private void closeQuietly(BufferedWriter writer) {
        if (writer == null) {
            return;
        }
        try {
            writer.close();
        } catch (IOException ignored) {
            // shutting down; nothing useful left to do
        }
    }

    /** Truncates a payload for logging and reports whether it happened, so no data is lost silently. */
    public static String hex(byte[] payload) {
        return hex(payload, 0, payload.length);
    }

    public static String hex(byte[] payload, int offset, int length) {
        int end = Math.min(payload.length, offset + length);
        int limit = Math.min(end, offset + HEX_LIMIT);
        StringBuilder sb = new StringBuilder((limit - offset) * 2);
        for (int i = offset; i < limit; i++) {
            sb.append(String.format(Locale.ROOT, "%02X", payload[i]));
        }
        if (limit < end) {
            sb.append("..(").append(end - limit).append(" more)");
        }
        return sb.toString();
    }

    public static int hexLimit() {
        return HEX_LIMIT;
    }

    /** The counters as plain nested maps, for the dashboard's summary panel. */
    public Map<String, Object> counters() {
        Map<String, Object> out = new LinkedHashMap<>();
        out.put("lastSeq", lastSequence());
        out.put("ringSize", ring.size());
        out.put("ringCapacity", ring.capacity());
        out.put("dropped", dropped());
        out.put("kinds", kindCounts());
        out.put("gamePackets", namedCounts(gamePacketCounts, false));
        out.put("queuePackets", namedCounts(queuePacketCounts, true));
        out.put("lastError", lastError);
        return out;
    }

    /** Packet counters keyed by name, which is what a human reads and what the filters select on. */
    private Map<String, Long> namedCounts(Map<Integer, Long> counts, boolean queue) {
        Map<String, Long> out = new LinkedHashMap<>();
        counts.entrySet().stream()
                .sorted((a, b) -> Long.compare(b.getValue(), a.getValue()))
                .forEach(e -> out.put(
                        queue ? networking.packets.QPacketType.name(e.getKey())
                              : networking.packets.GmPacketType.name(e.getKey()),
                        e.getValue()));
        return out;
    }
}
