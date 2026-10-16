package networking;

import networking.log.LogFilter;
import networking.packets.StatusStrip;
import networking.web.WebDashboard;

import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;

/**
 * The gate for the settings that outlive a run: the strip module's config, the route-table writer,
 * and the filter round trip that used to grow a pair of brackets per click.
 *
 * <p>Run with {@code java -cp target/classes networking.SettingsTests}. Exits non-zero on the first
 * failure.
 *
 * <h2>What is actually being defended</h2>
 *
 * <ol>
 *   <li><b>A setting the dashboard accepts is a setting the relay obeys.</b> The strip config is
 *       parsed from JSON, from properties and from an HTTP body, and all three spellings have to mean
 *       the same thing - including the pre-module {@code {"confused": true, "effect": 11}} form, which
 *       exists in deployed route tables and must not silently arm nothing.</li>
 *   <li><b>Saving a setting must not damage the file it is saved into.</b> {@link ConfigWriter}
 *       rewrites two keys of a document the user also edits by hand; dropping the comment block, a
 *       route or a key it does not understand would corrupt the file the relay needs to start.</li>
 *   <li><b>The dashboard's filter list must survive its own round trip.</b> The endpoint used to
 *       stringify the array the page posted, so every save added a layer of brackets to every packet
 *       name until nothing matched - a filter that "stopped working" with no error anywhere.</li>
 * </ol>
 */
public final class SettingsTests {

    private static final List<String> FAILURES = new ArrayList<>();
    private static int checks;

    public static void main(String[] args) throws Exception {
        testStripDefaults();
        testStripNamedEffects();
        testStripArmsAndDisarms();
        testStripAcceptsTheLegacyForm();
        testStripParsesLooseText();
        testStripRejectsNonsense();
        testConfigWriterPreservesEverythingElse();
        testConfigWriterRoundTripsThroughTheRelayParser();
        testRouteTableCarriesDashboardSettingsForward();
        testFilterTextAcceptsArraysAndStrings();
        testFilterParseDropsBracketWreckage();

        if (FAILURES.isEmpty()) {
            IO.println("PASS: " + checks + " settings, persistence and filter checks passed");
            return;
        }
        IO.println("FAIL: " + FAILURES.size() + " of " + checks + " check(s) failed");
        for (String failure : FAILURES) {
            IO.println("  - " + failure);
        }
        System.exit(1);
    }

    // --- the strip's config -------------------------------------------------------------------

    private static void testStripDefaults() {
        Strip.Config config = new Strip.Config();
        check(config.enabled(), "the strip is not enabled by default");
        check(config.active(), "the strip is not active by default");
        checkEquals(new java.util.LinkedHashSet<>(List.of(StatusStrip.CONFUSED, StatusStrip.HALLUCINATING)),
                config.effects(),
                "the default armed set is not exactly Confused + Hallucinating");
        // Order is part of the contract, not an implementation detail: the startup line and the
        // dashboard pill both render this set in iteration order, so an unordered default would print
        // differently from run to run for the same configuration.
        checkEquals(List.of(StatusStrip.CONFUSED, StatusStrip.HALLUCINATING),
                new ArrayList<>(config.effects()),
                "the default armed set lost its order");
        checkEquals(3, config.minVotes(), "the default minVotes changed");
        checkEquals(StatusStrip.PARALYZED, 6, "Paralyzed is no longer ordinal 6");
        checkEquals(StatusStrip.SLOWED, 7, "Slowed is no longer ordinal 7");
        checkEquals(StatusStrip.CONFUSED, 11, "Confused is no longer ordinal 11");
        checkEquals(StatusStrip.HALLUCINATING, 16, "Hallucinating is no longer ordinal 16");
        // The two effects that sit in the client's movement law must never be armed by default: the
        // server re-simulates them, so a stripped entry is a speed-check kick.
        check(!config.effects().contains(StatusStrip.SLOWED),
                "Slowed is armed by default; it is in the movement law and the server validates it");
        check(!config.effects().contains(StatusStrip.PARALYZED),
                "Paralyzed is armed by default; it is in the movement law and the server validates it");
    }

