using System;
using System.Collections.Generic;
using System.Linq;

namespace FreeWorldMultiplayer
{
    public sealed partial class MultiplayerSession
    {
        private sealed class GiftBarrier { internal GiftCheckpointRequest Request; internal string Simulator; internal bool Ready; }
        private readonly Dictionary<string, GiftBarrier> giftBarriers = new Dictionary<string, GiftBarrier>();
        private readonly Dictionary<long, GiftCheckpointRequest> giftFlushes = new Dictionary<long, GiftCheckpointRequest>();
        private readonly VisitTickSnapshot<GiftCheckpointRequest> giftFlushTickSnapshot = new VisitTickSnapshot<GiftCheckpointRequest>();
        private readonly HashSet<long> giftPausedMaps = new HashSet<long>();
        private string PrepareGiftArrival(TransferRecord transfer)
        {
            if (String.IsNullOrEmpty(transfer.GiftRecipientPlayerId) || transfer.Returning) return null;
            long mapId = transfer.Request.DestinationMapId;
            MapRecord map = EnsureMap(mapId);
            if (map.SimulatorPlayerId == null || map.SimulatorPlayerId == LocalPlayerId)
            { PublishLocalMaps(true); PublishPersonalUpdates(true); EnsureCurrentCheckpoint(map, true); return null; }
            if (map.AwaitingAuthorityReady) return "等待接收方母舰完成当前地表加载。";
            GiftBarrier barrier;
            if (giftBarriers.TryGetValue(transfer.TransferId, out barrier))
            {
                if (barrier.Request.AuthorityEpoch == map.AuthorityEpoch && barrier.Simulator == map.SimulatorPlayerId)
                { if (barrier.Ready) { EnsureCurrentCheckpoint(map, true); return null; } return "等待接收方母舰确认最终地表进度。"; }
                ClearGiftBarrier(transfer.TransferId);
            }
            if (giftBarriers.Values.Any(x => x.Request.MapId == mapId)) return "接收方母舰正在处理上一笔赠送。";
            barrier = new GiftBarrier { Simulator = map.SimulatorPlayerId, Request = new GiftCheckpointRequest { TransferId = transfer.TransferId, MapId = mapId, AuthorityEpoch = map.AuthorityEpoch } };
            giftBarriers.Add(transfer.TransferId, barrier);
            if (!TrySendCommunication(barrier.Simulator, "gift.flush", barrier.Request))
            { giftBarriers.Remove(transfer.TransferId); return "接收方地表模拟连接暂不可用。"; }
            return "正在收齐接收方母舰地表进度，然后交付赠送物资。";
        }
        private void ReceiveGiftFlush(GiftCheckpointRequest request)
        {
            MapRecord map;
            if (request == null || request.TransferId == null || !World.Maps.TryGetValue(request.MapId, out map)
                || map.SimulatorPlayerId != LocalPlayerId || map.AuthorityEpoch != request.AuthorityEpoch)
                throw new MessageRejectionException("赠送地表检查点请求的任期已失效。");
            giftPausedMaps.Add(request.MapId);
            Require(bridge.SetMapAuthority(new MapAccess { MapId = map.MapId, AuthorityEpoch = map.AuthorityEpoch, Revision = map.Revision, Access = MapAccessMode.Frozen }), "赠送落仓前收口接收方地表");
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
                    { giftFlushes.Remove(request.MapId); giftPausedMaps.Remove(request.MapId); continue; }
                    if (inFlightUpdates.Count != 0 || personalInFlight.Count != 0) continue;
                    PublishLocalMaps(true); PublishPersonalUpdates(true);
                    if (inFlightUpdates.Count != 0 || personalInFlight.Count != 0) continue;
                    Send("host", "gift.flushed", request); giftFlushes.Remove(request.MapId);
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
            giftBarriers.Remove(transferId); TrySendCommunication(barrier.Simulator, "gift.resume", barrier.Request);
        }
        private void ResumeGiftMap(GiftCheckpointRequest request)
        {
            if (request == null) return;
            MapRecord map;
            if (!World.Maps.TryGetValue(request.MapId, out map)) return;
            if (map.AuthorityEpoch < request.AuthorityEpoch) throw new MessageRejectionException("赠送地表恢复权限的任期尚未到达。");
            giftFlushes.Remove(request.MapId); giftPausedMaps.Remove(request.MapId);
            Require(bridge.SetMapAuthority(GetMapAccess(request.MapId)), "恢复赠送前临时收口的地表权限");
        }
        private bool IsGiftMapClosing(long mapId)
        { return giftPausedMaps.Contains(mapId) || IsHost && giftBarriers.Values.Any(x => x.Ready && x.Request.MapId == mapId); }
    }
}
