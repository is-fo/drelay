package networking.packets.qpackets;

import networking.GameReader;
import networking.GameWriter;
import networking.packets.Packet;
import networking.packets.QPacketType;

import java.io.IOException;

/**
 * {@code QJoin} (id 3) — sent by the client once it starts waiting in the queue.
 *
 * <p>Carries the account's queue token. From the decompiled {@code QJoin}: a single
 * {@code WriteString8}. The client stores it as {@code Client.CurrentQueueToken} when the queue
 * completes, which makes this the packet that ties a queue session to the game session.
 */
public class QJoin implements Packet {

    public static final int ID = QPacketType.JOIN;

    public String queueToken;

    public QJoin() {
    }

    public QJoin(String queueToken) {
        this.queueToken = queueToken;
    }

    @Override
    public void read(GameReader in) throws IOException {
        queueToken = in.readString8();
    }

    @Override
    public void write(GameWriter out) throws IOException {
        out.writeString8(queueToken);
    }

    @Override
    public String toString() {
        return "QJoin{queueToken='" + queueToken + "'}";
    }
}