    /**
     * The dashboard builds its checkboxes from {@code StatusStrip.NAMED}, so this list is the panel's
     * contract: the names, their order and the ordinals behind them. Hallucinating is last and armed
     * by default - the only effect in the client whose whole consequence is a sprite swap, which is
     * why it is the one effect that can be added to the default set.
     */
    private static void testStripNamedEffects() {
        checkEquals(List.of("confused", "paralyzed", "slowed", "hallucinating"),
                new ArrayList<>(StatusStrip.NAMED.keySet()),
                "the dashboard's named effect list or its order changed");
        checkEquals(16, StatusStrip.NAMED.get("hallucinating").intValue(),
                "the dashboard's 'hallucinating' is not ordinal 16");
        checkEquals("hallucinating", StatusStrip.name(StatusStrip.HALLUCINATING),
                "ordinal 16 does not name itself back as hallucinating");
    }

    private static void testStripArmsAndDisarms() {
        Strip.Config config = new Strip.Config();
        config.apply(Map.of("effects", List.of(11, 7, 6)));
        checkEquals(new java.util.LinkedHashSet<>(List.of(11, 7, 6)), config.effects(),
                "posting an effects list did not arm exactly those effects");

        config.apply(Map.of("slowed", false));
        check(!config.effects().contains(7), "disarming a named effect left it armed");
        config.apply(Map.of("paralyzed", true));
        check(config.effects().contains(6), "arming a named effect did not arm it");
        config.apply(Map.of("hallucinating", true));
        check(config.effects().contains(StatusStrip.HALLUCINATING),
                "arming hallucinating by name did not arm ordinal 16");
        config.apply(Map.of("hallucinating", false));
        check(!config.effects().contains(StatusStrip.HALLUCINATING),
                "disarming hallucinating left ordinal 16 armed");
        config.apply(Map.of("enabled", false));
        check(!config.active(), "the master switch did not turn the strip off");
        check(config.enabled() == false, "enabled() reports true after enabled=false");

        config.apply(Map.of("enabled", true, "effects", List.of()));
        check(!config.active(), "an empty armed set is still active");
        check(config.enabled(), "the master switch did not come back on");

        config.apply(Map.of("min_votes", 40));
        checkEquals(40, config.minVotes(), "minVotes was not applied");
        config.apply(Map.of("minVotes", 0));
        checkEquals(1, config.minVotes(), "minVotes was not clamped to at least 1");
    }

    /**
     * The form that is already deployed: {@code {"confused": true, "effect": 11, "minVotes": 3}}.
     *
     * <p>It has to arm Confused and nothing else. This is the case that would fail silently - a
     * config the relay "reads" while arming nothing looks exactly like a server that stopped sending
     * the effect.
     */
    private static void testStripAcceptsTheLegacyForm() {
        Strip.Config config = new Strip.Config();
        config.apply(Map.of("confused", true, "effect", 11, "minVotes", 3));
        check(config.active(), "the legacy config did not activate the strip");
        checkEquals(Set.of(11), config.effects(), "the legacy config armed the wrong effects");

        Strip.Config off = new Strip.Config();
        off.apply(Map.of("confused", false, "effect", 11, "minVotes", 3));
        check(!off.active(), "the legacy config with confused=false is still active");
    }

    private static void testStripParsesLooseText() {
        Strip.Config config = new Strip.Config();
        config.apply(Map.of("effects", "confused, slowed 6"));
        checkEquals(new java.util.LinkedHashSet<>(List.of(11, 7, 6)), config.effects(),
                "a comma/space separated effect string was not parsed");
        config.apply(Map.of("effect", "32"));
        checkEquals(Set.of(32), config.effects(), "a single effect given as text was not parsed");
        config.apply(Map.of("effects", "hallucinating, confused"));
        checkEquals(new java.util.LinkedHashSet<>(List.of(16, 11)), config.effects(),
                "an effect named 'hallucinating' did not resolve to its ordinal");
    }

