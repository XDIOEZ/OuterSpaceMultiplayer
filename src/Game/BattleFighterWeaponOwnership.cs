using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // Called after ValidateBattleWeaponEffects has checked wire structure,
    // each weapon and all beams. The former
    // weapons.Any(w => w.Ship != fighter.RuntimeId) captured a new fighter
    // closure in every authoritative/native replica checkpoint, including
    // fighters with no weapons. Iterate the real List<T> instead and use
    // one static, noncapturing selector shared across fighters.
    internal static class BattleFighterWeaponOwnership
    {
        internal static bool ContainsForeignShip<TWeapon>(
            List<TWeapon> weapons, long expectedShip, Func<TWeapon, long> shipId)
        {
            // This guard is normally unreachable because the shared wire
            // validator rejects null weapon lists earlier in the call chain.
            if (weapons == null) throw new ArgumentNullException("source");
            if (shipId == null) throw new ArgumentNullException(nameof(shipId));

            // List<T>.Enumerator is a value type, preserving the original
            // Any(predicate) visitation/early-exit and mutation-fault order.
            foreach (TWeapon weapon in weapons)
                if (shipId(weapon) != expectedShip) return true;
            return false;
        }
    }
}
