using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Script.FreeWorldAirShip.Galaxy.GalaxyField;
using Script.FreeWorldAirShip.Galaxy.NPCFleet;
using TMPro;
using UnityEngine;

namespace FreeWorldMultiplayer
{
    // Native fleet prefabs no longer contain NameplateRoot. NPCs have a complete
    // screen HUD, but create it lazily on hover; player proxy controllers never do.
    // Keep that HUD for NPCs and reuse only its Identity template for player fleets.
    internal static class NativeFleetNameplates
    {
        private static readonly Dictionary<GalaxyFleetVisual, MultiplayerFleetNameplate> views = new Dictionary<GalaxyFleetVisual, MultiplayerFleetNameplate>();
        internal static readonly FieldInfo NpcHudField = AccessTools.Field(typeof(GalaxyNPCFleet), "hud");
        internal static readonly MethodInfo EnsureNpcHud = AccessTools.Method(typeof(GalaxyNPCFleet), "EnsureHud");
        private static RectTransform layer;
        private static Canvas canvas;

        internal static void Install(Harmony harmony)
        {
            if (NpcHudField == null || EnsureNpcHud == null)
                throw new MissingMemberException("原生舰队 HUD 接口不完整。");
            foreach (string method in new[] { "InitWidget", "Update" })
                harmony.Patch(AccessTools.Method(typeof(GalaxyFleetVisual), method),
                    postfix: new HarmonyMethod(typeof(NativeFleetNameplates), nameof(RegisterPostfix)));
            harmony.Patch(AccessTools.Method(typeof(GameBridge), nameof(GameBridge.Dispose)),
                prefix: new HarmonyMethod(typeof(NativeFleetNameplates), nameof(DisposePrefix)));
        }

        private static void RegisterPostfix(GalaxyFleetVisual __instance)
        {
            GameBridge bridge = GameBridge.Active;
            if (bridge == null || bridge.Context == null || __instance == null) return;
            MultiplayerFleetNameplate existing;
            if (views.TryGetValue(__instance, out existing) && existing != null) return;
            GalaxyPlayerFleet player = __instance.GetComponent<GalaxyPlayerFleet>();
            GalaxyNPCFleet npc = __instance.GetComponent<GalaxyNPCFleet>();
            if (player == null && npc == null) return;
            var view = __instance.gameObject.AddComponent<MultiplayerFleetNameplate>();
            views[__instance] = view;
            view.Bind(bridge, __instance, player, npc);
        }

        private static void DisposePrefix(GameBridge __instance)
        {
            if (!ReferenceEquals(GameBridge.Active, __instance) || __instance.IsCreatingWorld) return;
            foreach (var view in new List<MultiplayerFleetNameplate>(views.Values))
                if (view != null) view.ReleaseSession();
            views.Clear();
            ReleaseLayer();
        }

        internal static void Unregister(GalaxyFleetVisual visual, MultiplayerFleetNameplate view)
        {
            MultiplayerFleetNameplate current;
            if (!ReferenceEquals(visual, null) && views.TryGetValue(visual, out current) && ReferenceEquals(current, view)) views.Remove(visual);
            if (views.Count == 0) ReleaseLayer();
        }

        internal static RectTransform GetLayer()
        {
            if (layer != null && canvas != null) return layer;
            var root = new GameObject("FWM.FleetNameplates", typeof(RectTransform));
            root.SetActive(false);
            layer = root.GetComponent<RectTransform>();
            canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.overrideSorting = true;
            canvas.sortingOrder = -100; // Same world-HUD layer as the native NPC labels.
            var scaler = root.AddComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = UnityEngine.UI.CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = .5f;
            var group = root.AddComponent<CanvasGroup>();
            group.interactable = false;
            group.blocksRaycasts = false;
            root.SetActive(true);
            return layer;
        }

        internal static float ScaleFactor { get { return canvas == null ? 1f : Mathf.Max(.001f, canvas.scaleFactor); } }

        private static void ReleaseLayer()
        {
            if (layer != null) { layer.gameObject.SetActive(false); UnityEngine.Object.Destroy(layer.gameObject); }
            layer = null; canvas = null;
        }
    }

