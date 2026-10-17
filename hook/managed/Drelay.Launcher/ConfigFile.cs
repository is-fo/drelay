// ConfigFile.cs -- the user-facing configuration file.
//
// HANDOFF-LAUNCH-CONFIG.md section 5 is the specification: the file lives at
// %LOCALAPPDATA%\drelay\hook\drelay.config.json, and it is read with System.Text.Json
// (already in the shared framework -- no new dependency) into a `Settings`, the same type
// the command line fills, so that the merge in `Options.Apply` is the only place
// precedence exists.
//
// THREE RULES FROM SECTION 5, AND EACH ONE IS A DESIGN DECISION RATHER THAN A STYLE:
//
//   1. A MISSING FILE IS NOT AN ERROR.  It means "defaults", and the first-run path
//      (section 6) is the only thing that creates one.
//   2. AN UNPARSEABLE *FIELD* IS A REFUSAL WITH A NAMED FIELD, NOT A SILENT DEFAULT.  A
//      typo in `"AutoAim": "closests"` must fail the way `--autoaim closests` already
//      does.  A typo that silently resolves to "off" costs a session spent watching the
//      game not aim.
//   3. TEST-HARNESS FLAGS ARE NOT IN THE SCHEMA.  `--test-target`, `--frame-helper`,
//      `--frame-replacement`, `--assembly`, `--exe`, `--off`, `--full-trace`, `--trace`,
//      `--world-log`, `--world-log-every`, `--move-steer` and `--escape-test` stay
//      command-line-only: a user-facing config that offers `FrameHelper` is offering a switch
//      that allocates 64 KB per frame on purpose, and one that offers `--world-log` is offering
//      a JSON line every 30 frames in every session it starts.  A config file that mentions one
//      of them is refusing nothing -- the key is simply unknown, and unknown keys are ignored.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Drelay.Launcher
{
    /// <summary>
    /// One row of the schema: the JSON key as a user writes it, and the flag it lands on.
    ///
    /// THE TABLE IS THE ANSWER TO SECTION 13 C7 ("every field of the config has a
    /// corresponding flag, and every user-facing flag a field").  It is data rather than
    /// prose so the test can assert it in both directions instead of trusting a table in a
    /// document: `LauncherHarness --check c7` walks this array, and separately compares it
    /// against the keys the reader actually recognised on a document that contains exactly
    /// these keys.
    /// </summary>
    internal sealed class ConfigField
    {
        public readonly string Key;
        public readonly string Flag;

        public ConfigField(string key, string flag)
        {
            Key = key;
            Flag = flag;
        }
    }

    internal static class ConfigFile
    {
        public const string FileName = "drelay.config.json";

        /// <summary>The field table of section 5.1, as data.</summary>
        public static readonly ConfigField[] Schema = new ConfigField[]
        {
            new ConfigField("GamePath", "--game"),
            new ConfigField("AutoAim", "--autoaim"),
            new ConfigField("Dodge.Enabled", "--dodge"),
            new ConfigField("Dodge.Act", "--dodge-act"),
            new ConfigField("Dodge.DecisionDisc", "--dodge-disc"),
            new ConfigField("Dodge.WindowSeconds", "--dodge-window"),
            new ConfigField("CollectDelayMs", "--collect-delay"),
            new ConfigField("DiscoveryWindowMs", "--discovery-ms"),
            new ConfigField("ResolveDelayMs", "--resolve-delay"),
            new ConfigField("LiveSeconds", "--live-seconds"),
            new ConfigField("PauseAfterRun", "--pause-after-run"),
            new ConfigField("RunRoot", "--runroot"),
            new ConfigField("DbgDump", "--dbg-dump"),
            new ConfigField("Verbose", "--verbose"),
            new ConfigField("NoWait", "--no-wait"),
            new ConfigField("TimeoutMs", "--timeout"),
            new ConfigField("Hook", "--hook"),
            new ConfigField("Bootstrap", "--bootstrap"),
        };

        /// <summary>
        /// The default location: `%LOCALAPPDATA%\drelay\hook\drelay.config.json`, which is the
        /// same root the run directories are already created under.
        ///
        /// NOT BESIDE THE EXECUTABLE, because the zip may be unpacked somewhere read-only
        /// (Program Files, a network share), and a first-run experience that cannot write its
        /// own config is not a first-run experience.
        /// </summary>
        public static string Directory(string localAppData)
        {
            if (string.IsNullOrEmpty(localAppData))
            {
                localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }
            return Path.Combine(localAppData, "drelay", "hook");
        }

        public static string DefaultPath(string localAppData)
        {
            return Path.Combine(Directory(localAppData), FileName);
        }

        /// <summary>
        /// Read the file into a `Settings` in which only the keys actually present are set.
        ///
        /// <paramref name="recognised"/> is how many keys of the schema were seen, and it is an
        /// output rather than a hidden counter for the C7 test: fed a document containing every
        /// key in <see cref="Schema"/>, the reader must recognise exactly those keys, so a key
        /// added to this method without being added to the table is a test failure rather than a
        /// silent extra setting.
        /// </summary>
        public static bool TryRead(string path, out Settings settings, out List<string> recognised,
                                   out string error)
        {
            settings = new Settings();
            recognised = new List<string>();
            error = null;

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                // Rule 1: a missing file is not an error.
                return true;
            }

            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (IOException ex)
            {
                error = path + ": cannot be read: " + ex.Message;
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                error = path + ": cannot be read: " + ex.Message;
                return false;
            }

            if (text.Trim().Length == 0)
            {
                // An empty file is a file with nothing in it, which is what a user who opened the
                // template and deleted the contents has.  It means "defaults", like a missing file.
                return true;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(text, new JsonDocumentOptions
                {
                    // `CommentHandling = Skip` and `AllowTrailingCommas` so a user's stray comment
                    // or trailing comma is not a startup failure.  The template itself is written
                    // with comments, so this is not a convenience: it is required by the file the
                    // launcher writes.
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
            }
            catch (JsonException ex)
            {
                error = path + ": is not valid JSON: " + ex.Message;
                return false;
            }

            using (document)
            {
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    error = path + ": the top level must be a JSON object, not " + Describe(root);
                    return false;
                }

                foreach (JsonProperty property in root.EnumerateObject())
                {
                    if (!ApplyProperty(path, property, settings, recognised, ref error))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// One top-level key.  Unknown keys are ignored (section 11 step 2); a known key with the
        /// wrong type or a bad value refuses and names the key.
        /// </summary>
        private static bool ApplyProperty(string file, JsonProperty property, Settings settings,
                                          List<string> recognised, ref string error)
        {
            switch (property.Name.ToLowerInvariant())
            {
                case "gamepath":
                    recognised.Add("GamePath");
                    return ReadString(file, property, ref settings.GamePath, ref error);

                case "autoaim":
                    recognised.Add("AutoAim");
                    if (property.Value.ValueKind == JsonValueKind.Null)
                    {
                        // Null is "off", which is the same statement as the key's absence: the
                        // written template leaves it null rather than "closest", because aiming
                        // is an action with a real side effect (`Prefs.enable_aimbot` is set and
                        // persisted).  Section 9.3.
                        settings.AutoAim = null;
                        return true;
                    }
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        error = Field(file, "AutoAim", "must be a mode name or null, got " + Describe(property.Value));
                        return false;
                    }
                    if (!Options.TryParseAutoAim(property.Value.GetString(), out string mode, out string why))
                    {
                        error = file + ": AutoAim " + why;
                        return false;
                    }
                    settings.AutoAim = mode;
                    return true;

                case "dodge":
                    return ReadDodge(file, property, settings, recognised, ref error);

                case "collectdelayms":
                    recognised.Add("CollectDelayMs");
                    return ReadInt(file, property, "CollectDelayMs", ref settings.CollectDelayMs, ref error);

                case "discoverywindowms":
                    recognised.Add("DiscoveryWindowMs");
                    return ReadInt(file, property, "DiscoveryWindowMs", ref settings.DiscoveryWindowMs, ref error);

                case "resolvedelayms":
                    recognised.Add("ResolveDelayMs");
                    return ReadInt(file, property, "ResolveDelayMs", ref settings.ResolveDelayMs, ref error);

                case "liveseconds":
                    recognised.Add("LiveSeconds");
                    return ReadInt(file, property, "LiveSeconds", ref settings.LiveSeconds, ref error);

                case "pauseafterrun":
                    recognised.Add("PauseAfterRun");
                    return ReadBool(file, property, "PauseAfterRun", ref settings.PauseAfterRun, ref error);

                case "runroot":
                    recognised.Add("RunRoot");
                    return ReadString(file, property, ref settings.RunRoot, ref error);

                case "dbgdump":
                    recognised.Add("DbgDump");
                    return ReadBool(file, property, "DbgDump", ref settings.DbgDump, ref error);

                case "verbose":
                    recognised.Add("Verbose");
                    return ReadBool(file, property, "Verbose", ref settings.Verbose, ref error);

                case "nowait":
                    recognised.Add("NoWait");
                    return ReadBool(file, property, "NoWait", ref settings.NoWait, ref error);

                case "timeoutms":
                    recognised.Add("TimeoutMs");
                    return ReadInt(file, property, "TimeoutMs", ref settings.TimeoutMs, ref error);

                case "hook":
                    recognised.Add("Hook");
                    return ReadString(file, property, ref settings.HookDll, ref error);

                case "bootstrap":
                    recognised.Add("Bootstrap");
                    return ReadString(file, property, ref settings.BootstrapDll, ref error);

                default:
                    // Unknown keys are ignored, on purpose: a config that carries a key from a
                    // later version must not stop this one from starting.
                    return true;
            }
        }

        private static bool ReadDodge(string file, JsonProperty property, Settings settings,
                                      List<string> recognised, ref string error)
        {
            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                return true;
            }
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                error = Field(file, "Dodge", "must be an object, got " + Describe(property.Value));
                return false;
            }
            recognised.Add("Dodge");
            foreach (JsonProperty child in property.Value.EnumerateObject())
            {
                switch (child.Name.ToLowerInvariant())
                {
                    case "enabled":
                        recognised.Add("Dodge.Enabled");
                        if (!ReadBool(file, child, "Dodge.Enabled", ref settings.Dodge, ref error)) { return false; }
                        break;
                    case "act":
                        recognised.Add("Dodge.Act");
                        if (!ReadBool(file, child, "Dodge.Act", ref settings.DodgeAct, ref error)) { return false; }
                        // `Act` implies `Enabled`, exactly as `--dodge-act` does.  The implication
                        // itself is applied once, in `Options.ApplyImplications`.
                        break;
                    case "decisiondisc":
                        recognised.Add("Dodge.DecisionDisc");
                        if (!ReadDouble(file, child, "Dodge.DecisionDisc", ref settings.DodgeDisc, ref error))
                        {
                            return false;
                        }
                        break;
                    case "windowseconds":
                        recognised.Add("Dodge.WindowSeconds");
                        if (!ReadDouble(file, child, "Dodge.WindowSeconds", ref settings.DodgeWindowSeconds, ref error))
                        {
                            return false;
                        }
                        break;
                    default:
                        break;
                }
            }
            return true;
        }

        private static bool ReadBool(string file, JsonProperty property, string key,
                                     ref bool? value, ref string error)
        {
            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                // Null means "not set" everywhere in this schema, which is what keeps the
                // compiled default in charge (section 10.5).
                return true;
            }
            if (property.Value.ValueKind != JsonValueKind.True && property.Value.ValueKind != JsonValueKind.False)
            {
                error = Field(file, key, "must be true or false, got " + Describe(property.Value));
                return false;
            }
            value = property.Value.GetBoolean();
            return true;
        }

        private static bool ReadInt(string file, JsonProperty property, string key,
                                    ref int? value, ref string error)
        {
            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                return true;
            }
            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out int parsed))
            {
                if (parsed < 0)
                {
                    error = Field(file, key, "must be a non-negative whole number, got " + parsed);
                    return false;
                }
                value = parsed;
                return true;
            }
            error = Field(file, key, "must be a non-negative whole number, got " + Describe(property.Value));
            return false;
        }

        private static bool ReadDouble(string file, JsonProperty property, string key,
                                       ref double? value, ref string error)
        {
            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                // `null` here is the same statement as `-1.0` on the command line: "not set", so
                // the solver's own default applies.
                return true;
            }
            if (property.Value.ValueKind != JsonValueKind.Number
                || !property.Value.TryGetDouble(out double parsed)
                || parsed < 0.0
                || double.IsNaN(parsed)
                || double.IsInfinity(parsed))
            {
                error = Field(file, key, "must be a non-negative number or null, got " + Describe(property.Value));
                return false;
            }
            value = parsed;
            return true;
        }

        private static bool ReadString(string file, JsonProperty property, ref string value, ref string error)
        {
            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                return true;
            }
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                error = Field(file, property.Name, "must be a string or null, got " + Describe(property.Value));
                return false;
            }
            string text = property.Value.GetString();
            if (!string.IsNullOrEmpty(text))
            {
                value = text;
            }
            return true;
        }

        private static string Field(string file, string key, string what)
        {
            return file + ": " + key + " " + what;
        }

        private static string Describe(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String: return "a string";
                case JsonValueKind.Number: return "a number";
                case JsonValueKind.True:
                case JsonValueKind.False: return "a boolean";
                case JsonValueKind.Array: return "an array";
                case JsonValueKind.Object: return "an object";
                case JsonValueKind.Null: return "null";
                default: return element.ValueKind.ToString().ToLowerInvariant();
            }
        }

        /// <summary>
        /// The template a first run writes, and -- because it is written before the configuration
        /// is resolved -- the settings that first run actually uses.
        ///
        /// IT IS NOT THE COMPILED DEFAULTS, AND THE DIFFERENCE IS THE POINT.  `Options`' defaults
        /// are all-off so that nothing acts unless it was asked; this file is the asking.  It turns
        /// on the two things a drelay session is for -- auto-aim at the closest damageable enemy,
        /// and the live dodge -- and it says so in the comments, in the same file, so that turning
        /// either off is an edit rather than a search.
        ///
        /// THIS DEVIATES FROM SECTION 9.3, WHICH SAID THE TEMPLATE LEAVES `AutoAim` NULL, AND THE
        /// DEVIATION IS THE OPERATOR'S DECISION RATHER THAN AN OVERSIGHT.  The argument against it
        /// stands and is worth remembering: aiming arms and persists the game's own
        /// `Prefs.enable_aimbot`, and the dodge moves the player.  The argument for it is that the
        /// alternative shipped a launcher whose headline feature -- "double-click it" -- attached a
        /// hook that did nothing, which is what the first no-argument run of 2026-10-16 reported.
        /// §15 records the decision and the state the file is in now.
        ///
        /// `DecisionDisc` AND `WindowSeconds` STAY NULL because their defaults belong to
        /// `DodgeConfig.Default()` and are not written down twice (section 10.5).  `ResolveDelayMs`,
        /// `RunRoot`, `DbgDump`, `Verbose`, `NoWait`, `TimeoutMs`, `Hook` and `Bootstrap` are in the
        /// schema and deliberately NOT in the template.
        /// </summary>
        public static string Template()
        {
            var text = new StringBuilder();
            text.Append("{\n");
            text.Append("  // drelay hook configuration. Written by a first run, and used by it.\n");
            text.Append("  //\n");
            text.Append("  // This is what a launch with no arguments does: auto-aim at the closest\n");
            text.Append("  // damageable enemy, and the live dodge (the solver decides every frame and\n");
            text.Append("  // steers the player when it has a move). Turn either off by setting\n");
            text.Append("  // \"AutoAim\" to null or \"Dodge\".\"Act\" to false.\n");
            text.Append("  //\n");
            text.Append("  // Every key is optional; a key that is absent or null means \"the launcher's\n");
            text.Append("  // default\". The command line always wins over this file: `--autoaim highesthp`\n");
            text.Append("  // beside \"AutoAim\": \"closest\" runs highesthp.\n");
            text.Append("\n");
            text.Append("  // Required unless the game is discovered or --game is given.\n");
            text.Append("  \"GamePath\": \"\",\n");
            text.Append("\n");
            text.Append("  // What the hook does. These are the release-relevant ones.\n");
            text.Append("  \"AutoAim\": \"closest\",     // \"closest\" | \"highesthp\" | null (off)\n");
            text.Append("  \"Dodge\": {\n");
            text.Append("    \"Enabled\": true,        // run the solver every frame and log decisions\n");
            text.Append("    \"Act\": true,            // ...and let the decision steer the player.\n");
            text.Append("                            // false still solves and logs; it just never moves you\n");
            text.Append("    \"DecisionDisc\": null,   // --dodge-disc, multiples of the disc-crossing time\n");
            text.Append("    \"WindowSeconds\": null   // --dodge-window, absolute floor in seconds\n");
            text.Append("  },\n");
            text.Append("\n");
            text.Append("  // Timing. The first two are reporting bounds: the patch no longer waits for\n");
            text.Append("  // the discovery window, so these only decide when the per-target verdicts are\n");
            text.Append("  // measured and how long the evidence recording runs.\n");
            text.Append("  \"CollectDelayMs\": 0,\n");
            text.Append("  \"DiscoveryWindowMs\": 10000,\n");
            text.Append("  \"LiveSeconds\": 5,\n");
            text.Append("  \"PauseAfterRun\": false    // true restores the pre-0.2 behaviour: stop reading\n");
            text.Append("                            // the world when the evidence window closes\n");
            text.Append("}\n");
            return text.ToString();
        }

        /// <summary>
        /// Write the template, creating the directory if it is not there yet.
        ///
        /// IT NEVER OVERWRITES: it is called only when the file does not exist, and it checks once
        /// more here so a future caller cannot delete a user's settings by accident.
        /// </summary>
        public static bool WriteTemplate(string path, out string error)
        {
            error = null;
            try
            {
                if (File.Exists(path))
                {
                    return true;
                }
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    System.IO.Directory.CreateDirectory(dir);
                }
                File.WriteAllText(path, Template(), new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                                       || ex is NotSupportedException || ex is ArgumentException)
            {
                error = path + ": cannot be written: " + ex.Message;
                return false;
            }
        }
    }
}
