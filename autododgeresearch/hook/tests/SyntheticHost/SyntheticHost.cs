// SyntheticHost.cs -- a stand-in for the game's apphost, for testing injection
// without touching the game.
//
// WHY THIS EXISTS
// ---------------
// Most of Phase 1 can only be exercised against a real process: the launcher
// suspends one, injects a DLL, the DLL waits for a startup hook, the startup hook
// finds the runtime, the native side validates it and either installs a detour or
// refuses.  Running that against the game means an operator at the keyboard and a
// game that has to start correctly each time -- a slow and fragile way to find a
// bug in the *plumbing*.
//
// So this program is the smallest process that has the same relevant properties
// as the game: a self-contained .NET apphost with `TieredCompilation` off, which
// runs `Main` and does nothing else.  Injection into it exercises:
//
//   * `CreateProcess(CREATE_SUSPENDED)` and the controlled environment block;
//   * the remote stub that loads the hook and calls its initialiser;
//   * the bootstrap running from DOTNET_STARTUP_HOOKS before this `Main`;
//   * the handshake, in both directions, including the ordering guarantee;
//   * the native side's assembly-identity check, and the *refusal* path when the
//     fingerprint does not match -- which is the path a synthetic host is
//     guaranteed to take, because it is not the game;
//   * the self-check file and the evidence files.
//
// What it deliberately does NOT exercise: the JIT detour's target matching, and
// the frame-tick patch.  Those need the game's own assembly, because matching is
// on the game's IL hashes.  A test that pretended otherwise would be inventing
// evidence.
//
// The two lines it prints are the observable ordering evidence: the bootstrap's
// line must be written before this one.
//
// A STAND-IN WORLD, ADDED SO THAT THE ARMING GATE CAN BE TESTED WITHOUT THE GAME.
//
// This host deliberately has no game assembly, so it cannot exercise IL-hash
// target matching or the frame-tick patch -- those need the real IL.  What it CAN
// exercise is everything the arming gate needs, because the gate's question is
// about *types*: "is `World.Shared` non-null, and does the pointer chain built
// from the measured offsets reproduce what reflection says?"
//
// The bootstrap resolves those types by name out of whatever assembly the
// launcher names, so a host that declares the same names with the same shape is
// enough to drive the whole path: the offset measurement, the probe, the
// cross-check, the verdict published into the shared block, and the self-check
// that reports it.  The shape has to match what the hook reads -- `World` with a
// *static* `Shared` and an instance `Player`, `Player` with an inline `GamePoint
// Position`, and `GamePoint` with two floats -- because that is what the pointer
// chain assumes.
//
// The world is created LATE and on another thread, which is the property under
// test: at attach there is no world, exactly as in the game, so a gate that armed
// at attach would be caught by `DRELAY_TEST_WORLD_DELAY_MS`.  A control
// (`DRELAY_TEST_NO_WORLD=1`) leaves it null forever, so "the cross-check passed"
// and "the cross-check never ran" can never be confused for one another.

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using DarzaCore.Geometry;
using DarzasDominion.Darza.Game;
using DarzasDominion.Darza.Game.Objects;
using DarzasDominion.Darza.Game.Objects.Entities;

namespace DarzasDominion.Darza.Game
{
    /// <summary>
    /// The shape <c>DarzasDominion.Darza.Game.World</c> has in the game, as far as
    /// the hook's read path is concerned: a static <c>Shared</c> reference and an
    /// instance <c>Player</c> reference.
    /// </summary>
    public sealed class World
    {
        public static World Shared;
        // Fully qualified because the namespace this type is in does not see the
        // one `Player` lives in, and the name has to be exactly what the bootstrap
        // looks up -- `DarzasDominion.Darza.Game.Objects.Entities.Player`.
        public DarzasDominion.Darza.Game.Objects.Entities.Player Player;

        /// <summary>
        /// THE DICTIONARY, AND IT IS A DICTIONARY BECAUSE THE GAME'S IS -- BUT IT IS NOT
        /// WHERE THE BULLETS ARE.
        ///
        /// This host used to put its projectiles here, on CLIENT-MAP.md section 5.2's claim
        /// that `World.AddObject` files a projectile in `Objects`.  The decompiled client
        /// says otherwise: `Entity.cs:1521,1550,1589` fires one with `Projectile.Rent` ->
        /// `World.GameObjectLayer.AddChild` -> `projectile.World = World`, and the only
        /// method that writes `Objects[obj.GameId]` is never called with a projectile.  The
        /// 2026-10-12 evening session is the measurement: a real zone, 54 600 frames, real
        /// bullets on screen, and a sensor reading this dictionary reported `live=0` for
        /// every one of them.
        ///
        /// SO THE HOST NOW HOLDS ONLY NON-PROJECTILES HERE, WHICH IS THE CLIENT'S SHAPE.
        /// Keeping a tidy `Projectile[]` would repeat the original mistake from the other
        /// direction; this way a sensor that reads only this dictionary finds nothing, in
        /// the fixture exactly as it does in the game, and the suite fails rather than
        /// passing on a shape the game does not have.
        /// </summary>
        public System.Collections.Generic.Dictionary<int, DarzasDominion.Darza.Game.Objects.GameObject> Objects;

        /// <summary>
        /// THE PROJECTILE SET, BECAUSE IT IS THE CLIENT'S PROJECTILE SET.  Every projectile
        /// the host spawns is added here through `AddChild`, exactly as
        /// `World.GameObjectLayer.AddChild(projectile)` does in the game, and the two
        /// static objects are here as well because the client's map objects are in both
        /// containers.
        /// </summary>
        public DarzasDominion.Darza.Game.Objects.GameObjectLayer GameObjectLayer;
    }
}

namespace DarzasDominion.Darza.Game.Objects.Entities
{
    /// <summary>
    /// The shape of the game's <c>Player</c>: one inline value-type field.  Nothing
    /// here is a game mechanic; it exists so that the offsets the bootstrap
    /// measures have something live to be checked against.
    /// </summary>
    public sealed class Player
    {
        public DarzaCore.Geometry.GamePoint Position;
    }
}

