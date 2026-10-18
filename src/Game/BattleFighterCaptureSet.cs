using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // Host fighter checkpoints repeatedly collect the same scene objects.
    // Reuse each frame's HashSet without retaining Unity references between
    // captures. Nested native callbacks must never clear the outer capture.
    internal sealed class BattleFighterCaptureSet<T>
    {
        private readonly HashSet<T> reusable = new HashSet<T>();
        private bool inUse, resetPending;

        internal Lease Rent()
        {
            HashSet<T> items = inUse ? new HashSet<T>() : reusable;
            if (ReferenceEquals(items, reusable)) inUse = true;
            return new Lease(this, items);
        }

        private void Release(HashSet<T> items)
        {
            items.Clear();
            if (!ReferenceEquals(items, reusable)) return;
            inUse = false;
            if (resetPending)
            {
                reusable.TrimExcess();
                resetPending = false;
            }
        }

        internal void Reset()
        {
            if (inUse) { resetPending = true; return; }
            reusable.Clear();
            reusable.TrimExcess();
            resetPending = false;
        }

        internal struct Lease : IDisposable
        {
            private readonly BattleFighterCaptureSet<T> pool;
            internal readonly HashSet<T> Items;

            internal Lease(BattleFighterCaptureSet<T> pool, HashSet<T> items)
            {
                this.pool = pool;
                Items = items;
            }

            public void Dispose() { pool.Release(Items); }
        }
    }
}
