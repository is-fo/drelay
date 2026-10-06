package networking;

import networking.log.Event;
import networking.log.EventLog;
import networking.log.LogClock;
import networking.packets.StatusStrip;
import networking.util.Fields;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.atomic.AtomicLong;

/**
 * The server→client status-effect strip, as a module the dashboard owns.
 *
 * <h2>What it is</h2>
 *
 * <p>It answers one question per server→client packet: "would the client be better off not knowing
 * about one of these effects?" If so the effect's nine-byte entry is cut out of the player's own
 * {@code StatsType.StatusEffects} list and the frame is re-framed with the shorter length. The wire
 * format, the reasoning about durations, and the refusal rules live in {@link StatusStrip}; the
 * player-object identification lives in {@link PlayerLocator}.
 *
 * <h2>Why it is a module and not three fields on {@code Config}</h2>
 *
 * <p>Because it is the one feature that changes bytes the server wrote, and everything about it has
 * to be inspectable from the dashboard and persistent across restarts: which effects are armed, how
 * many entries have been removed, and when the last rewrite happened. A bare boolean could not
 * answer "is this thing actually doing anything", which for a silent feature is the only question
 * that matters.
 *
 * <p>Unlike the auto-nexus rule this is <strong>on by default</strong>, with Confused and Hallucinating
 * armed. That is a deliberate default rather than an oversight: see {@code docs/INJECTION.md} section
 * 5 for what each effect does, why the server's copy cannot be removed, and what the blast radius is
 * if the player object is ever mis-identified.
 */
public final class Strip {

    /**
     * The ordinals armed when nothing overrides them, in the order the dashboard lists them.
     *
     * <p>Confused is the effect this feature was built for. Hallucinating is armed alongside it
     * because it is the one effect whose entire client-side consequence is a sprite swap: unlike
     * Slowed, Paralyzed, Cutscene, Grounded and FearOfTheBull it is not an input to the client's
     * movement law, so the client sends nothing derived from it and the server has nothing to
     * re-simulate against a stripped entry. Everything else the panel offers is either
     * server-computed (so removing the icon changes no outcome) or in a law the server checks.
     */
    private static final List<Integer> DEFAULT_EFFECTS =
            List.of(StatusStrip.CONFUSED, StatusStrip.HALLUCINATING);

    /** Everything tunable, all of it overridable from the route table, the dashboard or a property. */
    public static final class Config {

        /**
         * Master switch. On by default; {@link #effects} decides what is actually removed, so an
         * empty effect set is also "off" without the switch having to be flipped.
         */
        private volatile boolean enabled = true;

        /**
         * A live snapshot of the armed ordinals.
         *
         * <p>Volatile and immutable so the forwarding thread can test membership without a lock: the
         * set is replaced wholesale by {@link #apply}, never mutated in place. A strip that raced a
         * settings change would otherwise be able to remove an effect the operator had just disabled.
         *
         * <p>Insertion-ordered rather than {@code Set.of}: the startup line and the dashboard both
         * render this set in iteration order, and an unspecified order would make the same
         * configuration print differently from run to run.
         */
        private volatile Set<Integer> effects = immutable(DEFAULT_EFFECTS);

        /** The mutable set the snapshot is taken from; only touched under this object's monitor. */
        private final Set<Integer> armed = new LinkedHashSet<>(DEFAULT_EFFECTS);

        /** An unmodifiable snapshot that keeps the order it was built with. */
        private static Set<Integer> immutable(List<Integer> values) {
            return java.util.Collections.unmodifiableSet(new LinkedHashSet<>(values));
        }

        /** How many independent health readings an object needs before it is believed to be the player. */
        private volatile int minVotes = 3;

        public boolean enabled() {
            return enabled;
        }

        /** Whether a packet should be scanned at all: the switch is on and something is armed. */
        public boolean active() {
            return enabled && !effects.isEmpty();
        }

        public Set<Integer> effects() {
            return effects;
        }

        public int minVotes() {
            return minVotes;
        }

        public Config copy() {
            Config copy = new Config();
            copy.enabled = enabled;
            copy.minVotes = minVotes;
            copy.armed.clear();
            copy.armed.addAll(armed);
            copy.effects = Set.copyOf(armed);
            return copy;
        }

        /** The configuration as the dashboard and the config file see it. */
        public Map<String, Object> toMap() {
            return Fields.of()
                    .add("enabled", enabled)
                    .add("effects", new ArrayList<>(effects))
                    .add("minVotes", minVotes);
        }

