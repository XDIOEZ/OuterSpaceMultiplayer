using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using FreeWorldMultiplayer;

internal static class RelationValidationChecks
{
    private static WorldState MakeWorld(string id, Relation relation)
    {
        return new WorldState
        {
            WorldId = id,
            Name = "synthetic-relation-test",
            Snapshot = new WorldSnapshot { NativeJson = "test-only-native-state" },
            Relationships = new List<RelationshipRecord>
            {
                new RelationshipRecord { PlayerA = "synthetic-a", PlayerB = "synthetic-b",
                    Relation = relation }
            }
        };
    }

    private static string GenerationPath(string dir, int generation)
    {
        return Path.Combine(dir, "generation_" + generation.ToString("D16") + ".json");
    }

    // Model a *legacy* file that was written before validating the enum.
    // Rehash the fixture to prove rejection is semantic, not a checksum error.
    private static void InjectLegacyRelation(string dir, int generation, Relation invalid)
    {
        string generationPath = GenerationPath(dir, generation);
        WorldState legacy = JsonData.Read<WorldState>(File.ReadAllText(generationPath, Encoding.UTF8));
        legacy.Relationships[0].Relation = invalid;
        byte[] content = Encoding.UTF8.GetBytes(JsonData.Write(legacy));
        File.WriteAllBytes(generationPath, content);
        string pointerPath = Path.Combine(dir, "active.json");
        SavePointer pointer = JsonData.Read<SavePointer>(File.ReadAllText(pointerPath, Encoding.UTF8));
        using (var sha = SHA256.Create()) pointer.Sha256 = Convert.ToBase64String(sha.ComputeHash(content));
        File.WriteAllText(pointerPath, JsonData.Write(pointer), Encoding.UTF8);
    }

