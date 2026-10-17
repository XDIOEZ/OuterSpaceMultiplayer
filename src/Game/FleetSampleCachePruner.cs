using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // The same frame-local UID presence sets drive both native fleet samples
    // and cached transit content. Never remove from a Dictionary while its
    // keys are being enumerated: the Unity/Mono runtime can invalidate the
    // enumerator even if newer .NET runtimes allow certain removals.
    internal static class FleetSampleCachePruner
    {
        internal static int RemoveMissing<TValue>(
            Dictionary<long, TValue> cached, HashSet<long> present)
        {
            if (cached == null) throw new ArgumentNullException(nameof(cached));
            if (present == null) throw new ArgumentNullException(nameof(present));

            bool found = false;
            long firstMissing = 0;
            List<long> moreMissing = null;
            foreach (long uid in cached.Keys)
            {
                if (present.Contains(uid)) continue;
                if (!found)
                {
                    firstMissing = uid;
                    found = true;
                }
                else
                {
                    if (moreMissing == null) moreMissing = new List<long>();
                    moreMissing.Add(uid);
                }
            }

            // Common unchanged/one-removal frames need no temporary list or
            // LINQ iterator. Multiple removals preserve the previous
            // Keys.Where(...).ToArray() enumeration/removal order.
            if (!found) return 0;
            cached.Remove(firstMissing);
            if (moreMissing != null)
                foreach (long uid in moreMissing) cached.Remove(uid);
            return 1 + (moreMissing == null ? 0 : moreMissing.Count);
        }
    }
}