        /**
         * Applies a sparse set of overrides, exactly like the auto-nexus config.
         *
         * <p>Accepted keys, names case-insensitive with {@code _} folded to {@code -}:
         *
         * <ul>
         *   <li>{@code enabled} - the master switch;</li>
         *   <li>{@code effects} - a list, or a comma/space separated string, replacing the armed set;</li>
         *   <li>{@code effect} - a single ordinal, replacing the armed set (the pre-module form, kept
         *       so an existing {@code relay-routes.json} keeps working);</li>
         *   <li>{@code confused} / {@code paralyzed} / {@code slowed} / {@code hallucinating} - arm
         *       or disarm one named effect;</li>
         *   <li>{@code minVotes} - the locator's evidence requirement.</li>
         * </ul>
         *
         * <p>An unknown key is reported rather than ignored: a typo would otherwise look like a
         * setting that simply had no effect.
         */
        public synchronized List<String> apply(Map<String, ?> changes) {
            Map<String, Object> normalised = new LinkedHashMap<>();
            Map<String, String> original = new LinkedHashMap<>();
            for (Map.Entry<String, ?> raw : changes.entrySet()) {
                String key = raw.getKey().toLowerCase(java.util.Locale.ROOT).replace('_', '-');
                normalised.put(key, raw.getValue());
                original.putIfAbsent(key, raw.getKey());
            }
            List<String> applied = new ArrayList<>();
            // `effects` and `effect` replace the set; the named switches adjust it afterwards, so a
            // request may replace the set and disarm one name in the same call.
            if (normalised.containsKey("effects")) {
                Set<Integer> parsed = parseEffects(normalised.get("effects"));
                if (parsed != null) {
                    armed.clear();
                    armed.addAll(parsed);
                    applied.add("effects=" + armed);
                } else {
                    applied.add("ignored:" + original.get("effects"));
                }
            } else if (normalised.containsKey("effect")) {
                Set<Integer> parsed = parseEffects(normalised.get("effect"));
                if (parsed != null) {
                    armed.clear();
                    armed.addAll(parsed);
                    applied.add("effects=" + armed);
                } else {
                    applied.add("ignored:" + original.get("effect"));
                }
            }
            for (String name : StatusStrip.NAMED.keySet()) {
                if (!normalised.containsKey(name)) {
                    continue;
                }
                boolean on = bool(normalised.get(name), armed.contains(StatusStrip.NAMED.get(name)));
                if (on) {
                    armed.add(StatusStrip.NAMED.get(name));
                } else {
                    armed.remove(StatusStrip.NAMED.get(name));
                }
                applied.add(name + "=" + on);
            }
            if (normalised.containsKey("enabled") || normalised.containsKey("strip")) {
                String key = normalised.containsKey("enabled") ? "enabled" : "strip";
                enabled = bool(normalised.get(key), enabled);
                applied.add("enabled=" + enabled);
            }
            if (normalised.containsKey("minvotes") || normalised.containsKey("min-votes")
                    || normalised.containsKey("votes")) {
                String key = normalised.containsKey("minvotes") ? "minvotes"
                        : (normalised.containsKey("min-votes") ? "min-votes" : "votes");
                minVotes = (int) clampLong(normalised.get(key), minVotes, 1, 64);
                applied.add("minVotes=" + minVotes);
            }
            for (Map.Entry<String, Object> entry : normalised.entrySet()) {
                if (knownKeys.contains(entry.getKey())) {
                    continue;
                }
                applied.add("unknown:" + original.get(entry.getKey()));
            }
            effects = java.util.Collections.unmodifiableSet(new LinkedHashSet<>(armed));
            return applied;
        }

        private static final Set<String> knownKeys = Set.of(
                "enabled", "strip", "effect", "effects", "confused", "paralyzed", "slowed",
                "hallucinating", "minvotes", "min-votes", "votes");

        /** Parses a list, a single number or a comma/space separated string into ordinals. */
        private static Set<Integer> parseEffects(Object value) {
            Set<Integer> out = new LinkedHashSet<>();
            if (value == null) {
                return null;
            }
            if (value instanceof Iterable<?> iterable) {
                for (Object item : iterable) {
                    Integer effect = effectOf(item);
                    if (effect == null) {
                        return null;
                    }
                    out.add(effect);
                }
                return out;
            }
            if (value instanceof Number) {
                Integer effect = effectOf(value);
                return effect == null ? null : new LinkedHashSet<>(List.of(effect));
            }
            String text = String.valueOf(value);
            for (String part : text.split("[,\\s]+")) {
                if (part.isBlank()) {
                    continue;
                }
                Integer byName = StatusStrip.NAMED.get(part.trim().toLowerCase(java.util.Locale.ROOT));
                if (byName != null) {
                    out.add(byName);
                    continue;
                }
                try {
                    out.add(Integer.parseInt(part.trim()));
                } catch (NumberFormatException e) {
                    return null;
                }
            }
            return out;
        }

        private static Integer effectOf(Object value) {
            if (value instanceof Number n) {
                int effect = n.intValue();
                return effect < 0 ? null : effect;
            }
            if (value instanceof String s) {
                String text = s.trim().toLowerCase(java.util.Locale.ROOT);
                Integer byName = StatusStrip.NAMED.get(text);
                if (byName != null) {
                    return byName;
                }
                try {
                    int effect = Integer.parseInt(text);
                    return effect < 0 ? null : effect;
                } catch (NumberFormatException e) {
                    return null;
                }
            }
            return null;
        }

