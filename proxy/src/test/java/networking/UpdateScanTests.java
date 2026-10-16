package networking;

import networking.packets.StatusStrip;
import networking.packets.UpdateScan;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.util.ArrayList;
import java.util.List;

/**
 * The gate for the one place this relay changes bytes the server sent.
 *
 * <p>Run with {@code java -cp target/classes networking.UpdateScanTests}. Exits non-zero on the first
 * failure.
 *
 * <h2>What is actually being defended</h2>
 *
 * <p>A server→client rewrite is the only operation in this project whose failure mode is
 * <em>silent</em>. An injected packet that is wrong is wrong once and shows up as a refused escape. A
 * rewritten packet whose length is wrong desynchronises the framing for the rest of the session, and
 * the symptom is that the client stops seeing the world - an outcome indistinguishable from a server
 * problem, hours into a run, with no line in the log pointing at the cause.
 *
 * <p>So the checks below are not "does the happy path work". They are:
 *
 * <ol>
 *   <li>the walk lands on the <em>exact</em> last byte of real captured packets, including the ones
 *       carrying every {@code DataType} in use;</li>
 *   <li>a payload that does not decode exactly is <strong>refused</strong>, because a walk that
 *       guessed would produce wrong offsets and a wrong rewrite;</li>
 *   <li>only the named object's list is touched, and only for the named effect;</li>
 *   <li>the rewritten payload re-reads to its exact new length;</li>
 *   <li>the player locator refuses to answer until it has evidence, and a wrong answer is the one
 *       thing that could strip a teammate's debuff instead of the operator's.</li>
 * </ol>
 *
 * <h2>Why one fixture is a real capture</h2>
 *
 * <p>{@link #FIXTURE} is packet {@code seq 331827} from the 2026-10-05 play session - the
 * {@code GmUpdate} that carried the {@code Confused} status effect 100 ms after that character died,
 * with three status lists in it for three different objects. It is here because it is the packet this
 * feature exists for, and because a hand-built packet cannot reproduce the thing most likely to break
 * the walk: a large object block with mixed data types before the list being rewritten.
 */
public final class UpdateScanTests {

    private static final List<String> FAILURES = new ArrayList<>();
    private static int checks;

    private static final int CONFUSED = StatusStrip.CONFUSED;

    public static void main(String[] args) {
        testVarintMatchesTheCanonicalReader();
        testEveryDataTypeWalksToTheExactEnd();
        testTruncatedPayloadIsRefused();
        testFixtureWalksExactly();
        testStripRemovesOnlyThePlayersEntry();
        testStripIsIdempotentAndNarrow();
        testStripRejectsWhatItCannotUnderstand();
        testStripEmptiesASingleEntryList();
        testStripHandlesSeveralListsOnOneObject();
        testStripRemovesSeveralEffectsAtOnce();
        testLocatorNeedsEvidence();
        testLocatorRequiresAMargin();
        testLocatorPrefersOwnCharacterId();
        testLocatorResetsPerWorld();

        if (FAILURES.isEmpty()) {
            IO.println("PASS: " + checks + " update-scan, strip and player-locator checks passed");
            return;
        }
        IO.println("FAIL: " + FAILURES.size() + " of " + checks + " check(s) failed");
        for (String failure : FAILURES) {
            IO.println("  - " + failure);
        }
        System.exit(1);
    }

    // --- the walker ---------------------------------------------------------------------------

    /**
     * The scan's varint must agree with {@link GameReader#readVarint}, which is the reader the rest of
     * the relay already trusts.
     *
     * <p>They are two implementations of one format, so the test drives the <em>same bytes</em> through
     * both: {@code GameWriter.writeVarint} produces them, {@code GameReader} reads them, and the scan
     * reads the same bytes back out of an object id inside a synthetic packet.
     */
    private static void testVarintMatchesTheCanonicalReader() {
        int[] values = {0, 1, 63, 64, 65, 707, 1930, 458782, 1_000_000, -1, -707, Integer.MIN_VALUE + 1};
        for (int value : values) {
            byte[] encoded = varint(value);
            try {
                int canonical = new GameReader(new ByteArrayInputStream(encoded)).readVarint();
                checkEquals(value, canonical, "GameWriter/GameReader disagree on " + value);
            } catch (Exception e) {
                check(false, "GameReader threw on " + value + ": " + e);
                continue;
            }
            // The object carries one byte stat so the scan has something to report it by; an object
            // with no stats is walked but never announced.
            byte[] packet = update(object(value, stat(0, 0, new byte[]{0})));
            int[] seen = {Integer.MIN_VALUE};
            boolean walked = UpdateScan.walk(packet, new UpdateScan.Visitor() {
                @Override
                public void onStatusList(int objectId, int countOffset, int count, int firstEntryOffset) {
                }

                @Override
                public void onStat(int objectId, int statType, int dataType) {
                    seen[0] = objectId;
                }
            });
            check(walked, "synthetic packet for " + value + " did not walk");
            checkEquals(value, seen[0], "the scan read a different object id for " + value);
        }
    }

