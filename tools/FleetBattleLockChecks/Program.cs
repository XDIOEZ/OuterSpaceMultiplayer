using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private static int checks;
    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException("FAILED: " + reason);
        checks++;
    }

    // Exact pre-optimization expressions from MultiplayerSession:
    // the outer null guard models World == null and FleetBattles == null.
    private static PlayerFleetBattleRecord OldFind(
        Dictionary<string, PlayerFleetBattleRecord> battles, long fleetUid)
    {
        return battles == null ? null : battles.Values.FirstOrDefault(
            x => x.Stage != PlayerFleetBattleStage.Finished &&
                 (x.AttackerFleetId == fleetUid || x.DefenderFleetId == fleetUid));
    }
    private static bool OldHas(
        Dictionary<string, PlayerFleetBattleRecord> battles, string playerId)
    {
        return battles != null && battles.Values.Any(
            x => x.Stage != PlayerFleetBattleStage.Finished &&
                 (x.AttackerPlayerId == playerId || x.DefenderPlayerId == playerId));
    }
    private static string Result(Func<PlayerFleetBattleRecord> query,
        out PlayerFleetBattleRecord record)
    {
        record = null;
        try { record = query(); return "accepted"; }
        catch (Exception e) { return e.GetType().Name + ": " + e.Message; }
    }
    private static string Result(Func<bool> query, out bool value)
    {
        value = false;
        try { value = query(); return "accepted"; }
        catch (Exception e) { return e.GetType().Name + ": " + e.Message; }
    }
    private static void SameFleet(Dictionary<string, PlayerFleetBattleRecord> battles,
        long fleetUid, string scenario)
    {
        PlayerFleetBattleRecord oldRecord, newRecord;
        string original = Result(() => OldFind(battles, fleetUid), out oldRecord);
        string updated = Result(() => FleetBattleLockProbe.FindFleet(battles, fleetUid), out newRecord);
        Check(original == updated && ReferenceEquals(oldRecord, newRecord),
            scenario + " UID=" + fleetUid + " old=" + original + " new=" + updated);
        if (original == "accepted")
            Check((oldRecord == null) == (newRecord == null),
                scenario + ": exactly same locked/unlocked fleet result");
    }
    private static void SamePlayer(Dictionary<string, PlayerFleetBattleRecord> battles,
        string playerId, string scenario)
    {
        bool oldFound, newFound;
        string original = Result(() => OldHas(battles, playerId), out oldFound);
        string updated = Result(() => FleetBattleLockProbe.HasParticipant(battles, playerId), out newFound);
        Check(original == updated && oldFound == newFound,
            scenario + " player=" + playerId + " old=" + original + " new=" + updated);
    }
    private static Dictionary<string, PlayerFleetBattleRecord> History(int count)
    {
        var history = new Dictionary<string, PlayerFleetBattleRecord>(count);
        for (int i = 0; i < count; i++)
            history.Add("battle-" + i, new PlayerFleetBattleRecord {
                BattleId = "battle-" + i,
                Stage = PlayerFleetBattleStage.Finished,
                AttackerFleetId = i * 3L + 100,
                DefenderFleetId = i * 3L + 101,
                AttackerPlayerId = "attacker-" + i,
                DefenderPlayerId = "defender-" + i
            });
        return history;
    }
    private static void CompareQueries(Dictionary<string, PlayerFleetBattleRecord> battles,
        string description)
    {
        long[] ids = {
            long.MinValue, -1, 0, 1, 100, 101, 110, 191, long.MaxValue,
            (1L << 32), (1L << 32) | 1
        };
        foreach (long id in ids) SameFleet(battles, id, description);
        string[] players = { null, "", "Alice", "alice", "attacker-0",
            "defender-0", "none", "guest", "玩家", "e\u0301", "\u00e9" };
        foreach (string name in players) SamePlayer(battles, name, description);
    }
    private static long Allocated(Action action, int iterations)
    {
        for (int i = 0; i < 90; i++) action();
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }

    private static int Main()
    {
        try
        {
            CompareQueries(null, "room absent or FleetBattles null");
            CompareQueries(new Dictionary<string, PlayerFleetBattleRecord>(), "room has no battles");

            var history = History(64);
            CompareQueries(history, "finished room battle history");
            Check(FleetBattleLockProbe.FindFleet(history, 100) == null &&
                !FleetBattleLockProbe.HasParticipant(history, "attacker-0"),
                "completed battles are never treated as active locks");

            var active = history["battle-17"];
            active.Stage = PlayerFleetBattleStage.Active;
            SameFleet(history, active.AttackerFleetId, "attacker fleet is locked");
            SameFleet(history, active.DefenderFleetId, "defender fleet is locked");
            SamePlayer(history, active.AttackerPlayerId, "attacker player blocked from visits");
            SamePlayer(history, active.DefenderPlayerId, "defender player blocked from travel");
            Check(ReferenceEquals(FleetBattleLockProbe.FindFleet(history, active.AttackerFleetId),
                active), "active battle returns exact existing record, not a copy");

            foreach (PlayerFleetBattleStage stage in new[] {
                PlayerFleetBattleStage.Preparing, PlayerFleetBattleStage.Active,
                PlayerFleetBattleStage.Committing, PlayerFleetBattleStage.Blocked,
                (PlayerFleetBattleStage)999, PlayerFleetBattleStage.Finished
            })
            {
                active.Stage = stage;
                CompareQueries(history, "transition through battle stage " + stage);
                SameFleet(history, active.AttackerFleetId,
                    "stage-specific lock for real active battle fleet");
                SamePlayer(history, active.AttackerPlayerId,
                    "stage-specific participant guard");
                Check((FleetBattleLockProbe.FindFleet(history, active.AttackerFleetId) != null)
                    == (stage != PlayerFleetBattleStage.Finished),
                    "only Finished is excluded from the existing battle lock contract");
            }

            // The original FirstOrDefault chooses the first match, even if
            // multiple records happen to contain the same historical UID.
            var duplicateIds = new Dictionary<string, PlayerFleetBattleRecord>();
            var first = new PlayerFleetBattleRecord {
                Stage = PlayerFleetBattleStage.Blocked, AttackerFleetId = long.MaxValue,
                AttackerPlayerId = "Alice" };
            var second = new PlayerFleetBattleRecord {
                Stage = PlayerFleetBattleStage.Active, DefenderFleetId = long.MaxValue,
                DefenderPlayerId = "Alice" };
            duplicateIds.Add("first", first);
            duplicateIds.Add("second", second);
            SameFleet(duplicateIds, long.MaxValue, "duplicate fleet UIDs preserve first matching record");
            Check(ReferenceEquals(FleetBattleLockProbe.FindFleet(duplicateIds, long.MaxValue),
                first), "first matching blocked record has original dictionary insertion priority");
            duplicateIds.Remove("first");
            SameFleet(duplicateIds, long.MaxValue, "removing first lock reveals second");
            Check(ReferenceEquals(FleetBattleLockProbe.FindFleet(duplicateIds, long.MaxValue),
                second), "dictionary edits are read live rather than from cached stale indexes");
            duplicateIds.Add("after", first);
            SameFleet(duplicateIds, long.MaxValue, "re-adding a historical record updates priority");

            // Preserve the old predicate's exception short-circuit: an
            // ill-formed null record is only touched if no preceding match.
            var malformed = new Dictionary<string, PlayerFleetBattleRecord> {
                { "first", first }, { "corrupt", null }, { "last", second }
            };
            SameFleet(malformed, long.MaxValue, "first match precedes malformed record");
            SameFleet(malformed, long.MinValue, "miss encounters malformed record");
            SamePlayer(malformed, "Alice", "first player precedes malformed record");
            SamePlayer(malformed, "intruder", "unknown player reaches malformed record");
            malformed.Remove("corrupt");
            CompareQueries(malformed, "after corrupt record is removed");

            // String equality is case-sensitive and ordinal. A null
            // PlayerId can also match a missing PlayerId in malformed data,
            // exactly as it did before the optimization.
            var strings = new Dictionary<string, PlayerFleetBattleRecord> {
                { "p1", new PlayerFleetBattleRecord {
                    Stage = PlayerFleetBattleStage.Preparing,
                    AttackerPlayerId = "alice", DefenderPlayerId = "e\u0301",
                    AttackerFleetId = long.MinValue, DefenderFleetId = 1L << 32
                }},
                { "p2", new PlayerFleetBattleRecord {
                    Stage = PlayerFleetBattleStage.Blocked,
                    AttackerPlayerId = "Alice", DefenderPlayerId = "\u00e9",
                    AttackerFleetId = long.MaxValue, DefenderFleetId = -1
                }}
            };
            CompareQueries(strings, "ordinal participant strings and full signed fleet UIDs");
            Check(FleetBattleLockProbe.HasParticipant(strings, "alice") &&
                FleetBattleLockProbe.HasParticipant(strings, "Alice") &&
                FleetBattleLockProbe.HasParticipant(strings, "e\u0301") &&
                FleetBattleLockProbe.HasParticipant(strings, "\u00e9") &&
                !FleetBattleLockProbe.HasParticipant(strings, "ALICE"),
                "case and Unicode normalization remain independent identities");
            strings["p1"].AttackerPlayerId = null;
            SamePlayer(strings, null, "null PlayerId still matches a record with a null owner");

            // 5,000 randomized records and independently-random query orders
            // exercise the full old/new predicate equality/exception contract.
            var random = new Random(20261018);
            string[] playerNames = { null, "", "host", "guest", "Alice", "alice",
                "玩家", "e\u0301", "\u00e9", "offline" };
            long[] specialUids = { long.MinValue, long.MaxValue, 0, -1, 1,
                1L << 32, (1L << 32) | 1, -(1L << 32) };
            for (int sample = 0; sample < 5000; sample++)
            {
                var battles = new Dictionary<string, PlayerFleetBattleRecord>();
                int count = random.Next(0, 92);
                for (int i = 0; i < count; i++)
                {
                    if (random.Next(0, 97) == 0)
                    { battles.Add("b" + i, null); continue; }
                    long attack = random.Next(0, 4) == 0
                        ? specialUids[random.Next(specialUids.Length)]
                        : random.Next(-1000, 1000);
                    long defend = random.Next(0, 4) == 0
                        ? specialUids[random.Next(specialUids.Length)]
                        : random.Next(-1000, 1000);
                    battles.Add("b" + i, new PlayerFleetBattleRecord {
                        BattleId = "b" + i,
                        Stage = (PlayerFleetBattleStage)random.Next(0, 7),
                        AttackerFleetId = attack,
                        DefenderFleetId = defend,
                        AttackerPlayerId = playerNames[random.Next(playerNames.Length)],
                        DefenderPlayerId = playerNames[random.Next(playerNames.Length)]
                    });
                }
                for (int q = 0; q < 9; q++)
                {
                    long id = random.Next(0, 3) == 0
                        ? specialUids[random.Next(specialUids.Length)]
                        : random.Next(-1000, 1000);
                    SameFleet(battles, id, "random historical/native battle " + sample);
                    SamePlayer(battles, playerNames[random.Next(playerNames.Length)],
                        "random participant guard " + sample);
                }
            }

            // Repeated callback path from PresentRemoteFleetProxy:
            // every visible remote fleet probes the host's battle history
            // each frame even when all 64 retained battles have Finished.
            const int visibleFleets = 96, frames = 1500;
            var onlyHistory = History(64);
            var visibleUids = new long[visibleFleets];
            for (int i = 0; i < visibleFleets; i++)
                visibleUids[i] = i * 10000L + 7;
            Action legacyPresentation = () =>
            {
                foreach (long uid in visibleUids)
                    if (OldFind(onlyHistory, uid) != null)
                        throw new InvalidOperationException("Finished history locked native fleet");
            };
            Action indexedPresentation = () =>
            {
                foreach (long uid in visibleUids)
                    if (FleetBattleLockProbe.FindFleet(onlyHistory, uid) != null)
                        throw new InvalidOperationException("Finished history locked native fleet");
            };
            long oldBytes = Allocated(legacyPresentation, frames);
            long newBytes = Allocated(indexedPresentation, frames);
            Check(oldBytes > 0 && newBytes == 0,
                "production fleet lock scanning allocates no per-proxy closures or iterators");

            const int participantProbes = 7000;
            long oldParticipant = Allocated(() =>
            {
                if (OldHas(onlyHistory, "guest"))
                    throw new InvalidOperationException("Finished battle blocked guest");
            }, participantProbes);
            long newParticipant = Allocated(() =>
            {
                if (FleetBattleLockProbe.HasParticipant(onlyHistory, "guest"))
                    throw new InvalidOperationException("Finished battle blocked guest");
            }, participantProbes);
            Check(oldParticipant > 0 && newParticipant == 0,
                "production participant guard also avoids per-request LINQ allocations");
            Console.WriteLine("INFO: " + frames + " synthetic remote render frames × " +
                visibleFleets + " fleet lock probes across 64 finished battles: old LINQ " +
                oldBytes + " B, linked production probe " + newBytes +
                " B; " + participantProbes + " participant checks old " +
                oldParticipant + " B, new " + newParticipant +
                " B (managed allocations after warmup)");
            Console.WriteLine("PASS: " + checks +
                " production-linked battle lock and participant checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
