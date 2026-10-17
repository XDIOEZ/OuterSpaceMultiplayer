using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FreeWorldMultiplayer;

internal static class Program
{
    private static int checks;

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("FAILED: " + description);
        checks++;
    }

    private static bool Rejects<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    private static string ScopedIdentityFile(string root, string platform, string runtime)
    {
        string full = Path.GetFullPath(runtime).TrimEnd('\\', '/').ToUpperInvariant();
        string hash = CryptoData.Hash(platform + "\n" + full).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return Path.Combine(root, "identities", platform, hash, "identity.json");
    }

    private static void IdentityChecks(string root)
    {
        string steamA = Path.Combine(root, "fake-steam-A", "Outer Space_Data");
        string steamB = Path.Combine(root, "fake-steam-B", "Outer Space_Data");
        string tapTap = Path.Combine(root, "fake-taptap", "Outer Space_Data");
        string p3 = Path.Combine(root, "fake-project3", "Library");
        string store = Path.Combine(root, "private-identity-test-only");

        LocalIdentity first = IdentityStore.LoadOrCreateScoped(store, "Steam", steamA, "A");
        Check(!String.IsNullOrWhiteSpace(first.CredentialId) &&
            !String.IsNullOrWhiteSpace(first.Secret), "new local identity has a random credential and secret");
        LocalIdentity same = IdentityStore.LoadOrCreateScoped(store, "Steam", steamA, "Renamed");
        Check(first.CredentialId == same.CredentialId && first.Secret == same.Secret &&
            same.Nickname == "Renamed", "reopening same installation preserves identity, only updates in-memory nickname");
        LocalIdentity otherSteam = IdentityStore.LoadOrCreateScoped(store, "Steam", steamB, "A");
        Check(first.CredentialId != otherSteam.CredentialId && first.Secret != otherSteam.Secret,
            "two distinct Steam installations do not reuse the default identity");
        LocalIdentity anotherPlatform = IdentityStore.LoadOrCreateScoped(store, "TapTap", steamA, "A");
        Check(anotherPlatform.CredentialId != first.CredentialId,
            "Steam and TapTap use separate identity namespaces even at the same data path");
        LocalIdentity anotherDirectory = IdentityStore.LoadOrCreateScoped(store, "TapTap", tapTap, "A");
        Check(anotherDirectory.CredentialId != anotherPlatform.CredentialId,
            "one platform's different runtime data directories remain separate");
        LocalIdentity project = IdentityStore.LoadOrCreateScoped(store, "Project3", p3, "A");
        Check(project.CredentialId != first.CredentialId &&
            project.CredentialId != anotherPlatform.CredentialId,
            "Project3 identity is never imported from a Steam/TapTap run");
        Check(Rejects<ArgumentException>(() =>
            IdentityStore.LoadOrCreateScoped(store, "UnknownPlatform", steamA, "A")),
            "unknown transport platform is rejected before credentials are written");
        Check(Rejects<ArgumentException>(() =>
            IdentityStore.LoadOrCreateScoped(store, "Steam", "", "A")),
            "missing actual runtime data path is never assigned an identity");

        PlayerRecord host = new PlayerRecord { CredentialId = first.CredentialId,
            CredentialHash = CryptoData.Hash(first.Secret) };
        LocalIdentity recovered = IdentityStore.FindExistingForHost(
            store, "Steam", steamB, "A", host);
        Check(recovered != null && recovered.CredentialId == first.CredentialId
            && recovered.Secret == first.Secret,
            "explicit host continuation locates a matching historical scoped credential");
        Check(IdentityStore.FindExistingForHost(store, "Steam", steamB, "A",
                new PlayerRecord { CredentialId = "unmatched", CredentialHash = "none" }) == null,
            "unmatched host credential does not fabricate a new or nickname-matched identity");

        // FileShare.None simulates a second process without ever running the game.
        string lockPath = Path.Combine(Path.GetDirectoryName(ScopedIdentityFile(
            store, "Steam", steamA)), "profile.lock");
        using (var exclusive = new FileStream(lockPath, FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None))
        {
            Check(Rejects<IOException>(() =>
                IdentityStore.LoadOrCreateScoped(store, "Steam", steamA, "A")),
                "concurrent process lock fails without changing an established credential");
        }
        Check(IdentityStore.LoadOrCreateScoped(store, "Steam", steamA, "A").Secret ==
            first.Secret, "locked identity remains unchanged after rejected concurrent operation");

        string damagedRuntime = Path.Combine(root, "fake-damaged", "Outer Space_Data");
        string damagedPath = ScopedIdentityFile(store, "Other", damagedRuntime);
        Directory.CreateDirectory(Path.GetDirectoryName(damagedPath));
        const string invalidIdentity = "{not-valid-json";
        File.WriteAllText(damagedPath, invalidIdentity, Encoding.UTF8);
        Check(Rejects<InvalidDataException>(() =>
                IdentityStore.LoadOrCreateScoped(store, "Other", damagedRuntime, "A")),
            "malformed stored identity must not be silently replaced with a new credential");
        Check(File.ReadAllText(damagedPath, Encoding.UTF8) == invalidIdentity,
            "invalid identity's original bytes are preserved after failure");

        // The legacy Steam migration is exercised only with a synthetic test root.
        string legacyStore = Path.Combine(root, "private-legacy-test-only");
        string legacyA = Path.Combine(root, "fake-legacy-A");
        string legacyB = Path.Combine(root, "fake-legacy-B");
        var legacyIdentity = new LocalIdentity { CredentialId = "synthetic-credential",
            Secret = CryptoData.RandomToken(), Nickname = "Original" };
        string legacyFile = Path.Combine(legacyStore, "identity.json");
        AtomicFile.Write(legacyFile, Encoding.UTF8.GetBytes(JsonData.Write(legacyIdentity)));
        LocalIdentity migrated = IdentityStore.LoadOrCreateScoped(legacyStore, "Steam", legacyA, "A");
        Check(migrated.CredentialId == legacyIdentity.CredentialId &&
            migrated.Secret == legacyIdentity.Secret,
            "exactly one first Steam installation adopts a synthetic legacy identity");
        LocalIdentity next = IdentityStore.LoadOrCreateScoped(legacyStore, "Steam", legacyB, "B");
        Check(next.CredentialId != legacyIdentity.CredentialId &&
            next.Secret != legacyIdentity.Secret,
            "another Steam installation does not duplicate the migrated legacy credential");
        Check(IdentityStore.LoadOrCreateScoped(legacyStore, "Steam", legacyA, "A").Secret ==
            migrated.Secret, "migrated installation is stable across reloads");
        string claim = Path.Combine(legacyStore, "identities", "legacy-steam-owner.json");
        Check(File.Exists(claim) && File.Exists(legacyFile),
            "successful migration retains the original legacy file and an ownership claim");

        string brokenStore = Path.Combine(root, "private-broken-claim-only");
        string brokenClaim = Path.Combine(brokenStore, "identities", "legacy-steam-owner.json");
        AtomicFile.Write(brokenClaim, Encoding.UTF8.GetBytes("{broken-claim"));
        string corruptedClaimBytes = File.ReadAllText(brokenClaim);
        Check(Rejects<Newtonsoft.Json.JsonException>(() =>
                IdentityStore.LoadOrCreateScoped(brokenStore, "Steam", legacyA, "A")),
            "malformed legacy migration claim fails closed; no new credential issued");
        Check(File.ReadAllText(brokenClaim) == corruptedClaimBytes &&
            !File.Exists(ScopedIdentityFile(brokenStore, "Steam", legacyA)),
            "bad legacy claim and missing identity stay untouched after failed migration");
    }

    private static void SettingsChecks(string root)
    {
        string store = Path.Combine(root, "synthetic-settings-only");
        ModSettings defaults = ModSettingsStore.Load(store);
        Check(defaults.SchemaVersion == 1 && defaults.StartingEmployeeCount == 4 &&
            defaults.MaxBasesPerPlayer == 1 && defaults.JoinHostGalaxyOnFirstSpawn &&
            defaults.DefaultMaxPlayers == 4, "new-room defaults match the V1 requirements");
        Check(!File.Exists(Path.Combine(store, "settings.json")),
            "reading default settings does not write or create a player configuration");
        Check(Rejects<ArgumentOutOfRangeException>(() =>
            ModSettingsStore.ValidateStartingEmployeeCount(0)),
            "zero employees cannot silently become a valid new-room default");
        Check(Rejects<ArgumentOutOfRangeException>(() =>
            ModSettingsStore.ValidateStartingEmployeeCount(33)),
            "employee maximum is enforced");
        Check(Rejects<ArgumentOutOfRangeException>(() =>
            ModSettingsStore.ValidateMaxBasesPerPlayer(0)), "zero player bases is rejected");
        Check(Rejects<ArgumentOutOfRangeException>(() =>
            ModSettingsStore.ValidateMaxBasesPerPlayer(65)), "base cap maximum is enforced");
        Check(Rejects<ArgumentException>(() => ModSettingsStore.Save(store, null)),
            "null settings cannot be persisted");

        ModSettings settings = new ModSettings
        {
            StartingEmployeeCount = 6,
            MaxBasesPerPlayer = 3,
            JoinHostGalaxyOnFirstSpawn = false,
            DefaultMaxPlayers = 3,
            DefaultPort = 27016,
            DefaultUseSteam = true
        };
        ModSettingsStore.Save(store, settings);
        ModSettings loaded = ModSettingsStore.Load(store);
        Check(loaded.StartingEmployeeCount == 6 && loaded.MaxBasesPerPlayer == 3 &&
            !loaded.JoinHostGalaxyOnFirstSpawn && loaded.DefaultPort == 27016 &&
            loaded.DefaultMaxPlayers == 3 && loaded.DefaultUseSteam,
            "only the synthetic settings document is atomically persisted and reloaded");
        string settingsFile = Path.Combine(store, "settings.json");
        string before = File.ReadAllText(settingsFile, Encoding.UTF8);
        settings.DefaultPort = 0;
        Check(Rejects<ArgumentOutOfRangeException>(() =>
            ModSettingsStore.Save(store, settings)), "invalid port cannot overwrite saved settings");
        Check(File.ReadAllText(settingsFile, Encoding.UTF8) == before,
            "failed validation preserves the last valid on-disk settings bytes");
        settings.DefaultPort = 27016;
        settings.DefaultMaxPlayers = 1;
        Check(Rejects<ArgumentOutOfRangeException>(() =>
            ModSettingsStore.Save(store, settings)), "multiplayer requires at least two players");
        settings.DefaultMaxPlayers = 3;
        settings.SchemaVersion = 2;
        Check(Rejects<InvalidDataException>(() =>
            ModSettingsStore.Save(store, settings)), "future/unknown settings schema is rejected");

        const string broken = "{synthetic-broken-settings";
        File.WriteAllText(settingsFile, broken, Encoding.UTF8);
        Check(Rejects<InvalidDataException>(() => ModSettingsStore.Load(store)),
            "malformed saved settings cannot silently reset existing room defaults");
        Check(File.ReadAllText(settingsFile, Encoding.UTF8) == broken,
            "malformed settings preserve their original bytes");
    }

    private static WorldState World(string id, string name)
    {
        return new WorldState { WorldId = id, Name = name,
            Snapshot = new WorldSnapshot { NativeJson = "synthetic-native-checkpoint" },
            Transactions = new Dictionary<string, CommandResult>
            {
                { "synthetic-txn", new CommandResult { Applied = true } }
            } };
    }

    private static void SaveChecks(string root)
    {
        string storeRoot = Path.Combine(root, "synthetic-worlds-only");
        var store = new SaveStore(storeRoot);
        string id = Guid.NewGuid().ToString("N");
        WorldState world = World(id, "first-complete");
        Check(Rejects<InvalidDataException>(() => store.Load(id)),
            "a missing world does not fabricate a successful save");
        Check(Rejects<InvalidDataException>(() => store.Load("../untrusted")),
            "arbitrary world paths cannot escape the save root");

        store.Save(world);
        Check(world.SaveGeneration == 1 && world.Transactions["synthetic-txn"].Saved &&
            world.Transactions["synthetic-txn"].SaveGeneration == 1,
            "first successful save commits generation and transaction confirmation together");
        WorldState first = store.Load(id);
        Check(first.SaveGeneration == 1 && first.Name == "first-complete" &&
            first.Transactions["synthetic-txn"].Saved && first.Snapshot.NativeJson ==
            "synthetic-native-checkpoint", "first complete world reloads with native and transaction data");
        Check(SaveStore.ListWorlds(storeRoot).Any(w => w.WorldId == id &&
            w.SaveGeneration == 1 && String.IsNullOrEmpty(w.Error) && w.Name == "first-complete"),
            "complete world is selectable in the independent multiplayer list");

        world.Name = "second-complete";
        world.Transactions["new-transaction"] = new CommandResult { Applied = true };
        store.Save(world);
        Check(world.SaveGeneration == 2 && world.Transactions["new-transaction"].Saved &&
            world.Transactions["new-transaction"].SaveGeneration == 2,
            "second save does not reuse old commit confirmations");
        string directory = Path.Combine(storeRoot, "worlds", id);
        string latest = Path.Combine(directory, "generation_" + 2L.ToString("D16") + ".json");
        string previous = Path.Combine(directory, "generation_" + 1L.ToString("D16") + ".json");
        Check(File.Exists(latest) && File.Exists(previous) &&
            File.Exists(Path.Combine(directory, "active.json.bak")),
            "successful second generation retains an immediately preceding complete save");
        Check(SaveStore.ListWorlds(storeRoot).Any(w => w.WorldId == id &&
            w.SaveGeneration == 2 && w.Name == "second-complete" &&
            String.IsNullOrEmpty(w.Error)), "valid newest generation is listed as selectable");

        File.WriteAllText(latest, "corrupted synthetic generation", Encoding.UTF8);
        WorldState fallback = store.Load(id);
        Check(fallback.SaveGeneration == 1 && fallback.Name == "first-complete" &&
            !String.IsNullOrWhiteSpace(store.LastLoadWarning),
            "corrupted active generation falls back to last complete generation with warning");
        Check(SaveStore.ListWorlds(storeRoot).Any(w => w.WorldId == id &&
            w.SaveGeneration == 1 && w.Name.Contains("first-complete") &&
            w.Name.Contains("备份可恢复") && String.IsNullOrEmpty(w.Error)),
            "invalid current generation is still selectable through verified backup");
        string pointer = Path.Combine(directory, "active.json");
        string pointerBytes = File.ReadAllText(pointer, Encoding.UTF8);
        File.WriteAllText(pointer, "{damaged-pointer", Encoding.UTF8);
        Check(store.Load(id).SaveGeneration == 1 &&
            File.ReadAllText(pointer, Encoding.UTF8).Contains("{damaged-pointer"),
            "broken active pointer does not rewrite or erase the older complete generation");
        Check(SaveStore.ListWorlds(storeRoot).Any(w => w.WorldId == id &&
            w.SaveGeneration == 1 && String.IsNullOrEmpty(w.Error) &&
            w.Name.Contains("备份可恢复")),
            "broken current pointer is recovered in the continue-world list");
        string brokenPointerBytes = File.ReadAllText(pointer, Encoding.UTF8);
        File.Delete(pointer);
        Check(SaveStore.ListWorlds(storeRoot).Any(w => w.WorldId == id &&
            w.SaveGeneration == 1 && String.IsNullOrEmpty(w.Error)),
            "missing active pointer does not hide a valid backup from the UI");
        Check(!File.Exists(pointer), "read-only list does not silently repair missing active index");
        File.WriteAllText(pointer, brokenPointerBytes, Encoding.UTF8);
        Check(File.Exists(previous), "fallback path retains the older world checkpoint on disk");
        File.WriteAllText(Path.Combine(directory, "active.json.bak"), "{also-damaged", Encoding.UTF8);
        Check(Rejects<InvalidDataException>(() => store.Load(id)),
            "both invalid pointers fail closed instead of loading an invented world");
        Check(SaveStore.ListWorlds(storeRoot).Any(w => w.WorldId == id &&
            !String.IsNullOrWhiteSpace(w.Error)),
            "unrecoverable world remains visible but disabled in continue-world list");

        // Simulate an interrupted pointer commit using a directory occupying
        // the filename: the failed operation may leave an orphan generation.
        string failingId = Guid.NewGuid().ToString("N");
        string failingDir = Path.Combine(storeRoot, "worlds", failingId);
        Directory.CreateDirectory(Path.Combine(failingDir, "active.json"));
        WorldState interrupted = World(failingId, "uncommitted");
        Check(Rejects<IOException>(() => store.Save(interrupted)),
            "a blocked active pointer prevents a world from reporting save success");
        Check(interrupted.SaveGeneration == 0 &&
            !interrupted.Transactions["synthetic-txn"].Saved &&
            interrupted.Transactions["synthetic-txn"].SaveGeneration == 0,
            "failed pointer commit rolls back in-memory generation and transaction confirmation");
        Check(Rejects<InvalidDataException>(() => store.Load(failingId)),
            "an unselected orphan generation cannot masquerade as a committed world");
        string orphan = Path.Combine(failingDir, "generation_" + 1L.ToString("D16") + ".json");
        Check(File.Exists(orphan),
            "interrupted save preserves its uncommitted candidate for safe later cleanup");
        Directory.Delete(Path.Combine(failingDir, "active.json"));
        store.Save(interrupted);
        Check(interrupted.SaveGeneration == 2 && store.Load(failingId).SaveGeneration == 2,
            "retry never overwrites a failed orphan and commits a fresh complete generation");

        string pointerFile = Path.Combine(failingDir, "active.json");
        string committedBytes = File.ReadAllText(pointerFile, Encoding.UTF8);
        Check(Rejects<InvalidDataException>(() =>
            store.Save(new WorldState { WorldId = failingId })),
            "a missing native checkpoint is not saved as success");
        Check(File.ReadAllText(pointerFile, Encoding.UTF8) == committedBytes,
            "rejected invalid world leaves the committed active pointer byte-identical");
        Check(File.ReadAllText(pointer, Encoding.UTF8) != pointerBytes,
            "synthetic checksum corruption remained a local fixture, not a hidden repair");
    }

    public static int Main()
    {
        try
        {
            string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", ".."));
            string root = Path.Combine(repo, "artifacts", "identity-storage-checks",
                "case-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            IdentityChecks(root);
            SettingsChecks(root);
            SaveChecks(root);
            RecoveryPointerChecks.Run(root, Check);
            RelationValidationChecks.Run(root, Check);
            Console.WriteLine("PASS: " + checks +
                " production IdentityStore / ModSettingsStore / SaveStore checks");
            Console.WriteLine("SYNTHETIC_FIXTURE=" + root);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.GetType().Name + ": " + error.Message);
            return 1;
        }
    }
}
