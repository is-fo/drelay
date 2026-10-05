package networking;

import java.io.IOException;
import java.io.UncheckedIOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;

/**
 * Runs the PowerShell helpers that ship inside the jar.
 *
 * <p>Claiming a game server's address on the loopback interface is a Windows networking operation
 * with a long tail of provider quirks: the cmdlet that usually works is refused on some builds and
 * {@code netsh} is the fallback, an address can be Tentative or Duplicate while duplicate address
 * detection runs, and a claim must never be placed on an address a real interface owns. That
 * knowledge is in {@code Set-AddressClaim.ps1}; this class only locates a shell and runs it.
 *
 * <p>PowerShell is present on every supported Windows install, so using it keeps the jar free of
 * native code and leaves the verified script as the single implementation of the claim.
 */
final class PowerShell {

    /** Outcome of a helper: its exit code plus everything it printed, in order. */
    record Result(int exitCode, String output) {
        boolean ok() {
            return exitCode == 0;
        }

        /** The last non-blank line, which is what a helper's own error message ends with. */
        String lastLine() {
            String[] lines = output.split("\\R");
            for (int i = lines.length - 1; i >= 0; i--) {
                if (!lines[i].isBlank()) {
                    return lines[i].trim();
                }
            }
            return "";
        }
    }

    private PowerShell() {
    }

    static boolean isWindows() {
        return System.getProperty("os.name", "").toLowerCase(Locale.ROOT).contains("win");
    }

    /** The shell to use: {@code pwsh} when it is installed, Windows PowerShell otherwise. */
    static String executable() {
        for (String candidate : List.of("pwsh", "powershell")) {
            try {
                var probe = new ProcessBuilder(candidate, "-NoProfile", "-Command", "exit 0")
                        .redirectErrorStream(true)
                        .redirectOutput(ProcessBuilder.Redirect.DISCARD)
                        .start();
                if (probe.waitFor() == 0) {
                    return candidate;
                }
            } catch (IOException | InterruptedException e) {
                if (e instanceof InterruptedException) {
                    Thread.currentThread().interrupt();
                }
            }
        }
        return "powershell";
    }

    /**
     * Runs {@code script} with the given arguments and waits for it.
     *
     * <p>{@code echo=true} inherits this process's console, so a helper's progress is watched as it
     * happens - which matters, because claiming an address can take a few seconds of duplicate
     * address detection and silence there looks like a hang.
     */
    static Result run(Path script, List<String> arguments, Path workingDirectory, boolean echo) {
        var command = new ArrayList<String>();
        command.add(executable());
        command.add("-NoProfile");
        command.add("-ExecutionPolicy");
        command.add("Bypass");
        command.add("-File");
        command.add(script.toAbsolutePath().toString());
        command.addAll(arguments);

        var builder = new ProcessBuilder(command)
                .directory(workingDirectory.toFile())
                .redirectErrorStream(true);
        try {
            if (echo) {
                builder.inheritIO();
                return new Result(builder.start().waitFor(), "");
            }
            builder.redirectOutput(ProcessBuilder.Redirect.PIPE);
            Process process = builder.start();
            byte[] output = process.getInputStream().readAllBytes();
            return new Result(process.waitFor(), new String(output, StandardCharsets.UTF_8));
        } catch (IOException e) {
            throw new UncheckedIOException("could not run " + script.getFileName() + ": " + e.getMessage(), e);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new IllegalStateException("interrupted while running " + script.getFileName(), e);
        }
    }

    /**
     * Runs PowerShell source given inline and returns its exit code.
     *
     * <p>Used for the handful of one-line questions - is this process elevated, what is the LAN
     * address, start that elevated - where writing a file first would be noise.
     *
     * <p>Keep the source free of {@code "} characters. On Windows, {@code ProcessBuilder} assembles a
     * command line and {@code powershell.exe} re-parses it, and the embedded quotes do not survive
     * that round trip: a WQL filter written as {@code -Filter "Name='java.exe'"} arrives as
     * {@code -Filter Name='java.exe'}, which is a different (broken) command. Use single quotes and
     * PowerShell arrays instead - or {@link #runScript}, which is immune because the script never
     * touches a command line.
     */
    static Result inline(String source, boolean echo) {
        var builder = new ProcessBuilder(executable(), "-NoProfile", "-ExecutionPolicy", "Bypass",
                "-Command", source)
                .redirectErrorStream(true);
        try {
            if (echo) {
                builder.inheritIO();
                return new Result(builder.start().waitFor(), "");
            }
            Process process = builder.start();
            byte[] output = process.getInputStream().readAllBytes();
            return new Result(process.waitFor(), new String(output, StandardCharsets.UTF_8));
        } catch (IOException e) {
            throw new UncheckedIOException("could not run PowerShell: " + e.getMessage(), e);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new IllegalStateException("interrupted while running PowerShell", e);
        }
    }

    /**
     * Runs PowerShell source from a file, which sidesteps command-line quoting completely.
     *
     * <p>This is the form to prefer for anything longer than a line, or anything that contains quotes:
     * the file is read by PowerShell as written, so there is no layer that can rewrite it.
     */
    static Result runScript(String source, Path workingDirectory) {
        try {
            Path script = Files.createTempFile(workingDirectory, "drelay-", ".ps1");
            try {
                Files.writeString(script, source, StandardCharsets.UTF_8);
                var builder = new ProcessBuilder(executable(), "-NoProfile", "-ExecutionPolicy", "Bypass",
                        "-File", script.toAbsolutePath().toString())
                        .directory(workingDirectory.toFile())
                        .redirectErrorStream(true);
                Process process = builder.start();
                byte[] output = process.getInputStream().readAllBytes();
                return new Result(process.waitFor(), new String(output, StandardCharsets.UTF_8));
            } finally {
                Files.deleteIfExists(script);
            }
        } catch (IOException e) {
            throw new UncheckedIOException("could not run a PowerShell script: " + e.getMessage(), e);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new IllegalStateException("interrupted while running a PowerShell script", e);
        }
    }

