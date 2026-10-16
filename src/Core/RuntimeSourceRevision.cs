using System;

namespace FreeWorldMultiplayer
{
    // A fingerprint is evidence only when it has exactly the format produced
    // by the MOD build: "<version>+<64 SHA-256 hex characters>".
    internal static class RuntimeSourceRevision
    {
        internal const string Unavailable = "unavailable";

        internal static string FromInformationalVersion(string information)
        {
            if (String.IsNullOrEmpty(information)) return Unavailable;
            int separator = information.LastIndexOf('+');
            if (separator <= 0 || information.Length - separator - 1 != 64) return Unavailable;
            string digest = information.Substring(separator + 1);
            foreach (char character in digest)
                if (!Uri.IsHexDigit(character)) return Unavailable;
            return digest.ToUpperInvariant();
        }
    }
}
