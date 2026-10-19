using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace FreeWorldMultiplayer
{
    public sealed partial class MultiplayerRoomDialog
    {
        private sealed class PlayerDropdownRow
        {
            internal string PlayerId;
            internal UnityEngine.UI.Button Button;
            internal TMP_Text Caption;
        }

        private GameObject playerDropdown;
        private RectTransform playerDropdownPanel;
        private UnityEngine.UI.ScrollRect playerDropdownScroll;
        private string playerDropdownSessionId;
        private readonly List<PlayerDropdownRow> playerDropdownRows = new List<PlayerDropdownRow>();
        private readonly Vector3[] playerDropdownCorners = new Vector3[4];

        private void InstallPlayerDropdown()
        {
            // Replace only the relationship selector. Credential recovery keeps
            // its existing NextPlayer action and shares the selected PlayerId.
            playerButton.onClick.RemoveAllListeners();
            playerButton.onClick.AddListener(() =>
            {
                Safe(TogglePlayerDropdown);
                if (this != null && built) RefreshState();
            });
        }

        private void TogglePlayerDropdown()
        {
            if (ClosePlayerDropdown()) return;
            MultiplayerSession session = RequireSession();
            if (Busy || page != Page.World) return;
            if (!session.World.Players.Values.Any(p => p.PlayerId != session.LocalPlayerId)) return;
            if (playerDropdown == null) BuildPlayerDropdown();
            playerDropdownSessionId = session.SessionId;
            playerDropdown.transform.SetAsLastSibling();
            playerDropdown.SetActive(true);
            RefreshPlayerDropdown();
            UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(playerDropdownScroll.content);
            playerDropdownScroll.StopMovement();
            playerDropdownScroll.verticalNormalizedPosition = 1;
            PlayerDropdownRow focus = playerDropdownRows.FirstOrDefault(row => row.PlayerId == selectedPlayer)
                ?? playerDropdownRows.FirstOrDefault();
            if (focus != null)
            {
                if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(focus.Button.gameObject);
                RevealScrollRow(playerDropdownScroll, focus.Button.transform as RectTransform);
            }
        }

        private void BuildPlayerDropdown()
        {
            playerDropdown = MultiplayerUi.Node("FWM.PlayerDropdown", pages[(int)Page.World].transform);
            playerDropdown.SetActive(false);
            // Cover this dialog's controls so a click outside the list only
            // dismisses the dropdown, like a normal modal dropdown blocker.
            MultiplayerUi.Top((RectTransform)playerDropdown.transform, -28, -140, 1040, 820);
            var blockerImage = playerDropdown.AddComponent<UnityEngine.UI.Image>();
            blockerImage.color = Color.clear;
            blockerImage.raycastTarget = true;
            var blocker = playerDropdown.AddComponent<UnityEngine.UI.Button>();
            blocker.targetGraphic = blockerImage;
            blocker.transition = UnityEngine.UI.Selectable.Transition.None;
            blocker.navigation = new UnityEngine.UI.Navigation { mode = UnityEngine.UI.Navigation.Mode.None };
            blocker.onClick.AddListener(() => { ClosePlayerDropdown(); RefreshState(); });
            playerDropdown.AddComponent<MultiplayerPlayerDropdownLifetime>().Disabled = () => ClosePlayerDropdown();

            GameObject panel = MultiplayerUi.Node("FWM.PlayerDropdownPanel", playerDropdown.transform);
            playerDropdownPanel = (RectTransform)panel.transform;
            var surface = panel.AddComponent<UnityEngine.UI.Image>();
            surface.color = new Color32(29, 34, 36, 255);
            surface.raycastTarget = true;
            var outline = panel.AddComponent<UnityEngine.UI.Outline>();
            outline.effectColor = new Color32(119, 147, 150, 180);
            outline.effectDistance = new Vector2(1, -1);
            playerDropdownScroll = OfficialModUi.CreateVerticalScroll(panel.transform, "PlayerDropdownList");
        }

        private void RefreshPlayerDropdown()
        {
            MultiplayerSession session = Runtime == null ? null : Runtime.Session;
            WorldState world = session == null ? null : session.World;
            bool available = Connected && !Busy && world != null && page == Page.World;
            bool hasPlayers = available && world.Players.Values.Any(p => p.PlayerId != session.LocalPlayerId);
            if (playerButton != null) playerButton.interactable = hasPlayers;
            if (!hasPlayers || (playerDropdown != null && playerDropdown.activeSelf
                && playerDropdownSessionId != session.SessionId)) ClosePlayerDropdown();

            bool expanded = playerDropdown != null && playerDropdown.activeSelf;
            PlayerRecord selected = null;
            if (world != null && !String.IsNullOrEmpty(selectedPlayer)) world.Players.TryGetValue(selectedPlayer, out selected);
            SetCaption(playerButton, (selected == null || selected.PlayerId == session.LocalPlayerId
                ? available && !hasPlayers ? "暂无其他玩家" : "选择玩家"
                : RosterName(selected) + (selected.Online ? "（在线）" : "（离线）"))
                + (expanded ? " ▴" : " ▾"));
            if (!expanded) return;

            List<PlayerRecord> players = world.Players.Values.Where(p => p.PlayerId != session.LocalPlayerId)
                .OrderBy(RosterName, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.PlayerId, StringComparer.Ordinal).ToList();
            if (players.Count == 0) { ClosePlayerDropdown(); return; }
            bool changed = players.Count != playerDropdownRows.Count;
            for (int i = 0; !changed && i < players.Count; i++)
                changed = players[i].PlayerId != playerDropdownRows[i].PlayerId;
            if (changed) RebuildPlayerDropdown(players);
            for (int i = 0; i < players.Count; i++)
            {
                PlayerRecord player = players[i];
                PlayerDropdownRow row = playerDropdownRows[i];
                bool chosen = player.PlayerId == selectedPlayer;
                SetTextIfChanged(row.Caption, RosterName(player) + (player.Online ? "（在线）" : "（离线）"));
                SetColorIfChanged(row.Caption, chosen ? HeadingColor : player.Online ? BodyColor : MutedColor);
                SetSelectedVisual(row.Button, chosen);
            }

            // Only membership/order changes rebuild rows. Online labels and the
            // current selection refresh in place without resetting scrolling.
            ((RectTransform)playerButton.transform).GetWorldCorners(playerDropdownCorners);
            Vector3 bottomLeft = playerDropdown.transform.InverseTransformPoint(playerDropdownCorners[0]);
            float width = ((RectTransform)playerButton.transform).rect.width;
            float height = Mathf.Min(6, players.Count) * 52 + 12;
            MultiplayerUi.Top(playerDropdownPanel, bottomLeft.x, -bottomLeft.y + 4, width, height);
            MultiplayerUi.Stretch((RectTransform)playerDropdownScroll.transform, 4, 4, 4, 4);
        }

        private void RebuildPlayerDropdown(List<PlayerRecord> players)
        {
            float scrollPosition = playerDropdownScroll.verticalNormalizedPosition;
            EventSystem events = EventSystem.current;
            GameObject focused = events == null ? null : events.currentSelectedGameObject;
            string focusedPlayer = null;
            foreach (PlayerDropdownRow row in playerDropdownRows)
            {
                if (row.Button.gameObject == focused) { focusedPlayer = row.PlayerId; events.SetSelectedGameObject(null); }
                selectionLines.Remove(row.Button);
                row.Button.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(row.Button.gameObject);
            }
            playerDropdownRows.Clear();
            foreach (PlayerRecord player in players)
            {
                string id = player.PlayerId;
                var button = MultiplayerUi.CloneButton(ButtonTemplate, playerDropdownScroll.content,
                    "FWM.PlayerDropdownRow", "", () =>
                    {
                        Safe(() => SelectRosterPlayer(id));
                        ClosePlayerDropdown();
                        if (this != null && built) RefreshState();
                    });
                var layout = button.GetComponent<UnityEngine.UI.LayoutElement>()
                    ?? button.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
                layout.ignoreLayout = false;
                layout.minHeight = layout.preferredHeight = 44;
                layout.flexibleHeight = 0;
                playerDropdownRows.Add(new PlayerDropdownRow
                {
                    PlayerId = id, Button = button, Caption = OfficialModUi.Caption(button)
                });
            }
            for (int i = 0; i < playerDropdownRows.Count; i++)
            {
                var navigation = new UnityEngine.UI.Navigation { mode = UnityEngine.UI.Navigation.Mode.Explicit };
                navigation.selectOnUp = playerDropdownRows[Mathf.Max(0, i - 1)].Button;
                navigation.selectOnDown = playerDropdownRows[Mathf.Min(playerDropdownRows.Count - 1, i + 1)].Button;
                playerDropdownRows[i].Button.navigation = navigation;
            }
            UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(playerDropdownScroll.content);
            playerDropdownScroll.StopMovement();
            playerDropdownScroll.verticalNormalizedPosition = scrollPosition;
            if (focusedPlayer != null && events != null)
            {
                PlayerDropdownRow row = playerDropdownRows.FirstOrDefault(item => item.PlayerId == focusedPlayer)
                    ?? playerDropdownRows.FirstOrDefault();
                if (row != null) events.SetSelectedGameObject(row.Button.gameObject);
            }
        }

        internal bool ClosePlayerDropdown()
        {
            if (playerDropdown == null || !playerDropdown.activeSelf) return false;
            EventSystem events = EventSystem.current;
            GameObject focused = events == null ? null : events.currentSelectedGameObject;
            bool restoreFocus = focused != null && focused.transform.IsChildOf(playerDropdown.transform);
            if (restoreFocus) events.SetSelectedGameObject(null);
            playerDropdown.SetActive(false);
            playerDropdownScroll.StopMovement();
            if (restoreFocus && playerButton != null && playerButton.gameObject.activeInHierarchy)
                events.SetSelectedGameObject(playerButton.gameObject);
            return true;
        }
    }

    // Deactivating the World page or closing its dialog must also clear the
    // child's activeSelf flag, so returning to the page never reopens it.
    internal sealed class MultiplayerPlayerDropdownLifetime : MonoBehaviour
    {
        internal Action Disabled;
        private void OnDisable() { if (Disabled != null) Disabled(); }
    }
}