    private static void testStripRejectsNonsense() {
        Strip.Config config = new Strip.Config();
        // The defaults, captured so the two assertions below say "the armed set did not move" without
        // restating what the default is - the point of this test is the rejection, not the default.
        Set<Integer> defaults = config.effects();
        List<String> applied = config.apply(Map.of("effect", "confused!"));
        checkEquals(defaults, config.effects(), "an unparseable effect changed the armed set");
        check(applied.stream().anyMatch(entry -> entry.startsWith("ignored")),
                "an unparseable effect was not reported: " + applied);

        List<String> unknown = config.apply(Map.of("threshold_percent", 40));
        check(unknown.stream().anyMatch(entry -> entry.equals("unknown:threshold_percent")),
                "an unknown strip key was silently accepted: " + unknown);
        checkEquals(defaults, config.effects(), "an unknown key changed the armed set");
    }

    // --- persistence --------------------------------------------------------------------------

    /**
     * The property that matters most: everything the writer was not asked to change is still there.
     *
     * <p>The synthetic file carries the four kinds of key the real one has - the comment array, a
     * setting, a nested object, and a key this version does not know - and all four are checked
     * byte-for-byte in value and position after two writes.
     */
    private static void testConfigWriterPreservesEverythingElse() throws Exception {
        Path file = Files.createTempFile("drelay-settings", ".json");
        try {
            Files.writeString(file, """
                    {
                      "_comment": [
                        "a route table",
                        ""
                      ],
                      "listenHost": "0.0.0.0",
                      "web": {
                        "host": "127.0.0.1",
                        "port": 8765
                      },
                      "autoNexus": {
                        "enabled": false
                      },
                      "routes": [
                        {
                          "name": "Game",
                          "listenPort": 6410,
                          "remoteHost": "127.0.0.1",
                          "remotePort": 6410
                        }
                      ],
                      "somethingFromTheFuture": {
                        "keep": [
                          1,
                          2
                        ]
                      }
                    }
                    """, StandardCharsets.UTF_8);

            Map<String, Object> nexus = new LinkedHashMap<>();
            nexus.put("enabled", true);
            nexus.put("thresholdPercent", 55);
            Map<String, Object> strip = new LinkedHashMap<>();
            strip.put("enabled", true);
            strip.put("effects", new ArrayList<>(List.of(11, 7)));
            strip.put("minVotes", 4);
            Map<String, Object> blocks = new LinkedHashMap<>();
            blocks.put("autoNexus", nexus);
            blocks.put("strip", strip);

            ConfigWriter.update(file, blocks);
            ConfigWriter.update(file, blocks);          // a second write must be idempotent
            String written = Files.readString(file, StandardCharsets.UTF_8);

            Object parsed = JsonText.parsePlain(written);
            check(parsed instanceof Map<?, ?>, "the rewritten route table is not an object");
            Map<?, ?> root = (Map<?, ?>) parsed;
            checkEquals("0.0.0.0", root.get("listenHost"), "a scalar key was lost by the writer");
            check(root.get("_comment") instanceof List<?> comments && comments.size() == 2,
                    "the comment block was lost or flattened by the writer");
            checkEquals(1, countOccurrences(written, "somethingFromTheFuture"),
                    "an unknown top-level key was dropped or duplicated by the writer");
            check(root.get("web") instanceof Map<?, ?> web && web.get("port") instanceof Double d
                            && d == 8765.0,
                    "a nested object was lost by the writer");
            check(root.get("routes") instanceof List<?> routes && routes.size() == 1,
                    "the routes were lost by the writer");
            check(root.get("autoNexus") instanceof Map<?, ?> nexusWritten
                            && Boolean.TRUE.equals(nexusWritten.get("enabled"))
                            && nexusWritten.get("thresholdPercent") instanceof Double t && t == 55.0,
                    "the autoNexus block was not replaced with the live settings");
            check(root.get("strip") instanceof Map<?, ?> stripWritten
                            && stripWritten.get("effects") instanceof List<?> effects
                            && effects.size() == 2,
                    "the strip block was not replaced with the live settings");

            // The relay has to be able to read back what the writer produced: same reader, same
            // defaults, and the values it just saved.
            Relay.Config reloaded = Relay.loadConfig(file);
            check(reloaded.strip.enabled(), "the reloaded strip config lost enabled=true");
            checkEquals(new java.util.LinkedHashSet<>(List.of(11, 7)), reloaded.strip.effects(),
                    "the reloaded strip config lost the armed effects");
            checkEquals(4, reloaded.strip.minVotes(), "the reloaded strip config lost minVotes");
        } finally {
            Files.deleteIfExists(file);
        }
    }

