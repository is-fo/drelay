// A stand-in for the two hook constants the linked sources need. The real one lives in
// StartupHook.cs, which cannot be compiled without the game.
namespace Drelay.Bootstrap
{
    internal static class ManagedArmingProbe
    {
        internal const string WorldTypeName = "DarzasDominion.Darza.Game.World";
        internal const string PlayerTypeName = "DarzasDominion.Darza.Game.Objects.Entities.Player";
    }
}
