using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Security.Cryptography;
using FreeWorldMultiplayer;
using Newtonsoft.Json.Linq;

internal static class Program
{
    private static int checks;
    private static void Check(bool passed, string name)
    {
        if (!passed) throw new InvalidOperationException("FAILED: " + name);
        ++checks;
    }

    private static bool Rejects<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    // Independent reference formatter matching the previous production code.
    // Keep it in the tests so the optimized hex writer cannot validate itself.
    private static string LegacyHashContent(byte[] content, int offset, int count)
    {
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(content, offset, count)).Replace("-", "").ToLowerInvariant();
    }

    private static long MeasureHashAllocations(Func<string> action)
    {
        for (int i = 0; i < 20; i++) action(); // JIT and crypto initialization outside measurement.
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static MapSnapshot Example(long mapId)
    {
        string packedMap = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            new string('x', 210000) + "{\"world\":\"not-an-authority\"}"));
        string packedHero = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"kind\":\"hero\"}"));
        var native = new JObject
        {
            ["Map"] = packedMap,
            ["Terrain"] = JValue.CreateNull(),
            ["Heroes"] = new JArray(packedHero),
            ["Animals"] = new JArray(),
            ["DepartedHeroes"] = new JArray(),
            ["DepartedAnimals"] = new JArray(),
            ["Loot"] = new JArray(),
            ["Landings"] = new JArray()
        };
        return new MapSnapshot
        {
            MapId = mapId,
            Revision = 17,
            NativeJson = native.ToString(Newtonsoft.Json.Formatting.None),
            EntityOwners = new Dictionary<long, string> { { 1001L, "owner-a" } }
        };
    }

    public static int Main()
    {
        try
        {
            // Unique, ignored, test-created fixture. Never touches an actual
            // player save, installed game or runtime MOD cache directory.
            string project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", ".."));
            string root = Path.Combine(project, "artifacts", "map-cache-store-checks",
                "case-" + Guid.NewGuid().ToString("N"));
            const string world = "synthetic-world-a";
            const long mapId = 1000001000004L;
            const string scope = "synthetic-steam-target";
            Check(MapCacheStore.HashContent(new byte[0]) ==
                "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                "empty SHA256 retains the standard lowercase hexadecimal digest");
            Check(MapCacheStore.HashContent(Encoding.ASCII.GetBytes("abc")) ==
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                "nonempty SHA256 retains the standard lowercase hexadecimal digest");
            var rangeFixture = new byte[8192];
            new Random(12345).NextBytes(rangeFixture);
            bool legacyEquivalent = true;
            foreach (int count in new[] { 0, 1, 31, 32, 33, 64, 1024, 4088 })
                legacyEquivalent &= MapCacheStore.HashContent(rangeFixture, 7, count)
                    == LegacyHashContent(rangeFixture, 7, count);
            Check(legacyEquivalent, "bounded slices match the previous SHA256 text formatter exactly");
            var everyByte = new byte[256];
            for (int i = 0; i < everyByte.Length; i++) everyByte[i] = (byte)i;
            Check(MapCacheStore.HashContent(everyByte) == LegacyHashContent(everyByte, 0, everyByte.Length),
                "all 256 byte values produce identical cache content addresses");
            long legacyAllocated = MeasureHashAllocations(() => LegacyHashContent(rangeFixture, 7, 128));
            long optimizedAllocated = MeasureHashAllocations(() => MapCacheStore.HashContent(rangeFixture, 7, 128));
            Check(optimizedAllocated < legacyAllocated,
                "direct hex formatting allocates fewer bytes than three-string legacy formatting");
            Console.WriteLine("INFO: 1000 synthetic SHA256 hash formats allocated " + legacyAllocated
                + " bytes before and " + optimizedAllocated + " bytes after (managed allocations only)");
            byte[] slice = Encoding.UTF8.GetBytes("repeated-chunk-地图");
            byte[] padded = new byte[slice.Length + 11];
            Buffer.BlockCopy(slice, 0, padded, 5, slice.Length);
            Check(MapCacheStore.HashContent(padded, 5, slice.Length) == MapCacheStore.HashContent(slice),
                "slice hashing matches complete-buffer SHA256 without copying bytes");
            Check(MapCacheStore.HashContent(padded, 0, padded.Length) == MapCacheStore.HashContent(padded),
                "slice hashing supports the complete source range");
            Check(MapCacheStore.HashContent(padded, 0, 0) == MapCacheStore.HashContent(new byte[0]),
                "zero-length slice retains the standard SHA256 digest");
            Check(Rejects<ArgumentException>(() => MapCacheStore.HashContent(padded, 6, padded.Length)),
                "slice hashing rejects a range beyond the input buffer");
            var snapshot = Example(mapId);
            var encoded = MapSnapshotDeltaCodec.Encode(world, snapshot);
            Check(encoded.Manifest.TotalLength > 0 && encoded.Content.Count > 0,
                "production map codec builds valid cold input");
            Check(encoded.Content.All(chunk => MapCacheStore.HashContent(chunk.Value) == chunk.Key),
                "encoded content retains the exact bytes declared by sliced SHA256 hashes");

            var store = new MapCacheStore(root, scope);
            string firstHash = encoded.Content.Keys.First();
            Check(!store.ContainsContent(firstHash) && store.TryReadContent(firstHash) == null,
                "empty scoped cache has no arbitrary content");
            Check(store.TryReadManifest(world, mapId) == null,
                "uninitialized world/map has no content manifest");
            Check(Rejects<InvalidDataException>(() => store.WriteContent(firstHash,
                    Encoding.UTF8.GetBytes("wrong-content"))),
                "content cannot be stored under a forged SHA256 name");
            Check(Rejects<InvalidDataException>(() => store.ContainsContent("../bad")),
                "content hashes cannot escape cache scope");

            foreach (var pair in encoded.Content) store.WriteContent(pair.Key, pair.Value);
            Check(store.ContainsContent(firstHash),
                "content offer hint finds a bounded persisted chunk");
            byte[] original = encoded.Content[firstHash];
            Check(store.TryReadContent(firstHash).SequenceEqual(original),
                "persisted content read verifies SHA256 and returns exact bytes");

            store.WriteManifest(world, mapId, encoded.Manifest);
            MapCacheManifest manifest = store.TryReadManifest(world, mapId);
            Check(manifest != null && manifest.WorldId == world && manifest.MapId == mapId
                    && manifest.TotalLength == encoded.Manifest.TotalLength,
                "persisted manifest retains world/map/length identity");
            var allCached = MapSnapshotDeltaCodec.MakeTransfer(encoded, encoded.Content.Keys);
            Check(allCached.ChangedChunks.Count == 0,
                "hot map makes a transfer without duplicate content payload");
            var hot = MapSnapshotDeltaCodec.ReassembleFromVerifiedCache(
                new MapCacheTransfer { Manifest = manifest, ChangedChunks = allCached.ChangedChunks },
                world, mapId, hash => store.TryReadContent(hash));
            Check(hot.Revision == snapshot.Revision && hot.MapId == mapId &&
                JToken.DeepEquals(JToken.Parse(hot.NativeJson), JToken.Parse(snapshot.NativeJson)) &&
                hot.EntityOwners[1001L] == "owner-a",
                "real disk cache plus real codec restores exact native checkpoint and ownership");

            var reopened = new MapCacheStore(root, scope);
            Check(reopened.ContainsContent(firstHash) &&
                reopened.TryReadManifest(world, mapId) != null,
                "manifest and chunks survive a normal process restart");
            var otherScope = new MapCacheStore(root, "synthetic-taptap-target");
            Check(!otherScope.ContainsContent(firstHash) &&
                otherScope.TryReadManifest(world, mapId) == null,
                "Steam and TapTap cache scopes are isolated");
            Check(reopened.TryReadManifest("another-world", mapId) == null,
                "one multiplayer world cannot read another world's map manifest");
            Check(Rejects<InvalidDataException>(() =>
                MapSnapshotDeltaCodec.Reassemble(allCached, "another-world", mapId,
                    hash => reopened.TryReadContent(hash))),
                "wrong world cannot reassemble a seemingly hot snapshot");

            var invalidManifest = new MapCacheManifest
            {
                WorldId = "different-world", MapId = mapId, Schema = 1,
                TotalLength = manifest.TotalLength, SnapshotHash = manifest.SnapshotHash,
                Chunks = manifest.Chunks
            };
            Check(Rejects<InvalidDataException>(() =>
                reopened.WriteManifest(world, mapId, invalidManifest)),
                "manifest world mismatch rejected before disk persistence");

            string namespaceRoot = Path.Combine(root, "map-cache-v1",
                MapCacheStore.HashContent(Encoding.UTF8.GetBytes(scope)));
            string chunkPath = Path.Combine(namespaceRoot, "content", firstHash + ".bin");
            File.WriteAllBytes(chunkPath, new byte[] { 1, 2, 3 });
            Check(Rejects<InvalidDataException>(() => reopened.TryReadContent(firstHash)),
                "corrupted persisted content never passes read checksum");
            Check(Rejects<InvalidDataException>(() =>
                MapSnapshotDeltaCodec.ReassembleFromVerifiedCache(allCached, world, mapId,
                    hash => reopened.TryReadContent(hash))),
                "verified-cache restore still rejects corrupt on-disk chunks");
            reopened.WriteContent(firstHash, original);
            Check(reopened.TryReadContent(firstHash).SequenceEqual(original),
                "verified full content rewrites a corrupt cache entry safely");

            string strayTemp = chunkPath + ".tmp." + Guid.NewGuid().ToString("N");
            File.WriteAllText(strayTemp, "incomplete-temporary-cache");
            Check(File.Exists(strayTemp), "synthetic interrupted cache write was staged");
            var recovered = new MapCacheStore(root, scope);
            Check(!File.Exists(strayTemp) && recovered.ContainsContent(firstHash),
                "cache startup removes its own incomplete temp without losing valid content");

            recovered.InvalidateMap(world, mapId);
            Check(recovered.TryReadManifest(world, mapId) == null &&
                  recovered.ContainsContent(firstHash),
                "invalidating one map only clears its manifest and preserves verified reusable chunks");

            Console.WriteLine("PASS: " + checks +
                " production MapCacheStore persistence and MapSnapshotDeltaCodec checks");
            Console.WriteLine("FIXTURE: " + root);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
