using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // UID uniqueness is checked on every host capture and received battle
    // frame, even when no projectile/VFX exists. A frame-local reusable
    // HashSet avoids allocating a fresh set and buckets per checkpoint.
    internal sealed class BattleEffectIdValidationBuffer
    {
        private readonly HashSet<long> reusable = new HashSet<long>();
        private bool inUse;
        private bool resetPending;

        internal HashSet<long> Rent()
        {
            if (inUse) return new HashSet<long>(); // Nested native callback.
            inUse = true;
            return reusable;
        }

        internal void Release(HashSet<long> ids)
        {
            if (ids == null) throw new ArgumentNullException(nameof(ids));
            ids.Clear(); // Even an unsuccessful validation must not poison retry.
            if (!ReferenceEquals(ids, reusable)) return;
            inUse = false;
            if (resetPending)
            {
                reusable.TrimExcess();
                resetPending = false;
            }
        }

        // A battle may end from a nested Unity callback. Do not invalidate
        // the outer validation's still-active duplicate-ID set.
        internal void Reset()
        {
            if (inUse)
            {
                resetPending = true;
                return;
            }
            reusable.Clear();
            reusable.TrimExcess();
            resetPending = false;
        }
    }
}
