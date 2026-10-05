package networking;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * A small JSON reader for the launcher's own use.
 *
 * <p>The relay keeps a private reader for its route table because the whole project is built with no
 * dependencies, and that is deliberate: the release artifact is one jar that must run on any JDK
 * without a classpath, so nothing may be added to {@code pom.xml}. This reader exists separately
 * because it has to answer questions about two documents the relay never sees - the GitHub release
 * list and the game's server list - and it is written to be forgiving about shapes, since both are
 * external and may grow fields.
 */
final class JsonText {

    private JsonText() {
    }

    /** A parsed document; {@code root} is one of the record types below. */
    record Document(Value root) {
        Document {
            if (root == null) {
                throw new IllegalArgumentException("the document is empty");
            }
        }

        static Document of(String text) {
            var parser = new Parser(text);
            parser.skipWhitespace();
            Value value = parser.value();
            return new Document(value);
        }
    }

    sealed interface Value permits Str, Num, Bool, Null, Arr, Obj {
    }

    record Str(String value) implements Value {
    }

    record Num(double value) implements Value {
    }

    record Bool(boolean value) implements Value {
    }

    record Null() implements Value {
        static final Null INSTANCE = new Null();
    }

    record Arr(List<Value> values) implements Value {
        Arr {
            values = List.copyOf(values);
        }
    }

    record Obj(Map<String, Value> values) implements Value {
        String string(String key, String fallback) {
            return values.get(key) instanceof Str s ? s.value() : fallback;
        }

        double number(String key, double fallback) {
            return values.get(key) instanceof Num n ? n.value() : fallback;
        }

        long longValue(String key, long fallback) {
            return values.get(key) instanceof Num n ? (long) n.value() : fallback;
        }

        boolean bool(String key, boolean fallback) {
            return values.get(key) instanceof Bool b ? b.value() : fallback;
        }

        List<Value> array(String key) {
            return values.get(key) instanceof Arr a ? a.values() : List.of();
        }
    }

    static Document parse(String text) {
        return Document.of(text);
    }

    /** Parses the body of {@code drelay.properties}, which is a Java properties file, not JSON. */
    static Map<String, String> parseProperties(String text) {
        var values = new LinkedHashMap<String, String>();
        for (String raw : text.split("\\R")) {
            String line = raw.trim();
            if (line.isEmpty() || line.startsWith("#") || line.startsWith("!")) {
                continue;
            }
            int separator = line.indexOf('=');
            if (separator > 0) {
                values.put(line.substring(0, separator).trim(), line.substring(separator + 1).trim());
            }
        }
        return values;
    }

    /** Recursive-descent reader for JSON. Objects keep their key order, which is what the config needs. */
    private static final class Parser {
        private final String source;
        private int position;

        Parser(String source) {
            this.source = source;
        }

        void skipWhitespace() {
            while (position < source.length() && Character.isWhitespace(source.charAt(position))) {
                position++;
            }
        }

        Value value() {
            skipWhitespace();
            if (position >= source.length()) {
                throw new IllegalArgumentException("unexpected end of JSON");
            }
            return switch (source.charAt(position)) {
                case '{' -> object();
                case '[' -> array();
                case '"' -> new Str(string());
                case 't' -> {
                    literal("true");
                    yield new Bool(true);
                }
                case 'f' -> {
                    literal("false");
                    yield new Bool(false);
                }
                case 'n' -> {
                    literal("null");
                    yield Null.INSTANCE;
                }
                default -> number();
            };
        }

        private Value object() {
            expect('{');
            var values = new LinkedHashMap<String, Value>();
            skipWhitespace();
            if (peek() == '}') {
                position++;
                return new Obj(values);
            }
            while (true) {
                skipWhitespace();
                String key = string();
                skipWhitespace();
                expect(':');
                values.put(key, value());
                skipWhitespace();
                char separator = source.charAt(position++);
                if (separator == '}') {
                    return new Obj(values);
                }
                if (separator != ',') {
                    throw new IllegalArgumentException("expected , or } at " + position);
                }
            }
        }

        private Value array() {
            expect('[');
            var values = new ArrayList<Value>();
            skipWhitespace();
            if (peek() == ']') {
                position++;
                return new Arr(values);
            }
            while (true) {
                values.add(value());
                skipWhitespace();
                char separator = source.charAt(position++);
                if (separator == ']') {
                    return new Arr(values);
                }
                if (separator != ',') {
                    throw new IllegalArgumentException("expected , or ] at " + position);
                }
            }
        }

        private String string() {
            expect('"');
            var text = new StringBuilder();
            while (true) {
                char c = source.charAt(position++);
                if (c == '"') {
                    return text.toString();
                }
                if (c == '\\') {
                    char escape = source.charAt(position++);
                    switch (escape) {
                        case 'n' -> text.append('\n');
                        case 't' -> text.append('\t');
                        case 'r' -> text.append('\r');
                        case 'b' -> text.append('\b');
                        case 'f' -> text.append('\f');
                        case 'u' -> {
                            text.append((char) Integer.parseInt(source.substring(position, position + 4), 16));
                            position += 4;
                        }
                        default -> text.append(escape);
                    }
                } else {
                    text.append(c);
                }
            }
        }

        private Value number() {
            int start = position;
            while (position < source.length() && "-+.eE0123456789".indexOf(source.charAt(position)) >= 0) {
                position++;
            }
            return new Num(Double.parseDouble(source.substring(start, position)));
        }

        private char peek() {
            return source.charAt(position);
        }

        private void literal(String word) {
            if (!source.startsWith(word, position)) {
                throw new IllegalArgumentException("expected " + word + " at " + position);
            }
            position += word.length();
        }

        private void expect(char c) {
            skipWhitespace();
            if (source.charAt(position) != c) {
                throw new IllegalArgumentException("expected " + c + " at " + position);
            }
            position++;
        }
    }
}
