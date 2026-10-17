using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // Local to one already-decoded galaxy frame. The host may send many
    // newly-created fleet replicas and poses in the same baseline, but none
    // of these references may survive a subsequent frame or scene reset.
    internal sealed class FleetCreatedPoseIndex<TPose> where TPose : class
    {
        private const int DenseCreatedThreshold = 8;

        private struct CreatedIdentity : IEquatable<CreatedIdentity>
        {
            internal readonly string PlayerId;
            internal readonly long FleetUid;

            internal CreatedIdentity(string playerId, long fleetUid)
            { PlayerId = playerId; FleetUid = fleetUid; }

            public bool Equals(CreatedIdentity other)
            {
                return FleetUid == other.FleetUid
                    && String.Equals(PlayerId, other.PlayerId, StringComparison.Ordinal);
            }
            public override bool Equals(object value)
            { return value is CreatedIdentity && Equals((CreatedIdentity)value); }
            public override int GetHashCode()
            {
                unchecked
                {
                    return ((int)(FleetUid ^ (FleetUid >> 32)) * 397)
                        ^ (PlayerId == null ? 0 : StringComparer.Ordinal.GetHashCode(PlayerId));
                }
            }
        }

        private readonly HashSet<CreatedIdentity> created = new HashSet<CreatedIdentity>();
        private readonly Dictionary<long, TPose> validatedPoses = new Dictionary<long, TPose>();

        internal static bool ShouldIndex(int createdFleetCount)
        { return createdFleetCount >= DenseCreatedThreshold; }

        // Only called after the incoming FleetContent has passed owner,
        // unique UID, kind and complete field validation.
        internal void RegisterCreated(string playerId, long fleetUid)
        { created.Add(new CreatedIdentity(playerId, fleetUid)); }

        internal bool HasCreated(string playerId, long fleetUid)
        { return created.Contains(new CreatedIdentity(playerId, fleetUid)); }

        // Call only AFTER all poses have passed their original authority,
        // sequence, unique UID, galaxy and finite numeric guards. Do not
        // build the pose dictionary eagerly: validation error ordering must
        // remain unchanged and no native writes are allowed before checks.
        internal void RegisterValidatedPose(long fleetUid, TPose pose)
        { validatedPoses.Add(fleetUid, pose); }

        internal bool HasPose(long fleetUid)
        { return validatedPoses.ContainsKey(fleetUid); }

        internal TPose SinglePose(long fleetUid)
        {
            TPose pose;
            if (!validatedPoses.TryGetValue(fleetUid, out pose))
                throw new InvalidOperationException("Sequence contains no matching element");
            return pose;
        }
    }
}
