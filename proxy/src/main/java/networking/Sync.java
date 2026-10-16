package networking;

import java.io.IOException;
import java.io.UncheckedIOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.TreeSet;

/**
 * Writes the jar's bundled files into the working directory, without ever clobbering an edit.
 *
 * <p>A user who downloads one jar still needs a route table to edit and the PowerShell scripts that
 * claim the server addresses. Those are extracted on first run. The hard part is what happens on the
 * <em>second</em> run, after the user has changed a route, replaced an address or tuned the dashboard
 * port: a naive "extract every time" would silently undo that on the next launch, and a naive
 * "extract once" would never deliver an improved script or a corrected default.
 *
 * <p>So each file carries two hashes in the manifest beside the routes file: the hash of the content
 * that was last written, and the hash of the template it came from. On the next run the installed
 * file is compared against the recorded content hash:
 *
 * <ul>
 *   <li>equal - nobody edited it, so a newer template is written and the manifest updated;</li>
 *   <li>different - the user has an edit, so the file is left exactly as it is and the skipped
 *       upgrade is reported in the run's output;</li>
 *   <li>absent - the file was deliberately deleted, so the template is written again.</li>
 * </ul>
 *
 * <p>The manifest is a small line-oriented text file rather than JSON so that it stays readable and
 * diffable, and so a hand-edit of it cannot corrupt anything worse than one upgrade decision.
 */
final class Sync {

    /** Who a file in the install location belongs to: the packaged templates, or the user. */
    private static final String OURS = "ours";
    private static final String USER = "user";

    /**
     * One file's record: where it goes, what is there, what it came from, and whose it is.
     *
     * <p>{@code owner} is the field that makes the whole scheme work. Without it, a file the user
     * dropped beside the jar would have its own content recorded as "what we installed", and the next
     * launch would classify it as an untouched template and overwrite it. Ownership is therefore
     * recorded explicitly rather than inferred from hashes.
     */
    private record Entry(String resource, String path, String installedHash, String templateHash,
                         String owner) {
    }

    private final Path root;
    private final Path manifestPath;
    private final List<String> notes = new ArrayList<>();

    private Sync(Path root) {
        this.root = root;
        this.manifestPath = root.resolve("work").resolve("install-manifest.txt");
    }

    /** Installs or upgrades every bundled file under {@code root}; returns what was done. */
    static List<String> install(Path root) {
        var sync = new Sync(root);
        try {
            sync.run();
        } catch (IOException e) {
            throw new UncheckedIOException(e);
        }
        return sync.notes;
    }

    private void run() throws IOException {
        Map<String, Entry> previous = readManifest();
        Map<String, Entry> next = new LinkedHashMap<>();
        // Sorted so the manifest is stable and a diff of two versions is readable.
        for (String resource : new TreeSet<>(Resources.INSTALLED)) {
            String relative = Resources.installPath(resource);
            byte[] template = Resources.read(resource);
            String templateHash = sha256(template);
            Path target = root.resolve(relative.replace('/', java.io.File.separatorChar));

            Entry before = previous.get(resource);
            boolean installed = Files.exists(target);
            String currentHash = installed ? sha256(Files.readAllBytes(target)) : null;

            if (!installed) {
                notes.add("installed " + relative);
                next.put(resource, new Entry(resource, relative, templateHash, templateHash, OURS));
                write(target, template);
                continue;
            }

            if (before == null || USER.equals(before.owner())) {
                // The file has never been installed by us, or was handed to the user by an earlier
                // version of this launcher: it is theirs, and it stays exactly as it is. The hash we
                // record of it is only a watermark, so that an upgrade can say "the bundled copy
                // changed, yours was left alone" exactly once - which is why the recorded "installed"
                // hash must be the file's own content and not a template's.
                notes.add("keeping " + relative + " - it was already here, so it is treated as yours");
                next.put(resource, new Entry(resource, relative, currentHash, templateHash, USER));
                continue;
            }

            // Ours, and untouched since we wrote it: it may be upgraded.
            if (currentHash.equals(before.installedHash())) {
                if (!templateHash.equals(before.templateHash())) {
                    notes.add("updated " + relative);
                }
                next.put(resource, new Entry(resource, relative, templateHash, templateHash, OURS));
                if (!currentHash.equals(templateHash)) {
                    write(target, template);
                }
                continue;
            }

            // Ours, but edited since we wrote it: hand it to the user rather than overwrite.
            notes.add("kept your edited " + relative
                    + (templateHash.equals(before.templateHash())
                            ? " (it differs from the bundled copy; yours was left alone)"
                            : " (the bundled copy changed too; yours was left alone)"));
            next.put(resource, new Entry(resource, relative, before.installedHash(), templateHash, USER));
        }
        writeManifest(next);
    }

    private void write(Path target, byte[] content) throws IOException {
        Path parent = target.getParent();
        if (parent != null) {
            Files.createDirectories(parent);
        }
        // Temp file first, then an atomic move: a run interrupted mid-write leaves the previous file
        // intact instead of a truncated one. The same rule the PowerShell helpers follow.
        Path temporary = target.resolveSibling(target.getFileName() + ".drelay-new");
        Files.write(temporary, content);
        Files.move(temporary, target, StandardCopyOption.REPLACE_EXISTING);
    }

    private Map<String, Entry> readManifest() {
        Map<String, Entry> entries = new LinkedHashMap<>();
        if (!Files.exists(manifestPath)) {
            return entries;
        }
        try {
            for (String line : Files.readAllLines(manifestPath, StandardCharsets.UTF_8)) {
                String[] parts = line.split("\t", -1);
                if (parts.length == 5) {
                    entries.put(parts[0], new Entry(parts[0], parts[1], parts[2], parts[3], parts[4]));
                } else if (parts.length == 4) {
                    // A manifest written before ownership was recorded. The safe reading is "the user's
                    // file": the worst case is that a bundled update is not applied automatically,
                    // while the other reading would overwrite a route table.
                    entries.put(parts[0], new Entry(parts[0], parts[1], parts[2], parts[3], USER));
                }
            }
        } catch (IOException e) {
            // A manifest that cannot be read costs at most one upgrade decision; the files on disk
            // are what matters, so start over rather than refusing to launch.
            return new LinkedHashMap<>();
        }
        return entries;
    }

    private void writeManifest(Map<String, Entry> entries) throws IOException {
        var text = new StringBuilder();
        text.append("# Written by drelay. Columns: resource, installed path, installed sha256,"
                + " template sha256, owner (ours|user).\n");
        text.append("# A line whose owner is 'user' is never overwritten. Delete a line to let the"
                + " bundled copy be extracted again.\n");
        for (Entry entry : entries.values()) {
            text.append(entry.resource()).append('\t')
                    .append(entry.path()).append('\t')
                    .append(entry.installedHash()).append('\t')
                    .append(entry.templateHash()).append('\t')
                    .append(entry.owner()).append('\n');
        }
        Files.createDirectories(manifestPath.getParent());
        Files.writeString(manifestPath, text.toString(), StandardCharsets.UTF_8);
    }

    static String sha256(byte[] content) {
        try {
            var digest = MessageDigest.getInstance("SHA-256").digest(content);
            var hex = new StringBuilder(digest.length * 2);
            for (byte b : digest) {
                hex.append(Character.forDigit((b >> 4) & 0xF, 16)).append(Character.forDigit(b & 0xF, 16));
            }
            return hex.toString();
        } catch (NoSuchAlgorithmException e) {
            throw new IllegalStateException("SHA-256 is required by every Java runtime", e);
        }
    }
}
