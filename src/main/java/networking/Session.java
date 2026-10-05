package networking;

import networking.log.Event;
import networking.log.EventLog;
import networking.packets.GmPacketType;
import networking.packets.Injection;
import networking.packets.PacketRegistry;
import networking.util.Fields;
import networking.util.Json;

import java.io.IOException;
import java.io.OutputStream;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicLong;

/**
 * One client↔server conversation and everything the relay knows or does about it.
 *
 * <h2>Why a session object rather than locals in {@code handleSession}</h2>
 *
 * <p>Injection makes one session's two directions depend on each other. The downstream pump (server →
 * client) is where a {@code HealthUpdate} is noticed, but the bytes have to go out on the
 * <em>upstream</em> socket; the upstream pump is where the client's {@code Hello} proves the handshake
 * finished. Both threads, plus the dashboard's HTTP threads, need the same world state, so it lives in
 * one object with a lock rather than in a captured local. That is the whole reason this class exists.
 *
 * <h2>Serialization order, in both senses</h2>
 *
 * <p>See {@link Injection} for the byte-layer order. This class owns the <strong>state</strong> order,
 * which is the part that cannot be checked against a captured packet:
 *
 * <ol>
 *   <li>{@link #clientHelloSeen()} — the client's {@code Hello} (id 55) has been seen going up. Before
 *       it, the server is mid-handshake and any extra bytes corrupt the login fields.</li>
 *   <li>{@link #onWorldEntry()} / {@code MapInfo} (id 5) — the world exists. An escape before this has
 *       no world to leave, and the server would reject it or, worse, treat it as a login-time packet.</li>
 * </ol>
 *
 * <p>{@link #injectionReady()} is the conjunction, and it is re-armed - deliberately made false again -
 * on every new world entry and every new connection, because a session that retargets to a realm is a
 * fresh conversation with a fresh handshake. Uploading the world state to the dashboard is never
 * gated: only the write path is.
 */
public final class Session {

    /** Ordered so that {@code compareTo} answers "has this session got at least this far". */
    public enum Phase {
        /** Accepted, no relay-level observation yet. */
        NEW,
        /** The client dialled and the upstream connection opened. */
        CONNECTED,
        /** The client's {@code Hello} (id 55) was seen; the server is still reading the handshake. */
        HELLO,
        /** {@code MapInfo} arrived: a world exists and client→server injection becomes legal. */
        IN_WORLD,
        /** The session ended (either direction closed). */
        CLOSED
    }

    public final long id;
    public final String tag;
    /** Label of the service this session belongs to, e.g. {@code Game_Slave}. */
    public final String routeName;
    /** Whether this session speaks the queue's id space and framing; see {@link Relay.Route#isQueue()}. */
    public final boolean queue;
    public final int listenPort;
    public final String clientAddress;
    public final String destination;
    public final long startedWallMillis;
    public final long startedMonoMillis;

    private final EventLog log;

    /**
     * The writer for the client→server direction, published by {@link Relay} once the streams exist.
     *
     * <p>This is the handle that makes injection possible at all: the rule runs on the downstream
     * (server→client) thread but has to write into the upstream socket. It is set once and read from
     * another thread, hence {@code volatile}. The writer is <em>not</em> synchronized here on purpose:
     * callers must take its own monitor, which is the same one the upstream pump uses for every
     * forwarded packet, and that is what makes an injection atomic with respect to forwarding.
     */
    private volatile OutputStream upstreamWriter;

    private Phase phase = Phase.NEW;
    private boolean injectionReady;
    private final AtomicBoolean closed = new AtomicBoolean();
    private boolean endiannessKnown;

    /**
     * The length byte order this session settled on, defaulting to big-endian.
     *
     * <p>Big-endian is the right default because it is the game service's order and the only one an
     * {@code AUTO} session ever resolves to for real game traffic (the tie-break in
     * {@code SessionState.decodeLength} picks it). It is stored here so that the <em>injection</em>
     * path frames its message the same way the forwarding path frames everything else: if a session
     * ever resolved to little-endian and the injected frame did not follow, the server would read a
     * nonsense length and desync. Volatile because the pumps and the injection write path look at it.
     */
    private volatile Relay.Endianness endianness = Relay.Endianness.BIG;

