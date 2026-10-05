package networking.packets.qpackets;

import networking.GameReader;
import networking.GameWriter;
import networking.packets.Packet;
import networking.packets.QPacketType;

import java.io.IOException;

/**
 * {@code QPosition} (id 2) — the queue server telling the client where it is in line.
 *
 * <p>From the decompiled {@code QPosition}: a single varint. The client logs and displays this
 * as the queue position.
 */
public class QPosition implements Packet {

    public static final int ID = QPacketType.POSITION;

    public int position;

    public QPosition() {
    }

    public QPosition(int position) {
        this.position = position;
    }

    @Override
    public void read(GameReader in) throws IOException {
        position = in.readVarint();
    }

    @Override
    public void write(GameWriter out) throws IOException {
        out.writeVarint(position);
    }

    @Override
    public String toString() {
        return "QPosition{position=" + position + "}";
    }
}
