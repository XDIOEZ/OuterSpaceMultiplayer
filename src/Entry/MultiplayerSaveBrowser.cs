using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FreeWorldMultiplayer
{
    public sealed partial class MultiplayerRoomDialog
    {
        // The worker owns filesystem data only. Unity objects and Runtime are read
        // exclusively by the main-thread pump. At most one full save is parsed.
        private static readonly SemaphoreSlim SaveBrowserReadGate = new SemaphoreSlim(1, 1);
        private Task<SaveBrowserResult> saveBrowserWorker;
        private CancellationTokenSource saveBrowserCancellation = new CancellationTokenSource();
        private int saveBrowserEpoch, saveSelectionRevision;
        private bool saveBrowserActive, catalogLoading, catalogRequested, saveDeleteInProgress;
        private SaveBrowserResult pendingSaveDelete;
        private string saveCatalogError;
        private sealed class SaveBrowserResult
        {
            internal int Epoch, Selection;
            internal string Kind, WorldId, Token, Error, RecoveryPath;
            internal IList<WorldSummary> Worlds;
            internal WorldSummary World;
            internal bool Cancelled;
        }
        private bool SaveBrowserVisible => page == Page.Create && continueWorldForm != null && continueWorldForm.activeInHierarchy;
        private bool SaveBrowserOffline => Runtime != null && Runtime.Session == null && !Busy && !Connected
            && (Runtime.Transport == null || !Runtime.Transport.IsRunning) && !Runtime.IsWorldStorageMutating;
        private bool CanContinueWorld(WorldSummary world) => SaveBrowserOffline && !catalogLoading && !saveDeleteInProgress
            && world != null && !world.ValidationRequired && String.IsNullOrEmpty(world.Error);
        private bool CanDeleteWorld(WorldSummary world) => SaveBrowserOffline && !catalogLoading && !saveDeleteInProgress
            && world != null && !String.IsNullOrEmpty(world.CatalogToken);
        private string SaveValidationCaption(WorldSummary world)
        {
            if (!String.IsNullOrEmpty(world.Error)) return "无法读取";
            if (!world.ValidationRequired) return "可继续";
            return world.WorldId == selectedWorldId && !catalogLoading ? "正在校验…" : "待校验";
        }
        private void RefreshWorlds()
        {
            if (!SaveBrowserVisible || !SaveBrowserOffline || saveDeleteInProgress) return;
            InvalidateSaveBrowser();
            saveBrowserActive = true; catalogRequested = catalogLoading = true;
            saveCatalogError = null;
            RefreshCreateState();
            // Dispatch at the next UI update so the page becomes visible first.
        }
        private void InvalidateSaveBrowser()
        {
            saveBrowserEpoch++; saveSelectionRevision++;
            saveBrowserCancellation.Cancel(); saveBrowserCancellation.Dispose();
            saveBrowserCancellation = new CancellationTokenSource();
        }
        private void SuspendSaveBrowser()
        {
            InvalidateSaveBrowser();
            saveBrowserActive = false; catalogRequested = catalogLoading = false;
            if (pendingSaveDelete != null) { pendingSaveDelete = null; saveDeleteInProgress = false; }
        }
        private void SelectSavedWorld(string worldId)
        {
            if (!SaveBrowserOffline || catalogLoading || saveDeleteInProgress) return;
            if (selectedWorldId != worldId) { selectedWorldId = worldId; saveSelectionRevision++; }
            RefreshCreateState();
        }
        private void PumpSaveBrowser()
        {
            if (saveBrowserWorker != null && saveBrowserWorker.IsCompleted)
            {
                // Every worker returns a result, including filesystem failures.
                SaveBrowserResult result = saveBrowserWorker.GetAwaiter().GetResult();
                saveBrowserWorker = null;
                if (result.Kind == "delete")
                {
                    saveDeleteInProgress = false;
                    if (SaveBrowserVisible && SaveBrowserOffline)
                    {
                        if (!String.IsNullOrEmpty(result.Error)) Safe(() => { throw new InvalidOperationException(result.Error); });
                        else { localFeedback = "已删除所选联机世界。恢复目录：\n" + result.RecoveryPath; lastRuntimeFeedback = Runtime.Feedback; }
                        RefreshWorlds();
                    }
                }
                else if (!result.Cancelled && saveBrowserActive && result.Epoch == saveBrowserEpoch && SaveBrowserVisible && SaveBrowserOffline)
                {
                    if (result.Kind == "catalog")
                    {
                        catalogLoading = false;
                        saveCatalogError = result.Error;
                        worlds = result.Worlds ?? new List<WorldSummary>();
                        if (!worlds.Any(w => w.WorldId == selectedWorldId)) selectedWorldId = worlds.FirstOrDefault()?.WorldId;
                        saveSelectionRevision++;
                        RebuildSaveRows();
                        if (!String.IsNullOrEmpty(result.Error)) Safe(() => { throw new InvalidOperationException("读取联机存档列表失败：" + result.Error); });
                    }
                    else if (result.Selection == saveSelectionRevision && result.WorldId == selectedWorldId)
                    {
                        int index = worlds.ToList().FindIndex(w => w.WorldId == result.WorldId && w.CatalogToken == result.Token);
                        if (index >= 0)
                        {
                            WorldSummary validated = result.World;
                            if (validated == null) validated = new WorldSummary { WorldId = result.WorldId, Name = worlds[index].Name,
                                SavedAtUtc = worlds[index].SavedAtUtc, SaveGeneration = worlds[index].SaveGeneration,
                                CatalogToken = result.Token, Error = result.Error ?? "存档校验未完成。" };
                            worlds[index] = validated;
                            foreach (SaveRow row in saveRows.Where(r => r.World.WorldId == result.WorldId))
                            { row.World = validated; row.Name.text = validated.Name; }
                        }
                    }
                    RefreshCreateState(); RefreshVisualTheme();
                }
            }
            if (saveBrowserWorker != null) return;
            if (pendingSaveDelete != null)
            {
                SaveBrowserResult request = pendingSaveDelete; pendingSaveDelete = null;
                if (!DeleteRequestStillCurrent(request)) { saveDeleteInProgress = false; return; }
                IDisposable lease;
                try { lease = Runtime.BeginWorldStorageMutation(); }
                catch (Exception error) { saveDeleteInProgress = false; Safe(() => { throw error; }); return; }
                string root = Runtime.StorageRoot;
                try
                {
                    saveBrowserWorker = Task.Run(() =>
                    {
                        try
                        {
                            SaveBrowserReadGate.Wait();
                            try
                            {
                                WorldDeleteResult deleted = SaveStore.DeleteWorld(root, request.WorldId, request.Token);
                                request.RecoveryPath = deleted.RecoveryPath;
                            }
                            finally { SaveBrowserReadGate.Release(); }
                        }
                        catch (Exception error) { request.Error = error.ToString(); }
                        finally { lease.Dispose(); }
                        return request;
                    });
                }
                catch (Exception error)
                {
                    lease.Dispose(); saveDeleteInProgress = false;
                    Safe(() => { throw error; });
                }
                return;
            }
            if (!saveBrowserActive || !SaveBrowserVisible || !SaveBrowserOffline) return;
            var work = new SaveBrowserResult { Epoch = saveBrowserEpoch, Selection = saveSelectionRevision };
            string storageRoot = Runtime.StorageRoot;
            CancellationToken cancellation = saveBrowserCancellation.Token;
            if (catalogRequested)
            {
                catalogRequested = false; work.Kind = "catalog";
                saveBrowserWorker = Task.Run(() =>
                {
                    try
                    {
                        SaveBrowserReadGate.Wait(cancellation);
                        try { if (!cancellation.IsCancellationRequested) work.Worlds = SaveStore.ListWorldHeaders(storageRoot); }
                        finally { SaveBrowserReadGate.Release(); }
                    }
                    catch (Exception error) { work.Error = error.ToString(); }
                    work.Cancelled = cancellation.IsCancellationRequested; return work;
                });
            }
            else if (!catalogLoading)
            {
                WorldSummary selected = worlds.FirstOrDefault(w => w.WorldId == selectedWorldId);
                if (selected == null || !selected.ValidationRequired || !String.IsNullOrEmpty(selected.Error)) return;
                work.Kind = "validate"; work.WorldId = selected.WorldId; work.Token = selected.CatalogToken;
                saveBrowserWorker = Task.Run(() =>
                {
                    try
                    {
                        SaveBrowserReadGate.Wait(cancellation);
                        try { if (!cancellation.IsCancellationRequested) work.World = SaveStore.ValidateWorld(storageRoot, work.WorldId); }
                        finally { SaveBrowserReadGate.Release(); }
                    }
                    catch (Exception error) { work.Error = error.ToString(); }
                    work.Cancelled = cancellation.IsCancellationRequested; return work;
                });
            }
        }
        private bool DeleteRequestStillCurrent(SaveBrowserResult request)
        {
            WorldSummary selected = worlds.FirstOrDefault(w => w.WorldId == selectedWorldId);
            return this != null && saveBrowserActive && SaveBrowserVisible && SaveBrowserOffline
                && request.Epoch == saveBrowserEpoch && request.Selection == saveSelectionRevision
                && selected != null && selected.WorldId == request.WorldId && selected.CatalogToken == request.Token;
        }
        private void ConfirmDeleteWorld()
        {
            WorldSummary selected = SelectedWorld();
            if (!CanDeleteWorld(selected)) throw new InvalidOperationException("请先退出联机房间，等待当前操作完成后再删除存档。");
            var request = new SaveBrowserResult { Kind = "delete", Epoch = saveBrowserEpoch, Selection = saveSelectionRevision,
                WorldId = selected.WorldId, Token = selected.CatalogToken };
            MultiplayerRuntime runtime = Runtime;
            FairyUIDispatcher.showTip("确认删除这个联机世界？\n名称：" + selected.Name + "\n保存时间：" + FormatSaveTime(selected.SavedAtUtc)
                + "\n世界 ID：" + selected.WorldId + "\n删除后将从列表移除，文件保留在可恢复目录。",
                LanguageDef.getTextByID(Lan.btn_Confirm), LanguageDef.getTextByID(Lan.btn_Cancel), choice =>
                {
                    if (choice != Tip.TIP_CONFIRM || this == null) return;
                    Safe(() =>
                    {
                        if (!ReferenceEquals(Runtime, runtime) || saveDeleteInProgress || !DeleteRequestStillCurrent(request))
                            throw new InvalidOperationException("当前选择或联机会话已经变化，请重新选择存档后删除。");
                        saveDeleteInProgress = true; pendingSaveDelete = request;
                        RefreshState();
                    });
                });
        }
    }
}
