using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using FreeWorldMultiplayer;

internal static class Program
{
    private static int checks;
    private static void Check(bool ok, string description)
    {
        if (!ok) throw new InvalidOperationException("FAILED: " + description);
        checks++;
    }
    private static string LegacyKey(string battleId, string playerId, bool execute)
    {
        return execute ? "execute:" + battleId + ":" + playerId
            : battleId + ":" + playerId;
    }
    private static BattleInputSequenceKey NumericKey(string battleId, string playerId, bool execute)
    { return new BattleInputSequenceKey(battleId, playerId, execute); }
    private static bool Throws<T>(Action call) where T : Exception
    {
        try { call(); return false; }
        catch (T) { return true; }
    }

    private sealed class Scenario
    {
        internal readonly Dictionary<string, long> Old =
            new Dictionary<string, long>(StringComparer.Ordinal);
        internal readonly BattleInputSequenceIndex Current =
            new BattleInputSequenceIndex();
        internal readonly HashSet<BattleInputSequenceKey> Seen =
            new HashSet<BattleInputSequenceKey>();
        internal int OldRateCalls, CurrentRateCalls;
        internal int OldExecutes, CurrentExecutes;

        internal void Host(string battleId, string playerId, long sequence,
            bool rateAllowed, string description)
        {
            // Match the source ordering: rejection and replay happen before
            // ConsumeCommunicationRate; the accepted sequence is reserved
            // only after that rate guard permits the action.
            string before = LegacyKey(battleId, playerId, false);
            var after = NumericKey(battleId, playerId, false);
            Seen.Add(after);
            long oldPrior, currentPrior;
            bool oldExists = Old.TryGetValue(before, out oldPrior);
            bool currentExists = Current.TryGetValue(after, out currentPrior);
            Check(oldExists == currentExists && (!oldExists || oldPrior == currentPrior),
                description + " host prior sequence and identity");
            bool oldReplay = sequence <= 0 || oldExists && sequence <= oldPrior;
            bool currentReplay = sequence <= 0 || currentExists && sequence <= currentPrior;
            Check(oldReplay == currentReplay, description + " host replay and negative sequence");
            if (!oldReplay)
            {
                OldRateCalls++;
                CurrentRateCalls++;
                if (rateAllowed)
                {
                    Old[before] = sequence;
                    Current[after] = sequence;
                    OldExecutes++;
                    CurrentExecutes++;
                }
            }
            CompareKey(after, description + " host result");
            Check(OldRateCalls == CurrentRateCalls && OldExecutes == CurrentExecutes,
                description + " rate-limit and authoritative execute side-effect timing");
        }

        internal void Execute(string battleId, string playerId, long sequence,
            string description)
        {
            // The already-authorized simulator did not reject a nonpositive
            // sequence before its dictionary probe: preserve that difference
            // from the host's input path.
            string before = LegacyKey(battleId, playerId, true);
            var after = NumericKey(battleId, playerId, true);
            Seen.Add(after);
            long oldPrior, currentPrior;
            bool oldExists = Old.TryGetValue(before, out oldPrior);
            bool currentExists = Current.TryGetValue(after, out currentPrior);
            Check(oldExists == currentExists && (!oldExists || oldPrior == currentPrior),
                description + " executor prior sequence");
            if (!oldExists || sequence > oldPrior)
            {
                Old[before] = sequence;
                Current[after] = sequence;
            }
            CompareKey(after, description + " simulator independent replay window");
        }

        internal void EndBattle(string battleId, string description)
        {
            int before = Old.Count;
            // Pre-optimization TrimFinishedBattles snapshot, preserving the
            // original prefix filters and removal-after-enumeration order.
            foreach (string key in Old.Keys.Where(x =>
                x.StartsWith(battleId + ":", StringComparison.Ordinal) ||
                x.StartsWith("execute:" + battleId + ":", StringComparison.Ordinal)).ToList())
                Old.Remove(key);
            int expectedRemoved = before - Old.Count;
            int actualRemoved = Current.RemoveBattle(battleId);
            Check(expectedRemoved == actualRemoved,
                description + " same number of completed-battle windows removed");
            CompareAll(description + " remaining active/other-battle windows");
        }

        internal void CompareKey(BattleInputSequenceKey key, string description)
        {
            long oldSeq, newSeq;
            bool oldPresent = Old.TryGetValue(key.ToString(), out oldSeq);
            bool newPresent = Current.TryGetValue(key, out newSeq);
            Check(oldPresent == newPresent && (!oldPresent || oldSeq == newSeq),
                description + " cached sequence remains exact");
        }
        internal void CompareAll(string description)
        {
            Check(Old.Count == Current.Count, description + " total cached count");
            foreach (BattleInputSequenceKey key in Seen) CompareKey(key, description);
        }
    }

