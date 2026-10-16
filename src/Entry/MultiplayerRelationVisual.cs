using System;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FreeWorldMultiplayer
{
    // Relationship cards are a view of the authoritative world state. The
    // existing command handlers still own every relationship mutation.
    public sealed partial class MultiplayerRoomDialog
    {
        private Button relationFriendlyButton, relationNeutralButton, relationHostileButton;
        private Image relationStatusSurface, relationStateDot, relationStateSurface;
        private TMP_Text relationStateText, relationPeerCount;

        private void InstallRelationVisual()
        {
            Transform root = pages[(int)Page.World].transform;
            relationFriendlyButton = RequiredRelationButton(root, "Friendly");
            relationNeutralButton = RequiredRelationButton(root, "Neutral");
            relationHostileButton = RequiredRelationButton(root, "Hostile");

            // Separate the selected player's relationship from the rules below.
            Section(root, "RelationStatusSurface", -12, 105, 1008, 73);
            Transform surface = root.Find("FWM.RelationStatusSurface");
            relationStatusSurface = surface.GetComponent<Image>();
            relationStateDot = MakeDot(surface, "FWM.RelationDot", 22, 31, 10);
            MultiplayerUi.Top(playerStatus.rectTransform, 44, 110, 726, 56);
            SetColorIfChanged(playerStatus, BodyColor);

            GameObject state = MultiplayerUi.Node("FWM.RelationStateBadge", root);
            relationStateSurface = state.AddComponent<Image>();
            relationStateSurface.raycastTarget = false;
            MultiplayerUi.Top((RectTransform)state.transform, 783, 121, 201, 38);
            relationStateText = Label(state.transform, "RelationStateLabel", "请选择玩家", 16, 9, 0, 183, 38);
            relationStateText.alignment = TextAlignmentOptions.Center;

            relationPeerCount = Label(root, "RelationshipPeerCount", "", 16, 650, 0, 334, 34);
            relationPeerCount.alignment = TextAlignmentOptions.MidlineRight;
            relationPeerCount.color = MutedColor;
        }

        private static Button RequiredRelationButton(Transform root, string name)
        {
            Transform node = root.Find("FWM." + name);
            Button button = node == null ? null : node.GetComponent<Button>();
            if (button == null)
                throw new InvalidOperationException("玩家关系面板缺少按钮：" + name);
            return button;
        }

        private void RefreshRelationVisual()
        {
            if (relationStateText == null || page != Page.World || Runtime == null) return;

            MultiplayerSession session = Runtime.Session;
            WorldState world = session == null ? null : session.World;
            bool available = Connected && !Busy && world != null;
            int peers = available ? world.Players.Values.Count(p => p.PlayerId != session.LocalPlayerId) : 0;
            int online = available ? world.Players.Values.Count(p => p.PlayerId != session.LocalPlayerId && p.Online) : 0;
            SetTextIfChanged(relationPeerCount, available
                ? "其他玩家 " + peers + " · 在线 " + online
                : "尚未连接联机世界");

            PlayerRecord target;
            bool selected = available && !String.IsNullOrEmpty(selectedPlayer)
                && world.Players.TryGetValue(selectedPlayer, out target)
                && selectedPlayer != session.LocalPlayerId;
            if (playerButton != null) playerButton.interactable = available && peers > 0;

            RelationshipRecord pair = selected ? world.Relationships.FirstOrDefault(r =>
                (r.PlayerA == session.LocalPlayerId && r.PlayerB == selectedPlayer)
                || (r.PlayerB == session.LocalPlayerId && r.PlayerA == selectedPlayer)) : null;
            Relation relation = pair == null ? Relation.Neutral : pair.Relation;
            bool mine = pair != null && (pair.PlayerA == session.LocalPlayerId
                ? pair.FriendlyConsentA : pair.FriendlyConsentB);
            bool theirs = pair != null && (pair.PlayerA == session.LocalPlayerId
                ? pair.FriendlyConsentB : pair.FriendlyConsentA);
            bool pending = selected && relation == Relation.Neutral && (mine || theirs);

            // Sending a duplicate consent has no UI action. Neutral remains
            // available while consent is pending so a player can withdraw it.
            relationFriendlyButton.interactable = selected && relation != Relation.Friendly && !mine;
            relationNeutralButton.interactable = selected && (relation != Relation.Neutral || pending);
            relationHostileButton.interactable = selected && relation != Relation.Hostile;
            SetCaption(relationFriendlyButton, !selected ? "请求 / 同意友好"
                : relation == Relation.Friendly ? "已是友好"
                : mine ? "等待对方同意"
                : theirs ? "同意友好" : "请求友好");
            SetCaption(relationNeutralButton, !selected ? "设为中立"
                : pending ? mine ? "撤回友好申请" : "拒绝友好申请"
                : relation == Relation.Neutral ? "已是中立" : "设为中立");
            SetCaption(relationHostileButton, selected && relation == Relation.Hostile
                ? "已是敌对" : "设为敌对");

            string stateText = !selected ? (peers == 0 ? "暂无其他玩家" : "请选择玩家")
                : relation == Relation.Friendly ? "双方友好"
                : relation == Relation.Hostile ? "双方敌对"
                : mine ? "已申请 · 待同意"
                : theirs ? "对方申请友好" : "当前中立";
            Color stateColor = !selected ? MutedColor
                : relation == Relation.Friendly ? PositiveColor
                : relation == Relation.Hostile ? WarningColor
                : pending ? AccentColor : MutedColor;
            SetTextIfChanged(relationStateText, stateText);
            SetColorIfChanged(relationStateText, stateColor);
            SetColorIfChanged(relationStateDot, stateColor);
            SetColorIfChanged(relationStateSurface, pending ? new Color32(73, 63, 43, 240)
                : relation == Relation.Friendly ? new Color32(34, 61, 49, 240)
                : relation == Relation.Hostile ? new Color32(70, 40, 39, 240)
                : new Color32(40, 51, 54, 240));
            SetColorIfChanged(relationStatusSurface, pending ? new Color32(42, 41, 32, 218)
                : new Color32(20, 31, 35, 218));
            SetColorIfChanged(playerStatus, selected ? BodyColor : MutedColor);

            SetSelectedVisual(relationFriendlyButton, selected && relation == Relation.Friendly);
            SetSelectedVisual(relationNeutralButton, selected && relation == Relation.Neutral && !pending);
            SetSelectedVisual(relationHostileButton, selected && relation == Relation.Hostile);
        }
    }
}
