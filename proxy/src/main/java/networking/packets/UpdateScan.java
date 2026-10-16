package networking.packets;

/**
 * Walks a game {@code GmUpdate} (id 1) payload without decoding it into an object graph.
 *
 * <h2>Why this exists</h2>
 *
 * <p>Everything else in this package <em>reads</em> a packet into a model and lets the caller look at
 * fields. That is enough to log a health reading, but it cannot answer "where exactly in these bytes
 * is the status list", and it cannot be used to change a payload without re-encoding the whole thing
 * - which is the one thing {@code Relay} deliberately never does, because a round trip through a
 * mis-modelled packet silently corrupts a session.
 *
 * <p>A walker is the third option: it understands the container (object count, stat count, the size
 * of every {@code DataType}) and reports byte <em>offsets</em> into the original array. A caller can
 * then cut a range out of the original bytes and leave every other byte untouched, so a packet it
 * does not understand is still forwarded bit-for-bit.
 *
 * <h2>The layout, as measured</h2>
 *
 * <p>{@code [2-byte LE type id]} then, in order: the update id, the removed list, the killed list,
 * and the objects. Each object is a varint id, a {@code bool} "is new" (followed by a 2-byte LE type
 * when true), a byte stat count, and that many {@code [byte statType][byte dataType][value]} triples.
 * A trailing varint closes the packet.
 *
 * <p>Two layouts are <strong>not</strong> what the decompiled client says, and using them desyncs the
 * walk on the first packet that carries one:
 *
 * <ul>
 *   <li><strong>Item ({@code DataType 5})</strong> - the decompiled {@code Item.Read} never reads
 *       {@code CharacterId} and is short by eight bytes. The order used here is the empirically
 *       verified one (see {@code work/capture-analysis}): {@code i16 Type, u8 Count, i16 AffixType,
 *       u8 Flags, i64 CharacterId, string8 DroppedFor, u8 SpriteIndex, string8 SubDesc, i64 Traits,
 *       u16 Quality, u8 UpgradeProgress}.</li>
 *   <li><strong>Marks ({@code DataType 10})</strong> - a byte count then that many
 *       {@code [varint ObjectId][byte MarkColor]} pairs, from {@code DarzaGameNet.Structures.Mark}.</li>
 * </ul>
 *
 * <p>Both mistakes are silent in the worst way: the walk stays in bounds, produces plausible stat
 * boundaries, and every offset after the mistake is wrong. That is why {@link #walk} returns
 * {@code true} only when the payload decoded <em>exactly</em> to its last byte, and why callers must
 * treat {@code false} as "do not touch these bytes".
 *
 * <p>The varint is the game's own, identical to {@link networking.GameReader#readVarint}: the first
 * byte carries 6 value bits, a sign bit and a continuation bit, and later bytes carry 7. The groups
 * are therefore 6, 6, 7, 7, ... - see {@code GameReader.readVarint} for why the first continuation
 * group starts at bit 6 and not bit 7.
 */
public final class UpdateScan {

    /** The game packet id this walker understands. */
    public static final int UPDATE_ID = 1;

    /** The stat that carries a status-effect list; see {@code StatsType.StatusEffects}. */
    public static final int STATUS_EFFECTS_STAT = 78;

    /** The {@code DataType} of a status-effect list. */
    public static final int STATUS_EFFECT_DATA_TYPE = 8;

    /** {@code StatsType.OwnCharacterId} - present only on the local player's own object. */
    public static final int OWN_CHARACTER_ID_STAT = 183;

    /** {@code StatsType.Hp}. */
    public static final int HP_STAT = 2;

    /** {@code StatsType.Health} - the same reading under its newer name. */
    public static final int HEALTH_STAT = 80;

    /** Bytes per status-effect entry: {@code [i32 Effect][u8 Tier][f32 Duration]}. */
    public static final int STATUS_ENTRY_BYTES = 9;

    private UpdateScan() {
    }

    /** What a walk reports; every offset is into the array that was walked. */
    public interface Visitor {

        /**
         * One {@code StatsType.StatusEffects} list, guaranteed to lie entirely inside the payload.
         *
         * <p>{@link #walk} validates the whole list before reporting it, so a visitor may read all
         * {@code count} entries at {@code firstEntryOffset} without a bounds check of its own. A list
         * that does not fit ends the walk instead, and the visitor never hears about it.
         *
         * @param objectId the object the list belongs to
         * @param countOffset the offset of the list's 1-byte entry count
         * @param count the entry count that byte holds
         * @param firstEntryOffset the offset of the first 9-byte entry, or {@code countOffset + 1}
         *     when the list is empty
         */
        void onStatusList(int objectId, int countOffset, int count, int firstEntryOffset);

        /** A Short or Int stat, reported raw so a caller can correlate it against another packet. */
        default void onIntegerStat(int objectId, int statType, long value) {
        }

        /** Every stat, before its value is read; useful for diagnostics and tests. */
        default void onStat(int objectId, int statType, int dataType) {
        }
    }

