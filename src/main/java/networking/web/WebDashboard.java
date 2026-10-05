package networking.web;

import com.sun.net.httpserver.HttpExchange;
import com.sun.net.httpserver.HttpServer;
import networking.AutoNexus;
import networking.Session;
import networking.SessionRegistry;
import networking.log.Event;
import networking.log.EventLog;
import networking.log.LogClock;
import networking.log.LogFilter;
import networking.util.Fields;
import networking.util.Json;
import networking.util.Prefs;

import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Collection;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.Executors;

/**
 * The local observability console: a read-mostly HTTP API plus one self-contained page.
 *
 * <h2>Why this exists, and why on localhost</h2>
 *
 * <p>The relay's consumers are a game client and a server, and neither can be asked what happened.
 * During a session the useful questions are live ones - what is my HP right now, why did (or didn't)
 * the escape fire, did the server accept it, is injection even armed - and the answers have to be
 * visible while playing, on a second monitor or a phone browser. After the session the answers come
 * from the JSONL log instead ({@code work/logs/}), which is why this server never has to be the
 * system of record: it holds a bounded ring and hands it out.
 *
 * <p>It binds to loopback only and starts without a flag unless {@code drelay.web.port} is 0. The page
 * is a single HTML string with no build step, no CDN and no framework, so it cannot rot and works with
 * no network at all.
 *
 * <h2>The endpoints</h2>
 *
 * <table>
 *   <tr><td>{@code GET /}</td><td>the page</td></tr>
 *   <tr><td>{@code GET /api/state}</td><td>sessions, health, nexus state, counters, filters</td></tr>
 *   <tr><td>{@code GET /api/events?after=N&limit=M}</td><td>events newer than sequence N, filtered, newest last</td></tr>
 *   <tr><td>{@code GET /api/events?raw=1}</td><td>unfiltered, for the "everything" toggle</td></tr>
 *   <tr><td>{@code POST /api/nexus}</td><td>sparse overrides, e.g. {@code {"enabled":true,"threshold_percent":40}}</td></tr>
 *   <tr><td>{@code POST /api/filters}</td><td>replaces the filter list</td></tr>
 *   <tr><td>{@code GET /api/packets}</td><td>packet ids and names, for building a filter</td></tr>
 * </table>
 */
public final class WebDashboard {

    private final SessionRegistry registry;
    private final EventLog log;
    private final AutoNexus nexus;
    private final int requestedPort;
    private final String host;

    private volatile HttpServer server;
    private volatile int boundPort = -1;
    private volatile String startError;

    public WebDashboard(SessionRegistry registry, EventLog log, AutoNexus nexus, String host, int port) {
        this.registry = registry;
        this.log = log;
        this.nexus = nexus;
        this.host = host;
        this.requestedPort = port;
    }

    /** Starts the server; never throws, because observability must not be able to break a session. */
    public void start() {
        if (requestedPort <= 0) {
            IO.println("[web] dashboard disabled (drelay.web.port=0)");
            return;
        }
        // A busy port is the most likely failure, and the least useful one: another relay, or a
        // previous run that has not finished exiting, already holds it. Walk forward a few ports
        // instead of giving up, and say which one was taken - the URL is the whole point of the
        // dashboard, and "address in use" leaves the operator with no URL at all.
        Exception last = null;
        for (int attempt = 0; attempt < PORT_ATTEMPTS; attempt++) {
            int port = attemptedPort(attempt);
            try {
                HttpServer httpServer = HttpServer.create(new InetSocketAddress(host, port), 64);
                httpServer.setExecutor(Executors.newVirtualThreadPerTaskExecutor());
                httpServer.createContext("/", this::route);
                httpServer.start();
                this.server = httpServer;
                this.boundPort = httpServer.getAddress().getPort();
                if (attempt > 0) {
                    IO.println("[web] port %d was busy; the dashboard took %d instead"
                            .formatted(requestedPort, boundPort));
                }
                IO.println("[web] observability dashboard: http://%s:%d/".formatted(displayHost(), boundPort));
                IO.println("[web]   api: /api/state  /api/events  /api/nexus  /api/filters  /api/packets");
                recordHint(boundPort);
                return;
            } catch (Exception e) {
                last = e;
            }
        }
        startError = String.valueOf(last);
        System.err.println("[web] could not start the dashboard on %s:%d - %s".formatted(host, requestedPort, last));
        System.err.println("[web] the relay is unaffected; set -Ddrelay.web.port=<free port> to try another one");
    }

