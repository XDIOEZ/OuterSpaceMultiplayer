using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace FreeWorldMultiplayer
{
    internal sealed class MultiplayerFleetInterceptDialog : BaseMonoDialog
    {
        private static MultiplayerFleetInterceptDialog open, constructing;
        [SerializeField] private string battleId;
        [SerializeField] private string resultText;
        private string sessionId;
        private bool built, submitted;
        private double nextRefresh;
        private TMP_Text description;
        private UnityEngine.UI.Button engage, flee;
        private MultiplayerSession Session => MultiplayerRuntime.Instance == null ? null : MultiplayerRuntime.Instance.Session;

        internal static void OpenResponse(PlayerFleetBattleRecord record) { Open(record.BattleId, null); }
        internal static void OpenResult(string message) { Open(null, message); }
        private static void Open(string id, string message)
        {
            if (open != null && open.battleId == id && open.resultText == message)
            { open.transform.SetAsLastSibling(); return; }
            CloseOpen();
            GameObject source = MultiplayerUi.Node("FWM.FleetIntercept", null); source.SetActive(false);
            var dialog = source.AddComponent<MultiplayerFleetInterceptDialog>(); dialog.battleId = id; dialog.resultText = message;
            try
            {
                open = UIHelp.getPage<MultiplayerFleetInterceptDialog>(source, false, true, 0.55f);
                if (open == null) throw new InvalidOperationException("原生 UI 未创建舰队拦截窗口。");
            }
            catch
            {
                if (constructing != null)
                {
                    var failed = constructing; bool registered = false;
                    foreach (BaseMonoDialog candidate in UIDispatcher.NotifyArrays)
                        if (candidate == failed) { registered = true; break; }
                    if (registered) failed.closeDialog(); else UIDispatcher.destoryPanel(failed.gameObject);
                }
                throw;
            }
            finally { constructing = null; UnityEngine.Object.Destroy(source); }
        }
        internal static void CloseOpen() { if (open != null) open.closeDialog(); }
        public override void initWidget()
        {
            if (built) return; constructing = this;
            base.initWidget(); overlayKeyEvent(KEY_OPTION.REVICE_ALL);
            if (Session == null || !Session.IsConnected || Session.IsClosing) throw new InvalidOperationException("联机房间已关闭。");
            sessionId = Session.SessionId; ((RectTransform)transform).sizeDelta = new Vector2(620, 420);
            gameObject.AddComponent<UnityEngine.UI.Image>().color = new Color32(29, 34, 36, 255);
            TMP_FontAsset font = OfficialModUi.Font;
            TMP_Text heading = OfficialModUi.AddWindowFrame(transform, font, font.material, battleId == null ? "舰队拦截结果" : "舰队遭遇拦截");
            MultiplayerUi.Top(heading.rectTransform, 28, 20, 500, 42);
            var close = OfficialModUi.CreateClose(transform, () => closeDialog(), font, font.material);
            MultiplayerUi.Top((RectTransform)close.transform, 554, 18, 42, 42);
            description = AddText("Description", "", 20, 28, 92, 564, 158);
            if (battleId != null)
            {
                engage = AddAction("迎战", 28, () => Respond(true));
                flee = AddAction("逃跑", 324, () => Respond(false));
                AddText("Reminder", "关闭窗口不会作出选择，可点击右上方的拦截通知重新打开。请及时回应。", 16, 28, 344, 564, 52);
            }
            else
            {
                description.text = resultText;
                AddAction("知道了", 176, () => closeDialog());
            }
            gameObject.AddComponent<MultiplayerModalFit>(); built = true; Refresh();
        }
        private TMP_Text AddText(string name, string text, float size, float x, float y, float width, float height)
        {
            var label = MultiplayerUi.Node("FWM.Intercept" + name, transform).AddComponent<TextMeshProUGUI>();
            label.font = OfficialModUi.Font; label.fontSharedMaterial = label.font.material; label.fontSize = size;
            label.enableAutoSizing = false; label.richText = false; label.raycastTarget = false; label.enableWordWrapping = true;
            label.color = new Color32(225, 234, 236, 255); label.text = text;
            MultiplayerUi.Top(label.rectTransform, x, y, width, height); return label;
        }
        private UnityEngine.UI.Button AddAction(string caption, float x, Action action)
        {
            var button = MultiplayerUi.CloneButton(OfficialModUi.ButtonTemplate, transform, "FWM.InterceptAction", caption, () =>
            {
                try { action(); }
                catch (Exception error)
                {
                    submitted = false;
                    if (engage != null) engage.interactable = true;
                    if (flee != null) flee.interactable = true;
                    if (description != null) description.text = MultiplayerUi.BriefError(error.Message);
                    MultiplayerRuntime.Instance?.ReportUiFailure(error);
                }
            });
            MultiplayerUi.Top((RectTransform)button.transform, x, 274, 268, 48); return button;
        }
        private void Respond(bool accepted)
        {
            if (submitted) return;
            var session = Session;
            PlayerFleetBattleRecord current = session == null ? null : session.GetPendingFleetIntercept();
            if (current == null || current.BattleId != battleId) { closeDialog(); return; }
            session.RespondPlayerFleetIntercept(battleId, accepted);
            submitted = true; Refresh();
        }
        private void Refresh()
        {
            if (!built) return;
            var session = Session;
            if (session == null || !session.IsConnected || session.IsClosing || session.SessionId != sessionId) { closeDialog(); return; }
            if (battleId == null) return;
            PlayerFleetBattleRecord current = session.GetPendingFleetIntercept();
            if (current == null || current.BattleId != battleId) { closeDialog(); return; }
            PlayerRecord attacker;
            string name = session.World.Players.TryGetValue(current.AttackerPlayerId, out attacker) && !String.IsNullOrWhiteSpace(attacker.Nickname)
                ? attacker.Nickname : "敌对玩家";
            string text = submitted ? "正在提交选择，请等待回应。"
                : name + " 的舰队正在拦截你的舰队。\n\n迎战：双方进入舰队战斗。\n逃跑：脱离本次拦截，返回太空继续航行。";
            if (description.text != text) description.text = text;
            engage.interactable = flee.interactable = !submitted;
        }
        public override void uiUpdate()
        {
            if (Time.unscaledTimeAsDouble < nextRefresh) return; nextRefresh = Time.unscaledTimeAsDouble + 0.25;
            try { Refresh(); } catch (Exception error) { MultiplayerRuntime.Instance?.ReportUiFailure(error); closeDialog(); }
        }
        public override bool keyEventESCToClose() { closeDialog(); return true; }
        public override bool triggerKeyboard(KeyCode source, KeyCode execute) { if (execute == KeyCode.Escape) closeDialog(); return true; }
        public override void OnDestroy() { if (open == this) open = null; if (constructing == this) constructing = null; base.OnDestroy(); }
    }

    public sealed partial class MultiplayerRuntime
    {
        private readonly HashSet<string> reportedFleetInterceptResults = new HashSet<string>();
        private MultiplayerSession fleetInterceptSession;
        private UnityEngine.UI.Button fleetInterceptNotice;
        private string announcedFleetIntercept, fleetInterceptResult;
        private double nextFleetInterceptUi;

        private void ResetFleetInterceptUi()
        {
            MultiplayerFleetInterceptDialog.CloseOpen(); reportedFleetInterceptResults.Clear();
            announcedFleetIntercept = fleetInterceptResult = null; fleetInterceptSession = null;
            if (fleetInterceptNotice != null)
            { fleetInterceptNotice.gameObject.SetActive(false); Destroy(fleetInterceptNotice.gameObject); fleetInterceptNotice = null; }
        }
        private void TickFleetInterceptUi()
        {
            try
            {
                if (Session == null || !Session.IsConnected || Session.IsClosing)
                { if (fleetInterceptSession != null) ResetFleetInterceptUi(); return; }
                if (!ReferenceEquals(fleetInterceptSession, Session))
                {
                    ResetFleetInterceptUi(); fleetInterceptSession = Session; nextFleetInterceptUi = 0;
                    foreach (PlayerFleetBattleRecord record in Session.World.FleetBattles.Values)
                        if (record.Stage == PlayerFleetBattleStage.Finished) reportedFleetInterceptResults.Add(record.BattleId);
                }
                if (Time.unscaledTimeAsDouble < nextFleetInterceptUi) return;
                nextFleetInterceptUi = Time.unscaledTimeAsDouble + 0.25;
                foreach (PlayerFleetBattleRecord record in Session.World.FleetBattles.Values)
                {
                    if (record.AttackerPlayerId != Session.LocalPlayerId && record.DefenderPlayerId != Session.LocalPlayerId) continue;
                    // Detect a newly received terminal record even if the opponent replied
                    // before this UI poll ever saw its AwaitingResponse state.
                    if (record.Stage == PlayerFleetBattleStage.Finished && reportedFleetInterceptResults.Add(record.BattleId)
                        && !String.IsNullOrWhiteSpace(record.Error)) fleetInterceptResult = record.Error;
                }
                PlayerFleetBattleRecord pending = Session.GetPendingFleetIntercept();
                if (pending == null) announcedFleetIntercept = null;
                if (UIDispatcher.Canvas == null) return;
                if (pending != null && announcedFleetIntercept != pending.BattleId)
                {
                    MultiplayerFleetInterceptDialog.OpenResponse(pending); announcedFleetIntercept = pending.BattleId;
                }
                string caption = pending != null ? "舰队遭遇拦截 · 点击处理"
                    : fleetInterceptResult != null ? "舰队拦截：" + fleetInterceptResult + " · 点击查看" : null;
                if (caption == null) { if (fleetInterceptNotice != null) fleetInterceptNotice.gameObject.SetActive(false); return; }
                if (fleetInterceptNotice == null)
                {
                    fleetInterceptNotice = MultiplayerUi.CloneButton(OfficialModUi.ButtonTemplate, UIDispatcher.Canvas.transform,
                        "FWM.FleetInterceptNotice", "", OpenFleetInterceptNotice);
                    var rect = (RectTransform)fleetInterceptNotice.transform; rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.one;
                    rect.anchoredPosition = new Vector2(-18, -118); rect.sizeDelta = new Vector2(420, 44);
                }
                TMP_Text label = OfficialModUi.Caption(fleetInterceptNotice);
                if (label.text != caption) label.text = caption;
                if (!fleetInterceptNotice.gameObject.activeSelf) fleetInterceptNotice.gameObject.SetActive(true);
                fleetInterceptNotice.transform.SetAsLastSibling();
            }
            catch (Exception error) { nextFleetInterceptUi = Time.unscaledTimeAsDouble + 2; ReportUiFailure(error); }
        }
        private void OpenFleetInterceptNotice()
        {
            try
            {
                if (Session == null || !Session.IsConnected || Session.IsClosing) return;
                PlayerFleetBattleRecord pending = Session.GetPendingFleetIntercept();
                if (pending != null) MultiplayerFleetInterceptDialog.OpenResponse(pending);
                else if (fleetInterceptResult != null)
                {
                    MultiplayerFleetInterceptDialog.OpenResult(fleetInterceptResult); fleetInterceptResult = null;
                    if (fleetInterceptNotice != null) fleetInterceptNotice.gameObject.SetActive(false);
                }
            }
            catch (Exception error) { ReportUiFailure(error); }
        }
    }
}
