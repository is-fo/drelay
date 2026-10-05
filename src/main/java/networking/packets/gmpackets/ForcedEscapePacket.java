package networking.packets.gmpackets;

import networking.GameReader;
import networking.GameWriter;
import networking.packets.Packet;

import java.io.IOException;

/**
 * {@code GmForcedEscape} — game id 184, server to client: a string16 reason.
 *
 * <p>The server uses this to end an escape attempt and explain why (a boss room, a PvP state, an
 * anti-cheat refusal). The client shows the message in an alert but - a real bug worth knowing about -
 * does not clear its own cast state, so a proxy that injects an escape and sees this packet knows the
 * attempt failed and can say so instead of silently retrying.
 */
public class ForcedEscapePacket implements Packet {

    public String message = "";

    @Override
    public void read(GameReader in) throws IOException {
        message = in.readString16();
    }

    @Override
    public void write(GameWriter out) throws IOException {
        out.writeString16(message);
    }
}
