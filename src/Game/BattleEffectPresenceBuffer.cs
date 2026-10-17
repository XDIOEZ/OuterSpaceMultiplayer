using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // A received battle visual frame needs an ID set only while updating its
    // native replicas and expiring absent projectiles/VFX. Reuse its backing
    // buckets across frames rather than allocating LINQ iterators and a new
    // HashSet for every visual checkpoint.
    internal sealed class BattleEffectPresenceBuffer
    {
        private readonly HashSet<long> reusable = new HashSet<long>();
        private bool leased, resetPending;

        internal HashSet<long> Capture<T>(IEnumerable<T> projectiles,
            IEnumerable<T> vfx, Func<T, long> idOf)
        {
            if (projectiles == null) throw new ArgumentNullException(nameof(projectiles));
            if (vfx == null) throw new ArgumentNullException(nameof(vfx));
            if (idOf == null) throw new ArgumentNullException(nameof(idOf));

            // Native callbacks can reenter effect application. Do not clear
            // the outer frame's UID set or let a nested frame expire it.
            HashSet<long> present = leased ? new HashSet<long>() : reusable;
            if (ReferenceEquals(present, reusable)) leased = true;
            try
            {
                // Exactly the previous Projectiles.Concat(Vfx).Select(Id)
                // order, including duplicate UID coalescing.
                AddIds(present, projectiles, idOf);
                AddIds(present, vfx, idOf);
                return present;
            }
            catch
            {
                Release(present);
                throw;
            }
        }

        private static void AddIds<T>(HashSet<long> present,
            IEnumerable<T> poses, Func<T, long> idOf)
        {
            // BattleEffectsState owns List<T> pose collections. Iterating an
            // interface-typed IEnumerable<T> boxes List<T>.Enumerator,
            // creating two short-lived objects each received frame. The
            // typed List enumerator stays a struct and still throws if a
            // callback mutates that list during enumeration, as before.
            var list = poses as List<T>;
            if (list != null)
            {
                foreach (T pose in list) present.Add(idOf(pose));
                return;
            }
            // Arrays are used by a few local callers and need no iterator
            // allocation either. Other enumerables retain their old behavior.
            var array = poses as T[];
            if (array != null)
            {
                foreach (T pose in array) present.Add(idOf(pose));
                return;
            }
            foreach (T pose in poses) present.Add(idOf(pose));
        }

        internal void Release(HashSet<long> present)
        {
            if (present == null) throw new ArgumentNullException(nameof(present));
            present.Clear();
            if (!ReferenceEquals(present, reusable)) return;
            leased = false;
            if (resetPending)
            {
                reusable.TrimExcess();
                resetPending = false;
            }
        }

        // Called when the native battle/room ends. A reset inside a native
        // callback waits for the live frame to release its UID references.
        internal void Reset()
        {
            if (leased) { resetPending = true; return; }
            reusable.Clear();
            reusable.TrimExcess();
            resetPending = false;
        }
    }
}
