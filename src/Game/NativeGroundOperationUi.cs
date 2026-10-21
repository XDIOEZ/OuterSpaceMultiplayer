using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace FreeWorldMultiplayer
{
    // Extend the game's actual planet/location side card, rather than the MOD room page.
    internal static class NativeGroundOperationUi
    {
        internal static void Install(Harmony harmony)
        {
            var patched = new HashSet<MethodInfo>();
            foreach (Type type in typeof(ExpeditionInfo_ABase).Assembly.GetTypes())
            {
                if (!typeof(ExpeditionInfo_ABase).IsAssignableFrom(type)) continue;
                MethodInfo map = type.GetMethod("setStarMapInfo", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    null, new[] { typeof(FWStarMapInfo) }, null);
                if (map != null && patched.Add(map))
                    harmony.Patch(map, postfix: new HarmonyMethod(typeof(NativeGroundOperationUi), nameof(MapPostfix)));
            }
            harmony.Patch(AccessTools.Method(typeof(ExpeditionInfo_ABase), "setPoint", new[] { typeof(FairyPoint) }),
                postfix: new HarmonyMethod(typeof(NativeGroundOperationUi), nameof(PointPostfix)));
        }

        private static void MapPostfix(ExpeditionInfo_ABase __instance, FWStarMapInfo __0)
        {
            if (GameBridge.Active == null || __0 == null || (__0.starSpread != null && __0.starSpread.isAirMap())) return;
            GetView(__instance).BindMap(__instance, __0.starMapId);
        }

        private static void PointPostfix(ExpeditionInfo_ABase __instance, FairyPoint __0)
        {
            if (GameBridge.Active == null || __0 == null) return;
            ExpeditionFaction faction = NativeWire.Get(__instance, "factionGrid") as ExpeditionFaction;
            WorldMap worldMap = faction == null ? null : NativeWire.Get(faction, "worldMap") as WorldMap;
            if (worldMap == null || worldMap.starInfo == null) return;
            GetView(__instance).BindPoint(__instance, worldMap.starInfo.starId, __0.x, __0.y);
        }

        private static GroundOperationSideCard GetView(ExpeditionInfo_ABase panel)
        {
            GroundOperationSideCard view = panel.GetComponent<GroundOperationSideCard>();
            return view != null ? view : panel.gameObject.AddComponent<GroundOperationSideCard>();
        }
    }

    internal sealed class GroundOperationSideCard : MonoBehaviour
    {
        private ExpeditionInfo_ABase panel;
        private long mapId = -1;
        private string starId;
        private int pointX, pointY;
        private float nextRefresh;
        private bool failed;
        private ExpeditionItem operatorRow, observerRow, permissionRow;
        private TMP_Text operatorValue, observerValue, permissionValue;

        internal void BindMap(ExpeditionInfo_ABase owner, long id)
        {
            panel = owner; mapId = id; starId = null; nextRefresh = 0;
            RefreshSafely();
        }

        internal void BindPoint(ExpeditionInfo_ABase owner, string star, int x, int y)
        {
            panel = owner; mapId = -1; starId = star; pointX = x; pointY = y; nextRefresh = 0;
            RefreshSafely();
        }

        private void Update()
        {
            if (failed || panel == null) return;
            nextRefresh -= Time.unscaledDeltaTime;
            if (nextRefresh > 0) return;
            nextRefresh = 0.25f;
            RefreshSafely();
        }

        private void RefreshSafely()
        {
            if (failed) return;
            try { Refresh(); }
            catch (Exception error)
            {
                failed = true;
                HideRows();
                Logger.LogErrorEngine("[FreeWorldMultiplayer][ground-operation-ui] 地块侧边状态显示失败；map=" + mapId + "\n" + error);
            }
        }

        private void Refresh()
        {
            GameBridge bridge = GameBridge.Active;
            if (bridge == null || bridge.Context == null || panel == null) { HideRows(); return; }
            if (starId != null)
            {
                // A wilderness point may acquire a real map while this same card stays open.
                mapId = -1;
                foreach (FWStarMapInfo map in bridge.Maps.Values)
                    if (map.starId == starId && map.worldPoint != null && map.worldPoint.worldX == pointX && map.worldPoint.worldY == pointY
                        && map.starSpread != null && !map.starSpread.isAirMap()) { mapId = map.starMapId; break; }
            }
            var grid = NativeWire.Get(panel, "grid_Desc") as UnityEngine.UI.GridLayoutGroup;
            if (grid == null) throw new InvalidOperationException("原版地块侧边卡片缺少 Status/Grid。");
            // The native card clears/rebuilds its description rows during refresh. Recreate
            // only our destroyed rows and let the original layout size the status section.
            EnsureRow(ref operatorRow, ref operatorValue, grid.gameObject, "FWM.GroundOperator", "操作权");
            EnsureRow(ref observerRow, ref observerValue, grid.gameObject, "FWM.GroundObservers", "观战人数");
            EnsureRow(ref permissionRow, ref permissionValue, grid.gameObject, "FWM.GroundPermission", "当前权限");
            MapAccess access = mapId < 0 ? null : bridge.Context.GetMapAccess(mapId);
            // Frozen access is the context's sentinel for an unregistered map; a known
            // frozen world map still receives its ordinary Replica/Simulator projection.
            bool known = access != null && access.Access != MapAccessMode.Frozen;
            string actor = known ? bridge.Context.OperatorOfMap(mapId) : null;
            string name = actor == null ? null : bridge.Context.GetPlayerDisplayName(actor);
            if (String.IsNullOrWhiteSpace(name)) name = actor;
            bool mine = actor != null && actor == bridge.Context.LocalPlayerId;
            SetText(operatorValue, !known ? "待同步" : actor == null ? "空闲" : "占用 · " + name + (mine ? "（你）" : ""));
            SetText(observerValue, known ? bridge.Context.GroundObserverCount(mapId).ToString() : "—");
            SetText(permissionValue, !known ? "尚未加载地块" : mine ? "可操作自己的单位" : actor == null ? "尚未取得操作权" : "只读观战");
        }

        private void EnsureRow(ref ExpeditionItem row, ref TMP_Text value, GameObject grid, string name, string title)
        {
            if (row != null) { if (!row.gameObject.activeSelf) row.gameObject.SetActive(true); return; }
            // Pass null to the native language-key API. Player names and these MOD captions
            // are literal text and must not trigger missing-translation-key warnings.
            row = panel.addItemDesc(grid, null, null);
            row.name = name;
            Transform titleNode = row.transform.Find("Label0"), valueNode = row.transform.Find("Label1");
            TMP_Text label = titleNode == null ? null : titleNode.GetComponent<TMP_Text>();
            value = valueNode == null ? null : valueNode.GetComponent<TMP_Text>();
            if (label == null || value == null) throw new InvalidOperationException("原版地块状态行缺少 Label0/Label1：" + name);
            label.gameObject.SetActive(true); value.gameObject.SetActive(true);
            label.richText = false; value.richText = false;
            label.raycastTarget = false; value.raycastTarget = false;
            value.enableWordWrapping = false; value.overflowMode = TextOverflowModes.Ellipsis;
            label.text = title;
        }

        private static void SetText(TMP_Text label, string text)
        { if (label.text != text) label.text = text; }

        private void HideRows()
        {
            if (operatorRow != null) operatorRow.gameObject.SetActive(false);
            if (observerRow != null) observerRow.gameObject.SetActive(false);
            if (permissionRow != null) permissionRow.gameObject.SetActive(false);
        }
    }
}
