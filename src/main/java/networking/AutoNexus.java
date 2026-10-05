package networking;

import networking.log.Event;
import networking.log.EventLog;
import networking.log.LogClock;
import networking.packets.GmPacketType;
import networking.packets.Injection;
import networking.util.Fields;
import networking.util.Json;

import java.io.OutputStream;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ConcurrentLinkedDeque;
import java.util.concurrent.atomic.AtomicLong;

/**
 * The auto-nexus rule: watch the player's HP and inject {@code GmEscape} before the next hit lands.
 *
 * <h2>What it can and cannot be</h2>
 *
 * <p>This is <strong>reactive</strong>, and that is a property of the game rather than a shortcut.
 * The usual trick - predict the incoming hit's damage and escape before it applies - needs the client
 * to acknowledge damage so the acknowledgement can be suppressed; RotMG does that and Darza does not
 * (the capture shows no damage ack: {@code ProjectilesAck} is positional, and {@code AllyHit} /
 * {@code ProjHit} are sparse and optional). So the only truthful signal is the server's own
 * {@code HealthUpdate}, at ~10 Hz. A single burst that removes the rest of the bar between two health
 * packets cannot be escaped, and no amount of proxy logic changes that.
 *
 * <p>What the rule therefore optimises is <em>latency after a health reading</em>. The decision is
 * taken on the same virtual thread that just read the packet and the five bytes are written before
 * that packet is forwarded onward, so the escape leaves the relay within microseconds of the HP
 * reading arriving - measured in the log as {@code decisionLatencyMs}.
 *
 * <h2>The safety limits, and why each one exists</h2>
 *
 * <ul>
 *   <li><strong>World gate.</strong> Nothing is injected until the client's {@code Hello} and the
 *       server's {@code MapInfo} have both been seen; see {@link Session}.</li>
 *   <li><strong>Health required.</strong> A percentage cannot be computed before the first
 *       {@code HealthUpdate} of a world, and firing on a stale percentage from the previous world is
 *       exactly the bug that kills a character on entry.</li>
 *   <li><strong>Alive required.</strong> {@code Health == 0} means the button is dead or the player is
 *       in spirit form; an escape there is at best ignored.</li>
 *   <li><strong>Per-world budget and a minimum interval.</strong> The server accepts repeated escapes
 *       (the client itself sends a second request 140 ms after the first when the first appears to do
 *       nothing), so a proxy that fires on every low reading would spam it. The budget is the hard
 *       stop; the interval is the soft one.</li>
 *   <li><strong>Dry run.</strong> The rule runs, decides and logs, and writes nothing. This is how the
 *       threshold is tuned against a real session without risking the character.</li>
 * </ul>
 *
 * <h2>Threshold maths</h2>
 *
 * <p>{@code Health / MaxHealth} is the live HP bar, but a hit can be absorbed by shield and barrier
 * first, so the effective pool is {@code Health + Shield + Barrier} against {@code MaxHealth}. Both
 * are computed and both are logged; {@link Config#useEffectiveHp} chooses which one trips the rule.
 * {@code MaxHealth} of 0 or less means the server has not told us a maximum yet, and every comparison
 * declines rather than treating the player as dead.
 */
public final class AutoNexus {

    /** Everything tunable, all of it overridable from the route table or the dashboard. */
    public static final class Config {
        /** Master switch. Off means the rule is not even consulted. */
        public volatile boolean enabled;
        /** Operate but write nothing; the decisions still appear in the log and the dashboard. */
        public volatile boolean dryRun = true;
        /** Trip below this percentage of HP (or effective HP). 0 disables the rule without disabling the switch. */
        public volatile int thresholdPercent = 35;
        /** Include shield and barrier in the pool being compared against the threshold. */
        public volatile boolean useEffectiveHp;
        /** Declines to fire while the server says the area is safe. */
        public volatile boolean skipInSafeArea = true;
        /** Minimum milliseconds between two injected escapes in the same session. */
        public volatile long minIntervalMillis = 1200;
        /** Maximum injected escapes per world entry. */
        public volatile int maxPerWorld = 3;
        /**
         * Optional re-arm level: after firing, do not consider firing again until HP has been at or
         * above this percentage. 0 leaves the rule armed continuously (the interval and budget still
         * apply), which is the honest default: re-arming is a preference, not a safety property.
         */
        public volatile int rearmPercent;
        /**
         * Imitate the channelled escape (used when the character carries the {@code Impermanence}
         * totem) by sending {@code EscapeCastState{Casting=true}}, waiting {@link #castMillis}, then
         * the escape. Off by default: the instant form is byte-identical to what the client sends in
         * every non-channelled situation, and therefore proven acceptable to the server.
         */
        public volatile boolean useCastChannel;
        /** Wait applied between the cast-state packet and the escape when {@link #useCastChannel} is on. */
        public volatile long castMillis = 2000;

