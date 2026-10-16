package networking;

import networking.packets.GmPacketType;
import networking.packets.Injection;
import networking.packets.PacketRegistry;
import networking.packets.gmpackets.ReconnectPacket;
import networking.log.Event;
import networking.log.EventLog;
import networking.log.LogClock;
import networking.util.Prefs;
import networking.web.WebDashboard;

import java.io.ByteArrayInputStream;
import java.io.EOFException;
import java.io.IOException;
import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.NetworkInterface;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.LocalTime;
import java.time.format.DateTimeFormatter;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicLong;

/**
 * Local relay for Darza's Dominion traffic.
 *
 * <p>Why a relay instead of the original fixed-route proxy: the client no longer has a
 * compiled-in game-server address. It asks the HTTP API for a server list and then dials
 * whatever address the API hands back, so the destination changes between sessions. This
 * relay therefore listens on the ports the client dials and forwards each session to a
 * destination resolved at session time, either from a static route table or from a
 * dynamically learned address.
 *
 * <p>Wire format (verified against the decompiled client, see docs/PROTOCOL.md):
 * <pre>
 *   [4 bytes] payload length, big endian   (DarzaCore.Networking.ProgramConnection)
 *   [N bytes] payload; payload[0] is the packet id
 * </pre>
 * Game and Game_Slave send big-endian lengths; the queue server sends little-endian (see Endianness).
 * The old proxy corrected endianness with a "&gt; 1_000_000 -&gt; reverseBytes" heuristic
 * and wrote little-endian; both are wrong and are not used here.
 */
public final class Relay {

    /** [4-byte length][payload]; payload[0] is the packet id. */
    static final int LENGTH_PREFIX = 4;

    /**
     * Payload length byte order. It is <strong>not</strong> the same for every service:
     *
     * <ul>
     *   <li>The game server sends big-endian lengths. Verified against the live server:
     *       {@code test_relay_live.py} reads {@code 00 00 00 0A} before a 10-byte payload.</li>
     *   <li>The queue server sends little-endian lengths. Verified from a live session where the
     *       client's first frame began {@code 08 00 00 00} (little-endian 8) and a big-endian
     *       reader made of it 0x08000000 = 134217728.</li>
     * </ul>
     *
     * <p>Rather than hard-code a byte order per service name and hope, the relay starts in
     * {@link #AUTO} and locks in whichever order yields a plausible length. A wrong guess gives an
     * absurd value, so the distinction is unambiguous in practice.
     */
    enum Endianness {
        AUTO,
        BIG,
        LITTLE
    }

    /** Above this, a length is not believable; the largest observed payload is a few KB. */
    static final int MAX_PLAUSIBLE_PAYLOAD = 1 << 20;

    /**
     * One forwarded service.
     *
     * <p>{@code optional} marks a route whose destination is legitimately not serving: the client
     * may never dial that port at all, yet the relay must keep listening on it so that a dial it
     * does make later is captured instead of silently bypassing the proxy. A failed startup
     * preflight on such a route is reported as a note, not as a defect - {@code Game_Slave} is the
     * case this exists for (see {@code relay-routes.json} and docs/PROTOCOL.md 9).
     */
    record Route(int listenPort, String remoteHost, int remotePort, String name, Endianness endianness,
                 boolean optional) {

        Route(int listenPort, String remoteHost, int remotePort, String name) {
            this(listenPort, remoteHost, remotePort, name, Endianness.AUTO, false);
        }

        Route(int listenPort, String remoteHost, int remotePort, String name, boolean optional) {
            this(listenPort, remoteHost, remotePort, name, Endianness.AUTO, optional);
        }

        /**
         * The queue service has its own id space: id 1 is {@code QHello} there but {@code Update}
         * on the game service, and id 4 is {@code QError} versus {@code RegisterResp}.
         */
        boolean isQueue() {
            return "Queue".equalsIgnoreCase(name);
        }
    }

    static final class Config {
        String listenHost = "127.0.0.1";
        int defaultListenPort = 6410;
        String defaultRemoteHost = null;
        int defaultRemotePort = 0;
        final List<Route> routes = new ArrayList<>();
        final Map<Integer, String> learned = new ConcurrentHashMap<>();

        /**
         * Local address the outbound (upstream) connections are bound to, normally the machine's
         * LAN address. Required whenever a route's destination is claimed locally.
         *
         * <p>An address claim makes the destination a <em>local</em> address, and Windows delivers
         * a connection to a local address only when the sending socket belongs to the same
         * interface. An unbound dial therefore selects the destination itself as its source and
         * lands in this relay's own listener: the relay dials itself. Binding the source to an
         * address on a different interface - the claim lives on the loopback pseudo-interface -
         * makes the dial leave the host, which is what reaches the real server. Measured on this
         * machine; see docs/PROTOCOL.md, "The claim must sit on a different interface than the
         * upstream source".
         *
         * <p>{@code -Ddrelay.upstreamHost=192.168.0.39} overrides the route table, so a launcher
         * script can pass the address it resolved instead of the JSON pinning one machine.
         */
        String upstreamHost = null;

        /**
         * Local source port for outbound (upstream) connections, or 0 to let the OS choose.
         *
         * <p>Only the packet-level redirect needs this. It exists to give the WinDivert filter one
         * exact port to exclude, because that redirect is machine-wide on the destination ports and
         * otherwise catches the relay's own dials; a source-port guard cannot separate them, since
         * the OS hands both the client and the relay ports from the same range.
         *
         * <p>With an address claim the redirector is not in the path at all, so leave this 0. That
         * also removes its cost: Windows permits only one connection bound to a given local port, so
         * a pinned port serializes upstream dials and fails a second concurrent session (queue and
         * game overlapping, for instance).
         */
        int upstreamBindPort = 0;

        // --- observability -----------------------------------------------------------------
        //
        // All of this is optional and none of it can break a session: the dashboard is loopback-only
        // and its failure is reported rather than fatal, and the event log falls back to memory when
        // the directory cannot be written.

        /** Local directory for the structured event log, or null to keep everything in memory. */
        String logDirectory = "work/logs";
        /** Events kept in the dashboard ring. */
        int ringCapacity = 20_000;
        /** Rotate the event JSONL once it reaches this many bytes. */
        long logMaxBytes = 32L << 20;
        /** Dashboard bind host; loopback by default so nothing is exposed off the machine. */
        String webHost = "127.0.0.1";
        /** Dashboard port. 0 disables it. A negative value means "let the OS choose". */
        int webPort = 8765;

        // --- auto-nexus --------------------------------------------------------------------
        //
        // Defaults describe a rule that is installed but inert: `enabled` is off. Three ways in -
        // the route table, -Ddrelay.nexus.*, or the dashboard - and the first two are recorded in
        // the log as session notes so a later analysis knows which settings produced a run.

        final AutoNexus.Config nexus = new AutoNexus.Config();

        // --- server->client rewrites --------------------------------------------------------
        //
        // On by default with Confused and Hallucinating armed, and the only settings here that change
        // bytes the server sent rather than adding bytes of our own. It exists because some status
        // effects are applied purely client-side: the server states them, the client obeys them, and
        // nothing in the client expires them locally. Removing the entry is therefore the only way to
        // not be affected by them, and it is a gameplay change, not an observability one - hence a
        // module with its own dashboard panel, its own persistence and its own log lines. Only the
        // effects that no client-side law feeds back to the wire are armed; see
        // StatusStrip for the wire format and the measurements behind it, and docs/INJECTION.md 5.
        //
        // The auto-nexus config is read from the route table and then the dashboard owns it; the
        // strip config is the same contract (see Strip.Config), so the two are wired identically.

        final Strip.Config strip = new Strip.Config();
    }

    /** Serializes upstream dials when {@link Config#upstreamBindPort} is pinned. */
    private static final Object UPSTREAM_BIND_LOCK = new Object();

    private static final AtomicLong SESSION_IDS = new AtomicLong();
    private static final DateTimeFormatter CLOCK = DateTimeFormatter.ofPattern("HH:mm:ss.SSS");

    /** Addresses this relay claimed at runtime, so they can be given back when it exits. */
    private static final Set<String> LEARNED_CLAIMS = ConcurrentHashMap.newKeySet();