    public sealed class MultiplayerFleetNameplate : MonoBehaviour
    {
        private GameBridge bridge;
        private GalaxyFleetVisual visual;
        private GalaxyPlayerFleet player;
        private GalaxyNPCFleet npc;
        private RemotePlayerFleetProxyView proxy;
        private GalaxyNPCFleetHUD npcHud;
        private bool ownsNpcHud, wasVisible, subscribed, suppressedNative, nativeNameplateVisible;
        private TextMeshProUGUI label;
        private SphereCollider outline;
        private float refreshAt;

        internal void Bind(GameBridge owner, GalaxyFleetVisual fleetVisual, GalaxyPlayerFleet playerView, GalaxyNPCFleet npcView)
        {
            bridge = owner; visual = fleetVisual; player = playerView; npc = npcView;
            proxy = GetComponent<RemotePlayerFleetProxyView>();
        }

        private void LateUpdate()
        {
            if (bridge == null || !ReferenceEquals(GameBridge.Active, bridge) || bridge.Context == null)
            { ReleaseSession(); return; }
            var scene = GalaxyFieldScene.Instance;
            bool visible = visual != null && visual.IsFleetVisualVisible && scene != null && scene.CurState == State.GalaxyScene;
            if (!visible) { ReleasePresentation(); wasVisible = false; return; }

            // Proactively create the real native HUD. Its normal visibility never
            // depends on hovered/pinned, and its text already includes NPC identity.
            if (proxy == null && npc != null && npc.NpcSetBean != null && npc.BehaviorConfig != null)
            {
                if (npcHud == null)
                {
                    bool hadHud = NativeFleetNameplates.NpcHudField.GetValue(npc) as GalaxyNPCFleetHUD != null;
                    npcHud = NativeFleetNameplates.EnsureNpcHud.Invoke(npc, null) as GalaxyNPCFleetHUD;
                    ownsNpcHud = npcHud != null && !hadHud;
                }
                if (npcHud != null && !wasVisible && npcHud.IsSuppressed)
                    npcHud.ShowForFleet();
                wasVisible = true;
                return;
            }

            wasVisible = true;
            if (Time.unscaledTime < refreshAt) return;
            refreshAt = Time.unscaledTime + .25f;
            string ownerId = proxy != null ? proxy.PlayerId : null;
            FWFleet fleet = player != null && player.fleetbean != null ? player.fleetbean.bean : null;
            if (proxy != null)
            {
                string resolvedOwner; long uid;
                if (!bridge.TryResolvePlayerFleetProxy(proxy.FleetUid, out resolvedOwner, out uid)) { ReleaseLabel(); return; }
                ownerId = resolvedOwner;
                foreach (FWFleet candidate in bridge.OwnedFleetsForReplication(ownerId))
                    if (candidate.getUID() == uid) { fleet = candidate; break; }
            }
            else if (fleet != null) ownerId = bridge.Context.OwnerOfEntity(fleet.getUID());
            if (fleet == null || ownerId == null || !bridge.Online.Contains(ownerId)) { ReleaseLabel(); return; }
            var galaxy = GalaxySceneInterface.getInstance();
            if (galaxy == null || fleet.pos == null || fleet.pos.galaxyId != galaxy.getGalaxyId()) { ReleaseLabel(); return; }
            if (label == null) CreateLabel();
            string nickname = bridge.Context.GetPlayerDisplayName(ownerId);
            string fleetName = fleet.getName();
            string text = (String.IsNullOrWhiteSpace(nickname) ? "玩家" : nickname) + " · " + (String.IsNullOrWhiteSpace(fleetName) ? "舰队" : fleetName);
            text = text.Replace('\r', ' ').Replace('\n', ' ');
            if (label.text != text) label.text = text;
        }