    private String clientHelloDetail;
    private String worldName;
    private String worldMap;

    private volatile boolean safeArea;
    private volatile boolean casting;

    private final AtomicLong injectedPackets = new AtomicLong();
    private final AtomicLong forwardedToServer = new AtomicLong();
    private final AtomicLong forwardedToClient = new AtomicLong();
    private final AtomicLong bytesToServer = new AtomicLong();
    private final AtomicLong bytesToClient = new AtomicLong();

    private long lastClientTime = Long.MIN_VALUE;
    private long lastClientTimeWallMillis;

    private long lastHealthWallMillis;
    private int lastMaxHealth = -1;
    private int lastHealth = -1;
    private int lastShield = -1;
    private int lastBarrier = -1;
    private int healthSamples;

    /**
     * An immutable snapshot of the newest health reading, published for readers on other threads.
     *
     * <p>The fields above are written under this object's monitor by the downstream pump. The
     * <em>upstream</em> pump wants two of them when it notices the client sending its own escape, and
     * a dashboard thread wants a consistent pair for its panel. Reading the fields directly from those
     * threads has no happens-before relationship with the writes at all, so the JMM offers no
     * guarantee about what they see - and the failure is not a crash but a silently unrelated number
     * attached to an event an analyst is reading.
     *
     * <p>A single volatile reference to an immutable record removes the question: a reader sees either
     * the previous reading or the new one, never a mixture, and never a value that predates the
     * session. It costs one allocation per health packet, which is 10 per second.
     */
    private record HealthSnapshot(long wallMillis, int maxHealth, int health, int shield, int barrier) {}

    private volatile HealthSnapshot healthSnapshot;

    /** The escape/nexus bookkeeping; see {@link AutoNexus}. */
    public final AutoNexus nexus;

    public Session(long id, String tag, String routeName, boolean queue, int listenPort, String clientAddress,
                   String destination, EventLog log, AutoNexus nexus) {
        this.id = id;
        this.tag = tag;
        this.routeName = routeName;
        this.queue = queue;
        this.listenPort = listenPort;
        this.clientAddress = clientAddress;
        this.destination = destination;
        this.log = log;
        this.nexus = nexus;
        this.startedWallMillis = System.currentTimeMillis();
        this.startedMonoMillis = networking.log.LogClock.monoMillis();
    }

    public String tag() {
        return tag;
    }

    public Phase phase() {
        return phase;
    }

    public boolean injectionReady() {
        return injectionReady;
    }

    public synchronized boolean isClosed() {
        return closed.get();
    }

    public boolean isQueue() {
        return queue;
    }

    /** Publishes the upstream writer; see {@link #upstreamWriter()}. */
    public void upstreamWriter(OutputStream writer) {
        this.upstreamWriter = writer;
    }

    public OutputStream upstreamWriter() {
        return upstreamWriter;
    }

    public boolean isSafeArea() {
        return safeArea;
    }

    public boolean isCasting() {
        return casting;
    }

    public synchronized String worldName() {
        return worldName;
    }

    /** How stale the newest health reading is; used to report the decision's own latency. */
    public synchronized long millisSinceLastHealth() {
        return lastHealthWallMillis == 0 ? -1 : System.currentTimeMillis() - lastHealthWallMillis;
    }

    // --- observations from the pumps ---------------------------------------------------------

    /** Records the framing decision once the relay has detected the byte order for this session. */
    public synchronized void onEndianness(Relay.Endianness order) {
        if (order == null) {
            return;
        }
        this.endianness = order;
        if (!endiannessKnown) {
            endiannessKnown = true;
            emit(Event.builder(Event.KIND_NOTE)
                    .note("length byte order resolved to " + order)
                    .put("endianness", order.name()));
        }
    }

