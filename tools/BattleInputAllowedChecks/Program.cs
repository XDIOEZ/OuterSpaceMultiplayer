using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private static int checks;

    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException("FAILED: " + reason);
        checks++;
    }

    // The original production expression from HostBattleInput. Keep this
    // independent of the linked production hash-set helper.
    private static bool PreviousHostCheck(string operation)
    {
        return new[] { "Pilot", "Seat", "Vent", "Shield", "System",
            "Group", "AutoFire", "Autopilot", "Wing", "Target",
            "IssueMoveOrder", "AppendMoveWaypoint", "IssueRetreatOrder",
            "IssueAttackOrder", "IssueDefendOrder", "IssueCaptureOrder",
            "ClearOrder" }.Contains(operation);
    }

    private static readonly string[] Known = {
        "Pilot", "Seat", "Vent", "Shield", "System", "Group",
        "AutoFire", "Autopilot", "Wing", "Target", "IssueMoveOrder",
        "AppendMoveWaypoint", "IssueRetreatOrder", "IssueAttackOrder",
        "IssueDefendOrder", "IssueCaptureOrder", "ClearOrder"
    };

    private static void Equivalent(string candidate, string reason)
    {
        bool old = PreviousHostCheck(candidate);
        bool current = BattleInputAllowedOperations.Contains(candidate);
        Check(old == current, reason + ": original=" + old + " new=" + current);
    }

    private static long Allocated(Action action, int iterations)
    {
        for (int i = 0; i < 120; i++) action();
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }

    private static int Main()
    {
        try
        {
            Check(Known.Length == 17 && Known.Distinct(StringComparer.Ordinal).Count() == 17,
                "the original authority allow-list has exactly 17 distinct operations");
            foreach (string operation in Known)
            {
                Equivalent(operation, "allowed native operation " + operation);
                Check(BattleInputAllowedOperations.Contains(operation),
                    "validated native operation remains accepted exactly");
                // The host originally uses EqualityComparer<string>.Default:
                // Ordinal, not current culture or case-insensitive matching.
                Equivalent(operation.ToLowerInvariant(), "lowercase variant");
                Equivalent(operation.ToUpperInvariant(), "uppercase variant");
                Equivalent(operation + " ", "trailing whitespace");
                Equivalent(" " + operation, "leading whitespace");
                Equivalent(operation + ".extra", "suffix injection");
                Equivalent("prefix." + operation, "prefix injection");
                Equivalent(operation + "\0", "embedded terminal NUL");
                Equivalent(operation.Replace("o", "0"), "confusable digits");
                Equivalent(operation + "\u0301", "combining-mark variant");
            }

            string[] edgeCases = {
                null, "", " ", "\0", "None", "Unknown", "Fire",
                "Battle", "battle", "battleFrame", "visit", "chat",
                "Auto Fire", "Autofire", "AutoFire\r\n", "IssueCaptureOrderX",
                "issueCaptureOrder", "IssueAttackOrder:", "IssueRetreatOrder ",
                "Pilot/Seat", "System:Group", "Wing,Target",
                "İssueMoveOrder", "I\u0307ssueMoveOrder", "IssüeMoveOrder",
                "\u2212", "\u00df", "\ud800", "\udfff"
            };
            foreach (string sample in edgeCases)
                Equivalent(sample, "security and Unicode edge-case command");

            // Verify the string comparison is independent of process culture.
            var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
            foreach (string cultureName in new[] { "tr-TR", "en-US", "zh-CN" })
            {
                try
                {
                    System.Globalization.CultureInfo.CurrentCulture =
                        System.Globalization.CultureInfo.GetCultureInfo(cultureName);
                    foreach (string command in Known) Equivalent(command, "native allowed under " + cultureName);
                    foreach (string edge in edgeCases) Equivalent(edge, "native invalid under " + cultureName);
                }
                finally { System.Globalization.CultureInfo.CurrentCulture = previousCulture; }
            }

            // A host sees externally provided operation strings, including
            // malformed Unicode and newly introduced names. Randomized
            // differential checks ensure the new O(1) lookup rejects exactly
            // the same inputs as the original Enumerable.Contains.
            var random = new Random(20261018);
            const string chars = "AaZzIiöİß中文PilotSeat0:/._- \t\n";
            for (int i = 0; i < 45000; i++)
            {
                string sample;
                switch (random.Next(0, 7))
                {
                    case 0: sample = Known[random.Next(Known.Length)]; break;
                    case 1: sample = Known[random.Next(Known.Length)] + ":" + random.Next(3); break;
                    case 2: sample = null; break;
                    case 3: sample = Known[random.Next(Known.Length)].ToLowerInvariant(); break;
                    default:
                        int length = random.Next(0, 32);
                        var buffer = new char[length];
                        for (int j = 0; j < length; j++) buffer[j] = chars[random.Next(chars.Length)];
                        sample = new string(buffer);
                        break;
                }
                Equivalent(sample, "random peer command " + i);
            }

            // Ensure repeated lookups never widen, replace or mutate the
            // fixed protocol-5 allow-list (including for null).
            for (int i = 0; i < 25000; i++)
            {
                string allowed = Known[i % Known.Length];
                Check(BattleInputAllowedOperations.Contains(allowed)
                    && !BattleInputAllowedOperations.Contains("NOT-" + allowed),
                    "repeated host commands preserve exact, fixed authority allow-list");
            }

            // Only the operation-membership guard is measured. This is NOT
            // command transport, native execution, limiter behavior or FPS.
            const int attempts = 180000;
            string accepted = "IssueMoveOrder", rejected = "IssueMoveOrderX";
            int oldAccepted = 0, currentAccepted = 0;
            Action previousFrame = () =>
            {
                if (PreviousHostCheck(accepted)) oldAccepted++;
                if (PreviousHostCheck(rejected)) oldAccepted++;
            };
            Action currentFrame = () =>
            {
                if (BattleInputAllowedOperations.Contains(accepted)) currentAccepted++;
                if (BattleInputAllowedOperations.Contains(rejected)) currentAccepted++;
            };
            long before = Allocated(previousFrame, attempts);
            long after = Allocated(currentFrame, attempts);
            Check(before > after && before > 20000000 && after == 0,
                "linked immutable host operation set avoids per-input array allocations");
            Check(oldAccepted == currentAccepted,
                "optimized host membership accepts the same commands after warmup");
            Console.WriteLine("INFO: " + attempts + " two-command host checks: " +
                "original array/Contains " + before + " B, linked static Ordinal HashSet " +
                after + " B managed allocations after warmup");
            Console.WriteLine("PASS: " + checks +
                " production linked battle input operation allow-list checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
