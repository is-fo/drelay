package networking;

import java.io.IOException;
import java.net.InetAddress;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Duration;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * Keeps the route table's remote addresses current.
 *
 * <p>The game server is addressed by a bare IP taken from the game's own server list, and the
 * servers rotate. A pinned address therefore goes stale between releases, and a stale address does
 * not fail loudly: the claim is placed on an address nothing uses any more, the client talks to the
 * real server directly, and the proxy looks idle. Rather than asking a user to edit JSON, the
 * launcher refreshes the two addresses it can learn before every run and writes the result where the
 * relay will read it.
 *
 * <p>Each route in the template names how it is refreshed:
 *
 * <ul>
 *   <li>{@code "refresh": "serverlist"} - take {@code servers[0].host} from the game's public API;</li>
 *   <li>{@code "refresh": "queue.playdarzas.com"} - resolve that host name;</li>
 *   <li>no {@code refresh} - the address is deliberate and is never touched.</li>
 * </ul>
 *
 * <p>Nothing here is fatal. A refresh that fails leaves the previous address in place and says so,
 * because an offline machine with a working pinned address should still be able to start.
 */
final class Routes {

    private static final String SERVER_LIST = "https://playdarzas.com/api/v1/serverlist";
    private static final String SERVER_LIST_KEY = "serverlist";

    /** One JSON value: a string, a number, a boolean or a nested array/object. */
    private sealed interface Json {
    }

    private record JStr(String value) implements Json {
    }

    private record JNum(double value) implements Json {
    }

    private record JBool(boolean value) implements Json {
    }

    private record JArr(List<Json> values) implements Json {
    }

    private record JObj(Map<String, Json> values) implements Json {
    }

    private Routes() {
    }

    /** A route's mutable fields, in the template's order. Comments are carried separately. */
    private static final class Route {
        String name = "";
        int listenPort;
        String remoteHost;
        int remotePort;
        String refresh;
        boolean optional;
    }

    private static final class Document {
        List<String> comments = new ArrayList<>();
        List<Route> routes = new ArrayList<>();
        String listenHost;
        Integer upstreamBindPort;
        String upstreamHost;
        String logDirectory;
        Integer ringCapacity;
        Long logMaxBytes;
        Integer webPort;
        String webHost;
        /** Passed through verbatim: the relay owns these keys and the dashboard edits them live. */
        Json autoNexus;
    }

    /** What a refresh changed, so the launcher can report it in one line. */
    record Refresh(List<String> changes, List<String> failures) {
    }

    /**
     * Refreshes the addresses in {@code template} and writes the relay's working copy.
     *
     * @return the changes made and the refreshes that failed
     */
    static Refresh write(Path template, Path target, String upstreamHost) throws IOException {
        Document document = parse(Files.readString(template, StandardCharsets.UTF_8));
        document.upstreamHost = upstreamHost;

        var changes = new ArrayList<String>();
        var failures = new ArrayList<String>();
        String serverHost = null;
        for (Route route : document.routes) {
            if (route.refresh == null || route.refresh.isBlank()) {
                continue;
            }
            String resolved;
            if (route.refresh.equalsIgnoreCase(SERVER_LIST_KEY)) {
                if (serverHost == null) {
                    try {
                        serverHost = serverListHost();
                    } catch (Exception e) {
                        serverHost = "";
                        failures.add("the game's server list could not be read (" + e.getMessage() + ")");
                    }
                }
                resolved = serverHost;
            } else {
                try {
                    resolved = InetAddress.getByName(route.refresh).getHostAddress();
                } catch (Exception e) {
                    resolved = "";
                    failures.add(route.refresh + " did not resolve (" + e.getMessage() + ")");
                }
            }
            if (resolved == null || resolved.isBlank() || resolved.equals(route.remoteHost)) {
                continue;
            }
            changes.add("%s %s -> %s".formatted(route.name, route.remoteHost, resolved));
            route.remoteHost = resolved;
        }

        Files.createDirectories(target.getParent() == null ? Path.of(".") : target.getParent());
        Files.writeString(target, render(document), StandardCharsets.UTF_8);
        return new Refresh(changes, failures);
    }

    /** The current game server host from the game's own public API. */
    private static String serverListHost() throws IOException, InterruptedException {
        try (var client = HttpClient.newBuilder().connectTimeout(Duration.ofSeconds(10)).build()) {
            var request = HttpRequest.newBuilder(URI.create(SERVER_LIST))
                    .timeout(Duration.ofSeconds(15))
                    .header("User-Agent", "drelay/" + Resources.VERSION)
                    .GET()
                    .build();
            HttpResponse<String> response = client.send(request, HttpResponse.BodyHandlers.ofString());
            if (response.statusCode() != 200) {
                throw new IOException("HTTP " + response.statusCode() + " from " + SERVER_LIST);
            }
            Json root = parseJson(response.body());
            if (root instanceof JObj object && object.values().get("servers") instanceof JArr servers
                    && !servers.values().isEmpty() && servers.values().get(0) instanceof JObj server
                    && server.values().get("host") instanceof JStr host) {
                return host.value();
            }
            throw new IOException("the server list contained no servers[0].host");
        }
    }

