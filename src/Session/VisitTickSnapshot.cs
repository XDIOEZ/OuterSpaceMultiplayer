using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // Active visit/consent, chat and gift ticks can invoke callbacks that
    // mutate their dictionaries. Retain the original Values.ToList snapshot
    // semantics without allocating a fresh List on every pending tick.
    internal sealed class VisitTickSnapshot<T> where T : class
    {
        private readonly List<T> reusable = new List<T>();
        private bool inUse, resetPending;

        internal List<T> Capture(Dictionary<string, T> visits)
        {
            if (visits == null) throw new ArgumentNullException(nameof(visits));
            return CaptureDictionary(visits);
        }

        // Map-keyed gift checkpoints use long keys, while visit/chat records
        // use string keys. Only the value snapshot matters to the caller.
        internal List<T> CaptureDictionary<TKey>(Dictionary<TKey, T> visits)
        {
            if (visits == null) throw new ArgumentNullException(nameof(visits));
            bool useReusable = !inUse;
            List<T> snapshot = useReusable ? reusable : new List<T>(visits.Count);
            if (useReusable) inUse = true;
            try
            {
                // Dictionary.ValueCollection has a value-type enumerator.
                // Preserve its order and snapshot all records, not only
                // pending ones: callbacks must not extend the current tick.
                foreach (T visit in visits.Values) snapshot.Add(visit);
                return snapshot;
            }
            catch
            {
                Release(snapshot);
                throw;
            }
        }

        internal void Release(List<T> snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            snapshot.Clear(); // Do not retain player/consent references.
            if (!ReferenceEquals(snapshot, reusable)) return;
            inUse = false;
            if (resetPending)
            {
                reusable.Capacity = 0;
                resetPending = false;
            }
        }

        // Leave-room callbacks may fire while an active snapshot is leased.
        // Defer freeing the backing array until that tick releases it.
        internal void Reset()
        {
            if (inUse) { resetPending = true; return; }
            reusable.Clear();
            reusable.Capacity = 0;
            resetPending = false;
        }
    }
}
