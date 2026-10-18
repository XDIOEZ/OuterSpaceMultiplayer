using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace FreeWorldMultiplayer
{
    public sealed partial class GameBridge
    {
        private sealed class FleetValue { public string Name, NativeValue; }
        private sealed class FleetContent
        {
            public string PlayerId;
            public long FleetId;
            public int Kind;
            public bool Full, Primary;
            public List<FleetValue> Fields;
            // Keep a non-null default for native Json.NET deserialization,
            // including a malicious payload that omits the Fields property.
            public FleetContent() { Fields = new List<FleetValue>(); }
            internal FleetContent(List<FleetValue> fields) { Fields = fields; }
        }
        private sealed class FleetPose
        {
            public string PlayerId, GalaxyId;
            public long FleetId, WorldTimeMM;
            public float X, Y, Angle;
            public sbyte Status;
        }
        private sealed class FleetSyncFrame
        {
            public int Schema = 2;
            public long Sequence, BaseSequence;
            public bool Full;
            public List<FleetContent> Content = new List<FleetContent>();
            public List<FleetPose> Poses = new List<FleetPose>();
            public List<long> Removed = new List<long>();
        }
        private sealed class FleetSample
        {
            internal string PlayerId;
            internal int Kind;
            internal bool Primary;
            internal FleetPose Pose;
            internal readonly Dictionary<string, NativeWire.CachedValue> Values = new Dictionary<string, NativeWire.CachedValue>();
        }
        private sealed class GalaxyRecipient
        {
            internal long Sequence;
            internal readonly Dictionary<long, Dictionary<string, string>> Values = new Dictionary<long, Dictionary<string, string>>();
            internal readonly Dictionary<long, FleetPose> Poses = new Dictionary<long, FleetPose>();
            internal readonly Dictionary<string, TransitState> Transit = new Dictionary<string, TransitState>();
            internal readonly Dictionary<string, string> SharedValues = new Dictionary<string, string>();
            internal readonly HashSet<long> SharedMaps = new HashSet<long>();
            internal long SharedRevision = -1;
            internal FleetFrame Pending;
            internal GalaxySnapshot Snapshot;
        }
        private static readonly Dictionary<Type, FieldInfo[]> fleetSyncFields = new Dictionary<Type, FieldInfo[]>();
        private static readonly Func<string, string, FleetValue> NewFleetWireField =
            (name, encoded) => new FleetValue { Name = name, NativeValue = encoded };
        private static readonly Func<FieldInfo, string> NativeFleetFieldName = field => field.Name;
        private static readonly Func<FWAirShip, long> NativeFleetShipUid = ship => ship.getUID();
        private static readonly Func<FWGameData, bool> NativePlayerHasPrimaryFleet =
            player => player.galaxyBlock.playerFleet != null;
        private static readonly Func<FWGameData, long> NativePlayerPrimaryFleetUid =
            player => player.galaxyBlock.playerFleet.getUID();
        private static readonly Action<FWAirShip, FWAirShip> NativeFleetShipCopy =
            (source, destination) => NativeWire.Copy(source, destination);
        private readonly Dictionary<long, FleetSample> fleetSamples = new Dictionary<long, FleetSample>();
        private readonly FleetSamplePresenceBuffer fleetSamplePresence = new FleetSamplePresenceBuffer();
        private readonly Dictionary<long, NativeWire.CachedValue> transitValues = new Dictionary<long, NativeWire.CachedValue>();
        private readonly HashSet<long> sampledTransitIds = new HashSet<long>();
        private readonly Dictionary<string, GalaxyRecipient> galaxyRecipients = new Dictionary<string, GalaxyRecipient>();
        private readonly List<TransitState> sampledTransit = new List<TransitState>();
        private int fleetSampleFrame = -1;
        private long fleetSampleSequence, appliedFleetSequence;

        private static FieldInfo[] FleetFields(Type type)
        {
            if (type != typeof(FWFleet) && type != typeof(FWCaravanFleet))
                throw new InvalidOperationException("Unsupported owned fleet type: " + type.FullName);
            FieldInfo[] fields;
            if (!fleetSyncFields.TryGetValue(type, out fields))
            {
                fields = NativeWire.PersistentFields(type).Where(f => f.Name != "pos" && f.Name != "status").ToArray();
                fleetSyncFields.Add(type, fields);
            }
            return fields;
        }
        private static object FleetFieldValue(FWFleet fleet, FieldInfo field)
        {
            // These native public shadows exist only while OnBeforeSerialize runs. Read their
            // real, already-materialized sources without forcing cargo/NPC creation or packing
            // the entire fleet just to discover whether a passenger/item changed.
            if (field.Name == "serializedHeros") return fleet.peekHeros();
            if (field.Name == "serializedThings") return fleet.peekThings();
            return field.GetValue(fleet);
        }
        private static bool SameTransit(TransitState first, TransitState second)
        {
            return first != null && second != null && first.Expeditions.SequenceEqual(second.Expeditions)
                && first.Heroes.SequenceEqual(second.Heroes) && first.Animals.SequenceEqual(second.Animals);
        }
        private void SampleFleetSynchronization()
        {
            if (fleetSampleFrame == Time.frameCount) return;
            fleetSampleSequence++;
            var present = fleetSamplePresence.Rent();
            try
            {
                sampledTransit.Clear(); sampledTransitIds.Clear();
                foreach (var player in Players)
                {
                    foreach (FWFleet fleet in OwnedFleets(player.Key))
                    {
                        long uid = fleet.getUID();
                        if (Context.OwnerOfEntity(uid) != player.Key || !present.Add(uid)) throw new InvalidOperationException("Owned fleet identity is missing or duplicated: " + uid);
                        FleetSample sample;
                        if (!fleetSamples.TryGetValue(uid, out sample)) { sample = new FleetSample(); fleetSamples.Add(uid, sample); }
                        sample.PlayerId = player.Key; sample.Kind = fleet is FWCaravanFleet ? 1 : 0;
                        sample.Primary = ReferenceEquals(fleet, player.Value.galaxyBlock.playerFleet);
                        if (fleet.pos == null) throw new InvalidOperationException("Owned fleet has no physical position: " + uid);
                        Vector3 position; string galaxy; float heading;
                        using (EnterPlayer(player.Key))
                        {
                            foreach (FieldInfo field in FleetFields(fleet.GetType()))
                            {
                                NativeWire.CachedValue previous; sample.Values.TryGetValue(field.Name, out previous);
                                NativeWire.CachedValue value = NativeWire.CaptureValue(FleetFieldValue(fleet, field), previous);
                                sample.Values[field.Name] = value;
                                if (previous == null || previous.Encoded != value.Encoded) MarkRemoteFleetContentChanged(uid);
                            }
                            AirShipFleetInfoBean view = FleetView(player.Key, fleet);
                            position = nativeFleetPosition(view); galaxy = view.getCurGalaxyId(); heading = NativeFleetHeading(view);
                        }
                        sample.Pose = new FleetPose { PlayerId = player.Key, FleetId = uid, GalaxyId = galaxy,
                            X = position.x, Y = position.y, Angle = heading, Status = fleet.status, WorldTimeMM = WorldTimeMM };
                    }
                    sampledTransit.Add(CaptureCachedTransit(player.Key));
                }
                FleetSampleCachePruner.RemoveMissing(fleetSamples, present);
                FleetSampleCachePruner.RemoveMissing(transitValues, sampledTransitIds);
                fleetSampleFrame = Time.frameCount;
            }
            finally { fleetSamplePresence.Return(present); }
        }
        private GalaxySnapshot CaptureFleetFrame(string recipient)
        {
            GalaxyRecipient peer;
            if (!galaxyRecipients.TryGetValue(recipient, out peer)) { peer = new GalaxyRecipient(); galaxyRecipients.Add(recipient, peer); }
            if (peer.Pending != null) return peer.Snapshot; // A failed Send may retry this exact, uncommitted delta.
            SampleFleetSynchronization();
            var sync = new FleetSyncFrame { Sequence = fleetSampleSequence, BaseSequence = peer.Sequence, Full = peer.Sequence == 0 };
            foreach (var pair in fleetSamples)
            {
                FleetSample sample = pair.Value;
                Dictionary<string, string> known;
                bool full = !peer.Values.TryGetValue(pair.Key, out known);
                List<FleetValue> changed = FleetDeltaFieldSelector.Select(sample.Values, known, full, NewFleetWireField);
                if (changed != null)
                {
                    // Most frames only change poses. Do not construct an empty
                    // content DTO/list for each stable fleet and each recipient.
                    sync.Content.Add(new FleetContent(changed) { PlayerId = sample.PlayerId, FleetId = pair.Key,
                        Kind = sample.Kind, Primary = sample.Primary, Full = full });
                }
                FleetPose previousPose;
                if (!peer.Poses.TryGetValue(pair.Key, out previousPose) || !SameFleetPose(previousPose, sample.Pose)) sync.Poses.Add(sample.Pose);
            }
            foreach (long uid in peer.Values.Keys) if (!fleetSamples.ContainsKey(uid)) sync.Removed.Add(uid);
            var frame = new FleetFrame { Sync = sync, WorldTimeMM = WorldTimeMM, Online = Online.ToList(),
                Completed = completedFleetCommands.Values.ToList(), Shared = CaptureSharedWorld(recipient) };
            foreach (TransitState state in sampledTransit)
            {
                TransitState previous;
                if (!peer.Transit.TryGetValue(state.PlayerId, out previous) || !SameTransit(state, previous)) frame.Transit.Add(state);
            }
            peer.Snapshot = new GalaxySnapshot { NativeJson = NativeWire.Json(frame), Revision = WorldTimeMM, IsFullBaseline = sync.Full };
            peer.Pending = frame;
            return peer.Snapshot;
        }
        public void ConfirmGalaxySent(string recipient)
        {
            GalaxyRecipient peer;
            if (!galaxyRecipients.TryGetValue(recipient, out peer) || peer.Pending == null) throw new InvalidOperationException("No pending galaxy delta for recipient.");
            foreach (FleetContent content in peer.Pending.Sync.Content)
            {
                Dictionary<string, string> fields;
                if (!peer.Values.TryGetValue(content.FleetId, out fields)) { fields = new Dictionary<string, string>(); peer.Values.Add(content.FleetId, fields); }
                foreach (FleetValue value in content.Fields) fields[value.Name] = value.NativeValue;
            }
            foreach (FleetPose pose in peer.Pending.Sync.Poses) peer.Poses[pose.FleetId] = pose;
            foreach (long uid in peer.Pending.Sync.Removed) { peer.Values.Remove(uid); peer.Poses.Remove(uid); }
            foreach (TransitState transit in peer.Pending.Transit) peer.Transit[transit.PlayerId] = transit;
            foreach (SharedValue value in peer.Pending.Shared.Values) peer.SharedValues[SharedValueKey(value.Scope, value.Id, value.Field)] = value.NativeValue;
            foreach (MapDirectoryItem map in peer.Pending.Shared.Maps) peer.SharedMaps.Add(map.MapId);
            peer.SharedRevision = peer.Pending.Shared.Revision;
            peer.Sequence = peer.Pending.Sync.Sequence; peer.Pending = null; peer.Snapshot = null;
        }
        public void ResetGalaxyRecipient(string recipient)
        { galaxyRecipients.Remove(recipient); fleetSampleFrame = -1; nextSharedCapture = 0; }

        private static bool SameFleetPose(FleetPose first, FleetPose second)
        { return first.PlayerId == second.PlayerId && first.GalaxyId == second.GalaxyId && first.X == second.X && first.Y == second.Y && first.Angle == second.Angle && first.Status == second.Status; }
        private bool ValidateFleetFrameHeader(FleetSyncFrame sync)
        {
            if (sync == null || sync.Schema != 2 || sync.Sequence <= 0) throw new InvalidOperationException("Unsupported galaxy fleet delta schema.");
            if (sync.Sequence <= appliedFleetSequence)
            {
                if (sync.Full) throw new NativeRejectionException("Galaxy full baseline is stale; request a current full resynchronization.");
                return false;
            }
            if (!sync.Full && sync.BaseSequence != appliedFleetSequence) throw new InvalidOperationException("Galaxy fleet delta is missing its baseline; request a full galaxy resynchronization.");
            return true;
        }

        private void ApplyFleetFrame(FleetSyncFrame sync)
        {
            if (!ValidateFleetFrameHeader(sync)) return;
            // Pose-heavy frames used to rescan the same player's OwnedFleets for
            // every content/pose validation and again for pose application.
            // Index only dense frames; small deltas keep their allocation-free
            // linear lookup and identical validation behavior.
            var frameIndex = FleetFrameIndex<FWFleet>.ShouldIndex(sync.Content.Count, sync.Poses.Count)
                ? new FleetFrameIndex<FWFleet>(OwnedFleets, fleet => fleet.getUID()) : null;
            var staged = new List<Tuple<FleetContent, FWFleet, Dictionary<string, object>, bool>>();
            int createdFleetCount = 0;
            var ids = new HashSet<long>();
            foreach (FleetContent content in sync.Content)
            {
                FWGameData owner;
                if (!Players.TryGetValue(content.PlayerId, out owner) || Context.OwnerOfEntity(content.FleetId) != content.PlayerId || !ids.Add(content.FleetId))
                    throw new InvalidOperationException("Fleet content ownership is missing or duplicated.");
                Type type = content.Kind == 0 ? typeof(FWFleet) : content.Kind == 1 ? typeof(FWCaravanFleet) : null;
                if (type == null) throw new InvalidOperationException("Unsupported owned fleet kind.");
                FWFleet fleet = frameIndex == null ? OwnedFleets(content.PlayerId).SingleOrDefault(f => f.getUID() == content.FleetId)
                    : frameIndex.SingleOrDefault(content.PlayerId, content.FleetId);
                bool created = fleet == null;
                if (created)
                {
                    if (!content.Full || content.Primary) throw new InvalidOperationException("New fleet replica requires complete content and an existing registered player context.");
                    fleet = content.Kind == 0 ? new FWFleet() : new FWCaravanFleet();
                }
                if (fleet.GetType() != type || !created && content.Primary != ReferenceEquals(fleet, owner.galaxyBlock.playerFleet))
                    throw new InvalidOperationException("Fleet content changed its native type or primary identity.");
                var decoded = new Dictionary<string, object>();
                FieldInfo[] fields = FleetFields(type);
                // Keep tiny partial deltas on the original LINQ path. Dense
                // content snapshots reuse one exact ordinal name index for
                // every field in this record and discard it before native writes.
                var fieldLookup = content.Fields != null &&
                    FleetContentFieldLookup<FieldInfo>.ShouldIndex(content.Fields.Count, fields.Length)
                    ? new FleetContentFieldLookup<FieldInfo>(fields, NativeFleetFieldName) : null;
                foreach (FleetValue value in content.Fields)
                {
                    FieldInfo field = fieldLookup == null
                        ? fields.SingleOrDefault(f => f.Name == value.Name)
                        : fieldLookup.Find(value.Name);
                    if (field == null || decoded.ContainsKey(value.Name)) throw new InvalidOperationException("Fleet delta references an unknown or duplicated field.");
                    object incoming = NativeWire.Unpack(value.NativeValue, field.FieldType);
                    if (value.Name == "uid" && (long)incoming != content.FleetId || value.Name == "ownerId" && (int)incoming != owner.userId)
                        throw new InvalidOperationException("Fleet identity cannot be changed by a content delta.");
                    if (value.Name == "ships") ValidateFleetShips(owner, fleet, (List<FWAirShip>)incoming);
                    decoded.Add(value.Name, incoming);
                }
                if (content.Full && decoded.Count != fields.Length) throw new InvalidOperationException("Initial fleet content is incomplete.");
                if (sync.Full && !content.Full) throw new InvalidOperationException("A complete galaxy baseline contains partial fleet content.");
                staged.Add(Tuple.Create(content, fleet, decoded, created));
                if (created) createdFleetCount++;
            }
            FleetCreatedPoseIndex<FleetPose> createdPoseIndex = null;
            if (FleetCreatedPoseIndex<FleetPose>.ShouldIndex(createdFleetCount))
            {
                createdPoseIndex = new FleetCreatedPoseIndex<FleetPose>();
                foreach (var stage in staged)
                    if (stage.Item4) createdPoseIndex.RegisterCreated(stage.Item1.PlayerId, stage.Item1.FleetId);
            }
            var poseIds = new HashSet<long>();
            foreach (FleetPose pose in sync.Poses)
            {
                FWGameData owner;
                if (!Players.TryGetValue(pose.PlayerId, out owner) || Context.OwnerOfEntity(pose.FleetId) != pose.PlayerId || !poseIds.Add(pose.FleetId)
                    || !FWPlayerLocationGuard.IsPhysicalGalaxyId(pose.GalaxyId) || !owner.galaxyBlock.knownGalaxies.Any(g => g.id == pose.GalaxyId)
                    || float.IsNaN(pose.X) || float.IsInfinity(pose.X) || float.IsNaN(pose.Y) || float.IsInfinity(pose.Y)
                    || float.IsNaN(pose.Angle) || float.IsInfinity(pose.Angle) || pose.WorldTimeMM < 0
                    || !(frameIndex == null ? OwnedFleets(pose.PlayerId).Any(f => f.getUID() == pose.FleetId) : frameIndex.Contains(pose.PlayerId, pose.FleetId))
                        && !(createdPoseIndex == null
                            ? staged.Any(s => s.Item4 && s.Item1.FleetId == pose.FleetId && s.Item1.PlayerId == pose.PlayerId)
                            : createdPoseIndex.HasCreated(pose.PlayerId, pose.FleetId)))
                    throw new InvalidOperationException("Invalid authoritative fleet pose.");
            }
            if (createdPoseIndex != null)
                foreach (FleetPose pose in sync.Poses) createdPoseIndex.RegisterValidatedPose(pose.FleetId, pose);
            foreach (var stage in staged)
                if (stage.Item4 && !(createdPoseIndex == null
                    ? sync.Poses.Any(p => p.FleetId == stage.Item1.FleetId)
                    : createdPoseIndex.HasPose(stage.Item1.FleetId)))
                    throw new InvalidOperationException("New fleet replica has no physical galaxy pose.");
            var removed = new HashSet<long>(sync.Removed);
            if (removed.Count != sync.Removed.Count) throw new InvalidOperationException("Galaxy fleet removal identities are duplicated.");
            if (sync.Full)
            {
                foreach (var player in Players)
                    foreach (FWFleet fleet in frameIndex == null ? OwnedFleets(player.Key) : frameIndex.All(player.Key))
                        if (!ids.Contains(fleet.getUID())) removed.Add(fleet.getUID());
                if (staged.Any(stage => !poseIds.Contains(stage.Item1.FleetId))) throw new InvalidOperationException("A complete galaxy baseline is missing a fleet pose.");
            }
            // In a dense removal/full-baseline frame, the old per-UID Any()
            // scanned every player's primary fleet again and again. The first
            // eligible check still scans/short-circuits in original order;
            // only a fully successful scan is cached for subsequent checks.
            // No fleet writes occur until this entire preflight completes.
            var primaryIndex = removed.Count >= 8 && Players.Count >= 3
                ? new FleetPrimaryRemovalIndex<FWGameData>(
                    Players.Values, NativePlayerHasPrimaryFleet, NativePlayerPrimaryFleetUid)
                : null;
            foreach (long uid in removed)
                if (ids.Contains(uid) || poseIds.Contains(uid)
                    || (primaryIndex == null
                        ? Players.Values.Any(p => p.galaxyBlock.playerFleet != null && p.galaxyBlock.playerFleet.getUID() == uid)
                        : primaryIndex.Contains(uid)))
                    throw new InvalidOperationException("A delta cannot remove an updated or primary player fleet.");
            // All known fields and poses decode before the first native write. Pose-only frames
            // never call FWFleet.update or setFleetGalaxy and preserve cached ship wrappers.
            foreach (var stage in staged)
            {
                foreach (var value in stage.Item3)
                {
                    if (value.Key == "serializedHeros") { NativeWire.Set(stage.Item2, "heros", value.Value); stage.Item2.serializedHeros = null; }
                    else if (value.Key == "serializedThings") { NativeWire.Set(stage.Item2, "things", value.Value); stage.Item2.serializedThings = null; }
                    else if (value.Key == "ships") ApplyFleetShips(Players[stage.Item1.PlayerId], stage.Item2, (List<FWAirShip>)value.Value);
                    else NativeWire.Field(stage.Item2.GetType(), value.Key).SetValue(stage.Item2, value.Value);
                }
                if (stage.Item4)
                {
                    FleetPose pose = createdPoseIndex == null
                        ? sync.Poses.Single(p => p.FleetId == stage.Item1.FleetId)
                        : createdPoseIndex.SinglePose(stage.Item1.FleetId);
                    Players[stage.Item1.PlayerId].galaxyBlock.knownGalaxies.Single(g => g.id == pose.GalaxyId).galaxyFleetList.Add(stage.Item2);
                    if (frameIndex != null) frameIndex.RegisterCreated(stage.Item1.PlayerId, stage.Item1.FleetId, stage.Item2);
                    ownedFleetIndexFrame = -1;
                }
                string playerId = stage.Item1.PlayerId;
                if (playerId == Context.LocalPlayerId && stage.Item3.Keys.Any(RequiresFleetViewRebind))
                    using (EnterPlayer(playerId)) FleetView(playerId, stage.Item2).setFleetGalaxy(stage.Item2);
                var uidIndex = NativeWire.Get(Players[playerId], "uidIndex") as System.Collections.IDictionary;
                if (uidIndex != null) uidIndex.Clear();
                MarkRemoteFleetContentChanged(stage.Item1.FleetId);
            }
            foreach (FleetPose pose in sync.Poses)
            {
                FWFleet fleet = frameIndex == null ? OwnedFleets(pose.PlayerId).Single(f => f.getUID() == pose.FleetId)
                    : frameIndex.Single(pose.PlayerId, pose.FleetId);
                if (fleet.pos == null) fleet.pos = new FWGalaxyPos();
                fleet.pos.galaxyId = pose.GalaxyId; fleet.pos.gx = pose.X; fleet.pos.gy = pose.Y; fleet.pos.angle = pose.Angle; fleet.status = pose.Status;
                Players[pose.PlayerId].elapsedMM = pose.WorldTimeMM;
                PushRemoteFleetPose(pose, sync.Sequence, sync.Full);
            }
            foreach (long uid in removed)
            {
                RemoveRemoteFleetProxy(uid); remoteFleetPoses.Remove(uid);
                foreach (FWGalaxy galaxy in Players.Values.SelectMany(p => p.galaxyBlock.knownGalaxies).Distinct())
                    galaxy.galaxyFleetList.RemoveAll(bean => bean is FWFleet && ((FWFleet)bean).getUID() == uid);
                ownedFleetIndexFrame = -1;
            }
        }
        private static bool RequiresFleetViewRebind(string field)
        { return field == "ships" || field == "buffs" || field == "voyage" || field == "serializedHeros" || field == "herosResolved" || field == "serializedThings" || field == "thingsResolved" || field == "motherShipStarMapId"; }
        private static void ValidateFleetShips(FWGameData owner, FWFleet fleet, List<FWAirShip> ships)
        {
            if (ships == null) throw new InvalidOperationException("Fleet ship content is null.");
            var ids = new HashSet<long>();
            // The read-only pre-apply pass previously did two full
            // SingleOrDefault scans for each incoming ship. Index only dense
            // deltas and keep the original source precedence and malformed
            // native-list exception behavior.
            var existing = NativeFleetShipLookup<FWAirShip>.ShouldIndex(ships.Count)
                ? new NativeFleetShipLookup<FWAirShip>(owner.galaxyBlock.airShipList, fleet.ships, NativeFleetShipUid)
                : null;
            foreach (FWAirShip ship in ships)
            {
                if (ship == null || !ids.Add(ship.getUID())) throw new InvalidOperationException("Fleet ship content has a missing or duplicated identity.");
                FWAirShip current = existing == null
                    ? owner.galaxyBlock.airShipList.SingleOrDefault(s => s.getUID() == ship.getUID())
                        ?? fleet.ships.SingleOrDefault(s => s.getUID() == ship.getUID())
                    : existing.Find(ship.getUID());
                if (current != null && current.GetType() != ship.GetType()) throw new InvalidOperationException("Fleet ship content changed its native type.");
            }
        }
        private static void ApplyFleetShips(FWGameData owner, FWFleet fleet, List<FWAirShip> ships)
        {
            // A stored-ship entry may share this exact asset. Preserve its
            // existing reference and native wrappers, including during a
            // dense resync. No lookup escapes this single content apply.
            NativeFleetShipApply.Apply(ships, owner.galaxyBlock.airShipList,
                fleet.ships, NativeFleetShipUid, NativeFleetShipCopy);
        }
        private void ClearFleetSynchronization()
        {
            fleetSamples.Clear(); transitValues.Clear(); sampledTransitIds.Clear(); galaxyRecipients.Clear(); sampledTransit.Clear();
            fleetSamplePresence.Reset();
            fleetSampleFrame = -1; fleetSampleSequence = appliedFleetSequence = 0;
        }
    }
}