    // ---------------------------------------------------------------------------------------
    // JSON, kept deliberately small: the route table is written by this project and the API
    // response it reads has one field of interest.
    // ---------------------------------------------------------------------------------------

    private static Document parse(String text) {
        Json root = parseJson(text);
        if (!(root instanceof JObj object)) {
            throw new IllegalArgumentException("the route table must be a JSON object");
        }
        var document = new Document();
        Map<String, Json> values = object.values();
        if (values.get("_comment") instanceof JArr comments) {
            for (Json comment : comments.values()) {
                if (comment instanceof JStr line) {
                    document.comments.add(line.value());
                }
            }
        }
        document.listenHost = string(values.get("listenHost"));
        document.upstreamBindPort = integer(values.get("upstreamBindPort"));
        document.upstreamHost = string(values.get("upstreamHost"));
        document.logDirectory = string(values.get("logDirectory"));
        document.ringCapacity = integer(values.get("ringCapacity"));
        document.logMaxBytes = longValue(values.get("logMaxBytes"));
        if (values.get("web") instanceof JObj web) {
            document.webHost = string(web.values().get("host"));
            document.webPort = integer(web.values().get("port"));
        }
        // autoNexus is passed through untouched: the relay owns those keys and the launcher has no
        // business rewriting settings the dashboard can change live.
        document.autoNexus = values.get("autoNexus");
        if (values.get("routes") instanceof JArr routes) {
            for (Json entry : routes.values()) {
                if (!(entry instanceof JObj routeObject)) {
                    continue;
                }
                var route = new Route();
                Map<String, Json> fields = routeObject.values();
                route.name = orEmpty(string(fields.get("name")));
                route.listenPort = orZero(integer(fields.get("listenPort")));
                route.remoteHost = string(fields.get("remoteHost"));
                route.remotePort = orZero(integer(fields.get("remotePort")));
                route.refresh = string(fields.get("refresh"));
                route.optional = fields.get("optional") instanceof JBool b && b.value();
                document.routes.add(route);
            }
        }
        return document;
    }

    private static String string(Json value) {
        return value instanceof JStr s ? s.value() : null;
    }

    private static Integer integer(Json value) {
        return value instanceof JNum n ? (int) n.value() : null;
    }

    private static Long longValue(Json value) {
        return value instanceof JNum n ? (long) n.value() : null;
    }

    private static String orEmpty(String value) {
        return value == null ? "" : value;
    }

    private static String orDefault(String value, String fallback) {
        return value == null || value.isBlank() ? fallback : value;
    }

    private static int defaultInt(Integer value, int fallback) {
        return value == null ? fallback : value;
    }

    private static long defaultLong(Long value, long fallback) {
        return value == null ? fallback : value;
    }

    private static int orZero(Integer value) {
        return value == null ? 0 : value;
    }

    private static Json parseJson(String text) {
        var parser = new Parser(text);
        parser.skipWhitespace();
        Json value = parser.value();
        parser.skipWhitespace();
        return value;
    }

    /** Recursive-descent reader for the subset of JSON this project writes. */
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

        Json value() {
            skipWhitespace();
            if (position >= source.length()) {
                throw new IllegalArgumentException("unexpected end of JSON");
            }
            return switch (source.charAt(position)) {
                case '{' -> object();
                case '[' -> array();
                case '"' -> new JStr(string());
                case 't' -> {
                    literal("true");
                    yield new JBool(true);
                }
                case 'f' -> {
                    literal("false");
                    yield new JBool(false);
                }
                case 'n' -> {
                    literal("null");
                    yield JNull.INSTANCE;
                }
                default -> number();
            };
        }

