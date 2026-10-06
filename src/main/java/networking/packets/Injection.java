package networking.packets;

import networking.GameReader;
import networking.GameWriter;
import networking.packets.gmpackets.EscapeCastStatePacket;
import networking.packets.gmpackets.EscapePacket;
import networking.packets.gmpackets.HealthUpdatePacket;
import networking.packets.gmpackets.SafeAreaStatePacket;
import networking.util.Fields;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.util.Map;

/**
 * Builds and decodes <em>injected</em> packets, in the client's own serialization order.
 *
 * <h2>The order this class exists to get right</h2>
 *
 * <p>A packet on this wire is produced by three layers, and each one has to be applied in the order
 * the client applies it. Read top to bottom:
 *
 * <ol>
 *   <li><strong>Fields, little-endian, in the order {@code Write()} emits them.</strong> .NET's
 *       {@code BinaryWriter} is little-endian, so {@code ByteWriter.Write(ushort)} is two LE bytes;
 *       strings carry a 1/2/4-byte length prefix; the custom varint is 6 value bits + sign bit +
 *       continuation bit in the first byte, then 7-bit groups.</li>
 *   <li><strong>The packet's type id, 2 bytes little-endian, written <em>first</em>.</strong>
 *       {@code GmPacket.GetData()} begins with {@code byteWriter.Write(TypeId)} and only then calls
 *       {@code Write(...)}, so the id is not a header the transport adds - it is the first field, and
 *       the length covers it. The queue service differs: {@code QPacket.GetData()} writes a single
 *       byte id.</li>
 *   <li><strong>The 4-byte length prefix, big-endian, written last conceptually but placed
 *       first on the wire.</strong> Telepathy computes it as the payload length <em>including</em>
 *       the type id ({@code Utils.IntToBytesBigEndianNonAlloc(arraySegment.Count, ...)}), which is
 *       why an empty {@code GmEscape} is {@code 00 00 00 02 42 00} and not {@code 00 00 00 00 42 00}.
 *       The queue service expresses the same thing as {@code array.Length - 4}.</li>
 * </ol>
 *
 * <p>Get any of the three wrong and the failure is silent but total: a length that excludes the type
 * id makes the peer read the next packet's id as this one's first body byte, which desynchronises
 * every later packet in the session. That is why {@link #frame} is the only place a length prefix is
 * produced and why every test asserts against a captured byte string rather than a round-trip
 * through the same code.
 *
 * <h2>Why injection is not just "write bytes"</h2>
 *
 * <p>Serialization order is necessary but not sufficient; the peer also has a <em>state</em> order,
 * and a packet is only legal in the right state:
 *
 * <ul>
 *   <li>Before the client's {@code Hello} (id 55) the server is still reading the handshake, so any
 *       extra bytes are read as part of {@code Hello}'s fields and corrupt the login.</li>
 *   <li>The world does not exist until the server sends {@code MapInfo} (id 5); an escape before that
 *       has no world to escape from.</li>
 * </ul>
 *
 * <p>{@link networking.Relay.Session} therefore refuses to inject until it has seen both, and
 * re-arms that gate on every new world. See {@code docs/INJECTION.md}.
 */
public final class Injection {

    private Injection() {
    }

    // --- building ---------------------------------------------------------------------------

    /** The payload of a game packet: {@code [LE ushort id][body]}, with no length prefix. */
    public static byte[] gamePayload(int typeId, byte[] body) {
        byte[] payload = new byte[2 + (body == null ? 0 : body.length)];
        payload[0] = (byte) (typeId & 0xFF);
        payload[1] = (byte) ((typeId >>> 8) & 0xFF);
        if (body != null && body.length > 0) {
            System.arraycopy(body, 0, payload, 2, body.length);
        }
        return payload;
    }

    /** The payload of a queue packet: {@code [byte id][body]}. */
    public static byte[] queuePayload(int id, byte[] body) {
        byte[] payload = new byte[1 + (body == null ? 0 : body.length)];
        payload[0] = (byte) (id & 0xFF);
        if (body != null && body.length > 0) {
            System.arraycopy(body, 0, payload, 1, body.length);
        }
        return payload;
    }

    /**
     * The payload of a server→client health reading, for tests and for a replayed capture.
     *
     * <p>Lives here rather than in a test so that the encoder used to <em>build</em> a health packet
     * and the decoder used to read one are the same implementation - a test that builds a packet with
     * different code than the relay reads it with proves nothing about the relay.
     */
    public static byte[] healthUpdatePayload(int maxHealth, int health, int shield, int barrier) {
        var packet = new HealthUpdatePacket();
        packet.maxHealth = maxHealth;
        packet.health = health;
        packet.shield = shield;
        packet.barrier = barrier;
        return gamePayload(GmPacketType.HEALTH_UPDATE, body(packet));
    }

    /** The payload of a {@code GmMapInfo} as this relay needs to recognise it: just the type id. */
    public static byte[] mapInfoPayload() {
        return gamePayload(GmPacketType.MAP_INFO, new byte[0]);
    }

