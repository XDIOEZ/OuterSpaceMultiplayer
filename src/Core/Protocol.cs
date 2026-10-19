using System;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace FreeWorldMultiplayer
{
    internal sealed class WireMessage
    {
        public int Version = 1;
        public string Kind;
        public string SessionId;
        public string RequestId;
        public JToken Payload;
    }
    internal static class Protocol
    {
        internal const int CurrentVersion = 11;
        public static byte[] Encode(string kind, string sessionId, object payload, string requestId = null)
        {
            return Encoding.UTF8.GetBytes(JsonData.Write(new WireMessage { Kind = kind, SessionId = sessionId, RequestId = requestId, Payload = payload == null ? null : JToken.FromObject(payload, Newtonsoft.Json.JsonSerializer.Create(JsonData.Settings)) }));
        }
        public static WireMessage Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > 128 * 1024 * 1024) throw new InvalidDataException("Invalid multiplayer frame length.");
            var message = JsonData.Read<WireMessage>(Encoding.UTF8.GetString(bytes));
            if (message == null || message.Version != 1 || string.IsNullOrEmpty(message.Kind)) throw new InvalidDataException("Unsupported multiplayer protocol.");
            return message;
        }
    }
    internal sealed class Hello { public string CredentialId; public string Nickname; }
    internal sealed class Challenge { public string Nonce; public bool Enroll; }
    internal sealed class Authenticate { public string CredentialId; public string Verifier; public string Proof; public string Nickname; }
    internal sealed class Welcome { public string PlayerId; public WorldState World; public UidRange Uids; }
    internal sealed class PlayerIntroduction { public PlayerRecord Player; }
    internal sealed class PeerAuthentication { public string CredentialId; public string Nonce; public string Nickname; }
    internal sealed class AuthorityReady { public long MapId; public long AuthorityEpoch; public long Revision; }
    internal sealed class MapView { public MapSnapshot Snapshot; public MapAccess Access; public System.Collections.Generic.List<PlayerFieldState> Players = new System.Collections.Generic.List<PlayerFieldState>(); }
    internal sealed class MapReference { public long MapId; }
    internal sealed class ClockState { public bool Paused; public double Speed; public double RoomRealSeconds; public long WorldTimeMM; }
    internal sealed class RaidClockState { public System.Collections.Generic.List<AttackRecord> Active; public System.Collections.Generic.Dictionary<long, double> Cooldowns; }
    internal sealed class OwnerRegistration { public long EntityId; public string PlayerId; public long MapId; public long AuthorityEpoch; }
    internal sealed class SessionPolicy
    {
        public ListPolicyPlayers Players;
        public System.Collections.Generic.Dictionary<long, MapRecord> Maps;
        public System.Collections.Generic.Dictionary<long, string> EntityOwners;
        public System.Collections.Generic.List<RelationshipRecord> Relationships;
        public System.Collections.Generic.Dictionary<string, AttackRecord> Attacks;
        public System.Collections.Generic.Dictionary<string, TransferRecord> Transfers;
    }
    internal sealed class ListPolicyPlayers { public System.Collections.Generic.List<PlayerRecord> Items; }
}
