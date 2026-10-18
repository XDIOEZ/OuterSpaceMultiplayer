namespace FreeWorldMultiplayer
{
    // A wire message, visit request and fleet battle command share the same
    // 32-character ASCII hexadecimal identity contract. Do not use
    // Char.IsDigit or Unicode casing: only 0-9, a-f and A-F are permitted.
    internal static class CommunicationIdFormat
    {
        internal static bool IsValid(string id)
        {
            if (id == null || id.Length != 32) return false;
            for (int i = 0; i < 32; i++)
            {
                char c = id[i];
                if (!((c >= '0' && c <= '9') ||
                    (c >= 'a' && c <= 'f') ||
                    (c >= 'A' && c <= 'F')))
                    return false;
            }
            return true;
        }
    }
}
