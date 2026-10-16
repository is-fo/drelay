package networking.packets.gmpackets;

import networking.GameReader;
import networking.GameWriter;
import networking.packets.Packet;

import java.io.IOException;

/**
 * {@code GmSafeAreaState} — game id 291, server to client, one byte.
 *
 * <p>{@code Safe = true} means the server considers the player out of danger. The client uses it to
 * force the instant escape path; the relay tracks it only so auto-nexus can decline to fire where it
 * would be pointless, and records every transition so a missed nexus can be explained afterwards.
 */
public class SafeAreaStatePacket implements Packet {

    public boolean safe;

    @Override
    public void read(GameReader in) throws IOException {
        safe = in.readBool();
    }

    @Override
    public void write(GameWriter out) throws IOException {
        out.writeBool(safe);
    }
}
