using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // Holds the wire-weapon/native-controller pairs after the battle visual
    // preflight and until the corresponding visual checkpoint is applied.
    // KeyValuePair is a value type: unlike Tuple.Create it does not allocate
    // once per weapon. The buffer must not outlive a received checkpoint.
    internal sealed class BattleWeaponBindingBuffer<TWire, TController>
        where TWire : class
        where TController : class
    {
        private readonly List<KeyValuePair<TWire, TController>> reusable =
            new List<KeyValuePair<TWire, TController>>();
        private bool inUse, resetPending;

        internal List<KeyValuePair<TWire, TController>> Rent()
        {
            if (inUse) return new List<KeyValuePair<TWire, TController>>();
            inUse = true;
            return reusable;
        }

        internal void Release(List<KeyValuePair<TWire, TController>> bindings)
        {
            if (bindings == null) throw new ArgumentNullException(nameof(bindings));
            bindings.Clear(); // In particular, do not retain native controllers.
            if (!ReferenceEquals(bindings, reusable)) return;
            inUse = false;
            if (resetPending)
            {
                reusable.Capacity = 0;
                resetPending = false;
            }
        }

        // Native battle/room teardown may happen inside a callback while a
        // frame is applying. Delay its backing-array release until after the
        // outermost checkpoint releases the lease.
        internal void Reset()
        {
            if (inUse) { resetPending = true; return; }
            reusable.Clear();
            reusable.Capacity = 0;
            resetPending = false;
        }
    }
}
