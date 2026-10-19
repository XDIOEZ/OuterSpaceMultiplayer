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
            internal bool Expired, Disconnecting;
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

                if (!record.Expired || record.Disconnecting || record.RetryRemaining > 1e-9) continue;
                if (overdue == null) overdue = new List<KeyValuePair<string, Pending>>();
                overdue.Add(entry);
            }

            if (overdue == null) return 0;
            int closeRequests = 0;
            foreach (var entry in overdue)
            {
                Pending current;
                if (!pending.TryGetValue(entry.Key, out current) || !ReferenceEquals(current, entry.Value))
                    continue;
                // A transport close request can complete before its
                // Disconnected event reaches the session. Keep the expired
                // peer quarantined, and reserve the next retry BEFORE calling
                // transport (whose callbacks may reenter this Tick).
                entry.Value.RetryRemaining = DisconnectRetrySeconds;
                entry.Value.Disconnecting = true;
                try
                {
                    disconnect(entry.Key);
                    // Only the real Disconnected handler (Forget) or successful
                    // authentication (Complete) releases an admission slot.
                    // Returning from Disconnect alone is not a close receipt.
                    closeRequests++;
                }
                catch (IOException error)
                {
                    ReportDisconnectFailure(reportDisconnectFailure, entry.Key, error);
                }
                catch (InvalidOperationException error)
                {
                    ReportDisconnectFailure(reportDisconnectFailure, entry.Key, error);
                }
                catch
                {
                    // Preserve the previous behavior for unexpected transport
                    // faults: propagate, and allow the next Tick to try again.
                    entry.Value.RetryRemaining = 0;
                    throw;
                }
                finally { entry.Value.Disconnecting = false; }
            }
            return closeRequests;
        }

        private static void ReportDisconnectFailure(Action<string, Exception> report,
            string peer, Exception error)
        {
            if (report == null) return;
            // Diagnostics cannot cancel the cleanup of a different peer.
            try { report(peer, error); }
            catch (Exception) { }
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
