package networking.packets.qpackets;

import networking.GameReader;
import networking.GameWriter;
import networking.packets.Packet;
import networking.packets.QPacketType;

import java.io.IOException;

/**
 * {@code QError} (id 4) — the queue server rejecting or terminating a queue session.
 *
 * <p>From the decompiled {@code QError}: a single {@code WriteString8} message. The client keeps
 * it as {@code Queue.LastError}.
 */
public class QError implements Packet {

    public static final int ID = QPacketType.ERROR;

    public String message;

    public QError() {
    }

    public QError(String message) {
        this.message = message;
    }

    @Override
    public void read(GameReader in) throws IOException {
        message = in.readString8();
    }

    @Override
    public void write(GameWriter out) throws IOException {
        out.writeString8(message);
    }

    @Override
    public String toString() {
        return "QError{message='" + message + "'}";
    }
}
