package networking;

import java.io.DataInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;

public class GameReader extends DataInputStream {
    public GameReader(InputStream in) {
        super(in);
    }

    public int readIntLE() throws IOException {
        return Integer.reverseBytes(readInt());
    }

    public short readShortLE() throws IOException {
        return Short.reverseBytes(readShort());
    }

    public float readFloatLE() throws IOException {
        return Float.intBitsToFloat(Integer.reverseBytes(readInt()));
    }

    public int readVarint() throws IOException {
        int b = readByte() & 0xFF;
        int result = b & 0x3F;
        boolean isNegative = (b & 0x40) != 0;
        int shift = 6;

        while ((b & 0x80) != 0) {
            b = readByte() & 0xFF;
            result |= (b & 0x7F) << shift;
            shift += 7;
        }
        return isNegative ? -result : result;
    }

    public String readString8() throws IOException {
        int length = readUnsignedByte();
        byte[] bytes = new byte[length];
        readFully(bytes);
        return new String(bytes, StandardCharsets.UTF_8);
    }

    public String readString16() throws IOException {
        int length = readShortLE() & 0xFFFF;
        byte[] bytes = new byte[length];
        readFully(bytes);
        return new String(bytes, StandardCharsets.UTF_8);
    }
}
