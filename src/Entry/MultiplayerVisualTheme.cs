using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
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
        private static readonly Color32 DisabledColor = new Color32(134, 152, 156, 255);

        private readonly Dictionary<Button, Image> selectionLines = new Dictionary<Button, Image>();
        private readonly Dictionary<string, Button> clockControls = new Dictionary<string, Button>(StringComparer.Ordinal);
        private readonly Dictionary<Button, RowVisual> rowHighlights = new Dictionary<Button, RowVisual>();
        private readonly List<Button> obsoleteRows = new List<Button>();
        private readonly Dictionary<Button, Color> disabledCaptions = new Dictionary<Button, Color>();
        private readonly List<InputBorder> inputBorders = new List<InputBorder>();
        private GameObject connectionBadge;
        private TMP_Text connectionBadgeText, navigationHint;
        private Image connectionBadgeDot, feedbackDot;

        private sealed class InputBorder
        {
            internal TMP_InputField Field;
            internal Outline Outline;
        }

        private sealed class RowVisual
        {
            internal Image Highlight;
            internal MultiplayerRowHover Hover;
        }

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

            // The connection banner belongs to the header, not the navigation row:
            // the latter contains the cancel-connection button when a session is busy.
            connectionBadge = MultiplayerUi.Node("FWM.ConnectionBadge", transform);
            var badgeBackground = connectionBadge.AddComponent<Image>();
            badgeBackground.color = new Color32(41, 53, 57, 244);
            badgeBackground.raycastTarget = false;
            MultiplayerUi.Top((RectTransform)connectionBadge.transform, 578, 24, 210, 34);
            connectionBadgeDot = MakeDot(connectionBadge.transform, "FWM.ConnectionDot", 14, 13, 8);
            connectionBadgeText = Label(connectionBadge.transform, "ConnectionState", "尚未连接", 16, 32, 0, 176, 34);
            connectionBadgeText.color = MutedColor;
            navigationHint = Label(transform, "NavigationHint", "", 15, 512, 84, 276, 34);
            navigationHint.alignment = TextAlignmentOptions.MidlineRight;

            // Focus and invalid-value feedback apply only to cloned MOD inputs.
            foreach (TMP_InputField field in GetComponentsInChildren<TMP_InputField>(true))
            {
                if (field == null || field.targetGraphic == null) continue;
                Graphic graphic = field.targetGraphic;
                Outline border = graphic.GetComponent<Outline>() ?? graphic.gameObject.AddComponent<Outline>();
                border.useGraphicAlpha = false;
                border.effectDistance = new Vector2(1.5f, -1.5f);
                border.effectColor = new Color32(108, 142, 145, 72);
                inputBorders.Add(new InputBorder { Field = field, Outline = border });
            }
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
            bool connected = Connected;
            connectionBadgeText.text = connected ? (Runtime.Session.IsHost ? "已连接  ·  房主" : "已连接  ·  客人")
                : Busy ? "正在连接 / 退出" : "离线  ·  尚未加入";
            connectionBadgeDot.color = connected ? PositiveColor : Busy ? AccentColor : MutedColor;
            connectionBadgeText.color = connected ? BodyColor : MutedColor;
            string location = page == Page.Create
                ? (continueWorldForm != null && continueWorldForm.activeSelf ? "创建 / 继续世界" : "创建 / 新世界")
                : page == Page.Join ? "加入 / 连接方式"
                : page == Page.Room ? "房间 / 世界控制"
                : page == Page.World ? "房间 / 玩家关系" : "设置 / 默认选项";

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

            // Direct launch cannot use Steam; make that clear before the click.
            if (Runtime.DirectLaunch)
            {
                if (steamButton != null) steamButton.interactable = false;
                if (resumeSteamButton != null) resumeSteamButton.interactable = false;
                if (room != null) room.interactable = false;
                foreach (Button button in connectionButtons)
                    if (button != null && button.name == "FWM.JoinSteam") button.interactable = false;
                foreach (Button button in sessionButtons)
                    if (button != null && button.name == "FWM.Invite") button.interactable = false;
            }

            RefreshButtonAvailability();
            bool invalidFocusedInput;
            string inputHint = RefreshInputBorders(out invalidFocusedInput);
            navigationHint.text = inputHint ?? location;
            navigationHint.color = invalidFocusedInput ? WarningColor : MutedColor;
            RefreshRowHighlights();

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
                text.color = !entry.Value.interactable ? DisabledColor : selected ? HeadingColor : BodyColor;
            }
        }

        private void RefreshButtonAvailability()
        {
            // TMP labels are not the native button target graphic. Apply the same
            // disabled affordance to text, restoring its actual template/semantic color.
            foreach (KeyValuePair<Button, TMP_Text> entry in captions)
            {
                Button button = entry.Key;
                if (button == null || entry.Value == null || button.name == "FWM.CustomWorldPreset" || button.name == "FWM.SavedWorld") continue;
                if (!button.interactable)
                {
                    if (!disabledCaptions.ContainsKey(button)) disabledCaptions.Add(button, entry.Value.color);
                    entry.Value.color = DisabledColor;
                }
                else if (disabledCaptions.TryGetValue(button, out Color original))
                {
                    entry.Value.color = original;
                    disabledCaptions.Remove(button);
                }
            }
        }

        private static bool NumericRange(string name, out int minimum, out int maximum, out string label)
        {
            minimum = 0; maximum = 0; label = null;
            switch (name)
            {
                case "FWM.HostPort": case "FWM.ResumePort": case "FWM.JoinPort": case "FWM.SettingsPort":
                    minimum = 1; maximum = 65535; label = "端口"; return true;
                case "FWM.MaxPlayers": case "FWM.SettingsMaxPlayers":
                    minimum = 2; maximum = 64; label = "人数"; return true;
                case "FWM.StartingEmployees": case "FWM.SettingsEmployees":
                    minimum = 1; maximum = 32; label = "雇员"; return true;
                case "FWM.MaxBases": case "FWM.SettingsMaxBases":
                    minimum = 1; maximum = 64; label = "营地"; return true;
                default: return false; // Room IDs are UInt64; do not parse them as Int32.
            }
        }

        private string RefreshInputBorders(out bool invalidFocusedInput)
        {
            invalidFocusedInput = false;
            string focusedHint = null;
            foreach (InputBorder item in inputBorders)
            {
                if (item.Field == null || item.Outline == null || !item.Field.gameObject.activeInHierarchy) continue;
                bool hasRange = NumericRange(item.Field.name, out int min, out int max, out string label);
                bool invalid = hasRange && (!Int32.TryParse(item.Field.text, out int value) || value < min || value > max);
                bool focused = item.Field.interactable && item.Field.isFocused;
                // Invalid values remain visibly invalid even while the field has focus.
                Color32 color = !item.Field.interactable ? new Color32(90, 109, 111, 48)
                    : invalid ? WarningColor
                    : focused ? AccentColor
                    : new Color32(108, 142, 145, 72);
                item.Outline.effectColor = color;
                if (focused && hasRange)
                {
                    focusedHint = invalid ? label + "须为 " + min + "～" + max : label + "范围 " + min + "～" + max;
                    invalidFocusedInput = invalid;
                }
            }
            return focusedHint;
        }

        private RowVisual RowHighlight(Button button)
        {
            if (rowHighlights.TryGetValue(button, out RowVisual visual) && visual.Highlight != null) return visual;
            GameObject node = MultiplayerUi.Node("FWM.RowHighlight", button.transform);
            Image image = node.AddComponent<Image>(); image.raycastTarget = false;
            MultiplayerUi.Stretch((RectTransform)node.transform, 6, 6, 4, 4);
            node.transform.SetAsFirstSibling();
            visual = new RowVisual { Highlight = image, Hover = button.gameObject.AddComponent<MultiplayerRowHover>() };
            rowHighlights[button] = visual;
            return visual;
        }

        private void RefreshRowHighlights()
        {
            // Lists rebuild their native buttons on refresh; discard destroyed rows
            // rather than retaining stale Unity objects across repeated refreshes.
            obsoleteRows.Clear();
            foreach (KeyValuePair<Button, RowVisual> entry in rowHighlights)
                if (entry.Key == null || entry.Value.Highlight == null) obsoleteRows.Add(entry.Key);
            foreach (Button button in obsoleteRows) rowHighlights.Remove(button);

            // The creation, save and room pages are mutually exclusive. Do not
            // dirty graphics for rows in inactive scroll views every UI tick.
            if (saveScroll != null && saveScroll.gameObject.activeInHierarchy)
            {
                foreach (SaveRow row in saveRows)
                {
                    if (row.Button == null) continue;
                    bool invalid = !String.IsNullOrEmpty(row.World.Error);
                    bool selected = row.World.WorldId == selectedWorldId;
                    RowVisual visual = RowHighlight(row.Button);
                    visual.Highlight.color = selected ? new Color32(214, 187, 125, 45)
                        : invalid ? new Color32(225, 123, 106, 18)
                        : row.Button.interactable && visual.Hover.Hovered ? new Color32(161, 190, 194, 28) : Color.clear;
                    row.Name.color = invalid ? WarningColor : selected ? HeadingColor : BodyColor;
                }
            }
            if (presetScroll != null && presetScroll.gameObject.activeInHierarchy)
            {
                foreach (PresetRow row in presetRows)
                {
                    if (row.Button == null) continue;
                    bool invalid = !String.IsNullOrEmpty(row.Preset.Error);
                    bool selected = !invalid && row.Preset.Id == selectedPresetId;
                    RowVisual visual = RowHighlight(row.Button);
                    visual.Highlight.color = selected ? new Color32(214, 187, 125, 45)
                        : invalid ? new Color32(225, 123, 106, 18)
                        : row.Button.interactable && visual.Hover.Hovered ? new Color32(161, 190, 194, 28) : Color.clear;
                    row.Name.color = invalid ? WarningColor : selected ? HeadingColor : BodyColor;
                }
            }
        }
    }

    // Unity drives pointer transitions; the modal reads one cached flag on its
    // normal UI refresh and never allocates listeners or event delegates per frame.
    internal sealed class MultiplayerRowHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        internal bool Hovered { get; private set; }
        public void OnPointerEnter(PointerEventData eventData) { Hovered = true; }
        public void OnPointerExit(PointerEventData eventData) { Hovered = false; }
        private void OnDisable() { Hovered = false; }
    }
}
