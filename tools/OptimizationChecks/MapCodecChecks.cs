using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
        MapCacheStore.HashCalls = 0;
        var encoded = MapSnapshotDeltaCodec.Encode(WorldId, snapshot);
        check(encoded.Manifest.TotalLength > 1024 * 1024, "large map codec payload");
        check(encoded.Manifest.Chunks.Count > 1, "large map splits into chunks");
        check(encoded.Manifest.Chunks.Count > encoded.Content.Count, "large repeated map has duplicate cache chunk references");
        // Freeze the on-wire cache cut boundaries: the speedup must not force
        // an existing guest to discard reusable chunks from its old manifest.
        check(LegacyChunkBoundariesMatch(encoded), "optimized rolling hash preserves every legacy map chunk boundary");
        var contributions = (ulong[])typeof(MapSnapshotDeltaCodec)
            .GetField("RollingByteHashes", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        check(contributions.Length == 256 && Enumerable.Range(0, 256)
            .All(i => contributions[i] == LegacyByteHash((byte)i)),
            "precomputed rolling contributions exactly match all 256 legacy byte values");
        long previousChunkCopyBytes = encoded.Manifest.Chunks.Sum(chunk => (long)chunk.Length);
        long uniqueChunkCopyBytes = encoded.Content.Values.Sum(data => (long)data.Length);
        check(previousChunkCopyBytes == encoded.Manifest.TotalLength && uniqueChunkCopyBytes < previousChunkCopyBytes,
            "repeated source chunks now allocate only their distinct retained contents");
        check(MapCacheStore.HashCalls == encoded.Manifest.Chunks.Count + 1,
            "encoder hashes each original chunk slice and one full snapshot");
        check(encoded.Content.All(chunk => MapCacheStore.HashContent(chunk.Value) == chunk.Key),
            "encoded unique chunks retain the same SHA256 as their original slices");
        check(MatchesLegacySerializedBytes(encoded, snapshot),
            "large cached map bytes, SHA and cuts match legacy UTF-16-to-UTF-8 serialization exactly");
        byte[] fullPayloadBytes = ExpandEncodedBytes(encoded);
        check(JToken.DeepEquals(JObject.Parse(Encoding.UTF8.GetString(fullPayloadBytes)),
            MapSnapshotDeltaCodec.ParseCachePayload(fullPayloadBytes)),
            "large map streaming JSON reader reconstructs the same JObject as legacy string parsing");
        long legacyParseBytes = AllocatedByParsing(() => JObject.Parse(Encoding.UTF8.GetString(fullPayloadBytes)));
        long streamedParseBytes = AllocatedByParsing(() => MapSnapshotDeltaCodec.ParseCachePayload(fullPayloadBytes));
        Console.WriteLine("INFO: 4 large-map JSON parses allocated " + legacyParseBytes +
            " bytes with UTF-16 intermediate vs " + streamedParseBytes + " bytes via UTF-8 streaming");
        check(streamedParseBytes < legacyParseBytes,
            "streaming the existing map buffer lowers managed JSON parsing allocations");
        var trickyMap = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"note\":\"雪\\n\\t\\\\\\\"🌍\",\"ratio\":1.25}"));
        var special = new MapSnapshot
        {
            MapId = MapId,
            Revision = 43,
            EntityOwners = new Dictionary<long, string> { { 100L, "玩家 🌍\\towner" } },
            NativeJson = new JObject
            {
                ["Map"] = trickyMap,
                ["Terrain"] = JValue.CreateNull(),
                ["Heroes"] = new JArray(tiny),
                ["Animals"] = new JArray(),
                ["DepartedHeroes"] = new JArray(),
                ["DepartedAnimals"] = new JArray(),
                ["Loot"] = new JArray(new JObject { ["memo"] = "a\\r\\n特殊字符☃", ["value"] = 0.00012 }),
                ["Landings"] = new JArray(new JObject
                {
                    ["Expedition"] = tiny,
                    ["Heroes"] = new JArray(tiny),
                    ["Animals"] = new JArray()
                })
            }.ToString(Formatting.None)
        };
        var specialEncoded = MapSnapshotDeltaCodec.Encode(WorldId, special);
        check(MatchesLegacySerializedBytes(specialEncoded, special),
            "unicode, escapes, floating point and nested landing bytes retain legacy cache identity");
        check(JToken.DeepEquals(JObject.Parse(Encoding.UTF8.GetString(ExpandEncodedBytes(specialEncoded))),
            MapSnapshotDeltaCodec.ParseCachePayload(ExpandEncodedBytes(specialEncoded))),
            "unicode and nested landing map JSON parses identically from both inputs");
        check(SameJsonAcceptance("{\"a\":1}  \n // trailing comment"),
            "streaming cache JSON keeps legacy trailing-comment acceptance");
        check(SameJsonAcceptance("{\"a\":1}{\"b\":2}")
            && SameJsonAcceptance("[1,2]")
            && SameJsonAcceptance("{\"a\":1")
            && SameJsonAcceptance(""),
            "streaming cache JSON keeps legacy rejection of extra, non-object or truncated payloads");
        check(SameParseOutcome(new byte[] { (byte)'{', (byte)'\"', (byte)'a',
                    (byte)'\"', (byte)':', (byte)'\"', 0xC3, 0x28, (byte)'\"', (byte)'}' }),
            "streaming cache parser preserves strict UTF-8 error rejection");
        check(SameParseOutcome(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}' }),
            "streaming cache parser preserves legacy UTF-8 BOM handling");
        check(SameJsonAcceptance("{\"created\":\"2026-10-17T00:00:00Z\",\"extra\":null}")
            && SameJsonAcceptance("{\"a\":{\"b\":[1,2,3]}}   "),
            "streaming cache parser preserves legacy date-token and trailing-whitespace behavior");
        SameSnapshot(check, MapSnapshotDeltaCodec.Reassemble(MapSnapshotDeltaCodec.MakeTransfer(specialEncoded, null),
            WorldId, MapId, null), special, "unicode and nested landing serialization");
        Console.WriteLine("INFO: synthetic map avoids " + (previousChunkCopyBytes - uniqueChunkCopyBytes)
            + " bytes of duplicate transient chunk arrays");

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
        int cacheReads = 0;
        MapCacheStore.HashCalls = 0;
        restored = MapSnapshotDeltaCodec.Reassemble(hot, WorldId, MapId,
            hash => { cacheReads++; return cache.TryGetValue(hash, out var bytes) ? bytes : null; });
        check(cacheReads == encoded.Content.Count, "hot map reads each unique cached chunk once");
        check(MapCacheStore.HashCalls == encoded.Content.Count + 1,
            "hot map verifies each unique cached chunk once plus final snapshot hash");
        SameSnapshot(check, restored, snapshot, "hot cache");

        var cachedHash = encoded.Manifest.Chunks[0].Hash;
        var badCachedChunk = (byte[])cache[cachedHash].Clone();
        badCachedChunk[0] ^= 1;
        bool corruptCacheRejected = false;
        try { MapSnapshotDeltaCodec.Reassemble(hot, WorldId, MapId,
            hash => hash == cachedHash ? badCachedChunk : cache[hash]); }
        catch (InvalidDataException) { corruptCacheRejected = true; }
        check(corruptCacheRejected, "repeated cached chunks still reject corrupt content");

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
            && expected.EntityOwners.All(entry =>
                actual.EntityOwners.TryGetValue(entry.Key, out var playerId) && playerId == entry.Value),
            name + " owner registry");
    }

    private static byte[] ExpandEncodedBytes(MapSnapshotDeltaCodec.Encoded encoded)
    {
        var bytes = new byte[(int)encoded.Manifest.TotalLength];
        int offset = 0;
        foreach (MapCacheChunkReference chunk in encoded.Manifest.Chunks)
        {
            byte[] part = encoded.Content[chunk.Hash];
            Buffer.BlockCopy(part, 0, bytes, offset, part.Length);
            offset += part.Length;
        }
        if (offset != bytes.Length) throw new InvalidOperationException("Invalid synthetic cache fixture");
        return bytes;
    }

    private static long AllocatedByParsing(Func<JObject> parse)
    {
        parse(); parse(); // warm both parsers before sampling
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 4; ++i) parse();
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }

    private static bool SameJsonAcceptance(string json)
    {
        return SameParseOutcome(Encoding.UTF8.GetBytes(json));
    }

    private static bool SameParseOutcome(byte[] encoded)
    {
        JObject oldValue = null, newValue = null;
        bool oldRejected = false, newRejected = false;
        try { oldValue = JObject.Parse(new UTF8Encoding(false, true).GetString(encoded)); }
        catch (JsonException) { oldRejected = true; }
        catch (DecoderFallbackException) { oldRejected = true; }
        try { newValue = MapSnapshotDeltaCodec.ParseCachePayload(encoded); }
        catch (JsonException) { newRejected = true; }
        catch (DecoderFallbackException) { newRejected = true; }
        return oldRejected == newRejected
            && (oldRejected || JToken.DeepEquals(oldValue, newValue));
    }

    // This is intentionally the PRE-optimization construction: an entire
    // compact UTF-16 JSON string converted into one exact-length UTF-8 array.
    // Do not reuse the production stream encoder when testing byte identity.
    private static bool MatchesLegacySerializedBytes(MapSnapshotDeltaCodec.Encoded encoded, MapSnapshot snapshot)
    {
        var checkpoint = JObject.Parse(snapshot.NativeJson);
        ExpandLegacyString(checkpoint, "Map");
        foreach (string name in new[] { "Heroes", "Animals", "DepartedHeroes", "DepartedAnimals" })
            ExpandLegacyList(checkpoint[name]);
        foreach (JObject landing in (JArray)checkpoint["Landings"])
        {
            ExpandLegacyString(landing, "Expedition");
            ExpandLegacyList(landing["Heroes"]); ExpandLegacyList(landing["Animals"]);
        }
        var oldPayload = new JObject
        {
            ["Schema"] = MapSnapshotDeltaCodec.Schema,
            ["WorldId"] = WorldId,
            ["MapId"] = snapshot.MapId,
            ["Revision"] = snapshot.Revision,
            ["EntityOwners"] = JObject.FromObject(snapshot.EntityOwners, JsonSerializer.Create(JsonData.Settings)),
            ["Checkpoint"] = checkpoint
        };
        byte[] legacyBytes = Encoding.UTF8.GetBytes(oldPayload.ToString(Formatting.None));
        if (encoded.Manifest.TotalLength != legacyBytes.Length
            || encoded.Manifest.SnapshotHash != MapCacheStore.HashContent(legacyBytes))
            return false;
        int offset = 0;
        foreach (MapCacheChunkReference chunk in encoded.Manifest.Chunks)
        {
            byte[] bytes;
            if (!encoded.Content.TryGetValue(chunk.Hash, out bytes) || bytes.Length != chunk.Length
                || offset + chunk.Length > legacyBytes.Length) return false;
            for (int i = 0; i < bytes.Length; i++)
                if (bytes[i] != legacyBytes[offset + i]) return false;
            offset += bytes.Length;
        }
        return offset == legacyBytes.Length;
    }

    private static void ExpandLegacyString(JObject parent, string name)
    {
        parent[name] = Encoding.UTF8.GetString(Convert.FromBase64String((string)parent[name]));
    }
    private static void ExpandLegacyList(JToken list)
    {
        var items = (JArray)list;
        for (int i = 0; i < items.Count; i++)
            items[i] = Encoding.UTF8.GetString(Convert.FromBase64String((string)items[i]));
    }

    // The previous production algorithm, deliberately independent of the new
    // lookup table so a wrong constant or an off-by-one cut fails this test.
    private static ulong LegacyByteHash(byte input)
    {
        unchecked
        {
            ulong value = (ulong)input + 0x9E3779B97F4A7C15UL;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }

    private static bool LegacyChunkBoundariesMatch(MapSnapshotDeltaCodec.Encoded encoded)
    {
        var bytes = new byte[(int)encoded.Manifest.TotalLength];
        int offset = 0;
        foreach (MapCacheChunkReference chunk in encoded.Manifest.Chunks)
        {
            byte[] content = encoded.Content[chunk.Hash];
            if (chunk.Length != content.Length) return false;
            Buffer.BlockCopy(content, 0, bytes, offset, chunk.Length);
            offset += chunk.Length;
        }
        if (offset != bytes.Length) return false;

        int start = 0, index = 0;
        ulong rolling = 0;
        for (int i = 0; i < bytes.Length; ++i)
        {
            unchecked { rolling = (rolling << 1) + LegacyByteHash(bytes[i]); }
            int length = i - start + 1;
            if (length < 4096 || ((rolling & 16383) != 0 && length < 65536 && i + 1 != bytes.Length)) continue;
            if (index == encoded.Manifest.Chunks.Count || encoded.Manifest.Chunks[index++].Length != length) return false;
            start = i + 1; rolling = 0;
        }
        if (start < bytes.Length && (index == encoded.Manifest.Chunks.Count
            || encoded.Manifest.Chunks[index++].Length != bytes.Length - start)) return false;
        return index == encoded.Manifest.Chunks.Count;
    }
}
