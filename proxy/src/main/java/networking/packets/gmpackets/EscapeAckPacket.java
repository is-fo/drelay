package networking.packets.gmpackets;

import networking.GameReader;
import networking.GameWriter;
import networking.packets.Packet;

import java.io.IOException;

/**
 * {@code GmEscapeAck} — game id 158, server to client, one byte.
 *
 * <p>{@code Success} is the server's verdict on an escape request. <strong>The stock client throws
 * this away</strong> (its handler casts the packet to a variable it never reads and only fades the
 * screen), so the relay is the only place that can report it. That matters for exactly one question
 * after an injected nexus: did the server accept it. The relay therefore records every ack, with the
 * sequence number of the injection it answers.
 */
public class EscapeAckPacket implements Packet {

    public boolean success;

    @Override
    public void read(GameReader in) throws IOException {
        success = in.readBool();
    }

    @Override
    public void write(GameWriter out) throws IOException {
        out.writeBool(success);
    }
}
