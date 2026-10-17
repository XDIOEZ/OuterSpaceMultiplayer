using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // The real Storage.cs, IdentityStore.cs and ModSettings.cs are compiled
    // without Unity or a live World. Only domain DTOs are replaced.
    public sealed class LocalIdentity
    {
        public string CredentialId;
        public string Secret;
        public string Nickname;
    }
    public sealed class PlayerRecord
    {
        public string CredentialId;
        public string CredentialHash;
    }
    public sealed class CommandResult
    {
        public bool Applied;
        public bool Saved;
        public long SaveGeneration;
    }
    public sealed class WorldSnapshot
    {
        public string NativeJson;
    }
    public enum Relation { Neutral, Friendly, Hostile }
    public sealed class RelationshipRecord
    {
        public string PlayerA;
        public string PlayerB;
        public Relation Relation;
        public bool FriendlyConsentA;
        public bool FriendlyConsentB;
    }
    public sealed class WorldState
    {
        public int SchemaVersion = 1;
        public string WorldId;
        public string Name;
        public long SaveGeneration;
        public WorldSnapshot Snapshot;
        public List<RelationshipRecord> Relationships = new List<RelationshipRecord>();
        public Dictionary<string, CommandResult> Transactions =
            new Dictionary<string, CommandResult>();
    }
}
