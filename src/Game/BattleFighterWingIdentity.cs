using System;

namespace FreeWorldMultiplayer
{
    // A native carrier owns wings by its complete long UID and wing index.
    // The old carrier + "/" + wing string was rebuilt for each fighter during
    // validation and again when applying each replica checkpoint.
    internal struct BattleFighterWingIdentity : IEquatable<BattleFighterWingIdentity>
    {
        internal readonly long Carrier;
        internal readonly int Wing;

        internal BattleFighterWingIdentity(long carrier, int wing)
        {
            Carrier = carrier;
            Wing = wing;
        }

        public bool Equals(BattleFighterWingIdentity other)
        { return Carrier == other.Carrier && Wing == other.Wing; }

        public override bool Equals(object obj)
        { return obj is BattleFighterWingIdentity && Equals((BattleFighterWingIdentity)obj); }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((int)(Carrier ^ (Carrier >> 32)) * 397) ^ Wing;
            }
        }

        // Preserve diagnostics and the small-wing legacy slot-membership key.
        // Called only for legacy slot checks/errors, not for dense wing lookups.
        public override string ToString()
        { return Carrier + "/" + Wing; }
    }
}
