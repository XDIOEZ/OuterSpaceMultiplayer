using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // Room communications use four static categories. Keep the authenticated
    // player ID and category as an ordinal, two-part key so every battle
    // command/checkpoint can check its token bucket without creating a new
    // "player:category" string. Do not key by nickname or connection peer.
    internal struct CommunicationRateKey : IEquatable<CommunicationRateKey>
    {
        internal readonly string PlayerId;
        internal readonly string Category;

        internal CommunicationRateKey(string playerId, string category)
        {
            // String concatenation used to treat null as the empty string.
            PlayerId = playerId ?? String.Empty;
            Category = category ?? String.Empty;
        }

        public bool Equals(CommunicationRateKey other)
        {
            return String.Equals(PlayerId, other.PlayerId, StringComparison.Ordinal)
                && String.Equals(Category, other.Category, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        { return obj is CommunicationRateKey && Equals((CommunicationRateKey)obj); }

        public override int GetHashCode()
        {
            unchecked
            {
                return (StringComparer.Ordinal.GetHashCode(PlayerId ?? String.Empty) * 397)
                    ^ StringComparer.Ordinal.GetHashCode(Category ?? String.Empty);
            }
        }
    }

    // Instance-owned by MultiplayerSession: no player or rate state survives
    // another room. Rates, clock arithmetic and grant/rejection order match
    // the previous ConsumeCommunicationRate implementation.
    internal sealed class CommunicationRateLimiter
    {
        private sealed class Rate { internal double Tokens = 4, Last; }
        private readonly Dictionary<CommunicationRateKey, Rate> rates =
            new Dictionary<CommunicationRateKey, Rate>();

        internal bool Consume(string playerId, string category, double clock)
        {
            var key = new CommunicationRateKey(playerId, category);
            double capacity = category == "battle" ? 120 : category == "battleFrame" ? 20
                : category == "visit" ? 2 : 4;
            double refill = category == "battle" ? 60 : category == "battleFrame" ? 20
                : category == "visit" ? 0.1 : 1;
            Rate rate;
            if (!rates.TryGetValue(key, out rate))
                rates[key] = rate = new Rate { Last = clock, Tokens = capacity };
            rate.Tokens = Math.Min(capacity, rate.Tokens + (clock - rate.Last) * refill);
            rate.Last = clock;
            if (rate.Tokens < 1) return false;
            rate.Tokens -= 1;
            return true;
        }

        internal int ForgetPlayer(string playerId)
        {
            if (playerId == null) return 0;
            CommunicationRateKey first = default(CommunicationRateKey);
            bool hasFirst = false;
            List<CommunicationRateKey> more = null;
            // Iterate the whole dictionary before deleting, as the original
            // Keys.Where(...).ToList() did. No temporary collection is needed
            // for the common case of no matching player or a single bucket.
            foreach (CommunicationRateKey key in rates.Keys)
            {
                if (!String.Equals(key.PlayerId, playerId, StringComparison.Ordinal))
                    continue;
                if (!hasFirst) { first = key; hasFirst = true; }
                else
                {
                    if (more == null) more = new List<CommunicationRateKey>();
                    more.Add(key);
                }
            }
            if (!hasFirst) return 0;
            rates.Remove(first);
            if (more != null)
                foreach (CommunicationRateKey key in more) rates.Remove(key);
            return 1 + (more == null ? 0 : more.Count);
        }
    }
}
