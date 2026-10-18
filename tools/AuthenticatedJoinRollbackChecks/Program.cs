using System;
using System.Collections.Generic;
using FreeWorldMultiplayer;

internal static class Program
{
    private static int checks;
    private static void Check(bool value, string label)
    {
        if (!value) throw new InvalidOperationException("FAILED: " + label);
        checks++;
    }

    private static Exception Failure(Action action)
    {
        try { action(); return null; }
        catch (Exception error) { return error; }
    }

    private static void PersistedIdentityUnchanged(
        MultiplayerSession room, object uidRange, string label)
    {
        var record = room.TestPlayer;
        Check(record.InitialAssetsGranted && record.AssetCount == 4,
            label + ": first grant and employee count never undone or duplicated");
        Check(record.IssuedUidRanges.Count == 1 && record.IssuedUidRanges[0] == "first-range"
            && ReferenceEquals(record.Uids, uidRange),
            label + ": already-saved UID ownership retained on failed admission");
        Check(record.CredentialId == "private-id" && record.CredentialHash == "private-hash",
            label + ": saved credential binding retained for the same player");
        Check(record.CurrentMapId == 9,
            label + ": authoritative original map is not reset to a new mother ship");
    }

    private static void SuccessfulAdmission()
    {
        var room = new MultiplayerSession();
        object uid = room.TestPlayer.Uids;
        Check(room.HasHandshakeLease("socket-1") && room.HandshakeLeases == 1,
            "transport handshake reservation exists until welcome completion");
        room.Run("socket-1");
        Check(room.TestPlayer.Online && room.Native.NativeOnline.Contains(room.TestPlayer.PlayerId),
            "successful join marks the canonical and native player online");
        Check(room.PeerCount == 1 && room.PlayerRouteCount == 1 && room.IsMapped("socket-1"),
            "successful peer and reverse player maps registered exactly once");
        Check(room.Network.Connected && room.Network.Authenticated &&
            room.Network.WelcomeAttempts == 1 && room.Network.Admitted == 1,
            "original welcome was queued before transport authentication upgraded");
        Check(room.HandshakeLeases == 0 && room.MapAdmissions == 1 &&
            room.PolicyBroadcasts == 1 && room.RemovedPeers == 0,
            "valid join releases the handshake timer and initializes the map once");
        Check(room.LastStage == "auth.first-map-view",
            "diagnostics retain the real first map view stage");
        Check(room.LastIntroduction.CredentialId == null &&
            room.LastIntroduction.CredentialHash == null &&
            room.LastIntroduction.Uids == null &&
            room.LastIntroduction.IssuedUidRanges.Count == 0,
            "broadcast introduction never exposes credential or issued UID ranges");
        var welcome = room.Network.WelcomePayload as Welcome;
        Check(welcome != null && welcome.PlayerId == room.TestPlayer.PlayerId &&
            ReferenceEquals(welcome.Uids, uid),
            "successful welcome keeps original player identity and existing UID capacity");
        PersistedIdentityUnchanged(room, uid, "successful host admission");
    }

    private static void InvariantAfterFailedJoin(
        MultiplayerSession room, object uid, string stage)
    {
        Check(!room.TestPlayer.Online, stage + ": no ghost Online flag");
        Check(room.PeerCount == 0 && room.PlayerRouteCount == 0 &&
            !room.IsMapped("socket-1"), stage + ": no stale peer/player reverse identity");
        Check(!room.Native.NativeOnline.Contains(room.TestPlayer.PlayerId),
            stage + ": native player's fleet was explicitly frozen");
        Check(room.Network.Disconnections == 1 && !room.Network.Connected,
            stage + ": uncommitted login socket was disconnected");
        Check(room.HandshakeLeases == 0,
            stage + ": disconnected peer does not retain an unauthenticated slot");
        PersistedIdentityUnchanged(room, uid, stage);
    }

