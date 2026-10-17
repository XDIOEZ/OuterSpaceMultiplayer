using System;

namespace FreeWorldMultiplayer
{
    // Pure numeric portion of the existing battle effects wire validation.
    // The host captures and the replica validates every projectile, VFX and
    // beam on each snapshot. Explicit scalar arguments avoid constructing a
    // temporary float[] and invoking Enumerable.Any for every object.
    internal static class BattleEffectFiniteFields
    {
        private static bool Finite(float value)
        { return !float.IsNaN(value) && !float.IsInfinity(value); }

        internal static bool ValidPose(float x, float y, float z, float angle,
            float scaleX, float scaleY, float scaleZ)
        {
            return Finite(x) && Finite(y) && Finite(z) && Finite(angle)
                && Finite(scaleX) && Finite(scaleY) && Finite(scaleZ)
                && Math.Abs(scaleX) <= 10000 && Math.Abs(scaleY) <= 10000 && Math.Abs(scaleZ) <= 10000;
        }

        internal static bool ValidBeam(float x, float y, float z,
            float endX, float endY, float endZ, float width, float endWidth)
        {
            return Finite(x) && Finite(y) && Finite(z)
                && Finite(endX) && Finite(endY) && Finite(endZ)
                && Finite(width) && Finite(endWidth)
                && width >= 0 && endWidth >= 0;
        }
    }
}
