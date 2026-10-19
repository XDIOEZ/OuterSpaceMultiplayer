using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using SmartAI;
using SmartAI.Data;
using SmartAI.Managers;

namespace FreeWorldMultiplayer
{
    public sealed partial class GameBridge
    {
        private sealed class FleetDeploymentShip
        {
            public long Uid;
            public bool Reserve, Seat, Flagship;
        }
        private sealed class FleetDeployment
        {
            public int Schema = 1;
            public int Formation;
            public List<FleetDeploymentShip> Ships = new List<FleetDeploymentShip>();
        }
        private sealed class NativeBattlePreparation
        {
            public PlayerFleetBattleRecord Battle;
            public AirShipFleetInfoBean Fleet;
            public PageBattleDeployment Page;
            public bool Submitted;
        }
        private NativeBattlePreparation nativeBattlePreparation;
        private bool resolvingPlayerBattleScale;

        private BattleScaleAllocation ResolvePlayerBattleScale(PlayerFleetBattleRecord battle)
        {
            FWFleet attacker = TransferFleet(battle.AttackerPlayerId, battle.AttackerFleetId);
            FWFleet defender = TransferFleet(battle.DefenderPlayerId, battle.DefenderFleetId);
            Func<FWFleet, int> total = fleet => fleet.ships.Where(s => s != null && s.getHP() > 0)
                .Sum(s => s.getDefBean().deploymentPoints);
            Func<FWFleet, int> minimum = fleet => fleet.ships.Where(s => s != null && s.getHP() > 0)
                .Select(s => s.getDefBean().deploymentPoints).DefaultIfEmpty(0).Min();
            if (attacker.ships.Concat(defender.ships).Any(s => s == null || s.getHP() > 0 &&
                (s.getDefBean() == null || s.getDefBean().deploymentPoints <= 0)))
                throw new NativeRejectionException("参战舰船缺少有效部署点数。");
            BattleScaleAllocation allocation; string reason;
            resolvingPlayerBattleScale = true;
            try
            {
                if (!BattleScaleRules.TryAllocate(BattleScaleDefinition.CreateDefault(), total(attacker), total(defender),
                    minimum(attacker), minimum(defender), out allocation, out reason))
                    throw new NativeRejectionException("原版战场部署配额无效：" + reason);
            }
            finally { resolvingPlayerBattleScale = false; }
            return allocation;
        }

        private FleetDeployment ReadPlayerBattleConfiguration(PlayerFleetBattleRecord battle, PlayerFleetBattleConfiguration configuration)
        {
            if (battle == null || configuration == null || configuration.BattleId != battle.BattleId ||
                configuration.AuthorityEpoch != battle.AuthorityEpoch || string.IsNullOrEmpty(configuration.NativeJson) ||
                configuration.NativeJson.Length > 262144)
                throw new NativeRejectionException("战前配置身份、任期或数据无效。");
            bool attacker = configuration.PlayerId == battle.AttackerPlayerId;
            if (!attacker && configuration.PlayerId != battle.DefenderPlayerId || configuration.FleetId !=
                (attacker ? battle.AttackerFleetId : battle.DefenderFleetId))
                throw new NativeRejectionException("只能确认本场本人舰队的战前配置。");
            FWFleet fleet = TransferFleet(configuration.PlayerId, configuration.FleetId);
            FleetDeployment deployment = NativeWire.Parse<FleetDeployment>(configuration.NativeJson);
            if (deployment == null || deployment.Schema != 1 || deployment.Formation != (int)FleetFormationType.Square ||
                deployment.Ships == null || deployment.Ships.Count == 0 || deployment.Ships.Count > 1024 ||
                deployment.Ships.Any(s => s == null) || deployment.Ships.Select(s => s.Uid).Distinct().Count() != deployment.Ships.Count ||
                !new HashSet<long>(fleet.ships.Select(s => s.getUID())).SetEquals(deployment.Ships.Select(s => s.Uid)))
                throw new NativeRejectionException("战前配置须保留本人完整且唯一的真实舰船名单。");
            var originals = fleet.ships.ToDictionary(s => s.getUID());
            int selectedDp = 0, seats = 0; bool reservesStarted = false;
            foreach (FleetDeploymentShip selected in deployment.Ships)
            {
                FWAirShip original = originals[selected.Uid];
                // The native deployment page can choose a seat and order, but cannot refit a
                // ship, grant a flagship, heal damage, or replace any persistent asset.
                if (selected.Flagship != original.isFlagShip || selected.Seat && selected.Reserve)
                    throw new NativeRejectionException("战前配置包含非法旗舰或座驾标记。");
                if (selected.Reserve) reservesStarted = true;
                else
                {
                    if (reservesStarted || original.getHP() <= 0)
                        throw new NativeRejectionException("首发顺序无效或包含已毁舰船。");
                    selectedDp = checked(selectedDp + original.getDefBean().deploymentPoints);
                }
                if (selected.Seat) seats++;
            }
            BattleScaleAllocation allocation = ResolvePlayerBattleScale(battle);
            if (seats != 1 || selectedDp <= 0 || selectedDp > (attacker ? allocation.PlayerQuotaDp : allocation.RobotQuotaDp))
                throw new NativeRejectionException("战前配置必须有一艘首发座驾，且首发部署点数不能超过原版配额。");
            return deployment;
        }

