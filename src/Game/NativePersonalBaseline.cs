using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    public sealed partial class GameBridge
    {
        // A foreign Bean on a guest is only a projection while this machine has no
        // map simulation authority. Native presentation callbacks may touch that
        // Bean; its last confirmed wire generation must remain independent.
        private sealed class PersonalProjectionBaseline
        {
            internal FWGameData Player;
            internal long Revision;
            internal Dictionary<string, string> Fields;
        }
        private readonly Dictionary<string, PersonalProjectionBaseline> personalProjectionBaselines
            = new Dictionary<string, PersonalProjectionBaseline>();

        private void ResetLoadedPersonalBaselines()
        {
            personalProjectionBaselines.Clear();
            personalBaselines.Clear();
            personalValueCaches.Clear();
            foreach (string playerId in Players.Keys) ResetPersonalBaseline(playerId);
        }

        private void RememberPersonalProjection(string playerId, long revision)
        {
            if (Context.IsHost || playerId == Context.LocalPlayerId) return;
            personalProjectionBaselines[playerId] = new PersonalProjectionBaseline
            {
                Player = Players[playerId], Revision = revision,
                Fields = new Dictionary<string, string>(personalBaselines[playerId])
            };
        }

        private void AdvancePersonalProjection(PlayerUpdate update, PreparedPersonalDelta prepared)
        {
            PersonalProjectionBaseline previous;
            if (!personalProjectionBaselines.TryGetValue(update.PlayerId, out previous)) return;
            if (!ReferenceEquals(previous.Player, prepared.Player) || previous.Revision != update.BaseRevision)
            { personalProjectionBaselines.Remove(update.PlayerId); return; }
            // Never rebuild a complete confirmed shadow from a comparison cache:
            // an unrelated captured field may still be awaiting its own ACK.
            var fields = new Dictionary<string, string>(previous.Fields);
            foreach (PreparedPersonalField field in prepared.Fields)
                fields[field.Change.Name] = personalBaselines[update.PlayerId][field.Change.Name];
            personalProjectionBaselines[update.PlayerId] = new PersonalProjectionBaseline
            { Player = prepared.Player, Revision = update.Revision, Fields = fields };
        }

        private bool MatchesPersonalProjectionBefore(PlayerUpdate update, PersonalField change,
            Type fieldType, out PersonalProjectionBaseline expected)
        {
            expected = null;
            if (Context.IsHost || update.PlayerId == Context.LocalPlayerId
                || CurrentMapId >= 0 && Context.CanSimulateMap(CurrentMapId)) return false;
            PersonalProjectionBaseline baseline;
            string before;
            if (!personalProjectionBaselines.TryGetValue(update.PlayerId, out baseline)
                || !ReferenceEquals(baseline.Player, Players[update.PlayerId])
                || baseline.Revision != update.BaseRevision
                || Context.GetPlayerRevision(update.PlayerId) != update.BaseRevision
                || !baseline.Fields.TryGetValue(change.Name, out before)
                || !MatchesPersonalValue(before, change.Before, fieldType)) return false;
            expected = baseline;
            return true;
        }

        private void RequirePersonalRevision(PlayerUpdate update)
        {
            if (update == null || string.IsNullOrEmpty(update.PlayerId) || !Players.ContainsKey(update.PlayerId))
                throw new InvalidOperationException("Unknown personal delta owner.");
            if (update.BaseRevision < 0 || update.BaseRevision == long.MaxValue
                || update.Revision != update.BaseRevision + 1
                || Context.GetPlayerRevision(update.PlayerId) != update.BaseRevision)
                throw new InvalidOperationException("Personal delta revision does not match its current owner baseline.");
        }

        private void RequirePersonalProjectionUnchanged(PreparedPersonalDelta prepared)
        {
            PersonalProjectionBaseline expected = prepared.ProjectionBaseline;
            if (expected == null) return;
            PersonalProjectionBaseline current;
            if (!personalProjectionBaselines.TryGetValue(prepared.Update.PlayerId, out current)
                || !ReferenceEquals(current, expected)
                || Context.IsHost || prepared.Update.PlayerId == Context.LocalPlayerId
                || CurrentMapId >= 0 && Context.CanSimulateMap(CurrentMapId))
                throw new InvalidOperationException("Personal projection authority changed after validation.");
        }
    }
}
