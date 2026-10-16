package networking;

import java.awt.Desktop;
import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.URI;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.Optional;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;

/**
 * What runs when the released jar is started by double-click or by {@code java -jar drelay.jar}.
 *
 * <p>The relay itself is {@link Relay}. This entry point exists because getting the relay into the
 * client's path takes more than starting a JVM, and every one of those extra steps has a reason:
 *
 * <ol>
 *   <li><strong>Elevate once.</strong> Claiming a game server's address on the loopback interface
 *       needs the administrator token, and elevation cannot be requested from inside a running
 *       process - it has to happen before anything else is useful.</li>
 *   <li><strong>Update before claiming.</strong> A user who downloads one jar should not have to
 *       check the releases page again. Replacing the jar releases the claims on the way out, so the
 *       update has to happen while nothing is claimed.</li>
 *   <li><strong>Refresh the addresses.</strong> The game server is reached by a bare IP that rotates,
 *       so the launcher asks the game's own API and DNS before every run.</li>
 *   <li><strong>Claim and verify.</strong> The addresses are claimed on loopback before the client
 *       starts, so the client's own dial is delivered into the local stack where the relay listens.</li>
 *   <li><strong>Start the relay as a child process.</strong> It must be a child, not this JVM: the
 *       next auto-update replaces the jar this process runs from, and on Windows that only works
 *       once the process holding it has exited.</li>
 *   <li><strong>Release the claims on the way out.</strong> A claim that outlives the relay breaks
 *       the game until it is released.</li>
 * </ol>
 *
 * <p>Every step is skippable from the command line, which is what makes the launcher testable
 * without touching the machine's networking. {@code java -jar drelay.jar --help} lists them.
 */
public final class Launcher {

    /**
     * The property every relay child is started with, and the only reliable way to tell a relay apart
     * from the launcher that started it.
     *
     * <p>Both processes are {@code java -jar drelay.jar}, so a search for "drelay" matches the
     * launcher itself - which is how an earlier version of this cleanup killed its own process during
     * startup, leaving the user with a claimed address, no relay, and no explanation. Searching for a
     * property only the child carries cannot match the parent, and cannot match the PowerShell
     * command that runs the search either.
     */
    private static final String RELAY_MARKER = "-Ddrelay.relay=true";

    /** Flags this launcher owns; anything else on the command line belongs to the relay. */
    private static final List<String> OWN_FLAGS = List.of(
            "--no-update", "--no-claim", "--no-elevate", "--no-browser", "--config",
            "--relay", "--elevate", "--update", "update", "--check-update", "check-update",
            "--release-claims", "--stop", "--install", "--help", "-h", "--version");

    private final Path root;
    private final List<String> arguments;
    private final boolean update;
    private final boolean elevate;
    private final boolean claim;
    private final boolean browser;
    private final String configName;

    /** Everything a run can be asked to do. */
    private enum Command {
        /** Default: elevate, update, claim, start, release. */
        RUN,
        /** The relay alone, in this JVM: what the offline tests and a developer use. */
        RELAY,
        /** Internal: the elevated half of a RUN. */
        ELEVATE,
        /** Internal: install the newest release, if any, and restart. */
        UPDATE,
        /** Internal: give every claimed address back. */
        RELEASE_CLAIMS,
        /** Stop a relay left running by an earlier launcher, and release its claims. */
        STOP,
        /** Internal: extract or upgrade the bundled files and stop. */
        INSTALL,
        /** Ask GitHub whether a newer release exists, without installing it. */
        CHECK_UPDATE
    }

    private Launcher(Path root, List<String> arguments) {
        this.root = root;
        this.arguments = List.copyOf(arguments);
        this.update = !flag("--no-update");
        this.elevate = !flag("--no-elevate");
        this.claim = !flag("--no-claim");
        this.browser = !flag("--no-browser");
        this.configName = value("--config").orElse("work/relay-routes.json");
    }

