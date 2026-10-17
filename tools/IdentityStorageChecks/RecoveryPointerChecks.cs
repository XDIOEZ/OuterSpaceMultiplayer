using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FreeWorldMultiplayer;

internal static class RecoveryPointerChecks
{
    private sealed class Fixture
    {
        internal SaveStore Store;
        internal string Id, Directory, Active, Backup, FirstFile, SecondFile;
        internal byte[] BackupBytes, FirstBytes;
    }

    private static Fixture TwoGenerations(string root, string name)
    {
        string path = Path.Combine(root, "synthetic-recovery-" + name);
        var store = new SaveStore(path);
        string id = Guid.NewGuid().ToString("N");
        var world = new WorldState { WorldId = id, Name = "first-generation",
            Snapshot = new WorldSnapshot { NativeJson = "synthetic-world-not-real-game-data" },
            Transactions = new Dictionary<string, CommandResult> { { "one-time", new CommandResult { Applied = true } } } };
        store.Save(world);
        world.Name = "second-generation";
        store.Save(world);
        string dir = Path.Combine(path, "worlds", id);
        var f = new Fixture {
            Store = store, Id = id, Directory = dir,
            Active = Path.Combine(dir, "active.json"),
            Backup = Path.Combine(dir, "active.json.bak"),
            FirstFile = Path.Combine(dir, "generation_0000000000000001.json"),
            SecondFile = Path.Combine(dir, "generation_0000000000000002.json")
        };
        f.BackupBytes = File.ReadAllBytes(f.Backup);
        f.FirstBytes = File.ReadAllBytes(f.FirstFile);
        return f;
    }

    private static WorldState Recover(Fixture f)
    {
        WorldState state = f.Store.Load(f.Id);
        if (state.SaveGeneration != 1 || String.IsNullOrWhiteSpace(f.Store.LastLoadWarning))
            throw new InvalidOperationException("Synthetic recovery fixture did not select backup generation one.");
        return state;
    }

    private static void ConfirmFallback(Fixture f, WorldState state, Action<bool, string> check, string reason)
    {
        check(state.SaveGeneration == 3, reason + ": resumed save committed a new generation");
        check(File.ReadAllBytes(f.Backup).SequenceEqual(f.BackupBytes),
            reason + ": backup still points to the original verified generation");
        check(File.Exists(f.FirstFile) && File.ReadAllBytes(f.FirstFile).SequenceEqual(f.FirstBytes),
            reason + ": first complete generation bytes survived cleanup unchanged");
        string newFile = Path.Combine(f.Directory, "generation_0000000000000003.json");
        File.WriteAllText(newFile, "{damaged-after-success", Encoding.UTF8);
        WorldState reloaded = new SaveStore(Path.GetDirectoryName(Path.GetDirectoryName(f.Directory))).Load(f.Id);
        check(reloaded.SaveGeneration == 1 && reloaded.Name == "first-generation",
            reason + ": a later damaged new generation still falls back to original generation");
    }

    internal static void Run(string root, Action<bool, string> check)
    {
        var malformed = TwoGenerations(root, "malformed-active");
        File.WriteAllText(malformed.Active, "{synthetic-broken-pointer", Encoding.UTF8);
        WorldState restored = Recover(malformed);
        restored.Name = "resumed";
        malformed.Store.Save(restored);
        ConfirmFallback(malformed, restored, check, "corrupt active pointer");

        var damagedGeneration = TwoGenerations(root, "damaged-newest-generation");
        File.WriteAllText(damagedGeneration.SecondFile, "synthetic damaged active generation", Encoding.UTF8);
        WorldState previous = Recover(damagedGeneration);
        damagedGeneration.Store.Save(previous);
        ConfirmFallback(damagedGeneration, previous, check, "corrupt generation with parseable pointer");

        var missing = TwoGenerations(root, "missing-active-pointer");
        File.Delete(missing.Active);
        WorldState missingRecovered = Recover(missing);
        missing.Store.Save(missingRecovered);
        ConfirmFallback(missing, missingRecovered, check, "missing active pointer");

        var replaced = TwoGenerations(root, "backup-changed-after-load");
        File.WriteAllText(replaced.Active, "{synthetic-broken-pointer", Encoding.UTF8);
        WorldState beforeMutation = Recover(replaced);
        File.WriteAllText(replaced.Backup, "{corrupted-after-recovery", Encoding.UTF8);
        string currentActiveBytes = File.ReadAllText(replaced.Active, Encoding.UTF8);
        bool rejected = false;
        try { replaced.Store.Save(beforeMutation); }
        catch (Exception) { rejected = true; }
        check(rejected && beforeMutation.SaveGeneration == 1,
            "changed backup pointer aborts save before changing in-memory generation");
        check(File.ReadAllText(replaced.Active, Encoding.UTF8) == currentActiveBytes &&
            File.ReadAllBytes(replaced.FirstFile).SequenceEqual(replaced.FirstBytes),
            "failed backup-pointer validation leaves active pointer and old generation intact");
        check(!File.Exists(Path.Combine(replaced.Directory, "generation_0000000000000003.json")),
            "rejected backup-pointer mutation does not write a new generation");

        var changed = TwoGenerations(root, "generation-changed-after-load");
        File.WriteAllText(changed.Active, "{synthetic-broken-pointer", Encoding.UTF8);
        WorldState beforeCorruption = Recover(changed);
        File.WriteAllText(changed.FirstFile, "synthetic corruption after successful load", Encoding.UTF8);
        bool checksumRejected = false;
        try { changed.Store.Save(beforeCorruption); }
        catch (InvalidDataException) { checksumRejected = true; }
        check(checksumRejected && beforeCorruption.SaveGeneration == 1,
            "modified recovered-generation bytes reject pre-save recovery");
        check(!File.Exists(Path.Combine(changed.Directory, "generation_0000000000000003.json")),
            "checksum failure does not produce a misleading fresh commit");

        var blocked = TwoGenerations(root, "pointer-restoration-failed");
        File.WriteAllText(blocked.Active, "{synthetic-broken-pointer", Encoding.UTF8);
        WorldState pending = Recover(blocked);
        File.Delete(blocked.Active);
        Directory.CreateDirectory(blocked.Active);
        bool blockedWrite = false;
        try { blocked.Store.Save(pending); }
        catch (IOException) { blockedWrite = true; }
        check(blockedWrite && pending.SaveGeneration == 1,
            "failed recovery-pointer rewrite preserves in-memory save generation");
        check(File.ReadAllBytes(blocked.Backup).SequenceEqual(blocked.BackupBytes) &&
            File.ReadAllBytes(blocked.FirstFile).SequenceEqual(blocked.FirstBytes),
            "interrupted fallback-pointer repair never erases the only recoverable generation");
    }
}