    /**
     * Whether this session frames its length big-endian.
     *
     * <p>Unknown resolves to big-endian: see {@link #endianness}. The injection path frames with this,
     * so an injected message is byte-compatible with every forwarded one in the same stream.
     */
    public boolean frameBigEndian() {
        Relay.Endianness order = endianness;
        return order == null || order == Relay.Endianness.BIG || order == Relay.Endianness.AUTO;
    }

    /** The accept path finished and the upstream dial succeeded. */
    public void onConnected() {
        synchronized (this) {
            if (phase == Phase.NEW) {
                phase = Phase.CONNECTED;
            }
        }
        emit(Event.builder(Event.KIND_SESSION)
                .note("connected %s -> %s".formatted(clientAddress, destination))
                .put("route", routeName)
                .put("listenPort", listenPort)
                .put("destination", destination));
    }

    /**
     * One relay-observed packet, before it is forwarded.
     *
     * <p>Called on the hot path for every packet in both directions. It never throws and never blocks
     * on anything slower than a file append, because the forwarding write happens immediately after;
     * anything added here delays the session.
     */
    public void onPacket(String dir, int id, byte[] payload, String hex, Map<String, Object> decoded) {
        boolean toServer = Event.DIR_C2S.equals(dir);
        long mono = networking.log.LogClock.monoMillis();
        long wall = System.currentTimeMillis();

        if (toServer) {
            forwardedToServer.incrementAndGet();
            bytesToServer.addAndGet(payload.length);
        } else {
            forwardedToClient.incrementAndGet();
            bytesToClient.addAndGet(payload.length);
        }

        String name = isQueue() ? networking.packets.QPacketType.name(id) : GmPacketType.name(id);
        Event.Builder builder = Event.builder(Event.KIND_PACKET)
                .at(wall, mono)
                .dir(dir)
                .pkt(name)
                .pktId(id)
                .len(payload.length)
                .hex(hex)
                // The human-facing line is attached here and written by the log sink under its own
                // sequence lock. It keeps the format the existing Python tools parse, including the
                // registry description ("Reconnect (codec)"), which the JSON event does not carry.
                .textLine("[%s %s] %s len=%d id=0x%02X %-22s %s".formatted(
                        tag, dir, networking.log.LogClock.iso(wall), payload.length, id,
                        PacketRegistry.describe(isQueue(), id), hex));
        // The phase machine writes into the same event rather than emitting a second one. A packet
        // observed as one event is what the log should show; the alternative - a `session` event
        // alongside every `packet` event for the same bytes - doubles the log and makes a kind-based
        // filter (the default one filters on kind) select packets it was never meant to select.
        transition(builder, dir, id, decoded);
        emit(builder);
    }

    /** Advances the phase machine from what a packet proves, and keeps the world state current. */
    private void transition(Event.Builder event, String dir, int id, Map<String, Object> decoded) {
        if (isQueue()) {
            return;
        }
        boolean toServer = Event.DIR_C2S.equals(dir);
        if (toServer) {
            if (id == GmPacketType.HELLO) {
                onClientHello(event, decoded);
            }
            // The client's own clock is on the wire in its input packets. Tracking it here means a
            // future injector can synthesise input that is consistent with the client's timeline, and
            // it costs one varint read on packets the relay already has in hand.
            long time = clientTimeFrom(id, decoded);
            if (time != Long.MIN_VALUE) {
                lastClientTime = time;
                lastClientTimeWallMillis = System.currentTimeMillis();
            }
            if (id == GmPacketType.ESCAPE) {
                // Read the published snapshot, not the fields: this runs on the upstream pump thread
                // while the downstream pump writes them. See healthSnapshot.
                HealthSnapshot at = healthSnapshot;
                event.put("clientOwnEscape", true)
                        .putIf(at != null, "health", at == null ? null : at.health())
                        .putIf(at != null, "maxHealth", at == null ? null : at.maxHealth());
            }
        } else {
            if (id == GmPacketType.MAP_INFO) {
                onWorldEntry(event, decoded);
            } else if (id == GmPacketType.HEALTH_UPDATE) {
                onHealthUpdate(event, decoded);
            } else if (id == GmPacketType.SAFE_AREA_STATE) {
                boolean value = Boolean.TRUE.equals(decoded.get("safe"));
                if (value != safeArea) {
                    safeArea = value;
                    event.put("safeAreaChanged", true);
                }
            } else if (id == GmPacketType.ESCAPE_CAST_STATE) {
                casting = Boolean.TRUE.equals(decoded.get("casting"));
            } else if (id == GmPacketType.ESCAPE_ACK) {
                onEscapeAck(event, decoded);
            }
        }
    }

