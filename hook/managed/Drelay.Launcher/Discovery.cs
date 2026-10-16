// Discovery.cs -- finding the game when nobody said where it is.
//
// HANDOFF-LAUNCH-CONFIG.md section 7 is the specification.  The probe order is fixed and
// first-hit-wins:
//
//   1. HKCU\Software\Valve\Steam -> SteamPath (falling back to
//      HKLM\SOFTWARE\WOW6432Node\Valve\Steam -> InstallPath), then
//      <steam>\steamapps\libraryfolders.vdf, then every library in it.  This is the FIRST
//      probe and not the last, because on a Steam machine it is the only one that is right;
//      the pinned record is the fallback, not the other way round.
//   2. The pinned record, `GeneratedBuildTable.RecordedFrom` -- the machine the build was
//      verified on.  Exactly the case where it is right, and one absent path when it is wrong.
//   3. <launcher dir> and its parent -- covers "unpacked into the game folder", which is what
//      the release notes tell the user to do anyway.
//
// DISCOVERY PRODUCES A CANDIDATE; THE GATE DECIDES.  Finding a directory named
// `Darza's Dominion` does not make it the right build, and the two apphosts that ship together
// carry *different* assemblies: the root `DarzasDominion.exe` is the pinned one and the one
// under `OpenGL\` is not.  So a candidate is only accepted when the assembly beside the apphost
// is the pinned size, and the OpenGL variant is named as the problem rather than silently
// skipped -- a user with the OpenGL install needs to know that is what is wrong (section 7).
//
// THE ENVIRONMENT IS AN INTERFACE.  Everything this file needs -- the registry, the
// filesystem, the launcher's own directory -- arrives through `IDiscoveryEnv`, so the Steam
// library walk is testable against a fake tree with no Steam, no game and no registry
// (section 11 step 3).  The Windows implementation is `DiscoveryWindows.cs`, which is the one
// file the offline harness does not compile.

using System;
using System.Collections.Generic;
using System.IO;

namespace Drelay.Launcher
{
    /// <summary>Everything discovery is allowed to look at.</summary>
    internal interface IDiscoveryEnv
    {
        bool TryReadRegistryString(string keyPath, string valueName, out string value);
        string ReadTextOrNull(string path);
        bool DirectoryExists(string path);
        bool FileExists(string path);
        long FileSize(string path);
        string LauncherDirectory { get; }
    }

    /// <summary>
    /// The pinned build, as discovery needs it.  A parameter rather than a direct read of
    /// <c>GeneratedBuildTable</c> so that this file -- and the test that walks a fake Steam
    /// tree -- compiles without the bootstrap assembly.
    /// </summary>
    internal sealed class PinnedBuild
    {
        public string RecordedFrom;
        public string ExecutableFileName;
        public string AssemblyFileName;
        public long AssemblySize;
        public long AssemblySizeOpenGlVariant;
        public int SteamAppId;

        /// <summary>The directory name the pinned record used, e.g. `Darza's Dominion`.</summary>
        public string RecordedDirectoryName
        {
            get
            {
                if (string.IsNullOrEmpty(RecordedFrom)) { return null; }
                return Path.GetFileName(RecordedFrom.TrimEnd('\\', '/'));
            }
        }
    }

    internal sealed class DiscoveryResult
    {
        /// <summary>The game directory, or null when discovery failed.</summary>
        public string GameDirectory;

        /// <summary>The human-readable refusal, or null on success.</summary>
        public string Failure;

        /// <summary>
        /// True when the failure is "this is the OpenGL install".  It is a separate flag from
        /// `Failure` because the first-run path treats it differently: it is a refusal the user
        /// cannot fix by editing the config file (section 6's second rule), so it is printed and
        /// not paused on.
        /// </summary>
        public bool OpenGlVariant;

        public bool Found { get { return GameDirectory != null; } }
    }

    internal static class Discovery
    {
        private const string SteamKeyCurrentUser = @"HKEY_CURRENT_USER\Software\Valve\Steam";
        private const string SteamKeyMachine = @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam";
        private const string SteamPathValue = "SteamPath";
        private const string SteamInstallPathValue = "InstallPath";