    /**
     * One stat of every {@code DataType} the client can send, all in one object, must consume exactly
     * the bytes written.
     *
     * <p>The two sizes a naive implementation gets wrong are the Item (the decompiled reader is short
     * by eight bytes) and Marks. Both are included, and the walk is asserted to end on the last byte -
     * which is the only assertion that catches a wrong size, since a wrong size still stays in bounds.
     */
    private static void testEveryDataTypeWalksToTheExactEnd() {
        List<int[]> fired = new ArrayList<>();
        try {
            List<StatSpec> stats = new ArrayList<>();
            stats.add(stat(10, 0, new byte[]{0x34}));
            stats.add(stat(11, 1, shortBytes((short) -7)));
            stats.add(stat(12, 2, intBytes(707)));
            stats.add(stat(13, 3, longBytes(1234567890123L)));
            stats.add(stat(14, 4, string8("a name")));
            stats.add(stat(15, 5, itemBytes()));
            stats.add(stat(16, 6, pointBytes(1.5f, 2.5f)));
            stats.add(stat(17, 7, new byte[]{1, 2, 3, 4}));
            stats.add(stat(18, UpdateScan.STATUS_EFFECT_DATA_TYPE,
                    statusList(entry(5, 1, 1.0f), entry(CONFUSED, 1, 0.85f))));
            stats.add(stat(19, 9, floatBytes(9.5f)));
            stats.add(stat(20, 10, marksBytes(4242, 3)));

            byte[] packet = update(new ObjectSpec(7001, null, stats));
            boolean walked = UpdateScan.walk(packet, new UpdateScan.Visitor() {
                @Override
                public void onStatusList(int objectId, int countOffset, int count, int firstEntryOffset) {
                    fired.add(new int[]{count, firstEntryOffset});
                }

                @Override
                public void onIntegerStat(int objectId, int statType, long value) {
                    if (statType == 12) {
                        checkEquals(707L, value, "the scan mis-read an Int stat");
                    }
                }
            });
            check(walked, "a packet containing every DataType did not walk to its last byte");
            checkEquals(1, fired.size(), "the status list in the all-DataTypes packet was not reported");
            if (fired.size() == 1) {
                checkEquals(2, fired.get(0)[0], "the status list count was mis-read");
            }
        } catch (Exception e) {
            check(false, "building the all-DataTypes packet failed: " + e);
        }
    }

    /**
     * A payload that does not decode exactly must be refused.
     *
     * <p>This is the property the whole feature rests on: a walk that "mostly" worked produces offsets
     * that are wrong by a few bytes, and a rewrite built on them corrupts the stream. Every prefix of
     * the real capture that is not the whole packet must fail.
     */
    private static void testTruncatedPayloadIsRefused() {
        byte[] full = fixture();
        int refused = 0;
        int attempted = 0;
        for (int cut = 0; cut < full.length; cut += 7) {
            byte[] prefix = java.util.Arrays.copyOf(full, cut);
            attempted++;
            if (!UpdateScan.walk(prefix, silent())) {
                refused++;
            }
        }
        checkEquals(attempted, refused, "a truncated payload was accepted by the walk");

        byte[] corrupt = fixture().clone();
        corrupt[200] = (byte) 0xFF;                 // an impossible object count, mid-packet
        check(!UpdateScan.walk(corrupt, silent()),
                "a payload with a corrupt object block was accepted by the walk");
    }

