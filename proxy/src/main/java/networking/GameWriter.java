package networking;

import java.io.DataOutputStream;
import java.io.IOException;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;

/**
 * Mirrors {@code DarzaCore.Tools.ByteWriter} from the client.
 *
 * <p>Inverse of {@link GameReader}: all multi-byte primitives are little-endian (the client
 * writes through .NET's {@code BinaryWriter}). The outer packet length prefix is big-endian and
 * is written by {@link Relay}, not here.
 */
public class GameWriter extends DataOutputStream {

    public GameWriter(OutputStream out) {
        super(out);
    }

    // --- integers: little-endian ----------------------------------------------------------

    public void writeInt32(int value) throws IOException {
        writeInt(Integer.reverseBytes(value));
    }

    public void writeUInt32(long value) throws IOException {
        writeInt32((int) value);
    }

    public void writeInt16(short value) throws IOException {
        writeShort(Short.reverseBytes(value));
    }

    public void writeUInt16(int value) throws IOException {
        writeShort(Short.reverseBytes((short) value));
    }

    public void writeInt64(long value) throws IOException {
        writeLong(Long.reverseBytes(value));
    }

    // --- floats and doubles ---------------------------------------------------------------

    public void writeFloatLE(float value) throws IOException {
        writeInt32(Float.floatToIntBits(value));
    }

    public void writeDoubleLE(double value) throws IOException {
        writeInt64(Double.doubleToLongBits(value));
    }

    // --- strings --------------------------------------------------------------------------

    public void writeString8(String value) throws IOException {
        byte[] bytes = value.getBytes(StandardCharsets.UTF_8);
        writeByte(bytes.length);
        write(bytes);
    }

    public void writeString16(String value) throws IOException {
        byte[] bytes = value.getBytes(StandardCharsets.UTF_8);
        writeUInt16(bytes.length);
        write(bytes);
    }

    public void writeString32(String value) throws IOException {
        byte[] bytes = value.getBytes(StandardCharsets.UTF_8);
        writeInt32(bytes.length);
        write(bytes);
    }

    public void writeBool(boolean value) throws IOException {
        writeByte(value ? 1 : 0);
    }

    // --- composites -----------------------------------------------------------------------

    /** {@code GamePoint}: two little-endian floats. */
    public void writePoint(float x, float y) throws IOException {
        writeFloatLE(x);
        writeFloatLE(y);
    }

    /** {@code IntPoint}: two varints. */
    public void writeIntPoint(int x, int y) throws IOException {
        writeVarint(x);
        writeVarint(y);
    }

    /**
     * Custom variable-length integer; see {@link GameReader#readVarint()}.
     *
     * <p>First byte carries 6 value bits, the sign (bit 6) and the continuation flag (bit 7); each
     * following byte carries 7 more bits, and the <em>first</em> continuation byte holds bits 6-12.
     * The byte layout is therefore 6, 6, 7, 7, ... which is why {@code 707} is {@code 83 0B} and not
     * {@code 83 16}: the earlier encoder shifted the carry by 7 and so produced bytes that decoded to
     * a different number under the reader the client actually uses.
     */
    public void writeVarint(int value) throws IOException {
        boolean negative = value < 0;
        long remaining = Math.abs((long) value);

        int first = (int) (remaining & 0x3F);
        if (negative) {
            first |= 0x40;
        }
        remaining >>>= 6;

        if (remaining == 0) {
            writeByte(first);
            return;
        }

        writeByte(first | 0x80);
        while (remaining != 0) {
            int b = (int) (remaining & 0x7F);
            remaining >>>= 7;
            writeByte(remaining != 0 ? (b | 0x80) : b);
        }
    }

    // --- aliases kept so the legacy proxy keeps compiling ---------------------------------

    /** @deprecated payload fields are little-endian; use {@link #writeInt32(int)}. */
    @Deprecated
    public void writeIntLE(int value) throws IOException {
        writeInt32(value);
    }

    /** @deprecated payload fields are little-endian; use {@link #writeInt16(short)}. */
    @Deprecated
    public void writeShortLE(short value) throws IOException {
        writeInt16(value);
    }
}
