package networking;

import networking.packets.GmPacketType;
import networking.packets.Injection;

import java.util.LinkedHashMap;
import java.util.Locale;
import java.util.Map;

/**
 * Prints the byte strings the offline harness needs, straight from the relay's own encoders.
 *
 * <p>The point of asking the relay instead of hard-coding bytes in Python is that an end-to-end test
 * must not be able to pass against an encoder that disagrees with the production one. A constant
 * copied into a test file rots the moment the encoder changes; this cannot.
 *
 * <p>Values are printed as <strong>payloads</strong> - {@code [type id][body]}, no length prefix - and
 * the harness frames them with the same big-endian length rule the relay uses. That split is
 * deliberate: it is the mistake this file was first written with, and it cost a debugging session.
 * Handing over a <em>framed</em> message to a harness that frames it again produces
 * {@code 00 00 00 0B 00 00 00 07 46 00 ...}: the relay reads the inner header as a payload whose id is
 * 0, so every packet arrives named {@code Unknown} with no decoded fields, and nothing about the log
 * says why. One layer owns framing, and it is the harness.
 *
 * <p>Usage:
 * <pre>
 *   java -cp target/classes networking.TestVectors            -> all vectors, key=value per line
 *   java -cp target/classes networking.TestVectors escape     -> one vector
 *   java -cp target/classes networking.TestVectors health 707 30 0 0
 * </pre>
 */
public final class TestVectors {

    private TestVectors() {
    }

    /** key -> payload hex. Also used by {@code networking.InjectionTests} as a self-check. */
    public static Map<String, String> vectors() {
        Map<String, String> out = new LinkedHashMap<>();
        out.put("escape", hex(Injection.gamePayload(GmPacketType.ESCAPE, new byte[0])));
        out.put("caststate_on", hex(Injection.gamePayload(GmPacketType.ESCAPE_CAST_STATE, new byte[]{1})));
        out.put("mapinfo", hex(Injection.mapInfoPayload()));
        out.put("hello", hex(Injection.gamePayload(GmPacketType.HELLO, new byte[0])));
        out.put("health_full", hex(Injection.healthUpdatePayload(707, 707, 0, 0)));
        out.put("health_mid", hex(Injection.healthUpdatePayload(707, 300, 0, 0)));
        out.put("health_tiny", hex(Injection.healthUpdatePayload(707, 30, 0, 0)));
        out.put("escape_header", hex(Injection.frame(Injection.gamePayload(GmPacketType.ESCAPE, new byte[0]), true)));
        return out;
    }

    public static void main(String[] args) {
        if (args.length == 0) {
            vectors().forEach((key, value) -> IO.println(key + "=" + value));
            IO.println("note=payloads only; the harness owns the 4-byte big-endian length prefix");
            return;
        }
        switch (args[0]) {
            case "escape" -> IO.println(hex(Injection.gamePayload(GmPacketType.ESCAPE, new byte[0])));
            case "caststate" -> IO.println(hex(Injection.gamePayload(GmPacketType.ESCAPE_CAST_STATE, new byte[]{1})));
            case "mapinfo" -> IO.println(hex(Injection.mapInfoPayload()));
            case "hello" -> IO.println(hex(Injection.gamePayload(GmPacketType.HELLO, new byte[0])));
            case "health" -> {
                int max = args.length > 1 ? Integer.parseInt(args[1]) : 1000;
                int current = args.length > 2 ? Integer.parseInt(args[2]) : 100;
                int shield = args.length > 3 ? Integer.parseInt(args[3]) : 0;
                int barrier = args.length > 4 ? Integer.parseInt(args[4]) : 0;
                IO.println(hex(Injection.healthUpdatePayload(max, current, shield, barrier)));
            }
            case "framed-escape" -> IO.println(hex(Injection.frame(
                    Injection.gamePayload(GmPacketType.ESCAPE, new byte[0]), true)));
            default -> {
                System.err.println("unknown vector: " + args[0]);
                System.exit(2);
            }
        }
    }

    private static String hex(byte[] bytes) {
        StringBuilder sb = new StringBuilder(bytes.length * 2);
        for (byte b : bytes) {
            sb.append(String.format(Locale.ROOT, "%02X", b));
        }
        return sb.toString();
    }
}
