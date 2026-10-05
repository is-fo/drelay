package networking.log;

import java.util.ArrayList;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;

/**
 * A named, toggleable rule set that decides which events the dashboard shows.
 *
 * <p>The log is deliberately complete - every packet, both directions, for hours. That is the right
 * artifact for a post-session analysis and the wrong thing to look at while playing. A filter is the
 * middle ground: keep recording everything, show only what is being reasoned about right now.
 *
 * <p>A rule matches if <em>any</em> of its conditions hold (they are alternatives, not a conjunction):
 * a kind, a packet name, or a session tag. An event is shown when any <em>enabled</em> rule matches it.
 * No rules enabled means nothing is shown, which is a state worth being able to reach - a quiet
 * dashboard while a session is verified.
 *
 * <p>The first rule is the default on request: <strong>character HP and everything that acts on
 * it</strong>. That is not just {@code HealthUpdate} - it is the HP reading, the escape conversation
 * around it ({@code Escape}, {@code EscapeCastState}, {@code EscapeAck}, {@code ForcedEscape}), the
 * server's safe-area flag (which decides whether an escape is even meaningful), world entry, and every
 * decision or injection the relay itself made. Without the decision events the filter would show the
 * symptom and hide the cause.
 */
public record LogFilter(String name, boolean enabled, Set<String> kinds, Set<String> packets,
                        Set<String> sessions, boolean builtIn) {

    /** The one rule that is on when the relay starts, and the one the dashboard restores on reset. */
    public static final String DEFAULT_NAME = "character hp";

    /**
     * Packets that belong to the HP story.
     *
     * <p>{@code Update} (id 1) is in the list because it carries the same HP as fixed-width
     * {@code StatsType.Hp} (2) / {@code MaximumHp} (0) stats, which is the independent cross-check on
     * the varint decoder in {@code HealthUpdate}; seeing both in one view is how a mis-decode is
     * caught rather than trusted.
     */
    public static final Set<String> HP_PACKETS = Set.of(
            "HealthUpdate",
            "MapInfo",
            "MapInfoAck",
            "Update",
            "Escape",
            "EscapeCastState",
            "EscapeAck",
            "ForcedEscape",
            "SafeAreaState",
            "Reconnect",
            "Ping");

    public static LogFilter defaultFilter() {
        return new LogFilter(DEFAULT_NAME, true,
                Set.of(Event.KIND_HEALTH, Event.KIND_NEXUS, Event.KIND_INJECT, Event.KIND_WORLD,
                        Event.KIND_ERROR),
                HP_PACKETS,
                Set.of(),
                true);
    }

    /** Every event, for when the narrow view is not enough. */
    public static LogFilter everythingFilter() {
        return new LogFilter("everything", false,
                Set.of(Event.KIND_SESSION, Event.KIND_PACKET, Event.KIND_INJECT, Event.KIND_NEXUS,
                        Event.KIND_HEALTH, Event.KIND_WORLD, Event.KIND_NOTE, Event.KIND_ERROR),
                Set.of(),
                Set.of(),
                true);
    }

    /** Only the relay's own decisions and writes - the "why did it not nexus" view. */
    public static LogFilter nexusOnlyFilter() {
        return new LogFilter("nexus only", false,
                Set.of(Event.KIND_NEXUS, Event.KIND_INJECT, Event.KIND_WORLD, Event.KIND_ERROR),
                Set.of(),
                Set.of(),
                true);
    }

    /** The starting set: the HP view on, the wider ones available but off. */
    public static List<LogFilter> defaults() {
        List<LogFilter> filters = new ArrayList<>();
        filters.add(defaultFilter());
        filters.add(nexusOnlyFilter());
        filters.add(everythingFilter());
        return filters;
    }

    public boolean matches(Event event) {
        if (kinds != null && kinds.contains(event.kind)) {
            return true;
        }
        if (event.pkt != null && packets != null && packets.contains(event.pkt)) {
            return true;
        }
        return event.session != null && sessions != null && sessions.contains(event.session);
    }

    /**
     * A rule built from loose text, so the dashboard can post
     * {@code {"name":"hp","packets":"HealthUpdate, Escape*","kinds":"health"}}.
     *
     * <p>Packet names accept a trailing {@code *} as a prefix match, which is how a whole packet
     * family is selected without listing it ({@code MarketBoard*}).
     */
    public static LogFilter parse(String name, boolean enabled, String kinds, String packets, String sessions) {
        return new LogFilter(name == null || name.isBlank() ? "custom" : name.trim(),
                enabled,
                split(kinds),
                split(packets),
                split(sessions),
                false);
    }

    private static Set<String> split(String text) {
        Set<String> out = new LinkedHashSet<>();
        if (text == null) {
            return out;
        }
        for (String part : text.split("[,\\s]+")) {
            String value = part.trim();
            if (!value.isEmpty()) {
                out.add(value);
            }
        }
        return out;
    }

    /** Packet matching with {@code *} support, used when a filter's packet list has wildcards. */
    public static boolean packetMatches(Set<String> patterns, String packet) {
        if (patterns == null || packet == null) {
            return false;
        }
        for (String pattern : patterns) {
            if (wildcardMatch(pattern, packet)) {
                return true;
            }
        }
        return false;
    }

    /**
     * A case-insensitive prefix match, where a trailing {@code *} means "and anything after".
     *
     * <p>Only a trailing {@code *} is honoured. That is enough for how filters are actually written
     * ({@code MarketBoard*}, {@code Escape*}) and avoids the trap of a pattern language nobody asked
     * for, where {@code Esc*pe} silently matches nothing and looks like a broken filter.
     */
    public static boolean wildcardMatch(String pattern, String value) {
        if (pattern == null || value == null) {
            return false;
        }
        String needle = pattern.trim().toLowerCase(Locale.ROOT);
        String haystack = value.toLowerCase(Locale.ROOT);
        if (needle.endsWith("*")) {
            return haystack.startsWith(needle.substring(0, needle.length() - 1));
        }
        return needle.equals(haystack);
    }

    /** Kind matching, with the same trailing-{@code *} support. */
    public boolean matchesLoose(Event event) {
        for (String kind : kinds == null ? Set.<String>of() : kinds) {
            if (wildcardMatch(kind, event.kind)) {
                return true;
            }
        }
        if (packetMatches(packets, event.pkt)) {
            return true;
        }
        for (String session : sessions == null ? Set.<String>of() : sessions) {
            if (wildcardMatch(session, event.session)) {
                return true;
            }
        }
        return false;
    }
}
