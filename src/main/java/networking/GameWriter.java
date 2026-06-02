package networking;

import java.io.DataOutputStream;
import java.io.IOException;
import java.io.OutputStream;

public class GameWriter extends DataOutputStream {
    public GameWriter(OutputStream out) {
        super(out);
    }

    public void writeIntLE(int v) throws IOException {
        writeInt(Integer.reverseBytes(v));
    }

    public void writeShortLE(short v) throws IOException {
        writeShort(Short.reverseBytes(v));
    }

    public void writeFloatLE(float v) throws IOException {
        writeInt(Integer.reverseBytes(Float.floatToIntBits(v)));
    }

    public void writeVarint(int value) throws IOException {
        boolean isNegative = value < 0;
        long uval = Math.abs((long) value);

        int b = (int) (uval & 0x3F);
        if (isNegative) b |= 0x40;
        uval >>>= 6;

        if (uval != 0) {
            b |= 0x80;
            writeByte(b);
            while (uval != 0) {
                b = (int) (uval & 0x7F);
                uval >>>= 7;
                if (uval != 0) b |= 0x80;
                writeByte(b);
            }
        } else {
            writeByte(b);
        }
    }


}
