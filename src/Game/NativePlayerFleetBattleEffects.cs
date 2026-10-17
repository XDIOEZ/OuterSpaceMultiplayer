using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SmartAI;
using SmartAI.Core;
using SmartAI.Data;
using SmartAI.Managers;
using SmartAI.Utils;
using SmartAI.Weapon;
using UnityEngine;

namespace FreeWorldMultiplayer
{
    // This driver only interpolates received visual positions. It never advances a weapon,
    // projectile, missile, collision, payload, ammunition or battle rule.
    public sealed class PlayerBattleEffectsView : MonoBehaviour
    {
        internal GameBridge Bridge;
        private void Update() { if (Bridge != null) Bridge.UpdatePlayerBattleEffects(); }
    }

    public sealed partial class GameBridge
    {
        private sealed class BattleEffectPose
        {
            public long Id;
            public string Resource;
            public int Team;
            public float X, Y, Z, Angle, Sx = 1, Sy = 1, Sz = 1;
        }
        private sealed class BattleEffectEvent
        {
            public long Serial;
            public int Kind; // 1=projectile born, 2=projectile ended, 3=VFX born
            public BattleEffectPose Pose;
        }
        private sealed class BattleBeamLine
        {
            public bool Enabled, Charge, Muzzle, Hit;
            public float X, Y, Z, EndX, EndY, EndZ, Width, EndWidth;
        }
        private sealed class BattleWeaponEffect
        {
            public long Ship;
            public string Slot, Resource;
            public float Angle;
            public int Ammo;
            public List<BattleBeamLine> Beams = new List<BattleBeamLine>();
        }
        private sealed class BattleEffectsState
        {
            public int Schema = 1;
            public long Sequence;
            public List<BattleEffectPose> Projectiles = new List<BattleEffectPose>();
            public List<BattleEffectPose> Vfx = new List<BattleEffectPose>();
            public List<BattleEffectEvent> Events = new List<BattleEffectEvent>();
            public List<BattleWeaponEffect> Weapons = new List<BattleWeaponEffect>();
        }
        private sealed class BattleVfxSource { internal GameObject Object; internal BattleEffectPose Pose; }
        private sealed class BattleEffectReplica
        {
            internal GameObject Object;
            internal long Id;
            internal bool Vfx;
            internal Vector3 From, To;
            internal float FromAngle, ToAngle;
            internal double Start, Duration, Expires = -1;
        }
        private static readonly FieldInfo effectBeamEmitters = NativeWire.Field(typeof(BeamBehavior), "emitters");
        private readonly Dictionary<BaseProjectile, BattleEffectPose> battleEffectProjectiles = new Dictionary<BaseProjectile, BattleEffectPose>();
        private readonly Dictionary<GameObject, BattleVfxSource> battleEffectVfx = new Dictionary<GameObject, BattleVfxSource>();
        private readonly List<BattleEffectEvent> battleEffectEvents = new List<BattleEffectEvent>();
        private readonly Dictionary<long, BattleEffectReplica> battleEffectReplicas = new Dictionary<long, BattleEffectReplica>();
        private readonly BattleEffectFrameSnapshot<long, BattleEffectReplica> battleEffectFrameSnapshot =
            new BattleEffectFrameSnapshot<long, BattleEffectReplica>();
        private readonly Dictionary<string, WeaponData> battleEffectWeapons = new Dictionary<string, WeaponData>(StringComparer.Ordinal);
        private readonly Dictionary<string, WeaponController> battleEffectControllers = new Dictionary<string, WeaponController>(StringComparer.Ordinal);
        private readonly Dictionary<string, Stack<GameObject>> battleEffectShellPool = new Dictionary<string, Stack<GameObject>>(StringComparer.Ordinal);
        private readonly Dictionary<GameObject, string> battleEffectShellKeys = new Dictionary<GameObject, string>();
        private readonly Dictionary<GameObject, long> battleEffectVfxLeases = new Dictionary<GameObject, long>();
        private GameObject battleEffectsRoot;
        private long battleEffectSerial, battleEffectsSequence, appliedBattleEffectsSequence, appliedBattleEffectEvent;
        private double lastBattleEffectsReceipt;
        private bool applyingBattleEffects;