        private Json object() {
            expect('{');
            var values = new LinkedHashMap<String, Json>();
            skipWhitespace();
            if (peek() == '}') {
                position++;
                return new JObj(values);
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
                    return new JObj(values);
                }
                if (separator != ',') {
                    throw new IllegalArgumentException("expected , or } at " + position);
                }
            }
        }

        private Json array() {
            expect('[');
            var values = new ArrayList<Json>();
            skipWhitespace();
            if (peek() == ']') {
                position++;
                return new JArr(values);
            }
            while (true) {
                values.add(value());
                skipWhitespace();
                char separator = source.charAt(position++);
                if (separator == ']') {
                    return new JArr(values);
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

        private Json number() {
            int start = position;
            while (position < source.length() && "-+.eE0123456789".indexOf(source.charAt(position)) >= 0) {
                position++;
            }
            return new JNum(Double.parseDouble(source.substring(start, position)));
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

    private record JNull() implements Json {
        static final JNull INSTANCE = new JNull();
    }

    // ---------------------------------------------------------------------------------------
    // Rendering. The output is a normal JSON document with two-space indentation, plus one key
    // the relay ignores: _generated, so a user reading the file can see which run wrote it.
    // ---------------------------------------------------------------------------------------

    private static String render(Document document) {
        var out = new StringBuilder();
        out.append("{\n");
        if (!document.comments.isEmpty()) {
            out.append("  \"_comment\": [\n");
            for (int i = 0; i < document.comments.size(); i++) {
                out.append("    ").append(jsonString(document.comments.get(i)));
                out.append(i + 1 < document.comments.size() ? ",\n" : "\n");
            }
            out.append("  ],\n");
        }
        out.append("  \"_generated\": ")
                .append(jsonString("written by drelay " + Resources.VERSION
                        + " at " + java.time.LocalDateTime.now().withNano(0)))
                .append(",\n");
        out.append("  \"listenHost\": ").append(jsonString(document.listenHost)).append(",\n");
        out.append("  \"upstreamHost\": ").append(jsonString(document.upstreamHost)).append(",\n");
        out.append("  \"upstreamBindPort\": ").append(orZero(document.upstreamBindPort)).append(",\n");
        // Missing keys are written with the relay's own defaults rather than as zeros or empty
        // strings: a zero ring capacity or a blank listen host would be a real behaviour change,
        // while the relay's default is what a template that omits the key is asking for.
        out.append("  \"logDirectory\": ").append(jsonString(orDefault(document.logDirectory, "work/logs")))
                .append(",\n");
        out.append("  \"ringCapacity\": ").append(defaultInt(document.ringCapacity, 20_000)).append(",\n");
        out.append("  \"logMaxBytes\": ").append(defaultLong(document.logMaxBytes, 32L << 20)).append(",\n");
        out.append("  \"web\": {\n");
        out.append("    \"host\": ").append(jsonString(orDefault(document.webHost, "127.0.0.1"))).append(",\n");
        out.append("    \"port\": ").append(defaultInt(document.webPort, 8765)).append("\n");
        out.append("  },\n");
        out.append("  \"autoNexus\": ").append(renderJson(document.autoNexus, 2)).append(",\n");
        out.append("  \"routes\": [\n");
        for (int i = 0; i < document.routes.size(); i++) {
            Route route = document.routes.get(i);
            out.append("    {\n");
            out.append("      \"name\": ").append(jsonString(route.name)).append(",\n");
            out.append("      \"listenPort\": ").append(route.listenPort).append(",\n");
            out.append("      \"remoteHost\": ").append(jsonString(route.remoteHost)).append(",\n");
            out.append("      \"remotePort\": ").append(route.remotePort);
            if (route.refresh != null && !route.refresh.isBlank()) {
                out.append(",\n      \"refresh\": ").append(jsonString(route.refresh));
            }
            if (route.optional) {
                out.append(",\n      \"optional\": true");
            }
            out.append("\n    }").append(i + 1 < document.routes.size() ? ",\n" : "\n");
        }
        out.append("  ]\n");
        out.append("}\n");
        return out.toString();
    }

    private static String renderJson(Json value, int indent) {
        if (value == null || value instanceof JNull) {
            return "{}";
        }
        if (value instanceof JStr s) {
            return jsonString(s.value());
        }
        if (value instanceof JNum n) {
            return n.value() == Math.rint(n.value()) ? String.valueOf((long) n.value()) : String.valueOf(n.value());
        }
        if (value instanceof JBool b) {
            return String.valueOf(b.value());
        }
        if (value instanceof JArr array) {
            var out = new StringBuilder("[");
            for (int i = 0; i < array.values().size(); i++) {
                out.append(i == 0 ? "" : ", ").append(renderJson(array.values().get(i), indent));
            }
            return out.append(']').toString();
        }
        var object = (JObj) value;
        var out = new StringBuilder("{\n");
        int i = 0;
        for (Map.Entry<String, Json> entry : object.values().entrySet()) {
            out.append(" ".repeat(indent + 2)).append(jsonString(entry.getKey())).append(": ")
                    .append(renderJson(entry.getValue(), indent + 2));
            out.append(++i < object.values().size() ? ",\n" : "\n");
        }
        return out.append(" ".repeat(indent)).append('}').toString();
    }

    private static String jsonString(String value) {
        if (value == null) {
            return "\"\"";
        }
        var out = new StringBuilder("\"");
        for (char c : value.toCharArray()) {
            switch (c) {
                case '"' -> out.append("\\\"");
                case '\\' -> out.append("\\\\");
                case '\n' -> out.append("\\n");
                case '\r' -> out.append("\\r");
                case '\t' -> out.append("\\t");
                default -> {
                    if (c < 0x20) {
                        out.append("\\u%04x".formatted((int) c));
                    } else {
                        out.append(c);
                    }
                }
            }
        }
        return out.append('"').toString();
    }
}
