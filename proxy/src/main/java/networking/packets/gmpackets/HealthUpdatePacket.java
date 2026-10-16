package networking.packets.gmpackets;

import networking.GameReader;
import networking.GameWriter;
import networking.packets.Packet;

import java.io.IOException;

public class HealthUpdatePacket implements Packet {
    public int maxHealth, health, shield, barrier;

    @Override
    public void read(GameReader in) throws IOException {
        maxHealth = in.readVarint();
        health = in.readVarint();
        shield = in.readVarint();
        barrier = in.readVarint();
    }

    @Override
    public void write(GameWriter out) throws IOException {
        out.writeVarint(maxHealth);
        out.writeVarint(health);
        out.writeVarint(shield);
        out.writeVarint(barrier);
    }
}
