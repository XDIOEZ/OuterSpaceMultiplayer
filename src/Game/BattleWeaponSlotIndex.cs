using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // Frame-local only: native fighter weapons are recreated on spawn/refit.
    // SingleOrDefault semantics are important: only a duplicated *requested*
    // slot is an error; an unrelated duplicate cannot reject that weapon.
    internal sealed class BattleWeaponSlotIndex<TWeapon> where TWeapon : class
    {
        private readonly Dictionary<string, TWeapon> first =
            new Dictionary<string, TWeapon>(StringComparer.Ordinal);
        private readonly HashSet<string> duplicates = new HashSet<string>(StringComparer.Ordinal);
        private TWeapon nullSlot;
        private bool duplicateNull;

        internal BattleWeaponSlotIndex(IEnumerable<TWeapon> weapons, Func<TWeapon, string> slotOf)
        {
            if (weapons == null) throw new ArgumentNullException(nameof(weapons));
            if (slotOf == null) throw new ArgumentNullException(nameof(slotOf));
            foreach (TWeapon weapon in weapons)
            {
                if (weapon == null) continue; // Matches the native LINQ predicate.
                string slot = slotOf(weapon);
                if (slot == null)
                {
                    if (nullSlot == null) nullSlot = weapon;
                    else duplicateNull = true;
                }
                else if (!first.ContainsKey(slot)) first.Add(slot, weapon);
                else duplicates.Add(slot);
            }
        }

        internal TWeapon SingleOrDefault(string slot)
        {
            if (slot == null)
            {
                if (duplicateNull) throw new InvalidOperationException("Sequence contains more than one matching element");
                return nullSlot;
            }
            if (duplicates.Contains(slot)) throw new InvalidOperationException("Sequence contains more than one matching element");
            TWeapon weapon;
            return first.TryGetValue(slot, out weapon) ? weapon : null;
        }
    }
}
