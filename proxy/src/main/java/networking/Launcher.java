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
import java.util.concurrent.atomic.AtomicBoolean;

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
 *   <li><strong>Release the captures on the way out.</strong> A claim or a hosts block that outlives
 *       the relay breaks the game until it is released, so both are given back in a {@code finally}
 *       <em>and</em> from a shutdown hook - Ctrl+C, a closed console and a logoff reach only the
 *       hook, and that is the path that used to leave them behind.</li>
 * </ol>
 *
 * <p>Every step is skippable from the command line, which is what makes the launcher testable
 * without touching the machine's networking. {@code java -jar drelay.jar --help} lists them.
 *
 * <p>{@code --redirect} replaces the address claim with the packet redirector
 * ({@code drelay-redirect.exe}, built from {@code proxy/redirector}). The claim assigns the game
 * server's address to the loopback interface, and on some Windows 11 builds the relay's own dial from
 * that address is delivered back to itself and dropped - the handoff in
 * {@code docs/proxy/HANDOFF-WINDIVERT.md} records the measurement. The redirector instead hands the
 * client's own packets to the relay's listener as though the server had sent them, so the server's
 * address is never made local and the relay's upstream dial is an ordinary connection. It is started
 * and stopped with the relay, by the same disposal path, because a redirector left running after the
 * relay exits points the client at a dead listener.
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

    /** The redirector's image name, which is also how a leftover one is found and stopped. */
    private static final String REDIRECTOR_IMAGE = "drelay-redirect.exe";

    /**
     * One firewall rule owns the inbound allowance, so adding it twice cannot stack up rules.
     *
     * <p>The underscore is deliberate: the name is passed to {@code netsh} as a bare token, and a
     * space would split it into an option.
     */
    private static final String FIREWALL_RULE = "drelay_redirect";

    /** The property the relay reads to know a redirector owns the capture. */
    private static final String REDIRECT_PROPERTY = "drelay.redirect";

    /** Flags this launcher owns; anything else on the command line belongs to the relay. */
    private static final List<String> OWN_FLAGS = List.of(
            "--no-update", "--no-claim", "--no-elevate", "--no-browser", "--config",
            "--redirect", "--no-firewall",
            "--relay", "--elevate", "--update", "update", "--check-update", "check-update",
            "--release-claims", "--stop", "--install", "--help", "-h", "--version");

    private final Path root;
    private final List<String> arguments;
    private final boolean update;
    private final boolean elevate;
    private final boolean claim;
    private final boolean redirect;
    private final boolean firewall;
    private final boolean browser;
    private final String configName;

    /**
     * Whether this run's machine-wide side effects - the loopback claims and the hosts block - have
     * already been given back.
     *
     * <p>One flag, shared by the normal exit path and the shutdown hook, so exactly one of them does
     * the work. Both can run, and on Ctrl+C they usually race: the relay child dies, its {@code
     * waitFor} returns, the main thread reaches its {@code finally}, and the JVM's shutdown sequence
     * is running the hook at the same time. Releasing twice is not harmful, but a second release
     * while the first is mid-write is, so the work is claimed once and the other caller does nothing.
     */
    private final AtomicBoolean released = new AtomicBoolean();

    /**
     * The packet redirector child, when this run started one.
     *
     * <p>Static because {@link #releaseCapture} - which runs from a shutdown hook - must be able to
     * stop it without being handed it, exactly as the relay child is handled.
     */
    private static volatile Process redirector;

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
        this.redirect = flag("--redirect") || Boolean.parseBoolean(System.getProperty(REDIRECT_PROPERTY));
        this.firewall = !flag("--no-firewall");
        this.browser = !flag("--no-browser");
        this.configName = value("--config").orElse("work/relay-routes.json");
        if (redirect) {
            // The relay may run in this JVM (--relay, or a class directory); the child gets the same
            // answer as a -D property on its own command line.
            System.setProperty(REDIRECT_PROPERTY, "true");
        }
    }

    /** Whether this run captures with the packet redirector rather than by claiming addresses. */
    private boolean capturesByRedirect() {
        return redirect && claim;
    }

    /**
     * Whether this run claims addresses on the loopback interface.
     *
     * <p>Never both: claiming the game server's address is exactly what the redirector exists to
     * avoid, so {@code --redirect} turns the claim off rather than running two captures on one route.
     */
    private boolean claimsAddresses() {
        return claim && !redirect;
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
                Path template = root.resolve("relay-routes.json");
                // The hosts block is part of the same capture and is the reason "released the
                // addresses and the game still would not enter" happened: the claim was gone but the
                // queue name still resolved to a local address with nothing behind it. Giving both
                // back here is what makes this command the one-stop repair it is documented to be.
                releaseHosts(template);
                yield releaseClaims(template).exitCode();
            }
            case INSTALL -> {
                // Extract and upgrade only: no address is resolved, so this works offline and is
                // what a helper script calls when it needs the PowerShell files to exist.
                Sync.install(root).forEach(note -> IO.println(note));
                yield 0;
            }
            case STOP -> stopEverything();
            case ELEVATE -> startWithCapture(relayArguments());
            case RUN -> runAsUser(relayArguments());
        };
    }

    /**
     * The unelevated half: hand over to an elevated copy of this jar when the capture needs it,
     * otherwise carry on here.
     */
    private int runAsUser(List<String> relayArguments) throws Exception {
        boolean needsCapture = claimsAddresses() || capturesByRedirect();
        boolean needsElevation = needsCapture && PowerShell.isWindows();
        if (needsElevation && elevate && !PowerShell.isElevated()) {
            return relaunchElevated(relayArguments);
        }
        // Clear a relay from an earlier run before starting a new one, whichever way this run
        // proceeds: a leftover holds the listen ports, and the second relay would fail to bind them -
        // presenting as a claim problem or a client problem rather than the process that caused it.
        stopLeftoverRelays();
        // A redirector left behind by a killed run is the same class of problem and worse: it points
        // the client's connections at a listener that is not there.
        stopLeftoverRedirector();
        if (!needsCapture) {
            // No capture, but a hosts block from an earlier run still points the client at this
            // machine. Clearing it is the difference between "started with --no-claim" and
            // "started with --no-claim and the game cannot log in".
            releaseHosts(configPath());
            syncAndRefreshRoutes();
            IO.println("--no-claim: nothing is claimed, so the client reaches the real server directly.");
            return runRelay(relayArguments);
        }
        return startWithCapture(relayArguments);
    }

    /**
     * The elevated half: update, refresh, capture, run, release.
     *
     * <p>Ordering matters here and is not arbitrary. The update comes second because replacing the jar
     * means this process exits, and exiting releases the captures - so an update must happen while
     * nothing is captured. The captures are released in a {@code finally} and by a shutdown hook
     * because an unclaimed address or a running redirector left behind leaves the game unable to reach
     * its own server, which is much worse than a log line.
     *
     * <p>{@code --redirect} and a claim are mutually exclusive by construction: this method runs the
     * packet redirector instead of {@code Set-AddressClaim.ps1}, and the hosts capture still runs
     * first because a name-addressed route is captured by neither of them otherwise.
     */
    private int startWithCapture(List<String> relayArguments) throws Exception {
        // A leftover relay was already stopped by runAsUser, before it chose this branch, so the
        // listen ports are free by the time a capture is worth making.
        //
        // THE HOSTS BLOCK GOES FIRST, BEFORE THE ADDRESSES ARE REFRESHED, AND THAT ORDER IS THE
        // WHOLE POINT. The block points a name-addressed route at 127.0.0.1; refreshing before it
        // is removed resolves that name to loopback and writes the relay's own listen address into
        // the route table, so the relay refuses to start. A block left by a killed run used to
        // make the next start fail exactly once - and read as a network problem rather than as a
        // leftover. Releasing it here also means a run always begins from a clean capture, which
        // is what makes the launcher safe to run after any previous crash.
        releaseHosts(configPath());
        syncAndRefreshRoutes();

        if (update) {
            int code = installUpdate(relayArguments);
            if (code == 10) {
                IO.println("The updated jar starts in a moment.");
                return code;
            }
        }

        if (!claimsAddresses() && !capturesByRedirect()) {
            return runRelay(relayArguments);
        }
        if (!PowerShell.isWindows()) {
            IO.println("Address capture is a Windows feature. On " + System.getProperty("os.name")
                    + " the relay can still forward a route whose destination is not a real server address.");
            return runRelay(relayArguments);
        }

        // Names first: a route the client reaches by name is captured here and is skipped by
        // both the claim and the redirector below, so no route is ever captured twice.
        captureByHosts(configPath());

        // THE DISPOSAL IS ARMED BEFORE THE CAPTURE IS MADE, NOT AROUND THE RUN ALONE.
        //
        // A claim, a hosts block and a redirector are machine-wide side effects that outlive this
        // process when it dies badly, and one left behind breaks the game until someone works out
        // what happened. The `finally` below covers a normal exit and an exception; it does NOT
        // reliably cover Ctrl+C, a closed console window, a logoff or a crash, because on those paths
        // the JVM starts its shutdown sequence while this thread is still blocked in waitFor and the
        // main thread may never reach its own finally. A shutdown hook does run on those paths, so
        // both callers share one idempotent release.
        Thread disposal = new Thread(() -> releaseCapture(configPath()), "drelay-release");
        Runtime.getRuntime().addShutdownHook(disposal);
        try {
            if (capturesByRedirect()) {
                int started = startRedirector(configPath());
                if (started != 0) {
                    return started;
                }
                return runRelay(relayArguments);
            }
            PowerShell.Result claimed = claimAddresses(configPath());
            if (claimed.exitCode() == 3) {
                // The script refused: a route address belongs to a real interface. Claiming it would take
                // the machine off the network, so this stops rather than starting a proxy that cannot work.
                // The hosts block, however, has already been written above - so it is given back here
                // rather than left pointing the client at a relay that will never start.
                return 3;
            }
            if (!claimed.ok()) {
                IO.println();
                IO.println("The addresses could not all be claimed (exit " + claimed.exitCode() + ").");
                IO.println("The relay is starting anyway: whichever route is not captured will bypass it.");
                IO.println("Rerun with --no-claim to skip this step, or see docs/PROTOCOL.md for the claim.");
            }
            return runRelay(relayArguments);
        } finally {
            releaseCapture(configPath());
            try {
                Runtime.getRuntime().removeShutdownHook(disposal);
            } catch (IllegalStateException ignored) {
                // Already shutting down: the hook itself is (or has been) the disposal.
            }
            IO.println("Stopped.");
        }
    }

    /**
     * Gives back everything this run put on the machine: the redirector, its firewall rule, the hosts
     * block and the loopback claims.
     *
     * <p>Idempotent and safe from any thread, because it is the {@code finally} of the run and the
     * JVM's shutdown hook at once. The redirector goes first: it must stop handing packets to a
     * listener that is about to disappear. Then the hosts block: an address claim with no relay
     * behind it already breaks the client, while a stale name capture is the one that makes the game
     * fail to enter, which is the harder failure to recognise.
     */
    private void releaseCapture(Path config) {
        if (!released.compareAndSet(false, true)) {
            return;
        }
        IO.println();
        IO.println("Releasing the captures...");
        stopRedirector();
        closeFirewall();
        releaseHosts(config);
        if (!claimsAddresses()) {
            // Nothing this run did needs releasing. A claim left behind by an older release is not
            // cleared here - it takes an elevated prompt and is what --release-claims is for - so
            // trying would only print a failure on a machine that has nothing claimed.
            IO.println("  no address was claimed by this run; the client reaches the real server once the"
                    + " redirector has stopped");
            IO.println("  a claim left by an earlier run is cleared with: java -jar \"" + jarOrSelf()
                    + "\" --release-claims");
            return;
        }
        // Never let a failure here escape: this runs from a shutdown hook, where an exception has
        // nowhere to go and would only hide that the release did not happen.
        try {
            PowerShell.Result releasedClaims = releaseClaims(config);
            if (!releasedClaims.ok()) {
                IO.println("  not every claim was released. Finish with:");
                IO.println("    java -jar \"" + jarOrSelf() + "\" --release-claims");
            }
        } catch (Exception e) {
            IO.println("  the claim script could not be run: " + e.getMessage());
            IO.println("    finish with: java -jar \"" + jarOrSelf() + "\" --release-claims");
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
        if (capturesByRedirect()) {
            // The relay must not claim the address named by a GmReconnect: under a redirector that
            // is precisely what breaks the data path. It still learns the destination.
            command.add("-D" + REDIRECT_PROPERTY + "=true");
        }
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
        // hook covers a launcher that is stopped some other way, so the child does not outlive it.
        // The machine-wide side effects (the redirector, its firewall rule, the claims and the hosts
        // block) have their own hook, registered by startWithCapture around the capture - this one
        // only owns the child process.
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

    // ---------------------------------------------------------------------------------------
    // The packet redirector
    // ---------------------------------------------------------------------------------------

    /**
     * Starts {@code drelay-redirect.exe} and waits for it to report that the driver is open.
     *
     * <p>The redirector is a child process, not a thread, for the same reason the relay is: it is a
     * different runtime, and its lifetime has to be tied to this one. It is stopped by
     * {@link #releaseCapture}, which runs from the {@code finally} and from the shutdown hook, so
     * Ctrl+C, a closed console and a killed launcher all end it.
     *
     * <p>It gets the ports from the <em>generated</em> route table, computed the same way
     * {@code Set-HostsEntry.ps1} decides what it captures: a package-level redirect exists for exactly
     * the routes a hosts file cannot capture. Handing it a stale or hand-written port list is how the
     * proxy would end up listening for traffic nothing sends it.
     *
     * @return 0 when the redirector is running, or an exit code for the launcher
     */
    private int startRedirector(Path config) throws Exception {
        Optional<Path> exe = redirectorPath();
        if (exe.isEmpty()) {
            IO.println();
            IO.println("--redirect needs the packet redirector, and it is not installed.");
            IO.println("  looked for:");
            for (Path candidate : redirectorCandidates()) {
                IO.println("    " + candidate.toAbsolutePath());
            }
            IO.println("  build it with: proxy\\redirector\\build.ps1");
            IO.println("  or point at one with: -Ddrelay.redirector=<path to drelay-redirect.exe>");
            return 4;
        }

        List<Integer> ports = Routes.redirectPorts(config);
        if (ports.isEmpty()) {
            IO.println("--redirect: every route in " + config.getFileName()
                    + " is captured by the hosts file, so a packet redirector has nothing to do.");
            return 0;
        }

        openFirewall(ports);

        Path ready = root.resolve("work").resolve("redirector.ready");
        Files.createDirectories(ready.getParent());
        Files.deleteIfExists(ready);

        var command = new ArrayList<String>();
        command.add(exe.get().toAbsolutePath().toString());
        command.add("--ports");
        command.add(joinPorts(ports));
        command.add("--ready");
        command.add(ready.toAbsolutePath().toString());
        command.add("--stats-interval");
        command.add("5");

        IO.println("Starting the packet redirector for port(s) " + joinPorts(ports) + "...");
        IO.println("  it hands the client's own packets to this relay's listener as inbound traffic, so the");
        IO.println("  server's address is never made local and the relay's upstream dial stays ordinary");
        Process process = new ProcessBuilder(command)
                .directory(root.toFile())
                .redirectErrorStream(true)
                .start();
        redirector = process;
        Path log = root.resolve("work").resolve("logs").resolve("redirector-console.out");
        pump(process.getInputStream(), log);

        String verdict = awaitRedirector(ready);
        if (verdict.startsWith("failed")) {
            String reason = verdict.substring("failed:".length()).trim();
            stopRedirector();
            IO.println();
            IO.println("The packet redirector did not start: " + reason);
            IO.println("  no traffic is being redirected, so the relay is not started either - it would be");
            IO.println("  listening for connections that never arrive.");
            IO.println("  its output is also in " + log.toAbsolutePath());
            return 4;
        }
        if (verdict.isBlank()) {
            IO.println("  the redirector has not reported yet; it is starting anyway. Its output is in");
            IO.println("    " + log.toAbsolutePath());
        }
        watchRedirector(process, log);
        return 0;
    }

    /**
     * Says so, loudly, if the redirector dies while the relay is still running.
     *
     * <p>The failure is silent otherwise: with no redirector in the path the client's dial simply
     * goes to the real server, so the game keeps working and only the proxy stops. That is easy to
     * mistake for "the proxy is idle", which is why it is not left to a log file nobody opens.
     */
    private void watchRedirector(Process process, Path log) {
        Thread.startVirtualThread(() -> {
            try {
                process.waitFor();
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                return;
            }
            // A redirector this run stopped on purpose is not news: stopRedirector clears the field
            // before it kills the child.
            if (redirector != process || released.get()) {
                return;
            }
            IO.println();
            IO.println("!! the packet redirector stopped while the relay is still running.");
            IO.println("!! the client now reaches the real server directly, unproxied.");
            IO.println("!! its output is in " + log.toAbsolutePath());
        });
    }

    /**
     * Waits for the redirector's verdict file, or for the process to die.
     *
     * <p>The file is the same "write the fact where the other process can read it" arrangement the
     * relay uses for its dashboard URL. Ten seconds is generous: opening the handle installs a driver
     * on the first run, and a driver that has not come up in ten seconds is not going to.
     *
     * @return the file's first line, empty when it never appeared, or a "failed: ..." line
     */
    private String awaitRedirector(Path ready) {
        for (int attempt = 0; attempt < 100; attempt++) {
            try {
                if (Files.isRegularFile(ready)) {
                    String text = Files.readString(ready).trim();
                    if (!text.isEmpty()) {
                        IO.println("  " + text);
                        return text;
                    }
                }
            } catch (IOException ignored) {
                // Being written right now; try again on the next turn.
            }
            Process process = redirector;
            if (process != null && !process.isAlive()) {
                return "failed: the redirector exited with code " + process.exitValue()
                        + " before opening the driver";
            }
            try {
                Thread.sleep(100);
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                return "";
            }
        }
        return "";
    }

    /** Stops the redirector child, if this run started one. Safe to call more than once. */
    private void stopRedirector() {
        try {
            Files.deleteIfExists(root.resolve("work").resolve("redirector.ready"));
        } catch (IOException ignored) {
            // A stale verdict file only makes the next start print an old line for a moment.
        }
        Process process = redirector;
        redirector = null;
        if (process == null || !process.isAlive()) {
            return;
        }
        IO.println("Stopping the packet redirector...");
        process.destroy();
        try {
            if (!process.waitFor(5, TimeUnit.SECONDS)) {
                process.destroyForcibly();
            }
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        }
    }

    /**
     * Stops a redirector left behind by an earlier launcher.
     *
     * <p>Worse than a leftover relay: a redirector with no relay behind it sends the client's
     * connections to a closed port, so the game fails to connect and nothing explains why. It is
     * found by image name, which only this program carries.
     */
    private boolean stopLeftoverRedirector() {
        if (!PowerShell.isWindows()) {
            return false;
        }
        List<String> image = List.of(REDIRECTOR_IMAGE);
        int pid = PowerShell.findProcess(REDIRECTOR_IMAGE, image);
        if (pid < 0) {
            return false;
        }
        IO.println("A packet redirector from an earlier run is still running (pid " + pid + "); stopping it first.");
        IO.println("  it would send the client's connections to a relay that is not running.");
        int stopped = PowerShell.stopProcesses(REDIRECTOR_IMAGE, image);
        IO.println("  stopped " + stopped + " redirector process(es).");
        return stopped > 0;
    }

    /**
     * Where the redirector's exe is looked for, in order.
     *
     * <p>Two of these are the same directory in a release (the jar and {@code redirector\} together)
     * and two are the same in a checkout ({@code proxy\target\redirector}). Both layouts are covered
     * because both are used: the release by a user, the checkout by {@code drelay.ps1}.
     */
    private List<Path> redirectorCandidates() {
        var candidates = new ArrayList<Path>();
        String explicit = System.getProperty("drelay.redirector");
        if (explicit != null && !explicit.isBlank()) {
            candidates.add(Path.of(explicit.trim()));
        }
        candidates.add(root.resolve("redirector").resolve(REDIRECTOR_IMAGE));
        candidates.add(root.resolve("target").resolve("redirector").resolve(REDIRECTOR_IMAGE));
        candidates.add(root.resolve("work").resolve("redirector").resolve(REDIRECTOR_IMAGE));
        Updater.runningJar()
                .map(Path::toAbsolutePath)
                .map(Path::getParent)
                .ifPresent(directory -> candidates.add(directory.resolve("redirector").resolve(REDIRECTOR_IMAGE)));
        return candidates;
    }

    private Optional<Path> redirectorPath() {
        for (Path candidate : redirectorCandidates()) {
            if (Files.isRegularFile(candidate)) {
                return Optional.of(candidate);
            }
        }
        return Optional.empty();
    }

    /**
     * Allows the redirected connections through Windows Firewall.
     *
     * <p>THIS IS NOT OPTIONAL HOUSEKEEPING. Before the redirector, the client's connection was
     * loopback traffic to a claimed address, and Windows Firewall does not stand between two local
     * sockets. A reflected packet arrives as traffic from the game server's public address, so the
     * relay's listener is an <em>inbound</em> endpoint as far as the firewall is concerned, and the
     * default inbound policy would drop the handshake - with exactly the symptom this whole exercise
     * is meant to cure: a session that opens on one side and hears nothing back.
     *
     * <p>The rule is scoped to the ports and has one fixed name, so adding it twice cannot stack up
     * rules; {@link #closeFirewall} removes it and {@code --stop} removes a stale one.
     */
    private void openFirewall(List<Integer> ports) {
        if (!firewall) {
            IO.println("  --no-firewall: no inbound rule is added. If the game cannot connect, this is why.");
            return;
        }
        String list = joinPorts(ports);
        String source = "netsh advfirewall firewall delete rule name=" + FIREWALL_RULE
                + " | Out-Null; "
                + "netsh advfirewall firewall add rule name=" + FIREWALL_RULE
                + " dir=in action=allow protocol=TCP localport=" + list + " profile=any";
        PowerShell.Result result = PowerShell.inline(source, false);
        if (result.ok()) {
            IO.println("  allowed inbound TCP " + list + " through Windows Firewall (rule " + FIREWALL_RULE
                    + ", removed when this run ends)");
        } else {
            // netsh prints its whole help text on a syntax or privilege failure, so the last line is
            // not the reason; the exit code is the reliable part.
            IO.println("  could not add the firewall rule for TCP " + list + " (exit " + result.exitCode() + ").");
            IO.println("  an unelevated run cannot, and the redirected handshake will then be dropped:");
            IO.println("  add it by hand with:");
            IO.println("    netsh advfirewall firewall add rule name=" + FIREWALL_RULE
                    + " dir=in action=allow protocol=TCP localport=" + list + " profile=any");
        }
    }

    private void closeFirewall() {
        if (!firewall || !PowerShell.isWindows()) {
            return;
        }
        try {
            PowerShell.inline("netsh advfirewall firewall delete rule name=" + FIREWALL_RULE, false);
        } catch (RuntimeException e) {
            IO.println("  the firewall rule could not be removed: " + e.getMessage());
            IO.println("  remove it with: netsh advfirewall firewall delete rule name=" + FIREWALL_RULE);
        }
    }

    private static String joinPorts(List<Integer> ports) {
        var text = new StringBuilder();
        for (int i = 0; i < ports.size(); i++) {
            if (i > 0) {
                text.append(',');
            }
            text.append(ports.get(i));
        }
        return text.toString();
    }

    // ---------------------------------------------------------------------------------------
    // Claims
    // ---------------------------------------------------------------------------------------

    /**
     * Claims every route address, letting the claim script print its own progress.
     *
     * <p>{@code -SessionOnly} on purpose. A claim is a process-scoped capture: the launcher
     * releases it on the way out, so surviving this process is enough. Persisting it across a
     * reboot is how a machine ends up with the game server's address on loopback and no relay to
     * answer it - the game then cannot reach its own server and nothing in the UI says why. A
     * reboot is the natural "start from a clean slate", and this keeps it that way.
     */
    private PowerShell.Result claimAddresses(Path config) throws IOException {
        Path script = root.resolve("tools").resolve("Set-AddressClaim.ps1");
        IO.println("Claiming the game server addresses on the loopback interface...");
        IO.println("  this is what puts the relay in the client's path: no driver, no packet rewriting");
        return PowerShell.run(script,
                List.of("-Config", config.toAbsolutePath().toString(), "-SessionOnly"), root, true);
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

    /**
     * Points the game's name-addressed services at this machine through the hosts file.
     *
     * <p>THE OTHER HALF OF PUTTING THE RELAY IN THE CLIENT'S PATH, AND THE HALF THAT WORKS.
     * A route the client reaches by <em>name</em> cannot be captured by claiming its address:
     * the claim assigns the server's own address to loopback, and then the relay's own
     * upstream dial to that address is a dial to a local address, so the session opens and
     * nothing ever comes back. A hosts entry has no such conflict - the client resolves the
     * name to a local address and the relay dials the real one from the route table.
     * {@code Set-AddressClaim.ps1} skips exactly the routes this covers, so the two cannot
     * fight over a route.
     *
     * <p>Non-fatal on purpose: a failure here costs the queue capture, not the run.
     */
    private void captureByHosts(Path config) {
        Path script = root.resolve("tools").resolve("Set-HostsEntry.ps1");
        if (!Files.exists(script)) {
            IO.println("  no hosts helper found; name-addressed routes will bypass the relay");
            return;
        }
        try {
            PowerShell.Result result = PowerShell.run(script,
                    List.of("-Config", config.toAbsolutePath().toString()), root, true);
            if (!result.ok()) {
                IO.println("  the hosts entry could not be written (exit " + result.exitCode() + ").");
                IO.println("  the relay still starts; name-addressed routes may bypass it.");
            }
        } catch (RuntimeException e) {
            IO.println("  the hosts helper could not be run: " + e.getMessage());
        }
    }

    private void releaseHosts(Path config) {
        Path script = root.resolve("tools").resolve("Set-HostsEntry.ps1");
        if (!Files.exists(script)) {
            return;
        }
        try {
            PowerShell.Result result = PowerShell.run(script,
                    List.of("-Config", config.toAbsolutePath().toString(), "-Release"), root, true);
            if (!result.ok()) {
                IO.println("  the hosts entry could not be removed (exit " + result.exitCode() + ").");
                IO.println("  remove it with: tools\\Set-HostsEntry.ps1 -Release");
            }
        } catch (RuntimeException e) {
            IO.println("  the hosts helper could not be run to release: " + e.getMessage());
        }
    }

    // ---------------------------------------------------------------------------------------
    // Elevation
    // ---------------------------------------------------------------------------------------

    /**
     * Re-runs this jar from an elevated PowerShell and returns its exit code.
     *
     * <p>Elevation cannot be requested from inside a running process, so this spawns a new one and
     * waits for it. Both captures need the token - the claim to assign an address, the redirector to
     * load the WinDivert driver - which is why a run that does neither is never elevated.
     */
    private int relaunchElevated(List<String> relayArguments) throws IOException {
        Path jar = Updater.runningJar().orElse(null);
        if (jar == null) {
            IO.println("Not running from a jar, so elevation cannot be requested automatically.");
            if (capturesByRedirect()) {
                IO.println("Start it from an elevated prompt instead:");
                IO.println("  " + redirectorPath().map(Path::toString).orElse("drelay-redirect.exe")
                        + " --ports " + joinPorts(Routes.redirectPorts(configPath())));
            } else {
                IO.println("Claim from an elevated prompt instead:");
                IO.println("  tools\\Set-AddressClaim.ps1 -Config " + configPath().toAbsolutePath());
            }
            IO.println("  then start the relay with --no-claim --no-elevate");
            return 2;
        }
        IO.println(capturesByRedirect()
                ? "Administrator rights are needed to load the WinDivert driver, so Windows will ask"
                : "Administrator rights are needed to claim the server addresses, so Windows will ask");
        IO.println("for them now. Declining is safe: rerun with --no-claim to skip the capture entirely.");

        var forwarded = new ArrayList<String>();
        forwarded.add("--elevate");
        forwarded.add("--no-elevate");
        // The launcher's own capture flags have to cross the elevation boundary too: the elevated
        // copy is a fresh command line, and it must capture the same way this one was asked to.
        forwarded.addAll(captureFlags());
        forwarded.addAll(relayArguments);

        String argumentList = "'-jar', " + PowerShell.quote(jar.toAbsolutePath().toString()) + ", "
                + String.join(", ", forwarded.stream().map(PowerShell::quote).toList());
        String source = "$p = Start-Process -FilePath " + PowerShell.quote(Updater.javaExecutable())
                + " -ArgumentList @(" + argumentList + ")"
                + " -WorkingDirectory " + PowerShell.quote(root.toAbsolutePath().toString())
                + " -Verb RunAs -Wait -PassThru; exit $p.ExitCode";
        return PowerShell.inline(source, false).exitCode();
    }

    /**
     * The launcher flags a restarted copy must carry, and why they are not just command-line noise.
     *
     * <p>An update and an elevation both replace this process's command line. {@code --redirect} and
     * {@code --no-firewall} are the two flags that decide <em>which capture runs</em>, so losing them
     * on a restart would silently switch an update from the redirector back to the claim.
     */
    private List<String> captureFlags() {
        var flags = new ArrayList<String>();
        if (redirect) {
            flags.add("--redirect");
        }
        if (!firewall) {
            flags.add("--no-firewall");
        }
        return flags;
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
        relaunch.addAll(captureFlags());
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
        // The redirector and its firewall rule are part of the same net: a redirector with no relay
        // behind it sends the client's connections to a closed port, and an orphaned allow rule is a
        // hole nobody remembers opening.
        stopLeftoverRedirector();
        closeFirewall();
        IO.println();
        IO.println("Releasing the claimed addresses...");
        // The hosts capture is part of the same net: a stop that leaves the client resolving the
        // queue to 127.0.0.1 with no relay behind it has not stopped anything.
        releaseHosts(root.resolve("relay-routes.json"));
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

                By default the launcher asks for the administrator rights that claiming an address
                needs, updates itself, refreshes the server addresses, claims them, starts the relay
                and releases the claims when it stops. --redirect replaces the claim with a packet
                redirector, for the machines where claiming an address breaks the relay's own upstream
                dial (see docs/proxy/HANDOFF-WINDIVERT.md). The relay's own options are passed
                through, so -Ddrelay.* works:

                  java -jar drelay.jar -Ddrelay.web.port=0
                  java -jar drelay.jar --redirect
                  java -jar drelay.jar --no-claim --no-update

                Options:
                  --redirect          capture with the packet redirector instead of claiming an
                                      address; needs drelay-redirect.exe beside the jar
                                      (build it with proxy\\redirector\\build.ps1)
                  --no-firewall       do not add the inbound firewall rule the redirector needs
                  --no-update         do not check GitHub for a newer release
                  --no-claim          do not capture anything and do not ask for elevation; only
                                      useful when a route's destination is a local peer
                  --no-elevate        never re-run this jar to obtain administrator rights
                  --no-browser        do not open the dashboard in a browser
                  --config <path>     route table to use (default: work/relay-routes.json)

                Commands:
                  --check-update      report whether a newer release exists; change nothing
                  --update            install the newest release and restart
                  --stop              stop a running relay and the redirector, and give the claimed
                                      addresses, the firewall rule and the hosts block back
                  --relay             run the relay alone in this JVM (no update, no capture)
                  --release-claims    give every claimed address and the hosts block back
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
