using System;
using System.IO;
using UnityEngine;
using Steamworks;

// The global type name and base class are required by the native MOD loader.
public sealed class ModPluginLauncher : BaseModLauncher
{
    public override void launcher()
    {
        if (FreeWorldMultiplayer.MultiplayerRuntime.Instance != null) return;
        var root = new GameObject("FreeWorldMultiplayer.Runtime");
        UnityEngine.Object.DontDestroyOnLoad(root);
        try { root.AddComponent<FreeWorldMultiplayer.MultiplayerRuntime>().Initialize(config.Folder); }
        catch { root.SetActive(false); UnityEngine.Object.Destroy(root); throw; }
    }
}

namespace FreeWorldMultiplayer
{
    public sealed class MultiplayerRuntime : MonoBehaviour
    {
        public static MultiplayerRuntime Instance { get; private set; }
        internal MultiplayerSession Session { get; private set; }
        internal GameBridge Bridge { get; private set; }
        internal RoomTransport Transport { get; private set; }
        internal string StorageRoot { get; private set; }
        private string feedback;
        internal string Feedback { get { return feedback; } private set { if (feedback != value) feedback = MultiplayerDiagnostics.RedactForDisplay(value); } }
        internal string PendingInvite { get; private set; }
        private Callback<GameLobbyJoinRequested_t> inviteCallback;
        private UnityEngine.UI.Button entryButton;
        private UnityEngine.UI.Button nativeTemplate;
        private TMPro.TMP_Text nativeFont;
        private double nextUiSearch;
        private bool tickFailed;
        private bool awaitingClose;
        private Action pendingStart;
        private bool quitting;
        private bool unloading;
        private bool awaitingMenuReturn;
        private bool menuReturnRequested;
        private bool nativeReturnQueued;
        private bool showHostLeftNotice;
        private callbackInt nativeReturnCallback;
        private ViewCover menuReturnCover;
        private int menuReturnReadyFrame;
        private bool starting;
        private bool awaitingStartupCompletion;
        private string startupFailureContext;
        private string lastLoggedError;
        internal bool DirectLaunch { get; private set; }
        internal bool IsStarting { get { return starting || (Session != null && Session.IsStarting); } }

        internal void Initialize(string modFolder)
        {
            if (Instance != null) throw new InvalidOperationException("联机 MOD 不能重复初始化。");
            StorageRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CatStudio", "FreeWorldMultiplayer");
            DirectLaunch = Array.Exists(Environment.GetCommandLineArgs(), argument => argument == "-fwm-direct");
            Directory.CreateDirectory(StorageRoot);
            Feedback = "请选择新建联机世界、续开联机存档或加入房间。";
            Instance = this;
            if (DirectLaunch) DirectLaunchHooks.Install();
            Logger.LogEngine("[FreeWorldMultiplayer] MOD已加载；独立数据目录=" + StorageRoot);
        }

        private void Update()
        {
            if (Session != null && !tickFailed)
            {
                try { Session.Tick(Time.unscaledDeltaTime); }
                catch (Exception error)
                {
                    tickFailed = true;
                    ShowError("会话推进失败，已停止继续推进，请保存错误现场后退出房间。", error);
                }
            }
            if (awaitingStartupCompletion && Session != null && !Session.IsStarting)
            {
                awaitingStartupCompletion = false;
                if (!Session.IsConnected && !awaitingClose)
                {
                    string original = Session.LastError ?? Session.Status;
                    Logger.LogErrorEngine("[FreeWorldMultiplayer][startup] " + startupFailureContext + "\n" + original);
                    try { FinalizeSession(); }
                    catch (Exception cleanup) { Logger.LogErrorEngine("[FreeWorldMultiplayer][startup-cleanup] 原始失败=" + original + "\n清理失败=" + cleanup); }
                    Feedback = startupFailureContext + "\n" + original;
                }
                MultiplayerRoomDialog.RefreshOpenState();
            }
            if (awaitingClose && Session != null && !Session.IsConnected && !Session.IsStarting)
            {
                Action next = pendingStart; pendingStart = null;
                try { FinalizeSession(); if (next != null && !awaitingMenuReturn) Perform(next, "切换联机房间失败。"); }
                catch (Exception error) { ShowError("结束联机会话时释放资源失败。", error); }
            }
            if (awaitingMenuReturn) CompleteMenuReturn();
            TickFleetInterceptUi();
            nextUiSearch -= Time.unscaledDeltaTime;
            if (nextUiSearch > 0) return;
            nextUiSearch = 1;
            try { EnsureEntry(); EnsureSteamInvites(); }
            catch (Exception error) { ShowError("创建联机入口失败。", error); }
        }

