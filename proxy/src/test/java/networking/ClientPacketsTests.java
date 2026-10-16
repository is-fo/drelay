package networking;

import networking.packets.ClientPackets;
import networking.packets.GmPacketType;
import networking.util.Fields;

import java.io.ByteArrayOutputStream;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;

/**
 * Checks the client→server field extraction that the relay relies on for the player's clock.
 *
 * <p>Run with {@code java -cp target/classes networking.ClientPacketsTests}. Exits non-zero on the
 * first failure.
 *
 * <h2>Why this is checked against captured bytes</h2>
 *
 * <p>{@link ClientPackets} exists because three of the client's most frequent input packets have no
 * full codec: {@code Move} (74), {@code Shoot} (6) and {@code ActivateObject} (84). The relay reads one
 * field out of them today - the client's {@code Time}, a monotonic millisecond counter - and that
 * field's <em>offset</em> is the whole risk: it sits after a fixed-width prefix
 * ({@code float,float} for Move; {@code float,float,float} then two fields after it for Shoot; a varint
 * and a length-prefixed string for ActivateObject). Shift it by one byte and the relay reads a
 * plausible-looking wrong number for the rest of the session.
 *
 * <p>So every case below is a literal byte string. Two of them are real captured packets from
 * {@code work/logs/relay.out.log}, which is what makes them worth trusting: a round-trip through my own
 * encoder would agree with my own decoder even if both were wrong about the layout.
 *
 * <p>The other assertion is that a <strong>truncated or hostile payload yields no fields rather than an
 * exception</strong>. These readers run on the forwarding path for every input packet in both
 * directions, and a throw there would drop a live session over a diagnostic.
 */
public final class ClientPacketsTests {

    private static final List<String> FAILURES = new ArrayList<>();
    private static int checks;