    internal static void Run(string root, Action<bool, string> check)
    {
        foreach (Relation valid in new[] { Relation.Neutral, Relation.Friendly, Relation.Hostile })
        {
            check(RelationValidation.IsDefined(valid), "all three real relationship states are accepted");
            RelationValidation.RequireDefined(valid);
        }
        foreach (Relation invalid in new[] { (Relation)(-1), (Relation)3, (Relation)99 })
        {
            bool rejected = false;
            try { RelationValidation.RequireDefined(invalid); }
            catch (InvalidDataException) { rejected = true; }
            check(!RelationValidation.IsDefined(invalid) && rejected,
                "invalid numeric JSON relationship value is rejected before creating a relation");
        }

        bool missingRejected = false, nullEntryRejected = false;
        try { RelationValidation.ValidateSavedRelations(null); }
        catch (InvalidDataException) { missingRejected = true; }
        try { RelationValidation.ValidateSavedRelations(new List<RelationshipRecord> { null }); }
        catch (InvalidDataException) { nullEntryRejected = true; }
        check(missingRejected && nullEntryRejected, "null relationship lists and rows fail closed");

        string testRoot = Path.Combine(root, "synthetic-relationship-validations-only");
        var store = new SaveStore(testRoot);
        string validId = Guid.NewGuid().ToString("N");
        WorldState world = MakeWorld(validId, Relation.Neutral);
        store.Save(world);
        check(world.SaveGeneration == 1 && store.Load(validId).Relationships[0].Relation == Relation.Neutral,
            "neutral relation round-trips through a real complete save");
        world.Relationships[0].Relation = Relation.Friendly;
        world.Relationships[0].FriendlyConsentA = true;
        world.Relationships[0].FriendlyConsentB = true;
        store.Save(world);
        WorldState friendly = store.Load(validId);
        check(friendly.Relationships[0].Relation == Relation.Friendly &&
            friendly.Relationships[0].FriendlyConsentA && friendly.Relationships[0].FriendlyConsentB,
            "friendly relationship and mutual consent retain their saved semantics");
        world.Relationships[0].Relation = Relation.Hostile;
        world.Relationships[0].FriendlyConsentA = false;
        world.Relationships[0].FriendlyConsentB = false;
        store.Save(world);
        check(store.Load(validId).Relationships[0].Relation == Relation.Hostile,
            "hostile relationship survives a real save without becoming neutral");

        string invalidNewId = Guid.NewGuid().ToString("N");
        WorldState invalidNew = MakeWorld(invalidNewId, (Relation)99);
        bool invalidSaveRejected = false;
        try { store.Save(invalidNew); }
        catch (InvalidDataException) { invalidSaveRejected = true; }
        check(invalidSaveRejected && invalidNew.SaveGeneration == 0 &&
            !Directory.Exists(Path.Combine(testRoot, "worlds", invalidNewId)),
            "invalid relation cannot create a new save directory or generation");

        string dir = Path.Combine(testRoot, "worlds", validId);
        string priorPointer = File.ReadAllText(Path.Combine(dir, "active.json"), Encoding.UTF8);
        world.Relationships[0].Relation = (Relation)(-1);
        bool existingSaveRejected = false;
        try { store.Save(world); }
        catch (InvalidDataException) { existingSaveRejected = true; }
        check(existingSaveRejected && world.SaveGeneration == 3 &&
            File.ReadAllText(Path.Combine(dir, "active.json"), Encoding.UTF8) == priorPointer,
            "invalid relationship cannot mutate an existing save pointer or generation");
        check(!File.Exists(GenerationPath(dir, 4)), "bad relation does not write an orphan save");

        // Invalid enum in the newest generation with a *matching* SHA:
        // the loader must fall back to the last complete valid generation.
        string fallbackId = Guid.NewGuid().ToString("N");
        WorldState fallbackWorld = MakeWorld(fallbackId, Relation.Neutral);
        store.Save(fallbackWorld);
        fallbackWorld.Relationships[0].Relation = Relation.Hostile;
        store.Save(fallbackWorld);
        string fallbackDir = Path.Combine(testRoot, "worlds", fallbackId);
        byte[] beforeBackup = File.ReadAllBytes(Path.Combine(fallbackDir, "active.json.bak"));
        InjectLegacyRelation(fallbackDir, 2, (Relation)99);
        WorldState fallback = store.Load(fallbackId);
        check(fallback.SaveGeneration == 1 &&
            fallback.Relationships[0].Relation == Relation.Neutral &&
            !String.IsNullOrWhiteSpace(store.LastLoadWarning),
            "old invalid relation generation is rejected and healthy backup selected");
        check(File.ReadAllBytes(Path.Combine(fallbackDir, "active.json.bak")).SequenceEqual(beforeBackup),
            "loading a legacy invalid enum does not rewrite the backup pointer");
        check(SaveStore.ListWorlds(testRoot).Any(w => w.WorldId == fallbackId &&
            w.SaveGeneration == 1 && String.IsNullOrWhiteSpace(w.Error)),
            "continue-world list only exposes the healthy fallback generation");

        string legacyOnlyId = Guid.NewGuid().ToString("N");
        store.Save(MakeWorld(legacyOnlyId, Relation.Friendly));
        string legacyOnlyDir = Path.Combine(testRoot, "worlds", legacyOnlyId);
        InjectLegacyRelation(legacyOnlyDir, 1, (Relation)3);
        byte[] legacyBytes = File.ReadAllBytes(GenerationPath(legacyOnlyDir, 1));
        bool singleLegacyRejected = false;
        try { store.Load(legacyOnlyId); }
        catch (InvalidDataException) { singleLegacyRejected = true; }
        check(singleLegacyRejected, "legacy save with an undefined enum and no valid backup is refused");
        check(SaveStore.ListWorlds(testRoot).Any(w => w.WorldId == legacyOnlyId &&
            !String.IsNullOrWhiteSpace(w.Error)),
            "invalid legacy relation appears disabled, not as a playable neutral relationship");
        check(File.ReadAllBytes(GenerationPath(legacyOnlyDir, 1)).SequenceEqual(legacyBytes),
            "rejected legacy generation is preserved byte-for-byte for explicit recovery");
    }
}
