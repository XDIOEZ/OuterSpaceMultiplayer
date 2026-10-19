using System;
using System.Collections.Generic;
using System.Linq;

namespace FreeWorldMultiplayer
{
    public sealed partial class GameBridge
    {
        internal sealed class LootReservation
        {
            public string AttackId;
            public long MapId;
            public long SourceUid;
            public long CarryUid;
            public long HeroUid;
            public int Count;
            public FWRoleBean.carryType CarryType;
        }
        internal readonly List<LootReservation> LootReservations = new List<LootReservation>();
        internal bool CommittingRaidTransfer;
        internal bool HandlePickup(BaseRole role, FWRoleBean roleBean, FWThingBean item, int count, FWRoleBean.carryType carryType, callbackInt callback)
        {
            string actor = Context.OwnerOfEntity(roleBean.getUID());
            string owner = ProtectedGroundItemOwner(CurrentMapId, item);
            if (string.IsNullOrEmpty(actor)) return true; // Native NPC inventory remains native.
            if (!IsSimulator) { callback?.Invoke(Callback.FAILED); return false; }
            if (string.IsNullOrEmpty(owner) || actor == owner) return true;
            if (!Context.CanLootGroundOwner(actor, owner, CurrentMapId)) { callback?.Invoke(Callback.FAILED); return false; }
            if (!EnsureGroundRaid(actor, CurrentMapId, roleBean.getUID())) { callback?.Invoke(Callback.FAILED); return false; }
            AttackRecord attack = Context.GetAttack(CurrentMapId);
            if (attack == null || !IsGroundRaidParticipant(attack, actor) || !GroundRaidHeroes(attack, actor).Contains(roleBean.getUID())
                || !Context.CanLootGroundOwner(actor, owner, CurrentMapId) || count < 1)
            { callback?.Invoke(Callback.FAILED); return false; }
            int reserved = LootReservations.Where(r => r.AttackId == attack.AttackId && r.SourceUid == item.getUID()).Sum(r => r.Count);
            if (item.getSum() - reserved < count) { callback?.Invoke(Callback.FAILED); return false; }
            FWThingBean carried = NativeWire.Unpack<FWThingBean>(NativeWire.Pack(item));
            carried.uid = NextUid(); carried.setSum(count); carried.inBuildUID = -1;
            FWHeroBean hero = roleBean as FWHeroBean;
            if (hero == null) { callback?.Invoke(Callback.FAILED); return false; }
            // Keep the defending stack intact. The copy is an explicit loan, excluded from all
            // permanent transfer/consumption paths until the host commits the unique evacuation.
            (carryType == FWRoleBean.carryType.type_Daily ? hero.carry_Daily : hero.carry_Task).Add(carried);
            LootReservations.Add(new LootReservation { AttackId = attack.AttackId, MapId = CurrentMapId, SourceUid = item.getUID(),
                CarryUid = carried.getUID(), HeroUid = roleBean.getUID(), Count = count, CarryType = carryType });
            if (role != null) { item.releaseOccupy(role, count); role.refreshCarryAnim(carried, count, carryType); }
            callback?.Invoke(Callback.SUCCESS); return false;
        }
        internal bool IsReservedLoot(long uid) { return LootReservations.Any(r => r.CarryUid == uid); }
        internal int ReservedSourceCount(long uid) { return LootReservations.Where(r => r.SourceUid == uid).Sum(r => r.Count); }
        internal bool ReleaseLoot(long heroUid, long carryUid, int count)
        {
            LootReservation reservation = LootReservations.SingleOrDefault(r => r.HeroUid == heroUid && r.CarryUid == carryUid);
            if (reservation == null) return false;
            if (count < 1 || count > reservation.Count) throw new InvalidOperationException("Invalid reserved loot release count.");
            FWHeroBean hero = Players.Values.SelectMany(p => p.heros).Single(h => h.getUID() == heroUid);
            List<FWThingBean> bag = reservation.CarryType == FWRoleBean.carryType.type_Daily ? hero.carry_Daily : hero.carry_Task;
            FWThingBean copy = bag.Single(t => t.getUID() == carryUid);
            copy.subSum(count); reservation.Count -= count;
            if (reservation.Count == 0) { bag.Remove(copy); LootReservations.Remove(reservation); }
            return true;
        }
        internal void ReleaseAllLoot(long heroUid)
        {
            foreach (LootReservation reservation in LootReservations.Where(r => r.HeroUid == heroUid).ToArray())
                ReleaseLoot(heroUid, reservation.CarryUid, reservation.Count);
        }
        internal void SetLootReservations(long mapId, List<LootReservation> reservations)
        {
            ValidateLootReservations(mapId, reservations,
                uid => Maps[mapId].starSpread.things.SingleOrDefault(t => t.getUID() == uid),
                uid => FindPersistentBean(uid) as FWHeroBean);
            LootReservations.RemoveAll(r => r.MapId == mapId); LootReservations.AddRange(reservations);
        }
        internal void ValidateLootReservations(long mapId, List<LootReservation> reservations,
            Func<long, FWThingBean> sourceOf, Func<long, FWHeroBean> heroOf)
        {
            if (reservations == null || reservations.Any(r => r.MapId != mapId || r.Count <= 0)
                || reservations.Select(r => r.CarryUid).Distinct().Count() != reservations.Count)
                throw new InvalidOperationException("Malformed raid loot reservation checkpoint.");
            if (reservations.Count == 0) return;
            AttackRecord attack = Context.GetAttack(mapId);
            if (attack == null) throw new InvalidOperationException("Loot reservations have no matching active battle.");
            foreach (LootReservation reservation in reservations)
            {
                FWHeroBean hero = heroOf(reservation.HeroUid);
                if (reservation.AttackId != attack.AttackId || !GroundRaidHeroes(attack, Context.OwnerOfEntity(reservation.HeroUid)).Contains(reservation.HeroUid)
                    || hero == null || hero.isDead() || !hero.isInMap(mapId) || reservation.SourceUid == reservation.CarryUid)
                    throw new InvalidOperationException("Loot reservation party/source identity mismatch.");
                List<FWThingBean> bag;
                if (reservation.CarryType == FWRoleBean.carryType.type_Daily) bag = hero.carry_Daily;
                else if (reservation.CarryType == FWRoleBean.carryType.type_Task) bag = hero.carry_Task;
                else throw new InvalidOperationException("Loot reservation has an unsupported bag type.");
                FWThingBean copy = bag.SingleOrDefault(t => t.getUID() == reservation.CarryUid);
                FWThingBean source = sourceOf(reservation.SourceUid);
                if (copy == null || source == null || copy.getSum() != reservation.Count || copy.id != source.id || copy.type != source.type)
                    throw new InvalidOperationException("Loot reservation does not match a real source and carried loan.");
            }
            foreach (var group in reservations.GroupBy(r => r.SourceUid))
                if (group.Sum(r => r.Count) > sourceOf(group.Key).getSum()) throw new InvalidOperationException("Source stock is reserved more than once.");
        }
    }
}