    public static void main(String[] args) throws Exception {
        var launcher = new Launcher(workingDirectory(), List.of(args));
        if (launcher.flag("--help") || launcher.flag("-h")) {
            launcher.printHelp();
            return;
        }
        if (launcher.flag("--version")) {
            IO.println("drelay " + Resources.VERSION);
            return;
        }
        System.exit(launcher.run());
    }

    private int run() throws Exception {
        return switch (command()) {
            case RELAY -> runRelayInThisJvm();
            case UPDATE -> installUpdate(relayArguments());
            case CHECK_UPDATE -> checkUpdate();
            case RELEASE_CLAIMS -> {
                // The claim script reads its addresses from the route table, so the bundled copy has
                // to exist before it can release anything - including on a machine where the relay
                // was never started and only a claim was left behind. It is given the template rather
                // than the generated copy, because releasing a claim must not depend on a run having
                // happened: the template is there as soon as the jar is.
                Sync.install(root);
                yield releaseClaims(root.resolve("relay-routes.json")).exitCode();
            }
            case INSTALL -> {
                // Extract and upgrade only: no address is resolved, so this works offline and is
                // what a helper script calls when it needs the PowerShell files to exist.
                Sync.install(root).forEach(note -> IO.println(note));
                yield 0;
            }
            case STOP -> stopEverything();
            case ELEVATE -> startWithClaims(relayArguments());
            case RUN -> runAsUser(relayArguments());
        };
    }

    /**
     * The unelevated half: hand over to an elevated copy of this jar when claiming needs it,
     * otherwise carry on here.
     */
    private int runAsUser(List<String> relayArguments) throws Exception {
        boolean needsElevation = claim && PowerShell.isWindows();
        if (needsElevation && elevate && !PowerShell.isElevated()) {
            return relaunchElevated(relayArguments);
        }
        syncAndRefreshRoutes();
        // Clear a relay from an earlier run before starting a new one, whichever way this run
        // proceeds: a leftover holds the listen ports, and the second relay would fail to bind them -
        // presenting as a claim problem or a client problem rather than the process that caused it.
        // This is deliberately before the branches below, because --no-claim needs the ports too.
        stopLeftoverRelays();
        if (!claim) {
            IO.println("--no-claim: nothing is claimed, so the client reaches the real server directly.");
            return runRelay(relayArguments);
        }
        return startWithClaims(relayArguments);
    }

    /**
     * The elevated half: update, refresh, claim, run, release.
     *
     * <p>Ordering matters here and is not arbitrary. The update comes second because replacing the jar
     * means this process exits, and exiting releases the claims - so an update must happen while
     * nothing is claimed. The claims are released in a {@code finally} because an unclaimed address
     * leaves the game unable to reach its own server, which is much worse than a log line.
     */
    private int startWithClaims(List<String> relayArguments) throws Exception {
        // A leftover relay was already stopped by runAsUser, before it chose this branch, so the
        // listen ports are free by the time a claim is worth making.
        syncAndRefreshRoutes();

        if (update) {
            int code = installUpdate(relayArguments);
            if (code == 10) {
                IO.println("The updated jar starts in a moment.");
                return code;
            }
        }

        if (!claim) {
            return runRelay(relayArguments);
        }
        if (!PowerShell.isWindows()) {
            IO.println("Address claims are a Windows feature. On " + System.getProperty("os.name")
                    + " the relay can still forward a route whose destination is not a real server address.");
            return runRelay(relayArguments);
        }

        PowerShell.Result claimed = claimAddresses(configPath());
        if (claimed.exitCode() == 3) {
            // The script refused: a route address belongs to a real interface. Claiming it would take
            // the machine off the network, so this stops rather than starting a proxy that cannot work.
            return 3;
        }
        if (!claimed.ok()) {
            IO.println();
            IO.println("The addresses could not all be claimed (exit " + claimed.exitCode() + ").");
            IO.println("The relay is starting anyway: whichever route is not captured will bypass it.");
            IO.println("Rerun with --no-claim to skip this step, or see docs/PROTOCOL.md for the claim.");
        }

        try {
            return runRelay(relayArguments);
        } finally {
            IO.println();
            IO.println("Releasing the claimed addresses...");
            PowerShell.Result released = releaseClaims(configPath());
            if (!released.ok()) {
                IO.println("  not every claim was released. Finish with:");
                IO.println("    java -jar \"" + jarOrSelf() + "\" --release-claims");
            }
            IO.println("Stopped.");
        }
    }