    private static void testFixtureWalksExactly() {
        byte[] full = fixture();
        checkEquals(1066, full.length, "the captured fixture changed size");
        List<String> lists = new ArrayList<>();
        boolean walked = UpdateScan.walk(full, new UpdateScan.Visitor() {
            @Override
            public void onStatusList(int objectId, int countOffset, int count, int firstEntryOffset) {
                lists.add(objectId + "@" + countOffset + "x" + count);
            }
        });
        check(walked, "the captured Update did not walk to its exact last byte");
        checkEquals(List.of("1924@656x2", "1928@721x2", "1930@784x3"), lists,
                "the captured Update's status lists moved");
    }

    // --- the strip ----------------------------------------------------------------------------

    private static void testStripRemovesOnlyThePlayersEntry() {
        byte[] full = fixture();
        StatusStrip.Result result = StatusStrip.strip(full, 1930, CONFUSED);
        check(result != null, "the captured Confused entry was not stripped");
        if (result == null) {
            return;
        }
        checkEquals(1057, result.payload().length, "the stripped payload has the wrong length");
        checkEquals(1, result.removed(), "the wrong number of entries was reported as removed");
        checkEquals(1, result.lists(), "the wrong number of lists was reported as shortened");
        checkEquals(9, full.length - result.payload().length, "a status entry is not nine bytes");

        List<String> after = statusLists(result.payload());
        checkEquals(List.of("1924@656x2", "1928@721x2", "1930@784x2"), after,
                "the strip changed a list it was not asked to change");
        check(!containsEffect(result.payload(), 1930, CONFUSED),
                "the stripped payload still carries Confused on the player");
        check(containsEffect(result.payload(), 1930, 7),
                "the strip removed a neighbouring effect as well");
    }

    private static void testStripIsIdempotentAndNarrow() {
        StatusStrip.Result once = StatusStrip.strip(fixture(), 1930, CONFUSED);
        check(once != null, "the first strip returned nothing");
        if (once == null) {
            return;
        }
        check(StatusStrip.strip(once.payload(), 1930, CONFUSED) == null,
                "stripping an already-stripped packet changed it again");
        check(StatusStrip.strip(fixture(), 1924, CONFUSED) == null,
                "an object that never had Confused was rewritten");
        check(StatusStrip.strip(fixture(), 1928, CONFUSED) == null,
                "an object that never had Confused was rewritten");
        check(StatusStrip.strip(fixture(), 1930, 32) != null,
                "removing a different effect from the player removed nothing");
    }

    private static void testStripRejectsWhatItCannotUnderstand() {
        check(StatusStrip.strip(fixture(), -1, CONFUSED) == null,
                "the strip acted without knowing which object is the player");
        check(StatusStrip.strip(null, 1930, CONFUSED) == null, "the strip accepted a null payload");
        check(StatusStrip.strip(new byte[]{0x0A, 0x00}, 1930, CONFUSED) == null,
                "the strip acted on something that is not a GmUpdate");
        check(StatusStrip.strip(new byte[]{0x46, 0x00, 0x01}, 1930, CONFUSED) == null,
                "the strip acted on a HealthUpdate");
        byte[] truncated = java.util.Arrays.copyOf(fixture(), 800);
        check(StatusStrip.strip(truncated, 1930, CONFUSED) == null,
                "the strip rewrote a payload it could not walk");
    }

    private static void testStripEmptiesASingleEntryList() {
        byte[] packet = update(object(900, stat(UpdateScan.STATUS_EFFECTS_STAT,
                UpdateScan.STATUS_EFFECT_DATA_TYPE, statusList(entry(CONFUSED, 1, 1.0f)))));
        StatusStrip.Result result = StatusStrip.strip(packet, 900, CONFUSED);
        check(result != null, "a one-entry Confused list was not stripped");
        if (result == null) {
            return;
        }
        checkEquals(1, result.removed(), "the empty-list rewrite reported the wrong removal count");
        check(!containsEffect(result.payload(), 900, CONFUSED),
                "the emptied list still carries Confused");
        // The list is now `count = 0` and nothing else; the walk must still land exactly.
        check(UpdateScan.walk(result.payload(), silent()), "the emptied list broke the walk");
    }

