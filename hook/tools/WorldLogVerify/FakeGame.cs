// A fake game, shaped like the parts of the client WorldLog reads.
//
// =====================================================================================
// THE SHAPES ARE THE CLIENT'S OWN, AND THAT IS THE WHOLE POINT OF THIS FILE
// =====================================================================================
//
// The first version of this fake declared `ActivatableObject.X`, `ActivatableObject.Y`, and
// `Portal.Title`/`Tag` as PROPERTIES. The real client has none of those: a portal's position is
// `Node.Position`, a `Vector3` field, and `Title` is a public FIELD on `ActivatableObject` while
// `Tag` is a public FIELD on `GameObject`. Because the fake was written to match the resolver
// instead of the client, the verifier passed against a shape the game does not have, and the
// operator's 2026-10-18 session produced 292 frame lines whose every portal row was
// `"x":0,"y":0,"title":null` -- well formed, and useless.
//
// So every shape here is checked against `hook/work/decomp` and named in a comment:
//
//     Node                 (DarzasDominion.Nodes.Node)          public Vector3 Position
//     GameObject : Node    (DarzasDominion.Darza.Game.Objects)   public int GameId, string Tag,
//                                                               float Radius
//     StaticObject : GameObject                                   (nothing this log reads)
//     ActivatableObject : StaticObject                            public string Title
//     Portal : ActivatableObject                                  private bool locked,
//                                                                 public bool Called
//     Player : Character : Entity                                 public new int Health/MaxHealth
//                                                               shadowing Entity's doubles
//
// THE LAST ONE IS THE SUBTLE ONE AND IT IS THE REASON `FieldAccessor` GREW A TYPE CHECK. A
// hierarchy walk from `Player` finds `Player.Health`, an `int`, before it ever sees
// `Entity.Health`, a `double` -- and an `int` read through a `double`-returning accessor is the
// int's bits reinterpreted, which is 2.08E-322 for 42. This fake keeps both fields so that a
// regression in the reader is a failed assertion rather than a session spent.
using System.Collections.Generic;

namespace Microsoft.Xna.Framework
{
    /// <summary>
    /// `Microsoft.Xna.Framework.Vector3`, reduced to the three floats the log reads. It is a
    /// struct with public FIELDS because the real one is, and `NestedFloat` reads `Position.X`
    /// with an `ldflda`/`ldfld` pair that would be wrong for a property.
    /// </summary>
    public struct Vector3
    {
        public float X;
        public float Y;
        public float Z;

        public Vector3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }
    }
}

namespace DarzasDominion.Nodes
{
    /// <summary>`DarzasDominion.Nodes.Node` -- the position carrier for every map object.</summary>
    public class Node
    {
        public Microsoft.Xna.Framework.Vector3 Position;
    }
}

namespace DarzasDominion.Darza.Game.Objects
{
    /// <summary>`DarzasDominion.Darza.Game.Objects.GameObject`.</summary>
    public class GameObject : DarzasDominion.Nodes.Node
    {
        public int GameId;
        public string Tag;
        public float Radius = 16.0f;
    }
}

namespace DarzasDominion.Darza.Game.Objects.Map
{
    /// <summary>`DarzasDominion.Darza.Game.Objects.Map.StaticObject`.</summary>
    public class StaticObject : DarzasDominion.Darza.Game.Objects.GameObject
    {
        public bool BlocksPath;
    }
}

namespace DarzasDominion.Darza.Game.Objects.Map.Activatable
{
    /// <summary>
    /// `DarzasDominion.Darza.Game.Objects.Map.Activatable.ActivatableObject`.
    ///
    /// `Title` IS A FIELD HERE, as it is in the client, and it is one level up from `Portal` --
    /// which is why a `GetProperty("Title")` on `Portal` found nothing and the session's header
    /// listed the column as missing.
    /// </summary>
    public class ActivatableObject : DarzasDominion.Darza.Game.Objects.Map.StaticObject
    {
        public string Title;
        public string ButtonTitle;

        public virtual void Activate(int button)
        {
        }
    }

    /// <summary>
    /// `SoloDungeonStatue : ActivatableObject` -- one of the 46 non-`Portal` activatables the client
    /// declares, and the shape the world log spends `portalsOther` on. It is in the fake because a
    /// count of "other activatables" cannot answer "which object do I walk into", and the 2026-10-18
    /// re-run's one open question is exactly that.
    /// </summary>
    public class SoloDungeonStatue : ActivatableObject
    {
        public int Level = 25;
    }