    // ---------------------------------------------------------------------------------------
    // The relay process
    // ---------------------------------------------------------------------------------------

    /** Runs {@link Relay} in this JVM. Used by {@code --relay}, from a class directory, and by tests. */
    private int runRelayInThisJvm() throws Exception {
        // --relay means "run exactly what I asked for", so the arguments are passed through with the
        // launcher's own flags removed and no config path invented. Everything else about where the
        // config lives is the relay's default or the caller's explicit choice.
        var command = new ArrayList<String>();
        for (int i = 0; i < arguments.size(); i++) {
            String argument = arguments.get(i);
            if (argument.equalsIgnoreCase("--config")) {
                i++;
                continue;
            }
            if (argument.toLowerCase(Locale.ROOT).startsWith("--config=")) {
                continue;
            }
            if (isLauncherOwn(argument)) {
                continue;
            }
            command.add(argument);
        }
        Relay.main(command.toArray(String[]::new));
        return 0;
    }

    /**
     * Whether an argument belongs to this launcher rather than to the relay.
     *
     * <p>These are the names the relay does not know. Passing one on would be read as its config
     * path, because everything that is not a {@code -D} property is.
     */
    private static boolean isLauncherOwn(String argument) {
        if (argument.equals(RELAY_MARKER)) {
            return true;
        }
        String lower = argument.toLowerCase(Locale.ROOT);
        if (lower.equals("--no-update") || lower.equals("--no-browser") || lower.equals("--no-claim")) {
            return true;
        }
        return OWN_FLAGS.stream().anyMatch(flag -> flag.equalsIgnoreCase(argument));
    }

    /**
     * Starts the relay as a child JVM with this process's console attached.
     *
     * <p>The child inherits the console rather than a pipe so that the relay's own output - the route
     * table, the claim checks, the dashboard URL, every packet line - is what the user sees. A pipe
     * would work, but it also gives this process a reason to buffer, and then lose, a session's
     * output when it is killed.
     *
     * <p>Falls back to this JVM when there is no jar to start: a developer running from
     * {@code target/classes} gets the relay directly instead of a confusing second-JVM error.
     */
    private int runRelay(List<String> relayArguments) throws Exception {
        Path jar = Updater.runningJar().orElse(null);
        if (jar == null) {
            Relay.main(relayArguments.toArray(String[]::new));
            return 0;
        }

        var command = new ArrayList<String>();
        command.add(Updater.javaExecutable());
        command.add("-jar");
        command.add(jar.toAbsolutePath().toString());
        command.add("--relay");
        command.add(RELAY_MARKER);
        command.addAll(relayArguments);
        // The run config is named explicitly, and it is the generated copy under work/ - never the
        // bare template beside the jar. The two differ in exactly the keys the launcher computes:
        // upstreamHost above all, without which the relay refuses to start once an address is
        // claimed. Leaving the relay to its default ("relay-routes.json", the template) is how a
        // claimed run died with "no upstreamHost is set" while the correct file sat unused.
        command.add(configPath().toAbsolutePath().toString());

        IO.println("drelay " + Resources.VERSION + " - starting the relay (this window shows its log)");
        IO.println();
        Process relay = new ProcessBuilder(command)
                .directory(root.toFile())
                // Not inheritIO: the relay's output is also written to work/logs/relay-<run>.out so a
                // run can be diagnosed after its console window is gone. That file is the only record
                // of what the relay itself said - the launcher's own output stops at "starting the
                // relay", and a user reporting "it exited immediately" has nothing else to send.
                .redirectErrorStream(true)
                .start();
        Path relayLog = relayOutputPath();
        pump(relay.getInputStream(), relayLog);

        if (browser) {
            openDashboardWhenReady();
        }

        // Ctrl+C at the console reaches the whole process group, so the relay sees it directly; this
        // hook covers a launcher that is stopped some other way, so the claims are still released.
        var stopper = new Thread(() -> stop(relay), "relay-stop");
        Runtime.getRuntime().addShutdownHook(stopper);
        int exit = relay.waitFor();
        try {
            Runtime.getRuntime().removeShutdownHook(stopper);
        } catch (IllegalStateException ignored) {
            // Already shutting down, which is the other path that stops the relay.
        }
        if (exit != 0) {
            IO.println();
            IO.println("The relay exited with code " + exit + ".");
            IO.println("  its output is also in " + relayLog.toAbsolutePath());
        }
        return exit;
    }

