using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SmartAI;
using SmartAI.Armor;
using SmartAI.Command;
using SmartAI.Core;
using SmartAI.Data;
using SmartAI.Gameplay;
using SmartAI.Managers;
using SmartAI.Systems;
using SmartAI.Utils;
using SmartAI.Weapon;
using UnityEngine;

namespace FreeWorldMultiplayer
{
    public sealed class PlayerBattleFightersView : MonoBehaviour
    {
        internal GameBridge Bridge;
        private void Update() { if (Bridge != null) Bridge.UpdatePlayerBattleFighters(); }
    }
    public sealed partial class GameBridge
    {
        private sealed class BattleFighterSlot
        {
            public int Index, Status;
            public long Fighter = -1;
            public float Progress, Remaining;
        }
        private sealed class BattleFighterWing
        {
            public long Carrier;
            public int Index, Reserves, Command;
            public string Bay;
            public float ReserveProgress;
            public List<BattleFighterSlot> Slots = new List<BattleFighterSlot>();
        }
        private sealed class BattleFighterState
        {
            public long Carrier, RuntimeId;
            public int Wing, Slot, Hull, MaxHull;
            public string Bay;
            public float X, Y, Z, Angle, Vx, Vy, Throttle, Flux, HardFlux, ArmorRatio, ShieldAngle, ShieldArc;
            public bool Shield, ShieldVisible, Boost;
            public Dictionary<int, float> Cells = new Dictionary<int, float>();
            public List<BattleWeaponEffect> Weapons = new List<BattleWeaponEffect>();
        }
        private sealed class BattleFightersState
        {
            public int Schema = 1;
            public long Sequence;
            public List<BattleFighterWing> Wings = new List<BattleFighterWing>();
            public List<BattleFighterState> Fighters = new List<BattleFighterState>();
        }
        private sealed class BattleFighterReplica
        {
            internal FighterShip Ship;
            internal FighterWingRuntime Wing;
            internal FighterSlot Slot;
            internal EngineController[] Engines;
            internal Vector3 From, To;
            internal float FromAngle, ToAngle;
            internal double Start, Duration;
        }
        private readonly Dictionary<BattleFighterReplicaIdentity, BattleFighterReplica> playerBattleFighters =
            new Dictionary<BattleFighterReplicaIdentity, BattleFighterReplica>();
        private readonly HashSet<FighterShip> endingPlayerBattleFighters = new HashSet<FighterShip>();
        // FleetManager removes a dying/retreating carrier before its native wing presentation
        // finishes. Retain only the already-proven scene reference/asset identity during that
        // native lifetime; this contains no copied fleet, inventory or authoritative state.
        private readonly Dictionary<long, BaseShip> playerBattleFighterCarriers = new Dictionary<long, BaseShip>();
        private readonly BattleEffectFrameSnapshot<long, BaseShip> fighterCarrierCaptureSnapshot =
            new BattleEffectFrameSnapshot<long, BaseShip>();
        private readonly HashSet<BaseShip> retainedReplicaFighterCarriers = new HashSet<BaseShip>();
        private GameObject playerBattleFighterRoot;
        private long playerBattleFighterSequence, appliedPlayerBattleFighterSequence;
        private double playerBattleFighterReceipt;
        private static readonly Func<BattleFighterSlot, int> BattleDeclaredSlotIndex = value => value.Index;
        private static readonly Func<BattleFighterSlot, long> BattleDeclaredSlotFighter = value => value.Fighter;
        private static readonly Func<FighterSlot, int> NativeReplicaSlotIndex = value => value.slotIndex;
        private static readonly Func<WeaponController, string> ReplicaFighterWeaponSlot =
            weapon => weapon.GetComponentInParent<WeaponSlot>().slotId;
        internal bool CanRunPlayerBattleFighterSimulation { get { return !HasNativePlayerBattle || CanRunPlayerBattle; } }
        private static BattleFighterWingIdentity FighterWingKey(long carrier, int wing)
        { return new BattleFighterWingIdentity(carrier, wing); }
        private static BattleFighterReplicaIdentity FighterReplicaKey(BattleFighterState fighter)
        { return new BattleFighterReplicaIdentity(fighter.Carrier, fighter.Wing, fighter.Slot, fighter.RuntimeId); }
        internal void MarkPlayerBattleFighterEnding(FighterShip fighter)
        { if (HasNativePlayerBattle && !IsPlayerBattleReplica && fighter != null) endingPlayerBattleFighters.Add(fighter); }
        internal void MarkPlayerBattleWingEnding(FighterWingRuntime wing)
        { if (HasNativePlayerBattle && !IsPlayerBattleReplica && wing != null && wing.slots != null) foreach (FighterSlot slot in wing.slots) MarkPlayerBattleFighterEnding(slot.ship as FighterShip); }
        internal void ForgetPlayerBattleFighter(FighterShip fighter)
        { if (!ReferenceEquals(fighter, null)) endingPlayerBattleFighters.Remove(fighter); }
        internal void RememberPlayerBattleFighterCarrier(BaseShip carrier)
        {
            if (!HasNativePlayerBattle || carrier == null || carrier.WingManager == null || !carrier.WingManager.HasActiveWings) return;
            long uid = BattleSourceUid(carrier);
            if (!nativeBattleShips.ContainsKey(uid)) throw new InvalidOperationException("舰载机母舰没有本场真实舰船身份。");
            NativeFighterCarrierAliases.RemoveOldIds(playerBattleFighterCarriers, uid, carrier);
            playerBattleFighterCarriers[uid] = carrier;
        }
        private void RetainReplicaFighterCarrier(BaseShip carrier)
        {
            if (!IsPlayerBattleReplica || carrier == null) throw new InvalidOperationException("只能保留本场副本的离场母舰表现。");
            // MarkRemovedFromBattle recorded the source UID before FleetManager removed its
            // origin mapping. Do not try to recover that identity from the runtime UID here.
            if (!playerBattleFighterCarriers.Values.Any(value => ReferenceEquals(value, carrier)))
                throw new InvalidOperationException("离场母舰表现没有已确认的原舰船身份。");
            retainedReplicaFighterCarriers.Add(carrier);
            carrier.gameObject.SetActive(false);
        }

