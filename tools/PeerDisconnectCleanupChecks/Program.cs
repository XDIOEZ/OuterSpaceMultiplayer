using System;
using System.Collections.Generic;
using FreeWorldMultiplayer;

internal static class Program
{
    private static int checks;

    private static void Check(bool good, string message)
    {
        if (!good) throw new InvalidOperationException("FAILED: " + message);
        checks++;
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    private static void VerifyCommonCleanup(MultiplayerSession room, string label)
    {
        Check(!room.HasLeaverPeer && !room.HasRoomMapping,
            label + ": disconnected player's peer and reverse route both removed");
        Check(room.HasOtherPeer && room.HasOtherRouting &&
            room.TestWorld.Players[MultiplayerSession.Other].Online,
            label + ": other player's session and routes are untouched");
        Check(!room.TestWorld.Players.ContainsKey(MultiplayerSession.Leaver) ||
            !room.TestWorld.Players[MultiplayerSession.Leaver].Online,
            label + ": host no longer treats disconnected player as online");
        Check(!room.HasChallenge && !room.HasReply("reply-one") &&
            room.HasReply("reply-other") && !room.HasReset,
            label + ": stale challenge, reply route and galaxy reset released");
        Check(!room.HasCommand("old-command") && room.HasCommand("other-command"),
            label + ": only departed player's pending executions removed");
        foreach (MapRecord map in room.TestWorld.Maps.Values)
        {
            Check(map.Visitors == null ||
                !map.Visitors.Contains(MultiplayerSession.Leaver),
                label + ": offline player cannot remain a map visitor " + map.MapId);
            Check(map.SimulatorPlayerId != MultiplayerSession.Leaver,
                label + ": offline player cannot retain map simulator rights " + map.MapId);
        }
    }

    private static void HealthyDisconnect()
    {
        var room = new MultiplayerSession();
        MapRecord reassigned = room.AddMap(10, true, true);
        MapRecord frozen = room.AddMap(11, true, false);
        MapRecord unaffected = room.AddMap(12, false, true);
        room.Remove();

        VerifyCommonCleanup(room, "normal");
        Check(room.NativeFailure == null && !room.IsPaused,
            "healthy disconnect does not lock host authority or pause the room");
        Check(room.TestBridge.OfflineCalls == 1 &&
            !room.TestBridge.Online.Contains(MultiplayerSession.Leaver),
            "real native player offline hook ran exactly once");
        Check(reassigned.AuthorityEpoch == 10 &&
            reassigned.SimulatorPlayerId == MultiplayerSession.Other &&
            reassigned.AwaitingAuthorityReady == false,
            "authority handoff selects the remaining online visitor");
        Check(frozen.AuthorityEpoch == 10 &&
            frozen.SimulatorPlayerId == null && frozen.Mode == MapMode.Frozen,
            "map without a successor becomes frozen");
        Check(unaffected.AuthorityEpoch == 9 &&
            unaffected.SimulatorPlayerId == MultiplayerSession.Other,
            "unrelated map simulator epoch is not modified");
        Check(room.CacheCalls == 1 && room.ChatCalls == 1 &&
            room.BattleCalls == 1 && room.HandoffCalls == 2 &&
            room.PolicyCalls == 1 && room.StatusCalls == 1,
            "normal cleanup retains once-only callback ordering");
        room.Remove();
        Check(room.CacheCalls == 1 && room.HandoffCalls == 2 &&
            room.TestBridge.OfflineCalls == 1,
            "replayed Disconnected event cannot double-freeze or double-handoff");
        room.RemoveUnknown();
        Check(room.PolicyCalls == 1, "unknown peer cannot broadcast a fictitious map handoff");
    }

    private static void FailedAdvisorySteps()
    {
        foreach (string stage in new[] { "cache", "communication", "both", "status" })
        {
            var room = new MultiplayerSession();
            room.AddMap(10, true, true);
            room.AddMap(11, true, false);
            room.FailCache = stage == "cache" || stage == "both";
            room.FailCommunication = stage == "communication" || stage == "both";
            room.FailStatus = stage == "status";
            room.Remove();
            VerifyCommonCleanup(room, "advisory " + stage);
            Check(room.TestBridge.OfflineCalls == 1 &&
                !room.TestBridge.Online.Contains(MultiplayerSession.Leaver),
                "advisory " + stage + ": native offline still executes");
            Check(room.HandoffCalls == 2 && room.PolicyCalls == 1,
                "advisory " + stage + ": every map and the policy is still processed");
            Check(!room.IsPaused && room.NativeFailure == null,
                "advisory " + stage + ": safe room authority does not unnecessarily lock");
            if (stage != "status")
                Check(room.ErrorMessages.Count != 0, "advisory " + stage + ": failure is reported");
        }
    }

    private static void CriticalFailures()
    {
        foreach (string stage in new[] { "battle", "native", "policy", "battle-and-native" })
        {
            var room = new MultiplayerSession();
            room.AddMap(10, true, true);
            room.AddMap(11, true, false);
            room.FailBattle = stage == "battle" || stage == "battle-and-native";
            room.TestBridge.FailOffline = stage == "native" || stage == "battle-and-native";
            room.FailPolicy = stage == "policy";
            room.Remove();
            VerifyCommonCleanup(room, "critical " + stage);
            Check(room.IsPaused && room.NativeFailure != null,
                "critical " + stage + ": failed authority/native step locks the host");
            Check(room.HandoffCalls == 2 &&
                room.TestWorld.Maps[10].SimulatorPlayerId == MultiplayerSession.Other,
                "critical " + stage + ": native/battle/policy failure cannot skip map handoff");
            Check(room.PendingCommands == 1,
                "critical " + stage + ": deferred commands are not left for offline simulator");
        }
    }

    private static void FailedMapHandoff()
    {
        foreach (bool after in new[] { false, true })
        {
            var room = new MultiplayerSession();
            MapRecord bad = room.AddMap(10, true, true);
            MapRecord later = room.AddMap(11, true, true);
            room.AddMap(12, false, true);
            room.FailHandoffMap = 10;
            room.FailHandoffAfterChangingMap = after;
            room.Remove();
            VerifyCommonCleanup(room, "handoff after=" + after);
            Check(bad.SimulatorPlayerId == null &&
                bad.Mode == MapMode.Frozen &&
                !bad.AwaitingAuthorityReady &&
                bad.AuthorityEpoch == (after ? 11 : 10),
                "failed handoff revokes its old or half-created map lease and increments epoch");
            Check(room.TestBridge.NativeFrozenMaps.Contains(10),
                "failed handoff also asks native game to freeze corresponding map");
            Check(later.SimulatorPlayerId == MultiplayerSession.Other &&
                later.AuthorityEpoch == 10 && room.HandoffCalls == 2,
                "one map handoff failure cannot skip later map handoffs");
            Check(room.IsPaused && room.NativeFailure != null,
                "room is locked when a verified map checkpoint could not be handed off");
        }

        var unavailableNative = new MultiplayerSession();
        MapRecord missing = unavailableNative.AddMap(42, true, true);
        unavailableNative.FailHandoffMap = 42;
        unavailableNative.TestBridge.BadFreezes.Add(42);
        unavailableNative.Remove();
        VerifyCommonCleanup(unavailableNative, "native map freeze fault");
        Check(missing.SimulatorPlayerId == null && missing.Mode == MapMode.Frozen &&
            missing.AuthorityEpoch == 10 && unavailableNative.IsPaused,
            "failed native freeze still revokes canonical authority and locks world");

        var missingVisitors = new MultiplayerSession();
        MapRecord corrupted = missingVisitors.AddMap(60, true, false);
        corrupted.Visitors = null;
        missingVisitors.Remove();
        Check(corrupted.SimulatorPlayerId == null && corrupted.Mode == MapMode.Frozen &&
            missingVisitors.IsPaused,
            "malformed visitor registry cannot leave stale simulator identity");
    }

    private static void SecondaryFaultsAndIsolation()
    {
        var lockFailure = new MultiplayerSession();
        lockFailure.AddMap(10, true, true);
        lockFailure.FailBattle = true;
        lockFailure.FailLock = true;
        lockFailure.Remove();
        VerifyCommonCleanup(lockFailure, "lock notification callback failure");
        Check(lockFailure.IsPaused && lockFailure.NativeFailure != null,
            "room remains fail-closed if its own LockNativeFault notice throws");

        var displayFailure = new MultiplayerSession();
        displayFailure.AddMap(10, true, true);
        displayFailure.FailCache = true;
        displayFailure.FailError = true;
        displayFailure.Remove();
        VerifyCommonCleanup(displayFailure, "logging fault");
        Check(!displayFailure.IsPaused && displayFailure.NativeFailure == null,
            "logging failure never prevents authoritative eviction");

        var staleReverse = new MultiplayerSession();
        staleReverse.AddForeignRouting();
        staleReverse.AddMap(10, true, true);
        staleReverse.Remove();
        Check(!staleReverse.HasLeaverPeer &&
            staleReverse.TestWorld.Maps[10].SimulatorPlayerId != MultiplayerSession.Leaver,
            "only the exact old peer binding can be unlinked on peer reuse");

        var absentWorldRecord = new MultiplayerSession();
        absentWorldRecord.TestWorld.Players.Remove(MultiplayerSession.Leaver);
        absentWorldRecord.AddMap(10, true, true);
        absentWorldRecord.Remove();
        VerifyCommonCleanup(absentWorldRecord, "missing world player record");
        Check(absentWorldRecord.IsPaused,
            "missing authoritative player record fails closed instead of faking normal exit");

        Check(Throws<ArgumentNullException>(() =>
            PeerDisconnectCleanup.Attempt(null, null)),
            "null cleanup callback does not silently succeed");
        var error = new InvalidOperationException("first fault");
        Check(ReferenceEquals(PeerDisconnectCleanup.Attempt(
            () => throw new InvalidOperationException("second fault"), error), error),
            "first fatal reason remains available when subsequent cleanup also fails");
    }

    private static void RandomDisconnectInterleavings()
    {
        var random = new Random(20261018);
        for (int round = 0; round < 15000; round++)
        {
            var room = new MultiplayerSession();
            int maps = random.Next(1, 6);
            int failAt = random.Next(1, maps + 1);
            int failedMap = -1;
            for (int i = 1; i <= maps; i++)
            {
                bool remote = random.Next(4) != 0;
                room.AddMap(i, remote, random.Next(2) == 0);
                if (remote && failAt == i) failedMap = i;
            }
            int scenario = random.Next(0, 8);
            room.FailCache = scenario == 0;
            room.FailCommunication = scenario == 1;
            room.FailBattle = scenario == 2;
            room.TestBridge.FailOffline = scenario == 3;
            room.FailPolicy = scenario == 4;
            if (scenario >= 5 && failedMap > 0)
            {
                room.FailHandoffMap = failedMap;
                room.FailHandoffAfterChangingMap = scenario == 6;
                if (scenario == 7) room.TestBridge.BadFreezes.Add(failedMap);
            }
            room.Remove();
            VerifyCommonCleanup(room, "random " + round);
            Check(room.HandoffCalls <= maps,
                "random disconnect cannot reissue duplicate map simulator handoffs");
            if (scenario == 2 || scenario == 3 || scenario == 4 ||
                scenario >= 5 && failedMap > 0)
                Check(room.IsPaused && room.NativeFailure != null,
                    "random critical failure always locks a partially closed room " + round);
            else Check(!room.IsPaused, "random advisory cleanup keeps room operational " + round);
            room.Remove();
            Check(room.PendingCommands == 1, "random repeated close does not delete other player's work");
        }
    }

    private static int Main()
    {
        try
        {
            HealthyDisconnect();
            FailedAdvisorySteps();
            CriticalFailures();
            FailedMapHandoff();
            SecondaryFaultsAndIsolation();
            RandomDisconnectInterleavings();
            Console.WriteLine("PASS: " + checks +
                " production-linked disconnect authority cleanup checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
