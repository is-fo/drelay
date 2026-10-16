package networking.packets;

import networking.GameReader;
import networking.GameWriter;

import java.io.IOException;

/**
 * A packet payload codec.
 *
 * <p>The client's model is: one byte of packet id, then the packet's fields, and the enclosing
 * 4-byte big-endian length covers both. A {@code Packet} implementation therefore handles only
 * the fields; the id byte belongs to the framing layer / registry entry.
 *
 * <p>Implementations must round-trip: {@code write} after {@code read} has to reproduce the
 * original bytes, because the relay forwards bytes and a mis-modelled field silently corrupts a
 * session. {@code networking.PrimitiveTests} (in {@code src/test/java}) enforces that for the
 * implemented packets.
 */
public interface Packet {

    /**
     * Reads this packet's fields.
     *
     * @param in positioned immediately after the id byte
     */
    void read(GameReader in) throws IOException;

    /**
     * Writes this packet's fields.
     *
     * @param out positioned immediately after the id byte
     */
    void write(GameWriter out) throws IOException;
}
