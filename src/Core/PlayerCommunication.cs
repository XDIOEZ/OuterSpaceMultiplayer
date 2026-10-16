using System;

namespace FreeWorldMultiplayer
{
    public enum RoomChatDelivery { Sending, Delivered, Failed }
    public sealed class RoomChatMessage
    {
        public string MessageId, ClientMessageId, SenderPlayerId, TargetPlayerId, Text, Error;
        public long DeliverySequence;
        public RoomChatDelivery Delivery;
    }
    public enum PlayerVisitState { Waiting, Approved, Declined, Cancelled, Expired, Consumed }
    public sealed class PlayerVisitRequest
    {
        public string RequestId, RequesterPlayerId, RecipientPlayerId;
        public long RequesterFleetId, RecipientFleetId;
    }
    public sealed class PlayerVisitGrant
    {
        public string RequestId, Token, RequesterPlayerId, RecipientPlayerId;
        public long RequesterFleetId, RecipientFleetId, SourceMotherShipMapId, DestinationMotherShipMapId;
    }
    public sealed class PlayerVisitUpdate
    {
        public PlayerVisitRequest Request;
        public PlayerVisitState State;
        public string Reason;
        public double RemainingSeconds;
        // Included only in the requester's private approval, never room policy or WorldState.
        public PlayerVisitGrant Grant;
    }
    internal sealed class ChatSubmission
    {
        public string ClientMessageId, TargetPlayerId, Text;
        public long Sequence;
    }
    internal sealed class ChatReceipt { public string MessageId; }
    internal sealed class ChatOutcome
    {
        public string ClientMessageId, MessageId, Error;
        public bool Delivered;
    }
    internal sealed class VisitDecision { public string RequestId; public bool Accept; }
    internal sealed class VisitCancellation { public string RequestId; }
    internal sealed class GiftCheckpointRequest { public string TransferId; public long MapId, AuthorityEpoch; }
}
