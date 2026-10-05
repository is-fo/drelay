package networking;

import networking.packets.UpdateScan;

import java.util.ArrayDeque;
import java.util.Deque;
import java.util.HashMap;
import java.util.Map;

/**
 * Works out which object in the game's {@code GmUpdate} stream is <em>this</em> session's own player.
 *
 * <h2>Why a relay has to work this out at all</h2>
 *
 * <p>{@code GmUpdate} identifies its objects by a server-assigned id, and nothing in the packet says
 * which one is the character sitting at the keyboard. The obvious link - {@code GmUpdate} carrying
 * {@code StatsType.OwnCharacterId} (183) - exists but is sent only on the full stat dumps at world
 * entry and at the death/nexus transition. In the 4-hour capture at {@code work/logs} that is nine
 * packets; the status list a client-side filter needs to act on arrives hundreds of times per minute.
 * Waiting for an id that late is waiting until after the character is already dead.
 *
 * <p>So the id is <strong>derived</strong> instead, from the one packet that is unambiguously about
 * the local player: {@code GmHealthUpdate} (id 70). That packet has no object id, but its value does -
 * the object whose {@code Hp} (2) or {@code Health} (80) stat equals a health reading that just
 * arrived on this same connection is the local player, because no other object's health is being
 * reported to this client by name.
 *
 * <h2>Why the answers are per world, and why a margin is required</h2>
 *
 * <p>A world entry hands out new object ids, so the tally resets on {@code MapInfo}. Within one world
 * the vote is not close: replaying the capture gives a leader with 3-390 matching readings and a
 * runner-up with 0 or 1 in every one of the 31 worlds. But "not close" is measured, not guaranteed, so
 * {@link #playerId()} refuses to answer until the leader has at least {@code minVotes} and is strictly
 * ahead of every other object. A caller that cannot get an id must do nothing rather than guess, and
 * {@link #explain()} exists so the reason is visible in the log instead of being a silent no-op.
 *
 * <p>The {@code OwnCharacterId} sighting still matters: when it appears it is exact, so it overrides
 * the tally. It is treated as a confirmation, never as the only signal.
 */
public final class PlayerLocator {

    /** How stale the newest health reading may be for a match to count. */
    private static final long MATCH_WINDOW_MILLIS = 400;

    /** How many recent readings are kept for matching; a stat can lag the newest by a tick or two. */
    private static final int RECENT_READINGS = 4;

    private record Reading(int health, long wallMillis) {
    }

    private final int minVotes;

    private final Deque<Reading> readings = new ArrayDeque<>(RECENT_READINGS);
    private final Map<Integer, Integer> votes = new HashMap<>();

    private int worldEntryCount;
    private int ownCharacterIdObject = -1;
    private int resolved = -1;
    private int resolvedVotes;
    private int resolvedRunnerUp;
    private int reported = -1;
    private String explanation = "no world yet";

    /**
     * @param minVotes how many independent health continuations an object needs before it is believed
     */
    public PlayerLocator(int minVotes) {
        this.minVotes = Math.max(1, minVotes);
    }

    /** A new world means new object ids: everything learned about the previous one is void. */
    public synchronized void onWorldEntry() {
        readings.clear();
        votes.clear();
        ownCharacterIdObject = -1;
        resolved = -1;
        resolvedVotes = 0;
        resolvedRunnerUp = 0;
        reported = -1;
        worldEntryCount++;
        explanation = "no health reading matched an object yet in world " + worldEntryCount;
    }

    /**
     * Records a {@code GmHealthUpdate} reading; the local player's object carries the same number.
     *
     * <p>The timestamp is a parameter rather than read from the clock here so that a replayed capture
     * is matched on the capture's own timeline. Reading the clock instead makes every reading in a
     * replay look simultaneous, which inflates the tally and hides exactly the window behaviour the
     * live path depends on.
     */
    public synchronized void onHealth(int health, long nowMillis) {
        readings.addLast(new Reading(health, nowMillis));
        while (readings.size() > RECENT_READINGS) {
            readings.removeFirst();
        }
    }

