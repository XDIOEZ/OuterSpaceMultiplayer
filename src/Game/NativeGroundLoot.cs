using System;
using System.Collections.Generic;
using System.Linq;

namespace FreeWorldMultiplayer
{
    public sealed partial class GameBridge
    {
        private readonly Dictionary<string, GameCommand> pendingGroundRaids = new Dictionary<string, GameCommand>();
        private readonly Dictionary<string, DateTime> groundRaidRetryAt = new Dictionary<string, DateTime>();
        // Never use only the currently viewed scene: a non-viewed map still has defenders.
        public List<string> ActiveGroundPlayerIds(long mapId)
        {
            FWStarMapInfo map;
            if (!Maps.TryGetValue(mapId, out map) || map.starSpread == null || Players.Count == 0) return null;
            if (CurrentMapId == mapId && !IsNativeMapReady(mapId)) return null;
            return Players.Keys.Where(id => PlayerHeroes(id).Any(h => h.isInMap(mapId) && GroundHeroActive(h))).ToList();
        }
        internal bool GroundHeroActive(FWHeroBean hero)
        { return hero != null && !hero.isDead() && !hero.prisoner && !hero.isFallDown() && !GameHooks.IsKnockedOut(hero.getUID()); }

        private bool GroundHumanDefenderActive(FWHeroBean hero)
        { return GroundHeroActive(hero) && !hero.isAndroid(); }

