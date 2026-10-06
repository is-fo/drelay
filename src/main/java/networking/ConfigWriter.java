package networking;

import networking.util.Json;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.util.Collection;
import java.util.LinkedHashMap;
import java.util.Map;

/**
 * Writes runtime settings back into the route table, so a change made in the dashboard survives a
 * restart.
 *
 * <h2>Why this exists</h2>
 *
 * <p>Until this class, the dashboard was a control surface with no memory: the auto-nexus threshold
 * and the strip's armed effects were applied to the running relay and lost when it exited. That is
 * fine for a knob being experimented with and wrong for a setting being settled on - "the setting I
 * chose last night is back to the default" is indistinguishable from "the setting did not work".
 *
 * <h2>Which file it writes, and why that is enough</h2>
 *
 * <p>The file the relay was started with - {@code --config}, which is the generated
 * {@code work/relay-routes.json} when the launcher starts it and the installed route table when a
 * developer starts it by hand. That file is *the* route table for this run, so a change written there
 * is in effect for the next one. {@link Routes} additionally carries these two blocks forward when the
 * launcher regenerates the file, which is what makes the setting survive the launcher as well; see
 * {@code Routes.write}.
 *
 * <h2>What it refuses to do</h2>
 *
 * <p>It never rewrites the file from a model of the settings alone. The whole document is read, two
 * top-level keys are replaced, and everything else - the comment block, the routes, the addresses the
 * launcher just resolved, keys a future version added - is written back untouched and in order. A
 * settings writer that dropped a key it did not recognise would be a far worse bug than not saving at
 * all, because it would corrupt the file the relay needs to start.
 *
 * <p>A failure is reported, never fatal: the setting has been applied to the running relay by the time
 * this runs, and a read-only install must not turn a tuning change into a session failure.
 */
public final class ConfigWriter {

    /** Serialises writers: two dashboard changes can arrive on two HTTP threads at once. */
    private static final Object LOCK = new Object();

    private ConfigWriter() {
    }

    /**
     * Replaces the given top-level keys in {@code path}, preserving every other key.
     *
     * @param path the route table the relay was started with
     * @param blocks the top-level keys to set, by name, in the order they should appear
     * @throws IOException when the file cannot be read, parsed or written
     */
    public static void update(Path path, Map<String, Object> blocks) throws IOException {
        if (path == null) {
            throw new IOException("no route table path is known, so the setting cannot be saved");
        }
        synchronized (LOCK) {
            Map<String, Object> root = new LinkedHashMap<>();
            if (Files.exists(path)) {
                String text = Files.readString(path, StandardCharsets.UTF_8);
                // An empty file is treated as an empty document rather than as a parse failure: the
                // relay can be started with a config it will then fill in, and a zero-byte file is the
                // state a first run that only wrote settings leaves behind.
                if (!text.isBlank()) {
                    Object parsed = JsonText.parsePlain(text);
                    if (!(parsed instanceof Map<?, ?> map)) {
                        throw new IOException("the route table is not a JSON object: " + path);
                    }
                    for (Map.Entry<?, ?> entry : map.entrySet()) {
                        root.put(String.valueOf(entry.getKey()), entry.getValue());
                    }
                }
            }
            root.putAll(blocks);
            write(path, render(root, 0) + "\n");
        }
    }

    /**
     * Writes by way of a temporary file in the same directory and a move.
     *
     * <p>Same rule as the launcher's own installer: a run interrupted mid-write leaves the previous
     * route table intact rather than a truncated one, which for this file is the difference between a
     * relay that starts with last night's settings and one that does not start at all.
     */
    private static void write(Path path, String text) throws IOException {
        Path absolute = path.toAbsolutePath();
        Path parent = absolute.getParent();
        if (parent != null) {
            Files.createDirectories(parent);
        }
        Path temporary = absolute.resolveSibling(absolute.getFileName() + ".drelay-new");
        Files.writeString(temporary, text, StandardCharsets.UTF_8);
        try {
            Files.move(temporary, absolute, StandardCopyOption.REPLACE_EXISTING,
                    StandardCopyOption.ATOMIC_MOVE);
        } catch (java.nio.file.AtomicMoveNotSupportedException e) {
            Files.move(temporary, absolute, StandardCopyOption.REPLACE_EXISTING);
        }
    }

    /**
     * JSON with two-space indentation, arrays one element per line.
     *
     * <p>Indented rather than compact because this file is edited by hand: the comment block alone is
     * dozens of strings, and collapsing it onto one line would make the file the relay maintains
     * harder to read than the one it replaced. Strings go through {@link Json#quote(String)} so the
     * result is valid JSON for any value a setting can hold.
     */
    static String render(Object value, int indent) {
        if (value == null) {
            return "null";
        }
        if (value instanceof String s) {
            return Json.quote(s);
        }
        if (value instanceof Boolean b) {
            return b ? "true" : "false";
        }
        if (value instanceof Double d) {
            if (!Double.isFinite(d)) {
                return "null";
            }
            return d == Math.rint(d) && Math.abs(d) < 1e15 ? Long.toString(d.longValue())
                    : Double.toString(d);
        }
        if (value instanceof Number n) {
            return n.toString();
        }
        if (value instanceof Collection<?> collection) {
            if (collection.isEmpty()) {
                return "[]";
            }
            StringBuilder out = new StringBuilder("[");
            int i = 0;
            for (Object item : collection) {
                out.append(i++ == 0 ? "\n" : ",\n")
                        .append(" ".repeat(indent + 2))
                        .append(render(item, indent + 2));
            }
            return out.append('\n').append(" ".repeat(indent)).append(']').toString();
        }
        if (value instanceof Map<?, ?> map) {
            if (map.isEmpty()) {
                return "{}";
            }
            StringBuilder out = new StringBuilder("{\n");
            int i = 0;
            for (Map.Entry<?, ?> entry : map.entrySet()) {
                out.append(" ".repeat(indent + 2))
                        .append(Json.quote(String.valueOf(entry.getKey())))
                        .append(": ")
                        .append(render(entry.getValue(), indent + 2))
                        .append(++i < map.size() ? ",\n" : "\n");
            }
            return out.append(" ".repeat(indent)).append('}').toString();
        }
        return Json.quote(String.valueOf(value));
    }
}