        private void EnsureEntry()
        {
            if (UIDispatcher.Canvas == null || UIDispatcher.getUIRoot() == null) return;
            if (nativeTemplate == null || nativeFont == null)
            {
                foreach (var button in Resources.FindObjectsOfTypeAll<UnityEngine.UI.Button>())
                {
                    if (button == null || !button.gameObject.scene.IsValid() || button.name.StartsWith("FWM.", StringComparison.Ordinal)) continue;
                    var text = button.GetComponentInChildren<TMPro.TMP_Text>(true);
                    if (text == null || text.font == null || button.targetGraphic == null) continue;
                    nativeTemplate = button; nativeFont = text; break;
                }
            }
            if (entryButton != null || nativeTemplate == null || nativeFont == null) return;
            entryButton = MultiplayerUi.CloneButton(nativeTemplate, UIDispatcher.Canvas.transform, "FWM.Entry", "联机 V1", OpenPanel);
            var rect = (RectTransform)entryButton.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.one;
            rect.anchoredPosition = new Vector2(-18, -18);
            rect.sizeDelta = new Vector2(136, 40);
            MultiplayerEntryDrag.Attach(entryButton);
            entryButton.transform.SetAsLastSibling();
        }

        private void EnsureSteamInvites()
        {
            if (inviteCallback != null || DirectLaunch) return;
            // Merely loading the MOD in direct mode must never initialize the native Steam manager.
            bool existing = false;
            foreach (SteamManager manager in Resources.FindObjectsOfTypeAll<SteamManager>())
                if (manager != null && manager.gameObject.scene.IsValid()) { existing = true; break; }
            if (!existing || !SteamManager.Initialized) return;
            inviteCallback = Callback<GameLobbyJoinRequested_t>.Create(invite =>
            {
                PendingInvite = invite.m_steamIDLobby.m_SteamID.ToString();
                Feedback = "已接受 Steam 邀请。请在联机面板确认加入房间；当前房间会先保存退出。";
                OpenPanel();
                MultiplayerRoomDialog.SetInvitation(PendingInvite);
            });
        }

        internal void OpenPanel()
        {
            try
            {
                EnsureEntry();
                if (nativeTemplate == null || nativeFont == null) throw new InvalidOperationException("原生 UI 尚未加载按钮或中文字体，请进入主菜单后重试。");
                MultiplayerRoomDialog.Open(nativeTemplate, nativeFont);
            }
            catch (Exception error) { ShowError("无法打开联机面板。", error); }
        }