        public Config copy() {
            Config copy = new Config();
            copy.enabled = enabled;
            copy.dryRun = dryRun;
            copy.thresholdPercent = thresholdPercent;
            copy.useEffectiveHp = useEffectiveHp;
            copy.skipInSafeArea = skipInSafeArea;
            copy.minIntervalMillis = minIntervalMillis;
            copy.maxPerWorld = maxPerWorld;
            copy.rearmPercent = rearmPercent;
            copy.useCastChannel = useCastChannel;
            copy.castMillis = castMillis;
            return copy;
        }

        public Map<String, Object> toMap() {
            return Fields.of()
                    .add("enabled", enabled)
                    .add("dryRun", dryRun)
                    .add("thresholdPercent", thresholdPercent)
                    .add("useEffectiveHp", useEffectiveHp)
                    .add("skipInSafeArea", skipInSafeArea)
                    .add("minIntervalMillis", minIntervalMillis)
                    .add("maxPerWorld", maxPerWorld)
                    .add("rearmPercent", rearmPercent)
                    .add("useCastChannel", useCastChannel)
                    .add("castMillis", castMillis);
        }

        public String toJson() {
            return Json.value(toMap());
        }

        /**
         * Applies a sparse set of overrides.
         *
         * <p>Field names are lower-cased and {@code _} is folded to {@code -}, so the dashboard can
         * post {@code {"threshold_percent": 40}} or {@code {"thresholdPercent": 40}} and mean the same
         * thing. Values are validated here rather than at use: a threshold of 500 or a negative
         * interval would otherwise become a rule that either never fires or fires forever.
         */
        public synchronized List<String> apply(Map<String, ?> changes) {
            List<String> applied = new ArrayList<>();
            for (Map.Entry<String, ?> raw : changes.entrySet()) {
                String key = raw.getKey().toLowerCase(java.util.Locale.ROOT).replace('_', '-');
                Object value = raw.getValue();
                switch (key) {
                    case "enabled" -> {
                        enabled = bool(value, enabled);
                        applied.add("enabled=" + enabled);
                    }
                    case "dryrun", "dry-run" -> {
                        dryRun = bool(value, dryRun);
                        applied.add("dryRun=" + dryRun);
                    }
                    case "threshold", "thresholdpercent", "threshold-percent",
                         "nexuspercent", "nexus-percent", "hp-percent" -> {
                        thresholdPercent = clampInt(value, thresholdPercent, 0, 100);
                        applied.add("thresholdPercent=" + thresholdPercent);
                    }
                    case "useeffectivehp", "use-effective-hp", "effective" -> {
                        useEffectiveHp = bool(value, useEffectiveHp);
                        applied.add("useEffectiveHp=" + useEffectiveHp);
                    }
                    case "skipinsafearea", "skip-in-safe-area" -> {
                        skipInSafeArea = bool(value, skipInSafeArea);
                        applied.add("skipInSafeArea=" + skipInSafeArea);
                    }
                    case "minintervalmillis", "min-interval-millis", "delay", "delayms" -> {
                        minIntervalMillis = clampLong(value, minIntervalMillis, 0, 600_000);
                        applied.add("minIntervalMillis=" + minIntervalMillis);
                    }
                    case "maxperworld", "max-per-world", "maxattempts", "max-attempts" -> {
                        maxPerWorld = clampInt(value, maxPerWorld, 0, 1000);
                        applied.add("maxPerWorld=" + maxPerWorld);
                    }
                    case "rearmpercent", "rearm-percent" -> {
                        rearmPercent = clampInt(value, rearmPercent, 0, 100);
                        applied.add("rearmPercent=" + rearmPercent);
                    }
                    case "usecastchannel", "use-cast-channel" -> {
                        useCastChannel = bool(value, useCastChannel);
                        applied.add("useCastChannel=" + useCastChannel);
                    }
                    case "castmillis", "cast-millis" -> {
                        castMillis = clampLong(value, castMillis, 0, 60_000);
                        applied.add("castMillis=" + castMillis);
                    }
                    default -> {
                        // An unknown key is reported, not ignored: a typo in the dashboard would
                        // otherwise look like a setting that simply had no effect.
                        applied.add("unknown:" + raw.getKey());
                    }
                }
            }
            return applied;
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

        private static int clampInt(Object value, int fallback, int min, int max) {
            return (int) clampLong(value, fallback, min, max);
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

    /** One health reading, as the rule sees it. */
    public record Reading(int maxHealth, int health, int shield, int barrier) {
        public int pool(boolean effective) {
            return effective ? health + Math.max(0, shield) + Math.max(0, barrier) : health;
        }

        public int percent(boolean effective) {
            if (maxHealth <= 0) {
                return -1;
            }
            return (int) Math.round(100.0 * pool(effective) / maxHealth);
        }
    }

    /** An escape that has been written and whose acknowledgement has not arrived yet. */
    public record PendingInjection(long sessionId, long seq, long monoMillis, int hpPercent, String reason) {
        public long latencyMillis() {
            return LogClock.monoMillis() - monoMillis;
        }
    }

    /** What the rule decided, with the numbers behind the decision. */
    public record Decision(boolean fire, String action, String reason, int percent, Reading reading,
                           long sinceLastMillis, int injectionsThisWorld) {
        public String toNote() {
            return "%s: %s (%s)".formatted(action, reason, percent < 0 ? "no health yet" : percent + "%");
        }
    }

    private final Config config;
    private final EventLog log;
    private final AtomicLong decisions = new AtomicLong();
    private final AtomicLong fires = new AtomicLong();
    private final AtomicLong dryRuns = new AtomicLong();
    private final AtomicLong declines = new AtomicLong();

    /**
     * Injections awaiting a server verdict, <strong>keyed by session</strong>.
     *
     * <p>Not one queue for the whole relay. The realm flow makes that a real mistake rather than a
     * theoretical one: the client injects an escape whose ack never arrives, the server retargets it
     * to port 6411, and a fresh session opens. A single queue would match the <em>new</em> session's
     * first ack to the <em>old</em> session's injection, and the log would report the wrong sequence
     * number, the wrong latency and the wrong verdict - corrupting exactly the evidence a reader uses
     * to decide whether the instant escape works or the channelled one is needed.
     *
     * <p>Per session, an ack can only answer an escape this relay sent into <em>that</em> stream, which
     * is also what makes "the client sent this one itself" detectable: no pending entry means no
     * injection to attribute it to.
     */
    private final Map<Long, ConcurrentLinkedDeque<PendingInjection>> pending =
            new ConcurrentHashMap<>();
    private final ConcurrentLinkedDeque<Map<String, Object>> acks = new ConcurrentLinkedDeque<>();

    private volatile boolean lastAckSuccess;
    private volatile String lastAckNote = "no escape has been acknowledged yet";

    public AutoNexus(Config config, EventLog log) {
        this.config = config;
        this.log = log;
    }

    public Config config() {
        return config;
    }

    /** Called on every world entry; resets the per-world budget and health-dependent arming state. */
    public void onWorldEntry(Session session, Object playerId) {
        synchronized (session) {
            scratch(session).reset();
        }
        emit(Event.builder(Event.KIND_NEXUS)
                .session(session.tag())
                .note("armed for a new world: threshold %d%%, budget %d, interval %d ms, %s"
                        .formatted(config.thresholdPercent, config.maxPerWorld, config.minIntervalMillis,
                                config.dryRun ? "DRY RUN" : "live"))
                .put("playerId", playerId)
                .put("config", config.toMap()));
    }

    /**
     * Called on every {@code HealthUpdate}, on the thread that read it.
     *
     * <p>Everything below is deliberately cheap and non-blocking except the actual five-byte write,
     * which is the point of doing it here: the escape leaves before the health packet is forwarded
     * onward, so the client's own view cannot race ahead of the decision.
     */
    public void onHealth(Session session, Reading reading) {
        if (!config.enabled) {
            return;
        }
        Decision decision = decide(session, reading);
        decisions.incrementAndGet();
        if (!decision.fire()) {
            declines.incrementAndGet();
            // A declining decision is logged at health-packet rate, so it is only recorded when it
            // says something new; otherwise the log becomes 10 identical lines per second.
            maybeLogDecline(session, decision);
            return;
        }

        if (config.dryRun) {
            dryRuns.incrementAndGet();
            emit(Event.builder(Event.KIND_NEXUS)
                    .session(session.tag())
                    .note("DRY RUN would nexus: " + decision.reason())
                    .put("hpPercent", decision.percent())
                    .put("reading", readingJson(reading))
                    .put("thresholdPercent", config.thresholdPercent)
                    .put("injectionsThisWorld", decision.injectionsThisWorld()));
            return;
        }

        fire(session, decision, reading);
    }

    /** The pure decision, separated from the action so it can be tested without a socket. */
    public Decision decide(Session session, Reading reading) {
        Scratch scratch = scratch(session);
        long now = LogClock.monoMillis();
        int percent = reading.percent(config.useEffectiveHp);

        if (!config.enabled) {
            return new Decision(false, "decline", "auto-nexus is disabled", percent, reading, -1, 0);
        }
        if (!session.injectionReady()) {
            return new Decision(false, "decline", "injection is not armed (no world yet, or a fresh handshake)", percent,
                    reading, -1, scratch.injectionsThisWorld);
        }
        if (!session.hasHealth()) {
            return new Decision(false, "decline", "no HealthUpdate seen in this world", percent, reading, -1,
                    scratch.injectionsThisWorld);
        }
        if (reading.maxHealth() <= 0) {
            return new Decision(false, "decline", "the server has not reported a maximum HP", percent, reading, -1,
                    scratch.injectionsThisWorld);
        }
        if (reading.health() <= 0) {
            return new Decision(false, "decline", "HP is zero (dead or spirit form)", percent, reading, -1,
                    scratch.injectionsThisWorld);
        }
        if (config.skipInSafeArea && session.isSafeArea()) {
            return new Decision(false, "decline", "the server says this is a safe area", percent, reading, -1,
                    scratch.injectionsThisWorld);
        }
        long sinceLast = scratch.lastInjectionMono < 0 ? Long.MAX_VALUE : now - scratch.lastInjectionMono;
        if (scratch.injectionsThisWorld >= config.maxPerWorld) {
            return new Decision(false, "decline",
                    "world budget spent (%d/%d)".formatted(scratch.injectionsThisWorld, config.maxPerWorld),
                    percent, reading, sinceLast, scratch.injectionsThisWorld);
        }
        if (sinceLast < config.minIntervalMillis) {
            return new Decision(false, "decline",
                    "only %d ms since the last injection".formatted(sinceLast),
                    percent, reading, sinceLast, scratch.injectionsThisWorld);
        }
        if (config.rearmPercent > 0 && scratch.needsRearm && percent < config.rearmPercent) {
            return new Decision(false, "decline",
                    "waiting to re-arm (needs %d%%, at %d%%)".formatted(config.rearmPercent, percent),
                    percent, reading, sinceLast, scratch.injectionsThisWorld);
        }
        if (config.thresholdPercent <= 0) {
            return new Decision(false, "decline", "threshold is 0, so the rule is off", percent, reading, sinceLast,
                    scratch.injectionsThisWorld);
        }
        if (percent < 0) {
            return new Decision(false, "decline", "health is not measurable yet", percent, reading, sinceLast,
                    scratch.injectionsThisWorld);
        }
        if (percent >= config.thresholdPercent) {
            if (config.rearmPercent > 0 && scratch.needsRearm && percent >= config.rearmPercent) {
                scratch.needsRearm = false;
            }
            return new Decision(false, "decline",
                    "%d%% is not below the %d%% threshold".formatted(percent, config.thresholdPercent),
                    percent, reading, sinceLast, scratch.injectionsThisWorld);
        }

        String reason = "%d%% %s is below the %d%% threshold".formatted(
                percent, config.useEffectiveHp ? "(hp+shield+barrier)" : "HP", config.thresholdPercent);
        return new Decision(true, "nexus", reason, percent, reading, sinceLast, scratch.injectionsThisWorld);
    }

    private void fire(Session session, Decision decision, Reading reading) {
        OutputStream upstream = session.upstreamWriter();
        if (upstream == null) {
            emit(Event.builder(Event.KIND_ERROR)
                    .session(session.tag())
                    .note("cannot nexus: the upstream stream is not available"));
            return;
        }
        Scratch scratch = scratch(session);
        long mono = LogClock.monoMillis();
        long wall = System.currentTimeMillis();

        // The event that explains the whole feature's timing: how long after the health reading the
        // decision was taken, and what the reading actually was.
        long decisionLatency = session.millisSinceLastHealth();
        boolean bigEndian = session.frameBigEndian();
        synchronized (upstream) {
            if (config.useCastChannel) {
                long castSeq = session.inject(upstream, "EscapeCastState",
                        Injection.escapeCastState(true, bigEndian), "channelled escape, casting=true");
                if (castSeq < 0) {
                    // The cast-state packet is what tells the server a channel has begun. Sending the
                    // escape without it would imitate the wrong gesture, and sending it and then
                    // failing the escape would leave the server thinking a cast is open - so neither
                    // proceeds on a failed cast-state write.
                    emit(Event.builder(Event.KIND_ERROR)
                            .session(session.tag())
                            .note("channelled nexus abandoned: the EscapeCastState write failed"));
                    return;
                }
                // Deliberately inside the lock: the cast-state packet must reach the server before the
                // escape, and the body of the channelled escape is exactly that gap.
                //
                // The cost is real and worth stating precisely, because an earlier comment here
                // understated it: this thread IS the downstream pump, and the monitor it holds is the
                // one the upstream pump writes under. So for castMillis - 2000 ms by default, up to
                // 60 s as configured - BOTH directions stall: server->client traffic stops, and so
                // does every client input packet. Nothing is lost (both resume, and the escape has
                // already gone out ahead of the pause), but the client visibly freezes for the gap.
                //
                // That is why the instant form is the default and this is opt-in: the channelled
                // escape exists only for the case where the server refuses the instant one, and a
                // freeze is a far better failure than a refusal if that case is real.
                try {
                    Thread.sleep(config.castMillis);
                } catch (InterruptedException e) {
                    Thread.currentThread().interrupt();
                }
            }
            long seq = session.inject(upstream, "Escape", Injection.escape(bigEndian),
                    "auto-nexus at %d%% hp".formatted(decision.percent()));
            if (seq < 0) {
                return;
            }
            scratch.injectionsThisWorld++;
            scratch.lastInjectionMono = mono;
            scratch.needsRearm = config.rearmPercent > 0;
            ConcurrentLinkedDeque<PendingInjection> mine =
                    pending.computeIfAbsent(session.id, key -> new ConcurrentLinkedDeque<>());
            mine.addLast(new PendingInjection(session.id, seq, mono, decision.percent(), decision.reason()));
            while (mine.size() > 16) {
                mine.pollFirst();
            }
        }
        fires.incrementAndGet();
        emit(Event.builder(Event.KIND_NEXUS)
                .at(wall, mono)
                .note("NEXUS: " + decision.reason())
                .put("hpPercent", decision.percent())
                .put("reading", readingJson(reading))
                .put("thresholdPercent", config.thresholdPercent)
                .put("useEffectiveHp", config.useEffectiveHp)
                .put("injectionsThisWorld", scratch.injectionsThisWorld)
                .put("maxPerWorld", config.maxPerWorld)
                .put("decisionLatencyMs", decisionLatency)
                .put("sinceLastInjectionMs", decision.sinceLastMillis() == Long.MAX_VALUE ? null : decision.sinceLastMillis())
                .put("castChannel", config.useCastChannel));
    }

    /**
     * Matches a server acknowledgement to the injection it answers <em>in this session</em>.
     *
     * <p>The session is not decorative: an ack can only answer an escape this relay wrote into that
     * session's stream. An ack with nothing pending for this session is therefore the client's own
     * escape, and is reported as such rather than attributed to whichever injection happens to be
     * oldest in the relay.
     */
    public PendingInjection onEscapeAck(Session session, boolean success) {
        ConcurrentLinkedDeque<PendingInjection> mine = pending.get(session.id);
        PendingInjection matched = mine == null ? null : mine.pollFirst();
        lastAckSuccess = success;
        lastAckNote = matched == null
                ? (success ? "an escape was accepted (no injection pending in this session: the client sent it)"
                           : "an escape was refused (no injection pending in this session)")
                : (success
                        ? "injected escape #%d accepted after %d ms".formatted(matched.seq(), matched.latencyMillis())
                        : "injected escape #%d REFUSED after %d ms".formatted(matched.seq(), matched.latencyMillis()));
        Map<String, Object> ack = new java.util.LinkedHashMap<>();
        ack.put("success", success);
        ack.put("at", LogClock.iso(System.currentTimeMillis()));
        ack.put("note", lastAckNote);
        if (matched != null) {
            ack.put("injectionSeq", matched.seq());
            ack.put("latencyMs", matched.latencyMillis());
            ack.put("hpPercent", matched.hpPercent());
        }
        acks.addLast(ack);
        while (acks.size() > 32) {
            acks.pollFirst();
        }
        return matched;
    }

    /** A declining decision is only logged when the reason changes, to keep the log readable. */
    private void maybeLogDecline(Session session, Decision decision) {
        Scratch scratch = scratch(session);
        if (decision.reason().equals(scratch.lastDeclineReason)) {
            return;
        }
        scratch.lastDeclineReason = decision.reason();
        emit(Event.builder(Event.KIND_NEXUS)
                .session(session.tag())
                .note("no nexus: " + decision.reason())
                .put("hpPercent", decision.percent() < 0 ? null : decision.percent())
                .put("reading", readingJson(decision.reading()))
                .put("thresholdPercent", config.thresholdPercent)
                .put("injectionsThisWorld", decision.injectionsThisWorld()));
    }

    private static Map<String, Object> readingJson(Reading reading) {
        return Fields.of()
                .add("maxHealth", reading.maxHealth())
                .add("health", reading.health())
                .add("shield", reading.shield())
                .add("barrier", reading.barrier());
    }

    private Event emit(Event.Builder builder) {
        return log.emit(builder);
    }

    // --- per-session scratch ------------------------------------------------------------------

    /** Per-session mutable state; kept out of {@link Session} so the rule owns its own bookkeeping. */
    static final class Scratch {
        int injectionsThisWorld;
        long lastInjectionMono = -1;
        boolean needsRearm;
        String lastDeclineReason;

        void reset() {
            injectionsThisWorld = 0;
            lastInjectionMono = -1;
            needsRearm = false;
            lastDeclineReason = null;
        }
    }

    private final java.util.Map<Long, Scratch> scratches = new java.util.concurrent.ConcurrentHashMap<>();

    private Scratch scratch(Session session) {
        return scratches.computeIfAbsent(session.id, key -> new Scratch());
    }

    /**
     * Releases the per-session state when a session ends, so a long run does not accumulate it.
     *
     * <p>This includes the pending-injection queue. Leaving it behind was the other half of the
     * global-ack bug: a finished session's unacknowledged injection stayed at the head of the relay's
     * queue for the next session to be blamed for.
     */
    public void forget(Session session) {
        scratches.remove(session.id);
        pending.remove(session.id);
    }

    // --- dashboard view ----------------------------------------------------------------------

    public Map<String, Object> toMap() {
        List<Map<String, Object>> pendingView = new ArrayList<>();
        pending.forEach((sessionId, queue) -> {
            for (PendingInjection injection : queue) {
                pendingView.add(Fields.of()
                        .add("session", sessionId)
                        .add("seq", injection.seq())
                        .add("ageMs", injection.latencyMillis())
                        .add("hpPercent", injection.hpPercent())
                        .add("reason", injection.reason()));
            }
        });
        return Fields.of()
                .add("config", config.toMap())
                .add("decisions", decisions.get())
                .add("fires", fires.get())
                .add("dryRuns", dryRuns.get())
                .add("declines", declines.get())
                .add("lastAckSuccess", lastAckSuccess)
                .add("lastAckNote", lastAckNote)
                .add("recentAcks", new ArrayList<>(acks))
                .add("pendingAcks", pendingView);
    }

    public String toJson() {
        return Json.value(toMap());
    }
}