    /**
     * Walks {@code payload}, reporting to {@code visitor}.
     *
     * <p>Never throws and never reads outside the array: a payload that does not decode cleanly, for
     * any reason, ends the walk and returns {@code false}. The visitor may already have seen callbacks
     * for the part that did decode, so a caller that intends to rewrite bytes must discard everything
     * it collected when this returns {@code false}.
     *
     * @return true when the payload decoded exactly to its last byte
     */
    public static boolean walk(byte[] payload, Visitor visitor) {
        if (payload == null || payload.length < 2) {
            return false;
        }
        try {
            Cursor c = new Cursor(payload, 2);
            c.varint();                                   // update id
            for (int i = c.varint(); i > 0; i--) {        // removed object ids
                c.varint();
            }
            for (int i = c.varint(); i > 0; i--) {        // killed object ids
                c.varint();
            }
            for (int objects = c.varint(); objects > 0; objects--) {
                int objectId = c.varint();
                if (c.u8() != 0) {                        // is new
                    c.i16();                              // object type
                }
                for (int stats = c.u8(); stats > 0; stats--) {
                    int statType = c.u8();
                    int dataType = c.u8();
                    visitor.onStat(objectId, statType, dataType);
                    readValue(c, visitor, objectId, statType, dataType);
                }
            }
            c.varint();                                   // Dt
            return c.atEnd();
        } catch (Bounds e) {
            return false;
        }
    }

    private static void readValue(Cursor c, Visitor visitor, int objectId, int statType, int dataType)
            throws Bounds {
        switch (dataType) {
            case 0 -> c.skip(1);                          // Byte
            case 1 -> {                                   // Short
                short value = c.i16();
                visitor.onIntegerStat(objectId, statType, value);
            }
            case 2 -> {                                   // Int
                int value = c.i32();
                visitor.onIntegerStat(objectId, statType, value);
            }
            case 3 -> c.skip(8);                          // Long
            case 4 -> c.skip(c.u8());                     // String8
            case 5 -> skipItem(c);                        // Item
            case 6 -> c.skip(8);                          // Point: two floats
            case 7 -> c.skip(4);                          // Color: RGBA bytes
            case 8 -> readStatusList(c, visitor, objectId);
            case 9 -> c.skip(4);                          // Float
            case 10 -> {                                  // Marks
                for (int marks = c.u8(); marks > 0; marks--) {
                    c.varint();
                    c.skip(1);
                }
            }
            default -> throw new Bounds();
        }
    }

    /** The empirically verified {@code Item} layout; see the class comment. */
    private static void skipItem(Cursor c) throws Bounds {
        c.skip(2);                                        // Type
        c.skip(1);                                        // Count
        c.skip(2);                                        // AffixType
        c.skip(1);                                        // Flags
        c.skip(8);                                        // CharacterId
        c.skip(c.u8());                                   // DroppedFor
        c.skip(1);                                        // SpriteIndex
        c.skip(c.u8());                                   // SubDesc
        c.skip(8);                                        // Traits
        c.skip(2);                                        // Quality
        c.skip(1);                                        // UpgradeProgress
    }

    private static void readStatusList(Cursor c, Visitor visitor, int objectId) throws Bounds {
        int countOffset = c.position();
        int count = c.u8();
        int firstEntryOffset = c.position();
        // The whole list is validated *before* it is reported. Reporting first and skipping after
        // would hand a rewriter an offset it cannot safely read: the entries would look present, the
        // rewriter would index past the end of the array, and the failure would be an exception on the
        // forwarding thread rather than a refused walk. Every offset a visitor receives is in bounds.
        c.skip(count * STATUS_ENTRY_BYTES);
        visitor.onStatusList(objectId, countOffset, count, firstEntryOffset);
    }

    /** Raised when a read would leave the payload; never escapes {@link #walk}. */
    private static final class Bounds extends Exception {
        Bounds() {
            super(null, null, false, false);
        }
    }

    /**
     * A bounds-checked reader over one payload.
     *
     * <p>Deliberately not {@link networking.GameReader}: that one is a {@code DataInputStream} and
     * does not expose how far it has read, which is the whole point of a scan. The two agree on the
     * wire format, and {@code UpdateScanTests} proves it by running both over the same bytes.
     */
    private static final class Cursor {
        private final byte[] bytes;
        private int position;

        Cursor(byte[] bytes, int position) {
            this.bytes = bytes;
            this.position = position;
        }

        int position() {
            return position;
        }

        boolean atEnd() {
            return position == bytes.length;
        }

        void skip(int amount) throws Bounds {
            if (amount < 0 || position + amount > bytes.length) {
                throw new Bounds();
            }
            position += amount;
        }

        int u8() throws Bounds {
            if (position >= bytes.length) {
                throw new Bounds();
            }
            return bytes[position++] & 0xFF;
        }

        short i16() throws Bounds {
            int low = u8();
            int high = u8();
            return (short) (low | high << 8);
        }

        int i32() throws Bounds {
            int b0 = u8();
            int b1 = u8();
            int b2 = u8();
            int b3 = u8();
            return b0 | b1 << 8 | b2 << 16 | b3 << 24;
        }

        /** {@link networking.GameReader#readVarint}'s format: 6 value bits, then 6, 7, 7, ... */
        int varint() throws Bounds {
            int b = u8();
            int result = b & 0x3F;
            boolean negative = (b & 0x40) != 0;
            int shift = 6;
            while ((b & 0x80) != 0) {
                b = u8();
                result |= (b & 0x7F) << shift;
                shift += 7;
            }
            return negative ? -result : result;
        }
    }
}