    /** Where runtime claims are recorded, so a relay that was killed can still be cleaned up. */
    private static final Path LEARNED_CLAIMS_FILE = Path.of("work", "learned-claims.txt");

    sealed interface Json permits JStr, JNum, JObj, JArr {
        default String asString() { return null; }
        default int asInt() { return -1; }
        default JObj asObj() { return this instanceof JObj o ? o : null; }
    }

    record JStr(String value) implements Json {
        public String asString() { return value; }
    }

    record JNum(double value) implements Json {
        public int asInt() { return (int) value; }
    }

    record JObj(Map<String, Json> values) implements Json {
        Json get(String key) { return values.get(key); }
        String str(String key, String fallback) {
            Json v = values.get(key);
            return v == null || v.asString() == null ? fallback : v.asString();
        }
        boolean bool(String key, boolean fallback) {
            Json v = values.get(key);
            return v instanceof JNum n ? n.asInt() != 0 : fallback;
        }
    }

    record JArr(List<Json> values) implements Json {}

    /** Minimal JSON reader; the config is tiny and a dependency-free build is preferred. */
    static final class JsonParser {
        private final String src;
        private int pos;

        JsonParser(String src) { this.src = src; }

        static Json parse(String src) {
            var p = new JsonParser(src);
            p.skipWs();
            Json v = p.value();
            p.skipWs();
            return v;
        }

        private void skipWs() {
            while (pos < src.length() && Character.isWhitespace(src.charAt(pos))) pos++;
        }

        private Json value() {
            skipWs();
            if (pos >= src.length()) throw new IllegalArgumentException("unexpected end of JSON");
            char c = src.charAt(pos);
            return switch (c) {
                case '{' -> object();
                case '[' -> array();
                case '"' -> new JStr(string());
                // JSON literals. Booleans are carried as numbers so no extra Json subtype is needed,
                // and "null" becomes an empty string, which every accessor already treats as absent.
                case 't' -> { literal("true"); yield new JNum(1); }
                case 'f' -> { literal("false"); yield new JNum(0); }
                case 'n' -> { literal("null"); yield new JStr(null); }
                default -> number();
            };
        }

        private Json object() {
            expect('{');
            var map = new HashMap<String, Json>();
            skipWs();
            if (peek() == '}') { pos++; return new JObj(map); }
            while (true) {
                skipWs();
                String key = string();
                skipWs();
                expect(':');
                map.put(key, value());
                skipWs();
                char c = src.charAt(pos++);
                if (c == '}') return new JObj(map);
                if (c != ',') throw new IllegalArgumentException("expected , or } at " + pos);
            }
        }

        private Json array() {
            expect('[');
            var list = new ArrayList<Json>();
            skipWs();
            if (peek() == ']') { pos++; return new JArr(list); }
            while (true) {
                list.add(value());
                skipWs();
                char c = src.charAt(pos++);
                if (c == ']') return new JArr(list);
                if (c != ',') throw new IllegalArgumentException("expected , or ] at " + pos);
            }
        }

        private String string() {
            expect('"');
            var sb = new StringBuilder();
            while (true) {
                char c = src.charAt(pos++);
                if (c == '"') return sb.toString();
                if (c == '\\') {
                    char e = src.charAt(pos++);
                    switch (e) {
                        case 'n' -> sb.append('\n');
                        case 't' -> sb.append('\t');
                        case 'r' -> sb.append('\r');
                        case 'u' -> { sb.append((char) Integer.parseInt(src.substring(pos, pos + 4), 16)); pos += 4; }
                        default -> sb.append(e);
                    }
                } else {
                    sb.append(c);
                }
            }
        }

        private Json number() {
            int start = pos;
            while (pos < src.length() && "-+.eE0123456789".indexOf(src.charAt(pos)) >= 0) pos++;
            return new JNum(Double.parseDouble(src.substring(start, pos)));
        }

        private char peek() { return src.charAt(pos); }

        /** Consumes a bare JSON literal such as {@code true} or {@code null}. */
        private void literal(String word) {
            if (!src.startsWith(word, pos)) {
                throw new IllegalArgumentException("expected " + word + " at " + pos);
            }
            pos += word.length();
        }

        private void expect(char c) {
            skipWs();
            if (src.charAt(pos) != c) throw new IllegalArgumentException("expected " + c + " at " + pos);
            pos++;
        }
    }

