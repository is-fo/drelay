package networking;

import java.util.ArrayList;
import java.util.List;

/**
 * A lock-free-enough fixed-capacity ring of the newest items.
 *
 * <p>The relay emits tens of events per second for hours, and the dashboard must be able to ask
 * "what happened in the last N events" without the relay keeping an unbounded history. A ring is the
 * right shape: the newest {@code capacity} events are always available, memory is constant, and an
 * event that scrolls off has already been written to the JSONL log, which is where a full analysis
 * reads from anyway.
 *
 * <p>Backed by a plain array with a single {@code synchronized} writer path. Contention is one
 * monitor per event, which is nothing next to the socket write that follows it, and it removes every
 * question about visibility. An event's sequence number is its position in a global counter, so a
 * reader can ask for "everything after sequence N" and get exactly the new events, in order.
 */
public final class RingBuffer<T> {

    private final Object[] items;
    private final long[] sequences;
    private int next;
    private int size;
    private long newestSequence;
    private long oldestSequence;

    public RingBuffer(int capacity) {
        int bounded = Math.max(16, capacity);
        this.items = new Object[bounded];
        this.sequences = new long[bounded];
    }

    public synchronized void add(T item) {
        add(item, newestSequence + 1);
    }

    /** Adds with an explicit sequence; used when the caller owns the numbering. */
    public synchronized void add(T item, long sequence) {
        if (sequence > newestSequence) {
            newestSequence = sequence;
        }
        int slot = next;
        items[slot] = item;
        sequences[slot] = sequence;
        next = (next + 1) % items.length;
        if (size < items.length) {
            size++;
        }
        oldestSequence = sequences[(next - size + items.length) % items.length];
    }

    public synchronized int size() {
        return size;
    }

    public int capacity() {
        return items.length;
    }

    public synchronized long newestSequence() {
        return newestSequence;
    }

    /**
     * Everything added after {@code afterSequence}, oldest first.
     *
     * <p>When the caller has fallen further behind than the ring holds, the returned list simply
     * starts at the oldest surviving event; {@link #oldestSequence()} lets a caller notice and say so
     * rather than silently presenting a gap as continuity.
     */
    @SuppressWarnings("unchecked")
    public synchronized List<T> after(long afterSequence) {
        List<T> out = new ArrayList<>(Math.min(size, 4096));
        for (int i = 0; i < size; i++) {
            int slot = (next - size + i + items.length) % items.length;
            if (sequences[slot] > afterSequence) {
                out.add((T) items[slot]);
            }
        }
        return out;
    }

    /** The newest {@code count} items, oldest first. */
    @SuppressWarnings("unchecked")
    public synchronized List<T> last(int count) {
        int take = Math.min(count, size);
        List<T> out = new ArrayList<>(take);
        for (int i = size - take; i < size; i++) {
            int slot = (next - size + i + items.length) % items.length;
            out.add((T) items[slot]);
        }
        return out;
    }

    public synchronized long oldestSequence() {
        return size == 0 ? newestSequence : oldestSequence;
    }

    public synchronized void clear() {
        java.util.Arrays.fill(items, null);
        size = 0;
        next = 0;
    }
}
