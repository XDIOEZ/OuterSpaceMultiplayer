using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // A rejected/erroring network peer is never an authoritative update.
    // If an advisory error reply and then Disconnect both fail, quarantine
    // the peer until its transport can be closed; never abort the host Tick.
    internal sealed class RejectedPeerEvictionGate
    {
        internal const double RetrySeconds = 2;
        private sealed class Entry
        {
            internal string Reason;
            internal double RetryAfter;
            internal bool Disconnecting;
        }
        private readonly Dictionary<string, Entry> quarantined =
            new Dictionary<string, Entry>(StringComparer.Ordinal);

        internal int Count { get { return quarantined.Count; } }
        internal bool Contains(string peer)
        { return peer != null && quarantined.ContainsKey(peer); }

        internal bool Track(string peer, string reason)
        {
            if (String.IsNullOrEmpty(peer) || quarantined.ContainsKey(peer)) return false;
            quarantined.Add(peer, new Entry {
                Reason = String.IsNullOrWhiteSpace(reason) ? "异常连接已隔离，请重新加入。" : reason,
                RetryAfter = RetrySeconds
            });
            return true;
        }

        internal void Forget(string peer)
        { if (peer != null) quarantined.Remove(peer); }

        // The entry is kept even if Disconnect returned successfully: a
        // queued Disconnect event, not a request to close, retires the peer.
        // This prevents a late message being accepted before that event.
        internal bool TryDisconnect(string peer,
            Action<string, string> disconnect, Action<string, Exception> reportError)
        {
            if (disconnect == null) throw new ArgumentNullException(nameof(disconnect));
            Entry entry;
            if (peer == null || !quarantined.TryGetValue(peer, out entry) || entry.Disconnecting)
                return false;
            // Set the retry deadline BEFORE calling transport: callbacks may
            // synchronously deliver an event, modify the table or throw.
            entry.RetryAfter = RetrySeconds;
            entry.Disconnecting = true;
            try { disconnect(peer, entry.Reason); return true; }
            catch (Exception ex)
            {
                try { if (reportError != null) reportError(peer, ex); }
                catch (Exception) { } // Diagnostics can never cancel eviction.
                return false;
            }
            finally { entry.Disconnecting = false; }
        }

        internal void Tick(double seconds,
            Action<string, string> disconnect, Action<string, Exception> reportError)
        {
            if (Double.IsNaN(seconds) || Double.IsInfinity(seconds) || seconds < 0)
                throw new ArgumentOutOfRangeException(nameof(seconds));
            if (disconnect == null) throw new ArgumentNullException(nameof(disconnect));
            if (quarantined.Count == 0) return;

            // No callback runs while enumerating the live dictionary. Create
            // a snapshot only when at least one retry is due: a quarantined
            // socket waiting for its close event must not allocate each frame.
            List<KeyValuePair<string, Entry>> due = null;
            foreach (var item in quarantined)
            {
                if (item.Value.Disconnecting) continue;
                item.Value.RetryAfter -= seconds;
                if (item.Value.RetryAfter > 1e-9) continue;
                if (due == null) due = new List<KeyValuePair<string, Entry>>();
                due.Add(item);
            }
            if (due == null) return;
            // A callback may Forget/Track this peer ID or reenter Tick. Do
            // not act on an obsolete generation or retry a just-issued close.
            foreach (var item in due)
            {
                Entry current;
                if (!quarantined.TryGetValue(item.Key, out current) ||
                    !Object.ReferenceEquals(current, item.Value) ||
                    current.Disconnecting || current.RetryAfter > 1e-9) continue;
                TryDisconnect(item.Key, disconnect, reportError);
            }
        }
    }

    public sealed partial class MultiplayerSession
    {
        private readonly RejectedPeerEvictionGate rejectedPeerEvictions =
            new RejectedPeerEvictionGate();

        private void QuarantineRejectedPeer(string peer, string reason)
        {
            if (rejectedPeerEvictions.Track(peer, reason))
                rejectedPeerEvictions.TryDisconnect(peer,
                    (id, why) => transport.Disconnect(id, why),
                    (id, failure) => Error("关闭拒绝连接失败，将重新尝试：" + failure.Message));
        }

        private void TickRejectedPeerEvictions(double seconds)
        {
            if (!IsHost) return;
            rejectedPeerEvictions.Tick(seconds,
                (id, why) => transport.Disconnect(id, why),
                (id, failure) => Error("隔离连接仍未断开，稍后继续重试：" + failure.Message));
        }
    }
}
