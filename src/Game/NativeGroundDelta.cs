using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace FreeWorldMultiplayer
{
    public sealed partial class GameBridge
    {
        private BridgeResult ApplyLiveMapCheckpoint(MapSnapshot snapshot, MapAccess access)
        {
            bool wasApplying = Applying;
            Applying = true;
            try
            {
                NativeMapReplicator.PreparedLiveCheckpoint prepared;
                try
                {
                    if (access == null || access.MapId != snapshot.MapId || access.Revision != snapshot.Revision)
                        throw new InvalidOperationException("Live checkpoint access identity/revision mismatch.");
                    prepared = replicator.PrepareLiveCheckpoint(snapshot);
                }
                catch (SeedTerrainMismatchException error)
                { return new BridgeResult { Error = error.ToString(), NativeUnchanged = true, SeedMismatch = true }; }
                catch (AggregateException error) { return BridgeResult.Fail(error.ToString()); }
                catch (Exception error) { return BridgeResult.Reject(error.ToString()); }
                try
                {
                    replicator.ApplyLiveCheckpoint(prepared);
                    Access[snapshot.MapId] = access;
                    BridgeResult result = BridgeResult.Ok();
                    if (prepared.Materialized != null) result.PayloadJson = JsonData.Write(prepared.Materialized);
                    return result;
                }
                catch (Exception error) { return BridgeResult.Fail(error.ToString()); }
            }
            finally { Applying = wasApplying; }
        }
    }

    internal sealed partial class NativeMapReplicator
    {
        internal sealed class PreparedLiveCheckpoint
        {
            internal MapSnapshot Snapshot, Materialized;
            internal Checkpoint Checkpoint;
            internal MapUpdate Update;
            internal PreparedDelta Delta;
        }

        // A full network checkpoint is also used after a host transaction or authority
        // handoff. An already loaded map can reconcile that data through the same entity
        // managers as ordinary deltas; entering the native map again destroys both views.
        internal PreparedLiveCheckpoint PrepareLiveCheckpoint(MapSnapshot snapshot)
        {
            if (snapshot == null || snapshot.IsDeltaBaseline || string.IsNullOrEmpty(snapshot.NativeJson)
                || snapshot.EntityOwners == null || !bridge.IsMapReady(snapshot.MapId))
                throw new InvalidOperationException("Live reconciliation requires a complete checkpoint and a ready current map.");
            Checkpoint checkpoint = NativeWire.Parse<Checkpoint>(snapshot.NativeJson);
            if (checkpoint == null) throw new InvalidOperationException("Missing live native checkpoint.");
            FWStarMapInfo target = checkpoint.Terrain == null ? NativeWire.Unpack<FWStarMapInfo>(checkpoint.Map)
                : NativeWire.UnpackSeedMap(checkpoint.Map, NativeSeedMapCodec.Decode(checkpoint.Terrain));
            if (checkpoint.Terrain != null) NativeSeedMapCodec.ValidateMap(target, checkpoint.Terrain);
            FWStarMapInfo current;
            if (target == null || target.starMapId != snapshot.MapId || target.starSpread == null
                || !bridge.Maps.TryGetValue(snapshot.MapId, out current) || current.starSpread == null)
                throw new InvalidOperationException("Live checkpoint map identity mismatch.");
            string[,] oldTiles = current.starSpread.getTiles(), newTiles = target.starSpread.getTiles();
            if (oldTiles.GetLength(0) != newTiles.GetLength(0) || oldTiles.GetLength(1) != newTiles.GetLength(1))
                throw new InvalidOperationException("Live checkpoint cannot resize a loaded native map.");
            foreach (var pair in snapshot.EntityOwners)
            {
                string existing = bridge.Context.OwnerOfEntity(pair.Key);
                if (pair.Key < 0 || !bridge.Players.ContainsKey(pair.Value ?? "") || existing != null && existing != pair.Value)
                    throw new InvalidOperationException("Live checkpoint changes an entity's immutable player owner.");
            }
            foreach (string encoded in checkpoint.Heroes)
                ValidateCheckpointCharacter(NativeWire.Unpack<FWHeroBean>(encoded), snapshot);
            foreach (string encoded in checkpoint.Animals)
                ValidateCheckpointCharacter(NativeWire.Unpack<FWAnimalBean>(encoded), snapshot);
            foreach (string encoded in checkpoint.DepartedHeroes)
                ValidateCheckpointCharacterOwner(NativeWire.Unpack<FWHeroBean>(encoded), snapshot);
            foreach (string encoded in checkpoint.DepartedAnimals)
                ValidateCheckpointCharacterOwner(NativeWire.Unpack<FWAnimalBean>(encoded), snapshot);
            MapSnapshot materialized = null;
            if (checkpoint.Terrain != null)
            {
                checkpoint.Map = NativeWire.Pack(target); checkpoint.Terrain = null;
                materialized = new MapSnapshot { MapId = snapshot.MapId, Revision = snapshot.Revision,
                    NativeJson = NativeWire.Json(checkpoint), EntityOwners = new Dictionary<long, string>(snapshot.EntityOwners) };
            }
            var delta = new Delta { IdentitySchema = NativeIdentitySchema, Loot = checkpoint.Loot, Landings = checkpoint.Landings };
            Dictionary<string, IList> before = Collections(current, null), after = Collections(target, checkpoint);
            var departedHeroes = checkpoint.DepartedHeroes.Select(NativeWire.Unpack<FWHeroBean>).ToDictionary(h => h.getUID());
            var departedAnimals = checkpoint.DepartedAnimals.Select(NativeWire.Unpack<FWAnimalBean>).ToDictionary(a => a.getUID());
            foreach (var collection in after)
            {
                Dictionary<string, object> oldBeans = before[collection.Key].Cast<object>().ToDictionary(b => EntityKey(collection.Key, b));
                foreach (object bean in collection.Value)
                {
                    string key = EntityKey(collection.Key, bean), encoded = NativeWire.Pack(bean);
                    object old;
                    if (!oldBeans.TryGetValue(key, out old)) delta.Entities.Add(Change(collection.Key, "create", bean, encoded));
                    else
                    {
                        oldBeans.Remove(key);
                        if (NativeWire.Pack(old) != encoded) delta.Entities.Add(Change(collection.Key, "update", bean, encoded));
                    }
                }
                foreach (object old in oldBeans.Values)
                {
                    object departed = null;
                    if (collection.Key == "heroes") { FWHeroBean hero; if (departedHeroes.TryGetValue(NativeWire.Uid(old), out hero)) departed = hero; }
                    if (collection.Key == "ownedAnimals") { FWAnimalBean animal; if (departedAnimals.TryGetValue(NativeWire.Uid(old), out animal)) departed = animal; }
                    delta.Entities.Add(Change(collection.Key, "delete", old, departed == null ? null : NativeWire.Pack(departed)));
                }
            }
            foreach (string scope in FieldScopes)
            foreach (FieldInfo field in MapFields(scope))
            {
                object oldRoot = scope == "map" ? (object)current : current.starSpread;
                object newRoot = scope == "map" ? (object)target : target.starSpread;
                string value = NativeWire.Pack(field.GetValue(newRoot));
                if (value != NativeWire.Pack(field.GetValue(oldRoot)))
                    delta.Fields.Add(new FieldChange { Scope = scope, Field = field.Name, Value = value });
            }
            for (int y = 0; y < newTiles.GetLength(0); y++)
            for (int x = 0; x < newTiles.GetLength(1); x++)
                if (oldTiles[y, x] != newTiles[y, x]) delta.Tiles.Add(new TileChange { X = x, Y = y, Ground = newTiles[y, x] });
            long revision; revisions.TryGetValue(snapshot.MapId, out revision);
            var update = new MapUpdate { MapId = snapshot.MapId, BaseRevision = revision, Revision = snapshot.Revision,
                EntityOwners = snapshot.EntityOwners, NativeDeltaJson = NativeWire.Json(delta) };
            return new PreparedLiveCheckpoint { Snapshot = snapshot, Materialized = materialized,
                Checkpoint = checkpoint, Update = update, Delta = Validate(update) };
        }

        private void ValidateCheckpointCharacter(FWRoleBean character, MapSnapshot snapshot)
        {
            if (character == null || !character.isInMap(snapshot.MapId))
                throw new InvalidOperationException("Live checkpoint character is not in the target map.");
            ValidateCheckpointCharacterOwner(character, snapshot);
        }

        private void ValidateCheckpointCharacterOwner(FWRoleBean character, MapSnapshot snapshot)
        {
            if (character == null) throw new InvalidOperationException("Live checkpoint contains a missing character.");
            string owner;
            if (!snapshot.EntityOwners.TryGetValue(character.getUID(), out owner)) owner = bridge.Context.OwnerOfEntity(character.getUID());
            if (!bridge.Players.ContainsKey(owner ?? "")) throw new InvalidOperationException("Live checkpoint character has no registered player owner.");
        }

        internal void ApplyLiveCheckpoint(PreparedLiveCheckpoint prepared)
        {
            ApplyDelta(prepared.Update, prepared.Delta, true);
            // Full checkpoints contain persistent cells rather than interpolation samples.
            // Keep the existing role objects, but reconcile their actual grid occupancy too.
            ClearGroundPresentation(prepared.Snapshot.MapId, false);
            foreach (PreparedEntity entity in prepared.Delta.Entities)
            {
                if (entity.Change.Operation == "delete" || !(entity.Next is FWRoleBean)) continue;
                BaseRole role = SceneObject(entity.Change.Uid) as BaseRole;
                if (role == null) throw new InvalidOperationException("Checkpoint role is missing its scene projection.");
                if (role.cellX == role.roleBean.cellX && role.cellY == role.roleBean.cellY) continue;
                MapGrid.getGridNode(role.cellX, role.cellY).removeCrops(role);
                MapGrid.getInstance().releasePlance(role.cellX, role.cellY);
                role.setCell(role.roleBean.cellX, role.roleBean.cellY);
                MapGrid.getInstance().doPlace_Role(role, role.cellX, role.cellY);
                MapGrid.getGridNode(role.cellX, role.cellY).setCrops(role);
            }
            knownHeroes[prepared.Snapshot.MapId] = new HashSet<long>(prepared.Checkpoint.Heroes.Concat(prepared.Checkpoint.DepartedHeroes).Select(s => NativeWire.Unpack<FWHeroBean>(s).getUID()));
            knownAnimals[prepared.Snapshot.MapId] = new HashSet<long>(prepared.Checkpoint.Animals.Concat(prepared.Checkpoint.DepartedAnimals).Select(s => NativeWire.Unpack<FWAnimalBean>(s).getUID()));
        }
    }
}