    static Config loadConfig(Path path) throws IOException {
        var cfg = new Config();
        if (path == null || !Files.exists(path)) {
            return cfg;
        }
        Json root = JsonParser.parse(Files.readString(path));
        JObj obj = root.asObj();
        if (obj == null) {
            throw new IllegalArgumentException("config root must be an object");
        }
        Json listenHost = obj.get("listenHost");
        if (listenHost != null) cfg.listenHost = listenHost.asString();
        Json defaultPort = obj.get("listenPort");
        if (defaultPort != null) cfg.defaultListenPort = defaultPort.asInt();
        Json defaultHost = obj.get("defaultRemoteHost");
        if (defaultHost != null) cfg.defaultRemoteHost = defaultHost.asString();
        Json defaultRemotePort = obj.get("defaultRemotePort");
        if (defaultRemotePort != null) cfg.defaultRemotePort = defaultRemotePort.asInt();
        Json upstreamBind = obj.get("upstreamBindPort");
        if (upstreamBind != null) cfg.upstreamBindPort = upstreamBind.asInt();
        Json upstreamHostJson = obj.get("upstreamHost");
        if (upstreamHostJson != null) cfg.upstreamHost = upstreamHostJson.asString();

        // --- observability and auto-nexus --------------------------------------------------
        Json logDirectory = obj.get("logDirectory");
        if (logDirectory != null && logDirectory.asString() != null) {
            cfg.logDirectory = logDirectory.asString();
        }
        Json ringCapacity = obj.get("ringCapacity");
        if (ringCapacity != null && ringCapacity.asInt() > 0) {
            cfg.ringCapacity = ringCapacity.asInt();
        }
        Json logMaxBytes = obj.get("logMaxBytes");
        if (logMaxBytes != null && logMaxBytes.asInt() > 0) {
            cfg.logMaxBytes = logMaxBytes.asInt();
        }
        Json web = obj.get("web");
        if (web instanceof JObj webObj) {
            String host = webObj.str("host", null);
            if (host != null) cfg.webHost = host;
            Json port = webObj.get("port");
            if (port != null) cfg.webPort = port.asInt();
        }
        Json nexus = obj.get("autoNexus");
        if (nexus instanceof JObj nexusObj) {
            applyNexusConfig(cfg.nexus, nexusObj.values());
        }
        Json strip = obj.get("strip");
        if (strip instanceof JObj stripObj) {
            applyStripConfig(cfg.strip, stripObj.values());
        }

        // System properties and environment variables win over the file: a launcher script knows
        // things the checked-in JSON does not, and this is the only way to flip the master switch
        // for one run without editing a tracked file.
        cfg.logDirectory = Prefs.string("drelay.log.dir", "DRELAY_LOG_DIR", cfg.logDirectory);
        cfg.ringCapacity = Prefs.integer("drelay.log.ring", "DRELAY_LOG_RING", cfg.ringCapacity);
        cfg.logMaxBytes = Prefs.longValue("drelay.log.maxBytes", "DRELAY_LOG_MAX_BYTES", cfg.logMaxBytes);
        cfg.webHost = Prefs.string("drelay.web.host", "DRELAY_WEB_HOST", cfg.webHost);
        cfg.webPort = Prefs.integer("drelay.web.port", "DRELAY_WEB_PORT", cfg.webPort);

        // The strip is read from the route table first and then from properties, with the same
        // precedence as everything else: system property, then environment variable, then the file.
        // `effects` and `effect` replace the armed set; the named switches arm or disarm one effect,
        // which is what makes -Ddrelay.strip.confused=false work even though Confused is the default.
        Map<String, Object> stripOverrides = new java.util.LinkedHashMap<>();
        putIfSet(stripOverrides, "enabled", System.getProperty("drelay.strip.enabled"), System.getenv("DRELAY_STRIP_ENABLED"));
        putIfSet(stripOverrides, "effects", System.getProperty("drelay.strip.effects"), System.getenv("DRELAY_STRIP_EFFECTS"));
        putIfSet(stripOverrides, "effect", System.getProperty("drelay.strip.effect"), System.getenv("DRELAY_STRIP_EFFECT"));
        putIfSet(stripOverrides, "confused", System.getProperty("drelay.strip.confused"), System.getenv("DRELAY_STRIP_CONFUSED"));
        putIfSet(stripOverrides, "paralyzed", System.getProperty("drelay.strip.paralyzed"), System.getenv("DRELAY_STRIP_PARALYZED"));
        putIfSet(stripOverrides, "slowed", System.getProperty("drelay.strip.slowed"), System.getenv("DRELAY_STRIP_SLOWED"));
        putIfSet(stripOverrides, "hallucinating", System.getProperty("drelay.strip.hallucinating"), System.getenv("DRELAY_STRIP_HALLUCINATING"));
        putIfSet(stripOverrides, "minVotes", System.getProperty("drelay.strip.minVotes"), System.getenv("DRELAY_STRIP_MIN_VOTES"));
        if (!stripOverrides.isEmpty()) {
            IO.println("  strip from properties: " + String.join(", ", cfg.strip.apply(stripOverrides)));
        }

        Map<String, Object> nexusOverrides = new java.util.LinkedHashMap<>();
        putIfSet(nexusOverrides, "enabled", System.getProperty("drelay.nexus.enabled"), System.getenv("DRELAY_NEXUS_ENABLED"));
        putIfSet(nexusOverrides, "dryRun", System.getProperty("drelay.nexus.dryRun"), System.getenv("DRELAY_NEXUS_DRY_RUN"));
        putIfSet(nexusOverrides, "thresholdPercent", System.getProperty("drelay.nexus.percent"), System.getenv("DRELAY_NEXUS_PERCENT"));
        putIfSet(nexusOverrides, "useEffectiveHp", System.getProperty("drelay.nexus.effective"), System.getenv("DRELAY_NEXUS_EFFECTIVE"));
        putIfSet(nexusOverrides, "minIntervalMillis", System.getProperty("drelay.nexus.delayMs"), System.getenv("DRELAY_NEXUS_DELAY_MS"));
        putIfSet(nexusOverrides, "maxPerWorld", System.getProperty("drelay.nexus.maxPerWorld"), System.getenv("DRELAY_NEXUS_MAX_PER_WORLD"));
        putIfSet(nexusOverrides, "skipInSafeArea", System.getProperty("drelay.nexus.skipSafeArea"), System.getenv("DRELAY_NEXUS_SKIP_SAFE_AREA"));
        putIfSet(nexusOverrides, "rearmPercent", System.getProperty("drelay.nexus.rearm"), System.getenv("DRELAY_NEXUS_REARM"));
        putIfSet(nexusOverrides, "useCastChannel", System.getProperty("drelay.nexus.castChannel"), System.getenv("DRELAY_NEXUS_CAST_CHANNEL"));
        if (!nexusOverrides.isEmpty()) {
            cfg.nexus.apply(nexusOverrides);
        }

        // A launcher knows the machine's LAN address better than a checked-in JSON file does.
        String upstreamOverride = System.getProperty("drelay.upstreamHost");
        if (upstreamOverride != null && !upstreamOverride.isBlank()) {
            cfg.upstreamHost = upstreamOverride.trim();
        }
        Json routes = obj.get("routes");
        if (routes instanceof JArr arr) {
            for (Json r : arr.values()) {
                JObj ro = r.asObj();
                if (ro == null) continue;
                Json lp = ro.get("listenPort");
                Json rh = ro.get("remoteHost");
                Json rp = ro.get("remotePort");
                if (lp == null || rh == null || rp == null) {
                    System.err.println("skipping incomplete route: " + ro.values());
                    continue;
                }
                cfg.routes.add(new Route(
                        lp.asInt(),
                        rh.asString(),
                        rp.asInt(),
                        ro.str("name", "service"),
                        ro.bool("optional", false)));
            }
        }
        return cfg;
    }

    /** Copies a JSON object of auto-nexus settings onto the live config; unknown keys are reported. */
    private static void applyNexusConfig(AutoNexus.Config target, Map<String, Json> values) {
        Map<String, Object> plain = new java.util.LinkedHashMap<>();
        values.forEach((key, value) -> {
            if (value instanceof JNum number) {
                plain.put(key, number.value());
            } else if (value.asString() != null) {
                plain.put(key, value.asString());
            }
        });
        java.util.List<String> applied = target.apply(plain);
        if (!applied.isEmpty()) {
            IO.println("  auto-nexus from config: " + String.join(", ", applied));
        }
    }

    /**
     * Copies the route table's {@code strip} block onto the live config; unknown keys are reported.
     *
     * <p>Scalars keep their JSON type - a boolean stays a boolean - but `effects` may be a list, and it
     * has to arrive as one: flattening it to a string here is how the old single-effect form would
     * quietly become "arm nothing".
     */
    private static void applyStripConfig(Strip.Config target, Map<String, Json> values) {
        Map<String, Object> plain = new java.util.LinkedHashMap<>();
        values.forEach((key, value) -> {
            if (value instanceof JNum number) {
                plain.put(key, number.value());
            } else if (value instanceof JArr array) {
                java.util.List<Object> items = new java.util.ArrayList<>();
                for (Json item : array.values()) {
                    if (item instanceof JNum number) {
                        items.add(number.value());
                    } else if (item.asString() != null) {
                        items.add(item.asString());
                    }
                }
                plain.put(key, items);
            } else if (value.asString() != null) {
                plain.put(key, value.asString());
            }
        });
        java.util.List<String> applied = target.apply(plain);
        if (!applied.isEmpty()) {
            IO.println("  strip from config: " + String.join(", ", applied));
        }
    }

    /** Records an override only when either source actually supplied one. */
    private static void putIfSet(Map<String, Object> target, String key, String property, String environment) {
        String value = property != null && !property.isBlank() ? property
                : (environment != null && !environment.isBlank() ? environment : null);
        if (value != null) {
            target.put(key, value);
        }
    }

