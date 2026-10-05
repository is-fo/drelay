package networking;

import networking.packets.GmPacketType;
import networking.packets.PacketRegistry;
import networking.packets.QPacketType;
import networking.packets.gmpackets.HealthUpdatePacket;
import networking.packets.gmpackets.ReconnectPacket;
import networking.packets.qpackets.QError;
import networking.packets.qpackets.QHello;
import networking.packets.qpackets.QJoin;
import networking.packets.qpackets.QPosition;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.Set;

/**
 * Round-trip and layout checks for the client's serialization primitives.
 *
 * <p>Run with {@code java -cp target/classes networking.PrimitiveTests}. Exits non-zero on the
 * first failure so it can be used as a build gate. The varint cases are the ones that matter
 * most: the format is custom rather than protobuf, and every packet depends on it.
 */
public final class PrimitiveTests {

    private static final List<String> FAILURES = new ArrayList<>();

    public static void main(String[] args) throws Exception {
        testVarintBoundaries();
        testVarintWireLayout();
        testVarintRoundTrip();
        testLittleEndianLayout();
        testStringLayout();
        testBoolLayout();
        testPointLayout();
        testHealthUpdatePacket();
        testReconnectPacket();
        testQueuePackets();
        testTypeWidth();
        testRegistry();
        testPacketTypeIds();

        if (FAILURES.isEmpty()) {
            IO.println("PASS: all primitive, packet and packet-id checks passed");
            return;
        }
        IO.println("FAIL: " + FAILURES.size() + " check(s) failed");
        for (String failure : FAILURES) {
            IO.println("  - " + failure);
        }
        System.exit(1);
    }

    private static void check(boolean condition, String description) {
        if (!condition) {
            FAILURES.add(description);
        }
    }

    private static void checkEquals(Object expected, Object actual, String description) {
        if (!java.util.Objects.equals(expected, actual)) {
            FAILURES.add(description + " (expected " + expected + ", got " + actual + ")");
        }
    }

    private static byte[] encodeVarint(int value) throws Exception {
        var out = new ByteArrayOutputStream();
        var writer = new GameWriter(out);
        writer.writeVarint(value);
        return out.toByteArray();
    }

    private static int decodeVarint(byte[] bytes) throws Exception {
        return new GameReader(new ByteArrayInputStream(bytes)).readVarint();
    }

    private static void testVarintBoundaries() throws Exception {
        // The first byte carries 6 value bits (bits 0-5), the sign at bit 6 and the continuation at
        // bit 7; each later byte carries 7 bits, with the FIRST continuation byte starting at bit 6.
        // The group widths are therefore 6, 6, 7, 7, ... and not the 6, 7, 7 of a protobuf varint.
        checkEquals(1, encodeVarint(1).length, "varint(1) is one byte");
        checkEquals(1, encodeVarint(63).length, "varint(63) still fits one byte");
        check((encodeVarint(63)[0] & 0x80) == 0, "varint(63) has no continuation bit");

        // 64 needs a second byte, and the first byte must then be a continuation.
        byte[] sixtyFour = encodeVarint(64);
        checkEquals(2, sixtyFour.length, "varint(64) needs two bytes");
        check((sixtyFour[0] & 0x80) != 0, "varint(64) sets the continuation bit");
        checkEquals(0, sixtyFour[0] & 0x3F, "varint(64) low 6 bits are zero");
        checkEquals(1, sixtyFour[1] & 0x7F, "varint(64) second byte carries the carry");

        // Two bytes hold 6 + 7 = 13 bits, so 8191 is the largest two-byte value and 8192 needs three.
        checkEquals(2, encodeVarint(8191).length, "varint(8191) is the largest two-byte value");
        checkEquals(3, encodeVarint(8192).length, "varint(8192) needs three bytes");

        // The sign lives in bit 6 of the first byte, not in a value bit.
        byte[] minusOne = encodeVarint(-1);
        checkEquals(1, minusOne.length, "varint(-1) is one byte");
        check((minusOne[0] & 0x40) != 0, "varint(-1) sets the sign bit");
        checkEquals(1, minusOne[0] & 0x3F, "varint(-1) magnitude is 1");

        checkEquals(-63, decodeVarint(encodeVarint(-63)), "varint(-63) round-trips");
        checkEquals(-64, decodeVarint(encodeVarint(-64)), "varint(-64) round-trips");
        checkEquals(Integer.MAX_VALUE, decodeVarint(encodeVarint(Integer.MAX_VALUE)),
                "varint(Integer.MAX_VALUE) round-trips");
        checkEquals(Integer.MIN_VALUE + 1, decodeVarint(encodeVarint(Integer.MIN_VALUE + 1)),
                "varint(Integer.MIN_VALUE+1) round-trips");
    }