    /// <summary>
    /// `DarzasDominion.Darza.Game.Objects.Map.Activatable.Portal`, including `locked` being a
    /// private field and `Called` a public one. Both spellings are exercised on purpose: the
    /// resolver has to find a private field on the leaf type and a public one on a base.
    /// </summary>
    public class Portal : ActivatableObject
    {
        private bool locked;

        public bool Called;

        public void SetLocked(bool value)
        {
            locked = value;
        }
    }
}

namespace DarzasDominion.Darza.Game.Objects.Entities
{
    /// <summary>
    /// `DarzasDominion.Darza.Game.Objects.Entities.Entity` -- the owner of the real health, as
    /// `double`, and a `GameObject` (which is where a character's `Position` and `Radius` come
    /// from).
    /// </summary>
    public class Entity : DarzasDominion.Darza.Game.Objects.GameObject
    {
        public double Health = 100.0;
        public double MaxHealth = 120.0;
    }

    /// <summary>`Character`, which this log only needs as a link in the chain.</summary>
    public class Character : Entity
    {
        public float Speed = 9.0f;
    }

    /// <summary>
    /// `Player`, WITH THE SHADOWING FIELDS. `public new int Health` and `public new int MaxHealth`
    /// are the client's own declarations (`Player.cs:1547`, `:1549`), and `Position` is inherited
    /// from `Node` exactly as the log expects to find it.
    /// </summary>
    public class Player : Character
    {
        public new int Health = 150;
        public new int MaxHealth = 2000;
    }
}

namespace DarzaGameNet.Packets.Game.GameServer
{
    /// <summary>`GmMapInfo` -- every field this log reads is a public field on the packet type.</summary>
    public class GmMapInfo
    {
        public string Name;
        public string AssetName;
        public int Width;
        public int Height;
        public int PlayerId;
        public bool CanTeleport;
        public string Levels;
        public byte Difficulty;
        public bool OpenedByKey;
        public bool IsSoloRun;
    }
}

namespace DarzasDominion.Darza.Game
{
    /// <summary>The parts of `World` this log reads, with the client's own declarations.</summary>
    public class World
    {
        public static World Shared;

        public DarzasDominion.Darza.Game.Objects.Entities.Player Player;
        public DarzaGameNet.Packets.Game.GameServer.GmMapInfo MapInfo;
        public bool PlayerDead;
        public bool SafeWorld;
        public bool VaultWorld;
        public bool Space;
        public List<DarzasDominion.Darza.Game.Objects.Map.Activatable.ActivatableObject> Activatables
            = new List<DarzasDominion.Darza.Game.Objects.Map.Activatable.ActivatableObject>();

        // THE ONLY byte[,] ON World, which is how the hook finds the collision grid. The name is
        // deliberately meaningless: nothing may depend on it.
        public byte[,] hehe_EB2C_E5A5_EE82_E9C5_EF68 = new byte[140, 96];

        // `World.GetEnemies()` -- the same call `AutoAim` and `EnemyReader` resolve. It is on World
        // because that is where the client declares it, and `EnemyReader` looks for it there.
        public DarzasDominion.Darza.Game.Objects.Entities.Enemy[] GetEnemies()
        {
            return DarzasDominion.Darza.Game.Objects.Entities.Enemy.All();
        }
    }
}

namespace DarzasDominion.Darza.Game.Objects.Entities
{
    // The enemy element type. `GetEnemies` returns an array of these on the real client, and the
    // health it carries is `Entity`'s `double` rather than a shadowing int -- which is why the
    // enemy rows of the 2026-10-18 log were RIGHT while the player's were zero. The fake keeps
    // that asymmetry too: it is the thing being tested.
    public class Enemy : Entity
    {
        // `Position` AND `Radius` ARE INHERITED, exactly as they are on the client: `Enemy` is an
        // `Entity` is a `GameObject` is a `Node`, so an enemy's position is the same
        // `Node.Position` a portal's is and its radius is `GameObject.Radius`.
        public bool Dead;
        public bool Npc;

        public static Enemy[] All()
        {
            return new[]
            {
                Make(11, 130.0f, 140.0f, 40.0f, 60.0f, false),
                Make(12, 100.0f, 100.5f, 5.0f, 60.0f, false),
                Make(13, 300.0f, 300.0f, 9.0f, 9.0f, true),    // far away: must be culled out of "near"
                Make(14, 105.0f, 104.0f, 1.0f, 1.0f, false),
            };
        }

        private static Enemy Make(int id, float x, float y, double hp, double max, bool npc)
        {
            return new Enemy
            {
                GameId = id,
                Position = new Microsoft.Xna.Framework.Vector3(x, y, 0.0f),
                Radius = 0.5f,
                Health = hp,
                MaxHealth = max,
                Npc = npc,
            };
        }
    }
}
