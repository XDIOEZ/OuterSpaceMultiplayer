using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    internal sealed class GalaxySnapshot
    {
        internal bool IsFullBaseline;
        internal long Revision;
    }

    internal sealed class FakeGalaxyBridge
    {
        internal readonly HashSet<string> NeedFull = new HashSet<string>(StringComparer.Ordinal);
        internal readonly Dictionary<string, GalaxySnapshot> Unconfirmed =
            new Dictionary<string, GalaxySnapshot>(StringComparer.Ordinal);
        internal int ResetCalls, CaptureCalls, ConfirmCalls;
        internal bool FailConfirmation;

        internal void ResetGalaxyRecipient(string id)
        {
            ResetCalls++;
            NeedFull.Add(id);
            Unconfirmed.Remove(id);
        }

        internal GalaxySnapshot CaptureGalaxy(string id)
        {
            CaptureCalls++;
            GalaxySnapshot pending;
            if (Unconfirmed.TryGetValue(id, out pending)) return pending;
            pending = new GalaxySnapshot { IsFullBaseline = NeedFull.Contains(id), Revision = CaptureCalls };
            Unconfirmed.Add(id, pending);
            return pending;
        }

        internal void ConfirmGalaxySent(string id)
        {
            if (FailConfirmation) throw new InvalidOperationException("native confirmation failure");
            if (!Unconfirmed.ContainsKey(id)) throw new InvalidOperationException("no pending baseline");
            ConfirmCalls++;
            NeedFull.Remove(id);
            Unconfirmed.Remove(id);
        }
    }

    public sealed partial class MultiplayerSession
    {
        private readonly FakeGalaxyBridge bridge = new FakeGalaxyBridge();
        internal Action<string, string, GalaxySnapshot> OnSend;
        private void Send(string peer, string kind, object payload)
        {
            if (OnSend != null) OnSend(peer, kind, (GalaxySnapshot)payload);
        }
        internal FakeGalaxyBridge TestBridge { get { return bridge; } }
        internal GalaxyResetRequestGate TestGate { get { return galaxyResetRequests; } }
        internal void SendGalaxy(string peer, string playerId) { SendAndConfirmGalaxy(peer, playerId); }
        internal void AskReset(string playerId)
        { galaxyResetRequests.RequestReset(playerId, bridge.ResetGalaxyRecipient); }
        internal void PumpResets(double seconds)
        { galaxyResetRequests.Advance(seconds, bridge.ResetGalaxyRecipient); }
    }
}
