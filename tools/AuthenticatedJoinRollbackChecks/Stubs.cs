using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    public sealed class PlayerRecord
    {
        public string PlayerId, Nickname, CredentialId, CredentialHash;
        public object Uids;
        public readonly List<string> IssuedUidRanges = new List<string>();
        public bool Online, InitialAssetsGranted;
        public long CurrentMapId;
        public int AssetCount;
    }
    internal static class JsonData
    {
        internal static T Clone<T>(T source)
        {
            var player = source as PlayerRecord;
            if (player == null) return source;
            var result = new PlayerRecord {
                PlayerId = player.PlayerId, Nickname = player.Nickname,
                CredentialId = player.CredentialId, CredentialHash = player.CredentialHash,
                Uids = player.Uids, Online = player.Online,
                InitialAssetsGranted = player.InitialAssetsGranted,
                AssetCount = player.AssetCount, CurrentMapId = player.CurrentMapId
            };
            result.IssuedUidRanges.AddRange(player.IssuedUidRanges);
            return (T)(object)result;
        }
    }
    internal sealed class PlayerIntroduction { internal PlayerRecord Player; }
    internal sealed class Welcome { internal string PlayerId; internal object World, Uids; }
    internal interface ITransportPeerAdmission { void MarkAuthenticated(string peer); }
    internal sealed class BridgeResult
    {
        internal bool Success;
    }
    internal sealed class FakeBridge
    {
        internal readonly HashSet<string> NativeOnline = new HashSet<string>(StringComparer.Ordinal);
        internal bool FailOnline, FailOffline;
        internal int OnlineAttempts, OfflineAttempts;
        internal BridgeResult SetPlayerOnline(string playerId, bool online)
        {
            if (online)
            {
                OnlineAttempts++;
                if (FailOnline) return new BridgeResult { Success = false };
                NativeOnline.Add(playerId);
            }
            else
            {
                OfflineAttempts++;
                if (FailOffline) return new BridgeResult { Success = false };
                NativeOnline.Remove(playerId);
            }
            return new BridgeResult { Success = true };
        }
    }
    internal sealed class FakeTransport : ITransportPeerAdmission
    {
        internal bool FailWelcome, FailAdmission, FailDisconnect;
        internal bool Connected = true, Authenticated;
        internal int WelcomeAttempts, Admitted, Disconnections;
        internal object WelcomePayload;
        public void MarkAuthenticated(string peer)
        {
            Admitted++;
            if (FailAdmission) throw new InvalidOperationException("auth admission failed");
            Authenticated = true;
        }
        internal void Disconnect(string peer, string reason)
        {
            Disconnections++;
            if (FailDisconnect) throw new InvalidOperationException("cannot close socket");
            Connected = false; Authenticated = false;
        }
        internal void Send(string peer, string kind, object payload)
        {
            if (kind != "welcome") throw new InvalidOperationException("unexpected send: " + kind);
            WelcomeAttempts++;
            if (FailWelcome) throw new InvalidOperationException("welcome cannot queue");
            WelcomePayload = payload;
        }
    }

    public sealed partial class MultiplayerSession
    {
        private readonly FakeBridge bridge = new FakeBridge();
        private readonly FakeTransport transport = new FakeTransport();
        private readonly Dictionary<string, string> peers =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> playerPeers =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> challenges = new Dictionary<string, string>();
        private readonly Dictionary<string, string> rejectedPeers = new Dictionary<string, string>();
        private readonly List<string> errors = new List<string>();
        private bool IsHost = true;
        internal bool FailBroadcastJoined, FailBroadcastPolicy, FailEnterMap, FailStatus, FailRemovePeer;
        internal bool ThrowAfterRemovePeerUnlinks, FailError;
        internal bool NativeFaultLocked;
        internal int RemovedPeers, BroadcastJoined, PolicyBroadcasts, MapAdmissions;
        internal string LastStage;
        internal PlayerRecord LastIntroduction;

        private static void Require(BridgeResult result, string reason)
        {
            if (result == null || !result.Success) throw new InvalidOperationException(reason);
        }
        private object PublicWorld(string playerId) { return "public-world-for:" + playerId; }
        private void Broadcast(string kind, object payload)
        {
            if (kind != "playerJoined") throw new InvalidOperationException("unexpected broadcast kind");
            BroadcastJoined++;
            LastIntroduction = ((PlayerIntroduction)payload).Player;
            if (FailBroadcastJoined) throw new InvalidOperationException("join broadcast failed");
        }
        private void BroadcastPolicy()
        {
            PolicyBroadcasts++;
            if (FailBroadcastPolicy) throw new InvalidOperationException("policy broadcast failed");
        }
        private void Send(string peer, string kind, object payload)
        { transport.Send(peer, kind, payload); }
        private void MessageStage(string stage, long mapId = -1, string playerId = null)
        { LastStage = stage; }
        private void EnterMap(PlayerRecord player, long mapId)
        {
            MapAdmissions++;
            if (FailEnterMap) throw new InvalidOperationException("native map entry failed");
        }
        private void SetStatus(string status)
        {
            if (FailStatus) throw new InvalidOperationException("status callback failed");
        }
        private void Error(string reason)
        {
            errors.Add(reason);
            if (FailError) throw new InvalidOperationException("error callback itself failed");
        }
        private void LockNativeFault(string reason) { NativeFaultLocked = true; }
        private void RemovePeer(string peer)
        {
            RemovedPeers++;
            if (FailRemovePeer) throw new InvalidOperationException("RemovePeer failed before cleanup");
            string playerId;
            if (!peers.TryGetValue(peer, out playerId)) return;
            peers.Remove(peer);
            playerPeers.Remove(playerId);
            if (ThrowAfterRemovePeerUnlinks)
                throw new InvalidOperationException("RemovePeer failed after unlink");
            PlayerRecord player = TestPlayer;
            player.Online = false;
            Require(bridge.SetPlayerOnline(playerId, false), "offline native cleanup");
            BroadcastPolicy();
        }
        internal PlayerRecord TestPlayer;
        internal MultiplayerSession()
        {
            TestPlayer = new PlayerRecord {
                PlayerId = "player-one", Nickname = "GUEST",
                CredentialId = "private-id", CredentialHash = "private-hash",
                Uids = new object(), CurrentMapId = 9,
                InitialAssetsGranted = true, AssetCount = 4
            };
            TestPlayer.IssuedUidRanges.Add("first-range");
        }
        internal FakeBridge Native { get { return bridge; } }
        internal FakeTransport Network { get { return transport; } }
        internal IList<string> Errors { get { return errors; } }
        internal int PeerCount { get { return peers.Count; } }
        internal int PlayerRouteCount { get { return playerPeers.Count; } }
        internal bool IsMapped(string peer)
        { return peers.ContainsKey(peer) && playerPeers.ContainsKey(TestPlayer.PlayerId); }
        internal bool HasHandshakeLease(string peer) { return unauthenticatedPeerDeadline.Begin(peer); }
        internal int HandshakeLeases { get { return unauthenticatedPeerDeadline.Count; } }
        internal void Run(string peer) { CompleteAuthenticatedJoin(peer, TestPlayer); }
    }
}
