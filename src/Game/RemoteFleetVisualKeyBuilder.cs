using System;
using System.Collections.Generic;
using System.Text;

namespace FreeWorldMultiplayer
{
    // Build the exact legacy "seed:type:uid:resource|..." visual identity
    // without allocating a Select iterator or an intermediate string for
    // every native ship. Only a fleet's displayed native visual uses this
    // key; it is never an authoritative fleet or asset identity.
    internal sealed class RemoteFleetVisualKeyBuilder
    {
        private readonly StringBuilder reusable = new StringBuilder();
        private bool inUse, resetPending;

        internal string Build<TShip>(int moveSeed, string motherType,
            IEnumerable<TShip> ships, Func<TShip, long> uidOf,
            Func<TShip, string> resourceOf, string previousKey)
        {
            // Enumerable.Select throws for a null source before it enumerates
            // any ship. Preserve this malformed-native input contract.
            if (ships == null) throw new ArgumentNullException("source");
            if (uidOf == null) throw new ArgumentNullException(nameof(uidOf));
            if (resourceOf == null) throw new ArgumentNullException(nameof(resourceOf));

            bool useReusable = !inUse;
            StringBuilder builder = useReusable ? reusable : new StringBuilder();
            if (useReusable) inUse = true;
            try
            {
                builder.Length = 0;
                builder.Append(moveSeed).Append(':').Append(motherType).Append(':');
                bool first = true;
                // A typed List<T>.Enumerator retains the old exception when
                // a native callback mutates the list during iteration and
                // avoids boxing an IEnumerable<T> enumerator on Mono.
                List<TShip> list = ships as List<TShip>;
                if (list != null)
                {
                    foreach (TShip ship in list)
                        AppendShip(builder, ship, uidOf, resourceOf, ref first);
                }
                else
                {
                    foreach (TShip ship in ships)
                        AppendShip(builder, ship, uidOf, resourceOf, ref first);
                }

                // A dirty content field does not necessarily change fleet
                // formation. Match the existing key before allocating the
                // final string, so unchanged native visuals stay untouched.
                if (previousKey != null && previousKey.Length == builder.Length)
                {
                    bool equal = true;
                    for (int i = 0; i < builder.Length; i++)
                        if (previousKey[i] != builder[i]) { equal = false; break; }
                    if (equal) return previousKey;
                }
                return builder.ToString();
            }
            finally
            {
                builder.Length = 0;
                if (useReusable)
                {
                    inUse = false;
                    if (resetPending)
                    {
                        reusable.Capacity = 0;
                        resetPending = false;
                    }
                }
            }
        }

        private static void AppendShip<TShip>(StringBuilder builder, TShip ship,
            Func<TShip, long> uidOf, Func<TShip, string> resourceOf, ref bool first)
        {
            if (!first) builder.Append('|');
            first = false;
            // Keep the previous concatenation order and current-culture
            // numeric formatting. Null resource IDs append empty text, as in
            // string concatenation and String.Join.
            builder.Append(uidOf(ship)).Append(':').Append(resourceOf(ship));
        }

        // Called when room/scene cleanup discards the visual proxies. A
        // reentrant cleanup must not truncate an in-progress key comparison.
        internal void Reset()
        {
            if (inUse) { resetPending = true; return; }
            reusable.Length = 0;
            reusable.Capacity = 0;
            resetPending = false;
        }
    }
}