    private static void testStripHandlesSeveralListsOnOneObject() {
        byte[] packet = update(
                object(901,
                        stat(UpdateScan.STATUS_EFFECTS_STAT, UpdateScan.STATUS_EFFECT_DATA_TYPE,
                                statusList(entry(2, 1, 0.2f), entry(CONFUSED, 1, 0.5f))),
                        stat(9, 9, floatBytes(1.0f)),
                        stat(UpdateScan.STATUS_EFFECTS_STAT, UpdateScan.STATUS_EFFECT_DATA_TYPE,
                                statusList(entry(CONFUSED, 1, 0.9f), entry(17, 1, 0.1f)))));
        StatusStrip.Result result = StatusStrip.strip(packet, 901, CONFUSED);
        check(result != null, "two Confused lists on one object were not both stripped");
        if (result == null) {
            return;
        }
        checkEquals(2, result.removed(), "two lists with one removal each reported the wrong total");
        checkEquals(2, result.lists(), "two lists were not both counted");
        checkEquals(packet.length - 18, result.payload().length,
                "two entries removed did not shrink the packet by eighteen bytes");
        check(UpdateScan.walk(result.payload(), silent()), "the two-list rewrite broke the walk");
    }

    /**
     * Several armed effects at once, which is what the dashboard's checkboxes actually ask for.
     *
     * <p>The fixture's player list carries three entries ({@code Slowed}, {@code Confused} and
     * {@code Barrier}); arming two of them must remove exactly those two and leave the third, shorten
     * the packet by eighteen bytes, and name the ordinals it removed - the log line and the dashboard
     * counter are built from that list, so a strip that reported the wrong effects would be reported
     * as success while doing something else.
     */
    private static void testStripRemovesSeveralEffectsAtOnce() {
        byte[] full = fixture();
        StatusStrip.Result result = StatusStrip.strip(full, 1930,
                java.util.Set.of(StatusStrip.SLOWED, StatusStrip.CONFUSED));
        check(result != null, "arming two effects removed nothing from the captured packet");
        if (result == null) {
            return;
        }
        checkEquals(2, result.removed(), "the wrong number of entries was reported as removed");
        checkEquals(1, result.lists(), "only the player's list should have been shortened");
        checkEquals(full.length - 18, result.payload().length,
                "two entries removed did not shrink the packet by eighteen bytes");
        checkEquals(java.util.List.of(StatusStrip.SLOWED, StatusStrip.CONFUSED), result.removedEffects(),
                "the removed effects are not reported in ordinal order");
        check(!containsEffect(result.payload(), 1930, StatusStrip.SLOWED),
                "Slowed survived a strip that armed it");
        check(!containsEffect(result.payload(), 1930, StatusStrip.CONFUSED),
                "Confused survived a strip that armed it");
        check(containsEffect(result.payload(), 1930, StatusStrip.BARRIER),
                "an unarmed effect on the same list was removed as well");
        checkEquals(java.util.List.of("1924@656x2", "1928@721x2", "1930@784x1"), statusLists(result.payload()),
                "the multi-effect strip changed a list it was not asked to change");
        // An effect nobody has must not turn a real strip into a refusal.
        StatusStrip.Result withAbsent = StatusStrip.strip(full, 1930,
                java.util.Set.of(StatusStrip.CONFUSED, StatusStrip.PARALYZED));
        check(withAbsent != null && withAbsent.removed() == 1,
                "a set containing an effect the packet does not carry removed nothing");
        checkEquals(java.util.List.of(StatusStrip.CONFUSED), withAbsent == null ? null : withAbsent.removedEffects(),
                "an absent effect was reported as removed");
    }

    // --- the locator --------------------------------------------------------------------------

    private static void testLocatorNeedsEvidence() {
        PlayerLocator locator = new PlayerLocator(3);
        locator.onWorldEntry();
        checkEquals(-1, locator.playerId(), "the locator answered before it had any evidence");

        locator.onHealth(100, now());
        locator.observe(update(object(5, stat(UpdateScan.HP_STAT, 1, shortBytes((short) 100)))), now());
        checkEquals(-1, locator.playerId(), "the locator answered after a single matching reading");
        checkEquals("no object has 3 matching health readings yet (best 1, runner-up 0)",
                locator.explain(), "the locator's explanation does not describe why it declined");

        for (int i = 0; i < 2; i++) {
            locator.onHealth(100, now());
            locator.observe(update(object(5, stat(UpdateScan.HP_STAT, 1, shortBytes((short) 100)))), now());
        }
        checkEquals(5, locator.playerId(), "the locator did not resolve after three readings");
    }

