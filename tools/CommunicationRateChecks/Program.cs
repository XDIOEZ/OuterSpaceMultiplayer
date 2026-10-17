using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FreeWorldMultiplayer;

internal static class Program
{
    private sealed class Rate
    {
        internal double Tokens = 4;
        internal double Last;
    }

    // This is the original PlayerChat implementation before extracting the
    // production helper. Its categories, token arithmetic, disconnect prefix
    // and per-session dictionary are intentionally independent of the new key.
    private sealed class Legacy
    {
        private readonly Dictionary<string, Rate> rates =
            new Dictionary<string, Rate>();

        internal int Count { get { return rates.Count; } }

        internal bool Consume(string playerId, string category, double clock)
        {
            string key = playerId + ":" + category;
            double capacity = category == "battle" ? 120 :
                category == "battleFrame" ? 20 : category == "visit" ? 2 : 4;
            double refill = category == "battle" ? 60 :
                category == "battleFrame" ? 20 : category == "visit" ? 0.1 : 1;
            Rate rate;
            if (!rates.TryGetValue(key, out rate))
                rates[key] = rate = new Rate { Last = clock, Tokens = capacity };
            rate.Tokens = Math.Min(capacity, rate.Tokens + (clock - rate.Last) * refill);
            rate.Last = clock;
            if (rate.Tokens < 1) return false;
            rate.Tokens -= 1;
            return true;
        }

        internal int Forget(string playerId)
        {
            if (playerId == null) return 0;
            var stale = rates.Keys.Where(k =>
                k.StartsWith(playerId + ":", StringComparison.Ordinal)).ToList();
            foreach (string key in stale) rates.Remove(key);
            return stale.Count;
        }
    }