    /** Where the relay's own console output is mirrored, beside the event log. */
    private Path relayOutputPath() {
        String configured = configName == null ? "work/relay-routes.json" : configName;
        Path configDirectory = Path.of(configured).getParent();
        Path logs = (configDirectory == null ? Path.of("work") : configDirectory.resolve("logs"));
        return root.resolve(logs).resolve("relay-console.out");
    }

    /**
     * Copies the relay's output to this process's console and to a file.
     *
     * <p>On a virtual thread, because it is pure blocking I/O and this process does nothing else
     * while a session runs. The file is opened in append mode: an update restarts the relay, and the
     * interesting sequence is usually the one that spans that restart.
     */
    private void pump(InputStream from, Path to) {
        Thread.startVirtualThread(() -> {
            try {
                Files.createDirectories(to.toAbsolutePath().getParent());
                try (var reader = new BufferedReader(new InputStreamReader(from, StandardCharsets.UTF_8));
                     var writer = Files.newBufferedWriter(to, StandardCharsets.UTF_8,
                             java.nio.file.StandardOpenOption.CREATE,
                             java.nio.file.StandardOpenOption.APPEND)) {
                    writer.write("---- drelay " + Resources.VERSION + " at " + java.time.LocalDateTime.now()
                            + System.lineSeparator());
                    String line;
                    while ((line = reader.readLine()) != null) {
                        IO.println(line);
                        writer.write(line);
                        writer.write(System.lineSeparator());
                        writer.flush();
                    }
                }
            } catch (IOException e) {
                // The relay keeps running; only the mirror is lost, and that must never be fatal.
                IO.println("(could not mirror the relay's output to " + to + ": " + e.getMessage() + ")");
            }
        });
    }

    private static void stop(Process relay) {
        relay.destroy();
        try {
            if (!relay.waitFor(10, TimeUnit.SECONDS)) {
                relay.destroyForcibly();
            }
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        }
    }

    /**
     * Stops a relay left behind by a launcher that was killed, and says so.
     *
     * <p>The ordinary path already stops the child: Ctrl+C reaches the whole process group, and the
     * shutdown hook covers most other ways this process can end. "Most" is not all - a forced
     * {@code Stop-Process}, a crash, or a logoff can leave the child running with the listen ports
     * held - so the check is repeated here, where a leftover is still cheap to fix and where its
     * consequence (a new relay that cannot bind) would otherwise be reported as something else.
     *
     * <p>Only a relay is stopped, never this process: the search is for {@link #RELAY_MARKER}, which
     * the child is started with and the launcher does not have.
     *
     * @return whether anything was stopped
     */
    private boolean stopLeftoverRelays() {
        int pid = PowerShell.findProcess(RELAY_MARKER);
        if (pid < 0) {
            return false;
        }
        IO.println("A relay from an earlier run is still running (pid " + pid + "); stopping it first.");
        IO.println("  it holds the listen ports, so a new relay could not bind them.");
        int stopped = PowerShell.stopProcesses(RELAY_MARKER);
        IO.println("  stopped " + stopped + " relay process(es).");
        return stopped > 0;
    }