    public static void main(String[] args) throws Exception {
        // The first argument that is not a -D property is the route table. System properties are
        // passed on the command line too (they have to be: a JVM cannot set its own), and an earlier
        // version took args[0] blindly, so a property starting with -D was read as the config path
        // and the relay silently started with defaults instead of the file it was given.
        Path configPath = Path.of("relay-routes.json");
        for (String argument : args) {
            if (!argument.startsWith("-D")) {
                configPath = Path.of(argument);
                break;
            }
        }
        Config cfg = loadConfig(configPath);

        if (cfg.routes.isEmpty()) {
            cfg.routes.add(new Route(cfg.defaultListenPort, cfg.defaultRemoteHost, cfg.defaultRemotePort, "default"));
        }

        IO.println("drelay relay - config: " + configPath.toAbsolutePath());
        IO.println("  upstream source: %s, %s".formatted(
                cfg.upstreamHost == null || cfg.upstreamHost.isBlank() ? "(unbound)" : cfg.upstreamHost,
                cfg.upstreamBindPort == 0 ? "ephemeral port" : "pinned port " + cfg.upstreamBindPort));
        for (Route r : cfg.routes) {
            IO.println("  route %-10s listen :%d -> %s:%d".formatted(r.name(), r.listenPort(), r.remoteHost(), r.remotePort()));
        }

        // Observability comes up before any listener: if a session misbehaves in its first second,
        // the events that explain it already have somewhere to go.
        EventLog log = new EventLog(
                cfg.logDirectory == null ? null : Path.of(cfg.logDirectory),
                newRunId(),
                cfg.ringCapacity,
                cfg.logMaxBytes);
        AutoNexus nexus = new AutoNexus(cfg.nexus, log);
        Strip strip = new Strip(cfg.strip, log);
        SessionRegistry registry = new SessionRegistry(log, nexus);
        // The dashboard is given the path this run was started with, so a setting changed in the page
        // is written back to the file the relay read - that is what makes it survive a restart.
        WebDashboard dashboard = new WebDashboard(registry, log, nexus, strip, cfg.webHost, cfg.webPort,
                configPath);
        dashboard.start();

        IO.println("  auto-nexus: %s, threshold %d%%, %s, max %d per world, min interval %d ms".formatted(
                cfg.nexus.enabled ? "ENABLED" : "disabled",
                cfg.nexus.thresholdPercent,
                cfg.nexus.dryRun ? "dry run (writes nothing)" : "LIVE (injects Escape)",
                cfg.nexus.maxPerWorld,
                cfg.nexus.minIntervalMillis));
        // Printed unconditionally: "off" has to be as visible as "on", because the whole feature is
        // silent when it cannot name the local player and a run that never rewrote anything looks
        // exactly like a run where the debuff never arrived.
        IO.println(strip.describe());
        if (cfg.logDirectory != null) {
            IO.println("  event log: %s".formatted(Path.of(cfg.logDirectory, "events-" + log.runId() + ".jsonl").toAbsolutePath()));
        }

        // Refuse layouts whose upstream dials can only come back here, and learn which routes are
        // claimed locally: those are proved before any listener exists (see preflightClaimed).
        Set<String> claimedRoutes = validateUpstream(cfg);

        // Pinned IPs go stale: the operator rotates servers and the client learns the current
        // ones from the API at runtime. Resolve names now, and say plainly when a fixed address
        // fails, so a stale route is obvious rather than looking like a client-side bug.
        for (Route r : cfg.routes) {
            if (claimedRoutes.contains(r.name())) {
                // Synchronous, and before the listeners: a captured dial must fail here rather than
                // be answered by this relay's own listener and loop forever.
                preflightClaimed(r, cfg);
            } else {
                Thread.startVirtualThread(() -> preflight(r, cfg));
            }
        }

        for (Route route : cfg.routes) {
            Thread.startVirtualThread(() -> listen(route, cfg, registry, nexus, strip));
        }

        // A server can name a new destination at any time (GmReconnect), and that address has to be
        // claimed before the client dials it. Give those claims back when this relay stops.
        Runtime.getRuntime().addShutdownHook(new Thread(Relay::releaseLearnedClaims, "learned-claim-release"));
        Runtime.getRuntime().addShutdownHook(new Thread(() -> {
            log.emitNote("relay", "relay stopping; flushing the event log",
                    java.util.Map.of("sessions", registry.sessions().size(),
                            "events", log.lastSequence()));
            dashboard.stop();
            log.close();
        }, "observability-flush"));

        Thread.currentThread().join();
    }

    /**
     * A run id that sorts by time and is unique enough to find: {@code 20261003-141530-4821}.
     *
     * <p>Every run gets its own log files instead of appending to one, so "what happened in the
     * session I just played" is a single file rather than a search through a week of history, and a
     * run that is being analysed is never overwritten by the next one.
     */
    private static String newRunId() {
        return java.time.LocalDateTime.now().format(java.time.format.DateTimeFormatter.ofPattern("yyyyMMdd-HHmmss"))
                + "-" + (System.nanoTime() % 10_000);
    }

    /** The interface that owns {@code address}, or null when this machine does not have it. */
    private static NetworkInterface owningInterface(InetAddress address) {
        try {
            var interfaces = NetworkInterface.getNetworkInterfaces();
            while (interfaces.hasMoreElements()) {
                NetworkInterface networkInterface = interfaces.nextElement();
                var interfaceAddresses = networkInterface.getInetAddresses();
                while (interfaceAddresses.hasMoreElements()) {
                    if (interfaceAddresses.nextElement().equals(address)) {
                        return networkInterface;
                    }
                }
            }
        } catch (Exception ignored) {
            // best effort; a lookup failure must not stop the relay
        }
        return null;
    }

    /** Whether this interface is the loopback pseudo-interface, where a claim has to live. */
    private static boolean isLoopbackInterface(NetworkInterface networkInterface) {
        try {
            return networkInterface.isLoopback();
        } catch (Exception e) {
            return false;
        }
    }

    /**
     * Refuses a layout whose upstream dials can only come back to this relay, and returns the names
     * of the routes whose destination is claimed locally so those can be proved before the
     * listeners start.
     *
     * <p>Under an address claim the destination <em>is</em> a local address, so the dial escapes
     * only across a boundary Windows actually enforces. Measured on this machine that boundary is
     * the loopback interface, not "any other interface": a dial from one physical NIC to a local
     * address on another physical NIC, both on the same subnet, was still delivered locally, and
     * the relay then accepted its own dials and looped. So:
     *
     * <ul>
     *   <li>a claimed destination with no {@code upstreamHost} is refused - the unbound dial is
     *       local;</li>
     *   <li>a claim on a physical interface is refused, whatever {@code upstreamHost} says;</li>
     *   <li>an {@code upstreamHost} that is itself loopback is refused - the claim's local delivery
     *       would capture every dial;</li>
     *   <li>a claim on the loopback interface with a physical {@code upstreamHost} is allowed, and
     *       {@link #preflightClaimed} then proves the escape before any listener exists.</li>
     * </ul>
     */
    private static Set<String> validateUpstream(Config cfg) {
        record Claimed(String route, InetAddress address, NetworkInterface owner) {}

        var claimed = new ArrayList<Claimed>();
        for (Route r : cfg.routes) {
            if (r.remoteHost() == null) {
                continue;
            }
            InetAddress address;
            try {
                address = InetAddress.getByName(r.remoteHost());
            } catch (Exception e) {
                continue;                       // preflight reports unresolvable routes
            }
            // 127.0.0.0/8 is reserved, so it can never be a game server address: a destination there
            // is a deliberate local peer (the offline suites point a route at one). It is not a
            // claim - but pointing it at the relay's own listen port can only loop.
            if (address.isLoopbackAddress()) {
                if (r.listenPort() == r.remotePort()) {
                    System.err.println("Refusing to start: route %s points at %s:%d, the address and port it listens on."
                            .formatted(r.name(), address.getHostAddress(), r.remotePort()));
                    System.err.println("A dial to a loopback destination is delivered locally, so the relay would accept");
                    System.err.println("its own dials and loop. Point remotePort at the local peer's port instead.");
                    System.exit(2);
                }
                continue;
            }

            NetworkInterface owner = owningInterface(address);
            if (owner != null) {
                claimed.add(new Claimed(r.name(), address, owner));
            }
        }

        var names = new LinkedHashSet<String>();
        if (claimed.isEmpty()) {
            return names;                       // nothing is claimed: the legacy unbound dial is fine
        }

        if (cfg.upstreamHost == null) {
            System.err.println("Refusing to start: %d route destination(s) are claimed by this machine, and no"
                    .formatted(claimed.size()));
            System.err.println("upstreamHost is set, so every upstream dial would be delivered back into this");
            System.err.println("relay's own listener:");
            for (Claimed c : claimed) {
                System.err.println("  %s -> %s is claimed on '%s'".formatted(
                        c.route(), c.address().getHostAddress(), c.owner().getName()));
            }
            System.err.println("Set \"upstreamHost\" in the route table to this machine's LAN address, or pass");
            System.err.println("  -Ddrelay.upstreamHost=<lan-address>");
            System.exit(2);
        }

        InetAddress source;
        try {
            source = InetAddress.getByName(cfg.upstreamHost);
        } catch (Exception e) {
            System.err.println("upstreamHost '%s' does not resolve: %s".formatted(cfg.upstreamHost, e.getMessage()));
            System.exit(2);
            return names;
        }
        NetworkInterface sourceOwner = owningInterface(source);
        if (sourceOwner == null) {
            System.err.println("upstreamHost %s is not an address of this machine.".formatted(cfg.upstreamHost));
            System.exit(2);
        }
        if (isLoopbackInterface(sourceOwner)) {
            System.err.println("Refusing to start: upstreamHost %s is a loopback address, so a dial to a claimed"
                    .formatted(cfg.upstreamHost));
            System.err.println("destination would be delivered locally whatever the routes say. Bind it to the");
            System.err.println("machine's LAN address.");
            System.exit(2);
        }

        for (Claimed c : claimed) {
            if (!isLoopbackInterface(c.owner())) {
                System.err.println("Refusing to start: route %s targets %s, which this machine has on the physical"
                        .formatted(c.route(), c.address().getHostAddress()));
                System.err.println("interface '%s'. A claim there does not escape: measured, a dial from another NIC"
                        .formatted(c.owner().getName()));
                System.err.println("on the same subnet was still delivered locally, and the relay then accepted its");
                System.err.println("own dials in a loop. Claim on the loopback pseudo-interface instead:");
                System.err.println("  .\\tools\\Set-AddressClaim.ps1");
                System.exit(2);
            }
            names.add(c.route());
            IO.println("  claim check: %s destination %s is claimed on '%s'; upstream source %s is on '%s'"
                    .formatted(c.route(), c.address().getHostAddress(), c.owner().getName(),
                            cfg.upstreamHost, sourceOwner.getName()));
        }
        return names;
    }