    private static int checks;
    private static void Check(bool ok, string description)
    {
        if (!ok) throw new InvalidOperationException("FAILED: " + description);
        checks++;
    }
    private static int CountProduction(CommunicationRateLimiter limiter)
    {
        // Testing-only inspection, not part of the game implementation.
        var field = typeof(CommunicationRateLimiter).GetField("rates",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var rates = (System.Collections.IDictionary)field.GetValue(limiter);
        return rates.Count;
    }

    private static void SameConsume(Legacy old, CommunicationRateLimiter current,
        string playerId, string category, double clock, string label)
    {
        bool previous = old.Consume(playerId, category, clock);
        bool updated = current.Consume(playerId, category, clock);
        Check(previous == updated, label + " grant/rejection player=" +
            playerId + " category=" + category + " clock=" + clock);
        Check(old.Count == CountProduction(current),
            label + " bucket count and independent player/category identity");
    }

    private static void SameForget(Legacy old, CommunicationRateLimiter current,
        string playerId, string label)
    {
        int before = old.Forget(playerId);
        int after = current.ForgetPlayer(playerId);
        Check(before == after, label + " exact number of disconnected categories");
        Check(old.Count == CountProduction(current),
            label + " unrelated category/player buckets remain attached");
    }

    private static long Allocated(Action action, int iterations)
    {
        for (int i = 0; i < 100; i++) action();
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }

    private static int Main()
    {
        try
        {
            var a = new CommunicationRateKey("a", "chat");
            var b = new CommunicationRateKey("a", "chat");
            Check(a.Equals(b) && a.GetHashCode() == b.GetHashCode(),
                "identical player and category have equal ordinal hash keys");
            Check(!a.Equals(new CommunicationRateKey("A", "chat")) &&
                !a.Equals(new CommunicationRateKey("a", "CHAT")) &&
                !a.Equals(new CommunicationRateKey("a", "visit")),
                "player ID and message kind remain independently case-sensitive");
            Check(!new CommunicationRateKey("a:b", "chat").Equals(
                    new CommunicationRateKey("a", "b:chat")) &&
                !new CommunicationRateKey("a", "chat").Equals(
                    new CommunicationRateKey("a:", "chat")),
                "two-part identity never mistakes a player prefix for another account");
            Check(new CommunicationRateKey(null, null).Equals(
                    new CommunicationRateKey("", "")),
                "nullable input normalization keeps previous string-concatenation semantics");

            string[] categories = { "chat", "visit", "battle", "battleFrame" };
            int[] maximum = { 4, 2, 120, 20 };
            double[] refill = { 1, 0.1, 60, 20 };
            for (int i = 0; i < categories.Length; i++)
            {
                string category = categories[i];
                var original = new Legacy();
                var optimized = new CommunicationRateLimiter();
                for (int request = 0; request < maximum[i]; request++)
                    SameConsume(original, optimized, "guest", category, 2,
                        category + " initial capacity #" + request);
                Check(!optimized.Consume("guest", category, 2),
                    category + " rejects after its exact token capacity");
                Check(!original.Consume("guest", category, 2),
                    category + " old limiter rejects at the same count");
                Check(!optimized.Consume("guest", category, 2 + 0.49 / refill[i]),
                    category + " refuses a fractional token");
                Check(!original.Consume("guest", category, 2 + 0.49 / refill[i]),
                    category + " original fractional token refusal");
                // Advance from the previous 0.49-token checkpoint.
                SameConsume(original, optimized, "guest", category,
                    2 + 1.1 / refill[i], category + " rate refill after cooldown");
                SameForget(original, optimized, "guest",
                    category + " disconnect releases one independent bucket");
                SameConsume(original, optimized, "guest", category, 1,
                    category + " same user rejoins with new full-capacity bucket");
            }

            var old = new Legacy();
            var modern = new CommunicationRateLimiter();
            SameForget(old, modern, "absent-player", "empty limiter disconnect");
            SameForget(old, modern, null, "null player disconnect does not clear room");
            for (int i = 0; i < categories.Length; i++)
                SameConsume(old, modern, "player-1", categories[i], 5,
                    "first of four category-specific token buckets");
            SameConsume(old, modern, "player-2", "battle", 5,
                "independent second player tokens");
            SameConsume(old, modern, "player-1", "battle", 5,
                "same player battle rate does not consume chat or visit tokens");
            SameForget(old, modern, "player-1", "disconnect all four categories");
            SameConsume(old, modern, "player-2", "battle", 5,
                "another player's battle rate survives the disconnect");
            SameForget(old, modern, "player-1", "repeated disconnect is idempotent");
            SameConsume(old, modern, "player-1", "battle", 5,
                "fresh battle input after reconnect starts with full allowance");
            SameConsume(old, modern, "", "visit", 8,
                "old empty-string player ID is still distinguishable");
            SameConsume(old, modern, null, "visit", 8,
                "null player ID aliases empty string as the old concatenation did");
            SameForget(old, modern, "", "empty normalized player can be released");

            // Validate original behavior for allowed and unknown categories
            // including Unicode, case variants and backslashes. All
            // authoritative call sites use one of the four fixed literals.
            var rng = new Random(20261018);
            string[] players = {
                "1b9d67e2bff64565b418e8c1b90a4e37",
                "8d53a86ce14f4056b220a779e4cb1a0f",
                "host", "HOST", "", null,
                "user:23", "user-23", "玩家α", "guest:7"
            };
            string[] extended = {
                "chat", "visit", "battle", "battleFrame", "BATTLE",
                "", null, "chat-v2", "未知类别", "battle\\frame"
            };
            double time = 100;
            for (int iteration = 0; iteration < 30000; iteration++)
            {
                if (rng.Next(0, 4) != 0)
                {
                    // Rejecting a packet must not advance another category.
                    // Clock can stay fixed for bursts or occasionally move
                    // backwards on a bad tick; match old Math.Min precisely.
                    time += rng.Next(0, 20) == 0 ? -0.17 :
                        rng.Next(0, 5) == 0 ? 0 : rng.NextDouble() / 4;
                    SameConsume(old, modern, players[rng.Next(players.Length)],
                        extended[rng.Next(extended.Length)], time,
                        "random rate-check input " + iteration);
                }
                else
                {
                    SameForget(old, modern, players[rng.Next(players.Length)],
                        "random player disconnect " + iteration);
                }
            }

            // Repeated ACK/control traffic is the real hot path; benchmark
            // warmed token buckets, excluding network encoding, Unity and
            // host packet parsing. Both may reject after the initial burst.
            const int frames = 160000;
            var beforeRate = new Legacy();
            var afterRate = new CommunicationRateLimiter();
            beforeRate.Consume(players[0], "battleFrame", 10);
            afterRate.Consume(players[0], "battleFrame", 10);
            Action legacyFrame = () => beforeRate.Consume(players[0], "battleFrame", 10);
            Action optimizedFrame = () => afterRate.Consume(players[0], "battleFrame", 10);
            long oldBytes = Allocated(legacyFrame, frames);
            long newBytes = Allocated(optimizedFrame, frames);
            Check(oldBytes > newBytes && newBytes == 0 && oldBytes - newBytes > 1000000,
                "no temporary player:category strings on every received battle frame");

            var oldEmpty = new Legacy();
            var newEmpty = new CommunicationRateLimiter();
            oldEmpty.Consume("online", "chat", 1);
            newEmpty.Consume("online", "chat", 1);
            Action oldEmptyDisconnect = () => oldEmpty.Forget("never-online");
            Action newEmptyDisconnect = () => newEmpty.ForgetPlayer("never-online");
            long oldDisconnectBytes = Allocated(oldEmptyDisconnect, 4000);
            long newDisconnectBytes = Allocated(newEmptyDisconnect, 4000);
            Check(oldDisconnectBytes > newDisconnectBytes && newDisconnectBytes == 0,
                "disconnect of an unrelated player avoids LINQ and list allocation");
            Console.WriteLine("INFO: " + frames + " prewarmed battle-frame rate checks: " +
                "legacy concatenated identity " + oldBytes +
                " B, linked production composite limiter " + newBytes + " B");
            Console.WriteLine("INFO: 4000 no-match player disconnects: legacy LINQ " +
                oldDisconnectBytes + " B, new two-part key pruner " +
                newDisconnectBytes + " B (managed allocations after warmup)");
            Console.WriteLine("PASS: " + checks +
                " production-linked communication token bucket and disconnect checks");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
}
