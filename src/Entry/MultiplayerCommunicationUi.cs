using System;
using System.Linq;
using UnityEngine;

namespace FreeWorldMultiplayer
{
    public sealed partial class MultiplayerRuntime
    {
        private UnityEngine.UI.Button communicationNotice;
        private string communicationNoticePlayer, communicationNoticeText;
        private PlayerVisitGrant openVisitGrant;
        private void AttachCommunicationUi(MultiplayerSession session)
        {
            session.PlayerCommunicationRequested += OpenPlayerChat;
            session.PlayerFleetInteractionRequested += OpenPlayerFleetActions;
            session.ChatChanged += RoomChatChanged;
            session.VisitChanged += RoomVisitChanged;
        }
        private void DetachCommunicationUi(MultiplayerSession session)
        {
            session.PlayerCommunicationRequested -= OpenPlayerChat;
            session.PlayerFleetInteractionRequested -= OpenPlayerFleetActions;
            session.ChatChanged -= RoomChatChanged;
            session.VisitChanged -= RoomVisitChanged;
            openVisitGrant = null; communicationNoticePlayer = communicationNoticeText = null;
            if (communicationNotice != null) { communicationNotice.gameObject.SetActive(false); Destroy(communicationNotice.gameObject); communicationNotice = null; }
            MultiplayerChatDialog.CloseOpen(); MultiplayerFleetInteractionDialog.CloseOpen();
        }
        private string CommunicationName(string id)
        {
            PlayerRecord player;
            return Session != null && Session.World != null && id != null && Session.World.Players.TryGetValue(id, out player)
                ? String.IsNullOrWhiteSpace(player.Nickname) ? "未命名玩家" : player.Nickname : "玩家";
        }
        private void OpenPlayerChat(string playerId, long fleetUid)
        {
            try
            {
                MultiplayerFleetInteractionDialog.CloseOpen(); MultiplayerChatDialog.Open(playerId);
                if (communicationNoticePlayer == playerId) communicationNoticePlayer = communicationNoticeText = null;
            }
            catch (Exception error) { ReportUiFailure(error); }
        }
        private void OpenPlayerFleetActions(string playerId, long fleetUid)
        { try { MultiplayerFleetInteractionDialog.Open(playerId, fleetUid); } catch (Exception error) { ReportUiFailure(error); } }
        private void RoomChatChanged(RoomChatMessage message)
        {
            MultiplayerChatDialog.MarkDirty();
            if (Session == null || message.SenderPlayerId == Session.LocalPlayerId || MultiplayerChatDialog.IsShowing(message.SenderPlayerId)) return;
            communicationNoticePlayer = message.SenderPlayerId;
            communicationNoticeText = CommunicationName(message.SenderPlayerId) + " 发来消息 · 点击回复";
        }
        private void RoomVisitChanged(PlayerVisitUpdate update)
        {
            MultiplayerChatDialog.MarkDirty();
            if (Session == null) return;
            if (update.State == PlayerVisitState.Waiting && update.Request.RecipientPlayerId == Session.LocalPlayerId)
            {
                communicationNoticePlayer = update.Request.RequesterPlayerId;
                communicationNoticeText = CommunicationName(communicationNoticePlayer) + " 请求参观 · 点击处理";
            }
            if (update.State == PlayerVisitState.Approved && update.Request.RequesterPlayerId == Session.LocalPlayerId && update.Grant != null)
                openVisitGrant = update.Grant;
        }
        internal void OpenVisitTransport(PlayerVisitGrant grant)
        {
            if (Session == null || !Session.IsConnected || Session.IsClosing || Bridge == null) throw new InvalidOperationException("房间已关闭，赠送授权不可使用。");
            PlayerVisitUpdate current = Session.GetPlayerVisits(grant.RecipientPlayerId).FirstOrDefault(x => x.Request.RequestId == grant.RequestId);
            if (current == null || current.State != PlayerVisitState.Approved || current.RemainingSeconds <= 0 || current.Grant == null)
                throw new InvalidOperationException("赠送授权已经失效。");
            MultiplayerFleetInteractionDialog.CloseOpen(); MultiplayerChatDialog.CloseOpen();
            BridgeResult result = Bridge.OpenVisitTransport(current.Grant);
            if (result == null || !result.Success) throw new InvalidOperationException(result == null ? "原版运输页未返回结果。" : result.Error);
        }
        private void TickCommunicationUi()
        {
            if (Session == null || !Session.IsConnected || Session.IsClosing)
            { if (communicationNotice != null) communicationNotice.gameObject.SetActive(false); return; }
            if (openVisitGrant != null)
            {
                PlayerVisitGrant grant = openVisitGrant; openVisitGrant = null;
                try { OpenVisitTransport(grant); }
                catch (Exception error) { ReportUiFailure(error); OpenPlayerChat(grant.RecipientPlayerId, grant.RecipientFleetId); }
            }
            if (communicationNoticePlayer == null)
            { if (communicationNotice != null) communicationNotice.gameObject.SetActive(false); return; }
            if (UIDispatcher.Canvas == null) return;
            if (communicationNotice == null)
            {
                communicationNotice = MultiplayerUi.CloneButton(OfficialModUi.ButtonTemplate, UIDispatcher.Canvas.transform,
                    "FWM.CommunicationNotice", "", () => { if (communicationNoticePlayer != null) OpenPlayerChat(communicationNoticePlayer, -1); });
                var rect = (RectTransform)communicationNotice.transform; rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.one;
                rect.anchoredPosition = new Vector2(-18, -68); rect.sizeDelta = new Vector2(420, 44);
            }
            TMPro.TMP_Text label = OfficialModUi.Caption(communicationNotice);
            if (label.text != communicationNoticeText) label.text = communicationNoticeText;
            if (!communicationNotice.gameObject.activeSelf) { communicationNotice.gameObject.SetActive(true); communicationNotice.transform.SetAsLastSibling(); }
        }
    }
}
