using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // Correlate user-visible battle transitions without serializing configurations,
    // checkpoints, credentials or network payloads. Diagnostics never affect gameplay.
    internal static class PlayerFleetBattleTrace
    {
        private const int MaximumStates = 512;
        private static readonly Dictionary<string, string> states = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Queue<string> order = new Queue<string>();

        internal static void Emit(string phase, PlayerFleetBattleRecord battle, string detail = null, bool warning = false)
        {
            try
            {
                string battleId = Token(battle == null ? null : battle.BattleId, 64);
                long epoch = battle == null ? -1 : battle.AuthorityEpoch;
                string state = "stage=" + (battle == null ? "missing" : battle.Stage.ToString())
                    + " attacker=" + Token(battle == null ? null : battle.AttackerPlayerId, 64)
                    + "/" + (battle == null ? -1 : battle.AttackerFleetId)
                    + " defender=" + Token(battle == null ? null : battle.DefenderPlayerId, 64)
                    + "/" + (battle == null ? -1 : battle.DefenderFleetId)
                    + " attackerConfigured=" + (battle != null && battle.AttackerConfiguration != null)
                    + " defenderConfigured=" + (battle != null && battle.DefenderConfiguration != null)
                    + " preparationAuthorized=" + (battle != null && battle.PreparationAuthorized)
                    + " sceneAuthorized=" + (battle != null && battle.SceneAuthorized)
                    + " interrupted=" + (battle != null && battle.Interrupted)
                    + " warning=" + warning + " detail=" + Token(detail, 512);
                string key = battleId + "/" + epoch + "/" + Token(phase, 80);
                string previous;
                if (states.TryGetValue(key, out previous) && previous == state) return;
                if (!states.ContainsKey(key))
                {
                    if (states.Count >= MaximumStates) states.Remove(order.Dequeue());
                    order.Enqueue(key);
                }
                states[key] = state;
                string message = "[FreeWorldMultiplayer][fleet-battle] phase=" + Token(phase, 80)
                    + " battleId=" + battleId + " epoch=" + epoch + " " + state;
                if (warning) Logger.LogWarningEngine(message);
                else Logger.LogEngine(message);
            }
            catch { /* Preserve the original battle result if diagnostic formatting or logging fails. */ }
        }

        private static string Token(string value, int maximumLength)
        {
            if (String.IsNullOrEmpty(value)) return "none";
            value = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            return value.Length <= maximumLength ? value : value.Substring(0, maximumLength);
        }
    }
}
