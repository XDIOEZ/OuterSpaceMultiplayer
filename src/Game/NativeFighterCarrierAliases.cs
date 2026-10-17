using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // The authoritative native ship can acquire a new source UID between
    // battle snapshots. One Unity object must not remain registered under
    // multiple source IDs, but comparing Unity objects with == can conflate
    // a despawned shell with null. Match native references explicitly.
    internal static class NativeFighterCarrierAliases
    {
        internal static void RemoveOldIds<TCarrier>(
            Dictionary<long, TCarrier> carriers, long currentId, TCarrier carrier)
            where TCarrier : class
        {
            if (carriers == null) throw new ArgumentNullException(nameof(carriers));
            // Common case: there is no alias. A Dictionary enumerator is a
            // struct, avoiding the per-carrier LINQ iterator/ToArray from
            // the original capture hot path. Remove only *after* enumeration
            // ends, supporting the Unity Mono Dictionary implementation.
            while (true)
            {
                bool found = false;
                long obsolete = 0;
                foreach (KeyValuePair<long, TCarrier> pair in carriers)
                {
                    if (pair.Key == currentId || !ReferenceEquals(pair.Value, carrier)) continue;
                    obsolete = pair.Key;
                    found = true;
                    break;
                }
                if (!found) return;
                carriers.Remove(obsolete);
            }
        }
    }
}