    /**
     * The port to try on the {@code attempt}-th go: a negative configured port is a request for any
     * free port, and otherwise sequential ports are tried from the configured one.
     */
    private int attemptedPort(int attempt) {
        if (requestedPort < 0) {
            return 0;
        }
        return requestedPort + attempt;
    }

    private static final int PORT_ATTEMPTS = 5;

    private String displayHost() {
        return "0.0.0.0".equals(host) ? "127.0.0.1" : host;
    }

    public int port() {
        return boundPort;
    }

    public String startError() {
        return startError;
    }

    public void stop() {
        HttpServer current = server;
        if (current != null) {
            current.stop(0);
        }
        try {
            Files.deleteIfExists(hintPath());
        } catch (IOException ignored) {
            // A stale hint only costs the launcher one mis-timed browser tab.
        }
    }

    /**
     * Records the address actually bound, so the launcher can open the right page.
     *
     * <p>The port is not knowable before the server starts: a busy port makes it walk forward, and a
     * configured port of {@code -1} asks the OS for any free one. The launcher starts the relay as a
     * child process with an inherited console, so it cannot read this line from a pipe - it needs the
     * address written down somewhere, and {@code work/} is the directory both processes already
     * agree on. A failure to write is not worth reporting: the URL is still printed, and the
     * dashboard is unaffected.
     */
    private void recordHint(int port) {
        try {
            Path path = hintPath();
            if (path.getParent() != null) {
                Files.createDirectories(path.getParent());
            }
            Files.writeString(path, "http://%s:%d/\n".formatted(displayHost(), port), StandardCharsets.UTF_8);
        } catch (IOException ignored) {
            // best effort
        }
    }

    private static Path hintPath() {
        return Path.of(Prefs.string("drelay.log.dir", "DRELAY_LOG_DIR", "work/logs"))
                .resolveSibling("dashboard.txt");
    }

    // --- routing ------------------------------------------------------------------------------

    private void route(HttpExchange exchange) throws IOException {
        String path = exchange.getRequestURI().getPath();
        try {
            if ("OPTIONS".equals(exchange.getRequestMethod())) {
                respond(exchange, 204, "text/plain", "");
                return;
            }
            switch (path) {
                case "/", "/index.html" -> respond(exchange, 200, "text/html; charset=utf-8", DashboardPage.HTML);
                case "/api/state" -> respondJson(exchange, 200, stateJson());
                case "/api/events" -> respondJson(exchange, 200, eventsJson(exchange));
                case "/api/nexus" -> nexusEndpoint(exchange);
                case "/api/filters" -> filtersEndpoint(exchange);
                case "/api/packets" -> respondJson(exchange, 200, packetsJson());
                case "/api/log" -> respondJson(exchange, 200, logJson(exchange));
                case "/healthz" -> respond(exchange, 200, "text/plain", "ok");
                default -> respondJson(exchange, 404, Json.object(Json.of("error", "no such endpoint: " + path)));
            }
        } catch (Exception e) {
            respondJson(exchange, 500, Json.object(Json.of("error", String.valueOf(e))));
        } finally {
            exchange.close();
        }
    }

