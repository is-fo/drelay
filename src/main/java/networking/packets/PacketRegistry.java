package networking.packets;

import networking.packets.gmpackets.EscapePacket;
import networking.packets.gmpackets.HealthUpdatePacket;

import java.util.HashMap;
import java.util.Map;
import java.util.function.Supplier;

public class PacketRegistry {
    private static final Map<Integer, Supplier<Packet>> registry = new HashMap<>();

    public static void init() {
        registry.put(0x46, HealthUpdatePacket::new);
        registry.put(0x42, EscapePacket::new);
    }

    public static Packet createOrNull(int id) {
        var supplier = registry.get(id);
        return supplier != null ? supplier.get() : null;
    }
}
