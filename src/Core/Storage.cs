using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FreeWorldMultiplayer
{
    internal static class JsonData
    {
        internal static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.None,
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            MaxDepth = 128,
            ObjectCreationHandling = ObjectCreationHandling.Replace,
            DateParseHandling = DateParseHandling.None,
            FloatParseHandling = FloatParseHandling.Double
        };
        public static string Write(object value) { return JsonConvert.SerializeObject(value, Formatting.None, Settings); }
        public static T Read<T>(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Empty JSON payload.");
            return JsonConvert.DeserializeObject<T>(value, Settings);
        }
        public static T Clone<T>(T value) { return Read<T>(Write(value)); }
        public static T Payload<T>(JToken value)
        {
            if (value == null) throw new InvalidDataException("Missing message payload.");
            return value.ToObject<T>(JsonSerializer.Create(Settings));
        }
    }
    internal static class CryptoData
    {
        public static string RandomToken()
        {
            byte[] bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes);
        }
        public static string Hash(string value)
        {
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(value)));
        }
        public static string Proof(string verifier, string challenge)
        {
            using (var hmac = new HMACSHA256(Convert.FromBase64String(verifier)))
                return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(challenge)));
        }
        public static bool Equal(string a, string b)
        {
            if (a == null || b == null) return false;
            byte[] left = Encoding.UTF8.GetBytes(a), right = Encoding.UTF8.GetBytes(b);
            int diff = left.Length ^ right.Length;
            for (int i = 0; i < Math.Max(left.Length, right.Length); i++)
                diff |= (i < left.Length ? left[i] : 0) ^ (i < right.Length ? right[i] : 0);
            return diff == 0;
        }
    }
    public static class IdentityStore
    {
        public static LocalIdentity LoadOrCreate(string storageRoot, string nickname)
        {
            Directory.CreateDirectory(storageRoot);
            string path = Path.Combine(storageRoot, "identity.json");
            if (File.Exists(path))
            {
                LocalIdentity existing = JsonData.Read<LocalIdentity>(File.ReadAllText(path, Encoding.UTF8));
                if (existing == null || string.IsNullOrWhiteSpace(existing.CredentialId) || string.IsNullOrWhiteSpace(existing.Secret))
                    throw new InvalidDataException("联机身份文件损坏；不会生成另一身份覆盖原文件。");
                if (!string.IsNullOrWhiteSpace(nickname)) existing.Nickname = nickname;
                return existing;
            }
            var identity = new LocalIdentity { CredentialId = Guid.NewGuid().ToString("N"), Secret = CryptoData.RandomToken(), Nickname = nickname ?? "玩家" };
            AtomicFile.Write(path, Encoding.UTF8.GetBytes(JsonData.Write(identity)));
            return identity;
        }
    }
    internal static class AtomicFile
    {
        public static void Write(string path, byte[] data)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(directory);
            string temp = path + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(data, 0, data.Length);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
    public sealed class WorldSummary
    {
        public string WorldId;
        public string Name;
        public long SaveGeneration;
        public string SavedAtUtc;
        public string Error;
    }
    internal sealed class SavePointer
    {
        public string WorldId;
        public string Name;
        public long Generation;
        public string File;
        public string Sha256;
        public string SavedAtUtc;
    }
    public sealed class SaveStore
    {
        private readonly string root;
        private SavePointer lastLoadedPointer;
        public string LastLoadWarning { get; private set; }
        public string LastCleanupWarning { get; private set; }
        public SaveStore(string storageRoot) { root = Path.GetFullPath(Path.Combine(storageRoot, "worlds")); }
        private string WorldDirectory(string id)
        {
            Guid parsed;
            if (!Guid.TryParseExact(id, "N", out parsed)) throw new InvalidDataException("Invalid multiplayer world identifier.");
            return Path.Combine(root, id);
        }
        public static IList<WorldSummary> ListWorlds(string storageRoot)
        {
            var store = new SaveStore(storageRoot);
            var worlds = new List<WorldSummary>();
            if (!Directory.Exists(store.root)) return worlds;
            foreach (string dir in Directory.GetDirectories(store.root))
            {
                // An interrupted atomic pointer replace can leave only the .bak pointer.
                // The list must not block a world that Load can safely recover.
                if (!File.Exists(Path.Combine(dir, "active.json")) &&
                    !File.Exists(Path.Combine(dir, "active.json.bak"))) continue;
                string id = Path.GetFileName(dir);
                try
                {
                    // Reuse the authoritative, read-only pointer / generation / checksum
                    // validation. Merely parsing active.json can report a broken world as usable.
                    WorldState world = store.Load(id);
                    bool recovered = !String.IsNullOrWhiteSpace(store.LastLoadWarning);
                    worlds.Add(new WorldSummary { WorldId = id,
                        Name = (world.Name ?? id) + (recovered ? "（备份可恢复）" : ""),
                        SaveGeneration = world.SaveGeneration,
                        SavedAtUtc = store.lastLoadedPointer.SavedAtUtc });
                }
                catch (Exception ex) { worlds.Add(new WorldSummary { WorldId = id, Name = id, Error = "存档不可恢复：" + ex.Message }); }
            }
            return worlds.OrderByDescending(x => x.SavedAtUtc, StringComparer.Ordinal).ToList();
        }
        public void Save(WorldState world)
        {
            if (world == null || world.Snapshot == null) throw new InvalidDataException("Cannot save a world without the native world snapshot.");
            string dir = WorldDirectory(world.WorldId);
            Directory.CreateDirectory(dir);
            // If Load had to use active.json.bak, the current active.json is not a
            // trustworthy predecessor. File.Replace would otherwise overwrite the
            // only good .bak with that damaged pointer and cleanup would delete its
            // generation. Restore the already verified fallback as the active
            // pointer *before* starting a new save; a failure still leaves a
            // complete, selectable old generation.
            RestoreRecoveredPointerBeforeSave(world, dir);
            long oldGeneration = world.SaveGeneration;
            long generation = checked(oldGeneration + 1);
            // A failed previous write may leave an uncommitted generation file; never overwrite it.
            while (File.Exists(Path.Combine(dir, FileName(generation)))) generation = checked(generation + 1);
            world.SaveGeneration = generation;
            bool committed = false;
            var pendingResults = world.Transactions.Values.Where(x => x.Applied && !x.Saved).ToList();
            try
            {
                foreach (CommandResult result in pendingResults) { result.Saved = true; result.SaveGeneration = generation; }
                byte[] bytes = Encoding.UTF8.GetBytes(JsonData.Write(world));
                string file = FileName(generation);
                AtomicFile.Write(Path.Combine(dir, file), bytes);
                string digest;
                using (var sha = SHA256.Create()) digest = Convert.ToBase64String(sha.ComputeHash(bytes));
                var pointer = new SavePointer { WorldId = world.WorldId, Name = world.Name, Generation = generation, File = file, Sha256 = digest, SavedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) };
                AtomicFile.Write(Path.Combine(dir, "active.json"), Encoding.UTF8.GetBytes(JsonData.Write(pointer)));
                committed = true;
                // Keep the selected generation and its immediate predecessor. Files from failed commits
                // are harmless and are cleaned only after a new complete commit has been selected.
                try
                {
                    SavePointer previous = TryPointer(Path.Combine(dir, "active.json.bak"));
                    foreach (string old in Directory.GetFiles(dir, "generation_*.json"))
                    {
                        if (Path.GetFileName(old) == file || (previous != null && Path.GetFileName(old) == previous.File)) continue;
                        File.Delete(old);
                    }
                }
                catch (Exception ex) { LastCleanupWarning = "新代已提交，旧代清理失败：" + ex.Message; }
            }
            catch
            {
                if (!committed)
                {
                    world.SaveGeneration = oldGeneration;
                    foreach (CommandResult result in pendingResults) { result.Saved = false; result.SaveGeneration = 0; }
                }
                throw;
            }
        }
        private static string FileName(long generation) { return "generation_" + generation.ToString("D16", CultureInfo.InvariantCulture) + ".json"; }
        private void RestoreRecoveredPointerBeforeSave(WorldState world, string dir)
        {
            if (lastLoadedPointer == null || String.IsNullOrWhiteSpace(LastLoadWarning)
                || lastLoadedPointer.WorldId != world.WorldId
                || lastLoadedPointer.Generation != world.SaveGeneration) return;
            string backupPath = Path.Combine(dir, "active.json.bak");
            byte[] pointerBytes = File.ReadAllBytes(backupPath);
            SavePointer pointer = JsonData.Read<SavePointer>(Encoding.UTF8.GetString(pointerBytes));
            if (pointer == null || pointer.WorldId != world.WorldId || pointer.Generation != world.SaveGeneration
                || pointer.File != FileName(pointer.Generation) || !CryptoData.Equal(pointer.Sha256, lastLoadedPointer.Sha256))
                throw new InvalidDataException("Recovered backup pointer has changed; refusing to replace its only valid generation.");
            // Detect a newly damaged backup before touching either pointer. This
            // additional read is only paid on the first save following recovery.
            byte[] generationBytes = File.ReadAllBytes(Path.Combine(dir, pointer.File));
            string digest;
            using (var sha = SHA256.Create()) digest = Convert.ToBase64String(sha.ComputeHash(generationBytes));
            if (!CryptoData.Equal(digest, pointer.Sha256))
                throw new InvalidDataException("Recovered generation checksum changed before save; refusing to overwrite its pointer.");
            AtomicFile.Write(Path.Combine(dir, "active.json"), pointerBytes);
        }
        private static SavePointer TryPointer(string path)
        {
            try { return File.Exists(path) ? JsonData.Read<SavePointer>(File.ReadAllText(path, Encoding.UTF8)) : null; }
            catch (Exception) { return null; }
        }
        public WorldState Load(string worldId)
        {
            LastLoadWarning = null;
            lastLoadedPointer = null;
            string dir = WorldDirectory(worldId);
            Exception primaryError = null;
            foreach (string name in new[] { "active.json", "active.json.bak" })
            {
                try
                {
                    var pointer = JsonData.Read<SavePointer>(File.ReadAllText(Path.Combine(dir, name), Encoding.UTF8));
                    if (pointer.WorldId != worldId || pointer.File != FileName(pointer.Generation)) throw new InvalidDataException("Save pointer does not identify its world/generation.");
                    byte[] bytes = File.ReadAllBytes(Path.Combine(dir, pointer.File));
                    string digest;
                    using (var sha = SHA256.Create()) digest = Convert.ToBase64String(sha.ComputeHash(bytes));
                    if (!CryptoData.Equal(digest, pointer.Sha256)) throw new InvalidDataException("Save generation checksum mismatch.");
                    var world = JsonData.Read<WorldState>(Encoding.UTF8.GetString(bytes));
                    if (world == null || world.SchemaVersion != 1 || world.WorldId != worldId || world.SaveGeneration != pointer.Generation)
                        throw new InvalidDataException("Save generation metadata mismatch.");
                    if (name.EndsWith(".bak", StringComparison.Ordinal)) LastLoadWarning = "当前存档代不可读，已恢复上一完整代 " + pointer.Generation + "：" + primaryError?.Message;
                    lastLoadedPointer = pointer;
                    return world;
                }
                catch (Exception ex) { if (primaryError == null) primaryError = ex; }
            }
            throw new InvalidDataException("联机世界及上一完整存档代均不可读取；没有修改任何存档。", primaryError);
        }
    }
}
