using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FreeWorldMultiplayer;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class MapCodecChecks
{
    private const string WorldId = "demo_world";
    private const long MapId = 1000001000004;

    internal static void Run(Action<bool, string> check)
    {
        var packed = Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('X', 1024 * 1024)));
        var tiny = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"name\":\"hero\"}"));
        var checkpoint = new JObject
        {
            ["Map"] = packed,
            ["Terrain"] = JValue.CreateNull(),
            ["Heroes"] = new JArray(tiny),
            ["Animals"] = new JArray(),
            ["DepartedHeroes"] = new JArray(),
            ["DepartedAnimals"] = new JArray(),
            ["Loot"] = new JArray(),
            ["Landings"] = new JArray
            {
                new JObject
                {
                    ["Expedition"] = tiny,
                    ["Heroes"] = new JArray(),
                    ["Animals"] = new JArray()
                }
            }
        };
        var snapshot = new MapSnapshot
        {
            MapId = MapId,
            Revision = 42,
            NativeJson = checkpoint.ToString(Formatting.None),
            EntityOwners = new Dictionary<long, string> { { 100L, "player" } }
        };
        var encoded = MapSnapshotDeltaCodec.Encode(WorldId, snapshot);
        check(encoded.Manifest.TotalLength > 1024 * 1024, "large map codec payload");
        check(encoded.Manifest.Chunks.Count > 1, "large map splits into chunks");

        var full = MapSnapshotDeltaCodec.MakeTransfer(encoded, null);
        check(full.ChangedChunks.Count == encoded.Content.Count, "full transfer sends each unique content chunk");
        MapCacheStore.HashCalls = 0;
        var restored = MapSnapshotDeltaCodec.Reassemble(full, WorldId, MapId, null);
        check(MapCacheStore.HashCalls == full.ChangedChunks.Count + 1,
            "cold map validates each unique incoming chunk only once plus final snapshot hash");
        SameSnapshot(check, restored, snapshot, "full transfer");

        var available = encoded.Manifest.Chunks.Select(x => x.Hash).Distinct().Where((hash, i) => i % 2 == 0).ToList();
        var cache = new Dictionary<string, byte[]>(encoded.Content, StringComparer.Ordinal);
        var mixed = MapSnapshotDeltaCodec.MakeTransfer(encoded, available);
        check(mixed.ChangedChunks.Count < full.ChangedChunks.Count, "warm transfer skips cached chunks");
        MapCacheStore.HashCalls = 0;
        restored = MapSnapshotDeltaCodec.Reassemble(mixed, WorldId, MapId,
            hash => cache.TryGetValue(hash, out var bytes) ? bytes : null);
        check(MapCacheStore.HashCalls >= mixed.ChangedChunks.Count + 1
            && MapCacheStore.HashCalls < full.ChangedChunks.Count * 2 + 1,
            "warm map still validates cached chunks without double hashing received chunks");
        SameSnapshot(check, restored, snapshot, "partial cache");

        var hot = MapSnapshotDeltaCodec.MakeTransfer(encoded, encoded.Content.Keys);
        check(hot.ChangedChunks.Count == 0, "fully cached transfer sends no changed chunks");
        restored = MapSnapshotDeltaCodec.Reassemble(hot, WorldId, MapId,
            hash => cache.TryGetValue(hash, out var bytes) ? bytes : null);
        SameSnapshot(check, restored, snapshot, "hot cache");

        bool failed = false;
        try { MapSnapshotDeltaCodec.Reassemble(mixed, WorldId, MapId, _ => null); }
        catch (InvalidDataException) { failed = true; }
        check(failed, "missing cached content rejects incomplete map");

        failed = false;
        try { MapSnapshotDeltaCodec.Reassemble(full, "another_world", MapId, null); }
        catch (InvalidDataException) { failed = true; }
        check(failed, "different world cannot reuse map content");

        var first = full.ChangedChunks[0];
        var corrupt = (byte[])first.Data.Clone();
        corrupt[0] ^= 1;
        var changed = full.ChangedChunks.Select(x => new MapCacheChunk
        {
            Hash = x.Hash, Data = x == first ? corrupt : x.Data
        }).ToList();
        failed = false;
        try
        {
            MapSnapshotDeltaCodec.Reassemble(new MapCacheTransfer { Manifest = full.Manifest, ChangedChunks = changed },
                WorldId, MapId, null);
        }
        catch (InvalidDataException) { failed = true; }
        check(failed, "corrupt chunk rejected before applying native map");
    }

    private static void SameSnapshot(Action<bool, string> check, MapSnapshot actual, MapSnapshot expected, string name)
    {
        check(actual.MapId == expected.MapId && actual.Revision == expected.Revision, name + " identity and revision");
        check(JToken.DeepEquals(JToken.Parse(actual.NativeJson), JToken.Parse(expected.NativeJson)),
            name + " native checkpoint");
        check(actual.EntityOwners.Count == expected.EntityOwners.Count
            && actual.EntityOwners[100L] == "player", name + " owner registry");
    }
}