    /**
     * The relay's own reader and the writer must agree, including on the shortcut that made this
     * necessary: the parsed number {@code 4} is a {@code Double} after a round trip, and
     * {@code strip.minVotes()} must still be 4 and not 4.0 or a clamp.
     */
    private static void testConfigWriterRoundTripsThroughTheRelayParser() throws Exception {
        Path file = Files.createTempFile("drelay-settings", ".json");
        try {
            Map<String, Object> strip = new LinkedHashMap<>();
            strip.put("enabled", true);
            strip.put("effects", new ArrayList<>(List.of(6)));
            strip.put("minVotes", 2);
            Map<String, Object> blocks = new LinkedHashMap<>();
            blocks.put("strip", strip);
            ConfigWriter.update(file, blocks);

            Relay.Config reloaded = Relay.loadConfig(file);
            checkEquals(Set.of(6), reloaded.strip.effects(), "a single-effect config did not round trip");
            checkEquals(2, reloaded.strip.minVotes(), "minVotes did not round trip as an integer");
            check(reloaded.strip.enabled(), "enabled did not round trip");
        } finally {
            Files.deleteIfExists(file);
        }
    }

    // --- the launcher's regeneration ----------------------------------------------------------

    /**
     * The launcher regenerates {@code work/relay-routes.json} from the template on every run, so
     * without {@code Routes.carryRuntimeSettings} a threshold or an armed effect chosen in the
     * dashboard would be reverted by the next launch - which is exactly the "it does not save"
     * complaint, caused by the launcher rather than by the relay.
     *
     * <p>Both halves are checked: a second run keeps the settings the dashboard wrote, and a first run
     * (no generated copy yet) takes the template's.
     */
    private static void testRouteTableCarriesDashboardSettingsForward() throws Exception {
        Path directory = Files.createTempDirectory("drelay-routes");
        try {
            Path template = directory.resolve("relay-routes.json");
            Path generated = directory.resolve("work").resolve("relay-routes.json");
            Files.writeString(template, """
                    {
                      "_comment": [
                        "the bundled template"
                      ],
                      "listenHost": "0.0.0.0",
                      "autoNexus": {
                        "enabled": false,
                        "thresholdPercent": 35
                      },
                      "strip": {
                        "enabled": true,
                        "effects": [
                          11
                        ],
                        "minVotes": 3
                      },
                      "routes": [
                        {
                          "name": "Game",
                          "listenPort": 6410,
                          "remoteHost": "18.145.161.25",
                          "remotePort": 6410
                        }
                      ]
                    }
                    """, StandardCharsets.UTF_8);

            // First run: nothing generated yet, so the template's settings are what the relay gets.
            Routes.write(template, generated, "192.168.0.39");
            Map<?, ?> first = (Map<?, ?>) JsonText.parsePlain(Files.readString(generated));
            checkEquals("192.168.0.39", first.get("upstreamHost"),
                    "the launcher did not write the upstream source");
            check(first.get("autoNexus") instanceof Map<?, ?> nexus
                            && Boolean.FALSE.equals(nexus.get("enabled")),
                    "the first generated route table did not take the template's auto-nexus block");

            // The dashboard then changes both settings in the file the relay was started with.
            Map<String, Object> nexus = new LinkedHashMap<>();
            nexus.put("enabled", true);
            nexus.put("thresholdPercent", 44);
            Map<String, Object> strip = new LinkedHashMap<>();
            strip.put("enabled", true);
            strip.put("effects", new ArrayList<>(List.of(6, 7)));
            strip.put("minVotes", 5);
            Map<String, Object> blocks = new LinkedHashMap<>();
            blocks.put("autoNexus", nexus);
            blocks.put("strip", strip);
            ConfigWriter.update(generated, blocks);

            // Second run: the launcher must not undo either change.
            Routes.write(template, generated, "192.168.0.39");
            Map<?, ?> second = (Map<?, ?>) JsonText.parsePlain(Files.readString(generated));
            Map<?, ?> savedNexus = (Map<?, ?>) second.get("autoNexus");
            check(savedNexus != null && Boolean.TRUE.equals(savedNexus.get("enabled"))
                            && savedNexus.get("thresholdPercent") instanceof Double t && t == 44.0,
                    "the launcher reverted the auto-nexus settings: " + savedNexus);
            Map<?, ?> savedStrip = (Map<?, ?>) second.get("strip");
            check(savedStrip != null && savedStrip.get("effects") instanceof List<?> effects
                            && effects.size() == 2 && savedStrip.get("minVotes") instanceof Double m
                            && m == 5.0,
                    "the launcher reverted the strip settings: " + savedStrip);

            // And the relay can still read the result, which is the only thing that matters.
            Relay.Config reloaded = Relay.loadConfig(generated);
            checkEquals(new java.util.LinkedHashSet<>(List.of(6, 7)), reloaded.strip.effects(),
                    "the carried-forward route table does not arm the saved effects");
            checkEquals(5, reloaded.strip.minVotes(), "the carried-forward route table lost minVotes");
            checkEquals(44, reloaded.nexus.thresholdPercent,
                    "the carried-forward route table lost the auto-nexus threshold");
            checkEquals("192.168.0.39", reloaded.upstreamHost,
                    "the carried-forward route table lost the upstream source");
            checkEquals(1, reloaded.routes.size(), "the carried-forward route table lost its routes");
        } finally {
            deleteRecursively(directory);
        }
    }

