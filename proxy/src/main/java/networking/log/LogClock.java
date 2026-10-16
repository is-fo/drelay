package networking.log;

import java.time.Instant;
import java.time.LocalDateTime;
import java.time.ZoneId;
import java.time.format.DateTimeFormatter;

/**
 * The relay's single source of time.
 *
 * <p>Two clocks are recorded on every event on purpose. The wall clock is what a human correlates
 * with {@code %LOCALAPPDATA%\RippleStudio\Darza\logs\darza.log} and with the relay's own text lines.
 * The monotonic millisecond counter is what an analysis must use for <em>intervals</em>: a session
 * that spans an NTP correction or a daylight-saving transition would otherwise show a negative
 * delay between two packets, and every rate in a report would be wrong.
 */
public final class LogClock {

    private static final DateTimeFormatter TIME = DateTimeFormatter.ofPattern("HH:mm:ss.SSS");
    private static final DateTimeFormatter STAMP = DateTimeFormatter.ofPattern("HH:mm:ss.SSS");

    private static final long ORIGIN_NANOS = System.nanoTime();
    private static final long ORIGIN_WALL = System.currentTimeMillis();

    private LogClock() {
    }

    /** Milliseconds since this process started; never goes backwards. */
    public static long monoMillis() {
        return (System.nanoTime() - ORIGIN_NANOS) / 1_000_000L;
    }

    public static long wallOriginMillis() {
        return ORIGIN_WALL;
    }

    /** A compact local timestamp appended to a JSON event, for eyeballing the raw log. */
    public static String iso(long wallMillis) {
        return LocalDateTime.ofInstant(Instant.ofEpochMilli(wallMillis), ZoneId.systemDefault())
                .format(STAMP);
    }

    /** The same timestamp the legacy text lines used, so both logs line up line by line. */
    public static String logTime() {
        return LocalDateTime.now().format(TIME);
    }
}
