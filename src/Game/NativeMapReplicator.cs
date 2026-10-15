using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace FreeWorldMultiplayer
{
    internal sealed class NativeMapReplicator : IDisposable
    {
        internal sealed class Checkpoint
        {
            public string Map;
            public List<string> Heroes = new List<string>();
            public List<string> Animals = new List<string>();
            public List<string> DepartedHeroes = new List<string>();
            public List<string> DepartedAnimals = new List<string>();
            public List<GameBridge.LootReservation> Loot = new List<GameBridge.LootReservation>();
            public List<GameBridge.NativeLanding> Landings = new List<GameBridge.NativeLanding>();
        }
        internal sealed class EntityChange
        {
            public string Collection;
            public long Uid;
            public string Operation;
            public string Bean;
        }
        internal sealed class FieldChange { public string Field; public string Value; }
        internal sealed class Pose
        {
            public long Uid;
            public float X, Y, Z;
            public sbyte Direction;
            public int Animation;
            public bool Loop;
            public bool IsAttack;
            public long TargetUid = -1;
        }
        internal sealed class Delta
        {
            public List<EntityChange> Entities = new List<EntityChange>();
            public List<FieldChange> Fields = new List<FieldChange>();
            public List<Pose> Poses = new List<Pose>();
            public List<TileChange> Tiles = new List<TileChange>();
            public List<GameBridge.LootReservation> Loot = new List<GameBridge.LootReservation>();
            public List<GameBridge.NativeLanding> Landings = new List<GameBridge.NativeLanding>();
        }
        internal sealed class TileChange { public int X, Y; public string Ground; }
        private readonly GameBridge bridge;
        private readonly Dictionary<long, long> revisions = new Dictionary<long, long>();
        private readonly Dictionary<long, HashSet<long>> knownHeroes = new Dictionary<long, HashSet<long>>();
        private readonly Dictionary<long, HashSet<long>> knownAnimals = new Dictionary<long, HashSet<long>>();
        private static readonly Dictionary<string, string[]> Managers = new Dictionary<string, string[]>
        {
            { "structs", new[] {"buildManager", "addStruct", "removeStruct"} },
            { "floors", new[] {"buildManager", "addStructFloor", "removeStructFloor"} },
            { "platforms", new[] {"buildManager", "addStructPlatform", "removeStructPlatform"} },
            { "furnitures", new[] {"buildManager", "addFurniture", "removeFurnture"} },
            { "defenses", new[] {"buildManager", "addDefense", "removeDefense"} },
            { "plays", new[] {"buildManager", "addPlay", "removePlay"} },
            { "electrics", new[] {"buildManager", "addElectric", "removeElectric"} },
            { "produces", new[] {"buildManager", "addProduce", "removeProduce"} },
            { "templers", new[] {"buildManager", "addTempler", "removeTempler"} },
            { "medicals", new[] {"buildManager", "addMedical", "removeMedical"} },
            { "impuritys", new[] {"buildManager", "addSundry", "removeSundry"} },
            { "decros", new[] {"buildManager", "addDecro", "removeDecro"} },
            { "things", new[] {"thingManager", "addThing", "removeThing"} },
            { "heroes", new[] {"heroManager", "addRole", "removeRole"} },
            { "ownedAnimals", new[] {"animalManager", "addAnimal", "removeAnimal"} },
            { "thirdRoles", new[] {"heroManager", "addRole", "removeRole"} },
            { "animals", new[] {"animalManager", "addAnimal", "removeAnimal"} },
            { "monsters", new[] {"monsterManager", "addMonster", "removeMonster"} },
            { "plans", new[] {"planManager", "addPlan", "removePlan"} },
            { "ores", new[] {"oreManager", "addOre", "removeOre"} },
            { "plants", new[] {"plantManager", "addPlant", "removePlant"} },
            { "zones", new[] {"zoneManager", "addArea", "removeArea"} },
            { "rooms", new[] {"roomManage", "addRoomBean", "removeRoomBean"} }
        };
        internal NativeMapReplicator(GameBridge bridge) { this.bridge = bridge; }
        internal void SetRevision(long mapId, long revision) { revisions[mapId] = revision; }
        internal IEnumerable<FWBaseBean> Beans(UserFWStarSpread spread)
        {
            foreach (FieldInfo field in NativeWire.PersistentFields(typeof(UserFWStarSpread)))
            {
                IList list = field.GetValue(spread) as IList;
                if (list == null) continue;
                foreach (object item in list) if (item is FWUidItem) yield return (FWBaseBean)item;
            }
        }
        private IEnumerable<FWHeroBean> Heroes(long mapId)
        {
            return bridge.Players.Values.SelectMany(p => p.heros).Where(h => h.isInMap(mapId)).GroupBy(h => h.getUID()).Select(g => g.First());
        }
        private IEnumerable<FWAnimalBean> Animals(long mapId)
        {
            return bridge.Players.Values.SelectMany(p => p.animals).Where(h => h.isInMap(mapId)).GroupBy(h => h.getUID()).Select(g => g.First());
        }
        internal MapSnapshot Capture(long mapId)
        {
            FWStarMapInfo map;
            if (!bridge.Maps.TryGetValue(mapId, out map)) throw new InvalidOperationException("Unknown shared map " + mapId);
            var native = new Checkpoint { Map = NativeWire.Pack(map), Heroes = Heroes(mapId).Select(NativeWire.Pack).ToList(), Animals = Animals(mapId).Select(NativeWire.Pack).ToList() };
            HashSet<long> heroIds, animalIds;
            if (!knownHeroes.TryGetValue(mapId, out heroIds)) knownHeroes[mapId] = heroIds = new HashSet<long>();
            if (!knownAnimals.TryGetValue(mapId, out animalIds)) knownAnimals[mapId] = animalIds = new HashSet<long>();
            foreach (FWHeroBean hero in Heroes(mapId)) heroIds.Add(hero.getUID());
            foreach (FWAnimalBean animal in Animals(mapId)) animalIds.Add(animal.getUID());
            native.DepartedHeroes = bridge.Players.Keys.SelectMany(bridge.PlayerHeroes).Where(h => heroIds.Contains(h.getUID()) && !h.isInMap(mapId)).Select(NativeWire.Pack).ToList();
            native.DepartedAnimals = bridge.Players.Keys.SelectMany(bridge.PlayerAnimals).Where(a => animalIds.Contains(a.getUID()) && !a.isInMap(mapId)).Select(NativeWire.Pack).ToList();
            native.Loot = bridge.LootReservations.Where(r => r.MapId == mapId).ToList();
            native.Landings = bridge.CaptureNativeLandings(mapId);
            var result = new MapSnapshot { MapId = mapId, NativeJson = NativeWire.Json(native) };
            long revision; if (revisions.TryGetValue(mapId, out revision)) result.Revision = revision;
            foreach (FWBaseBean bean in Beans(map.starSpread).Concat<FWBaseBean>(Heroes(mapId)).Concat(Animals(mapId)))
            {
                if (bean is FWThingBean) continue; // Stock follows its map/bag/carrier, never an immutable employee owner.
                string owner = bridge.Context.OwnerOfEntity(NativeWire.Uid(bean));
                if (string.IsNullOrEmpty(owner))
                {
                    if (bean is FWHeroBean)
                        owner = bridge.Players.FirstOrDefault(p => p.Value.heros.Any(h => h.getUID() == NativeWire.Uid(bean))).Key;
                    else if (bean is FWAnimalBean)
                        owner = bridge.Players.FirstOrDefault(p => p.Value.animals.Any(a => a.getUID() == NativeWire.Uid(bean))).Key;
                    else if (bean is FWBuildBean || bean is FWPlanBean)
                        owner = bridge.Context.OwnerOfMap(mapId);
                    if (!string.IsNullOrEmpty(owner)) bridge.Context.RegisterEntityOwner(NativeWire.Uid(bean), owner);
                }
                if (!string.IsNullOrEmpty(owner)) result.EntityOwners[NativeWire.Uid(bean)] = owner;
            }
            return result;
        }
        private Dictionary<string, IList> Collections(FWStarMapInfo map, Checkpoint checkpoint)
        {
            var result = new Dictionary<string, IList>();
            foreach (string name in Managers.Keys)
            {
                if (name == "heroes") result.Add(name, checkpoint == null ? Heroes(map.starMapId).ToList() : checkpoint.Heroes.Select(NativeWire.Unpack<FWHeroBean>).ToList());
                else if (name == "ownedAnimals") result.Add(name, checkpoint == null ? Animals(map.starMapId).ToList() : checkpoint.Animals.Select(NativeWire.Unpack<FWAnimalBean>).ToList());
                else result.Add(name, (IList)NativeWire.Get(map.starSpread, name));
            }
            return result;
        }
        internal MapUpdate CaptureUpdate(long mapId, MapSnapshot previous)
        {
            MapSnapshot next = Capture(mapId);
            if (previous == null) throw new InvalidOperationException("Map delta requires the acknowledged checkpoint baseline.");
            Checkpoint oldCheckpoint = NativeWire.Parse<Checkpoint>(previous.NativeJson);
            FWStarMapInfo oldMap = NativeWire.Unpack<FWStarMapInfo>(oldCheckpoint.Map);
            FWStarMapInfo current = bridge.Maps[mapId];
            Dictionary<string, IList> oldLists = Collections(oldMap, oldCheckpoint), newLists = Collections(current, null);
            var delta = new Delta();
            delta.Loot = bridge.LootReservations.Where(r => r.MapId == mapId).ToList();
            delta.Landings = bridge.CaptureNativeLandings(mapId);
            foreach (var pair in newLists)
            {
                Dictionary<long, object> before = oldLists[pair.Key].Cast<object>().ToDictionary(NativeWire.Uid);
                Dictionary<long, object> after = pair.Value.Cast<object>().ToDictionary(NativeWire.Uid);
                foreach (long uid in before.Keys.Except(after.Keys))
                {
                    object departed = pair.Key == "heroes" || pair.Key == "ownedAnimals" ? bridge.FindPersistentBean(uid) : null;
                    delta.Entities.Add(new EntityChange { Collection = pair.Key, Uid = uid, Operation = "delete", Bean = departed == null ? null : NativeWire.Pack(departed) });
                }
                foreach (var bean in after)
                {
                    string encoded = NativeWire.Pack(bean.Value); object previousBean;
                    if (!before.TryGetValue(bean.Key, out previousBean)) delta.Entities.Add(new EntityChange { Collection = pair.Key, Uid = bean.Key, Operation = "create", Bean = encoded });
                    else if (encoded != NativeWire.Pack(previousBean)) delta.Entities.Add(new EntityChange { Collection = pair.Key, Uid = bean.Key, Operation = "update", Bean = encoded });
                }
            }
            foreach (FieldInfo field in NativeWire.PersistentFields(typeof(UserFWStarSpread)))
            {
                if (Managers.ContainsKey(field.Name) || field.Name == "oresShadow" || field.Name == "plantsShadow" || field.Name == "TilesShadow" || field.Name == "legacyOres" || field.Name == "legacyPlants") continue;
                string before = NativeWire.Pack(field.GetValue(oldMap.starSpread));
                string after = NativeWire.Pack(field.GetValue(current.starSpread));
                if (before != after) delta.Fields.Add(new FieldChange { Field = field.Name, Value = after });
            }
            string[,] previousTiles = oldMap.starSpread.getTiles(), currentTiles = current.starSpread.getTiles();
            if (previousTiles.GetLength(0) != currentTiles.GetLength(0) || previousTiles.GetLength(1) != currentTiles.GetLength(1)) throw new InvalidOperationException("Live map resizing requires a formal map transition.");
            for (int y = 0; y < currentTiles.GetLength(0); y++) for (int x = 0; x < currentTiles.GetLength(1); x++)
                if (previousTiles[y, x] != currentTiles[y, x]) delta.Tiles.Add(new TileChange { X = x, Y = y, Ground = currentTiles[y, x] });
            if (bridge.CurrentMapId == mapId && MapManager.instance != null)
                foreach (BaseRole hero in MapManager.instance.heroManager.getAllRoles().Cast<BaseRole>()
                    .Concat(MapManager.instance.animalManager.getAnimals()).Concat(MapManager.instance.monsterManager.monsters))
                    delta.Poses.Add(new Pose { Uid = hero.getUID(), X = hero.transform.localPosition.x, Y = hero.transform.localPosition.y,
                        Z = hero.transform.localPosition.z, Direction = hero.mod_Move.forwardDir, Animation = hero.roleAnim.playIndex,
                        Loop = hero.roleAnim.loop, IsAttack = hero.roleAnim.isAttackAction(),
                        TargetUid = hero.roleAnim.target == null ? -1 : hero.roleAnim.target.getUID() });
            long revision = checked(previous.Revision + 1); revisions[mapId] = revision; next.Revision = revision;
            return new MapUpdate { MapId = mapId, BaseRevision = previous.Revision, Revision = revision,
                NativeDeltaJson = NativeWire.Json(delta), Checkpoint = next,
                EntityOwners = next.EntityOwners,
                Players = bridge.CapturePersonalDeltas() };
        }
        internal void RestoreCheckpoint(MapSnapshot snapshot)
        {
            Checkpoint checkpoint = NativeWire.Parse<Checkpoint>(snapshot.NativeJson);
            FWStarMapInfo map = NativeWire.Unpack<FWStarMapInfo>(checkpoint.Map);
            if (map.starMapId != snapshot.MapId) throw new InvalidOperationException("Checkpoint map identity mismatch.");
            FWStarMapInfo existing;
            if (bridge.Maps.TryGetValue(map.starMapId, out existing)) { NativeWire.Copy(map, existing); map = existing; }
            bridge.Maps[map.starMapId] = map; map.starSpread.setStarMapInfo(map);
            bridge.RegisterMapDirectoryEntry(map);
            foreach (var owner in snapshot.EntityOwners) bridge.Context.RegisterEntityOwner(owner.Key, owner.Value);
            foreach (string encoded in checkpoint.DepartedHeroes) RestoreDepartedHero(NativeWire.Unpack<FWHeroBean>(encoded), snapshot.MapId);
            foreach (string encoded in checkpoint.DepartedAnimals) RestoreDepartedAnimal(NativeWire.Unpack<FWAnimalBean>(encoded), snapshot.MapId);
            foreach (string encoded in checkpoint.Heroes)
            {
                FWHeroBean hero = NativeWire.Unpack<FWHeroBean>(encoded);
                string owner = bridge.Context.OwnerOfEntity(hero.getUID());
                FWGameData data;
                if (!bridge.Players.TryGetValue(owner ?? "", out data)) throw new InvalidOperationException("Checkpoint references unknown hero owner.");
                AddPersistentHero(hero);
            }
            foreach (string encoded in checkpoint.Animals)
            {
                FWAnimalBean animal = NativeWire.Unpack<FWAnimalBean>(encoded);
                string owner = bridge.Context.OwnerOfEntity(animal.getUID());
                FWGameData data;
                if (!bridge.Players.TryGetValue(owner ?? "", out data)) throw new InvalidOperationException("Checkpoint references unknown animal owner.");
                AddPersistentAnimal(animal);
            }
            revisions[map.starMapId] = snapshot.Revision;
            knownHeroes[map.starMapId] = new HashSet<long>(checkpoint.Heroes.Concat(checkpoint.DepartedHeroes).Select(s => NativeWire.Unpack<FWHeroBean>(s).getUID()));
            knownAnimals[map.starMapId] = new HashSet<long>(checkpoint.Animals.Concat(checkpoint.DepartedAnimals).Select(s => NativeWire.Unpack<FWAnimalBean>(s).getUID()));
            bridge.SetLootReservations(map.starMapId, checkpoint.Loot);
            bridge.RestoreNativeLandings(map.starMapId, checkpoint.Landings);
            // This entry is used exclusively for initial load / authority handoff, not live updates.
            MapManager.isInitFinish = false;
            UserBean.getInstance().enterMap(map);
        }
        internal void ApplyDelta(MapUpdate update)
        {
            FWStarMapInfo map;
            if (!bridge.Maps.TryGetValue(update.MapId, out map)) throw new InvalidOperationException("Delta arrived before checkpoint.");
            long revision; revisions.TryGetValue(update.MapId, out revision);
            if (revision == update.Revision) return;
            if (revision != update.BaseRevision) throw new InvalidOperationException("Native delta baseline mismatch; require acknowledged full checkpoint.");
            Delta delta = NativeWire.Parse<Delta>(update.NativeDeltaJson);
            foreach (var owner in update.EntityOwners) bridge.Context.RegisterEntityOwner(owner.Key, owner.Value);
            bool visible = bridge.CurrentMapId == update.MapId && MapManager.instance != null;
            var lists = Collections(map, null);
            foreach (EntityChange change in delta.Entities)
            {
                IList list;
                if (!lists.TryGetValue(change.Collection, out list)) throw new InvalidOperationException("Unrecognized entity collection.");
                object previous = list.Cast<object>().FirstOrDefault(x => NativeWire.Uid(x) == change.Uid);
                Type beanType = list.GetType().GetGenericArguments()[0];
                object next = string.IsNullOrEmpty(change.Bean) ? null : NativeWire.Unpack(change.Bean, beanType);
                if (next != null && NativeWire.Uid(next) != change.Uid) throw new InvalidOperationException("Entity patch UID mismatch.");
                if (change.Operation == "delete")
                {
                    if (previous == null) throw new InvalidOperationException("Removing absent native entity.");
                    if (visible) SceneMutation(change.Collection, previous, false);
                    list.Remove(previous);
                    if (change.Collection == "heroes")
                    {
                        if (next is FWHeroBean) RestoreDepartedHero((FWHeroBean)next, update.MapId);
                        else foreach (FWGameData data in bridge.Players.Values) data.heros.RemoveAll(h => h.getUID() == change.Uid && h.isInMap(update.MapId));
                    }
                    else if (change.Collection == "ownedAnimals")
                    {
                        if (next is FWAnimalBean) RestoreDepartedAnimal((FWAnimalBean)next, update.MapId);
                        else foreach (FWGameData data in bridge.Players.Values) data.animals.RemoveAll(a => a.getUID() == change.Uid && a.isInMap(update.MapId));
                    }
                }
                else if (change.Operation == "create")
                {
                    if (previous != null) throw new InvalidOperationException("Creating duplicate native entity.");
                    if (change.Collection == "heroes") next = AddPersistentHero((FWHeroBean)next);
                    else if (change.Collection == "ownedAnimals")
                    {
                        next = AddPersistentAnimal((FWAnimalBean)next);
                    }
                    list.Add(next);
                    if (visible) SceneMutation(change.Collection, next, true);
                }
                else if (change.Operation == "update")
                {
                    if (previous == null) throw new InvalidOperationException("Updating absent native entity.");
                    NativeWire.Copy(next, previous);
                    if (visible) RefreshScene(change.Collection, (FWBaseBean)previous);
                }
                else throw new InvalidOperationException("Unrecognized entity operation.");
            }
            foreach (FieldChange change in delta.Fields)
            {
                FieldInfo field = NativeWire.PersistentFields(typeof(UserFWStarSpread)).SingleOrDefault(f => f.Name == change.Field);
                if (field == null || Managers.ContainsKey(field.Name)) throw new InvalidOperationException("Unrecognized map field patch.");
                object value = NativeWire.Unpack(change.Value, field.FieldType);
                object target = field.GetValue(map.starSpread);
                if (target is IList && value is IList && !field.FieldType.IsArray)
                { ((IList)target).Clear(); foreach (object item in (IList)value) ((IList)target).Add(item); }
                else if (value is FWBaseBean && target != null && value.GetType() == target.GetType()) NativeWire.Copy(value, target);
                else field.SetValue(map.starSpread, value);
            }
            foreach (TileChange tile in delta.Tiles)
            {
                if (visible) map.starSpread.changeTile(tile.X, tile.Y, tile.Ground);
                else map.starSpread.getTiles()[tile.Y, tile.X] = tile.Ground;
            }
            if (visible) foreach (Pose pose in delta.Poses) ApplyPose(pose);
            bridge.SetLootReservations(update.MapId, delta.Loot);
            bridge.RestoreNativeLandings(update.MapId, delta.Landings);
            revisions[update.MapId] = update.Revision;
        }
        internal void Validate(MapUpdate update)
        {
            FWStarMapInfo map;
            if (!bridge.Maps.TryGetValue(update.MapId, out map)) throw new InvalidOperationException("Delta references an unloaded map.");
            Delta delta = NativeWire.Parse<Delta>(update.NativeDeltaJson);
            Dictionary<string, IList> lists = Collections(map, null);
            var touched = new HashSet<string>();
            foreach (EntityChange change in delta.Entities)
            {
                IList list;
                if (!lists.TryGetValue(change.Collection, out list) || !touched.Add(change.Collection + ":" + change.Uid)) throw new InvalidOperationException("Unknown or duplicate entity mutation.");
                object previous = list.Cast<object>().FirstOrDefault(x => NativeWire.Uid(x) == change.Uid);
                if (change.Operation != "create" && change.Operation != "update" && change.Operation != "delete") throw new InvalidOperationException("Unknown entity mutation.");
                if (change.Operation == "create" ? previous != null : previous == null)
                {
                    long current; revisions.TryGetValue(update.MapId, out current);
                    if (current != update.Revision) throw new InvalidOperationException("Entity mutation baseline mismatch.");
                }
                if (change.Operation != "delete" || !string.IsNullOrEmpty(change.Bean))
                {
                    object decoded = NativeWire.Unpack(change.Bean, list.GetType().GetGenericArguments()[0]);
                    if (NativeWire.Uid(decoded) != change.Uid) throw new InvalidOperationException("Entity UID mismatch.");
                    if (previous != null && previous.GetType() != decoded.GetType()) throw new InvalidOperationException("Entity subtype changed in-place.");
                }
            }
            foreach (FieldChange change in delta.Fields)
            {
                FieldInfo field = NativeWire.PersistentFields(typeof(UserFWStarSpread)).SingleOrDefault(f => f.Name == change.Field);
                if (field == null || Managers.ContainsKey(field.Name)) throw new InvalidOperationException("Invalid map field mutation.");
                NativeWire.Unpack(change.Value, field.FieldType);
            }
            foreach (TileChange tile in delta.Tiles)
                if (tile.X < 0 || tile.Y < 0 || tile.X >= map.starSpread.xLength || tile.Y >= map.starSpread.yLength || Map_GroundDef.getDataID(tile.Ground) == null)
                    throw new InvalidOperationException("Invalid terrain patch.");
            if (delta.Loot.Any(r => r.MapId != update.MapId || r.Count <= 0) || delta.Loot.Select(r => r.CarryUid).Distinct().Count() != delta.Loot.Count)
                throw new InvalidOperationException("Invalid loot reservation delta.");
            var stagedItems = map.starSpread.things.ToDictionary(t => t.getUID());
            var stagedHeroes = bridge.Players.Keys.SelectMany(bridge.PlayerHeroes).GroupBy(h => h.getUID()).ToDictionary(g => g.Key, g => g.First());
            foreach (EntityChange change in delta.Entities)
            {
                if (change.Collection == "things")
                {
                    if (change.Operation == "delete") stagedItems.Remove(change.Uid);
                    else stagedItems[change.Uid] = NativeWire.Unpack<FWThingBean>(change.Bean);
                }
                else if (change.Collection == "heroes")
                {
                    if (change.Operation == "delete") stagedHeroes.Remove(change.Uid);
                    else stagedHeroes[change.Uid] = NativeWire.Unpack<FWHeroBean>(change.Bean);
                }
            }
            bridge.ValidateLootReservations(update.MapId, delta.Loot,
                uid => { FWThingBean source; return stagedItems.TryGetValue(uid, out source) ? source : null; },
                uid => { FWHeroBean hero; return stagedHeroes.TryGetValue(uid, out hero) ? hero : null; });
            bridge.ValidateNativeLandings(update.MapId, delta.Landings);
        }
        private FWHeroBean AddPersistentHero(FWHeroBean hero)
        {
            string owner = bridge.Context.OwnerOfEntity(hero.getUID()); FWGameData data;
            if (!bridge.Players.TryGetValue(owner ?? "", out data)) throw new InvalidOperationException("New hero has no registered owner.");
            FWHeroBean canonical = bridge.PlayerHeroes(owner).SingleOrDefault(h => h.getUID() == hero.getUID());
            if (canonical == null) canonical = hero; else NativeWire.Copy(hero, canonical);
            foreach (FWFleet fleet in bridge.OwnedFleetsForReplication(owner)) fleet.removeHeroByUid(hero.getUID());
            if (!data.heros.Any(h => h.getUID() == canonical.getUID())) data.heros.Add(canonical);
            return canonical;
        }
        private FWAnimalBean AddPersistentAnimal(FWAnimalBean animal)
        {
            string owner = bridge.Context.OwnerOfEntity(animal.getUID());
            if (!bridge.Players.ContainsKey(owner ?? "")) throw new InvalidOperationException("Animal owner is not registered.");
            FWAnimalBean canonical = bridge.PlayerAnimals(owner).SingleOrDefault(a => a.getUID() == animal.getUID());
            if (canonical == null) canonical = animal; else NativeWire.Copy(animal, canonical);
            foreach (FWFleet fleet in bridge.OwnedFleetsForReplication(owner)) fleet.removeAnimalByUid(animal.getUID());
            if (!bridge.Players[owner].animals.Any(a => a.getUID() == canonical.getUID())) bridge.Players[owner].animals.Add(canonical);
            return canonical;
        }
        private void RestoreDepartedHero(FWHeroBean hero, long sourceMapId)
        {
            string owner = bridge.Context.OwnerOfEntity(hero.getUID());
            if (!bridge.Players.ContainsKey(owner ?? "")) throw new InvalidOperationException("Departed hero owner is unknown.");
            FWHeroBean canonical = bridge.PlayerHeroes(owner).SingleOrDefault(h => h.getUID() == hero.getUID());
            if (canonical == null) bridge.Players[owner].heros.Add(hero);
            else if (canonical.isInMap(sourceMapId))
            {
                NativeWire.Copy(hero, canonical);
                if (hero.fleetUid >= 0)
                {
                    FWFleet target = bridge.OwnedFleetsForReplication(owner).SingleOrDefault(f => f.getUID() == hero.fleetUid);
                    if (target == null) throw new InvalidOperationException("Departed hero target fleet is unknown.");
                    using (bridge.EnterPlayer(owner)) target.AddHero(canonical);
                }
            }
        }
        private void RestoreDepartedAnimal(FWAnimalBean animal, long sourceMapId)
        {
            string owner = bridge.Context.OwnerOfEntity(animal.getUID());
            if (!bridge.Players.ContainsKey(owner ?? "")) throw new InvalidOperationException("Departed animal owner is unknown.");
            FWAnimalBean canonical = bridge.PlayerAnimals(owner).SingleOrDefault(a => a.getUID() == animal.getUID());
            if (canonical == null) bridge.Players[owner].animals.Add(animal);
            else if (canonical.isInMap(sourceMapId))
            {
                NativeWire.Copy(animal, canonical);
                if (animal.fleetUid >= 0)
                {
                    FWFleet target = bridge.OwnedFleetsForReplication(owner).SingleOrDefault(f => f.getUID() == animal.fleetUid);
                    if (target == null) throw new InvalidOperationException("Departed animal target fleet is unknown.");
                    using (bridge.EnterPlayer(owner)) target.AddAnimal(canonical);
                }
            }
        }
        private void SceneMutation(string collection, object bean, bool add)
        {
            string[] spec = Managers[collection];
            if (collection == "plants" && !add) NativeWire.Call(MapManager.instance.plantManager, "removePlant", bean, false);
            else NativeWire.Call(NativeWire.Get(MapManager.instance, spec[0]), spec[add ? 1 : 2], bean);
        }
        internal SpriteObject SceneObject(long uid)
        {
            if (MapManager.instance == null) return null;
            BaseHero hero = MapManager.instance.heroManager.getHeroByUID(uid); if (hero != null) return hero;
            BaseAnimal animal = MapManager.instance.animalManager.getAnimalByUID(uid); if (animal != null) return animal;
            BaseMonster monster = MapManager.instance.monsterManager.getMonsterByUID(uid); if (monster != null) return monster;
            BaseBuild build = MapManager.instance.buildManager.getBuildByUID(uid); if (build != null) return build;
            return null;
        }
        private void RefreshScene(string collection, FWBaseBean bean)
        {
            if (collection == "things")
            {
                BaseThing thing = MapManager.instance.thingManager.getThingByUID(NativeWire.Uid(bean));
                if (thing == null) throw new InvalidOperationException("Item is missing its scene projection.");
                thing.repaint(); return;
            }
            if (collection == "plans")
            {
                BasePlanGraph graph = MapManager.instance.planManager.getPlanGraphByUID(NativeWire.Uid(bean));
                if (graph == null) throw new InvalidOperationException("Plan is missing its scene projection.");
                graph.repaint(); return;
            }
            if (collection == "plants")
            {
                NativeWire.Call(MapManager.instance.plantManager.getPlantByBean((FWPlantBean)bean), "repaint"); return;
            }
            if (collection == "ores")
            {
                FWMapOreBean ore = (FWMapOreBean)bean;
                NativeWire.Call(MapManager.instance.oreManager.getOreByCell(ore.cellX, ore.cellY), "repaint"); return;
            }
            if (collection == "zones") { MapManager.instance.zoneManager.repaint(); return; }
            if (collection == "rooms") { MapManager.instance.roomManage.repaint(); return; }
            if (collection == "floors") { NativeWire.Call(MapManager.instance.buildManager.getFloorByUID(NativeWire.Uid(bean)), "repaint"); return; }
            if (collection == "platforms") { NativeWire.Call(MapManager.instance.buildManager.getPlatformByUID(NativeWire.Uid(bean)), "repaint"); return; }
            SpriteObject obj = SceneObject(NativeWire.Uid(bean));
            if (obj == null) throw new InvalidOperationException("Persistent entity is missing its scene projection: " + NativeWire.Uid(bean));
            if (obj is BaseHero)
            {
                BaseHero hero = (BaseHero)obj; hero.roleAnim.refreshEquip(); hero.carryContainer.refreshAnim();
            }
            NativeWire.Call(obj, "repaint");
        }
        private void ApplyPose(Pose pose)
        {
            BaseRole hero = SceneObject(pose.Uid) as BaseRole;
            if (hero == null) throw new InvalidOperationException("Pose references missing character.");
            if (hero.cellX != hero.roleBean.cellX || hero.cellY != hero.roleBean.cellY)
            {
                MapGrid.getGridNode(hero.cellX, hero.cellY).removeCrops(hero);
                MapGrid.getInstance().releasePlance(hero.cellX, hero.cellY);
                hero.setCell(hero.roleBean.cellX, hero.roleBean.cellY);
                MapGrid.getInstance().doPlace_Role(hero, hero.cellX, hero.cellY);
                MapGrid.getGridNode(hero.cellX, hero.cellY).setCrops(hero);
            }
            hero.transform.localPosition = new Vector3(pose.X, pose.Y, pose.Z);
            hero.mod_Move.forwardDir = pose.Direction;
            HeroAnim anim = hero.roleAnim as HeroAnim;
            if (anim != null)
            {
                anim.target = pose.TargetUid == -1 ? null : SceneObject(pose.TargetUid);
                anim.correctPlayer(pose.Direction);
                if (pose.Animation >= 0 && (anim.playIndex != pose.Animation || anim.loop != pose.Loop)) anim.playAction(pose.Animation, pose.Loop, pose.IsAttack);
            }
            else if (hero.roleAnim is MonsterAnim)
            {
                MonsterAnim monster = (MonsterAnim)hero.roleAnim;
                monster.target = pose.TargetUid == -1 ? null : SceneObject(pose.TargetUid);
                monster.correctPlayer(pose.Direction);
                if (pose.Animation >= 0 && (monster.playIndex != pose.Animation || monster.loop != pose.Loop)) monster.playAction(pose.Animation, pose.Loop, pose.IsAttack);
            }
            else if (hero.roleAnim is AnimalAnim)
            {
                AnimalAnim animal = (AnimalAnim)hero.roleAnim;
                animal.target = pose.TargetUid == -1 ? null : SceneObject(pose.TargetUid);
                if (pose.Animation >= 0) animal.doAction(pose.Animation, pose.Direction, pose.Loop ? -1 : 1, !pose.Loop);
            }
        }
        public void Dispose() { revisions.Clear(); knownHeroes.Clear(); knownAnimals.Clear(); }
    }
}
