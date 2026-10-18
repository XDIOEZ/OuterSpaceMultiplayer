using System;
using System.Collections.Generic;
using System.Linq;

namespace FreeWorldMultiplayer
{
    internal static class DeferredCommandReplyMap
    {
        internal static void RemoveForPeer(Dictionary<string, string> replies, string peer)
        {
            foreach (string key in replies.Where(x => x.Value == peer).Select(x => x.Key).ToList())
                replies.Remove(key);
        }
    }
    internal sealed class FakeGalaxyResetRequests
    {
        internal readonly HashSet<string> Pending = new HashSet<string>();
        internal void ForgetPlayer(string player) { Pending.Remove(player); }
    }
    internal sealed class FakeBridge
    {
        internal readonly HashSet<string> Online = new HashSet<string>();
        internal readonly List<long> NativeFrozenMaps = new List<long>();
        internal readonly HashSet<long> BadFreezes = new HashSet<long>();
        internal int OfflineCalls;
        internal bool FailOffline;
        internal BridgeResult SetPlayerOnline(string playerId, bool online)
        {
            if (!online)
            {
                OfflineCalls++;
                if (FailOffline) return BridgeResult.Fail("fake native offline unavailable");
                Online.Remove(playerId);
            }
            else Online.Add(playerId);
            return BridgeResult.Ok();
        }
        internal BridgeResult SetMapAuthority(MapAccess access)
        {
            if (access.Access != MapAccessMode.Frozen)
                return BridgeResult.Fail("test only calls frozen map authority");
            if (BadFreezes.Contains(access.MapId)) return BridgeResult.Fail("native map cannot freeze");
            NativeFrozenMaps.Add(access.MapId);
            return BridgeResult.Ok();
        }
    }

    public sealed partial class MultiplayerSession
    {
        internal const string Leaver = "leaver";
        internal const string Peer = "socket-leaver";
        internal const string Other = "other";

        private readonly Dictionary<string, string> challenges = new Dictionary<string, string>();
        private readonly Dictionary<string, string> rejectedPeers = new Dictionary<string, string>();
        private readonly Dictionary<string, string> commandReplyPeers = new Dictionary<string, string>();
        private readonly Dictionary<string, string> peers = new Dictionary<string, string>();
        private readonly Dictionary<string, string> playerPeers = new Dictionary<string, string>();
        private readonly FakeGalaxyResetRequests galaxyResetRequests = new FakeGalaxyResetRequests();
        private readonly Dictionary<string, GameCommand> pendingExecutions = new Dictionary<string, GameCommand>();
        private readonly FakeBridge bridge = new FakeBridge();
        private readonly WorldState World = new WorldState();
        private string nativeFault;
        private bool Paused;
        internal readonly List<string> CallOrder = new List<string>();
        internal readonly List<string> ErrorMessages = new List<string>();
        internal bool FailCache, FailCommunication, FailBattle, FailPolicy, FailStatus, FailError, FailLock;
        internal long FailHandoffMap = -1;
        internal bool FailHandoffAfterChangingMap;
        internal int CacheCalls, ChatCalls, BattleCalls, HandoffCalls, PolicyCalls, StatusCalls;

        internal MultiplayerSession()
        {
            World.Players.Add(Leaver, new PlayerRecord { PlayerId = Leaver, Nickname = "PlayerOne", Online = true });
            World.Players.Add(Other, new PlayerRecord { PlayerId = Other, Nickname = "PlayerTwo", Online = true });
            bridge.Online.Add(Leaver);
            bridge.Online.Add(Other);
            peers[Peer] = Leaver;
            playerPeers[Leaver] = Peer;
            peers["socket-other"] = Other;
            playerPeers[Other] = "socket-other";
            challenges[Peer] = "challenge";
            rejectedPeers[Peer] = "obsolete";
            commandReplyPeers["reply-one"] = Peer;
            commandReplyPeers["reply-other"] = "socket-other";
            galaxyResetRequests.Pending.Add(Leaver);
            pendingExecutions["old-command"] = new GameCommand { CommandId = "old-command", PlayerId = Leaver };
            pendingExecutions["other-command"] = new GameCommand { CommandId = "other-command", PlayerId = Other };
        }

