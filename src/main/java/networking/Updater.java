package networking;

import java.io.IOException;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.file.Files;
import java.nio.file.Path;
import java.security.MessageDigest;
import java.time.Duration;
import java.util.ArrayList;
import java.util.List;
import java.util.Optional;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * Finds and installs a newer release from GitHub.
 *
 * <p>The release artifact is one jar, so an update is one file: download it, check it, swap it in,
 * start it. That is what this class does, and every step refuses on doubt rather than proceeding -
 * an updater that installs a truncated or unverifiable jar is worse than no updater at all, because
 * the user cannot run the program to find out what went wrong.
 *
 * <p>Two details are Windows-specific and worth stating plainly:
 *
 * <ul>
 *   <li>A running jar can be replaced on Windows (the file is not held open for writing), but the
 *       copy that is executing is still the old one, so the swap is followed by a restart.</li>
 *   <li>The restart cannot be performed by the process that is being replaced, because the file it
 *       is running from is the one being written. A short PowerShell helper waits for this process
 *       to exit, moves the new jar into place and relaunches it.</li>
 * </ul>
 *
 * <p>The SHA-256 of the downloaded jar is checked against the release's {@code SHA256SUMS.txt}
 * before anything is replaced. If a release does not publish that file, the download is refused:
 * HTTPS alone would trust the transport, and the point of the check is to trust the bytes.
 */
final class Updater {

    /** A newer release, with everything needed to install it. */
    record Available(String version, String jarUrl, String expectedSha256, long sizeBytes) {
    }

    private static final String USER_AGENT = "drelay/" + Resources.VERSION;
    private static final Pattern SHA256_LINE = Pattern.compile("^([0-9a-fA-F]{64})\\s+\\*?(.+)$");

    private Updater() {
    }

    /** The jar this process is running from, or empty when the classes are not in a jar. */
    static Optional<Path> runningJar() {
        try {
            URI location = Updater.class.getProtectionDomain().getCodeSource().getLocation().toURI();
            Path path = Path.of(location);
            return Files.isRegularFile(path) ? Optional.of(path) : Optional.empty();
        } catch (Exception e) {
            return Optional.empty();
        }
    }

    /**
     * The newest published, non-draft release that is newer than this build, or empty.
     *
     * <p>Reads the release list rather than {@code /releases/latest} so that a release published as a
     * prerelease can still be picked up by a build that opted into it, and so the jar asset's URL is
     * taken from the API instead of being constructed from the tag name.
     */
    static Optional<Available> check() throws IOException, InterruptedException {
        String body = get(URI.create("https://api.github.com/repos/" + Resources.REPOSITORY
                + "/releases?per_page=10"));
        JsonText.Document document = JsonText.parse(body);
        if (!(document.root() instanceof JsonText.Arr releases)) {
            throw new IOException("the release list was not an array");
        }
        for (JsonText.Value value : releases.values()) {
            if (!(value instanceof JsonText.Obj release) || release.bool("draft", false)) {
                continue;
            }
            String tag = release.string("tag_name", "");
            String version = normalise(tag);
            if (version.isEmpty() || compare(version, Resources.VERSION) <= 0) {
                continue;
            }
            String jarUrl = null;
            long size = 0;
            for (JsonText.Value assetValue : release.array("assets")) {
                if (assetValue instanceof JsonText.Obj asset
                        && "drelay.jar".equals(asset.string("name", ""))) {
                    jarUrl = asset.string("browser_download_url", null);
                    size = asset.longValue("size", 0);
                }
            }
            if (jarUrl == null) {
                continue;
            }
            Optional<String> sums = sha256For(jarUrl, "drelay.jar");
            if (sums.isEmpty()) {
                // A release without checksums is not installable automatically. Say which one, so the
                // reason is visible instead of the update silently never happening.
                System.err.println("release " + tag + " publishes no usable SHA256SUMS.txt;"
                        + " skipping it (download it by hand from the releases page if you want it)");
                continue;
            }
            return Optional.of(new Available(version, jarUrl, sums.get(), size));
        }
        return Optional.empty();
    }

