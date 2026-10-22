// WorldLogVerify -- drives the real WorldLog.cs against a fake game and parses what it writes.
//
// WHY THIS EXISTS. `worlds.jsonl` is what the operator's collection session produces, and it is the
// only source of the map names, portal titles and realm geometry that PLAN-AUTOFARM.md sections 5
// and 8 are blocked on. A session logged by a writer that emits malformed JSON, or that fails to
// escape a portal title containing a quote, is a session that has to be run again -- and this
// project already paid for exactly that once, when the trace recorder's unescaped newline turned a
// healthy recording into a file every reader reported as CORRUPT.
//
// So the writer is exercised offline: the REAL WorldLog.cs and the REAL EnemyReader.cs are compiled
// here, against a fake game whose field names are the client's own, and every line the writer emits
// is parsed by MiniJson (copied from the SolverHarness, which is the project's existing reader).
using System;
using System.Collections.Generic;
using System.IO;
using Drelay.Bootstrap;
using Drelay.SolverHarness;

internal static class Program
{
    private static int _failures;

    private static int Main()
    {
        Environment.SetEnvironmentVariable("DRELAY_WORLDLOG", "1");
        // THE LOG GOES BESIDE THE BUILD OUTPUT AND NOT INTO THE REPOSITORY. `bin/` is gitignored with
        // the rest of the build, so a CI run of this check leaves nothing behind -- and the run
        // directory is not a temp path either, because a sandboxed shell may not be allowed to write
        // there while the process itself is.
        string dir = Path.Combine(AppContext.BaseDirectory, "verify-run");
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }
        Directory.CreateDirectory(dir);

        // --- the fake world ---------------------------------------------------------------
        var world = new DarzasDominion.Darza.Game.World();
        world.Player = new DarzasDominion.Darza.Game.Objects.Entities.Player();
        world.Player.Position = new Microsoft.Xna.Framework.Vector3(100.0f, 100.0f, 0.0f);
        world.MapInfo = new DarzaGameNet.Packets.Game.GameServer.GmMapInfo
        {
            Name = "The Tavern",
            AssetName = "nexus",
            Width = 140,
            Height = 96,
            PlayerId = 7,
            Difficulty = 3,
            IsSoloRun = false,
            OpenedByKey = true,
            CanTeleport = false,
            // A TITLE WITH EVERYTHING THAT BREAKS A HAND-ROLLED WRITER. The operator's session is
            // user-visible free text off the wire; a quote, a backslash and a newline in one is not
            // hypothetical, and the trace recorder's own header is the precedent.
            Levels = "1-20",
        };
        world.SafeWorld = true;
        // THE PORTAL'S POSITION LIVES ON `Node.Position`, A Vector3 -- THERE IS NO `X` ON A PORTAL.
        // The first version of this fake gave it one, which is why the verifier never noticed that
        // the reader was looking for a field the client does not have.
        world.Activatables.Add(new DarzasDominion.Darza.Game.Objects.Map.Activatable.Portal
        {
            GameId = 501,
            Position = new Microsoft.Xna.Framework.Vector3(101.5f, 103.25f, 0.0f),
            Title = "Realm \"of\" Doom\\Night\nline two",
            Tag = "realm:hard",
        });
        ((DarzasDominion.Darza.Game.Objects.Map.Activatable.Portal)world.Activatables[0])
            .SetLocked(true);
        // A NON-PORTAL ACTIVATABLE, which is what a lobby full of furniture actually is. Without it
        // the `others` block was never exercised, and the block exists because the 2026-10-18
        // re-run could not answer "which object in this room leads to the realm" -- the file said
        // "29 other activatables" and nothing else.
        world.Activatables.Add(
            new DarzasDominion.Darza.Game.Objects.Map.Activatable.SoloDungeonStatue
            {
                GameId = 902,
                Position = new Microsoft.Xna.Framework.Vector3(104.0f, 102.0f, 0.0f),
                Title = "Solo Dungeon",
            });
        DarzasDominion.Darza.Game.World.Shared = world;

