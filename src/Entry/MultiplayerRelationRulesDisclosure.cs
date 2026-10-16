using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FreeWorldMultiplayer
{
    // Make the room's relationship list the primary content. The reference
    // rules remain available on demand without changing gameplay permissions.
    public sealed partial class MultiplayerRoomDialog
    {
        private Image relationRulesSurface;
        private TMP_Text relationRulesHeading, relationRulesDescription, relationRulesSummary;
        private Button relationRulesToggle;
        private TMP_Text relationRulesToggleCaption;
        private bool relationRulesExpanded;

        private void InstallRelationRulesDisclosure()
        {
            Transform root = pages[(int)Page.World].transform;
            // BuildWorld creates a background and a TMP label with the same
            // name. Distinguish them by component, not Transform.Find().
            foreach (Transform child in root)
            {
                if (child.name != "FWM.RelationRules") continue;
                if (child.TryGetComponent(out Image surface)) relationRulesSurface = surface;
                if (child.TryGetComponent(out TMP_Text description)) relationRulesDescription = description;
            }
            Transform heading = root.Find("FWM.RelationRulesHeading");
            relationRulesHeading = heading == null ? null : heading.GetComponent<TMP_Text>();
            if (relationRulesSurface == null || relationRulesHeading == null ||
                relationRulesDescription == null || playerRosterScroll == null)
                throw new InvalidOperationException("玩家关系页面缺少规则说明或玩家列表。");
            relationRulesDescription.alignment = TextAlignmentOptions.TopLeft;

            relationRulesToggle = MultiplayerUi.CloneButton(ButtonTemplate, root,
                "FWM.RelationRulesToggle", "查看完整规则", ToggleRelationRules);
            relationRulesToggleCaption = OfficialModUi.Caption(relationRulesToggle);
            relationRulesToggleCaption.fontSize = 16;

            relationRulesSummary = Label(root, "RelationRulesSummary",
                "友好需双方同意；敌对可单方设置。关系决定合作与攻击，不限制参观地块。",
                16, 15, 226, 728, 42);
            relationRulesSummary.enableWordWrapping = true;
            relationRulesSummary.alignment = TextAlignmentOptions.MidlineLeft;

            ApplyRelationRulesLayout();
        }

        private void ToggleRelationRules()
        {
            relationRulesExpanded = !relationRulesExpanded;
            ApplyRelationRulesLayout();
        }

        private void ApplyRelationRulesLayout()
        {
            bool expanded = relationRulesExpanded;
            MultiplayerUi.Top(relationRulesSurface.rectTransform,
                -12, 184, 1008, expanded ? 220 : 86);
            MultiplayerUi.Top(relationRulesHeading.rectTransform, 0, 191, 620, 32);
            MultiplayerUi.Top(relationRulesDescription.rectTransform, 0, 242, 984, 153);
            relationRulesDescription.gameObject.SetActive(expanded);
            relationRulesSummary.gameObject.SetActive(!expanded);
            MultiplayerUi.Top((RectTransform)relationRulesToggle.transform, 766, 193, 218, 38);
            SetTextIfChanged(relationRulesToggleCaption, expanded ? "收起规则" : "查看完整规则");
            SetSelectedVisual(relationRulesToggle, expanded);

            // The compact view shows four or more players at once, rather than
            // only the bottom two rows in the original fixed rules layout.
            float headerY = expanded ? 413 : 279;
            float scrollY = expanded ? 448 : 314;
            MultiplayerUi.Top(playerRosterHeading.rectTransform, 0, headerY, 450, 28);
            MultiplayerUi.Top((RectTransform)playerRosterScroll.transform,
                0, scrollY, 984, 560 - scrollY);
            MultiplayerUi.Top(playerRosterEmpty.rectTransform, 22, scrollY + 20, 910, 52);
            LayoutRosterFilters(headerY);
        }
    }
}