// `Projectile` IS IN `...Game.Objects`, NOT `...Game.Objects.Entities`, AND THE NAMESPACE
// IS PART OF THE FIXTURE RATHER THAN A STYLE CHOICE.  `known-build.json` pins the game's
// four targets by declaring type, and the collision and spawn targets are
// `DarzasDominion.Darza.Game.Objects.Projectile`.  The bootstrap looks types up by those
// names, so a host that put its stand-in in a tidier namespace would test a hook that
// cannot find the real one -- which is exactly what the first version of this did:
// `types not found: World=True Projectile=False`.
// THE NODE THE WORLD'S LAYERS HANG OFF, AT THE SHAPE THE HOOK RESOLVES.
//
// THIS FIXTURE USED TO HAVE NO `Node` AND NO LAYER AT ALL, AND THAT IS EXACTLY WHY IT
// MISSED THE BUG THE 2026-10-12 EVENING SESSION FOUND.  The client's shape is
// `Projectile : GameObject : DarzasDominion.Nodes.Node`; the world's live projectiles hang
// off `World.GameObjectLayer` (`GameObjectLayer : Node`, children in `Node._children`);
// and `World.Objects` holds only the map's static objects and entities, because a
// projectile is added with `GameObjectLayer.AddChild` and never through the registration
// method that writes `Objects[obj.GameId]`.  A fixture that put its projectiles in
// `World.Objects` -- which this one did, in good faith, on CLIENT-MAP.md section 5.2's
// claim -- tested a world the game does not have: all ninety checks passed while the hook
// read zero bullets in a real zone for fifteen minutes.
namespace DarzasDominion.Nodes
{
    /// <summary>
    /// The child list every drawable hangs off.  `_children` is the field and `Children`
    /// is the property over it, because the hook looks the FIELD up by name -- a fixture
    /// offering only the property would fail a lookup the client passes, which is the
    /// mirror image of the mistake this file is being fixed for.
    /// </summary>
    public class Node
    {
        protected System.Collections.Generic.List<Node> _children =
            new System.Collections.Generic.List<Node>();

        public System.Collections.Generic.List<Node> Children => _children;

        public virtual void AddChild(Node child)
        {
            _children.Add(child);
        }

        public virtual void RemoveChild(Node child)
        {
            _children.Remove(child);
        }
    }
}

namespace DarzasDominion.Darza.Game.Objects
{
    /// <summary>
    /// Anything that lives in the world's object graph.  The client's chain is
    /// `Projectile : GameObject : Node`, and the dictionary and the drawing layer share
    /// occupants, so a sensor has to filter by type in both -- and a fixture whose
    /// collections held only projectiles could never catch a filter that does not filter.
    /// </summary>
    public class GameObject : DarzasDominion.Nodes.Node
    {
        public int ObjectId;
    }

    /// <summary>
    /// The shape of the game's `Projectile`, as far as the sensor's read path is
    /// concerned: an identity to key records on, a live position, and a radius.
    ///
    /// `GameId` and `StartTime` together are the key (DODGE-ALGORITHM.md section 3.2),
    /// and the host changes both when it re-rents an instance, so a sensor that keyed on
    /// the object's address instead would inherit a dead bullet's history -- the pooling
    /// hazard, made reproducible.
    /// </summary>
    public sealed class Projectile : GameObject
    {
        public int GameId;
        public int StartTime;
        public DarzaCore.Geometry.GamePoint Position;
        public float Radius;

        /// <summary>The trajectory family.  `GetPosition` is called on this object, not reimplemented.</summary>
        public DarzaCore.Data.ProjDesc Desc;

        /// <summary>
        /// The client's own two exclusion flags, modelled because a filter nothing can fail is a
        /// filter nothing tests.
        ///
        /// `Projectile.Hit()` is `if (HitsPlayer) return HitCharacter(); return HitEnemy();`
        /// (`Projectile.cs:1505`), and the client's spawn path sets `HitsPlayer` to
        /// `this is Enemy` (`Entity.cs:1600`) -- so a projectile fired by the local player can never
        /// reach the player-collision code. `LocalSeedDerived` (`:115`) is the client's second,
        /// independent statement of the same thing: true only for projectiles the local player
        /// fired. The host sets one projectile in three to be a local shot, born on the player and
        /// aimed away, so a threat set that includes them is visible here as well as live.
        /// </summary>
        public bool HitsPlayer = true;

        /// <summary>See <see cref="HitsPlayer"/>.</summary>
        public bool LocalSeedDerived;

        /// <summary>The direction the game stores, which the sampler passes straight through.</summary>
        public float Sin;
        public float Cos;

        /// <summary>Milliseconds since this instance's spawn; the sampler's time base.</summary>
        public int AgeMs;

        /// <summary>Where this trajectory started, so the model is a function of absolute position.</summary>
        public float SpawnOriginX;
        public float SpawnOriginY;

        /// <summary>
        /// Where the local player is, for this tick.  The client's predicate reads
        /// `World.Player.Position` itself; the stand-in cannot name that type's instance, so
        /// the driver hands it the position instead -- the same input, moved one step.
        /// </summary>
        public DarzaCore.Geometry.GamePoint Target;

        /// <summary>
        /// THE GAME'S OWN COLLISION PREDICATE, at the shape the hook patches:
        /// `private bool HitCharacter()` in the real client, called from `Hit()` when
        /// `HitsPlayer` is set, once per projectile per frame.
        ///
        /// IT RETURNS AN ANSWER, AND THE ANSWER IS THE DECISION.  This fixture used to
        /// return a bare `true` and to be called only on the tick the driver had already
        /// decided the bullet was touching the player -- which made the hook's count of
        /// "collision decisions" agree with the host's by construction, and hid a real
        /// defect: the hook observed EVERY entry to the method, and in the client that is
        /// every evaluation, not every hit (the 2026-10-12 20:42 session: 418 328 entries
        /// for 48 resolved hits).  So the method now computes the answer the way the client
        /// does, and the driver calls it for every live projectile every tick, exactly as
        /// `Projectile.Update` does.  A hook that counts evaluations is now caught here.
        ///
        /// NOINLINING IS LOAD-BEARING, AND IT IS THE FIRST THING THIS FIXTURE TAUGHT.
        /// A tiny method with a `return true` is exactly what the JIT loves to inline, and
        /// the first run with this method reported `collision MISSING -- never compiled in
        /// the discovery window` while the host was calling it fifteen times.  A method that
        /// was inlined has no entry point of its own, so the JIT detour never sees it and
        /// there is nothing to patch.  `CLIENT-FINDINGS.md` section 3 lists this as a
        /// low-but-real risk for the real `HitCharacter` ("if inlining is observed, fall
        /// back to hooking `Hit()`"), and the synthetic host reproduces it rather than
        /// hiding it.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool HitCharacter()
        {
            HitCharacterCalls = HitCharacterCalls + 1;
            float dx = Position.X - Target.X;
            float dy = Position.Y - Target.Y;
            bool decided = ((dx * dx) + (dy * dy)) <= (Radius * Radius);
            if (decided)
            {
                HitCharacterDecisions = HitCharacterDecisions + 1;
            }
            return decided;
        }

        /// <summary>Every entry to the predicate this run -- the evaluations, not the hits.</summary>
        public static long HitCharacterCalls;

        /// <summary>The entries that answered `true` -- the game's actual collision decisions.</summary>
        public static long HitCharacterDecisions;
    }

