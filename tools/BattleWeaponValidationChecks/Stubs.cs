using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    #pragma warning disable CS0649 // Wire DTO fields assigned by Unity/JSON at runtime.
    // Only private wire DTO boundaries are replaced. All validation rules are
    // compiled directly from the two linked production source files.
    public sealed partial class GameBridge
    {
        internal sealed class BattleBeamLine
        {
            public bool Enabled, Charge, Muzzle, Hit;
            public float X, Y, Z, EndX, EndY, EndZ, Width, EndWidth;
        }
        internal sealed class BattleWeaponEffect
        {
            public long Ship;
            public string Slot, Resource;
            public float Angle;
            public int Ammo;
            public List<BattleBeamLine> Beams = new List<BattleBeamLine>();
        }
        internal static void CheckWeaponWire(List<BattleWeaponEffect> weapons)
        { ValidateBattleWeaponEffects(weapons); }
    }
    #pragma warning restore CS0649
}