    /**
     * A tail of the JSONL event log.
     *
     * <p>The ring is what the live view reads, but a cursor that has fallen behind it has a hole in
     * it. This endpoint is the way out: the file is the complete record, so "what happened before the
     * page was opened" is answerable without restarting anything.
     */
    private String logJson(HttpExchange exchange) throws IOException {
        Map<String, String> query = query(exchange);
        int limit = (int) Math.max(1, Math.min(2000, parseLong(query.get("lines"), 200)));
        Path file = log.directory() == null ? null
                : log.directory().resolve("nexus-" + log.runId() + ".jsonl");
        List<Map<String, Object>> lines = new ArrayList<>();
        if (file != null && java.nio.file.Files.exists(file)) {
            List<String> all = java.nio.file.Files.readAllLines(file, StandardCharsets.UTF_8);
            for (String line : all.subList(Math.max(0, all.size() - limit), all.size())) {
                if (!line.isBlank()) {
                    lines.add(Fields.of().add("raw", line));
                }
            }
        }
        return Json.object(
                Json.of("file", file == null ? null : file.toAbsolutePath().toString()),
                Json.of("lines", lines),
                Json.of("count", lines.size()));
    }

    // --- the state snapshot -------------------------------------------------------------------

    private String stateJson() {
        List<Json.Field> fields = new ArrayList<>();
        long now = System.currentTimeMillis();
        fields.add(Json.of("now", LogClock.iso(now)));
        fields.add(Json.of("uptimeMs", LogClock.monoMillis()));
        fields.add(Json.of("project", "drelay relay"));

        Collection<Session> sessions = registry.sessions();
        List<Map<String, Object>> sessionViews = new ArrayList<>();
        Session freshest = null;
        for (Session session : sessions) {
            sessionViews.add(view(session));
            if (freshest == null || session.id > freshest.id) {
                freshest = session;
            }
        }
        fields.add(Json.of("sessions", sessionViews));
        fields.add(Json.of("activeSessions", sessions.size()));
        // The newest session drives the headline HP panel, because that is the one being played.
        fields.add(Json.of("primary", freshest == null ? null : freshest.tag()));
        fields.add(Json.of("health", freshest == null ? null : freshest.healthJson()));
        fields.add(Json.of("nexus", nexus.toMap()));
        fields.add(Json.of("counters", registry.counters()));
        fields.add(Json.of("filters", filtersMap()));
        fields.add(Json.of("log", logFields()));
        fields.add(Json.of("web", Fields.of()
                .add("port", boundPort)
                .add("host", displayHost())
                .add("error", startError)));
        // A response, not a log line: the page reads every one of these keys on every poll, so a null
        // must be present rather than omitted.
        return Json.objectAlways(fields);
    }

    private Map<String, Object> logFields() {
        return Fields.of()
                .add("directory", log.directory() == null ? null : log.directory().toAbsolutePath().toString())
                .add("run", log.runId())
                .add("eventsFile", log.directory() == null ? null
                        : log.directory().resolve("events-" + log.runId() + ".jsonl").toString())
                .add("nexusFile", log.directory() == null ? null
                        : log.directory().resolve("nexus-" + log.runId() + ".jsonl").toString())
                .add("hexLimit", EventLog.hexLimit())
                .add("lastSeq", log.lastSequence())
                .add("ringCapacity", log.ring().capacity())
                .add("dropped", log.dropped())
                .add("lastError", log.lastError());
    }

    /** A session as the dashboard's session table wants it; nested objects stay as maps. */
    private Map<String, Object> view(Session session) {
        Map<String, Object> out = new LinkedHashMap<>();
        out.put("tag", session.tag());
        out.put("route", session.routeName);
        out.put("queue", session.isQueue());
        out.put("client", session.clientAddress);
        out.put("destination", session.destination);
        out.put("phase", session.phase().name());
        out.put("injectionReady", session.injectionReady());
        out.put("closed", session.isClosed());
        out.put("uptimeMs", LogClock.monoMillis() - session.startedMonoMillis);
        out.put("worldName", session.worldName());
        out.put("safeArea", session.isSafeArea());
        out.put("casting", session.isCasting());
        out.put("health", session.healthJson());
        return out;
    }

    // --- events -------------------------------------------------------------------------------

