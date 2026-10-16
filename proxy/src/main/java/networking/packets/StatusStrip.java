package networking.packets;

import java.io.ByteArrayOutputStream;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.TreeMap;

/**
 * Removes status effects from the server→client {@code GmUpdate} stream.
 *
 * <h2>What this is for</h2>
 *
 * <p>{@code StatsType.StatusEffects} (78) is the only channel a status effect ever arrives on. Walking
 * the captured stream settles that: over 26,278 fully-decoded {@code GmUpdate} packets the legacy
 * {@code StatsType.Confused} (68) byte stat never appears once, while effect ordinal 11 - the
 * {@code DarzaCore.Data.StatusEffect.Confused} the client renders as "Scrambles core movement
 * controls" - arrives hundreds of times inside a stat-78 list. Each entry is nine bytes:
 *
 * <pre>{@code [int32 Effect][uint8 Tier][float32 Duration]}</pre>
 *
 * <p>The ordinals are the enum's declaration order, which is not obfuscated, so more than one effect
 * can be named by number: 6 is {@code Paralyzed}, 7 is {@code Slowed}, 16 is
 * {@code Hallucinating}. {@link #NAMED} carries the four the dashboard offers; the relay accepts any
 * ordinal.
 *
 * <h2>Why removal and not a shortened duration</h2>
 *
 * <p>Zeroing or shortening an entry does nothing, and this is worth stating precisely because it
 * looks like it should work. The client's only status model is
 * {@code Entity.Effects.EffectData}, and every list the server sends <em>replaces</em> it wholesale
 * ({@code StatusEffects.UpdateEffects}: {@code EffectData = statusEffects}). The question "do I have
 * this effect" is answered by {@code ContainsEffect}, which scans that array comparing
 * {@code Effect == effect} and never looks at {@code Duration}. {@code StatusEffectInstance}'s
 * equality is a hash of {@code Effect}, {@code Tier} and {@code Id} - not {@code Duration} - and the
 * client's own ticking status model ({@code DarzaGameNet.Packets.Status}) is never instantiated
 * anywhere in the shipped client, so nothing expires an entry locally. The entry is either present or
 * it is not; a duration of zero is only a different number in a field nobody reads.
 *
 * <h2>Why the payload is rebuilt rather than the list edited in place</h2>
 *
 * <p>Dropping an entry shortens the packet, and the frame's 4-byte length prefix has to agree with the
 * new length. {@link networking.Relay} therefore owns the re-framing; this class only produces the new
 * payload. The caller must also re-run the walk on the result before trusting it - see {@link #strip},
 * which does that itself and refuses to return anything it could not re-read.
 */
public final class StatusStrip {

    /** {@code DarzaCore.Data.StatusEffect.Paralyzed}. */
    public static final int PARALYZED = 6;

    /** {@code DarzaCore.Data.StatusEffect.Slowed}. */
    public static final int SLOWED = 7;

    /** {@code DarzaCore.Data.StatusEffect.Confused}. */
    public static final int CONFUSED = 11;

    /**
     * {@code DarzaCore.Data.StatusEffect.Hallucinating}.
     *
     * <p>The one effect in the client whose entire consequence is cosmetic, and the only one verified
     * safe to remove: it is read only by {@code World.SetHallucinating} →
     * {@code GameObject.ToggleHallucinating}, which swaps the sprite set. It is not part of the
     * movement law, the input remap, the attack rate or any packet the client sends, so unlike Slowed
     * and Cutscene there is nothing on the wire for the server to re-simulate against a stripped
     * entry. That has been confirmed, so it is armed by default alongside Confused.
     */
    public static final int HALLUCINATING = 16;

    /**
     * {@code DarzaCore.Data.StatusEffect.Barrier}.
     *
     * <p>Not offered by the dashboard (it is a benefit, not a debuff) but named because it is the
     * effect the test fixture carries alongside the two being removed, and a bare {@code 32} in a test
     * asserting "the unarmed entry survived" is exactly the kind of magic number that stops being
     * true without anyone noticing.
     */
    public static final int BARRIER = 32;

