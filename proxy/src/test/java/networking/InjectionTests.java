package networking;

import networking.log.Event;
import networking.log.EventLog;
import networking.packets.GmPacketType;
import networking.packets.Injection;

import java.io.ByteArrayOutputStream;
import java.io.OutputStream;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;

/**
 * The gate for everything the relay can <em>write</em>.
 *
 * <p>Run with {@code java -cp target/classes networking.InjectionTests}. Exits non-zero on the first
 * failure.
 *
 * <h2>Why these assertions are byte literals and not round-trips</h2>
 *
 * <p>A round-trip test - encode, decode, compare objects - passes even when both directions share the
 * same wrong idea of the format. The failure mode that matters here is precisely that: a length prefix
 * that excludes the type id, or a packet id written big-endian, round-trips perfectly through this
 * code and desynchronises the real server on the first packet. So every assertion below is against a
 * literal byte string that was established independently - from the captured
 * {@code 4200} escape in {@code work/logs/}, from {@code GmPacket.GetData()} writing
 * {@code byteWriter.Write(TypeId)} before {@code Write(...)}, and from Telepathy framing with
 * {@code IntToBytesBigEndianNonAlloc}.
 *
 * <h2>What is checked</h2>
 *
 * <ol>
 *   <li>the exact bytes of every packet the relay can inject, in both length byte orders;</li>
 *   <li>the <strong>state</strong> order - that injection stays disarmed until the client's
 *       {@code Hello} and the server's {@code MapInfo} have both been seen, and re-arms per world;</li>
 *   <li>that the write is refused unless the caller holds the stream monitor, which is the only thing
 *       keeping an injected message from splitting a forwarded one;</li>
 *   <li>the auto-nexus rule's guards, one at a time, on a session that can be driven without sockets.</li>
 * </ol>
 */
public final class InjectionTests {

    private static final List<String> FAILURES = new ArrayList<>();
    private static int checks;

    public static void main(String[] args) {
        testEscapeBytes();
        testEscapeCastStateBytes();
        testQueueFraming();
        testInjectionFollowsSessionFraming();
        testHealthUpdateDecode();
        testServerPacketDecode();
        testInjectionGate();
        testInjectionRequiresWriteLock();
        testAutoNexusRule();
        testAcknowledgementsArePerSession();
        testConfigValidation();
        testFilterDefaults();
        testKickReasonIsLogged();
        testJsonEscaping();

        if (FAILURES.isEmpty()) {
            IO.println("PASS: " + checks + " injection, gate, rule and log checks passed");
            return;
        }
        IO.println("FAIL: " + FAILURES.size() + " of " + checks + " check(s) failed");
        for (String failure : FAILURES) {
            IO.println("  - " + failure);
        }
        System.exit(1);
    }

    // --- helpers ------------------------------------------------------------------------------

    private static void check(boolean condition, String description) {
        checks++;
        if (!condition) {
            FAILURES.add(description);
        }
    }

    private static void checkEquals(Object expected, Object actual, String description) {
        checks++;
        if (!java.util.Objects.equals(expected, actual)) {
            FAILURES.add(description + " (expected " + expected + ", got " + actual + ")");
        }
    }