    private String eventsJson(HttpExchange exchange) {
        Map<String, String> query = query(exchange);
        long after = parseLong(query.get("after"), 0);
        int limit = (int) Math.max(1, Math.min(5000, parseLong(query.get("limit"), 1000)));
        boolean raw = "1".equals(query.get("raw")) || "true".equalsIgnoreCase(query.get("raw"));

        SessionRegistry.FilterResult result = registry.eventsAfter(after, limit, raw);
        List<Json.Field> fields = new ArrayList<>();
        fields.add(Json.of("after", after));
        fields.add(Json.of("lastSeq", log.lastSequence()));
        fields.add(Json.of("oldestSeq", log.ring().oldestSequence()));
        fields.add(Json.of("gap", after > 0 && after < log.ring().oldestSequence()));
        fields.add(Json.of("scanned", result.scanned()));
        fields.add(Json.of("kept", result.events().size()));
        fields.add(Json.of("hidden", result.hidden()));
        List<Map<String, Object>> events = new ArrayList<>();
        for (Event event : result.events()) {
            events.add(eventAsMap(event));
        }
        fields.add(Json.of("events", events));
        return Json.object(fields);
    }

    /** The event as a plain map so the page can render any of it generically, without a schema copy. */
    private Map<String, Object> eventAsMap(Event event) {
        Map<String, Object> out = new LinkedHashMap<>();
        out.put("seq", event.seq);
        out.put("t", LogClock.iso(event.wallMillis));
        out.put("ms", event.monoMillis);
        out.put("kind", event.kind);
        out.put("sess", event.session);
        if (event.dir != null) {
            out.put("dir", event.dir);
        }
        if (event.pkt != null) {
            out.put("pkt", event.pkt);
        }
        if (event.pktId != null) {
            out.put("id", event.pktId);
        }
        if (event.len != null) {
            out.put("len", event.len);
        }
        if (event.hex != null) {
            out.put("hex", event.hex);
        }
        if (event.note != null) {
            out.put("note", event.note);
        }
        if (event.data != null && !event.data.isEmpty()) {
            out.put("data", event.data);
        }
        return out;
    }

    // --- mutation -----------------------------------------------------------------------------

    private void nexusEndpoint(HttpExchange exchange) throws IOException {
        if (!"POST".equals(exchange.getRequestMethod())) {
            respondJson(exchange, 405, Json.object(Json.of("error", "POST the overrides to /api/nexus")));
            return;
        }
        Map<String, Object> body = readJsonObject(exchange);
        List<String> applied = nexus.config().apply(body);
        // The response carries the *whole* resulting config, not just what changed: the dashboard's
        // controls are rebuilt from it, so a value that was clamped (a negative interval) is visible
        // immediately rather than silently differing from what was typed.
        log.emitNote("relay", "auto-nexus settings changed from the dashboard",
                Fields.of().add("request", body).add("applied", applied)
                        .add("config", nexus.config().toMap()));
        respondJson(exchange, 200, Json.object(
                Json.of("applied", applied),
                Json.of("config", nexus.config().toMap())));
    }

    private void filtersEndpoint(HttpExchange exchange) throws IOException {
        if ("GET".equals(exchange.getRequestMethod())) {
            respondJson(exchange, 200, Json.value(filtersMap()));
            return;
        }
        if (!"POST".equals(exchange.getRequestMethod())) {
            respondJson(exchange, 405, Json.object(Json.of("error", "GET or POST /api/filters")));
            return;
        }
        Map<String, Object> body = readJsonObject(exchange);
        List<LogFilter> filters = parseFilters(body);
        if (filters == null) {
            respondJson(exchange, 400, Json.object(Json.of("error",
                    "expected {\"filters\":[{\"name\":...,\"enabled\":...,\"kinds\":...,\"packets\":...,\"sessions\":...}]}")));
            return;
        }
        registry.filters(filters);
        log.emitNote("relay", "dashboard filters replaced",
                Fields.of().add("count", filters.size())
                        .add("enabled", filters.stream().filter(LogFilter::enabled).map(LogFilter::name).toList()));
        respondJson(exchange, 200, Json.value(filtersMap()));
    }

