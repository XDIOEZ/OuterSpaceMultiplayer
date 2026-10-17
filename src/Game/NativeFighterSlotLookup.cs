using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // A single ApplyPlayerBattleFighters checkpoint owns this index. Native
    // FighterSlot instances belong to the current Unity wing and must never
    // survive a scene transition, carrier replacement or a later frame.
    internal sealed class NativeFighterSlotLookup<TSlot> where TSlot : class
    {
        private readonly Dictionary<int, TSlot> first = new Dictionary<int, TSlot>();
        private HashSet<int> duplicate;

        private NativeFighterSlotLookup() { }

        internal static NativeFighterSlotLookup<TSlot> TryCreate(TSlot[] slots, Func<TSlot, int> indexOf)
        {
            if (slots == null) throw new ArgumentNullException(nameof(slots));
            if (indexOf == null) throw new ArgumentNullException(nameof(indexOf));
            var result = new NativeFighterSlotLookup<TSlot>();
            foreach (TSlot slot in slots)
            {
                // A malformed native wing may contain null slots. Preserve
                // the old short-circuit Any/Single behavior for that wing
                // instead of eagerly touching the invalid native component.
                if (slot == null) return null;
                int id = indexOf(slot);
                if (!result.first.ContainsKey(id)) result.first.Add(id, slot);
                else
                {
                    if (result.duplicate == null) result.duplicate = new HashSet<int>();
                    result.duplicate.Add(id);
                }
            }
            return result;
        }

        // Matches native wing.slots.Any(s => s.slotIndex == slotIndex).
        internal bool Contains(int slotIndex) { return first.ContainsKey(slotIndex); }

        // Matches native wing.slots.Single(s => s.slotIndex == slotIndex).
        // Only duplicates of the requested slot fail; unrelated duplicates
        // must not reject an otherwise valid replicated slot.
        internal TSlot Single(int slotIndex)
        {
            if (duplicate != null && duplicate.Contains(slotIndex))
                throw new InvalidOperationException("Sequence contains more than one matching element");
            TSlot slot;
            if (!first.TryGetValue(slotIndex, out slot))
                throw new InvalidOperationException("Sequence contains no matching element");
            return slot;
        }
    }
}
