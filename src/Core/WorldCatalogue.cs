using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace FreeWorldMultiplayer
{
    public sealed class WorldDeleteResult
    {
        public string WorldId;
        public string RecoveryPath;
    }

    public sealed partial class SaveStore
    {
        private const int MaximumPointerBytes = 64 * 1024;
        private readonly string storageRootPath;

        // Catalogue reads never hash or deserialize a generation. A header is only
        // a candidate; callers must validate the selected world before enabling Continue.
        public static IList<WorldSummary> ListWorldHeaders(string storageRoot)
        {
            var store = new SaveStore(storageRoot);
            var result = new List<WorldSummary>();
            EnsureNoReparsePath(store.root);
            if (!Directory.Exists(store.root)) return result;
            foreach (string dir in Directory.GetDirectories(store.root))
            {
                string id = Path.GetFileName(dir);
                Guid parsed;
                if (!Guid.TryParseExact(id, "N", out parsed)) continue;
                // Keep damaged worlds visible so the player can explicitly remove them.
                result.Add(store.ReadWorldHeader(id));
            }
            return result.OrderByDescending(x => x.SavedAtUtc, StringComparer.Ordinal).ToList();
        }

        public static WorldSummary ValidateWorld(string storageRoot, string worldId)
        {
            var store = new SaveStore(storageRoot);
            try
            {
                using (store.AcquireWorldOperation(worldId))
                {
                    // The original full loader retains checksum, metadata, relations
                    // and previous-generation recovery checks, without any shortcut.
                    WorldState world = store.LoadCore(worldId);
                    WorldSummary summary = store.ReadWorldHeader(worldId);
                    summary.Name = (world.Name ?? worldId) +
                        (!String.IsNullOrWhiteSpace(store.LastLoadWarning) ? "（备份可恢复）" : "");
                    summary.SaveGeneration = world.SaveGeneration;
                    summary.SavedAtUtc = store.lastLoadedPointer.SavedAtUtc;
                    summary.Error = null;
                    summary.ValidationRequired = false;
                    return summary;
                }
            }
            catch (Exception ex)
            {
                WorldSummary summary = store.ReadWorldHeader(worldId);
                summary.Error = "存档不可继续：" + ex.Message;
                summary.ValidationRequired = false;
                return summary;
            }
        }

        public static WorldDeleteResult DeleteWorld(string storageRoot, string worldId, string expectedCatalogToken)
        {
            if (String.IsNullOrWhiteSpace(expectedCatalogToken))
                throw new InvalidOperationException("请刷新存档列表后重新确认删除。");
            var store = new SaveStore(storageRoot);
            using (store.AcquireWorldOperation(worldId, false))
            {
                string source = store.WorldDirectory(worldId);
                EnsureDirectChild(store.root, source);
                EnsureNoReparsePath(source);
                if (!Directory.Exists(source)) throw new DirectoryNotFoundException("所选联机世界已不存在，请刷新列表。");
                WorldSummary current = store.ReadWorldHeader(worldId);
                if (!CryptoData.Equal(expectedCatalogToken, current.CatalogToken))
                    throw new InvalidOperationException("所选存档在确认期间已发生变化，请刷新后重新确认删除。");

                string deletedRoot = Path.Combine(store.storageRootPath, "deleted-worlds");
                string retiredWorld = Path.Combine(deletedRoot, worldId);
                EnsureDirectChild(store.storageRootPath, deletedRoot);
                EnsureDirectChild(deletedRoot, retiredWorld);
                EnsureNoReparsePath(retiredWorld);
                bool createdRetiredDirectory = !Directory.Exists(retiredWorld);
                Directory.CreateDirectory(retiredWorld);
                string destination = Path.Combine(retiredWorld,
                    DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
                EnsureDirectChild(retiredWorld, destination);
                try
                {
                    // Same storage root / volume: no recursive delete or file copy.
                    // Recheck all parents immediately before the single rename.
                    EnsureNoReparsePath(source);
                    EnsureNoReparsePath(destination);
                    Directory.Move(source, destination);
                }
                catch
                {
                    if (createdRetiredDirectory)
                    {
                        // Only remove our empty container; never recurse or remove user data.
                        try { Directory.Delete(retiredWorld, false); } catch { }
                    }
                    throw;
                }
                return new WorldDeleteResult { WorldId = worldId, RecoveryPath = destination };
            }
        }

        private WorldSummary ReadWorldHeader(string worldId)
        {
            var summary = new WorldSummary { WorldId = worldId, Name = worldId, ValidationRequired = true };
            var signature = new StringBuilder(worldId);
            SavePointer selected = null;
            string firstError = null;
            try
            {
                string dir = WorldDirectory(worldId);
                EnsureDirectChild(root, dir);
                EnsureNoReparsePath(dir);
                if (!Directory.Exists(dir)) throw new DirectoryNotFoundException("联机世界目录不存在。");
                foreach (string name in new[] { "active.json", "active.json.bak" })
                {
                    string path = Path.Combine(dir, name);
                    try
                    {
                        AppendFileMetadata(signature, path, name);
                        byte[] bytes = ReadSmallPointer(path);
                        signature.Append('|').Append(CryptoData.Hash(Convert.ToBase64String(bytes)));
                        SavePointer pointer = JsonData.Read<SavePointer>(Encoding.UTF8.GetString(bytes));
                        if (pointer == null || pointer.WorldId != worldId || pointer.File != FileName(pointer.Generation))
                            throw new InvalidDataException("存档索引与世界或保存代不一致。");
                        string generation = Path.Combine(dir, pointer.File);
                        AppendFileMetadata(signature, generation, pointer.File);
                        if (!File.Exists(generation)) throw new FileNotFoundException("存档代文件不存在。");
                        if (selected == null) selected = pointer;
                    }
                    catch (Exception ex)
                    {
                        signature.Append('|').Append(ex.GetType().Name);
                        if (firstError == null) firstError = ex.Message;
                    }
                }
                if (selected == null) summary.Error = "存档索引不可读取：" + firstError;
                else
                {
                    summary.Name = selected.Name ?? worldId;
                    summary.SaveGeneration = selected.Generation;
                    summary.SavedAtUtc = selected.SavedAtUtc;
                }
                if (Directory.Exists(Path.Combine(storageRootPath, "deleted-worlds", worldId)))
                    summary.Error = "该世界已移入已删除目录；当前同名目录需人工确认后恢复。";
            }
            catch (Exception ex) { summary.Error = "存档目录不可读取：" + ex.Message; signature.Append('|').Append(ex.GetType().Name); }
            summary.CatalogToken = CryptoData.Hash(signature.ToString());
            return summary;
        }

        private static byte[] ReadSmallPointer(string path)
        {
            EnsureNoReparsePath(path);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length > MaximumPointerBytes) throw new InvalidDataException("存档索引超出允许大小。");
                using (var bytes = new MemoryStream())
                {
                    byte[] buffer = new byte[4096];
                    int count;
                    while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        if (bytes.Length + count > MaximumPointerBytes) throw new InvalidDataException("存档索引超出允许大小。");
                        bytes.Write(buffer, 0, count);
                    }
                    return bytes.ToArray();
                }
            }
        }

        private static void AppendFileMetadata(StringBuilder value, string path, string label)
        {
            EnsureNoReparsePath(path);
            var info = new FileInfo(path);
            value.Append('|').Append(label).Append(':').Append(info.Exists ? "1" : "0");
            if (info.Exists) value.Append(':').Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks);
        }

        private IDisposable AcquireWorldOperation(string worldId, bool rejectRetired = true)
        {
            string world = WorldDirectory(worldId);
            EnsureDirectChild(root, world);
            EnsureNoReparsePath(world);
            string locks = Path.Combine(storageRootPath, "world-locks");
            EnsureDirectChild(storageRootPath, locks);
            EnsureNoReparsePath(locks);
            Directory.CreateDirectory(locks);
            string lockPath = Path.Combine(locks, worldId + ".lock");
            EnsureNoReparsePath(lockPath);
            FileStream operation;
            try { operation = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex)
            {
                // Never wait on Unity's main thread for another process or validation.
                throw new IOException("所选联机存档正在保存或校验，请稍后重试。", ex);
            }
            try
            {
                EnsureNoReparsePath(world);
                string retired = Path.Combine(storageRootPath, "deleted-worlds", worldId);
                EnsureNoReparsePath(retired);
                if (rejectRetired && Directory.Exists(retired))
                    throw new InvalidOperationException("该联机世界已移入已删除目录，不会重新创建或覆盖它。");
                return operation;
            }
            catch { operation.Dispose(); throw; }
        }

        private static void EnsureDirectChild(string parent, string child)
        {
            string normalizedParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedChild = Path.GetFullPath(child);
            if (!String.Equals(Path.GetDirectoryName(normalizedChild), normalizedParent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("拒绝访问联机存档目录范围之外的路径。");
        }

        private static void EnsureNoReparsePath(string path)
        {
            string current = Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("联机存档路径包含链接或重解析点，已拒绝操作。");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                string parent = Path.GetDirectoryName(current);
                if (String.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
                current = parent;
            }
        }
    }
}