    @SuppressWarnings("unchecked")
    private List<LogFilter> parseFilters(Map<String, Object> body) {
        Object raw = body.get("filters");
        if (!(raw instanceof List<?> list)) {
            return null;
        }
        List<LogFilter> filters = new ArrayList<>();
        for (Object item : list) {
            if (!(item instanceof Map<?, ?> map)) {
                continue;
            }
            Map<String, Object> entry = (Map<String, Object>) map;
            filters.add(LogFilter.parse(
                    string(entry.get("name")),
                    !Boolean.FALSE.equals(entry.get("enabled")),
                    string(entry.get("kinds")),
                    string(entry.get("packets")),
                    string(entry.get("sessions"))));
        }
        return filters.isEmpty() ? null : filters;
    }

    private Map<String, Object> filtersMap() {
        List<Map<String, Object>> filters = new ArrayList<>();
        for (LogFilter filter : registry.filters()) {
            filters.add(Fields.of()
                    .add("name", filter.name())
                    .add("enabled", filter.enabled())
                    .add("kinds", new ArrayList<>(filter.kinds() == null ? List.of() : filter.kinds()))
                    .add("packets", new ArrayList<>(filter.packets() == null ? List.of() : filter.packets()))
                    .add("sessions", new ArrayList<>(filter.sessions() == null ? List.of() : filter.sessions()))
                    .add("builtIn", filter.builtIn()));
        }
        return Fields.of()
                .add("filters", filters)
                .add("default", LogFilter.DEFAULT_NAME)
                .add("hpPackets", new ArrayList<>(LogFilter.HP_PACKETS));
    }

    private String packetsJson() {
        List<Map<String, Object>> game = new ArrayList<>();
        for (int id : networking.packets.PacketRegistry.gameIds()) {
            game.add(Fields.of().add("id", id).add("name", networking.packets.GmPacketType.name(id)));
        }
        List<Map<String, Object>> queue = new ArrayList<>();
        for (int id : networking.packets.PacketRegistry.queueIds()) {
            queue.add(Fields.of().add("id", id).add("name", networking.packets.QPacketType.name(id)));
        }
        return Json.object(Json.of("game", game), Json.of("queue", queue));
    }

    // --- http plumbing ------------------------------------------------------------------------

    private Map<String, Object> readJsonObject(HttpExchange exchange) throws IOException {
        try (InputStream in = exchange.getRequestBody()) {
            String body = new String(in.readAllBytes(), StandardCharsets.UTF_8);
            if (body.isBlank()) {
                return Map.of();
            }
            Object parsed = MiniJson.parse(body);
            if (parsed instanceof Map<?, ?> map) {
                Map<String, Object> out = new LinkedHashMap<>();
                map.forEach((key, value) -> out.put(String.valueOf(key), value));
                return out;
            }
            return Map.of("value", parsed);
        }
    }

    private void respondJson(HttpExchange exchange, int status, String body) throws IOException {
        respond(exchange, status, "application/json; charset=utf-8", body);
    }

    private void respond(HttpExchange exchange, int status, String contentType, String body) throws IOException {
        byte[] bytes = body.getBytes(StandardCharsets.UTF_8);
        exchange.getResponseHeaders().set("Content-Type", contentType);
        exchange.getResponseHeaders().set("Cache-Control", "no-store");
        exchange.getResponseHeaders().set("Access-Control-Allow-Origin", "*");
        exchange.getResponseHeaders().set("Access-Control-Allow-Headers", "content-type");
        exchange.sendResponseHeaders(status, bytes.length);
        try (OutputStream out = exchange.getResponseBody()) {
            out.write(bytes);
        }
    }

    private static Map<String, String> query(HttpExchange exchange) {
        Map<String, String> out = new LinkedHashMap<>();
        String raw = exchange.getRequestURI().getRawQuery();
        if (raw == null || raw.isEmpty()) {
            return out;
        }
        for (String pair : raw.split("&")) {
            int eq = pair.indexOf('=');
            if (eq < 0) {
                out.put(decode(pair), "");
            } else {
                out.put(decode(pair.substring(0, eq)), decode(pair.substring(eq + 1)));
            }
        }
        return out;
    }