    /**
     * The effects the dashboard offers by name, in the order it lists them.
     *
     * <p>Confused is first because it is the one this feature was built for, and it and Hallucinating
     * are the two armed by default; Paralyzed and Slowed are the two other debuffs the capture shows
     * arriving on the local player, both of which sit in the movement law and are therefore off until
     * asked for. The map is the single place a name and an ordinal are tied together, so the
     * dashboard, the log line and the config file cannot disagree.
     */
    public static final Map<String, Integer> NAMED = named();

    /** Built in a fixed order: the dashboard renders these in the order it reads them. */
    private static Map<String, Integer> named() {
        var out = new LinkedHashMap<String, Integer>();
        out.put("confused", CONFUSED);
        out.put("paralyzed", PARALYZED);
        out.put("slowed", SLOWED);
        out.put("hallucinating", HALLUCINATING);
        return java.util.Collections.unmodifiableMap(out);
    }

    private StatusStrip() {
    }

    /** The display name for an ordinal, or {@code "effect N"} when the dashboard has no name for it. */
    public static String name(int effect) {
        for (Map.Entry<String, Integer> entry : NAMED.entrySet()) {
            if (entry.getValue() == effect) {
                return entry.getKey();
            }
        }
        return "effect " + effect;
    }

    /**
     * One stat-78 list that lost at least one entry.
     *
     * @param countOffset the offset of the list's 1-byte entry count
     * @param firstEntryOffset the offset of its first 9-byte entry
     * @param count the entry count before the rewrite
     * @param keptCount the entry count after it
     * @param kept the surviving entries, concatenated
     */
    private record Rewrite(int countOffset, int firstEntryOffset, int count, int keptCount,
                           byte[] kept) {
    }

    /**
     * The replacement payload and what was taken out of it.
     *
     * @param payload the rewritten game packet payload
     * @param removed how many status entries were dropped
     * @param lists how many stat-78 lists were shortened
     * @param removedEffects which ordinals were actually dropped, sorted
     */
    public record Result(byte[] payload, int removed, int lists, List<Integer> removedEffects) {
    }

    /** Drops one effect; the single-effect form the tests and a hand-run use. */
    public static Result strip(byte[] payload, int playerId, int effect) {
        return strip(payload, playerId, Set.of(effect));
    }

