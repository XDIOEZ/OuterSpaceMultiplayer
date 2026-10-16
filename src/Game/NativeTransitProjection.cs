using System;
using System.Collections.Generic;
using System.Linq;

namespace FreeWorldMultiplayer
{
    public sealed partial class GameBridge
    {
        private sealed class TransitState
        {
            public string PlayerId;
            public List<string> Expeditions = new List<string>();
            public List<string> Heroes = new List<string>();
            public List<string> Animals = new List<string>();
        }
        private TransitState CaptureTransit(string playerId)
        {
            FWGameData data = Players[playerId];
            var frame = new TransitState { PlayerId = playerId };
            var heroes = new HashSet<long>(); var animals = new HashSet<long>();
            foreach (FWExpedition expedition in data.expeditionSeq)
            {
                frame.Expeditions.Add(NativeWire.Pack(expedition));
                foreach (long uid in expedition.roleUidList) heroes.Add(uid);
                foreach (long uid in expedition.animalUidList) animals.Add(uid);
            }
            foreach (FWHeroBean hero in data.heros.Where(h => heroes.Contains(h.getUID()) && h.starMapId < 0 && h.fleetUid < 0)) frame.Heroes.Add(NativeWire.Pack(hero));
            foreach (FWAnimalBean animal in data.animals.Where(a => animals.Contains(a.getUID()) && a.starMapId < 0 && a.fleetUid < 0)) frame.Animals.Add(NativeWire.Pack(animal));
            return frame;
        }
        private void ApplyTransit(TransitState state)
        {
            FWGameData data;
            if (state == null || !Players.TryGetValue(state.PlayerId, out data)) throw new InvalidOperationException("运输同步引用未知玩家。");
            var incoming = state.Expeditions.Select(NativeWire.Unpack<FWExpedition>).ToList();
            var incomingHeroes = state.Heroes.Select(NativeWire.Unpack<FWHeroBean>).ToList();
            var incomingAnimals = state.Animals.Select(NativeWire.Unpack<FWAnimalBean>).ToList();
            var ids = new HashSet<long>(); var heroIds = new HashSet<long>(); var animalIds = new HashSet<long>();
            foreach (FWExpedition expedition in incoming)
            {
                if (!ids.Add(expedition.getUID())) throw new InvalidOperationException("运输同步包含重复原生远征编号。");
                foreach (long uid in expedition.roleUidList) heroIds.Add(uid);
                foreach (long uid in expedition.animalUidList) animalIds.Add(uid);
            }
            foreach (FWHeroBean hero in incomingHeroes)
                if (!heroIds.Contains(hero.getUID()) || Context.OwnerOfEntity(hero.getUID()) != state.PlayerId || hero.starMapId >= 0 || hero.fleetUid >= 0) throw new InvalidOperationException("运输中的雇员归属或位置不匹配。");
            foreach (FWAnimalBean animal in incomingAnimals)
                if (!animalIds.Contains(animal.getUID()) || Context.OwnerOfEntity(animal.getUID()) != state.PlayerId || animal.starMapId >= 0 || animal.fleetUid >= 0) throw new InvalidOperationException("运输中的动物归属或位置不匹配。");
            // Most galaxy frames have no (or very few) transiting entities. Avoid
            // indexing full collections for those frames; index once for larger batches.
            var expeditionsByUid = incoming.Count >= 4 ? data.expeditionSeq.ToDictionary(e => e.getUID()) : null;
            var heroesByUid = incomingHeroes.Count >= 4 ? data.heros.ToDictionary(h => h.getUID()) : null;
            var animalsByUid = incomingAnimals.Count >= 4 ? data.animals.ToDictionary(a => a.getUID()) : null;
            foreach (FWExpedition expedition in incoming)
            {
                FWExpedition current;
                if (expeditionsByUid == null) current = data.expeditionSeq.SingleOrDefault(e => e.getUID() == expedition.getUID());
                else expeditionsByUid.TryGetValue(expedition.getUID(), out current);
                if (current == null) data.expeditionSeq.Add(expedition); else NativeWire.Copy(expedition, current);
            }
            data.expeditionSeq.RemoveAll(e => !ids.Contains(e.getUID()));
            foreach (FWHeroBean hero in incomingHeroes)
            {
                FWHeroBean current;
                if (heroesByUid == null) current = data.heros.SingleOrDefault(h => h.getUID() == hero.getUID());
                else heroesByUid.TryGetValue(hero.getUID(), out current);
                // Ground checkpoints own resident entities; a late transit frame cannot overwrite one.
                if (current != null && (current.starMapId >= 0 || current.fleetUid >= 0)) continue;
                if (current == null) { data.heros.Add(hero); if (heroesByUid != null) heroesByUid.Add(hero.getUID(), hero); }
                else NativeWire.Copy(hero, current);
            }
            foreach (FWAnimalBean animal in incomingAnimals)
            {
                FWAnimalBean current;
                if (animalsByUid == null) current = data.animals.SingleOrDefault(a => a.getUID() == animal.getUID());
                else animalsByUid.TryGetValue(animal.getUID(), out current);
                if (current != null && (current.starMapId >= 0 || current.fleetUid >= 0)) continue;
                if (current == null) { data.animals.Add(animal); if (animalsByUid != null) animalsByUid.Add(animal.getUID(), animal); }
                else NativeWire.Copy(animal, current);
            }
        }
        private void RebindTransitArrivalsToFleet(string playerId)
        {
            FWGameData data = Players[playerId];
            foreach (FWFleet fleet in OwnedFleets(playerId))
            {
                List<FWHeroBean> heroes = fleet.getHeros();
                for (int i = 0; i < heroes.Count; i++)
                {
                    FWHeroBean transit = data.heros.SingleOrDefault(h => h.getUID() == heroes[i].getUID() && h.starMapId < 0 && h.fleetUid < 0);
                    if (transit == null) continue;
                    NativeWire.Copy(heroes[i], transit); heroes[i] = transit; data.heros.Remove(transit);
                }
                List<FWAnimalBean> animals = fleet.getAnimals();
                for (int i = 0; i < animals.Count; i++)
                {
                    FWAnimalBean transit = data.animals.SingleOrDefault(a => a.getUID() == animals[i].getUID() && a.starMapId < 0 && a.fleetUid < 0);
                    if (transit == null) continue;
                    NativeWire.Copy(animals[i], transit); animals[i] = transit; data.animals.Remove(transit);
                }
            }
        }
    }
}
