using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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
        }

        private readonly Dictionary<string, Pending> pending = new Dictionary<string, Pending>();
        internal int Count { get { return pending.Count; } }

        private static string Key(MapCacheOutboundKind kind, string viewId)
        { return ((int)kind).ToString() + ":" + viewId; }

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
            if (pending.ContainsKey(key)) return false;
            try { enqueue(encoded); return true; }
            catch (IOException) { }
            catch (InvalidOperationException) { }

            if (pending.Count >= MaxPending)
                throw new InvalidDataException("Too many deferred map cache messages.");
            pending.Add(key, new Pending { Key = key, Kind = kind, ViewId = viewId,
                MapId = mapId, Epoch = epoch, Revision = revision, Bytes = encoded,
                Remaining = RetrySeconds });
            return false;
        }

        internal void Advance(double seconds, Func<MapCacheOutboundKind, string, long, long, long, bool> stillCurrent,
            Action<byte[]> enqueue)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
                throw new ArgumentOutOfRangeException(nameof(seconds));
            if (stillCurrent == null || enqueue == null) throw new ArgumentNullException("callback");
            if (pending.Count == 0) return;
            foreach (Pending entry in pending.Values.ToList())
            {
                Pending current;
                if (!pending.TryGetValue(entry.Key, out current) || !ReferenceEquals(current, entry)) continue;
                if (!stillCurrent(entry.Kind, entry.ViewId, entry.MapId, entry.Epoch, entry.Revision))
                { pending.Remove(entry.Key); continue; }
                entry.Remaining = Math.Max(0, entry.Remaining - seconds);
                if (entry.Remaining > 0) continue;
                try
                {
                    enqueue(entry.Bytes);
                    if (pending.TryGetValue(entry.Key, out current) && ReferenceEquals(current, entry))
                        pending.Remove(entry.Key);
                }
                catch (IOException) { entry.Remaining = RetrySeconds; }
                catch (InvalidOperationException) { entry.Remaining = RetrySeconds; }
            }
        }

        internal void Remove(MapCacheOutboundKind kind, string viewId)
        { pending.Remove(Key(kind, viewId)); }
        internal void Clear() { pending.Clear(); }

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
