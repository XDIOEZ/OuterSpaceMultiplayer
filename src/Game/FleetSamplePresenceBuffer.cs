using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // A host samples all authoritative fleet UIDs once per Unity frame.
    // Reuse its presence set when sampling is not nested, rather than
    // allocating a fresh HashSet<long> for every frame. Native callbacks
    // can reenter a checkpoint; a nested capture must have its own set.
    internal sealed class FleetSamplePresenceBuffer
    {
        private readonly HashSet<long> reusable = new HashSet<long>();
        private bool inUse, resetPending;

        internal HashSet<long> Rent()
        {
            if (inUse) return new HashSet<long>();
            inUse = true;
            return reusable;
        }

        internal void Return(HashSet<long> present)
        {
            if (present == null) throw new ArgumentNullException(nameof(present));
            present.Clear();
            if (!ReferenceEquals(present, reusable)) return;
            inUse = false;
            if (resetPending)
            {
                reusable.TrimExcess();
                resetPending = false;
            }
        }

        // Battle/room exit releases capacity accumulated during fleet bursts.
        // If a scene change happens inside a native sampling callback, do
        // not invalidate the active frame's duplicate-UID presence set.
        internal void Reset()
        {
            if (inUse) { resetPending = true; return; }
            reusable.Clear();
            reusable.TrimExcess();
            resetPending = false;
        }
    }
}
