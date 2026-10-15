using System;
using System.IO;

namespace FreeWorldMultiplayer
{
    public sealed partial class MultiplayerSession
    {
        private TransportOptions pendingHostOptions;
        private bool startupCancelled;
        private bool disposeRequested;
        private Exception startupFailure;

        private void CancelHostCreation()
        {
            if (bridge.IsCreatingWorld) IsStarting = true;
            startupCancelled = true; closing = true; Paused = true;
            if (LastError == null) SetStatus("已取消创建请求，正在等待本次生成安全结束……");
        }
        private void DeferStartupFailure(Exception error)
        {
            if (startupFailure == null)
            {
                startupFailure = error;
                Error("联机世界创建失败，等待原生生成线程结束后清理：" + error);
            }
            startupCancelled = true; closing = true; Paused = true;
        }
        private void TickHostCreation()
        {
            bool completed;
            GameSnapshot native;
            PlayerSnapshot player;
            BridgeResult result;
            try { result = bridge.PollCreateWorld(out completed, out native, out player); }
            catch (Exception error)
            {
                if (bridge.IsCreatingWorld) { DeferStartupFailure(error); return; }
                FinishStartupFailure(error); return;
            }
            if (!completed)
            {
                if (result == null || !result.Success)
                {
                    var error = new InvalidOperationException("原生世界生成 Pending 返回无效结果：" + (result == null ? "null" : result.Error));
                    if (bridge.IsCreatingWorld) DeferStartupFailure(error); else FinishStartupFailure(error);
                }
                return;
            }
            if (bridge.IsCreatingWorld)
            {
                // A completed worker which has not been consumed is still protected from Dispose.
                DeferStartupFailure(new InvalidOperationException("原生生成报告完成，但尚未结束或收尾，暂不能释放联机钩子。"));
                return;
            }
            if (startupFailure != null) { FinishStartupFailure(startupFailure); return; }
            if (result == null || !result.Success)
            {
                FinishStartupFailure(new InvalidOperationException("原生世界生成失败：" + (result == null ? "null result" : result.Error))); return;
            }
            if (startupCancelled)
            {
                IsStarting = false; pendingHostOptions = null;
                try { CompleteDisconnect(); SetStatus("已取消新建联机世界，未保存或开启房间。"); }
                catch (Exception cleanup) { Error("已取消创建，但原生上下文清理失败：" + cleanup); }
                if (disposeRequested) disposed = true;
                return;
            }
            try
            {
                if (native == null || player == null) throw new InvalidDataException("New world bridge omitted native/player state.");
                PlayerRecord host = World.Players[LocalPlayerId];
                World.Snapshot = native; World.WorldTimeMM = native.WorldTimeMM;
                ApplyPlayerRecord(host, player);
                host.InitialAssetsGranted = true; host.CurrentMapId = player.MotherShipMapId;
                RegisterMotherShip(host);
                // Final snapshots were captured on this main-thread completion. No simulation has
                // run between their capture and first commit, so reuse them for the first generation.
                IsStarting = false;
                SaveOrThrow(captureNativeState: false);
                pendingHostOptions.MaxPlayers = World.Options.MaxPlayers;
                transport.StartHost(pendingHostOptions); pendingHostOptions = null;
                IsConnected = true;
                if (host.CurrentMapId >= 0) EnterMap(host, host.CurrentMapId);
                SetStatus("房间已创建：" + World.Name);
            }
            catch (Exception error) { FinishStartupFailure(error); }
        }
        private void FinishStartupFailure(Exception error)
        {
            IsStarting = false; pendingHostOptions = null; Paused = true;
            string original = "新建联机世界失败：" + error;
            Error(original);
            try { CompleteDisconnect(); }
            catch (Exception cleanup) { Error(original + Environment.NewLine + "退出清理同时失败：" + cleanup); }
            if (disposeRequested) disposed = true;
        }
    }
}