        private static boolean bool(Object value, boolean fallback) {
            if (value instanceof Boolean b) {
                return b;
            }
            if (value instanceof Number n) {
                return n.intValue() != 0;
            }
            if (value instanceof String s) {
                return switch (s.trim().toLowerCase(java.util.Locale.ROOT)) {
                    case "true", "yes", "on", "1" -> true;
                    case "false", "no", "off", "0" -> false;
                    default -> fallback;
                };
            }
            return fallback;
        }

        private static long clampLong(Object value, long fallback, long min, long max) {
            long parsed = fallback;
            if (value instanceof Number n) {
                parsed = n.longValue();
            } else if (value instanceof String s) {
                try {
                    parsed = Long.parseLong(s.trim());
                } catch (NumberFormatException e) {
                    return fallback;
                }
            }
            return Math.max(min, Math.min(max, parsed));
        }
    }

    private final Config config;
    private final EventLog log;
    private final AtomicLong packetsStripped = new AtomicLong();
    private final AtomicLong entriesRemoved = new AtomicLong();

    private volatile String lastStrippedAt;
    private volatile int lastObjectId = -1;
    private volatile int lastBytesBefore;
    private volatile int lastBytesAfter;

    public Strip(Config config, EventLog log) {
        this.config = config;
        this.log = log;
    }

    public Config config() {
        return config;
    }

    /**
     * Removes the armed effects from {@code playerId}'s lists in {@code payload}.
     *
     * <p>Called on the forwarding thread, after the packet has already been logged, so the event log
     * keeps what the server actually said. Returns {@code null} when there is nothing to change - the
     * caller then forwards the original bytes.
     *
     * @return the replacement payload, or {@code null} to forward the original unchanged
     */
    public byte[] rewrite(Session session, byte[] payload, int playerId) {
        StatusStrip.Result result = StatusStrip.strip(payload, playerId, config.effects());
        if (result == null) {
            return null;
        }
        packetsStripped.incrementAndGet();
        entriesRemoved.addAndGet(result.removed());
        lastStrippedAt = LogClock.iso(System.currentTimeMillis());
        lastObjectId = playerId;
        lastBytesBefore = payload.length;
        lastBytesAfter = result.payload().length;
        log.emit(Event.builder(Event.KIND_NOTE)
                .session(session.tag())
                .note("stripped %d status effect(s) %s from object %d: %d -> %d bytes"
                        .formatted(result.removed(), result.removedEffects(), playerId,
                                payload.length, result.payload().length))
                .put("stripped", result.removed())
                .put("lists", result.lists())
                .put("effects", result.removedEffects())
                .put("objectId", playerId)
                .put("bytesBefore", payload.length)
                .put("bytesAfter", result.payload().length)
                .put("hexBefore", EventLog.hex(payload))
                .put("hexAfter", EventLog.hex(result.payload())));
        return result.payload();
    }

    /** The one-line startup report, which has to make "off" as visible as "on". */
    public String describe() {
        if (!config.enabled()) {
            return "  strip: disabled (server->client payloads are forwarded verbatim)";
        }
        if (config.effects().isEmpty()) {
            return "  strip: enabled but no effect is armed (server->client payloads are forwarded verbatim)";
        }
        List<String> names = new ArrayList<>();
        for (int effect : config.effects()) {
            names.add("%d (%s)".formatted(effect, StatusStrip.name(effect)));
        }
        return "  strip: removing status effect(s) %s from the local player's lists (needs %d matching health readings)"
                .formatted(String.join(", ", names), config.minVotes());
    }

    // --- dashboard view ----------------------------------------------------------------------

    public Map<String, Object> toMap() {
        List<Map<String, Object>> named = new ArrayList<>();
        StatusStrip.NAMED.forEach((name, effect) -> named.add(Fields.of()
                .add("name", name)
                .add("effect", effect)
                .add("armed", config.effects().contains(effect))));
        return Fields.of()
                .add("config", config.toMap())
                .add("named", named)
                .add("active", config.active())
                .add("packetsStripped", packetsStripped.get())
                .add("entriesRemoved", entriesRemoved.get())
                .add("lastStrippedAt", lastStrippedAt)
                .add("lastObjectId", lastObjectId < 0 ? null : lastObjectId)
                .add("lastBytesBefore", lastBytesBefore == 0 ? null : lastBytesBefore)
                .add("lastBytesAfter", lastBytesAfter == 0 ? null : lastBytesAfter);
    }

    /** The counters as the dashboard reads them, without the config (which is in the event). */
    public Map<String, Object> counters() {
        return Fields.of()
                .add("packetsStripped", packetsStripped.get())
                .add("entriesRemoved", entriesRemoved.get())
                .add("lastStrippedAt", lastStrippedAt);
    }
}