    /** Starts PowerShell source detached: the caller terminates while it keeps going. */
    static void startDetached(String source) {
        try {
            new ProcessBuilder(executable(), "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", source)
                    .redirectOutput(ProcessBuilder.Redirect.DISCARD)
                    .redirectError(ProcessBuilder.Redirect.DISCARD)
                    .start();
        } catch (IOException e) {
            throw new UncheckedIOException("could not start PowerShell: " + e.getMessage(), e);
        }
    }

    /**
     * Whether this process already holds the administrator token.
     *
     * <p>Asked of Windows rather than guessed from an environment variable: a token can be elevated
     * however the process was started, and the answer decides whether the launch continues or
     * requests elevation.
     */
    static boolean isElevated() {
        if (!isWindows()) {
            return false;
        }
        var source = "$id=[Security.Principal.WindowsPrincipal]"
                + "[Security.Principal.WindowsIdentity]::GetCurrent();"
                + "if ($id.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { exit 0 } else { exit 1 }";
        return inline(source, false).ok();
    }

    /** A single-quoted PowerShell string literal, escaping embedded quotes the PowerShell way. */
    static String quote(String value) {
        return "'" + value.replace("'", "''") + "'";
    }

    /**
     * The PowerShell fragment that builds {@code $exclude}: this process and every ancestor.
     *
     * <p>Emitting it here rather than in each helper keeps one definition of "not me". The failure it
     * prevents is worth the noise: both process searches match on a command line, and the launcher
     * that runs them is itself a {@code java -jar drelay.jar} process.
     */
    private static String selfExclusion() {
        long pid = ProcessHandle.current().pid();
        return "$exclude = @(" + pid + ");$p = " + pid + ";"
                + "while ($p) { $o = Get-CimInstance Win32_Process -Filter \"ProcessId=$p\" -ErrorAction SilentlyContinue;"
                + " if (-not $o) { break }; $exclude += $o.ParentProcessId; $p = $o.ParentProcessId };";
    }

    /**
     * The PID of a running process whose command line contains {@code needle}, or -1.
     *
     * <p>Asked through {@code Win32_Process} rather than guessed, because the point of the question is
     * to avoid killing something unrelated: a bare PID from a file can be reused by any process after
     * a reboot, while a command line that still names this program cannot.
     *
     * <p>Both helpers below write with {@code Write-Output}, not {@code [Console]::Out}: only the
     * former travels through the redirected standard output this method reads.
     */
    static int findProcess(String needle) {
        if (!isWindows()) {
            return -1;
        }
        // No double quotes around the needle: single-quoted PowerShell literals survive the
        // command-line round trip that {@link #inline} documents the pitfalls of.
        String source = selfExclusion()
                + "$p = Get-CimInstance Win32_Process -Filter 'Name=''java.exe'' or Name=''javaw.exe'''"
                + " -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -and $_.CommandLine -like "
                + quote("*" + needle + "*") + " -and ($exclude -notcontains $_.ProcessId) }"
                + " | Select-Object -First 1;"
                + "if ($p) { Write-Output $p.ProcessId } else { Write-Output '-1' }";
        Result result = inline(source, false);
        for (String line : result.output().split("\\R")) {
            try {
                return Integer.parseInt(line.trim());
            } catch (NumberFormatException ignored) {
                // CIM can emit a warning line before the value; keep looking.
            }
        }
        return -1;
    }

    /**
     * Stops every java process whose command line contains {@code needle}, and returns how many went.
     *
     * <p>This process and its ancestors are excluded by PID. The callers look for a marker only a
     * relay carries, so self-exclusion should never be needed - but "should never" is not a good
     * enough answer for a function whose failure mode is killing the program that called it, and an
     * earlier version of this code did exactly that.
     */
    static int stopProcesses(String needle) {
        if (!isWindows()) {
            return 0;
        }
        String source = selfExclusion()
                + "$needle = " + quote(needle) + ";"
                + "$n = 0;"
                + "Get-CimInstance Win32_Process -Filter 'Name=''java.exe'' or Name=''javaw.exe''' -ErrorAction SilentlyContinue"
                + " | Where-Object { $_.CommandLine -and $_.CommandLine -like ('*' + $needle + '*')"
                + " -and ($exclude -notcontains $_.ProcessId) }"
                + " | ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop; $n++ } catch { } };"
                + "Write-Output $n";
        Result result = inline(source, false);
        for (String line : result.output().split("\\R")) {
            try {
                return Integer.parseInt(line.trim());
            } catch (NumberFormatException ignored) {
                // Same as above: read past anything that is not the count.
            }
        }
        return 0;
    }

    /** Writes a {@code .ps1} file next to the helper that needs it. */
    static Path writeScript(Path path, String content) throws IOException {
        Files.createDirectories(path.getParent());
        Files.writeString(path, content, StandardCharsets.UTF_8);
        return path;
    }
}
