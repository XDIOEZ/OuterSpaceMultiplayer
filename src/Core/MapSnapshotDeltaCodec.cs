using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FreeWorldMultiplayer
{
    public sealed class MapCacheChunkReference { public string Hash; public int Length; }
    public sealed class MapCacheManifest
    {
        public int Schema = 1;
        public string WorldId;
        public long MapId;
        public string SnapshotHash;
        public long TotalLength;
        public List<MapCacheChunkReference> Chunks = new List<MapCacheChunkReference>();
    }
    public sealed class MapCacheChunk { public string Hash; public byte[] Data; }
    public sealed class MapCacheTransfer
    {
        public MapCacheManifest Manifest;
        public List<MapCacheChunk> ChangedChunks = new List<MapCacheChunk>();
    }
    internal sealed class MapCacheQuery
    {
        public string ViewId;
        public string WorldId;
        public long MapId, AuthorityEpoch, Revision;
        public bool FullTerrain;
        public bool IgnoreCache;
    }
    internal sealed class MapCacheOffer
    {
        public string ViewId;
        public string WorldId;
        public long MapId;
        public List<string> AvailableChunks = new List<string>();
        public bool FullTerrain;
    }
    internal sealed class MapCacheRetry { public string ViewId; public long MapId; public bool FullTerrain; }
    internal static class MapSnapshotDeltaCodec
    {
        internal const int Schema = 1;
        internal const int MaxChunks = 32768;
        internal const int MaxSnapshotBytes = 128 * 1024 * 1024;
        private const int MinChunk = 4096, MaxChunk = 65536;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly HashSet<string> CheckpointFields = new HashSet<string>(StringComparer.Ordinal)
            { "Map", "Terrain", "Heroes", "Animals", "DepartedHeroes", "DepartedAnimals", "Loot", "Landings" };
        internal sealed class Encoded
        {
            internal MapCacheManifest Manifest;
            internal Dictionary<string, byte[]> Content = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        }
        private struct WrittenChunk
        {
            internal int Offset;
            internal int Length;
        }
        internal static Encoded Encode(string worldId, MapSnapshot snapshot)
        {
            if (string.IsNullOrWhiteSpace(worldId) || snapshot == null || snapshot.IsDeltaBaseline || snapshot.MapId < 0
                || snapshot.EntityOwners == null || string.IsNullOrEmpty(snapshot.NativeJson)) throw new InvalidDataException("A cache requires the host's complete wire checkpoint.");
            JObject checkpoint = JObject.Parse(snapshot.NativeJson);
            TransformCheckpoint(checkpoint, true);
            var payload = new JObject { ["Schema"] = Schema, ["WorldId"] = worldId, ["MapId"] = snapshot.MapId,
                ["Revision"] = snapshot.Revision, ["EntityOwners"] = JObject.FromObject(snapshot.EntityOwners, JsonSerializer.Create(JsonData.Settings)), ["Checkpoint"] = checkpoint };
            byte[] bytes = Utf8.GetBytes(payload.ToString(Formatting.None));
            if (bytes.Length == 0 || bytes.Length > MaxSnapshotBytes) throw new InvalidDataException("Expanded map cache payload exceeds 128 MiB.");
            var result = new Encoded { Manifest = new MapCacheManifest { WorldId = worldId, MapId = snapshot.MapId,
                TotalLength = bytes.Length, SnapshotHash = MapCacheStore.HashContent(bytes) } };
            int start = 0; ulong rolling = 0;
            for (int i = 0; i < bytes.Length; ++i)
            {
                unchecked { ulong value = (ulong)bytes[i] + 0x9E3779B97F4A7C15UL; value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL; value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL; rolling = (rolling << 1) + (value ^ (value >> 31)); }
                int length = i - start + 1;
                if (length < MinChunk || (rolling & 16383) != 0 && length < MaxChunk && i + 1 != bytes.Length) continue;
                AddChunk(result, bytes, start, length); start = i + 1; rolling = 0;
            }
            if (start < bytes.Length) AddChunk(result, bytes, start, bytes.Length - start);
            ValidateManifest(result.Manifest, worldId, snapshot.MapId);
            return result;
        }
        private static void AddChunk(Encoded encoded, byte[] all, int offset, int count)
        {
            // Repeated map content is common. Hash the original slice first so only
            // distinct chunks allocate a retained byte array; do not create and
            // immediately discard a duplicate chunk on every repeated boundary.
            string hash = MapCacheStore.HashContent(all, offset, count);
            encoded.Manifest.Chunks.Add(new MapCacheChunkReference { Hash = hash, Length = count });
            if (!encoded.Content.ContainsKey(hash))
            {
                var data = new byte[count];
                Buffer.BlockCopy(all, offset, data, 0, count);
                encoded.Content.Add(hash, data);
            }
        }
        internal static MapCacheTransfer MakeTransfer(Encoded encoded, IEnumerable<string> available)
        {
            var known = new HashSet<string>(available ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            if (known.Count > MaxChunks || known.Any(x => !IsHash(x))) throw new InvalidDataException("Invalid map cache chunk offer.");
            return new MapCacheTransfer { Manifest = encoded.Manifest, ChangedChunks = encoded.Content.Where(x => !known.Contains(x.Key))
                .Select(x => new MapCacheChunk { Hash = x.Key, Data = x.Value }).ToList() };
        }
        internal static MapSnapshot Reassemble(MapCacheTransfer transfer, string worldId, long mapId, Func<string, byte[]> read)
        {
            if (transfer == null || transfer.ChangedChunks == null) throw new InvalidDataException("Missing map cache transfer.");
            ValidateManifest(transfer.Manifest, worldId, mapId);
            var expected = new HashSet<string>(transfer.Manifest.Chunks.Select(x => x.Hash), StringComparer.Ordinal);
            if (transfer.ChangedChunks.Count > expected.Count) throw new InvalidDataException("Excess map cache chunks.");
            var changed = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            long incoming = 0;
            foreach (MapCacheChunk chunk in transfer.ChangedChunks)
            {
                if (chunk == null || !expected.Contains(chunk.Hash) || chunk.Data == null || chunk.Data.Length > MaxChunk
                    || changed.ContainsKey(chunk.Hash) || MapCacheStore.HashContent(chunk.Data) != chunk.Hash)
                    throw new InvalidDataException("Invalid or corrupt changed map chunk.");
                incoming += chunk.Data.Length;
                if (incoming > MaxSnapshotBytes) throw new InvalidDataException("Map chunks exceed the full payload limit.");
                changed.Add(chunk.Hash, chunk.Data);
            }
            // ValidateManifest already bounded the complete length and proved all chunk
            // lengths add up to it. Fill that exact buffer once: MemoryStream.ToArray()
            // previously copied another full 20+ MiB array on every map cache restore.
            byte[] bytes = new byte[(int)transfer.Manifest.TotalLength];
            // Reuse bytes already written into the final buffer for repeated hashes;
            // retaining every disk-loaded byte[] would double the live map memory.
            var written = new Dictionary<string, WrittenChunk>(StringComparer.Ordinal);
            int writeOffset = 0;
            foreach (MapCacheChunkReference reference in transfer.Manifest.Chunks)
            {
                WrittenChunk previous;
                if (written.TryGetValue(reference.Hash, out previous))
                {
                    if (previous.Length != reference.Length)
                        throw new InvalidDataException("Required cached map chunk is unavailable: " + reference.Hash);
                    Buffer.BlockCopy(bytes, previous.Offset, bytes, writeOffset, reference.Length);
                }
                else
                {
                    byte[] data;
                    bool fromTransfer = changed.TryGetValue(reference.Hash, out data);
                    if (!fromTransfer) data = read == null ? null : read(reference.Hash);
                    // Received chunks were verified above; verify each on-disk chunk
                    // once before allowing later references to reuse these bytes.
                    if (data == null || data.Length != reference.Length ||
                        (!fromTransfer && MapCacheStore.HashContent(data) != reference.Hash))
                        throw new InvalidDataException("Required cached map chunk is unavailable: " + reference.Hash);
                    Buffer.BlockCopy(data, 0, bytes, writeOffset, data.Length);
                    written.Add(reference.Hash, new WrittenChunk { Offset = writeOffset, Length = data.Length });
                }
                writeOffset += reference.Length;
            }
            if (writeOffset != bytes.Length) throw new InvalidDataException("Map cache content length differs from the manifest.");
            if (MapCacheStore.HashContent(bytes) != transfer.Manifest.SnapshotHash) throw new InvalidDataException("Reassembled map checksum differs from the host.");
            JObject payload = JObject.Parse(Utf8.GetString(bytes));
            if ((int?)payload["Schema"] != Schema || (string)payload["WorldId"] != worldId || (long?)payload["MapId"] != mapId)
                throw new InvalidDataException("Reassembled map identity/schema differs from the host.");
            var checkpoint = payload["Checkpoint"] as JObject;
            if (checkpoint == null) throw new InvalidDataException("Reassembled checkpoint is missing.");
            TransformCheckpoint(checkpoint, false);
            return new MapSnapshot { MapId = mapId, Revision = (long)payload["Revision"], NativeJson = checkpoint.ToString(Formatting.None),
                EntityOwners = payload["EntityOwners"].ToObject<Dictionary<long, string>>(JsonSerializer.Create(JsonData.Settings)) };
        }
        internal static bool IsHash(string value)
        { return value != null && value.Length == 64 && value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f'); }
        internal static void ValidateManifest(MapCacheManifest manifest, string worldId, long mapId)
        {
            if (manifest == null || manifest.Schema != Schema || manifest.WorldId != worldId || manifest.MapId != mapId
                || !IsHash(manifest.SnapshotHash) || manifest.TotalLength <= 0 || manifest.TotalLength > MaxSnapshotBytes
                || manifest.Chunks == null || manifest.Chunks.Count == 0 || manifest.Chunks.Count > MaxChunks)
                throw new InvalidDataException("Invalid map cache manifest.");
            long total = 0;
            foreach (MapCacheChunkReference chunk in manifest.Chunks)
            {
                if (chunk == null || !IsHash(chunk.Hash) || chunk.Length <= 0 || chunk.Length > MaxChunk) throw new InvalidDataException("Invalid map cache chunk reference.");
                total = checked(total + chunk.Length);
            }
            if (total != manifest.TotalLength) throw new InvalidDataException("Map cache chunk lengths differ from the manifest.");
        }
        private static void TransformCheckpoint(JObject checkpoint, bool expand)
        {
            if (checkpoint.Properties().Any(x => !CheckpointFields.Contains(x.Name))) throw new InvalidDataException("Unknown map checkpoint cache field.");
            TransformPacked(checkpoint, "Map", expand);
            foreach (string field in new[] { "Heroes", "Animals", "DepartedHeroes", "DepartedAnimals" }) TransformPackedList(checkpoint[field], expand);
            JArray landings = checkpoint["Landings"] as JArray;
            if (landings == null) throw new InvalidDataException("Invalid map landing cache list.");
            foreach (JObject landing in landings)
            {
                TransformPacked(landing, "Expedition", expand);
                TransformPackedList(landing["Heroes"], expand); TransformPackedList(landing["Animals"], expand);
            }
        }
        private static void TransformPacked(JObject parent, string name, bool expand)
        {
            if (parent[name] == null || parent[name].Type != JTokenType.String) throw new InvalidDataException("Missing packed checkpoint field: " + name);
            string value = (string)parent[name];
            parent[name] = expand ? Utf8.GetString(Convert.FromBase64String(value)) : Convert.ToBase64String(Utf8.GetBytes(value));
        }
        private static void TransformPackedList(JToken token, bool expand)
        {
            JArray list = token as JArray;
            if (list == null) throw new InvalidDataException("Invalid packed checkpoint entity list.");
            for (int i = 0; i < list.Count; ++i)
            {
                if (list[i].Type != JTokenType.String) throw new InvalidDataException("Invalid packed checkpoint entity.");
                string value = (string)list[i];
                list[i] = expand ? Utf8.GetString(Convert.FromBase64String(value)) : Convert.ToBase64String(Utf8.GetBytes(value));
            }
        }
    }
}
