using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Newtonsoft.Json;

namespace FreeWorldMultiplayer
{
    // Minimal domain types used by the production codec. The test replaces only IO/game
    // dependencies; it compiles the real MapSnapshotDeltaCodec implementation.
    public sealed class MapSnapshot
    {
        public long MapId;
        public long Revision;
        public bool IsDeltaBaseline;
        public string NativeJson;
        public Dictionary<long, string> EntityOwners = new Dictionary<long, string>();
    }

    internal static class JsonData
    {
        internal static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.None,
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            MaxDepth = 128,
            ObjectCreationHandling = ObjectCreationHandling.Replace,
            DateParseHandling = DateParseHandling.None,
            FloatParseHandling = FloatParseHandling.Double
        };
    }

    internal static class MapCacheStore
    {
        internal static int HashCalls;
        internal static string HashContent(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            HashCalls++;
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }
    }
}