    /**
     * Drops every effect in {@code effects} from every status list belonging to {@code playerId}.
     *
     * <p>Only the local player's own list is touched. Every other object in the packet keeps its
     * effects: a boss that confuses a whole party would otherwise have the debuff removed from
     * everyone, which is a far more visible change than removing it from one character.
     *
     * @param payload a complete game packet payload, {@code [2-byte LE id][body]}
     * @param playerId the local player's object id, or {@code -1} when it is not known yet
     * @param effects the {@code StatusEffect} ordinals to remove
     * @return the replacement, or {@code null} when there is nothing to change, the payload is not an
     *     {@code Update}, the player is unknown, or the walk did not decode exactly
     */
    public static Result strip(byte[] payload, int playerId, Set<Integer> effects) {
        if (payload == null || playerId < 0 || effects == null || effects.isEmpty() || payload.length < 2) {
            return null;
        }
        if ((payload[0] & 0xFF) != (UpdateScan.UPDATE_ID & 0xFF) || (payload[1] & 0xFF) != 0) {
            return null;
        }

        List<Rewrite> rewrites = new ArrayList<>(1);
        Map<Integer, Integer> removedByEffect = new TreeMap<>();
        boolean walked = UpdateScan.walk(payload, new UpdateScan.Visitor() {
            @Override
            public void onStatusList(int objectId, int countOffset, int count, int firstEntryOffset) {
                if (objectId != playerId || count == 0 || !fits(payload, count, firstEntryOffset)) {
                    return;
                }
                byte[] kept = new byte[count * UpdateScan.STATUS_ENTRY_BYTES];
                int keptLength = 0;
                for (int i = 0; i < count; i++) {
                    int entry = firstEntryOffset + i * UpdateScan.STATUS_ENTRY_BYTES;
                    int effect = effectAt(payload, entry);
                    if (effects.contains(effect)) {
                        removedByEffect.merge(effect, 1, Integer::sum);
                        continue;
                    }
                    System.arraycopy(payload, entry, kept, keptLength, UpdateScan.STATUS_ENTRY_BYTES);
                    keptLength += UpdateScan.STATUS_ENTRY_BYTES;
                }
                if (keptLength == kept.length) {
                    return;
                }
                byte[] trimmed = new byte[keptLength];
                System.arraycopy(kept, 0, trimmed, 0, keptLength);
                rewrites.add(new Rewrite(countOffset, firstEntryOffset, count,
                        keptLength / UpdateScan.STATUS_ENTRY_BYTES, trimmed));
            }

            @Override
            public void onStat(int objectId, int statType, int dataType) {
            }
        });
        if (!walked || rewrites.isEmpty()) {
            return null;
        }

        var out = new ByteArrayOutputStream(payload.length);
        int cursor = 0;
        int removed = 0;
        for (Rewrite rewrite : rewrites) {
            out.write(payload, cursor, rewrite.countOffset() - cursor);
            out.write(rewrite.keptCount());
            out.write(rewrite.kept(), 0, rewrite.kept().length);
            cursor = rewrite.firstEntryOffset() + rewrite.count() * UpdateScan.STATUS_ENTRY_BYTES;
            removed += rewrite.count() - rewrite.keptCount();
        }
        out.write(payload, cursor, payload.length - cursor);
        byte[] result = out.toByteArray();

        // A rewrite that does not re-read to its exact new length is a rewritten packet that would
        // desync the session. Refusing it costs one lost debuff removal; accepting it costs the run.
        if (!reWalkIsClean(result, playerId, effects)) {
            return null;
        }
        return new Result(result, removed, rewrites.size(), List.copyOf(removedByEffect.keySet()));
    }

    /**
     * Whether all {@code count} entries at {@code firstEntryOffset} lie inside {@code payload}.
     *
     * <p>Belt and braces: {@link UpdateScan} already refuses to report a list that does not fit, and
     * this class indexes the array directly, so it re-checks rather than trusting a contract it does
     * not own. An index out of bounds here would be an exception on the forwarding thread.
     */
    private static boolean fits(byte[] payload, int count, int firstEntryOffset) {
        return count >= 0 && firstEntryOffset >= 0
                && (long) firstEntryOffset + (long) count * UpdateScan.STATUS_ENTRY_BYTES
                        <= payload.length;
    }

    /** The effect ordinal of a 9-byte entry, little-endian, without allocating. */
    private static int effectAt(byte[] payload, int offset) {
        return (payload[offset] & 0xFF)
                | (payload[offset + 1] & 0xFF) << 8
                | (payload[offset + 2] & 0xFF) << 16
                | (payload[offset + 3] & 0xFF) << 24;
    }

    /** True when {@code result} decodes exactly and no longer carries any of {@code effects} on {@code playerId}. */
    private static boolean reWalkIsClean(byte[] result, int playerId, Set<Integer> effects) {
        boolean[] clean = {true};
        boolean walked = UpdateScan.walk(result, new UpdateScan.Visitor() {
            @Override
            public void onStatusList(int objectId, int countOffset, int count, int firstEntryOffset) {
                if (objectId != playerId || !fits(result, count, firstEntryOffset)) {
                    return;
                }
                for (int i = 0; i < count; i++) {
                    if (effects.contains(effectAt(result, firstEntryOffset + i * UpdateScan.STATUS_ENTRY_BYTES))) {
                        clean[0] = false;
                    }
                }
            }

            @Override
            public void onStat(int objectId, int statType, int dataType) {
            }
        });
        return walked && clean[0];
    }
}
