using System;
using System.Collections.Generic;
using System.IO;

namespace FreeWorldMultiplayer
{
    // Host-only preflight for a remote simulator's map transaction.
    // An entity's OWNER may be the visited base or a visiting player, but a
    // previously unknown UID must come from the submitting SIMULATOR's
    // currently issued UID range. Existing host-owned/transported entities
    // remain valid without retroactively attributing their UID allocation.
    internal static class MapEntityUidValidation
    {
        internal static void RequireRemoteProvenance(
            IDictionary<long, string> submittedOwners,
            IDictionary<long, string> canonicalOwners,
            IDictionary<long, string> acknowledgedMapOwners,
            IDictionary<string, PlayerRecord> players,
            string simulatorPlayerId,
            string sessionId)
        {
            if (submittedOwners == null || canonicalOwners == null || players == null)
                throw new InvalidDataException("Remote map UID ownership registry is incomplete.");
            PlayerRecord simulator;
            if (String.IsNullOrEmpty(simulatorPlayerId) ||
                String.IsNullOrEmpty(sessionId) ||
                !players.TryGetValue(simulatorPlayerId, out simulator) || simulator == null)
                throw new InvalidOperationException("Remote map UID provenance has no authenticated simulator.");

            foreach (var claimed in submittedOwners)
            {
                if (claimed.Key < 0 || String.IsNullOrEmpty(claimed.Value)
                    || !players.ContainsKey(claimed.Value))
                    throw new InvalidOperationException("Remote map contains an invalid UID or entity owner.");

                string knownOwner;
                bool inCanonical = canonicalOwners.TryGetValue(claimed.Key, out knownOwner);
                if (inCanonical && !String.Equals(knownOwner, claimed.Value, StringComparison.Ordinal))
                    throw new InvalidOperationException("Remote map cannot change an existing entity owner.");

                string checkpointOwner = null;
                bool inCheckpoint = acknowledgedMapOwners != null &&
                    acknowledgedMapOwners.TryGetValue(claimed.Key, out checkpointOwner);
                if (inCheckpoint && !String.Equals(checkpointOwner, claimed.Value, StringComparison.Ordinal))
                    throw new InvalidOperationException("Remote map cannot change its acknowledged entity owner.");

                if (inCanonical || inCheckpoint) continue;

                bool issuedToSimulator = false;
                if (simulator.IssuedUidRanges != null)
                {
                    foreach (UidRange range in simulator.IssuedUidRanges)
                    {
                        if (range != null &&
                            String.Equals(range.SessionId, sessionId, StringComparison.Ordinal) &&
                            range.First >= 0 && range.Last >= range.First &&
                            claimed.Key >= range.First && claimed.Key <= range.Last)
                        {
                            issuedToSimulator = true;
                            break;
                        }
                    }
                }
                if (!issuedToSimulator)
                    throw new InvalidOperationException(
                        "Remote map tried to register a new UID outside the simulator's host-issued range.");
            }
        }
    }
}
