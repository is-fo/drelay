package networking;

import networking.packets.ConfusedStrip;
import networking.packets.UpdateScan;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.TreeMap;

/**
 * Drives the real walker, strip and player locator over a captured run.
 *
 * <p>Not part of the offline checks: it needs a capture, and a capture is a file on one machine. Run
 * it by hand after building:
 *
 * <pre>{@code
 * python work/make_replay.py work/replay.txt "G:\drelay\work\logs\events-*.jsonl"
 * java -cp "target\classes;target\test-classes" networking.CaptureReplay work/replay.txt 1930
 * }</pre>
 *
 * <p>The argument is the object id the operator's own player is known to have had in the fatal world;
 * it is printed as a cross-check, not used to drive anything. What this proves that a unit test cannot:
 * that {@link UpdateScan} decodes <em>every</em> {@code GmUpdate} a real server sent, that
 * {@link PlayerLocator} names one object per world, and that {@link ConfusedStrip} only ever fires on
 * the effect it was asked for.
 *
 * <p>Input line format, produced by {@code work/make_replay.py}: {@code W sess ms},
 * {@code H sess ms health}, {@code U sess ms hex}.
 */
public final class CaptureReplay {

    public static void main(String[] args) throws Exception {
        Path path = Path.of(args.length > 0 ? args[0] : "work/replay.txt");
        int expectedPlayerId = args.length > 1 ? Integer.parseInt(args[1]) : -1;
        int minVotes = args.length > 2 ? Integer.parseInt(args[2]) : 3;

        Map<String, PlayerLocator> locators = new HashMap<>();
        Map<String, Integer> worlds = new TreeMap<>();
        Map<String, Integer> resolvedIn = new TreeMap<>();
        Map<String, Integer> stripsPerEffect = new TreeMap<>();
        Map<String, Integer> stripsPerObject = new TreeMap<>();
        List<String> failures = new ArrayList<>();
        long updates = 0;
        long walked = 0;
        long strips = 0;
        long bytesRemoved = 0;

        for (String line : Files.readAllLines(path)) {
            if (line.isEmpty()) {
                continue;
            }
            String[] parts = line.split(" ", 4);
            String kind = parts[0];
            String session = parts[1];
            long millis = Long.parseLong(parts[2]);
            PlayerLocator locator = locators.computeIfAbsent(session, key -> new PlayerLocator(minVotes));

            switch (kind) {
                case "W" -> {
                    locator.onWorldEntry();
                    worlds.merge(session, 1, Integer::sum);
                }
                case "H" -> locator.onHealth(Integer.parseInt(parts[3]), millis);
                case "U" -> {
                    updates++;
                    byte[] payload = java.util.HexFormat.of().parseHex(parts[3]);
                    if (!UpdateScan.walk(payload, silent())) {
                        failures.add(session + " " + millis + " walk failed on a real packet");
                        continue;
                    }
                    walked++;
                    locator.observe(payload, millis);
                    int playerId = locator.playerId();
                    if (playerId < 0) {
                        continue;
                    }
                    locator.takeNewlyResolved();
                    Integer previous = resolvedIn.get(session);
                    if (previous == null || previous != playerId) {
                        resolvedIn.put(session, playerId);
                        System.out.printf("%-15s world %-3d t=%-9d resolved player object %-8d %s%n",
                                session, worlds.getOrDefault(session, 0), millis, playerId,
                                locator.explain());
                    }
                    ConfusedStrip.Result result =
                            ConfusedStrip.strip(payload, playerId, ConfusedStrip.CONFUSED);
                    if (result != null) {
                        strips++;
                        bytesRemoved += payload.length - result.payload().length;
                        stripsPerEffect.merge("effect " + ConfusedStrip.CONFUSED, 1, Integer::sum);
                        stripsPerObject.merge(session + " obj " + playerId, 1, Integer::sum);
                        System.out.printf("%-15s world %-3d t=%-9d STRIP obj %-8d %d ent %d -> %d bytes%n",
                                session, worlds.getOrDefault(session, 0), millis, playerId,
                                result.removed(), payload.length, result.payload().length);
                        if (result.removed() * UpdateScan.STATUS_ENTRY_BYTES
                                != payload.length - result.payload().length) {
                            failures.add("strip shrank the packet by the wrong amount at "
                                    + session + " " + millis);
                        }
                        if (!UpdateScan.walk(result.payload(), silent())) {
                            failures.add("rewritten packet does not re-walk at " + session + " " + millis);
                        }
                    }
                }
                default -> {
                }
            }
        }

        System.out.println();
        System.out.printf("minVotes:          %d%n", minVotes);
        System.out.printf("worlds:            %d%n", worlds.values().stream().mapToInt(Integer::intValue).sum());
        System.out.printf("Update packets:    %d%n", updates);
        System.out.printf("walked exactly:    %d/%d%n", walked, updates);
        System.out.printf("strips performed:  %d (removed %d bytes)%n", strips, bytesRemoved);
        System.out.println("strips by object:  " + stripsPerObject);
        System.out.println("strips by effect:  " + stripsPerEffect);
        System.out.println("resolved player:   " + resolvedIn);
        if (expectedPlayerId >= 0) {
            System.out.printf("expected object %d %s%n", expectedPlayerId,
                    resolvedIn.containsValue(expectedPlayerId) ? "was resolved" : "was NOT resolved");
        }
        if (failures.isEmpty()) {
            System.out.println("PASS: every real Update walked and every rewrite re-read");
            return;
        }
        System.out.println("FAIL: " + failures.size() + " problem(s)");
        for (String failure : failures.subList(0, Math.min(20, failures.size()))) {
            System.out.println("  - " + failure);
        }
        System.exit(1);
    }

    private static UpdateScan.Visitor silent() {
        return new UpdateScan.Visitor() {
            @Override
            public void onStatusList(int objectId, int countOffset, int count, int firstEntryOffset) {
            }
        };
    }
}
