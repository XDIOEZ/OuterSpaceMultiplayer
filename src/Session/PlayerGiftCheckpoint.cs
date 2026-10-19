using System;
using System.Collections.Generic;
using System.Linq;

namespace FreeWorldMultiplayer
{
    public sealed partial class MultiplayerSession
    {
        private sealed class GiftBarrier { internal GiftCheckpointRequest Request; internal string Simulator; internal bool Ready; }
        private readonly Dictionary<string, GiftBarrier> giftBarriers = new Dictionary<string, GiftBarrier>();
        // If a reliable resume cannot even enter the peer's send queue, keep
        // the original capability until the same simulator/epoch can receive it.
        private readonly Dictionary<string, GiftBarrier> pendingGiftResumes = new Dictionary<string, GiftBarrier>();
        private readonly VisitTickSnapshot<GiftBarrier> pendingGiftResumeSnapshot = new VisitTickSnapshot<GiftBarrier>();
        private double giftResumeRetryElapsed;
        private readonly Dictionary<long, GiftCheckpointRequest> giftFlushes = new Dictionary<long, GiftCheckpointRequest>();
        private readonly VisitTickSnapshot<GiftCheckpointRequest> giftFlushTickSnapshot = new VisitTickSnapshot<GiftCheckpointRequest>();
        private readonly HashSet<long> giftPausedMaps = new HashSet<long>();
        // The flush queue is removed as soon as gift.flushed has been sent, but
        // the real native map remains frozen until the matching resume arrives.
        private readonly Dictionary<long, GiftCheckpointRequest> pausedGiftRequests = new Dictionary<long, GiftCheckpointRequest>();
        private static bool SameGiftCheckpoint(GiftCheckpointRequest left, GiftCheckpointRequest right)
        {
            return left != null && right != null && left.MapId == right.MapId
                && left.AuthorityEpoch == right.AuthorityEpoch
                && String.Equals(left.TransferId, right.TransferId, StringComparison.Ordinal);
        }
        private string PrepareGiftArrival(TransferRecord transfer)
        {
            long mapId = transfer.Returning ? transfer.Request.SourceMapId : transfer.Request.DestinationMapId;
            if (mapId < 0) return null;
            MapRecord map = EnsureMap(mapId);
            if (map.SimulatorPlayerId == null || map.SimulatorPlayerId == LocalPlayerId)
            { PublishLocalMaps(true); PublishPersonalUpdates(true); EnsureCurrentCheckpoint(map, true); return null; }
            if (map.AwaitingAuthorityReady) return "等待目标地表完成当前加载。";
            foreach (GiftBarrier pending in pendingGiftResumes.Values)
                if (pending.Request.MapId == mapId && pending.Simulator == map.SimulatorPlayerId)
                    return "目标地表尚未恢复上一笔运输的地表权限。";
            GiftBarrier barrier;
            if (giftBarriers.TryGetValue(transfer.TransferId, out barrier))
            {
                if (barrier.Request.AuthorityEpoch == map.AuthorityEpoch && barrier.Simulator == map.SimulatorPlayerId)
                { if (barrier.Ready) { EnsureCurrentCheckpoint(map, true); return null; } return "等待目标地表确认最终地表进度。"; }
                ClearGiftBarrier(transfer.TransferId);
            }
            if (giftBarriers.Values.Any(x => x.Request.MapId == mapId)) return "目标地表正在处理上一笔运输。";
            barrier = new GiftBarrier { Simulator = map.SimulatorPlayerId, Request = new GiftCheckpointRequest { TransferId = transfer.TransferId, MapId = mapId, AuthorityEpoch = map.AuthorityEpoch } };
            giftBarriers.Add(transfer.TransferId, barrier);
            if (!TrySendCommunication(barrier.Simulator, "gift.flush", barrier.Request))
            { giftBarriers.Remove(transfer.TransferId); return "接收方地表模拟连接暂不可用。"; }
            return "正在收齐目标地表进度，然后交付运输物资。";
        }
        private void ReceiveGiftFlush(GiftCheckpointRequest request)
        {
            MapRecord map;
            if (request == null || request.TransferId == null || !World.Maps.TryGetValue(request.MapId, out map)
                || map.SimulatorPlayerId != LocalPlayerId || map.AuthorityEpoch != request.AuthorityEpoch)
                throw new MessageRejectionException("赠送地表检查点请求的任期已失效。");
            GiftCheckpointRequest previous;
            if (pausedGiftRequests.TryGetValue(request.MapId, out previous) && !SameGiftCheckpoint(previous, request))
                throw new MessageRejectionException("同一地表的上一笔运输尚未解除冻结。");
            giftPausedMaps.Add(request.MapId);
            pausedGiftRequests[request.MapId] = request;
            Require(bridge.SetMapAuthority(new MapAccess { MapId = map.MapId, AuthorityEpoch = map.AuthorityEpoch, Revision = map.Revision, SimulatorPlayerId = map.SimulatorPlayerId, Access = MapAccessMode.Frozen }), "运输落仓前收口目标地表");
            giftFlushes[request.MapId] = request;
        }
        private void TickGiftFlushes()
        {
            // This runs on every connected guest tick, usually without any
            // pending gifts. Snapshot only active work: Send/Publish callbacks
            // may remove or add requests during the iteration.
            if (giftFlushes.Count == 0) return;
            var snapshot = giftFlushTickSnapshot.CaptureDictionary(giftFlushes);
            try
            {
                foreach (GiftCheckpointRequest request in snapshot)
                {
                    MapRecord map;
                    if (!World.Maps.TryGetValue(request.MapId, out map) || map.AuthorityEpoch != request.AuthorityEpoch || map.SimulatorPlayerId != LocalPlayerId)
                    {
                        GiftCheckpointRequest current;
                        if (giftFlushes.TryGetValue(request.MapId, out current) && SameGiftCheckpoint(current, request))
                            giftFlushes.Remove(request.MapId);
                        if (pausedGiftRequests.TryGetValue(request.MapId, out current) && SameGiftCheckpoint(current, request))
                        { pausedGiftRequests.Remove(request.MapId); giftPausedMaps.Remove(request.MapId); }
                        continue;
                    }
                    if (inFlightUpdates.Count != 0 || personalInFlight.Count != 0) continue;
                    PublishLocalMaps(true); PublishPersonalUpdates(true);
                    if (inFlightUpdates.Count != 0 || personalInFlight.Count != 0) continue;
                    Send("host", "gift.flushed", request);
                    GiftCheckpointRequest remaining;
                    if (giftFlushes.TryGetValue(request.MapId, out remaining) && SameGiftCheckpoint(remaining, request))
                        giftFlushes.Remove(request.MapId);
                }
            }
            finally { giftFlushTickSnapshot.Release(snapshot); }
        }
        private void ConfirmGiftFlush(string actor, GiftCheckpointRequest request)
        {
            GiftBarrier barrier; MapRecord map;
            if (request == null || request.TransferId == null || !giftBarriers.TryGetValue(request.TransferId, out barrier)
                || barrier.Simulator != actor || request.MapId != barrier.Request.MapId || request.AuthorityEpoch != barrier.Request.AuthorityEpoch
                || !World.Maps.TryGetValue(request.MapId, out map) || map.SimulatorPlayerId != actor || map.AuthorityEpoch != request.AuthorityEpoch)
                throw new MessageRejectionException("赠送地表检查点回执已过期。");
            barrier.Ready = true;
        }
        private void ClearGiftBarrier(string transferId)
        {
            GiftBarrier barrier;
            if (!giftBarriers.TryGetValue(transferId, out barrier)) return;
            giftBarriers.Remove(transferId);
            // Reserve the retry before calling transport/native callbacks:
            // even an unexpected exception must not lose the only resume.
            pendingGiftResumes[transferId] = barrier;
            if (TrySendCommunication(barrier.Simulator, "gift.resume", barrier.Request))
                pendingGiftResumes.Remove(transferId);
        }
        private void TickGiftResumes(double seconds)
        {
            if (!IsHost || World == null || !IsConnected || pendingGiftResumes.Count == 0) return;
            // Unscaled room time, independent of pause and game speed. Keep
            // failed enqueue attempts bounded even when the peer queue is full.
            giftResumeRetryElapsed += seconds;
            if (giftResumeRetryElapsed < 1) return;
            giftResumeRetryElapsed = 0;
            var snapshot = pendingGiftResumeSnapshot.Capture(pendingGiftResumes);
            try
            {
                foreach (GiftBarrier barrier in snapshot)
                {
                    GiftBarrier current;
                    if (!pendingGiftResumes.TryGetValue(barrier.Request.TransferId, out current)
                        || !ReferenceEquals(current, barrier)) continue;
                    MapRecord map;
                    if (!World.Maps.TryGetValue(barrier.Request.MapId, out map)
                        || map.SimulatorPlayerId != barrier.Simulator)
                    { pendingGiftResumes.Remove(barrier.Request.TransferId); continue; }
                    if (TrySendCommunication(barrier.Simulator, "gift.resume", barrier.Request))
                        pendingGiftResumes.Remove(barrier.Request.TransferId);
                }
            }
            finally { pendingGiftResumeSnapshot.Release(snapshot); }
        }
        private void ResumeGiftMap(GiftCheckpointRequest request)
        {
            if (request == null) return;
            MapRecord map;
            if (!World.Maps.TryGetValue(request.MapId, out map)) return;
            if (map.AuthorityEpoch < request.AuthorityEpoch) throw new MessageRejectionException("赠送地表恢复权限的任期尚未到达。");
            GiftCheckpointRequest current;
            // The host may have already rebased the map to a NEW epoch while
            // completing the gift. The old resume must still release its
            // exact frozen transfer, never a later transfer in that map.
            if (!pausedGiftRequests.TryGetValue(request.MapId, out current)
                || !SameGiftCheckpoint(current, request)) return;
            // Restore the real native access first. Failure must keep the
            // freeze and current identity for a subsequent reliable retry.
            Require(bridge.SetMapAuthority(GetMapAccess(request.MapId)), "恢复赠送前临时收口的地表权限");
            pausedGiftRequests.Remove(request.MapId);
            giftPausedMaps.Remove(request.MapId);
            if (giftFlushes.TryGetValue(request.MapId, out current) && SameGiftCheckpoint(current, request))
                giftFlushes.Remove(request.MapId);
        }
        private bool IsGiftMapClosing(long mapId)
        { return giftPausedMaps.Contains(mapId) || IsHost && giftBarriers.Values.Any(x => x.Ready && x.Request.MapId == mapId); }
    }
}