    /** The client's {@code Hello}: the first half of the state-order gate. */
    private void onClientHello(Event.Builder event, Map<String, Object> decoded) {
        synchronized (this) {
            if (phase.compareTo(Phase.HELLO) < 0) {
                phase = Phase.HELLO;
            }
            // Injection stays off: the server is reading this packet's fields right now.
            injectionReady = false;
            clientHelloDetail = "version=" + decoded.get("version") + " queueToken=" + decoded.get("queueToken");
        }
        event.put("phase", Phase.HELLO.name())
                .put("injectionReady", false)
                .put("note", "handshake started; client->server injection stays disarmed until a world exists");
    }

    /**
     * {@code MapInfo} (id 5) — a world exists.
     *
     * <p>This is the moment injection becomes legal, and also the moment every world-scoped value has
     * to be thrown away. A realm transition reuses this TCP connection, so "the last reading we saw" is
     * from a <em>different world</em>, and the client does exactly the same reset internally (it
     * clears its escape-cast state and its safe-area flag on {@code MapInfo}) — which is a useful
     * confirmation that this is the right boundary rather than a guess.
     *
     * <p>Three of those resets are load-bearing, and an earlier version of this method did none of
     * them while the documentation claimed it did:
     *
     * <ul>
     *   <li><strong>the health reading</strong> — otherwise the rule's "no reading in this world yet"
     *       guard is dead after the first world, and the dashboard shows the previous world's HP until
     *       the server happens to send a new one;</li>
     *   <li><strong>the safe-area flag</strong> — the failure here is silent and total: if the server
     *       does not re-send {@code SafeAreaState} in the new world, a stale {@code true} makes
     *       {@code skipInSafeArea} decline every reading, for the whole world, with no error
     *       anywhere;</li>
     *   <li><strong>the cast state</strong> — stale for the same reason, though it only affects what
     *       the dashboard reports today.</li>
     * </ul>
     *
     * <p>Clearing to "unknown/not-safe" rather than to the previous world's values is the deliberate
     * direction: a world we know nothing about is treated as somewhere an escape is worth attempting.
     * The per-world injection budget and the rule's arming state are reset by
     * {@link AutoNexus#onWorldEntry}.
     */
    private void onWorldEntry(Event.Builder event, Map<String, Object> decoded) {
        boolean hadHealth;
        boolean wasSafe;
        synchronized (this) {
            phase = Phase.IN_WORLD;
            injectionReady = true;
            worldName = str(decoded.get("mapName"));
            worldMap = str(decoded.get("mapFile"));

            hadHealth = healthSamples > 0;
            wasSafe = safeArea;
            lastMaxHealth = -1;
            lastHealth = -1;
            lastShield = -1;
            lastBarrier = -1;
            lastHealthWallMillis = 0;
            healthSamples = 0;
            healthSnapshot = null;
            safeArea = false;
            casting = false;
        }
        event.put("phase", Phase.IN_WORLD.name())
                .put("injectionReady", true)
                .put("note", "world entered; injection armed, world state cleared")
                .putIf(hadHealth, "clearedHealthSample", true)
                .putIf(wasSafe, "clearedSafeArea", true);
        nexus.onWorldEntry(this, decoded.get("playerId"));
    }

