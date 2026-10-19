using System;
using System.Collections.Generic;
using System.Linq;
using SmartAI;
using SmartAI.Core;
using SmartAI.Data;
using SmartAI.Managers;
using SmartAI.Armor;
using SmartAI.Systems;
using UnityEngine;

namespace FreeWorldMultiplayer
{
    public sealed partial class GameBridge
    {
        private sealed class FleetBattleCrew { public string Zone; public int Hp; }
        private sealed class FleetBattleShip
        {
            public long Uid, Fleet;
            public int Hull, Armor;
            public bool Present, Dead, Retreated, Shield, ShieldVisible, Venting, Overloaded;
            public float X, Y, Rotation, Vx, Vy, AngularVelocity, Flux, HardFlux, ArmorRatio, ShieldAngle, ShieldArc;
            public List<float> WeaponAngles = new List<float>();
            public Dictionary<int, float> Cells = new Dictionary<int, float>();
            public List<FleetBattleCrew> Crew = new List<FleetBattleCrew>();
        }
        private sealed class FleetBattleState
        {
            public int Schema = 1;
            public FleetBattleEnvironment Environment;
            public List<FleetBattleSceneItem> Scene;
            public string Effects;
            public string Fighters;
            public List<FleetBattleShip> Ships = new List<FleetBattleShip>();
        }
        private PlayerFleetBattleRecord nativePlayerBattle;
        private readonly Dictionary<long, FleetBattleShip> nativeBattleShips = new Dictionary<long, FleetBattleShip>();
        private readonly HashSet<string> committedPlayerBattles = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<long>> playerBattleRosters = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
        private bool applyingBattleInput, finishingPlayerBattle, nativeBattleEnded, nativeBattleStarted;
        private string nativeBattleOutcome;
        internal bool HasNativePlayerBattle { get { return nativePlayerBattle != null && BattleContext.BattleUid == nativePlayerBattle.NativeBattleUid; } }
        internal bool IsPlayerBattleReplica { get { return HasNativePlayerBattle && nativePlayerBattle.SimulatorPlayerId != Context.LocalPlayerId; } }
        private PlayerFleetBattleRecord CurrentNativeBattle
        {
            get { return nativePlayerBattle == null ? null : Context.GetFleetBattleLock(nativePlayerBattle.AttackerFleetId) ?? nativePlayerBattle; }
        }
        private bool PlayerBattleActive { get { PlayerFleetBattleRecord b = CurrentNativeBattle; return b != null && b.Stage == PlayerFleetBattleStage.Active && !b.Interrupted && !nativeBattleEnded; } }
        private bool CanRunPlayerBattle { get { return HasNativePlayerBattle && !IsPlayerBattleReplica && PlayerBattleActive && !Context.IsPaused(); } }
        private long BattleSourceUid(BaseShip ship)
        {
            BattleShipOrigin origin;
            if (FleetManager.Instance != null && FleetManager.Instance.TryGetOrigin(ship, out origin)) return origin.SourceShipUid;
            return ship.GetShipData().uid;
        }
        private BaseShip FindBattleShip(long uid)
        { return FleetManager.Instance == null ? null : FleetManager.Instance.GetAllShips().Values.FirstOrDefault(s => s != null && BattleSourceUid(s) == uid); }
        private IEnumerable<Tuple<string, FWFleet>> BattleFleets(PlayerFleetBattleRecord battle)
        {
            yield return Tuple.Create(battle.AttackerPlayerId, TransferFleet(battle.AttackerPlayerId, battle.AttackerFleetId));
            yield return Tuple.Create(battle.DefenderPlayerId, TransferFleet(battle.DefenderPlayerId, battle.DefenderFleetId));
        }
        private FleetBattleShip ArchiveBattleShip(FWAirShip ship, long fleet)
        {
            return new FleetBattleShip { Uid = ship.getUID(), Fleet = fleet, Hull = ship.getHP(), Armor = ship.getArmor(), Cells = ship.GetDamageCell(),
                Crew = ship.getCrewFittings().Where(c => c != null).Select(c => new FleetBattleCrew { Zone = c.zoneSlotId, Hp = c.hp }).ToList() };
        }
        private AirShipFleetInfoBean DetachedPlayerBattleFleet(string owner, FWFleet fleet)
        {
            using (EnterPlayer(owner))
            {
                FWFleet copy = NativeWire.Unpack<FWFleet>(NativeWire.Pack(fleet));
                var view = new AirShipFleetInfoBean(); view.setData_WeaponArea(copy); return view;
            }
        }
        public BridgeResult BeginPlayerFleetBattle(PlayerFleetBattleRecord battle)
        {
            return Run(() =>
            {
                if (battle == null || string.IsNullOrEmpty(battle.BattleId) || battle.NativeBattleUid < 0)
                    throw new InvalidOperationException("玩家舰战缺少房主战斗身份。");
                bool attacker = Context.LocalPlayerId == battle.AttackerPlayerId;
                if (!attacker && Context.LocalPlayerId != battle.DefenderPlayerId) throw new InvalidOperationException("非参战者不能进入玩家舰战。");
                if (nativePlayerBattle != null)
                { if (nativePlayerBattle.BattleId == battle.BattleId && nativePlayerBattle.AuthorityEpoch == battle.AuthorityEpoch) return; throw new InvalidOperationException("本机已有另一场玩家舰战。"); }
                if (BattleManager.Instance != null) throw new NativeRejectionException("本机仍有尚未释放的原生战场。");
                if (!battle.SceneAuthorized || battle.AttackerConfiguration == null || battle.DefenderConfiguration == null)
                    throw new NativeRejectionException("双方原版战前配置尚未确认，不能创建战场。");
                // Validate both authoritative selections before allocating a native battle.
                ReadPlayerBattleConfiguration(battle, battle.AttackerConfiguration);
                ReadPlayerBattleConfiguration(battle, battle.DefenderConfiguration);
                ClosePlayerBattlePreparation(battle);
                var pairs = BattleFleets(battle).ToList();
                if (pairs.Any(p => p.Item2.ships.Count == 0)) throw new NativeRejectionException("参战舰队没有可部署舰船。");
                nativeBattleShips.Clear(); ClearPlayerBattleEffects(); ClearPlayerBattleFighters();
                foreach (var p in pairs) foreach (FWAirShip ship in p.Item2.ships) nativeBattleShips.Add(ship.getUID(), ArchiveBattleShip(ship, p.Item2.getUID()));
                nativePlayerBattle = battle; nativeBattleEnded = false; nativeBattleStarted = false; nativeBattleOutcome = null;
                string own = attacker ? battle.AttackerPlayerId : battle.DefenderPlayerId, enemy = attacker ? battle.DefenderPlayerId : battle.AttackerPlayerId;
                long ownUid = attacker ? battle.AttackerFleetId : battle.DefenderFleetId, enemyUid = attacker ? battle.DefenderFleetId : battle.AttackerFleetId;
                using (EnterPlayer(own))
                {
                    AirShipFleetInfoBean ownFleet = FleetView(own, TransferFleet(own, ownUid)), enemyFleet = FleetView(enemy, TransferFleet(enemy, enemyUid));
                    GalaxyStar galaxy = GalaxyMapData.getInstance().getGalaxyStar(ownFleet.getGalaxyId());
                    if (galaxy == null) throw new InvalidOperationException("玩家舰战物理星系尚未完成原生初始化。");
                    // Both are detached battle views of the same real fleet/ship identities.
                    // Persistent ships are touched only by the host's final per-ship commit.
                    BattleEncounterEnvironmentSnapshot environment = CreatePlayerBattleEnvironment(battle, galaxy);
                    battleEnvironment = DescribeBattleEnvironment(environment); appliedBattleEnvironment = null;
                    // This is the same standard two-fleet entry used by the original
                    // StartCustomBattleWithDeployment, now only after both original pages confirm.
                    BattleContext.EnterBattleTest(battle.NativeBattleUid, galaxy, PreparedPlayerBattleFleet(battle, own, ownFleet.bean), PreparedPlayerBattleFleet(battle, enemy, enemyFleet.bean), null, environment);
                }
            });
        }
        public bool IsPlayerFleetBattleReady(PlayerFleetBattleRecord battle)
        {
            if (!HasNativePlayerBattle || nativePlayerBattle.BattleId != battle.BattleId || BattleManager.Instance == null || FleetManager.Instance == null) return false;
            if (BattleManager.Instance.GetCurrentState() == BattleManager.BattleState.None) return false;
            SetPlayerBattleEnvironment(battleEnvironment);
            foreach (BaseShip ship in FleetManager.Instance.GetAllShips().Values)
            {
                if (ship == null || ship.GetShipData() == null) return false;
                if (!nativeBattleShips.ContainsKey(BattleSourceUid(ship))) throw new InvalidOperationException("原生部署生成了不属于本场双方的舰船。");
                if (IsPlayerBattleReplica) ConfigurePlayerBattleReplica(ship);
            }
            return FleetManager.Instance.GetAllShips().Count != 0;
        }
        private FleetBattleShip CaptureBattleShip(BaseShip ship, bool dead = false, bool retreated = false)
        {
            long uid = BattleSourceUid(ship); FleetBattleShip previous;
            if (!nativeBattleShips.TryGetValue(uid, out previous)) throw new InvalidOperationException("战斗舰船缺少真实资产映射：" + uid);
            ArmorStateData armor = ship.ArmorManager == null ? new ArmorStateData() : ship.ArmorManager.GetArmorStateData();
            ShieldEntity shield = ship.ShieldManager == null ? null : NativeWire.Get(ship.ShieldManager, "_entity") as ShieldEntity;
            Rigidbody2D body = ship.GetComponent<Rigidbody2D>();
            FleetBattleShip state = new FleetBattleShip { Uid = uid, Fleet = previous.Fleet, Present = true,
                Hull = dead ? 0 : Mathf.Max(0, Mathf.RoundToInt(ship.GetCurrentHull())), Armor = (int)NativeWire.Call(BattleManager.Instance, "CalculateArchiveArmor", ship),
                Cells = new Dictionary<int, float>(armor.DamagedCells), ArmorRatio = armor.OverallHealthPercentage,
                Dead = dead || ship.IsDead(), Retreated = retreated, X = ship.transform.position.x, Y = ship.transform.position.y,
                Rotation = ship.transform.eulerAngles.z, Vx = body == null ? 0 : body.velocity.x, Vy = body == null ? 0 : body.velocity.y,
                AngularVelocity = body == null ? 0 : body.angularVelocity, Shield = ship.ShieldManager != null && ship.ShieldManager.IsShieldOn,
                Flux = ship.FluxManager == null ? 0 : ship.FluxManager.CurrentFlux, HardFlux = ship.FluxManager == null ? 0 : ship.FluxManager.HardFlux,
                Venting = ship.FluxManager != null && ship.FluxManager.IsVenting, Overloaded = ship.FluxManager != null && ship.FluxManager.IsOverloaded,
                ShieldVisible = shield != null && shield.IsVisible, ShieldAngle = shield == null ? 0 : shield.transform.localEulerAngles.z,
                ShieldArc = ship.ShieldManager == null ? 0 : (float)NativeWire.Get(ship.ShieldManager, "_currentArcSize") };
            if (ship.WeaponManager != null) foreach (var weapon in ship.WeaponManager.AllWeapons)
                state.WeaponAngles.Add(weapon != null && weapon.rotatingPart != null ? weapon.rotatingPart.localEulerAngles.z : 0);
            FWAirShip asset = BattleFleets(nativePlayerBattle).SelectMany(p => p.Item2.ships).Single(s => s.getUID() == uid);
            BattleResultData recorded = dead ? (BattleResultData)NativeWire.Get(BattleManager.Instance, "_battleResult") : null;
            BattleShipResultData death = recorded == null ? null : recorded.Participants.SelectMany(p => p.Ships).SingleOrDefault(s => s.SourceShipUid == uid || s.Uid == uid);
            foreach (FWCrewFitting crew in asset.getCrewFittings().Where(c => c != null))
            {
                var runtime = ship.CrewManager == null ? null : ship.CrewManager.GetCrewInZone(crew.zoneSlotId);
                FWCrewFitting survival = death == null ? null : death.SurvivedCrews.Concat(death.DeadCrews).SingleOrDefault(c => c.zoneSlotId == crew.zoneSlotId);
                if (dead && survival == null) throw new InvalidOperationException("真实舰船死亡未记录本次乘员生还结果：" + uid + "/" + crew.zoneSlotId);
                state.Crew.Add(new FleetBattleCrew { Zone = crew.zoneSlotId, Hp = dead ? survival.hp : runtime != null ? Mathf.Max(0, (int)runtime.CurrentHull) : crew.hp });
            }
            nativeBattleShips[uid] = state; return state;
        }
        internal void RecordPlayerBattleShipExit(BaseShip ship, bool dead)
        {
            if (!HasNativePlayerBattle || IsPlayerBattleReplica || ship == null) return;
            CaptureBattleShip(ship, dead, !dead);
            // Native death/retreat's automatic seat picker only searches Team.Player. A
            // remotely piloted Robot ship must not switch the attacker's own selected seat.
            if (!IsLocalBattleInputShip(ship) && ship.IsPlayerSeat)
            {
                NativeWire.Set(NativeWire.Get(ship, "_controlComponent"), "_isPlayerSeat", false);
                NativeWire.Set(ship, "_isPlayerSeat", false);
            }
        }
        public PlayerFleetBattleFrame CapturePlayerFleetBattle(PlayerFleetBattleRecord battle)
        {
            if (!HasNativePlayerBattle || IsPlayerBattleReplica || nativePlayerBattle.BattleId != battle.BattleId)
                throw new InvalidOperationException("只有本场唯一战斗模拟者可捕获舰战。");
            FleetManager.Instance.CompletePendingDeaths();
            foreach (BaseShip ship in FleetManager.Instance.GetAllShips().Values.ToList()) if (ship != null) CaptureBattleShip(ship);
            return new PlayerFleetBattleFrame { BattleId = battle.BattleId, AuthorityEpoch = battle.AuthorityEpoch,
                BaseRevision = battle.Revision, Revision = battle.Revision + 1, Ended = nativeBattleEnded, Outcome = nativeBattleOutcome,
                NativeJson = NativeWire.Json(new FleetBattleState { Ships = nativeBattleShips.Values.OrderBy(s => s.Uid).ToList(), Environment = battleEnvironment, Scene = CapturePlayerBattleScene(), Effects = CapturePlayerBattleEffects(), Fighters = CapturePlayerBattleFighters() }) };
        }
        private FleetBattleState ValidateBattleFrame(PlayerFleetBattleRecord battle, PlayerFleetBattleFrame frame, bool allowCommitted)
        {
            if (battle == null || frame == null || frame.BattleId != battle.BattleId || frame.AuthorityEpoch != battle.AuthorityEpoch)
                throw new InvalidOperationException("舰战检查点身份或任期不一致。");
            FleetBattleState state = NativeWire.Parse<FleetBattleState>(frame.NativeJson);
            if (state == null || state.Schema != 1 || state.Ships == null || state.Ships.Count > 1024 || state.Ships.Select(s => s.Uid).Distinct().Count() != state.Ships.Count)
                throw new InvalidOperationException("舰战检查点结构无效。");
            ValidatePlayerBattleScene(state);
            ValidatePlayerBattleEffects(state.Effects);
            ValidatePlayerBattleFighters(state.Fighters);
            var roster = BattleFleets(battle).SelectMany(p => p.Item2.ships.Select(s => Tuple.Create(p.Item2.getUID(), s))).ToDictionary(p => p.Item2.getUID());
            HashSet<long> enrolled;
            if (!playerBattleRosters.TryGetValue(battle.BattleId, out enrolled))
            {
                enrolled = new HashSet<long>(state.Ships.Select(s => s.Uid));
                if (battle.Stage == PlayerFleetBattleStage.Preparing && battle.Revision == 0 && !enrolled.SetEquals(roster.Keys))
                    throw new InvalidOperationException("舰战首次检查点未包含双方完整真实舰船名单。");
            }
            if (!enrolled.SetEquals(state.Ships.Select(s => s.Uid)))
                throw new InvalidOperationException("舰战检查点与双方真实舰船名单不一致。");
            foreach (FleetBattleShip ship in state.Ships)
            {
                Tuple<long, FWAirShip> original;
                if (!roster.TryGetValue(ship.Uid, out original) || original.Item1 != ship.Fleet) throw new InvalidOperationException("舰战检查点舰船不属于真实参战舰队。");
                float[] numbers = { ship.X, ship.Y, ship.Rotation, ship.Vx, ship.Vy, ship.AngularVelocity, ship.Flux, ship.HardFlux, ship.ArmorRatio, ship.ShieldAngle, ship.ShieldArc };
                if (numbers.Any(n => float.IsNaN(n) || float.IsInfinity(n)) || ship.Hull < 0 || ship.Armor < 0 || ship.Flux < 0 || ship.HardFlux < 0 || ship.HardFlux > ship.Flux
                    || ship.ArmorRatio < 0 || ship.ArmorRatio > 1 || ship.Cells == null || ship.Cells.Count > 100000
                    || ship.Cells.Values.Any(v => float.IsNaN(v) || v < 0 || v > 1) || ship.Crew == null || ship.Crew.Count > 256
                    || ship.Crew.Any(c => c == null || c.Hp < 0 || !original.Item2.getCrewFittings().Any(o => o.zoneSlotId == c.Zone))
                    || ship.Crew.Select(c => c.Zone).Distinct().Count() != ship.Crew.Count || ship.Dead && ship.Hull != 0
                    || ship.ShieldArc < 0 || ship.ShieldArc > 360 || ship.WeaponAngles == null || ship.WeaponAngles.Count > 512 || ship.WeaponAngles.Any(v => float.IsNaN(v) || float.IsInfinity(v)))
                    throw new InvalidOperationException("舰战检查点包含非法伤损或乘员状态。");
            }
            if (!playerBattleRosters.ContainsKey(battle.BattleId)) playerBattleRosters.Add(battle.BattleId, enrolled);
            return state;
        }
        public BridgeResult ApplyPlayerFleetBattleFrame(PlayerFleetBattleRecord battle, PlayerFleetBattleFrame frame)
        {
            return Run(() =>
            {
                FleetBattleState state = ValidateBattleFrame(battle, frame, false);
                // A third-party host validates the same fixed schema and real UID roster without
                // entering its singleton BattleContext. It keeps the confirmed frame in Session.
                // In Preparing, even a participant's Begin has not guaranteed its async native
                // BattleManager/environment is ready. Apply the initial frame only at Active.
                if (battle.Stage == PlayerFleetBattleStage.Preparing || !HasNativePlayerBattle || !IsPlayerBattleReplica) return;
                HashSet<long> fighterCarriers = PlayerBattleFighterCarrierIds(state.Fighters);
                applyingBattleInput = true;
                try { ApplyPlayerBattleScene(state); foreach (FleetBattleShip ship in state.Ships)
                {
                    BaseShip runtime = FindBattleShip(ship.Uid);
                    bool registered = runtime != null;
                    bool retainedForFighters = fighterCarriers.Contains(ship.Uid);
                    if (runtime == null && retainedForFighters) playerBattleFighterCarriers.TryGetValue(ship.Uid, out runtime);
                    if (runtime == null && (ship.Present && !ship.Dead && !ship.Retreated || retainedForFighters))
                    {
                        string owner = ship.Fleet == battle.AttackerFleetId ? battle.AttackerPlayerId : battle.DefenderPlayerId;
                        FWFleet fleet = TransferFleet(owner, ship.Fleet);
                        TeamType team = owner == Context.LocalPlayerId ? TeamType.Player : TeamType.Robot;
                        BattleParticipantDefinition participant = BattleParticipantDefinition.CreateLegacy(team, ship.Fleet);
                        participant.fleetSnapshot = PreparedPlayerBattleFleet(battle, owner, fleet);
                        runtime = BattleManager.Instance.SpawnReinforcement(participant.fleetSnapshot.bean.ships.Single(s => s.getUID() == ship.Uid), participant,
                            new Vector3(ship.X, ship.Y), new Vector3(ship.X, ship.Y), null);
                        if (runtime == null) throw new InvalidOperationException("原生舰战增援投影无法构建：" + ship.Uid);
                        runtime.CompleteBattleEntry(); runtime.SetNormal(); ConfigurePlayerBattleReplica(runtime);
                        registered = true;
                    }
                    if (runtime == null) { nativeBattleShips[ship.Uid] = ship; continue; }
                    if (!ship.Present) { runtime.BeginBattleEntry(); runtime.gameObject.SetActive(false); nativeBattleShips[ship.Uid] = ship; continue; }
                    if (!ship.Dead && !ship.Retreated) runtime.gameObject.SetActive(true);
                    if (!ship.Dead && !ship.Retreated) runtime.CompleteBattleEntry();
                    QueuePlayerBattlePose(runtime, ship, appliedNativeBattleRevision == 0 || frame.BaseRevision != appliedNativeBattleRevision);
                    Rigidbody2D body = runtime.GetComponent<Rigidbody2D>();
                    if (body != null) { body.simulated = false; body.velocity = new Vector2(ship.Vx, ship.Vy); body.angularVelocity = ship.AngularVelocity; }
                    NativeWire.Set(runtime.StructureManager, "CurrentHull", ship.Hull);
                    Action<float, float> hullChanged = NativeWire.Get(runtime.StructureManager, "OnHullChanged") as Action<float, float>;
                    if (hullChanged != null) hullChanged(ship.Hull, runtime.StructureManager.MaxHull);
                    runtime.ArmorManager.ApplyArmorStateData(new ArmorStateData { OverallHealthPercentage = ship.ArmorRatio, DamagedCells = ship.Cells });
                    if (runtime.FluxManager != null)
                    {
                        NativeWire.Set(runtime.FluxManager, "CurrentFlux", ship.Flux); NativeWire.Set(runtime.FluxManager, "HardFlux", ship.HardFlux);
                        NativeWire.Set(runtime.FluxManager, "IsVenting", ship.Venting); NativeWire.Set(runtime.FluxManager, "IsOverloaded", ship.Overloaded);
                        Action<float, float> changed = NativeWire.Get(runtime.FluxManager, "OnFluxChanged") as Action<float, float>;
                        if (changed != null) changed(ship.Flux, runtime.FluxManager.MaxFlux);
                    }
                    if (runtime.ShieldManager != null)
                    {
                        runtime.ShieldManager.ToggleShield(ship.Shield);
                        NativeWire.Set(runtime.ShieldManager, "_currentArcSize", ship.ShieldArc);
                        ShieldEntity entity = NativeWire.Get(runtime.ShieldManager, "_entity") as ShieldEntity;
                        if (entity != null) { entity.transform.localRotation = Quaternion.Euler(0, 0, ship.ShieldAngle); entity.SetVisible(ship.ShieldVisible); entity.UpdateShape(ship.ShieldArc); }
                    }
                    if (runtime.CrewManager != null) foreach (FleetBattleCrew crew in ship.Crew)
                    { var member = runtime.CrewManager.GetCrewInZone(crew.Zone); if (member != null) member.SetCurrentHull(crew.Hp); }
                    if (runtime.WeaponManager != null)
                    {
                        if (runtime.WeaponManager.AllWeapons.Count != ship.WeaponAngles.Count) throw new InvalidOperationException("本场真实舰船的武器槽位与权威帧不一致。");
                        for (int index = 0; index < ship.WeaponAngles.Count; index++)
                        { var weapon = runtime.WeaponManager.AllWeapons[index]; if (weapon != null && weapon.rotatingPart != null) weapon.rotatingPart.localRotation = Quaternion.Euler(0, 0, ship.WeaponAngles[index]); }
                    }
                    if (ship.Dead || ship.Retreated)
                    {
                        if (ship.Dead) NativeWire.Set(NativeWire.Get(runtime, "_stateComponent"), "m_CurrentState", ShipState.Dead);
                        NativeWire.Set(NativeWire.Get(runtime, "_controlComponent"), "_isPlayerSeat", false); NativeWire.Set(runtime, "_isPlayerSeat", false);
                        if (registered)
                        {
                            if (runtime.IsFlagship) NativeWire.Call(FleetManager.Instance, "RevokeFlagship", runtime);
                            NativeWire.Call(FleetManager.Instance, "RemoveShipFromLists", runtime);
                        }
                        playerBattlePoses.Remove(ship.Uid);
                        // Its real wing configuration may still back authoritative fighters.
                        // Keep this removed shell hidden, outside every live fleet/seat list.
                        if (retainedForFighters) RetainReplicaFighterCarrier(runtime);
                        else if (PoolManager.Instance != null) PoolManager.Instance.Despawn(runtime.gameObject);
                        else UnityEngine.Object.Destroy(runtime.gameObject);
                    }
                    nativeBattleShips[ship.Uid] = ship;
                }
                    if (FleetManager.Instance.GetPlayerSeat() == null)
                    {
                        BaseShip next = FleetManager.Instance.GetPlayerControllableShips().FirstOrDefault(s => s != null && s.gameObject.activeInHierarchy && !s.IsDead() && BattlePresenceRules.CanReceiveOrders(s));
                        if (next != null) FleetManager.Instance.TrySetPlayerSeat(next);
                        else { Action<BaseShip> changed = NativeWire.Get(FleetManager.Instance, "OnPlayerShipChanged") as Action<BaseShip>; if (changed != null) changed(null); }
                    }
                    ApplyPlayerBattleFighters(state.Fighters); ApplyPlayerBattleEffects(state.Effects); appliedNativeBattleRevision = frame.Revision; if (frame.Ended) nativeBattleEnded = true;
                } finally { applyingBattleInput = false; }
            });
        }
        public BridgeResult CommitPlayerFleetBattleResult(PlayerFleetBattleRecord battle, PlayerFleetBattleFrame frame)
        {
            return Run(() =>
            {
                if (!Context.IsHost) throw new InvalidOperationException("只有房主可写入双方真实舰战战果。");
                if (battle.AssetsCommitted) return;
                if (committedPlayerBattles.Contains(battle.BattleId)) return;
                FleetBattleState state = ValidateBattleFrame(battle, frame, false);
                foreach (var pair in BattleFleets(battle).ToList()) using (EnterPlayer(pair.Item1))
                {
                    foreach (FleetBattleShip result in state.Ships.Where(s => s.Fleet == pair.Item2.getUID()))
                    {
                        FWAirShip ship = pair.Item2.ships.Single(s => s.getUID() == result.Uid);
                        ship.setHP(result.Hull); ship.setArmor(result.Armor); ship.SetDamageCell(result.Cells);
                        foreach (FWCrewFitting crew in ship.getCrewFittings().ToList())
                        {
                            FleetBattleCrew updated = result.Crew.SingleOrDefault(c => c.Zone == crew.zoneSlotId);
                            if (updated == null) continue;
                            crew.setHp(updated.Hp);
                            long uid;
                            if (long.TryParse(crew.heroUid, out uid) && uid > 0)
                            {
                                FWHeroBean hero = PlayerHeroes(pair.Item1).SingleOrDefault(h => h.getUID() == uid);
                                if (hero != null)
                                {
                                    hero.setHp(updated.Hp); if (updated.Hp == 0) hero.dead = true;
                                    if (result.Dead && updated.Hp > 0)
                                    {
                                        hero.setOnShip(false); hero.starMapId = -1; hero.fleetUid = pair.Item2.getUID();
                                        if (!pair.Item2.getHeros().Any(h => h.getUID() == uid)) pair.Item2.AddHero(hero);
                                        Players[pair.Item1].heros.Remove(hero);
                                        // Remove only the old dead-ship fitting link, so native
                                        // BattleOver cannot delete this same surviving person.
                                        crew.heroUid = "-1";
                                    }
                                }
                            }
                        }
                        if (!result.Dead) ship.SetCrewFittings(ship.getCrewFittings().Where(c => c.hp > 0).ToList());
                    }
                    // Native consumption handles destroyed ships, crew links, seat reassignment
                    // and the game's last-seat survival rule; cargo/progress are never replaced.
                    FleetView(pair.Item1, pair.Item2).BattleOver();
                }
                committedPlayerBattles.Add(battle.BattleId);
            });
        }
        public BridgeResult StopPlayerFleetBattle(PlayerFleetBattleRecord battle)
        {
            return Run(() =>
            {
                ClosePlayerBattlePreparation(battle);
                playerBattleRosters.Remove(battle.BattleId); committedPlayerBattles.Remove(battle.BattleId);
                if (nativePlayerBattle == null || nativePlayerBattle.BattleId != battle.BattleId) return;
                finishingPlayerBattle = true;
                bool stopped = false; var failures = new List<Exception>();
                try
                {
                    if (BattleManager.Instance != null)
                    { NativeWire.Call(BattleManager.Instance, "ChangeState", BattleManager.BattleState.Settling); BattleManager.Instance.FinishSettlement(); }
                    BattleContext.ClearContext(); Time.timeScale = 1;
                    stopped = true;
                }
                catch (Exception error) { failures.Add(error); }
                finally
                {
                    try { ClearPlayerBattleEffects(); } catch (Exception error) { failures.Add(error); }
                    try { ClearPlayerBattleFighters(); } catch (Exception error) { failures.Add(error); }
                    finally { finishingPlayerBattle = false; if (stopped) ResetPlayerBattleRuntime(); else nativeBattleEnded = true; }
                }
                if (failures.Count != 0) throw new AggregateException("释放已结算玩家战场失败；保留原始清理异常。", failures);
            });
        }
        internal bool InterceptPlayerBattleEnd(BattleResultData.ResultType outcome)
        {
            if (!HasNativePlayerBattle || finishingPlayerBattle) return false;
            if (!IsPlayerBattleReplica)
            { FleetManager.Instance.CompletePendingDeaths(); nativeBattleEnded = true; nativeBattleOutcome = outcome.ToString(); }
            return true;
        }
        internal bool UpdatePlayerBattleManager()
        {
            if (!HasNativePlayerBattle) return true;
            ActivatePreparedPlayerBattleShips();
            SynchronizePlayerBattleClock();
            if (IsPlayerBattleReplica && PlayerBattleActive && BattleManager.Instance.GetCurrentState() == BattleManager.BattleState.Entering)
            { nativeBattleStarted = true; NativeWire.Call(BattleManager.Instance, "ChangeState", BattleManager.BattleState.Fighting); }
            if (!IsPlayerBattleReplica && PlayerBattleActive && !nativeBattleStarted && BattleManager.Instance.GetCurrentState() == BattleManager.BattleState.Entering)
                BattleManager.Instance.StartBattle();
            return CanRunPlayerBattle;
        }
        private void ReleasePlayerBattleOnDispose()
        {
            ClosePlayerBattlePreparation(null);
            if (nativePlayerBattle == null) return;
            var failures = new List<Exception>();
            try
            {
                if (HasNativePlayerBattle)
                {
                    if (BattleManager.Instance != null)
                    {
                        NativeWire.Call(BattleManager.Instance, "ClearBattleData", true);
                        UnityEngine.Object.DestroyImmediate(BattleManager.Instance.gameObject);
                    }
                    BattleContext.ClearContext(); AirShipUIUtils.DisposeBattleForContextChange(); Time.timeScale = 1;
                }
            }
            catch (Exception error) { failures.Add(error); }
            finally
            {
                try { ClearPlayerBattleEffects(); } catch (Exception error) { failures.Add(error); }
                try { ClearPlayerBattleFighters(); } catch (Exception error) { failures.Add(error); }
                finally { ResetPlayerBattleRuntime(); playerBattleRosters.Clear(); committedPlayerBattles.Clear(); }
            }
            if (failures.Count != 0) throw new AggregateException("会话退出时玩家战场释放失败。", failures);
        }
        private void ResetPlayerBattleRuntime()
        {
            nativePlayerBattle = null; nativeBattleShips.Clear(); battlePilotControls.Clear(); battlePilotSendTimes.Clear();
            replicaManualPilots.Clear(); preparingBattleShips.Clear(); playerBattlePoses.Clear(); appliedNativeBattleRevision = 0;
            battleEnvironment = null; appliedBattleEnvironment = null; nativeBattleStarted = false;
        }
    }
}
