using System;
using System.Collections.Generic;
using System.IO;
using FreeWorldMultiplayer;

internal static class MapTransactionPlayerChecks
{
    private static bool Rejected(IList<PlayerUpdate> players)
    {
        try { MapTransactionPlayerValidation.RequireUnique(players); return false; }
        catch (InvalidDataException) { return true; }
    }

    internal static void Run(Action<bool, string> check)
    {
        check(!Rejected(new List<PlayerUpdate>()),
            "legal map transaction with no personal changes remains accepted");
        check(!Rejected(new List<PlayerUpdate> { new PlayerUpdate { PlayerId = "A", BaseRevision = 1, Revision = 2 } }),
            "one player's legal personal delta remains accepted");
        check(!Rejected(new List<PlayerUpdate> { new PlayerUpdate { PlayerId = "A" }, new PlayerUpdate { PlayerId = "B" } }),
            "two distinct participants remain legal in the same atomic map update");

        var conflicting = new List<PlayerUpdate> {
            new PlayerUpdate { PlayerId = "A", BaseRevision = 3, Revision = 4, NativeFieldsJson = "credit-before-after" },
            new PlayerUpdate { PlayerId = "A", BaseRevision = 3, Revision = 4, NativeFieldsJson = "different-credit-value" }
        };
        int nativeApplies = 0;
        try { MapTransactionPlayerValidation.RequireUnique(conflicting); nativeApplies++; }
        catch (InvalidDataException) { }
        check(nativeApplies == 0 && Rejected(conflicting),
            "conflicting duplicate player revisions abort before any hypothetical native Apply");
        check(Rejected(new List<PlayerUpdate> { new PlayerUpdate { PlayerId = "A" }, new PlayerUpdate { PlayerId = "A" } }),
            "even identical repeated updates are rejected instead of silently double-applied");
        check(Rejected(new List<PlayerUpdate> { new PlayerUpdate { PlayerId = "A", BaseRevision = 1 },
            new PlayerUpdate { PlayerId = "A", BaseRevision = 2 } }),
            "repeated identity with different revisions is not allowed to bypass atomicity");
        check(Rejected(new List<PlayerUpdate> { new PlayerUpdate { PlayerId = "A" }, null }),
            "null personal delta is rejected before later native writes");
        check(Rejected(new List<PlayerUpdate> { new PlayerUpdate { PlayerId = "" } }) &&
            Rejected(new List<PlayerUpdate> { new PlayerUpdate { PlayerId = " " } }) &&
            Rejected(null), "empty identifiers and absent entire player list fail closed");

        var many = new List<PlayerUpdate>();
        for (int i = 0; i < 10000; i++) many.Add(new PlayerUpdate { PlayerId = "synthetic-" + i });
        check(!Rejected(many), "preflight accepts 10k distinct identities with bounded linear work");
    }
}