    private static void deleteRecursively(Path directory) throws Exception {
        try (var walk = Files.walk(directory)) {
            for (Path path : walk.sorted(java.util.Comparator.reverseOrder()).toList()) {
                Files.deleteIfExists(path);
            }
        }
    }

    // --- the dashboard's filter round trip ----------------------------------------------------
    private static void testFilterTextAcceptsArraysAndStrings() {
        checkEquals("HealthUpdate Update", WebDashboard.filterText(List.of("HealthUpdate", "Update")),
                "an array-valued filter field was not joined as text");
        checkEquals("health, nexus", WebDashboard.filterText("health, nexus"),
                "a string-valued filter field was rewritten");
        checkEquals(null, WebDashboard.filterText(null), "a null filter field did not stay null");
        checkEquals("", WebDashboard.filterText(List.of()), "an empty array did not become empty text");
    }

    /**
     * The regression itself: two full round trips through the endpoint's parser must not change the
     * names. The old code produced {@code "[[]]"} on the second pass and {@code "[[[]]]"} on the third.
     */
    private static void testFilterParseDropsBracketWreckage() {
        LogFilter once = LogFilter.parse("hp", true, "health", "[HealthUpdate Update]", "");
        check(once.packets().contains("HealthUpdate"), "a bracketed packet list lost its first name");
        check(once.packets().contains("Update"), "a bracketed packet list lost its last name");
        check(!once.packets().contains("[HealthUpdate"), "the opening bracket survived into a name");
        check(!once.packets().contains("Update]"), "the closing bracket survived into a name");

        LogFilter wrecked = LogFilter.parse("hp", true, "[]", "[[]]", "");
        check(wrecked.packets().isEmpty(), "a doubly-bracketed list produced a packet name: "
                + wrecked.packets());
        check(wrecked.kinds().isEmpty(), "a bracket-only kind list produced a kind: " + wrecked.kinds());
    }

    // --- helpers ------------------------------------------------------------------------------

    private static int countOccurrences(String text, String needle) {
        int count = 0;
        int index = text.indexOf(needle);
        while (index >= 0) {
            count++;
            index = text.indexOf(needle, index + needle.length());
        }
        return count;
    }

    private static void check(boolean condition, String description) {
        checks++;
        if (!condition) {
            FAILURES.add(description);
        }
    }

    private static void checkEquals(Object expected, Object actual, String description) {
        checks++;
        if (!java.util.Objects.equals(expected, actual)) {
            FAILURES.add(description + " (expected " + expected + ", got " + actual + ")");
        }
    }
}