        public BridgeResult ValidatePlayerFleetBattleConfiguration(PlayerFleetBattleRecord battle, PlayerFleetBattleConfiguration configuration)
        { return Run(() => ReadPlayerBattleConfiguration(battle, configuration)); }

        public BridgeResult BeginPlayerFleetBattlePreparation(PlayerFleetBattleRecord battle)
        {
            return Run(() =>
            {
                if (battle == null || string.IsNullOrEmpty(battle.BattleId) || battle.Interrupted)
                    throw new NativeRejectionException("本场原版战前配置身份无效或已经中断。");
                bool attacker = Context.LocalPlayerId == battle.AttackerPlayerId;
                if (!attacker && Context.LocalPlayerId != battle.DefenderPlayerId)
                    throw new NativeRejectionException("非参战者不能打开战前配置。");
                if (battle.SceneAuthorized && battle.AttackerConfiguration != null && battle.DefenderConfiguration != null)
                { ClosePlayerBattlePreparation(battle); return; }
                if (battle.Stage != PlayerFleetBattleStage.Preparing || !battle.PreparationAuthorized || battle.SceneAuthorized)
                    throw new NativeRejectionException("房主尚未授权本场原版战前配置。");
                if ((attacker ? battle.AttackerConfiguration : battle.DefenderConfiguration) != null)
                { ClosePlayerBattlePreparation(battle); return; }
                if (nativeBattlePreparation != null)
                {
                    if (nativeBattlePreparation.Battle.BattleId == battle.BattleId &&
                        nativeBattlePreparation.Battle.AuthorityEpoch == battle.AuthorityEpoch) return;
                    throw new NativeRejectionException("本机已有另一场战前配置。");
                }
                if (nativePlayerBattle != null || BattleManager.Instance != null)
                    throw new NativeRejectionException("本机原生战场尚未释放。");
                string own = Context.LocalPlayerId, enemy = attacker ? battle.DefenderPlayerId : battle.AttackerPlayerId;
                long ownUid = attacker ? battle.AttackerFleetId : battle.DefenderFleetId;
                long enemyUid = attacker ? battle.DefenderFleetId : battle.AttackerFleetId;
                BattleScaleAllocation allocation = ResolvePlayerBattleScale(battle);
                using (EnterPlayer(own))
                {
                    AirShipFleetInfoBean ownFleet = DetachedPlayerBattleFleet(own, TransferFleet(own, ownUid));
                    AirShipFleetInfoBean enemyFleet = DetachedPlayerBattleFleet(enemy, TransferFleet(enemy, enemyUid));
                    // UI edits are isolated copies; closing or disconnecting never changes assets.
                    var preparation = new NativeBattlePreparation { Battle = battle, Fleet = ownFleet };
                    nativeBattlePreparation = preparation;
                    try
                    {
                        PageBattleDeployment page = preparation.Page = UIHelp.showPageBattleDeployment();
                        page.setCloseCallBack(() =>
                        {
                            if (nativeBattlePreparation != preparation || preparation.Submitted) return;
                            nativeBattlePreparation = null;
                            Context.CancelBattlePreparation(new PlayerFleetBattleReference { BattleId = battle.BattleId, AuthorityEpoch = battle.AuthorityEpoch });
                        });
                        page.InitSession(ownFleet.getListAirShipBeans().Where(s => s.bean.getHP() > 0).ToList(),
                            enemyFleet.getListAirShipBeans().Where(s => s.bean.getHP() > 0).ToList(), new List<AirShipDataBean>(),
                            enemyFleet.getName(), enemyFleet.bean.taskLv, attacker ? allocation.PlayerQuotaDp : allocation.RobotQuotaDp,
                            (vanguard, formation) =>
                            {
                                BridgeResult result = Run(() => ConfirmPlayerBattlePreparation(preparation, vanguard, formation));
                                if (!result.Success) FairyUIDispatcher.showToast(result.Error);
                            });
                    }
                    catch { ClosePlayerBattlePreparation(battle); throw; }
                }
            });
        }

