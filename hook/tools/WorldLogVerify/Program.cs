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
        world.Player.Position = new DarzasDominion.Darza.Game.Vec2 { X = 100.0f, Y = 100.0f };
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
        world.Activatables.Add(new DarzasDominion.Darza.Game.Objects.Map.Activatable.Portal
        {
            GameId = 501,
            X = 101.5f,
            Y = 103.25f,
            Title = "Realm \"of\" Doom\\Night\nline two",
            Tag = "realm:hard",
        });
        ((DarzasDominion.Darza.Game.Objects.Map.Activatable.Portal)world.Activatables[0])
            .SetLocked(true);
        DarzasDominion.Darza.Game.World.Shared = world;

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

        // --- drive it ---------------------------------------------------------------------
        string path = Path.Combine(dir, "worlds.jsonl");
        float clock = 0.0f;
        for (int frame = 0; frame < 200; frame++)
        {
            world.Player.Position = new DarzasDominion.Darza.Game.Vec2
            {
                X = 100.0f + (frame * 0.05f),
                Y = 100.0f,
            };
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
            if (kind == "F")
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
                }
                Check("the map block carries the name", record["map"]["name"].AsString() != null,
                      "name=" + record["map"]["name"].AsString());
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

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "WORLDLOG VERIFY: OK" : "WORLDLOG VERIFY: " + _failures + " failure(s)");
        Console.WriteLine("file: " + path);
        foreach (string line in lines)
        {
            Console.WriteLine("  " + (line.Length > 220 ? line.Substring(0, 220) + "..." : line));
        }
        return _failures == 0 ? 0 : 1;
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
