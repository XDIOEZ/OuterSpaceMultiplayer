using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // An already-validated fighter frame needs its four-part replica IDs
    // only until missing native shells have been removed. Reuse the backing
    // HashSet buckets across checkpoints instead of rebuilding them each
    // time the same fighters report telemetry.
    internal sealed class BattleFighterReplicaPresenceBuffer<TFighter>
    {
        private readonly Func<TFighter, BattleFighterReplicaIdentity> identityOf;
        private readonly HashSet<BattleFighterReplicaIdentity> reusable =
            new HashSet<BattleFighterReplicaIdentity>();
        private bool inUse, resetPending;

        internal BattleFighterReplicaPresenceBuffer(
            Func<TFighter, BattleFighterReplicaIdentity> identityOf)
        {
            this.identityOf = identityOf ?? throw new ArgumentNullException(nameof(identityOf));
        }

        internal HashSet<BattleFighterReplicaIdentity> Capture(List<TFighter> fighters)
        {
            if (fighters == null) throw new ArgumentNullException(nameof(fighters));
            // Despawn callbacks may synchronously apply another checkpoint.
            // Never let a nested capture overwrite the outer frame's IDs.
            var present = inUse
                ? new HashSet<BattleFighterReplicaIdentity>()
                : reusable;
            if (ReferenceEquals(present, reusable)) inUse = true;
            try
            {
                // List<T>.Enumerator is a struct, with the same mutation
                // detection and enumeration order as the original foreach.
                foreach (TFighter fighter in fighters)
                    present.Add(identityOf(fighter));
                return present;
            }
            catch
            {
                Release(present);
                throw;
            }
        }

        internal void Release(HashSet<BattleFighterReplicaIdentity> present)
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

        // End of battle/room: allow a previous high-density fighter burst to
        // return its managed backing storage. Defer while native cleanup is
        // still using the current frame's set.
        internal void Reset()
        {
            if (inUse) { resetPending = true; return; }
            reusable.Clear();
            reusable.TrimExcess();
            resetPending = false;
        }
    }
}