    private static void FailureAtEachStage()
    {
        string[] stages = {
            "native-online", "announce", "welcome", "admission",
            "map", "policy", "status"
        };
        foreach (string stage in stages)
        {
            var room = new MultiplayerSession();
            var uid = room.TestPlayer.Uids;
            room.HasHandshakeLease("socket-1");
            room.Native.FailOnline = stage == "native-online";
            room.FailBroadcastJoined = stage == "announce";
            room.Network.FailWelcome = stage == "welcome";
            room.Network.FailAdmission = stage == "admission";
            room.FailEnterMap = stage == "map";
            room.FailBroadcastPolicy = stage == "policy";
            room.FailStatus = stage == "status";

            Exception failure = Failure(() => room.Run("socket-1"));
            Check(failure is InvalidOperationException,
                stage + ": real injected admission fault reaches host rejection pipeline");
            InvariantAfterFailedJoin(room, uid, stage);
            Check(room.Native.OfflineAttempts >= 1,
                stage + ": even pre-welcome exceptions undo native visibility");
            Check(room.RemovedPeers == (stage == "native-online" || stage == "announce" ? 0 : 1),
                stage + ": linked users get normal disconnect cleanup, unlinked get native rollback");

            // Reconnect with identical saved identity. The saved grant is not
            // recreated, and no old socket or UID range is reused as new assets.
            room.Native.FailOnline = false;
            room.FailBroadcastJoined = false;
            room.Network.FailWelcome = false;
            room.Network.FailAdmission = false;
            room.FailEnterMap = false;
            room.FailBroadcastPolicy = false;
            room.FailStatus = false;
            room.Network.Connected = true;
            room.Network.Authenticated = false;
            room.HasHandshakeLease("socket-reconnected");
            Check(Failure(() => room.Run("socket-reconnected")) == null,
                stage + ": same identity can join again when the fault recovers");
            Check(room.TestPlayer.Online && room.Native.NativeOnline.Contains(room.TestPlayer.PlayerId)
                && room.IsMapped("socket-reconnected"),
                stage + ": reconnected user recovers the one authoritative fleet");
            PersistedIdentityUnchanged(room, uid, stage + " recovered");
        }
    }

