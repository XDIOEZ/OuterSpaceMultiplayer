using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FreeWorldMultiplayer
{
    public sealed partial class MultiplayerSession
    {
        // This is an application barrier, not another map authority or a second snapshot.
        // Keep the reliable stream's entire transactions (including Players) until native init ends.
        private const int MaxLoadingMessages = 128;
        private const long MaxLoadingWireBytes = 32L * 1024 * 1024;
        private const double MapLoadingTimeoutSeconds = 60;
        private sealed class NativeMapLoad
        {
            internal long MapId, Epoch, Revision;
            internal double Elapsed;
        }
        private sealed class LoadingMessage
        {
            internal WireMessage Message;
            internal MapView LocalView;
            // An already decoded, host-origin cache view. Keep it typed in the FIFO rather
            // than serializing tens of megabytes back into JToken only to parse it again.
            // Replay MUST use ReceiveMapView, not the host-only ApplyLocalMapView path.
            internal MapView NetworkView;
            internal string Peer, Player;
            internal bool Disconnected;
            internal string CacheViewId;
            internal long WireBytes;
        }
        private sealed class LoadingResync
        {
            internal long Epoch, Revision;
            internal double Elapsed;
            internal readonly Dictionary<string, long> Players = new Dictionary<string, long>();
        }
        private NativeMapLoad nativeMapLoad;
        private readonly List<LoadingMessage> loadingMessages = new List<LoadingMessage>();
        private readonly Dictionary<long, LoadingResync> loadingResyncs = new Dictionary<long, LoadingResync>();
        private readonly HashSet<long> requestedMapResyncs = new HashSet<long>();
        private long loadingWireBytes, receivingWireBytes;
        private bool replayingLoadingMessage, mapLoadingFailed, disconnectAfterMapLoad;
        private bool mapLoadingClockHeld, pauseAfterMapLoading;
        private bool HasMapLoadingBarrier => nativeMapLoad != null || loadingMessages.Count != 0 || loadingResyncs.Count != 0 || HasMapCacheBarrier;
        // Reserve one protocol-bounded message: a single full view can exceed the normal budget.
        // Stop dequeuing before the next packet instead of dropping any queued transaction.
        private bool MapLoadingBackpressure => !mapLoadingFailed && (nativeMapLoad != null || loadingResyncs.Count == 0 && !HasMapCacheBarrier)
            && (loadingMessages.Count >= MaxLoadingMessages || loadingWireBytes >= MaxLoadingWireBytes);

        private bool DeferMapLoadingMessage(string peer, WireMessage message)
        {
            if (replayingLoadingMessage || !HasMapLoadingBarrier || !IsConnected || message.SessionId != SessionId) return false;
            string player = null;
            if (IsHost && (peer == null || !peers.TryGetValue(peer, out player))) return false;
            // These do not apply map/personal assets. A close/disconnect must always remain usable.
            if (message.Kind == "ping" || message.Kind == "pong" || message.Kind == "clock" || message.Kind == "raidClock"
                || message.Kind == "saved" || message.Kind == "uids" || message.Kind == "error" || message.Kind == "closed"
                || message.Kind == "closing" || message.Kind == "checkpointRequest" || message.Kind == "leaveAck"
                || message.Kind.StartsWith("chat.", StringComparison.Ordinal)) return false;
            if (mapLoadingFailed) throw new MessageRejectionException("地图加载事务已暂停，请退出并重连房主最后确认的状态。");
            var queued = new LoadingMessage { Peer = peer, Player = player, Message = message, WireBytes = receivingWireBytes };
            if (!IsHost && message.Kind == "view")
            {
                MapView view = JsonData.Payload<MapView>(message.Payload);
                ValidateLoadingView(view);
                CoalesceLoadingViews(view);
            }
            EnqueueLoadingMessage(queued);
            return true;
        }
        private void EnqueueLoadingMessage(LoadingMessage queued)
        {
            // One received frame is itself bounded by Protocol.Decode. The next dequeue stops at
            // the soft budget; resync-wait without native work has no such drain, so fail explicitly.
            if (loadingMessages.Count >= MaxLoadingMessages || loadingWireBytes >= MaxLoadingWireBytes)
            {
                if (nativeMapLoad != null && !IsHost) RequestMapResync(nativeMapLoad.MapId);
                FailMapLoading(new InvalidOperationException("地图重新同步等待队列已达到上限，未继续应用或确认事务。"));
                throw new MessageRejectionException("地图同步队列已满，请退出后重新加入。");
            }
            if (queued.WireBytes < 0 || queued.WireBytes > 128L * 1024 * 1024)
                throw new InvalidDataException("A pending map view exceeds the bounded multiplayer frame size.");
            loadingMessages.Add(queued); loadingWireBytes += queued.WireBytes;
            if (MapLoadingBackpressure && nativeMapLoad != null && !IsHost) RequestMapResync(nativeMapLoad.MapId);
        }
        private static void ValidateLoadingView(MapView view)
        {
            if (view == null || view.Snapshot == null || view.Access == null || view.Snapshot.MapId != view.Access.MapId
                || view.Snapshot.Revision != view.Access.Revision || view.Players == null)
                throw new InvalidDataException("Incomplete or mismatched map loading view.");
            RequireFullCheckpoint(view.Snapshot, view.Access.MapId);
            if (view.Snapshot.EntityOwners == null || view.Players.Any(x => x == null)) throw new InvalidDataException("Map view ownership/player fields are missing.");
        }
        private bool QueueLocalMapView(MapView view)
        {
            ValidateLoadingView(view);
            if (!HasMapLoadingBarrier || replayingLoadingMessage && nativeMapLoad == null) return false;
            CoalesceLoadingViews(view);
            long bytes = checked(2L * view.Snapshot.NativeJson.Length + 256);
            EnqueueLoadingMessage(new LoadingMessage { LocalView = view, WireBytes = bytes });
            return true;
        }
        private void BeginLocalMapLoad(MapView view)
        {
            ValidateLoadingView(view);
            if (nativeMapLoad != null) throw new InvalidOperationException("Previous native map load has not completed.");
            ValidateResyncCoverage(view);
            awaitingLocalReady.Clear();
            // Applying an authoritative checkpoint to an already instantiated map is an
            // atomic in-place projection. There is no native enterMap/init to wait for.
            if (bridge.IsMapReady(view.Access.MapId)) return;
            nativeMapLoad = new NativeMapLoad { MapId = view.Access.MapId, Epoch = view.Access.AuthorityEpoch, Revision = view.Access.Revision };
            if (IsHost && !mapLoadingClockHeld)
            {
                pauseAfterMapLoading = Paused; mapLoadingClockHeld = true; Paused = true;
                Broadcast("clock", new ClockState { Paused = true, Speed = Speed, RoomRealSeconds = World.RoomRealSeconds, WorldTimeMM = World.WorldTimeMM });
            }
        }
        private void CompleteLocalViewApplication(MapView view)
        {
            requestedMapResyncs.Remove(view.Access.MapId); loadingResyncs.Remove(view.Access.MapId);
            if (view.Access.Access == MapAccessMode.Simulator) awaitingLocalReady[view.Access.MapId] = view.Access;
        }
        private static bool SameOrNewerView(MapView next, MapView previous)
        {
            return next.Access.MapId == previous.Access.MapId && next.Access.AuthorityEpoch >= previous.Access.AuthorityEpoch
                && next.Access.Revision >= previous.Access.Revision;
        }
        private static bool CoversPlayer(MapView view, string player, long revision)
        { return view.Players.Any(x => x != null && x.PlayerId == player && x.PersonalRevision >= revision); }
        private bool CoveredByLoadingView(LoadingMessage queued, MapView view)
        {
            MapView old = queued.LocalView ?? queued.NetworkView;
            if (old == null && queued.Message != null && queued.Message.Kind == "view") old = JsonData.Payload<MapView>(queued.Message.Payload);
            if (old != null) return SameOrNewerView(view, old)
                && old.Players.All(x => x != null && CoversPlayer(view, x.PlayerId, x.PersonalRevision));
            // Never remove a host's original peer submission: it still needs native validation,
            // commit and a real ACK, even if a locally captured full view looks newer.
            if (IsHost || queued.Message == null) return false;
            if (queued.Message.Kind == "map")
            {
                MapUpdate update = JsonData.Payload<MapUpdate>(queued.Message.Payload);
                return update != null && update.SessionId == SessionId && update.MapId == view.Access.MapId
                    && update.AuthorityEpoch <= view.Access.AuthorityEpoch && update.Revision <= view.Access.Revision
                    && (update.Players == null || update.Players.All(x => x != null && CoversPlayer(view, x.PlayerId, x.Revision)));
            }
            if (queued.Message.Kind == "personal")
            {
                PlayerUpdate update = JsonData.Payload<PlayerUpdate>(queued.Message.Payload);
                return update != null && CoversPlayer(view, update.PlayerId, update.Revision);
            }
            // ACK/reset/commands/results have additional lifecycle effects, so keep them in order.
            return false;
        }
        private void CoalesceLoadingViews(MapView view, int beforePosition = -1)
        {
            // Only supersede a provably covered suffix. In particular, never move a full personal
            // baseline ahead of a later delta/reset, or discard an unrelated player's progress.
            for (int i = (beforePosition < 0 ? loadingMessages.Count : beforePosition) - 1; i >= 0; --i)
            {
                LoadingMessage old = loadingMessages[i];
                if (old.CacheViewId != null) break;
                // Policy changes no native personal fields. Keep it, but it need not prevent a
                // later full view from replacing earlier, fully covered map transactions.
                if (CanKeepBeforeLoadingView(old, view)) continue;
                if (!CoveredByLoadingView(old, view)) break;
                loadingWireBytes -= old.WireBytes; loadingMessages.RemoveAt(i);
            }
        }
        private static bool CanKeepBeforeLoadingView(LoadingMessage queued, MapView view)
        {
            if (queued.Message == null) return false;
            if (queued.Message.Kind == "policy" || queued.Message.Kind == "playerJoined") return true;
            string player; long revision;
            if (queued.Message.Kind == "personalAck" || queued.Message.Kind == "personal")
            {
                PlayerUpdate update = JsonData.Payload<PlayerUpdate>(queued.Message.Payload);
                if (update == null) return false;
                player = update.PlayerId; revision = update.Revision;
                // A covered delta can be superseded. An unrelated player's delta remains in its
                // original position; this full view cannot change any of that player's fields.
                if (queued.Message.Kind == "personal" && CoversPlayer(view, player, revision)) return false;
            }
            else if (queued.Message.Kind == "personalReset")
            {
                PlayerFieldState reset = JsonData.Payload<PlayerFieldState>(queued.Message.Payload);
                if (reset == null) return false;
                player = reset.PlayerId; revision = reset.PersonalRevision;
            }
            else return false;
            PlayerFieldState baseline = view.Players.FirstOrDefault(x => x.PlayerId == player);
            return baseline == null || baseline.PersonalRevision >= revision;
        }
        private void ValidateResyncCoverage(MapView view)
        {
            LoadingResync required;
            if (!loadingResyncs.TryGetValue(view.Access.MapId, out required)) return;
            if (view.Access.AuthorityEpoch < required.Epoch || view.Access.Revision < required.Revision)
                throw new MessageRejectionException("该地图恢复点早于等待中的事务，继续等待房主最新完整状态。");
            if (required.Players.Any(x => !CoversPlayer(view, x.Key, x.Value)))
                throw new InvalidDataException("完整地图恢复点未覆盖等待中的地图与个人事务：" + view.Access.MapId);
        }
        private void RequestMapResync(long mapId)
        {
            if (IsHost || !requestedMapResyncs.Add(mapId)) return;
            Send("host", "resync", new MapReference { MapId = mapId });
        }
        private void WaitForMapResync(MapUpdate update)
        {
            LoadingResync waiting;
            if (!loadingResyncs.TryGetValue(update.MapId, out waiting))
            {
                waiting = new LoadingResync { Epoch = update.AuthorityEpoch };
                loadingResyncs.Add(update.MapId, waiting);
                Error("地图变动缺包，正在等待房主完整状态：" + update.MapId);
            }
            waiting.Revision = Math.Max(waiting.Revision, update.Revision);
            foreach (PlayerUpdate player in update.Players ?? new List<PlayerUpdate>())
            {
                if (player == null) throw new InvalidDataException("Incomplete queued personal transaction.");
                long previous; waiting.Players.TryGetValue(player.PlayerId, out previous);
                waiting.Players[player.PlayerId] = Math.Max(previous, player.Revision);
            }
            RequestMapResync(update.MapId);
        }
        private void TickMapLoading(double seconds)
        {
            if (mapLoadingFailed || nativeFault != null || World == null) return;
            try
            {
                if (nativeMapLoad != null)
                {
                    nativeMapLoad.Elapsed += seconds;
                    if (!bridge.IsMapReady(nativeMapLoad.MapId))
                    {
                        if (nativeMapLoad.Elapsed >= MapLoadingTimeoutSeconds)
                            throw new TimeoutException("地图初始化超时：map=" + nativeMapLoad.MapId + " epoch=" + nativeMapLoad.Epoch + " revision=" + nativeMapLoad.Revision);
                        return;
                    }
                    nativeMapLoad = null;
                }
                foreach (var pair in loadingResyncs)
                {
                    pair.Value.Elapsed += seconds;
                    if (pair.Value.Elapsed >= MapLoadingTimeoutSeconds) throw new TimeoutException("等待完整地图状态超时：map=" + pair.Key + " epoch=" + pair.Value.Epoch);
                }
                int budget = 32;
                while (nativeMapLoad == null && loadingMessages.Count != 0 && HasInboundWorkBudget && budget-- > 0 && nativeFault == null)
                {
                    LoadingMessage queued = loadingMessages[0];
                    if (queued.CacheViewId != null) return; // The host's full view fills this exact reliable-stream slot.
                    if (loadingResyncs.Count != 0 && !CanReplayWhileResyncing(queued)) return;
                    CountInboundWork();
                    loadingMessages.RemoveAt(0); loadingWireBytes -= queued.WireBytes;
                    replayingLoadingMessage = true;
                    try
                    {
                        if (queued.LocalView != null) ApplyLocalMapView(queued.LocalView);
                        else if (queued.NetworkView != null)
                        {
                            receivingKind = "view";
                            MessageStage("view.personal-fields-before-map", queued.NetworkView.Access.MapId, LocalPlayerId);
                            long applyStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                            try { ReceiveMapView(queued.NetworkView); }
                            finally { ReportSlowOperation("map.cache.apply", applyStarted, queued.WireBytes, queued.NetworkView.Access.MapId); }
                        }
                        else if (queued.Disconnected)
                        {
                            string current;
                            if (peers.TryGetValue(queued.Peer, out current) && current == queued.Player) RemovePeer(queued.Peer);
                        }
                        else
                        {
                            receivingKind = queued.Message.Kind;
                            MessageStage("map.loading-replay", nativeMapLoad == null ? -1 : nativeMapLoad.MapId, queued.Player);
                            receivingWireBytes = queued.WireBytes;
                            if (IsHost)
                            {
                                string current;
                                // A failed optional refusal may quarantine this peer while
                                // its later frames are already waiting in the loading FIFO.
                                // They were never applied/ACKed and must not bypass isolation.
                                if (rejectedPeerEvictions.Contains(queued.Peer)
                                    || !peers.TryGetValue(queued.Peer, out current) || current != queued.Player) continue;
                                HandleHostMessage(queued.Peer, queued.Message);
                            }
                            else HandleClientMessage(queued.Message);
                        }
                    }
                    catch (MessageRejectionException rejected)
                    {
                        // A queued lease/command can legitimately expire before native init ends.
                        // It is never an applied native transaction and must not poison the load.
                        // StatusChanged is an optional UI observer, not another map transaction.
                        try { Error("等待地图载入的消息已失效：" + rejected.Message); }
                        catch (Exception) { }
                        string boundPlayer;
                        if (IsHost && queued.Peer != null && queued.Player != null
                            && peers.TryGetValue(queued.Peer, out boundPlayer) && boundPlayer == queued.Player)
                        {
                            const string isolationReason = "地图载入期间拒绝通知未能送达，隔离该连接。";
                            try
                            {
                                OptionalPeerNotice.SendOrDisconnect(
                                    () => Send(queued.Peer, "error", rejected.Message),
                                    () =>
                                    {
                                        // Track BEFORE disconnect: a synchronous Disconnected callback
                                        // may Forget the old lease; never re-add it afterwards.
                                        string current;
                                        if (!peers.TryGetValue(queued.Peer, out current) || current != queued.Player) return;
                                        rejectedPeerEvictions.Track(queued.Peer, isolationReason);
                                        transport.Disconnect(queued.Peer, isolationReason);
                                    });
                            }
                            catch (Exception)
                            {
                                // Unknown failures while encoding an optional refusal are not
                                // native/map/ACK failures. Quarantine only this still-bound peer.
                                string current;
                                if (peers.TryGetValue(queued.Peer, out current) && current == queued.Player)
                                    QuarantineRejectedPeer(queued.Peer, isolationReason);
                            }
                        }
                    }
                    finally { replayingLoadingMessage = false; }
                }
            }
            catch (Exception error) { FailMapLoading(error); }
        }
        private bool CanReplayWhileResyncing(LoadingMessage queued)
        {
            if (queued.LocalView != null || queued.NetworkView != null || queued.Message != null && queued.Message.Kind == "view") return true;
            // Metadata may precede the requested view, but personal/native transactions must not
            // pass its missing predecessor. A later covering view removes their covered suffix.
            if (queued.Message == null) return false;
            if (queued.Message.Kind == "policy" || queued.Message.Kind == "playerJoined"
                || queued.Message.Kind == "personalAck" || queued.Message.Kind == "personalReset") return true;
            if (queued.Message.Kind == "personal")
            {
                PlayerUpdate update = JsonData.Payload<PlayerUpdate>(queued.Message.Payload);
                PlayerRecord player;
                return update != null && World.Players.TryGetValue(update.PlayerId, out player)
                    && (player.PersonalRevision == update.BaseRevision || player.PersonalRevision == update.Revision);
            }
            if (loadingMessages.Any(x => x.LocalView != null || x.NetworkView != null || x.Message != null && x.Message.Kind == "view"))
                throw new InvalidDataException("完整地图恢复点不能覆盖队列前方的事务，保留现场：" + queued.Message.Kind);
            return false;
        }
        private bool DeferMapLoadingDisconnect(string peer)
        {
            string player;
            if (!IsHost || !HasMapLoadingBarrier || mapLoadingFailed || !peers.TryGetValue(peer, out player)) return false;
            EnqueueLoadingMessage(new LoadingMessage { Peer = peer, Player = player, Disconnected = true, WireBytes = 64 });
            return true;
        }
        private void FailMapLoading(Exception error)
        {
            if (mapLoadingFailed) return;
            mapLoadingFailed = true;
            receivingKind = "map-loading";
            MessageStage("map.loading", nativeMapLoad == null ? receivingMapId : nativeMapLoad.MapId, LocalPlayerId);
            ReportMessageFault(null, error);
            LockNativeFault("地图加载/同步未完成，未确认排队事务：" + error.Message);
        }
        private void ClearLocalMapLoading(long mapId)
        {
            awaitingLocalReady.Remove(mapId); requestedMapResyncs.Remove(mapId); loadingResyncs.Remove(mapId);
            // The actual native init must finish before another ApplyMap. Do not pretend its
            // manager was cancelled just because the logical visitor/lease has gone away.
        }
        private void TickLocalAuthorityReady()
        {
            if (HasMapLoadingBarrier || World == null) return;
            foreach (var entry in awaitingLocalReady.ToList())
            {
                MapRecord map;
                PlayerRecord local;
                if (!World.Maps.TryGetValue(entry.Key, out map) || map.AuthorityEpoch != entry.Value.AuthorityEpoch
                    || map.SimulatorPlayerId != LocalPlayerId || map.Revision != entry.Value.Revision
                    || !World.Players.TryGetValue(LocalPlayerId, out local) || local.CurrentMapId != entry.Key)
                { awaitingLocalReady.Remove(entry.Key); continue; }
                if (!bridge.IsMapReady(entry.Key)) continue;
                awaitingLocalReady.Remove(entry.Key);
                var ready = new AuthorityReady { MapId = entry.Key, AuthorityEpoch = entry.Value.AuthorityEpoch, Revision = entry.Value.Revision };
                if (IsHost) ConfirmAuthority(LocalPlayerId, ready); else Send("host", "ready", ready);
            }
        }
        private void ClearMapLoading()
        {
            nativeMapLoad = null; loadingMessages.Clear(); loadingWireBytes = 0;
            awaitingLocalReady.Clear(); loadingResyncs.Clear(); requestedMapResyncs.Clear();
            mapLoadingClockHeld = false;
            ClearMapCacheSession();
        }
        private void RestoreMapLoadingClock()
        {
            if (!mapLoadingClockHeld || HasMapLoadingBarrier || awaitingLocalReady.Count != 0 || closing || nativeFault != null) return;
            mapLoadingClockHeld = false; Paused = pauseAfterMapLoading;
            Broadcast("clock", new ClockState { Paused = Paused, Speed = Speed, RoomRealSeconds = World.RoomRealSeconds, WorldTimeMM = World.WorldTimeMM });
        }
        private bool DeferDisconnectUntilMapReady()
        {
            if (nativeMapLoad == null && (!IsHost || !HasMapLoadingBarrier)) return false;
            disconnectAfterMapLoad = true; closing = true;
            if (!IsHost)
            {
                // The host remains canonical. After its connection has gone, none of these
                // unapplied transactions can be ACKed or submitted; only finish the native init.
                loadingMessages.Clear(); loadingWireBytes = 0;
                loadingResyncs.Clear(); requestedMapResyncs.Clear(); awaitingLocalReady.Clear();
                ClearMapCacheSession();
            }
            if (LastError == null) SetStatus("正在结束地图载入，然后退出联机……");
            return true;
        }
    }
}