        private string CapturePlayerBattleFighters()
        {
            var state = new BattleFightersState { Sequence = checked(playerBattleFighterSequence + 1) };
            var observed = new HashSet<FighterShip>();
            var currentCarriers = new HashSet<BaseShip>(FleetManager.Instance.GetAllShips().Values);
            foreach (BaseShip carrier in currentCarriers) RememberPlayerBattleFighterCarrier(carrier);
            var retainedCarriers = fighterCarrierCaptureSnapshot.Capture(playerBattleFighterCarriers);
            try
            {
                // A native wing/scene callback can remove a carrier while
                // we inspect its registered shell. Snapshot first, then
                // update the dictionary exactly as the old ToArray path did.
                foreach (var retained in retainedCarriers)
                {
                    BaseShip carrier = retained.Value;
                    if (carrier == null) { playerBattleFighterCarriers.Remove(retained.Key); continue; }
                    if (carrier.WingManager == null) throw new InvalidOperationException("已登记母舰丢失原生联队管理器。");
                    long carrierUid = retained.Key;
                    if (!nativeBattleShips.ContainsKey(carrierUid)) throw new InvalidOperationException("舰载机母舰不属于本场真实参战名单。");
                    foreach (FighterWingRuntime wing in carrier.WingManager.activeWings)
                    {
                        if (wing == null || wing.Data == null || wing.slots == null) throw new InvalidOperationException("原生舰载机联队缺少配置或槽位。");
                        var summary = new BattleFighterWing { Carrier = carrierUid, Index = wing.WingIndex, Bay = wing.Data.zoneSlotId,
                            Reserves = wing.currentReserves, ReserveProgress = wing.reserveProgress, Command = (int)carrier.WingManager.currentCommand };
                        foreach (FighterSlot slot in wing.slots)
                        {
                            FighterShip fighter = slot.ship as FighterShip;
                            bool alive = slot.status == SlotStatus.Active && fighter != null && fighter.gameObject.activeInHierarchy && !fighter.IsDead() && !endingPlayerBattleFighters.Contains(fighter);
                            summary.Slots.Add(new BattleFighterSlot { Index = slot.slotIndex, Status = (int)slot.status,
                                Progress = slot.progress, Remaining = slot.remainingTime, Fighter = alive ? fighter.GetUid() : -1 });
                            if (!alive) continue;
                            if (!ReferenceEquals(fighter.ParentCarrier, carrier) || !ReferenceEquals(fighter.ParentWing, wing) || !observed.Add(fighter))
                                throw new InvalidOperationException("原生舰载机的母舰、联队或槽位归属不一致。");
                            ArmorStateData armor = fighter.ArmorManager.GetArmorStateData();
                            ShieldEntity shield = fighter.ShieldManager == null ? null : NativeWire.Get(fighter.ShieldManager, "_entity") as ShieldEntity;
                            Vector2 velocity = fighter.GetVelocity();
                            var value = new BattleFighterState { Carrier = carrierUid, RuntimeId = fighter.GetUid(), Wing = wing.WingIndex, Slot = slot.slotIndex, Bay = wing.Data.zoneSlotId,
                                X = fighter.transform.position.x, Y = fighter.transform.position.y, Z = fighter.transform.position.z, Angle = fighter.transform.eulerAngles.z,
                                Vx = velocity.x, Vy = velocity.y, Throttle = fighter.Motor == null ? 0 : fighter.Motor.CurrentThrottle, Boost = fighter.Motor != null && fighter.Motor.IsBoosting,
                                Hull = fighter.StructureManager.CurrentHull, MaxHull = fighter.StructureManager.MaxHull,
                                ArmorRatio = armor.OverallHealthPercentage, Cells = new Dictionary<int, float>(armor.DamagedCells),
                                Flux = fighter.FluxManager == null ? 0 : fighter.FluxManager.CurrentFlux, HardFlux = fighter.FluxManager == null ? 0 : fighter.FluxManager.HardFlux,
                                Shield = fighter.ShieldManager != null && fighter.ShieldManager.IsShieldOn, ShieldVisible = shield != null && shield.IsVisible,
                                ShieldAngle = shield == null ? 0 : shield.transform.localEulerAngles.z, ShieldArc = fighter.ShieldManager == null ? 0 : (float)NativeWire.Get(fighter.ShieldManager, "_currentArcSize") };
                            if (fighter.WeaponManager != null) foreach (WeaponController weapon in fighter.WeaponManager.AllWeapons)
                            {
                                if (weapon == null || weapon.weaponData == null) continue;
                                WeaponSlot weaponSlot = weapon.GetComponentInParent<WeaponSlot>();
                                if (weaponSlot == null) throw new InvalidOperationException("原生舰载机武器没有正式插槽。");
                                var visual = new BattleWeaponEffect { Ship = fighter.GetUid(), Slot = weaponSlot.slotId, Resource = weapon.weaponData.id,
                                    Ammo = weapon.CurrentAmmo, Angle = weapon.rotatingPart == null ? 0 : weapon.rotatingPart.localEulerAngles.z };
                                BeamBehavior beam = weapon.GetComponent<BeamBehavior>();
                                if (beam != null) foreach (object emitter in EffectEmitters(beam))
                                {
                                    LineRenderer line = (LineRenderer)NativeWire.Get(emitter, "line");
                                    if (line == null || line.positionCount != 2) throw new InvalidOperationException("原生舰载机光束端点无效。");
                                    Vector3 a = line.GetPosition(0), b = line.GetPosition(1);
                                    visual.Beams.Add(new BattleBeamLine { Enabled = line.enabled, X = a.x, Y = a.y, Z = a.z, EndX = b.x, EndY = b.y, EndZ = b.z,
                                        Width = line.startWidth, EndWidth = line.endWidth, Charge = EffectActive(emitter, "chargeVFXObj"), Muzzle = EffectActive(emitter, "startVFXObj"), Hit = EffectActive(emitter, "endVFXHullObj") });
                                }
                                value.Weapons.Add(visual);
                            }
                            state.Fighters.Add(value);
                        }
                        // A departed carrier is retained only for fighters which are actually
                        // still flying. Empty terminal wings do not require a dead carrier shell
                        // on a peer which never observed that carrier's deployment.
                        if (currentCarriers.Contains(carrier) || summary.Slots.Any(s => s.Fighter >= 0)) state.Wings.Add(summary);
                    }
                }
            }
            finally { fighterCarrierCaptureSnapshot.Release(retainedCarriers); }
            // Native landing clears the slot before Despawn/Destroy, and no-pool Destroy
            // can complete at frame end. Death/landing/wing-disposal boundary hooks identify
            // that legal terminal interval; no timer or guessed orphan repair is involved.
            if (FighterManager.Instance != null && FighterManager.Instance.AllFighters.Any(f => f != null && f.gameObject.activeInHierarchy && !observed.Contains(f)
                && !endingPlayerBattleFighters.Contains(f) && !f.IsDead() && (f.StructureManager == null || f.StructureManager.CurrentHull > 0)))
                throw new InvalidOperationException("本场存在未绑定真实母舰槽位的舰载机。");
            ValidateFightersState(state);
            string result = NativeWire.Json(state); playerBattleFighterSequence = state.Sequence; return result;
        }
        private static void ValidateFightersState(BattleFightersState state)
        {
            if (state == null || state.Schema != 1 || state.Sequence <= 0 || state.Wings == null || state.Fighters == null || state.Wings.Count > 4096 || state.Fighters.Count > 8192)
                throw new InvalidOperationException("玩家舰战舰载机快照结构无效。");
            var wings = new Dictionary<BattleFighterWingIdentity, BattleFighterWing>();
            foreach (BattleFighterWing wing in state.Wings)
            {
                if (wing == null || wing.Carrier < 0 || wing.Index < 0 || wing.Index > 255 || String.IsNullOrEmpty(wing.Bay) || wing.Bay.Length > 128
                    || wing.Reserves < 0 || wing.Command < 0 || wing.Command > 2 || wing.Slots == null || wing.Slots.Count > 256
                    || float.IsNaN(wing.ReserveProgress) || float.IsInfinity(wing.ReserveProgress) || wing.ReserveProgress < 0 || wing.ReserveProgress > 1)
                    throw new InvalidOperationException("玩家舰载机联队身份或补充状态无效。");
                wings.Add(FighterWingKey(wing.Carrier, wing.Index), wing);
                var slots = new HashSet<int>();
                foreach (BattleFighterSlot slot in wing.Slots)
                    if (slot == null || !slots.Add(slot.Index) || slot.Index < 0 || slot.Index > 255 || slot.Status < 0 || slot.Status > 3
                        || float.IsNaN(slot.Progress) || float.IsInfinity(slot.Progress) || slot.Progress < 0 || slot.Progress > 1
                        || float.IsNaN(slot.Remaining) || float.IsInfinity(slot.Remaining) || slot.Remaining < 0 || slot.Fighter < -1
                        || slot.Fighter >= 0 && slot.Status != (int)SlotStatus.Active)
                        throw new InvalidOperationException("玩家舰载机槽位状态无效。");
            }
            var ids = new HashSet<BattleFighterReplicaIdentity>();
            // For densely populated fighter frames, resolve each active slot
            // directly instead of scanning all fighters once per wing slot.
            // Keep the old tiny-frame path without building an extra index.
            var slotMembership = state.Fighters.Count >= 8 ? new BattleFighterSlotMembership() : null;
            var occupied = slotMembership == null ? new HashSet<string>(StringComparer.Ordinal) : null;
            foreach (BattleFighterState fighter in state.Fighters)
            {
                BattleFighterWing wing;
                if (fighter == null || fighter.RuntimeId < 0 || !ids.Add(FighterReplicaKey(fighter))
                    || !(slotMembership == null
                        ? occupied.Add(FighterWingKey(fighter.Carrier, fighter.Wing).ToString() + "/" + fighter.Slot)
                        : slotMembership.TryRegister(fighter.Carrier, fighter.Wing, fighter.Slot, fighter.RuntimeId))
                    || !wings.TryGetValue(FighterWingKey(fighter.Carrier, fighter.Wing), out wing) || wing.Bay != fighter.Bay
                    || !(slotMembership == null
                        ? wing.Slots.Any(s => s.Index == fighter.Slot && s.Fighter == fighter.RuntimeId)
                        : slotMembership.DeclaredSlotMatches(fighter.Carrier, fighter.Wing, wing.Slots,
                            fighter.Slot, fighter.RuntimeId, BattleDeclaredSlotIndex, BattleDeclaredSlotFighter))
                    || fighter.Hull < 0 || fighter.MaxHull <= 0 || fighter.Hull > fighter.MaxHull || fighter.ArmorRatio < 0 || fighter.ArmorRatio > 1
                    || fighter.Flux < 0 || fighter.HardFlux < 0 || fighter.HardFlux > fighter.Flux || fighter.ShieldArc < 0 || fighter.ShieldArc > 360
                    || fighter.Cells == null || fighter.Cells.Count > 100000 || !BattleFighterFiniteFields.ValidCells(fighter.Cells)
                    || !BattleFighterFiniteFields.ValidTelemetry(fighter.X, fighter.Y, fighter.Z, fighter.Angle,
                        fighter.Vx, fighter.Vy, fighter.Throttle, fighter.Flux, fighter.HardFlux,
                        fighter.ArmorRatio, fighter.ShieldAngle, fighter.ShieldArc))
                    throw new InvalidOperationException("玩家舰载机身份、母舰槽位或伤损状态不一致。");
                // Reuse the very same effects weapon/beam validation without
                // allocating unrelated projectile/VFX/event DTO lists for
                // every fighter on every host/replica checkpoint.
                ValidateBattleWeaponEffects(fighter.Weapons);
                if (fighter.Weapons.Any(w => w.Ship != fighter.RuntimeId)) throw new InvalidOperationException("舰载机武器引用另一架战机。");
            }
            foreach (BattleFighterWing wing in state.Wings)
                foreach (BattleFighterSlot slot in wing.Slots)
                    if (slot.Fighter >= 0 && !(slotMembership == null
                        ? state.Fighters.Any(f => f.Carrier == wing.Carrier && f.Wing == wing.Index && f.Slot == slot.Index && f.RuntimeId == slot.Fighter)
                        : slotMembership.Matches(wing.Carrier, wing.Index, slot.Index, slot.Fighter)))
                        throw new InvalidOperationException("活动舰载机槽位缺少完整表现状态。");
        }
        private void ValidatePlayerBattleFighters(string encoded)
        { ValidateFightersState(NativeWire.Parse<BattleFightersState>(encoded)); }
        private static HashSet<long> PlayerBattleFighterCarrierIds(string encoded)
        { return new HashSet<long>(NativeWire.Parse<BattleFightersState>(encoded).Fighters.Select(f => f.Carrier)); }
        private FighterWingRuntime ResolveReplicaWing(BattleFighterWing value, out NativeFighterSlotLookup<FighterSlot> slotLookup)
        {
            if (!nativeBattleShips.ContainsKey(value.Carrier)) throw new InvalidOperationException("舰载机投影引用本场外母舰。");
            BaseShip carrier = FindBattleShip(value.Carrier);
            if (carrier != null) RememberPlayerBattleFighterCarrier(carrier);
            else playerBattleFighterCarriers.TryGetValue(value.Carrier, out carrier);
            if (carrier == null || carrier.WingManager == null) throw new InvalidOperationException("舰载机母舰尚未部署或已经离场。");
            FighterWingRuntime wing = carrier.WingManager.activeWings.SingleOrDefault(w => w.WingIndex == value.Index && w.Data != null && w.Data.zoneSlotId == value.Bay);
            if (wing == null || wing.Data.fighterShipVariant == null || wing.slots == null || wing.slots.Length != value.Slots.Count)
                throw new InvalidOperationException("本机真实母舰机库和联队配置不一致。");
            // Dense wing checkpoints used to rescan every native slot for
            // every incoming slot during both validation and state apply.
            // Keep sparse and malformed native arrays on the original path.
            slotLookup = wing.slots.Length >= 8
                ? NativeFighterSlotLookup<FighterSlot>.TryCreate(wing.slots, NativeReplicaSlotIndex)
                : null;
            foreach (BattleFighterSlot slot in value.Slots)
                if (!(slotLookup == null ? wing.slots.Any(s => s.slotIndex == slot.Index)
                    : slotLookup.Contains(slot.Index)))
                    throw new InvalidOperationException("本机真实舰载机槽位配置不一致。");
            return wing;
        }
        private static void DisableReplicaFighter(FighterShip fighter)
        {
            // Explicitly retain native renderer/particle animation only. Every manager/AI is
            // called solely by the authoritative native fighter, never by this shell.
            foreach (MonoBehaviour behaviour in fighter.GetComponentsInChildren<MonoBehaviour>(true))
                if (!(behaviour is WeaponVisuals)) behaviour.enabled = false;
            foreach (Rigidbody2D body in fighter.GetComponentsInChildren<Rigidbody2D>(true)) body.simulated = false;
            foreach (Collider2D collider in fighter.GetComponentsInChildren<Collider2D>(true)) collider.enabled = false;
            foreach (Collider collider in fighter.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
        }
        private BattleFighterReplica CreateReplicaFighter(BattleFighterState value, FighterWingRuntime wing)
        {
            if (playerBattleFighterRoot == null)
            {
                playerBattleFighterRoot = new GameObject("MultiplayerFighterVisuals");
                playerBattleFighterRoot.AddComponent<PlayerBattleFightersView>().Bridge = this;
            }
            GameObject prefab = BattleResourceManager.GetFighterShipPrefab();
            if (prefab == null) throw new InvalidOperationException("原生舰载机模板资源缺失。");
            GameObject instance = UnityEngine.Object.Instantiate(prefab, playerBattleFighterRoot.transform, false);
            FighterShip fighter = instance.GetComponent<FighterShip>();
            try
            {
                if (fighter == null) throw new InvalidOperationException("原生舰载机模板缺少FighterShip。");
                DisableReplicaFighter(fighter);
                BaseShip carrier = playerBattleFighterCarriers[value.Carrier];
                // Native Initialize performs hull/module/weapon/visual installation and owns
                // its private runtime ShipData clone. SetData/AI InitializeBrain/OnSpawn are
                // deliberately not called; these are not a second wing simulation or asset.
                using (EnterPlayer(BattleShipOwner(value.Carrier)))
                {
                    fighter.Initialize(value.RuntimeId, wing.Data.fighterShipVariant, carrier.GetTeamType());
                    fighter.InitializeFighter(carrier, wing);
                    if (fighter.Motor != null) fighter.Motor.Initialize(fighter);
                }
                DisableReplicaFighter(fighter);
                FighterSlot slot = wing.slots.Single(s => s.slotIndex == value.Slot);
                slot.ship = fighter;
                if (FighterManager.Instance != null) FighterManager.Instance.Register(fighter); // Native UI/selection registry only.
                instance.name = "MultiplayerFighter." + FighterReplicaKey(value).ToString();
                return new BattleFighterReplica { Ship = fighter, Wing = wing, Slot = slot, Engines = fighter.GetComponentsInChildren<EngineController>(true) };
            }
            catch
            {
                if (fighter != null) { DisableReplicaFighter(fighter); if (FighterManager.Instance != null) FighterManager.Instance.Unregister(fighter); }
                UnityEngine.Object.Destroy(instance); throw;
            }
        }
        private void ApplyPlayerBattleFighters(string encoded)
        {
            BattleFightersState state = NativeWire.Parse<BattleFightersState>(encoded); ValidateFightersState(state);
            if (!IsPlayerBattleReplica || state.Sequence <= appliedPlayerBattleFighterSequence) return;
            var wings = new Dictionary<BattleFighterWingIdentity, FighterWingRuntime>();
            Dictionary<BattleFighterWingIdentity, NativeFighterSlotLookup<FighterSlot>> nativeSlots = null;
            foreach (BattleFighterWing value in state.Wings)
            {
                NativeFighterSlotLookup<FighterSlot> slotLookup;
                BattleFighterWingIdentity key = FighterWingKey(value.Carrier, value.Index);
                wings.Add(key, ResolveReplicaWing(value, out slotLookup));
                if (slotLookup != null)
                {
                    if (nativeSlots == null) nativeSlots = new Dictionary<BattleFighterWingIdentity, NativeFighterSlotLookup<FighterSlot>>();
                    nativeSlots.Add(key, slotLookup);
                }
            }
            var present = new HashSet<BattleFighterReplicaIdentity>();
            foreach (BattleFighterState value in state.Fighters) present.Add(FighterReplicaKey(value));
            // Remove the previous generation before assigning a reused bay/slot to its new
            // runtime fighter. A returned/dead craft can never keep the new craft's slot.
            var stale = BattleFighterReplicaPruner.FindMissing(playerBattleFighters, present);
            if (stale != null)
                foreach (BattleFighterReplicaIdentity key in stale) RemoveReplicaFighter(key);
            double now = Time.timeAsDouble;
            double interval = playerBattleFighterReceipt == 0 ? 0 : Math.Min(0.25, Math.Max(0.01, now - playerBattleFighterReceipt));
            foreach (BattleFighterWing value in state.Wings)
            {
                BattleFighterWingIdentity wingKey = FighterWingKey(value.Carrier, value.Index);
                FighterWingRuntime wing = wings[wingKey];
                NativeFighterSlotLookup<FighterSlot> slotLookup;
                if (nativeSlots == null || !nativeSlots.TryGetValue(wingKey, out slotLookup)) slotLookup = null;
                wing.currentReserves = value.Reserves; wing.reserveProgress = value.ReserveProgress;
                wing.manager.currentCommand = (CarrierCommand)value.Command;
                foreach (BattleFighterSlot valueSlot in value.Slots)
                {
                    FighterSlot slot = slotLookup == null
                        ? wing.slots.Single(s => s.slotIndex == valueSlot.Index)
                        : slotLookup.Single(valueSlot.Index);
                    slot.status = (SlotStatus)valueSlot.Status; slot.progress = valueSlot.Progress; slot.remainingTime = valueSlot.Remaining;
                    if (valueSlot.Fighter < 0) slot.ship = null;
                }
                wing.manager.TriggerUIUpdate();
            }
            foreach (BattleFighterState value in state.Fighters)
            {
                BattleFighterReplicaIdentity key = FighterReplicaKey(value); BattleFighterReplica replica;
                if (!playerBattleFighters.TryGetValue(key, out replica) || replica.Ship == null)
                {
                    replica = CreateReplicaFighter(value, wings[FighterWingKey(value.Carrier, value.Wing)]);
                    playerBattleFighters[key] = replica;
                    replica.Ship.transform.position = new Vector3(value.X, value.Y, value.Z); replica.Ship.transform.rotation = Quaternion.Euler(0, 0, value.Angle);
                }
                FighterShip fighter = replica.Ship;
                replica.From = fighter.transform.position; replica.To = new Vector3(value.X, value.Y, value.Z);
                replica.FromAngle = fighter.transform.eulerAngles.z; replica.ToAngle = value.Angle; replica.Start = now; replica.Duration = interval;
                NativeWire.Set(fighter.StructureManager, "MaxHull", value.MaxHull); NativeWire.Set(fighter.StructureManager, "CurrentHull", value.Hull);
                Action<float, float> hullChanged = NativeWire.Get(fighter.StructureManager, "OnHullChanged") as Action<float, float>;
                if (hullChanged != null) hullChanged(value.Hull, value.MaxHull);
                fighter.ArmorManager.ApplyArmorStateData(new ArmorStateData { OverallHealthPercentage = value.ArmorRatio, DamagedCells = value.Cells });
                if (fighter.FluxManager != null) { NativeWire.Set(fighter.FluxManager, "CurrentFlux", value.Flux); NativeWire.Set(fighter.FluxManager, "HardFlux", value.HardFlux); }
                if (fighter.Motor != null)
                { NativeWire.Set(fighter.Motor, "_velocity", new Vector3(value.Vx, value.Vy, 0)); NativeWire.Set(fighter.Motor, "_simulatedThrottle", value.Throttle); NativeWire.Set(fighter.Motor, "_isBoosting", value.Boost); }
                if (fighter.ShieldManager != null)
                {
                    fighter.ShieldManager.ToggleShield(value.Shield); NativeWire.Set(fighter.ShieldManager, "_currentArcSize", value.ShieldArc);
                    ShieldEntity entity = NativeWire.Get(fighter.ShieldManager, "_entity") as ShieldEntity;
                    if (entity != null) { entity.transform.localRotation = Quaternion.Euler(0, 0, value.ShieldAngle); entity.SetVisible(value.ShieldVisible); entity.UpdateShape(value.ShieldArc); }
                }
                // Each SingleOrDefault used to walk every weapon and resolve its
                // Unity parent slot for each received visual. Multi-weapon
                // fighters index this exact frame's native components once.
                var weaponSlots = value.Weapons.Count > 1
                    ? new BattleWeaponSlotIndex<WeaponController>(fighter.WeaponManager.AllWeapons, ReplicaFighterWeaponSlot)
                    : null;
                foreach (BattleWeaponEffect visual in value.Weapons)
                {
                    WeaponController weapon = weaponSlots == null
                        ? fighter.WeaponManager.AllWeapons.SingleOrDefault(w => w != null && w.GetComponentInParent<WeaponSlot>().slotId == visual.Slot)
                        : weaponSlots.SingleOrDefault(visual.Slot);
                    if (weapon == null || weapon.weaponData == null || weapon.weaponData.id != visual.Resource) throw new InvalidOperationException("本机舰载机武器插槽资源不一致。");
                    if (weapon.rotatingPart != null) weapon.rotatingPart.localRotation = Quaternion.Euler(0, 0, visual.Angle);
                    NativeWire.Set(weapon, "CurrentAmmo", visual.Ammo);
                    BeamBehavior beam = weapon.GetComponent<BeamBehavior>();
                    if ((beam == null ? 0 : EffectEmitters(beam).Count) != visual.Beams.Count) throw new InvalidOperationException("本机舰载机光束发射器数量不一致。");
                    if (beam == null) continue;
                    IList emitters = EffectEmitters(beam);
                    for (int i = 0; i < visual.Beams.Count; i++)
                    {
                        BattleBeamLine line = visual.Beams[i]; object emitter = emitters[i]; LineRenderer renderer = (LineRenderer)NativeWire.Get(emitter, "line");
                        Vector3 start = new Vector3(line.X, line.Y, line.Z), end = new Vector3(line.EndX, line.EndY, line.EndZ);
                        renderer.SetPosition(0, start); renderer.SetPosition(1, end); renderer.startWidth = line.Width; renderer.endWidth = line.EndWidth; renderer.enabled = line.Enabled;
                        SetBeamParticle(emitter, "chargeVFXObj", line.Charge, start); SetBeamParticle(emitter, "startVFXObj", line.Muzzle, start); SetBeamParticle(emitter, "endVFXHullObj", line.Hit, end);
                    }
                }
                DisableReplicaFighter(fighter); // Shield/visual installation may have enabled a collider.
            }
            appliedPlayerBattleFighterSequence = state.Sequence; playerBattleFighterReceipt = now;
        }
        internal void UpdatePlayerBattleFighters()
        {
            if (!IsPlayerBattleReplica || Context.IsPaused()) return;
            double now = Time.timeAsDouble;
            foreach (BattleFighterReplica replica in playerBattleFighters.Values)
            {
                if (replica.Ship == null) continue;
                float t = replica.Duration <= 0 ? 1 : Mathf.Clamp01((float)((now - replica.Start) / replica.Duration));
                replica.Ship.transform.position = Vector3.Lerp(replica.From, replica.To, t);
                replica.Ship.transform.rotation = Quaternion.Euler(0, 0, Mathf.LerpAngle(replica.FromAngle, replica.ToAngle, t));
                replica.Ship.VisualManager?.UpdateLogic();
                // Native engine controller reads only the received motor telemetry. Avoid
                // EngineManager's mobility-stat writes and every fighter simulation manager.
                foreach (EngineController engine in replica.Engines) if (engine != null) engine.UpdateLogic();
            }
        }
        private void RemoveReplicaFighter(BattleFighterReplicaIdentity key)
        {
            BattleFighterReplica replica;
            if (!playerBattleFighters.TryGetValue(key, out replica)) return;
            playerBattleFighters.Remove(key);
            if (replica.Ship == null) return;
            if (replica.Slot != null && ReferenceEquals(replica.Slot.ship, replica.Ship)) replica.Slot.ship = null;
            DisableReplicaFighter(replica.Ship);
            if (FighterManager.Instance != null) FighterManager.Instance.Unregister(replica.Ship);
            replica.Ship.gameObject.SetActive(false); UnityEngine.Object.Destroy(replica.Ship.gameObject);
        }
        private void ClearPlayerBattleFighters()
        {
            var errors = new List<Exception>();
            foreach (BattleFighterReplicaIdentity key in playerBattleFighters.Keys.ToArray()) try { RemoveReplicaFighter(key); } catch (Exception error) { errors.Add(error); }
            // Removed replica carriers are not returned to the pool while their fighters
            // still reference their native wing configuration. Release only these explicitly
            // retained shells here, even when BattleContext has already been cleared.
            foreach (BaseShip carrier in retainedReplicaFighterCarriers)
                try
                {
                    if (carrier == null) continue;
                    if (PoolManager.Instance != null) PoolManager.Instance.Despawn(carrier.gameObject);
                    else UnityEngine.Object.Destroy(carrier.gameObject);
                }
                catch (Exception error) { errors.Add(error); }
            retainedReplicaFighterCarriers.Clear();
            playerBattleFighters.Clear(); endingPlayerBattleFighters.Clear(); playerBattleFighterCarriers.Clear();
            fighterCarrierCaptureSnapshot.Reset();
            if (playerBattleFighterRoot != null)
            {
                PlayerBattleFightersView view = playerBattleFighterRoot.GetComponent<PlayerBattleFightersView>(); if (view != null) view.Bridge = null;
                UnityEngine.Object.Destroy(playerBattleFighterRoot); playerBattleFighterRoot = null;
            }
            playerBattleFighterSequence = appliedPlayerBattleFighterSequence = 0; playerBattleFighterReceipt = 0;
            if (errors.Count != 0) throw new AggregateException("释放玩家舰战舰载机投影失败。", errors);
        }
    }
    internal static class NativePlayerFleetBattleFighterHooks
    {
        private static GameBridge Bridge { get { return GameBridge.Active; } }
        internal static void Install(Harmony harmony)
        {
            Patch(harmony, typeof(FighterShip), "Update"); Patch(harmony, typeof(FighterMotor), "Update");
            Patch(harmony, typeof(FighterAI), "UpdateBrainLogic"); Patch(harmony, typeof(WingCommander), "Update");
            Patch(harmony, typeof(WingCommander), "HandleCommandChanged"); Patch(harmony, typeof(WingCommander), "HandlePriorityTargetChanged");
            Patch(harmony, typeof(WingManager), "UpdateLogic"); Patch(harmony, typeof(FighterWingRuntime), "Tick");
            Patch(harmony, typeof(FighterWingRuntime), "LaunchFighterInternal");
            MethodInfo launch = AccessTools.Method(typeof(WingManager), "TryRequestLaunch");
            if (launch == null) throw new MissingMethodException(typeof(WingManager).FullName, "TryRequestLaunch");
            harmony.Patch(launch, prefix: new HarmonyMethod(typeof(NativePlayerFleetBattleFighterHooks), "LaunchPrefix"));
            harmony.Patch(AccessTools.Method(typeof(FighterShip), "OnFighterDeath"), prefix: new HarmonyMethod(typeof(NativePlayerFleetBattleFighterHooks), "DeathPrefix"));
            harmony.Patch(AccessTools.Method(typeof(WingManager), "OnFighterLanded"), prefix: new HarmonyMethod(typeof(NativePlayerFleetBattleFighterHooks), "LandedPrefix"));
            harmony.Patch(AccessTools.Method(typeof(FighterWingRuntime), "Dispose"), prefix: new HarmonyMethod(typeof(NativePlayerFleetBattleFighterHooks), "WingDisposePrefix"));
            harmony.Patch(AccessTools.Method(typeof(FighterManager), "Unregister"), postfix: new HarmonyMethod(typeof(NativePlayerFleetBattleFighterHooks), "Unregistered"));
            harmony.Patch(AccessTools.Method(typeof(BaseShip), "MarkRemovedFromBattle"), prefix: new HarmonyMethod(typeof(NativePlayerFleetBattleFighterHooks), "CarrierRemoving"));
            foreach (MethodInfo method in typeof(FighterShip).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.ReturnType == typeof(float) && m.Name.StartsWith("ApplyDamage", StringComparison.Ordinal)))
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(NativePlayerFleetBattleFighterHooks), "DamagePrefix"));
        }
        private static void Patch(Harmony h, Type type, string name)
        {
            MethodInfo method = AccessTools.Method(type, name); if (method == null) throw new MissingMethodException(type.FullName, name);
            h.Patch(method, prefix: new HarmonyMethod(typeof(NativePlayerFleetBattleFighterHooks), "SimulationPrefix"));
        }
        private static bool SimulationPrefix() { return Bridge == null || Bridge.CanRunPlayerBattleFighterSimulation; }
        private static void DeathPrefix(FighterShip __instance) { if (Bridge != null) Bridge.MarkPlayerBattleFighterEnding(__instance); }
        private static void LandedPrefix(ShipCore __0, FighterWingRuntime __1) { if (Bridge != null && __1 != null) Bridge.MarkPlayerBattleFighterEnding(__0 as FighterShip); }
        private static void WingDisposePrefix(FighterWingRuntime __instance) { if (Bridge != null) Bridge.MarkPlayerBattleWingEnding(__instance); }
        private static void Unregistered(FighterShip __0) { if (Bridge != null) Bridge.ForgetPlayerBattleFighter(__0); }
        private static void CarrierRemoving(BaseShip __instance) { if (Bridge != null) Bridge.RememberPlayerBattleFighterCarrier(__instance); }
        private static bool LaunchPrefix(ref bool __result) { if (SimulationPrefix()) return true; __result = false; return false; }
        private static bool DamagePrefix(ref float __result) { if (SimulationPrefix()) return true; __result = 0; return false; }
    }
}