    /// <summary>
    /// An occupant that is NOT a projectile.  The client puts its map's static objects in
    /// `World.Objects` AND in the drawing layer (they are registered and added as children
    /// at `World.cs:5496`), so the host puts its two here in both as well -- one witness
    /// for "the dictionary filter filters" and one for "the layer walk filters".
    /// </summary>
    public sealed class StaticObject : GameObject
    {
        public DarzaCore.Geometry.GamePoint Position;
    }

    /// <summary>
    /// The container the client actually keeps its live projectiles in.
    ///
    /// `AddChild` AND `RemoveChild` ARE OVERRIDDEN AND CALL `base`, WHICH IS WHAT THE
    /// CLIENT DOES: the real layer keeps extra split lists for drawing, and its override
    /// reaches `Node._children` through `base.AddChild`.  A host that put its children in a
    /// private list of its own would test a hook that reads something the client does not
    /// have.
    /// </summary>
    public class GameObjectLayer : DarzasDominion.Nodes.Node
    {
        public override void AddChild(DarzasDominion.Nodes.Node child)
        {
            base.AddChild(child);
        }

        public override void RemoveChild(DarzasDominion.Nodes.Node child)
        {
            base.RemoveChild(child);
        }
    }
}

// THE TRAJECTORY FAMILIES, AT THE NAME AND SHAPE THE SENSOR LOOKS FOR.
//
// DODGE-ALGORITHM.md section 4.1 is emphatic that the hook CALLS this rather than
// reimplementing it, and the third of its reasons is the decisive one: calling it makes
// the prediction exactly consistent with the collision by construction.  That only holds
// if the sampler is calling the real family, so the fixture needs more than one family --
// a host with a single straight desc could never show that the linearity gate
// distinguishes anything, and the fraction of real bullets that are straight is one of
// the numbers Phase 2 exists to produce.
namespace DarzaCore.Data
{
    /// <summary>
    /// The game's `ProjDesc`: a virtual `GetPosition` with one override per family
    /// (straight, wavy, parametric, boomerang, orbiting, curving, bouncing).
    /// </summary>
    public abstract class ProjDesc
    {
        /// <summary>Milliseconds, in the same units `time` is.</summary>
        public int Lifetime = 2000;

        public abstract void GetPosition(int time, float sin, float cos,
                                         out float x, out float y, int gameId);
    }

    /// <summary>`x = sin*v*t`, `y = cos*v*t` -- the family the gate may project.</summary>
    public sealed class StraightDesc : ProjDesc
    {
        public float Speed = 120.0f;

        public override void GetPosition(int time, float sin, float cos,
                                         out float x, out float y, int gameId)
        {
            float t = time * 0.001f;
            x = sin * Speed * t;
            y = cos * Speed * t;
        }
    }

    /// <summary>
    /// The same motion with a weave on top, which is the family the gate must refuse to
    /// project.  The period is chosen well under the sampler's 30 ms step so the weave
    /// cannot be reconstructed as its chord -- that is the aliasing failure
    /// DODGE-ALGORITHM.md section 4.2 describes, and a fixture that aliased cleanly would
    /// make the gate look right for the wrong reason.
    /// </summary>
    public sealed class WavyDesc : ProjDesc
    {
        public float Speed = 120.0f;
        public float WeaveAmplitude = 25.0f;
        public float WeavePeriodMs = 220.0f;

        public override void GetPosition(int time, float sin, float cos,
                                         out float x, out float y, int gameId)
        {
            float t = time * 0.001f;
            float weave = WeaveAmplitude * (float)Math.Sin((2.0 * Math.PI * time) / WeavePeriodMs);
            x = (sin * Speed * t) + (cos * weave);
            y = (cos * Speed * t) - (sin * weave);
        }
    }
}

namespace DarzaCore.Geometry
{
    /// <summary>
    /// A value type with two floats, which is what <c>Player.Position</c> is: the
    /// hook reads it as two consecutive `float`s at the field's offset, so the
    /// layout has to be sequential and nothing may precede them.
    /// </summary>
    public struct GamePoint
    {
        public float X;
        public float Y;
    }
}

// A TARGET THE HOOK CAN ACTUALLY MATCH, WHICH THIS HOST HAS NEVER HAD.
//
// Until now every run of Test-Injection.ps1 exercised the hook with a target
// table that could only ever describe the *game*: `PublishTargets()` publishes
// GeneratedBuildTable.Targets and nothing else, so no method of this host has an
// IL hash the JIT detour can match, `found` stays 0 on every row, and
// `DrelayPatchTargets` skips them all.  That is why run 5's honest verdict is
// `GAP 9c the read path is open but no frame has entered the detour yet`: the
// arming gate can be tested here, the patch cannot.
//
// `Cadence.FrameTick` closes that gap.  It is deliberately:
//
//   * an INSTANCE method taking one `float`, which is `DarzaWindow.Update`'s
//     shape (`void Update(float)`) and therefore `FrameTickDetour`'s
//     `void (__cdecl *)(void* self, float total)` -- so the existing detour body
//     and the existing native dispatch by name are exercised exactly as they are
//     against the game, with no test-only code path;
//   * `NoInlining`, because an inlined method has no entry point of its own and
//     the JIT detour would never see it compile.  That is the whole measurement;
//   * called on a fixed cadence from `CadenceRunner.Run`, so it is JITted on the
//     first iteration -- after the detour is installed -- and then entered
//     thousands of times, exactly as a frame tick is.
//
// The identity the test hands the hook is the *metadata* identity: declaring
// type `SyntheticHostFixture.Cadence`, name `FrameTick`, arity 1, and the
// SHA-256 of the method's IL bytes.  `tools/il_method_hash.py` computes it with
// `il_index.py`'s own reader and hash function, so the recipe is not restated
// anywhere and cannot drift from the game's.
namespace SyntheticHostFixture
{
    /// <summary>
    /// The stand-in frame tick: the method `Test-Injection.ps1` run 7 matches by
    /// IL hash and patches.
    /// </summary>
    public sealed class Cadence
    {
        /// <summary>How many times the body has run -- the detour must not eat these.</summary>
        public long Calls;

        /// <summary>The last argument seen, so a broken ABI is visible as a value.</summary>
        public float LastTotal;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void FrameTick(float total)
        {
            Calls = Calls + 1;
            LastTotal = total;
        }
    }

    /// <summary>
    /// An object the cadence loop holds and reads every iteration.  It is
    /// reachable from no static, no field and no other frame, which is what makes
    /// it a witness: if the runtime's stack walk stops at the hook's own native
    /// return address -- the hypothesis in PHASE2-SCOPE.md section 3 -- this
    /// frame is never scanned, the object is collected while still in use, and
    /// its marker changes.  Nothing collects the loop's own working set for us,
    /// so the host's allocator thread (§ below) is what forces the collections.
    /// </summary>
    public sealed class Witness
    {
        public int Marker;
    }