    /**
     * The byte layouts, pinned to values that were measured rather than derived.
     *
     * <p>Every pair below except the last comes from real traffic: {@code 830B} is the
     * {@code MaxHealth} out of the captured {@code 4600830B830B0000}, and its value (707) is fixed
     * independently by the same character's fixed-width {@code StatsType.Hp} int16 in the matching
     * {@code GmUpdate}. {@code 8B64} is the {@code GmReconnect} port 6411 measured on 2026-10-02.
     * Those two are what resolved the group widths; a decoder that reads the first continuation byte
     * at bit 7 turns 707 into 1411 and 6411 into 12811.
     */
    private static void testVarintWireLayout() throws Exception {
        checkEquals("83 0B", hex(encodeVarint(707)),
                "707 encodes as 83 0B (captured MaxHealth, confirmed by the fixed-width Hp stat)");
        checkEquals(707, decodeVarint(new byte[]{(byte) 0x83, 0x0B}),
                "83 0B decodes as 707, not as the 1411 a bit-7 shift would produce");
        checkEquals("A4 0A", hex(encodeVarint(676)), "676 encodes as A4 0A");
        checkEquals("AA 09", hex(encodeVarint(618)), "618 encodes as AA 09");
        checkEquals("BA 03", hex(encodeVarint(250)), "250 encodes as BA 03");
        checkEquals("89 02", hex(encodeVarint(137)), "137 encodes as 89 02");
        checkEquals("8B 64", hex(encodeVarint(6411)),
                "6411 encodes as 8B 64 (the captured GmReconnect port)");
        checkEquals(6411, decodeVarint(new byte[]{(byte) 0x8B, 0x64}), "8B 64 decodes as 6411");
        checkEquals("43", hex(encodeVarint(-3)), "-3 encodes as a single sign-bit byte 43");

        // And the whole HealthUpdate payload of the captured 46 00 83 0B 83 0B 00 00.
        var decoded = new HealthUpdatePacket();
        decoded.read(new GameReader(new ByteArrayInputStream(
                new byte[]{(byte) 0x83, 0x0B, (byte) 0x83, 0x0B, 0x00, 0x00})));
        checkEquals(707, decoded.maxHealth, "the captured HealthUpdate's maxHealth is 707");
        checkEquals(707, decoded.health, "the captured HealthUpdate's health is 707");
        checkEquals(0, decoded.shield, "the captured HealthUpdate's shield is 0");
        checkEquals(0, decoded.barrier, "the captured HealthUpdate's barrier is 0");
    }

    private static void testVarintRoundTrip() throws Exception {
        int[] values = {
                0, 1, 2, 5, 31, 32, 63, 64, 65, 100, 127, 128, 255, 256, 1000, 4095, 4096,
                65535, 65536, 1_000_000, 100_000_000, Integer.MAX_VALUE,
                -1, -2, -63, -64, -65, -100, -1000, -65536, -1_000_000
        };
        for (int value : values) {
            int decoded = decodeVarint(encodeVarint(value));
            checkEquals(value, decoded, "varint round-trip for " + value);
        }
    }

    private static void testLittleEndianLayout() throws Exception {
        // Payload fields are little-endian: the client writes through .NET BinaryWriter.
        var out = new ByteArrayOutputStream();
        var writer = new GameWriter(out);
        writer.writeInt32(0x01020304);
        byte[] bytes = out.toByteArray();
        checkEquals(4, bytes.length, "int32 is 4 bytes");
        checkEquals("04 03 02 01", hex(bytes), "int32 0x01020304 is little-endian");

        var out16 = new ByteArrayOutputStream();
        new GameWriter(out16).writeUInt16(0x0102);
        checkEquals("02 01", hex(out16.toByteArray()), "uint16 0x0102 is little-endian");

        // And the reader must agree.
        var reader = new GameReader(new ByteArrayInputStream(new byte[]{0x04, 0x03, 0x02, 0x01}));
        checkEquals(0x01020304, reader.readInt32(), "reader parses little-endian int32");
    }

