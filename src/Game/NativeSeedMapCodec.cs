using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using UnityEngine;

namespace FreeWorldMultiplayer
{
    internal sealed class SeedTerrainMismatchException : Exception
    {
        internal readonly long MapId;
        internal readonly string ExpectedHash, ActualHash;
        internal SeedTerrainMismatchException(long mapId, string expected, string actual, string reason, Exception inner = null)
            : base("SEED地形需要完整基准：map=" + mapId + " reason=" + reason + " expected=" + expected + " actual=" + actual, inner)
        { MapId = mapId; ExpectedHash = expected; ActualHash = actual; }
    }

    // This codec reconstructs terrain only. It never calls the full map factory, changes an
    // FWUidCounter, opens a world RNG, creates Beans, or installs/activates a native map.
    internal static class NativeSeedMapCodec
    {
        private const string Generator = "NativeTerrainV2.Optimized";
        private const int MaxCells = 1024 * 1024, MaxCacheCells = 1024 * 1024, MaxCacheEntries = 8;
        internal sealed class Point { public float X, Y; }
        internal sealed class Anchor { public int Kind, Edge; public Point Position, Tangent; }
        internal sealed class Feature
        {
            public string Id, Ground;
            public int Type, Priority;
            public Anchor Start, End;
            public float Width, Falloff, Strength;
            public List<Point> Points = new List<Point>();
        }
        internal sealed class Boundary
        {
            public string Id;
            public int Type, Relaxations, ConnectionWidth;
            public Point Direction;
            public float Center, Radius, Noise, Depth, Shore, Strength;
        }
        internal sealed class Recipe
        {
            public string Generator, Seed, Ground, ProfileHash;
            public int X, Y, Width, Height;
            public List<Feature> Features = new List<Feature>();
            public List<Boundary> Boundaries = new List<Boundary>();
            public List<Point> LandDirections = new List<Point>();
        }
        internal sealed class Override { public int X, Y; public string Ground; }
        internal sealed class SeedTerrainPayload
        {
            public int Schema;
            public long MapId = -1;
            public Recipe Recipe;
            public string BaseHash, CurrentHash;
            public List<Override> Overrides = new List<Override>();
        }
        private sealed class CachedTerrain
        {
            internal string Key, Hash;
            internal string[,] Tiles; // Owned independent immutable baseline; never returned.
            internal long Use;
        }
        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, CachedTerrain> Cache = new Dictionary<string, CachedTerrain>(StringComparer.Ordinal);
        private static int cacheCells;
        private static long cacheUse;
        private static NativeApi api;
        private static bool apiChecked;
        private static string apiError;
        private static Func<string, byte[]> readContent;
        private static Action<string, byte[]> writeContent;