        private static BattleEffectPose ReadEffectPose(long id, string resource, Transform t, int team = 0)
        {
            return new BattleEffectPose { Id = id, Resource = resource, Team = team, X = t.position.x, Y = t.position.y, Z = t.position.z,
                Angle = t.eulerAngles.z, Sx = t.lossyScale.x, Sy = t.lossyScale.y, Sz = t.lossyScale.z };
        }
        internal void TrackPlayerBattleProjectile(BaseProjectile projectile)
        {
            if (!CanRunPlayerBattle || applyingBattleEffects || projectile == null || projectile.weaponData == null) return;
            // Missile.Initialize calls the same base initializer; one spawn gets one visual ID.
            if (battleEffectProjectiles.ContainsKey(projectile)) return;
            BattleEffectPose pose = ReadEffectPose(++battleEffectSerial, projectile.weaponData.id, projectile.transform, (int)projectile.GetTeamType());
            battleEffectProjectiles.Add(projectile, pose);
            battleEffectEvents.Add(new BattleEffectEvent { Serial = pose.Id, Kind = 1, Pose = pose });
        }
        internal void EndPlayerBattleProjectile(BaseProjectile projectile)
        {
            BattleEffectPose previous;
            if (projectile == null || !battleEffectProjectiles.TryGetValue(projectile, out previous)) return;
            battleEffectProjectiles.Remove(projectile);
            BattleEffectPose pose = ReadEffectPose(previous.Id, previous.Resource, projectile.transform, previous.Team);
            battleEffectEvents.Add(new BattleEffectEvent { Serial = ++battleEffectSerial, Kind = 2, Pose = pose });
        }
        internal void TrackPlayerBattleVfx(string resource, GameObject instance)
        {
            if (!CanRunPlayerBattle || applyingBattleEffects || instance == null) return;
            BattleEffectPose pose = ReadEffectPose(++battleEffectSerial, resource, instance.transform);
            battleEffectVfx[instance] = new BattleVfxSource { Object = instance, Pose = pose };
            battleEffectEvents.Add(new BattleEffectEvent { Serial = pose.Id, Kind = 3, Pose = pose });
        }
        private static IList EffectEmitters(BeamBehavior beam) { return (IList)effectBeamEmitters.GetValue(beam); }
        private static bool EffectActive(object emitter, string field)
        { GameObject value = NativeWire.Get(emitter, field) as GameObject; return value != null && value.activeSelf; }
        private string CapturePlayerBattleEffects()
        {
            var state = new BattleEffectsState { Sequence = checked(battleEffectsSequence + 1) };
            foreach (var pair in battleEffectProjectiles.ToArray())
            {
                if (pair.Key == null || !pair.Key.gameObject.activeInHierarchy) { battleEffectProjectiles.Remove(pair.Key); continue; }
                state.Projectiles.Add(ReadEffectPose(pair.Value.Id, pair.Value.Resource, pair.Key.transform, pair.Value.Team));
            }
            foreach (var pair in battleEffectVfx.ToArray())
            {
                if (pair.Key == null || !pair.Key.activeInHierarchy) { battleEffectVfx.Remove(pair.Key); continue; }
                state.Vfx.Add(ReadEffectPose(pair.Value.Pose.Id, pair.Value.Pose.Resource, pair.Key.transform));
            }
            foreach (BaseShip ship in FleetManager.Instance.GetAllShips().Values)
            {
                if (ship == null || ship.WeaponManager == null) continue;
                foreach (WeaponController weapon in ship.WeaponManager.AllWeapons)
                {
                    if (weapon == null || weapon.weaponData == null) continue;
                    WeaponSlot slot = weapon.GetComponentInParent<WeaponSlot>();
                    if (slot == null || String.IsNullOrEmpty(slot.slotId)) throw new InvalidOperationException("玩家舰战武器缺少正式插槽身份。");
                    var value = new BattleWeaponEffect { Ship = BattleSourceUid(ship), Slot = slot.slotId, Resource = weapon.weaponData.id,
                        Ammo = weapon.CurrentAmmo, Angle = weapon.rotatingPart == null ? 0 : weapon.rotatingPart.localEulerAngles.z };
                    BeamBehavior beam = weapon.GetComponent<BeamBehavior>();
                    if (beam != null) foreach (object emitter in EffectEmitters(beam))
                    {
                        LineRenderer line = (LineRenderer)NativeWire.Get(emitter, "line");
                        if (line == null || line.positionCount != 2) throw new InvalidOperationException("原生光束缺少两个表现端点。");
                        Vector3 start = line.GetPosition(0), end = line.GetPosition(1);
                        value.Beams.Add(new BattleBeamLine { Enabled = line.enabled, X = start.x, Y = start.y, Z = start.z,
                            EndX = end.x, EndY = end.y, EndZ = end.z, Width = line.startWidth, EndWidth = line.endWidth,
                            Charge = EffectActive(emitter, "chargeVFXObj"), Muzzle = EffectActive(emitter, "startVFXObj"), Hit = EffectActive(emitter, "endVFXHullObj") });
                    }
                    state.Weapons.Add(value);
                }
            }
            state.Events.AddRange(battleEffectEvents);
            ValidateEffectLimits(state);
            string encoded = NativeWire.Json(state);
            battleEffectsSequence = state.Sequence;
            // Session retains the exact captured frame until reliable acknowledgement. The
            // next frame needs only new events plus the complete currently-live visual set.
            battleEffectEvents.Clear();
            return encoded;
        }
        private static void ValidateEffectPose(BattleEffectPose pose)
        {
            if (pose == null || pose.Id <= 0 || String.IsNullOrEmpty(pose.Resource) || pose.Resource.Length > 128
                || pose.Team < 0 || pose.Team > 2 || new[] { pose.X, pose.Y, pose.Z, pose.Angle, pose.Sx, pose.Sy, pose.Sz }.Any(v => float.IsNaN(v) || float.IsInfinity(v))
                || Math.Abs(pose.Sx) > 10000 || Math.Abs(pose.Sy) > 10000 || Math.Abs(pose.Sz) > 10000)
                throw new InvalidOperationException("玩家舰战表现身份、资源或坐标无效。");
        }
        private static void ValidateEffectLimits(BattleEffectsState state)
        {
            if (state == null || state.Schema != 1 || state.Sequence <= 0 || state.Projectiles == null || state.Vfx == null || state.Events == null || state.Weapons == null
                || state.Projectiles.Count > 16000 || state.Vfx.Count > 16000 || state.Events.Count > 32000 || state.Weapons.Count > 8192)
                throw new InvalidOperationException("玩家舰战表现快照结构或数量无效。");
            var ids = new HashSet<long>();
            foreach (BattleEffectPose pose in state.Projectiles.Concat(state.Vfx))
            { ValidateEffectPose(pose); if (!ids.Add(pose.Id)) throw new InvalidOperationException("玩家舰战表现实例编号重复。"); }
            long previous = 0;
            foreach (BattleEffectEvent item in state.Events)
            {
                if (item == null || item.Serial <= previous || item.Kind < 1 || item.Kind > 3) throw new InvalidOperationException("玩家舰战表现事件序列无效。");
                ValidateEffectPose(item.Pose);
                if (item.Kind != 2 && item.Serial != item.Pose.Id || item.Kind == 2 && item.Serial <= item.Pose.Id) throw new InvalidOperationException("舰战表现事件和实例编号不一致。");
                previous = item.Serial;
            }
            var weaponSlots = new HashSet<string>(StringComparer.Ordinal);
            foreach (BattleWeaponEffect value in state.Weapons)
            {
                if (value == null || value.Beams == null || value.Beams.Count > 128 || String.IsNullOrEmpty(value.Slot) || value.Slot.Length > 128
                    || String.IsNullOrEmpty(value.Resource) || value.Resource.Length > 128 || float.IsNaN(value.Angle) || float.IsInfinity(value.Angle)
                    || !weaponSlots.Add(value.Ship + ":" + value.Slot)) throw new InvalidOperationException("舰战武器表现字段或插槽身份无效。");
                foreach (BattleBeamLine line in value.Beams)
                    if (line == null || new[] { line.X, line.Y, line.Z, line.EndX, line.EndY, line.EndZ, line.Width, line.EndWidth }.Any(v => float.IsNaN(v) || float.IsInfinity(v))
                        || line.Width < 0 || line.EndWidth < 0) throw new InvalidOperationException("舰战光束端点或宽度无效。");
            }
        }
        private static void ValidatePlayerBattleEffects(string encoded)
        { ValidateEffectLimits(NativeWire.Parse<BattleEffectsState>(encoded)); }
        private WeaponData ResolveEffectWeapon(string id)
        {
            WeaponData data;
            if (!battleEffectWeapons.TryGetValue(id, out data))
            {
                data = BattleUtils.LoadWeaponDataById(id);
                if (data == null || data.id != id || data.weaponLayout == null || data.weaponLayout.projectileData == null)
                    throw new InvalidOperationException("本机缺少已登记武器的弹丸表现资源：" + id);
                battleEffectWeapons.Add(id, data);
            }
            return data;
        }
        private WeaponController ResolveEffectController(BattleWeaponEffect value)
        {
            BaseShip ship = FindBattleShip(value.Ship);
            if (ship == null) return null; // A recorded destroyed/departed ship has no live weapon shell.
            string key = value.Ship + ":" + value.Slot;
            WeaponController weapon;
            if (!battleEffectControllers.TryGetValue(key, out weapon) || weapon == null)
            {
                weapon = ship.WeaponManager.AllWeapons.SingleOrDefault(w => w != null && w.GetComponentInParent<WeaponSlot>().slotId == value.Slot);
                if (weapon == null) throw new InvalidOperationException("本机缺少本场舰船武器插槽：" + key);
                battleEffectControllers[key] = weapon;
            }
            if (weapon.weaponData == null || weapon.weaponData.id != value.Resource) throw new InvalidOperationException("玩家舰战武器插槽资源不一致：" + key);
            return weapon;
        }
        private void EnsureBattleEffectsRoot()
        {
            if (battleEffectsRoot != null) return;
            battleEffectsRoot = new GameObject("MultiplayerBattleVisuals");
            battleEffectsRoot.AddComponent<PlayerBattleEffectsView>().Bridge = this;
        }
        private static void DisableEffectGameplay(GameObject instance)
        {
            // Awake only caches components on native projectile templates. We never call
            // Initialize/OnSpawn, so no flight, missile registry, damage or transient lease exists.
            foreach (MonoBehaviour script in instance.GetComponentsInChildren<MonoBehaviour>(true))
                if (!(script is ProjectileVisuals)) script.enabled = false;
            foreach (Collider2D collider in instance.GetComponentsInChildren<Collider2D>(true)) collider.enabled = false;
            foreach (Collider collider in instance.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
            foreach (Rigidbody2D body in instance.GetComponentsInChildren<Rigidbody2D>(true)) body.simulated = false;
        }
        private BattleEffectReplica CreateProjectileReplica(BattleEffectPose pose)
        {
            WeaponData data = ResolveEffectWeapon(pose.Resource);
            GameObject instance = null; Stack<GameObject> pool;
            if (battleEffectShellPool.TryGetValue(pose.Resource, out pool))
                while (pool.Count != 0 && instance == null) instance = pool.Pop();
            if (instance == null)
            {
                GameObject prefab = data.type == WeaponType.Missile ? BattleResourceManager.GetMissileProjectilePrefab() : BattleResourceManager.GetBallisticProjectilePrefab();
                if (prefab == null) throw new InvalidOperationException("原生弹丸表现模板缺失：" + pose.Resource);
                instance = UnityEngine.Object.Instantiate(prefab, battleEffectsRoot.transform, false);
                battleEffectShellKeys.Add(instance, pose.Resource);
            }
            DisableEffectGameplay(instance);
            ProjectileVisuals visuals = instance.GetComponent<ProjectileVisuals>();
            if (visuals == null) throw new InvalidOperationException("原生弹丸模板缺少ProjectileVisuals：" + pose.Resource);
            visuals.Configure(data, pose.Team == (int)TeamType.Player ? TeamType.Robot : pose.Team == (int)TeamType.Robot ? TeamType.Player : (TeamType)pose.Team);
            visuals.UpdateDissolve(1);
            DisableEffectGameplay(instance); // Configure can add a payload component/collider.
            instance.SetActive(true);
            return new BattleEffectReplica { Object = instance, Id = pose.Id };
        }
        private BattleEffectReplica CreateVfxReplica(BattleEffectPose pose)
        {
            GameObject prefab;
            if (VFXManager.Instance == null || !VFXManager.Instance.TryGetVFXPrefab(pose.Resource, out prefab))
                throw new InvalidOperationException("本机缺少已登记战斗特效：" + pose.Resource);
            GameObject instance = VFXManager.Instance.SpawnVFXActor(pose.Resource, new Vector3(pose.X, pose.Y, pose.Z), Quaternion.Euler(0, 0, pose.Angle), battleEffectsRoot.transform, "Effect", 1, true);
            if (instance == null) throw new InvalidOperationException("原生特效池未生成玩家舰战表现：" + pose.Resource);
            // VFX keeps its native visual/particle controllers; physics cannot create extra hits.
            foreach (Collider2D collider in instance.GetComponentsInChildren<Collider2D>(true)) collider.enabled = false;
            foreach (Collider collider in instance.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
            foreach (Rigidbody2D body in instance.GetComponentsInChildren<Rigidbody2D>(true)) body.simulated = false;
            foreach (PayloadEmitter payload in instance.GetComponentsInChildren<PayloadEmitter>(true)) payload.enabled = false;
            foreach (BaseProjectile projectile in instance.GetComponentsInChildren<BaseProjectile>(true)) projectile.enabled = false;
            foreach (WeaponController weapon in instance.GetComponentsInChildren<WeaponController>(true)) weapon.enabled = false;
            battleEffectVfxLeases[instance] = pose.Id;
            return new BattleEffectReplica { Object = instance, Vfx = true, Id = pose.Id };
        }
        private void ApplyEffectPose(BattleEffectPose pose, bool vfx, double interval)
        {
            BattleEffectReplica replica;
            if (!battleEffectReplicas.TryGetValue(pose.Id, out replica))
            {
                replica = vfx ? CreateVfxReplica(pose) : CreateProjectileReplica(pose);
                battleEffectReplicas.Add(pose.Id, replica);
                replica.Object.transform.position = new Vector3(pose.X, pose.Y, pose.Z);
                replica.Object.transform.rotation = Quaternion.Euler(0, 0, pose.Angle);
            }
            if (replica.Vfx != vfx) throw new InvalidOperationException("战斗表现实例在弹丸和特效之间改变类型。");
            long lease;
            if (replica.Object == null || vfx && (!battleEffectVfxLeases.TryGetValue(replica.Object, out lease) || lease != pose.Id))
            {
                replica = vfx ? CreateVfxReplica(pose) : CreateProjectileReplica(pose);
                battleEffectReplicas[pose.Id] = replica;
                replica.Object.transform.position = new Vector3(pose.X, pose.Y, pose.Z);
                replica.Object.transform.rotation = Quaternion.Euler(0, 0, pose.Angle);
            }
            replica.From = replica.Object.transform.position; replica.FromAngle = replica.Object.transform.eulerAngles.z;
            replica.To = new Vector3(pose.X, pose.Y, pose.Z); replica.ToAngle = pose.Angle;
            replica.Start = Time.timeAsDouble; replica.Duration = interval; replica.Expires = -1;
            replica.Object.transform.localScale = new Vector3(pose.Sx, pose.Sy, pose.Sz);
        }
        private static void SetBeamParticle(object emitter, string field, bool active, Vector3 position)
        {
            GameObject instance = NativeWire.Get(emitter, field) as GameObject;
            if (instance == null) { if (active) throw new InvalidOperationException("本机光束缺少已启用的原生粒子表现：" + field); return; }
            instance.transform.position = position;
            if (instance.activeSelf != active) instance.SetActive(active);
            if (active) { ParticleSystem particle = instance.GetComponent<ParticleSystem>(); if (particle != null && !particle.isPlaying) particle.Play(); }
        }
        private void ApplyPlayerBattleEffects(string encoded)
        {
            BattleEffectsState state = NativeWire.Parse<BattleEffectsState>(encoded);
            ValidateEffectLimits(state);
            if (!IsPlayerBattleReplica || state.Sequence <= appliedBattleEffectsSequence) return;
            // Resolve every fixed native resource/weapon before changing any visual object.
            foreach (BattleEffectPose pose in state.Projectiles.Concat(state.Events.Where(e => e.Kind != 3).Select(e => e.Pose))) ResolveEffectWeapon(pose.Resource);
            foreach (BattleEffectPose pose in state.Vfx.Concat(state.Events.Where(e => e.Kind == 3).Select(e => e.Pose)))
            { GameObject prefab; if (VFXManager.Instance == null || !VFXManager.Instance.TryGetVFXPrefab(pose.Resource, out prefab)) throw new InvalidOperationException("本机缺少已登记战斗特效：" + pose.Resource); }
            var weapons = new List<Tuple<BattleWeaponEffect, WeaponController>>();
            foreach (BattleWeaponEffect value in state.Weapons)
            {
                WeaponController weapon = ResolveEffectController(value);
                if (weapon == null) continue;
                BeamBehavior beam = weapon.GetComponent<BeamBehavior>();
                if ((beam == null ? 0 : EffectEmitters(beam).Count) != value.Beams.Count) throw new InvalidOperationException("双方原生光束发射器数量不一致。");
                weapons.Add(Tuple.Create(value, weapon));
            }
            EnsureBattleEffectsRoot(); applyingBattleEffects = true;
            try
            {
                double now = Time.timeAsDouble;
                double interval = lastBattleEffectsReceipt == 0 ? 0 : Math.Min(0.25, Math.Max(0.01, now - lastBattleEffectsReceipt));
                var present = new HashSet<long>(state.Projectiles.Concat(state.Vfx).Select(p => p.Id));
                foreach (BattleEffectEvent item in state.Events)
                {
                    if (item.Serial <= appliedBattleEffectEvent) continue;
                    if (item.Kind == 1 || item.Kind == 3) ApplyEffectPose(item.Pose, item.Kind == 3, 0);
                    else ApplyEffectPose(item.Pose, false, 0.1);
                    appliedBattleEffectEvent = item.Serial;
                }
                foreach (BattleEffectPose pose in state.Projectiles) ApplyEffectPose(pose, false, interval);
                foreach (BattleEffectPose pose in state.Vfx) ApplyEffectPose(pose, true, interval);
                foreach (var pair in battleEffectReplicas) if (!present.Contains(pair.Key) && pair.Value.Expires < 0) pair.Value.Expires = now + 0.12;
                foreach (var pair in weapons)
                {
                    BattleWeaponEffect value = pair.Item1; WeaponController weapon = pair.Item2;
                    if (weapon.rotatingPart != null) weapon.rotatingPart.localRotation = Quaternion.Euler(0, 0, value.Angle);
                    NativeWire.Set(weapon, "CurrentAmmo", value.Ammo); // HUD only; no ConsumeAmmo or fire evaluation.
                    BeamBehavior beam = weapon.GetComponent<BeamBehavior>();
                    if (beam == null) continue;
                    IList emitters = EffectEmitters(beam);
                    for (int i = 0; i < value.Beams.Count; i++)
                    {
                        BattleBeamLine incoming = value.Beams[i]; object emitter = emitters[i];
                        LineRenderer line = (LineRenderer)NativeWire.Get(emitter, "line");
                        Vector3 start = new Vector3(incoming.X, incoming.Y, incoming.Z), end = new Vector3(incoming.EndX, incoming.EndY, incoming.EndZ);
                        line.SetPosition(0, start); line.SetPosition(1, end); line.startWidth = incoming.Width; line.endWidth = incoming.EndWidth; line.enabled = incoming.Enabled;
                        SetBeamParticle(emitter, "chargeVFXObj", incoming.Charge, start);
                        SetBeamParticle(emitter, "startVFXObj", incoming.Muzzle, start);
                        SetBeamParticle(emitter, "endVFXHullObj", incoming.Hit, end);
                    }
                }
                appliedBattleEffectsSequence = state.Sequence; lastBattleEffectsReceipt = now;
            }
            finally { applyingBattleEffects = false; }
        }
        internal void UpdatePlayerBattleEffects()
        {
            if (!HasNativePlayerBattle || !IsPlayerBattleReplica || Context.IsPaused()) return;
            double now = Time.timeAsDouble;
            // Preserve snapshot-before-removal order. The reusable buffer
            // avoids a new dictionary-sized array on each active render frame.
            var snapshot = battleEffectFrameSnapshot.Capture(battleEffectReplicas);
            try
            {
                for (int i = 0; i < snapshot.Count; i++)
                {
                    var pair = snapshot[i];
                    BattleEffectReplica value = pair.Value;
                    long lease;
                    if (value.Object == null || value.Vfx && (!battleEffectVfxLeases.TryGetValue(value.Object, out lease) || lease != value.Id)
                        || value.Expires >= 0 && now >= value.Expires)
                    { ReleaseBattleEffect(value); battleEffectReplicas.Remove(pair.Key); continue; }
                    float t = value.Duration <= 0 ? 1 : Mathf.Clamp01((float)((now - value.Start) / value.Duration));
                    value.Object.transform.position = Vector3.Lerp(value.From, value.To, t);
                    value.Object.transform.rotation = Quaternion.Euler(0, 0, Mathf.LerpAngle(value.FromAngle, value.ToAngle, t));
                }
            }
            finally { battleEffectFrameSnapshot.Release(snapshot); }
        }
        private void ReleaseBattleEffect(BattleEffectReplica replica)
        {
            if (replica.Object == null) return;
            if (replica.Vfx)
            {
                // A native VFX may already have returned itself to its pool. Do not reclaim
                // a subsequently reused instance that is no longer parented to our view.
                long lease;
                if (battleEffectsRoot != null && replica.Object.transform.parent == battleEffectsRoot.transform
                    && battleEffectVfxLeases.TryGetValue(replica.Object, out lease) && lease == replica.Id)
                {
                    battleEffectVfxLeases.Remove(replica.Object);
                    if (VFXManager.Instance != null) VFXManager.Instance.DespawnVFX(replica.Object); else UnityEngine.Object.Destroy(replica.Object);
                }
                return;
            }
            string key = battleEffectShellKeys[replica.Object]; Stack<GameObject> pool;
            if (!battleEffectShellPool.TryGetValue(key, out pool)) battleEffectShellPool.Add(key, pool = new Stack<GameObject>());
            replica.Object.SetActive(false);
            if (pool.Count < 128) pool.Push(replica.Object);
            else { battleEffectShellKeys.Remove(replica.Object); UnityEngine.Object.Destroy(replica.Object); }
        }
        private void ClearPlayerBattleEffects()
        {
            var errors = new List<Exception>();
            foreach (BattleEffectReplica value in battleEffectReplicas.Values)
                try { ReleaseBattleEffect(value); } catch (Exception error) { errors.Add(error); }
            battleEffectReplicas.Clear(); battleEffectProjectiles.Clear(); battleEffectVfx.Clear(); battleEffectEvents.Clear();
            battleEffectFrameSnapshot.Reset();
            battleEffectShellPool.Clear(); battleEffectShellKeys.Clear(); battleEffectWeapons.Clear(); battleEffectControllers.Clear(); battleEffectVfxLeases.Clear();
            if (battleEffectsRoot != null)
            {
                PlayerBattleEffectsView view = battleEffectsRoot.GetComponent<PlayerBattleEffectsView>(); if (view != null) view.Bridge = null;
                UnityEngine.Object.Destroy(battleEffectsRoot); battleEffectsRoot = null;
            }
            battleEffectSerial = battleEffectsSequence = appliedBattleEffectsSequence = appliedBattleEffectEvent = 0;
            lastBattleEffectsReceipt = 0;
            if (errors.Count != 0) throw new AggregateException("玩家舰战表现资源释放失败。", errors);
        }
    }

    internal static class NativePlayerFleetBattleEffectsHooks
    {
        private static GameBridge Bridge { get { return GameBridge.Active; } }
        internal static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(BaseProjectile), "Initialize"), postfix: new HarmonyMethod(typeof(NativePlayerFleetBattleEffectsHooks), "ProjectileBorn"));
            harmony.Patch(AccessTools.Method(typeof(BaseProjectile), "OnDespawn"), prefix: new HarmonyMethod(typeof(NativePlayerFleetBattleEffectsHooks), "ProjectileEnded"));
            harmony.Patch(AccessTools.Method(typeof(BaseProjectile), "DespawnSelf"), prefix: new HarmonyMethod(typeof(NativePlayerFleetBattleEffectsHooks), "ProjectileEnded"));
            harmony.Patch(AccessTools.Method(typeof(VFXManager), "SpawnVFXActor"), postfix: new HarmonyMethod(typeof(NativePlayerFleetBattleEffectsHooks), "VfxBorn"));
        }
        private static void ProjectileBorn(BaseProjectile __instance) { if (Bridge != null) Bridge.TrackPlayerBattleProjectile(__instance); }
        private static void ProjectileEnded(BaseProjectile __instance) { if (Bridge != null) Bridge.EndPlayerBattleProjectile(__instance); }
        private static void VfxBorn(string __0, GameObject __result) { if (Bridge != null) Bridge.TrackPlayerBattleVfx(__0, __result); }
    }
}
