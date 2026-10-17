using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace FreeWorldMultiplayer
{
    public sealed partial class MultiplayerSession
    {
        public event Action<string, long> PlayerFleetInteractionRequested;
        public event Action<string, long> PlayerCommunicationRequested;
        public event Action<RoomChatMessage> ChatChanged;
        private sealed class ChatPending { internal RoomChatMessage Message; internal double Seconds = 10; }
        private sealed class ChatRelay
        {
            internal string Sender, Recipient;
            internal RoomChatMessage Message;
            internal ChatOutcome Outcome;
            internal double Seconds = 10;
        }
        private sealed class CommunicationRate { internal double Tokens = 4, Last; }
        private double communicationTime;
        private long outgoingChatSequence, incomingChatSequence, hostChatSequence;
        private readonly List<RoomChatMessage> chatHistory = new List<RoomChatMessage>();
        private readonly Dictionary<string, ChatPending> pendingChats = new Dictionary<string, ChatPending>();
        private readonly Dictionary<string, ChatRelay> chatRelays = new Dictionary<string, ChatRelay>();
        private readonly Dictionary<string, ChatRelay> chatDeliveries = new Dictionary<string, ChatRelay>();
        private readonly Dictionary<string, long> senderChatSequences = new Dictionary<string, long>();
        private readonly Dictionary<string, CommunicationRate> communicationRates = new Dictionary<string, CommunicationRate>();

        private static bool CommunicationId(string id)
        { return id != null && id.Length == 32 && id.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F'); }
        private static T ReadCommunication<T>(WireMessage message)
        {
            try { return JsonData.Payload<T>(message.Payload); }
            catch (JsonException) { throw new MessageRejectionException("通讯消息格式无效。"); }
            catch (InvalidDataException) { throw new MessageRejectionException("通讯消息内容缺失。"); }
        }
        private void RequireCommunicationRoom()
        {
            if (!IsConnected || World == null || closing || shutdown || !transport.IsRunning)
                throw new InvalidOperationException("房间未连接或正在关闭，无法通讯。");
        }
        private void ValidateCommunicationTarget(string target, long fleetUid)
        {
            RequireCommunicationRoom();
            PlayerRecord player;
            if (target == LocalPlayerId || target == null || !World.Players.TryGetValue(target, out player)
                || !player.Online || fleetUid < 0 || OwnerOfEntity(fleetUid) != target)
                throw new InvalidOperationException("目标舰队所属玩家已离线或舰队归属已经变化。");
        }
        public void RequestPlayerFleetInteraction(string playerId, long fleetUid)
        { ValidateCommunicationTarget(playerId, fleetUid); PlayerFleetInteractionRequested?.Invoke(playerId, fleetUid); }
        public void RequestPlayerCommunication(string playerId, long fleetUid)
        { ValidateCommunicationTarget(playerId, fleetUid); PlayerCommunicationRequested?.Invoke(playerId, fleetUid); }
        private bool ConsumeCommunicationRate(string playerId, string category)
        {
            string key = playerId + ":" + category;
            double capacity = category == "battle" ? 120 : category == "battleFrame" ? 20 : category == "visit" ? 2 : 4;
            double refill = category == "battle" ? 60 : category == "battleFrame" ? 20 : category == "visit" ? 0.1 : 1;
            CommunicationRate rate;
            if (!communicationRates.TryGetValue(key, out rate)) communicationRates[key] = rate = new CommunicationRate { Last = communicationTime, Tokens = capacity };
            rate.Tokens = Math.Min(capacity, rate.Tokens + (communicationTime - rate.Last) * refill); rate.Last = communicationTime;
            if (rate.Tokens < 1) return false;
            rate.Tokens -= 1; return true;
        }
        private bool TrySendCommunication(string playerId, string kind, object payload)
        {
            if (playerId == LocalPlayerId) { HandleLocalCommunication(kind, payload); return true; }
            string peer;
            if (World == null || !World.Players.ContainsKey(playerId) || !World.Players[playerId].Online
                || !playerPeers.TryGetValue(playerId, out peer) || !transport.IsRunning) return false;
            try { Send(peer, kind, payload); return true; }
            catch (IOException) { return false; }
            catch (InvalidOperationException) { return false; }
        }
        private static RoomChatMessage CopyChat(RoomChatMessage message)
        {
            return new RoomChatMessage { MessageId = message.MessageId, ClientMessageId = message.ClientMessageId,
                SenderPlayerId = message.SenderPlayerId, TargetPlayerId = message.TargetPlayerId, Text = message.Text,
                Error = message.Error, Delivery = message.Delivery, DeliverySequence = message.DeliverySequence };
        }
        private void NotifyChat(RoomChatMessage message) { ChatChanged?.Invoke(CopyChat(message)); }
        private void AddChat(RoomChatMessage message)
        {
            if (chatHistory.Count >= 256)
            {
                int index = chatHistory.FindIndex(x => x.Delivery != RoomChatDelivery.Sending);
                if (index < 0) throw new InvalidOperationException("消息队列已满，请等待发送结果。");
                chatHistory.RemoveAt(index);
            }
            chatHistory.Add(message); NotifyChat(message);
        }
        public IList<RoomChatMessage> GetChatMessages(string playerId)
        { return chatHistory.Where(x => x.SenderPlayerId == playerId || x.TargetPlayerId == playerId).Select(CopyChat).ToList(); }
        public void SendPrivateMessage(string targetPlayerId, string text)
        {
            RequireCommunicationRoom();
            if (pendingChats.Count >= 8) throw new InvalidOperationException("尚有消息等待送达，请稍后再发。");
            PlayerRecord target;
            if (targetPlayerId == LocalPlayerId || targetPlayerId == null || !World.Players.TryGetValue(targetPlayerId, out target) || !target.Online)
                throw new InvalidOperationException("对方已离线，消息未发送。");
            text = NormalizeChatText(text);
            string invalid = ValidateChatText(text);
            if (invalid != null) throw new ArgumentException(invalid);
            string id = Guid.NewGuid().ToString("N");
            var message = new RoomChatMessage { ClientMessageId = id, SenderPlayerId = LocalPlayerId, TargetPlayerId = targetPlayerId, Text = text, Delivery = RoomChatDelivery.Sending };
            pendingChats.Add(id, new ChatPending { Message = message }); AddChat(message);
            var submission = new ChatSubmission { ClientMessageId = id, Sequence = ++outgoingChatSequence, TargetPlayerId = targetPlayerId, Text = text };
            try { if (IsHost) HostChat(LocalPlayerId, submission); else Send("host", "chat.submit", submission); }
            catch { CompleteLocalChat(new ChatOutcome { ClientMessageId = id, Error = "消息未能交给房主，未确认送达。" }); throw; }
        }
        private static string NormalizeChatText(string text) => text == null ? null : text.Replace("\r\n", "\n").Replace('\r', '\n');
        private static string ValidateChatText(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return "请先输入消息。";
            if (text.Length > 1024 || Encoding.UTF8.GetByteCount(text) > 4096) return "每条消息最多 1024 个字符。";
            int lines = 1;
            foreach (char ch in text)
            {
                if (ch == '\n' && ++lines > 8) return "每条消息最多 8 行。";
                if (Char.IsControl(ch) && ch != '\n' && ch != '\t') return "消息包含不支持的控制字符。";
            }
            return null;
        }
        private void HostChat(string actor, ChatSubmission request)
        {
            if (request == null || !CommunicationId(request.ClientMessageId) || request.Sequence <= 0) throw new MessageRejectionException("消息编号无效。");
            string key = actor + ":" + request.ClientMessageId;
            ChatRelay prior;
            if (chatRelays.TryGetValue(key, out prior)) { if (prior.Outcome != null) TrySendCommunication(actor, "chat.result", prior.Outcome); return; }
            long sequence;
            if (senderChatSequences.TryGetValue(actor, out sequence) && request.Sequence <= sequence)
            { TrySendCommunication(actor, "chat.result", new ChatOutcome { ClientMessageId = request.ClientMessageId, Error = "消息编号已过期，未重复投递。" }); return; }
            senderChatSequences[actor] = request.Sequence;
            request.Text = NormalizeChatText(request.Text);
            string reason = ValidateChatText(request.Text);
            if (!ConsumeCommunicationRate(actor, "chat")) reason = "发送过快，请稍后再发。";
            PlayerRecord target;
            if (reason == null && (closing || shutdown || !IsConnected)) reason = "房间正在关闭，消息未发送。";
            if (reason == null && (!World.Players.ContainsKey(actor) || !World.Players[actor].Online)) reason = "发信玩家已离线。";
            if (reason == null && (request.TargetPlayerId == actor || request.TargetPlayerId == null || !World.Players.TryGetValue(request.TargetPlayerId, out target) || !target.Online)) reason = "对方已离线，消息未发送。";
            if (reason == null && chatDeliveries.Values.Count(x => x.Sender == actor) >= 8) reason = "尚有消息等待送达，请稍后再发。";
            if (chatRelays.Count >= 512)
            {
                string oldest = chatRelays.FirstOrDefault(x => x.Value.Outcome != null).Key;
                if (oldest != null) chatRelays.Remove(oldest);
                else reason = "房间通讯队列已满，请稍后再发。";
            }
            var relay = new ChatRelay { Sender = actor, Recipient = request.TargetPlayerId,
                Message = new RoomChatMessage { MessageId = Guid.NewGuid().ToString("N"), ClientMessageId = request.ClientMessageId,
                    SenderPlayerId = actor, TargetPlayerId = request.TargetPlayerId, Text = request.Text, Delivery = RoomChatDelivery.Delivered, DeliverySequence = ++hostChatSequence } };
            if (chatRelays.Count < 512) chatRelays[key] = relay;
            if (reason != null) { FinishChatRelay(relay, reason); return; }
            chatDeliveries.Add(relay.Message.MessageId, relay);
            if (!TrySendCommunication(relay.Recipient, "chat.message", relay.Message)) FinishChatRelay(relay, "对方连接已断开，未确认送达。");
        }
        private void FinishChatRelay(ChatRelay relay, string reason)
        {
            if (relay.Outcome != null) return;
            chatDeliveries.Remove(relay.Message.MessageId);
            relay.Outcome = new ChatOutcome { ClientMessageId = relay.Message.ClientMessageId, MessageId = relay.Message.MessageId, Delivered = reason == null, Error = reason };
            TrySendCommunication(relay.Sender, "chat.result", relay.Outcome);
        }
        private void ReceiveChat(RoomChatMessage message)
        {
            if (message == null || !CommunicationId(message.MessageId) || !CommunicationId(message.ClientMessageId)
                || message.TargetPlayerId != LocalPlayerId || message.SenderPlayerId == LocalPlayerId
                || !World.Players.ContainsKey(message.SenderPlayerId) || ValidateChatText(message.Text) != null || message.DeliverySequence <= 0)
                throw new MessageRejectionException("收到无效的房间消息。");
            if (message.DeliverySequence > incomingChatSequence)
            { incomingChatSequence = message.DeliverySequence; message.Delivery = RoomChatDelivery.Delivered; AddChat(CopyChat(message)); }
            var receipt = new ChatReceipt { MessageId = message.MessageId };
            if (IsHost) AcknowledgeChat(LocalPlayerId, receipt); else Send("host", "chat.received", receipt);
        }
        private void AcknowledgeChat(string actor, ChatReceipt receipt)
        {
            ChatRelay relay;
            if (receipt != null && receipt.MessageId != null && chatDeliveries.TryGetValue(receipt.MessageId, out relay) && relay.Recipient == actor)
                FinishChatRelay(relay, null);
        }
        private void CompleteLocalChat(ChatOutcome outcome)
        {
            ChatPending pending;
            if (outcome == null || outcome.ClientMessageId == null || !pendingChats.TryGetValue(outcome.ClientMessageId, out pending)) return;
            pendingChats.Remove(outcome.ClientMessageId);
            pending.Message.MessageId = outcome.MessageId;
            pending.Message.Delivery = outcome.Delivered ? RoomChatDelivery.Delivered : RoomChatDelivery.Failed;
            pending.Message.Error = outcome.Delivered ? null : outcome.Error ?? "消息未确认送达。"; NotifyChat(pending.Message);
        }
        private void HandleLocalCommunication(string kind, object payload)
        {
            if (kind == "chat.message") ReceiveChat((RoomChatMessage)payload);
            else if (kind == "chat.result") CompleteLocalChat((ChatOutcome)payload);
            else if (kind == "visit.update") ReceiveVisit((PlayerVisitUpdate)payload);
            // The host already owns the canonical record; do not replace it with a clone
            // while the transaction that published it still holds the same record.
            else if (kind == "battle.state" && !IsHost) ReceiveBattleState((PlayerFleetBattleRecord)payload);
            else if (kind == "battle.ack") ReceiveBattleAck((PlayerFleetBattleAck)payload);
            else if (kind == "battle.frame") ReceiveBattleFrame((PlayerFleetBattleFrame)payload);
            else if (kind == "battle.stopRequest") ReceiveBattleStop((PlayerFleetBattleReference)payload);
            else if (kind == "gift.flush") ReceiveGiftFlush((GiftCheckpointRequest)payload);
            else if (kind == "gift.resume") ResumeGiftMap((GiftCheckpointRequest)payload);
        }
        private bool HandleHostCommunication(string peer, WireMessage message)
        {
            if (!message.Kind.StartsWith("chat.", StringComparison.Ordinal) && !message.Kind.StartsWith("visit.", StringComparison.Ordinal) && !message.Kind.StartsWith("gift.", StringComparison.Ordinal)) return false;
            string actor;
            if (!peers.TryGetValue(peer, out actor) || message.SessionId != SessionId) throw new MessageRejectionException("通讯所属连接或房间已失效。");
            switch (message.Kind)
            {
                case "chat.submit": HostChat(actor, ReadCommunication<ChatSubmission>(message)); break;
                case "chat.received": AcknowledgeChat(actor, ReadCommunication<ChatReceipt>(message)); break;
                case "visit.request": HostVisitRequest(actor, ReadCommunication<PlayerVisitRequest>(message)); break;
                case "visit.decision": HostVisitDecision(actor, ReadCommunication<VisitDecision>(message)); break;
                case "visit.cancel": HostVisitCancel(actor, ReadCommunication<VisitCancellation>(message)); break;
                case "gift.flushed": ConfirmGiftFlush(actor, ReadCommunication<GiftCheckpointRequest>(message)); break;
                default: throw new MessageRejectionException("不支持的通讯消息。");
            }
            return true;
        }
        private bool HandleClientCommunication(WireMessage message)
        {
            switch (message.Kind)
            {
                case "chat.message": ReceiveChat(ReadCommunication<RoomChatMessage>(message)); return true;
                case "chat.result": CompleteLocalChat(ReadCommunication<ChatOutcome>(message)); return true;
                case "visit.update": ReceiveVisit(ReadCommunication<PlayerVisitUpdate>(message)); return true;
                case "gift.flush": ReceiveGiftFlush(ReadCommunication<GiftCheckpointRequest>(message)); return true;
                case "gift.resume": ResumeGiftMap(ReadCommunication<GiftCheckpointRequest>(message)); return true;
                default: return false;
            }
        }
        private void TickCommunication(double seconds)
        {
            communicationTime += seconds;
            // The real-time room tick runs even when nobody is chatting.
            // Keep snapshot iteration when callbacks can mutate the dictionary,
            // but do not allocate empty Lists on every idle frame.
            if (pendingChats.Count != 0)
                foreach (ChatPending pending in pendingChats.Values.ToList())
                    if ((pending.Seconds -= seconds) <= 0) CompleteLocalChat(new ChatOutcome { ClientMessageId = pending.Message.ClientMessageId, Error = "等待送达回执超时，未确认对方收到。" });
            if (IsHost && chatDeliveries.Count != 0)
                foreach (ChatRelay relay in chatDeliveries.Values.ToList())
                    if ((relay.Seconds -= seconds) <= 0) FinishChatRelay(relay, "对方未确认收到，消息送达超时。");
            TickVisits(seconds);
        }
        private void DisconnectCommunication(string playerId, bool all)
        {
            if (IsHost)
            {
                foreach (ChatRelay relay in chatDeliveries.Values.Where(x => all || x.Sender == playerId || x.Recipient == playerId).ToList())
                    FinishChatRelay(relay, "玩家已断线或房间已关闭，消息未确认送达。");
                if (playerId != null)
                {
                    senderChatSequences.Remove(playerId);
                    foreach (string key in communicationRates.Keys.Where(x => x.StartsWith(playerId + ":", StringComparison.Ordinal)).ToList()) communicationRates.Remove(key);
                }
            }
            if (all) foreach (ChatPending pending in pendingChats.Values.ToList())
                CompleteLocalChat(new ChatOutcome { ClientMessageId = pending.Message.ClientMessageId, Error = "已离开房间，消息未确认送达。" });
            DisconnectVisits(playerId, all);
        }
    }
}