        private void ConfirmPlayerBattlePreparation(NativeBattlePreparation preparation, List<DeploymentShipState> vanguard, FleetFormationType formation)
        {
            if (nativeBattlePreparation != preparation || preparation.Submitted) return;
            PlayerFleetBattleRecord battle = Context.GetFleetBattleLock(preparation.Fleet.getUid());
            if (battle == null || battle.BattleId != preparation.Battle.BattleId || battle.AuthorityEpoch != preparation.Battle.AuthorityEpoch ||
                battle.Stage != PlayerFleetBattleStage.Preparing || !battle.PreparationAuthorized || battle.SceneAuthorized)
                throw new NativeRejectionException("本场战前配置已取消或授权已失效。");
            if ((Context.LocalPlayerId == battle.AttackerPlayerId ? battle.AttackerConfiguration : battle.DefenderConfiguration) != null)
            { ClosePlayerBattlePreparation(battle); return; }
            var deployment = new FleetDeployment { Formation = (int)formation };
            foreach (DeploymentShipState state in vanguard)
                deployment.Ships.Add(new FleetDeploymentShip { Uid = state.ShipData.getUid(), Reserve = false,
                    Seat = state.IsSeat, Flagship = state.IsFlagship });
            var selected = new HashSet<long>(deployment.Ships.Select(s => s.Uid));
            foreach (AirShipDataBean reserve in preparation.Page.GetShipsForPanel(PanelRole.DeploymentReserve))
                if (selected.Add(reserve.getUid()))
                    deployment.Ships.Add(new FleetDeploymentShip { Uid = reserve.getUid(), Reserve = true, Flagship = reserve.bean.isFlagShip });
            foreach (FWAirShip ship in preparation.Fleet.bean.ships.Where(s => selected.Add(s.getUID())))
                deployment.Ships.Add(new FleetDeploymentShip { Uid = ship.getUID(), Reserve = true, Flagship = ship.isFlagShip });
            var configuration = new PlayerFleetBattleConfiguration { BattleId = battle.BattleId, AuthorityEpoch = battle.AuthorityEpoch,
                PlayerId = Context.LocalPlayerId, FleetId = preparation.Fleet.getUid(), NativeJson = NativeWire.Json(deployment) };
            ReadPlayerBattleConfiguration(battle, configuration);
            // Reliable enqueue is not host acceptance. Retain the original page until its
            // authoritative record contains this player's configuration, allowing rejection retry.
            Context.SubmitBattleConfiguration(configuration);
            if ((Context.LocalPlayerId == battle.AttackerPlayerId ? battle.AttackerConfiguration : battle.DefenderConfiguration) != null)
            { preparation.Submitted = true; ClosePlayerBattlePreparation(battle); }
        }

        private void ClosePlayerBattlePreparation(PlayerFleetBattleRecord battle)
        {
            NativeBattlePreparation preparation = nativeBattlePreparation;
            if (preparation == null || battle != null && preparation.Battle.BattleId != battle.BattleId) return;
            nativeBattlePreparation = null;
            if (preparation.Page != null)
            {
                preparation.Page.setCloseCallBack(null);
                preparation.Page.ConfirmBattleTransition();
                preparation.Page.closeDialog();
            }
        }

        public BridgeResult CancelPlayerFleetBattlePreparation(PlayerFleetBattleRecord battle)
        { return Run(() => ClosePlayerBattlePreparation(battle)); }

        private AirShipFleetInfoBean PreparedPlayerBattleFleet(PlayerFleetBattleRecord battle, string owner, FWFleet fleet)
        {
            PlayerFleetBattleConfiguration configuration = owner == battle.AttackerPlayerId ? battle.AttackerConfiguration : battle.DefenderConfiguration;
            FleetDeployment deployment = ReadPlayerBattleConfiguration(battle, configuration);
            FWFleet copy;
            using (EnterPlayer(owner)) { copy = NativeWire.Unpack<FWFleet>(NativeWire.Pack(fleet)); }
            var originals = copy.ships.ToDictionary(s => s.getUID());
            copy.ships.Clear();
            foreach (FleetDeploymentShip selected in deployment.Ships)
            {
                FWAirShip ship = originals[selected.Uid];
                if (ship.getHP() <= 0) continue;
                ship.isReserve = selected.Reserve; ship.setIsSeatShip(selected.Seat); ship.isFlagShip = selected.Flagship;
                copy.ships.Add(ship);
            }
            using (EnterPlayer(owner))
            { var view = new AirShipFleetInfoBean(); view.setData_WeaponArea(copy); return view; }
        }