    private static void testLocatorRequiresAMargin() {
        PlayerLocator locator = new PlayerLocator(2);
        locator.onWorldEntry();
        for (int i = 0; i < 3; i++) {
            locator.onHealth(100, now());
            locator.observe(update(
                    object(5, stat(UpdateScan.HP_STAT, 1, shortBytes((short) 100))),
                    object(6, stat(UpdateScan.HP_STAT, 1, shortBytes((short) 100)))), now());
        }
        checkEquals(-1, locator.playerId(),
                "the locator guessed between two objects with identical tallies");
    }

    private static void testLocatorPrefersOwnCharacterId() {
        PlayerLocator locator = new PlayerLocator(3);
        locator.onWorldEntry();
        locator.onHealth(100, now());
        locator.observe(update(object(5, stat(UpdateScan.HP_STAT, 1, shortBytes((short) 100)))),
                now());
        checkEquals(-1, locator.playerId(), "one reading was enough, which it should not be");

        locator.observe(update(object(77, stat(UpdateScan.OWN_CHARACTER_ID_STAT, 3, longBytes(4242L)))),
                now());
        checkEquals(77, locator.playerId(), "an OwnCharacterId sighting did not resolve the player");
    }

    private static void testLocatorResetsPerWorld() {
        PlayerLocator locator = new PlayerLocator(2);
        locator.onWorldEntry();
        for (int i = 0; i < 3; i++) {
            locator.onHealth(100, now());
            locator.observe(update(object(5, stat(UpdateScan.HP_STAT, 1, shortBytes((short) 100)))), now());
        }
        checkEquals(5, locator.playerId(), "the locator did not resolve before the world change");
        locator.onWorldEntry();
        checkEquals(-1, locator.playerId(),
                "the locator kept an object id from the previous world, where ids are renumbered");
    }

    // --- builders -----------------------------------------------------------------------------

    private record StatSpec(int statType, int dataType, byte[] value) {
    }

    private record ObjectSpec(int id, Integer newType, List<StatSpec> stats) {
    }

    private static StatSpec stat(int statType, int dataType, byte[] value) {
        return new StatSpec(statType, dataType, value);
    }

    private static ObjectSpec object(int id, StatSpec... stats) {
        return new ObjectSpec(id, null, List.of(stats));
    }

    /**
     * A complete game {@code GmUpdate} payload.
     *
     * <p>Written with the project's own {@link GameWriter} because the varint is the one field whose
     * bytes a hand-written fixture would silently get wrong - the first continuation group carries bits
     * 6-12, not 7-13, and an earlier round of this project had an encoder that disagreed with its own
     * reader. Everything else (which stat, which data type, how long the value is) is supplied by the
     * caller, so a size the walker gets wrong is a failing test rather than a shared mistake.
     */
    private static byte[] update(ObjectSpec... objects) {
        try (var bytes = new ByteArrayOutputStream(); var w = new GameWriter(bytes)) {
            w.writeUInt16(UpdateScan.UPDATE_ID);
            w.writeVarint(0);                       // update id
            w.writeVarint(0);                       // no removed objects
            w.writeVarint(0);                       // no killed objects
            w.writeVarint(objects.length);
            for (ObjectSpec object : objects) {
                w.writeVarint(object.id());
                w.writeBool(object.newType() != null);
                if (object.newType() != null) {
                    w.writeInt16(object.newType().shortValue());
                }
                w.writeByte(object.stats().size());
                for (StatSpec spec : object.stats()) {
                    w.writeByte(spec.statType());
                    w.writeByte(spec.dataType());
                    w.write(spec.value());
                }
            }
            w.writeVarint(0);                       // Dt
            return bytes.toByteArray();
        } catch (Exception e) {
            throw new IllegalStateException("building a synthetic Update failed", e);
        }
    }

    // --- byte helpers -------------------------------------------------------------------------

    private static byte[] varint(int value) {
        try (var bytes = new ByteArrayOutputStream(); var w = new GameWriter(bytes)) {
            w.writeVarint(value);
            return bytes.toByteArray();
        } catch (Exception e) {
            throw new IllegalStateException(e);
        }
    }

