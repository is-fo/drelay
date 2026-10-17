package networking;

import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.List;

/**
 * The gate for {@link Routes#write}: what a stale hosts capture must not do to the route table.
 *
 * <p>Run with {@code java -cp target/classes networking.RoutesTests}. Exits non-zero on the first
 * failure.
 *
 * <h2>What is actually being defended</h2>
 *
 * <p>A run that is killed leaves a hosts block mapping a name-addressed route to {@code 127.0.0.1}.
 * The launcher now removes that block before it refreshes, but the refresh itself has to be the
 * second line of defence: if it resolved the name first, it would write the relay's own listen
 * address into the route table, and the relay would then refuse to start ("route Queue points at
 * 127.0.0.1:6412, the address and port it listens on"). That is a complete refusal to proxy,
 * caused by a file a previous run left behind, and it reads like a network failure.
 *
 * <p>The test uses {@code 127.0.0.1} as the refresh value rather than a name, so it is
 * deterministic on a machine with no DNS and no hosts edit: the code under test cannot tell the
 * two apart because it resolves both through the operating system.
 */
public final class RoutesTests {

    private static final List<String> FAILURES = new ArrayList<>();
    private static int checks;

    public static void main(String[] args) throws Exception {
        testLoopbackRefreshIsRejected();

        if (!FAILURES.isEmpty()) {
            for (String failure : FAILURES) {
                System.err.println("FAIL: " + failure);
            }
            System.err.println(checks + " check(s), " + FAILURES.size() + " failure(s)");
            System.exit(1);
        }
        System.out.println("PASS: " + checks + " route-table refresh checks passed");
    }

    private static void testLoopbackRefreshIsRejected() throws Exception {
        Path dir = Files.createTempDirectory("drelay-routes-test");
        try {
            Path template = dir.resolve("template.json");
            Path target = dir.resolve("generated.json");
            Files.writeString(template, """
                    {
                      "listenHost": "0.0.0.0",
                      "routes": [
                        {
                          "name": "Queue",
                          "listenPort": 6412,
                          "remoteHost": "203.0.113.9",
                          "remotePort": 6412,
                          "refresh": "127.0.0.1"
                        }
                      ]
                    }
                    """, StandardCharsets.UTF_8);

            Routes.Refresh refresh = Routes.write(template, target, "192.168.0.11");

            check("a loopback refresh is reported as a refusal",
                    !refresh.failures().isEmpty());
            check("a loopback refresh is not reported as a change",
                    refresh.changes().isEmpty());

            String written = Files.readString(target, StandardCharsets.UTF_8);
            check("the pinned server address survives the refresh",
                    written.contains("\"remoteHost\": \"203.0.113.9\""));
            check("the relay's own listen address never becomes a destination",
                    !written.contains("\"remoteHost\": \"127.0.0.1\""));
            check("the upstream source is still written",
                    written.contains("\"upstreamHost\": \"192.168.0.11\""));
        } finally {
            delete(dir);
        }
    }

    private static void delete(Path dir) {
        try (var walk = Files.walk(dir)) {
            walk.sorted(Comparator.reverseOrder()).forEach(path -> {
                try {
                    Files.deleteIfExists(path);
                } catch (Exception ignored) {
                    // A temp directory that cannot be removed is not a test failure.
                }
            });
        } catch (Exception ignored) {
            // Same.
        }
    }

    private static void check(String what, boolean ok) {
        checks++;
        if (!ok) {
            FAILURES.add(what);
        }
    }
}
