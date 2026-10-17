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
        private struct WingKey : IEquatable<WingKey>
        {
            internal readonly long Carrier;
            internal readonly int Wing;
            internal WingKey(long carrier, int wing)
            { Carrier = carrier; Wing = wing; }
            public bool Equals(WingKey other)
            { return Carrier == other.Carrier && Wing == other.Wing; }
            public override bool Equals(object value)
            { return value is WingKey && Equals((WingKey)value); }
            public override int GetHashCode()
            {
                unchecked { return ((int)(Carrier ^ (Carrier >> 32)) * 397) ^ Wing; }
            }
        }

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
        // A validated wing may have up to 256 slots, but we only index wings
        // actually referenced by a fighter. Empty/unused wings cost nothing.
        // Per-frame indexes are never retained across native fighter respawns.
        private readonly HashSet<WingKey> indexedWings = new HashSet<WingKey>();
        private readonly Dictionary<SlotKey, long> declaredSlots = new Dictionary<SlotKey, long>();

        internal bool DeclaredSlotMatches<TSlot>(long carrier, int wing,
            IEnumerable<TSlot> slots, int slot, long runtimeId,
            Func<TSlot, int> indexOf, Func<TSlot, long> fighterOf)
        {
            if (slots == null) throw new ArgumentNullException(nameof(slots));
            if (indexOf == null) throw new ArgumentNullException(nameof(indexOf));
            if (fighterOf == null) throw new ArgumentNullException(nameof(fighterOf));
            if (indexedWings.Add(new WingKey(carrier, wing)))
            {
                // The caller has already rejected null and duplicate slot
                // identities. Do not weaken that validation or copy native
                // Unity components into this transient wire-state index.
                foreach (TSlot candidate in slots)
                    declaredSlots.Add(new SlotKey(carrier, wing, indexOf(candidate)), fighterOf(candidate));
            }
            long declared;
            return declaredSlots.TryGetValue(new SlotKey(carrier, wing, slot), out declared)
                && declared == runtimeId;
        }

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
