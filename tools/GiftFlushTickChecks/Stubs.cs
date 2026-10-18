using System;
using System.Collections.Generic;

// These fake native/session fields are populated only by tests that exercise
// their corresponding branch; untouched fields intentionally default to zero.
#pragma warning disable CS0649

namespace FreeWorldMultiplayer
{
    internal sealed class GiftCheckpointRequest
    {
        public string TransferId;
        public long MapId, AuthorityEpoch;
    }
    internal sealed class TransferRequest { public long DestinationMapId; }
    internal sealed class TransferRecord
    {
        public string TransferId, GiftRecipientPlayerId;
        public TransferRequest Request;
        public bool Returning;
    }
    internal sealed class MapRecord
    {
        public long MapId, AuthorityEpoch, Revision;
        public string SimulatorPlayerId;
        public bool AwaitingAuthorityReady;
    }
    internal sealed class RoomWorld
    {
        public readonly Dictionary<long, MapRecord> Maps = new Dictionary<long, MapRecord>();
    }
    internal enum MapAccessMode { Frozen, Simulator }
    internal sealed class MapAccess
    {
        public long MapId, AuthorityEpoch, Revision;
        public MapAccessMode Access;
    }
    internal sealed class FakeBridge
    {
        public int Freezes;
        public int Restores, FailedRestores;
        public bool RejectRestore;
        public bool SetMapAuthority(MapAccess access)
        {
            if (access.Access == MapAccessMode.Frozen) Freezes++;
            else
            {
                if (RejectRestore) { FailedRestores++; return false; }
                Restores++;
            }
            return true;
        }
    }
    internal sealed class MessageRejectionException : InvalidOperationException
    {
        internal MessageRejectionException(string message) : base(message) { }
    }

    // This is only the external state and fake transport/native boundary used
    // by the real PlayerGiftCheckpoint.cs partial class linked by the project.
    public sealed partial class MultiplayerSession
    {
        private readonly RoomWorld World = new RoomWorld();
        private readonly FakeBridge bridge = new FakeBridge();
        private readonly Dictionary<string, int> inFlightUpdates = new Dictionary<string, int>();
        private readonly Dictionary<string, int> personalInFlight = new Dictionary<string, int>();
        private string LocalPlayerId = "guest";
        private bool IsHost;
        private bool IsConnected = true;
        internal Func<string, string, object, bool> OnTrySend;
        internal Action<string, string, object> OnSend;
        internal Action OnPublishMaps, OnPublishPersonal;
        internal int MapPublishes, PersonalPublishes;
        private MapRecord EnsureMap(long mapId) { return World.Maps[mapId]; }
        private void EnsureCurrentCheckpoint(MapRecord map, bool forced) { }
        private bool TrySendCommunication(string peer, string kind, object payload)
        {
            if (OnTrySend != null) return OnTrySend(peer, kind, payload);
            OnSend?.Invoke(peer, kind, payload);
            return true;
        }
        private void PublishLocalMaps(bool forced) { MapPublishes++; OnPublishMaps?.Invoke(); }
        private void PublishPersonalUpdates(bool forced) { PersonalPublishes++; OnPublishPersonal?.Invoke(); }
        private void Send(string peer, string kind, object payload) { OnSend?.Invoke(peer, kind, payload); }
        private void Require(bool result, string reason)
        {
            if (!result) throw new InvalidOperationException(reason);
        }
        private MapAccess GetMapAccess(long mapId)
        { return new MapAccess { MapId = mapId, AuthorityEpoch = World.Maps[mapId].AuthorityEpoch, Access = MapAccessMode.Simulator }; }

        internal void AddMap(long id, long epoch = 1, string simulator = "guest")
        {
            World.Maps[id] = new MapRecord { MapId = id, AuthorityEpoch = epoch, SimulatorPlayerId = simulator };
        }
        internal void ChangeMap(long id, long epoch, string simulator)
        {
            World.Maps[id].AuthorityEpoch = epoch;
            World.Maps[id].SimulatorPlayerId = simulator;
        }
        internal void RemoveMap(long id) { World.Maps.Remove(id); }
        internal void Enqueue(GiftCheckpointRequest request)
        {
            giftFlushes[request.MapId] = request;
            giftPausedMaps.Add(request.MapId);
            pausedGiftRequests[request.MapId] = request;
        }
        internal void Receive(GiftCheckpointRequest request) { ReceiveGiftFlush(request); }
        internal void Resume(GiftCheckpointRequest request) { ResumeGiftMap(request); }
        internal void Tick() { TickGiftFlushes(); }
        internal void Host() { IsHost = true; LocalPlayerId = "host"; }
        internal string BeginGift(string transferId, long mapId)
        {
            return PrepareGiftArrival(new TransferRecord
            {
                TransferId = transferId,
                GiftRecipientPlayerId = "guest",
                Request = new TransferRequest { DestinationMapId = mapId }
            });
        }
        internal void CancelGift(string transferId) { ClearGiftBarrier(transferId); }
        internal void RetryResumes(double seconds) { TickGiftResumes(seconds); }
        internal int PendingResumeCount { get { return pendingGiftResumes.Count; } }
        internal int BarrierCount { get { return giftBarriers.Count; } }
        internal int FrozenRequestCount { get { return pausedGiftRequests.Count; } }
        internal void RejectNativeRestore(bool value) { bridge.RejectRestore = value; }
        internal int RestoreCount { get { return bridge.Restores; } }
        internal int FailedRestoreCount { get { return bridge.FailedRestores; } }
        internal int PendingCount { get { return giftFlushes.Count; } }
        internal bool IsPaused(long id) { return giftPausedMaps.Contains(id); }
        internal int FreezeCount { get { return bridge.Freezes; } }
        internal void HoldFlight() { inFlightUpdates["pending"] = 1; }
        internal void HoldPersonal() { personalInFlight["pending"] = 1; }
        internal void ClearFlights() { inFlightUpdates.Clear(); personalInFlight.Clear(); }
    }
}
