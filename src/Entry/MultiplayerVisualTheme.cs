using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FreeWorldMultiplayer
{
    // Modal-local presentation only: never changes the shared Canvas, native prefabs or game rules.
    public sealed partial class MultiplayerRoomDialog
    {
        private static readonly Color32 HeadingColor = new Color32(246, 230, 191, 255);
        private static readonly Color32 BodyColor = new Color32(225, 234, 236, 255);
        private static readonly Color32 MutedColor = new Color32(159, 181, 186, 255);
        private static readonly Color32 AccentColor = new Color32(238, 215, 158, 255);
        private static readonly Color32 PositiveColor = new Color32(140, 207, 173, 255);
        private static readonly Color32 WarningColor = new Color32(235, 163, 143, 255);

        private readonly Dictionary<Button, Image> selectionLines = new Dictionary<Button, Image>();
        private readonly Dictionary<string, Button> clockControls = new Dictionary<string, Button>(StringComparer.Ordinal);
        private GameObject connectionBadge;
        private TMP_Text connectionBadgeText;
        private Image connectionBadgeDot, feedbackDot;

        private void InstallVisualTheme()
        {
            // A quiet footer separates actionable errors and save feedback from page content.
            GameObject feedbackSurface = MultiplayerUi.Node("FWM.FeedbackSurface", transform);
            var surface = feedbackSurface.AddComponent<Image>();
            surface.color = new Color32(21, 30, 34, 240);
            surface.raycastTarget = false;
            MultiplayerUi.Top((RectTransform)feedbackSurface.transform, 28, 726, 984, 70);
            feedbackSurface.transform.SetSiblingIndex(status.transform.GetSiblingIndex());
            feedbackDot = MakeDot(feedbackSurface.transform, "FWM.FeedbackDot", 18, 20, 8);
            MultiplayerUi.Top(status.rectTransform, 52, 739, 944, 50);

            // Room state remains visible while changing pages and never covers the tabs.
            connectionBadge = MultiplayerUi.Node("FWM.ConnectionBadge", transform);
            var badgeBackground = connectionBadge.AddComponent<Image>();
            badgeBackground.color = new Color32(41, 53, 57, 244);
            badgeBackground.raycastTarget = false;
            MultiplayerUi.Top((RectTransform)connectionBadge.transform, 746, 85, 266, 34);
            connectionBadgeDot = MakeDot(connectionBadge.transform, "FWM.ConnectionDot", 14, 13, 8);
            connectionBadgeText = Label(connectionBadge.transform, "ConnectionState", "尚未连接", 16, 32, 0, 222, 34);
            connectionBadgeText.color = MutedColor;
        }

        private static Image MakeDot(Transform parent, string name, float x, float y, float size)
        {
            GameObject dot = MultiplayerUi.Node(name, parent);
            Image image = dot.AddComponent<Image>();
            image.color = MutedColor; image.raycastTarget = false;
            MultiplayerUi.Top((RectTransform)dot.transform, x, y, size, size);
            return image;
        }

        private void SetSelectedVisual(Button button, bool selected)
        {
            if (button == null) return;
            Image line;
            if (!selectionLines.TryGetValue(button, out line) || line == null)
            {
                GameObject underline = MultiplayerUi.Node("FWM.SelectionUnderline", button.transform);
                line = underline.AddComponent<Image>();
                line.raycastTarget = false;
                line.color = AccentColor;
                var rect = (RectTransform)underline.transform;
                rect.anchorMin = new Vector2(0, 0);
                rect.anchorMax = new Vector2(1, 0);
                rect.offsetMin = new Vector2(14, 1);
                rect.offsetMax = new Vector2(-14, 4);
                selectionLines[button] = line;
            }
            line.enabled = selected;
        }

        private void StyleActionButton(Button button, string name)
        {
            if (name == "Pause" || name == "Speed1" || name == "Speed3" || name == "Speed5")
                clockControls[name] = button;
            if (name == "Hostile" || name == "Leave" || name == "RebindCredential")
            {
                TMP_Text caption = OfficialModUi.Caption(button);
                caption.color = WarningColor;
            }
        }

        private void RefreshVisualTheme()
        {
            if (connectionBadge == null || Runtime == null) return;
            connectionBadge.SetActive(page != Page.Settings);
            bool connected = Connected;
            connectionBadgeText.text = connected ? (Runtime.Session.IsHost ? "已连接  ·  房主" : "已连接  ·  客人")
                : Busy ? "正在连接 / 退出" : "离线  ·  尚未加入";
            connectionBadgeDot.color = connected ? PositiveColor : Busy ? AccentColor : MutedColor;
            connectionBadgeText.color = connected ? BodyColor : MutedColor;

            string message = status.text ?? String.Empty;
            bool warning = message.IndexOf("失败", StringComparison.Ordinal) >= 0
                || message.IndexOf("错误", StringComparison.Ordinal) >= 0
                || message.IndexOf("无法", StringComparison.Ordinal) >= 0
                || message.IndexOf("未完成", StringComparison.Ordinal) >= 0
                || message.IndexOf("拒绝", StringComparison.Ordinal) >= 0;
            bool positive = !warning && (message.IndexOf("成功", StringComparison.Ordinal) >= 0
                || message.IndexOf("已保存", StringComparison.Ordinal) >= 0
                || message.IndexOf("已连接", StringComparison.Ordinal) >= 0);
            feedbackDot.color = warning ? WarningColor : positive ? PositiveColor : AccentColor;
            status.color = warning ? WarningColor : BodyColor;

            // Indicate the effective host clock setting, not just the buttons' last click.
            MultiplayerSession session = Runtime.Session;
            foreach (KeyValuePair<string, Button> entry in clockControls)
            {
                bool selected = connected && session != null && session.IsHost
                    && (entry.Key == "Pause" ? session.Paused
                        : !session.Paused && ((entry.Key == "Speed1" && session.Speed == 1)
                            || (entry.Key == "Speed3" && session.Speed == 3)
                            || (entry.Key == "Speed5" && session.Speed == 5)));
                SetSelectedVisual(entry.Value, selected);
                TMP_Text text = captions[entry.Value];
                text.fontStyle = selected ? FontStyles.Bold : FontStyles.Normal;
                text.color = selected ? HeadingColor : BodyColor;
            }

            // Direct-launch has no Steam session; disabled controls communicate that
            // before a user clicks, rather than reporting an avoidable error afterward.
            if (Runtime.DirectLaunch)
            {
                if (steamButton != null) steamButton.interactable = false;
                if (resumeSteamButton != null) resumeSteamButton.interactable = false;
                if (room != null) room.interactable = false;
                foreach (Button button in connectionButtons)
                    if (button != null && button.name == "FWM.JoinSteam") button.interactable = false;
            }
        }
    }
}
