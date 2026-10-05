package networking;

import networking.log.Event;
import networking.log.EventLog;
import networking.log.LogFilter;
import networking.util.Fields;
import networking.util.Json;

import java.util.ArrayList;
import java.util.Collection;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.atomic.AtomicLong;

/**
 * Every live session, plus the filters and counters the dashboard reads.
 *
 * <p>This is the seam between the relay's hot path and its observability: the pumps publish a
 * {@link Session} here and forget about it, and the dashboard reads from here on entirely separate
 * threads. Nothing in the HTTP path takes a lock the pumps hold for longer than a field read, so a
 * slow browser tab can never delay a packet.
 *
 * <p>Closed sessions are kept for a while rather than removed immediately. The most common thing to
 * want right after a session ends - especially one that just misbehaved - is to look at it, and a
 * dashboard that empties the moment the client disconnects would answer "nothing is here".
 */
public final class SessionRegistry {

    /** How many finished sessions stay visible. */
    private static final int CLOSED_HISTORY = 8;

    private final EventLog log;
    private final AutoNexus nexus;
    private final Map<String, Session> sessions = new ConcurrentHashMap<>();
    private final List<Session> order = java.util.Collections.synchronizedList(new ArrayList<>());
    private final AtomicLong sessionIds = new AtomicLong();
    private final AtomicLong totalSessions = new AtomicLong();
    private final AtomicLong totalPackets = new AtomicLong();
    private final AtomicLong rejectedSessions = new AtomicLong();

    private volatile List<LogFilter> filters = new java.util.concurrent.CopyOnWriteArrayList<>(LogFilter.defaults());

    public SessionRegistry(EventLog log, AutoNexus nexus) {
        this.log = log;
        this.nexus = nexus;
    }

    public long nextSessionId() {
        return sessionIds.incrementAndGet();
    }

    /** The shared event sink, so a session can publish without holding a reference to this class. */
    public EventLog log() {
        return log;
    }

    /** Registers a session and gives it the relay-wide sequence of a session-start event. */
    public Session register(Session session) {
        sessions.put(session.tag, session);
        order.add(session);
        totalSessions.incrementAndGet();
        trimHistory();
        return session;
    }

    public Session get(String tag) {
        return sessions.get(tag);
    }

    public Collection<Session> sessions() {
        List<Session> snapshot;
        synchronized (order) {
            snapshot = new ArrayList<>(order);
        }
        return snapshot;
    }

    public void onSessionClosed(Session session) {
        nexus.forget(session);
        trimHistory();
    }

    public void countPackets(int count) {
        totalPackets.addAndGet(count);
    }

    public void countRejected() {
        rejectedSessions.incrementAndGet();
    }

    /** Drops the oldest finished sessions once the visible history is full. */
    private void trimHistory() {
        synchronized (order) {
            int closed = 0;
            for (int i = order.size() - 1; i >= 0; i--) {
                if (order.get(i).isClosed()) {
                    closed++;
                    if (closed > CLOSED_HISTORY) {
                        Session removed = order.remove(i);
                        sessions.remove(removed.tag);
                    }
                }
            }
        }
    }

    // --- filters ------------------------------------------------------------------------------

    public List<LogFilter> filters() {
        return filters;
    }

    public void filters(List<LogFilter> replacement) {
        this.filters = new java.util.concurrent.CopyOnWriteArrayList<>(replacement);
    }

    /** Whether an event passes the enabled filter rules. */
    public boolean visible(Event event) {
        for (LogFilter filter : filters) {
            if (filter.enabled() && filter.matchesLoose(event)) {
                return true;
            }
        }
        return false;
    }

    /** The result of an event query, including how much was hidden so the page can say so. */
    public record FilterResult(List<Event> events, long scanned, long hidden) {
    }

    /**
     * Events after {@code afterSequence}, oldest first.
     *
     * <p>Filtering happens <em>after</em> reading the ring, and the scan is bounded by {@code limit}
     * only on the kept events, not on the scanned ones: a dashboard filtering for a rare event must
     * still be able to walk past thousands of packets to find it. The scan itself is bounded by the
     * ring, which is bounded by configuration.
     */
    public FilterResult eventsAfter(long afterSequence, int limit, boolean raw) {
        List<Event> candidates = log.ring().after(afterSequence);
        List<Event> kept = new ArrayList<>(Math.min(limit, candidates.size()));
        long hidden = 0;
        for (Event event : candidates) {
            if (!raw && !visible(event)) {
                hidden++;
                continue;
            }
            kept.add(event);
        }
        // Newest-last for appending, but bounded: a page that has been away should get the tail.
        if (kept.size() > limit) {
            hidden += kept.size() - limit;
            kept = new ArrayList<>(kept.subList(kept.size() - limit, kept.size()));
        }
        return new FilterResult(kept, candidates.size(), hidden);
    }

    // --- dashboards --------------------------------------------------------------------------

    /** The counters as plain nested maps, for the dashboard's HTTP layer to serialize. */
    public Map<String, Object> counters() {
        Map<String, Object> logCounters = log.counters();
        return Fields.of()
                .add("sessionsTotal", totalSessions.get())
                .add("sessionsRejected", rejectedSessions.get())
                .add("packetsTotal", totalPackets.get())
                .add("eventsTotal", log.lastSequence())
                .add("eventsDropped", log.dropped())
                .add("kinds", logCounters.get("kinds"))
                .add("gamePackets", logCounters.get("gamePackets"))
                .add("queuePackets", logCounters.get("queuePackets"));
    }
}
