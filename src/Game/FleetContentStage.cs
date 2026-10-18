using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // One decoded fleet-content record staged for ApplyFleetFrame's existing
    // validate-all-before-native-write transaction. Unlike Tuple.Create,
    // this value type does not allocate a separate object per fleet update.
    //
    // The original Item1..Item4 members are retained deliberately so the
    // caller's field order, aliasing, and existing native-write flow remain
    // exactly as before. No decoded dictionary or fleet object is copied.
    internal struct FleetContentStage<TContent, TFleet>
    {
        internal readonly TContent Item1;
        internal readonly TFleet Item2;
        internal readonly Dictionary<string, object> Item3;
        internal readonly bool Item4;

        internal FleetContentStage(TContent content, TFleet fleet,
            Dictionary<string, object> decoded, bool created)
        {
            Item1 = content;
            Item2 = fleet;
            Item3 = decoded;
            Item4 = created;
        }
    }
}
