package networking.util;

import java.util.Locale;

/**
 * Reads typed values out of a {@code -D} system property or an environment variable.
 *
 * <p>There are three ways to configure this relay and they serve different operators: the route table
 * JSON is the checked-in default, a system property is what a launcher script can pass on one line
 * without editing a file, and an environment variable is what survives a shortcut or a service. The
 * auto-nexus switches matter enough to be reachable from all three, so the precedence is fixed and
 * documented in one place: <strong>system property, then environment variable, then default</strong>.
 *
 * <p>Every reader is total: an unparseable value returns the default rather than throwing at startup,
 * because a typo in a launcher script should not stop the relay from carrying a session.
 */
public final class Prefs {

    private Prefs() {
    }

    public static String string(String property, String environment, String fallback) {
        String value = System.getProperty(property);
        if (value == null || value.isBlank()) {
            value = environment == null ? null : System.getenv(environment);
        }
        return value == null || value.isBlank() ? fallback : value.trim();
    }

    public static boolean flag(String property, String environment, boolean fallback) {
        String value = string(property, environment, null);
        if (value == null) {
            return fallback;
        }
        return switch (value.toLowerCase(Locale.ROOT)) {
            case "1", "true", "yes", "on", "y" -> true;
            case "0", "false", "no", "off", "n" -> false;
            default -> fallback;
        };
    }

    public static int integer(String property, String environment, int fallback) {
        String value = string(property, environment, null);
        if (value == null) {
            return fallback;
        }
        try {
            return Integer.parseInt(value);
        } catch (NumberFormatException e) {
            return fallback;
        }
    }

    public static long longValue(String property, String environment, long fallback) {
        String value = string(property, environment, null);
        if (value == null) {
            return fallback;
        }
        try {
            return Long.parseLong(value);
        } catch (NumberFormatException e) {
            return fallback;
        }
    }
}