    /// <summary>
    /// Keeps the world's projectile set moving: spawns, retires, and occasionally re-rents
    /// an instance the way a pool does.
    ///
    /// THE SET IT MOVES IS `World.GameObjectLayer.Children`, NOT `World.Objects`, because
    /// that is where the client's bullets are (see the `World` type's own note).  The
    /// spawning, retiring and re-renting are unchanged in substance; only the container
    /// moved, and it moved because the fixture was modelling a world the game does not
    /// have.
    ///
    /// WHY THE SET HAS TO CHANGE.  A sensor's whole job is to keep up with a set that
    /// changes, and the two rules that are easy to get wrong only exist because it
    /// changes: a bullet that stops being observed must be retired by evidence rather
    /// than by quota, and a re-rented instance must not inherit the dead bullet's
    /// record.  A fixture whose object set were constant would let both be wrong and
    /// still look right -- and its own counters are what the hook's numbers are checked
    /// against, so they are the evidence rather than a convenience.
    /// </summary>
    public static class ObjectSetDriver
    {
        public static long Spawned;
        public static long Retired;
        public static long Reused;

        /// <summary>
        /// Projectiles currently live in the world's drawing layer -- which is where the
        /// client keeps them, and therefore where this counter is taken from.
        /// </summary>
        public static int LiveProjectiles;

        /// <summary>Non-projectile occupants, which a correct filter must skip.</summary>
        public static int LiveOthers;

        /// <summary>
        /// Nodes in the layer, bullets or not.  The host's own witness for how big the
        /// walk the sensor now does actually is -- `layer_nodes=` on the sensor line is
        /// checked against this, and the walk's cost is CLIENT-MAP.md section 10.6's
        /// question rather than a detail.
        /// </summary>
        public static int LayerNodes;

        /// <summary>
        /// Projectiles left in `World.Objects`: zero, on purpose, because the client has
        /// zero.  Published to the cadence file so the suite can assert the sensor's
        /// `objects_live=0` against the host's own answer rather than against a comment.
        /// </summary>
        public static int ObjectsProjectiles;

        /// <summary>Live projectiles by family, so the gate's split has an independent witness.</summary>
        public static int LiveStraight;
        public static int LiveWavy;

        private static int _nextId = 1000;
        private static int _nextGameId = 1;
        private static int _tick;
        private static long _incoming;
        private static long _outgoing;

        /// <summary>
        /// Projectiles spawned as the local player's own shots: `HitsPlayer` false,
        /// `LocalSeedDerived` true, born on the player. There is no correct threat set that contains
        /// one, so a sensor that reports them is wrong by construction.
        /// </summary>
        private static long _localShots;

        /// <summary>Local shots the fixture has spawned. See <see cref="_localShots"/>.</summary>
        public static long LocalShots => _localShots;

        /// <summary>Collisions the host decided, which is what the hook's count is checked against.</summary>
        public static long Hits;

        public static void Populate(DarzasDominion.Darza.Game.World world)
        {
            world.Objects = new System.Collections.Generic.Dictionary<int, GameObject>();
            world.GameObjectLayer = new GameObjectLayer();

            // Two things that are NOT projectiles, deliberately, AND THEY GO INTO BOTH
            // CONTAINERS, which is where the client's map objects are: registered into
            // `Objects` and added to the layer as children (`World.cs:5496`).  The
            // dictionary one is what makes a filter that never rejects anything show up as
            // `other=0`; the layer one is the same test on the walk the sensor now does,
            // as `layer_other=`.
            for (int i = 0; i < 2; i++)
            {
                int id = _nextId++;
                StaticObject staticObject = new StaticObject { ObjectId = id };
                Publish(world, id, staticObject);
                LayerAdd(world, staticObject);
                LiveOthers++;
            }

            for (int i = 0; i < 12; i++)
            {
                Spawn(world);
            }
        }

        /// <summary>Spawns a projectile, sometimes into a recycled instance.</summary>
        public static void Spawn(DarzasDominion.Darza.Game.World world)
        {
            int id = _nextId++;
            GameObject occupant;
            if (_tick % 5 == 4)
            {
                // A RE-RENT: the same object, a new identity.  `Reused` is counted so the
                // run's evidence shows the pooling path was actually exercised rather
                // than merely available.
                occupant = new Projectile();
                Reused++;
            }
            else
            {
                occupant = new Projectile();
            }

            Projectile projectile = (Projectile)occupant;
            projectile.ObjectId = id;
            projectile.GameId = _nextGameId++;
            projectile.StartTime = _tick * 16;
            projectile.Radius = 6.0f;

            // TWO FAMILIES, ALTERNATING, SO THE LINEARITY GATE HAS SOMETHING TO REFUSE.
            // A fixture where every bullet is straight would let a gate that always
            // answers "linear" look correct -- and that answer is the dangerous one,
            // because projecting a weave paints danger where the bullet is not.
            // TWO AIMS, SO THE COLLISION COMPARISON HAS BOTH ANSWERS.  Half the bullets are
            // aimed at the player and will be decided to hit; the rest are aimed a long
            // way off.  A fixture where every bullet hit would let a sensor that predicts
            // *everything* look perfect -- which is precisely the false-positive failure
            // Phase 2 exists to measure.
            DarzasDominion.Darza.Game.Objects.Entities.Player player = world.Player;
            float spawnX = 0.0f;
            float spawnY = 0.0f;
            float angle = (float)((_nextGameId * 0.7) % (2.0 * Math.PI));
            if (player != null)
            {
                if ((_nextGameId % 2) == 0)
                {
                    // INCOMING: 140 units out, on a bearing that points at the player.
                    //
                    // THE BEARING AND THE DESC'S OWN CONVENTION HAVE TO AGREE.  The
                    // families compute `x = sin*v*t, y = cos*v*t`, so the direction of
                    // travel is `(sin a, cos a)` and NOT the usual `(cos a, sin a)`.  The
                    // first version placed the spawn on a `(cos, sin)` offset and handed the
                    // desc `a`, so every "incoming" bullet set off perpendicular to the line
                    // it was supposedly aimed along and none of them reached the player --
                    // `host_hits=0`, and a comparison with nothing to compare.
                    double bearing = (_nextGameId * 0.7) % (2.0 * Math.PI);
                    float sinA = (float)Math.Sin(bearing);
                    float cosA = (float)Math.Cos(bearing);
                    spawnX = player.Position.X - (sinA * 140.0f);
                    spawnY = player.Position.Y - (cosA * 140.0f);
                    angle = (float)bearing;
                    _incoming++;
                }
                else
                {
                    spawnX = player.Position.X - 250.0f;
                    spawnY = player.Position.Y - 250.0f;
                    _outgoing++;
                }
            }

            projectile.SpawnOriginX = spawnX;
            projectile.SpawnOriginY = spawnY;
            projectile.Sin = (float)Math.Sin(angle);
            projectile.Cos = (float)Math.Cos(angle);
            projectile.Desc = (_nextGameId % 3 == 0)
                ? (DarzaCore.Data.ProjDesc)new DarzaCore.Data.WavyDesc()
                : new DarzaCore.Data.StraightDesc();
            projectile.AgeMs = 0;

            // ONE IN THREE IS A LOCAL SHOT: BORN AT THE MUZZLE, AIMED AWAY, AND UNABLE TO HIT ITS
            // OWNER. This is the case that made a live dodge take the wheel around the player's own
            // muzzle (SPIKE3.2-OFFLINE-SOLVER.md section 12): `HitsPlayer` false, `LocalSeedDerived`
            // true, and the spawn point inside the player's collision disc, which is what let it
            // reach the threat set at all. Without it here, the exclusion `TrajectorySampler` now
            // applies is a branch no test in this repository can reach.
            bool localShot = player != null && (_nextGameId % 3 == 1);
            if (localShot)
            {
                projectile.HitsPlayer = false;
                projectile.LocalSeedDerived = true;
                spawnX = player.Position.X;
                spawnY = player.Position.Y;
                projectile.SpawnOriginX = spawnX;
                projectile.SpawnOriginY = spawnY;
                _localShots++;
            }
            else
            {
                projectile.HitsPlayer = true;
                projectile.LocalSeedDerived = false;
            }

            Place(projectile);
            // INTO THE LAYER, NOT INTO `World.Objects`, AND THAT IS THE CLIENT'S PATH:
            // `Entity.cs:1521,1550,1589` rents the projectile, adds it with
            // `World.GameObjectLayer.AddChild` and sets its `World`, and never passes it
            // to the method that writes `Objects[obj.GameId]`.
            LayerAdd(world, projectile);
            Spawned++;
            LiveProjectiles = LiveProjectiles + 1;
            if (projectile.Desc is DarzaCore.Data.WavyDesc)
            {
                LiveWavy++;
            }
            else
            {
                LiveStraight++;
            }
        }

