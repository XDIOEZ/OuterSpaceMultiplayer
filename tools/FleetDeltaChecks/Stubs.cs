namespace FreeWorldMultiplayer
{
    internal static class NativeWire
    {
        // The native CachedValue is immutable; only Encoded is consumed by
        // FleetDeltaFieldSelector. Do not replace the selector under test.
        internal sealed class CachedValue
        {
            internal readonly string Encoded;
            internal CachedValue(string encoded) { Encoded = encoded; }
        }
    }
}
