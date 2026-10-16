using System;
using FreeWorldMultiplayer;

internal static class RuntimeRevisionChecks
{
    internal static void Run(Action<bool, string> check)
    {
        string source = new string('a', 64);
        check(RuntimeSourceRevision.FromInformationalVersion("V1.19+" + source) == source.ToUpperInvariant(),
            "loaded release source revision is normalized");
        check(RuntimeSourceRevision.FromInformationalVersion("V1.19") == RuntimeSourceRevision.Unavailable,
            "release version alone is not a source fingerprint");
        check(RuntimeSourceRevision.FromInformationalVersion(null) == RuntimeSourceRevision.Unavailable,
            "missing assembly source metadata is unverified");
        check(RuntimeSourceRevision.FromInformationalVersion("V1.19+" + new string('f', 63)) == RuntimeSourceRevision.Unavailable,
            "truncated source digest is rejected");
        check(RuntimeSourceRevision.FromInformationalVersion("V1.19+" + new string('z', 64)) == RuntimeSourceRevision.Unavailable,
            "non-hex source digest is rejected");
        check(RuntimeSourceRevision.FromInformationalVersion("V1.19+" + new string('f', 65)) == RuntimeSourceRevision.Unavailable,
            "overlong source digest is rejected");
    }
}
