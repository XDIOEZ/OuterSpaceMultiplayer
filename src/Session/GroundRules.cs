using System;
using System.Collections.Generic;
using System.Linq;

namespace FreeWorldMultiplayer
{
    public sealed partial class MultiplayerSession
    {
        public bool IsPlayerOnline(string playerId)
        {
            PlayerRecord player;
            return World != null && playerId != null && World.Players.TryGetValue(playerId, out player) && player.Online;
        }

        public bool IsFriendlyToward(string actor, string target)
        {
            if (actor == null || target == null || World == null) return false;
            if (actor == target) return true;
            RelationshipRecord pair = FindRelationship(actor, target, false);
            return pair != null && (pair.PlayerA == actor ? pair.FriendlyConsentA : pair.FriendlyConsentB);
        }

        private bool AreMutuallyFriendly(string a, string b)
        {
            return a == b || GetRelation(a, b) == Relation.Friendly && IsFriendlyToward(a, b) && IsFriendlyToward(b, a);
        }

        public bool IsRaidCoolingDown(long mapId)
        {
            MapRecord map;
            return World != null && World.Maps.TryGetValue(mapId, out map) && map.RaidCooldownSeconds > 0;
        }

        // This is the current garrison/coalition check. Native pickup/transport registers
        // its raid at execution, rather than starting a timer while merely finding work.
        private bool HasGroundLootQualification(string actor, string owner, long mapId)
        {
            if (World == null || actor == null || owner == null || !World.Players.ContainsKey(actor)) return false;
            if (actor == owner) return true;
            MapRecord map;
            if (!World.Maps.TryGetValue(mapId, out map)) return false;
            if (World.Players.Values.Any(p => p.MotherShipMapId == mapId)) return false;
            List<string> present = bridge.ActiveGroundPlayerIds(mapId);
            if (present == null) return false;
            // Unknown employee ownership is not evidence that an area is undefended.
            if (present.Any(p => p == null || !World.Players.ContainsKey(p))) return false;
            if (map.OwnerPlayerId != null) return !present.Contains(map.OwnerPlayerId);
            if (present.Contains(owner)) return false;
            string[] active = present.Distinct().ToArray();
            if (active.Length == 0) return true;
            if (!active.Contains(actor)) return false;
            for (int i = 0; i < active.Length; i++)
                for (int j = i + 1; j < active.Length; j++)
                    if (!AreMutuallyFriendly(active[i], active[j])) return false;
            return true;
        }

        public bool CanLootGroundOwner(string actor, string owner, long mapId)
        {
            if (actor == owner && actor != null) return true;
            if (owner == null || !HasGroundLootQualification(actor, owner, mapId)) return false;
            AttackRecord attack = ActiveGroundAttack(mapId);
            if (Paused || closing) return false;
            if (attack == null) return !IsRaidCoolingDown(mapId);
            return attack.Stage == AttackStage.Active && !attack.Blocked
                && (!attack.EvacuationUsed || attack.EvacuatingPlayerId == actor)
                && attack.ElapsedSeconds < RaidDuration(attack);
        }

        private AttackRecord ActiveGroundAttack(long mapId)
        {
            MapRecord map; AttackRecord attack;
            return World != null && World.Maps.TryGetValue(mapId, out map) && map.ActiveAttackId != null
                && World.Attacks.TryGetValue(map.ActiveAttackId, out attack) && IsBattleStage(attack) ? attack : null;
        }

        private static double RaidDuration(AttackRecord attack)
        {
            return attack.DurationSeconds >= 60 && attack.DurationSeconds <= 7200 ? attack.DurationSeconds : 600;
        }

        private static bool IsRaidParticipant(AttackRecord attack, string playerId)
        {
            return attack != null && playerId != null && (attack.AttackerPlayerId == playerId
                || attack.ParticipantPlayerIds != null && attack.ParticipantPlayerIds.Contains(playerId));
        }

        private TransferRecord RestoreGroundRaidCarrier(string actor, long mapId, RaidRequest request)
        {
            // A client-selected vehicle/fleet is not proof of the actual landing pod. Recover
            // its saved, debited transfer before inspecting capacity or opening a new battle.
            var candidates = World.Transfers.Values.Where(t => t.PlayerId == actor && !t.Returning && t.Request != null
                && t.Request.DestinationMapId == mapId && t.NativeExpeditionId > 0
                && (t.Stage == TransferStage.Arrived || t.Stage == TransferStage.Landing)
                && t.Request.HeroIds != null && t.Request.HeroIds.Any(uid => request.AttackerHeroIds.Contains(uid))).ToList();
            TransferRecord carrier = candidates.FirstOrDefault(t => t.NativeExpeditionId == request.ExistingExpeditionId)
                ?? candidates.OrderBy(t => t.TransferId, StringComparer.Ordinal).FirstOrDefault();
            if (carrier == null) throw new InvalidOperationException("未找到本次在场雇员已落地的真实出征运输单，不能用任意载具生成搜刮额度。");
            request.ExistingExpeditionId = carrier.NativeExpeditionId;
            request.SourceMapId = carrier.Request.SourceMapId;
            request.SourceFleetId = carrier.Request.SourceFleetId >= 0 ? carrier.Request.SourceFleetId : World.Players[actor].FleetId;
            request.NativeTransportJson = carrier.Request.NativeManifestJson;
            request.AttackerHeroIds = request.AttackerHeroIds.Where(carrier.Request.HeroIds.Contains).Distinct().ToList();
            return carrier;
        }

        private void RegisterRaidParticipant(AttackRecord attack, string playerId, RaidRequest request)
        {
            if (attack.ParticipantPlayerIds == null) attack.ParticipantPlayerIds = new List<string>();
            if (attack.ParticipantHeroIds == null) attack.ParticipantHeroIds = new Dictionary<string, List<long>>();
            if (attack.ParticipantFleetIds == null) attack.ParticipantFleetIds = new Dictionary<string, long>();
            if (!attack.ParticipantPlayerIds.Contains(attack.AttackerPlayerId)) attack.ParticipantPlayerIds.Add(attack.AttackerPlayerId);
            if (!attack.ParticipantHeroIds.ContainsKey(attack.AttackerPlayerId)) attack.ParticipantHeroIds[attack.AttackerPlayerId] = attack.AttackerHeroIds.ToList();
            if (!attack.ParticipantFleetIds.ContainsKey(attack.AttackerPlayerId)) attack.ParticipantFleetIds[attack.AttackerPlayerId] = attack.SourceFleetId;
            if (!attack.ParticipantPlayerIds.Contains(playerId)) attack.ParticipantPlayerIds.Add(playerId);
            List<long> heroes;
            if (!attack.ParticipantHeroIds.TryGetValue(playerId, out heroes)) attack.ParticipantHeroIds[playerId] = request.AttackerHeroIds.Distinct().ToList();
            else foreach (long uid in request.AttackerHeroIds) if (!heroes.Contains(uid)) heroes.Add(uid);
            if (!attack.ParticipantFleetIds.ContainsKey(playerId)) attack.ParticipantFleetIds[playerId] = request.SourceFleetId;
        }
    }
}
