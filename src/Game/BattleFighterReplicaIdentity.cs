using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // Per-fighter replica identity in the already-validated battle state.
    // Keep all four numbers: native runtime UIDs can be reused when a new
    // craft launches from the same carrier/wing/slot.
    internal struct BattleFighterReplicaIdentity : IEquatable<BattleFighterReplicaIdentity>
    {
        internal readonly long Carrier;
        internal readonly int Wing;
        internal readonly int Slot;
        internal readonly long RuntimeId;

        internal BattleFighterReplicaIdentity(long carrier, int wing, int slot, long runtimeId)
        {
            Carrier = carrier;
            Wing = wing;
            Slot = slot;
            RuntimeId = runtimeId;
        }

        public bool Equals(BattleFighterReplicaIdentity other)
        {
            return Carrier == other.Carrier && Wing == other.Wing
                && Slot == other.Slot && RuntimeId == other.RuntimeId;
        }
        public override bool Equals(object other)
        { return other is BattleFighterReplicaIdentity && Equals((BattleFighterReplicaIdentity)other); }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)(Carrier ^ (Carrier >> 32));
                hash = hash * 397 ^ Wing;
                hash = hash * 397 ^ Slot;
                return hash * 397 ^ (int)(RuntimeId ^ (RuntimeId >> 32));
            }
        }

        // Only used for the original GameObject name when a brand-new
        // replica is spawned. Routine validation/lookup does not allocate
        // these old slash-delimited strings anymore.
        public override string ToString()
        { return Carrier + "/" + Wing + "/" + Slot + "/" + RuntimeId; }
    }

    internal static class BattleFighterReplicaPruner
    {
        // The original Keys.Where(...).ToArray() walked keys in dictionary
        // order, then removed them after the enumeration completed. Keep that
        // snapshot-before-mutation behavior, but allocate no temporary
        // collection when no fighter has left the battle this frame.
        internal static List<BattleFighterReplicaIdentity> FindMissing<T>(
            Dictionary<BattleFighterReplicaIdentity, T> replicas,
            HashSet<BattleFighterReplicaIdentity> present)
        {
            if (replicas == null) throw new ArgumentNullException(nameof(replicas));
            if (present == null) throw new ArgumentNullException(nameof(present));
            List<BattleFighterReplicaIdentity> stale = null;
            foreach (BattleFighterReplicaIdentity key in replicas.Keys)
                if (!present.Contains(key))
                {
                    if (stale == null) stale = new List<BattleFighterReplicaIdentity>();
                    stale.Add(key);
                }
            return stale;
        }
    }
}
