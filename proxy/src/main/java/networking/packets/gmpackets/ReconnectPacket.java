package networking.packets.gmpackets;

import networking.GameReader;
import networking.GameWriter;
import networking.packets.Packet;

import java.io.IOException;

/**
 * {@code GmReconnect} - game id 36, server to client: the server moves this client to another game
 * server.
 *
 * <p>This is the one packet that changes where a session lives, and it is why {@code Game_Slave}
 * cannot be pinned down as a fixed address: the client does not decide the slave, the server tells
 * it which host and port to dial. Field order and types come from the client's
 * {@code GmReconnect.Read}/{@code Write}:
 *
 * <pre>
 *   Host        string32   4-byte little-endian length, then UTF-8
 *   Port        varint
 *   ToBeyond    bool       one byte
 *   CharacterId int64      only when bytes remain; the client leaves it at -1 otherwise
 * </pre>
 *
 * <p>Under an address claim this packet is the one way a session can leave the proxy unnoticed: the
 * new address must be claimed <em>before</em> the client dials it. {@link networking.Relay}
 * therefore logs the target instead of forwarding it silently. Decoding is logging only - the
 * payload is still forwarded byte for byte, so a mis-read here cannot corrupt a session.
 */
public class ReconnectPacket implements Packet {

    public String host = "";
    public int port;
    public boolean toBeyond;
    public long characterId = -1L;

    @Override
    public void read(GameReader in) throws IOException {
        host = in.readString32();
        port = in.readVarint();
        toBeyond = in.readBool();
        // Mirrors the client, which reads the id only while bytes remain. The relay decodes from a
        // byte array, so available() is the exact remaining count here.
        characterId = in.available() > 0 ? in.readInt64() : -1L;
    }

    @Override
    public void write(GameWriter out) throws IOException {
        out.writeString32(host);
        out.writeVarint(port);
        out.writeBool(toBeyond);
        out.writeInt64(characterId);
    }
}