    private void onHealthUpdate(Event.Builder event, Map<String, Object> decoded) {
        int max = intOf(decoded.get("maxHealth"), -1);
        int health = intOf(decoded.get("health"), -1);
        int shield = intOf(decoded.get("shield"), -1);
        int barrier = intOf(decoded.get("barrier"), -1);
        synchronized (this) {
            lastMaxHealth = max;
            lastHealth = health;
            lastShield = shield;
            lastBarrier = barrier;
            lastHealthWallMillis = System.currentTimeMillis();
            healthSamples++;
            healthSnapshot = new HealthSnapshot(lastHealthWallMillis, max, health, shield, barrier);
        }
        event.putAll(decoded)
                .put("hpPercent", hpPercent())
                .put("effectivePercent", effectivePercent());
        nexus.onHealth(this, new AutoNexus.Reading(max, health, shield, barrier));
    }

    private void onEscapeAck(Event.Builder event, Map<String, Object> decoded) {
        boolean success = intOf(decoded.get("success"), 0) != 0;
        AutoNexus.PendingInjection pending = nexus.onEscapeAck(this, success);
        event.put("injectionAccepted", success)
                .putIf(pending != null, "answersInjectionSeq", pending == null ? null : pending.seq())
                .putIf(pending != null, "latencyMs", pending == null ? null : pending.latencyMillis());
    }

    public void onClosed(String reason) {
        closed.set(true);
        synchronized (this) {
            phase = Phase.CLOSED;
            injectionReady = false;
        }
        emit(Event.builder(Event.KIND_SESSION)
                .note("closed: " + reason)
                .put("packetsToServer", forwardedToServer.get())
                .put("packetsToClient", forwardedToClient.get())
                .put("injected", injectedPackets.get()));
    }

    // --- injection ---------------------------------------------------------------------------

    /**
     * Writes one injected message into the client→server stream and records it.
     *
     * <p><strong>The caller must already hold the stream's monitor.</strong> Both pumps serialize their
     * writes on the {@code GameWriter} they own; injecting from the downstream pump while holding the
     * upstream writer's monitor - the same object the upstream pump synchronizes on - guarantees the
     * bytes land whole, between two forwarded messages, and never interleaved with one. The check
     * below exists because getting that wrong would corrupt the session in a way that looks like a
     * server bug, and it is worth refusing loudly at the one place responsible.
     *
     * <h2>Why the event is emitted before the write</h2>
     *
     * <p>The intuitive order is the reverse - write, then log what you wrote - and that is what this
     * did until {@code tools/test_relay_stress.py} caught the consequence: the server received more
     * escapes than the log recorded. The gap is the window between the write and the log line, and a
     * process that stops in it (a shutdown, a crash, a kill) leaves a packet on the wire that the log
     * does not mention. <strong>For a safety feature that direction is the dangerous one</strong>: a
     * reader checking "did auto-nexus ever fire unexpectedly" would find fewer firings than happened,
     * and the record would understate exactly the thing being audited.
     *
     * <p>Emitting first makes the log a superset of the wire, and the superset is what an audit wants.
     * A write that then fails is reported as an additional {@code error} event naming the same
     * injection, so the log distinguishes "attempted and failed" from "attempted and succeeded" rather
     * than silently over-reporting. The cost is that a failed write leaves one more event than packets;
     * the benefit is that no packet can ever leave unrecorded.
     *
     * @return the sequence number of the injection event, or -1 when nothing was written
     */
    public long inject(OutputStream upstreamOut, String name, byte[] framed, String reason) {
        if (!Thread.holdsLock(upstreamOut)) {
            emit(Event.builder(Event.KIND_ERROR)
                    .note("refusing to inject " + name + ": the caller does not hold the upstream write lock,"
                            + " so the length prefix and payload could interleave with a forwarded packet"));
            return -1;
        }
        long count = injectedPackets.incrementAndGet();
        Event event = emit(Event.builder(Event.KIND_INJECT)
                .dir(Event.DIR_C2S)
                .pkt(name)
                .len(framed.length - 4)
                .hex(EventLog.hex(framed, 4, framed.length - 4))
                .note("injected " + name + (reason == null ? "" : " (" + reason + ")"))
                .put("injectionIndex", count)
                .put("framed", EventLog.hex(framed)));
        try {
            upstreamOut.write(framed);
            upstreamOut.flush();
        } catch (IOException e) {
            // The attempt is already on the record; this says it did not complete. Naming the sequence
            // number ties the two lines together, so a reader can see which recorded injection failed
            // rather than having to guess from the ordering.
            emit(Event.builder(Event.KIND_ERROR)
                    .note("injection #%d (%s) was recorded but its write failed: %s"
                            .formatted(event.seq, name, explain(e))));
            return -1;
        }
        return event.seq;
    }

