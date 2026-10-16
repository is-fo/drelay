package networking;

import java.util.ArrayList;
import java.util.List;

/**
 * The gate for {@link Updater}'s release-list arithmetic: which releases this build will install.
 *
 * <p>Run with {@code java -cp target/classes networking.UpdaterTests}. Exits non-zero on the first
 * failure. It touches no network - the whole point is that the decision is a pure function of the
 * tag and the running version, so it can be checked offline.
 *
 * <h2>What is actually being defended</h2>
 *
 * <ol>
 *   <li><b>One repository, two products.</b> The hook's releases are tagged {@code hook-v<version>}
 *       and appear in the same {@code /releases} list the proxy reads. {@link Updater#isProxyTag}
 *       is what keeps them out of the comparison. It is not a cosmetic filter: {@code compare} reads
 *       any non-numeric component as {@code 0}, so without the check a hook tag is not rejected - it
 *       is read as a version, silently, and the answer it produces is whatever the leading digits
 *       happen to be.</li>
 *   <li><b>A tag that is not a version must never be compared as one.</b> The historical release was
 *       tagged {@code 50} (its release <em>name</em> was {@code v0.0.1-alpha}), and {@code "50"}
 *       compares as newer than every {@code 0.0.x} this project has ever published. A tag shape
 *       check refuses it rather than trusting the list's contents.</li>
 *   <li><b>The tag is what carries the version, and the {@code v} is optional.</b> Both spellings
 *       exist in the wild and both must be read as the same version.</li>
 * </ol>
 */
public final class UpdaterTests {

    private static final List<String> FAILURES = new ArrayList<>();
    private static int checks;

    public static void main(String[] args) {
        testProxyTagsAreAccepted();
        testHookAndUnrelatedTagsAreRefused();
        testNormalise();
        testPrereleaseOrdersBelowItsRelease();
        testNonNumericComponentsReadAsZero();

        if (FAILURES.isEmpty()) {
            IO.println("PASS: " + checks + " release-selection checks passed");
            return;
        }
        IO.println("FAIL: " + FAILURES.size() + " of " + checks + " check(s) failed");
        for (String failure : FAILURES) {
            IO.println("  - " + failure);
        }
        System.exit(1);
    }

    // --- the tag shape ------------------------------------------------------------------------

    private static void testProxyTagsAreAccepted() {
        for (String tag : new String[] {"v0.0.2-alpha", "v1.2.3", "1.2.3", "V1.2.3", "v0.0.10",
                "v2.0.0-rc1", "v1.0.0-SNAPSHOT.2"}) {
            check(Updater.isProxyTag(tag), "proxy tag " + tag + " must be accepted");
        }
    }

    private static void testHookAndUnrelatedTagsAreRefused() {
        for (String tag : new String[] {"hook-v0.1.0", "hook-v1.2.3", "50", "", "latest",
                "v1.2", "1.2.3.4", "release-1.2.3", "v-1.2.3"}) {
            check(!Updater.isProxyTag(tag), "tag " + tag + " must be refused as a proxy version");
        }
        check(!Updater.isProxyTag(null), "a null tag must be refused");
    }

    // --- normalise ----------------------------------------------------------------------------

    private static void testNormalise() {
        checkEquals("1.2.3", Updater.normalise("v1.2.3"), "a lowercase v prefix is stripped");
        checkEquals("1.2.3", Updater.normalise("V1.2.3"), "an uppercase V prefix is stripped");
        checkEquals("1.2.3", Updater.normalise("1.2.3"), "a bare version is left alone");
        checkEquals("0.0.2-alpha", Updater.normalise("v0.0.2-alpha"), "the prerelease suffix survives");
        checkEquals("hook-v1.2.3", Updater.normalise("hook-v1.2.3"),
                "a hook tag is not silently turned into a version - isProxyTag is what refuses it");
    }

    // --- the comparison -----------------------------------------------------------------------

    private static void testPrereleaseOrdersBelowItsRelease() {
        check(Updater.compare("1.1.0-rc1", "1.1.0") < 0, "1.1.0-rc1 sorts below 1.1.0");
        check(Updater.compare("1.1.0", "1.1.0-rc1") > 0, "1.1.0 sorts above 1.1.0-rc1");
        checkEquals(0, Updater.compare("1.1.0", "1.1.0"), "two identical releases compare equal");
    }

    private static void testNonNumericComponentsReadAsZero() {
        // These are the exact readings that make isProxyTag load-bearing: compare() does not fail on
        // a tag that is not a version, it answers.
        check(Updater.compare("hook-v1.2.3", "0.0.2-alpha") < 0,
                "'hook-v1.2.3' reads as 0.0.0 and sorts below 0.0.2 - 'hook' is a non-numeric"
                        + " component, so the 1.2.3 in the tag is never seen");
        check(Updater.compare("50", "0.0.2-alpha") > 0,
                "the historical bare '50' tag reads as version 50 and IS newer than 0.0.2-alpha,"
                        + " which is why the tag's shape has to be checked rather than trusted");
        checkEquals(1, Updater.compare("0.0.10", "0.0.9"), "components compare numerically, not as text");
    }

    // --- helpers ------------------------------------------------------------------------------

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
