using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // Used only to select wire fields from the host's sampled fleet state.
    // Previous values are per-recipient *confirmed* values, not pending sends.
    // Returning null on no change avoids allocating a FleetContent and its
    // per-fleet field list for every stable fleet in every recipient frame.
    internal static class FleetDeltaFieldSelector
    {
        internal static List<TWire> Select<TWire>(
            Dictionary<string, NativeWire.CachedValue> current,
            Dictionary<string, string> confirmed,
            bool full,
            Func<string, string, TWire> create)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            if (create == null) throw new ArgumentNullException(nameof(create));
            List<TWire> changed = null;
            foreach (KeyValuePair<string, NativeWire.CachedValue> field in current)
            {
                string encoded = field.Value.Encoded;
                string old;
                if (full || confirmed == null || !confirmed.TryGetValue(field.Key, out old)
                    || !String.Equals(old, encoded, StringComparison.Ordinal))
                {
                    if (changed == null) changed = new List<TWire>();
                    changed.Add(create(field.Key, encoded));
                }
            }
            return changed;
        }
    }
}