        // THE PLAYER'S HEALTH IS AN INT THAT SHADOWS A DOUBLE, AND THE COST OF THAT IS THE WHOLE
        // REASON THIS ASSERTION EXISTS. `FieldAccessor.Double` on the int used to produce the int's
        // bits reinterpreted -- 2.08E-322 for a health of 150 -- which printed as `0`, passed every
        // `> 0` test, and read as "the client's health is zero" for a whole session.
        world.Player.Health = 150;
        world.Player.MaxHealth = 2000;

        // --- the quest reader's origin guard -------------------------------------------------
        //
        // The first live quest walk set off for the map's top left. The reader now samples both
        // `Position` and `ExactPosition`, refuses a marker that is not a place, and counts it. This
        // fake gives one activatable marker a local (0,0) `Position` and a real `ExactPosition`, and
        // one event marker that is (0,0) in both -- the two halves of that fix.
        CheckQuests(world);

        // THE WORLD LOG RESOLVES THE ENEMY READER ITSELF, AND THIS HARNESS RELIES ON IT.
        //
        // In the hook `EnemyReader` is resolved by `DodgeAdapter.Resolve`, which returns at its first
        // line when `DRELAY_DODGE` is unset -- so a collection session with the dodge off used to
        // write a well-formed file with no enemy rows. `DRELAY_DODGE` is deliberately not set here,
        // so the reader has to come from `WorldLog.Resolve`, which is the only module in this run
        // that could provide it. The enemy-row assertions below are what proves it did.
        Check("WorldLog resolves against the fake game",
              WorldLog.Resolve(typeof(DarzasDominion.Darza.Game.World).Assembly, dir),
              WorldLog.UnresolvedReason);

        Check("the world log resolved the enemy reader for itself, with no dodge in the run",
              EnemyReader.Available,
              "EnemyReader was not available after WorldLog.Resolve: " + EnemyReader.Status());

        // THE MECHANISM UNDERNEATH EVERY COLUMN, CHECKED BEFORE THE WRITER USES IT.
        //
        // `FieldAccessor` emits IL and the JIT takes the return type at its word, so a field read
        // through the wrong accessor is not an error at run time: it converts, or it reinterprets
        // the bits. `Player.Health` is an `int` shadowing a `double`, and the second case is what
        // turned every live `hp` into 2.08E-322. These three assertions are the reason that cannot
        // come back quietly, and they are here rather than in the hook because this is the process
        // that already compiles the real accessor.
        CheckAccessorTypes();

        // --- drive it ---------------------------------------------------------------------
        string path = Path.Combine(dir, "worlds.jsonl");
        var playerObject = world.Player;
        float clock = 0.0f;
        for (int frame = 0; frame < 200; frame++)
        {
            // A FRAME WITH NO PLAYER, WHICH IS WHAT EVERY WORLD CHANGE LOOKS LIKE FROM INSIDE.
            // The client tears the player down and rebuilds it, so a sample either side of the change
            // reads (0,0). Frame 120 is a sampled frame; frame 121 restores the same object. The log
            // must say `ok:false` there, and the frame after it must NOT report a speed measured
            // against the placeholder -- the 2026-10-18 file has one such row and it says 1917.
            if (frame == 121)
            {
                world.Player = playerObject;
            }
            world.Player.Position = new Microsoft.Xna.Framework.Vector3(
                100.0f + (frame * 0.05f), 100.0f, 0.0f);
            if (frame == 60)
            {
                // A WORLD CHANGE: a new MapInfo object, a new name, and `SafeWorld` off. All four
                // signals are exercised here except the grid's dimensions, which change at frame 90.
                world.MapInfo = new DarzaGameNet.Packets.Game.GameServer.GmMapInfo
                {
                    Name = "Plains of Peril",
                    AssetName = "realm1",
                    Width = 140,
                    Height = 96,
                    PlayerId = 7,
                    Difficulty = 5,
                    IsSoloRun = true,
                    Levels = "20-40",
                };
                world.SafeWorld = false;
            }
            if (frame == 90)
            {
                world.hehe_EB2C_E5A5_EE82_E9C5_EF68 = new byte[160, 96];
            }
            // A FRAME WITH NO PLAYER, WHICH IS WHAT EVERY WORLD CHANGE LOOKS LIKE FROM INSIDE.
            // Frame 120 is a sampled frame; frame 121 restores the same object (above, before the
            // position is written). The log must say `ok:false` there, and the frame after it must
            // NOT report a speed measured against the placeholder -- the 2026-10-18 file has one
            // such row and it says 1917 on a character that walks at 9.3.
            if (frame == 120)
            {
                world.Player = null;
            }
            clock += 16.667f;
            WorldLog.OnFrame(frame, clock);
        }
        WorldLog.Close();

