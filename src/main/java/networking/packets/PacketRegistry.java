package networking.packets;

import networking.packets.gmpackets.EscapeAckPacket;
import networking.packets.gmpackets.EscapeCastStatePacket;
import networking.packets.gmpackets.EscapePacket;
import networking.packets.gmpackets.ForcedEscapePacket;
import networking.packets.gmpackets.HealthUpdatePacket;
import networking.packets.gmpackets.ReconnectPacket;
import networking.packets.gmpackets.SafeAreaStatePacket;
import networking.packets.qpackets.QError;
import networking.packets.qpackets.QHello;
import networking.packets.qpackets.QJoin;
import networking.packets.qpackets.QPosition;

import java.util.HashMap;
import java.util.Map;
import java.util.Set;
import java.util.TreeSet;
import java.util.function.Supplier;

/**
 * Maps packet ids to codecs, separately per service.
 *
 * <p>Queue and Game do <strong>not</strong> share an id space: id 1 is {@code QHello} on the
 * queue and {@code Update} on the game, and id 4 is {@code QError} versus {@code RegisterResp}.
 * Both services frame identically (4-byte big-endian length, first payload byte is the id), so
 * the registry has to be chosen per connection, not per packet.
 *
 * <p>Only ids that have a verified codec are registered. Everything else is forwarded as raw
 * bytes by {@link networking.Relay}, which is the safe default: re-encoding through a
 * mis-modelled packet silently corrupts a session.
 */
public final class PacketRegistry {

    private PacketRegistry() {
    }

    private static final Map<Integer, Supplier<Packet>> GAME = new HashMap<>();
    private static final Map<Integer, Supplier<Packet>> QUEUE = new HashMap<>();
    private static boolean initialised;

    public static synchronized void init() {
        if (initialised) {
            return;
        }

        // --- game service (GmPacketType ids) ----------------------------------------------
        GAME.put(GmPacketType.HEALTH_UPDATE, HealthUpdatePacket::new);
        GAME.put(GmPacketType.ESCAPE, EscapePacket::new);
        // Reconnect carries the host and port the server wants this client to move to. It is the
        // only packet that redirects a session, so the relay decodes it to log the target: under an
        // address claim, an unclaimed target is otherwise a session that silently leaves the proxy.
        GAME.put(GmPacketType.RECONNECT, ReconnectPacket::new);
        // The escape conversation. Decoding these is what turns "an escape was injected" into "the
        // server accepted / refused it, after N ms" - the one question the stock client cannot
        // answer, because it discards EscapeAck and never records the ack's latency.
        GAME.put(GmPacketType.ESCAPE_ACK, EscapeAckPacket::new);
        GAME.put(GmPacketType.FORCED_ESCAPE, ForcedEscapePacket::new);
        GAME.put(GmPacketType.ESCAPE_CAST_STATE, EscapeCastStatePacket::new);
        GAME.put(GmPacketType.SAFE_AREA_STATE, SafeAreaStatePacket::new);

        // --- queue service (QPacketType ids) ----------------------------------------------
        QUEUE.put(QPacketType.HELLO, QHello::new);
        QUEUE.put(QPacketType.POSITION, QPosition::new);
        QUEUE.put(QPacketType.JOIN, QJoin::new);
        QUEUE.put(QPacketType.ERROR, QError::new);

        initialised = true;
    }

    /** @return a fresh codec for a game-service packet id, or {@code null} if unknown. */
    public static Packet createGameOrNull(int id) {
        init();
        return create(GAME, id);
    }

    /** @return a fresh codec for a queue-service packet id, or {@code null} if unknown. */
    public static Packet createQueueOrNull(int id) {
        init();
        return create(QUEUE, id);
    }

    /**
     * Backwards-compatible alias for the game registry.
     *
     * @deprecated prefer {@link #createGameOrNull(int)} or {@link #createQueueOrNull(int)}, which
     *     make the service explicit.
     */
    @Deprecated
    public static Packet createOrNull(int id) {
        return createGameOrNull(id);
    }

    private static Packet create(Map<Integer, Supplier<Packet>> registry, int id) {
        Supplier<Packet> supplier = registry.get(id);
        return supplier != null ? supplier.get() : null;
    }

    public static Set<Integer> gameIds() {
        init();
        return new TreeSet<>(GAME.keySet());
    }

    public static Set<Integer> queueIds() {
        init();
        return new TreeSet<>(QUEUE.keySet());
    }

    /** Describes a packet id for logging: the enum name plus any known codec. */
    public static String describe(boolean queue, int id) {
        String name = queue ? QPacketType.name(id) : GmPacketType.name(id);
        boolean known = queue ? queueIds().contains(id) : gameIds().contains(id);
        return known ? name + " (codec)" : name;
    }
}
