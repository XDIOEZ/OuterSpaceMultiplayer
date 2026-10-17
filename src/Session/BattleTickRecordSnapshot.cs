using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // A native/transport callback can finish or remove a battle while Tick
    // processes it. Keep the previous snapshot-before-callback semantics,
    // but reuse the backing list across active room frames. A reentrant Tick
    // must never clear the outer Tick's snapshot.
    internal sealed class BattleTickRecordSnapshot
    {
        private readonly List<PlayerFleetBattleRecord> reusable =
            new List<PlayerFleetBattleRecord>();
        private bool inUse, resetPending;

        internal List<PlayerFleetBattleRecord> Capture(
            Dictionary<string, PlayerFleetBattleRecord> battles, bool unfinishedOnly)
        {
            if (battles == null) throw new ArgumentNullException(nameof(battles));
            bool primary = !inUse;
            var snapshot = primary ? reusable : new List<PlayerFleetBattleRecord>(battles.Count);
            if (primary) inUse = true;
            try
            {
                // Iterating the concrete Values collection avoids a LINQ
                // iterator and preserves Dictionary insertion/enumeration
                // order from the old Where(...).ToList / Values.ToList calls.
                foreach (PlayerFleetBattleRecord record in battles.Values)
                    if (!unfinishedOnly || record.Stage != PlayerFleetBattleStage.Finished
                        && record.Stage != PlayerFleetBattleStage.Blocked)
                        snapshot.Add(record);
                return snapshot;
            }
            catch
            {
                snapshot.Clear();
                if (primary)
                {
                    inUse = false;
                    if (resetPending)
                    {
                        reusable.Capacity = 0;
                        resetPending = false;
                    }
                }
                throw;
            }
        }

        internal void Release(List<PlayerFleetBattleRecord> snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            snapshot.Clear(); // Do not retain finished battle/asset references.
            if (!ReferenceEquals(snapshot, reusable)) return;
            inUse = false;
            if (resetPending)
            {
                reusable.Capacity = 0;
                resetPending = false;
            }
        }

        internal void Reset()
        {
            // Leave an in-flight snapshot intact if room closure happens
            // synchronously inside a native battle callback.
            if (inUse) { resetPending = true; return; }
            reusable.Clear();
            reusable.Capacity = 0;
            resetPending = false;
        }
    }
}