        // --- parse every line --------------------------------------------------------------
        string[] lines = File.ReadAllLines(path);
        Check("the log wrote lines", lines.Length > 3, "got " + lines.Length + " line(s)");
        int frames = 0;
        int transitions = 0;
        bool sawEscapedTitle = false;
        bool sawEnemy = false;
        bool sawGrid = false;
        bool sawNoPlayer = false;
        bool sawOthers = false;
        float speedAfterNoPlayer = -1.0f;
        Json header = null;
        foreach (string line in lines)
        {
            if (line.Length == 0)
            {
                continue;
            }
            Json record;
            try
            {
                record = Json.Parse(line);
            }
            catch (Exception ex)
            {
                Fail("line is not JSON: " + ex.Message + " -- " + line);
                continue;
            }
            string kind = record["t"].AsString();
            if (kind == "H")
            {
                header = record;
            }
            else if (kind == "F")
            {
                frames++;
                if (record["enemies"]["near"].Items.Count > 0)
                {
                    sawEnemy = true;
                    // NEAREST FIRST: the enemy at (100,100.5) must precede the one at (130,140).
                    var first = record["enemies"]["near"].Items[0];
                    Check("the enemy rows are the nearest first",
                          first["id"].AsInt() == 12 || first["id"].AsInt() == 14,
                          "first id " + first["id"].AsInt());
                    Check("the enemy count is the whole population and not the sample",
                          record["enemies"]["n"].AsInt() >= 4,
                          "n=" + record["enemies"]["n"].AsInt());
                    Check("enemy health is read",
                          first["maxhp"].AsFloat() > 0.0f,
                          "maxhp=" + first["maxhp"].AsFloat());
                }
                if (record["grid"]["gw"].AsInt() == 140 || record["grid"]["gw"].AsInt() == 160)
                {
                    sawGrid = true;
                }
                // The frame with no player must SAY SO, and the frame after it must not measure a
                // speed from the placeholder position it left behind.
                if (record["f"].AsInt() == 120)
                {
                    sawNoPlayer = !record["player"]["ok"].AsBool(true);
                }
                if (record["f"].AsInt() == 150)
                {
                    speedAfterNoPlayer = record["player"]["spd"].AsFloat(-1.0f);
                }
                var portals = record["portals"].Items;
                if (portals.Count == 1)
                {
                    string title = portals[0]["title"].AsString();
                    if (title != null && title.IndexOf('"') >= 0 && title.IndexOf('\n') >= 0)
                    {
                        sawEscapedTitle = true;
                    }
                    Check("the portal's private `locked` field was found",
                          portals[0]["locked"].AsBool(),
                          "locked=" + portals[0]["locked"].AsBool());
                    Check("the portal is a real row", portals[0]["id"].AsInt() == 501,
                          "id=" + portals[0]["id"].AsInt());

                    // THE THREE COLUMNS THE 2026-10-18 SESSION WROTE AS ZERO OR NULL ON EVERY ROW.
                    // `x`/`y` come from `Node.Position` (a Vector3 -- there is no `X` on a portal),
                    // `title` from `ActivatableObject.Title` (a FIELD on a base type, not a property
                    // on `Portal`), `tag` from `GameObject.Tag` (a field two levels up). Each one is
                    // asserted for its VALUE, because a zero passes every "is it present" test.
                    Check("the portal's position came from Node.Position",
                          Math.Abs(portals[0]["x"].AsDouble() - 101.5) < 1e-3
                          && Math.Abs(portals[0]["y"].AsDouble() - 103.25) < 1e-3,
                          "x=" + portals[0]["x"].AsDouble() + " y=" + portals[0]["y"].AsDouble());
                    Check("the portal's title was read from a field on a base type",
                          title == "Realm \"of\" Doom\\Night\nline two",
                          "title=" + (title ?? "(null)"));
                    Check("the portal's tag was read from GameObject.Tag",
                          portals[0]["tag"].AsString() == "realm:hard",
                          "tag=" + portals[0]["tag"].AsString());
                }

                // THE PLAYER'S HEALTH IS AN `int` SHADOWING A `double`, so this asserts the VALUE
                // and not positivity: a mistyped read is 2.08E-322, which is greater than zero.
                if (record["player"]["x"].AsDouble() > 0.0)
                {
                    Check("the player's health is the int, not its bits reinterpreted",
                          Math.Abs(record["player"]["hp"].AsDouble() - 150.0) < 1e-9,
                          "hp=" + record["player"]["hp"].AsDouble());
                    Check("the player's max health is the int, not its bits reinterpreted",
                          Math.Abs(record["player"]["maxhp"].AsDouble() - 2000.0) < 1e-9,
                          "maxhp=" + record["player"]["maxhp"].AsDouble());
                }
                Check("the map block carries the name", record["map"]["name"].AsString() != null,
                      "name=" + record["map"]["name"].AsString());

                // THE NON-PORTALS ARE NAMED BY TYPE, WITH THE NEAREST INSTANCE. A count of "other
                // activatables" is what left the re-run unable to say which object in the lobby
                // leads to the realm.
                var others = record["others"].Items;
                if (others.Count > 0)
                {
                    bool sawStatue = false;
                    foreach (Json other in others)
                    {
                        if (other["type"].AsString() != "SoloDungeonStatue")
                        {
                            continue;
                        }
                        sawStatue = true;
                        sawOthers = true;
                        Check("a non-Portal activatable is named by its runtime type",
                              other["n"].AsInt() == 1, "n=" + other["n"].AsInt());
                        Check("the non-Portal row carries the object's own title",
                              other["title"].AsString() == "Solo Dungeon",
                              "title=" + other["title"].AsString());
                        Check("the non-Portal row carries the object's position",
                              Math.Abs(other["x"].AsDouble() - 104.0) < 1e-3
                              && Math.Abs(other["y"].AsDouble() - 102.0) < 1e-3,
                              "x=" + other["x"].AsDouble() + " y=" + other["y"].AsDouble());
                    }
                    Check("the activation statue is in the others block", sawStatue,
                          "SoloDungeonStatue was not named");
                }
            }
            else if (kind == "T")
            {
                transitions++;
                string signals = record["signals"].AsString();
                Check("a transition names its signal", !string.IsNullOrEmpty(signals)
                      && signals != "(none)", "signals=" + signals);
            }
        }