        public static DiscoveryResult Find(IDiscoveryEnv env, PinnedBuild pinned)
        {
            var result = new DiscoveryResult();
            if (env == null || pinned == null)
            {
                result.Failure = "discovery has no environment to look in";
                return result;
            }

            var candidates = new List<string>();
            var problems = new List<string>();

            foreach (string probe in CandidateDirectories(env, pinned))
            {
                if (string.IsNullOrEmpty(probe)) { continue; }
                string directory = Trim(probe);
                if (!env.DirectoryExists(directory)) { continue; }

                string exe = Path.Combine(directory, pinned.ExecutableFileName);
                if (!env.FileExists(exe)) { continue; }

                string assembly = Path.Combine(directory, pinned.AssemblyFileName);
                if (!env.FileExists(assembly))
                {
                    problems.Add(directory + ": has " + pinned.ExecutableFileName
                                 + " but no " + pinned.AssemblyFileName);
                    continue;
                }

                long size = env.FileSize(assembly);
                if (size == pinned.AssemblySize)
                {
                    result.GameDirectory = directory;
                    return result;
                }
                if (size == pinned.AssemblySizeOpenGlVariant)
                {
                    // NAMED, NOT SKIPPED.  Retrying the next candidate would hide the one
                    // explanation the user needs: the hook only knows the root apphost, and this
                    // directory holds the OpenGL build.
                    result.OpenGlVariant = true;
                    result.Failure = directory + " holds the OpenGL build of the game ("
                                     + pinned.AssemblyFileName + " is " + size + " bytes, the pinned "
                                     + "DirectX one is " + pinned.AssemblySize + " bytes), and only the "
                                     + "root " + pinned.ExecutableFileName + " is the build this hook was "
                                     + "compiled against. Point GamePath at the root install.";
                    return result;
                }

                problems.Add(directory + ": " + pinned.AssemblyFileName + " is " + size
                             + " bytes, not the pinned " + pinned.AssemblySize);
            }

            result.Failure = problems.Count > 0
                ? "a directory named like the game was found, but not the pinned build: "
                  + string.Join("; ", problems.ToArray())
                : "no Steam library, no pinned location and no launcher directory held the game";
            return result;
        }

        /// <summary>The probe list, in order, with duplicates removed.</summary>
        private static IEnumerable<string> CandidateDirectories(IDiscoveryEnv env, PinnedBuild pinned)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var results = new List<string>();

            foreach (string candidate in SteamCandidates(env, pinned))
            {
                if (seen.Add(candidate)) { results.Add(candidate); }
            }

            foreach (string candidate in PinnedAndLocal(env, pinned))
            {
                if (seen.Add(candidate)) { results.Add(candidate); }
            }

            return results;
        }

        /// <summary>
        /// Probe 1: the Steam registry, the library list, and the app manifest.
        ///
        /// THE APP ID IS USED FOR THE MANIFEST, NOT FOR THE DIRECTORY.  `appmanifest_<id>.acf`
        /// names the install directory the user actually got (`"installdir"`), which is the
        /// authoritative answer and survives a rename; the pinned record's own leaf name is the
        /// fallback for a library whose manifest was not written yet.  Neither is trusted to be
        /// the right *build*: that is what the size check above is for.
        /// </summary>
        private static IEnumerable<string> SteamCandidates(IDiscoveryEnv env, PinnedBuild pinned)
        {
            var results = new List<string>();

            string steam = null;
            if (!env.TryReadRegistryString(SteamKeyCurrentUser, SteamPathValue, out steam)
                || string.IsNullOrEmpty(steam))
            {
                env.TryReadRegistryString(SteamKeyMachine, SteamInstallPathValue, out steam);
            }
            if (string.IsNullOrEmpty(steam)) { return results; }

            var libraries = new List<string>();
            string libraryFolders = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            string vdf = env.ReadTextOrNull(libraryFolders);
            if (vdf != null)
            {
                libraries.AddRange(ParseQuotedValue(vdf, "path"));
            }
            if (libraries.Count == 0)
            {
                // The registry path is itself a library, and on a single-drive install the VDF
                // says nothing the registry did not already say.
                libraries.Add(steam);
            }

            foreach (string library in libraries)
            {
                string common = Path.Combine(library, "steamapps", "common");

                string manifest = Path.Combine(library, "steamapps",
                                               "appmanifest_" + pinned.SteamAppId + ".acf");
                string acf = env.ReadTextOrNull(manifest);
                if (acf != null)
                {
                    List<string> installDirs = ParseQuotedValue(acf, "installdir");
                    if (installDirs.Count > 0)
                    {
                        results.Add(Path.Combine(common, installDirs[0]));
                    }
                }

                string recordedName = pinned.RecordedDirectoryName;
                if (!string.IsNullOrEmpty(recordedName))
                {
                    results.Add(Path.Combine(common, recordedName));
                }
            }

            return results;
        }

