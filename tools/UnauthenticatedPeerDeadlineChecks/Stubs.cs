using System;
using System.Collections.Generic;
using System.IO;

namespace FreeWorldMultiplayer
{
    internal sealed class FakeTransport
    {
        internal readonly List<string> Disconnected = new List<string>();
        internal readonly Dictionary<string, int> Failures = new Dictionary<string, int>(StringComparer.Ordinal);
        internal readonly HashSet<string> Active = new HashSet<string>(StringComparer.Ordinal);
        internal Action<string> OnDisconnected;
        internal bool AutomaticallyDeliverDisconnected = true;
        internal void Disconnect(string peer, string reason)
        {
            int count;
            if (Failures.TryGetValue(peer, out count) && count > 0)
            {
                Failures[peer] = count - 1;
                throw new IOException("disconnection transport unavailable");
            }
            Active.Remove(peer);
            Disconnected.Add(peer);
            if (AutomaticallyDeliverDisconnected && OnDisconnected != null)
                OnDisconnected(peer);
        }
    }

    public sealed partial class MultiplayerSession
    {
        private bool IsHost = true;
        private readonly Dictionary<string, string> challenges = new Dictionary<string, string>();
        private readonly Dictionary<string, string> rejectedPeers = new Dictionary<string, string>();
        private readonly FakeTransport transport = new FakeTransport();
        private readonly List<string> errors = new List<string>();
        internal MultiplayerSession() { transport.OnDisconnected = PeerLeft; }
        private void Error(string description) { errors.Add(description); }

        internal int Connect(string peer)
        {
            transport.Active.Add(peer);
            if (!unauthenticatedPeerDeadline.Begin(peer))
                transport.Disconnect(peer, "unauthenticated capacity reached");
            return unauthenticatedPeerDeadline.Count;
        }
        internal void Challenge(string peer) { challenges[peer] = "nonce"; }
        internal void Reject(string peer) { rejectedPeers[peer] = "invalid auth"; }
        internal void Authenticate(string peer) { unauthenticatedPeerDeadline.Complete(peer); }
        internal void PeerLeft(string peer) { unauthenticatedPeerDeadline.Forget(peer); }
        internal void Tick(double seconds) { TickUnauthenticatedPeers(seconds); }
        internal void SetHost(bool value) { IsHost = value; }
        internal int PendingCount { get { return unauthenticatedPeerDeadline.Count; } }
        internal bool HasChallenge(string peer) { return challenges.ContainsKey(peer); }
        internal bool HasRejection(string peer) { return rejectedPeers.ContainsKey(peer); }
        internal bool Expired(string peer) { return unauthenticatedPeerDeadline.IsExpired(peer); }
        internal FakeTransport TestTransport { get { return transport; } }
        internal IList<string> TestErrors { get { return errors; } }
    }
}