        private sealed class NativeApi
        {
            internal readonly MethodInfo Options, Profile, Fields, Features, Influence, Classify, Flatten, Roughness;
            internal readonly FieldInfo Water, Landform, StarWorld;
            internal NativeApi()
            {
                Type type = typeof(FWStarMapCreater);
                Type profile = Nested(type, "StarMapTerrainProfile"), fields = Nested(type, "TerrainFields"),
                    features = Nested(type, "FeatureFields"), terrain = Nested(type, "TerrainClassification");
                Options = Method(type, "ResolvePlanetLiquidBoundaryOptions", typeof(FWGameData), typeof(FWStarMapInfo), typeof(FWStarMapGenerationOptions));
                Profile = Method(type, "ParseTerrainProfile", typeof(StarGroundDef));
                Fields = Method(type, "CreateTerrainFields", typeof(string), profile, typeof(int), typeof(int), typeof(int), typeof(int), typeof(bool));
                Features = Method(type, "CreateFeatureFields", typeof(List<FWStarMapTerrainFeatureDirective>), typeof(List<FWStarMapLiquidBoundaryDirective>),
                    typeof(List<Vector2>), typeof(string), typeof(int), typeof(int), typeof(int), typeof(int), typeof(string), typeof(bool));
                Influence = Method(type, "ApplyFeatureInfluence", fields, features);
                Classify = Method(type, "ClassifyTerrain", profile, fields, features, typeof(int), typeof(int), typeof(bool));
                Flatten = Method(type, "FlattenRoads", fields, features, terrain);
                Roughness = Method(type, "RecalculateRoughness", fields, features);
                if (Options.ReturnType != typeof(FWStarMapGenerationOptions) || Profile.ReturnType != profile || Fields.ReturnType != fields
                    || Features.ReturnType != features || Classify.ReturnType != terrain || Influence.ReturnType != typeof(void)
                    || Flatten.ReturnType != typeof(void) || Roughness.ReturnType != typeof(void))
                    throw new MissingMethodException("Native terrain stage return contracts have changed.");
                Water = Field(profile, "Water"); Landform = Field(terrain, "Landform"); StarWorld = Field(typeof(FWStarInfo), "starWorld");
                if (Landform.FieldType != typeof(string[,]) || StarWorld.FieldType != typeof(FWStarWorld))
                    throw new MissingFieldException("Native terrain field contracts have changed.");
            }
            private static Type Nested(Type type, string name)
            { return type.GetNestedType(name, BindingFlags.NonPublic) ?? throw new MissingMemberException(type.FullName, name); }
            private static FieldInfo Field(Type type, string name)
            { return type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) ?? throw new MissingFieldException(type.FullName, name); }
            private static MethodInfo Method(Type type, string name, params Type[] args)
            { return type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic, null, args, null) ?? throw new MissingMethodException(type.FullName, name); }
        }
        private static bool HasApi(out string reason)
        {
            lock (CacheLock)
            {
                if (!apiChecked)
                {
                    try { api = new NativeApi(); }
                    catch (MissingMemberException error) { apiError = error.Message; }
                    apiChecked = true;
                }
                reason = apiError; return api != null;
            }
        }
        private static object Call(MethodInfo method, params object[] args)
        {
            try { return method.Invoke(null, args); }
            catch (TargetInvocationException error)
            { throw new InvalidOperationException("Native seed terrain stage failed: " + method.Name, error.InnerException ?? error); }
        }
        private static Point Copy(Vector2 value) { return new Point { X = value.x, Y = value.y }; }
        private static Vector2 Native(Point value) { return new Vector2(value.X, value.Y); }
        private static Anchor Copy(FWStarMapTerrainAnchor value)
        { return value == null ? null : new Anchor { Kind = (int)value.Kind, Edge = (int)value.Edge, Position = Copy(value.NormalizedPosition), Tangent = Copy(value.Tangent) }; }
        private static FWStarMapTerrainAnchor Native(Anchor value)
        { return new FWStarMapTerrainAnchor { Kind = (FWStarMapTerrainAnchorKind)value.Kind, Edge = (FWStarMapTerrainEdge)value.Edge, NormalizedPosition = Native(value.Position), Tangent = Native(value.Tangent) }; }
        private static void CaptureOptions(Recipe recipe, FWStarMapGenerationOptions options)
        {
            if (options == null) return;
            if (options.TerrainFeatures != null) foreach (FWStarMapTerrainFeatureDirective value in options.TerrainFeatures)
            {
                if (value == null) continue; // The native feature rasterizer ignores null directives.
                recipe.Features.Add(new Feature { Id = value.StableId, Ground = value.SurfaceGroundResId, Type = (int)value.Type,
                    Priority = value.Priority, Start = Copy(value.Start), End = Copy(value.End), Width = value.Width, Falloff = value.Falloff, Strength = value.Strength,
                    Points = (value.NormalizedControlPoints ?? new List<Vector2>()).Select(Copy).ToList() });
            }
            if (options.LiquidBoundaries != null) foreach (FWStarMapLiquidBoundaryDirective value in options.LiquidBoundaries)
            {
                if (value == null) continue;
                recipe.Boundaries.Add(new Boundary { Id = value.StableId, Type = (int)value.LiquidType, Direction = Copy(value.OutwardDirection),
                    Center = value.CenterDistanceMultiplier, Radius = value.CoreHexRadiusRatio, Noise = value.CoastNoiseAmplitude,
                    Relaxations = value.CoastRelaxationPasses, Depth = value.DeepWaterDepth, Shore = value.ShoreFalloff,
                    ConnectionWidth = value.LandConnectionWidth, Strength = value.Strength });
            }
            if (options.LandConnectionDirections != null) recipe.LandDirections = options.LandConnectionDirections.Select(Copy).ToList();
        }
        private static FWStarMapGenerationOptions NativeOptions(Recipe recipe)
        {
            var result = new FWStarMapGenerationOptions { AutoDetectPlanetLiquidBoundaries = false, CaptureDiagnostics = false };
            foreach (Feature value in recipe.Features) result.TerrainFeatures.Add(new FWStarMapTerrainFeatureDirective { StableId = value.Id,
                Type = (FWStarMapTerrainFeatureType)value.Type, Start = Native(value.Start), End = Native(value.End), Width = value.Width,
                Falloff = value.Falloff, Strength = value.Strength, Priority = value.Priority, SurfaceGroundResId = value.Ground,
                NormalizedControlPoints = value.Points.Select(Native).ToList() });
            foreach (Boundary value in recipe.Boundaries) result.LiquidBoundaries.Add(new FWStarMapLiquidBoundaryDirective { StableId = value.Id,
                LiquidType = (FWStarMapBoundaryLiquidType)value.Type, OutwardDirection = Native(value.Direction), CenterDistanceMultiplier = value.Center,
                CoreHexRadiusRatio = value.Radius, CoastNoiseAmplitude = value.Noise, CoastRelaxationPasses = value.Relaxations,
                DeepWaterDepth = value.Depth, ShoreFalloff = value.Shore, LandConnectionWidth = value.ConnectionWidth, Strength = value.Strength });
            result.LandConnectionDirections = recipe.LandDirections.Select(Native).ToList();
            return result;
        }

        internal static bool TryEncode(FWGameData world, FWStarMapInfo map, out SeedTerrainPayload payload, out string reason)
        {
            try { return TryEncodeCandidate(world, map, out payload, out reason); }
            catch (InvalidDataException error)
            {
                // Codec bounds are deliberately narrower than the native editor's custom
                // terrain options. Those legal maps keep their complete native representation.
                payload = null; reason = "Seed codec cannot represent this native terrain: " + error.Message; return false;
            }
        }
        private static bool TryEncodeCandidate(FWGameData world, FWStarMapInfo map, out SeedTerrainPayload payload, out string reason)
        {
            payload = null; reason = null;
            if (map == null || map.GetType() != typeof(FWStarMapInfo) || map.starSpread == null || map.starSpread.isAirMap()
                || map.starSpread.underGround || map.starSpread.mapSpecialId != "-1"
                || map.worldSiteResId != "-1" || map.worldPoint == null || string.IsNullOrEmpty(map.seed))
            { reason = "This map uses the full native terrain format."; return false; }
            if (!HasApi(out reason)) return false;
            string[,] current = map.starSpread.getTiles();
            if (current == null) throw new InvalidOperationException("Native seed map has no terrain: " + map.starMapId);
            int width = current.GetLength(1), height = current.GetLength(0);
            if (width <= 0 || height <= 0 || width > 2048 || height > 2048 || (long)width * height > MaxCells
                || current.GetLowerBound(0) != 0 || current.GetLowerBound(1) != 0)
            { reason = "Native terrain exceeds the bounded seed recipe size."; return false; }
            FWStarInfo star = world == null ? null : world.getStarInfo(map.starId);
            FWStarWorld planet = star == null ? null : api.StarWorld.GetValue(star) as FWStarWorld;
            FWStarWorldPoint[,] points = planet == null ? null : planet.getPoints();
            // Native options resolution can load/generate/save a missing planet. This optional
            // network codec may read only an already valid planet cache, never create one.
            if (points == null || !points.Cast<FWStarWorldPoint>().Any(value => value != null))
            { reason = "Planet terrain options are not loaded; send full terrain."; return false; }
            StarGroundDef ground = StarGroundDef.getDataID(map.worldPoint.starGroundResId);
            if (ground == null) throw new InvalidOperationException("Native seed terrain definition is missing: " + map.worldPoint.starGroundResId);
            object profile = Call(api.Profile, ground);
            var recipe = new Recipe { Generator = Generator, Seed = map.seed, Ground = ground.ID, X = map.worldPoint.worldX, Y = map.worldPoint.worldY,
                Width = width, Height = height, ProfileHash = HashBytes(System.Text.Encoding.UTF8.GetBytes(NativeWire.Json(profile))) };
            CaptureOptions(recipe, (FWStarMapGenerationOptions)Call(api.Options, world, map, null));
            ValidateRecipe(recipe);
            int fullSize = NativeWire.Json(MatrixShadow<string>.From2D(current)).Length;
            if (NativeWire.Json(new SeedTerrainPayload { Schema = 1, MapId = map.starMapId, Recipe = recipe,
                BaseHash = new string('0', 64), CurrentHash = new string('0', 64) }).Length >= fullSize)
            { reason = "Full native terrain is smaller than even an unchanged seed recipe."; return false; }
            CachedTerrain baseline = GetBaseline(recipe, profile, null, map.starMapId);
            var result = new SeedTerrainPayload { Schema = 1, MapId = map.starMapId, Recipe = recipe, BaseHash = baseline.Hash, CurrentHash = HashTiles(current) };
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                if (!String.Equals(current[y, x], baseline.Tiles[y, x], StringComparison.Ordinal)) result.Overrides.Add(new Override { X = x, Y = y, Ground = current[y, x] });
            // The native shadow already uses a palette/RLE. Prefer full terrain when a seed
            // header plus real modifications would cost more than that existing compact form.
            if (NativeWire.Json(result).Length >= fullSize)
            { reason = "Full native terrain is smaller than the seed recipe and overrides."; return false; }
            payload = result; return true;
        }
        internal static string[,] Decode(SeedTerrainPayload payload)
        {
            if (payload == null || payload.Schema != 1 || payload.MapId < 0 || !ValidHash(payload.BaseHash) || !ValidHash(payload.CurrentHash)
                || payload.Overrides == null) throw new InvalidDataException("Invalid seed terrain payload.");
            if (payload.Recipe != null && (payload.Recipe.Generator == null || payload.Recipe.Generator.Length > 128))
                throw new InvalidDataException("Invalid seed terrain generator identifier.");
            if (payload.Recipe != null && payload.Recipe.Generator != Generator)
                throw new SeedTerrainMismatchException(payload.MapId, Generator, payload.Recipe.Generator, "Native terrain recipe capability differs.");
            ValidateRecipe(payload.Recipe);
            if (payload.Overrides.Count > (long)payload.Recipe.Width * payload.Recipe.Height) throw new InvalidDataException("Too many terrain overrides.");
            string reason;
            if (!HasApi(out reason)) throw new SeedTerrainMismatchException(payload.MapId, payload.BaseHash, "unavailable", reason);
            StarGroundDef ground = StarGroundDef.getDataID(payload.Recipe.Ground);
            if (ground == null) throw new SeedTerrainMismatchException(payload.MapId, payload.Recipe.Ground, "missing", "Ground definition is unavailable.");
            object profile;
            try { profile = Call(api.Profile, ground); }
            catch (InvalidOperationException error) { throw new SeedTerrainMismatchException(payload.MapId, payload.Recipe.ProfileHash, "invalid", "Ground profile differs.", error); }
            string profileHash = HashBytes(System.Text.Encoding.UTF8.GetBytes(NativeWire.Json(profile)));
            if (profileHash != payload.Recipe.ProfileHash) throw new SeedTerrainMismatchException(payload.MapId, payload.Recipe.ProfileHash, profileHash, "Ground profile differs.");
            CachedTerrain baseline;
            try { baseline = GetBaseline(payload.Recipe, profile, payload.BaseHash, payload.MapId); }
            catch (InvalidOperationException error)
            { throw new SeedTerrainMismatchException(payload.MapId, payload.BaseHash, "unavailable", "Native terrain reconstruction failed.", error); }
            catch (InvalidDataException error)
            { throw new SeedTerrainMismatchException(payload.MapId, payload.BaseHash, "invalid-cache", "Local terrain baseline is invalid.", error); }
            string[,] tiles = (string[,])baseline.Tiles.Clone();
            var changed = new HashSet<int>();
            foreach (Override value in payload.Overrides)
            {
                if (value == null || value.X < 0 || value.Y < 0 || value.X >= payload.Recipe.Width || value.Y >= payload.Recipe.Height
                    || !changed.Add(value.Y * payload.Recipe.Width + value.X) || !ValidId(value.Ground))
                    throw new InvalidDataException("Invalid or duplicate seed terrain override.");
                if (Map_GroundDef.getDataID(value.Ground) == null)
                    throw new SeedTerrainMismatchException(payload.MapId, value.Ground, "missing", "Override ground definition is unavailable.");
                tiles[value.Y, value.X] = value.Ground;
            }
            string currentHash = HashTiles(tiles);
            if (currentHash != payload.CurrentHash) throw new SeedTerrainMismatchException(payload.MapId, payload.CurrentHash, currentHash, "Final terrain checksum differs.");
            return tiles;
        }
        private static CachedTerrain GetBaseline(Recipe recipe, object profile, string expected, long mapId)
        {
            string key = HashBytes(System.Text.Encoding.UTF8.GetBytes(NativeWire.Json(recipe)));
            Func<string, byte[]> read;
            lock (CacheLock)
            {
                CachedTerrain cached;
                if (Cache.TryGetValue(key, out cached) && (expected == null || expected == cached.Hash))
                { cached.Use = ++cacheUse; return cached; }
                read = readContent;
            }
            if (expected != null && read != null)
            {
                byte[] bytes = read(expected);
                if (bytes != null)
                {
                    if (HashBytes(bytes) != expected) throw new InvalidDataException("Seed terrain content cache checksum differs.");
                    string[,] cached = DecodeTiles(bytes, recipe.Width, recipe.Height);
                    return CacheTerrain(key, cached, expected);
                }
            }
            FWStarMapGenerationOptions options = NativeOptions(recipe);
            object fields = Call(api.Fields, recipe.Seed, profile, recipe.X, recipe.Y, recipe.Width, recipe.Height, true);
            List<FWStarMapLiquidBoundaryDirective> boundaries = api.Water.GetValue(profile) == null ? null : options.LiquidBoundaries;
            object features = Call(api.Features, options.TerrainFeatures, boundaries, options.LandConnectionDirections,
                recipe.Seed, recipe.X, recipe.Y, recipe.Width, recipe.Height, recipe.Ground, false);
            Call(api.Influence, fields, features);
            object classified = Call(api.Classify, profile, fields, features, recipe.Width, recipe.Height, true);
            Call(api.Flatten, fields, features, classified);
            Call(api.Roughness, fields, features);
            return CacheGeneratedTerrain(key, recipe, classified, expected, mapId);
        }
        private static CachedTerrain CacheGeneratedTerrain(string key, Recipe recipe, object classified, string expected, long mapId)
        {
            string[,] tiles = api.Landform.GetValue(classified) as string[,];
            if (tiles == null || tiles.GetLength(0) != recipe.Height || tiles.GetLength(1) != recipe.Width)
                throw new InvalidOperationException("Native seed classifier returned invalid terrain dimensions.");
            byte[] bytes = EncodeTiles(tiles);
            string hash = HashBytes(bytes);
            if (expected != null && hash != expected) throw new SeedTerrainMismatchException(mapId, expected, hash, "Native terrain generation differs.");
            Action<string, byte[]> write;
            lock (CacheLock) write = writeContent;
            if (write != null) write(hash, bytes);
            return CacheTerrain(key, tiles, hash);
        }
        private static CachedTerrain CacheTerrain(string key, string[,] tiles, string hash)
        {
            var cached = new CachedTerrain { Key = key, Hash = hash, Tiles = (string[,])tiles.Clone() };
            lock (CacheLock)
            {
                CachedTerrain old;
                if (Cache.TryGetValue(key, out old)) { cacheCells -= old.Tiles.Length; Cache.Remove(key); }
                while (Cache.Count >= MaxCacheEntries || cacheCells + tiles.Length > MaxCacheCells)
                {
                    CachedTerrain oldest = Cache.Values.OrderBy(value => value.Use).First(); Cache.Remove(oldest.Key); cacheCells -= oldest.Tiles.Length;
                }
                cached.Use = ++cacheUse; Cache.Add(key, cached); cacheCells += cached.Tiles.Length;
            }
            return cached;
        }
        internal static void ConfigureContentCache(Func<string, byte[]> read, Action<string, byte[]> write)
        { lock (CacheLock) { readContent = read; writeContent = write; } }
        internal static void ClearCache()
        { lock (CacheLock) { Cache.Clear(); cacheCells = 0; cacheUse = 0; readContent = null; writeContent = null; } }
        internal static string HashTiles(string[,] tiles)
        { return SeedTerrainStreamingCodec.HashTiles(tiles); }
        internal static byte[] EncodeTiles(string[,] tiles)
        { return SeedTerrainStreamingCodec.EncodeTiles(tiles); }
        private static string[,] DecodeTiles(byte[] bytes, int width, int height)
        {
            // Every length is checked before allocating, including content recovered from the
            // local cache. The palette/index encoding is stable across CLR and game versions.
            if (bytes.Length > MaxCells * 4 + 4096 * 1028 + 16) throw new InvalidDataException("Seed terrain cache content exceeds its limit.");
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8))
            {
                if (reader.ReadInt32() != 0x31544D46 || reader.ReadInt32() != width || reader.ReadInt32() != height)
                    throw new InvalidDataException("Seed terrain cache dimensions differ.");
                int count = reader.ReadInt32();
                if (count <= 0 || count > 4096 || count > (long)width * height) throw new InvalidDataException("Invalid seed terrain palette size.");
                var palette = new string[count]; var unique = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < count; i++)
                {
                    int length = reader.ReadInt32();
                    if (length <= 0 || length > 1024 || length > stream.Length - stream.Position) throw new InvalidDataException("Invalid seed terrain palette text length.");
                    string value = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(length));
                    if (!ValidId(value) || !unique.Add(value) || Map_GroundDef.getDataID(value) == null) throw new InvalidDataException("Seed terrain cache references invalid ground.");
                    palette[i] = value;
                }
                if (stream.Length - stream.Position != (long)width * height * 4) throw new InvalidDataException("Seed terrain cache cell count differs.");
                var tiles = new string[height, width];
                for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                {
                    int index = reader.ReadInt32();
                    if (index < 0 || index >= count) throw new InvalidDataException("Seed terrain cache palette index is invalid.");
                    tiles[y, x] = palette[index];
                }
                if (stream.Position != stream.Length) throw new InvalidDataException("Seed terrain cache has trailing content.");
                return tiles;
            }
        }
        private static string HashBytes(byte[] value)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(value)).Replace("-", "").ToLowerInvariant(); }
        internal static void ValidateMap(FWStarMapInfo map, SeedTerrainPayload payload)
        {
            if (map == null || map.GetType() != typeof(FWStarMapInfo) || payload == null || payload.Recipe == null || map.starSpread == null
                || map.starSpread.isAirMap() || map.starSpread.underGround || map.starSpread.mapSpecialId != "-1" || map.worldSiteResId != "-1"
                || map.starMapId != payload.MapId || map.starSpread.starMapId != payload.MapId || map.seed != payload.Recipe.Seed
                || map.worldPoint == null || map.worldPoint.worldX != payload.Recipe.X || map.worldPoint.worldY != payload.Recipe.Y
                || map.worldPoint.starGroundResId != payload.Recipe.Ground || map.starSpread.xLength != payload.Recipe.Width || map.starSpread.yLength != payload.Recipe.Height)
                throw new InvalidDataException("Seed terrain recipe does not describe this native map.");
        }
        private static bool ValidId(string value) { return !String.IsNullOrEmpty(value) && value.Length <= 256; }
        private static bool ValidHash(string value)
        {
            return value != null && value.Length == 64 && value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
        }
        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value) && Math.Abs(value) <= 1000000; }
        private static bool Valid(Point value) { return value != null && Finite(value.X) && Finite(value.Y); }
        private static bool Valid(Anchor value)
        { return value != null && value.Kind >= 0 && value.Kind <= 1 && value.Edge >= 0 && value.Edge <= 3 && Valid(value.Position) && Valid(value.Tangent); }
        private static void ValidateRecipe(Recipe recipe)
        {
            if (recipe == null || recipe.Generator != Generator || String.IsNullOrEmpty(recipe.Seed) || recipe.Seed.Length > 2048 || !ValidId(recipe.Ground)
                || !ValidHash(recipe.ProfileHash) || recipe.Width <= 0 || recipe.Height <= 0 || recipe.Width > 2048 || recipe.Height > 2048
                || (long)recipe.Width * recipe.Height > MaxCells || recipe.X < 0 || recipe.X >= WorldMapUtils.world_Width || recipe.Y < 0 || recipe.Y >= WorldMapUtils.world_Height
                || recipe.Features == null || recipe.Boundaries == null || recipe.LandDirections == null
                || recipe.Features.Count > 64 || recipe.Boundaries.Count > 64 || recipe.LandDirections.Count > 64 || recipe.LandDirections.Any(value => !Valid(value)))
                throw new InvalidDataException("Invalid or unsupported seed terrain recipe.");
            var featureIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (Feature value in recipe.Features)
                if (value == null || !ValidId(value.Id) || !featureIds.Add(value.Id) || value.Type < 0 || value.Type > 2 || !Valid(value.Start) || !Valid(value.End)
                    || !Finite(value.Width) || value.Width <= 0 || !Finite(value.Falloff) || value.Falloff < 0 || !Finite(value.Strength) || value.Strength <= 0
                    || value.Points == null || value.Points.Count > 64 || value.Points.Any(point => !Valid(point)) || !ValidId(value.Ground))
                    throw new InvalidDataException("Invalid seed terrain feature.");
            var boundaryIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (Boundary value in recipe.Boundaries)
                if (value == null || !ValidId(value.Id) || !boundaryIds.Add(value.Id) || value.Type != 0 || !Valid(value.Direction)
                    || value.Direction.X * value.Direction.X + value.Direction.Y * value.Direction.Y <= 0.000001f
                    || !Finite(value.Center) || value.Center <= 0 || !Finite(value.Radius) || value.Radius <= 0 || !Finite(value.Noise) || value.Noise < 0
                    || value.Relaxations < 0 || value.Relaxations > 64 || !Finite(value.Depth) || value.Depth <= 0 || !Finite(value.Shore) || value.Shore < 0
                    || value.ConnectionWidth <= 0 || value.ConnectionWidth > 2048 || !Finite(value.Strength) || value.Strength <= 0)
                    throw new InvalidDataException("Invalid seed terrain boundary.");
        }
    }
}
