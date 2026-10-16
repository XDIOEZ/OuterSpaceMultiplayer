using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
        private Button lastVisibleSaveSelection, lastVisiblePresetSelection;
        private Button copyFeedbackButton;
        private Button expandFeedbackButton, copyRoomButton;
        private Button joinIpButton, joinSteamButton, saveSettingsButton;
        private TMP_Text copyFeedbackCaption, expandFeedbackCaption, feedbackHeading, copyRoomCaption;
        private string copiedFeedback, copiedRoomId;
        private double copiedFeedbackUntil, copiedRoomUntil;
        private bool feedbackExpanded, lastFeedbackExpanded;
        private Image feedbackSurfaceImage;
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
            feedbackSurfaceImage = feedbackSurface.AddComponent<Image>();
            feedbackSurfaceImage.color = new Color32(21, 30, 34, 240);
            feedbackSurfaceImage.raycastTarget = false;
            MultiplayerUi.Top((RectTransform)feedbackSurface.transform, 28, 726, 984, 70);
            feedbackSurface.transform.SetSiblingIndex(status.transform.GetSiblingIndex());
            feedbackDot = MakeDot(feedbackSurface.transform, "FWM.FeedbackDot", 18, 20, 8);
            MultiplayerUi.Top(status.rectTransform, 64, 739, 662, 50);
            feedbackHeading = Label(transform, "FeedbackExpandedHeading", "操作反馈", 19, 64, 603, 500, 30);
            feedbackHeading.color = HeadingColor;
            feedbackHeading.gameObject.SetActive(false);
            expandFeedbackButton = MultiplayerUi.CloneButton(ButtonTemplate, transform, "FWM.ExpandFeedback",
                "展开", ToggleFeedback);
            MultiplayerUi.Top((RectTransform)expandFeedbackButton.transform, 742, 741, 100, 43);
            expandFeedbackCaption = OfficialModUi.Caption(expandFeedbackButton);
            expandFeedbackCaption.fontSize = 16;
            copyFeedbackButton = MultiplayerUi.CloneButton(ButtonTemplate, transform, "FWM.CopyFeedback",
                "复制提示", CopyFeedback);
            MultiplayerUi.Top((RectTransform)copyFeedbackButton.transform, 852, 741, 144, 43);
            copyFeedbackCaption = OfficialModUi.Caption(copyFeedbackButton);
            copyFeedbackCaption.fontSize = 16;

            // Steam guests and hosts can share the real lobby ID directly.
            // In IP-only rooms there is no Steam ID to copy or fabricate.
            MultiplayerUi.Top(roomStatus.rectTransform, 0, 0, 984, 76);
            copyRoomButton = MultiplayerUi.CloneButton(ButtonTemplate, pages[(int)Page.Room].transform,
                "FWM.CopyRoomId", "复制 Steam 房间号", CopyRoomId);
            MultiplayerUi.Top((RectTransform)copyRoomButton.transform, 756, 18, 224, 42);
            copyRoomCaption = OfficialModUi.Caption(copyRoomButton);
            copyRoomCaption.fontSize = 16;
            copyRoomButton.gameObject.SetActive(false);
            InstallRoomDisclosure();
            InstallRelationVisual();
            InstallPlayerRoster();
            InstallRelationRulesDisclosure();

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
            // Resolve once after all pages exist; none of these are shared game buttons.
            foreach (Button button in GetComponentsInChildren<Button>(true))
            {
                switch (button.name)
                {
                    case "FWM.JoinTcp": joinIpButton = button; break;
                    case "FWM.JoinSteam": joinSteamButton = button; break;
                    case "FWM.SaveSettings": saveSettingsButton = button; break;
                }
            }
            gameObject.AddComponent<MultiplayerInputTabNavigation>().Owner = this;
        }

        private static void SetTextIfChanged(TMP_Text label, string value)
        {
            if (label != null && label.text != value) label.text = value;
        }

        private static void SetColorIfChanged(Graphic graphic, Color color)
        {
            if (graphic != null && graphic.color != color) graphic.color = color;
        }

        private void CopyFeedback()
        {
            if (status == null || String.IsNullOrWhiteSpace(status.text)) return;
            try
            {
                // Only copy the short text already presented to the player,
                // never session internals, credentials or raw exception traces.
                GUIUtility.systemCopyBuffer = status.text;
                copiedFeedback = status.text;
                copiedFeedbackUntil = Time.unscaledTimeAsDouble + 2.5;
                SetTextIfChanged(copyFeedbackCaption, "已复制");
            }
            catch (Exception error) { if (Runtime != null) Runtime.ReportUiFailure(error); }
        }

        private void CopyRoomId()
        {
            if (!Connected || Busy || Runtime.Transport == null) return;
            string roomId = Runtime.Transport.RoomId;
            if (String.IsNullOrWhiteSpace(roomId)) return;
            try
            {
                GUIUtility.systemCopyBuffer = roomId;
                copiedRoomId = roomId;
                copiedRoomUntil = Time.unscaledTimeAsDouble + 2.5;
                SetTextIfChanged(copyRoomCaption, "已复制房间号");
            }
            catch (Exception error) { Runtime.ReportUiFailure(error); }
        }

        private void ToggleFeedback()
        {
            if (status == null || String.IsNullOrWhiteSpace(status.text)) return;
            feedbackExpanded = !feedbackExpanded;
            ApplyFeedbackLayout();
        }

        private void ApplyFeedbackLayout()
        {
            if (feedbackSurfaceImage == null || lastFeedbackExpanded == feedbackExpanded) return;
            lastFeedbackExpanded = feedbackExpanded;
            feedbackSurfaceImage.raycastTarget = feedbackExpanded; // Block clicks through the expanded message.
            MultiplayerUi.Top((RectTransform)feedbackSurfaceImage.transform,
                28, feedbackExpanded ? 594 : 726, 984, feedbackExpanded ? 202 : 70);
            MultiplayerUi.Top(status.rectTransform, 64,
                feedbackExpanded ? 640 : 739, feedbackExpanded ? 916 : 662, feedbackExpanded ? 82 : 50);
            status.maxVisibleLines = feedbackExpanded ? 6 : 2;
            feedbackHeading.gameObject.SetActive(feedbackExpanded);
            SetTextIfChanged(expandFeedbackCaption, feedbackExpanded ? "收起" : "展开");
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
            // Large scroll lists can contain dozens of unselected buttons.
            // Create the decorative underline only after a row is selected.
            if (!selected && !selectionLines.ContainsKey(button)) return;
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
            if (line.enabled != selected) line.enabled = selected;
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
            SetTextIfChanged(connectionBadgeText, connected ? (Runtime.Session.IsHost ? "已连接  ·  房主" : "已连接  ·  客人")
                : Busy ? "正在连接 / 退出" : "离线  ·  尚未加入");
            SetColorIfChanged(connectionBadgeDot, connected ? PositiveColor : Busy ? AccentColor : MutedColor);
            SetColorIfChanged(connectionBadgeText, connected ? BodyColor : MutedColor);
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
            SetColorIfChanged(feedbackDot, warning ? WarningColor : positive ? PositiveColor : AccentColor);
            SetColorIfChanged(status, warning ? WarningColor : BodyColor);
            if (feedbackExpanded && String.IsNullOrWhiteSpace(message))
            {
                feedbackExpanded = false;
                ApplyFeedbackLayout();
            }
            if (expandFeedbackButton != null)
            {
                expandFeedbackButton.interactable = !String.IsNullOrWhiteSpace(message);
                SetColorIfChanged(expandFeedbackCaption,
                    expandFeedbackButton.interactable ? BodyColor : DisabledColor);
            }
            if (copyFeedbackButton != null)
            {
                copyFeedbackButton.interactable = !String.IsNullOrWhiteSpace(message);
                SetTextIfChanged(copyFeedbackCaption, copyFeedbackButton.interactable && message == copiedFeedback
                    && Time.unscaledTimeAsDouble < copiedFeedbackUntil
                    ? "已复制" : "复制提示");
                SetColorIfChanged(copyFeedbackCaption, copyFeedbackButton.interactable ? BodyColor : DisabledColor);
            }

            string roomId = Runtime.Transport == null ? null : Runtime.Transport.RoomId;
            if (copyRoomButton != null)
            {
                bool hasRoomId = !String.IsNullOrWhiteSpace(roomId);
                if (copyRoomButton.gameObject.activeSelf != hasRoomId)
                {
                    copyRoomButton.gameObject.SetActive(hasRoomId);
                    MultiplayerUi.Top(roomStatus.rectTransform, 0, 0, hasRoomId ? 730 : 984, 76);
                }
                copyRoomButton.interactable = connected && !Busy && hasRoomId;
                SetColorIfChanged(copyRoomCaption, copyRoomButton.interactable ? BodyColor : DisabledColor);
                SetTextIfChanged(copyRoomCaption, roomId == copiedRoomId
                    && Time.unscaledTimeAsDouble < copiedRoomUntil ? "已复制房间号" : "复制 Steam 房间号");
            }

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

            RefreshRoomDisclosure(connected, roomId);
            RefreshSubmitAvailability();
            RefreshRelationVisual();
            RefreshPlayerRoster();
            RefreshButtonAvailability();
            bool invalidFocusedInput;
            string inputHint = RefreshInputBorders(out invalidFocusedInput);
            bool needsAttention;
            string formHint = FormGuidance(out needsAttention);
            string listHint = inputHint == null ? FocusedListHint() : null;
            SetTextIfChanged(navigationHint, inputHint ?? listHint ?? formHint ?? location);
            SetColorIfChanged(navigationHint, invalidFocusedInput ||
                (inputHint == null && listHint == null && needsAttention) ? WarningColor : MutedColor);
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
                if (entry.Key == "Pause")
                    SetTextIfChanged(text, session == null || !connected
                        ? "暂停 / 继续" : session.Paused ? "继续运行" : "暂停世界");
                FontStyles style = selected ? FontStyles.Bold : FontStyles.Normal;
                if (text.fontStyle != style) text.fontStyle = style;
                SetColorIfChanged(text, !entry.Value.interactable ? DisabledColor : selected ? HeadingColor : BodyColor);
            }
        }

        private void RefreshButtonAvailability()
        {
            // TMP labels are not the native button target graphic. Apply the same
            // disabled affordance to text, restoring its actual template/semantic color.
            foreach (KeyValuePair<Button, TMP_Text> entry in captions)
            {
                Button button = entry.Key;
                if (button == null || entry.Value == null || button.name == "FWM.CustomWorldPreset" ||
                    button.name == "FWM.SavedWorld" || button.name == "FWM.PlayerRosterRow") continue;
                if (!button.interactable)
                {
                    if (!disabledCaptions.ContainsKey(button)) disabledCaptions.Add(button, entry.Value.color);
                    SetColorIfChanged(entry.Value, DisabledColor);
                }
                else if (disabledCaptions.TryGetValue(button, out Color original))
                {
                    SetColorIfChanged(entry.Value, original);
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

        private static bool InvalidNumberField(TMP_InputField field)
        {
            if (field == null || !NumericRange(field.name, out int min, out int max, out string label))
                return false;
            return !Int32.TryParse(field.text, out int value) || value < min || value > max;
        }

        private static bool InvalidSteamRoom(TMP_InputField field)
        {
            if (field == null || String.IsNullOrWhiteSpace(field.text)) return false;
            // Steam lobby IDs are UInt64, unlike port and player-count fields.
            return !UInt64.TryParse(field.text.Trim(), NumberStyles.None,
                CultureInfo.InvariantCulture, out ulong roomId) || roomId == 0;
        }

        private void RefreshSubmitAvailability()
        {
            // Session/permission state is set by the page refresh first. These
            // gates only enforce local input validity; actual session methods
            // remain the authority and still validate again at submission time.
            bool idle = !Busy && !Connected;
            if (createWorldButton != null)
                createWorldButton.interactable = createWorldButton.interactable && idle
                    && worldName != null && !String.IsNullOrWhiteSpace(worldName.text)
                    && !InvalidNumberField(maxPlayers) && !InvalidNumberField(maxBases)
                    && !InvalidNumberField(startingEmployees) && !InvalidNumberField(port);
            if (loadButton != null)
                loadButton.interactable = loadButton.interactable && idle && !InvalidNumberField(resumePort);
            if (joinIpButton != null)
                joinIpButton.interactable = idle && joinAddress != null
                    && !String.IsNullOrWhiteSpace(joinAddress.text) && !InvalidNumberField(joinPort);
            if (joinSteamButton != null)
                joinSteamButton.interactable = idle && !Runtime.DirectLaunch && room != null
                    && !String.IsNullOrWhiteSpace(room.text) && !InvalidSteamRoom(room);
            if (saveSettingsButton != null)
                saveSettingsButton.interactable = !Busy && !InvalidNumberField(settingsEmployees)
                    && !InvalidNumberField(settingsPlayers) && !InvalidNumberField(settingsMaxBases)
                    && !InvalidNumberField(settingsPort);
        }

        private string FormGuidance(out bool needsAttention)
        {
            needsAttention = false;
            if (Busy) return "连接状态处理中…";
            if (page == Page.Create)
            {
                if (continueWorldForm != null && continueWorldForm.activeInHierarchy)
                {
                    WorldSummary selected = worlds.FirstOrDefault(w => w.WorldId == selectedWorldId);
                    if (selected == null) { needsAttention = true; return saveRows.Count == 0 ? "暂无联机存档" : "请选择存档"; }
                    if (!String.IsNullOrEmpty(selected.Error)) { needsAttention = true; return "所选存档无法继续"; }
                    if (InvalidNumberField(resumePort)) { needsAttention = true; return "请检查房主端口"; }
                    return "所选存档可继续";
                }
                if (SelectedPreset() == null) { needsAttention = true; return "先选择自定义世界配置"; }
                if (worldName == null || String.IsNullOrWhiteSpace(worldName.text))
                { needsAttention = true; return "请输入世界名称"; }
                if (InvalidNumberField(maxPlayers) || InvalidNumberField(maxBases)
                    || InvalidNumberField(startingEmployees) || InvalidNumberField(port))
                { needsAttention = true; return "请检查红框中的设置"; }
                return "配置完成 · 可以创建";
            }
            if (page == Page.Join)
            {
                if (joinIpButton != null && joinIpButton.interactable)
                    return "IP 参数完整 · 可尝试加入";
                if (joinSteamButton != null && joinSteamButton.interactable)
                    return "Steam 房间号有效 · 可尝试加入";
                needsAttention = true;
                if (InvalidNumberField(joinPort)) return Runtime.DirectLaunch ? "请检查 IP 端口" : "IP 端口无效（Steam 不受影响）";
                if (!Runtime.DirectLaunch && InvalidSteamRoom(room)) return "Steam 房间号无效（IP 不受影响）";
                return Runtime.DirectLaunch ? "填写房主 IP 地址" : "填写 IP 地址或 Steam 房间号";
            }
            if (page == Page.Settings && (InvalidNumberField(settingsEmployees)
                || InvalidNumberField(settingsPlayers) || InvalidNumberField(settingsMaxBases)
                || InvalidNumberField(settingsPort)))
            { needsAttention = true; return "请检查红框中的设置"; }
            return null;
        }

        private string RefreshInputBorders(out bool invalidFocusedInput)
        {
            invalidFocusedInput = false;
            string focusedHint = null;
            foreach (InputBorder item in inputBorders)
            {
                if (item.Field == null || item.Outline == null || !item.Field.gameObject.activeInHierarchy) continue;
                bool hasRange = NumericRange(item.Field.name, out int min, out int max, out string label);
                bool steamField = item.Field == room && !Runtime.DirectLaunch;
                bool invalid = (hasRange && InvalidNumberField(item.Field))
                    || (steamField && InvalidSteamRoom(item.Field));
                bool focused = item.Field.interactable && item.Field.isFocused;
                // Invalid values remain visibly invalid even while the field has focus.
                Color32 color = !item.Field.interactable ? new Color32(90, 109, 111, 48)
                    : invalid ? WarningColor
                    : focused ? AccentColor
                    : new Color32(108, 142, 145, 72);
                if (item.Outline.effectColor != color) item.Outline.effectColor = color;
                if (focused && hasRange)
                {
                    focusedHint = invalid ? label + "须为 " + min + "～" + max : label + "范围 " + min + "～" + max;
                    invalidFocusedInput = invalid;
                }
                else if (focused && steamField)
                {
                    focusedHint = invalid ? "Steam 房间号须为有效正整数" : "输入房主提供的 Steam 房间号";
                    invalidFocusedInput = invalid;
                }
                else if (focused && item.Field == playerRosterSearch)
                    focusedHint = "按昵称筛选玩家 · 清除可恢复全部";
                else if (focused) focusedHint = "Tab 下一个  ·  Shift+Tab 上一个";
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
                Button selectedButton = null;
                foreach (SaveRow row in saveRows)
                {
                    if (row.Button == null) continue;
                    bool invalid = !String.IsNullOrEmpty(row.World.Error);
                    bool selected = row.World.WorldId == selectedWorldId;
                    if (selected) selectedButton = row.Button;
                    RowVisual visual = RowHighlight(row.Button);
                    SetColorIfChanged(visual.Highlight, selected ? new Color32(214, 187, 125, 45)
                        : invalid ? new Color32(225, 123, 106, 18)
                        : row.Button.interactable && visual.Hover.Hovered ? new Color32(161, 190, 194, 28) : Color.clear);
                    // The page refresh already owns row caption colors. Keep its
                    // selected/normal palette to avoid two conflicting TMP writes
                    // per tick; the invalid metadata and background remain tinted.
                    SetColorIfChanged(row.Name, selected ? AccentColor : BodyColor);
                }
                // A disk refresh rebuilds the rows and resets the native scroll
                // to the top. Reveal the preserved selection once for the new
                // button instance, without overriding ordinary user scrolling.
                if (selectedButton != lastVisibleSaveSelection)
                {
                    lastVisibleSaveSelection = selectedButton;
                    if (selectedButton != null) RevealScrollRow(saveScroll, selectedButton.transform as RectTransform);
                }
            }
            if (presetScroll != null && presetScroll.gameObject.activeInHierarchy)
            {
                Button selectedButton = null;
                foreach (PresetRow row in presetRows)
                {
                    if (row.Button == null) continue;
                    bool invalid = !String.IsNullOrEmpty(row.Preset.Error);
                    bool selected = !invalid && row.Preset.Id == selectedPresetId;
                    if (selected) selectedButton = row.Button;
                    RowVisual visual = RowHighlight(row.Button);
                    SetColorIfChanged(visual.Highlight, selected ? new Color32(214, 187, 125, 45)
                        : invalid ? new Color32(225, 123, 106, 18)
                        : row.Button.interactable && visual.Hover.Hovered ? new Color32(161, 190, 194, 28) : Color.clear);
                    SetColorIfChanged(row.Name, selected ? AccentColor : BodyColor);
                }
                if (selectedButton != lastVisiblePresetSelection)
                {
                    lastVisiblePresetSelection = selectedButton;
                    if (selectedButton != null) RevealScrollRow(presetScroll, selectedButton.transform as RectTransform);
                }
            }
        }

        private string FocusedListHint()
        {
            GameObject selected = EventSystem.current == null ? null : EventSystem.current.currentSelectedGameObject;
            Button button = selected == null ? null : selected.GetComponentInParent<Button>();
            if (button == null || !button.interactable) return null;
            if (page == Page.World)
                return IsRosterButton(button) ? "↑↓ 选择玩家  ·  Home/End 首尾" : null;
            if (page != Page.Create) return null;
            bool saves = continueWorldForm != null && continueWorldForm.activeInHierarchy;
            if (saves)
            {
                foreach (SaveRow row in saveRows)
                    if (row.Button == button) return "↑↓ 选择存档  ·  Home/End 首尾";
            }
            else
            {
                foreach (PresetRow row in presetRows)
                    if (row.Button == button) return "↑↓ 选择配置  ·  Home/End 首尾";
            }
            return null;
        }

        internal bool NavigateWorldList(Button current, KeyCode key)
        {
            if (page == Page.World) return NavigatePlayerRoster(current, key);
            if (page != Page.Create || Busy || Connected || current == null) return false;
            bool saves = continueWorldForm != null && continueWorldForm.activeInHierarchy;
            ScrollRect scroll = saves ? saveScroll : presetScroll;
            if (scroll == null || !scroll.gameObject.activeInHierarchy) return false;

            // The list is the authority for row order, and disabled/invalid
            // preset rows are excluded. Saved-world errors stay selectable so
            // the player can still inspect why a save is unavailable.
            var buttons = new List<Button>();
            if (saves)
            {
                foreach (SaveRow row in saveRows)
                    if (row.Button != null && row.Button.interactable && row.Button.gameObject.activeInHierarchy)
                        buttons.Add(row.Button);
            }
            else
            {
                foreach (PresetRow row in presetRows)
                    if (row.Button != null && row.Button.interactable && row.Button.gameObject.activeInHierarchy)
                        buttons.Add(row.Button);
            }
            int index = buttons.IndexOf(current);
            if (index < 0) return false;

            int next = index;
            switch (key)
            {
                case KeyCode.UpArrow: next = Mathf.Max(0, index - 1); break;
                case KeyCode.DownArrow: next = Mathf.Min(buttons.Count - 1, index + 1); break;
                case KeyCode.Home: next = 0; break;
                case KeyCode.End: next = buttons.Count - 1; break;
                default: return false;
            }
            if (next == index) return true;
            Button target = buttons[next];
            target.onClick.Invoke(); // Same selection path and validation as a mouse click.
            EventSystem events = EventSystem.current;
            if (events != null) events.SetSelectedGameObject(target.gameObject);
            RevealScrollRow(scroll, target.transform as RectTransform);
            return true;
        }

        private static void RevealScrollRow(ScrollRect scroll, RectTransform row)
        {
            if (scroll == null || row == null || scroll.viewport == null || scroll.content == null) return;
            LayoutRebuilder.ForceRebuildLayoutImmediate(scroll.content);
            float scrollableHeight = scroll.content.rect.height - scroll.viewport.rect.height;
            if (scrollableHeight <= 0) return;
            Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(scroll.viewport, row);
            Rect view = scroll.viewport.rect;
            const float padding = 8;
            float offset = bounds.max.y > view.yMax - padding
                ? bounds.max.y - (view.yMax - padding)
                : bounds.min.y < view.yMin + padding
                    ? bounds.min.y - (view.yMin + padding) : 0;
            if (Mathf.Approximately(offset, 0)) return;
            scroll.StopMovement();
            scroll.verticalNormalizedPosition = Mathf.Clamp01(scroll.verticalNormalizedPosition + offset / scrollableHeight);
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

    // Every native field has Navigation.Mode.None so arrow keys stay with the text
    // editor. Offer predictable keyboard traversal without touching game input
    // outside this modal or moving focus to controls on hidden pages.
    internal sealed class MultiplayerInputTabNavigation : MonoBehaviour
    {
        internal MultiplayerRoomDialog Owner;
        private bool legacyKeyboardAvailable = true;

        private void Update()
        {
            if (!legacyKeyboardAvailable) return;
            bool tab, backwards;
            KeyCode listKey;
            try
            {
                tab = Input.GetKeyDown(KeyCode.Tab);
                listKey = Input.GetKeyDown(KeyCode.UpArrow) ? KeyCode.UpArrow
                    : Input.GetKeyDown(KeyCode.DownArrow) ? KeyCode.DownArrow
                    : Input.GetKeyDown(KeyCode.Home) ? KeyCode.Home
                    : Input.GetKeyDown(KeyCode.End) ? KeyCode.End : KeyCode.None;
                if (!tab && listKey == KeyCode.None) return;
                if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)
                    || Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) return;
                backwards = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            }
            catch (InvalidOperationException)
            {
                // A player using only Unity's new Input System must not receive
                // a repeated legacy-input exception from a purely visual feature.
                legacyKeyboardAvailable = false;
                return;
            }

            EventSystem events = EventSystem.current;
            GameObject selected = events == null ? null : events.currentSelectedGameObject;
            if (selected == null || !selected.transform.IsChildOf(transform)) return;
            if (listKey != KeyCode.None)
            {
                Button row = selected.GetComponentInParent<Button>();
                if (row != null && Owner != null) Owner.NavigateWorldList(row, listKey);
                return;
            }
            if (!tab) return;
            TMP_InputField current = selected.GetComponentInParent<TMP_InputField>();
            if (current == null || !current.isActiveAndEnabled || !current.interactable) return;

            TMP_InputField[] fields = GetComponentsInChildren<TMP_InputField>(true);
            var available = new List<TMP_InputField>(fields.Length);
            foreach (TMP_InputField field in fields)
                if (field != null && field.isActiveAndEnabled && field.gameObject.activeInHierarchy && field.interactable)
                    available.Add(field);
            if (available.Count < 2) return;
            int index = available.IndexOf(current);
            if (index < 0) return;
            int nextIndex = (index + (backwards ? available.Count - 1 : 1)) % available.Count;
            TMP_InputField next = available[nextIndex];
            if (next == current) return;
            current.DeactivateInputField();
            events.SetSelectedGameObject(next.gameObject);
            next.ActivateInputField();
        }
    }
}
