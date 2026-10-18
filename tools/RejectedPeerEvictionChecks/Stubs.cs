using System;
using System.Collections.Generic;
using System.IO;

namespace FreeWorldMultiplayer
{
    internal sealed class FakeTransport
    {
        internal readonly Dictionary<string, int> CloseFailures =
            new Dictionary<string, int>(StringComparer.Ordinal);
        internal readonly List<string> Attempts = new List<string>();
        internal readonly HashSet<string> Closed = new HashSet<string>(StringComparer.Ordinal);
        internal Action<string> OnDisconnect;

        internal void Disconnect(string peer, string reason)
        {
            Attempts.Add(peer);
            if (OnDisconnect != null) OnDisconnect(peer);
            int remaining;
            if (CloseFailures.TryGetValue(peer, out remaining) && remaining > 0)
            {
                CloseFailures[peer] = remaining - 1;
                throw new IOException("synthetic failed close");
            }
            Closed.Add(peer);
        }
    }

    public sealed partial class MultiplayerSession
    {
        private bool IsHost = true;
        private readonly FakeTransport transport = new FakeTransport();
        internal readonly List<string> Errors = new List<string>();
        internal bool ThrowFromError;

        private void Error(string reason)
        {
            Errors.Add(reason);
            if (ThrowFromError) throw new InvalidOperationException("status sink failed");
        }
        internal int QuarantinedCount { get { return rejectedPeerEvictions.Count; } }
        internal bool IsQuarantined(string peer) { return rejectedPeerEvictions.Contains(peer); }
        internal FakeTransport Fake { get { return transport; } }
        internal void Rejected(string peer, string reason)
        { QuarantineRejectedPeer(peer, reason); }
        internal void Advance(double seconds) { TickRejectedPeerEvictions(seconds); }
        internal void Left(string peer) { rejectedPeerEvictions.Forget(peer); }
        internal void Host(bool value) { IsHost = value; }

        // Mimic only the actual Tick catch's advisory branch; production
        // OptionalPeerNotice and eviction gate are both compiled into test.
        internal void ReportRejectedMessage(string peer, bool failNotice)
        {
            OptionalPeerNoticeOutcome notice = OptionalPeerNotice.SendOrDisconnect(
                () => { if (failNotice) throw new IOException("queue exhausted"); },
                () => transport.Disconnect(peer, "错误提示无法发送"));
            if (notice != OptionalPeerNoticeOutcome.Sent)
                rejectedPeerEvictions.Track(peer, "错误提示无法发送");
        }
    }
}
