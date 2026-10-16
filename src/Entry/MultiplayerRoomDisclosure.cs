using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FreeWorldMultiplayer
{
    // Keep rarely used credential recovery out of the room's primary action
    // hierarchy. This is presentation-only; the existing guarded handlers
    // still own authentication, confirmation and reliable persistence.
    public sealed partial class MultiplayerRoomDialog
    {
        private readonly List<GameObject> recoveryDetailNodes = new List<GameObject>();
        private GameObject recoverySurface, recoveryCollapsedHint;
        private Button recoveryDisclosureButton, steamInviteButton;
        private TMP_Text recoveryDisclosureCaption, roomControlHint;
        private bool recoveryExpanded;

        private void InstallRoomDisclosure()
        {
            Transform root = pages[(int)Page.Room].transform;
            Transform surface = root.Find("FWM.RecoveryControls");
            Transform heading = root.Find("FWM.RecoveryHeading");
            if (surface == null || heading == null)
                throw new InvalidOperationException("房间身份恢复面板缺少原有的背景或标题。");
            recoverySurface = surface.gameObject;
            heading.gameObject.SetActive(false);

            foreach (string name in new[]
            {
                "CopyRecovery", "RecoveryPlayer", "RecoveryTarget",
                "RecoveryCode", "RebindCredential", "RecoveryHint"
            })
            {
                Transform node = root.Find("FWM." + name);
                if (node == null)
                    throw new InvalidOperationException("房间身份恢复面板缺少控件：" + name);
                recoveryDetailNodes.Add(node.gameObject);
            }

            recoveryDisclosureButton = MultiplayerUi.CloneButton(ButtonTemplate, root,
                "FWM.RecoveryDisclosure", "展开身份恢复", ToggleRoomRecovery);
            MultiplayerUi.Top((RectTransform)recoveryDisclosureButton.transform, 0, 320, 258, 42);
            recoveryDisclosureCaption = OfficialModUi.Caption(recoveryDisclosureButton);
            recoveryDisclosureCaption.fontSize = 17;
            recoveryCollapsedHint = Label(root, "RecoveryCollapsedHint",
                "身份恢复是低频高级操作。玩家可在设置中复制本机恢复码；只有房主能为离线玩家重绑登录身份。",
                17, 20, 379, 936, 56).gameObject;
            recoveryCollapsedHint.GetComponent<TMP_Text>().enableWordWrapping = true;

            Transform invite = root.Find("FWM.Invite");
            Transform hint = root.Find("FWM.RoomControlHint");
            steamInviteButton = invite == null ? null : invite.GetComponent<Button>();
            roomControlHint = hint == null ? null : hint.GetComponent<TMP_Text>();
            if (steamInviteButton == null || roomControlHint == null)
                throw new InvalidOperationException("房间缺少 Steam 邀请按钮或控制提示。");

            SetRoomRecoveryExpanded(false);
        }

        private void ToggleRoomRecovery()
        {
            SetRoomRecoveryExpanded(!recoveryExpanded);
        }

        private void SetRoomRecoveryExpanded(bool expanded)
        {
            bool wasExpanded = recoveryExpanded;
            recoveryExpanded = expanded;
            foreach (GameObject node in recoveryDetailNodes)
                if (node != null && node.activeSelf != expanded) node.SetActive(expanded);
            if (recoverySurface != null)
                MultiplayerUi.Top((RectTransform)recoverySurface.transform, -12, 310,
                    1008, expanded ? 236 : 140);
            if (recoveryCollapsedHint != null)
                recoveryCollapsedHint.SetActive(!expanded);
            if (recoveryDisclosureCaption != null)
            {
                SetTextIfChanged(recoveryDisclosureCaption, expanded ? "收起身份恢复" : "展开身份恢复");
                SetColorIfChanged(recoveryDisclosureCaption, expanded ? HeadingColor : BodyColor);
            }
            if (recoveryDisclosureButton != null)
                SetSelectedVisual(recoveryDisclosureButton, expanded);

            if (wasExpanded && !expanded)
            {
                // Invalidate the two-click rebind confirmation when hiding its
                // target. A stale pending confirmation must not survive a collapse.
                ClearFocus();
                armedRecoveryPlayer = null;
                armedRecoveryCode = null;
                if (rebindButton != null) SetCaption(rebindButton, "确认重绑离线玩家");
                if (recoveryCode != null) recoveryCode.text = String.Empty;
            }
        }

        private void RefreshRoomDisclosure(bool connected, string roomId)
        {
            if (recoveryExpanded && (!HasRoomContext || page != Page.Room))
                SetRoomRecoveryExpanded(false);

            // A TCP-only host, even one launched through Steam, has no Steam
            // lobby to invite friends into. Match the real transport capability.
            bool hasSteamLobby = connected && !Busy && !String.IsNullOrWhiteSpace(roomId);
            if (steamInviteButton != null && steamInviteButton.interactable != hasSteamLobby)
                steamInviteButton.interactable = hasSteamLobby;
            if (roomControlHint != null && HasRoomContext)
            {
                bool steamStarting = !hasSteamLobby && Runtime.Transport != null
                    && Runtime.Transport.Name.IndexOf("Steam", StringComparison.Ordinal) >= 0;
                SetTextIfChanged(roomControlHint,
                    String.IsNullOrWhiteSpace(roomId)
                        ? steamStarting
                            ? "只有房主可以调整时间与保存世界。Steam 房间号尚未就绪，邀请暂不可用。"
                            : "只有房主可以调整时间与保存世界。当前是 IP 直连，Steam 邀请不可用。"
                        : "只有房主可以调整时间与保存世界。可邀请 Steam 好友，或分享房间号。");
            }
        }
    }
}
