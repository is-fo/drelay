package networking.packets.qpackets;

import networking.GameReader;
import networking.GameWriter;
import networking.packets.Packet;
import networking.packets.QPacketType;

import java.io.IOException;

/**
 * {@code QHello} (id 1) — the queue handshake, client to queue server.
 *
 * <p>Carries the game server name the client wants. From the decompiled {@code QHello}: a single
 * {@code WriteString8}.
 */
public class QHello implements Packet {

    public static final int ID = QPacketType.HELLO;

    public String gameServerName;

    public QHello() {
    }

    public QHello(String gameServerName) {
        this.gameServerName = gameServerName;
    }

    @Override
    public void read(GameReader in) throws IOException {
        gameServerName = in.readString8();
    }

    @Override
    public void write(GameWriter out) throws IOException {
        out.writeString8(gameServerName);
    }

    @Override
    public String toString() {
        return "QHello{gameServerName='" + gameServerName + "'}";
    }
}
