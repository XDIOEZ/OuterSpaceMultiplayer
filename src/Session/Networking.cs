using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FreeWorldMultiplayer
{
    public sealed partial class MultiplayerSession
    {
        private void Send(string peerId, string kind, object payload, string requestId = null)
        {
            transport.Send(peerId, Protocol.Encode(kind, SessionId, payload, requestId));
        }
        private void Broadcast(string kind, object payload, string exceptPeer = null)
        {
            if (!IsHost || !transport.IsRunning) return;
            // All recipients share one immutable session envelope; both transports clone per peer.
            byte[] encoded = null;
            foreach (string peer in peers.Keys.ToList())
            {
                if (peer == exceptPeer) continue;
                if (encoded == null) encoded = EncodeMessage(kind, payload, null);
                transport.Send(peer, encoded);
            }
        }
        private void HandleTransport(TransportEvent item)
        {
            if (item == null) return;
            if (item.Kind == TransportEventKind.Error) { Error(item.Error ?? "传输错误"); return; }
            if (item.Kind == TransportEventKind.Disconnected)
            {
                rejectedPeerEvictions.Forget(item.PeerId);
                if (IsHost) unauthenticatedPeerDeadline.Forget(item.PeerId);
                if (IsHost) RemovePeer(item.PeerId);
                else { if (IsConnected && !closing) HostLeft = true; IsConnected = false; Paused = true; Error(item.Error ?? "与房主的连接已断开；重连将使用房主状态。"); CompleteDisconnect(); }
                return;
            }
            if (item.Kind == TransportEventKind.Connected)
            {
                if (IsHost)
                {
                    if (!unauthenticatedPeerDeadline.Begin(item.PeerId))
                        transport.Disconnect(item.PeerId, "未认证连接数量已达上限，请稍后加入。");
                }
                else Send("host", "hello", new Hello { CredentialId = identity.CredentialId, Nickname = identity.Nickname });
                return;
            }
            if (item.Kind != TransportEventKind.Message) return;
            // An isolated peer must not mutate maps, commands or credentials
            // while its transport is still refusing to close.
            if (IsHost && rejectedPeerEvictions.Contains(item.PeerId)) return;
            // An untrusted peer must never cause a 128 MiB UTF-16 string and
            // JSON token tree allocation before its handshake is complete.
            if (IsHost && !UnauthenticatedFramePolicy.AllowsFrame(true,
                item.PeerId != null && peers.ContainsKey(item.PeerId),
                item.Data == null ? 0 : item.Data.Length))
            {
                if (item.PeerId != null)
                {
                    try { transport.Disconnect(item.PeerId, "未认证消息超过握手大小上限。"); }
                    catch (IOException) { }
                    catch (InvalidOperationException) { }
                }
                return;
            }
            WireMessage message = Protocol.Decode(item.Data);
            if (IsHost && (item.PeerId == null || !peers.ContainsKey(item.PeerId))
                && !UnauthenticatedFramePolicy.IsHandshakeKind(message.Kind))
                throw new InvalidDataException("尚未认证的连接只能发送 hello/auth。");
            if (IsHost) HandleHostMessage(item.PeerId, message);
            else HandleClientMessage(message);
        }
        private void HandleHostMessage(string peer, WireMessage message)
        {
            if (World == null) return;
            if (nativeFault != null && message.Kind != "ping" && message.Kind != "pong" && message.Kind != "leave") throw new InvalidOperationException("房主原生事务已锁定，拒绝继续写入：" + nativeFault);
            if (closing && message.Kind != "map" && message.Kind != "personalUpdate" && message.Kind != "finalDone" && message.Kind != "pong" && message.Kind != "ping") return;
            if (message.Kind == "hello")
            {
                if (unauthenticatedPeerDeadline.IsExpired(peer))
                    throw new InvalidOperationException("未在允许时间内完成身份认证，请重新连接。");
                if (peers.ContainsKey(peer)) throw new InvalidOperationException("Connection is already authenticated.");
                Hello hello = JsonData.Payload<Hello>(message.Payload);
                if (World.RevokedCredentialIds.Contains(hello.CredentialId)) throw new InvalidOperationException("该旧凭证已由房主撤销绑定。");
                if (string.IsNullOrWhiteSpace(hello.CredentialId) || hello.CredentialId.Length > 128) throw new InvalidDataException("Invalid credential identifier.");
                string nonce = CryptoData.RandomToken();
                challenges[peer] = new PeerAuthentication { CredentialId = hello.CredentialId, Nonce = nonce, Nickname = hello.Nickname };
                Send(peer, "challenge", new Challenge { Nonce = nonce, Enroll = !World.Players.Values.Any(x => x.CredentialId == hello.CredentialId) });
                return;
            }
            if (message.Kind == "auth")
            {
                if (unauthenticatedPeerDeadline.IsExpired(peer))
                    throw new InvalidOperationException("未在允许时间内完成身份认证，请重新连接。");
                AuthenticatePeer(peer, JsonData.Payload<Authenticate>(message.Payload)); return;
            }
            string playerId;
            if (!peers.TryGetValue(peer, out playerId)) throw new InvalidOperationException("Player handshake has not completed.");
            if (message.SessionId != SessionId) throw new InvalidOperationException("旧房间会话的消息已失效。");
            switch (message.Kind)
            {
                case "galaxyReset": galaxyResetRequests.RequestReset(playerId, bridge.ResetGalaxyRecipient); break;
                case "ping": Send(peer, "pong", null); break;
                case "pong": break;
                case "finalDone": finalCheckpointPeers.Remove(peer); break;
                case "leave":
                    RemovePeer(peer);
                    var left = CommandResult.Ok("leave");
                    try { SaveOrThrow(); left.Saved = true; left.SaveGeneration = World.SaveGeneration; }
                    catch (Exception ex) { left.Success = false; left.Error = "退出检查点已接收，保存失败：" + ex.Message; }
                    Send(peer, "leaveAck", left); break;
                case "command":
                    GameCommand command = JsonData.Payload<GameCommand>(message.Payload);
                    command.PlayerId = playerId; command.SessionId = SessionId;
                    if (command.Kind == CommandKind.FleetCommand)
                        FleetCommandTrace.Emit("host.received", command.CommandId, command.EntityId, true, "queued");
                    CommandResult result = ExecuteHostCommand(command);
                    string replyKey = TransactionKey(command);
                    DeferredCommandReplyMap.Update(commandReplyPeers, replyKey, peer, result,
                        pendingExecutions.ContainsKey(replyKey));
                    TraceHostFleetResult(command, result);
                    Send(peer, "result", result);
                    break;
                case "map": AcceptMapUpdate(playerId, peer, JsonData.Payload<MapUpdate>(message.Payload)); break;
                case "ready": ConfirmAuthority(playerId, JsonData.Payload<AuthorityReady>(message.Payload)); break;
                case "resync":
                    MapReference reference = JsonData.Payload<MapReference>(message.Payload);
                    MapRecord resync;
                    if (!World.Maps.TryGetValue(reference.MapId, out resync) || !resync.Visitors.Contains(playerId)) throw new InvalidOperationException("只能重同步自己正在访问的地图。");
                    SendMapView(resync, playerId); break;
                case "uids":
                    ReceiveUidRangeRequest(peer, playerId, message.RequestId);
                    break;
                case "execution": AcceptExecution(playerId, JsonData.Payload<CommandResult>(message.Payload)); break;
                case "personalUpdate": AcceptPersonalUpdate(playerId, peer, JsonData.Payload<PlayerUpdate>(message.Payload)); break;
                case "owner":
                    OwnerRegistration owner = JsonData.Payload<OwnerRegistration>(message.Payload);
                    MapRecord ownerMap;
                    bool assigned = World.Players[playerId].IssuedUidRanges.Any(x => x.SessionId == SessionId && owner.EntityId >= x.First && owner.EntityId <= x.Last);
                    if (!assigned || !World.Maps.TryGetValue(owner.MapId, out ownerMap) || ownerMap.SimulatorPlayerId != playerId || ownerMap.AuthorityEpoch != owner.AuthorityEpoch
                        || string.IsNullOrEmpty(owner.PlayerId) || !World.Players.ContainsKey(owner.PlayerId)
                        || (owner.PlayerId != playerId && owner.PlayerId != ownerMap.OwnerPlayerId && !ownerMap.Visitors.Contains(owner.PlayerId)))
                        throw new InvalidOperationException("新实体归属登记不匹配号段、模拟任期或地表玩家归属。");
                    RegisterEntityOwner(owner.EntityId, owner.PlayerId); break;
                default: throw new InvalidDataException("Unknown client message: " + message.Kind);
            }
        }
        private void AuthenticatePeer(string peer, Authenticate authentication)
        {
            PeerAuthentication challenge;
            if (World.RevokedCredentialIds.Contains(authentication.CredentialId)) throw new InvalidOperationException("该旧凭证已由房主撤销绑定。");
            if (!challenges.TryGetValue(peer, out challenge) || challenge.CredentialId != authentication.CredentialId) throw new InvalidOperationException("身份挑战不存在或已经失效。");
            challenges.Remove(peer);
            PlayerRecord credentialPlayer = World.Players.Values.FirstOrDefault(x => x.CredentialId == authentication.CredentialId);
            string verifier = credentialPlayer == null ? authentication.Verifier : credentialPlayer.CredentialHash;
            string proof = CryptoData.Proof(verifier, SessionId + ":" + challenge.Nonce + ":" + authentication.CredentialId);
            if (!CryptoData.Equal(proof, authentication.Proof)) throw new InvalidOperationException("玩家身份凭证不匹配。");
            string steamAccount = RemoteSteamAccount(peer);
            PlayerRecord player = ResolveRemoteSteamAccount(credentialPlayer, steamAccount);
            bool newPlayer = player == null;
            if (player != null && player.Online) throw new InvalidOperationException("该身份已有有效连接。");
            if (World.Players.Values.Count(x => x.Online) >= World.Options.MaxPlayers) throw new InvalidOperationException("房间人数已满。");
            if (newPlayer)
            {
                player = NewPlayer(Guid.NewGuid().ToString("N"), authentication.CredentialId, verifier, challenge.Nickname);
                World.Players.Add(player.PlayerId, player);
            }
            if (!player.InitialAssetsGranted)
            {
                player.Uids = ReserveUidRange(false);
                PlayerSnapshot snapshot = null;
                try
                {
                    ApplyCriticalNative(() => bridge.CreatePlayer(player.PlayerId, player.Uids, out snapshot), "为新玩家创建独立母舰与初始资产");
                    ApplyPlayerRecord(player, snapshot);
                    player.InitialAssetsGranted = true;
                    player.CurrentMapId = player.MotherShipMapId;
                    RegisterMotherShip(player);
                }
                catch (Exception ex) { if (nativeFault == null) LockNativeFault(ex.Message); throw; }
            }
            else player.Uids = ReserveUidRange(false);
            player.IssuedUidRanges.Add(player.Uids);
            player.CurrentMapId = ReconnectMap(player);
            // Save before confirming either first assets or newly allocated IDs. A failed handshake
            // keeps the exact player record in host memory; a retry never grants another asset set.
            SaveWithSteamAccountBinding(player, steamAccount, newPlayer);
            CompleteAuthenticatedJoin(peer, player);
        }
        private long ReconnectMap(PlayerRecord player)
        {
            AttackRecord defence = World.Attacks.Values.Where(a => a.DefenderPlayerId == player.PlayerId && a.Stage != AttackStage.Finished)
                .OrderByDescending(a => a.ElapsedSeconds).ThenBy(a => a.MapId).FirstOrDefault();
            return defence == null ? player.CurrentMapId : defence.MapId;
        }
        private WorldState PublicWorld(string recipient)
        {
            WorldState view = JsonData.Clone(World);
            view.Transactions.Clear();
            foreach (PlayerRecord player in view.Players.Values)
            {
                player.CredentialId = null; player.CredentialHash = null;
                if (player.PlayerId != recipient) player.Uids = null;
                player.IssuedUidRanges.Clear();
            }
            return view;
        }
        private void HandleClientMessage(WireMessage message)
        {
            if (message.Kind == "challenge")
            {
                SessionId = message.SessionId;
                Challenge challenge = JsonData.Payload<Challenge>(message.Payload);
                string verifier = CryptoData.Hash(identity.Secret);
                Send("host", "auth", new Authenticate { CredentialId = identity.CredentialId, Nickname = identity.Nickname, Verifier = challenge.Enroll ? verifier : null, Proof = CryptoData.Proof(verifier, SessionId + ":" + challenge.Nonce + ":" + identity.CredentialId) });
                return;
            }
            if (message.Kind == "welcome")
            {
                if (message.SessionId != SessionId) throw new InvalidOperationException("Welcome is for a different host session.");
                Welcome welcome = JsonData.Payload<Welcome>(message.Payload);
                World = welcome.World; LocalPlayerId = welcome.PlayerId;
                World.Players[LocalPlayerId].Uids = welcome.Uids;
                AttachBridge();
                Require(bridge.LoadWorld(World.Snapshot, World.Players[LocalPlayerId].Snapshot), "载入房主共享世界");
                foreach (PlayerRecord player in World.Players.Values)
                {
                    Require(bridge.ApplyPlayer(player.Snapshot, false), "恢复独立玩家记录");
                    Require(bridge.SetPlayerOnline(player.PlayerId, player.Online), "应用舰队在线状态");
                }
                foreach (RelationshipRecord relation in World.Relationships) Require(bridge.ApplyRelationship(relation), "应用玩家关系");
                IsConnected = true; SetStatus("已加入：" + World.Name); return;
            }
            if (message.SessionId != SessionId || World == null) throw new InvalidOperationException("旧房间会话的消息已失效。");
            switch (message.Kind)
            {
                case "ping": Send("host", "pong", null); break;
                case "pong": break;
                case "policy": ApplyPolicy(JsonData.Payload<SessionPolicy>(message.Payload)); break;
                case "playerJoined":
                    PlayerRecord introduced = JsonData.Payload<PlayerIntroduction>(message.Payload).Player;
                    PlayerRecord known;
                    if (!World.Players.TryGetValue(introduced.PlayerId, out known) || known.Snapshot == null)
                    {
                        World.Players[introduced.PlayerId] = introduced;
                        Require(bridge.ApplyPlayer(introduced.Snapshot, false), "初始化新加入玩家的独立上下文");
                        ApplyPlayerRecord(introduced, introduced.Snapshot);
                    }
                    Require(bridge.SetPlayerOnline(introduced.PlayerId, introduced.Online), "显示新加入玩家舰队");
                    break;
                case "error": Error(JsonData.Payload<string>(message.Payload)); break;
                case "clock":
                    ClockState clock = JsonData.Payload<ClockState>(message.Payload);
                    Paused = clock.Paused; Speed = clock.Speed; World.WorldTimeMM = clock.WorldTimeMM; World.RoomRealSeconds = clock.RoomRealSeconds; break;
                case "raidClock":
                    RaidClockState raidClock = JsonData.Payload<RaidClockState>(message.Payload);
                    foreach (AttackRecord active in raidClock.Active) World.Attacks[active.AttackId] = active;
                    foreach (MapRecord timedMap in World.Maps.Values) timedMap.RaidCooldownSeconds = raidClock.Cooldowns.ContainsKey(timedMap.MapId) ? raidClock.Cooldowns[timedMap.MapId] : 0;
                    break;
                case "galaxy": Require(bridge.ApplyGalaxy(JsonData.Payload<GalaxySnapshot>(message.Payload)), "同步同星系舰队"); break;
                case "view": ReceiveMapView(JsonData.Payload<MapView>(message.Payload)); break;
                case "map": ReceiveMapUpdate(JsonData.Payload<MapUpdate>(message.Payload)); break;
                case "mapRejected":
                    var rejection = JsonData.Payload<AuthorityReady>(message.Payload);
                    inFlightUpdates.Remove(rejection.MapId); Error("地图提交版本过期，等待房主重发完整状态。"); break;
                case "uids": ReceiveUidRenewal(message); break;
                case "personal":
                    PlayerUpdate personal = JsonData.Payload<PlayerUpdate>(message.Payload);
                    ReceiveCommittedPersonal(personal); break;
                case "personalAck":
                    PlayerUpdate personalAck = JsonData.Payload<PlayerUpdate>(message.Payload);
                    ReceivePersonalAcknowledgement(personalAck); break;
                case "personalReset":
                    PlayerFieldState reset = JsonData.Payload<PlayerFieldState>(message.Payload);
                    CompletePersonalReset(reset); break;
                case "execute": ExecuteOnSimulator(JsonData.Payload<GameCommand>(message.Payload)); break;
                case "result": NotifyCommandResult(JsonData.Payload<CommandResult>(message.Payload)); break;
                case "saved": World.SaveGeneration = JsonData.Payload<long>(message.Payload); break;
                case "leaveAck":
                    CommandResult left = JsonData.Payload<CommandResult>(message.Payload);
                    leaveAcknowledged = true;
                    if (left.Saved) { World.SaveGeneration = left.SaveGeneration; SetStatus("退出前检查点已由房主保存"); }
                    else Error(left.Error ?? "房主未确认退出前进度已保存。");
                    break;
                case "closing": Paused = true; PublishLocalMaps(); SetStatus(JsonData.Payload<string>(message.Payload)); break;
                case "checkpointRequest": Paused = true; finalCheckpointRequested = true; PublishLocalMaps(); SetStatus(JsonData.Payload<string>(message.Payload)); break;
                case "closed":
                    string closedReason = JsonData.Payload<string>(message.Payload);
                    if (IsConnected && !closing) HostLeft = true;
                    IsConnected = false; Paused = true;
                    try { CompleteDisconnect(); }
                    finally { SetStatus(closedReason); }
                    break;
                default: throw new InvalidDataException("Unknown host message: " + message.Kind);
            }
        }
        private void RemovePeer(string peer)
        {
            RemovePeerAuthorityFirst(peer);
        }
        private void BroadcastPolicy()
        {
            if (!IsHost) return;
            var players = World.Players.Values.Select(x => new PlayerRecord { PlayerId = x.PlayerId, Nickname = x.Nickname, InitialAssetsGranted = x.InitialAssetsGranted, Online = x.Online, PersonalRevision = x.PersonalRevision, MotherShipMapId = x.MotherShipMapId, FleetId = x.FleetId, FormalBaseMapId = x.FormalBaseMapId, CurrentMapId = x.CurrentMapId, GalaxyId = x.GalaxyId }).ToList();
            var maps = new Dictionary<long, MapRecord>();
            foreach (MapRecord map in World.Maps.Values)
                maps[map.MapId] = new MapRecord { MapId = map.MapId, OwnerPlayerId = map.OwnerPlayerId, SimulatorPlayerId = map.SimulatorPlayerId, AuthorityEpoch = map.AuthorityEpoch, Revision = map.Revision, LastSimulatedWorldMM = map.LastSimulatedWorldMM, Mode = map.Mode, Visitors = new List<string>(map.Visitors), RaidCooldownSeconds = map.RaidCooldownSeconds, ActiveAttackId = map.ActiveAttackId, AwaitingAuthorityReady = map.AwaitingAuthorityReady };
            Broadcast("policy", new SessionPolicy { Players = new ListPolicyPlayers { Items = players }, Maps = maps, EntityOwners = World.EntityOwners, Relationships = World.Relationships, Attacks = World.Attacks, Transfers = World.Transfers });
        }
        private void ApplyPolicy(SessionPolicy policy)
        {
            foreach (PlayerRecord update in policy.Players.Items)
            {
                PlayerRecord player;
                if (!World.Players.TryGetValue(update.PlayerId, out player)) { player = update; World.Players.Add(player.PlayerId, player); }
                else { player.Online = update.Online; player.CurrentMapId = update.CurrentMapId; player.FormalBaseMapId = update.FormalBaseMapId; player.Nickname = update.Nickname; }
                Require(bridge.SetPlayerOnline(player.PlayerId, player.Online), "同步离线舰队状态");
            }
            foreach (var entry in policy.Maps)
            {
                MapRecord old;
                if (World.Maps.TryGetValue(entry.Key, out old))
                {
                    entry.Value.Checkpoint = old.Checkpoint;
                    if (old.AuthorityEpoch == entry.Value.AuthorityEpoch) entry.Value.Revision = old.Revision;
                }
                World.Maps[entry.Key] = entry.Value;
            }
            World.EntityOwners = policy.EntityOwners; World.Attacks = policy.Attacks;
            World.Transfers = policy.Transfers ?? new Dictionary<string, TransferRecord>();
            World.Relationships = policy.Relationships;
            foreach (RelationshipRecord relation in policy.Relationships) Require(bridge.ApplyRelationship(relation), "同步关系并清理过期合作任务");
        }
    }
}