        /// <summary>
        /// COPY ON WRITE, AND THE REASON IS A MEASUREMENT RATHER THAN TASTE.
        ///
        /// The driver's churn runs on its own thread while the hook reads the world from
        /// the game's.  Mutating a dictionary under an enumerator makes `MoveNext` throw
        /// `InvalidOperationException`, which the sensor catches and counts as an
        /// unreconciled frame -- so the fixture was manufacturing a failure the real client
        /// cannot have, because there the dictionary is only ever touched by the frame
        /// thread, which is where the hook is too.  Publishing a fresh dictionary and
        /// letting the in-flight enumerator finish on the old one removes the artifact and
        /// keeps the churn.
        ///
        /// Only `Populate` writes the dictionary now, because the dictionary holds only
        /// non-projectiles and the fixture never retires one -- so there is no `Unpublish`
        /// to go with this.  The container the churn does mutate, the layer, has its own
        /// copy-on-write below.
        /// </summary>
        private static void Publish(DarzasDominion.Darza.Game.World world, int id, GameObject occupant)
        {
            var next = new System.Collections.Generic.Dictionary<int, GameObject>(world.Objects);
            next[id] = occupant;
            world.Objects = next;
        }

        /// <summary>
        /// The layer's copy-on-write, for exactly the reason <see cref="Publish"/> has one.
        ///
        /// The churn runs on its own thread while the hook walks
        /// `World.GameObjectLayer.Children` from the game's.  The client mutates that list
        /// in place, but it only ever does so from the frame thread -- and the hook runs on
        /// that same thread, before the original body -- so the client can never see a
        /// half-mutated list.  The fixture's driver is a *different* thread and can, so it
        /// publishes a fresh layer with a copied child list instead, and an in-flight walk
        /// finishes on the old one.  Same artifact, same fix as the dictionary's.
        /// </summary>
        private static void LayerAdd(DarzasDominion.Darza.Game.World world, GameObject occupant)
        {
            GameObjectLayer next = new GameObjectLayer();
            if (world.GameObjectLayer != null)
            {
                foreach (DarzasDominion.Nodes.Node child in world.GameObjectLayer.Children)
                {
                    next.AddChild(child);
                }
            }
            next.AddChild(occupant);
            world.GameObjectLayer = next;
            LayerNodes = next.Children.Count;
        }

        private static void LayerRemove(DarzasDominion.Darza.Game.World world, GameObject occupant)
        {
            GameObjectLayer next = new GameObjectLayer();
            if (world.GameObjectLayer != null)
            {
                foreach (DarzasDominion.Nodes.Node child in world.GameObjectLayer.Children)
                {
                    if (!object.ReferenceEquals(child, occupant))
                    {
                        next.AddChild(child);
                    }
                }
            }
            world.GameObjectLayer = next;
            LayerNodes = next.Children.Count;
        }

        /// <summary>Puts the projectile where its own trajectory model says it is, from its own origin.</summary>
        private static void Place(Projectile projectile)
        {
            float x;
            float y;
            projectile.Desc.GetPosition(projectile.AgeMs, projectile.Sin, projectile.Cos, out x, out y,
                                        projectile.GameId);
            projectile.Position = new DarzaCore.Geometry.GamePoint
            {
                X = projectile.SpawnOriginX + x,
                Y = projectile.SpawnOriginY + y
            };
        }