        /// <summary>Probes 2 and 3: the pinned record, then the launcher's own directory.</summary>
        private static IEnumerable<string> PinnedAndLocal(IDiscoveryEnv env, PinnedBuild pinned)
        {
            var results = new List<string>();
            if (!string.IsNullOrEmpty(pinned.RecordedFrom))
            {
                results.Add(pinned.RecordedFrom);
            }
            if (!string.IsNullOrEmpty(env.LauncherDirectory))
            {
                results.Add(env.LauncherDirectory);
                string parent = Path.GetDirectoryName(env.LauncherDirectory.TrimEnd('\\', '/'));
                if (!string.IsNullOrEmpty(parent)) { results.Add(parent); }
            }
            return results;
        }

        /// <summary>
        /// Every value written after a `"key"` in a Steam KeyValues file.
        ///
        /// A TOLERANT SCANNER RATHER THAN A PARSER, deliberately.  `libraryfolders.vdf` is a
        /// Valve KeyValues file with two historical shapes (numbered keys holding a plain path,
        /// and numbered blocks holding a `"path"`), and the only thing this launcher wants out of
        /// it is the list of strings that follow a given key.  A full parser would be a second
        /// implementation of Valve's format to keep in step with Steam's; a scanner that skips
        /// everything it does not recognise cannot disagree with Steam about anything that
        /// matters here.
        /// </summary>
        internal static List<string> ParseQuotedValue(string text, string key)
        {
            var values = new List<string>();
            if (string.IsNullOrEmpty(text)) { return values; }

            // ONE TOKEN AT A TIME, AND A MATCH CONSUMES THE TOKEN AFTER IT.
            //
            // The obvious shape -- read a token, read its value, read the next token -- assumes
            // the file is a flat list of pairs.  A KeyValues file is not: a nested block gives a
            // run of quoted tokens with an odd length between two braces, and the pairing then
            // drifts so that every later `"path"` is read as somebody else's *value*.  Consuming
            // only the matched key's own value keeps the scan aligned no matter how the blocks
            // nest, which is the only property this scanner needs.
            int i = 0;
            string token;
            while ((token = NextQuoted(text, ref i)) != null)
            {
                if (!string.Equals(token, key, StringComparison.OrdinalIgnoreCase)) { continue; }
                string value = NextQuoted(text, ref i);
                if (value == null) { break; }
                values.Add(Unescape(value));
            }
            return values;
        }

        /// <summary>The next quoted string, or null at the end of the text.</summary>
        private static string NextQuoted(string text, ref int i)
        {
            while (i < text.Length && text[i] != '"') { i++; }
            if (i >= text.Length) { return null; }
            i++; // opening quote
            int start = i;
            while (i < text.Length && text[i] != '"')
            {
                if (text[i] == '\\' && i + 1 < text.Length) { i++; }
                i++;
            }
            string token = text.Substring(start, Math.Min(i, text.Length) - start);
            if (i < text.Length) { i++; } // closing quote
            return token;
        }

        private static string Unescape(string value)
        {
            return value.Replace("\\\\", "\\").Replace("\\\"", "\"");
        }

        private static string Trim(string path)
        {
            string trimmed = path.Trim().Trim('"');
            trimmed = trimmed.Replace('/', '\\');
            return trimmed.TrimEnd('\\');
        }
    }
}
