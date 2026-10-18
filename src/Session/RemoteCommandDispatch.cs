using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // The host must not leave a command marked "pending at simulator" when
    // the reliable transport rejected the initial execute enqueue.
    internal static class RemoteCommandDispatch
    {
        internal static void EnqueueOrRollback(
            IDictionary<string, GameCommand> pending,
            string key,
            GameCommand snapshot,
            Action send)
        {
            if (pending == null) throw new ArgumentNullException(nameof(pending));
            if (String.IsNullOrEmpty(key)) throw new ArgumentException("Missing command identity.", nameof(key));
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (send == null) throw new ArgumentNullException(nameof(send));

            // Add (not an indexer assignment) rejects accidental replacement
            // of a distinct still-unresolved command with the same key.
            pending.Add(key, snapshot);
            try { send(); }
            catch
            {
                GameCommand current;
                // Synchronous callbacks can resolve/requeue a command while
                // the send call is unwinding. Never delete the newer entry.
                if (pending.TryGetValue(key, out current) &&
                    Object.ReferenceEquals(current, snapshot))
                    pending.Remove(key);
                throw;
            }
        }

        // This enforces only transport ordering, NOT the stronger
        // "map delta durably committed" proof needed to close BUG-01.
        internal static void PublishBeforeReceipt(
            bool applied, Action publishMap, Action sendExecution)
        {
            if (publishMap == null) throw new ArgumentNullException(nameof(publishMap));
            if (sendExecution == null) throw new ArgumentNullException(nameof(sendExecution));
            // A genuine failure has no native effects to prove and must still
            // reach the host if map publishing itself is unavailable.
            // Only an Applied=true claim requires the map-first barrier.
            if (applied) publishMap();
            sendExecution();
        }
    }
}