    /** Claims every route address, letting the claim script print its own progress. */
    private PowerShell.Result claimAddresses(Path config) throws IOException {
        Path script = root.resolve("tools").resolve("Set-AddressClaim.ps1");
        IO.println("Claiming the game server addresses on the loopback interface...");
        IO.println("  this is what puts the relay in the client's path: no driver, no packet rewriting");
        return PowerShell.run(script, List.of("-Config", config.toAbsolutePath().toString()), root, true);
    }

    private PowerShell.Result releaseClaims(Path config) throws IOException {
        Path script = root.resolve("tools").resolve("Set-AddressClaim.ps1");
        if (!Files.exists(script)) {
            IO.println("  no claim script found; nothing to release");
            return new PowerShell.Result(0, "");
        }
        return PowerShell.run(script, List.of("-Config", config.toAbsolutePath().toString(), "-Release"),
                root, true);
    }

    // ---------------------------------------------------------------------------------------
    // Elevation
    // ---------------------------------------------------------------------------------------

    /**
     * Re-runs this jar from an elevated PowerShell and returns its exit code.
     *
     * <p>Elevation cannot be requested from inside a running process, so this spawns a new one and
     * waits for it. Only the claim needs the token - which is why {@code --no-claim} exists, and why
     * a run that does not claim is never elevated.
     */
    private int relaunchElevated(List<String> relayArguments) throws IOException {
        Path jar = Updater.runningJar().orElse(null);
        if (jar == null) {
            IO.println("Not running from a jar, so elevation cannot be requested automatically.");
            IO.println("Claim from an elevated prompt instead:");
            IO.println("  tools\\Set-AddressClaim.ps1 -Config " + configPath().toAbsolutePath());
            IO.println("  then start the relay with --no-claim --no-elevate");
            return 2;
        }
        IO.println("Administrator rights are needed to claim the server addresses, so Windows will ask");
        IO.println("for them now. Declining is safe: rerun with --no-claim to skip the claim entirely.");

        var forwarded = new ArrayList<String>();
        forwarded.add("--elevate");
        forwarded.add("--no-elevate");
        forwarded.addAll(relayArguments);

        String argumentList = "'-jar', " + PowerShell.quote(jar.toAbsolutePath().toString()) + ", "
                + String.join(", ", forwarded.stream().map(PowerShell::quote).toList());
        String source = "$p = Start-Process -FilePath " + PowerShell.quote(Updater.javaExecutable())
                + " -ArgumentList @(" + argumentList + ")"
                + " -WorkingDirectory " + PowerShell.quote(root.toAbsolutePath().toString())
                + " -Verb RunAs -Wait -PassThru; exit $p.ExitCode";
        return PowerShell.inline(source, false).exitCode();
    }

    // ---------------------------------------------------------------------------------------
    // Update
    // ---------------------------------------------------------------------------------------

    /**
     * Checks for a newer release and, if there is one, replaces this jar and restarts.
     *
     * <p>Exit code 10 means "a new jar is being installed": the caller must stop now, because the
     * file it is running from is about to be replaced, and the helper has already been told how to
     * start it again. Every other failure is reported and then ignored - an unreachable GitHub is
     * not a reason to refuse to run.
     */
    private int installUpdate(List<String> relayArguments) throws Exception {
        Path jar = Updater.runningJar().orElse(null);
        Optional<Updater.Available> available = checkQuietly();
        if (available.isEmpty()) {
            IO.println("drelay " + Resources.VERSION + " is up to date.");
            return 0;
        }
        Updater.Available release = available.get();
        if (jar == null) {
            IO.println("drelay " + release.version() + " is available, but this is not a jar install;"
                    + " get it from https://github.com/" + Resources.REPOSITORY + "/releases/latest");
            return 0;
        }

        Path downloaded = jar.toAbsolutePath().getParent().resolve("drelay-" + release.version() + ".jar.part");
        IO.println("Updating drelay " + Resources.VERSION + " -> " + release.version()
                + " (%.1f MB)".formatted(release.sizeBytes() / 1_048_576.0));
        try {
            Updater.download(release, downloaded);
        } catch (Exception e) {
            Files.deleteIfExists(downloaded);
            IO.println("Update failed: " + e.getMessage());
            IO.println("Starting the installed version instead.");
            return 0;
        }

        // The elevated copy is relaunched elevated, so "no elevation" stays in the forwarded
        // arguments: the new process must not ask Windows for the token a second time.
        var relaunch = new ArrayList<String>();
        relaunch.add("--no-elevate");
        relaunch.add("--elevate");
        relaunch.addAll(relayArguments);
        Updater.scheduleInstall(jar.toAbsolutePath(), downloaded, relaunch);

        IO.println("A new jar is being installed; drelay restarts in a moment.");
        IO.println("  what the updater did is logged to " + Updater.logPath(jar.toAbsolutePath()));
        return 10;
    }