    /** The body of a packet written by the generic {@link Packet#write} path. */
    public static byte[] body(Packet packet) {
        var buffer = new ByteArrayOutputStream(32);
        try (var writer = new GameWriter(buffer)) {
            packet.write(writer);
        } catch (IOException e) {
            throw new IllegalStateException("serializing " + packet.getClass().getSimpleName()
                    + " failed: " + e.getMessage(), e);
        }
        return buffer.toByteArray();
    }

    /**
     * The complete on-wire message: {@code [4-byte BE length][payload]}.
     *
     * @param bigEndianLength the game service frames big-endian and the queue little-endian; the
     *     relay detects which per session and passes that decision in rather than guessing here
     *     (see {@code docs/PROTOCOL.md} 4).
     */
    public static byte[] frame(byte[] payload, boolean bigEndianLength) {
        byte[] out = new byte[4 + payload.length];
        int length = payload.length;
        if (bigEndianLength) {
            out[0] = (byte) (length >>> 24);
            out[1] = (byte) (length >>> 16);
            out[2] = (byte) (length >>> 8);
            out[3] = (byte) length;
        } else {
            out[0] = (byte) length;
            out[1] = (byte) (length >>> 8);
            out[2] = (byte) (length >>> 16);
            out[3] = (byte) (length >>> 24);
        }
        System.arraycopy(payload, 0, out, 4, payload.length);
        return out;
    }

    // --- the packets this relay injects ------------------------------------------------------

    /**
     * {@code GmEscape} (id 66), empty payload: {@code 00 00 00 02 42 00}.
     *
     * <p>This is the whole of an instant nexus. It is byte-for-byte what the client sends itself from
     * {@code World.RequestEscape}'s instant branch and from every post-death menu path, so a server
     * cannot distinguish it from a player pressing the button.
     *
     * @param bigEndianLength the length byte order this session settled on. Every forwarded frame in a
     *     session uses the order its {@code SessionState} detected, so an injected frame must use the
     *     same one: if the two disagreed, the server would read an absurd length and the whole stream
     *     would desync. It is a parameter rather than a constant for exactly that reason - the game
     *     service is big-endian in practice, but "in practice" is not a wire format.
     */
    public static byte[] escape(boolean bigEndianLength) {
        return frame(gamePayload(GmPacketType.ESCAPE, body(new EscapePacket())), bigEndianLength);
    }

    /**
     * {@code GmEscapeCastState} (id 290, {@code Casting = true}): {@code 00 00 00 03 22 01 01}.
     *
     * <p>Only needed to imitate the channelled escape used when the character carries the
     * {@code Impermanence} totem and is alive, out of a safe area and past the world-entry window.
     * The relay's default is the instant form; this exists so the alternative can be tried without a
     * code change when the instant form turns out to be refused.
     */
    public static byte[] escapeCastState(boolean casting, boolean bigEndianLength) {
        var packet = new EscapeCastStatePacket();
        packet.casting = casting;
        return frame(gamePayload(GmPacketType.ESCAPE_CAST_STATE, body(packet)), bigEndianLength);
    }

    // --- decoding what comes back ------------------------------------------------------------

    /**
     * Decodes a server→client payload with the registered codec for its id.
     *
     * <p>Returns an empty map when there is no codec or the bytes do not decode, so a caller can use
     * the result unconditionally. Every decode failure is a log line rather than an exception: a
     * mis-read may cost a diagnostic, never a byte of the session, because the payload is forwarded
     * from the original bytes regardless.
     */
    public static Map<String, Object> decode(boolean queue, int id, byte[] payload) {
        int offset = queue ? 1 : 2;
        if (payload.length < offset) {
            return Map.of();
        }
        Packet packet = queue ? PacketRegistry.createQueueOrNull(id) : PacketRegistry.createGameOrNull(id);
        if (packet == null) {
            return Map.of();
        }
        try {
            packet.read(new GameReader(new ByteArrayInputStream(payload, offset, payload.length - offset)));
        } catch (Exception e) {
            return Fields.of().add("decodeError", e.getClass().getSimpleName()
                    + (e.getMessage() == null ? "" : ": " + e.getMessage()));
        }
        Fields fields = Fields.of();
        if (packet instanceof HealthUpdatePacket health) {
            fields.add("maxHealth", health.maxHealth)
                    .add("health", health.health)
                    .add("shield", health.shield)
                    .add("barrier", health.barrier);
        } else if (packet instanceof SafeAreaStatePacket safe) {
            fields.add("safe", safe.safe);
        } else if (packet instanceof EscapeCastStatePacket cast) {
            fields.add("casting", cast.casting);
        } else if (packet instanceof networking.packets.gmpackets.EscapeAckPacket ack) {
            fields.add("success", ack.success ? 1 : 0);
        } else if (packet instanceof networking.packets.gmpackets.ForcedEscapePacket forced) {
            fields.add("message", forced.message);
        } else if (packet instanceof networking.packets.gmpackets.KickedPacket kicked) {
            fields.add("reason", kicked.reason);
        } else if (packet instanceof networking.packets.gmpackets.ReconnectPacket reconnect) {
            fields.add("host", reconnect.host)
                    .add("port", reconnect.port)
                    .add("toBeyond", reconnect.toBeyond)
                    .add("characterId", reconnect.characterId);
        }
        return fields;
    }
}