    private static byte[] shortBytes(short value) {
        return new byte[]{(byte) value, (byte) (value >> 8)};
    }

    private static byte[] longBytes(long value) {
        byte[] out = new byte[8];
        for (int i = 0; i < 8; i++) {
            out[i] = (byte) (value >>> (8 * i));
        }
        return out;
    }

    private static byte[] floatBytes(float value) {
        return intBytes(Float.floatToIntBits(value));
    }

    private static byte[] intBytes(int value) {
        return new byte[]{(byte) value, (byte) (value >> 8), (byte) (value >> 16), (byte) (value >> 24)};
    }

    private static byte[] entry(int effect, int tier, float duration) {
        byte[] out = new byte[UpdateScan.STATUS_ENTRY_BYTES];
        System.arraycopy(intBytes(effect), 0, out, 0, 4);
        out[4] = (byte) tier;
        System.arraycopy(floatBytes(duration), 0, out, 5, 4);
        return out;
    }

    private static byte[] statusList(byte[]... entries) {
        byte[] out = new byte[1 + UpdateScan.STATUS_ENTRY_BYTES * entries.length];
        out[0] = (byte) entries.length;
        for (int i = 0; i < entries.length; i++) {
            System.arraycopy(entries[i], 0, out, 1 + i * UpdateScan.STATUS_ENTRY_BYTES,
                    UpdateScan.STATUS_ENTRY_BYTES);
        }
        return out;
    }

    private static byte[] string8(String value) {
        byte[] text = value.getBytes(java.nio.charset.StandardCharsets.UTF_8);
        byte[] out = new byte[1 + text.length];
        out[0] = (byte) text.length;
        System.arraycopy(text, 0, out, 1, text.length);
        return out;
    }

    private static byte[] pointBytes(float x, float y) throws Exception {
        try (var bytes = new ByteArrayOutputStream(); var w = new GameWriter(bytes)) {
            w.writeFloatLE(x);
            w.writeFloatLE(y);
            return bytes.toByteArray();
        }
    }

    private static byte[] marksBytes(int objectId, int color) throws Exception {
        try (var bytes = new ByteArrayOutputStream(); var w = new GameWriter(bytes)) {
            w.writeByte(1);
            w.writeVarint(objectId);
            w.writeByte(color);
            return bytes.toByteArray();
        }
    }

    /** The empirically verified Item layout, written by hand so a size change is a test failure. */
    private static byte[] itemBytes() throws Exception {
        try (var bytes = new ByteArrayOutputStream(); var w = new GameWriter(bytes)) {
            w.writeInt16((short) 17);      // Type
            w.writeByte(2);                // Count
            w.writeInt16((short) 3);       // AffixType
            w.writeByte(4);                // Flags
            w.writeInt64(5566778899L);     // CharacterId
            w.writeString8("abc");         // DroppedFor
            w.writeByte(9);                // SpriteIndex
            w.writeString8("de");          // SubDesc
            w.writeInt64(42L);             // Traits
            w.writeUInt16(5);              // Quality
            w.writeByte(6);                // UpgradeProgress
            return bytes.toByteArray();
        }
    }

    private static void writeEntry(GameWriter w, int effect, int tier, float duration) throws Exception {
        w.write(intBytes(effect));
        w.writeByte(tier);
        w.write(floatBytes(duration));
    }

    private static long now() {
        return System.currentTimeMillis();
    }

    // --- assertions ---------------------------------------------------------------------------

    private static UpdateScan.Visitor silent() {
        return new UpdateScan.Visitor() {
            @Override
            public void onStatusList(int objectId, int countOffset, int count, int firstEntryOffset) {
            }
        };
    }

    private static List<String> statusLists(byte[] payload) {
        List<String> out = new ArrayList<>();
        UpdateScan.walk(payload, new UpdateScan.Visitor() {
            @Override
            public void onStatusList(int objectId, int countOffset, int count, int firstEntryOffset) {
                out.add(objectId + "@" + countOffset + "x" + count);
            }
        });
        return out;
    }

