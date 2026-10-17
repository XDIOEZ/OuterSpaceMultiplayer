using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // These are the numeric portions of the native fighter wire contract.
    // Every authoritative fighter checkpoint and each replica validation
    // checks the same fields. Never allocate a float[] or a LINQ iterator
    // for each fighter merely to verify its numbers.
    internal static class BattleFighterFiniteFields
    {
        private static bool Finite(float value)
        { return !float.IsNaN(value) && !float.IsInfinity(value); }

        internal static bool ValidTelemetry(float x, float y, float z, float angle,
            float velocityX, float velocityY, float throttle, float flux, float hardFlux,
            float armorRatio, float shieldAngle, float shieldArc)
        {
            return Finite(x) && Finite(y) && Finite(z) && Finite(angle)
                && Finite(velocityX) && Finite(velocityY) && Finite(throttle)
                && Finite(flux) && Finite(hardFlux) && Finite(armorRatio)
                && Finite(shieldAngle) && Finite(shieldArc);
        }

        // Call only after the existing null and 100000-cell limits. Just like
        // the previous Any predicate, NaN and values outside [0,1] fail;
        // signed zero is valid and all dictionary keys remain unconstrained.
        internal static bool ValidCells(Dictionary<int, float> cells)
        {
            if (cells == null) throw new ArgumentNullException(nameof(cells));
            foreach (float value in cells.Values)
                if (float.IsNaN(value) || value < 0 || value > 1) return false;
            return true;
        }
    }
}
