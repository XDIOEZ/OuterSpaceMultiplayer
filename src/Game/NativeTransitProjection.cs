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
            // Most galaxy frames contain no expedition. Empty transit manifests still
            // need to be sent so clients can clear stale expeditions, but require no scan.
            if (data.expeditionSeq.Count == 0) return frame;
            var heroes = new HashSet<long>(); var animals = new HashSet<long>();
            foreach (FWExpedition expedition in data.expeditionSeq)
            {
                frame.Expeditions.Add(NativeWire.Pack(expedition));
                foreach (long uid in expedition.roleUidList) heroes.Add(uid);
                foreach (long uid in expedition.animalUidList) animals.Add(uid);
            }
            // Cargo-only expeditions have no character manifests. Avoid walking the
            // player's full resident lists when there is nothing to serialize.
            if (heroes.Count != 0)
                foreach (FWHeroBean hero in data.heros)
                    if (heroes.Contains(hero.getUID()) && hero.starMapId < 0 && hero.fleetUid < 0)
                        frame.Heroes.Add(NativeWire.Pack(hero));
            if (animals.Count != 0)
                foreach (FWAnimalBean animal in data.animals)
                    if (animals.Contains(animal.getUID()) && animal.starMapId < 0 && animal.fleetUid < 0)
                        frame.Animals.Add(NativeWire.Pack(animal));
            return frame;
        }
        private void ApplyTransit(TransitState state)
        {
            FWGameData data;
            if (state == null || !Players.TryGetValue(state.PlayerId, out data)) throw new InvalidOperationException("运输同步引用未知玩家。");
            // Keep the authoritative empty manifest's removal semantics without
            // decoding lists or allocating UID sets on every idle galaxy frame.
            if (state.Expeditions.Count == 0 && state.Heroes.Count == 0 && state.Animals.Count == 0)
            {
                if (data.expeditionSeq.Count != 0) data.expeditionSeq.Clear();
                return;
            }
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
            // Build transit-only UID lookups lazily. Previously each fleet occupant
            // scanned the player's entire hero/animal list on every galaxy frame.
            ILookup<long, FWHeroBean> transitHeroes = null;
            ILookup<long, FWAnimalBean> transitAnimals = null;
            HashSet<long> reboundHeroes = null;
            HashSet<long> reboundAnimals = null;
            bool inspectedHeroes = false, inspectedAnimals = false;
            foreach (FWFleet fleet in OwnedFleets(playerId))
            {
                List<FWHeroBean> heroes = fleet.getHeros();
                if (heroes.Count != 0 && data.heros.Count != 0)
                {
                    if (!inspectedHeroes)
                    {
                        inspectedHeroes = true;
                        // Most frames have no unassigned transit passengers. Do not
                        // allocate a lookup or consumed-ID set for those frames.
                        if (data.heros.Any(h => h.starMapId < 0 && h.fleetUid < 0))
                        {
                            transitHeroes = data.heros.Where(h => h.starMapId < 0 && h.fleetUid < 0).ToLookup(h => h.getUID());
                            reboundHeroes = new HashSet<long>();
                        }
                    }
                    if (transitHeroes != null)
                    {
                        for (int i = 0; i < heroes.Count; i++)
                        {
                            long uid = heroes[i].getUID();
                            if (reboundHeroes.Contains(uid)) continue;
                            FWHeroBean transit = transitHeroes[uid].SingleOrDefault();
                            if (transit == null) continue;
                            NativeWire.Copy(heroes[i], transit); heroes[i] = transit; data.heros.Remove(transit);
                            reboundHeroes.Add(uid); // A second fleet must not reuse a consumed arrival.
                        }
                    }
                }
                List<FWAnimalBean> animals = fleet.getAnimals();
                if (animals.Count != 0 && data.animals.Count != 0)
                {
                    if (!inspectedAnimals)
                    {
                        inspectedAnimals = true;
                        if (data.animals.Any(a => a.starMapId < 0 && a.fleetUid < 0))
                        {
                            transitAnimals = data.animals.Where(a => a.starMapId < 0 && a.fleetUid < 0).ToLookup(a => a.getUID());
                            reboundAnimals = new HashSet<long>();
                        }
                    }
                    if (transitAnimals != null)
                    {
                        for (int i = 0; i < animals.Count; i++)
                        {
                            long uid = animals[i].getUID();
                            if (reboundAnimals.Contains(uid)) continue;
                            FWAnimalBean transit = transitAnimals[uid].SingleOrDefault();
                            if (transit == null) continue;
                            NativeWire.Copy(animals[i], transit); animals[i] = transit; data.animals.Remove(transit);
                            reboundAnimals.Add(uid);
                        }
                    }
                }
            }
        }
    }
}