        /// <summary>Drives the set for the life of the process: move, collide, churn, retire.</summary>
        public static void Run(DarzasDominion.Darza.Game.World world, int milliseconds)
        {
            // THIRTY MILLISECONDS, NOT ONE HUNDRED AND FIFTY, AND THE COLLISION TEST IS WHY.
            // A straight bullet moves 3.6 units per 30 ms step at this speed; the hit disc
            // is 6 units across.  At a 150 ms step it would move 18 units and could pass
            // straight through the disc without ever being inside it -- a fixture that
            // silently never collides, which would make the whole comparison vacuous.
            const int TickMs = 30;

            // COLLISIONS START EARLY AND CONTINUE, WHICH IS NOT THE SAME AS STARTING LATE.
            // The hook matches targets in a thirty-second discovery window and patches after
            // it, so the collision method has to be COMPILED inside that window to be
            // patched at all -- and a fixture that waits for the patch before making its
            // first collision never has the method compiled in time, which is what happened
            // when this delay was set to 32 s: three collisions, all of them unpatched, and
            // not one line of comparison.  So collisions begin early and the *run* is what
            // is made long enough for the patched window to contain most of them.  The hook matches its targets in a
            // thirty-second discovery window and patches afterwards, so a fixture that starts
            // colliding six seconds in has most of its collisions decided before there is
            // anything to observe them -- the first run of this reported host_hits=15 with
            // three collision lines.  DRELAY_TEST_HITS_AFTER_MS is the delay, and it exists
            // because the alternative is a comparison that silently measures a fraction of
            // the truth.
            int hitsAfterMs = 5000;
            string hitsAfter = Environment.GetEnvironmentVariable("DRELAY_TEST_HITS_AFTER_MS");
            if (!string.IsNullOrEmpty(hitsAfter) && int.TryParse(hitsAfter, out int parsedHitsAfter) &&
                parsedHitsAfter >= 0)
            {
                hitsAfterMs = parsedHitsAfter;
            }
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < milliseconds)
            {
                System.Threading.Thread.Sleep(TickMs);
                _tick++;

                // EVERY LIVE PROJECTILE MOVES, THROUGH ITS OWN DESC, AND IS TESTED FOR THE
                // COLLISION THE HOOK IS WATCHING FOR.  This is what makes the fixture a
                // trajectory source rather than a table of positions: the sampler's
                // prediction can only be checked against a position the same function
                // produced, which is the consistency DODGE-ALGORITHM.md section 4.1 buys by
                // calling rather than reimplementing.
                // EVERY LIVE PROJECTILE IS ASKED, EVERY TICK, ONCE THE FIGHT HAS STARTED --
                // which is what the client does.  `Projectile.Update` calls `Hit()` every
                // frame, `Hit()` calls `HitCharacter()` when `HitsPlayer` is set, and a
                // `true` answer makes the client remove the projectile.  The host does the
                // same, and counts both the answers and the questions: the difference is
                // the defect this fixture did not use to be able to see (the hook counted
                // evaluations as decisions -- see PHASE2-SCOPE.md section 4.14).
                //
                // NOTHING IS ASKED BEFORE `hitsAfterMs`, AND THAT IS NOT A CONVENIENCE.
                // Tiering is off, so the method is compiled exactly once, by its first
                // call -- and a first call made before the hook's discovery window opens
                // means a method the hook never sees.  Five seconds puts that first call
                // inside the window; a fixture that asked from tick one would never be
                // patched at all.
                GameObjectLayer layer = world.GameObjectLayer;
                if (layer != null && clock.ElapsedMilliseconds >= hitsAfterMs)
                {
                    // THE SNAPSHOT IS TAKEN ONCE AND THE WALK DOES NOT MUTATE IT: a removal
                    // publishes a fresh layer, so an in-flight walk on the sensor's thread
                    // is never disturbed and this loop never trips over its own edit.
                    foreach (DarzasDominion.Nodes.Node child in layer.Children)
                    {
                        Projectile projectile = child as Projectile;
                        if (projectile == null)
                        {
                            continue;
                        }
                        projectile.AgeMs = projectile.AgeMs + TickMs;
                        Place(projectile);

                        DarzasDominion.Darza.Game.Objects.Entities.Player player = world.Player;
                        if (player == null)
                        {
                            continue;
                        }
                        projectile.Target = player.Position;

                        // THE CLIENT'S OWN DISPATCH, BEFORE THE METHOD IT DISPATCHES TO. `Hit()` is
                        // `if (HitsPlayer) return HitCharacter(); return HitEnemy();`, so a local
                        // shot is never asked the player question at all -- and a fixture that asked
                        // anyway would report hits the client never resolves, which is the failure
                        // mode the flags exist to prevent.
                        if (!projectile.HitsPlayer)
                        {
                            continue;
                        }

                        // THE GAME'S OWN DECISION, through the method the hook patches.
                        if (projectile.HitCharacter())
                        {
                            bool victimWavy = projectile.Desc is DarzaCore.Data.WavyDesc;
                            LayerRemove(world, projectile);
                            LiveProjectiles = LiveProjectiles - 1;
                            if (victimWavy)
                            {
                                LiveWavy--;
                            }
                            else
                            {
                                LiveStraight--;
                            }
                            Hits++;
                        }
                    }
                }

                // Retire the oldest projectile every hundred and twentieth tick -- 3.6 seconds --
                // so the sensor has to notice an absence rather than only ever seeing arrivals,
                // and so an incoming bullet (1.2 s of flight) arrives BEFORE it is retired.  At
                // 1.5 s the fixture retired every bullet just before it could collide, which is
                // why the first run of this reported host_hits=0.
                if ((_tick % 120) == 0 && LiveProjectiles > 4)
                {
                    GameObjectLayer retireLayer = world.GameObjectLayer;
                    Projectile retireVictim = null;
                    if (retireLayer != null)
                    {
                        foreach (DarzasDominion.Nodes.Node child in retireLayer.Children)
                        {
                            Projectile projectile = child as Projectile;
                            if (projectile != null)
                            {
                                retireVictim = projectile;
                                break;
                            }
                        }
                    }
                    if (retireVictim != null)
                    {
                        bool victimWavy = retireVictim.Desc is DarzaCore.Data.WavyDesc;
                        LayerRemove(world, retireVictim);
                        LiveProjectiles = LiveProjectiles - 1;
                        if (victimWavy)
                        {
                            LiveWavy--;
                        }
                        else
                        {
                            LiveStraight--;
                        }
                        Retired++;
                    }
                }

                if (LiveProjectiles < 20)
                {
                    Spawn(world);
                }
            }
        }
    }

    /// <summary>
    /// The cadence loop, and the caller frame the witness lives in.
    /// </summary>
    public static class CadenceRunner
    {
        public const int WitnessMarker = 0x5A5A1234;

        /// <summary>Body executions the host itself observed.</summary>
        public static long SelfObservedCalls;

        /// <summary>Iterations where the witness had been corrupted; 0 is the pass.</summary>
        public static long WitnessFailures;

        /// <summary>1 the witness was intact at the end, 0 it was not, -1 never ran.</summary>
        public static int WitnessIntact = -1;

        /// <summary>Collections the forcing thread saw, so "it survived" is not "nothing ran".</summary>
        public static long Allocations;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Run(Cadence cadence, int milliseconds)
        {
            // A FRESH WITNESS EVERY ITERATION, AND THE REASON IS THE WHOLE TEST.
            //
            // The first version held one object for the entire loop.  That object is
            // read every iteration, so ordinary collections promote it to gen1/gen2
            // long before the hook patches anything -- and once it is promoted, a gen0
            // collection never revisits it, so "it survived" would have been true
            // whether or not the runtime can walk the frames outside the hook's native
            // detour.  It measured nothing.
            //
            // Allocated immediately before the call and checked immediately after, the
            // witness is young and uniquely referenced by *this* frame: if the
            // walker stops at the detour's native return address and never scans this
            // frame, the forced collection inside the helper reclaims it and the
            // marker below changes.  That is the failure PHASE2-SCOPE.md section 3
            // states, made observable.
            Witness witness = new Witness { Marker = WitnessMarker };

            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            long calls = 0;
            bool corrupted = false;

            try
            {
                while (clock.ElapsedMilliseconds < milliseconds)
                {
                    cadence.FrameTick((float)clock.Elapsed.TotalMilliseconds);
                    calls = calls + 1;

                    // The read is what keeps the witness live across the call above.
                    if (witness.Marker != WitnessMarker)
                    {
                        corrupted = true;
                        WitnessFailures = WitnessFailures + 1;
                        break;
                    }

                    witness = new Witness { Marker = WitnessMarker };

                    System.Threading.Thread.Sleep(16);
                }
            }
            finally
            {
                GC.KeepAlive(witness);
            }

            SelfObservedCalls = calls;
            WitnessIntact = (corrupted || witness.Marker != WitnessMarker) ? 0 : 1;
        }

        /// <summary>Full compacting collections this thread forced, when asked to.</summary>
        public static long ForcedFullCollections;

        /// <summary>
        /// Allocate continuously, so collections happen *while* <see cref="Run"/>
        /// is on the stack.  A run that survives without ever collecting has
        /// measured nothing, which is why the host reports the counter.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void AllocateUntil(int milliseconds, ref bool stop)
        {
            // THE CONTROL FOR THE FORCED-COLLECTION QUESTION.
            //
            // The managed helper can force a full compacting collection from *inside*
            // the detour's call chain, and that is the regime that killed the process:
            // an access violation in `coreclr!gc_heap::plan_phase`, during the mark
            // phase of the second such collection.  Ordinary collections did not.
            // Those two regimes differ in two ways at once -- how full the collection
            // is, and which thread triggers it -- and this switch separates them: with
            // `DRELAY_TEST_FORCE_GC_MS` set, the *host* forces the same full
            // compacting collection from its own thread, which the runtime walks
            // normally, while the helper is suspended by it on the patched stack.
            int forceGcMs = 0;
            string forceGc = Environment.GetEnvironmentVariable("DRELAY_TEST_FORCE_GC_MS");
            if (!string.IsNullOrEmpty(forceGc) && int.TryParse(forceGc, out int parsedForceGc) &&
                parsedForceGc > 0)
            {
                forceGcMs = parsedForceGc;
            }

            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            long nextForcedGc = forceGcMs;
            while (clock.ElapsedMilliseconds < milliseconds && !stop)
            {
                // Small and short-lived on purpose: the point is collection
                // pressure, not retention.  Kept in a local list so the JIT
                // cannot drop the allocations.
                byte[] ballast = new byte[4096];
                ballast[0] = (byte)clock.ElapsedMilliseconds;
                Allocations = Allocations + 1;

                if (forceGcMs > 0 && clock.ElapsedMilliseconds >= nextForcedGc)
                {
                    nextForcedGc = clock.ElapsedMilliseconds + forceGcMs;
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                    ForcedFullCollections = ForcedFullCollections + 1;
                }

                if ((Allocations & 0x3F) == 0)
                {
                    System.Threading.Thread.Sleep(1);
                }
            }
        }
    }
}

