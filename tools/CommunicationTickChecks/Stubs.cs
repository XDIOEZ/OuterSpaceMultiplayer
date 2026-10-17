using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace FreeWorldMultiplayer
{
    internal sealed class MessageRejectionException : InvalidOperationException
    {
        internal MessageRejectionException(string message) : base(message) { }
    }
    internal sealed class WireMessage
    {
        internal string Kind, SessionId;
        internal object Payload;
    }
    internal static class JsonData
    {
        internal static T Payload<T>(object value) { return (T)value; }
        internal static T Clone<T>(T value)
        {
            return value == null ? default(T) : JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value));
        }
    }
    internal sealed class TransportStub { internal bool IsRunning = true; }
    public sealed class PlayerRecord
    {
        public string PlayerId;
        public bool Online;
        public long FleetId, MotherShipMapId;
    }
    public sealed class WorldState
    {
        public readonly Dictionary<string, PlayerRecord> Players = new Dictionary<string, PlayerRecord>();
    }
    public sealed class BridgeResult
    {
        public bool Success = true;
        public string Error;
    }
    public sealed class GameBridge
    {
        public BridgeResult ValidatePlayerFleetContact(string source, long sourceFleet, string destination, long targetFleet)
        { return new BridgeResult(); }
    }
    public sealed class TransferRequest
    {
        public string VisitToken, TransferId, NativeManifestJson;
        public long SourceMapId, DestinationMapId, SourceFleetId = -1, DestinationFleetId = -1;
        public readonly List<long> HeroIds = new List<long>();
    }
    public sealed partial class MultiplayerSession
    {
        public bool IsHost = true, IsConnected = true;
        public string LocalPlayerId = "host";
        public string SessionId = "session";
        public WorldState World = new WorldState();
        private readonly TransportStub transport = new TransportStub();
        private bool closing, shutdown;
        private readonly GameBridge bridge = new GameBridge();
        private readonly Dictionary<string, string> peers = new Dictionary<string, string>();
        private readonly Dictionary<string, string> playerPeers = new Dictionary<string, string>();
        public readonly List<string> SentKinds = new List<string>();
        public MultiplayerSession()
        {
            World.Players.Add("host", new PlayerRecord { PlayerId = "host", FleetId = 100, MotherShipMapId = 110, Online = true });
            World.Players.Add("guest", new PlayerRecord { PlayerId = "guest", FleetId = 200, MotherShipMapId = 210, Online = true });
            playerPeers.Add("guest", "guest-peer");
            peers.Add("guest-peer", "guest");
        }
        private void Send(string target, string kind, object payload) { SentKinds.Add(kind); }
        private string OwnerOfEntity(long uid) { return uid == 100 ? "host" : uid == 200 ? "guest" : null; }
        private string OwnerOfMap(long mapId) { return mapId == 110 ? "host" : mapId == 210 ? "guest" : null; }
        private bool IsPlayerFleetBattleLocked(long uid) { return false; }
        private void ReceiveBattleState(PlayerFleetBattleRecord record) { }
        private void ReceiveBattleAck(PlayerFleetBattleAck ack) { }
        private void ReceiveBattleFrame(PlayerFleetBattleFrame frame) { }
        private void ReceiveBattleStop(PlayerFleetBattleReference reference) { }
        private void ReceiveGiftFlush(GiftCheckpointRequest request) { }
        private void ResumeGiftMap(GiftCheckpointRequest request) { }
        private void ConfirmGiftFlush(string player, GiftCheckpointRequest request) { }

        // Test adapters are declared in this fake boundary, while TickCommunication
        // and TickVisits execute the linked, unmodified production method bodies.
        public void TestTick(double seconds) { TickCommunication(seconds); }
        public double TestClock { get { return communicationTime; } }
        public int PendingCount { get { return pendingChats.Count; } }
        public int DeliveryCount { get { return chatDeliveries.Count; } }
        public void AddPending(string id, double seconds)
        {
            var message = new RoomChatMessage { ClientMessageId = id, SenderPlayerId = "host", TargetPlayerId = "guest", Delivery = RoomChatDelivery.Sending };
            pendingChats.Add(id, new ChatPending { Message = message, Seconds = seconds });
            chatHistory.Add(message);
        }
        public double PendingSeconds(string id) { return pendingChats[id].Seconds; }
        public RoomChatMessage ChatById(string id)
        { return chatHistory.Find(m => m.ClientMessageId == id); }
        public void AddDelivery(string id, double seconds)
        {
            var relay = new ChatRelay
            {
                Sender = "guest", Recipient = "host", Seconds = seconds,
                Message = new RoomChatMessage { ClientMessageId = "client-" + id, MessageId = id }
            };
            chatDeliveries.Add(id, relay);
            chatRelays.Add("guest:client-" + id, relay);
        }
        public void AddLocalVisit(string id, double seconds)
        {
            localVisits.Add(id, new PlayerVisitUpdate
            {
                Request = new PlayerVisitRequest { RequestId = id, RequesterPlayerId = "host", RecipientPlayerId = "guest" },
                State = PlayerVisitState.Waiting, RemainingSeconds = seconds
            });
        }
        public PlayerVisitUpdate LocalVisit(string id) { return localVisits[id]; }
        public void AddHostVisit(string id, double seconds)
        {
            hostVisits.Add(id, new HostVisit
            {
                Request = new PlayerVisitRequest { RequestId = Guid.NewGuid().ToString("N"), RequesterPlayerId = "guest", RecipientPlayerId = "host" },
                State = PlayerVisitState.Waiting, Seconds = seconds
            });
        }
        public PlayerVisitState HostVisitStatus(string id) { return hostVisits[id].State; }
        public double HostVisitSeconds(string id) { return hostVisits[id].Seconds; }
        public void AddSettledHistory(int localCount, int hostCount)
        {
            for (int i = 0; i < localCount; i++)
                localVisits.Add("local-history-" + i, new PlayerVisitUpdate
                {
                    Request = new PlayerVisitRequest { RequestId = Guid.NewGuid().ToString("N") },
                    State = PlayerVisitState.Declined, RemainingSeconds = 50
                });
            for (int i = 0; i < hostCount; i++)
                hostVisits.Add("host-history-" + i, new HostVisit
                {
                    Request = new PlayerVisitRequest { RequestId = Guid.NewGuid().ToString("N") },
                    State = PlayerVisitState.Consumed, Seconds = 0
                });
        }
    }
}