    private static void testStringLayout() throws Exception {
        var out = new ByteArrayOutputStream();
        var writer = new GameWriter(out);
        writer.writeString8("hi");
        writer.writeString16("hey");
        writer.writeString32("hello");
        byte[] bytes = out.toByteArray();

        // 1-byte length + 2, 2-byte length + 3, 4-byte length + 5
        checkEquals(2 + 1 + 3 + 2 + 5 + 4, bytes.length, "string block length");
        checkEquals(2, bytes[0] & 0xFF, "string8 length prefix");

        var reader = new GameReader(new ByteArrayInputStream(bytes));
        checkEquals("hi", reader.readString8(), "string8 round-trip");
        checkEquals("hey", reader.readString16(), "string16 round-trip");
        checkEquals("hello", reader.readString32(), "string32 round-trip");

        // UTF-8: a multi-byte character must be counted in bytes, not characters.
        var utf8 = new ByteArrayOutputStream();
        new GameWriter(utf8).writeString16("é");
        checkEquals(2, utf8.toByteArray()[0] & 0xFF, "string16 length is byte count for UTF-8");
    }

    private static void testBoolLayout() throws Exception {
        var out = new ByteArrayOutputStream();
        var writer = new GameWriter(out);
        writer.writeBool(true);
        writer.writeBool(false);
        byte[] bytes = out.toByteArray();
        checkEquals(2, bytes.length, "two bools are two bytes");
        checkEquals(1, bytes[0] & 0xFF, "true encodes as 1");

        var reader = new GameReader(new ByteArrayInputStream(bytes));
        check(reader.readBool(), "true round-trips");
        check(!reader.readBool(), "false round-trips");
        // The client treats any non-zero byte as true.
        check(new GameReader(new ByteArrayInputStream(new byte[]{0x7F})).readBool(),
                "non-zero byte reads as true");
    }

    private static void testPointLayout() throws Exception {
        var out = new ByteArrayOutputStream();
        new GameWriter(out).writePoint(1.5f, -2.25f);
        byte[] bytes = out.toByteArray();
        checkEquals(8, bytes.length, "GamePoint is two floats");

        var reader = new GameReader(new ByteArrayInputStream(bytes));
        float[] point = reader.readPoint();
        checkEquals(1.5f, point[0], "GamePoint.X round-trips");
        checkEquals(-2.25f, point[1], "GamePoint.Y round-trips");

        // Little-endian check: 1.5f is 0x3FC00000, so the bytes must read 00 00 C0 3F.
        checkEquals("00 00 C0 3F", hex(new byte[]{bytes[0], bytes[1], bytes[2], bytes[3]}),
                "GamePoint.X is little-endian");

        var intOut = new ByteArrayOutputStream();
        new GameWriter(intOut).writeIntPoint(300, -7);
        var intReader = new GameReader(new ByteArrayInputStream(intOut.toByteArray()));
        int[] intPoint = intReader.readIntPoint();
        checkEquals(300, intPoint[0], "IntPoint.X round-trips through varints");
        checkEquals(-7, intPoint[1], "IntPoint.Y round-trips through varints");
    }

    private static void testHealthUpdatePacket() throws Exception {
        // GmHealthUpdate: four varints, all fields short in the client's model.
        var packet = new HealthUpdatePacket();
        packet.maxHealth = 250;
        packet.health = 137;
        packet.shield = 60;
        packet.barrier = -3;

        var out = new ByteArrayOutputStream();
        packet.write(new GameWriter(out));
        byte[] payload = out.toByteArray();

        var decoded = new HealthUpdatePacket();
        decoded.read(new GameReader(new ByteArrayInputStream(payload)));
        checkEquals(250, decoded.maxHealth, "HealthUpdate.maxHealth round-trips");
        checkEquals(137, decoded.health, "HealthUpdate.health round-trips");
        checkEquals(60, decoded.shield, "HealthUpdate.shield round-trips");
        checkEquals(-3, decoded.barrier, "HealthUpdate.barrier round-trips");

        // Verified against the capture: 707 -> 83 0B, 676 -> A4 0A, 618 -> AA 09 (all three confirmed
        // by the same character's fixed-width StatsType.Hp int16 in the matching GmUpdate).
        checkEquals(6, payload.length,
                "HealthUpdate encodes to the expected varint sizes");
        checkEquals("BA 03 89 02 3C 43", hex(payload),
                "HealthUpdate varint byte layout");
    }

