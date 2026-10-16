package networking.util;

import java.util.ArrayList;
import java.util.Collection;
import java.util.List;
import java.util.Locale;
import java.util.Map;

/**
 * Minimal JSON writer, the counterpart of {@code Relay.JsonParser}.
 *
 * <p>It exists for one reason: the relay's observability output (structured event log and the
 * dashboard API) has to be machine-readable by ordinary tools - {@code jq}, Python, a browser - and
 * a hand-rolled {@code String.format} would produce invalid JSON the first time a player name or a
 * chat line contained a quote or a control character. Every string goes through
 * {@link #quote(String)}, which is where that correctness lives.
 *
 * <p>Objects are written as ordered key/value pairs so the log is diffable between runs.
 */
public final class Json {

    private Json() {
    }

    /** One key/value pair of a JSON object, in insertion order. */
    public record Field(String key, Object value) {
    }

    /** An already-serialized JSON fragment, for the rare case where one must be embedded verbatim. */
    public record Raw(String json) {
    }

    /** Wraps a serialized fragment so {@link #value(Object)} emits it as JSON rather than a string. */
    public static Raw raw(String json) {
        return new Raw(json == null ? "null" : json);
    }

    public static Field of(String key, Object value) {
        return new Field(key, value);
    }

    public static List<Field> fields(Field... pairs) {
        return new ArrayList<>(List.of(pairs));
    }

    /**
     * Serializes a value the way {@link #object} and {@link #array} expect it.
     *
     * <p>A {@code CharSequence} is quoted as a JSON <em>string</em>, which is what makes
     * {@code Json.of("note", "a \"quoted\" note")} safe. That also means a nested value must be a
     * {@code Map} or {@code Collection}, never a pre-serialized JSON string: passing one would embed
     * it as an escaped string and the consumer would receive text where it expected an object. Use
     * {@link #raw(String)} to embed an already-serialized fragment deliberately.
     */
    @SuppressWarnings("unchecked")
    public static String value(Object value) {
        if (value == null) {
            return "null";
        }
        if (value instanceof Raw raw) {
            return raw.json();
        }
        if (value instanceof String s) {
            return quote(s);
        }
        if (value instanceof Boolean b) {
            return b ? "true" : "false";
        }
        if (value instanceof Float f) {
            // A NaN or an infinity is not valid JSON. Emit null rather than a token no parser accepts.
            return Float.isFinite(f) ? trim(f.doubleValue()) : "null";
        }
        if (value instanceof Double d) {
            return Double.isFinite(d) ? trim(d) : "null";
        }
        if (value instanceof Number n) {
            return n.toString();
        }
        if (value instanceof Map<?, ?> map) {
            List<Field> out = new ArrayList<>();
            for (Map.Entry<?, ?> e : map.entrySet()) {
                out.add(new Field(String.valueOf(e.getKey()), e.getValue()));
            }
            return object(out);
        }
        if (value instanceof Collection<?> collection) {
            return array(collection);
        }
        if (value instanceof Object[] arrayValue) {
            return array(List.of(arrayValue));
        }
        if (value instanceof List<?> list) {
            return array(list);
        }
        return quote(String.valueOf(value));
    }

    /** {@code {"k":v,...}} in the given order. A null value omits the key; see {@link #objectAlways}. */
    public static String object(List<Field> fields) {
        return object(fields, true);
    }

    /**
     * {@code {"k":v,...}} in the given order, <strong>keeping</strong> keys whose value is null.
     *
     * <p>The two behaviours exist for two different consumers, and mixing them up causes real bugs in
     * both directions:
     *
     * <ul>
     *   <li>A <em>log event</em> wants omission. Most packets have a {@code pktId} and no {@code note},
     *       and writing {@code "note":null} on every one of thousands of lines is noise that makes the
     *       file harder to read and bigger for no information.</li>
     *   <li>An <em>API response</em> wants presence. A dashboard reads {@code state.health} and
     *       {@code state.primary} on every poll; if the relay drops those keys when there is no
     *       session, the page sees {@code undefined} instead of {@code null} and its "no session yet"
     *       branch never runs. Silently changing an object's <em>shape</em> is worse than a null.</li>
     * </ul>
     */
    public static String objectAlways(List<Field> fields) {
        return object(fields, false);
    }

    private static String object(List<Field> fields, boolean omitNullValues) {
        StringBuilder sb = new StringBuilder(64);
        sb.append('{');
        boolean first = true;
        for (Field field : fields) {
            if (field == null || (omitNullValues && field.value() == null)) {
                continue;
            }
            if (!first) {
                sb.append(',');
            }
            first = false;
            sb.append(quote(field.key())).append(':').append(value(field.value()));
        }
        return sb.append('}').toString();
    }

    public static String object(Field... pairs) {
        return object(List.of(pairs));
    }

    public static String array(Collection<?> values) {
        StringBuilder sb = new StringBuilder(64);
        sb.append('[');
        boolean first = true;
        for (Object value : values) {
            if (!first) {
                sb.append(',');
            }
            first = false;
            sb.append(value(value));
        }
        return sb.append(']').toString();
    }

    /**
     * A JSON string literal.
     *
     * <p>Escapes the two characters that would end the literal, the C0 controls (as four-hex-digit
     * escapes where JSON has no short form), and DEL. Everything above U+001F is emitted as-is and the
     * result is UTF-8 on the wire, which is what the log and the browser both expect.
     */
    public static String quote(String raw) {
        StringBuilder sb = new StringBuilder(raw.length() + 8);
        sb.append('"');
        for (int i = 0; i < raw.length(); i++) {
            char c = raw.charAt(i);
            switch (c) {
                case '"' -> sb.append("\\\"");
                case '\\' -> sb.append("\\\\");
                case '\n' -> sb.append("\\n");
                case '\r' -> sb.append("\\r");
                case '\t' -> sb.append("\\t");
                case '\b' -> sb.append("\\b");
                case '\f' -> sb.append("\\f");
                default -> {
                    if (c < 0x20 || c == 0x7F) {
                        sb.append(String.format(Locale.ROOT, "\\u%04x", (int) c));
                    } else {
                        sb.append(c);
                    }
                }
            }
        }
        return sb.append('"').toString();
    }

    /**
     * A number without the {@code .0} that {@code Double.toString} adds, so an integer-valued field
     * looks like an integer in the log.
     */
    private static String trim(double d) {
        if (d == Math.rint(d) && Math.abs(d) < 1e15) {
            return Long.toString((long) d);
        }
        return Double.toString(d);
    }
}
