package networking.packets.gmpackets;

import networking.GameReader;
import networking.GameWriter;
import networking.packets.Packet;

import java.io.IOException;

/**
 * {@code GmKicked} — game id 185, server to client: a string16 reason.
 *
 * <p>This is the server explaining why it ended the session, and it is the one packet that makes a
 * failed experiment self-explaining. Without it a disconnect is a socket close with no cause; with
 * it, {@code events-*.jsonl} carries the server's own words — "speed hack detected", a positional
 * desync, an anti-cheat refusal — next to the packet that preceded it. That matters most for exactly
 * the change this relay makes: a stripped status entry is a client that moves at a speed the server's
 * model does not allow, and the reason on this packet is what names that as the cause instead of
 * leaving a bare {@code disconnected}.
 *
 * <p>Confirmed against the shipped client's {@code DarzaGameNet.Packets.Game.GameServer.GmKicked},
 * whose {@code Read}/{@code Write} are a single {@code ReadString16}/{@code WriteString16} of
 * {@code Reason} — the same shape as {@link ForcedEscapePacket}, one id later.
 */
public class KickedPacket implements Packet {

    public String reason = "";

    @Override
    public void read(GameReader in) throws IOException {
        reason = in.readString16();
    }

    @Override
    public void write(GameWriter out) throws IOException {
        out.writeString16(reason);
    }
}