        private static void Require(BridgeResult result, string context)
        {
            if (result == null || !result.Success)
                throw new InvalidOperationException(context + " -> " + (result == null ? "null" : result.Error));
        }
        private void ForgetMapCachePeer(string player)
        {
            CacheCalls++; CallOrder.Add("cache");
            if (FailCache) throw new InvalidOperationException("cache cleanup failed");
        }
        private void DisconnectCommunication(string player, bool delayed)
        {
            ChatCalls++; CallOrder.Add("chat");
            if (FailCommunication) throw new InvalidOperationException("chat callback failed");
        }
        private void DisconnectPlayerFleetBattles(string player, bool delayed)
        {
            BattleCalls++; CallOrder.Add("battle");
            if (FailBattle) throw new InvalidOperationException("battle settlement failed");
        }
        private void ReassignMap(MapRecord map)
        {
            HandoffCalls++;
            CallOrder.Add("handoff:" + map.MapId);
            if (FailHandoffMap == map.MapId && !FailHandoffAfterChangingMap)
                throw new InvalidOperationException("map checkpoint cannot be captured");
            map.AuthorityEpoch++;
            map.AwaitingAuthorityReady = false;
            map.SimulatorPlayerId = map.Visitors.Contains(Other) ? Other : null;
            map.Mode = map.SimulatorPlayerId == null ? MapMode.Frozen : MapMode.Realtime;
            if (FailHandoffMap == map.MapId && FailHandoffAfterChangingMap)
                throw new InvalidOperationException("map visitor notification failed");
        }
        private void BroadcastPolicy()
        {
            PolicyCalls++; CallOrder.Add("policy");
            if (FailPolicy) throw new InvalidOperationException("broadcast failed");
        }
        private void SetStatus(string status)
        {
            StatusCalls++;
            if (FailStatus) throw new InvalidOperationException("UI status callback failed");
        }
        private void Error(string message)
        {
            ErrorMessages.Add(message);
            if (FailError) throw new InvalidOperationException("log callback failed");
        }
        private void LockNativeFault(string message)
        {
            nativeFault = message;
            Paused = true;
            if (FailLock) throw new InvalidOperationException("clock broadcast failed");
        }

        internal WorldState TestWorld { get { return World; } }
        internal FakeBridge TestBridge { get { return bridge; } }
        internal bool IsPaused { get { return Paused; } }
        internal string NativeFailure { get { return nativeFault; } }
        internal bool HasLeaverPeer { get { return peers.ContainsKey(Peer); } }
        internal bool HasOtherPeer { get { return peers.ContainsKey("socket-other"); } }
        internal bool HasOtherRouting { get { return playerPeers.ContainsKey(Other); } }
        internal int PendingCommands { get { return pendingExecutions.Count; } }
        internal bool HasCommand(string key) { return pendingExecutions.ContainsKey(key); }
        internal bool HasChallenge { get { return challenges.ContainsKey(Peer); } }
        internal bool HasReply(string key) { return commandReplyPeers.ContainsKey(key); }
        internal bool HasReset { get { return galaxyResetRequests.Pending.Contains(Leaver); } }
        internal bool HasRoomMapping { get { return playerPeers.ContainsKey(Leaver); } }
        internal MapRecord AddMap(long id, bool leaverSimulator, bool otherVisitor)
        {
            var map = new MapRecord {
                MapId = id, OwnerPlayerId = Leaver,
                SimulatorPlayerId = leaverSimulator ? Leaver : Other,
                AuthorityEpoch = 9, Revision = 41,
                Mode = MapMode.Realtime,
                AwaitingAuthorityReady = true,
                Visitors = new List<string> { Leaver }
            };
            if (otherVisitor) map.Visitors.Add(Other);
            World.Maps.Add(id, map);
            return map;
        }
        internal void Remove()
        {
            rejectedPeers.Remove(Peer);
            RemovePeerAuthorityFirst(Peer);
        }
        internal void RemoveUnknown() { RemovePeerAuthorityFirst("never-connected"); }
        internal void AddForeignRouting()
        {
            playerPeers[Leaver] = "new-peer-same-user";
        }
    }
}