    private static long Allocated(Action action, int count)
    {
        for (int i = 0; i < 160; i++) action();
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < count; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }

    private static int Main()
    {
        try
        {
            const string battleA = "00112233445566778899aabbccddeeff";
            const string battleB = "00112233445566778899aabbccddeef0";
            const string battleC = "ffeeddccbbaa99887766554433221100";
            var players = new[] {
                "host", "HOST", "guest", "Guest", "steám",
                "玩家:A", "e\u0301", "\u00e9", "", null
            };
            foreach (string battleId in new[] { battleA, battleB, battleC, "", null })
                foreach (string player in players)
                    foreach (bool execute in new[] { false, true })
                    {
                        var key = NumericKey(battleId, player, execute);
                        Check(key.ToString() == LegacyKey(battleId, player, execute),
                            "exact legacy key text for diagnostics and normalized null");
                        var duplicate = NumericKey(battleId, player, execute);
                        Check(key.Equals(duplicate) &&
                            key.GetHashCode() == duplicate.GetHashCode(),
                            "composite keys preserve ordinal equality and hash contract");
                    }
            Check(!NumericKey(battleA, "host", false).Equals(
                    NumericKey(battleA, "host", true)) &&
                !NumericKey(battleA, "host", false).Equals(
                    NumericKey(battleB, "host", false)) &&
                !NumericKey(battleA, "host", false).Equals(
                    NumericKey(battleA, "HOST", false)),
                "battle ID, player identity and host/execute namespace stay independent");
            Check(NumericKey(battleA, null, false).Equals(
                    NumericKey(battleA, "", false)),
                "null actor is normalized to original string-concat empty identity");
            Check(Throws<KeyNotFoundException>(() =>
            {
                long ignored = new BattleInputSequenceIndex()[
                    NumericKey(battleA, "host", false)];
                GC.KeepAlive(ignored);
            }), "missing battle input sequence keeps dictionary indexer failure contract");

            var scenario = new Scenario();
            scenario.Host(battleA, "host", -1, true, "host rejects negative sequence");
            scenario.Host(battleA, "host", 0, true, "host rejects zero sequence");
            scenario.Host(battleA, "host", 1, false,
                "host rate rejection must not reserve first sequence");
            scenario.Host(battleA, "host", 1, true,
                "first permitted host input is executed exactly once");
            scenario.Host(battleA, "host", 1, true,
                "duplicate host command never consumes rate tokens");
            scenario.Host(battleA, "host", -100, true,
                "negative host command remains ignored after success");
            scenario.Host(battleA, "host", 2, false,
                "rate rejection on next sequence preserves last accepted revision");
            scenario.Host(battleA, "host", 2, true,
                "retry after rate allowance updates host revision");
            scenario.Execute(battleA, "host", -1,
                "simulator accepts an initial negative sequence after authorization");
            scenario.Execute(battleA, "host", 0,
                "simulator accepts later zero sequence");
            scenario.Execute(battleA, "host", 0,
                "simulator rejects duplicate zero sequence");
            scenario.Execute(battleA, "host", 3,
                "simulator moves forward independently from host's revision");
            scenario.Execute(battleA, "host", 2,
                "out-of-order simulator execution is ignored");
            scenario.Host(battleA, "HOST", 1, true,
                "case-sensitive player ID has an independent host window");
            scenario.Execute(battleB, "host", long.MaxValue,
                "highest 64-bit sequence belongs only to another battle");
            scenario.Host(battleB, "host", long.MaxValue, true,
                "host accepts full 64-bit sequence range");
            scenario.Host(battleB, "host", long.MaxValue, true,
                "duplicate 64-bit maximum is ignored without rate consumption");
            scenario.Host(battleA, "玩家:A", 1, true,
                "player identifier containing a colon preserves original key");
            scenario.EndBattle(battleA,
                "finishing one battle removes host and simulator entries of every participant");
            scenario.Host(battleA, "host", 1, true,
                "restarted battle ID begins with a fresh host replay window");
            scenario.Execute(battleA, "host", 1,
                "restarted simulator begins with a fresh execute replay window");
            scenario.EndBattle(battleC,
                "finishing an unrelated battle must not clear other native windows");
            scenario.EndBattle(battleB,
                "finishing the second battle must not clear the restarted first");

            // Randomized old/new behavior under interleaved battles, players,
            // host throttling, simulator duplicates and cleanup callbacks.
            string[] battles = { battleA, battleB, battleC,
                "abcdef0123456789abcdef0123456789", "77777777777777777777777777777777" };
            var rng = new Random(20261018);
            for (int iteration = 0; iteration < 16000; iteration++)
            {
                string battleId = battles[rng.Next(battles.Length)];
                string playerId = players[rng.Next(players.Length)];
                long sequence = rng.Next(0, 11) == 0 ? long.MaxValue - rng.Next(0, 5)
                    : rng.Next(-5, 110);
                switch (rng.Next(0, 13))
                {
                    case 0:
                        scenario.EndBattle(battleId, "random finished-battle cleanup " + iteration);
                        break;
                    case 1:
                    case 2:
                    case 3:
                        scenario.Execute(battleId, playerId, sequence,
                            "random simulator delivery " + iteration);
                        break;
                    default:
                        scenario.Host(battleId, playerId, sequence,
                            rng.Next(0, 5) != 0, "random host rate/replay " + iteration);
                        break;
                }
                if (iteration % 131 == 0)
                    scenario.CompareAll("random periodic full replay-window audit " + iteration);
            }
            scenario.CompareAll("all random battles and actor namespaces");
            foreach (string battle in battles)
                scenario.EndBattle(battle, "room teardown after randomized battle state");
            Check(scenario.Old.Count == 0 && scenario.Current.Count == 0,
                "battle teardown releases every sequence record without leaking a player ID");

            // Measure the EXACT old per-message string key and new production
            // composite struct when both maps already contain the sequence.
            var oldIndex = new Dictionary<string, long>(StringComparer.Ordinal);
            var newIndex = new BattleInputSequenceIndex();
            oldIndex[LegacyKey(battleA, "host", false)] = 900;
            oldIndex[LegacyKey(battleA, "host", true)] = 700;
            newIndex[NumericKey(battleA, "host", false)] = 900;
            newIndex[NumericKey(battleA, "host", true)] = 700;
            Action legacyInput = () =>
            {
                long current;
                string key = battleA + ":" + players[0];
                if (!oldIndex.TryGetValue(key, out current) || current != 900)
                    throw new InvalidOperationException("Legacy host sequence was not found");
            };
            Action modernInput = () =>
            {
                long current;
                var key = new BattleInputSequenceKey(battleA, players[0], false);
                if (!newIndex.TryGetValue(key, out current) || current != 900)
                    throw new InvalidOperationException("Composite host sequence was not found");
            };
            const int incomingCommands = 180000;
            long oldInputBytes = Allocated(legacyInput, incomingCommands);
            long newInputBytes = Allocated(modernInput, incomingCommands);
            Check(oldInputBytes > newInputBytes && newInputBytes == 0,
                "steady battle input ordering lookup removes temporary string-key allocation");
            Console.WriteLine("INFO: " + incomingCommands +
                " repeated battle input sequence probes: legacy concat-key " +
                oldInputBytes + " B, linked production composite key " +
                newInputBytes + " B (managed allocations after warmup)");

            // Repeated TrimFinishedBattles for retained finished records
            // previously still allocated a Where/ToList snapshot each time.
            Action oldNoMatch = () =>
            {
                foreach (string key in oldIndex.Keys.Where(x =>
                    x.StartsWith(battleC + ":", StringComparison.Ordinal) ||
                    x.StartsWith("execute:" + battleC + ":", StringComparison.Ordinal)).ToList())
                    oldIndex.Remove(key);
            };
            Action newNoMatch = () =>
            {
                if (newIndex.RemoveBattle(battleC) != 0)
                    throw new InvalidOperationException("Unrelated battle was pruned");
            };
            const int finishedPasses = 10000;
            long oldPruneBytes = Allocated(oldNoMatch, finishedPasses);
            long newPruneBytes = Allocated(newNoMatch, finishedPasses);
            Check(oldPruneBytes > newPruneBytes && newPruneBytes == 0,
                "retained finished battles no longer allocate empty sequence-prune snapshots");
            Console.WriteLine("INFO: " + finishedPasses +
                " no-match completed-battle prune passes: old LINQ snapshot " +
                oldPruneBytes + " B, linked production prune " + newPruneBytes + " B");
            Check(newIndex.RemoveBattle(battleA) == 2 && newIndex.Count == 0,
                "both host and simulator sequence keys are removed together");
            Console.WriteLine("PASS: " + checks +
                " production-linked battle input replay/cleanup checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
