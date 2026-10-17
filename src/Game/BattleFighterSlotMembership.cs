using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // One incoming/native battle-fighter snapshot only. Validated fighter
    // identities are not Unity objects and are never retained between frames.
    // The old final pass scanned every fighter for every active wing slot;
    // the dictionary preserves its exact carrier/wing/slot/runtime-UID match.
    internal sealed class BattleFighterSlotMembership
    {
        private struct SlotKey : IEquatable<SlotKey>
        {
            internal readonly long Carrier;
            internal readonly int Wing, Slot;

            internal SlotKey(long carrier, int wing, int slot)
            { Carrier = carrier; Wing = wing; Slot = slot; }

            public bool Equals(SlotKey other)
            { return Carrier == other.Carrier && Wing == other.Wing && Slot == other.Slot; }

            public override bool Equals(object value)
            { return value is SlotKey && Equals((SlotKey)value); }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = (int)(Carrier ^ (Carrier >> 32));
                    return (hash * 397 ^ Wing) * 397 ^ Slot;
                }
            }
        }

        private readonly Dictionary<SlotKey, long> fighters = new Dictionary<SlotKey, long>();

        // Same uniqueness check as the previous occupied HashSet of
        // "carrier/wing/slot", including different runtime IDs in one slot.
        internal bool TryRegister(long carrier, int wing, int slot, long runtimeId)
        {
            var key = new SlotKey(carrier, wing, slot);
            if (fighters.ContainsKey(key)) return false;
            fighters.Add(key, runtimeId);
            return true;
        }

        internal bool Matches(long carrier, int wing, int slot, long runtimeId)
        {
            long registered;
            return fighters.TryGetValue(new SlotKey(carrier, wing, slot), out registered)
                && registered == runtimeId;
        }
    }
}