    /**
     * Proves that a claimed route's dial leaves the host, before any listener can answer it.
     *
     * <p>A temporary listener is bound to the claimed address first. If the dial is captured
     * locally that listener accepts it, which is unambiguous and independent of the localized
     * Windows error text - and because this relay's listeners have not started yet, an accepted
     * dial cannot be mistaken for the real server answering. Exit 3 rather than start a proxy that
     * would accept its own dials forever.
     */
    private static void preflightClaimed(Route route, Config cfg) {
        InetAddress address;
        try {
            address = InetAddress.getByName(route.remoteHost());
        } catch (Exception e) {
            System.err.println("[%s] destination %s does not resolve: %s".formatted(
                    route.name(), route.remoteHost(), e.getMessage()));
            System.exit(3);
            return;
        }

        IO.println("[%s] %s is claimed locally; dialling it through %s before any listener exists"
                .formatted(route.name(), address.getHostAddress(), cfg.upstreamHost));

        ServerSocket trap = null;
        try {
            trap = new ServerSocket(route.listenPort(), 1, address);
        } catch (IOException e) {
            IO.println("[%s] could not bind a probe listener on %s:%d (%s); checking with a plain dial"
                    .formatted(route.name(), address.getHostAddress(), route.listenPort(), e.getMessage()));
        }

        Socket capturedSocket = null;
        try {
            CompletableFuture<Socket> accepted = new CompletableFuture<>();
            if (trap != null) {
                final ServerSocket trapSocket = trap;
                Thread.startVirtualThread(() -> {
                    try {
                        accepted.complete(trapSocket.accept());
                    } catch (Exception e) {
                        accepted.complete(null);
                    }
                });
            }

            try (var upstream = dialUpstream(address.getHostAddress(), route.remotePort(), cfg, route.name())) {
                // The socket is used only for its side effect: it either connects or it does not. Naming
                // the local port keeps that explicit and makes the failure message say which source the
                // dial actually left from.
                IO.println("[%s] probe dial bound to %s".formatted(
                        route.name(), upstream.getLocalSocketAddress()));
                if (trap != null) {
                    try {
                        capturedSocket = accepted.get(2, TimeUnit.SECONDS);
                    } catch (Exception ignored) {
                        // nothing accepted the dial, which is the result we want
                    }
                }
                if (capturedSocket != null) {
                    System.err.println("Refusing to start: the dial to the claimed address %s:%d was delivered"
                            .formatted(address.getHostAddress(), route.listenPort()));
                    System.err.println("locally - it arrived at this machine's own probe listener instead of leaving");
                    System.err.println("for the real server, so a claimed address cannot be reached from %s."
                            .formatted(cfg.upstreamHost));
                    System.exit(3);
                }
                IO.println("[%s] destination OK: %s:%d - the dial left the host and the real server answered"
                        .formatted(route.name(), route.remoteHost(), route.remotePort()));
            } catch (IOException e) {
                if (route.optional()) {
                    // Expected for an optional route: that port is simply not serving. The probe
                    // listener saw nothing, so the dial did leave the host - which is the property the
                    // claim depends on - so this is information, not a failure.
                    IO.println("[%s] destination %s:%d is not serving at the moment (%s)".formatted(
                            route.name(), route.remoteHost(), route.remotePort(), e.getMessage()));
                    IO.println("[%s] fine for an optional route: the probe listener saw nothing, so the dial left the"
                            .formatted(route.name()));
                    IO.println("[%s] host. The listener on :%d stays up, so a dial the server sends here is captured."
                            .formatted(route.name(), route.listenPort()));
                } else {
                    System.err.println("[%s] destination UNREACHABLE: %s:%d - %s".formatted(
                            route.name(), route.remoteHost(), route.remotePort(), e.getMessage()));
                    System.err.println("[%s] the probe listener saw nothing, so the dial did leave the host: the route's"
                            .formatted(route.name()));
                    System.err.println("[%s] address/port or the real server is the problem, not the claim."
                            .formatted(route.name()));
                }
            }
        } finally {
            if (capturedSocket != null) {
                closeQuietly(capturedSocket);
            }
            if (trap != null) {
                try {
                    trap.close();
                } catch (IOException ignored) {
                    // nothing useful left to do
                }
            }
        }
    }

    /**
     * Checks that a route's destination actually resolves and accepts a connection.
     *
     * <p>Run once at startup, concurrently with the listeners. A rotated server address would
     * otherwise surface as the client silently dropping back to the menu, which looks like a
     * client bug rather than a stale line in {@code relay-routes.json}.
     *
     * <p>The check uses the production dial path, not a plain connect: with a claim in place a
     * plain connect is delivered locally and would report this relay's own listener as "OK".
     */
    private static void preflight(Route route, Config cfg) {
        String host = route.remoteHost();
        if (host == null) {
            return;
        }
        InetAddress address = null;
        try {
            address = InetAddress.getByName(host);      // resolves DNS names too

            // A local destination is the expected state under an address claim - the claim is what
            // puts this relay in the client's path. It works only because the upstream source is
            // bound elsewhere (validateUpstream enforced that), so report which is which instead of
            // the old "your hosts file is lying to you" warning.
            var owner = owningInterface(address);
            if (owner != null && !address.isLoopbackAddress()) {
                IO.println("[%s] destination %s is claimed by this machine on '%s'; upstream dials bind %s"
                        .formatted(route.name(), address.getHostAddress(), owner.getName(), cfg.upstreamHost));
            }

            try (var socket = dialUpstream(address.getHostAddress(), route.remotePort(), cfg, route.name())) {
                IO.println("[%s] destination OK: %s:%d (%s) via local %s".formatted(
                        route.name(), host, route.remotePort(), address.getHostAddress(),
                        socket.getLocalSocketAddress()));
            }
        } catch (Exception e) {
            if (route.optional()) {
                IO.println("[%s] destination %s:%d is not serving at the moment (%s); optional route, so that is fine"
                        .formatted(route.name(), host, route.remotePort(), e.getMessage()));
                return;
            }
            System.err.println("[%s] destination UNREACHABLE: %s:%d - %s".formatted(
                    route.name(), host, route.remotePort(), e.getMessage()));
            if (address != null && address.isLoopbackAddress()) {
                System.err.println("[%s] that is a loopback peer (the offline suite points a route at one): nothing"
                        .formatted(route.name()));
                System.err.println("[%s] is listening on that port.".formatted(route.name()));
            } else {
                System.err.println("[%s] this is the real server, dialled through upstreamHost %s; a failure here"
                        .formatted(route.name(), cfg.upstreamHost));
                System.err.println("[%s] means that source address cannot reach it (wrong LAN address?). The current"
                        .formatted(route.name()));
                System.err.println("[%s] game server address is in https://playdarzas.com/api/v1/serverlist"
                        .formatted(route.name()));
            }
        }
    }

    /**
     * Opens the connection to the real server, bound to the configured upstream source.
     *
     * <p>Binding {@link Config#upstreamHost} is what makes the dial leave the host when the
     * destination is claimed locally; leaving it unset is correct only when no route is claimed.
     * Binding {@link Config#upstreamBindPort} as well is only for the packet-level redirect, whose
     * filter excludes that exact port - and because Windows allows one socket per local port,
     * pinned dials are serialized by {@link #UPSTREAM_BIND_LOCK}.
     */
    private static Socket dialUpstream(String host, int port, Config cfg, String tag) throws IOException {
        InetAddress source = cfg.upstreamHost == null ? null : InetAddress.getByName(cfg.upstreamHost);

        if (source == null && cfg.upstreamBindPort == 0) {
            var socket = new Socket();
            socket.connect(new InetSocketAddress(host, port), 10_000);
            return socket;
        }
        if (cfg.upstreamBindPort != 0) {
            synchronized (UPSTREAM_BIND_LOCK) {
                return dialBound(source, cfg.upstreamBindPort, host, port);
            }
        }
        return dialBound(source, 0, host, port);
    }

