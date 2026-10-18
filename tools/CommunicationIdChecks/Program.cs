using System;
using System.Globalization;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private static int checks;
    // Independent exact old PlayerChat.CommunicationId implementation.
    private static bool Previous(string id)
    { return id != null && id.Length == 32 && id.All(c =>
        c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F'); }

    private static void Equal(string value, string description)
    {
        bool old = Previous(value);
        bool modern = CommunicationIdFormat.IsValid(value);
        if (old != modern)
            throw new InvalidOperationException("FAILED: " + description +
                ": previous=" + old + " new=" + modern);
        checks++;
    }
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("FAILED: " + description);
        checks++;
    }
    private static long Allocated(Action action, int frames)
    {
        for (int i = 0; i < 70; i++) action();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < frames; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static int Main()
    {
        try
        {
            Equal(null, "null client, visit, or battle message ID");
            Equal("", "empty ID");
            Equal("0123456789abcdefABCDEF0123456789", "valid lowercase and uppercase ASCII");
            Check(CommunicationIdFormat.IsValid("0123456789abcdefABCDEF0123456789"),
                "production parser accepts a mixed-case 32-digit identifier");
            string[] malformed = {
                "0000000000000000000000000000000",       // 31
                "000000000000000000000000000000000",     // 33
                "0000000000000000000000000000000G",
                "0000000000000000000000000000000g",
                "0000000000000000000000000000000-",
                "0000000000000000000000000000000 ",
                "0000000000000000000000000000000\n",
                "0000000000000000000000000000000\0",
                "0000000000000000000000000000000\ud800",
                "0000000000000000000000000000000\udfff",
                "0000000000000000000000000000000\u0661", // Arabic digit
                "0000000000000000000000000000000\uff19", // fullwidth digit
                "0000000000000000000000000000000\u00e9",
                "0000000000000000000000000000000\u0391"
            };
            foreach (string sample in malformed)
            {
                Equal(sample, "ASCII-only malformed boundary");
                Check(!CommunicationIdFormat.IsValid(sample),
                    "malformed or non-ASCII ID must be rejected");
            }

            string hex = "0123456789abcdefABCDEF";
            // Exhaust each UTF-16 code unit in three strategically different
            // slots. This includes embedded NUL, combining marks, surrogate
            // halves, fullwidth letters and all other Unicode ranges.
            char[] candidate = new string('0', 32).ToCharArray();
            foreach (int slot in new[] { 0, 15, 31 })
            {
                for (int code = 0; code <= char.MaxValue; code++)
                {
                    candidate[slot] = (char)code;
                    string id = new string(candidate);
                    Equal(id, "exhaustive Unicode code-unit validation at index " + slot);
                    Check(CommunicationIdFormat.IsValid(id) == hex.IndexOf((char)code) >= 0,
                        "only ASCII hex is accepted at index " + slot);
                }
                candidate[slot] = '0';
            }

            // Short-circuit and boundary behavior for arbitrary sizes,
            // including oversized attacker-controlled IDs.
            var random = new Random(20261018);
            for (int length = 0; length <= 128; length++)
            {
                for (int sample = 0; sample < 100; sample++)
                {
                    var characters = new char[length];
                    for (int j = 0; j < length; j++)
                    {
                        int mode = random.Next(0, 4);
                        characters[j] = mode == 0
                            ? hex[random.Next(hex.Length)]
                            : (char)random.Next(0, 65536);
                    }
                    Equal(new string(characters), "random mixed-validity UTF-16 ID length " + length);
                }
            }

            // Real GUID N-format identities, generated exactly as in live
            // chat requests and host-authorized fleet battle commands.
            for (int i = 0; i < 4500; i++)
            {
                string guid = Guid.NewGuid().ToString("N");
                Equal(guid, "generated real chat/visit/battle command ID");
                Check(CommunicationIdFormat.IsValid(guid),
                    "valid GUID N-format is never rejected");
                string upper = guid.ToUpperInvariant();
                Equal(upper, "uppercase received 32-digit GUID");
                Check(CommunicationIdFormat.IsValid(upper),
                    "uppercase ASCII GUID remains valid across transports");
            }

            var originalCulture = CultureInfo.CurrentCulture;
            try
            {
                // The wire contract is ordinal and must not depend on the
                // system locale, case folding or Unicode digit categories.
                foreach (string culture in new[] { "tr-TR", "ar-SA", "zh-CN", "en-US" })
                {
                    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                    foreach (string id in new[] {
                        "0123456789abcdefABCDEF0123456789",
                        "0123456789abcdeFABCDEF012345678G",
                        "0123456789abcdeFABCDEF012345678\u0661"
                    })
                        Equal(id, "locale-independent wire ID validation " + culture);
                }
            }
            finally { CultureInfo.CurrentCulture = originalCulture; }

            const int distinct = 128, repetitions = 1800;
            var ids = new string[distinct];
            for (int i = 0; i < distinct; i++)
                ids[i] = i.ToString("x8") + (i + 1).ToString("X8") +
                    (i + 2).ToString("x8") + (i + 3).ToString("X8");
            Action oldFrame = () =>
            {
                foreach (string id in ids)
                    if (!Previous(id)) throw new Exception("Valid pre-optimization ID rejected");
            };
            Action newFrame = () =>
            {
                foreach (string id in ids)
                    if (!CommunicationIdFormat.IsValid(id))
                        throw new Exception("Valid production ID rejected");
            };
            long oldBytes = Allocated(oldFrame, repetitions);
            long newBytes = Allocated(newFrame, repetitions);
            Check(oldBytes > newBytes && newBytes == 0,
                "repeated validated chat/visit/battle IDs create no managed LINQ iterators");
            Console.WriteLine("INFO: " + repetitions +
                " synthetic network checkpoints × " + distinct +
                " valid 32-digit IDs: old LINQ All allocations " +
                oldBytes + " B, linked production IsValid allocations " +
                newBytes + " B after JIT warmup");
            Console.WriteLine("PASS: " + checks +
                " production-linked communication ID compatibility checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
