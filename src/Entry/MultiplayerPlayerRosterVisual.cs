using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FreeWorldMultiplayer
{
    // Native, scrollable player selection. Rows only change the dialog's
    // selected target; the existing relation commands and authority checks
    // remain the sole way to change another player's relationship.
    public sealed partial class MultiplayerRoomDialog
    {
        private sealed class PlayerRosterRow
        {
            internal string PlayerId, DisplayName;
            internal Button Button;
            internal TMP_Text Name, Details;
        }

        private readonly List<PlayerRosterRow> playerRosterRows = new List<PlayerRosterRow>();
        private readonly Dictionary<string, RelationshipRecord> playerRosterRelations =
            new Dictionary<string, RelationshipRecord>(StringComparer.Ordinal);
        private ScrollRect playerRosterScroll;
        private TMP_Text playerRosterEmpty, playerRosterHeading;
        private string playerRosterSessionId, lastRosterSelection;

        private void InstallPlayerRoster()
        {
            Transform root = pages[(int)Page.World].transform;
            playerRosterHeading = Label(root, "PlayerRosterHeading",
                "世界玩家  ·  点击选择要查看的玩家", 17, 0, 432, 984, 23);
            playerRosterHeading.color = MutedColor;

            playerRosterScroll = OfficialModUi.CreateVerticalScroll(root, "PlayerRoster");
            MultiplayerUi.Top((RectTransform)playerRosterScroll.transform, 0, 459, 984, 101);
            playerRosterEmpty = Label(root, "PlayerRosterEmpty",
                "暂无其他玩家。好友加入后会显示在这里。", 18, 22, 482, 910, 52);
        }

        private static string RosterName(PlayerRecord player)
        {
            return String.IsNullOrWhiteSpace(player.Nickname) ? "未命名玩家" : player.Nickname;
        }

        private void ClearPlayerRoster()
        {
            foreach (PlayerRosterRow row in playerRosterRows)
            {
                captions.Remove(row.Button);
                disabledCaptions.Remove(row.Button);
                selectionLines.Remove(row.Button);
                rowHighlights.Remove(row.Button);
            }
            playerRosterRows.Clear();
            foreach (Transform child in playerRosterScroll.content)
            {
                child.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(child.gameObject);
            }
            lastRosterSelection = null;
        }

        private void RebuildPlayerRoster(List<PlayerRecord> players, bool newSession)
        {
            float previousScroll = playerRosterScroll.verticalNormalizedPosition;
            GameObject focused = EventSystem.current == null ? null : EventSystem.current.currentSelectedGameObject;
            string focusedPlayerId = null;
            foreach (PlayerRosterRow row in playerRosterRows)
                if (row.Button != null && row.Button.gameObject == focused)
                {
                    focusedPlayerId = row.PlayerId;
                    break;
                }
            ClearPlayerRoster();
            foreach (PlayerRecord player in players)
            {
                string id = player.PlayerId;
                Button button = Button(playerRosterScroll.content, "PlayerRosterRow", "", 0, 0, 920, 44,
                    () => SelectRosterPlayer(id));
                captions.Remove(button); // Native caption is blank; dedicated labels carry the text.
                LayoutElement layout = button.GetComponent<LayoutElement>() ??
                    button.gameObject.AddComponent<LayoutElement>();
                layout.ignoreLayout = false;
                layout.minHeight = layout.preferredHeight = 44;
                layout.flexibleHeight = 0;

                TMP_Text name = Label(button.transform, "PlayerRosterName", RosterName(player),
                    18, 16, 1, 480, 42);
                TMP_Text details = Label(button.transform, "PlayerRosterDetails", "",
                    16, 510, 1, 402, 42);
                details.alignment = TextAlignmentOptions.MidlineRight;
                playerRosterRows.Add(new PlayerRosterRow
                {
                    PlayerId = id, DisplayName = RosterName(player),
                    Button = button, Name = name, Details = details
                });
            }
            LayoutRebuilder.ForceRebuildLayoutImmediate(playerRosterScroll.content);
            playerRosterScroll.StopMovement();
            playerRosterScroll.verticalNormalizedPosition = newSession ? 1 : previousScroll;
            if (focusedPlayerId != null && !newSession && EventSystem.current != null)
                foreach (PlayerRosterRow row in playerRosterRows)
                    if (row.PlayerId == focusedPlayerId)
                    {
                        EventSystem.current.SetSelectedGameObject(row.Button.gameObject);
                        break;
                    }
        }

        private string PlayerRosterRelation(string localPlayerId,
            string remotePlayerId, out Color color)
        {
            playerRosterRelations.TryGetValue(remotePlayerId, out RelationshipRecord pair);
            Relation relation = pair == null ? Relation.Neutral : pair.Relation;
            if (relation == Relation.Friendly) { color = PositiveColor; return "友好"; }
            if (relation == Relation.Hostile) { color = WarningColor; return "敌对"; }
            bool mine = pair != null && (pair.PlayerA == localPlayerId ?
                pair.FriendlyConsentA : pair.FriendlyConsentB);
            bool theirs = pair != null && (pair.PlayerA == localPlayerId ?
                pair.FriendlyConsentB : pair.FriendlyConsentA);
            color = mine || theirs ? AccentColor : MutedColor;
            return mine ? "已申请友好" : theirs ? "待我同意" : "中立";
        }

        private void RefreshPlayerRoster()
        {
            if (playerRosterScroll == null || page != Page.World || Runtime == null) return;
            MultiplayerSession session = Runtime.Session;
            WorldState world = session == null ? null : session.World;
            if (!Connected || Busy || world == null)
            {
                if (playerRosterRows.Count != 0) ClearPlayerRoster();
                playerRosterRelations.Clear();
                playerRosterSessionId = null;
                playerRosterEmpty.gameObject.SetActive(true);
                SetTextIfChanged(playerRosterEmpty, Busy ? "连接状态处理中…" : "尚未连接联机世界。");
                return;
            }

            List<PlayerRecord> players = world.Players.Values
                .Where(p => p.PlayerId != session.LocalPlayerId)
                .OrderBy(p => p.Nickname ?? String.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.PlayerId, StringComparer.Ordinal).ToList();
            bool newSession = playerRosterSessionId != session.SessionId;
            bool rebuild = newSession || players.Count != playerRosterRows.Count;
            if (!rebuild)
                for (int i = 0; i < players.Count; i++)
                    if (playerRosterRows[i].PlayerId != players[i].PlayerId ||
                        playerRosterRows[i].DisplayName != RosterName(players[i]))
                    { rebuild = true; break; }

            if (rebuild)
            {
                playerRosterSessionId = session.SessionId;
                RebuildPlayerRoster(players, newSession);
            }

            playerRosterRelations.Clear();
            foreach (RelationshipRecord relation in world.Relationships)
            {
                if (relation.PlayerA == session.LocalPlayerId && !String.IsNullOrEmpty(relation.PlayerB))
                    playerRosterRelations[relation.PlayerB] = relation;
                else if (relation.PlayerB == session.LocalPlayerId && !String.IsNullOrEmpty(relation.PlayerA))
                    playerRosterRelations[relation.PlayerA] = relation;
            }
            playerRosterEmpty.gameObject.SetActive(players.Count == 0);
            if (players.Count == 0)
                SetTextIfChanged(playerRosterEmpty, "暂无其他玩家。好友加入后会显示在这里。");
            SetTextIfChanged(playerRosterHeading, "世界玩家（" + players.Count + "）· 点击选择");
            for (int i = 0; i < players.Count; i++)
            {
                PlayerRecord player = players[i];
                PlayerRosterRow row = playerRosterRows[i];
                bool selected = row.PlayerId == selectedPlayer;
                Color relationColor;
                string relationship = PlayerRosterRelation(session.LocalPlayerId,
                    row.PlayerId, out relationColor);
                SetTextIfChanged(row.Details, (player.Online ? "在线" : "离线") + "  ·  " + relationship);
                SetColorIfChanged(row.Name, selected ? HeadingColor : player.Online ? BodyColor : MutedColor);
                SetColorIfChanged(row.Details, relationColor);
                SetSelectedVisual(row.Button, selected);
                RowVisual visual = RowHighlight(row.Button);
                SetColorIfChanged(visual.Highlight, selected ? new Color32(214, 187, 125, 45)
                    : visual.Hover.Hovered ? new Color32(161, 190, 194, 28) : Color.clear);
            }

            // Reveal a newly selected player once; do not fight mouse-wheel
            // scrolling while the selection remains unchanged.
            if (lastRosterSelection != selectedPlayer)
            {
                lastRosterSelection = selectedPlayer;
                foreach (PlayerRosterRow row in playerRosterRows)
                    if (row.PlayerId == selectedPlayer)
                    {
                        RevealScrollRow(playerRosterScroll, row.Button.transform as RectTransform);
                        break;
                    }
            }
        }

        private void SelectRosterPlayer(string playerId)
        {
            MultiplayerSession session = Runtime == null ? null : Runtime.Session;
            if (!Connected || Busy || session == null || session.World == null ||
                playerId == session.LocalPlayerId || !session.World.Players.ContainsKey(playerId)) return;
            if (selectedPlayer == playerId) return;
            selectedPlayer = playerId;
            armedRecoveryPlayer = null;
            armedRecoveryCode = null;
            if (rebindButton != null) SetCaption(rebindButton, "确认重绑离线玩家");
        }

        private bool IsRosterButton(Button button)
        {
            foreach (PlayerRosterRow row in playerRosterRows)
                if (row.Button == button) return true;
            return false;
        }

        private bool NavigatePlayerRoster(Button button, KeyCode key)
        {
            if (page != Page.World || !Connected || Busy || button == null) return false;
            int index = playerRosterRows.FindIndex(row => row.Button == button);
            if (index < 0) return false;
            int next = index;
            switch (key)
            {
                case KeyCode.UpArrow: next = Mathf.Max(0, index - 1); break;
                case KeyCode.DownArrow: next = Mathf.Min(playerRosterRows.Count - 1, index + 1); break;
                case KeyCode.Home: next = 0; break;
                case KeyCode.End: next = playerRosterRows.Count - 1; break;
                default: return false;
            }
            if (next == index) return true;
            Button target = playerRosterRows[next].Button;
            target.onClick.Invoke();
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(target.gameObject);
            RevealScrollRow(playerRosterScroll, target.transform as RectTransform);
            return true;
        }
    }
}