    private static Socket dialBound(InetAddress source, int localPort, String host, int port) throws IOException {
        var socket = new Socket();
        try {
            socket.bind(new InetSocketAddress(source, localPort));
            socket.connect(new InetSocketAddress(host, port), 10_000);
            return socket;
        } catch (IOException e) {
            closeQuietly(socket);
            throw new IOException("upstream dial from %s:%d to %s:%d failed (%s)".formatted(
                    source == null ? "0.0.0.0" : source.getHostAddress(), localPort, host, port, e.getMessage()), e);
        }
    }

    private static void listen(Route route, Config cfg, SessionRegistry registry, AutoNexus nexus,
                               Strip strip) {
        try (var serverSocket = new ServerSocket(route.listenPort(), 50, InetAddress.getByName(cfg.listenHost))) {
            IO.println("[%s] listening on %s:%d".formatted(route.name(), cfg.listenHost, route.listenPort()));
            while (true) {
                Socket client = serverSocket.accept();
                Thread.startVirtualThread(() -> handleSession(client, route, cfg, registry, nexus, strip));
            }
        } catch (IOException e) {
            System.err.println("[%s] listener failed: %s".formatted(route.name(), e.getMessage()));
        }
    }

    private static void handleSession(Socket client, Route route, Config cfg, SessionRegistry registry,
                                      AutoNexus nexus, Strip strip) {
        long id = registry.nextSessionId();
        String tag = "%s#%d".formatted(route.name(), id);

        String host = route.remoteHost();
        int port = route.remotePort();
        // A GmReconnect-learned destination wins over the configured one: the server, not this file,
        // knows where the session lives now. The configured address stays as the fallback for a dial
        // that arrives before any retarget (and for the queue, which never retargets).
        String learned = cfg.learned.get(route.listenPort());
        if (learned != null) {
            int colon = learned.lastIndexOf(':');
            if (colon > 0) {
                host = learned.substring(0, colon);
                port = Integer.parseInt(learned.substring(colon + 1));
            }
        }
        if (host == null) {
            host = cfg.defaultRemoteHost;
            port = cfg.defaultRemotePort;
        }
        if (host == null) {
            registry.countRejected();
            System.err.println("[%s] no destination configured for port %d; dropping".formatted(tag, route.listenPort()));
            closeQuietly(client);
            return;
        }

        String clientAddr = String.valueOf(client.getRemoteSocketAddress());
        Session session = null;
        try (client; var server = dialUpstream(host, port, cfg, tag)) {
            IO.println("[%s] %s -> %s:%d".formatted(tag, clientAddr, host, port));

            var clientIn = new GameReader(client.getInputStream());
            var clientOut = new GameWriter(client.getOutputStream());
            var serverIn = new GameReader(server.getInputStream());
            var serverOut = new GameWriter(server.getOutputStream());

            // Both directions of one session share the detected byte order, so the reply is
            // framed the same way as the request.
            var framing = new SessionState(route.endianness());

            session = new Session(id, tag, route.name(), route.isQueue(), route.listenPort(),
                    clientAddr, host + ":" + port, registry.log(), nexus,
                    new PlayerLocator(cfg.strip.minVotes()));
            session.upstreamWriter(serverOut);
            registry.register(session);
            session.onConnected();

            // The Session object is what makes the two directions able to see each other; the framing
            // state stays separate because it is about bytes, not about the world.
            var boundSession = session;
            var upstream = Thread.startVirtualThread(
                    () -> pump(clientIn, serverOut, boundSession, Event.DIR_C2S, route, framing, cfg, strip,
                            registry));
            var downstream = Thread.startVirtualThread(
                    () -> pump(serverIn, clientOut, boundSession, Event.DIR_S2C, route, framing, cfg, strip,
                            registry));

            upstream.join();
            downstream.join();
        } catch (Exception e) {
            if (session != null) {
                registry.log().emitError(tag, "session error: " + e.getMessage());
            } else {
                registry.countRejected();
                System.err.println("[%s] session error: %s".formatted(tag, e.getMessage()));
            }
        } finally {
            if (session != null) {
                session.onClosed("relay session ended");
                registry.onSessionClosed(session);
            }
            closeQuietly(client);
        }
    }

    /**
     * Read one framed packet, log it, forward it byte-for-byte.
     *
     * <p>Payloads are never re-encoded in this path. A round-trip through a
     * {@code Packet} implementation can silently corrupt a session if any field is
     * mis-modelled, so the relay only rewrites bytes when a rewriter is installed.
     *
     * <p>The length byte order is detected per session (see {@link Endianness}) because the queue
     * and game servers disagree, and the detected order is then used for both directions of that
     * session. The first frame of each direction is additionally dumped as raw bytes, so a wrong
     * guess is visible in the log rather than having to be inferred.
     */
    private static void pump(GameReader in, GameWriter out, Session world, String dir, Route route,
                             SessionState framing, Config cfg, Strip strip, SessionRegistry registry) {
        boolean firstFrame = true;
        try {
            while (true) {
                byte[] header = new byte[LENGTH_PREFIX];
                in.readFully(header);

                int payloadLength = framing.decodeLength(header, world.tag() + " " + dir, firstFrame);
                if (firstFrame) {
                    world.onEndianness(framing.endianness());
                }
                if (payloadLength < 0 || payloadLength > MAX_PLAUSIBLE_PAYLOAD) {
                    throw new IOException("implausible payload length %d (header %s, endianness %s)"
                            .formatted(payloadLength, printHelperHex(header), framing.endianness));
                }

                byte[] payload = new byte[payloadLength];
                in.readFully(payload);

                if (payload.length > 0) {
                    // Everything in this block is observation and bookkeeping - a decode, an event, a
                    // retarget side effect. None of it decides what the session does, and none of it is
                    // allowed to end one: an exception here must cost a log line, not a live direction.
                    // The bytes below are forwarded from `payload` either way, which is the whole
                    // reason the relay forwards rather than re-encodes.
                    //
                    // This contains per packet rather than per stream. A stream-level catch (which is
                    // all there was before) turns any unexpected RuntimeException anywhere in the
                    // decode/log/rule path into a torn-down session, which is a far worse outcome than
                    // the mis-decode that caused it.
                    try {
                        int packetId = typeId(route.isQueue(), payload);
                        Map<String, Object> decoded = decodeFields(route.isQueue(), packetId, payload);
                        world.onPacket(dir, packetId, payload, EventLog.hex(payload), decoded);
                        registry.countPackets(1);
                        // A retarget has to be acted on - a claim and a learned port - before the packet
                        // is handed over, so the client cannot dial the new address first. Both logs and
                        // this side effect happen while the payload is still ours.
                        onRetarget(world, route, packetId, payload, cfg, registry);
                    } catch (Exception e) {
                        registry.log().emitError(world.tag(),
                                dir + " could not process a packet (forwarding it unchanged): " + explain(e));
                    }
                }

                byte[] rewritten = (strip.config().active() && Event.DIR_S2C.equals(dir))
                        ? rewriteServerPacket(world, route.name(), payload, strip, registry)
                        : null;
                if (rewritten != null) {
                    payload = rewritten;
                    payloadLength = rewritten.length;
                }

                // The same monitor the injection path takes. Both directions of a session therefore
                // serialize on their own writer, which is what keeps a length prefix and its payload
                // contiguous and an injected message from landing inside a forwarded one.
                synchronized (out) {
                    out.write(framing.encodeLength(payloadLength));
                    out.write(payload);
                    out.flush();
                }

                firstFrame = false;
            }
        } catch (EOFException e) {
            registry.log().emitNote(world.tag(), dir + " closed (peer hung up)", null);
        } catch (Exception e) {
            registry.log().emitError(world.tag(), dir + " stream error: " + explain(e));
        }
    }