    /**
     * {@code --stop}: end every running relay and give the claimed addresses back.
     *
     * <p>The repair for the state that is easy to end up in and hard to read - a relay still holding
     * 6410-6412 after a console was closed, and three addresses claimed on the loopback interface.
     * It is also what {@code drelay.ps1 -Stop} runs.
     */
    private int stopEverything() throws IOException {
        Sync.install(root);
        int stopped = 0;
        // Older releases' relays are found by their command line, newer ones by their marker. Both
        // are attempted because a stale relay from either is what this command exists to clear.
        for (String needle : List.of(RELAY_MARKER, "--relay")) {
            stopped += PowerShell.stopProcesses(needle);
        }
        IO.println(stopped == 0
                ? "No running relay was found."
                : "Stopped " + stopped + " relay process(es).");
        IO.println();
        IO.println("Releasing the claimed addresses...");
        PowerShell.Result released = releaseClaims(root.resolve("relay-routes.json"));
        if (!released.ok()) {
            IO.println("Some claims could not be released (exit " + released.exitCode() + ").");
            IO.println("Run this from an elevated prompt to finish the job.");
        }
        return released.exitCode();
    }

    /** Asks GitHub for a newer release. A failure here is reported, never fatal. */
    private Optional<Updater.Available> checkQuietly() {
        try {
            return Updater.check();
        } catch (Exception e) {
            IO.println("Could not check for updates: " + e.getMessage());
            return Optional.empty();
        }
    }

    private int checkUpdate() {
        Optional<Updater.Available> available = checkQuietly();
        if (available.isEmpty()) {
            IO.println("drelay " + Resources.VERSION + " is the newest release of " + Resources.REPOSITORY + ".");
        } else {
            IO.println("drelay " + available.get().version() + " is available; you have " + Resources.VERSION + ".");
            IO.println("  install it with: java -jar drelay.jar --update");
            IO.println("  or from: https://github.com/" + Resources.REPOSITORY + "/releases/latest");
        }
        return 0;
    }

    // ---------------------------------------------------------------------------------------
    // Bundled files, addresses and the dashboard
    // ---------------------------------------------------------------------------------------

    /** Extracts or upgrades the bundled files, then refreshes the addresses the route table pins. */
    private void syncAndRefreshRoutes() throws IOException {
        List<String> notes = Sync.install(root);
        Path template = root.resolve("relay-routes.json");
        Path generated = root.resolve("work").resolve("relay-routes.json");
        // The relay refuses to start when a route is claimed and no upstreamHost is set, and the only
        // correct value is this machine's LAN address, which the bundled template cannot know. It is
        // written into a generated copy so the template stays a template and this machine's address
        // never ends up in a file a user is asked to share or diff.
        String upstream = claim ? lanAddress().orElse(null) : null;
        Routes.Refresh refresh = Routes.write(template, generated, upstream);

        if (!notes.isEmpty() || !refresh.changes().isEmpty() || !refresh.failures().isEmpty()) {
            IO.println("drelay " + Resources.VERSION + " - files in " + root.toAbsolutePath());
            notes.forEach(note -> IO.println("  " + note));
            refresh.changes().forEach(change -> IO.println("  address refreshed: " + change));
            refresh.failures().forEach(failure -> IO.println(
                    "  address not refreshed: " + failure + " - keeping the address already in the file"));
        }
        if (upstream == null && claim) {
            IO.println("  no LAN address could be determined; pass one with -Ddrelay.upstreamHost=<address>");
        }
    }

