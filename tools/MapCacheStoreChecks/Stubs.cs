using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace FreeWorldMultiplayer
{
    // Game/Unity dependencies are replaced, never the production cache logic.
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
            DateParseHandling = DateParseHandling.None
        };
        internal static T Read<T>(string json) { return JsonConvert.DeserializeObject<T>(json, Settings); }
        internal static string Write(object value) { return JsonConvert.SerializeObject(value, Settings); }
    }
}
