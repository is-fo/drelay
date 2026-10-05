package networking.packets;

/**
 * Queue service packet ids, decoded from the shipped assembly's {@code QPacketType} enum.
 *
 * <p>The queue protocol is tiny: five ids. The client dials {@code queue.playdarzas.com:6412}
 * (the hostname comes from the API's {@code queueIp} field), sends {@code Join}, receives
 * {@code Position} updates while waiting, and either gets an {@code Error} or proceeds to the
 * game server.
 *
 * <p>Note this is a different id space from {@link GmPacketType} even though both services use
 * the same 4-byte big-endian framing.
 */
public final class QPacketType {

    private QPacketType() {
    }

    private static final String[] NAMES = {
            "Unknown",   // 0
            "Hello",     // 1
            "Position",  // 2
            "Join",      // 3
            "Error"      // 4
    };

    public static final int HELLO = 1;
    public static final int POSITION = 2;
    public static final int JOIN = 3;
    public static final int ERROR = 4;

    public static String name(int id) {
        if (id < 0 || id >= NAMES.length) {
            return "Unknown(" + id + ")";
        }
        return NAMES[id];
    }

    public static int id(String name) {
        for (int i = 0; i < NAMES.length; i++) {
            if (NAMES[i].equals(name)) {
                return i;
            }
        }
        return -1;
    }

    public static int count() {
        return NAMES.length;
    }
}