        Check("no line failed to parse", true, lines.Length + " line(s)");
        Check("the log wrote frame lines", frames > 3, frames + " frame line(s)");
        Check("the world change produced a transition line", transitions >= 2,
              transitions + " transition(s)");
        Check("a portal title with a quote, a backslash and a newline round-tripped",
              sawEscapedTitle, "the escaping was never exercised");
        Check("the enemy block was filled", sawEnemy, "no enemy rows");
        Check("the grid block carries the dimensions", sawGrid, "no grid dimensions seen");
        Check("the non-Portal activatables are named, not just counted", sawOthers,
              "no `others` block with a typed row was written");
        Check("a frame with no player says so instead of writing (0,0)", sawNoPlayer,
              "frame 120 did not report player.ok=false");
        Check("no speed is measured across a world change",
              speedAfterNoPlayer >= 0.0f && speedAfterNoPlayer < 5.0f,
              "frame 150 spd=" + speedAfterNoPlayer);

        // --- the header ---------------------------------------------------------------------
        //
        // THE HEADER IS THE OPERATOR'S ONLY WARNING THAT A SESSION IS UNUSABLE, so the columns the
        // 2026-10-18 run silently zeroed are asserted to be OUT of `missing` and IN `resolved`.
        // A writer that regresses to the old `X`/`_x` lookup fails here, on a machine with no game,
        // instead of after a session.
        Check("the log wrote a header", header != null, "no H line");
        if (header != null)
        {
            var missing = header["missing"].Items;
            var missingNames = new List<string>();
            foreach (Json item in missing)
            {
                missingNames.Add(item.AsString());
            }
            string missingText = string.Join(",", missingNames.ToArray());
            Check("no column is missing against the client's real shapes",
                  missingNames.Count == 0, "missing=[" + missingText + "]");
            Check("the portal geometry is not named as missing",
                  missingText.IndexOf("Portal.X/Y") < 0, "missing=[" + missingText + "]");

            var resolved = header["resolved"];
            Check("the header names the member the portal's position came from",
                  resolved["Portal.X"].AsString() == "Node.Position.X",
                  "Portal.X=" + resolved["Portal.X"].AsString());
            Check("the header names the portal title's declaring type",
                  resolved["Portal.Title"].AsString() == "ActivatableObject.Title",
                  "Portal.Title=" + resolved["Portal.Title"].AsString());
            Check("the header names the portal tag's declaring type",
                  resolved["Portal.Tag"].AsString() == "GameObject.Tag",
                  "Portal.Tag=" + resolved["Portal.Tag"].AsString());
            Check("the header names the health column's field AND width",
                  resolved["Player.Health"].AsString() == "Player.Health (Int32)",
                  "Player.Health=" + resolved["Player.Health"].AsString());
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "WORLDLOG VERIFY: OK" : "WORLDLOG VERIFY: " + _failures + " failure(s)");
        Console.WriteLine("file: " + path);
        foreach (string line in lines)
        {
            Console.WriteLine("  " + (line.Length > 220 ? line.Substring(0, 220) + "..." : line));
        }
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// The quest reader against the fake's own `World.Quest`/`World.EventQuests`, including the
    /// `Position`-is-(0,0)-and-`ExactPosition`-is-real case the first live report produced, and the
    /// registry check that refuses a marker from another map.
    /// </summary>
    private static void CheckQuests(DarzasDominion.Darza.Game.World world)
    {
        world.Quest = new DarzasDominion.Darza.Game.Objects.Map.Activatable.SoloDungeonStatue
        {
            GameId = 9001,
            Position = new Microsoft.Xna.Framework.Vector3(0.0f, 0.0f, 0.0f),
            ExactPosition = new Microsoft.Xna.Framework.Vector3(110.0f, 120.0f, 0.0f),
            Title = "Quest Giver",
        };
        world.EventQuests.Add(new DarzasDominion.Darza.Game.Objects.GameObject
        {
            GameId = 9002,
            Position = new Microsoft.Xna.Framework.Vector3(0.0f, 0.0f, 0.0f),
            ExactPosition = new Microsoft.Xna.Framework.Vector3(0.0f, 0.0f, 0.0f),
        });
        // THE STALENESS FIXTURE: a marker with a perfectly good position that is NOT an object in
        // this world. It is what `World.Quest` looks like after a map change -- `ClearWorld` empties
        // `Objects` and leaves `Quest` set -- and walking to it is walking to a coordinate in a map
        // that is not loaded.
        var stale = new DarzasDominion.Darza.Game.Objects.GameObject
        {
            GameId = 9003,
            Position = new Microsoft.Xna.Framework.Vector3(50.0f, 50.0f, 0.0f),
        };
        world.EventQuests.Add(stale);
        // THE TRACKED MARKER AND THE (0,0) EVENT MARKER ARE OBJECTS IN THIS WORLD; 9003 is not.
        world.Objects[9001] = world.Quest;
        world.Objects[9002] = world.EventQuests[0];

        QuestReader.ResolveWorld(typeof(DarzasDominion.Darza.Game.World).GetField("Shared"));
        Check("QuestReader resolves World.Quest and World.EventQuests",
              QuestReader.Resolve(typeof(DarzasDominion.Darza.Game.World).Assembly,
                                  "DarzasDominion.Darza.Game.World"),
              QuestReader.Status());

        var snapshot = new DodgeSnapshot();
        snapshot.Clear();
        snapshot.Player.X = 100.0f;
        snapshot.Player.Y = 100.0f;
        DodgeConfig config = DodgeConfig.Default();
        config.FarmMode = true;
        config.FarmQuest = true;
        QuestReader.Fill(snapshot, config);

        bool one = snapshot.Quests.Count == 1;
        Check("the (0,0) Position falls back to ExactPosition, and a marker with no place is skipped",
              one && snapshot.Quests[0].X == 110.0f && snapshot.Quests[0].Y == 120.0f
              && snapshot.Quests[0].Primary && snapshot.Quests[0].Activatable
              && QuestReader.MarkersSkipped >= 1,
              one
                  ? snapshot.Quests.Count + " marker(s), first at "
                    + snapshot.Quests[0].X + "," + snapshot.Quests[0].Y
                    + ", skipped " + QuestReader.MarkersSkipped
                  : snapshot.Quests.Count + " marker(s), skipped " + QuestReader.MarkersSkipped);

        Check("a marker that is not an object in this world is refused as stale",
              QuestReader.MarkersStale == 1,
              "stale " + QuestReader.MarkersStale + " (the event marker at 9002 is not in Objects)");

        if (one)
        {
            string why;
            Check("an activatable quest marker is entered through Activate(0)",
                  QuestReader.TryActivate(snapshot.Quests[0].Object, out why), why);
        }

        // --- THE REGISTRY SCAN: a quest the client flags on the OBJECT, not on `World.Quest` ----
        //
        // THE OPERATOR'S 22:36 SESSION: a quest indicator on screen for `Athena, Queen of
        // Arachnids`, `World.Quest` null, `EventQuests` empty, and a farmer that spent the session
        // on roam waypoints. The client's other statement about a quest object is its data:
        // `EntityData.Quest` / `EntityData.EventQuest`. This is that object -- flagged, in
        // `Objects`, and in neither of the two tracked collections.
        var athena = new DarzasDominion.Darza.Game.Objects.Entities.Enemy
        {
            GameId = 9100,
            Position = new Microsoft.Xna.Framework.Vector3(320.0f, 240.0f, 0.0f),
            Radius = 1.0f,
            Data = new DarzaCore.Data.Entities.EntityData
            {
                Name = "Athena, Queen of Arachnids",
                Class = "Enemy",
                Quest = true,
            },
        };
        world.Objects[9100] = athena;
        var scanned = new DodgeSnapshot();
        // `QuestScanFrames` CALLS, NOT ONE: the registry walk is every tenth frame (see
        // `ScanRegistry`), which is the cost contract, so a test that scanned once would only prove
        // that the cache was not updated early.
        for (int i = 0; i < 10; i++)
        {
            scanned.Clear();
            scanned.Player.X = 100.0f;
            scanned.Player.Y = 100.0f;
            QuestReader.Fill(scanned, config);
        }

        bool found = false;
        for (int i = 0; i < scanned.Quests.Count; i++)
        {
            if (scanned.Quests[i].GameId == 9100
                && scanned.Quests[i].Name == "Athena, Queen of Arachnids"
                && scanned.Quests[i].X == 320.0f && scanned.Quests[i].Y == 240.0f)
            {
                found = true;
            }
        }
        Check("a quest the client flags in the object's DATA is found by the registry scan",
              found && QuestReader.DataMarkers >= 1,
              "scanned " + QuestReader.ScannedObjects + " object(s), " + QuestReader.DataMarkers
              + " data-flagged marker(s); " + scanned.Quests.Count
              + " marker(s) published, and the flagged one is the `Athena, Queen of Arachnids` at"
              + " (320,240) in `World.Objects` with `EntityData.Quest = true` -- in neither"
              + " `World.Quest` nor `World.EventQuests`, which is the state the 2026-10-19 session"
              + " was in.");
    }

