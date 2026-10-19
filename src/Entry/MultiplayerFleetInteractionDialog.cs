using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FreeWorldMultiplayer
{
    // The same ExpeditionInfo_Base shell used by ExpeditionInfo_NPC.initBase.
    // No SetNpcBean/event shell/AI admission is invoked for a real player's fleet.
    internal sealed class MultiplayerFleetInteractionDialog : BaseMonoDialog
    {
        private static MultiplayerFleetInteractionDialog open, constructing;
        [SerializeField] private string targetPlayerId;
        [SerializeField] private long targetFleetId;
        private string sessionId;
        private bool built;
        private double nextRefresh;
        private TMP_Text title, description;
        private Button attack, chat, visit;
        private MultiplayerSession Session => MultiplayerRuntime.Instance == null ? null : MultiplayerRuntime.Instance.Session;
        internal static void Open(string playerId, long fleetUid)
        {
            CloseOpen();
            GameObject source = MultiplayerUi.Node("FWM.RemoteFleetInteraction", null); source.SetActive(false);
            var dialog = source.AddComponent<MultiplayerFleetInteractionDialog>(); dialog.targetPlayerId = playerId; dialog.targetFleetId = fleetUid;
            try { open = UIHelp.getPage<MultiplayerFleetInteractionDialog>(source, false, true, 0.2f); if (open == null) throw new InvalidOperationException("原生 UI 未创建舰队交互窗口。"); }
            catch
            {
                if (constructing != null)
                {
                    var failed = constructing; bool registered = false;
                    foreach (BaseMonoDialog candidate in UIDispatcher.NotifyArrays) if (candidate == failed) { registered = true; break; }
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
            base.initWidget(); overlayKeyEvent(KEY_OPTION.REVICE_ALL); setClickOutZoneClose(true);
            if (Session == null || !Session.IsConnected) throw new InvalidOperationException("联机房间已关闭。");
            sessionId = Session.SessionId; ((RectTransform)transform).sizeDelta = new Vector2(318, 636);
            var hit = gameObject.AddComponent<Image>(); hit.color = Color.clear; hit.raycastTarget = true;
            GameObject prefab = UIHelp.getUIPrefab_Galaxy("expedition/ExpeditionInfo_Base");
            if (prefab == null) throw new InvalidOperationException("原生 NPC 信息外壳缺失：expedition/ExpeditionInfo_Base。");
            GameObject staging = MultiplayerUi.Node("FWM.FleetShellStaging", transform); staging.SetActive(false);
            GameObject shell = null;
            try
            {
                shell = UnityEngine.Object.Instantiate(prefab, staging.transform, false); shell.SetActive(false);
                foreach (MonoBehaviour behaviour in shell.GetComponentsInChildren<MonoBehaviour>(true))
                    if (!(behaviour is Graphic) && !(behaviour is Selectable) && !(behaviour is LayoutGroup)
                        && !(behaviour is LayoutElement) && !(behaviour is ContentSizeFitter) && !(behaviour is Mask)
                        && !(behaviour is RectMask2D) && !(behaviour is BaseMeshEffect) && !(behaviour is ScrollRect)
                        && !(behaviour is UnityEngine.UI.ProceduralImage.ProceduralImageModifier))
                        UnityEngine.Object.DestroyImmediate(behaviour);
                foreach (Graphic graphic in shell.GetComponentsInChildren<Graphic>(true)) graphic.raycastTarget = false;
                foreach (Selectable control in shell.GetComponentsInChildren<Selectable>(true)) control.gameObject.SetActive(false);
                Transform heading = shell.transform.Find("Title/Title/Title");
                if (heading == null || (title = heading.GetComponent<TMP_Text>()) == null)
                    throw new InvalidOperationException("原生 NPC 信息外壳结构不匹配：Title/Title/Title。");
                foreach (string path in new[] { "Desc", "Status", "Title/Type" })
                { Transform child = shell.transform.Find(path); if (child != null) child.gameObject.SetActive(false); }
                Transform texture = shell.transform.Find("BG/Texture"); if (texture != null) texture.gameObject.SetActive(false);
                title.richText = false; shell.transform.SetParent(transform, false);
                MultiplayerUi.Stretch((RectTransform)shell.transform, 0, 0, 0, 0); shell.SetActive(true);
            }
            catch { if (shell != null) UnityEngine.Object.Destroy(shell); throw; }
            finally { UnityEngine.Object.Destroy(staging); }
            description = MultiplayerUi.Node("FWM.RemoteFleetDescription", transform).AddComponent<TextMeshProUGUI>();
            description.font = OfficialModUi.Font; description.fontSharedMaterial = description.font.material;
            description.fontSize = 18; description.richText = false; description.raycastTarget = false; description.enableWordWrapping = true;
            description.color = new Color32(225, 234, 236, 255); MultiplayerUi.Top(description.rectTransform, 20, 130, 278, 280);
            attack = AddAction("攻击", 448, () => { Session.RequestPlayerFleetAttack(targetPlayerId, targetFleetId); Refresh(); });
            chat = AddAction("交流", 502, () => Session.RequestPlayerCommunication(targetPlayerId, targetFleetId));
            visit = AddAction("参观", 556, () => { Session.RequestPlayerVisit(targetPlayerId, targetFleetId); MultiplayerChatDialog.Open(targetPlayerId); closeDialog(); });
            var close = OfficialModUi.CreateClose(transform, () => closeDialog(), OfficialModUi.Font, OfficialModUi.Font.material);
            MultiplayerUi.Top((RectTransform)close.transform, 270, 12, 32, 32);
            gameObject.AddComponent<MultiplayerModalFit>(); built = true; Refresh();
        }
        private Button AddAction(string caption, float y, Action action)
        {
            Button button = MultiplayerUi.CloneButton(OfficialModUi.ButtonTemplate, transform, "FWM.RemoteFleetAction", caption, () =>
            {
                try { action(); }
                catch (Exception error) { MultiplayerRuntime.Instance?.ReportUiFailure(error); if (description != null) description.text = MultiplayerUi.BriefError(error.Message); }
            });
            MultiplayerUi.Top((RectTransform)button.transform, 20, y, 278, 44); return button;
        }
        private void Refresh()
        {
            if (!built) return;
            var session = Session;
            if (session == null || !session.IsConnected || session.SessionId != sessionId) { closeDialog(); return; }
            PlayerRecord target; bool exists = session.World.Players.TryGetValue(targetPlayerId, out target);
            bool valid = exists && target.Online && !session.IsClosing && session.OwnerOfEntity(targetFleetId) == targetPlayerId;
            bool hostile = valid && session.GetRelation(session.LocalPlayerId, targetPlayerId) == Relation.Hostile;
            PlayerRecord local;
            bool locked = session.IsPlayerFleetBattleLocked(targetFleetId)
                || session.World.Players.TryGetValue(session.LocalPlayerId, out local) && session.IsPlayerFleetBattleLocked(local.FleetId);
            PlayerFleetBattleRecord intercept = null;
            foreach (PlayerFleetBattleRecord battle in session.World.FleetBattles.Values)
            {
                if ((battle.AttackerFleetId != targetFleetId && battle.DefenderFleetId != targetFleetId)
                    || battle.AttackerPlayerId != session.LocalPlayerId && battle.DefenderPlayerId != session.LocalPlayerId) continue;
                if (battle.Stage == PlayerFleetBattleStage.Preparing || battle.Stage == PlayerFleetBattleStage.Active
                    || battle.Stage == PlayerFleetBattleStage.Committing) { closeDialog(); return; }
                if (battle.Stage == PlayerFleetBattleStage.AwaitingResponse) intercept = battle;
            }
            attack.interactable = hostile && !locked; chat.interactable = valid; visit.interactable = valid && !locked;
            string heading = exists ? target.Nickname + " 的舰队" : "玩家舰队";
            if (title.text != heading) title.text = heading;
            string text = !valid ? "对方已离线或舰队已离开。" : intercept != null
                ? intercept.AttackerPlayerId == session.LocalPlayerId ? "已发起拦截，正在等待对方选择迎战或逃跑。" : "舰队遭遇拦截，请在拦截通知中选择迎战或逃跑。"
                : locked ? "舰队正在准备或进行战斗。可交流，不能重复攻击或赠送。"
                : hostile ? "双方已敌对，可以发起舰队拦截。\n对方将收到通知，并选择迎战或逃跑。\n\n交流：发送房间内私聊。\n\n参观：对方同意后，从你的母舰向对方母舰赠送物资。"
                : "中立或友好关系不能攻击。\n\n交流：发送房间内私聊。\n\n参观：对方同意后，从你的母舰向对方母舰赠送物资。";
            if (description.text != text) description.text = text;
        }
        public override void uiUpdate() { if (Time.unscaledTimeAsDouble >= nextRefresh) { nextRefresh = Time.unscaledTimeAsDouble + 0.25; Refresh(); } }
        public override bool keyEventESCToClose() { closeDialog(); return true; }
        public override bool triggerKeyboard(KeyCode source, KeyCode execute) { if (execute == KeyCode.Escape) closeDialog(); return true; }
        public override void OnDestroy() { if (open == this) open = null; if (constructing == this) constructing = null; base.OnDestroy(); }
    }
}