    /** Downloads the release jar to {@code target}, verifying its checksum. */
    static void download(Available available, Path target) throws IOException, InterruptedException {
        Files.createDirectories(target.getParent());
        Path temporary = target.resolveSibling(target.getFileName() + ".download");
        try (var client = client()) {
            var request = HttpRequest.newBuilder(URI.create(available.jarUrl()))
                    .timeout(Duration.ofMinutes(10))
                    .header("User-Agent", USER_AGENT)
                    .GET()
                    .build();
            HttpResponse<Path> response = client.send(request, HttpResponse.BodyHandlers.ofFile(temporary));
            if (response.statusCode() != 200) {
                throw new IOException("HTTP " + response.statusCode() + " downloading " + available.jarUrl());
            }
        }
        byte[] bytes = Files.readAllBytes(temporary);
        Files.deleteIfExists(temporary);

        if (bytes.length < 4 || bytes[0] != 'P' || bytes[1] != 'K') {
            throw new IOException("the downloaded file is not a jar (" + bytes.length + " bytes)");
        }
        String actual = sha256(bytes);
        if (!actual.equalsIgnoreCase(available.expectedSha256())) {
            throw new IOException(("the downloaded jar does not match its published checksum"
                    + " (expected %s, got %s); nothing was replaced")
                    .formatted(available.expectedSha256(), actual));
        }
        Files.write(target, bytes);
    }

    /**
     * Starts the helper that swaps the jar in and relaunches it, then returns so the caller can exit.
     *
     * <p>The helper runs detached and waits for this process to end before touching the file. Its
     * output is discarded: by the time it has anything to say, the process that could display it is
     * gone.
     */
    static void scheduleInstall(Path jar, Path replacement, List<String> relaunchArguments) {
        String script = """
                $ErrorActionPreference = 'Stop'
                $jar = $env:DRELAY_UPDATE_TARGET
                $new = $env:DRELAY_UPDATE_FILE
                $log = $env:DRELAY_UPDATE_LOG
                function Say($m) { Add-Content -LiteralPath $log -Value ("[{0}] {1}" -f (Get-Date -Format o), $m) }
                try {
                  Wait-Process -Id %d -Timeout 90 -ErrorAction SilentlyContinue
                  Start-Sleep -Milliseconds 400
                  if (-not (Test-Path -LiteralPath $new)) { throw "the downloaded jar is gone: $new" }
                  $bytes = [System.IO.File]::ReadAllBytes($new)
                  if ($bytes.Length -lt 4 -or $bytes[0] -ne 0x50 -or $bytes[1] -ne 0x4B) { throw "the downloaded jar is not a zip: $new" }
                  $backup = "$jar.previous"
                  Copy-Item -LiteralPath $jar -Destination $backup -Force
                  Move-Item -LiteralPath $new -Destination $jar -Force
                  $identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
                  if ($identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
                    Start-Process -FilePath %s -ArgumentList @('-jar', $jar, %s)
                  } else {
                    Start-Process -FilePath %s -ArgumentList @('-jar', $jar, %s) -Verb RunAs
                  }
                  Say "installed and restarted"
                  Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
                } catch {
                  Say ("update failed: " + $_.Exception.Message)
                }
                """.formatted(
                ProcessHandle.current().pid(),
                PowerShell.quote(javaExecutable()),
                String.join(", ", relaunchArguments.stream().map(PowerShell::quote).toList()),
                PowerShell.quote(javaExecutable()),
                String.join(", ", relaunchArguments.stream().map(PowerShell::quote).toList()));

        String source = "$env:DRELAY_UPDATE_TARGET = " + PowerShell.quote(jar.toAbsolutePath().toString()) + "; "
                + "$env:DRELAY_UPDATE_FILE = " + PowerShell.quote(replacement.toAbsolutePath().toString()) + "; "
                + "$env:DRELAY_UPDATE_LOG = " + PowerShell.quote(logPath(jar).toString()) + "; "
                + "& { " + script + " }";
        PowerShell.startDetached(source);
    }

    /** Where the update helper records what it did; read on the next launch if something went wrong. */
    static Path logPath(Path jar) {
        return jar.toAbsolutePath().getParent().resolve("drelay-update.log");
    }

