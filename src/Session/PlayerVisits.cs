using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace FreeWorldMultiplayer
{
    public sealed partial class MultiplayerSession
    {
        public event Action<PlayerVisitUpdate> VisitChanged;
        private sealed class HostVisit
        {
            internal PlayerVisitRequest Request;
            internal PlayerVisitState State;
            internal PlayerVisitGrant Grant;
            internal double Seconds = 60;
            internal string TransferId;
        }
        private readonly Dictionary<string, HostVisit> hostVisits = new Dictionary<string, HostVisit>();
        private readonly Dictionary<string, PlayerVisitUpdate> localVisits = new Dictionary<string, PlayerVisitUpdate>();
        private readonly VisitTickSnapshot<HostVisit> hostVisitTickSnapshot = new VisitTickSnapshot<HostVisit>();
        private readonly VisitTickSnapshot<PlayerVisitUpdate> localVisitTickSnapshot = new VisitTickSnapshot<PlayerVisitUpdate>();
        public IList<PlayerVisitUpdate> GetPlayerVisits(string targetPlayerId)
        {
            return localVisits.Values.Where(x => x.Request.RequesterPlayerId == targetPlayerId || x.Request.RecipientPlayerId == targetPlayerId)
                .Select(JsonData.Clone).ToList();
        }
        public void RequestPlayerVisit(string targetPlayerId, long fleetUid)
        {
            ValidateCommunicationTarget(targetPlayerId, fleetUid);
            if (localVisits.Values.Any(x => x.Request.RequesterPlayerId == LocalPlayerId && (x.State == PlayerVisitState.Waiting || x.State == PlayerVisitState.Approved)))
                throw new InvalidOperationException("已有参观申请或赠送授权，请先完成或取消。");
            var request = new PlayerVisitRequest { RequestId = Guid.NewGuid().ToString("N"), RequesterPlayerId = LocalPlayerId,
                RecipientPlayerId = targetPlayerId, RequesterFleetId = World.Players[LocalPlayerId].FleetId, RecipientFleetId = fleetUid };
            ReceiveVisit(new PlayerVisitUpdate { Request = request, State = PlayerVisitState.Waiting, RemainingSeconds = 60, Reason = "申请已发送，等待对方同意。" });
            try { if (IsHost) HostVisitRequest(LocalPlayerId, request); else Send("host", "visit.request", request); }
            catch { ReceiveVisit(new PlayerVisitUpdate { Request = request, State = PlayerVisitState.Cancelled, Reason = "申请未能交给房主。" }); throw; }
        }
        public void DecidePlayerVisit(string requestId, bool accept)
        {
            RequireCommunicationRoom();
            var decision = new VisitDecision { RequestId = requestId, Accept = accept };
            if (IsHost) HostVisitDecision(LocalPlayerId, decision); else Send("host", "visit.decision", decision);
        }
        public void CancelPlayerVisit(string requestId)
        {
            RequireCommunicationRoom();
            var cancellation = new VisitCancellation { RequestId = requestId };
            if (IsHost) HostVisitCancel(LocalPlayerId, cancellation); else Send("host", "visit.cancel", cancellation);
        }
        private bool VisitParticipantsOnline(HostVisit visit)
        {
            PlayerRecord from, to;
            return World != null && World.Players.TryGetValue(visit.Request.RequesterPlayerId, out from) && from.Online
                && World.Players.TryGetValue(visit.Request.RecipientPlayerId, out to) && to.Online
                && OwnerOfEntity(visit.Request.RequesterFleetId) == from.PlayerId && OwnerOfEntity(visit.Request.RecipientFleetId) == to.PlayerId
                && !IsPlayerFleetBattleLocked(visit.Request.RequesterFleetId) && !IsPlayerFleetBattleLocked(visit.Request.RecipientFleetId)
                && !IsPlayerFleetBattleLocked(from.FleetId) && !IsPlayerFleetBattleLocked(to.FleetId);
        }
        private void HostVisitRequest(string actor, PlayerVisitRequest request)
        {
            if (request == null || !CommunicationId(request.RequestId)) throw new MessageRejectionException("参观申请编号无效。");
            HostVisit existing;
            if (hostVisits.TryGetValue(request.RequestId, out existing))
            {
                if (existing.Request.RequesterPlayerId != actor) throw new MessageRejectionException("参观申请不属于当前玩家。");
                SendVisitUpdate(existing, "参观申请状态未改变。"); return;
            }
            PlayerRecord sender, target;
            if (!World.Players.TryGetValue(actor, out sender) || !sender.Online)
                throw new MessageRejectionException("参观双方须在当前房间在线。");
            if (request.RecipientPlayerId == actor || request.RecipientPlayerId == null || !World.Players.TryGetValue(request.RecipientPlayerId, out target) || !target.Online)
            {
                request = JsonData.Clone(request); request.RequesterPlayerId = actor;
                TrySendCommunication(actor, "visit.update", new PlayerVisitUpdate { Request = request, State = PlayerVisitState.Cancelled, Reason = "对方已离线，申请未发送。" }); return;
            }
            request = JsonData.Clone(request); request.RequesterPlayerId = actor; request.RequesterFleetId = sender.FleetId;
            var visit = new HostVisit { Request = request, State = PlayerVisitState.Waiting };
            BridgeResult contact = bridge.ValidatePlayerFleetContact(actor, sender.FleetId, target.PlayerId, request.RecipientFleetId);
            string reason = closing || shutdown ? "房间正在关闭，申请未发送。" : !VisitParticipantsOnline(visit) ? "目标舰队归属已变化。"
                : contact == null || !contact.Success ? "当前舰队无法发起参观：" + (contact == null ? "接触校验未返回。" : contact.Error)
                : !ConsumeCommunicationRate(actor, "visit") ? "参观申请过于频繁，请稍后重试。"
                : hostVisits.Values.Any(x => x.Request.RequesterPlayerId == actor && (x.State == PlayerVisitState.Waiting || x.State == PlayerVisitState.Approved)) ? "已有未完成的参观申请。"
                : hostVisits.Values.Count(x => x.Request.RecipientPlayerId == target.PlayerId && x.State == PlayerVisitState.Waiting) >= 4 ? "对方待处理的参观申请已满。" : null;
            if (hostVisits.Count >= 128)
            {
                string old = hostVisits.FirstOrDefault(x => x.Value.State != PlayerVisitState.Waiting && x.Value.State != PlayerVisitState.Approved).Key;
                if (old != null) hostVisits.Remove(old); else reason = "房间参观申请队列已满。";
            }
            if (reason != null) { visit.State = PlayerVisitState.Cancelled; SendVisitUpdate(visit, reason); return; }
            hostVisits.Add(request.RequestId, visit);
            SendVisitUpdate(visit, "等待对方同意；同意后仅可从申请人母舰向对方母舰赠送物资。");
        }
        private void HostVisitDecision(string actor, VisitDecision decision)
        {
            HostVisit visit;
            if (decision == null || decision.RequestId == null || !hostVisits.TryGetValue(decision.RequestId, out visit)
                || visit.Request.RecipientPlayerId != actor) throw new MessageRejectionException("该参观申请不属于当前收件玩家。");
            if (visit.State != PlayerVisitState.Waiting) { SendVisitUpdate(visit, "该申请已经处理或失效。"); return; }
            if (closing || !VisitParticipantsOnline(visit) || visit.Seconds <= 0)
            { EndVisit(visit, PlayerVisitState.Expired, "申请已过期或对方已离线。"); return; }
            if (!decision.Accept) { EndVisit(visit, PlayerVisitState.Declined, "对方已拒绝参观申请。"); return; }
            BridgeResult contact = bridge.ValidatePlayerFleetContact(visit.Request.RequesterPlayerId, visit.Request.RequesterFleetId,
                visit.Request.RecipientPlayerId, visit.Request.RecipientFleetId);
            if (contact == null || !contact.Success) { EndVisit(visit, PlayerVisitState.Cancelled, "双方舰队已离开接触星系或进入战斗。"); return; }
            PlayerRecord source = World.Players[visit.Request.RequesterPlayerId], destination = World.Players[visit.Request.RecipientPlayerId];
            BridgeResult mothersContact = bridge.ValidatePlayerFleetContact(source.PlayerId, source.FleetId, destination.PlayerId, destination.FleetId);
            if (mothersContact == null || !mothersContact.Success)
            { EndVisit(visit, PlayerVisitState.Cancelled, "双方母舰不在同一接触范围内，不能直接赠送物资。"); return; }
            if (source.MotherShipMapId < 0 || destination.MotherShipMapId < 0 || source.MotherShipMapId == destination.MotherShipMapId
                || OwnerOfMap(source.MotherShipMapId) != source.PlayerId || OwnerOfMap(destination.MotherShipMapId) != destination.PlayerId)
                throw new InvalidOperationException("参观双方的独立母舰归属不完整，未签发赠送授权。");
            visit.State = PlayerVisitState.Approved; visit.Seconds = 300;
            visit.Grant = new PlayerVisitGrant { RequestId = visit.Request.RequestId, Token = Guid.NewGuid().ToString("N"),
                RequesterPlayerId = source.PlayerId, RecipientPlayerId = destination.PlayerId,
                RequesterFleetId = source.FleetId, RecipientFleetId = destination.FleetId,
                SourceMotherShipMapId = source.MotherShipMapId, DestinationMotherShipMapId = destination.MotherShipMapId };
            SendVisitUpdate(visit, "对方已同意；5 分钟内可提交一次单向物资赠送。");
        }
        private void HostVisitCancel(string actor, VisitCancellation cancellation)
        {
            HostVisit visit;
            if (cancellation == null || cancellation.RequestId == null || !hostVisits.TryGetValue(cancellation.RequestId, out visit)
                || (visit.Request.RequesterPlayerId != actor && visit.Request.RecipientPlayerId != actor))
                throw new MessageRejectionException("参观申请不存在或不属于当前玩家。");
            if (visit.State == PlayerVisitState.Waiting || visit.State == PlayerVisitState.Approved)
                EndVisit(visit, PlayerVisitState.Cancelled, "参观申请或未使用的赠送授权已取消。");
        }
        private void SendVisitUpdate(HostVisit visit, string reason)
        {
            foreach (string recipient in new[] { visit.Request.RequesterPlayerId, visit.Request.RecipientPlayerId })
                TrySendCommunication(recipient, "visit.update", new PlayerVisitUpdate { Request = JsonData.Clone(visit.Request), State = visit.State,
                    RemainingSeconds = visit.Seconds, Reason = reason,
                    Grant = recipient == visit.Request.RequesterPlayerId && visit.State == PlayerVisitState.Approved ? JsonData.Clone(visit.Grant) : null });
        }
        private void EndVisit(HostVisit visit, PlayerVisitState state, string reason)
        { visit.State = state; visit.Grant = null; visit.Seconds = 0; SendVisitUpdate(visit, reason); }
        private void ReceiveVisit(PlayerVisitUpdate update)
        {
            if (update == null || update.Request == null || !CommunicationId(update.Request.RequestId)
                || update.Request.RequesterPlayerId != LocalPlayerId && update.Request.RecipientPlayerId != LocalPlayerId)
                throw new MessageRejectionException("参观状态目标无效。");
            PlayerVisitUpdate old;
            bool changed = !localVisits.TryGetValue(update.Request.RequestId, out old) || old.State != update.State;
            if (localVisits.Count >= 64 && !localVisits.ContainsKey(update.Request.RequestId))
            {
                string remove = localVisits.FirstOrDefault(x => x.Value.State != PlayerVisitState.Waiting && x.Value.State != PlayerVisitState.Approved).Key;
                if (remove != null) localVisits.Remove(remove); else throw new MessageRejectionException("本机参观通知队列已满。");
            }
            localVisits[update.Request.RequestId] = JsonData.Clone(update);
            if (changed) VisitChanged?.Invoke(JsonData.Clone(update));
        }
        private void TickVisits(double seconds)
        {
            // Keep the original snapshot when an active consent may expire and
            // invoke VisitChanged (whose listeners can add/remove visit records).
            // Settled history stays in the UI for later viewing; its timers can
            // be updated directly without allocating a List every idle frame.
            if (localVisits.Count != 0)
            {
                bool mayNotify = false;
                foreach (PlayerVisitUpdate update in localVisits.Values)
                    if (update.State == PlayerVisitState.Waiting || update.State == PlayerVisitState.Approved)
                    { mayNotify = true; break; }
                if (!mayNotify)
                {
                    foreach (PlayerVisitUpdate update in localVisits.Values)
                        update.RemainingSeconds = Math.Max(0, update.RemainingSeconds - seconds);
                }
                else
                {
                    var snapshot = localVisitTickSnapshot.Capture(localVisits);
                    try
                    {
                        foreach (PlayerVisitUpdate update in snapshot)
                        {
                            update.RemainingSeconds = Math.Max(0, update.RemainingSeconds - seconds);
                            if (update.RemainingSeconds == 0 && (update.State == PlayerVisitState.Waiting || update.State == PlayerVisitState.Approved))
                            { update.State = PlayerVisitState.Expired; update.Grant = null; update.Reason = "参观申请或赠送授权已超时。"; VisitChanged?.Invoke(JsonData.Clone(update)); }
                        }
                    }
                    finally { localVisitTickSnapshot.Release(snapshot); }
                }
            }
            if (!IsHost || hostVisits.Count == 0) return;
            // Finished/blocked visits can remain in the host's bounded history.
            // They have no ticking work; avoid snapshotting them indefinitely.
            bool hasDeadline = false;
            foreach (HostVisit visit in hostVisits.Values)
                if ((visit.State == PlayerVisitState.Waiting || visit.State == PlayerVisitState.Approved)
                    && visit.TransferId == null) { hasDeadline = true; break; }
            if (!hasDeadline) return;
            var hostSnapshot = hostVisitTickSnapshot.Capture(hostVisits);
            try
            {
                foreach (HostVisit visit in hostSnapshot)
                    if ((visit.State == PlayerVisitState.Waiting || visit.State == PlayerVisitState.Approved) && visit.TransferId == null)
                    {
                        visit.Seconds -= seconds;
                        if (visit.Seconds <= 0) EndVisit(visit, PlayerVisitState.Expired, "参观申请或赠送授权已超时。");
                        else if (!VisitParticipantsOnline(visit)) EndVisit(visit, PlayerVisitState.Cancelled, "参观双方不再同时在线。");
                    }
            }
            finally { hostVisitTickSnapshot.Release(hostSnapshot); }
        }
        private void DisconnectVisits(string playerId, bool all)
        {
            if (IsHost) foreach (HostVisit visit in hostVisits.Values.Where(x => x.State == PlayerVisitState.Waiting || x.State == PlayerVisitState.Approved).ToList())
                if (all || visit.Request.RequesterPlayerId == playerId || visit.Request.RecipientPlayerId == playerId)
                    EndVisit(visit, PlayerVisitState.Cancelled, "玩家已离开或房间已关闭，赠送授权已撤销。");
            if (all)
            {
                foreach (PlayerVisitUpdate update in localVisits.Values)
                { if (update.State == PlayerVisitState.Waiting || update.State == PlayerVisitState.Approved) { update.State = PlayerVisitState.Cancelled; update.Grant = null; update.Reason = "已离开房间。"; update.RemainingSeconds = 0; } }
                localVisitTickSnapshot.Reset();
                hostVisitTickSnapshot.Reset();
            }
        }
        private bool TouchesForeignMother(string actor, TransferRequest request)
        { return World.Players.Values.Any(x => x.PlayerId != actor && x.MotherShipMapId >= 0 && (x.MotherShipMapId == request.SourceMapId || x.MotherShipMapId == request.DestinationMapId)); }
        private static bool GrantMatches(PlayerVisitGrant grant, string actor, TransferRequest request)
        {
            if (grant == null || grant.RequesterPlayerId != actor || grant.Token != request.VisitToken
                || request.SourceMapId != grant.SourceMotherShipMapId || request.DestinationMapId != grant.DestinationMotherShipMapId
                || request.SourceFleetId >= 0 || request.DestinationFleetId >= 0 || request.HeroIds == null || request.HeroIds.Count != 0) return false;
            JObject manifest;
            try { manifest = JObject.Parse(request.NativeManifestJson ?? "{}"); }
            catch (Newtonsoft.Json.JsonException) { return false; }
            var items = manifest["Items"] as JArray; var heroes = manifest["Heroes"] as JArray; var animals = manifest["Animals"] as JArray;
            string[] fields = { "VehicleId", "DurationSeconds", "Items", "Heroes", "Animals", "UseSpaceElevator", "MissionUid" };
            return manifest.Properties().All(x => fields.Contains(x.Name)) && items != null && items.Count > 0
                && heroes != null && heroes.Count == 0 && animals != null && animals.Count == 0;
        }
        public bool IsVisitTransportAuthorized(TransferRequest request)
        {
            if (!IsConnected || closing || World == null || request == null) return false;
            if (String.IsNullOrEmpty(request.VisitToken)) return !TouchesForeignMother(LocalPlayerId, request);
            if (IsHost)
            {
                HostVisit visit = hostVisits.Values.FirstOrDefault(x => x.Grant != null && x.Grant.Token == request.VisitToken);
                return visit != null && visit.State == PlayerVisitState.Approved && (visit.TransferId == null || visit.TransferId == request.TransferId) && visit.Seconds > 0
                    && VisitParticipantsOnline(visit) && GrantMatches(visit.Grant, visit.Request.RequesterPlayerId, request);
            }
            return localVisits.Values.Any(x => x.State == PlayerVisitState.Approved && x.RemainingSeconds > 0 && GrantMatches(x.Grant, LocalPlayerId, request)
                && World.Players.ContainsKey(x.Grant.RecipientPlayerId) && World.Players[x.Grant.RecipientPlayerId].Online);
        }
        private HostVisit ReserveVisitTransfer(string actor, TransferRequest request)
        {
            if (String.IsNullOrEmpty(request.VisitToken))
            { if (TouchesForeignMother(actor, request)) throw new InvalidOperationException("跨玩家母舰运输需要对方同意的单向赠送授权。"); return null; }
            HostVisit visit = hostVisits.Values.FirstOrDefault(x => x.Grant != null && x.Grant.Token == request.VisitToken);
            if (visit == null || visit.State != PlayerVisitState.Approved || visit.Seconds <= 0 || visit.TransferId != null
                || !VisitParticipantsOnline(visit) || !GrantMatches(visit.Grant, actor, request)
                || World.Players[actor].MotherShipMapId != visit.Grant.SourceMotherShipMapId
                || World.Players[actor].FleetId != visit.Grant.RequesterFleetId || OwnerOfEntity(visit.Grant.RequesterFleetId) != actor
                || World.Players[visit.Grant.RecipientPlayerId].FleetId != visit.Grant.RecipientFleetId || OwnerOfEntity(visit.Grant.RecipientFleetId) != visit.Grant.RecipientPlayerId
                || World.Players[visit.Grant.RecipientPlayerId].MotherShipMapId != visit.Grant.DestinationMotherShipMapId)
                throw new InvalidOperationException("赠送授权已失效、被使用或运输方向不匹配。");
            BridgeResult contact = bridge.ValidatePlayerFleetContact(actor, visit.Grant.RequesterFleetId, visit.Grant.RecipientPlayerId, visit.Grant.RecipientFleetId);
            if (contact == null || !contact.Success) throw new InvalidOperationException("赠送提交时双方舰队不再处于有效接触星系。");
            visit.TransferId = request.TransferId; return visit;
        }
        private void CompleteVisitTransfer(HostVisit visit, bool release)
        {
            if (visit == null || visit.State != PlayerVisitState.Approved) return;
            if (release)
            { visit.TransferId = null; SendVisitUpdate(visit, "本次运输尚未扣除物资，可在授权期限内修改后重试。"); }
            else EndVisit(visit, PlayerVisitState.Consumed, "本次单向赠送已提交；授权已使用，不能再次装货。");
        }
    }
}