    /// <summary>
    /// The three shapes an emitted accessor can be asked to read, and the one it must refuse.
    ///
    /// THE SECOND CASE IS THE ONE THAT SHIPPED BROKEN AND THE THIRD IS THE GUARD THAT STOPS IT
    /// HAPPENING AGAIN. `Player.Health` is `public new int Health` shadowing `Entity`'s `double`,
    /// so a hierarchy walk finds the int -- and before `FieldAccessor` converted, an int read
    /// through a `double`-returning delegate produced 2.08E-322, a positive denormal that prints as
    /// `0` and passes every `> 0` test. It is `SteeringPilot`'s liveness gate and `DodgeSolver`'s
    /// lethal-damage threshold; see `FieldAccessor`'s header for the receipts.
    /// </summary>
    private static void CheckAccessorTypes()
    {
        const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

        System.Reflection.FieldInfo intHealth =
            typeof(DarzasDominion.Darza.Game.Objects.Entities.Player).GetField("Health", Any);
        System.Reflection.FieldInfo doubleHealth =
            typeof(DarzasDominion.Darza.Game.Objects.Entities.Enemy).GetField("Health", Any);
        System.Reflection.FieldInfo title =
            typeof(DarzasDominion.Darza.Game.Objects.Map.Activatable.ActivatableObject)
                .GetField("Title", Any);

        Check("the fake game's Player.Health is the int that shadows Entity's double",
              intHealth != null && intHealth.FieldType == typeof(int)
              && intHealth.DeclaringType == typeof(DarzasDominion.Darza.Game.Objects.Entities.Player),
              intHealth == null ? "not found" : intHealth.DeclaringType.Name + "." + intHealth.Name
                                  + " is " + intHealth.FieldType.Name);

        var player = new DarzasDominion.Darza.Game.Objects.Entities.Player { Health = 150 };
        Check("a double accessor CONVERTS an int field rather than reinterpreting it",
              Math.Abs(FieldAccessor.Double(intHealth)(player) - 150.0) < 1e-9,
              "read " + FieldAccessor.Double(intHealth)(player));
        Check("a float accessor converts a double field",
              Math.Abs(FieldAccessor.Float(doubleHealth)(
                           new DarzasDominion.Darza.Game.Objects.Entities.Enemy { Health = 400.0 })
                       - 400.0f) < 1e-3,
              "double field read as float");

        // A WRONG ACCESSOR IS A NAMED FAILURE ON THE RESOLVE CALLBACK, NOT A DENORMAL ON THE FRAME
        // PATH. If this stops throwing, the next type change in the client is silent again.
        bool threw = false;
        try
        {
            FieldAccessor.Double(title);
        }
        catch (ArgumentException)
        {
            threw = true;
        }
        Check("a numeric accessor refuses a non-numeric field, by name", threw,
              "FieldAccessor.Double accepted a System.String field");
    }

    private static void Check(string what, bool ok, string detail)
    {
        if (ok)
        {
            Console.WriteLine("  ok    " + what);
            return;
        }
        Fail(what + " -- " + detail);
    }

    private static void Fail(string what)
    {
        _failures++;
        Console.WriteLine("  FAIL  " + what);
    }
}
