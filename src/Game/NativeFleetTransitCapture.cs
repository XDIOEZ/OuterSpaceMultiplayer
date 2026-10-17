using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    public sealed partial class GameBridge
    {
        private string CaptureTransitValue(FWBaseBean bean)
        {
            long uid = NativeWire.Uid(bean);
            sampledTransitIds.Add(uid);
            NativeWire.CachedValue previous;
            transitValues.TryGetValue(uid, out previous);
            NativeWire.CachedValue value = NativeWire.CaptureValue(bean, previous);
            transitValues[uid] = value;
            return value.Encoded;
        }

        // Called by SampleFleetSynchronization for each player in the shared
        // galaxy snapshot. Cargo-only expeditions have no resident passengers,
        // but the former implementation still scanned every hero and animal.
        // Preserve the complete expedition manifest and native list ordering.
        private TransitState CaptureCachedTransit(string playerId)
        {
            FWGameData data = Players[playerId];
            var state = new TransitState { PlayerId = playerId };
            if (data.expeditionSeq.Count == 0) return state;
            HashSet<long> heroes = null, animals = null;
            foreach (FWExpedition expedition in data.expeditionSeq)
            {
                state.Expeditions.Add(CaptureTransitValue(expedition));
                foreach (long uid in expedition.roleUidList)
                {
                    if (heroes == null) heroes = new HashSet<long>();
                    heroes.Add(uid);
                }
                foreach (long uid in expedition.animalUidList)
                {
                    if (animals == null) animals = new HashSet<long>();
                    animals.Add(uid);
                }
            }
            if (heroes != null)
                foreach (FWHeroBean hero in data.heros)
                    if (heroes.Contains(hero.getUID()) && hero.starMapId < 0 && hero.fleetUid < 0)
                        state.Heroes.Add(CaptureTransitValue(hero));
            if (animals != null)
                foreach (FWAnimalBean animal in data.animals)
                    if (animals.Contains(animal.getUID()) && animal.starMapId < 0 && animal.fleetUid < 0)
                        state.Animals.Add(CaptureTransitValue(animal));
            return state;
        }
    }
}
