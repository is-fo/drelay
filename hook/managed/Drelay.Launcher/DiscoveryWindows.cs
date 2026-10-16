// DiscoveryWindows.cs -- the real registry and filesystem behind `IDiscoveryEnv`.
//
// THE ONE FILE THE OFFLINE HARNESS DOES NOT COMPILE, and that is the point of the split:
// `Discovery.cs` holds every decision (probe order, the library walk, the build gate) and this
// file holds the four syscalls.  A test of the walk against a fake tree therefore tests the
// shipped logic rather than a second copy of it, and this file is small enough to read in one
// sitting -- which is the only honest way to be sure that the fake and the real environment
// implement the same contract.

using System;
using System.IO;
using System.Runtime.Versioning;
using System.Text;

namespace Drelay.Launcher
{
    // The registry is Windows-only and this class is the only place in the launcher that touches
    // it, so the platform requirement is stated once, here, rather than at every call site.  The
    // caller guards the construction with `OperatingSystem.IsWindows()`, which is what keeps the
    // analyzer's promise honest rather than merely silenced.
    [SupportedOSPlatform("windows")]
    internal sealed class WindowsDiscoveryEnv : IDiscoveryEnv
    {
        private readonly string _launcherDirectory;

        public WindowsDiscoveryEnv(string launcherDirectory)
        {
            _launcherDirectory = launcherDirectory;
        }

        public string LauncherDirectory
        {
            get { return _launcherDirectory; }
        }

        /// <summary>
        /// A string value from the registry, with the hive written into the key path.
        ///
        /// The path form is `HKEY_CURRENT_USER\...`, which is how the documents name these keys;
        /// mapping it here rather than at every call site keeps the probe list readable and keeps
        /// the hive choice in one place.
        /// </summary>
        public bool TryReadRegistryString(string keyPath, string valueName, out string value)
        {
            value = null;
            if (string.IsNullOrEmpty(keyPath)) { return false; }

            Microsoft.Win32.RegistryKey hive;
            string subKey;
            if (keyPath.StartsWith("HKEY_CURRENT_USER\\", StringComparison.OrdinalIgnoreCase))
            {
                hive = Microsoft.Win32.Registry.CurrentUser;
                subKey = keyPath.Substring("HKEY_CURRENT_USER\\".Length);
            }
            else if (keyPath.StartsWith("HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase))
            {
                hive = Microsoft.Win32.Registry.LocalMachine;
                subKey = keyPath.Substring("HKEY_LOCAL_MACHINE\\".Length);
            }
            else
            {
                return false;
            }

            try
            {
                using (var key = hive.OpenSubKey(subKey, writable: false))
                {
                    if (key == null) { return false; }
                    value = key.GetValue(valueName) as string;
                    return !string.IsNullOrEmpty(value);
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException
                                       || ex is UnauthorizedAccessException
                                       || ex is IOException)
            {
                // A registry that cannot be read is a discovery that finds nothing through this
                // probe, not a launcher that fails to start.  The game may be one directory away.
                return false;
            }
        }

        /// <summary>
        /// A small text file, or null.  Bounded by size on purpose: `libraryfolders.vdf` is a few
        /// kilobytes and `appmanifest_*.acf` is smaller, so a file claiming to be one of them and
        /// weighing a gigabyte is not one of them, and reading it into memory would be the
        /// launcher's fault rather than the user's.
        /// </summary>
        public string ReadTextOrNull(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) { return null; }
                var info = new FileInfo(path);
                if (info.Length <= 0 || info.Length > 4 * 1024 * 1024) { return null; }
                // Steam writes these in the system code page; ASCII is a subset of both that and
                // UTF-8, and every byte this scanner looks at -- quotes, braces, digits, slashes
                // -- is ASCII.  Reading as UTF-8 with replacement avoids a BOM-at-the-front edge
                // case and cannot shift the bytes that matter.
                using (var reader = new StreamReader(path, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                                       || ex is NotSupportedException || ex is ArgumentException)
            {
                return null;
            }
        }

        public bool DirectoryExists(string path)
        {
            try
            {
                return !string.IsNullOrEmpty(path) && Directory.Exists(path);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException
                                       || ex is PathTooLongException)
            {
                return false;
            }
        }

        public bool FileExists(string path)
        {
            try
            {
                return !string.IsNullOrEmpty(path) && File.Exists(path);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException
                                       || ex is PathTooLongException)
            {
                return false;
            }
        }

        public long FileSize(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                                       || ex is ArgumentException || ex is NotSupportedException
                                       || ex is PathTooLongException)
            {
                return -1;
            }
        }
    }
}
