using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // The two received-input paths have independent sequence windows.
    // A command must never share its host-ordering slot with the simulator's
    // separately acknowledged battle.execute delivery.
    internal struct BattleInputSequenceKey : IEquatable<BattleInputSequenceKey>
    {
        internal readonly string BattleId;
        internal readonly string PlayerId;
        internal readonly bool Execute;

        internal BattleInputSequenceKey(string battleId, string playerId, bool execute)
        {
            // String concatenation in the old path treated null as empty.
            // Keep that normalization before the caller's authority guards.
            BattleId = battleId ?? String.Empty;
            PlayerId = playerId ?? String.Empty;
            Execute = execute;
        }

        public bool Equals(BattleInputSequenceKey other)
        {
            return Execute == other.Execute &&
                String.Equals(BattleId, other.BattleId, StringComparison.Ordinal) &&
                String.Equals(PlayerId, other.PlayerId, StringComparison.Ordinal);
        }

        public override bool Equals(object value)
        { return value is BattleInputSequenceKey && Equals((BattleInputSequenceKey)value); }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = StringComparer.Ordinal.GetHashCode(BattleId ?? String.Empty);
                hash = hash * 397 ^ StringComparer.Ordinal.GetHashCode(PlayerId ?? String.Empty);
                return hash * 397 ^ (Execute ? 1 : 0);
            }
        }

        // Diagnostics only. Never called by the steady battle-input loop.
        public override string ToString()
        { return (Execute ? "execute:" : "") + BattleId + ":" + PlayerId; }
    }

    // Scoped to one MultiplayerSession. The map is not serialized and never
    // confers asset ownership: all host/player/epoch validation still occurs
    // before either sequence probe in PlayerFleetBattles.
    internal sealed class BattleInputSequenceIndex
    {
        private readonly Dictionary<BattleInputSequenceKey, long> entries =
            new Dictionary<BattleInputSequenceKey, long>();

        internal bool TryGetValue(BattleInputSequenceKey key, out long sequence)
        { return entries.TryGetValue(key, out sequence); }

        internal long this[BattleInputSequenceKey key]
        {
            get { return entries[key]; }
            set { entries[key] = value; }
        }

        internal int Count { get { return entries.Count; } }

        internal int RemoveBattle(string battleId)
        {
            battleId = battleId ?? String.Empty;
            bool found = false;
            BattleInputSequenceKey first = default(BattleInputSequenceKey);
            List<BattleInputSequenceKey> more = null;
            // The previous Keys.Where(...).ToList() completed enumeration
            // before removal. Keep that snapshot ordering and avoid a new
            // list when zero or one sequence window belongs to this battle.
            foreach (BattleInputSequenceKey key in entries.Keys)
            {
                if (!String.Equals(key.BattleId, battleId, StringComparison.Ordinal))
                    continue;
                if (!found) { first = key; found = true; }
                else
                {
                    if (more == null) more = new List<BattleInputSequenceKey>();
                    more.Add(key);
                }
            }
            if (!found) return 0;
            entries.Remove(first);
            if (more != null)
                foreach (BattleInputSequenceKey key in more) entries.Remove(key);
            return 1 + (more == null ? 0 : more.Count);
        }
    }
}