    private static String decode(String value) {
        return java.net.URLDecoder.decode(value, StandardCharsets.UTF_8);
    }

    private static long parseLong(String value, long fallback) {
        try {
            return value == null ? fallback : Long.parseLong(value.trim());
        } catch (NumberFormatException e) {
            return fallback;
        }
    }

    private static String string(Object value) {
        return value == null ? null : String.valueOf(value);
    }

    /** A tiny JSON reader for request bodies; the relay has no JSON dependency by design. */
    static final class MiniJson {
        private final String src;
        private int pos;

        private MiniJson(String src) {
            this.src = src;
        }

        static Object parse(String text) {
            MiniJson parser = new MiniJson(text);
            parser.ws();
            Object value = parser.value();
            parser.ws();
            return value;
        }

        private void ws() {
            while (pos < src.length() && Character.isWhitespace(src.charAt(pos))) {
                pos++;
            }
        }

        private Object value() {
            ws();
            if (pos >= src.length()) {
                return null;
            }
            char c = src.charAt(pos);
            return switch (c) {
                case '{' -> object();
                case '[' -> array();
                case '"' -> string();
                case 't' -> literal("true", Boolean.TRUE);
                case 'f' -> literal("false", Boolean.FALSE);
                case 'n' -> literal("null", null);
                default -> number();
            };
        }

        private Object literal(String word, Object result) {
            if (!src.startsWith(word, pos)) {
                throw new IllegalArgumentException("expected " + word + " at " + pos);
            }
            pos += word.length();
            return result;
        }

        private Map<String, Object> object() {
            expect('{');
            Map<String, Object> out = new LinkedHashMap<>();
            ws();
            if (peek() == '}') {
                pos++;
                return out;
            }
            while (true) {
                ws();
                String key = string();
                ws();
                expect(':');
                out.put(key, value());
                ws();
                char c = src.charAt(pos++);
                if (c == '}') {
                    return out;
                }
                if (c != ',') {
                    throw new IllegalArgumentException("expected , or } at " + pos);
                }
            }
        }

        private List<Object> array() {
            expect('[');
            List<Object> out = new ArrayList<>();
            ws();
            if (peek() == ']') {
                pos++;
                return out;
            }
            while (true) {
                out.add(value());
                ws();
                char c = src.charAt(pos++);
                if (c == ']') {
                    return out;
                }
                if (c != ',') {
                    throw new IllegalArgumentException("expected , or ] at " + pos);
                }
            }
        }

        private String string() {
            expect('"');
            StringBuilder sb = new StringBuilder();
            while (true) {
                char c = src.charAt(pos++);
                if (c == '"') {
                    return sb.toString();
                }
                if (c == '\\') {
                    char escape = src.charAt(pos++);
                    switch (escape) {
                        case 'n' -> sb.append('\n');
                        case 't' -> sb.append('\t');
                        case 'r' -> sb.append('\r');
                        case 'b' -> sb.append('\b');
                        case 'f' -> sb.append('\f');
                        case 'u' -> {
                            sb.append((char) Integer.parseInt(src.substring(pos, pos + 4), 16));
                            pos += 4;
                        }
                        default -> sb.append(escape);
                    }
                } else {
                    sb.append(c);
                }
            }
        }

        private Object number() {
            int start = pos;
            while (pos < src.length() && "-+.eE0123456789".indexOf(src.charAt(pos)) >= 0) {
                pos++;
            }
            String text = src.substring(start, pos);
            if (text.isEmpty()) {
                throw new IllegalArgumentException("expected a value at " + start);
            }
            if (text.indexOf('.') < 0 && text.indexOf('e') < 0 && text.indexOf('E') < 0) {
                try {
                    return Long.parseLong(text);
                } catch (NumberFormatException ignored) {
                    // fall through to double
                }
            }
            return Double.parseDouble(text);
        }

        private char peek() {
            return src.charAt(pos);
        }

        private void expect(char c) {
            ws();
            if (src.charAt(pos) != c) {
                throw new IllegalArgumentException("expected " + c + " at " + pos);
            }
            pos++;
        }
    }
}
