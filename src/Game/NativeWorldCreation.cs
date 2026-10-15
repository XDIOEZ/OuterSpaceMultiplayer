using System;
using System.Diagnostics;
using System.Threading;
using Script.FreeWorldAirShip.Galaxy;
using SmartAI.Data;
using UnityEngine.UI;

namespace FreeWorldMultiplayer
{
    public sealed partial class GameBridge
    {
        private sealed class WorldCreation
        {
            internal Thread Worker;
            internal FWGameData GameData;
            internal Exception Error;
            internal volatile bool Completed;
            internal readonly Stopwatch Elapsed = Stopwatch.StartNew();
        }
        private WorldCreation worldCreation;
        private UILoading creationLoading;
        private bool creationCancelSent;
        private readonly object seedUidLock = new object();
        public bool IsCreatingWorld { get { return worldCreation != null; } }

        // ArchiveGen prepares resource catalogs on Unity's thread, generates only the native
        // archive on a dedicated worker, and dispatches completion back to Unity. Keep that split:
        // createStartFleet may post resource work to FairyRunnable and wait for LateUpdate.
        public BridgeResult BeginCreateWorld(WorldOptions options, string hostPlayerId, UidRange uids)
        {
            if (worldCreation != null || World != null || Players.Count != 0) return BridgeResult.Reject("已有世界正在创建或已绑定，不能重复创建。");
            if (Context == null || uids == null || uids.Next > uids.Last) return BridgeResult.Reject("新世界缺少联机会话或初始实体号段。");
            try
            {
                FWStartConfig config = StartConfig(options);
                // Fail type-graph/configuration errors before the worker binds native global data.
                NativeWire.Pack(config);
                OpenWorldCreationLoading();
                Logger.LogEngine("[FreeWorldMultiplayer][WorldCreate] stage=prepare-resources begin");
                var bandSnapshot = (GalaxyBandGenerationSnapshot)NativeWire.Call(typeof(ArchiveGen), "prepareArchiveGenData");
                PrepareStartingShipVariant(config);
                var pending = new WorldCreation();
                hostId = hostPlayerId;
                lock (seedUidLock) { seedRange = uids; Seeding = true; }
                pending.Worker = new Thread(() =>
                {
                    try
                    {
                        Logger.LogEngine("[FreeWorldMultiplayer][WorldCreate] stage=native-generation begin");
                        // This is the exact background segment used by ArchiveGen. Do not move
                        // session, transport, map loading or serializer finalization to this thread.
                        pending.GameData = FWWorldGameDataService.registerUnactivatedUser(config, bandSnapshot);
                    }
                    catch (Exception error) { pending.Error = error; }
                    finally { pending.Completed = true; }
                }) { IsBackground = true, Name = "FreeWorldMultiplayer.NativeWorldCreation" };
                worldCreation = pending;
                pending.Worker.Start();
                return BridgeResult.Ok();
            }
            catch (Exception error)
            {
                worldCreation = null;
                lock (seedUidLock) { Seeding = false; seedRange = null; }
                string reason = "World creation preparation failed: " + error;
                try { CloseWorldCreationLoading(); }
                catch (Exception cleanup) { reason += Environment.NewLine + "Loading cleanup also failed: " + cleanup; }
                return BridgeResult.Fail(reason);
            }
        }

        public BridgeResult PollCreateWorld(out bool completed, out GameSnapshot world, out PlayerSnapshot player)
        {
            completed = false; world = null; player = null;
            WorldCreation pending = worldCreation;
            if (pending == null) { completed = true; return BridgeResult.Reject("没有待完成的新世界创建。"); }
            if (!pending.Completed) return BridgeResult.Ok();
            completed = true;
            BridgeResult result;
            try
            {
                if (pending.Error != null)
                    throw new InvalidOperationException("Native world generation failed after " + pending.Elapsed.ElapsedMilliseconds + "ms.", pending.Error);
                if (pending.GameData == null) throw new InvalidOperationException("Native world creation completed without game data.");
                Logger.LogEngine("[FreeWorldMultiplayer][WorldCreate] stage=native-generation complete elapsedMs=" + pending.Elapsed.ElapsedMilliseconds);
                World = pending.GameData; World.newComer = false;
                Players.Add(hostId, World); Online.Add(hostId);
                IndexMaps(World); RegisterPlayerAssets(hostId, World);
                ResetPersonalBaseline(hostId);
                ActivateLocal(hostId);
                Logger.LogEngine("[FreeWorldMultiplayer][WorldCreate] stage=capture-initial-state begin");
                world = CaptureWorld(); player = CapturePlayer(hostId);
                Logger.LogEngine("[FreeWorldMultiplayer][WorldCreate] stage=complete elapsedMs=" + pending.Elapsed.ElapsedMilliseconds);
                result = BridgeResult.Ok();
            }
            catch (Exception error) { result = BridgeResult.Fail("World creation finalization failed: " + error); }
            finally
            {
                // A volatile Completed read observes all worker writes. Only now may Core dispose
                // the bridge, restore the previous single-player context, or unload the UID hooks.
                lock (seedUidLock) { Seeding = false; seedRange = null; }
                worldCreation = null;
            }
            try { CloseWorldCreationLoading(); }
            catch (Exception cleanup)
            {
                return BridgeResult.Fail((result.Success ? "World creation loading cleanup failed: " : result.Error + Environment.NewLine + "Loading cleanup also failed: ") + cleanup);
            }
            return result;
        }

        private void OpenWorldCreationLoading()
        {
            SenderCenter.showConnectDialog("正在创建联机世界，点击取消创建。", 0, 0, false);
            creationLoading = UILoading.getInstance();
            if (creationLoading == null) throw new InvalidOperationException("Native archive loading dialog was not created.");
            var background = creationLoading.transform.Find("BG");
            Button cancel = background == null ? null : background.GetComponent<Button>();
            if (cancel == null) throw new InvalidOperationException("Native archive loading dialog is missing its BG button.");
            creationCancelSent = false;
            // Only this creation's existing native button changes. UILoading's normal cancellable
            // callback hides its overlay unconditionally; keep the worker's exclusive barrier instead.
            cancel.onClick.RemoveAllListeners();
            cancel.onClick.AddListener(() =>
            {
                if (worldCreation == null || creationCancelSent) return;
                if (Context.RequestCancelWorldCreation == null) throw new InvalidOperationException("World creation cancellation is not connected to the session.");
                Context.RequestCancelWorldCreation();
                creationCancelSent = true;
                creationLoading.setContent("正在结束本次创建，请等待……", 0, 0, false, null);
                cancel.interactable = false;
            });
        }
        private void CloseWorldCreationLoading()
        {
            UILoading loading = creationLoading; creationLoading = null;
            if (loading != null && UILoading.getInstance() == loading) SenderCenter.hideConnectDialog();
        }

        private static void PrepareStartingShipVariant(FWStartConfig config)
        {
            if (config == null || config.FleetSetting == null) throw new InvalidOperationException("Missing native fleet start configuration.");
            if (config.FleetSetting.Type == "0") return; // Exact native no-initial-ship branch.
            string path = (string)NativeWire.Call(typeof(FWWorldGameDataService), "resolveStartFleetShipVariantPath", config.FleetSetting.Type);
            ShipVariant loaded = FWWorldGameDataService.loadByShipVariantPath(path);
            if (loaded == null) loaded = FWWorldGameDataService.setShipVariantPathCache(path);
            if (loaded == null || FWWorldGameDataService.loadByShipVariantPath(path) != loaded)
                throw new InvalidOperationException("Selected starter ship template could not be cached on Unity's thread: " + path);
        }
    }
}
