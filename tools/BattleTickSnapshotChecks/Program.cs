using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private static int checks;
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new InvalidOperationException("FAILED: " + label);
        checks++;
    }
    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
    private static Dictionary<string, PlayerFleetBattleRecord> Records(int count)
    {
        var records = new Dictionary<string, PlayerFleetBattleRecord>();
        for (int i = 0; i < count; i++)
        {
            string id = "battle-" + i;
            var stage = (PlayerFleetBattleStage)(i % 5);
            records.Add(id, new PlayerFleetBattleRecord
            {
                BattleId = id,
                NativeBattleUid = i + 1000,
                Stage = stage,
                AttackerPlayerId = i % 2 == 0 ? "host" : "guest",
                DefenderPlayerId = "opponent"
            });
        }
        return records;
    }
    private static bool Active(PlayerFleetBattleRecord record)
    {
        return record.Stage != PlayerFleetBattleStage.Finished
            && record.Stage != PlayerFleetBattleStage.Blocked;
    }
    private static List<PlayerFleetBattleRecord> OldSnapshot(
        Dictionary<string, PlayerFleetBattleRecord> battles, bool unfinished)
    {
        // The original two production expressions in the same ordering:
        // Values.Where(...).ToList for the host's relationship pass, and
        // Values.ToList for the main authoritative battle Tick.
        return unfinished ? battles.Values.Where(record =>
            record.Stage != PlayerFleetBattleStage.Finished &&
            record.Stage != PlayerFleetBattleStage.Blocked).ToList()
            : battles.Values.ToList();
    }
    private static void Same(IList<PlayerFleetBattleRecord> old,
        IList<PlayerFleetBattleRecord> current, string label)
    {
        Check(old.Count == current.Count, label + ": snapshot count");
        for (int i = 0; i < old.Count; i++)
            Check(ReferenceEquals(old[i], current[i]),
                label + ": native record reference and order " + i);
    }
    private static void Differential(BattleTickRecordSnapshot buffer,
        Dictionary<string, PlayerFleetBattleRecord> battles, string label)
    {
        foreach (bool activeOnly in new[] { true, false })
        {
            List<PlayerFleetBattleRecord> old = OldSnapshot(battles, activeOnly);
            List<PlayerFleetBattleRecord> current = buffer.Capture(battles, activeOnly);
            try { Same(old, current, label + (activeOnly ? ": relationship pass" : ": main Tick")); }
            finally { buffer.Release(current); }
        }
    }
    private static long Allocations(Action action, int times)
    {
        for (int i = 0; i < 100; i++) action();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < times; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
    private static string OldTick(Dictionary<string, PlayerFleetBattleRecord> battles,
        Action<PlayerFleetBattleRecord> onUnfinished, Action<PlayerFleetBattleRecord> onAll)
    {
        try
        {
            foreach (var record in OldSnapshot(battles, true)) onUnfinished(record);
            foreach (var record in OldSnapshot(battles, false)) onAll(record);
            return "accepted";
        }
        catch (Exception error) { return error.GetType().Name + ": " + error.Message; }
    }
    private static string NewTick(Dictionary<string, PlayerFleetBattleRecord> battles,
        BattleTickRecordSnapshot buffer,
        Action<PlayerFleetBattleRecord> onUnfinished, Action<PlayerFleetBattleRecord> onAll)
    {
        try
        {
            var filtered = buffer.Capture(battles, true);
            try { foreach (var record in filtered) onUnfinished(record); }
            finally { buffer.Release(filtered); }
            // The main pass MUST be captured after relationship callbacks:
            // StopNonHostileBattles is allowed to finish/remove battles.
            var all = buffer.Capture(battles, false);
            try { foreach (var record in all) onAll(record); }
            finally { buffer.Release(all); }
            return "accepted";
        }
        catch (Exception error) { return error.GetType().Name + ": " + error.Message; }
    }
    private static Dictionary<string, PlayerFleetBattleRecord> Clone(
        Dictionary<string, PlayerFleetBattleRecord> source)
    {
        var clone = new Dictionary<string, PlayerFleetBattleRecord>();
        foreach (var pair in source)
        {
            PlayerFleetBattleRecord value = pair.Value;
            clone.Add(pair.Key, new PlayerFleetBattleRecord
            {
                BattleId = value.BattleId,
                NativeBattleUid = value.NativeBattleUid,
                Stage = value.Stage,
                AttackerPlayerId = value.AttackerPlayerId,
                DefenderPlayerId = value.DefenderPlayerId
            });
        }
        return clone;
    }
    private static void CompareTicks(Dictionary<string, PlayerFleetBattleRecord> initial,
        int mutationSeed, string label)
    {
        var old = Clone(initial);
        var current = Clone(initial);
        var oldOrder = new List<string>();
        var newOrder = new List<string>();
        var randomOld = new Random(mutationSeed);
        var randomNew = new Random(mutationSeed);
        // Use independent mutation counters so both implementations insert
        // the same deterministic new record IDs.
        int oldAdded = 0, newAdded = 0;
        Action<PlayerFleetBattleRecord> oldRelationship = record =>
        {
            oldOrder.Add("r:" + record.BattleId);
            int decision = randomOld.Next(0, 15);
            if (decision == 0) old.Remove(record.BattleId);
            if (decision == 1) record.Stage = PlayerFleetBattleStage.Finished;
            if (decision == 2)
            {
                string next = "injected-" + oldAdded++;
                old.Add(next, new PlayerFleetBattleRecord { BattleId = next, Stage = PlayerFleetBattleStage.Preparing });
            }
        };
        Action<PlayerFleetBattleRecord> newRelationship = record =>
        {
            newOrder.Add("r:" + record.BattleId);
            int decision = randomNew.Next(0, 15);
            if (decision == 0) current.Remove(record.BattleId);
            if (decision == 1) record.Stage = PlayerFleetBattleStage.Finished;
            if (decision == 2)
            {
                string next = "injected-" + newAdded++;
                current.Add(next, new PlayerFleetBattleRecord { BattleId = next, Stage = PlayerFleetBattleStage.Preparing });
            }
        };
        Action<PlayerFleetBattleRecord> oldMain = record =>
        {
            oldOrder.Add("t:" + record.BattleId + ":" + (int)record.Stage);
            if (record.NativeBattleUid % 3 == 0) old.Remove(record.BattleId);
        };
        Action<PlayerFleetBattleRecord> newMain = record =>
        {
            newOrder.Add("t:" + record.BattleId + ":" + (int)record.Stage);
            if (record.NativeBattleUid % 3 == 0) current.Remove(record.BattleId);
        };
        string oldResult = OldTick(old, oldRelationship, oldMain);
        string newResult = NewTick(current, new BattleTickRecordSnapshot(),
            newRelationship, newMain);
        Check(oldResult == newResult, label + ": callback failure/acceptance matches");
        Check(oldOrder.SequenceEqual(newOrder), label + ": callback record order and mutable stages");
        Check(old.Keys.SequenceEqual(current.Keys), label + ": post-callback dictionary membership/order");
        foreach (var pair in old)
            Check(current[pair.Key].Stage == pair.Value.Stage,
                label + ": post-callback record stage " + pair.Key);
    }

    private static void Benchmark()
    {
        var battles = Records(128);
        var buffer = new BattleTickRecordSnapshot();
        const int samples = 6000;
        Action oldHost = () =>
        {
            List<PlayerFleetBattleRecord> active = OldSnapshot(battles, true);
            List<PlayerFleetBattleRecord> all = OldSnapshot(battles, false);
            if (active.Count != 78 || all.Count != 128)
                throw new InvalidOperationException("legacy battle count changed");
        };
        Action newHost = () =>
        {
            var active = buffer.Capture(battles, true);
            try { if (active.Count != 78) throw new InvalidOperationException("active count changed"); }
            finally { buffer.Release(active); }
            var all = buffer.Capture(battles, false);
            try { if (all.Count != 128) throw new InvalidOperationException("record count changed"); }
            finally { buffer.Release(all); }
        };
        long oldBytes = Allocations(oldHost, samples);
        long newBytes = Allocations(newHost, samples);
        Check(oldBytes > newBytes && newBytes == 0,
            "host battle Tick snapshot helper removes per-frame LINQ arrays");
        Console.WriteLine("INFO: " + samples +
            " synthetic host Tick frames × 128 retained battles (78 nonterminal): old LINQ snapshots " +
            oldBytes + " B, production reused snapshots " + newBytes +
            " B (managed allocation after warmup)");
        Action oldGuest = () => { if (OldSnapshot(battles, false).Count != 128) throw new Exception(); };
        Action newGuest = () =>
        {
            var records = buffer.Capture(battles, false);
            try { if (records.Count != 128) throw new Exception(); }
            finally { buffer.Release(records); }
        };
        long oldGuestBytes = Allocations(oldGuest, samples);
        long newGuestBytes = Allocations(newGuest, samples);
        Check(oldGuestBytes > newGuestBytes && newGuestBytes == 0,
            "guest Tick also avoids the full dictionary snapshot allocation");
        Console.WriteLine("INFO: " + samples +
            " synthetic guest Tick frames: old full snapshots " + oldGuestBytes +
            " B, production reused snapshots " + newGuestBytes + " B");
    }

    public static int Main()
    {
        try
        {
            var buffer = new BattleTickRecordSnapshot();
            Check(Throws<ArgumentNullException>(() => buffer.Capture(null, false)),
                "cannot snapshot a null battle registry");
            Check(Throws<ArgumentNullException>(() => buffer.Release(null)),
                "cannot release a null snapshot");
            foreach (int size in new[] { 0, 1, 2, 8, 64, 128, 4096 })
                Differential(buffer, Records(size), "battle registry size " + size);

            var active = Records(20);
            var outer = buffer.Capture(active, false);
            var nested = buffer.Capture(active, true);
            Check(!ReferenceEquals(outer, nested) && outer.Count == 20 &&
                nested.Count == OldSnapshot(active, true).Count,
                "nested native/transport callbacks lease an independent filtered snapshot");
            active.Clear();
            Check(outer.Count == 20 && nested.Count > 0,
                "dictionary mutation after snapshot leaves both already-captured lists intact");
            buffer.Release(nested);
            Check(outer.Count == 20, "nested release leaves outer frame untouched");
            buffer.Reset();
            Check(outer.Count == 20, "room close defers reset while Tick holds its snapshot");
            buffer.Release(outer);
            Check(outer.Capacity == 0 && outer.Count == 0,
                "room exit frees oversized snapshot backing arrays and battle references");
            var newRoom = buffer.Capture(Records(3), false);
            Check(ReferenceEquals(newRoom, outer) && newRoom.Count == 3,
                "next room does not reuse any stale battle record");
            buffer.Release(newRoom);
            buffer.Reset();

            var exceptionRecords = Records(12);
            var exceptionSnapshot = buffer.Capture(exceptionRecords, true);
            Check(Throws<InvalidOperationException>(() =>
            {
                try
                {
                    foreach (var record in exceptionSnapshot)
                        if (record.NativeBattleUid == 1002)
                            throw new InvalidOperationException("native fleet stopped");
                }
                finally { buffer.Release(exceptionSnapshot); }
            }), "synchronous battle callback exception propagates unchanged");
            var recovered = buffer.Capture(exceptionRecords, false);
            Check(recovered.Count == 12 && ReferenceEquals(recovered, exceptionSnapshot),
                "failed Tick still returns clean reusable backing list");
            buffer.Release(recovered);

            var altered = Records(30);
            var alreadyCaptured = buffer.Capture(altered, true);
            var previousCount = alreadyCaptured.Count;
            var alteredList = OldSnapshot(altered, true);
            Check(previousCount == alteredList.Count,
                "captured nonterminal records match the original host selection");
            foreach (var pair in altered)
                if (pair.Value.Stage != PlayerFleetBattleStage.Finished)
                    pair.Value.Stage = PlayerFleetBattleStage.Finished;
            Check(alreadyCaptured.Count == previousCount,
                "stage changes during callbacks do not retroactively refilter the snapshot");
            buffer.Release(alreadyCaptured);
            Differential(buffer, altered,
                "next Tick reflects battle stage changes after callbacks");

            var random = new Random(20261018);
            for (int trial = 0; trial < 4200; trial++)
            {
                int n = random.Next(0, 175);
                var registry = Records(n);
                foreach (var pair in registry)
                    pair.Value.Stage = (PlayerFleetBattleStage)random.Next(-2, 9);
                Differential(buffer, registry, "random native battle stages " + trial);
                if (trial % 10 == 0)
                    CompareTicks(registry, trial, "random host Tick with dictionary mutation " + trial);
            }

            var special = Records(160);
            foreach (var pair in special)
                pair.Value.Stage = PlayerFleetBattleStage.Finished;
            // An idle room's FleetBattleTickGate skips these snapshots entirely;
            // a retained deadline makes the host execute the same full path.
            Differential(buffer, special, "retained finished history with orphaned deadline");
            CompareTicks(special, 123, "finished/blocked states retained for host deadline");
            Benchmark();
            Console.WriteLine("PASS: " + checks +
                " linked active fleet battle Tick snapshot checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
