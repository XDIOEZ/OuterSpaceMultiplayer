using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private sealed class Battle
    {
        internal string Attacker, Defender;
        internal Battle(string attacker, string defender)
        { Attacker = attacker; Defender = defender; }
    }

    private static int checks;
    private static long sink;

    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException("FAILED: " + reason);
        checks++;
    }

    // Models the three original new[] call sites with their distinct
    // continue/filter and exception boundaries, without changing the ACK
    // and battle/scene logic around them.
    private static string SendState(Battle battle, bool optimized,
        List<string> outputs, bool mutate, bool failAfterFirst)
    {
        try
        {
            if (optimized)
            {
                foreach (string player in new BattleParticipantPair(battle.Attacker, battle.Defender))
                    Send(player);
            }
            else
            {
                foreach (string player in new[] { battle.Attacker, battle.Defender })
                    Send(player);
            }
            return "accepted";
        }
        catch (Exception e) { return e.GetType().Name + ": " + e.Message; }

        void Send(string player)
        {
            outputs.Add(player);
            if (mutate && outputs.Count == 1)
            {
                // The second participant must have been captured BEFORE
                // the first Send triggered an unrelated synchronous callback.
                battle.Attacker = "NEW-ATTACKER";
                battle.Defender = "NEW-DEFENDER";
            }
            if (failAfterFirst && outputs.Count == 1)
                throw new InvalidOperationException("network send failed");
        }
    }

    private static string EnterScene(Battle battle, string local, bool optimized,
        List<string> outputs, bool mutate, bool failAfterFirst)
    {
        try
        {
            if (optimized)
            {
                foreach (string player in new BattleParticipantPair(battle.Attacker, battle.Defender))
                    Enter(player);
            }
            else
            {
                foreach (string player in new[] { battle.Attacker, battle.Defender })
                    Enter(player);
            }
            return "accepted";
        }
        catch (Exception e) { return e.GetType().Name + ": " + e.Message; }

        void Enter(string player)
        {
            if (player == local) return; // The actual production 'continue'.
            outputs.Add(player);
            if (mutate && outputs.Count == 1)
            {
                battle.Attacker = "changed";
                battle.Defender = "changed";
            }
            if (failAfterFirst && outputs.Count == 1)
                throw new InvalidOperationException("galaxy preflight send failed");
        }
    }

    private static string RelayFrame(Battle battle, string actor, string local,
        bool initial, bool optimized, List<string> outputs,
        bool mutate, bool failAfterFirst)
    {
        try
        {
            if (optimized)
            {
                if (!initial)
                    foreach (string peer in new BattleParticipantPair(battle.Attacker, battle.Defender))
                        Relay(peer);
            }
            else
            {
                if (!initial)
                    foreach (string peer in new[] { battle.Attacker, battle.Defender })
                        Relay(peer);
            }
            return "accepted";
        }
        catch (Exception e) { return e.GetType().Name + ": " + e.Message; }

        void Relay(string peer)
        {
            if (peer == actor || peer == local) return;
            outputs.Add(peer);
            if (mutate && outputs.Count == 1)
            {
                battle.Attacker = "third-party-mutated";
                battle.Defender = "third-party-mutated";
            }
            if (failAfterFirst && outputs.Count == 1)
                throw new InvalidOperationException("battle frame send failed");
        }
    }

    private static void Same(Battle battle, string actor, string local,
        bool initial, bool mutate, bool failed, string context)
    {
        foreach (int mode in new[] { 0, 1, 2 })
        {
            var old = new Battle(battle.Attacker, battle.Defender);
            var next = new Battle(battle.Attacker, battle.Defender);
            var expected = new List<string>();
            var actual = new List<string>();
            string a = mode == 0 ? SendState(old, false, expected, mutate, failed)
                : mode == 1 ? EnterScene(old, local, false, expected, mutate, failed)
                : RelayFrame(old, actor, local, initial, false, expected, mutate, failed);
            string b = mode == 0 ? SendState(next, true, actual, mutate, failed)
                : mode == 1 ? EnterScene(next, local, true, actual, mutate, failed)
                : RelayFrame(next, actor, local, initial, true, actual, mutate, failed);
            Check(a == b, context + " mode=" + mode + " same result/exception");
            Check(expected.SequenceEqual(actual),
                context + " mode=" + mode + " exactly same recipient order, repeats and nulls");
            Check(old.Attacker == next.Attacker && old.Defender == next.Defender,
                context + " mode=" + mode + " synchronous callback changes are identical");
        }
    }

    private static long Allocated(Action operation, int times)
    {
        for (int i = 0; i < 100; i++) operation();
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < times; i++) operation();
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }

    private static void LegacyHotPath(string a, string b)
    {
        var participants = new[] { a, b };
        foreach (string player in participants) sink += player == null ? 0 : player.Length;
        GC.KeepAlive(participants); // Do not let an optimizing JIT elide the actual old array.
    }

    private static void ProductionHotPath(string a, string b)
    {
        foreach (string player in new BattleParticipantPair(a, b))
            sink += player == null ? 0 : player.Length;
    }

    private static int Main()
    {
        try
        {
            var fixedCases = new[] {
                new Battle("host", "guest"),
                new Battle("host", "host"),
                new Battle("HOST", "host"),
                new Battle(null, null),
                new Battle("", ""),
                new Battle(null, "guest"),
                new Battle("guest", null),
                new Battle("a:b", "a"),
                new Battle("e\u0301", "\u00e9"),
                new Battle("主舰", "舰队"),
                new Battle("!", "A"),
            };
            foreach (var battle in fixedCases)
                foreach (bool initial in new[] { true, false })
                    foreach (bool mutate in new[] { true, false })
                        foreach (bool failed in new[] { true, false })
                            Same(battle, "host", "guest", initial, mutate, failed,
                                "pair boundary data");

            var mutable = new Battle("first", "second");
            var firstRun = new List<string>();
            Check(SendState(mutable, true, firstRun, true, false) == "accepted"
                && firstRun.Count == 2 && firstRun[0] == "first"
                && firstRun[1] == "second",
                "second battle participant is snapshotted before first network send");
            var iterator = new BattleParticipantPair("first", "second");
            var left = iterator.GetEnumerator();
            var right = iterator.GetEnumerator();
            Check(left.MoveNext() && left.Current == "first"
                && right.MoveNext() && right.Current == "first"
                && left.MoveNext() && left.Current == "second"
                && right.MoveNext() && right.Current == "second"
                && !left.MoveNext() && !right.MoveNext(),
                "nested participant enumerations have independent indices and exactly two entries");

            var random = new Random(20261018);
            string[] players = { null, "", "HOST", "host", "guest", "guest:two",
                "主控", "a/b", "player-1", "e\u0301", "\u00e9" };
            for (int i = 0; i < 16000; i++)
            {
                var battle = new Battle(players[random.Next(players.Length)],
                    players[random.Next(players.Length)]);
                string actor = players[random.Next(players.Length)];
                string local = players[random.Next(players.Length)];
                Same(battle, actor, local, random.Next(2) == 0,
                    random.Next(2) == 0, random.Next(4) == 0,
                    "random authoritative two-participant wire frame " + i);
            }

            // Measure the precise old two-element array path, excluding
            // network I/O, serialization, scene updates and battle authority.
            const int frames = 300000;
            Action legacy = () => LegacyHotPath("attacker", "defender");
            Action updated = () => ProductionHotPath("attacker", "defender");
            long oldBytes = Allocated(legacy, frames);
            long newBytes = Allocated(updated, frames);
            Check(oldBytes > newBytes && oldBytes - newBytes >= 1000000,
                "struct-based pair iteration reduces real repeated array allocation");
            Console.WriteLine("INFO: " + frames +
                " synthetic two-player battle dispatch loops: old new[] path " +
                oldBytes + " B, linked production enumerator " + newBytes +
                " B (managed allocations after warmup)");
            checks += VisitRecipientChecks.Run();
            Console.WriteLine("PASS: " + checks +
                " production linked battle and visit recipient ordering/allocation checks");
            GC.KeepAlive(sink);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