    private static void testReconnectPacket() throws Exception {
        // GmReconnect: string32 host, varint port, bool toBeyond, optional int64 characterId.
        // The bytes below are written out by hand from the client's Read/Write order, so this pins
        // the codec to the wire format rather than to itself.
        byte[] wire = {
                0x0D, 0x00, 0x00, 0x00,                                     // string32 length 13
                0x31, 0x38, 0x2E, 0x31, 0x34, 0x35, 0x2E, 0x31,             // "18.145.1"
                0x36, 0x31, 0x2E, 0x32, 0x35,                               // "61.25"
                (byte) 0x8B, 0x64,                                          // varint 6411
                0x01,                                                       // toBeyond = true
                (byte) 0xD2, 0x02, (byte) 0x96, 0x49, 0x00, 0x00, 0x00, 0x00 // 1234567890, little-endian
        };

        var decoded = new ReconnectPacket();
        decoded.read(new GameReader(new ByteArrayInputStream(wire)));
        checkEquals("18.145.161.25", decoded.host, "Reconnect.host decodes from string32");
        checkEquals(6411, decoded.port, "Reconnect.port decodes from varint 8B 64");
        check(decoded.toBeyond, "Reconnect.toBeyond decodes");
        checkEquals(1234567890L, decoded.characterId, "Reconnect.characterId decodes as little-endian int64");

        var out = new ByteArrayOutputStream();
        decoded.write(new GameWriter(out));
        checkEquals(hex(wire), hex(out.toByteArray()), "Reconnect re-encodes byte for byte");

        // With no trailing id the client leaves CharacterId at -1; the codec must do the same
        // instead of failing on a short payload.
        byte[] withoutId = {0x01, 0x00, 0x00, 0x00, 0x78, 0x05, 0x00};
        var shortPacket = new ReconnectPacket();
        shortPacket.read(new GameReader(new ByteArrayInputStream(withoutId)));
        checkEquals("x", shortPacket.host, "Reconnect.host decodes for a 1-char host");
        checkEquals(5, shortPacket.port, "Reconnect.port decodes for a small varint");
        checkEquals(-1L, shortPacket.characterId, "a missing character id stays -1");
    }

    private static void testQueuePackets() throws Exception {
        // QHello: one string8
        var hello = new QHello("Zenith");
        var helloOut = new ByteArrayOutputStream();
        hello.write(new GameWriter(helloOut));
        checkEquals("06 5A 65 6E 69 74 68", hex(helloOut.toByteArray()),
                "QHello encodes name length + UTF-8 bytes");
        var helloBack = new QHello();
        helloBack.read(new GameReader(new ByteArrayInputStream(helloOut.toByteArray())));
        checkEquals("Zenith", helloBack.gameServerName, "QHello.gameServerName round-trips");

        // QJoin: one string8 token
        var join = new QJoin("abc");
        var joinOut = new ByteArrayOutputStream();
        join.write(new GameWriter(joinOut));
        var joinBack = new QJoin();
        joinBack.read(new GameReader(new ByteArrayInputStream(joinOut.toByteArray())));
        checkEquals("abc", joinBack.queueToken, "QJoin.queueToken round-trips");

        // QPosition: one varint
        var position = new QPosition(300);
        var posOut = new ByteArrayOutputStream();
        position.write(new GameWriter(posOut));
        checkEquals("AC 04", hex(posOut.toByteArray()), "QPosition(300) is varint AC 04");
        var posBack = new QPosition();
        posBack.read(new GameReader(new ByteArrayInputStream(posOut.toByteArray())));
        checkEquals(300, posBack.position, "QPosition.position round-trips");

        // QError: one string8 message
        var error = new QError("full");
        var errOut = new ByteArrayOutputStream();
        error.write(new GameWriter(errOut));
        var errBack = new QError();
        errBack.read(new GameReader(new ByteArrayInputStream(errOut.toByteArray())));
        checkEquals("full", errBack.message, "QError.message round-trips");
    }

    private static void testTypeWidth() {
        // GmPacket.GetData() writes TypeId, a ushort; QPacket.GetData() writes Id, one byte. Reading a
        // game id from payload[0] alone is right for every id below 256 and wrong for JumpScare (320),
        // which is exactly why the 2-byte game type went unnoticed until a GmReconnect was decoded.
        checkEquals(320, Relay.typeId(false, new byte[]{0x40, 0x01}),
                "game type is a 2-byte little-endian ushort");
        checkEquals(70, Relay.typeId(false, new byte[]{0x46, 0x00, (byte) 0x83, 0x0B}),
                "game id does not include body bytes");
        checkEquals(1, Relay.typeId(true, new byte[]{0x01, 0x06}),
                "queue type is a single byte");
        checkEquals(64, Relay.typeId(true, new byte[]{0x40, 0x01}),
                "queue id does not read a second byte");
        checkEquals(2, Relay.bodyOffset(false), "a game body starts after two type bytes");
        checkEquals(1, Relay.bodyOffset(true), "a queue body starts after one type byte");
    }

