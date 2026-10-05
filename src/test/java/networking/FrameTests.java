package networking;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.util.ArrayList;
import java.util.List;

/**
 * Framing checks for the relay's per-session length byte order.
 *
 * <p>These exist because the two services genuinely disagree, and getting it wrong is silent in
 * the worst way: the relay reads an absurd length and drops the session. Both cases below are
 * taken from real observed traffic rather than invented:
 *
 * <ul>
 *   <li>game server: {@code 00 00 00 0A} = big-endian 10 (seen in tools/test_relay_live.py)</li>
 *   <li>queue server: {@code 08 00 00 00} = little-endian 8, which a big-endian reader turns into
 *       0x08000000 = 134217728 (seen in a live session relay log before this was fixed)</li>
 * </ul>
 *
 * <p>Also runs the full read/forward/write path over a socket pair with little-endian framing,
 * which the local echo test (tools/test_relay.py) does not cover.
 *
 * <p>Run: {@code java -cp target/classes networking.FrameTests}
 */
public final class FrameTests {

    private static final List<String> FAILURES = new ArrayList<>();

    public static void main(String[] args) throws Exception {
        testQueueHeaderFromLiveCapture();
        testGameHeaderFromLiveCapture();
        testLittleEndianRoundTrip();
        testBigEndianRoundTrip();
        testAmbiguousSmallHeaderDefaultsToBig();
        testEncodeMatchesDetectedOrder();

        if (FAILURES.isEmpty()) {
            IO.println("PASS: framing checks passed (both byte orders, including the live queue case)");
            return;
        }
        IO.println("FAIL: " + FAILURES.size() + " check(s) failed");
        for (String failure : FAILURES) {
            IO.println("  - " + failure);
        }
        System.exit(1);
    }

    private static void check(boolean condition, String description) {
        if (!condition) {
            FAILURES.add(description);
        }
    }

    private static void checkEquals(Object expected, Object actual, String description) {
        if (!java.util.Objects.equals(expected, actual)) {
            FAILURES.add(description + " (expected " + expected + ", got " + actual + ")");
        }
    }

    private static byte[] hexToBytes(String hex) {
        String clean = hex.replace(" ", "");
        byte[] out = new byte[clean.length() / 2];
        for (int i = 0; i < out.length; i++) {
            out[i] = (byte) Integer.parseInt(clean.substring(i * 2, i * 2 + 2), 16);
        }
        return out;
    }

    /** The exact header that made the relay report "implausible payload length 134217728". */
    private static void testQueueHeaderFromLiveCapture() throws Exception {
        byte[] header = hexToBytes("08 00 00 00");
        var state = new Relay.SessionState(Relay.Endianness.AUTO);
        int length = state.decodeLength(header, "test-queue", true);
        checkEquals(8, length, "little-endian queue header 08 00 00 00 decodes to 8");
        checkEquals(Relay.Endianness.LITTLE, state.endianness(),
                "queue header resolves detection to LITTLE");
    }

    /** The header the real game server sends ahead of its 10-byte opening Ping. */
    private static void testGameHeaderFromLiveCapture() throws Exception {
        byte[] header = hexToBytes("00 00 00 0A");
        var state = new Relay.SessionState(Relay.Endianness.AUTO);
        int length = state.decodeLength(header, "test-game", true);
        checkEquals(10, length, "big-endian game header 00 00 00 0A decodes to 10");
        checkEquals(Relay.Endianness.BIG, state.endianness(),
                "game header resolves detection to BIG");
    }

    /** A header whose low bytes make both readings plausible must not break the session. */
    private static void testAmbiguousSmallHeaderDefaultsToBig() throws Exception {
        byte[] header = hexToBytes("00 00 00 01");
        var state = new Relay.SessionState(Relay.Endianness.AUTO);
        int length = state.decodeLength(header, "test-ambiguous", true);
        checkEquals(1, length, "ambiguous header decodes to 1");
        checkEquals(Relay.Endianness.BIG, state.endianness(),
                "ambiguous header defaults to BIG (the game service's order)");
    }

    private static void testEncodeMatchesDetectedOrder() throws Exception {
        var little = new Relay.SessionState(Relay.Endianness.AUTO);
        little.decodeLength(hexToBytes("0A 00 00 00"), "test-encode-le", true);
        checkEquals("0A000000", hex(little.encodeLength(10)),
                "LITTLE session writes a little-endian length");

        var big = new Relay.SessionState(Relay.Endianness.AUTO);
        checkEquals("0000000A", hex(new Relay.SessionState(Relay.Endianness.BIG).encodeLength(10)),
                "BIG session writes a big-endian length");
        big.decodeLength(hexToBytes("00 00 00 0A"), "test-encode-be", true);
        checkEquals(Relay.Endianness.BIG, big.endianness(), "00 00 00 0A resolves to BIG");
    }

