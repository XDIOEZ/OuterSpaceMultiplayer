using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace FreeWorldMultiplayer
{
    public sealed partial class GameBridge : IGameBridge
    {
        internal static GameBridge Active;
        internal BridgeContext Context;
        internal bool Applying;
        internal bool Executing;
        internal bool Advancing;
        internal volatile bool Seeding;
        internal string ActingPlayer;
        internal readonly Dictionary<string, FWGameData> Players = new Dictionary<string, FWGameData>();
        internal readonly Dictionary<long, MapAccess> Access = new Dictionary<long, MapAccess>();
        internal readonly Dictionary<long, FWStarMapInfo> Maps = new Dictionary<long, FWStarMapInfo>();
        internal readonly Dictionary<string, AttackRecord> Attacks = new Dictionary<string, AttackRecord>();
        internal readonly HashSet<string> Online = new HashSet<string>();
        internal FWGameData World;
        private FWGameData singlePlayerData;
        private Archive singlePlayerArchive;
        private UserFWStarSpread singlePlayerMap;
        private string hostId;
        private Harmony harmony;
        private UidRange seedRange;
        private readonly NativeMapReplicator replicator;
        private double worldFractionMM;
        private readonly Dictionary<long, long> offlineRemainders = new Dictionary<long, long>();
        private sealed class PersonalField { public string Name; public string Before; public string After; }
        private sealed class PersonalDelta { public int NativePlayerDelta = 1; public List<PersonalField> Fields = new List<PersonalField>(); }
        private readonly Dictionary<string, Dictionary<string, string>> personalBaselines = new Dictionary<string, Dictionary<string, string>>();
        private static readonly HashSet<string> PersonalFields = new HashSet<string>(new[] {
            "creditPoint", "virtualStore", "virtualAnimalStore", "virtualRobotStore", "techonologys", "technologyCustomPinIds", "technologyCustomPinDataVersion", "pdaIds", "completeMissionIds", "completeWorldSiteIds", "missions", "missionHandoffs", "followMissionUID", "missionRefreshRecList", "decrees", "heroTeams", "equipSchemes", "heroSchemes", "standoneScheme", "dnaList", "ET_LuaRecords", "scriptList", "guideMissionHandoffs" });
        public long WorldTimeMM { get { return World == null ? 0 : World.elapsedMM; } }
        internal long CurrentMapId { get { return UserBean.getInstance().curStarMap == null ? -1 : UserBean.getInstance().curStarMap.starMapId; } }
        internal bool IsSimulator { get { return Context != null && Context.CanSimulateMap(CurrentMapId); } }

        public GameBridge() { replicator = new NativeMapReplicator(this); }
        public void Attach(BridgeContext context)
        {
            if (Active != null && Active != this) throw new InvalidOperationException("Another multiplayer world is still attached.");
            Context = context ?? throw new ArgumentNullException("context");
            singlePlayerData = UserBean.getInstance().gameData;
            singlePlayerArchive = UserBean.getInstance().archive;
            singlePlayerMap = UserBean.getInstance().curStarMap;
            Active = this;
            harmony = new Harmony("FreeWorldMultiplayer.game.v1");
            GameHooks.Install(harmony);
        }
        internal long NextUid()
        {
            lock (seedUidLock)
            {
                if (Seeding)
                {
                    if (seedRange == null || seedRange.Next > seedRange.Last) throw new InvalidOperationException("Initial asset UID allocation exhausted.");
                    return seedRange.Next++;
                }
            }
            return Context.AllocateUid(CurrentMapId);
        }
        internal IDisposable EnterPlayer(string playerId)
        {
            FWGameData data;
            if (!Players.TryGetValue(playerId, out data)) throw new InvalidOperationException("Unknown player context: " + playerId);
            UserBean user = UserBean.getInstance();
            FWGameData previous = user.gameData;
            string previousActor = ActingPlayer;
            NativeWire.Set(user, "_gameData", data); ActingPlayer = playerId;
            return new Restore(() => { NativeWire.Set(user, "_gameData", previous); ActingPlayer = previousActor; });
        }
        private sealed class Restore : IDisposable
        {
            private Action restore;
            internal Restore(Action restore) { this.restore = restore; }
            public void Dispose() { Action action = restore; restore = null; if (action != null) action(); }
        }
        private BridgeResult Run(Action action)
        {
            try { action(); return BridgeResult.Ok(); }
            catch (NativeRejectionException error) { return BridgeResult.Reject(error.Message); }
            catch (Exception error) { return BridgeResult.Fail(error.GetType().Name + ": " + error.Message); }
        }
        private FWStartConfig StartConfig(WorldOptions options)
        {
            FWStartConfig config = string.IsNullOrWhiteSpace(options.StartPresetJson)
                ? FWStartConfig.createExample() : NativeWire.Parse<FWStartConfig>(options.StartPresetJson);
            if (config == null) throw new InvalidOperationException("Missing native start configuration.");
            config.MothershipEnabled = "1";
            config.BaseSetting.Type = "-1";
            return config;
        }
        public BridgeResult CreatePlayer(string playerId, UidRange uids, out PlayerSnapshot player)
        {
            player = null;
            if (IsCreatingWorld) return BridgeResult.Reject("新世界尚未完成，不能同时创建新玩家。");
            if (Players.ContainsKey(playerId)) { player = CapturePlayer(playerId); return BridgeResult.Ok(); }
            try
            {
                if (World == null) throw new InvalidOperationException("No host world.");
                Seeding = true; seedRange = uids;
                FWGameData current = UserBean.getInstance().gameData;
                UserFWStarSpread currentMap = UserBean.getInstance().curStarMap;
                Archive currentArchive = UserBean.getInstance().archive;
                FWGameData fresh;
                try
                {
                    fresh = CreateNativePlayer(playerId);
                }
                finally
                {
                    UserBean.getInstance().useGameData(current);
                    UserBean.getInstance().curStarMap = currentMap;
                    UserBean.getInstance().archive = currentArchive;
                }
                fresh.newComer = false;
                Players.Add(playerId, fresh); RebindSharedGalaxies(fresh);
                fresh.elapsedMM = World.elapsedMM;
                IndexMaps(fresh); RegisterPlayerAssets(playerId, fresh);
                ResetPersonalBaseline(playerId);
                player = CapturePlayer(playerId);
                return BridgeResult.Ok();
            }
            catch (Exception error) { return BridgeResult.Fail("Native player asset creation: " + error.Message); }
            finally { Seeding = false; seedRange = null; }
        }
        private FWGameData CreateNativePlayer(string playerId)
        {
            // Reuse the actual starter constructors against the existing galaxy. Generating another
            // galaxy and discarding it would leave NPC/mission references in a different world.
            FWStartConfig config = World.startConfig.copyBean();
            config.MothershipEnabled = "1"; config.BaseSetting.Type = "-1";
            PrepareStartingShipVariant(config);
            FWStartRandomContext random = FWStartRandomContext.CreateForNewArchive(config);
            var data = new FWGameData { startConfig = config, userId = checked(10000 + Players.Count),
                userData = new FWUserData(), messageStore = new FWMessageStore(), galaxyBlock = new FWGBGalaxy(),
                statistics = new FWGBStatistics(), elapsedMM = World.elapsedMM, repStellarForceResId = World.repStellarForceResId,
                technologyCustomPinIds = new List<string>(), technologyCustomPinDataVersion = FWGameData.TECHNOLOGY_CUSTOM_PIN_DATA_VERSION };
            data.galaxyBlock.knownGalaxies = World.galaxyBlock.knownGalaxies.Where(g => g.id != FWGameData.MOTHERSHIP_GALAXY_ID).ToList();
            data.setRandomRecord(FWRandomUtils.createRandomRecord(random.Unit("runtime.randomRecord").seed));
            NativeWire.Set(UserBean.getInstance(), "_gameData", data);
            FWGalaxy galaxy = data.galaxyBlock.knownGalaxies.First(g => g.stars != null && g.stars.Count > 0);
            FWStarInfo star = galaxy.stars.First();
            data.galaxyBlock.playerFleet = (FWFleet)NativeWire.Call(typeof(FWWorldGameDataService), "createStartFleet", data,
                config.FleetSetting.Type, new List<FWThingBean>(), new List<FWHeroBean>(), galaxy, star);
            foreach (TechnologyDef technology in TechnologyDef.beans)
                if (technology.SubType == (int)TechnologyDef.SubTypes.HiddenDefaultUnlock) data.techonologys.Add(technology.ID);
            object definition = NativeWire.Call(typeof(FWWorldGameDataService), "getMothershipDefinition", config);
            if (definition == null) throw new InvalidOperationException("No configured native mothership definition.");
            string template = (string)NativeWire.Call(typeof(FWWorldGameDataService), "getMothershipTemplatePath", definition);
            string ground = (string)NativeWire.Get(definition, "StarGroundID");
            if (string.IsNullOrWhiteSpace(ground)) ground = "120"; // Same documented native factory default.
            data.mothership = (FWStarInfo)NativeWire.Call(typeof(FWWorldGameDataService), "createMothership", data, random, template, ground);
            NativeWire.Call(typeof(FWWorldGameDataService), "unlockInitialMothershipLayer", data, definition);
            List<FWThingBean> things = FWThingUtils.createThingList(config.FleetSetting.Item);
            var roles = (List<FWHeroBean>)NativeWire.Call(typeof(FWWorldGameDataService), "createSpecialRoleList", config.FleetSetting.Role, random, "startFleet.roles");
            if (config.StartSetting.IsNoraCarried == "1") roles.Add(FWRoleUtils.createSpecialRole(random.Unit("startBase.role.nora.seed").randomSeed(), "1003"));
            NativeWire.Call(typeof(FWWorldGameDataService), "attachStartContentToStarMap", data, data.getMothershipStarMapInfo(), things, roles);
            NativeWire.Call(UserBean.getInstance(), "addCoins", (long)ZConfigDef.OrigiCP);
            UserBean.getInstance().addVirtualItem(new FWSItem { type = TypeStatics.TYPE_RANGE_VirtualProps, id = "1", sum = ZConfigDef.OrigiTechPoint });
            data.lastStarId = data.getMothershipStarMapInfo().starId; data.lastStarMapId = data.getMothershipStarMapInfo().starMapId;
            data.userData.nickname = playerId;
            return data;
        }
        private void RegisterPlayerAssets(string id, FWGameData data)
        {
            foreach (FWHeroBean hero in PlayerHeroes(id)) Context.RegisterEntityOwner(hero.getUID(), id);
            foreach (FWAnimalBean animal in PlayerAnimals(id)) Context.RegisterEntityOwner(animal.getUID(), id);
            foreach (FWAirShip ship in data.galaxyBlock.airShipList) Context.RegisterEntityOwner(ship.getUID(), id);
            Context.RegisterEntityOwner(data.galaxyBlock.playerFleet.getUID(), id);
            FWStarMapInfo mother = data.getMothershipStarMapInfo();
            if (mother == null) throw new InvalidOperationException("Native start did not create the player's mothership.");
            Context.RegisterEntityOwner(mother.starMapId, id);
            foreach (FWBaseBean bean in replicator.Beans(mother.starSpread))
                if (bean is FWBuildBean || bean is FWPlanBean) Context.RegisterEntityOwner(NativeWire.Uid(bean), id);
        }
        internal void IndexMaps(FWGameData data)
        {
            FWGameDataUtils.loopStarMapInfo(data, map => { Maps[map.starMapId] = map; return false; });
        }
        private void RebindSharedGalaxies(FWGameData data)
        {
            List<FWGalaxy> personal = data.galaxyBlock.knownGalaxies.Where(g => g.id == FWGameData.MOTHERSHIP_GALAXY_ID).ToList();
            data.galaxyBlock.knownGalaxies = World.galaxyBlock.knownGalaxies.Where(g => g.id != FWGameData.MOTHERSHIP_GALAXY_ID).Concat(personal).ToList();
            data.restoreMothershipReferences();
            RebindSharedWorldRoots(data);
        }
        private void ActivateLocal(string id)
        {
            FWGameData data = Players[id];
            UserBean.getInstance().useGameData(data);
            // There is deliberately no ArchiveInfo: all saves are intercepted and owned by Session.
            UserBean.getInstance().archive = null;
            // Session's single MapView/ApplyMap path performs the actual asynchronous scene entry.
            // Binding a personal context must never start a second concurrent map load.
        }
        private sealed class SavedWorld
        {
            public string HostId;
            public string World;
            public Dictionary<string, string> Players = new Dictionary<string, string>();
            public Dictionary<long, long> OfflineRemainders = new Dictionary<long, long>();
            public List<LootReservation> Loot = new List<LootReservation>();
            public List<NativeLanding> Landings = new List<NativeLanding>();
        }
        public GameSnapshot CaptureWorld()
        {
            SavedWorld saved = new SavedWorld { HostId = hostId, World = NativeWire.Pack(World), OfflineRemainders = new Dictionary<long, long>(offlineRemainders) };
            saved.Loot = LootReservations.ToList();
            foreach (long mapId in NativeLandings.Values.Select(l => l.MapId).Distinct().ToArray()) saved.Landings.AddRange(CaptureNativeLandings(mapId));
            foreach (var pair in Players) if (pair.Key != hostId) saved.Players.Add(pair.Key, PackPlayerData(pair.Value));
            return new GameSnapshot { NativeJson = NativeWire.Json(saved), WorldTimeMM = WorldTimeMM };
        }
        public PlayerSnapshot CapturePlayer(string playerId)
        {
            FWGameData data = Players[playerId];
            return new PlayerSnapshot { PlayerId = playerId, NativeJson = PackPlayerData(data),
                MotherShipMapId = data.getMothershipStarMapInfo().starMapId,
                FleetId = data.galaxyBlock.playerFleet.getUID(), HeroIds = PlayerHeroes(playerId).Select(h => h.getUID()).ToList(),
                OwnedEntityIds = data.heros.Select(h => h.getUID()).Concat(data.galaxyBlock.airShipList.Select(s => s.getUID())).ToList() };
        }
        private string PackPlayerData(FWGameData data)
        {
            List<FWGalaxy> galaxies = data.galaxyBlock.knownGalaxies;
            try
            {
                data.galaxyBlock.knownGalaxies = galaxies.Where(g => g.id == FWGameData.MOTHERSHIP_GALAXY_ID).ToList();
                return NativeWire.Pack(data);
            }
            finally { data.galaxyBlock.knownGalaxies = galaxies; }
        }
        public BridgeResult LoadWorld(GameSnapshot world, PlayerSnapshot localPlayer)
        {
            return Run(() =>
            {
                Applying = true;
                try
                {
                    SavedWorld saved = NativeWire.Parse<SavedWorld>(world.NativeJson);
                    World = NativeWire.Unpack<FWGameData>(saved.World); hostId = saved.HostId;
                    offlineRemainders.Clear(); foreach (var remainder in saved.OfflineRemainders) offlineRemainders.Add(remainder.Key, remainder.Value);
                    LootReservations.Clear(); LootReservations.AddRange(saved.Loot);
                    NativeLandings.Clear();
                    Players.Clear(); Maps.Clear(); Players.Add(hostId, World);
                    foreach (var pair in saved.Players) Players.Add(pair.Key, NativeWire.Unpack<FWGameData>(pair.Value));
                    if (!Players.ContainsKey(localPlayer.PlayerId)) Players.Add(localPlayer.PlayerId, NativeWire.Unpack<FWGameData>(localPlayer.NativeJson));
                    foreach (FWGameData data in Players.Values) { if (data != World) RebindSharedGalaxies(data); IndexMaps(data); }
                    foreach (var group in saved.Landings.GroupBy(l => l.MapId)) RestoreNativeLandings(group.Key, group.ToList());
                    ActivateLocal(localPlayer.PlayerId);
                }
                finally { Applying = false; }
            });
        }
        public BridgeResult ApplyPlayer(PlayerSnapshot player, bool localPlayer)
        {
            return Run(() =>
            {
                if (player.NativeJson.StartsWith("{", StringComparison.Ordinal))
                {
                    PersonalDelta delta = NativeWire.Parse<PersonalDelta>(player.NativeJson);
                    FWGameData existing = Players[player.PlayerId];
                    foreach (PersonalField change in delta.Fields)
                    {
                        if (!PersonalFields.Contains(change.Name)) throw new InvalidOperationException("Rejected personal field: " + change.Name);
                        FieldInfo field = NativeWire.Field(typeof(FWGameData), change.Name);
                        if (NativeWire.Pack(field.GetValue(existing)) == change.After) continue;
                        if (NativeWire.Pack(field.GetValue(existing)) != change.Before) throw new InvalidOperationException("Personal delta baseline mismatch: " + change.Name);
                    }
                    foreach (PersonalField change in delta.Fields)
                    {
                        FieldInfo field = NativeWire.Field(typeof(FWGameData), change.Name);
                        field.SetValue(existing, NativeWire.Unpack(change.After, field.FieldType));
                    }
                    ResetPersonalBaseline(player.PlayerId);
                    return;
                }
                FWGameData canonical;
                if (Players.TryGetValue(player.PlayerId, out canonical))
                {
                    if (canonical.getMothershipStarMapInfo().starMapId != player.MotherShipMapId)
                        throw new InvalidOperationException("Player snapshot conflicts with the loaded canonical world generation.");
                    // LoadWorld already restored this exact generation. Replacing it with a second
                    // unpack would split World/Players and all shared galaxy/map references.
                    if (localPlayer) ActivateLocal(player.PlayerId);
                    return;
                }
                FWGameData data = NativeWire.Unpack<FWGameData>(player.NativeJson);
                if (World != null) RebindSharedGalaxies(data);
                Players[player.PlayerId] = data; IndexMaps(data);
                ResetPersonalBaseline(player.PlayerId);
                if (localPlayer) ActivateLocal(player.PlayerId);
            });
        }
        private void ResetPersonalBaseline(string playerId)
        {
            personalBaselines[playerId] = PersonalFields.ToDictionary(name => name, name => NativeWire.Pack(NativeWire.Get(Players[playerId], name)));
        }
        internal List<PlayerUpdate> CapturePersonalDeltas()
        {
            var updates = new List<PlayerUpdate>();
            foreach (var pair in Players)
            {
                Dictionary<string, string> previous;
                if (!personalBaselines.TryGetValue(pair.Key, out previous)) { ResetPersonalBaseline(pair.Key); continue; }
                var delta = new PersonalDelta();
                foreach (string field in PersonalFields)
                {
                    string after = NativeWire.Pack(NativeWire.Get(pair.Value, field));
                    if (after != previous[field]) { delta.Fields.Add(new PersonalField { Name = field, Before = previous[field], After = after }); previous[field] = after; }
                }
                if (delta.Fields.Count > 0)
                {
                    long revision = Context.GetPlayerRevision(pair.Key);
                    updates.Add(new PlayerUpdate { PlayerId = pair.Key, BaseRevision = revision, Revision = revision + 1, NativeDeltaJson = NativeWire.Json(delta) });
                }
            }
            return updates;
        }
        public List<PlayerUpdate> CapturePlayerUpdates() { return CapturePersonalDeltas(); }
        public MapSnapshot CaptureMap(long mapId) { return replicator.Capture(mapId); }
        public MapUpdate CaptureMapUpdate(long mapId, MapSnapshot previous) { return replicator.CaptureUpdate(mapId, previous); }
        public BridgeResult ApplyMap(MapSnapshot map, MapAccess access)
        {
            return Run(() =>
            {
                Applying = true;
                try { Access[map.MapId] = access; replicator.RestoreCheckpoint(map); }
                finally { Applying = false; }
            });
        }
        public BridgeResult ApplyMapUpdate(MapUpdate update)
        {
            return Run(() =>
            {
                Applying = true;
                try
                {
                    replicator.Validate(update);
                    foreach (PlayerUpdate player in update.Players) ValidatePersonal(player);
                    replicator.ApplyDelta(update);
                    foreach (PlayerUpdate player in update.Players) ApplyPersonal(player);
                }
                finally { Applying = false; }
            });
        }
        public BridgeResult ValidateMapUpdate(MapUpdate update)
        {
            return Run(() => { replicator.Validate(update); foreach (PlayerUpdate player in update.Players) ValidatePersonal(player); });
        }
        private PersonalDelta ValidatePersonal(PlayerUpdate update)
        {
            FWGameData player;
            if (!Players.TryGetValue(update.PlayerId, out player)) throw new InvalidOperationException("Unknown personal delta owner.");
            PersonalDelta delta = NativeWire.Parse<PersonalDelta>(update.NativeDeltaJson);
            if (delta.NativePlayerDelta != 1) throw new InvalidOperationException("Unknown personal delta schema.");
            foreach (PersonalField change in delta.Fields)
            {
                if (!PersonalFields.Contains(change.Name)) throw new InvalidOperationException("Rejected personal delta field.");
                FieldInfo field = NativeWire.Field(typeof(FWGameData), change.Name);
                string current = NativeWire.Pack(field.GetValue(player));
                if (current != change.Before && current != change.After) throw new InvalidOperationException("Personal field baseline mismatch: " + change.Name);
                NativeWire.Unpack(change.After, field.FieldType);
            }
            return delta;
        }
        private void ApplyPersonal(PlayerUpdate update)
        {
            PersonalDelta delta = ValidatePersonal(update);
            foreach (PersonalField change in delta.Fields)
            {
                FieldInfo field = NativeWire.Field(typeof(FWGameData), change.Name);
                field.SetValue(Players[update.PlayerId], NativeWire.Unpack(change.After, field.FieldType));
            }
            ResetPersonalBaseline(update.PlayerId);
        }
        public BridgeResult ApplyPlayerUpdate(PlayerUpdate update) { return Run(() => ApplyPersonal(update)); }
        public PlayerFieldState CapturePlayerFields(string playerId)
        {
            return new PlayerFieldState { PlayerId = playerId, PersonalRevision = Context.GetPlayerRevision(playerId),
                NativeFieldsJson = NativeWire.Json(PersonalFields.ToDictionary(name => name, name => NativeWire.Pack(NativeWire.Get(Players[playerId], name)))) };
        }
        public BridgeResult ApplyPlayerFields(PlayerFieldState state)
        {
            return Run(() =>
            {
                var fields = NativeWire.Parse<Dictionary<string, string>>(state.NativeFieldsJson);
                if (fields.Count != PersonalFields.Count || fields.Keys.Any(k => !PersonalFields.Contains(k))) throw new InvalidOperationException("Personal field resync schema mismatch.");
                var decoded = fields.ToDictionary(pair => pair.Key, pair => NativeWire.Unpack(pair.Value, NativeWire.Field(typeof(FWGameData), pair.Key).FieldType));
                foreach (var pair in decoded) NativeWire.Set(Players[state.PlayerId], pair.Key, pair.Value);
                ResetPersonalBaseline(state.PlayerId);
            });
        }
        public BridgeResult SetMapAuthority(MapAccess access)
        {
            return Run(() =>
            {
                Access[access.MapId] = access;
                replicator.SetRevision(access.MapId, access.Revision);
                if (CurrentMapId == access.MapId && access.Access != MapAccessMode.Simulator && MapManager.instance != null)
                {
                    AirSupportManager.getInstance()?.CancelExpeditionLandings();
                    foreach (BaseHero hero in MapManager.instance.heroManager.getAllRoles()) hero.doInterruptAction();
                }
            });
        }
        public BridgeResult AdvanceWorld(double realSeconds, double speed, IList<string> onlinePlayerIds)
        {
            return Run(() =>
            {
                if (!Context.IsHost) throw new InvalidOperationException("Only host advances the world.");
                if (realSeconds < 0 || speed < 0 || double.IsNaN(speed)) throw new ArgumentOutOfRangeException("speed");
                Online.Clear(); foreach (string id in onlinePlayerIds) Online.Add(id);
                worldFractionMM += realSeconds * speed * 1000.0;
                long delta = (long)worldFractionMM; worldFractionMM -= delta;
                Advancing = true;
                try
                {
                    using (EnterPlayer(hostId))
                    {
                        WorldSimulationAdvanceResult result = WorldClockAdvanceService.AdvanceBy(World, delta);
                        if (!result.success) throw new InvalidOperationException("World advance rejected: " + result.reason);
                    }
                    foreach (var pair in Players) pair.Value.elapsedMM = World.elapsedMM;
                    TickPlayerFleets(delta);
                }
                finally { Advancing = false; }
            });
        }
        public BridgeResult SimulateOffline(long mapId, long fromWorldMM, long toWorldMM)
        {
            return Run(() =>
            {
                if (toWorldMM <= fromWorldMM) return;
                FWStarMapInfo map = Maps[mapId];
                string owner = Context.OwnerOfMap(mapId);
                using (EnterPlayer(owner))
                {
                    FWGameData data = Players[owner];
                    List<FWHeroBean> originalHeroes = data.heros;
                    long originalTime = data.elapsedMM;
                    long remainder; offlineRemainders.TryGetValue(mapId, out remainder);
                    long duration = checked(toWorldMM - fromWorldMM + remainder);
                    long seconds = duration / FWTimeUtils.SECOND;
                    offlineRemainders[mapId] = duration % FWTimeUtils.SECOND;
                    try
                    {
                        // The native worker selector scans gameData.heros. Include actual resident
                        // visitors exactly once, without changing their permanent owning list.
                        data.heros = Players.Values.SelectMany(p => p.heros).Where(h => h.isInMap(mapId))
                            .Where(h => Context.OwnerOfEntity(h.getUID()) == owner || Context.GetRelation(Context.OwnerOfEntity(h.getUID()), owner) == Relation.Friendly)
                            .GroupBy(h => h.getUID()).Select(g => g.First()).ToList();
                        data.elapsedMM = fromWorldMM - remainder;
                        if (seconds > 0) FWStarMapAutoLiveService.simulateExtraOfflineFps(map, seconds);
                    }
                    finally { data.heros = originalHeroes; data.elapsedMM = originalTime; }
                    FWStarMapAutoLiveService.syncObservedCheckpoint(map, toWorldMM);
                }
            });
        }
        public BridgeResult ClaimMap(long mapId, string ownerPlayerId)
        {
            return Run(() =>
            {
                FWStarMapInfo map;
                FWGameData player;
                if (!Maps.TryGetValue(mapId, out map) || map.starSpread == null || !map.isAvaliable()) throw new InvalidOperationException("Claim target has no valid native map.");
                if (!Players.TryGetValue(ownerPlayerId, out player)) throw new InvalidOperationException("Claim owner has no native player context.");
                string existing = Context.OwnerOfMap(mapId);
                if (!string.IsNullOrEmpty(existing) && existing != ownerPlayerId) throw new InvalidOperationException("地块已被其他玩家占领。");
                using (EnterPlayer(ownerPlayerId))
                {
                    FWPlayerLocationGuard.EnsureSurfaceMap(player, map);
                    map.setType(FWStarMapInfo.TYPE_DEPUTY_BASE);
                    if (!player.developStarMapIds.Contains(mapId)) player.developStarMapIds.Add(mapId);
                }
            });
        }
        public BridgeResult SetFormalBase(long mapId, string ownerPlayerId)
        {
            return Run(() =>
            {
                if (Context.OwnerOfMap(mapId) != ownerPlayerId) throw new InvalidOperationException("Only an occupied own map can become a formal base.");
                FWGameData player = Players[ownerPlayerId]; FWStarMapInfo map = Maps[mapId];
                if (map.starSpread == null || !map.isAvaliable() || map.starSpread.isAirMap()) throw new InvalidOperationException("Formal base must be an available surface map.");
                foreach (FWStarMapInfo previous in Maps.Values)
                    if (previous.starMapId != mapId && Context.OwnerOfMap(previous.starMapId) == ownerPlayerId && previous.isRootBase()) previous.setType(FWStarMapInfo.TYPE_DEPUTY_BASE);
                map.setType(FWStarMapInfo.TYPE_ROOT_BASE); player.lastBaseStarMapId = mapId;
                FWStarMapAutoLiveService.syncObservedCheckpoint(map, WorldTimeMM);
            });
        }
        public BridgeResult ExecuteCommand(GameCommand command)
        {
            return Run(() =>
            {
                if (command.MapId != CurrentMapId || !Context.CanSimulateMap(command.MapId))
                    throw new InvalidOperationException("Command simulator has not loaded the authority map.");
                Executing = true;
                try { using (EnterPlayer(command.PlayerId)) ExecuteNative(command); }
                finally { Executing = false; }
            });
        }
        internal bool Route(CommandKind kind, long actorUid, long targetUid, object payload)
        {
            if (Applying || Executing) return true;
            MapAccess access = Context.GetMapAccess(CurrentMapId);
            var command = new GameCommand { Kind = kind, PlayerId = Context.LocalPlayerId, MapId = CurrentMapId,
                EntityId = actorUid, TargetEntityId = targetUid, PayloadJson = NativeWire.Json(payload),
                AuthorityEpoch = access == null ? 0 : access.AuthorityEpoch, BaseRevision = access == null ? 0 : access.Revision };
            CommandResult result = Context.SubmitCommand(command);
            if (!result.Success && !string.IsNullOrEmpty(result.Error)) Debug.LogWarning("[FreeWorldMultiplayer] " + result.Error);
            return false;
        }
        private void ExecuteNative(GameCommand command)
        {
            JObject payload = JObject.Parse(command.PayloadJson ?? "{}");
            BaseHero actor = MapManager.instance.heroManager.getHeroByUID(command.EntityId);
            switch (command.Kind)
            {
                case CommandKind.Move:
                    if (actor == null) throw new InvalidOperationException("Missing command hero.");
                    RoleCommand.order_Fight_Move(actor, payload.Value<short>("X"), payload.Value<short>("Y")); break;
                case CommandKind.Conscript:
                    if (actor == null) throw new InvalidOperationException("Missing command hero.");
                    RoleCommand.order_Fight_Conscript(actor, payload.Value<bool>("Manual")); break;
                case CommandKind.Attack:
                    if (actor == null) throw new InvalidOperationException("Missing command hero.");
                    SpriteObject target = replicator.SceneObject(command.TargetEntityId);
                    if (target == null) throw new InvalidOperationException("Attack target no longer exists.");
                    if (!Context.HasPermission(command.PlayerId, command.MapId, command.TargetEntityId, Permission.Attack)) throw new InvalidOperationException("Attack denied.");
                    RoleCommand.order_Fight_Attack(actor, target); break;
                case CommandKind.TakeItem:
                    FWThingBean thing = UserBean.getInstance().curStarMap.getThingBeanByUID(command.TargetEntityId);
                    if (actor == null || thing == null) throw new InvalidOperationException("Missing pickup actor or item.");
                    if (!Context.HasPermission(command.PlayerId, command.MapId, thing.getUID(), Permission.TakeInventory)
                        && !Context.HasPermission(command.PlayerId, command.MapId, thing.getUID(), Permission.Loot)) throw new InvalidOperationException("Pickup denied.");
                    FWPlanBean carry = new FWPlanBean(FWPlanBean.TASK_CARRY, -1, "-1");
                    carry.objectUID = thing.getUID(); carry.setCell(thing.cellX, thing.cellY); carry.setPlanStep(FWPlanBean.STEP_BLUEPRINT);
                    BasePlanGraph pickupPlan = MapManager.instance.planManager.getThingPlan(thing.getUID(), FWPlanBean.TASK_CARRY)
                        ?? LocalCoreHandle.Plan_addNewPlan_LN(carry);
                    if (!pickupPlan.rolePlan.isCanDo(actor)) throw new InvalidOperationException("当前雇员无法沿原生任务路径拿取该物资。");
                    actor.doAction(pickupPlan.rolePlan); break;
                default:
                    if (NativeBuildingCommands.TryExecute(this, command)) break;
                    NativeCommands.Execute(this, command); break;
            }
        }
        internal object FindSceneObject(long uid)
        {
            SpriteObject sprite = replicator.SceneObject(uid);
            if (sprite != null) return sprite;
            return MapManager.instance == null ? null : MapManager.instance.thingManager.getThingByUID(uid);
        }
        internal FWBaseBean FindPersistentBean(long uid)
        {
            foreach (string playerId in Players.Keys)
            {
                FWHeroBean hero = PlayerHeroes(playerId).FirstOrDefault(h => h.getUID() == uid); if (hero != null) return hero;
                FWAnimalBean animal = PlayerAnimals(playerId).FirstOrDefault(h => h.getUID() == uid); if (animal != null) return animal;
            }
            foreach (FWStarMapInfo map in Maps.Values)
            {
                FWBaseBean found = replicator.Beans(map.starSpread).FirstOrDefault(b => NativeWire.Uid(b) == uid);
                if (found != null) return found;
            }
            return null;
        }
        internal IEnumerable<FWHeroBean> PlayerHeroes(string playerId)
        { return Players[playerId].heros.Concat(OwnedFleets(playerId).SelectMany(f => f.getHeros())).GroupBy(h => h.getUID()).Select(g => g.First()); }
        internal IEnumerable<FWAnimalBean> PlayerAnimals(string playerId)
        { return Players[playerId].animals.Concat(OwnedFleets(playerId).SelectMany(f => f.getAnimals())).GroupBy(a => a.getUID()).Select(g => g.First()); }
        internal IEnumerable<FWFleet> OwnedFleetsForReplication(string playerId) { return OwnedFleets(playerId); }
        internal void EnsureMapCharacters()
        {
            if (MapManager.instance == null || UserBean.getInstance().curStarMap == null) return;
            foreach (FWHeroBean hero in Players.Values.SelectMany(p => p.heros).Where(h => h.isInMap(CurrentMapId)))
                if (MapManager.instance.heroManager.getHeroByUID(hero.getUID()) == null) MapManager.instance.heroManager.addRole(hero);
        }
        public void Dispose()
        {
            if (IsCreatingWorld) throw new InvalidOperationException("原生世界生成仍持有上下文，请等待PollCreateWorld完成后再释放联机会话。");
            var failures = new List<Exception>();
            try { if (harmony != null) harmony.UnpatchAll(harmony.Id); }
            catch (Exception error) { failures.Add(error); }
            try { DisposeGalaxy(); }
            catch (Exception error) { failures.Add(error); }
            if (Active == this) Active = null;
            try
            {
                UserBean user = UserBean.tryGetInstance();
                if (user != null)
                {
                    // Cleanup must also work when NativeWire's static initialization was the failure.
                    // Native binding restores the previous archive's services after a failed/cancelled
                    // worker. The raw field finally guarantees its reference even if binding fails.
                    FieldInfo field = typeof(UserBean).GetField("_gameData", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (field == null) throw new MissingFieldException(typeof(UserBean).FullName, "_gameData");
                    try
                    {
                        user.archive = singlePlayerArchive;
                        if (!ReferenceEquals(user.gameData, singlePlayerData))
                        {
                            if (singlePlayerData != null) user.useGameData(singlePlayerData);
                            else { field.SetValue(user, null); FWGalaxyEventService.Initialize(null); }
                        }
                    }
                    finally { field.SetValue(user, singlePlayerData); user.archive = singlePlayerArchive; user.curStarMap = singlePlayerMap; }
                }
            }
            catch (Exception error) { failures.Add(error); }
            finally { replicator.Dispose(); Players.Clear(); Maps.Clear(); Access.Clear(); }
            if (failures.Count != 0) throw new AggregateException("联机会话释放失败；请查看各阶段原始异常。", failures);
        }
    }
}