    private static String hex(byte[] bytes) {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < bytes.length; i++) {
            if (i > 0) {
                sb.append(' ');
            }
            sb.append(String.format("%02X", bytes[i]));
        }
        return sb.toString();
    }

    /** A session that can be driven directly, with no sockets involved. */
    private static Session session(EventLog log) {
        AutoNexus nexus = new AutoNexus(new AutoNexus.Config(), log);
        return new Session(1, "Game#1", "Game", false, 6410, "/127.0.0.1:50000", "1.2.3.4:6410", log, nexus);
    }

    /**
     * Feeds a packet into a session the way the pump does.
     *
     * <p>Note the use of {@link #healthPayload} rather than hand-written bytes for health packets. The
     * first version of these tests wrote varint fixtures by hand from the <em>old</em> 6,7,7 grouping,
     * so after the codec was corrected they silently meant different numbers - {@code E8 03}, which
     * used to be 1000, is {@code -232} under the real 6, 6, 7 layout. A hand-written varint is a
     * fixture that goes on passing while testing nothing, so the encoder builds them now.
     */
    private static void feed(Session session, String dir, int id, byte[] payload) {
        session.onPacket(dir, id, payload, EventLog.hex(payload), Injection.decode(false, id, payload));
    }

    /** A {@code GmHealthUpdate} payload built by the relay's own encoder; no hand-written varints. */
    private static byte[] healthPayload(int maxHealth, int health, int shield, int barrier) {
        return Injection.healthUpdatePayload(maxHealth, health, shield, barrier);
    }

    // --- 1. the injected bytes -----------------------------------------------------------------

    private static void testEscapeBytes() {
        // The single most important byte string in this project: five bytes, and the length field
        // must count the two-byte type id. `00 00 00 00 42 00` would read as a zero-length packet
        // followed by a packet with id 0x0042 = 66... which is the same id, one frame later, and
        // the server would then read the *next* real packet's length from the wrong offset.
        checkEquals("00 00 00 02 42 00", hex(Injection.escape(true)),
                "GmEscape frames as BE length 2 then the LE ushort id 66");
        byte[] framed = Injection.escape(true);
        checkEquals(6, framed.length, "an escape message is 6 bytes on the wire");
        checkEquals(2, framed[3] & 0xFF, "the length counts the type id bytes");
        checkEquals(0x42, framed[4] & 0xFF, "the id is 66 in the first type byte");
        checkEquals(0x00, framed[5] & 0xFF, "the id's high byte follows it, little-endian");
    }

    private static void testEscapeCastStateBytes() {
        // GmEscapeCastState (290 = 0x0122) writes one bool: casting=true is 0x01.
        checkEquals("00 00 00 03 22 01 01", hex(Injection.escapeCastState(true, true)),
                "EscapeCastState(casing=true) is BE length 3, LE id 0x0122, byte 1");
        checkEquals("00 00 00 03 22 01 00", hex(Injection.escapeCastState(false, true)),
                "EscapeCastState(casting=false) carries a zero byte");
    }

    private static void testQueueFraming() {
        // The queue service frames identically except for the byte order, and its type is one byte.
        // This is the shape the relay would use if a queue-side injection were ever added; pinning it
        // here means the little-endian branch of the framer cannot rot unnoticed.
        byte[] payload = Injection.queuePayload(3, "tok".getBytes(java.nio.charset.StandardCharsets.UTF_8));
        checkEquals("03 74 6F 6B", hex(payload), "a queue payload is a one-byte id then the body");
        checkEquals("04 00 00 00 03 74 6F 6B", hex(Injection.frame(payload, false)),
                "queue framing writes the length little-endian");
        checkEquals("00 00 00 04 03 74 6F 6B", hex(Injection.frame(payload, true)),
                "the same payload framed big-endian for the game service");
    }

    /**
     * An injected frame must use the same length order the session's forwarding uses.
     *
     * <p>An {@code AUTO} session latches its order from the first header, and every forwarded frame is
     * then encoded with that order. An injected frame that picked its own order would be correct on a
     * big-endian session and fatal on a little-endian one - the server would read the length backwards
     * and desync the whole stream. The game service resolves to big-endian in practice, which is
     * exactly why this needs a test: a hardcoded default hides the bug until the day it does not hold.
     */
    private static void testInjectionFollowsSessionFraming() {
        EventLog log = EventLog.memoryOnly(256);

        // A session that resolved little-endian: the queue's real order, and what AUTO picks when the
        // big-endian reading is implausible (the queue's first header is 08 00 00 00).
        AutoNexus unused = new AutoNexus(new AutoNexus.Config(), log);
        Session little = new Session(51, "Queue#51", "Queue", true, 6412, "/x", "y:6412", log, unused);
        check(little.frameBigEndian(), "a session reports big-endian before any header is seen");
        little.onEndianness(Relay.Endianness.LITTLE);
        check(!little.frameBigEndian(), "a session that resolved little-endian reports little-endian");
        checkEquals("02 00 00 00 42 00", hex(Injection.escape(little.frameBigEndian())),
                "the injected escape follows a little-endian session's framing");

        // The ordinary case is unchanged.
        AutoNexus nexus = new AutoNexus(new AutoNexus.Config(), log);
        Session big = new Session(52, "Game#52", "Game", false, 6410, "/x", "y:6410", log, nexus);
        big.onEndianness(Relay.Endianness.BIG);
        check(big.frameBigEndian(), "a session that resolved big-endian reports big-endian");
        checkEquals("00 00 00 02 42 00", hex(Injection.escape(big.frameBigEndian())),
                "the injected escape follows a big-endian session's framing");

        // The channelled form takes the same decision.
        checkEquals("03 00 00 00 22 01 01", hex(Injection.escapeCastState(true, false)),
                "the channelled form also follows the session's framing");

        // And the rule itself must consult the session rather than a constant: drive a real injection
        // into a little-endian session and read the stream.
        AutoNexus.Config config = new AutoNexus.Config();
        config.enabled = true;
        config.dryRun = false;
        config.thresholdPercent = 50;
        AutoNexus rule = new AutoNexus(config, log);
        Session subject = new Session(53, "Game#53", "Game", false, 6410, "/x", "y:6410", log, rule);
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        subject.upstreamWriter(out);
        subject.onEndianness(Relay.Endianness.LITTLE);
        feed(subject, Event.DIR_C2S, GmPacketType.HELLO, new byte[]{0x37, 0x00, 0x00});
        feed(subject, Event.DIR_S2C, GmPacketType.MAP_INFO, new byte[]{0x05, 0x00});
        feed(subject, Event.DIR_S2C, GmPacketType.HEALTH_UPDATE, healthPayload(1000, 100, 0, 0));
        synchronized (out) {
            rule.onHealth(subject, new AutoNexus.Reading(1000, 100, 0, 0));
        }
        checkEquals("02 00 00 00 42 00", hex(out.toByteArray()),
                "an injection into a little-endian session is framed little-endian");
    }

    // --- 2. decoding what comes back -----------------------------------------------------------

    private static void testHealthUpdateDecode() {
        // The captured packet 46 00 83 0B 83 0B 00 00 is the ground truth for the varint grouping:
        // the same character's fixed-width StatsType.Hp int16 in the matching GmUpdate reads 707, so
        // 83 0B is 707. A decoder that reads the first continuation byte at bit 7 calls it 1411.
        byte[] payload = {0x46, 0x00, (byte) 0x83, 0x0B, (byte) 0x83, 0x0B, 0x00, 0x00};
        Map<String, Object> decoded = Injection.decode(false, GmPacketType.HEALTH_UPDATE, payload);
        checkEquals(707, ((Number) decoded.get("maxHealth")).intValue(), "HealthUpdate maxHealth decodes");
        checkEquals(707, ((Number) decoded.get("health")).intValue(), "HealthUpdate health decodes");
        checkEquals(0, ((Number) decoded.get("shield")).intValue(), "HealthUpdate shield decodes");
        checkEquals(0, ((Number) decoded.get("barrier")).intValue(), "HealthUpdate barrier decodes");

        // A later packet in the same session, whose health the fixed-width stream confirms as 676.
        Map<String, Object> later = Injection.decode(false, GmPacketType.HEALTH_UPDATE,
                new byte[]{0x46, 0x00, (byte) 0x83, 0x0B, (byte) 0xA4, 0x0A, 0x00, 0x00});
        checkEquals(676, ((Number) later.get("health")).intValue(),
                "the second captured HealthUpdate decodes to 676");
    }

    private static void testServerPacketDecode() {
        // EscapeAck (158 = 0x9E) carries one byte; the captured success case was 9E 00 01.
        Map<String, Object> ack = Injection.decode(false, GmPacketType.ESCAPE_ACK,
                new byte[]{(byte) 0x9E, 0x00, 0x01});
        checkEquals(1, ((Number) ack.get("success")).intValue(), "EscapeAck success decodes");

        // SafeAreaState (291 = 0x0123) carries one bool.
        Map<String, Object> safe = Injection.decode(false, GmPacketType.SAFE_AREA_STATE,
                new byte[]{0x23, 0x01, 0x00});
        checkEquals(Boolean.FALSE, safe.get("safe"), "SafeAreaState safe=false decodes");
        Map<String, Object> unsafe = Injection.decode(false, GmPacketType.SAFE_AREA_STATE,
                new byte[]{0x23, 0x01, 0x01});
        checkEquals(Boolean.TRUE, unsafe.get("safe"), "SafeAreaState safe=true decodes");

        // ForcedEscape (184 = 0xB8) is a string16.
        Map<String, Object> forced = Injection.decode(false, GmPacketType.FORCED_ESCAPE,
                new byte[]{(byte) 0xB8, 0x00, 0x03, 0x00, 'n', 'o', 'p'});
        checkEquals("nop", forced.get("message"), "ForcedEscape message decodes from string16");

        // Kicked (185 = 0xB9) is the same string16 one id later - the server's reason for ending it.
        Map<String, Object> kicked = Injection.decode(false, GmPacketType.KICKED,
                new byte[]{(byte) 0xB9, 0x00, 0x05, 0x00, 's', 'p', 'e', 'e', 'd'});
        checkEquals("speed", kicked.get("reason"), "GmKicked reason decodes from string16");

        // An unknown id decodes to nothing rather than throwing: the relay must never fail a session
        // because a codec is missing.
        checkEquals(Map.of(), Injection.decode(false, 12345, new byte[]{0x01, 0x02, 0x03}),
                "an unregistered id decodes to no fields");
    }

    // --- 3. the state-order gate ---------------------------------------------------------------

    private static void testInjectionGate() {
        EventLog log = EventLog.memoryOnly(512);
        Session session = session(log);

        check(!session.injectionReady(),
                "a fresh session is not injectable: the handshake has not even started");
        checkEquals(Session.Phase.NEW, session.phase(), "a fresh session is in phase NEW");

        // The client's Hello arrives. The server is now reading that packet's fields, so the gate
        // must stay shut - this is the assertion that protects the login.
        feed(session, Event.DIR_C2S, GmPacketType.HELLO, new byte[]{0x37, 0x00, 0x00});
        check(!session.injectionReady(),
                "after the client's Hello the gate is still shut: the server is mid-handshake");
        checkEquals(Session.Phase.HELLO, session.phase(), "the session advances to phase HELLO");

        // MapInfo arrives: a world exists, so an escape now has somewhere to go.
        feed(session, Event.DIR_S2C, GmPacketType.MAP_INFO, new byte[]{0x05, 0x00});
        check(session.injectionReady(), "after MapInfo the gate opens");
        checkEquals(Session.Phase.IN_WORLD, session.phase(), "the session advances to phase IN_WORLD");

        // A world-scoped value is set, so that the reset below has something to clear. The earlier
        // version of this test asserted the reset without ever establishing the value, which made it
        // pass against a Session that reset nothing at all.
        feed(session, Event.DIR_S2C, GmPacketType.HEALTH_UPDATE, healthPayload(1000, 200, 0, 0));
        feed(session, Event.DIR_S2C, GmPacketType.SAFE_AREA_STATE, new byte[]{0x23, 0x01, 0x01});
        check(session.hasHealth(), "a health reading is established in the first world");
        check(session.isSafeArea(), "a safe-area flag is established in the first world");
        check(session.hpPercent() >= 0, "the first world's HP percentage is measurable");

        // A second world (a dungeon, a new realm) re-arms: health, safe area, budget and arming all
        // reset. Reusing one connection across worlds is what a realm transition does, so a stale
        // reading here is a reading from a different world.
        feed(session, Event.DIR_S2C, GmPacketType.MAP_INFO, new byte[]{0x05, 0x00});
        check(session.injectionReady(), "a new world re-arms injection");
        check(!session.hasHealth(), "a new world clears the health reading, so no stale percentage is acted on");
        checkEquals(-1, session.hpPercent(), "the HP percentage is unknown again in the new world");
        check(!session.isSafeArea(),
                "a new world clears the safe-area flag (a stale true would silently disable the rule)");
        check(session.healthJson().get("health") == null, "the dashboard reports no HP for the new world");

        // Ending the session closes the gate for good.
        session.onClosed("test");
        check(!session.injectionReady(), "a closed session is not injectable");
    }

    // --- 4. the write must be atomic with forwarding -------------------------------------------

    private static void testInjectionRequiresWriteLock() {
        EventLog log = EventLog.memoryOnly(512);
        Session session = session(log);
        session.upstreamWriter(new ByteArrayOutputStream());

        // Without the monitor the write is refused, and the refusal is recorded rather than thrown:
        // the caller is a pump thread whose failure would drop a live session.
        long refused = session.inject((ByteArrayOutputStream) session.upstreamWriter(), "Escape",
                Injection.escape(true), "test without the lock");
        checkEquals(-1L, refused, "an injection without the upstream write lock is refused");

        ByteArrayOutputStream out = new ByteArrayOutputStream();
        long seq;
        synchronized (out) {
            seq = session.inject(out, "Escape", Injection.escape(true), "test with the lock");
        }
        check(seq > 0, "an injection holding the upstream write lock is written");
        checkEquals("00 00 00 02 42 00", hex(out.toByteArray()),
                "exactly the five bytes reach the stream, with no framing bytes added or lost");

        // The event trail has to explain it: a refusal, then a write, in that order.
        List<Event> events = log.ring().last(10);
        check(events.stream().anyMatch(e -> Event.KIND_ERROR.equals(e.kind)
                        && e.note != null && e.note.contains("does not hold the upstream write lock")),
                "the refusal is recorded as an error event");
        check(events.stream().anyMatch(e -> Event.KIND_INJECT.equals(e.kind) && "Escape".equals(e.pkt)),
                "the successful injection is recorded as an inject event");

        // A write that fails is recorded as an attempt *and* a failure, never as nothing. The event is
        // emitted before the write, so a process that dies mid-injection still leaves a record of
        // having tried - the log must be a superset of the wire, because an injection that left
        // unrecorded is the one failure an audit of a safety feature cannot tolerate. That direction
        // was the bug the stress harness caught: the server saw an escape the log did not mention.
        Session failing = session(log);
        OutputStream broken = new OutputStream() {
            @Override
            public void write(int b) throws java.io.IOException {
                throw new java.io.IOException("simulated socket failure");
            }
        };
        failing.upstreamWriter(broken);
        long failedSeq;
        synchronized (broken) {
            failedSeq = failing.inject(broken, "Escape", Injection.escape(true), "test failure path");
        }
        checkEquals(-1L, failedSeq, "a failed write reports that nothing was sent");
        List<Event> trail = log.ring().last(6);
        check(trail.stream().anyMatch(e -> Event.KIND_INJECT.equals(e.kind)),
                "the attempt is recorded even though the write failed");
        check(trail.stream().anyMatch(e -> Event.KIND_ERROR.equals(e.kind)
                        && e.note != null && e.note.contains("its write failed")),
                "the failure is recorded and names the injection it belongs to");
    }

    // --- 5. the rule ---------------------------------------------------------------------------

    private static void testAutoNexusRule() {
        EventLog log = EventLog.memoryOnly(2048);
        AutoNexus.Config config = new AutoNexus.Config();
        AutoNexus nexus = new AutoNexus(config, log);
        Session session = new Session(2, "Game#2", "Game", false, 6410, "/127.0.0.1:1", "1.2.3.4:6410", log, nexus);

        AutoNexus.Reading low = new AutoNexus.Reading(1000, 200, 0, 0);
        AutoNexus.Reading healthy = new AutoNexus.Reading(1000, 900, 0, 0);

        // Disabled: nothing happens, not even a decision.
        config.enabled = false;
        check(!nexus.decide(session, low).fire(), "a disabled rule never fires");

        config.enabled = true;
        check(!nexus.decide(session, low).fire(),
                "the rule does not fire before a world exists (injection is not armed)");
        check(nexus.decide(session, low).reason().contains("not armed"),
                "the decline says injection is not armed");

        // Enter the world and give it a health reading.
        feed(session, Event.DIR_C2S, GmPacketType.HELLO, new byte[]{0x37, 0x00, 0x00});
        feed(session, Event.DIR_S2C, GmPacketType.MAP_INFO, new byte[]{0x05, 0x00});
        check(!nexus.decide(session, low).fire(),
                "the rule does not fire before the first HealthUpdate of the world");

        feed(session, Event.DIR_S2C, GmPacketType.HEALTH_UPDATE, new byte[]{0x46, 0x00, 0x01});
        check(session.hasHealth(), "the session records that it has a health reading");
        check(!nexus.decide(session, healthy).fire(), "a healthy reading does not fire");
        check(nexus.decide(session, low).fire(), "200/1000 against a 35% threshold fires");

        // Effective HP: 200 hp plus 200 shield is 40% of max, so it must not fire when shield counts.
        config.useEffectiveHp = true;
        AutoNexus.Reading shielded = new AutoNexus.Reading(1000, 200, 200, 0);
        AutoNexus.Decision withShield = nexus.decide(session, shielded);
        check(!withShield.fire(), "effective HP of 40% does not fire against a 35% threshold");
        config.useEffectiveHp = false;
        check(nexus.decide(session, shielded).fire(),
                "the same reading fires when only HP counts (200/1000 = 20%)");

        // Safe area: the escape would be pointless, so the rule declines.
        config.skipInSafeArea = true;
        feed(session, Event.DIR_S2C, GmPacketType.SAFE_AREA_STATE, new byte[]{0x23, 0x01, 0x01});
        check(session.isSafeArea(), "the safe-area flag is tracked from SafeAreaState");
        check(!nexus.decide(session, low).fire(), "the rule declines inside a safe area");
        feed(session, Event.DIR_S2C, GmPacketType.SAFE_AREA_STATE, new byte[]{0x23, 0x01, 0x00});
        check(!session.isSafeArea(), "leaving the safe area clears the flag");

        // Dead: HP zero is not a low-health emergency.
        feed(session, Event.DIR_S2C, GmPacketType.HEALTH_UPDATE, healthPayload(1000, 0, 0, 0));
        AutoNexus.Reading dead = new AutoNexus.Reading(1000, 0, 0, 0);
        check(!nexus.decide(session, dead).fire(), "the rule declines when HP is zero");

        testBudgetAndInterval(log, low);
    }

    /**
     * The two rate limits, and the write itself.
     *
     * <p>This is the end of the chain that matters most: the rule decides, the bytes go into the
     * stream, and the counters that a later analysis reads agree with what the socket received. The
     * assertions are on the <em>bytes</em>, so a rule that decides correctly but writes nothing - or
     * writes a malformed frame - cannot pass.
     */
    private static void testBudgetAndInterval(EventLog log, AutoNexus.Reading low) {
        AutoNexus.Config config = new AutoNexus.Config();
        config.enabled = true;
        config.dryRun = false;
        config.thresholdPercent = 50;
        config.maxPerWorld = 3;
        config.minIntervalMillis = 0;

        AutoNexus nexus = new AutoNexus(config, log);
        Session session = new Session(3, "Game#3", "Game", false, 6410, "/127.0.0.1:2", "1.2.3.4:6410", log, nexus);
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        session.upstreamWriter(out);
        feed(session, Event.DIR_C2S, GmPacketType.HELLO, new byte[]{0x37, 0x00, 0x00});
        feed(session, Event.DIR_S2C, GmPacketType.MAP_INFO, new byte[]{0x05, 0x00});
        feed(session, Event.DIR_S2C, GmPacketType.HEALTH_UPDATE, new byte[]{0x46, 0x00, 0x01});

        check(nexus.decide(session, low).fire(), "the rule fires once the world and a reading exist");
        synchronized (out) {
            nexus.onHealth(session, low);
        }
        checkEquals(1L, session.injectedCount(), "the first fire wrote one escape");

        // With a minimum interval in force, the next reading is refused - and the refusal says why,
        // which is what a later analysis reads instead of guessing.
        config.minIntervalMillis = 60_000;
        AutoNexus.Decision blocked = nexus.decide(session, low);
        check(!blocked.fire(), "the minimum interval blocks a second escape in the same instant");
        check(blocked.reason().contains("since the last injection"), "the decline names the interval");

        // Lower it again and spend the rest of the budget; the budget is the harder stop.
        config.minIntervalMillis = 0;
        synchronized (out) {
            nexus.onHealth(session, low);
        }
        synchronized (out) {
            nexus.onHealth(session, low);
        }
        checkEquals(3L, session.injectedCount(), "three escapes were written, which is the budget");
        check(!nexus.decide(session, low).fire(), "the per-world budget stops the fourth injection");
        check(nexus.decide(session, low).reason().contains("budget"), "the decline names the budget");

        byte[] written = out.toByteArray();
        checkEquals(18, written.length, "three escapes are eighteen bytes");
        checkEquals("00 00 00 02 42 00 00 00 00 02 42 00 00 00 00 02 42 00", hex(written),
                "every injected message is a complete frame, adjacent to the next, with no framing bytes"
                        + " added or lost between them");

        // A fresh world resets the budget, which is what makes the rule usable across a session.
        feed(session, Event.DIR_S2C, GmPacketType.MAP_INFO, new byte[]{0x05, 0x00});
        feed(session, Event.DIR_S2C, GmPacketType.HEALTH_UPDATE, new byte[]{0x46, 0x00, 0x01});
        check(nexus.decide(session, low).fire(), "a new world re-arms the budget");

        // Dry run: the decision is made and recorded, and nothing reaches the stream.
        config.dryRun = true;
        int before = out.size();
        synchronized (out) {
            nexus.onHealth(session, low);
        }
        checkEquals(before, out.size(), "a dry run writes nothing");
        check(log.ring().last(20).stream().anyMatch(e -> e.note != null && e.note.startsWith("DRY RUN")),
                "a dry run is still recorded as a decision");

        testCastChannel(log, low);
        testRearm(log);
    }

    /**
     * The channelled-escape variant.
     *
     * <p>Off by default, because the instant form is byte-identical to what the client sends in every
     * non-channelled situation and is therefore known-acceptable. It exists for the case where the
     * server refuses the instant form because the character carries the {@code Impermanence} totem -
     * so what is worth testing is the <em>order and content</em> of the two messages: cast state
     * first, then the escape, each a complete frame, with nothing in between.
     */
    private static void testCastChannel(EventLog log, AutoNexus.Reading low) {
        AutoNexus.Config config = new AutoNexus.Config();
        config.enabled = true;
        config.dryRun = false;
        config.thresholdPercent = 50;
        config.useCastChannel = true;
        config.castMillis = 0;              // the gap is a real wait; zero here so the test stays fast

        AutoNexus nexus = new AutoNexus(config, log);
        Session session = new Session(41, "Game#41", "Game", false, 6410, "/x", "y:6410", log, nexus);
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        session.upstreamWriter(out);
        feed(session, Event.DIR_C2S, GmPacketType.HELLO, new byte[]{0x37, 0x00, 0x00});
        feed(session, Event.DIR_S2C, GmPacketType.MAP_INFO, new byte[]{0x05, 0x00});
        feed(session, Event.DIR_S2C, GmPacketType.HEALTH_UPDATE, new byte[]{0x46, 0x00, 0x01});

        synchronized (out) {
            nexus.onHealth(session, low);
        }

        // 7 bytes of cast state (BE length 3, LE id 290, one byte) then 6 bytes of escape.
        checkEquals("00 00 00 03 22 01 01 00 00 00 02 42 00", hex(out.toByteArray()),
                "the channelled form sends EscapeCastState{true} and then the escape, in that order");
        checkEquals(2L, session.injectedCount(), "both messages are counted as injections");

        List<Event> injects = log.ring().last(10).stream()
                .filter(e -> Event.KIND_INJECT.equals(e.kind) && "Game#41".equals(e.session))
                .toList();
        checkEquals(2, injects.size(), "both injections are recorded");
        checkEquals("EscapeCastState", injects.get(0).pkt, "the cast-state packet is logged first");
        checkEquals("Escape", injects.get(1).pkt, "the escape is logged second");

        // The default really is the instant form: the same input with the channel off sends only the
        // five-byte escape. This is the assertion that keeps the default honest.
        config.useCastChannel = false;
        AutoNexus instant = new AutoNexus(config, log);
        Session second = new Session(42, "Game#42", "Game", false, 6410, "/x", "y:6410", log, instant);
        ByteArrayOutputStream out2 = new ByteArrayOutputStream();
        second.upstreamWriter(out2);
        feed(second, Event.DIR_C2S, GmPacketType.HELLO, new byte[]{0x37, 0x00, 0x00});
        feed(second, Event.DIR_S2C, GmPacketType.MAP_INFO, new byte[]{0x05, 0x00});
        feed(second, Event.DIR_S2C, GmPacketType.HEALTH_UPDATE, new byte[]{0x46, 0x00, 0x01});
        synchronized (out2) {
            instant.onHealth(second, low);
        }
        checkEquals("00 00 00 02 42 00", hex(out2.toByteArray()),
                "with the cast channel off only the instant escape is sent");
    }

    /**
     * The optional re-arm level.
     *
     * <p>Its purpose is to stop a character that is stuck below the threshold from being nexused
     * repeatedly the moment the interval allows it: after firing, HP must come back up to
     * {@code rearmPercent} before the rule will fire again. What matters is that it gates the
     * <em>second</em> fire and that it clears itself, so a recovery genuinely re-arms rather than
     * latching the rule off forever - a bug that would be invisible in a short test and fatal in a
     * long session.
     */
    private static void testRearm(EventLog log) {
        AutoNexus.Config config = new AutoNexus.Config();
        config.enabled = true;
        config.dryRun = false;
        config.thresholdPercent = 40;
        config.rearmPercent = 80;
        config.minIntervalMillis = 0;
        config.maxPerWorld = 10;

        AutoNexus nexus = new AutoNexus(config, log);
        Session session = new Session(43, "Game#43", "Game", false, 6410, "/x", "y:6410", log, nexus);
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        session.upstreamWriter(out);
        feed(session, Event.DIR_C2S, GmPacketType.HELLO, new byte[]{0x37, 0x00, 0x00});
        feed(session, Event.DIR_S2C, GmPacketType.MAP_INFO, new byte[]{0x05, 0x00});
        feed(session, Event.DIR_S2C, GmPacketType.HEALTH_UPDATE, new byte[]{0x46, 0x00, 0x01});

        AutoNexus.Reading critical = new AutoNexus.Reading(1000, 100, 0, 0);    // 10%
        AutoNexus.Reading recovered = new AutoNexus.Reading(1000, 900, 0, 0);   // 90%

        check(nexus.decide(session, critical).fire(), "the first low reading fires");
        synchronized (out) {
            nexus.onHealth(session, critical);
        }
        checkEquals(1L, session.injectedCount(), "one escape was written");

        // Still critical, interval elapsed, budget left - and it must still decline, because HP has
        // not come back up. Without this gate the rule would nexus at the health packet's own rate.
        AutoNexus.Decision held = nexus.decide(session, critical);
        check(!held.fire(), "the rule holds fire until HP re-arms");
        check(held.reason().contains("re-arm"), "the decline says it is waiting to re-arm");

        // A single healthy reading is enough to re-arm, and does not itself fire.
        AutoNexus.Decision afterRecovery = nexus.decide(session, recovered);
        check(!afterRecovery.fire(), "a recovered reading does not fire");
        check(nexus.decide(session, critical).fire(),
                "after recovery, the next low reading fires again");

        // And with rearmPercent 0 there is no gate at all, so the setting cannot latch the rule off.
        config.rearmPercent = 0;
        AutoNexus ungated = new AutoNexus(config, log);
        Session other = new Session(44, "Game#44", "Game", false, 6410, "/x", "y:6410", log, ungated);
        other.upstreamWriter(new ByteArrayOutputStream());
        feed(other, Event.DIR_C2S, GmPacketType.HELLO, new byte[]{0x37, 0x00, 0x00});
        feed(other, Event.DIR_S2C, GmPacketType.MAP_INFO, new byte[]{0x05, 0x00});
        feed(other, Event.DIR_S2C, GmPacketType.HEALTH_UPDATE, new byte[]{0x46, 0x00, 0x01});
        check(ungated.decide(other, critical).fire(), "with no re-arm level the rule stays armed");
    }

    /**
     * An acknowledgement answers only an injection from <em>its own</em> session.
     *
     * <p>This is the realm transition, which is the flow that makes it matter: the client injects an
     * escape, the ack never arrives, the server retargets it to 6411 and a new session opens. If
     * acknowledgements were matched in one relay-wide queue - as they were - the new session's first
     * ack would be reported against the old session's injection, with a wrong sequence number, a wrong
     * latency and a wrong verdict. That verdict is the only evidence a reader has for whether the
     * instant escape works, so attributing it to the wrong attempt is worse than not reporting it.
     */
    private static void testAcknowledgementsArePerSession() {
        EventLog log = EventLog.memoryOnly(512);
        AutoNexus.Config config = new AutoNexus.Config();
        config.enabled = true;
        config.dryRun = false;
        config.thresholdPercent = 50;
        AutoNexus nexus = new AutoNexus(config, log);

        Session game = new Session(61, "Game#61", "Game", false, 6410, "/x", "y:6410", log, nexus);
        ByteArrayOutputStream gameOut = new ByteArrayOutputStream();
        game.upstreamWriter(gameOut);
        feed(game, Event.DIR_C2S, GmPacketType.HELLO, new byte[]{0x37, 0x00, 0x00});
        feed(game, Event.DIR_S2C, GmPacketType.MAP_INFO, new byte[]{0x05, 0x00});
        feed(game, Event.DIR_S2C, GmPacketType.HEALTH_UPDATE, healthPayload(1000, 100, 0, 0));
        synchronized (gameOut) {
            nexus.onHealth(game, new AutoNexus.Reading(1000, 100, 0, 0));
        }
        checkEquals(1L, game.injectedCount(), "the first session injected an escape");
        check(nexus.toMap().get("pendingAcks").toString().contains("61"),
                "the injection is pending against its own session");

        // A second session, whose client sends its own escape (which this relay did not write).
        Session realm = new Session(62, "Game_Slave#62", "Game_Slave", false, 6411, "/x", "z:6411", log, nexus);
        realm.upstreamWriter(new ByteArrayOutputStream());
        feed(realm, Event.DIR_C2S, GmPacketType.HELLO, new byte[]{0x37, 0x00, 0x00});
        feed(realm, Event.DIR_S2C, GmPacketType.MAP_INFO, new byte[]{0x05, 0x00});

        AutoNexus.PendingInjection matched = nexus.onEscapeAck(realm, true);
        check(matched == null,
                "an ack in a session with no injection pending is not attributed to another session's");
        check(nexus.toMap().get("lastAckNote").toString().contains("the client sent it"),
                "the ack is reported as the client's own escape");

        // And the real session's injection is still waiting for its own verdict.
        AutoNexus.PendingInjection own = nexus.onEscapeAck(game, true);
        check(own != null, "the injecting session's own ack still matches its injection");
        checkEquals(61L, own == null ? -1L : own.sessionId(), "the match names the session that injected");

        // A finished session's pending injection must not outlive it: that was the other half of the
        // bug, where a dead session's entry stayed at the head of a global queue.
        Session third = new Session(63, "Game#63", "Game", false, 6410, "/x", "y:6410", log, nexus);
        third.upstreamWriter(new ByteArrayOutputStream());
        feed(third, Event.DIR_C2S, GmPacketType.HELLO, new byte[]{0x37, 0x00, 0x00});
        feed(third, Event.DIR_S2C, GmPacketType.MAP_INFO, new byte[]{0x05, 0x00});
        feed(third, Event.DIR_S2C, GmPacketType.HEALTH_UPDATE, healthPayload(1000, 100, 0, 0));
        synchronized (third.upstreamWriter()) {
            nexus.onHealth(third, new AutoNexus.Reading(1000, 100, 0, 0));
        }
        nexus.forget(third);
        Session fourth = new Session(64, "Game#64", "Game", false, 6410, "/x", "y:6410", log, nexus);
        fourth.upstreamWriter(new ByteArrayOutputStream());
        check(nexus.onEscapeAck(fourth, true) == null,
                "a forgotten session's pending injection is not answered by a later session's ack");
    }

    private static void testConfigValidation() {
        AutoNexus.Config config = new AutoNexus.Config();
        config.apply(Map.of(
                "threshold_percent", "40",
                "enabled", "true",
                "min_interval_millis", "-5",
                "max_per_world", "99999",
                "nonsense", "1"));
        checkEquals(40, config.thresholdPercent, "an underscore alias sets the threshold");
        check(config.enabled, "a string 'true' enables the rule");
        checkEquals(0L, config.minIntervalMillis, "a negative interval is clamped to zero");
        checkEquals(1000, config.maxPerWorld, "an absurd budget is clamped");
    }

    // --- 6. the dashboard's filter list --------------------------------------------------------

    private static void testFilterDefaults() {
        List<networking.log.LogFilter> filters = networking.log.LogFilter.defaults();
        checkEquals("character hp", filters.get(0).name(), "the first filter is the HP view");
        check(filters.get(0).enabled(), "the HP filter is on by default");
        check(filters.stream().skip(1).noneMatch(networking.log.LogFilter::enabled),
                "every other default filter starts off");

        EventLog log = EventLog.memoryOnly(64);
        Session session = session(log);
        feed(session, Event.DIR_S2C, GmPacketType.HEALTH_UPDATE, new byte[]{0x46, 0x00, 0x01});
        feed(session, Event.DIR_S2C, 9999, new byte[]{(byte) 0x0F, 0x27, 0x01});

        List<Event> events = log.ring().last(20);
        Event health = events.stream().filter(e -> "HealthUpdate".equals(e.pkt)).findFirst().orElseThrow();
        Event other = events.stream().filter(e -> e.pktId != null && e.pktId == 9999).findFirst().orElseThrow();
        check(filters.get(0).matchesLoose(health), "the HP filter keeps a HealthUpdate");
        check(!filters.get(0).matchesLoose(other), "the HP filter hides an unrelated packet");
        check(networking.log.LogFilter.parse("x", true, "", "Market*", "")
                        .matchesLoose(Event.builder(Event.KIND_PACKET).pkt("MarketBoardSearch").build()),
                "a trailing * is a prefix match");
    }

    // --- 7. log correctness --------------------------------------------------------------------

    /**
     * The kick reason has to survive into the log, twice: once on the packet event, and again on the
     * session-close event the server's socket teardown produces a moment later. The second is the one
     * that matters after a run - a close with a cause and a close without one look identical
     * otherwise, and the cause is the whole point of decoding this packet.
     */
    private static void testKickReasonIsLogged() {
        byte[] payload = {(byte) 0xB9, 0x00, 0x05, 0x00, 's', 'p', 'e', 'e', 'd'};
        EventLog log = EventLog.memoryOnly(64);
        Session session = session(log);
        feed(session, Event.DIR_S2C, GmPacketType.KICKED, payload);

        Event kicked = log.ring().last(5).stream()
                .filter(e -> "Kicked".equals(e.pkt)).findFirst().orElseThrow();
        checkEquals("speed", kicked.data.get("reason"), "the kick reason did not reach the event's data");
        check(Boolean.TRUE.equals(kicked.data.get("kicked")), "the kick event is not marked as a kick");
        check(kicked.note != null && kicked.note.contains("speed"),
                "the kick event's note does not name the reason: " + kicked.note);

        session.onClosed("peer hung up");
        Event closed = log.ring().last(5).stream()
                .filter(e -> Event.KIND_SESSION.equals(e.kind)).findFirst().orElseThrow();
        checkEquals("speed", closed.data.get("kickedReason"),
                "the session-close event lost the server's kick reason");
        check(closed.note != null && closed.note.contains("speed"),
                "the close note does not repeat the kick reason: " + closed.note);
    }

    private static void testJsonEscaping() {
        // The log is read by other tools; a quote or a control character in a name must not be able to
        // produce a line no parser accepts.
        checkEquals("\"a\\\"b\"", networking.util.Json.quote("a\"b"), "a quote is escaped");
        checkEquals("\"a\\\\b\"", networking.util.Json.quote("a\\b"), "a backslash is escaped");
        checkEquals("\"a\\u0001b\"", networking.util.Json.quote("a\u0001b"), "a control character is escaped");
        checkEquals("\"é\"", networking.util.Json.quote("é"), "non-ASCII is emitted as-is for UTF-8");
        checkEquals("1", networking.util.Json.value(1.0d), "an integral double prints without .0");
        checkEquals("null", networking.util.Json.value(Double.NaN), "NaN is not emitted as a bare token");

        EventLog log = EventLog.memoryOnly(64);
        Event event = Event.builder(Event.KIND_NOTE).session("relay").note("a \"quoted\" note").build();
        check(event.toJson().contains("\\\"quoted\\\""), "a note survives JSON escaping");
        check(event.toJson().startsWith("{\"seq\":"), "the event's field order is stable");
    }
}