    /**
     * The one place this relay changes bytes the server sent.
     *
     * <p>Called after the packet has been logged, so the packet event in {@code events-*.jsonl} still
     * carries what the server actually said and the rewrite is a separate, attributable event. It is
     * also the last thing that happens before the frame is written, because dropping a status entry
     * shortens the payload: the 4-byte length prefix has to agree with the new length or every later
     * packet in the session desynchronises. {@code pump} re-frames the returned array with this
     * session's detected byte order, so the rewritten frame is framed exactly like the one it
     * replaces.
     *
     * <p>Everything here is inert unless the strip module has something armed
     * ({@code strip.config().active()}), and nothing here can end a session: a payload it does not
     * understand, a player it cannot name, or any exception at all leaves the original bytes
     * forwardable, which is what the caller falls back to.
     *
     * <p>The byte work itself belongs to {@link networking.packets.StatusStrip} and the module that
     * owns the settings and the counters is {@link Strip}; what stays here is the part that is about
     * this session: naming the local player object and refusing to act while it is unknown.
     *
     * @return the replacement payload, or {@code null} to forward the original unchanged
     */
    private static byte[] rewriteServerPacket(Session world, String routeName, byte[] payload,
                                              Strip strip, SessionRegistry registry) {
        if ("Queue".equalsIgnoreCase(routeName) || payload.length < 2) {
            return null;
        }
        // The id is the payload's first field, little-endian; anything that is not a GmUpdate has no
        // status list to find.
        if ((payload[0] & 0xFF) != (networking.packets.UpdateScan.UPDATE_ID & 0xFF)
                || (payload[1] & 0xFF) != 0) {
            return null;
        }
        try {
            world.player.observe(payload, System.currentTimeMillis());
            // Reported once per world: the whole feature is silent when it cannot name the local
            // player, and a silent no-op is exactly what a first live run has to be able to tell
            // apart from "the debuff never arrived".
            int resolved = world.player.takeNewlyResolved();
            if (resolved > 0) {
                registry.log().emitNote(world.tag(),
                        "local player object resolved to %d: %s".formatted(
                                resolved, world.player.explain()), null);
            }
            int playerId = world.player.playerId();
            if (playerId < 0) {
                return null;
            }
            return strip.rewrite(world, payload, playerId);
        } catch (Exception e) {
            registry.log().emitError(world.tag(),
                    "server->client strip failed (forwarding the original): " + explain(e));
            return null;
        }
    }

    /** Decodes only what a session tracks: registered codecs, plus the input packets' clock field. */
    private static Map<String, Object> decodeFields(boolean queue, int packetId, byte[] payload) {
        Map<String, Object> decoded = new LinkedHashMap<>(Injection.decode(queue, packetId, payload));
        if (!queue) {
            Map<String, Object> client = networking.packets.ClientPackets.decode(packetId, payload);
            if (!client.isEmpty()) {
                decoded.putAll(client);
            }
        }
        return decoded;
    }

    /**
     * Bytes of type prefix a service writes before its fields.
     *
     * <p>The two services do not agree, and it is not obvious from the wire: {@code
     * GmPacket.GetData()} writes {@code TypeId}, a <em>ushort</em>, so a game payload starts with two
     * little-endian type bytes, while {@code QPacket.GetData()} writes {@code Id}, a single byte.
     */
    static int bodyOffset(boolean queue) {
        return queue ? 1 : 2;
    }

    /**
     * The packet id, read the way the owning service writes it.
     *
     * <p>Reading {@code payload[0]} alone happens to be right for every game id below 256 - which is
     * all of them except JumpScare (320) - and that is exactly why the 2-byte game type stayed
     * hidden until a {@code GmReconnect} payload failed to decode.
     */
    static int typeId(boolean queue, byte[] payload) {
        if (payload.length == 0) {
            return -1;
        }
        int low = payload[0] & 0xFF;
        if (queue || payload.length < 2) {
            return low;
        }
        return low | ((payload[1] & 0xFF) << 8);
    }

    /**
     * Handles the one packet that moves a session: {@code GmReconnect} (game id 36, server to client).
     *
     * <p>It carries the host and port the server wants this client to dial next. Under an address
     * claim that makes it the single place a session can silently leave the proxy, because the client
     * dials the new address itself. So before this packet is handed on - the write happens after this
     * method returns, which closes the race completely - the relay:
     *
     * <ol>
     *   <li>claims the named address on the loopback pseudo-interface, so the client's dial to it is
     *       delivered here instead of escaping to the real server;</li>
     *   <li>records it as the destination for that port, so the listener that accepts the follow-up
     *       connection forwards to the address the server just named.</li>
     * </ol>
     *
     * <p>All of it is best effort. If the claim fails - an unelevated relay cannot claim anything -
     * the packet is still forwarded and the failure is reported, because dropping it would break the
     * client for no gain.
     */
    private static void onRetarget(Session world, Route route, int packetId, byte[] payload, Config cfg,
                                   SessionRegistry registry) {
        if (route.isQueue() || packetId != GmPacketType.RECONNECT) {
            return;
        }
        String tag = world.tag();
        int offset = bodyOffset(false);
        if (payload.length <= offset) {
            return;
        }

        ReconnectPacket reconnect;
        try {
            reconnect = new ReconnectPacket();
            reconnect.read(new GameReader(new ByteArrayInputStream(payload, offset, payload.length - offset)));
        } catch (Exception e) {
            registry.log().emitNote(tag, "Reconnect payload did not decode: " + explain(e), null);
            return;
        }
        if (reconnect.host == null || reconnect.host.isBlank() || reconnect.port <= 0) {
            registry.log().emitNote(tag, "Reconnect carried no usable target",
                    Map.of("host", String.valueOf(reconnect.host), "port", reconnect.port));
            return;
        }

        boolean claimed = ensureClaimed(reconnect.host, tag);
        cfg.learned.put(reconnect.port, reconnect.host + ":" + reconnect.port);
        // Both lines are intentional. The first is the format the operator and the existing offline
        // suite read; the second is the one that actually matters for correctness, because "learned
        // the destination" is what makes the client's follow-up dial arrive here instead of escaping.
        registry.log().writeTextLine("[%s]   RETARGET -> %s:%d  characterId=%d  toBeyond=%s".formatted(
                tag, reconnect.host, reconnect.port, reconnect.characterId, reconnect.toBeyond));
        registry.log().writeTextLine("[%s]   port %d now forwards to %s:%d%s".formatted(
                tag, reconnect.port, reconnect.host, reconnect.port,
                claimed ? "" : " (its address is NOT claimed, so that dial will not arrive here)"));
        registry.log().emit(Event.builder(Event.KIND_WORLD)
                .session(tag)
                .pkt("Reconnect")
                .pktId(GmPacketType.RECONNECT)
                .note("RETARGET -> %s:%d%s".formatted(reconnect.host, reconnect.port,
                        claimed ? "" : " (address NOT claimed, so that dial will not arrive here)"))
                .put("host", reconnect.host)
                .put("port", reconnect.port)
                .put("characterId", reconnect.characterId)
                .put("toBeyond", reconnect.toBeyond)
                .put("claimed", claimed));
    }

    /** A message even for the exceptions that carry none, such as {@code EOFException}. */
    private static String explain(Exception e) {
        String message = e.getMessage();
        return message == null || message.isBlank() ? e.getClass().getSimpleName() : message;
    }