    /**
     * This machine's LAN address: the source the relay binds its own upstream dials to.
     *
     * <p>Found by asking the OS which local address a route to the internet would use. A UDP socket
     * that never sends a packet is enough to get that answer, and it needs neither elevation nor a
     * reachable name server.
     */
    private Optional<String> lanAddress() {
        for (String probe : List.of("8.8.8.8", "1.1.1.1")) {
            try (var socket = new DatagramSocket()) {
                socket.connect(InetAddress.getByName(probe), 53);
                InetAddress local = socket.getLocalAddress();
                if (local != null && !local.isLoopbackAddress() && !local.isLinkLocalAddress()
                        && !local.isAnyLocalAddress()) {
                    return Optional.of(local.getHostAddress());
                }
            } catch (Exception ignored) {
                // Offline, or no route out: try the next probe, then let the caller fall back.
            }
        }
        return Optional.empty();
    }

    /**
     * Opens the dashboard in the default browser once the relay has reported its URL.
     *
     * <p>The URL cannot be known in advance - the relay walks forward when its port is busy - so the
     * relay records the address it bound in {@code work/dashboard.txt}, beside the config. Polling
     * that file opens exactly the address the relay is serving, instead of a guess at the default
     * port that would show the user an empty page when the port had moved.
     */
    private void openDashboardWhenReady() {
        CompletableFuture.runAsync(() -> {
            Path hint = root.resolve("work").resolve("dashboard.txt");
            for (int attempt = 0; attempt < 60; attempt++) {
                try {
                    if (Files.exists(hint)) {
                        String url = Files.readString(hint).trim();
                        if (!url.isEmpty()) {
                            openBrowser(url);
                            return;
                        }
                    }
                    Thread.sleep(500);
                } catch (IOException e) {
                    return;
                } catch (InterruptedException e) {
                    Thread.currentThread().interrupt();
                    return;
                }
            }
        });
    }

    private void openBrowser(String url) {
        IO.println("Dashboard: " + url);
        try {
            if (Desktop.isDesktopSupported() && Desktop.getDesktop().isSupported(Desktop.Action.BROWSE)) {
                Desktop.getDesktop().browse(URI.create(url));
                return;
            }
        } catch (Exception ignored) {
            // Headless, or a desktop that is not there: fall through to the Windows shell.
        }
        try {
            new ProcessBuilder("rundll32", "url.dll,FileProtocolHandler", url).start();
        } catch (IOException e) {
            IO.println("Open " + url + " in a browser to watch the session.");
        }
    }

    // ---------------------------------------------------------------------------------------
    // Command line
    // ---------------------------------------------------------------------------------------

    private Command command() {
        for (String argument : arguments) {
            switch (argument.toLowerCase(Locale.ROOT)) {
                case "--relay" -> {
                    return Command.RELAY;
                }
                case "--elevate" -> {
                    return Command.ELEVATE;
                }
                case "--update", "update" -> {
                    return Command.UPDATE;
                }
                case "--check-update", "check-update" -> {
                    return Command.CHECK_UPDATE;
                }
                case "--release-claims" -> {
                    return Command.RELEASE_CLAIMS;
                }
                case "--stop" -> {
                    return Command.STOP;
                }
                case "--install" -> {
                    return Command.INSTALL;
                }
                default -> {
                    // A bare .json argument is the relay's config path, read by the RELAY command.
                }
            }
        }
        return Command.RUN;
    }