internal static class SyntheticHost
{
    private static void Main(string[] args)
    {
        // The marker the test script looks for.  Written by the *host*, i.e.
        // after the startup hook has already run and returned, so its position
        // relative to the bootstrap's own log line is a direct test of the
        // ordering guarantee rather than an inference from timestamps.
        string marker = Environment.GetEnvironmentVariable("DRELAY_TEST_HOST_MARKER");
        if (!string.IsNullOrEmpty(marker))
        {
            try
            {
                File.AppendAllText(marker,
                    DateTime.UtcNow.ToString("O") + " synthetic host Main reached" + Environment.NewLine,
                    Encoding.ASCII);
            }
            catch (IOException)
            {
            }
        }

        Console.WriteLine("synthetic host: Main reached (the bootstrap has already run and returned)");

        // Prove that tiering really is off in this process, because that is the
        // property the whole hook design rests on and this host exists to
        // reproduce the game's runtime configuration.  A host that quietly ran
        // with tiering on would make every test against it meaningless.
        bool tiered = AppContext.TryGetSwitch("System.Runtime.TieredCompilation", out bool value) && value;
        Console.WriteLine("synthetic host: TieredCompilation = " + (tiered ? "ON (WRONG)" : "off"));

        // THE WORLD, EVENTUALLY.  See the note at the top of this file.
        //
        // A thread rather than a timer, and `Sleep` rather than a delay loop: this
        // is a test fixture, and the property under test is only "not yet".
        if (Environment.GetEnvironmentVariable("DRELAY_TEST_NO_WORLD") != "1")
        {
            int worldDelayMs = 3000;
            string delay = Environment.GetEnvironmentVariable("DRELAY_TEST_WORLD_DELAY_MS");
            if (!string.IsNullOrEmpty(delay) && int.TryParse(delay, out int parsedDelay) && parsedDelay >= 0)
            {
                worldDelayMs = parsedDelay;
            }

            var creator = new System.Threading.Thread(() =>
            {
                if (worldDelayMs > 0)
                {
                    System.Threading.Thread.Sleep(worldDelayMs);
                }
                // THE WORLD FIRST, THE PLAYER LATER, AND THE GAP BETWEEN THEM IS THE
                // POINT.
                //
                // The real game does exactly this: `World.Shared` stops being null while
                // a scene is loading, and `World.Player` stays null until a character
                // exists.  The first version of the arming cross-check treated a non-null
                // world as "ready", asked reflection for the player it did not have, and
                // refused the callback for the whole session -- which two live runs paid
                // for and this fixture now reproduces offline.  `DRELAY_TEST_PLAYER_DELAY_MS`
                // is measured from the world's creation, so the default leaves a
                // three-second window in which the hook must wait rather than refuse.
                World.Shared = new World { Player = null };
                Console.WriteLine("synthetic host: world created after " + worldDelayMs +
                                  " ms, with no player in it yet");

                int playerDelayMs = 3000;
                string playerDelay = Environment.GetEnvironmentVariable("DRELAY_TEST_PLAYER_DELAY_MS");
                if (!string.IsNullOrEmpty(playerDelay) &&
                    int.TryParse(playerDelay, out int parsedPlayerDelay) && parsedPlayerDelay >= 0)
                {
                    playerDelayMs = parsedPlayerDelay;
                }
                if (playerDelayMs > 0)
                {
                    System.Threading.Thread.Sleep(playerDelayMs);
                }

                World world = World.Shared;
                if (world != null)
                {
                    world.Player = new Player
                    {
                        // Values that are neither zero nor round numbers, so a
                        // cross-check that compared something other than these
                        // floats -- or that read uninitialised memory -- cannot
                        // pass by accident.
                        Position = new GamePoint { X = 12.5f, Y = -7.25f }
                    };
                    Console.WriteLine("synthetic host: player created " + playerDelayMs +
                                      " ms after the world");

                    // THE OBJECT SET, ONCE THE CHARACTER EXISTS.  It is populated after
                    // the player on purpose: the arming gate's whole waiting window is
                    // about a world that has no player yet, and adding objects first
                    // would exercise a different ordering than the one the hook is
                    // built for.
                    SyntheticHostFixture.ObjectSetDriver.Populate(world);
                    var setDriver = new System.Threading.Thread(() =>
                        SyntheticHostFixture.ObjectSetDriver.Run(world, 120000));
                    setDriver.IsBackground = true;
                    setDriver.Start();
                    Console.WriteLine("synthetic host: object set populated; the driver will churn it");
                }
            });
            creator.IsBackground = true;
            creator.Start();
        }
        else
        {
            Console.WriteLine("synthetic host: DRELAY_TEST_NO_WORLD=1, so World.Shared stays null");
        }

        // THE CADENCE, IN PLACE OF THE PLAIN SLEEP.
        //
        // The host used to sleep here and exit.  It now runs a stand-in frame
        // loop instead, so that `SyntheticHostFixture.Cadence.FrameTick` is
        // JITted (after the hook's detour is installed) and then entered
        // thousands of times -- which is the only way a target of this host can
        // be matched and patched at all.  The loop is still bounded by
        // `DRELAY_TEST_HOLD_MS`, so every existing run's lifetime is unchanged.
        int holdMs = 3000;
        string hold = Environment.GetEnvironmentVariable("DRELAY_TEST_HOLD_MS");
        if (!string.IsNullOrEmpty(hold) && int.TryParse(hold, out int parsed) && parsed > 0)
        {
            holdMs = parsed;
        }

        // WAIT BEFORE THE FIRST CALL, AND THIS IS NOT PADDING.
        //
        // Installing the detour and *matching* targets are two different states,
        // and the second one starts later: the native side waits
        // `--collect-delay` ms after the bootstrap releases `Main` before it opens
        // its watching window, and a method compiled before that is deliberately
        // "not lost, only unwatched" (see the hook's own log line).  This host
        // releases `Main` as soon as the hook is installed, so without a delay it
        // compiles its stand-in frame tick in the unwatched interval -- the method
        // is JITted exactly once with tiering off, so there is no second chance,
        // and the run reports `0 of 5 target(s) patched` while the cadence loop
        // runs fifteen hundred times around it.  Measured, not guessed: that is
        // what run 7 did the first time it was written.
        int cadenceDelayMs = 0;
        string cadenceDelay = Environment.GetEnvironmentVariable("DRELAY_TEST_CADENCE_DELAY_MS");
        if (!string.IsNullOrEmpty(cadenceDelay) && int.TryParse(cadenceDelay, out int parsedCadenceDelay) &&
            parsedCadenceDelay >= 0)
        {
            cadenceDelayMs = parsedCadenceDelay;
        }

        // A SECOND THREAD THAT ALLOCATES, so collections can happen *while* the
        // cadence loop is on the stack.  `DRELAY_TEST_NO_ALLOCATOR=1` is the
        // control: without it, "the witness survived" would be indistinguishable
        // from "no collection ever ran".
        bool stopAllocator = false;
        System.Threading.Thread allocator = null;
        if (Environment.GetEnvironmentVariable("DRELAY_TEST_NO_ALLOCATOR") != "1")
        {
            allocator = new System.Threading.Thread(() =>
                SyntheticHostFixture.CadenceRunner.AllocateUntil(cadenceDelayMs + holdMs + 1000, ref stopAllocator));
            allocator.IsBackground = true;
            allocator.Start();
        }

        if (cadenceDelayMs > 0)
        {
            Console.WriteLine("synthetic host: waiting " + cadenceDelayMs +
                              " ms before the first cadence call, so the method is compiled while the hook is watching");
            System.Threading.Thread.Sleep(cadenceDelayMs);
        }

        var cadence = new SyntheticHostFixture.Cadence();
        SyntheticHostFixture.CadenceRunner.Run(cadence, holdMs);
        stopAllocator = true;

        Console.WriteLine("synthetic host: cadence ran " + SyntheticHostFixture.CadenceRunner.SelfObservedCalls +
                          " call(s); the patched body ran " + cadence.Calls + " time(s); witness intact=" +
                          SyntheticHostFixture.CadenceRunner.WitnessIntact + "; allocations=" +
                          SyntheticHostFixture.CadenceRunner.Allocations);

        // A FILE, NOT THE CONSOLE.  The launcher does not promise to forward the
        // child's stdout, and every other assertion in this fixture that needs a
        // number out of the host reads a file the host wrote (see
        // DRELAY_TEST_HOST_MARKER above).  The same discipline applies here.
        string cadenceFile = Environment.GetEnvironmentVariable("DRELAY_TEST_CADENCE_FILE");
        if (!string.IsNullOrEmpty(cadenceFile))
        {
            try
            {
                File.WriteAllText(cadenceFile,
                    "self_observed_calls=" + SyntheticHostFixture.CadenceRunner.SelfObservedCalls + "\n" +
                    "patched_body_calls=" + cadence.Calls + "\n" +
                    "witness_intact=" + SyntheticHostFixture.CadenceRunner.WitnessIntact + "\n" +
                    "witness_failures=" + SyntheticHostFixture.CadenceRunner.WitnessFailures + "\n" +
                    "allocations=" + SyntheticHostFixture.CadenceRunner.Allocations + "\n" +
                    "forced_full_collections=" + SyntheticHostFixture.CadenceRunner.ForcedFullCollections + "\n" +
                    "live_projectiles=" + SyntheticHostFixture.ObjectSetDriver.LiveProjectiles + "\n" +
                    "live_others=" + SyntheticHostFixture.ObjectSetDriver.LiveOthers + "\n" +
                    "layer_nodes=" + SyntheticHostFixture.ObjectSetDriver.LayerNodes + "\n" +
                    "objects_projectiles=" + SyntheticHostFixture.ObjectSetDriver.ObjectsProjectiles + "\n" +
                    "live_straight=" + SyntheticHostFixture.ObjectSetDriver.LiveStraight + "\n" +
                    "live_wavy=" + SyntheticHostFixture.ObjectSetDriver.LiveWavy + "\n" +
                    "host_hits=" + Projectile.HitCharacterDecisions + "\n" +
                    "hit_character_evaluations=" + Projectile.HitCharacterCalls + "\n" +
                    "spawned=" + SyntheticHostFixture.ObjectSetDriver.Spawned + "\n" +
                    "retired=" + SyntheticHostFixture.ObjectSetDriver.Retired + "\n" +
                    "reused=" + SyntheticHostFixture.ObjectSetDriver.Reused + "\n" +
                    "last_total=" + cadence.LastTotal.ToString("R") + "\n",
                    Encoding.ASCII);
            }
            catch (IOException)
            {
            }
        }

        Console.WriteLine("synthetic host: exiting");
        Environment.Exit(0);
    }
}