    /** A message even for the exceptions that carry none, such as {@code EOFException}. */
    private static String explain(Exception e) {
        String message = e.getMessage();
        return message == null || message.isBlank() ? e.getClass().getSimpleName() : message;
    }

    // --- derived views -----------------------------------------------------------------------

    /** How many messages this relay has written into this session's upstream direction. */
    public long injectedCount() {
        return injectedPackets.get();
    }

    public long packetsToServer() {
        return forwardedToServer.get();
    }

    public long packetsToClient() {
        return forwardedToClient.get();
    }

    public synchronized int hpPercent() {
        if (lastMaxHealth <= 0 || lastHealth < 0) {
            return -1;
        }
        return (int) Math.round(100.0 * lastHealth / lastMaxHealth);
    }

    public synchronized int effectivePercent() {
        if (lastMaxHealth <= 0 || lastHealth < 0) {
            return -1;
        }
        return (int) Math.round(100.0 * (lastHealth + Math.max(0, lastShield) + Math.max(0, lastBarrier))
                / lastMaxHealth);
    }

    public synchronized boolean hasHealth() {
        return healthSamples > 0;
    }

    public long lastClientTime() {
        return lastClientTime;
    }

    /**
     * Extracts the client's {@code Time} field from an input packet.
     *
     * <p>Layouts are the ones verified against the capture in {@code docs/FEATURES-AUTOMATION.md}:
     * {@code Move} is {@code [LE u16 id][float X][float Y][varint Time]}, {@code Shoot} adds an angle
     * before the time and two fields after it, {@code ActivateObject} puts a varint id and a string32
     * first. Only the offset to {@code Time} matters here.
     */
    private long clientTimeFrom(int id, Map<String, Object> decoded) {
        Object time = decoded.get("time");
        if (time instanceof Number number) {
            return number.longValue();
        }
        return Long.MIN_VALUE;
    }

    private static int intOf(Object value, int fallback) {
        return value instanceof Number number ? number.intValue() : fallback;
    }

    private static String str(Object value) {
        return value == null ? null : String.valueOf(value);
    }

    private Event emit(Event.Builder builder) {
        return log.emit(builder.session(tag));
    }

    // --- dashboard view ----------------------------------------------------------------------

    /**
     * The health block, which is what the dashboard's default filter is about.
     *
     * <p>A field the server has not told us is present-and-null rather than absent, because the page
     * reads it on every poll: {@code undefined} and {@code null} take different branches there, and an
     * object whose shape changes under the reader is worse than one with nulls in it.
     */
    public synchronized Map<String, Object> healthJson() {
        Fields fields = Fields.of()
                .add("maxHealth", lastMaxHealth < 0 ? null : lastMaxHealth)
                .add("health", lastHealth < 0 ? null : lastHealth)
                .add("shield", lastShield < 0 ? null : lastShield)
                .add("barrier", lastBarrier < 0 ? null : lastBarrier)
                .add("hpPercent", hpPercent() < 0 ? null : hpPercent())
                .add("effectivePercent", effectivePercent() < 0 ? null : effectivePercent())
                .add("samples", healthSamples)
                .add("ageMs", healthSamples == 0 ? null : System.currentTimeMillis() - lastHealthWallMillis);
        return new LinkedHashMap<>(fields);
    }
}