    public static void main(String[] args) throws Exception {
        testMove();
        testMoveCaptured();
        testShoot();
        testActivateObjectCaptured();
        testTruncation();
        testUnknownId();
        testClientPacketsDoNotDisturbCodecs();

        if (FAILURES.isEmpty()) {
            IO.println("PASS: " + checks + " client-packet field checks passed");
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

    /** Float comparison with a tolerance: a decoded float is bit-exact here, but state it as a range. */
    private static void checkClose(float expected, float actual, float tolerance, String description) {
        checks++;
        if (!(Math.abs(expected - actual) <= tolerance)) {
            FAILURES.add(description + " (expected " + expected + " +/- " + tolerance + ", got " + actual + ")");
        }
    }

    private static byte[] concat(byte[]... parts) {
        var out = new ByteArrayOutputStream();
        for (byte[] part : parts) {
            out.writeBytes(part);
        }
        return out.toByteArray();
    }

    private static byte[] varint(int value) throws Exception {
        var out = new ByteArrayOutputStream();
        new GameWriter(out).writeVarint(value);
        return out.toByteArray();
    }

    private static byte[] leFloat(float value) throws Exception {
        var out = new ByteArrayOutputStream();
        new GameWriter(out).writeFloatLE(value);
        return out.toByteArray();
    }

    private static byte[] leInt32(int value) throws Exception {
        var out = new ByteArrayOutputStream();
        new GameWriter(out).writeInt32(value);
        return out.toByteArray();
    }

    private static byte[] leString32(String value) throws Exception {
        byte[] raw = value.getBytes(StandardCharsets.UTF_8);
        return concat(leInt32(raw.length), raw);
    }

    private static byte[] leUInt16(int value) throws Exception {
        var out = new ByteArrayOutputStream();
        new GameWriter(out).writeUInt16(value);
        return out.toByteArray();
    }

    /** The payload as the relay sees it: a 2-byte little-endian type id then the body. */
    private static byte[] payload(int id, byte[] body) throws Exception {
        return concat(leUInt16(id), body);
    }

    private static Number number(Map<String, Object> fields, String key) {
        Object value = fields.get(key);
        return value instanceof Number n ? n : null;
    }

    // --- 1. Move ------------------------------------------------------------------------------

    private static void testMove() throws Exception {
        // GmMove: [float X][float Y][varint Time]
        byte[] body = concat(leFloat(127.8843f), leFloat(107.8739f), varint(27322));
        Map<String, Object> fields = ClientPackets.decode(GmPacketType.MOVE, payload(GmPacketType.MOVE, body));
        checkEquals(27322L, number(fields, "time") == null ? null : number(fields, "time").longValue(),
                "Move's Time is read after the two position floats");
        checkEquals(11, body.length,
                "8 bytes of point plus 3 bytes of varint: 27322 needs 6+7+7 value bits");

        // A Move with no time field at all: the position is present, the clock is unknown.
        Map<String, Object> truncated = ClientPackets.decode(GmPacketType.MOVE,
                payload(GmPacketType.MOVE, concat(leFloat(1f), leFloat(2f))));
        check(truncated.get("time") == null, "a Move without a Time field reports no time");

        // A large time: the varint grows as the session runs on.
        long late = 5_000_000L;
        byte[] lateBody = concat(leFloat(1f), leFloat(2f), varint((int) late));
        Map<String, Object> lateFields = ClientPackets.decode(GmPacketType.MOVE,
                payload(GmPacketType.MOVE, lateBody));
        checkEquals(late, number(lateFields, "time").longValue(),
                "a multi-byte varint time is read correctly");
    }

    /**
     * A real captured {@code Move}, decoded by hand.
     *
     * <p>This is the offset check that matters: the relay reads the player's clock out of this packet
     * on every move, and the field sits at a fixed offset after the two position floats. The captured
     * body is {@code 0000E942 0000F142 B9F301 00} — X = 116.5, Y = 120.5, then the varint
     * {@code B9 F3 01}, then one trailing byte.
     *
     * <p>That trailing byte is worth a note, because it is a <em>discrepancy</em> rather than a
     * confirmation: the decompiled {@code GmMove} declares {@code HistoryCount} as a byte and then a
     * {@code HistoryCount}-long array after {@code Time}, which this payload has no room for. The
     * captured session was on build 5.8.2 and the decompiled source is that same build, so either the
     * field list is richer than the source suggests or its order differs. It does not affect the relay
     * - {@code Time} is before the extra field either way, and the relay reads no further than that -
     * but it is exactly why this class reads one field and stops, instead of trying to consume a
     * {@code Move} to its last byte. Recorded here so the next person does not assume the layout is
     * fully accounted for.
     */
    private static void testMoveCaptured() throws Exception {
        byte[] captured = java.util.HexFormat.of().parseHex("4A000000E9420000F142B9F30100");
        checkEquals(14, captured.length, "the captured Move payload is 14 bytes including the type id");

        // The varint, read on its own, is the client's clock. This is the value the relay records.
        // B9 F3 01 = 0x39 | (0x73 << 6) | (0x01 << 13) = 57 + 7360 + 8192 = 15609 ms after the client
        // started counting - about 16 seconds, consistent with a player who joined the tavern moments
        // earlier. (An earlier version of this test guessed "126000..127000" from a mis-decomposed
        // byte offset; the value is checked against that arithmetic, not against a guessed range.)
        var timeReader = new GameReader(new java.io.ByteArrayInputStream(captured, 10, 3));
        int time = timeReader.readVarint();
        checkEquals(15609, time, "B9 F3 01 is 15609 ms by the 6, 6, 7 grouping");

        Map<String, Object> fields = ClientPackets.decode(GmPacketType.MOVE, captured);
        checkEquals((long) time, number(fields, "time").longValue(),
                "the decoder reads exactly the varint at payload offset 10");

        // And the position ahead of it, so the offset is anchored to something real.
        var positionReader = new GameReader(new java.io.ByteArrayInputStream(captured, 2, 8));
        float[] point = positionReader.readPoint();
        checkClose(116.5f, point[0], 0.001f, "the captured Move's X is 116.5");
        checkClose(120.5f, point[1], 0.001f, "the captured Move's Y is 120.5");
    }

    // --- 2. Shoot -----------------------------------------------------------------------------

    private static void testShoot() throws Exception {
        // GmShoot: [float X][float Y][float Angle][varint Time][varint BulletId][byte BulletIndex]
        // The 63 captured Shoot packets are all 19 bytes of body, with the angle at payload offset 10.
        byte[] body = concat(leFloat(127.8843f), leFloat(107.8739f), leFloat(1.5707964f),
                varint(27322), varint(4242), new byte[]{7});
        byte[] full = payload(GmPacketType.SHOOT, body);
        Map<String, Object> fields = ClientPackets.decode(GmPacketType.SHOOT, full);

        checkEquals(27322L, number(fields, "time").longValue(), "Shoot's Time is read after the angle");
        checkEquals(4242L, number(fields, "bulletId").longValue(), "Shoot's BulletId follows the time");
        checkEquals(7, number(fields, "bulletIndex").intValue(), "Shoot's BulletIndex is one byte");
        checkClose(1.5707964f, number(fields, "angle").floatValue(), 0.0001f,
                "Shoot's angle is the third float");
        check(fields.get("angle") instanceof Float, "the angle is reported as a float, not a double");

        // The angle really does sit at payload offset 10, which is what makes an in-place aim
        // correction a four-byte overwrite with no re-encoding. Assert the offset, not just the value.
        var reader = new GameReader(new java.io.ByteArrayInputStream(full, 10, 4));
        checkClose(1.5707964f, reader.readFloatLE(), 0.0001f,
                "Shoot's angle is at payload offset 10 (the layout an aim rewrite would depend on)");
    }

    // --- 3. ActivateObject against the real capture --------------------------------------------

    private static void testActivateObjectCaptured() throws Exception {
        // The captured packet from work/logs, 21 bytes on the wire including the 2-byte type id:
        //   54 00              id 84
        //   B4 CD 30           varint ObjectId 398196
        //   00 00 00 00        string32 "" (the field order is Value BEFORE Point)
        //   BD C4 FF 42        float X 127.8843
        //   70 BF D7 42        float Y 107.8739
        //   BA 6A 00 00        int32 Time 27322
        byte[] captured = java.util.HexFormat.of().parseHex("5400B4CD3000000000BDC4FF4270BFD742BA6A0000");
        checkEquals(21, captured.length, "the captured ActivateObject is 21 bytes");

        Map<String, Object> fields = ClientPackets.decode(84, captured);
        checkEquals(398196L, number(fields, "objectId").longValue(),
                "the captured ObjectId decodes from the varint B4 CD 30");
        checkEquals("", fields.get("value"), "the captured Value is the empty string");
        checkEquals(27322, number(fields, "time").intValue(),
                "the captured Time decodes as a plain little-endian int32");
        // The field order is what discriminates the two readings of this layout: the alternative
        // (position first) would put X at exactly 0.0 and read the string length as 1121435504.
        checkClose(127.8843f, number(fields, "x").floatValue(), 0.001f,
                "X is read after the string, not from the first word");
        checkClose(107.8739f, number(fields, "y").floatValue(), 0.001f,
                "Y follows X");

        // And a payload built the same way from the documented layout must decode identically.
        byte[] rebuilt = payload(84, concat(varint(398196), leString32(""),
                leFloat(127.8843f), leFloat(107.8739f), leInt32(27322)));
        checkEquals(21, rebuilt.length, "a rebuilt ActivateObject is also 21 bytes");
        Map<String, Object> rebuiltFields = ClientPackets.decode(84, rebuilt);
        checkEquals(27322, number(rebuiltFields, "time").intValue(),
                "a rebuilt ActivateObject decodes to the same Time");
        checkClose(127.8843f, number(rebuiltFields, "x").floatValue(), 0.001f,
                "a rebuilt ActivateObject decodes to the same X");
    }

    // --- 4. hostile input ---------------------------------------------------------------------

    private static void testTruncation() throws Exception {
        // Every prefix of a Move payload: the reader must either return fields or return none, and
        // must never throw. This runs for every input packet in a live session, so an exception here
        // would take down a session over a log line.
        for (int length = 0; length <= 12; length++) {
            final int len = length;
            try {
                ClientPackets.decode(GmPacketType.MOVE, new byte[len]);
            } catch (RuntimeException e) {
                FAILURES.add("a " + len + "-byte Move payload threw " + e);
            }
            checks++;
        }

        // The same for Shoot, which has more fields to run out of.
        for (int length = 0; length <= 20; length++) {
            final int len = length;
            try {
                ClientPackets.decode(GmPacketType.SHOOT, new byte[len]);
            } catch (RuntimeException e) {
                FAILURES.add("a " + len + "-byte Shoot payload threw " + e);
            }
            checks++;
        }

        // A string32 length that claims more bytes than exist must not be honoured.
        byte[] lying = payload(84, concat(varint(1), leInt32(1_000_000), new byte[]{1, 2, 3}));
        Map<String, Object> fields = ClientPackets.decode(84, lying);
        check(fields.get("value") == null, "a string32 longer than the payload is refused");
        check(fields.get("x") == null, "fields after an impossible string are not guessed at");

        // A negative string32 length is equally impossible.
        byte[] negative = payload(84, concat(varint(1), leInt32(-5), new byte[]{1, 2, 3}));
        check(ClientPackets.decode(84, negative).get("value") == null,
                "a negative string32 length is refused");

        // A varint that never terminates before the buffer ends.
        byte[] unterminated = payload(74, concat(leFloat(1f), leFloat(2f),
                new byte[]{(byte) 0x80, (byte) 0x80, (byte) 0x80}));
        check(ClientPackets.decode(GmPacketType.MOVE, unterminated).get("time") == null,
                "an unterminated varint reports no time");
    }

    // --- 5. what it must NOT do ---------------------------------------------------------------

    private static void testUnknownId() throws Exception {
        Fields none = ClientPackets.decode(9999, payload(9999, new byte[]{1, 2, 3}));
        check(none.isEmpty(), "an id with no client-packet layout decodes to no fields");

        // A queue id must not be interpreted as a game packet layout (id 2 is QPosition, and it is
        // also nothing at all on the game side). This is the same separation PacketRegistry enforces.
        Fields queueish = ClientPackets.decode(1, new byte[]{1, 6});
        check(queueish.isEmpty(), "an unrelated id is not read with a game-packet layout");
    }

    private static void testClientPacketsDoNotDisturbCodecs() throws Exception {
        // The relay calls Injection.decode (registered codecs) and ClientPackets.decode (these
        // layouts) for the same packet. A registered id must be handled by the registry, and this
        // class must stay quiet about it, or the same payload would be decoded twice with two
        // different field sets.
        byte[] health = java.util.HexFormat.of().parseHex("4600830B830B0000");
        Map<String, Object> fromClient = ClientPackets.decode(GmPacketType.HEALTH_UPDATE, health);
        check(fromClient.isEmpty(),
                "HealthUpdate has a registry codec and is not also read as a raw client layout");

        Map<String, Object> fromInjection = networking.packets.Injection.decode(
                false, GmPacketType.HEALTH_UPDATE, health);
        checkEquals(707, ((Number) fromInjection.get("health")).intValue(),
                "the registry codec still reads the health packet");
    }
}
