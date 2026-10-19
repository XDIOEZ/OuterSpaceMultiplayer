using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FreeWorldMultiplayer
{
    internal sealed class MessageRejectionException : Exception
    { internal MessageRejectionException(string message) : base(message) { } }
    internal sealed class PlayerUpdate
    {
        internal string PlayerId, NativeDeltaJson;
        internal long BaseRevision, Revision;
    }
    internal sealed class PlayerFieldState
    {
        internal string PlayerId;
        internal long PersonalRevision;
    }
    internal sealed class PlayerRecord { internal long PersonalRevision; }
    internal sealed class WorldState
    {
        internal Dictionary<string, PlayerRecord> Players = new Dictionary<string, PlayerRecord>();
    }
    internal sealed class BridgeResult
    {
        internal bool Success = true;
    }
    internal sealed class FakeBridge
    {
        internal int Applied;
        internal bool ShouldFail;
        internal BridgeResult ApplyPlayerFields(PlayerFieldState reset)
        {
            Applied++;
            return new BridgeResult { Success = !ShouldFail };
        }
    }
    internal sealed class FakeTransport
    {
        internal bool IsRunning = true;
        internal readonly List<byte[]> Sent = new List<byte[]>();
        internal readonly List<byte[]> Attempted = new List<byte[]>();
        internal int Failures, IOExceptionFailures;
        internal Action OnSend;
        internal void Send(string peer, byte[] bytes)
        {
            if (peer != "host") throw new Exception("Unexpected personal receiver.");
            Attempted.Add(bytes);
            if (Failures-- > 0)
            {
                if (IOExceptionFailures-- > 0) throw new IOException("synthetic TCP queue full");
                throw new InvalidOperationException("synthetic closed queue");
            }
            Sent.Add(bytes);
            if (OnSend != null) OnSend();
        }
    }
    public sealed partial class MultiplayerSession
    {
        private readonly FakeBridge bridge = new FakeBridge();
        private readonly FakeTransport transport = new FakeTransport();
        private readonly HashSet<string> personalInFlight = new HashSet<string>();
        private WorldState World = new WorldState();
        private string LocalPlayerId = "local", SessionId = "session";
        private bool IsConnected = true, IsHost, closing, Paused;
        private string nativeFault;
        private bool FailEncode, FailFaultNotification;
        internal int EncodeCalls, Locks;
        internal bool IsPending { get { return pendingPersonalOutbound != null; } }
        internal bool Queued { get { return pendingPersonalOutbound != null && pendingPersonalOutbound.Enqueued; } }
        internal int FlightCount { get { return personalInFlight.Count; } }
        internal bool IsFaulted { get { return nativeFault != null && Paused; } }
        internal long Revision { get { return World.Players["local"].PersonalRevision; } }
        internal FakeTransport Network { get { return transport; } }
        internal FakeBridge Native { get { return bridge; } }

        internal MultiplayerSession() { World.Players.Add("local", new PlayerRecord()); }
        internal void SendDelta(PlayerUpdate update) { SendCapturedPersonalUpdate(update); }
        internal void Clock(double seconds) { TickPersonalOutbound(seconds); }
        internal void Ack(PlayerUpdate ack) { ReceivePersonalAcknowledgement(ack); }
        internal void Reset(PlayerFieldState state) { CompletePersonalReset(state); }
        internal void Leave() { ClearPersonalOutbound(); IsConnected = false; }
        internal void Rejoin() { IsConnected = true; }
        internal void ChangeSession(string next) { SessionId = next; }
        internal void FailEncoding() { FailEncode = true; }
        internal void FailStatus() { FailFaultNotification = true; }
        internal void Close() { closing = true; }
        internal void NewRevision(long revision) { World.Players["local"].PersonalRevision = revision; }
        internal string PendingFrameText
        { get { return pendingPersonalOutbound == null || pendingPersonalOutbound.Frame == null ? null :
              Encoding.UTF8.GetString(pendingPersonalOutbound.Frame); } }

        private byte[] EncodeMessage(string kind, object payload, string requestId)
        {
            EncodeCalls++;
            if (FailEncode) throw new FormatException("synthetic encoding failure");
            var update = (PlayerUpdate)payload;
            return Encoding.UTF8.GetBytes(kind + "|" + SessionId + "|" + update.PlayerId + "|" +
                update.BaseRevision + "|" + update.Revision + "|" + update.NativeDeltaJson);
        }
        private void LockNativeFault(string value)
        {
            Locks++;
            nativeFault = value;
            Paused = true;
            if (FailFaultNotification) throw new IOException("UI status failed");
        }
        private static void Require(BridgeResult result, string operation)
        {
            if (result == null || !result.Success)
                throw new InvalidOperationException(operation);
        }
    }
}
