package networking;

import java.io.IOException;
import java.io.InputStream;
import java.io.UncheckedIOException;
import java.util.List;
import java.util.Properties;

/**
 * Access to the files that live inside the jar.
 *
 * <p>The release artifact is a single jar that a user downloads and runs, so everything the launcher
 * needs at runtime - the route table template, its own PowerShell helpers and the version string -
 * ships as a classpath resource and is written out on demand by {@link Sync}.
 */
final class Resources {

    /** Version and release repository, read from {@code /drelay.properties}. */
    static final String VERSION;
    static final String REPOSITORY;

    /**
     * Files copied out of the jar, in the order they are installed.
     *
     * <p>Each name is also the path it is installed to, relative to the working directory. The
     * {@code tools/} prefix on the PowerShell scripts is not cosmetic: both scripts locate their
     * working directory as {@code (parent of script root)}, and the development tree has them under
     * {@code tools/}, so the jar reproduces the layout the scripts are written against.
     */
    static final List<String> INSTALLED = List.of(
            "relay-routes.json",
            "tools/Set-AddressClaim.ps1",
            "tools/Restore-HostsFile.ps1");

    static {
        var properties = new Properties();
        try (InputStream in = Resources.class.getResourceAsStream("/drelay.properties")) {
            if (in != null) {
                properties.load(in);
            }
        } catch (IOException e) {
            throw new UncheckedIOException(e);
        }
        VERSION = properties.getProperty("version", "0.0.0").trim();
        REPOSITORY = properties.getProperty("repository", "is-fo/drelay").trim();
    }

    private Resources() {
    }

    /** The name this resource is installed as, which is the resource's own path in the jar. */
    static String installPath(String resource) {
        return resource;
    }

    static byte[] read(String resource) throws IOException {
        try (InputStream in = Resources.class.getResourceAsStream("/" + resource)) {
            if (in == null) {
                throw new IOException("the jar does not contain " + resource);
            }
            return in.readAllBytes();
        }
    }
}