    private static void SecondaryFailureStillPreservesFirstCause()
    {
        var removeFailed = new MultiplayerSession();
        removeFailed.HasHandshakeLease("socket-1");
        removeFailed.Network.FailWelcome = true;
        removeFailed.FailRemovePeer = true;
        Exception first = Failure(() => removeFailed.Run("socket-1"));
        Check(first != null && first.Message == "welcome cannot queue",
            "RemovePeer exception never masks the original welcome failure");
        Check(removeFailed.Errors.Count != 0 && !removeFailed.TestPlayer.Online &&
            removeFailed.PeerCount == 0 && removeFailed.PlayerRouteCount == 0,
            "failed normal removal reports a problem and still clears canonical ownership");
        Check(!removeFailed.Native.NativeOnline.Contains(removeFailed.TestPlayer.PlayerId)
            && removeFailed.Native.OfflineAttempts >= 1,
            "failed RemovePeer invokes fallback native freeze");

        var afterUnlink = new MultiplayerSession();
        afterUnlink.HasHandshakeLease("socket-1");
        afterUnlink.Network.FailWelcome = true;
        afterUnlink.ThrowAfterRemovePeerUnlinks = true;
        first = Failure(() => afterUnlink.Run("socket-1"));
        Check(first != null && first.Message == "welcome cannot queue"
            && afterUnlink.PeerCount == 0 && afterUnlink.PlayerRouteCount == 0,
            "cleanup fault after partial unlink remains retry-safe");
        Check(afterUnlink.Native.OfflineAttempts > 0 &&
            !afterUnlink.Native.NativeOnline.Contains("player-one"),
            "partial unlink still freezes native player during rollback");

        var nativeFault = new MultiplayerSession();
        nativeFault.HasHandshakeLease("socket-1");
        nativeFault.FailBroadcastJoined = true;
        nativeFault.Native.FailOffline = true;
        first = Failure(() => nativeFault.Run("socket-1"));
        Check(first != null && first.Message == "join broadcast failed" &&
            nativeFault.NativeFaultLocked,
            "cannot freeze native fleet locks room authority without masking the first error");
        Check(!nativeFault.TestPlayer.Online && nativeFault.PeerCount == 0,
            "even unrecoverable native failure never leaves a canonical ghost online");

        var socketFailed = new MultiplayerSession();
        socketFailed.HasHandshakeLease("socket-1");
        socketFailed.Network.FailWelcome = true;
        socketFailed.Network.FailDisconnect = true;
        first = Failure(() => socketFailed.Run("socket-1"));
        Check(first != null && first.Message == "welcome cannot queue",
            "secondary socket close exception retains original login failure");
        Check(socketFailed.HandshakeLeases == 1 && socketFailed.Errors.Count != 0 &&
            socketFailed.PeerCount == 0 && !socketFailed.TestPlayer.Online,
            "failed socket close leaves an untrusted timeout for eventual eviction");

        var afterAuthenticated = new MultiplayerSession();
        afterAuthenticated.HasHandshakeLease("socket-1");
        afterAuthenticated.FailEnterMap = true;
        afterAuthenticated.Network.FailDisconnect = true;
        first = Failure(() => afterAuthenticated.Run("socket-1"));
        Check(first != null && first.Message == "native map entry failed"
            && afterAuthenticated.HandshakeLeases == 1,
            "failed disconnect after MarkAuthenticated re-arms the untrusted cleanup timer");

        var loggingFailed = new MultiplayerSession();
        loggingFailed.HasHandshakeLease("socket-1");
        loggingFailed.Network.FailWelcome = true;
        loggingFailed.FailRemovePeer = true;
        loggingFailed.FailError = true;
        first = Failure(() => loggingFailed.Run("socket-1"));
        Check(first != null && first.Message == "welcome cannot queue" &&
            !loggingFailed.TestPlayer.Online,
            "secondary diagnostics callback cannot mask real authentication failure");
    }

    private static void RandomFaultRecovery()
    {
        var rng = new Random(20261018);
        for (int round = 0; round < 12000; round++)
        {
            var room = new MultiplayerSession();
            var uid = room.TestPlayer.Uids;
            int stage = rng.Next(0, 7);
            room.Native.FailOnline = stage == 0;
            room.FailBroadcastJoined = stage == 1;
            room.Network.FailWelcome = stage == 2;
            room.Network.FailAdmission = stage == 3;
            room.FailEnterMap = stage == 4;
            room.FailBroadcastPolicy = stage == 5;
            room.FailStatus = stage == 6;
            room.HasHandshakeLease("socket-1");
            Exception first = Failure(() => room.Run("socket-1"));
            Check(first != null && !room.TestPlayer.Online &&
                !room.Native.NativeOnline.Contains("player-one"),
                "random fault leaves zero native/host online ghosts " + round);
            Check(room.PeerCount == 0 && room.PlayerRouteCount == 0 &&
                room.Network.Disconnections == 1,
                "random fault cleans the exact peer identity once " + round);
            Check(room.TestPlayer.AssetCount == 4 && room.TestPlayer.InitialAssetsGranted &&
                ReferenceEquals(room.TestPlayer.Uids, uid) && room.TestPlayer.IssuedUidRanges.Count == 1,
                "random fault cannot duplicate/erase initial grants and UID ranges " + round);
        }
    }

    private static int Main()
    {
        try
        {
            SuccessfulAdmission();
            FailureAtEachStage();
            SecondaryFailureStillPreservesFirstCause();
            RandomFaultRecovery();
            Console.WriteLine("PASS: " + checks + " production-linked half-join rollback checks");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