    /**
     * Claims {@code host} on the loopback pseudo-interface, so that a dial to it is delivered locally.
     *
     * <p>Uses {@code netsh ... store=active}, matching the {@code -SessionOnly} default of
     * {@code tools/Set-AddressClaim.ps1}: the claim sits in the active store only, so a reboot clears
     * it and nothing persistent is modified. It refuses an address a real interface owns - that is how
     * a machine loses its network - and waits for the address to appear, because a claim is only
     * useful once duplicate-address detection has finished.
     *
     * <p>It does <em>not</em> re-run the startup escape probe: that probe needs a trap listener on
     * the claimed address, and this relay is already listening on the port. The escape itself is the
     * measured property of a loopback claim dialled from a physical source, which is what
     * {@code preflightClaimed} proves at startup for every configured claim.
     */
    private static boolean ensureClaimed(String host, String tag) {
        InetAddress address;
        try {
            address = InetAddress.getByName(host);
        } catch (Exception e) {
            System.err.println("[%s]   cannot resolve %s to claim it: %s".formatted(tag, host, explain(e)));
            return false;
        }
        if (!(address instanceof Inet4Address)) {
            System.err.println("[%s]   %s is not an IPv4 address; only IPv4 claims are supported".formatted(tag, host));
            return false;
        }
        if (address.isLoopbackAddress()) {
            return true;
        }
        String ip = address.getHostAddress();
        if (LEARNED_CLAIMS.contains(ip)) {
            return true;
        }

        NetworkInterface owner = owningInterface(address);
        if (owner != null && !isLoopback(owner)) {
            System.err.println("[%s]   refusing to claim %s: it belongs to '%s', a real interface"
                    .formatted(tag, ip, owner.getName()));
            return false;
        }

        int index = loopbackIndex();
        if (index < 0) {
            System.err.println("[%s]   no loopback interface found; cannot claim %s".formatted(tag, ip));
            return false;
        }

        if (runNetsh(tag, "add", String.valueOf(index), ip, "255.255.255.255", "store=active") != 0) {
            System.err.println("[%s]   could not claim %s. An unelevated relay cannot claim anything: start it"
                    .formatted(tag, ip));
            System.err.println("[%s]   through the launcher (java -jar drelay.jar), which asks Windows for the"
                    .formatted(tag));
            System.err.println("[%s]   administrator token before starting this process. Otherwise the client"
                    .formatted(tag));
            System.err.println("[%s]   dials %s directly and this session leaves the proxy.".formatted(tag, ip));
            return false;
        }

        LEARNED_CLAIMS.add(ip);
        writeLearnedClaims();

        for (int i = 0; i < 50 && owningInterface(address) == null; i++) {
            try {
                Thread.sleep(100);
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                break;
            }
        }
        boolean usable = owningInterface(address) != null;
        IO.println("[%s]   claimed %s on the loopback interface%s".formatted(tag, ip, usable ? "" : " (not usable yet)"));
        return usable;
    }

    /** @return the interface index of the loopback pseudo-interface, or -1 when it cannot be found. */
    private static int loopbackIndex() {
        try {
            var interfaces = NetworkInterface.getNetworkInterfaces();
            while (interfaces.hasMoreElements()) {
                NetworkInterface networkInterface = interfaces.nextElement();
                if (networkInterface.isLoopback()) {
                    return networkInterface.getIndex();
                }
            }
        } catch (Exception ignored) {
            // best effort; the caller reports the failure
        }
        return -1;
    }

    /** {@code isLoopback} throws, and a lookup failure must not decide the answer. */
    private static boolean isLoopback(NetworkInterface networkInterface) {
        try {
            return networkInterface.isLoopback();
        } catch (Exception e) {
            return false;
        }
    }

    /** Runs one netsh address operation, echoing whatever it says so a refusal is diagnosable. */
    private static int runNetsh(String tag, String action, String... arguments) {
        List<String> command = new ArrayList<>(List.of("netsh", "interface", "ipv4", action, "address"));
        command.addAll(List.of(arguments));
        try {
            Process process = new ProcessBuilder(command).redirectErrorStream(true).start();
            String output = new String(process.getInputStream().readAllBytes(), StandardCharsets.UTF_8).trim();
            if (!process.waitFor(20, TimeUnit.SECONDS)) {
                process.destroyForcibly();
                System.err.println("[%s]   netsh %s timed out".formatted(tag, action));
                return -1;
            }
            if (process.exitValue() != 0 && !output.isBlank()) {
                System.err.println("[%s]   netsh: %s".formatted(tag, output));
            }
            return process.exitValue();
        } catch (Exception e) {
            System.err.println("[%s]   netsh %s failed: %s".formatted(tag, action, explain(e)));
            return -1;
        }
    }

    /** Records the runtime claims where a human - or stop-elevated.ps1 - can find them. */
    private static void writeLearnedClaims() {
        try {
            Files.createDirectories(LEARNED_CLAIMS_FILE.getParent());
            Files.write(LEARNED_CLAIMS_FILE, LEARNED_CLAIMS);
        } catch (Exception ignored) {
            // recording is a convenience; never let it disturb a session
        }
    }

    /** Gives back every address this relay claimed at runtime. */
    private static void releaseLearnedClaims() {
        int count = LEARNED_CLAIMS.size();
        if (count == 0) {
            return;
        }
        int index = loopbackIndex();
        for (String ip : LEARNED_CLAIMS) {
            if (index >= 0) {
                runNetsh("relay", "delete", String.valueOf(index), ip, "store=active");
            }
        }
        LEARNED_CLAIMS.clear();
        writeLearnedClaims();
        IO.println("released " + count + " runtime claim(s)");
    }

    /**
     * Per-session length byte order.
     *
     * <p>Starts from the route's configured order, or {@link Endianness#AUTO} to decide from the
     * first header. A wrong guess produces an absurd value (8 read big-endian when the sender meant
     * little-endian becomes 134217728), so detection is a simple plausibility comparison. Once
     * resolved it is reused for every later frame and for the write path, so both directions of a
     * session stay consistent.
     */
    static final class SessionState {
        private Endianness endianness;

        SessionState(Endianness endianness) {
            this.endianness = endianness;
        }

        synchronized int decodeLength(byte[] header, String tag, boolean firstFrame) throws IOException {
            if (endianness == Endianness.AUTO) {
                int big = readBig(header);
                int little = readLittle(header);
                boolean bigOk = big >= 0 && big <= MAX_PLAUSIBLE_PAYLOAD;
                boolean littleOk = little >= 0 && little <= MAX_PLAUSIBLE_PAYLOAD;

                if (bigOk && !littleOk) {
                    endianness = Endianness.BIG;
                } else if (littleOk && !bigOk) {
                    endianness = Endianness.LITTLE;
                } else if (bigOk) {
                    // Both plausible (small values look fine either way) - assume big-endian,
                    // which is what the game service uses, and say so.
                    endianness = Endianness.BIG;
                } else {
                    throw new IOException("could not determine length byte order from header %s"
                            .formatted(printHelperHex(header)));
                }
                IO.println("[%s] length byte order detected: %s (header %s)".formatted(
                        tag, endianness, printHelperHex(header)));
            }

            int value = endianness == Endianness.BIG ? readBig(header) : readLittle(header);
            if (firstFrame) {
                IO.println("[%s] first header raw=%s -> %s length %d".formatted(
                        tag, printHelperHex(header), endianness, value));
            }
            return value;
        }

        byte[] encodeLength(int payloadLength) {
            Endianness order = endianness == Endianness.AUTO ? Endianness.BIG : endianness;
            byte[] out = new byte[LENGTH_PREFIX];
            if (order == Endianness.BIG) {
                out[0] = (byte) (payloadLength >>> 24);
                out[1] = (byte) (payloadLength >>> 16);
                out[2] = (byte) (payloadLength >>> 8);
                out[3] = (byte) payloadLength;
            } else {
                out[0] = (byte) payloadLength;
                out[1] = (byte) (payloadLength >>> 8);
                out[2] = (byte) (payloadLength >>> 16);
                out[3] = (byte) (payloadLength >>> 24);
            }
            return out;
        }

        Endianness endianness() {
            return endianness;
        }

        private static int readBig(byte[] h) {
            return ((h[0] & 0xFF) << 24) | ((h[1] & 0xFF) << 16) | ((h[2] & 0xFF) << 8) | (h[3] & 0xFF);
        }

        private static int readLittle(byte[] h) {
            return ((h[3] & 0xFF) << 24) | ((h[2] & 0xFF) << 16) | ((h[1] & 0xFF) << 8) | (h[0] & 0xFF);
        }
    }

    private static String printHelperHex(byte[] data) {
        var sb = new StringBuilder(data.length * 2);
        for (byte b : data) {
            sb.append(String.format(Locale.ROOT, "%02X", b));
        }
        return sb.toString();
    }

    private static void closeQuietly(Socket socket) {
        try {
            socket.close();
        } catch (IOException ignored) {
        }
    }
}
