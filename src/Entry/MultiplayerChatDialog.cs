using System;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FreeWorldMultiplayer
{
    internal sealed class MultiplayerChatDialog : BaseMonoDialog
    {
        private static MultiplayerChatDialog open, constructing;
        [SerializeField] private string targetPlayerId;
        private string sessionId;
        private bool built, dirty = true;
        private double nextRefresh;
        private TMP_Text heading, historyText, visitText, feedbackText;
        private TMP_InputField input;
        private ScrollRect history;
        private LayoutElement historyHeight;
        private Button send, accept, reject, cancel, transport, nextPeer;
        private string feedback;
        private MultiplayerSession Session => MultiplayerRuntime.Instance == null ? null : MultiplayerRuntime.Instance.Session;
        internal static void Open(string target)
        {
            if (open != null) { open.targetPlayerId = target; open.dirty = true; open.transform.SetAsLastSibling(); return; }
            var source = MultiplayerUi.Node("FWM.PlayerChat", null); source.SetActive(false);
            var dialog = source.AddComponent<MultiplayerChatDialog>(); dialog.targetPlayerId = target;
            try { open = UIHelp.getPage<MultiplayerChatDialog>(source, false, true, 0.55f); if (open == null) throw new InvalidOperationException("原生 UI 未创建通讯窗口。"); }
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
        internal static bool IsShowing(string id) => open != null && open.targetPlayerId == id;
        internal static void MarkDirty() { if (open != null) open.dirty = true; }
        internal static void CloseOpen() { if (open != null) open.closeDialog(); }
        public override void initWidget()
        {
            if (built) return; constructing = this;
            base.initWidget(); overlayKeyEvent(KEY_OPTION.REVICE_ALL);
            if (Session == null || !Session.IsConnected) throw new InvalidOperationException("尚未连接联机房间。");
            sessionId = Session.SessionId; ((RectTransform)transform).sizeDelta = new Vector2(760, 740);
            gameObject.AddComponent<Image>().color = new Color32(29, 34, 36, 255);
            TMP_FontAsset font = OfficialModUi.Font; Material material = font.material;
            heading = OfficialModUi.AddWindowFrame(transform, font, material, "玩家通讯");
            gameObject.AddComponent<MultiplayerModalFit>();
            var close = OfficialModUi.CreateClose(transform, () => closeDialog(), font, material);
            MultiplayerUi.Top((RectTransform)close.transform, 694, 18, 42, 42);
            nextPeer = AddButton("NextConversation", "切换会话", 550, 82, 182, 38, NextConversation);
            AddText("PrivateScope", "当前房间一对一通讯", 17, 28, 84, 500, 34);
            history = OfficialModUi.CreateVerticalScroll(transform, "ChatHistory");
            MultiplayerUi.Top((RectTransform)history.transform, 28, 132, 704, 300);
            historyText = AddText("Messages", "暂无消息。", 18, 0, 0, 660, 60, history.content);
            historyText.enableWordWrapping = true; historyText.overflowMode = TextOverflowModes.Overflow;
            historyHeight = historyText.gameObject.AddComponent<LayoutElement>(); historyHeight.preferredHeight = 60;
            input = OfficialModUi.CreateInput(transform, "ChatText", "", 1024, font, material);
            input.lineType = TMP_InputField.LineType.MultiLineNewline; input.textComponent.enableWordWrapping = true;
            MultiplayerUi.Top(input.GetComponent<RectTransform>(), 28, 446, 548, 70);
            send = AddButton("SendChat", "发送", 592, 446, 140, 70, () => { Session.SendPrivateMessage(targetPlayerId, input.text); input.text = ""; dirty = true; });
            visitText = AddText("VisitState", "参观同意后，仅可向对方母舰赠送物资。", 17, 28, 532, 704, 56); visitText.enableWordWrapping = true;
            accept = AddButton("AcceptVisit", "同意参观", 28, 600, 220, 44, () => Decide(true));
            reject = AddButton("RejectVisit", "拒绝", 264, 600, 220, 44, () => Decide(false));
            cancel = AddButton("CancelVisit", "取消申请 / 授权", 500, 600, 232, 44, CancelVisit);
            transport = AddButton("VisitTransport", "打开单向赠送运输", 28, 600, 456, 44, OpenTransport);
            feedbackText = AddText("ChatFeedback", "消息收到回执后显示“已送达”；离线消息不会补发。", 16, 28, 664, 704, 54); feedbackText.enableWordWrapping = true;
            built = true; Refresh();
        }
        private TMP_Text AddText(string name, string text, float size, float x, float y, float width, float height, Transform parent = null)
        {
            var label = MultiplayerUi.Node("FWM." + name, parent ?? transform).AddComponent<TextMeshProUGUI>();
            label.font = OfficialModUi.Font; label.fontSharedMaterial = label.font.material; label.fontSize = size;
            label.enableAutoSizing = false; label.richText = false; label.raycastTarget = false;
            label.alignment = TextAlignmentOptions.TopLeft; label.overflowMode = TextOverflowModes.Ellipsis;
            label.color = new Color32(225, 234, 236, 255); label.text = text;
            MultiplayerUi.Top(label.rectTransform, x, y, width, height); return label;
        }
        private Button AddButton(string name, string text, float x, float y, float width, float height, Action action)
        {
            Button button = MultiplayerUi.CloneButton(OfficialModUi.ButtonTemplate, transform, "FWM." + name, text, () =>
            {
                try { feedback = null; action(); dirty = true; }
                catch (Exception error) { feedback = MultiplayerUi.BriefError(error.Message); MultiplayerRuntime.Instance.ReportUiFailure(error); }
            });
            MultiplayerUi.Top((RectTransform)button.transform, x, y, width, height); return button;
        }
        private PlayerVisitUpdate CurrentVisit()
        {
            return Session.GetPlayerVisits(targetPlayerId).OrderByDescending(x => x.State == PlayerVisitState.Waiting || x.State == PlayerVisitState.Approved)
                .ThenByDescending(x => x.RemainingSeconds).FirstOrDefault();
        }
        private void Decide(bool accepted) { var visit = CurrentVisit(); if (visit != null) Session.DecidePlayerVisit(visit.Request.RequestId, accepted); }
        private void CancelVisit() { var visit = CurrentVisit(); if (visit != null) Session.CancelPlayerVisit(visit.Request.RequestId); }
        private void OpenTransport() { var visit = CurrentVisit(); if (visit != null && visit.Grant != null) MultiplayerRuntime.Instance.OpenVisitTransport(visit.Grant); }
        private void NextConversation()
        {
            var peers = Session.World.Players.Values.Where(x => x.PlayerId != Session.LocalPlayerId).OrderBy(x => x.Nickname).ToList();
            if (peers.Count == 0) return;
            int index = peers.FindIndex(x => x.PlayerId == targetPlayerId); targetPlayerId = peers[(index + 1) % peers.Count].PlayerId; dirty = true;
        }
        private static void Set(TMP_Text label, string text) { if (label.text != text) label.text = text; }
        private void Refresh()
        {
            if (!built) return;
            var session = Session;
            if (session == null || session.SessionId != sessionId || !session.IsConnected) { closeDialog(); return; }
            PlayerRecord target; bool online = targetPlayerId != null && session.World.Players.TryGetValue(targetPlayerId, out target) && target.Online;
            string name = targetPlayerId != null && session.World.Players.ContainsKey(targetPlayerId) ? session.World.Players[targetPlayerId].Nickname : "玩家";
            Set(heading, "与 " + name + " 通讯"); send.interactable = input.interactable = online && !session.IsClosing;
            nextPeer.interactable = session.World.Players.Count > 2;
            if (dirty)
            {
                dirty = false; var text = new StringBuilder();
                foreach (RoomChatMessage message in session.GetRecentChatMessages(targetPlayerId, 40))
                {
                    bool mine = message.SenderPlayerId == session.LocalPlayerId;
                    string state = !mine ? "" : message.Delivery == RoomChatDelivery.Sending ? " · 发送中" : message.Delivery == RoomChatDelivery.Delivered ? " · 已送达" : " · 未确认送达";
                    text.Append(mine ? "我" : name).Append(state).Append('\n').Append(message.Text).Append('\n');
                    if (mine && message.Delivery == RoomChatDelivery.Failed) text.Append(message.Error).Append('\n');
                    text.Append('\n');
                }
                Set(historyText, text.Length == 0 ? "暂无消息。" : text.ToString());
                historyHeight.preferredHeight = Mathf.Max(60, historyText.GetPreferredValues(historyText.text, 660, 0).y + 8);
                LayoutRebuilder.ForceRebuildLayoutImmediate(history.content); history.StopMovement(); history.verticalNormalizedPosition = 0;
            }
            PlayerVisitUpdate visit = CurrentVisit();
            bool waiting = visit != null && visit.State == PlayerVisitState.Waiting && visit.RemainingSeconds > 0;
            bool approved = visit != null && visit.State == PlayerVisitState.Approved && visit.RemainingSeconds > 0;
            bool incoming = visit != null && visit.Request.RecipientPlayerId == session.LocalPlayerId;
            accept.gameObject.SetActive(waiting && incoming); reject.gameObject.SetActive(waiting && incoming);
            cancel.gameObject.SetActive(waiting || approved); transport.gameObject.SetActive(approved && !incoming && visit.Grant != null);
            accept.interactable = reject.interactable = cancel.interactable = transport.interactable = !session.IsClosing;
            Set(visitText, visit == null ? "参观同意后，仅可向对方母舰赠送物资。" : visit.Reason + ((waiting || approved) ? "\n剩余 " + Math.Ceiling(visit.RemainingSeconds) + " 秒" : ""));
            Set(feedbackText, feedback ?? (!online ? "对方已离线，无法发送新消息。" : "消息收到回执后显示“已送达”；离线消息不会补发。"));
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
}
