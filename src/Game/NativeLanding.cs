using System;
using System.Collections.Generic;
using System.Linq;

namespace FreeWorldMultiplayer
{
    public sealed partial class GameBridge
    {
        internal sealed class NativeLanding
        {
            public long ExpeditionUid;
            public long MapId;
            public string PlayerId;
            public string Expedition;
            public List<string> Heroes = new List<string>();
            public List<string> Animals = new List<string>();
            public bool Complete;
        }
        internal readonly Dictionary<long, NativeLanding> NativeLandings = new Dictionary<long, NativeLanding>();

        internal void QueueNativeLanding(FWExpedition expedition, long mapId, string ownerPlayerId)
        {
            if (expedition == null || expedition.endStarMapId != mapId || !Players.ContainsKey(ownerPlayerId) || !Maps.ContainsKey(mapId))
                throw new InvalidOperationException("Native landing owner/map/expedition mismatch.");
            NativeLanding existing;
            if (NativeLandings.TryGetValue(expedition.getUID(), out existing))
            {
                if (existing.PlayerId != ownerPlayerId || existing.MapId != mapId) throw new InvalidOperationException("Landing identity changed.");
                return;
            }
            NativeLandings.Add(expedition.getUID(), new NativeLanding { ExpeditionUid = expedition.getUID(), MapId = mapId,
                PlayerId = ownerPlayerId, Expedition = NativeWire.Pack(expedition) });
        }
        internal List<NativeLanding> CaptureNativeLandings(long mapId)
        {
            foreach (NativeLanding landing in NativeLandings.Values.Where(l => l.MapId == mapId && !l.Complete))
            {
                FWGameData owner = Players[landing.PlayerId];
                FWExpedition expedition = owner.expeditionSeq.SingleOrDefault(e => e.getUID() == landing.ExpeditionUid);
                if (expedition == null) throw new InvalidOperationException("Pending landing lost its canonical expedition: " + landing.ExpeditionUid);
                landing.Expedition = NativeWire.Pack(expedition);
                landing.Heroes = expedition.roleUidList.Select(uid => NativeWire.Pack(owner.heros.Single(h => h.getUID() == uid))).ToList();
                landing.Animals = expedition.animalUidList.Select(uid => NativeWire.Pack(owner.animals.Single(a => a.getUID() == uid))).ToList();
            }
            return NativeLandings.Values.Where(l => l.MapId == mapId).ToList();
        }
        internal void ValidateNativeLandings(long mapId, List<NativeLanding> landings)
        {
            if (landings == null || landings.Select(l => l.ExpeditionUid).Distinct().Count() != landings.Count)
                throw new InvalidOperationException("Duplicate native landing checkpoint.");
            foreach (NativeLanding landing in landings)
            {
                if (landing.MapId != mapId || !Players.ContainsKey(landing.PlayerId)) throw new InvalidOperationException("Unknown native landing owner/map.");
                FWExpedition expedition = NativeWire.Unpack<FWExpedition>(landing.Expedition);
                if (expedition.getUID() != landing.ExpeditionUid || expedition.endStarMapId != mapId)
                    throw new InvalidOperationException("Native landing payload identity mismatch.");
                foreach (string hero in landing.Heroes) NativeWire.Unpack<FWHeroBean>(hero);
                foreach (string animal in landing.Animals) NativeWire.Unpack<FWAnimalBean>(animal);
            }
        }
        internal void RestoreNativeLandings(long mapId, List<NativeLanding> landings)
        {
            ValidateNativeLandings(mapId, landings);
            foreach (NativeLanding landing in landings)
            {
                FWGameData owner = Players[landing.PlayerId];
                FWExpedition previous = owner.expeditionSeq.SingleOrDefault(e => e.getUID() == landing.ExpeditionUid);
                if (landing.Complete)
                {
                    if (previous != null) owner.expeditionSeq.Remove(previous);
                }
                else
                {
                    FWExpedition next = NativeWire.Unpack<FWExpedition>(landing.Expedition);
                    if (previous == null) owner.expeditionSeq.Add(next); else NativeWire.Copy(next, previous);
                    foreach (string encoded in landing.Heroes)
                    {
                        FWHeroBean hero = NativeWire.Unpack<FWHeroBean>(encoded);
                        FWHeroBean canonical = PlayerHeroes(landing.PlayerId).SingleOrDefault(h => h.getUID() == hero.getUID());
                        if (canonical == null) canonical = hero; else NativeWire.Copy(hero, canonical);
                        foreach (FWFleet fleet in OwnedFleets(landing.PlayerId)) fleet.removeHeroByUid(hero.getUID());
                        if (!owner.heros.Any(h => h.getUID() == canonical.getUID())) owner.heros.Add(canonical);
                    }
                    foreach (string encoded in landing.Animals)
                    {
                        FWAnimalBean animal = NativeWire.Unpack<FWAnimalBean>(encoded);
                        FWAnimalBean canonical = PlayerAnimals(landing.PlayerId).SingleOrDefault(a => a.getUID() == animal.getUID());
                        if (canonical == null) canonical = animal; else NativeWire.Copy(animal, canonical);
                        foreach (FWFleet fleet in OwnedFleets(landing.PlayerId)) fleet.removeAnimalByUid(animal.getUID());
                        if (!owner.animals.Any(a => a.getUID() == canonical.getUID())) owner.animals.Add(canonical);
                    }
                }
                NativeLandings[landing.ExpeditionUid] = landing;
            }
        }
        internal void PumpNativeLandings()
        {
            if (!IsSimulator || !IsMapReady(CurrentMapId)) return;
            AirSupportManager manager = AirSupportManager.getInstance();
            if (manager == null) throw new InvalidOperationException("Ready ground map has no native AirSupportManager.");
            foreach (NativeLanding landing in NativeLandings.Values.Where(l => l.MapId == CurrentMapId && !l.Complete).ToArray())
            {
                FWExpedition expedition = Players[landing.PlayerId].expeditionSeq.Single(e => e.getUID() == landing.ExpeditionUid);
                if (!string.IsNullOrEmpty(expedition.landingFailure)) throw new InvalidOperationException(expedition.landingFailure);
                if (WorldTimeMM < expedition.endTime) continue;
                if (manager.airSupports.Any(s => s != null && s.expedition == expedition)) continue;
                // landing is transient in the native archive. Resume from the authoritative party,
                // never from a destroyed animation callback or by granting the cargo a second time.
                if (expedition.isLanding()) expedition.cancelLanding();
                string[,] tiles = Maps[CurrentMapId].starSpread.getTiles();
                using (EnterPlayer(landing.PlayerId)) manager.addLanding(expedition, tiles.GetLength(1) / 2, tiles.GetLength(0) / 2);
                if (!string.IsNullOrEmpty(expedition.landingFailure)) throw new InvalidOperationException(expedition.landingFailure);
            }
        }
        internal void UpdateNativeLandingClock()
        {
            AirSupportManager manager = AirSupportManager.getInstance();
            if (manager == null) return;
            float speed = IsSimulator ? (float)Context.SimulationSpeed() : 0;
            foreach (AirSupport support in manager.airSupports)
            {
                if (support == null || support.expedition == null) continue;
                DG.Tweening.Tween tween = NativeWire.Get(support, "tweener") as DG.Tweening.Tween;
                DG.Tweening.Tween sequence = NativeWire.Get(support, "sequence") as DG.Tweening.Tween;
                if (tween != null) tween.timeScale = speed;
                if (sequence != null) sequence.timeScale = speed;
            }
        }
        internal IDisposable EnterNativeLanding(ExpeditionEnter response)
        {
            FWExpedition expedition = NativeWire.Get(response, "expedition") as FWExpedition;
            if (expedition == null) return null;
            NativeLanding landing;
            if (!NativeLandings.TryGetValue(expedition.getUID(), out landing)) throw new InvalidOperationException("Unregistered multiplayer landing callback.");
            if (!IsSimulator || CurrentMapId != landing.MapId) throw new InvalidOperationException("Landing callback has no map simulation authority.");
            return EnterPlayer(landing.PlayerId);
        }
        internal void CompleteNativeLanding(ExpeditionEnter response, bool success)
        {
            FWExpedition expedition = NativeWire.Get(response, "expedition") as FWExpedition;
            if (expedition == null) return;
            NativeLanding landing = NativeLandings[expedition.getUID()];
            if (!success) throw new InvalidOperationException("Native landing was rejected: " + (expedition.landingFailure ?? "scene/context changed"));
            landing.Complete = true;
        }
        internal bool IsNativeLandingComplete(long expeditionUid, long mapId, IEnumerable<long> heroIds)
        {
            NativeLanding landing;
            return NativeLandings.TryGetValue(expeditionUid, out landing) && landing.MapId == mapId && landing.Complete
                && heroIds.All(uid => Players[landing.PlayerId].heros.Any(h => h.getUID() == uid && h.isInMap(mapId)));
        }
    }
}
