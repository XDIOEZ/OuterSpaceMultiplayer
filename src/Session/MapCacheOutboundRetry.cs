using System;
using System.Collections.Generic;
using System.IO;

namespace FreeWorldMultiplayer
{
    // These are outbound cache negotiation messages, never map/asset commits.
    // Retain the exact encoded frame after a failed transport enqueue, not a
    // decoded map view or a closure that can retain a large native checkpoint.
    internal enum MapCacheOutboundKind { Offer, CacheRetry, FullTerrainRetry }

    internal sealed class MapCacheOutboundRetry
    {
        internal const double RetrySeconds = 2;
        internal const int MaxPending = 16;

        private sealed class Pending
        {
            internal string Key, ViewId;
            internal MapCacheOutboundKind Kind;
            internal long MapId, Epoch, Revision;
            internal byte[] Bytes;
            internal double Remaining;
            internal bool Sending;
        }

        private readonly Dictionary<string, Pending> pending = new Dictionary<string, Pending>();
        // A first enqueue has not created a deferred Pending yet. Guard it
        // separately so synchronous transport callbacks cannot enqueue the
        // same ViewId a second time. Numeric leases are allocation-free and
        // distinguish a new room's replacement after Clear.
        private readonly Dictionary<string, long> sending = new Dictionary<string, long>();
        private long nextSendLease;
        private readonly VisitTickSnapshot<Pending> retrySnapshot = new VisitTickSnapshot<Pending>();
        internal int Count { get { return pending.Count; } }

        private static string Key(MapCacheOutboundKind kind, string viewId)
        { return ((int)kind).ToString() + ":" + viewId; }

        private bool IsSending(string key, long lease)
        {
            long actual;
            return sending.TryGetValue(key, out actual) && actual == lease;
        }

        internal bool HasPending(MapCacheOutboundKind kind, string viewId)
        { return pending.ContainsKey(Key(kind, viewId)); }

        internal bool TrySend(MapCacheOutboundKind kind, string viewId, long mapId, long epoch,
            long revision, byte[] encoded, Action<byte[]> enqueue)
        {
            if (string.IsNullOrEmpty(viewId) || mapId < 0 || encoded == null || encoded.Length == 0
                || enqueue == null || !Enum.IsDefined(typeof(MapCacheOutboundKind), kind))
                throw new ArgumentException("Invalid outbound map cache request.");
            string key = Key(kind, viewId);
            // A duplicate query does not create a second FIFO slot or bypass
            // the failure backoff. Tick will retry its original encoded packet.
            if (pending.ContainsKey(key) || sending.ContainsKey(key)) return false;
            long lease = unchecked(++nextSendLease);
            sending.Add(key, lease);
            try
            {
                bool accepted = false;
                try { enqueue(encoded); accepted = true; }
                catch (IOException) { }
                catch (InvalidOperationException) { }

                // Disconnect/room teardown may Clear this first send and
                // immediately register a new same-key request. Never report
                // or defer the old one over that replacement lease.
                if (!IsSending(key, lease)) return false;
                if (accepted) return true;
                if (pending.Count >= MaxPending)
                    throw new InvalidDataException("Too many deferred map cache messages.");
                pending.Add(key, new Pending { Key = key, Kind = kind, ViewId = viewId,
                    MapId = mapId, Epoch = epoch, Revision = revision, Bytes = encoded,
                    Remaining = RetrySeconds });
                return false;
            }
            finally
            {
                // The callback may have replaced this key with a newer lease.
                // The original frame's completion cannot unlock that send.
                if (IsSending(key, lease)) sending.Remove(key);
            }
        }

        internal void Advance(double seconds, Func<MapCacheOutboundKind, string, long, long, long, bool> stillCurrent,
            Action<byte[]> enqueue)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
                throw new ArgumentOutOfRangeException(nameof(seconds));
            if (stillCurrent == null || enqueue == null) throw new ArgumentNullException("callback");
            if (pending.Count == 0) return;
            // Capture the same dictionary-order snapshot as Values.ToList, but
            // reuse its storage across congested frames. A synchronous send or
            // policy callback may remove/recreate entries while it is in use.
            List<Pending> snapshot = retrySnapshot.Capture(pending);
            try
            {
                foreach (Pending entry in snapshot)
                {
                    Pending current;
                    if (!pending.TryGetValue(entry.Key, out current) || !ReferenceEquals(current, entry)
                        || entry.Sending) continue;
                    bool valid = stillCurrent(entry.Kind, entry.ViewId, entry.MapId, entry.Epoch, entry.Revision);
                    // Recheck after the callback: a replacement must not be
                    // sent or erased by this old lease, even if it has the key.
                    if (!pending.TryGetValue(entry.Key, out current) || !ReferenceEquals(current, entry)) continue;
                    if (!valid) { pending.Remove(entry.Key); continue; }
                    entry.Remaining = Math.Max(0, entry.Remaining - seconds);
                    if (entry.Remaining > 0) continue;
                    // A transport callback can synchronously reenter Advance.
                    // Keep the exact pending lease reserved until that send
                    // returns, even when the retry timer is already at zero.
                    entry.Sending = true;
                    try
                    {
                        enqueue(entry.Bytes);
                        if (pending.TryGetValue(entry.Key, out current) && ReferenceEquals(current, entry))
                            pending.Remove(entry.Key);
                    }
                    catch (IOException) { entry.Remaining = RetrySeconds; }
                    catch (InvalidOperationException) { entry.Remaining = RetrySeconds; }
                    finally { entry.Sending = false; }
                }
            }
            finally { retrySnapshot.Release(snapshot); }
        }

        internal void Remove(MapCacheOutboundKind kind, string viewId)
        {
            string key = Key(kind, viewId);
            pending.Remove(key);
            sending.Remove(key);
        }
        internal void Clear() { pending.Clear(); sending.Clear(); retrySnapshot.Reset(); }

        // A failed host query may only undo its own reservation. In particular,
        // an inline transport callback must not erase a newer lease for this id.
        internal static bool RemoveIfSame<TKey, TValue>(IDictionary<TKey, TValue> entries, TKey key, TValue expected)
            where TValue : class
        {
            TValue actual;
            if (!entries.TryGetValue(key, out actual) || !ReferenceEquals(actual, expected)) return false;
            return entries.Remove(key);
        }
    }
}