        private void CreateLabel()
        {
            GameObject prefab = visual.NpcTextHudPrefab;
            Transform identity = prefab == null ? null : prefab.transform.Find("Identity");
            TextMeshProUGUI template = identity == null ? null : identity.GetComponent<TextMeshProUGUI>();
            if (template == null)
            {
                enabled = false;
                throw new InvalidOperationException("原生舰队名牌的 Identity 文字模板缺失。");
            }
            label = UnityEngine.Object.Instantiate(template, NativeFleetNameplates.GetLayer(), false);
            label.name = "FWM.FleetIdentity";
            label.raycastTarget = false;
            label.richText = false;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Ellipsis;
            RectTransform rect = label.rectTransform;
            rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f);
            rect.pivot = new Vector2(.5f, .5f);
            rect.anchoredPosition = Vector2.zero;
            rect.localRotation = Quaternion.identity;
            rect.localScale = Vector3.one;
            rect.sizeDelta = new Vector2(380, 24);
            label.gameObject.SetActive(false); // First projection enables it before rendering.
            outline = visual.ClickTarget.GetComponent<SphereCollider>();
            nativeNameplateVisible = visual.IsNameplateVisible;
            suppressedNative = true;
            visual.SetNameplateVisible(false);
            Canvas.willRenderCanvases += RefreshScreenPosition;
            subscribed = true;
        }

        private void RefreshScreenPosition()
        {
            if (label == null) return;
            var scene = GalaxyFieldScene.Instance;
            bool visible = bridge != null && ReferenceEquals(GameBridge.Active, bridge) && visual != null && visual.IsFleetVisualVisible
                && isActiveAndEnabled && scene != null && scene.CurState == State.GalaxyScene;
            Camera camera = visible ? scene.MainCamera : null;
            if (camera == null || !camera.isActiveAndEnabled) { label.gameObject.SetActive(false); return; }
            Vector3 screen = camera.WorldToScreenPoint(transform.position);
            if (screen.z <= 0 || screen.x < 0 || screen.x > Screen.width || screen.y < 0 || screen.y > Screen.height)
            { label.gameObject.SetActive(false); return; }
            float scale = NativeFleetNameplates.ScaleFactor;
            float radiusPixels = 0;
            if (outline != null)
            {
                Vector3 extent = outline.bounds.extents;
                float radius = Mathf.Max(extent.x, extent.y);
                radiusPixels = Mathf.Abs(camera.WorldToScreenPoint(transform.position + camera.transform.up * radius).y - screen.y);
            }
            screen.y += Mathf.Max(32f * scale, radiusPixels) + 12f * scale;
            Vector2 halfPixels = label.rectTransform.sizeDelta * (.5f * scale);
            float maxX = Mathf.Max(halfPixels.x, Screen.width - halfPixels.x);
            float maxY = Mathf.Max(halfPixels.y, Screen.height - halfPixels.y);
            screen.x = Mathf.Clamp(screen.x, halfPixels.x, maxX);
            screen.y = Mathf.Clamp(screen.y, halfPixels.y, maxY);
            Vector2 point = Vector2.zero;
            var parent = label.rectTransform.parent as RectTransform;
            bool projected = parent != null && RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, screen, null, out point);
            if (projected) label.rectTransform.anchoredPosition = point;
            label.gameObject.SetActive(projected);
        }

        private void ReleaseLabel()
        {
            if (subscribed) { Canvas.willRenderCanvases -= RefreshScreenPosition; subscribed = false; }
            if (label != null) { label.gameObject.SetActive(false); UnityEngine.Object.Destroy(label.gameObject); }
            label = null; outline = null;
            if (suppressedNative && visual != null) visual.SetNameplateVisible(nativeNameplateVisible);
            suppressedNative = false;
        }

        private void ReleasePresentation()
        {
            ReleaseLabel();
            if (ownsNpcHud && npcHud != null)
            {
                npcHud.HideImmediate();
                if (npc != null && ReferenceEquals(NativeFleetNameplates.NpcHudField.GetValue(npc), npcHud)) NativeFleetNameplates.NpcHudField.SetValue(npc, null);
                UnityEngine.Object.Destroy(npcHud.gameObject);
            }
            npcHud = null; ownsNpcHud = false;
        }

        internal void ReleaseSession()
        {
            ReleasePresentation();
            bridge = null;
            NativeFleetNameplates.Unregister(visual, this);
            enabled = false;
            UnityEngine.Object.Destroy(this);
        }

        private void OnDisable() { ReleasePresentation(); wasVisible = false; refreshAt = 0; }
        private void OnDestroy() { ReleasePresentation(); NativeFleetNameplates.Unregister(visual, this); }
    }
}
