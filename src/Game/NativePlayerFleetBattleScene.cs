using System;
using System.Collections.Generic;
using System.Linq;
using SmartAI;
using SmartAI.Data;
using SmartAI.Managers;
using SmartAI.Gameplay;
using SmartAI.Rules;
using SmartAI.Utils;
using UnityEngine;

namespace FreeWorldMultiplayer
{
    public sealed partial class GameBridge
    {
        private sealed class FleetBattleEnvironment
        {
            public int Seed, StarType;
            public string Profile, BeltProfile;
            public bool HasStar, Belt;
            public float StarX, StarY, Distance, Size, Density;
        }
        private sealed class FleetBattleSceneItem
        {
            public string Path, Kind;
            public bool Active, Contested;
            public float X, Y, Z, Rotation, Sx, Sy, Sz, Progress;
            public int Owner, Capturing, ComponentIndex;
        }
        // Names alone are not Unity hierarchy identities: native environment prefabs
        // contain several equally named siblings. Keep addresses for this root's life,
        // including removed slots, so later removals cannot renumber surviving nodes.
        private sealed class BattleEnvironmentNodes
        {
            public Transform Root;
            public readonly Dictionary<Transform, string> Paths = new Dictionary<Transform, string>();
            public readonly Dictionary<string, Transform> Nodes = new Dictionary<string, Transform>(StringComparer.Ordinal);
            public BattleEnvironmentNodes(Transform root) { Root = root; Index(root, "@2", false); }
            public void Refresh() { Index(Root, "@2", false); }
            public void Index(Transform node, string path, bool restoring)
            {
                Transform existing;
                if (restoring && Nodes.TryGetValue(path, out existing) && existing != null && existing != node)
                    throw new InvalidOperationException("战场静态节点恢复身份已被占用：" + path);
                Paths[node] = path; Nodes[path] = node;
                for (int i = 0; i < node.childCount; i++)
                {
                    Transform child = node.GetChild(i); string childPath;
                    if (!restoring && Paths.TryGetValue(child, out childPath)) { Index(child, childPath, false); continue; }
                    string name = Uri.EscapeDataString(child.name); int ordinal = 0;
                    // Restored descendants retain the template's sibling ordering too.
                    for (int sibling = 0; restoring && sibling < i; sibling++)
                        if (node.GetChild(sibling).name == child.name) ordinal++;
                    do { childPath = path + "/" + name + "~" + (ordinal++).ToString(System.Globalization.CultureInfo.InvariantCulture); }
                    while (!restoring && Nodes.ContainsKey(childPath));
                    Index(child, childPath, restoring);
                }
            }
            public Transform Resolve(string path)
            {
                Transform node;
                if (path == "@2" || path.StartsWith("@2/", StringComparison.Ordinal))
                    return Nodes.TryGetValue(path, out node) ? node : null;
                // Old checkpoints are readable only when each name is unambiguous.
                node = Root;
                foreach (string name in path.Split('/'))
                {
                    Transform match = null;
                    for (int i = 0; i < node.childCount; i++)
                    {
                        Transform child = node.GetChild(i);
                        if (child.name != name) continue;
                        if (match != null) throw new InvalidOperationException("旧战场检查点含歧义层级：" + path);
                        match = child;
                    }
                    if (match == null) return null;
                    node = match;
                }
                return node;
            }
        }
        private BattleEnvironmentNodes battleEnvironmentNodes;
        private FleetBattleEnvironment battleEnvironment;
        private string appliedBattleEnvironment;
        private bool applyingBattleClock;
        internal bool IsApplyingPlayerBattleClock { get { return applyingBattleClock; } }
        private static G_BattleField ResolvePlayerBattleField(PlayerFleetBattleRecord battle)
        {
            // Use the same real configuration as the rule that BattleManager creates.
            // The legacy Bean spawner separately looks up ID 1, while the standard
            // rule/environment use ID 0; missing ID 1 otherwise leaves both fleets null.
            G_BattleField field = new StandardBattleRule(null, null).GetFieldConfig();
            if (field == null)
            {
                PlayerFleetBattleTrace.Emit("field.preflight", battle, "reason=standard-rule-field-missing", true);
                throw new NativeRejectionException("原版标准舰战的战场配置尚未加载，不能进入战场。");
            }
            if (float.IsNaN(field.FieldWidth) || float.IsInfinity(field.FieldWidth) || field.FieldWidth <= 1f ||
                float.IsNaN(field.FieldHeight) || float.IsInfinity(field.FieldHeight) || field.FieldHeight <= 1f)
            {
                PlayerFleetBattleTrace.Emit("field.preflight", battle, "fieldId=" + field.ID + ";reason=invalid-dimensions", true);
                throw new NativeRejectionException("原版标准舰战的战场尺寸无效，不能进入战场。");
            }
            PlayerFleetBattleTrace.Emit("field.preflight", battle, "fieldId=" + field.ID + ";width=" + field.FieldWidth + ";height=" + field.FieldHeight);
            return field;
        }
        internal bool InitializePlayerBattleFleets(BattleSpawner spawner)
        {
            if (!HasNativePlayerBattle) return false;
            string spawnStep = "context";
            try
            {
                PlayerFleetBattleTrace.Emit("spawner.initialize", nativePlayerBattle, "local=" + Context.LocalPlayerId);
                if (BattleContext.WorldSiteBean != null || BattleContext.CurrentAiIntervention != null)
                    throw new InvalidOperationException("玩家舰战的原生战斗上下文与标准双舰队入口不一致。");
                spawnStep = "field";
                G_BattleField field = ResolvePlayerBattleField(nativePlayerBattle);
                spawnStep = "fleet-data";
                FleetData self = BattleSpawner.CreateFleetDataFromBean(BattleContext.SelfFleetInfo);
                FleetData enemy = BattleSpawner.CreateFleetDataFromBean(BattleContext.EnemyFleetInfo);
                if (self == null || enemy == null || self.ships.Count == 0 || enemy.ships.Count == 0)
                {
                    PlayerFleetBattleTrace.Emit("spawner.fleet-data-missing", nativePlayerBattle,
                        "selfPresent=" + (self != null) + ";enemyPresent=" + (enemy != null)
                        + ";selfShips=" + (self == null ? -1 : self.ships.Count)
                        + ";enemyShips=" + (enemy == null ? -1 : enemy.ships.Count), true);
                    throw new InvalidOperationException("双方已确认的真实舰队未完整传入原生战场。");
                }
                spawnStep = "fleet-positions";
                self.fleetPos = BattleUtils.GetPosByArea(field.GetSelfPosVec(), field);
                enemy.fleetPos = BattleUtils.GetPosByArea(field.GetEnemyPosVec(), field);
                // Only replace the legacy fleet-data setup. The original standard rule
                // retains environment building, deployment, ship spawning and simulation.
                NativeWire.Set(spawner, "SelfFleetData", self);
                NativeWire.Set(spawner, "EnemyFleetData", enemy);
                PlayerFleetBattleTrace.Emit("spawner.complete", nativePlayerBattle, "fieldId=" + field.ID
                    + ";selfShips=" + self.ships.Count + ";enemyShips=" + enemy.ships.Count);
                return true;
            }
            catch (Exception error)
            {
                PlayerFleetBattleTrace.Emit("spawner.failed", nativePlayerBattle, "step=" + spawnStep + ";exception=" + error.GetType().Name, true);
                throw;
            }
        }
        private BattleEncounterEnvironmentSnapshot CreatePlayerBattleEnvironment(PlayerFleetBattleRecord battle, GalaxyStar galaxy)
        {
            BattleEncounterEnvironmentSnapshot environment; string error;
            AirShipFleetInfoBean actor = FleetView(battle.AttackerPlayerId, TransferFleet(battle.AttackerPlayerId, battle.AttackerFleetId));
            if (!Script.FreeWorldAirShip.Galaxy.GalaxyBattleEncounterEnvironmentBuilder.TryCreate(galaxy, actor, out environment, out error))
                throw new InvalidOperationException("无法从真实接触星系构建玩家战场环境：" + error);
            environment.VisualSeed = unchecked((int)(battle.NativeBattleUid ^ (battle.NativeBattleUid >> 32)));
            return environment;
        }
        private GameObject PlayerBattleEnvironmentRoot()
        {
            object builder = NativeWire.Get(BattleManager.Instance, "_envBuilder");
            GameObject root = NativeWire.Get(builder, "_environmentRoot") as GameObject;
            if (root == null) throw new InvalidOperationException("原生玩家战场环境尚未构建。");
            return root;
        }
        private static FleetBattleEnvironment DescribeBattleEnvironment(BattleEncounterEnvironmentSnapshot e)
        {
            return new FleetBattleEnvironment { Seed = e.VisualSeed, Profile = e.EnvironmentProfileId, HasStar = e.Stellar.HasPrimaryStar,
                StarType = (int)e.Stellar.StarType, StarX = e.Stellar.DirectionToStar.x, StarY = e.Stellar.DirectionToStar.y,
                Distance = e.Stellar.Distance01, Size = e.Stellar.ApparentSize01, Belt = e.AsteroidBelt.IsInside, BeltProfile = e.AsteroidBelt.ProfileId, Density = e.AsteroidBelt.Density01 };
        }
        private static BattleEncounterEnvironmentSnapshot RestoreBattleEnvironment(FleetBattleEnvironment e)
        {
            return new BattleEncounterEnvironmentSnapshot { VisualSeed = e.Seed, EnvironmentProfileId = e.Profile,
                Stellar = new BattleStellarVisualContext { HasPrimaryStar = e.HasStar, StarType = (BattleStarVisualType)e.StarType,
                    DirectionToStar = new Vector2(e.StarX, e.StarY), Distance01 = e.Distance, ApparentSize01 = e.Size },
                AsteroidBelt = new BattleAsteroidBeltVisualContext { IsInside = e.Belt, ProfileId = e.BeltProfile, Density01 = e.Density } };
        }
        private void SynchronizePlayerBattleClock()
        {
            BattleManager manager = BattleManager.Instance;
            bool paused = Context.IsPaused(); Time.timeScale = paused ? 0 : (float)Context.SimulationSpeed();
            applyingBattleClock = true;
            try
            {
                if (paused && (manager.GetCurrentState() == BattleManager.BattleState.Fighting || manager.GetCurrentState() == BattleManager.BattleState.Retreating))
                    NativeWire.Call(manager, "ApplyPause");
                else if (!paused && manager.GetCurrentState() == BattleManager.BattleState.Paused)
                {
                    FleetManager.Instance.ResumeAllShips();
                    NativeWire.Call(NativeWire.Get(manager, "_timeManager"), "ResumeTimer");
                    var previous = (BattleManager.BattleState)NativeWire.Get(manager, "stateBeforePause");
                    NativeWire.Call(manager, "ChangeState", previous == BattleManager.BattleState.Retreating ? previous : BattleManager.BattleState.Fighting);
                }
            }
            finally { applyingBattleClock = false; }
        }
        internal void SetPlayerBattleMenuFocus(object owner, bool paused)
        {
            if (owner == null) throw new ArgumentNullException("owner");
            var owners = (HashSet<object>)NativeWire.Get(BattleManager.Instance, "menuPauseOwners");
            if (paused) owners.Add(owner); else owners.Remove(owner);
        }
        private void SetPlayerBattleEnvironment(FleetBattleEnvironment description)
        {
            string encoded = NativeWire.Json(description);
            if (encoded == appliedBattleEnvironment) return;
            GameObject root = PlayerBattleEnvironmentRoot();
            BattleEncounterEnvironmentSnapshot environment = RestoreBattleEnvironment(description);
            string error; if (!environment.TryValidate(out error)) throw new InvalidOperationException("玩家舰战环境描述无效：" + error);
            foreach (BackgroundDepthAsteroidIndirectRenderer renderer in root.GetComponentsInChildren<BackgroundDepthAsteroidIndirectRenderer>(true))
                NativeWire.Set(renderer, "randomizeSeedOnRuntime", false);
            foreach (BattleEnvironmentProfile profile in root.GetComponentsInChildren<BattleEnvironmentProfile>(true)) profile.ApplyEncounterEnvironment(environment);
            appliedBattleEnvironment = encoded; battleEnvironment = description;
        }
        private BattleEnvironmentNodes PlayerBattleEnvironmentNodes(GameObject root)
        {
            if (battleEnvironmentNodes == null || battleEnvironmentNodes.Root != root.transform)
                battleEnvironmentNodes = new BattleEnvironmentNodes(root.transform);
            else battleEnvironmentNodes.Refresh();
            return battleEnvironmentNodes;
        }
        private static int BattleSceneComponentIndex(Component component)
        {
            Component[] peers = component is StrategicPoint
                ? component.GetComponents<StrategicPoint>().Cast<Component>().ToArray()
                : component.GetComponents<StaticObstacle>().Cast<Component>().ToArray();
            return Array.IndexOf(peers, component);
        }
        private static Component BattleSceneComponent(Transform node, FleetBattleSceneItem item)
        {
            if (node == null) return null;
            Component[] peers = item.Kind == "Point"
                ? node.GetComponents<StrategicPoint>().Cast<Component>().ToArray()
                : node.GetComponents<StaticObstacle>().Cast<Component>().ToArray();
            return item.ComponentIndex < peers.Length ? peers[item.ComponentIndex] : null;
        }
        private static bool ValidBattleScenePath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length > 512) return false;
            string[] parts = path.Split('/');
            if (parts.Any(p => p.Length == 0 || p == "." || p == "..")) return false;
            if (parts[0] != "@2") return true;
            for (int i = 1; i < parts.Length; i++)
            {
                string part = parts[i]; int split = part.LastIndexOf('~'), ordinal;
                if (split < 0 || !int.TryParse(part.Substring(split + 1), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out ordinal) || ordinal < 0
                    || ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture) != part.Substring(split + 1)) return false;
                string name = part.Substring(0, split);
                if (Uri.EscapeDataString(Uri.UnescapeDataString(name)) != name) return false;
            }
            return true;
        }
        private List<FleetBattleSceneItem> CapturePlayerBattleScene()
        {
            GameObject root = PlayerBattleEnvironmentRoot(); var items = new List<FleetBattleSceneItem>();
            BattleEnvironmentNodes nodes = PlayerBattleEnvironmentNodes(root);
            foreach (Component component in root.GetComponentsInChildren<StaticObstacle>(true).Cast<Component>()
                .Concat(root.GetComponentsInChildren<StrategicPoint>(true).Cast<Component>()))
            {
                Transform t = component.transform; StrategicPoint point = component as StrategicPoint;
                items.Add(new FleetBattleSceneItem { Path = nodes.Paths[t], Kind = point == null ? "Obstacle" : "Point", ComponentIndex = BattleSceneComponentIndex(component),
                    Active = component.gameObject.activeSelf, X = t.localPosition.x, Y = t.localPosition.y, Z = t.localPosition.z,
                    Rotation = t.localEulerAngles.z, Sx = t.localScale.x, Sy = t.localScale.y, Sz = t.localScale.z,
                    Owner = point == null ? 0 : (int)point.OwnerTeam, Capturing = point == null ? 0 : (int)point.CapturingTeam,
                    Progress = point == null ? 0 : point.CaptureProgress, Contested = point != null && point.IsContested });
            }
            if (items.Select(i => Tuple.Create(i.Path, i.Kind, i.ComponentIndex)).Distinct().Count() != items.Count)
                throw new InvalidOperationException("原生战场静态对象存在重复组件身份，不能生成歧义联机布局。");
            return items;
        }
        private static void ValidatePlayerBattleScene(FleetBattleState state)
        {
            if (state.Environment == null || state.Scene == null || state.Scene.Count > 10000) throw new InvalidOperationException("玩家战场缺少同一环境或静态布局。");
            string error; if (!RestoreBattleEnvironment(state.Environment).TryValidate(out error)) throw new InvalidOperationException("玩家战场环境字段无效：" + error);
            foreach (FleetBattleSceneItem item in state.Scene)
                if (item == null || !ValidBattleScenePath(item.Path) || item.ComponentIndex < 0 || item.ComponentIndex > 1023
                    || item.Kind != "Obstacle" && item.Kind != "Point" || new[] { item.X, item.Y, item.Z, item.Rotation, item.Sx, item.Sy, item.Sz, item.Progress }.Any(v => float.IsNaN(v) || float.IsInfinity(v))
                    || item.Progress < 0 || item.Progress > 1 || item.Owner < 0 || item.Owner > 2 || item.Capturing < 0 || item.Capturing > 2)
                    throw new InvalidOperationException("玩家战场静态布局含非法字段。");
            if (state.Scene.Select(i => Tuple.Create(i.Path, i.Kind, i.ComponentIndex)).Distinct().Count() != state.Scene.Count)
                throw new InvalidOperationException("玩家战场静态组件身份重复。");
            foreach (var group in state.Scene.GroupBy(i => i.Path, StringComparer.Ordinal))
            {
                FleetBattleSceneItem first = group.First();
                if (group.Any(i => i.Active != first.Active || i.X != first.X || i.Y != first.Y || i.Z != first.Z || i.Rotation != first.Rotation
                    || i.Sx != first.Sx || i.Sy != first.Sy || i.Sz != first.Sz))
                    throw new InvalidOperationException("同一战场静态节点的组件布局不一致。");
            }
        }
        private void ApplyPlayerBattleScene(FleetBattleState state)
        {
            SetPlayerBattleEnvironment(state.Environment);
            GameObject root = PlayerBattleEnvironmentRoot();
            BattleEnvironmentNodes nodes = PlayerBattleEnvironmentNodes(root);
            var known = new HashSet<Transform>(state.Scene.Select(i => nodes.Resolve(i.Path)).Where(t => t != null));
            foreach (Component component in root.GetComponentsInChildren<StaticObstacle>(true).Cast<Component>().Concat(root.GetComponentsInChildren<StrategicPoint>(true).Cast<Component>()))
                if (!known.Contains(component.transform)) component.gameObject.SetActive(false);
            BattleEnvironmentNodes templateNodes = null;
            foreach (FleetBattleSceneItem item in state.Scene)
            {
                Transform t = nodes.Resolve(item.Path);
                if (t == null)
                {
                    object builder = NativeWire.Get(BattleManager.Instance, "_envBuilder");
                    GameObject template = NativeWire.Get(builder, "_environmentTemplate") as GameObject;
                    if (templateNodes == null && template != null) templateNodes = new BattleEnvironmentNodes(template.transform);
                    Transform original = templateNodes == null ? null : templateNodes.Resolve(item.Path);
                    int split = item.Path.LastIndexOf('/'); Transform parent = split < 0 ? root.transform : nodes.Resolve(item.Path.Substring(0, split));
                    if (original == null || parent == null || BattleSceneComponent(original, item) == null)
                        throw new InvalidOperationException("本机原生战场模板无法恢复同一静态节点：" + item.Path);
                    GameObject clone = UnityEngine.Object.Instantiate(original.gameObject, parent, false); clone.name = original.name; t = clone.transform;
                    // Register the copied subtree with its original reserved addresses;
                    // appending it must not change identities of same-name siblings.
                    if (item.Path == "@2" || item.Path.StartsWith("@2/", StringComparison.Ordinal)) nodes.Index(t, item.Path, true);
                    else nodes.Refresh();
                }
                Component native = BattleSceneComponent(t, item);
                if (native == null) throw new InvalidOperationException("玩家战场静态节点的原生组件类型或序号不一致：" + item.Path);
                t.localPosition = new Vector3(item.X, item.Y, item.Z); t.localRotation = Quaternion.Euler(0, 0, item.Rotation); t.localScale = new Vector3(item.Sx, item.Sy, item.Sz);
                t.gameObject.SetActive(item.Active);
                if (item.Kind == "Point")
                {
                    StrategicPoint point = (StrategicPoint)native;
                    if (point == null) throw new InvalidOperationException("玩家战术据点的原生节点类型不一致。");
                    // Replica's native self/enemy teams are reversed relative to the simulator.
                    Func<int, TeamType> team = n => n == (int)TeamType.Player ? TeamType.Robot : n == (int)TeamType.Robot ? TeamType.Player : (TeamType)n;
                    NativeWire.Set(point, "_ownerTeam", team(item.Owner)); NativeWire.Set(point, "_capturingTeam", team(item.Capturing));
                    NativeWire.Set(point, "_captureProgress", item.Progress); NativeWire.Set(point, "_isContested", item.Contested); NativeWire.Call(point, "UpdateVisuals");
                }
            }
        }
    }
}
