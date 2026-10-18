using System;
using System.Collections.Generic;
using System.IO;

namespace FreeWorldMultiplayer
{
    // This is a transport-slot lifecycle guard, NOT an authentication scheme.
    // A peer gets one absolute deadline from Connected; hello/challenge retries
    // never extend it. Only an authenticated welcome leaves the pending set.
    internal sealed class UnauthenticatedPeerDeadline
    {
        internal const int MaxPendingPeers = 32;
        internal const double HandshakeSeconds = 30;
        internal const double DisconnectRetrySeconds = 2;

        private sealed class Pending
        {
            internal double Remaining = HandshakeSeconds;
            internal double RetryRemaining;
            internal bool Expired;
        }

        private readonly Dictionary<string, Pending> pending =
            new Dictionary<string, Pending>(StringComparer.Ordinal);

        internal int Count { get { return pending.Count; } }

        internal bool Begin(string peerId)
        {
            if (String.IsNullOrEmpty(peerId)) return false;
            if (pending.ContainsKey(peerId)) return true;
            if (pending.Count >= MaxPendingPeers) return false;
            pending.Add(peerId, new Pending());
            return true;
        }

        internal bool IsExpired(string peerId)
        {
            Pending record;
            return peerId != null && pending.TryGetValue(peerId, out record) && record.Expired;
        }

        internal void Complete(string peerId)
        {
            if (peerId != null) pending.Remove(peerId);
        }

        internal void Forget(string peerId) { Complete(peerId); }
        internal void Clear() { pending.Clear(); }

        internal int Tick(double seconds, Action<string> disconnect,
            Action<string, Exception> reportDisconnectFailure = null)
        {
            if (Double.IsNaN(seconds) || Double.IsInfinity(seconds) || seconds < 0)
                throw new ArgumentOutOfRangeException(nameof(seconds));
            if (disconnect == null) throw new ArgumentNullException(nameof(disconnect));
            if (pending.Count == 0) return 0;

            List<KeyValuePair<string, Pending>> overdue = null;
            foreach (var entry in pending)
            {
                Pending record = entry.Value;
                if (!record.Expired)
                {
                    record.Remaining -= seconds;
                    // Unity unscaled deltas are floating point. Allow only a
                    // nanosecond of arithmetic rounding at the 30s boundary.
                    if (record.Remaining <= 1e-9) record.Expired = true;
                }
                else record.RetryRemaining -= seconds;

                if (!record.Expired || record.RetryRemaining > 1e-9) continue;
                if (overdue == null) overdue = new List<KeyValuePair<string, Pending>>();
                overdue.Add(entry);
            }

            if (overdue == null) return 0;
            int disconnected = 0;
            foreach (var entry in overdue)
            {
                Pending current;
                if (!pending.TryGetValue(entry.Key, out current) || !ReferenceEquals(current, entry.Value))
                    continue;
                try
                {
                    disconnect(entry.Key);
                    // Transport may synchronously produce a disconnected event;
                    // never delete a reconnected peer sharing the old key.
                    if (pending.TryGetValue(entry.Key, out current) && ReferenceEquals(current, entry.Value))
                        pending.Remove(entry.Key);
                    disconnected++;
                }
                catch (IOException error)
                {
                    entry.Value.RetryRemaining = DisconnectRetrySeconds;
                    if (reportDisconnectFailure != null) reportDisconnectFailure(entry.Key, error);
                }
                catch (InvalidOperationException error)
                {
                    entry.Value.RetryRemaining = DisconnectRetrySeconds;
                    if (reportDisconnectFailure != null) reportDisconnectFailure(entry.Key, error);
                }
            }
            return disconnected;
        }
    }

    public sealed partial class MultiplayerSession
    {
        private readonly UnauthenticatedPeerDeadline unauthenticatedPeerDeadline =
            new UnauthenticatedPeerDeadline();

        private void TickUnauthenticatedPeers(double seconds)
        {
            if (!IsHost) return;
            unauthenticatedPeerDeadline.Tick(seconds,
                peer =>
                {
                    challenges.Remove(peer);
                    transport.Disconnect(peer, "握手超时，未完成玩家身份认证。");
                },
                (peer, error) => Error("清理未认证连接失败，将重试：" + error.Message));
        }
    }
}