    private static boolean containsEffect(byte[] payload, int objectId, int effect) {
        boolean[] found = {false};
        UpdateScan.walk(payload, new UpdateScan.Visitor() {
            @Override
            public void onStatusList(int id, int countOffset, int count, int firstEntryOffset) {
                if (id != objectId) {
                    return;
                }
                for (int i = 0; i < count; i++) {
                    int at = firstEntryOffset + i * UpdateScan.STATUS_ENTRY_BYTES;
                    int value = (payload[at] & 0xFF) | (payload[at + 1] & 0xFF) << 8
                            | (payload[at + 2] & 0xFF) << 16 | (payload[at + 3] & 0xFF) << 24;
                    if (value == effect) {
                        found[0] = true;
                    }
                }
            }
        });
        return found[0];
    }

    private static void check(boolean condition, String description) {
        checks++;
        if (!condition) {
            FAILURES.add(description);
        }
    }

    private static void checkEquals(Object expected, Object actual, String description) {
        checks++;
        if (!java.util.Objects.equals(expected, actual)) {
            FAILURES.add(description + " (expected " + expected + ", got " + actual + ")");
        }
    }

    /**
     * Packet {@code seq 331827} of the 2026-10-05 session: the {@code GmUpdate} that carried
     * {@code Confused} on object 1930, with two other objects' status lists alongside it.
     */
    private static byte[] fixture() {
        return java.util.HexFormat.of().parseHex(FIXTURE);
    }

    private static final String FIXTURE =
            "0100840603BE08BD08AD08002F940B00014B06251FFF43B5EF0244962000014B067C94014478800244902000035B0260" +
            "1500005002B60400004B06268701442BC70344990D01040B021C090000803F4B0600600444006002449A0D01040B021C" +
            "090000803F4B0600600444006002449B0D01040B021C090000803F4B0600A0044400600244A00E01040B021C09000080" +
            "3F4B060020044400E00344A10E01040B021C090000803F4B060060044400E00344A20E01040B021C090000803F4B0600" +
            "60044400E00344871F01040B031C090000803F6D00014B060020044400E00344BD1F01FBF6031C090000803F6D00014B" +
            "060020044400E00344901201040B021C090000803F4B060040FF4300600744A51F011F0B031C090000803F6D00014B06" +
            "0040FE4300200744A71F01FBF6031C090000803F6D00014B060040FF4300200744A81F01FBF6031C090000803F6D0001" +
            "4B0600C0FF4300200744881201040B021C090000803F4B0600E0004400200744801F01040B031C090000803F6D00014B" +
            "0600E0004400200744A41F011F0B031C090000803F6D00014B060020014400E00644A91F01FBF6031C090000803F6D00" +
            "014B060020004400200744AA1F01FBF6031C090000803F6D00014B060060004400200744AB1F01FBF6031C090000803F" +
            "6D00014B0600A0004400200744AC1F01FBF6031C090000803F6D00014B0600E0004400200744AD1F011F0B031C090000" +
            "803F6D00014B060020014400200744BF1F011F0B031C090000803F6D00014B060020044400200444BF1D00034B064494" +
            "FC431E3502445002DD0100000301AB00801E000103011B00811E00024B068802FB4385D1024403015900821E00014B06" +
            "F584FE434EAE0044831E00024B062F72FC43FC80014403018100841E00034E08022000000001E5948747110000000100" +
            "00003250023502000003014400851E00024B06A8E9FB439D820244500295010000881E00034B063CCAFE43922501444E" +
            "08020200000001CDCC4C3E2000000001F794874703015000891E00034B06DABB014495F000445002DA01000003011700" +
            "8A1E00074B06BA29FF43336D02444E08032000000001FD9487470700000001F953D33F0B000000019A99593F5002F500" +
            "0000030152000201F5005002F5000000030152008B1E00024B06FEABFD4306000144030155008E1E00010301FB01901E" +
            "00024B0696000044AA3E00440301A200951E00024B06934D0044EE73FF4303015000981E00025002C70200000301B400" +
            "9C1E00024B06DAE7FC4395FF014403010001A11E00024B066C13FF4377C30044500232020000A31E00034B06CE990044" +
            "748A00445002AC0100000301CA00812000024B066BF5FC43AB33024403016700822000025002E402000003013D008320" +
            "00014B064AB3FE4339CC00448420000250020C0200000301C700852000044B064072FE43E58B01445002110200000301" +
            "EA008702000000008803";
}