    /**
     * Scans one {@code GmUpdate} payload, voting for any object that repeats a recent reading and
     * noting an {@code OwnCharacterId} sighting.
     *
     * <p>Cheap and allocation-free apart from the walk itself. A payload the walker rejects changes
     * nothing: a half-decoded update would vote for the wrong objects.
     */
    public synchronized void observe(byte[] updatePayload, long nowMillis) {
        if (updatePayload == null) {
            return;
        }
        UpdateScan.walk(updatePayload, new UpdateScan.Visitor() {
            @Override
            public void onStatusList(int objectId, int countOffset, int count, int firstEntryOffset) {
                // Nothing to learn from the list itself; the walk must still traverse it.
            }

            @Override
            public void onIntegerStat(int objectId, int statType, long value) {
                if (statType != UpdateScan.HP_STAT && statType != UpdateScan.HEALTH_STAT) {
                    return;
                }
                for (Reading reading : readings) {
                    if (reading.health() == value && nowMillis - reading.wallMillis() <= MATCH_WINDOW_MILLIS) {
                        votes.merge(objectId, 1, Integer::sum);
                        return;
                    }
                }
            }

            @Override
            public void onStat(int objectId, int statType, int dataType) {
                if (statType == UpdateScan.OWN_CHARACTER_ID_STAT) {
                    ownCharacterIdObject = objectId;
                }
            }
        });
    }

    /**
     * The local player's object id, or {@code -1} when it cannot be established yet.
     *
     * <p>A negative answer is normal for the first moments of a world and is not an error; callers
     * must treat it as "leave the bytes alone".
     *
     * <h2>Why the answer is latched rather than recomputed</h2>
     *
     * <p>The tally keeps growing, so the leader can in principle change as a fight goes on. Acting on
     * a moving answer would mean rewriting one object's list early and a different object's list
     * later, which is both harder to explain from the log and a way to affect two characters instead
     * of one. So the first object that satisfies the test is kept for the rest of the world. Replaying
     * the 2026-10-05 capture shows no world where a resolved leader was later overtaken, so the latch
     * costs nothing there; it only removes the possibility.
     */
    public synchronized int playerId() {
        if (ownCharacterIdObject >= 0) {
            explanation = "object %d carried OwnCharacterId".formatted(ownCharacterIdObject);
            return ownCharacterIdObject;
        }
        if (resolved >= 0) {
            return resolved;
        }
        int leader = -1;
        int best = 0;
        int runnerUp = 0;
        for (Map.Entry<Integer, Integer> entry : votes.entrySet()) {
            int count = entry.getValue();
            if (count > best) {
                runnerUp = best;
                best = count;
                leader = entry.getKey();
            } else if (count > runnerUp) {
                runnerUp = count;
            }
        }
        if (leader < 0 || best < minVotes || best <= runnerUp) {
            explanation = "no object has %d matching health readings yet (best %d, runner-up %d)"
                    .formatted(minVotes, best, runnerUp);
            return -1;
        }
        resolved = leader;
        resolvedVotes = best;
        resolvedRunnerUp = runnerUp;
        explanation = "object %d matched %d health readings (runner-up %d)"
                .formatted(resolved, resolvedVotes, resolvedRunnerUp);
        return resolved;
    }

    /** Why {@link #playerId()} answers as it does, for a log line. */
    public synchronized String explain() {
        return explanation;
    }

    /**
     * The object id to report, the first time it becomes known in this world.
     *
     * <p>{@link #playerId()} is queried on every packet, so a caller that logs the answer wants this
     * instead: it returns a positive id once per world and {@code -1} forever after, which turns "the
     * feature is silently doing nothing" into one line in the log per world.
     *
     * @return a positive id exactly once per world, otherwise {@code -1}
     */
    public synchronized int takeNewlyResolved() {
        int id = playerId();
        if (id < 0 || id == reported) {
            return -1;
        }
        reported = id;
        return id;
    }

    /** How many objects have been voted for in this world; for diagnostics and tests. */
    public synchronized int candidateCount() {
        return votes.size();
    }
}