    /**
     * The arguments the relay child should be started with: system properties, and nothing else.
     *
     * <p>Everything the launcher owns is dropped, because the relay does not know those names and
     * would treat one as a config path - {@link #RELAY_MARKER} in particular, which is passed as a
     * JVM property on the child's command line and is therefore already in effect before the relay's
     * arguments are read. A bare path is dropped too: the launcher names the run config itself, and
     * it must be the generated copy under {@code work/} rather than the bare template.
     */
    private List<String> relayArguments() {
        var kept = new ArrayList<String>();
        for (int i = 0; i < arguments.size(); i++) {
            String argument = arguments.get(i);
            if (argument.startsWith("drelay.relay=")) {
                continue;                       // the marker, if the -D prefix was split off
            }
            if (argument.equalsIgnoreCase("--config")) {
                i++;                            // skip its value as well
                continue;
            }
            if (argument.toLowerCase(Locale.ROOT).startsWith("--config=")) {
                continue;
            }
            if (isLauncherOwn(argument)) {
                continue;
            }
            // Only -D properties survive. A bare argument is a config path, and this method's caller
            // supplies that path from configPath().
            if (!argument.startsWith("-D")) {
                continue;
            }
            kept.add(argument);
        }
        return kept;
    }

    private boolean flag(String name) {
        return arguments.stream().anyMatch(argument -> argument.equalsIgnoreCase(name));
    }

    /** The value after {@code --name}, or after {@code --name=}. */
    private Optional<String> value(String name) {
        for (int i = 0; i < arguments.size(); i++) {
            String argument = arguments.get(i);
            if (argument.equalsIgnoreCase(name) && i + 1 < arguments.size()) {
                return Optional.of(arguments.get(i + 1));
            }
            String prefix = name.toLowerCase(Locale.ROOT) + "=";
            if (argument.toLowerCase(Locale.ROOT).startsWith(prefix)) {
                return Optional.of(argument.substring(prefix.length()));
            }
        }
        return Optional.empty();
    }

    private Path configPath() {
        return root.resolve(configName);
    }

    /** The jar this process runs from, or a placeholder when it is not running from one. */
    private String jarOrSelf() {
        return Updater.runningJar().map(path -> path.toAbsolutePath().toString()).orElse("drelay.jar");
    }

    private void printHelp() {
        IO.println("""
                drelay %s - a local relay and packet inspector for Darza's Dominion.

                Usage:
                  java -jar drelay.jar [options]

                The launcher asks for the administrator rights that claiming an address needs, updates
                itself, refreshes the server addresses, claims them, starts the relay and releases the
                claims when it stops. The relay's own options are passed through, so -Ddrelay.* works:

                  java -jar drelay.jar -Ddrelay.web.port=0
                  java -jar drelay.jar --no-claim --no-update

                Options:
                  --no-update         do not check GitHub for a newer release
                  --no-claim          do not claim any address and do not ask for elevation; nothing
                                      is captured, which only helps when a route's destination is a
                                      local peer
                  --no-elevate        never re-run this jar to obtain administrator rights
                  --no-browser        do not open the dashboard in a browser
                  --config <path>     route table to use (default: work/relay-routes.json)

                Commands:
                  --check-update      report whether a newer release exists; change nothing
                  --update            install the newest release and restart
                  --stop              stop a running relay and give the claimed addresses back
                  --relay             run the relay alone in this JVM (no update, no claim)
                  --release-claims    give every claimed address back
                  --install           extract or upgrade the bundled files, then stop
                  --version           print the version
                  --help              this text

                The dashboard URL is printed at startup; logs are written to work/logs/.
                """.formatted(Resources.VERSION));
    }

    /**
     * Where the bundled files live: beside the jar, so one downloaded file means one directory.
     *
     * <p>A jar in a read-only location cannot work anyway - the relay writes its event log beside
     * itself - so this does not try to be clever about falling back somewhere else.
     */
    private static Path workingDirectory() {
        return Updater.runningJar()
                .map(path -> path.toAbsolutePath().getParent())
                .orElseGet(() -> Path.of(System.getProperty("user.dir", ".")).toAbsolutePath());
    }
}
