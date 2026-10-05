package networking.packets.gmpackets;

import networking.GameReader;
import networking.GameWriter;
import networking.packets.Packet;

import java.io.IOException;

/**
 * {@code GmEscapeCastState} — game id 290, client to server, one byte.
 *
 * <p>The client sends this to open ({@code Casting = true}) or abandon ({@code false}) the channelled
 * escape that a character with the {@code Impermanence} totem goes through. The relay only sends it
 * when configured to imitate that channel instead of the instant escape; see
 * {@link networking.packets.Injection#escapeCastState(boolean)}.
 */
public class EscapeCastStatePacket implements Packet {

    public boolean casting;

    @Override
    public void read(GameReader in) throws IOException {
        casting = in.readBool();
    }

    @Override
    public void write(GameWriter out) throws IOException {
        out.writeBool(casting);
    }
}
