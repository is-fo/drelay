package networking;

import java.io.DataInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;

/**
 * Mirrors {@code DarzaCore.Tools.ByteReader} from the client.
 *
 * <p>Endianness is the trap here: the client's {@code ByteReader} delegates to .NET's
 * {@code BinaryReader}, which is <strong>little-endian</strong> for every multi-byte primitive
 * (int, uint, short, ushort, long, float, double). Only the outer packet framing uses a
 * big-endian length (see {@link Relay}). A packet is therefore
 * {@code [4-byte BE length][payload]} whose payload fields are little-endian.
 *
 * <p>The inherited {@code readInt}/{@code readShort}/{@code readLong} are big-endian
 * ({@code DataInputStream} semantics) and are kept only for the framing header and for the
 * legacy {@code GameProxy}. Payload fields use the little-endian readers below.
 *
 * <p>Field formats, taken from the decompiled reader:
 * <ul>
 *   <li>{@code ReadString8/16/32} - 1/2/4-byte length prefix, then that many UTF-8 bytes</li>
 *   <li>{@code ReadBool} - one byte; {@code != 0} is true</li>
 *   <li>{@code ReadPoint} - two little-endian floats (X, Y)</li>
 *   <li>{@code ReadIntPoint} - two varints</li>
 *   <li>{@code ReadVarint} - 6 value bits, 1 sign bit, 1 continuation bit, then 7-bit groups</li>
 * </ul>
 */
public class GameReader extends DataInputStream {

    public GameReader(InputStream in) {
        super(in);
    }

    // --- integers: .NET BinaryReader is little-endian -------------------------------------

    public int readInt32() throws IOException {
        return Integer.reverseBytes(readInt());
    }

    public long readUInt32() throws IOException {
        return readInt32() & 0xFFFFFFFFL;
    }

    public short readInt16() throws IOException {
        return Short.reverseBytes(readShort());
    }

    public int readUInt16() throws IOException {
        return readInt16() & 0xFFFF;
    }

    public long readInt64() throws IOException {
        return Long.reverseBytes(readLong());
    }

    public int readUInt8() throws IOException {
        return readUnsignedByte();
    }

    public int readInt8() throws IOException {
        return readByte();
    }

    // --- floats and doubles ---------------------------------------------------------------

    public float readFloatLE() throws IOException {
        return Float.intBitsToFloat(readInt32());
    }

    public double readDoubleLE() throws IOException {
        return Double.longBitsToDouble(readInt64());
    }

    // --- strings --------------------------------------------------------------------------

    public String readString8() throws IOException {
        return readFixedString(readUnsignedByte());
    }

    public String readString16() throws IOException {
        return readFixedString(readUInt16());
    }

    public String readString32() throws IOException {
        return readFixedString(readInt32());
    }

    private String readFixedString(int length) throws IOException {
        if (length < 0) {
            throw new IOException("negative string length " + length);
        }
        byte[] bytes = new byte[length];
        readFully(bytes);
        return new String(bytes, StandardCharsets.UTF_8);
    }

    public boolean readBool() throws IOException {
        return readUnsignedByte() != 0;
    }

    public byte[] readBytesExact(int amount) throws IOException {
        byte[] bytes = new byte[amount];
        readFully(bytes);
        return bytes;
    }

    // --- composites -----------------------------------------------------------------------

    /** {@code GamePoint}: two little-endian floats. */
    public float[] readPoint() throws IOException {
        return new float[]{readFloatLE(), readFloatLE()};
    }

    /** {@code IntPoint}: two varints. */
    public int[] readIntPoint() throws IOException {
        return new int[]{readVarint(), readVarint()};
    }

    /**
     * Custom variable-length integer, not a protobuf varint.
     *
     * <p>First byte: bits 0-5 are the low 6 value bits, bit 6 is the sign, bit 7 is the
     * continuation flag. Each following byte adds 7 more value bits. <strong>The first
     * continuation byte starts at bit 6, not bit 7</strong> - i.e. the groups are 6, 6, 7, 7, ...
     * That is the part the decompiled source could not settle (its masks are encrypted constants)
     * and it was resolved against the wire: for the 23 {@code HealthUpdate} readings in
     * {@code work/logs/relay.out.log} whose HP also appears as a fixed-width
     * {@code StatsType.Hp} int16 in the same {@code GmUpdate}, value {@code 830B} must be 707 -
     * which only this grouping produces. {@code work/varint_align.py} is the measurement.
     *
     * <p>An earlier version read the first continuation byte at bit 7, which decodes 707 as 1411
     * - a health bar nobody could reconcile with the game's own numbers. The bug is exactly the
     * kind that hides: a wrong shift produces a wrong value of the right magnitude, and the
     * packet's byte length is unaffected, so nothing downstream notices.
     */
    public int readVarint() throws IOException {
        int b = readUnsignedByte();
        int result = b & 0x3F;
        boolean negative = (b & 0x40) != 0;
        // The first continuation byte contributes at bit 6, so the groups are 6, 6, 7, 7, ...
        // Stepping before the read is the off-by-one that made this read 707 as 1411: it shifted the
        // first continuation to bit 13, which is where the *second* one belongs.
        int shift = 6;

        while ((b & 0x80) != 0) {
            b = readUnsignedByte();
            result |= (b & 0x7F) << shift;
            shift += 7;
        }
        return negative ? -result : result;
    }

    // --- aliases kept so the legacy proxy keeps compiling ---------------------------------

    /** @deprecated payload fields are little-endian; use {@link #readInt32()}. */
    @Deprecated
    public int readIntLE() throws IOException {
        return readInt32();
    }

    /** @deprecated payload fields are little-endian; use {@link #readInt16()}. */
    @Deprecated
    public short readShortLE() throws IOException {
        return readInt16();
    }
}
