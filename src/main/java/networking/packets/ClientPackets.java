package networking.packets;

import networking.util.Fields;

import java.io.ByteArrayInputStream;
import java.io.IOException;

/**
 * Lightweight field readers for the captured, verified client→server packet layouts.
 *
 * <p>{@link Injection#decode} handles packets that have a {@link Packet} codec. This class covers the
 * ones that do not need a full codec but whose fields the relay genuinely uses - currently the
 * client's own clock, which sits in {@code Move}, {@code Shoot} and {@code ActivateObject}. The
 * layouts come from the capture analysis in {@code docs/FEATURES-AUTOMATION.md}, where each one was
 * validated by requiring the decode to consume a real packet to its exact last byte.
 *
 * <p>Every read is bounds-checked and every failure returns "no fields" rather than throwing. These
 * run on the forwarding path, and a mis-read costs a log line; it must never cost a byte of the
 * session, because the payload is forwarded from the original array regardless.
 */
public final class ClientPackets {

    private ClientPackets() {
    }

    /** {@code GmMove} (74): {@code [float X][float Y][varint Time]}. */
    static Fields move(byte[] body) {
        var r = new Reader(body);
        r.skip(8);                          // X, Y
        return Fields.of().add("time", r.varintOrNull());
    }

    /**
     * {@code GmShoot} (6): {@code [float X][float Y][float Angle][varint Time][varint BulletId][byte BulletIndex]}.
     *
     * <p>Worth recording even on a packet the relay does not act on: the angle sits at a fixed
     * payload offset of 10, so a future aim correction is a four-byte overwrite with no re-encoding
     * and no length change, which is the safest possible modification of live traffic.
     */
    static Fields shoot(byte[] body) {
        var r = new Reader(body);
        Float x = r.floatOrNull();
        Float y = r.floatOrNull();
        Float angle = r.floatOrNull();
        Long time = r.varintOrNull();
        return Fields.of()
                .add("x", x).add("y", y).add("angle", angle).add("time", time)
                .add("bulletId", r.varintOrNull())
                .add("bulletIndex", r.byteOrNull());
    }

    /**
     * {@code GmActivateObject} (84): {@code [varint ObjectId][string32 Value][float X][float Y][int32 Time]}.
     *
     * <p>The field order is {@code Value}-before-{@code Point}; the decompiled {@code Write} omits the
     * {@code string32} length write entirely, so the bytes - not the source - settled it (three
     * captured packets decode to their last byte this way and not the other).
     */
    static Fields activateObject(byte[] body) {
        var r = new Reader(body);
        Long objectId = r.varintOrNull();
        String value = r.string32OrNull();
        Float x = r.floatOrNull();
        Float y = r.floatOrNull();
        Integer time = r.int32OrNull();
        return Fields.of()
                .add("objectId", objectId).add("value", value)
                .add("x", x).add("y", y).add("time", time);
    }

    /** Parses the payload of a game packet by id, returning the fields worth logging. */
    public static Fields decode(int id, byte[] payload) {
        int offset = 2;                     // the 2-byte little-endian type id
        if (payload.length <= offset) {
            return Fields.of();
        }
        byte[] body = new byte[payload.length - offset];
        System.arraycopy(payload, offset, body, 0, body.length);
        try {
            return switch (id) {
                case GmPacketType.MOVE -> move(body);
                case GmPacketType.SHOOT -> shoot(body);
                case 84 -> activateObject(body);          // GmActivateObject
                default -> Fields.of();
            };
        } catch (RuntimeException e) {
            return Fields.of().add("decodeError", e.getMessage());
        }
    }

    /** A forward-only reader that answers {@code null} instead of throwing on a short buffer. */
    private static final class Reader {
        private final byte[] bytes;
        private int offset;

        Reader(byte[] bytes) {
            this.bytes = bytes;
        }

        boolean skip(int count) {
            if (offset + count > bytes.length) {
                offset = bytes.length;
                return false;
            }
            offset += count;
            return true;
        }

        Integer byteOrNull() {
            if (offset + 1 > bytes.length) {
                return null;
            }
            return bytes[offset++] & 0xFF;
        }

        Long varintOrNull() {
            if (offset >= bytes.length) {
                return null;
            }
            int b = bytes[offset++] & 0xFF;
            long result = b & 0x3F;
            boolean negative = (b & 0x40) != 0;
            // Groups are 6, 6, 7, 7, ... ; see GameReader.readVarint for how that was settled.
            int shift = 6;
            while ((b & 0x80) != 0) {
                if (offset >= bytes.length) {
                    return null;
                }
                b = bytes[offset++] & 0xFF;
                result |= (long) (b & 0x7F) << shift;
                shift += 7;
                if (shift > 63) {
                    return null;
                }
            }
            return negative ? -result : result;
        }

        Integer int32OrNull() {
            if (offset + 4 > bytes.length) {
                return null;
            }
            int value = (bytes[offset] & 0xFF)
                    | ((bytes[offset + 1] & 0xFF) << 8)
                    | ((bytes[offset + 2] & 0xFF) << 16)
                    | ((bytes[offset + 3] & 0xFF) << 24);
            offset += 4;
            return value;
        }

        Float floatOrNull() {
            Integer bits = int32OrNull();
            return bits == null ? null : Float.intBitsToFloat(bits);
        }

        String string32OrNull() {
            Integer length = int32OrNull();
            if (length == null || length < 0 || offset + length > bytes.length) {
                return null;
            }
            String value = new String(bytes, offset, length, java.nio.charset.StandardCharsets.UTF_8);
            offset += length;
            return value;
        }
    }

    /** Kept so the class can expose a full-stream decode for tests without duplicating the reader. */
    @SuppressWarnings("unused")
    private static Fields decodeStream(int id, byte[] body) throws IOException {
        return decode(id, body);
    }
}