        internal string GroundZoneOwner(FWAreaZoneBean zone, long mapId)
        {
            if (zone == null) return null;
            string owner = Context.OwnerOfEntity(zone.getUID());
            if (owner != null) return owner;
            FWStarMapInfo map;
            if (Maps.TryGetValue(mapId, out map) && map.starSpread != null)
                foreach (FWBox box in replicator.Beans(map.starSpread).OfType<FWBox>())
                    if (ReferenceEquals(box.areaZoneBean, zone)) return Context.OwnerOfEntity(box.getUID()) ?? Context.OwnerOfMap(mapId);
            return Context.OwnerOfMap(mapId);
        }
        internal bool CanTakeGroundZone(string actor, FWAreaZoneBean zone)
        {
            string owner = GroundZoneOwner(zone, CurrentMapId);
            return owner == null || owner == actor || Context.CanLootGroundOwner(actor, owner, CurrentMapId);
        }
        internal string ProtectedGroundItemOwner(long mapId, FWThingBean item)
        {
            string mapOwner = Context.OwnerOfMap(mapId);
            if (mapOwner != null) return mapOwner;
            FWStarMapInfo map;
            if (item == null || !Maps.TryGetValue(mapId, out map) || map.starSpread == null) return null;
            // Cell membership is authoritative, including stock whose native scene is unloaded.
            FWAreaZoneBean zone = map.starSpread.zones.FirstOrDefault(z => ZoneDef.isStoreType(z.id) && z.isInZone(item.cellX, item.cellY));
            if (zone != null) return GroundZoneOwner(zone, mapId);
            foreach (FWBox box in replicator.Beans(map.starSpread).OfType<FWBox>())
                if (box.areaZoneBean.isInZone(item.cellX, item.cellY)) return Context.OwnerOfEntity(box.getUID());
            return null;
        }
        internal bool CanTakeGroundThing(string actor, BaseThing thing)
        { return thing == null || CanTakeGroundItem(actor, CurrentMapId, thing.thingBean); }
        private bool CanTakeGroundItem(string actor, long mapId, FWThingBean item)
        {
            string owner = ProtectedGroundItemOwner(mapId, item);
            return owner == null || owner == actor || Context.CanLootGroundOwner(actor, owner, mapId);
        }
        internal bool IsGroundRaidParticipant(AttackRecord attack, string actor)
        { return attack != null && (attack.AttackerPlayerId == actor || attack.ParticipantPlayerIds.Contains(actor)); }
        private List<long> GroundRaidHeroes(AttackRecord attack, string actor)
        {
            List<long> heroes;
            return attack.ParticipantHeroIds.TryGetValue(actor, out heroes) ? heroes
                : attack.AttackerPlayerId == actor ? attack.AttackerHeroIds : new List<long>();
        }
        private long GroundRaidFleet(AttackRecord attack, string actor)
        {
            long fleet;
            return attack.ParticipantFleetIds.TryGetValue(actor, out fleet) ? fleet : actor == attack.AttackerPlayerId ? attack.SourceFleetId : -1;
        }
        internal bool EnsureGroundRaid(string actor, long mapId, long heroUid = -1)
        {
            if (string.IsNullOrEmpty(actor) || !Players.ContainsKey(actor)) return false;
            if (Context.OwnerOfMap(mapId) == actor) return true; // Resident defenders never become a returning raid party.
            FWStarMapInfo map;
            if (!Maps.TryGetValue(mapId, out map) || map.starSpread == null) return false;
            if (map.starSpread.isAirMap()) return true;
            if (ActiveGroundPlayerIds(mapId) == null) return false;
            AttackRecord attack = Context.GetAttack(mapId);
            string key = actor + ":" + mapId + ":" + heroUid;
            if (attack != null && attack.Stage == AttackStage.Active && IsGroundRaidParticipant(attack, actor)
                && (heroUid < 0 || GroundRaidHeroes(attack, actor).Contains(heroUid)))
            { pendingGroundRaids.Remove(key); groundRaidRetryAt.Remove(key); return true; }
            if (Context.IsRaidCoolingDown(mapId)) return false;
            DateTime retryAt;
            if (groundRaidRetryAt.TryGetValue(key, out retryAt) && retryAt > DateTime.UtcNow) return false;
            var heroes = PlayerHeroes(actor).Where(h => h.isInMap(mapId) && GroundHeroActive(h)).Select(h => h.getUID()).ToList();
            if (heroes.Count == 0) return false;
            // Use a completed, saved native landing. Never choose a different fleet's larger
            // vehicle to manufacture a new raid capacity after employees are already ashore.
            TransferRecord arrival = NativeLandings.Values.Where(l => l.MapId == mapId && l.PlayerId == actor && l.Complete)
                .Select(l => Context.GetTransfer(l.ExpeditionUid)).Where(t => t != null && !t.Returning && t.PlayerId == actor
                    && t.Request.DestinationMapId == mapId && t.Request.HeroIds.Any(heroes.Contains)
                    && (heroUid < 0 || t.Request.HeroIds.Contains(heroUid)))
                .OrderBy(t => t.TransferId, StringComparer.Ordinal).FirstOrDefault();
            if (arrival == null) return false;
            FWFleet fleet = arrival.Request.SourceFleetId >= 0 ? TransferFleet(actor, arrival.Request.SourceFleetId) : Players[actor].galaxyBlock.playerFleet;
            var request = new RaidRequest { PlayerId = actor, AlreadyOnGround = true, ExistingExpeditionId = arrival.NativeExpeditionId,
                MapId = mapId, SourceMapId = arrival.Request.SourceMapId, SourceFleetId = fleet.getUID(),
                AttackerHeroIds = heroes.Where(arrival.Request.HeroIds.Contains).ToList(), NativeTransportJson = arrival.Request.NativeManifestJson };
            GameCommand command;
            if (!pendingGroundRaids.TryGetValue(key, out command))
            {
                command = new GameCommand { CommandId = Guid.NewGuid().ToString("N"), Kind = CommandKind.StartRaid, MapId = mapId,
                    PlayerId = actor, PayloadJson = JsonData.Write(request) };
                pendingGroundRaids[key] = command;
                Context.RegisterCommandCallback(command.CommandId, result =>
                { if (!result.Success && !result.Applied) pendingGroundRaids.Remove(key); });
            }
            groundRaidRetryAt[key] = DateTime.UtcNow.AddSeconds(1);
            CommandResult submitted = Context.SubmitCommand(command);
            attack = Context.GetAttack(mapId);
            return submitted.Success && attack != null && attack.Stage == AttackStage.Active && IsGroundRaidParticipant(attack, actor);
        }
    }
}
