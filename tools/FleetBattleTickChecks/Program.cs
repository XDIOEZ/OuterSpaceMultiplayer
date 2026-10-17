using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private static int checks;
    private static void Check(bool passed, string reason)
    {
        if (!passed) throw new InvalidOperationException("FAILED: " + reason);
        checks++;
    }

    private static long Allocations(Action action, int samples)
    {
        for (int i = 0; i < 100; i++) action();
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < samples; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }

    private static Dictionary<string, PlayerFleetBattleRecord> Battles(int count, PlayerFleetBattleStage stage)
    {
        var result = new Dictionary<string, PlayerFleetBattleRecord>(count);
        for (int i = 0; i < count; i++)
        {
            string id = "battle-" + i;
            result.Add(id, new PlayerFleetBattleRecord { BattleId = id, Stage = stage });
        }
        return result;
    }

    private static int Main()
    {
        try
        {
            Check(!FleetBattleTickGate.HasWork(null, true, 0), "null battle collection has no work");
            var empty = Battles(0, PlayerFleetBattleStage.Finished);
            Check(!FleetBattleTickGate.HasWork(empty, true, 0), "empty host room has no battle work");
            Check(!FleetBattleTickGate.HasWork(empty, true, 1),
                "orphaned deadline with no corresponding battle cannot advance");
            Check(!FleetBattleTickGate.HasWork(empty, false, 0), "empty guest room has no battle work");

            const int maxHistory = 64;
            var history = Battles(maxHistory, PlayerFleetBattleStage.Finished);
            Check(!FleetBattleTickGate.HasWork(history, true, 0), "finished host battle history is idle");
            Check(!FleetBattleTickGate.HasWork(history, false, 0), "finished guest battle history is idle");
            Check(FleetBattleTickGate.HasWork(history, true, 1),
                "host with retained deadlines still executes the original per-record deadline path");
            Check(!FleetBattleTickGate.HasWork(history, false, 1),
                "guest does not own or process host battle deadlines");
            history["battle-5"].Stage = PlayerFleetBattleStage.Blocked;
            Check(!FleetBattleTickGate.HasWork(history, true, 0),
                "blocked records remain visible without repeated native battle tick work");
            Check(FleetBattleTickGate.HasWork(history, true, 1),
                "blocked records with host deadlines continue through original timeout logic");

            foreach (var state in new[] { PlayerFleetBattleStage.Preparing, PlayerFleetBattleStage.Active,
                PlayerFleetBattleStage.Committing })
            {
                history["battle-5"].Stage = state;
                Check(FleetBattleTickGate.HasWork(history, true, 0),
                    "host still processes " + state + " even among finished history");
                Check(FleetBattleTickGate.HasWork(history, false, 0),
                    "guest still processes " + state + " even among finished history");
            }
            history["battle-5"].Stage = PlayerFleetBattleStage.Finished;
            Check(!FleetBattleTickGate.HasWork(history, true, 0),
                "transition to finished stops unnecessary history ticks immediately");
            history.Add("new-battle", new PlayerFleetBattleRecord
            {
                BattleId = "new-battle", Stage = PlayerFleetBattleStage.Preparing
            });
            Check(FleetBattleTickGate.HasWork(history, true, 0),
                "new battle resumes the regular processing without resetting a cached flag");
            history.Remove("new-battle");
            Check(!FleetBattleTickGate.HasWork(history, true, 0),
                "removing the last active battle restores the idle path");
            history["battle-0"].Stage = (PlayerFleetBattleStage)999;
            Check(FleetBattleTickGate.HasWork(history, true, 0),
                "unknown stages are not silently skipped by the idle optimization");
            history["battle-0"].Stage = PlayerFleetBattleStage.Finished;

            // Previous host Tick created one filtered snapshot in
            // StopNonHostileBattles and another full snapshot for its main loop.
            // These are only the measured snapshot allocations, NOT game FPS.
            const int ticks = 5000;
            long legacy = Allocations(() =>
            {
                GC.KeepAlive(history.Values.Where(b =>
                    b.Stage != PlayerFleetBattleStage.Finished &&
                    b.Stage != PlayerFleetBattleStage.Blocked).ToList());
                GC.KeepAlive(history.Values.ToList());
            }, ticks);
            long optimized = Allocations(() =>
            {
                if (FleetBattleTickGate.HasWork(history, true, 0))
                    throw new InvalidOperationException("Finished history unexpectedly needs work");
            }, ticks);
            Check(optimized == 0 && legacy > optimized,
                "production gate eliminates historical battle-list allocations in idle host ticks");
            long emptyAllocated = Allocations(() =>
            {
                if (FleetBattleTickGate.HasWork(empty, true, 0))
                    throw new InvalidOperationException("An empty battle list unexpectedly needs work");
            }, ticks);
            Check(emptyAllocated == 0, "empty battle room gate is allocation-free");
            Console.WriteLine("INFO: " + ticks + " idle host checks with " + maxHistory +
                " finished battles: previous two snapshots " + legacy +
                " bytes, production gate " + optimized + " bytes (managed allocations)");
            Console.WriteLine("PASS: " + checks + " linked fleet battle tick gate checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
