package networking.log;

import networking.util.Json;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * One structured observation, the unit of both the in-memory ring and the JSONL log.
 *
 * <p>Why this exists instead of more {@code IO.println} calls: the relay can only be analysed
 * <em>after</em> a play session, when nobody can look at a console. A human-readable line loses the
 * things an analysis actually needs - which session, which direction, the raw payload, and the
 * decoded fields side by side - and can only be searched with regexes that break whenever a format
 * string changes. Every field here is retrievable by name from {@code work/logs/*.jsonl}.
 *
 * <p>{@code seq} is a monotonically increasing counter over one relay run. It is what makes the
 * causal chain auditable: the event that carried a {@code HealthUpdate}, the decision event that
 * followed it, and the injection event that followed <em>that</em> are three consecutive sequence
 * numbers, and each one names the one before it. Without it, an injected packet is indistinguishable
 * from one the client sent, which is exactly the question worth answering when a session misbehaves.
 */
public final class Event {

    /** Counters for every category this relay emits, surfaced verbatim in the dashboard. */
    public static final String KIND_SESSION = "session";
    public static final String KIND_PACKET = "packet";
    public static final String KIND_INJECT = "inject";
    public static final String KIND_NEXUS = "nexus";
    public static final String KIND_HEALTH = "health";
    public static final String KIND_WORLD = "world";
    public static final String KIND_NOTE = "note";
    public static final String KIND_ERROR = "error";

    public static final String DIR_C2S = "C->S";
    public static final String DIR_S2C = "S->C";

    public final long seq;
    public final long wallMillis;
    public final long monoMillis;
    public final String kind;
    /** Session tag such as {@code Game_Slave#3}, or {@code relay} for relay-wide events. */
    public final String session;
    public final String dir;
    public final String pkt;
    public final Integer pktId;
    public final Integer len;
    public final String hex;
    /** Decoded, human-facing fields; also every extra key/value an emitter wants on the event. */
    public final Map<String, Object> data;
    public final String note;
    /**
     * The human-readable line for the text log, or null.
     *
     * <p>It travels on the event rather than being written by a second call, so that the line lands
     * under the same lock and in the same order as the event's {@code seq}. Writing it separately let
     * a packet's text line appear after the next event's, so the text log and the JSONL disagreed
     * about order - see {@code EventLog.emit}. It is deliberately <em>not</em> serialized into the
     * JSON: it is a rendering of fields the JSON already carries.
     */
    public final String textLine;

    private Event(Builder builder) {
        this.seq = builder.seq;
        this.wallMillis = builder.wallMillis;
        this.monoMillis = builder.monoMillis;
        this.kind = builder.kind;
        this.session = builder.session;
        this.dir = builder.dir;
        this.pkt = builder.pkt;
        this.pktId = builder.pktId;
        this.len = builder.len;
        this.hex = builder.hex;
        this.data = builder.data;
        this.note = builder.note;
        this.textLine = builder.textLine;
    }

    public boolean isPacket() {
        return KIND_PACKET.equals(kind) || KIND_INJECT.equals(kind);
    }

    public static Builder builder(String kind) {
        return new Builder(kind);
    }

    /** Serializes to one line of JSON. Field order is fixed so two runs can be diffed directly. */
    public String toJson() {
        List<Json.Field> fields = new ArrayList<>(12);
        fields.add(Json.of("seq", seq));
        fields.add(Json.of("t", LogClock.iso(wallMillis)));
        fields.add(Json.of("ms", monoMillis));
        fields.add(Json.of("kind", kind));
        fields.add(Json.of("sess", session));
        fields.add(Json.of("dir", dir));
        fields.add(Json.of("pkt", pkt));
        fields.add(Json.of("id", pktId));
        fields.add(Json.of("len", len));
        fields.add(Json.of("hex", hex));
        fields.add(Json.of("note", note));
        fields.add(Json.of("data", data));
        return Json.object(fields);
    }

    @Override
    public String toString() {
        return toJson();
    }

    /** Mutable builder; a caller sets only the fields it has. */
    public static final class Builder {
        private final String kind;
        private long seq;
        private long wallMillis;
        private long monoMillis;
        private String session = "relay";
        private String dir;
        private String pkt;
        private Integer pktId;
        private Integer len;
        private String hex;
        private final Map<String, Object> data = new LinkedHashMap<>();
        private String note;
        private String textLine;

        Builder(String kind) {
            this.kind = kind;
            this.wallMillis = System.currentTimeMillis();
            this.monoMillis = LogClock.monoMillis();
        }

        public Builder seq(long value) {
            this.seq = value;
            return this;
        }

        public Builder at(long wallMillis, long monoMillis) {
            this.wallMillis = wallMillis;
            this.monoMillis = monoMillis;
            return this;
        }

        public Builder session(String value) {
            this.session = value;
            return this;
        }

        public Builder dir(String value) {
            this.dir = value;
            return this;
        }

        public Builder pkt(String value) {
            this.pkt = value;
            return this;
        }

        public Builder pktId(Integer value) {
            this.pktId = value;
            return this;
        }

        public Builder len(Integer value) {
            this.len = value;
            return this;
        }

        public Builder hex(String value) {
            this.hex = value;
            return this;
        }

        public Builder note(String value) {
            this.note = value;
            return this;
        }

        /**
         * Attaches the text-log rendering, to be written by {@code EventLog.emit} under its sequence
         * lock. See {@link Event#textLine} for why it is not a separate call.
         */
        public Builder textLine(String value) {
            this.textLine = value;
            return this;
        }

        public Builder put(String key, Object value) {
            if (value != null) {
                data.put(key, value);
            }
            return this;
        }

        public Builder putAll(Map<String, ?> values) {
            if (values != null) {
                values.forEach(this::put);
            }
            return this;
        }

        /**
         * Sets a field only when the condition holds, so an emitter can write
         * {@code .putIf(c > 0, "count", c)} instead of a conditional block per field.
         */
        public Builder putIf(boolean condition, String key, Object value) {
            return condition ? put(key, value) : this;
        }

        public Event build() {
            return new Event(this);
        }
    }
}
