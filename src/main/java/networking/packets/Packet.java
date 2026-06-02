package networking.packets;

import networking.GameReader;
import networking.GameWriter;

import java.io.IOException;

public interface Packet {
    void read(GameReader in) throws IOException;

    void write(GameWriter out) throws IOException;
}