        internal void ConfigurePlayerBattleAllocation(ref BattleScaleAllocation allocation, ref string reason, ref bool success)
        {
            if (!HasNativePlayerBattle || resolvingPlayerBattleScale) return;
            BattleScaleAllocation canonical = ResolvePlayerBattleScale(nativePlayerBattle);
            bool attacker = Context.LocalPlayerId == nativePlayerBattle.AttackerPlayerId;
            allocation = new BattleScaleAllocation { TotalActiveDp = canonical.TotalActiveDp, WasCapacityLimited = canonical.WasCapacityLimited,
                PlayerAvailableDp = attacker ? canonical.PlayerAvailableDp : canonical.RobotAvailableDp,
                RobotAvailableDp = attacker ? canonical.RobotAvailableDp : canonical.PlayerAvailableDp,
                PlayerQuotaDp = attacker ? canonical.PlayerQuotaDp : canonical.RobotQuotaDp,
                RobotQuotaDp = attacker ? canonical.RobotQuotaDp : canonical.PlayerQuotaDp, IsValid = true, Reason = "Accepted" };
            success = true; reason = "Accepted";
        }
    }

    internal static class NativePlayerFleetBattlePreparationHooks
    {
        internal static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(BattleReinforcementCoordinator), "BuildInitialPlans"),
                prefix: new HarmonyMethod(typeof(NativePlayerFleetBattlePreparationHooks), "DeploymentPlanPrefix"));
            harmony.Patch(AccessTools.Method(typeof(BattleReinforcementCoordinator), "SelectParticipantForWave"),
                postfix: new HarmonyMethod(typeof(NativePlayerFleetBattlePreparationHooks), "ReinforcementPostfix"));
            harmony.Patch(AccessTools.Method(typeof(BattleReinforcementCoordinator), "HasWaveDemand"),
                postfix: new HarmonyMethod(typeof(NativePlayerFleetBattlePreparationHooks), "WaveDemandPostfix"));
            harmony.Patch(AccessTools.Method(typeof(BattleScaleRules), "TryAllocate"),
                postfix: new HarmonyMethod(typeof(NativePlayerFleetBattlePreparationHooks), "AllocationPostfix"));
        }
        private static bool DeploymentPlanPrefix(BattleSideDeploymentPlan __0)
        {
            GameBridge bridge = GameBridge.Active;
            if (bridge == null || !bridge.HasNativePlayerBattle) return true;
            // Keep native team and control authority unchanged. Only deployment choices are
            // authoritative here; changing Robot to Player also changes retreat/seat ownership.
            int remaining = __0.DpQuota;
            foreach (BattleParticipantDeploymentPlan plan in __0.Participants)
            {
                plan.InitialSelection.Clear(); plan.Reinforcements.Clear();
                int selected = 0;
                foreach (FWAirShip ship in plan.SourceFleet.ships)
                {
                    if (ship.isReserve) { plan.Reinforcements.Add(ship); continue; }
                    int dp = ship.getDefBean().deploymentPoints;
                    if (dp <= 0 || dp > remaining) throw new InvalidOperationException("已确认首发舰队超过本场原版部署配额。");
                    plan.InitialSelection.Add(ship); selected += dp; remaining -= dp;
                }
                plan.TargetActiveDp = selected;
            }
            return false;
        }
        private static void ReinforcementPostfix(ref BattleParticipantDeploymentPlan __result)
        {
            GameBridge bridge = GameBridge.Active;
            if (bridge == null || !bridge.HasNativePlayerBattle || __result == null) return;
            if (__result.TargetActiveDp <= __result.ActiveDp + __result.PendingArrivalDp) __result = null;
        }
        private static void WaveDemandPostfix(BattleSideDeploymentPlan __0, ref bool __result)
        {
            GameBridge bridge = GameBridge.Active;
            if (bridge == null || !bridge.HasNativePlayerBattle) return;
            __result = __0 != null && __0.FreeDp > 0 && __0.Participants.Any(p => !p.Withdrawn && p.Reinforcements.Count > 0 &&
                p.TargetActiveDp > p.ActiveDp + p.PendingArrivalDp);
        }
        private static void AllocationPostfix(ref BattleScaleAllocation allocation, ref string reason, ref bool __result)
        {
            GameBridge bridge = GameBridge.Active;
            if (bridge != null) bridge.ConfigurePlayerBattleAllocation(ref allocation, ref reason, ref __result);
        }
    }
}
