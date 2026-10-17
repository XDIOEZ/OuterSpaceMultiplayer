using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace FreeWorldMultiplayer
{
    // Disposable derived data only. Neither a cache hit nor a manifest grants map authority.
    public sealed class MapCacheStore
    {
        private const long MaxCacheBytes = 512L * 1024 * 1024;
        private const int MaxContentBytes = 128 * 1024 * 1024, MaxContents = 8192, MaxMaps = 64;
        private const int MaxManifestBytes = 8 * 1024 * 1024;
        private sealed class ManifestEnvelope
        {
            public int Schema;
            public string Hash, Json;
        }
        private sealed class Entry
        {
            internal string Path;
            internal long Length;
            internal bool Manifest;
            internal LinkedListNode<Entry> Node;
        }
        private readonly object gate = new object();
        private readonly string root, contents, maps;
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<Entry> lru = new LinkedList<Entry>();
        private long bytes;
        private int contentCount, mapCount;

        public MapCacheStore(string storageRoot, string runtimeScope)
        {
            if (string.IsNullOrWhiteSpace(storageRoot) || string.IsNullOrWhiteSpace(runtimeScope))
                throw new ArgumentException("Map cache requires an explicit storage root and platform/runtime scope.");
            root = Path.GetFullPath(Path.Combine(storageRoot, "map-cache-v1", HashContent(Encoding.UTF8.GetBytes(runtimeScope))));
            contents = Path.Combine(root, "content"); maps = Path.Combine(root, "maps");
            Directory.CreateDirectory(contents); Directory.CreateDirectory(maps);
            RemoveTemporaryFiles(contents, false); RemoveTemporaryFiles(maps, true);
            var files = new List<FileInfo>();
            foreach (string path in Directory.GetFiles(contents, "*.bin"))
                if (IsHash(Path.GetFileNameWithoutExtension(path))) files.Add(new FileInfo(path));
            foreach (string path in Directory.GetFiles(maps, "*.json"))
                if (IsManifestName(Path.GetFileNameWithoutExtension(path))) files.Add(new FileInfo(path));
            files.Sort((left, right) => left.LastWriteTimeUtc.CompareTo(right.LastWriteTimeUtc));
            foreach (FileInfo file in files) if (File.Exists(file.FullName)) Index(file.FullName, file.Length, file.Extension == ".json");
            Trim();
        }

        public static string HashContent(byte[] content)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            return HashContent(content, 0, content.Length);
        }
        // Hash an existing wire buffer slice without copying each chunk first.
        // The complete-array API above remains unchanged for persisted content.
        internal static string HashContent(byte[] content, int offset, int count)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            using (var sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(content, offset, count);
                // BitConverter.ToString + Replace + ToLowerInvariant allocates
                // three temporary strings for every cached map chunk. Emit the
                // same 64 lowercase hex characters directly using one buffer.
                const string hex = "0123456789abcdef";
                var chars = new char[digest.Length * 2];
                for (int i = 0; i < digest.Length; i++)
                {
                    byte b = digest[i];
                    chars[2 * i] = hex[b >> 4];
                    chars[2 * i + 1] = hex[b & 15];
                }
                return new string(chars);
            }
        }
        private static bool IsHash(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char c in value) if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')) return false;
            return true;
        }
        private static void RequireHash(string value)
        { if (!IsHash(value)) throw new InvalidDataException("Map cache content hash must be a lowercase SHA256 digest."); }
        private static bool IsManifestName(string value)
        {
            long mapId;
            return value != null && value.Length > 65 && value[64] == '-' && IsHash(value.Substring(0, 64))
                && long.TryParse(value.Substring(65), NumberStyles.None, CultureInfo.InvariantCulture, out mapId) && mapId >= 0;
        }
        private string ManifestPath(string worldId, long mapId)
        {
            if (string.IsNullOrWhiteSpace(worldId) || mapId < 0) throw new ArgumentException("Map cache requires a world and canonical map identity.");
            return Path.Combine(maps, HashContent(Encoding.UTF8.GetBytes(worldId)) + "-" + mapId.ToString(CultureInfo.InvariantCulture) + ".json");
        }

        // A cheap offer hint only. A hit is not integrity validation or map authority.
        public bool ContainsContent(string contentHash)
        {
            RequireHash(contentHash);
            lock (gate)
            {
                string path = Path.Combine(contents, contentHash + ".bin");
                var file = new FileInfo(path);
                if (!File.Exists(path)) { Forget(path); return false; }
                if (file.Length < 0 || file.Length > MaxContentBytes) return false;
                Index(path, file.Length, false); Trim(); return true;
            }
        }
        public byte[] TryReadContent(string contentHash)
        {
            RequireHash(contentHash);
            lock (gate)
            {
                string path = Path.Combine(contents, contentHash + ".bin");
                byte[] result = ReadFile(path, MaxContentBytes);
                if (result == null) { Forget(path); return null; }
                if (HashContent(result) != contentHash) throw new InvalidDataException("Map cache content checksum differs: " + contentHash);
                Index(path, result.LongLength, false); Trim(); return result;
            }
        }
        public void WriteContent(string contentHash, byte[] content)
        {
            RequireHash(contentHash);
            if (content == null || content.Length > MaxContentBytes || HashContent(content) != contentHash)
                throw new InvalidDataException("Map cache write does not match its declared bounded content hash.");
            lock (gate)
            {
                string path = Path.Combine(contents, contentHash + ".bin");
                AtomicWrite(path, content); Index(path, content.LongLength, false); Trim();
            }
        }

        public MapCacheManifest TryReadManifest(string worldId, long mapId)
        {
            lock (gate)
            {
                string path = ManifestPath(worldId, mapId);
                byte[] encoded = ReadFile(path, MaxManifestBytes);
                if (encoded == null) { Forget(path); return null; }
                ManifestEnvelope envelope;
                MapCacheManifest manifest;
                try
                {
                    envelope = JsonData.Read<ManifestEnvelope>(Encoding.UTF8.GetString(encoded));
                    if (envelope == null || envelope.Schema != 1 || !IsHash(envelope.Hash) || envelope.Json == null
                        || HashContent(Encoding.UTF8.GetBytes(envelope.Json)) != envelope.Hash)
                        throw new InvalidDataException("Map cache manifest checksum differs.");
                    manifest = JsonData.Read<MapCacheManifest>(envelope.Json);
                }
                catch (Newtonsoft.Json.JsonException error) { throw new InvalidDataException("Map cache manifest JSON is invalid.", error); }
                ValidateManifest(worldId, mapId, manifest);
                Index(path, encoded.LongLength, true); Trim();
                // Individual chunks can have been evicted. The caller offers only verified
                // TryReadContent hits; it must still verify the assembled SnapshotHash.
                return manifest;
            }
        }
        public void WriteManifest(string worldId, long mapId, MapCacheManifest manifest)
        {
            lock (gate)
            {
                string json = JsonData.Write(manifest);
                ValidateManifest(worldId, mapId, JsonData.Read<MapCacheManifest>(json));
                var envelope = new ManifestEnvelope { Schema = 1, Json = json, Hash = HashContent(Encoding.UTF8.GetBytes(json)) };
                byte[] encoded = Encoding.UTF8.GetBytes(JsonData.Write(envelope));
                if (encoded.Length > MaxManifestBytes) throw new InvalidDataException("Map cache manifest exceeds its size limit.");
                string path = ManifestPath(worldId, mapId);
                AtomicWrite(path, encoded); Index(path, encoded.LongLength, true); Trim();
            }
        }
        public void InvalidateMap(string worldId, long mapId)
        {
            lock (gate)
            {
                string path = ManifestPath(worldId, mapId);
                DeleteKnownPath(path); Forget(path);
            }
        }
        private static void ValidateManifest(string worldId, long mapId, MapCacheManifest manifest)
        {
            if (manifest == null || manifest.Schema != MapSnapshotDeltaCodec.Schema || manifest.WorldId != worldId || manifest.MapId != mapId
                || !IsHash(manifest.SnapshotHash) || manifest.TotalLength <= 0 || manifest.TotalLength > MapSnapshotDeltaCodec.MaxSnapshotBytes
                || manifest.Chunks == null || manifest.Chunks.Count == 0 || manifest.Chunks.Count > MapSnapshotDeltaCodec.MaxChunks)
                throw new InvalidDataException("Map cache manifest identity, schema or size is invalid.");
            long total = 0; var lengths = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (MapCacheChunkReference chunk in manifest.Chunks)
            {
                if (chunk == null || !IsHash(chunk.Hash) || chunk.Length <= 0 || chunk.Length > MaxContentBytes)
                    throw new InvalidDataException("Map cache chunk identity or length is invalid.");
                int previous;
                if (lengths.TryGetValue(chunk.Hash, out previous) && previous != chunk.Length)
                    throw new InvalidDataException("The same map cache content hash declares different lengths.");
                lengths[chunk.Hash] = chunk.Length; total += chunk.Length;
                if (total > MapSnapshotDeltaCodec.MaxSnapshotBytes) throw new InvalidDataException("Map cache manifest content exceeds its size limit.");
            }
            if (total != manifest.TotalLength) throw new InvalidDataException("Map cache manifest total length differs from its chunks.");
        }

        private static byte[] ReadFile(string path, int limit)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length < 0 || stream.Length > limit) throw new InvalidDataException("Map cache file exceeds its size limit.");
                    byte[] data = new byte[(int)stream.Length]; int offset = 0;
                    while (offset < data.Length)
                    {
                        int count = stream.Read(data, offset, data.Length - offset);
                        if (count == 0) throw new InvalidDataException("Map cache file ended before its declared length.");
                        offset += count;
                    }
                    if (stream.ReadByte() != -1) throw new InvalidDataException("Map cache file changed while being read.");
                    return data;
                }
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }
        private void AtomicWrite(string path, byte[] data)
        {
            string temp = path + ".tmp." + Guid.NewGuid().ToString("N");
            Exception failure = null;
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(data, 0, data.Length); stream.Flush(); }
                // Cache loss is recoverable. Avoid a durable-world fsync for every network chunk.
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                try { if (File.Exists(temp)) DeleteKnownPath(temp); }
                catch (Exception cleanup)
                { if (failure != null) throw new AggregateException("Map cache write and temporary-file cleanup failed.", failure, cleanup); throw; }
            }
        }
        private void RemoveTemporaryFiles(string directory, bool manifest)
        {
            foreach (string path in Directory.GetFiles(directory, "*.tmp.*"))
            {
                string name = Path.GetFileName(path); int marker = name.LastIndexOf(".tmp.", StringComparison.Ordinal);
                if (marker < 0 || name.Length - marker - 5 != 32) continue;
                string token = name.Substring(marker + 5);
                bool valid = true; foreach (char c in token) if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')) { valid = false; break; }
                string original = name.Substring(0, marker);
                if (valid && (manifest ? original.EndsWith(".json", StringComparison.Ordinal) && IsManifestName(Path.GetFileNameWithoutExtension(original))
                    : original.EndsWith(".bin", StringComparison.Ordinal) && IsHash(Path.GetFileNameWithoutExtension(original)))) DeleteKnownPath(path);
            }
        }
        private void DeleteKnownPath(string path)
        {
            string full = Path.GetFullPath(path);
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Map cache cleanup target escaped its own directory.");
            File.Delete(full);
        }
        private void Index(string path, long length, bool manifest)
        {
            Entry entry;
            if (entries.TryGetValue(path, out entry))
            { bytes -= entry.Length; lru.Remove(entry.Node); }
            else
            {
                entry = new Entry { Path = path, Manifest = manifest }; entries.Add(path, entry);
                if (manifest) mapCount++; else contentCount++;
            }
            entry.Length = length; entry.Node = lru.AddLast(entry); bytes += length;
        }
        private void Forget(string path)
        {
            Entry entry;
            if (!entries.TryGetValue(path, out entry)) return;
            entries.Remove(path); lru.Remove(entry.Node); bytes -= entry.Length;
            if (entry.Manifest) mapCount--; else contentCount--;
        }
        private void Trim()
        {
            while (bytes > MaxCacheBytes || contentCount > MaxContents || mapCount > MaxMaps)
            {
                bool removeMap = mapCount > MaxMaps, removeContent = mapCount <= MaxMaps && contentCount > MaxContents;
                LinkedListNode<Entry> node = lru.First;
                while (node != null && (removeMap && !node.Value.Manifest || removeContent && node.Value.Manifest)) node = node.Next;
                if (node == null) throw new InvalidOperationException("Map cache LRU accounting has no eviction candidate.");
                DeleteKnownPath(node.Value.Path); Forget(node.Value.Path);
            }
        }
    }
}