    private static void testRegistry() {
        PacketRegistry.init();

        check(PacketRegistry.createGameOrNull(GmPacketType.HEALTH_UPDATE) instanceof HealthUpdatePacket,
                "game registry resolves HealthUpdate");
        check(PacketRegistry.createQueueOrNull(QPacketType.JOIN) instanceof QJoin,
                "queue registry resolves Join");
        check(PacketRegistry.createQueueOrNull(QPacketType.HELLO) instanceof QHello,
                "queue registry resolves Hello");

        // The two id spaces must stay separate: id 4 is RegisterResp on the game service but
        // QError on the queue, and id 1 is Update versus QHello.
        check(PacketRegistry.createGameOrNull(QPacketType.HELLO) == null,
                "queue id 1 must not resolve as a game packet");
        check(PacketRegistry.createQueueOrNull(GmPacketType.HEALTH_UPDATE) == null,
                "game id 70 must not resolve as a queue packet");
        check(PacketRegistry.createGameOrNull(GmPacketType.RECONNECT) instanceof ReconnectPacket,
                "game registry resolves Reconnect");
        checkEquals(Set.of(36, 66, 70, 158, 184, 290, 291), PacketRegistry.gameIds(),
                "game registry holds only verified ids");
        checkEquals(Set.of(1, 2, 3, 4), PacketRegistry.queueIds(), "queue registry holds the four queue ids");

        checkEquals("HealthUpdate (codec)", PacketRegistry.describe(false, 70),
                "describe marks a known game id");
        checkEquals("Update", PacketRegistry.describe(false, 1),
                "describe names an unmodelled game id");
        // QPacketType names the member "Hello" (the "Q" prefix is on the class, not the enum).
        checkEquals("Hello (codec)", PacketRegistry.describe(true, 1),
                "describe marks a known queue id");

        // The escape conversation is the relay's own subject matter now, so its codecs are pinned here.
        check(PacketRegistry.createGameOrNull(GmPacketType.ESCAPE_ACK)
                instanceof networking.packets.gmpackets.EscapeAckPacket, "registry resolves EscapeAck");
        check(PacketRegistry.createGameOrNull(GmPacketType.FORCED_ESCAPE)
                instanceof networking.packets.gmpackets.ForcedEscapePacket, "registry resolves ForcedEscape");
        check(PacketRegistry.createGameOrNull(GmPacketType.SAFE_AREA_STATE)
                instanceof networking.packets.gmpackets.SafeAreaStatePacket, "registry resolves SafeAreaState");
        check(PacketRegistry.createGameOrNull(GmPacketType.ESCAPE_CAST_STATE)
                instanceof networking.packets.gmpackets.EscapeCastStatePacket, "registry resolves EscapeCastState");
    }

    private static void testPacketTypeIds() {
        checkEquals("Unknown", GmPacketType.name(0), "GmPacketType 0 is Unknown");
        checkEquals("HealthUpdate", GmPacketType.name(70), "GmPacketType 70 is HealthUpdate");
        checkEquals(70, GmPacketType.id("HealthUpdate"), "HealthUpdate id is 70");
        checkEquals("Escape", GmPacketType.name(66), "GmPacketType 66 is Escape");
        checkEquals("Hello", GmPacketType.name(55), "GmPacketType 55 is Hello");
        checkEquals("HelloResp", GmPacketType.name(28), "GmPacketType 28 is HelloResp");
        checkEquals("Goto", GmPacketType.name(17), "GmPacketType 17 is Goto");
        checkEquals("Reconnect", GmPacketType.name(36), "GmPacketType 36 is Reconnect");
        checkEquals("JumpScare", GmPacketType.name(320), "GmPacketType 320 is JumpScare");
        checkEquals(321, GmPacketType.count(), "GmPacketType has 321 entries");
        check(GmPacketType.name(9999).startsWith("Unknown"), "out-of-range id is reported");
        checkEquals(-1, GmPacketType.id("NoSuchPacket"), "unknown name returns -1");

        checkEquals("Join", QPacketType.name(3), "QPacketType 3 is Join");
        checkEquals(2, QPacketType.POSITION, "QPacketType.POSITION is 2");
        checkEquals(5, QPacketType.count(), "QPacketType has 5 entries");
    }

    private static String hex(byte[] bytes) {
        var sb = new StringBuilder();
        for (int i = 0; i < bytes.length; i++) {
            if (i > 0) {
                sb.append(' ');
            }
            sb.append(String.format("%02X", bytes[i]));
        }
        return sb.toString();
    }
}