        private void CreateSession(string nickname)
        {
            if (Session != null) throw new InvalidOperationException("旧会话尚未完全退出。");
            LocalIdentity identity = IdentityStore.LoadOrCreate(StorageRoot, nickname);
            Transport = new RoomTransport();
            Bridge = new GameBridge();
            Session = new MultiplayerSession(Transport, Bridge, StorageRoot, identity);
            Session.StatusChanged += StatusChanged;
            Session.CommandProjectionRequired += ProjectCommandResult;
            Session.CommandCompleted += CommandCompleted;
            tickFailed = false;
        }
        private void StatusChanged(string message) { Feedback = message; }
        private void ProjectCommandResult(CommandResult result)
        {
            // These are real native fleet/map callbacks, not optional UI notices.
            // Their failures are handled fail-closed by the Session before any
            // success feedback is published through CommandCompleted.
            if (Bridge != null) Bridge.ObserveFleetCommandResult(result);
            if (Bridge != null) Bridge.ObserveTravelCommandResult(result);
        }
        private void CommandCompleted(CommandResult result)
        {
            Feedback = !result.Success ? "操作未完成：" + result.Error : result.Saved ? "操作完成，已保存。" : result.Applied ? "操作已执行，等待下一次统一保存。" : "操作已提交，等待房主执行回执。";
        }
        internal void ReportUiFailure(string message) { Feedback = message; }
        internal string GetRecoveryCode(string nickname)
        {
            if (Session != null) return Session.GetLocalRecoveryCode();
            LocalIdentity local = IdentityStore.LoadOrCreate(StorageRoot, nickname);
            return Newtonsoft.Json.JsonConvert.SerializeObject(new { local.CredentialId, Verifier = CryptoData.Hash(local.Secret) });
        }
        internal void HostNew(string nickname, WorldOptions world, TransportOptions transport)
        {
            QueueStart(nickname, session => session.HostNew(world, transport), "新建联机世界失败。");
        }
        internal void HostLoad(string nickname, string worldId, TransportOptions transport)
        {
            QueueStart(nickname, session => session.HostLoad(worldId, transport), "续开联机世界失败。");
        }
        internal void Join(string nickname, TransportOptions transport)
        {
            QueueStart(nickname, session => { session.Join(transport); PendingInvite = null; }, "加入房间失败。");
        }
        private void QueueStart(string nickname, Action<MultiplayerSession> start, string failure)
        {
            Perform(() =>
            {
                if (awaitingMenuReturn || nativeReturnQueued) throw new InvalidOperationException("正在退出联机房间并返回主界面，请稍后重新加入。");
                if (IsStarting) { Feedback = "联机会话正在创建，请等待当前创建完成。"; MultiplayerRoomDialog.RefreshOpenState(); return; }
                if (awaitingClose) throw new InvalidOperationException("正在保存退出旧房间，请等待完成。");
                Action create = () =>
                {
                    starting = true; startupFailureContext = failure;
                    Feedback = "正在准备联机会话，请等待……";
                    MultiplayerRoomDialog.RefreshOpenState();
                    try
                    {
                        CreateSession(nickname); start(Session);
                        awaitingStartupCompletion = Session.IsStarting;
                    }
                    catch (Exception original)
                    {
                        try { FinalizeSession(); }
                        catch (Exception cleanup) { Logger.LogErrorEngine("[FreeWorldMultiplayer][startup-cleanup] 原始失败=" + original + "\n清理失败=" + cleanup); }
                        throw; // Cleanup must not replace the original factory/serializer exception.
                    }
                    finally
                    {
                        starting = false;
                        try { MultiplayerRoomDialog.RefreshOpenState(); }
                        catch (Exception uiError) { Logger.LogErrorEngine("[FreeWorldMultiplayer][startup-ui] 刷新创建状态失败：" + uiError); }
                    }
                };
                if (Session == null) create();
                else { pendingStart = create; CloseSession(); }
            }, failure);
        }
        internal void Submit(GameCommand command)
        {
            Perform(() =>
            {
                EnsureStartupFinished();
                if (Session == null || !Session.IsConnected || tickFailed) throw new InvalidOperationException("当前没有可操作的联机会话。");
                string immediate = CommandSubmitStatus.ImmediateFeedback(Session.Submit(command));
                if (immediate != null) Feedback = immediate;
            }, "提交操作失败。");
        }
        private void EnsureStartupFinished() { if (IsStarting) throw new InvalidOperationException("正在创建联机世界，请等待创建完成。"); }
        internal void Save() { Perform(() => { EnsureStartupFinished(); if (Session == null) throw new InvalidOperationException("未开房。"); Session.Save(); }, "联机保存失败。"); }
        internal void SetClock(bool paused, double speed) { Perform(() => { EnsureStartupFinished(); if (Session == null) throw new InvalidOperationException("未开房。"); Session.SetClock(paused, speed); }, "调整世界时钟失败。"); }
        internal void Invite() { Perform(() => { EnsureStartupFinished(); if (Transport == null) throw new InvalidOperationException("未开房。"); Transport.InviteFriends(); }, "邀请好友失败。"); }
        internal void CloseSession()
        {
            var closing = Session;
            if (closing == null) return;
            // The session owns final checkpoint collection, full-generation save and bridge restoration.
            awaitingClose = true;
            // An explicit shutdown still needs its bounded network/save stages after a gameplay fault.
            tickFailed = false;
            closing.Disconnect();
            if (!closing.IsConnected && !closing.IsStarting)
            {
                Action next = pendingStart; pendingStart = null;
                FinalizeSession(); if (next != null && !awaitingMenuReturn) next();
            }
        }
        internal bool TryQueueNativeReturn(callbackInt callback)
        {
            if (quitting || unloading || Session == null) return false;
            // Preserve the native completion callback while deferring its entire return path.
            // Repeated menu clicks share one disconnect/save rather than clearing the world early.
            if (callback != null) nativeReturnCallback += callback;
            nativeReturnQueued = true; pendingStart = null;
            Feedback = "正在退出联机房间并返回主界面。";
            Perform(CloseSession, "退出房间并返回主界面失败。");
            return true;
        }
        private void FinalizeSession()
        {
            ResetFleetInterceptUi();
            var closing = Session;
            if (closing == null)
            {
                try { if (Transport != null) Transport.Dispose(); }
                finally { Bridge = null; Transport = null; awaitingClose = false; awaitingStartupCompletion = false; }
                return;
            }
            if (closing.IsStarting)
            {
                // The native factory worker still uses the seeding UID scope and Harmony hooks.
                // Request cancellation; keep polling until it has returned to the main thread.
                closing.Disconnect(); awaitingClose = true; return;
            }
            closing.StatusChanged -= StatusChanged;
            closing.CommandProjectionRequired -= ProjectCommandResult;
            closing.CommandCompleted -= CommandCompleted;
            bool hostLeft = closing.HostLeft && !quitting && !unloading;
            try
            {
                closing.Dispose();
                Feedback = !String.IsNullOrWhiteSpace(closing.LastError) ? "已退出联机房间。" + closing.LastError : closing.Status;
            }
            finally
            {
                Session = null; Bridge = null; Transport = null; tickFailed = false; awaitingClose = false; awaitingStartupCompletion = false;
                if (hostLeft || nativeReturnQueued)
                {
                    // CompleteDisconnect/Dispose has already released the transport and native
                    // world bindings. Only now may the game's own return-to-cover cleanup run.
                    pendingStart = null;
                    nativeReturnQueued = false; showHostLeftNotice = hostLeft;
                    awaitingMenuReturn = true; menuReturnRequested = false; menuReturnCover = null;
                }
            }
        }
        private void CompleteMenuReturn()
        {
            if (quitting || unloading) { CancelMenuReturn(); return; }
            try
            {
                if (!menuReturnRequested)
                {
                    menuReturnRequested = true;
                    Feedback = showHostLeftNotice ? "房主已经离开游戏，正在返回主界面。" : "正在返回主界面。";
                    MultiplayerRoomDialog.CloseOpen();
                    // Same native path as MenuDialog's direct return. It clears script/battle,
                    // network and scene state without writing this guest world to a solo save.
                    callbackInt completed = nativeReturnCallback; nativeReturnCallback = null;
                    NetSender.getInstance().backCover(completed);
                    return;
                }
                ViewCover cover = ViewCover.Instance;
                GameObject uiRoot = UIDispatcher.getUIRoot();
                if (UIUntils.isEnterCovering || !UIUntils.isInScene(UIUntils.SCENE_LIST.SCENE_COVER)
                    || cover == null || !cover.isActiveAndEnabled || !cover.gameObject.scene.isLoaded
                    || UIDispatcher.Canvas == null || !UIDispatcher.Canvas.isActiveAndEnabled
                    || uiRoot == null || !uiRoot.activeInHierarchy || !cover.transform.IsChildOf(uiRoot.transform))
                { menuReturnCover = null; return; }
                if (menuReturnCover != cover)
                {
                    // A live cover for one full frame ensures ViewCover.Start and its native
                    // menu/user cleanup have finished before registering the notification.
                    menuReturnCover = cover; menuReturnReadyFrame = Time.frameCount; return;
                }
                if (Time.frameCount <= menuReturnReadyFrame) return;
                bool notify = showHostLeftNotice;
                CancelMenuReturn();
                if (notify)
                {
                    FairyUIDispatcher.showTip("房主已经离开游戏", "确定", null, null);
                    Feedback = "房主已经离开游戏。";
                }
            }
            catch (Exception error)
            {
                CancelMenuReturn();
                ShowError("退出联机后返回主界面失败。", error);
            }
        }
        private void CancelMenuReturn()
        {
            awaitingMenuReturn = false; nativeReturnQueued = false; showHostLeftNotice = false;
            menuReturnRequested = false; menuReturnCover = null; nativeReturnCallback = null;
        }
        internal void Leave() { Perform(CloseSession, "退出房间失败，不能宣称本轮进度已经保存。"); }
        private void Perform(Action action, string context)
        {
            try { action(); }
            catch (Exception error) { ShowError(context, error); }
        }
        private void ShowError(string context, Exception error)
        {
            Feedback = context + "\n" + error;
            string signature = context + error.GetType().Name + error.Message;
            if (signature == lastLoggedError) return;
            lastLoggedError = signature;
            Logger.LogErrorEngine("[FreeWorldMultiplayer] " + context + "\n" + error);
        }
        private void OnApplicationQuit()
        {
            quitting = true;
            CancelMenuReturn();
            pendingStart = null;
            try { CloseSession(); FinalizeSession(); }
            catch (Exception error) { ShowError("退出时联机保存失败。", error); }
        }
        private void OnDestroy()
        {
            if (Instance != this) return;
            unloading = true;
            CancelMenuReturn();
            pendingStart = null;
            if (!quitting) { try { CloseSession(); FinalizeSession(); } catch (Exception error) { ShowError("卸载联机 MOD 失败。", error); } }
            if (inviteCallback != null) { inviteCallback.Dispose(); inviteCallback = null; }
            MultiplayerRoomDialog.CloseOpen();
            DirectLaunchHooks.Uninstall();
            if (entryButton != null) UnityEngine.Object.Destroy(entryButton.gameObject);
            Instance = null;
        }
    }
}
