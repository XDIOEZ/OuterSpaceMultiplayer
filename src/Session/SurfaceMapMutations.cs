using System;
using System.Linq;

namespace FreeWorldMultiplayer
{
    public sealed partial class MultiplayerSession
    {
        // Ordinary landings change map contents, not the identity of its simulator. The existing
        // flush capability closes the simulator's previous commits before the host changes assets.
        private bool IsFrozenHostMapMutation(MapUpdate update)
        {
            GiftCheckpointRequest request;
            return update != null && !String.IsNullOrEmpty(update.NativeDeltaJson) && giftPausedMaps.Contains(update.MapId)
                && pausedGiftRequests.TryGetValue(update.MapId, out request)
                && request.AuthorityEpoch == update.AuthorityEpoch
                && !inFlightUpdates.ContainsKey(update.MapId) && personalInFlight.Count == 0;
        }

        private void PublishTransferMapMutation(long mapId)
        {
            if (mapId < 0) return;
            MapRecord map = EnsureMap(mapId);
            MapSnapshot before = map.Checkpoint;
            RequireFullCheckpoint(before, mapId);
            if (before.Revision != map.Revision)
                throw new InvalidOperationException("运输落仓缺少同版本地表检查点。");
            if (map.SimulatorPlayerId != null && map.SimulatorPlayerId != LocalPlayerId
                && !giftBarriers.Values.Any(x => x.Ready && x.Request.MapId == mapId
                    && x.Request.AuthorityEpoch == map.AuthorityEpoch && x.Simulator == map.SimulatorPlayerId))
                throw new InvalidOperationException("运输落仓未收齐当前模拟者的最终检查点。");

            MapUpdate update = bridge.CaptureMapUpdate(mapId, before);
            if (update == null) return;
            if (update.Checkpoint == null || update.EntityOwners == null || update.Players == null)
                throw new InvalidOperationException("运输地表差量缺少完整事务标记。");
            MapTransactionPlayerValidation.RequireUnique(update.Players);
            foreach (PlayerUpdate personal in update.Players)
            {
                PlayerRecord player;
                if (!World.Players.TryGetValue(personal.PlayerId, out player)
                    || personal.BaseRevision != player.PersonalRevision
                    || personal.Revision != checked(personal.BaseRevision + 1))
                    throw new InvalidOperationException("运输地表差量的个人版本不连续。");
            }
            update.CommitId = Guid.NewGuid().ToString("N"); update.SessionId = SessionId;
            update.MapId = mapId; update.AuthorityEpoch = map.AuthorityEpoch;
            update.BaseRevision = map.Revision; update.Revision = checked(map.Revision + 1);
            update.Checkpoint.Revision = update.Revision;
            foreach (var owner in update.EntityOwners) RegisterOwner(owner.Key, owner.Value);
            foreach (PlayerUpdate personal in update.Players)
                World.Players[personal.PlayerId].PersonalRevision = personal.Revision;
            map.Revision = update.Revision; map.LastCommitId = update.CommitId;
            map.Checkpoint = update.Checkpoint; map.LastSimulatedWorldMM = World.WorldTimeMM;
            if (map.SimulatorPlayerId == LocalPlayerId) localBaselines[mapId] = update.Checkpoint;
            Require(bridge.SetMapAuthority(AccessFor(map, LocalPlayerId)), "推进运输地表差量版本");

            // The simulator receives the host mutation before its matching resume, on the same
            // reliable stream. Cache negotiation must not put a resume ahead of map application.
            string simulatorPeer;
            if (map.SimulatorPlayerId != null && map.SimulatorPlayerId != LocalPlayerId)
            {
                if (!playerPeers.TryGetValue(map.SimulatorPlayerId, out simulatorPeer))
                    throw new InvalidOperationException("运输地表模拟者已断线，保留未完成事务。");
                Send(simulatorPeer, "map", WireMapUpdate(update));
            }
            foreach (string visitor in map.Visitors.ToList())
            {
                string peer;
                if (visitor != map.SimulatorPlayerId && playerPeers.TryGetValue(visitor, out peer))
                    SendCommittedMapToObserver(peer, update);
            }
            foreach (PlayerUpdate personal in update.Players)
            {
                string peer;
                if (!map.Visitors.Contains(personal.PlayerId) && playerPeers.TryGetValue(personal.PlayerId, out peer))
                    BroadcastCommittedPersonalToRecipient(peer, personal);
            }
        }

        private void CompleteFrozenHostMapMutation(MapRecord map)
        {
            // Capture a new complete baseline only after native application. The simulator must
            // compare its next commit with the host's landing instead of its pre-flush world.
            MapSnapshot snapshot = EnsureCurrentCheckpoint(map, true);
            localBaselines[map.MapId] = snapshot;
        }
    }
}
