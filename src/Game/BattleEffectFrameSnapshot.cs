using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // Battle VFX are removed as they expire, so iterating the live dictionary
    // directly is unsafe. Reuse the frame-local snapshot storage instead of
    // allocating one KeyValuePair[] for every render frame. A nested Update
    // (e.g. from a native despawn callback) gets an independent snapshot.
    internal sealed class BattleEffectFrameSnapshot<TKey, TValue>
    {
        private readonly List<KeyValuePair<TKey, TValue>> reusable = new List<KeyValuePair<TKey, TValue>>();
        private bool inUse, resetPending;

        internal List<KeyValuePair<TKey, TValue>> Capture(Dictionary<TKey, TValue> source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            bool useReusable = !inUse;
            List<KeyValuePair<TKey, TValue>> snapshot = useReusable
                ? reusable : new List<KeyValuePair<TKey, TValue>>(source.Count);
            if (useReusable) inUse = true;
            try
            {
                foreach (KeyValuePair<TKey, TValue> pair in source) snapshot.Add(pair);
                return snapshot;
            }
            catch
            {
                snapshot.Clear();
                if (useReusable) inUse = false;
                throw;
            }
        }

        internal void Release(List<KeyValuePair<TKey, TValue>> snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            snapshot.Clear(); // Release native effect references even on exceptional ticks.
            if (ReferenceEquals(snapshot, reusable))
            {
                inUse = false;
                if (resetPending)
                {
                    reusable.Capacity = 0;
                    resetPending = false;
                }
            }
        }

        // Called only as part of the normal native battle visual cleanup.
        // Releasing the backing array avoids retaining a large one-time burst
        // for the lifetime of the multiplayer room.
        internal void Reset()
        {
            if (inUse) { resetPending = true; return; }
            reusable.Clear();
            reusable.Capacity = 0;
            resetPending = false;
        }
    }
}
