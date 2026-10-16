// A fake game, shaped like the parts of the client WorldLog reads. The field and property NAMES
// are the real ones (checked against hook/work/decomp), so the resolution walk this exercises is
// the walk the hook will make.
using System.Collections.Generic;

namespace DarzasDominion.Darza.Game
{
    public struct Vec2
    {
        public float X;
        public float Y;
    }
}

namespace DarzasDominion.Darza.Game.Objects.Entities
{
    public class Player
    {
        public DarzasDominion.Darza.Game.Vec2 Position;
        public float Radius = 0.25f;
        public double Health = 100.0;
        public double MaxHealth = 120.0;
    }
}

namespace DarzaGameNet.Packets.Game.GameServer
{
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

namespace DarzasDominion.Darza.Game.Objects.Map.Activatable
{
    public class ActivatableObject
    {
        public int GameId;
        public float X;
        public float Y;
        // THE REAL ONE IS A PRIVATE FIELD ON `Portal`, reached up the hierarchy. See `Portal.locked`.
    }

    public class Portal : ActivatableObject
    {
        private bool locked;
        public bool Called;

        public string Title { get; set; }
        public string Tag { get; set; }

        public void SetLocked(bool value) { locked = value; }
    }
}

namespace DarzasDominion.Darza.Game
{
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
        public Enemy[] GetEnemies()
        {
            return Enemy.All();
        }
    }

    // The enemy element type. `GetEnemies` returns an array of these on the real client.
    public class Enemy
    {
        public int GameId;
        public DarzasDominion.Darza.Game.Vec2 Position;
        public float Radius = 0.5f;
        public float Health = 40.0f;
        public float MaxHealth = 60.0f;
        public bool Dead;
        public bool Npc;

        public static Enemy[] All()
        {
            return new[]
            {
                Make(11, 130.0f, 140.0f, 0.5f, 40.0f, 60.0f, false),
                Make(12, 100.0f, 100.5f, 0.5f, 5.0f, 60.0f, false),
                Make(13, 300.0f, 300.0f, 0.5f, 9.0f, 9.0f, true),    // far away: must be culled out of "near"
                Make(14, 105.0f, 104.0f, 0.5f, 1.0f, 1.0f, false),
            };
        }

        private static Enemy Make(int id, float x, float y, float r, float hp, float max,
                                  bool npc)
        {
            return new Enemy
            {
                GameId = id,
                Position = new DarzasDominion.Darza.Game.Vec2 { X = x, Y = y },
                Radius = r,
                Health = hp,
                MaxHealth = max,
                Npc = npc,
            };
        }
    }
}