    /** The java launcher to relaunch with: the running JVM if it can be found, otherwise {@code javaw}. */
    static String javaExecutable() {
        String home = System.getProperty("java.home", "");
        if (!home.isEmpty()) {
            Path candidate = Path.of(home, "bin", PowerShell.isWindows() ? "javaw.exe" : "java");
            if (Files.isExecutable(candidate)) {
                return candidate.toString();
            }
        }
        return PowerShell.isWindows() ? "javaw.exe" : "java";
    }

    /** The {@code SHA256SUMS.txt} entry for {@code assetName}, from the same release as {@code assetUrl}. */
    private static Optional<String> sha256For(String assetUrl, String assetName)
            throws IOException, InterruptedException {
        String base = assetUrl.substring(0, assetUrl.lastIndexOf('/') + 1);
        String body;
        try {
            body = get(URI.create(base + "SHA256SUMS.txt"));
        } catch (IOException e) {
            return Optional.empty();
        }
        for (String line : body.split("\\R")) {
            Matcher matcher = SHA256_LINE.matcher(line.trim());
            if (matcher.matches()) {
                String name = matcher.group(2).trim();
                if (name.equals(assetName) || name.endsWith("/" + assetName)) {
                    return Optional.of(matcher.group(1).toLowerCase());
                }
            }
        }
        return Optional.empty();
    }

    private static String get(URI uri) throws IOException, InterruptedException {
        try (var client = client()) {
            var request = HttpRequest.newBuilder(uri)
                    .timeout(Duration.ofSeconds(30))
                    .header("User-Agent", USER_AGENT)
                    .header("Accept", "application/vnd.github+json")
                    .GET()
                    .build();
            HttpResponse<String> response = client.send(request, HttpResponse.BodyHandlers.ofString());
            if (response.statusCode() != 200) {
                throw new IOException("HTTP " + response.statusCode() + " from " + uri);
            }
            return response.body();
        }
    }

    private static HttpClient client() {
        return HttpClient.newBuilder()
                .connectTimeout(Duration.ofSeconds(15))
                .followRedirects(HttpClient.Redirect.NORMAL)
                .build();
    }

    /** {@code v1.2.3} and {@code 1.2.3} both become {@code 1.2.3}; anything else stays as it is. */
    static String normalise(String tag) {
        String value = tag == null ? "" : tag.trim();
        return value.startsWith("v") || value.startsWith("V") ? value.substring(1) : value;
    }

    /**
     * Compares two dotted version strings numerically.
     *
     * <p>A suffix after {@code -} (a prerelease) sorts below the release it belongs to, so
     * {@code 1.1.0-rc1} does not replace {@code 1.1.0}, which is the ordering a user expects.
     */
    static int compare(String left, String right) {
        String[] leftParts = left.split("-", 2);
        String[] rightParts = right.split("-", 2);
        String[] leftNumbers = leftParts[0].split("\\.");
        String[] rightNumbers = rightParts[0].split("\\.");
        for (int i = 0; i < Math.max(leftNumbers.length, rightNumbers.length); i++) {
            int a = number(leftNumbers, i);
            int b = number(rightNumbers, i);
            if (a != b) {
                return Integer.compare(a, b);
            }
        }
        boolean leftPre = leftParts.length > 1;
        boolean rightPre = rightParts.length > 1;
        if (leftPre == rightPre) {
            return 0;
        }
        return leftPre ? -1 : 1;
    }

    private static int number(String[] parts, int index) {
        if (index >= parts.length) {
            return 0;
        }
        try {
            return Integer.parseInt(parts[index].trim());
        } catch (NumberFormatException e) {
            return 0;
        }
    }

    static String sha256(byte[] content) {
        try {
            var digest = MessageDigest.getInstance("SHA-256").digest(content);
            var hex = new StringBuilder(digest.length * 2);
            for (byte b : digest) {
                hex.append(Character.forDigit((b >> 4) & 0xF, 16)).append(Character.forDigit(b & 0xF, 16));
            }
            return hex.toString();
        } catch (Exception e) {
            throw new IllegalStateException(e);
        }
    }

    /** The arguments the relaunched jar should receive, so an update never changes how it was run. */
    static List<String> relaunchArguments(List<String> original) {
        return new ArrayList<>(original);
    }
}