    /** Full session over an in-process socket pair, to prove the pump honours LITTLE. */
    private static void testLittleEndianRoundTrip() throws Exception {
        try (var listener = new java.net.ServerSocket(0, 1, java.net.InetAddress.getLoopbackAddress())) {
            int port = listener.getLocalPort();
            var serverSide = new Thread(() -> {
                try (var s = listener.accept()) {
                    var in = new GameReader(s.getInputStream());
                    var out = new GameWriter(s.getOutputStream());
                    var session = new Relay.SessionState(Relay.Endianness.AUTO);

                    // read the client's frame using the same detection the relay uses
                    byte[] header = new byte[4];
                    in.readFully(header);
                    int length = session.decodeLength(header, "test-peer", true);
                    byte[] payload = new byte[length];
                    in.readFully(payload);

                    // reply framed in the detected order
                    byte[] reply = new byte[]{0x02, (byte) 0xAC, 0x04};
                    out.write(session.encodeLength(reply.length));
                    out.write(reply);
                    out.flush();

                    if (length != 3 || payload[0] != 0x03) {
                        FAILURES.add("peer read unexpected request: length=" + length
                                + " id=" + (payload.length > 0 ? payload[0] : -1));
                    }
                } catch (Exception e) {
                    FAILURES.add("peer failed: " + e);
                }
            });
            serverSide.start();

            try (var client = new java.net.Socket(java.net.InetAddress.getLoopbackAddress(), port)) {
                var out = new GameWriter(client.getOutputStream());
                var in = new GameReader(client.getInputStream());
                var session = new Relay.SessionState(Relay.Endianness.AUTO);

                // Little-endian request: length 3 = 03 00 00 00, id 0x03, then two bytes.
                byte[] request = new byte[]{0x03, 0x2A, 0x2B};
                out.write(session.encodeLength(request.length));
                out.write(request);
                out.flush();

                byte[] header = new byte[4];
                in.readFully(header);
                int length = session.decodeLength(header, "test-client", false);
                byte[] payload = new byte[length];
                in.readFully(payload);
                checkEquals(3, length, "reply length arrives intact over LITTLE framing");
                checkEquals("02AC04", hex(payload), "reply payload arrives byte-exact");
            }
            serverSide.join(5000);
        }
    }

    /** Same, with the game service's big-endian order. */
    private static void testBigEndianRoundTrip() throws Exception {
        try (var listener = new java.net.ServerSocket(0, 1, java.net.InetAddress.getLoopbackAddress())) {
            int port = listener.getLocalPort();
            var serverSide = new Thread(() -> {
                try (var s = listener.accept()) {
                    var in = new GameReader(s.getInputStream());
                    var out = new GameWriter(s.getOutputStream());
                    var session = new Relay.SessionState(Relay.Endianness.BIG);
                    byte[] header = new byte[4];
                    in.readFully(header);
                    int length = session.decodeLength(header, "test-peer-be", true);
                    byte[] payload = new byte[length];
                    in.readFully(payload);
                    byte[] reply = new byte[]{0x5C, 0, 0};
                    out.write(session.encodeLength(reply.length));
                    out.write(reply);
                    out.flush();
                } catch (Exception e) {
                    FAILURES.add("peer(be) failed: " + e);
                }
            });
            serverSide.start();

            try (var client = new java.net.Socket(java.net.InetAddress.getLoopbackAddress(), port)) {
                var out = new GameWriter(client.getOutputStream());
                var in = new GameReader(client.getInputStream());
                var session = new Relay.SessionState(Relay.Endianness.AUTO);

                byte[] request = new byte[]{0x12, (byte) 0xFF};
                out.write(session.encodeLength(request.length));
                out.write(request);
                out.flush();

                byte[] header = new byte[4];
                in.readFully(header);
                int length = session.decodeLength(header, "test-client-be", false);
                byte[] payload = new byte[length];
                in.readFully(payload);
                checkEquals(3, length, "reply length arrives intact over BIG framing");
                checkEquals("5C0000", hex(payload), "reply payload arrives byte-exact (BIG)");
            }
            serverSide.join(5000);
        }
    }

    private static String hex(byte[] bytes) {
        var sb = new StringBuilder();
        for (byte b : bytes) {
            sb.append(String.format("%02X", b));
        }
        return sb.toString();
    }
}
