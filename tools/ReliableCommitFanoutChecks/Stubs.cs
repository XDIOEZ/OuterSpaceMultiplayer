using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FreeWorldMultiplayer
{
    internal sealed class MapUpdate
    {
        internal string CommitId, SessionId, NativeDeltaJson;
        internal long MapId, AuthorityEpoch, BaseRevision, Revision;
        internal List<PlayerUpdate> Players = new List<PlayerUpdate>();
    }
    internal sealed class PlayerUpdate
    {
        internal string PlayerId;
        internal long BaseRevision, Revision;
    }

    internal sealed class FakeTransport
    {
        internal bool IsRunning = true;
        internal readonly List<string> Events = new List<string>();
        internal readonly HashSet<string> SendFailures = new HashSet<string>(StringComparer.Ordinal);
        internal readonly HashSet<string> DisconnectFailures = new HashSet<string>(StringComparer.Ordinal);
        internal readonly HashSet<string> Disconnected = new HashSet<string>(StringComparer.Ordinal);
        internal readonly Dictionary<string, List<string>> Messages = new Dictionary<string, List<string>>();
        internal int Encodes;
        internal void Send(string peer, byte[] payload)
        {
            string data = Encoding.UTF8.GetString(payload);
            Events.Add("send:" + peer + ":" + data);
            if (SendFailures.Contains(peer)) throw new IOException("queue full:" + peer);
            if (Disconnected.Contains(peer)) throw new InvalidOperationException("disconnected:" + peer);
            List<string> messages;
            if (!Messages.TryGetValue(peer, out messages))
                Messages[peer] = messages = new List<string>();
            messages.Add(data);
        }
        internal void Disconnect(string peer, string reason)
        {
            Events.Add("disconnect:" + peer);
            if (DisconnectFailures.Contains(peer)) throw new InvalidOperationException("cannot disconnect:" + peer);
            Disconnected.Add(peer);
        }
    }

    public sealed partial class MultiplayerSession
    {
        private bool IsHost = true;
        private readonly FakeTransport transport = new FakeTransport();
        private readonly Dictionary<string, string> peers = new Dictionary<string, string>(StringComparer.Ordinal);
        private byte[] EncodeMessage(string kind, object payload, string requestId = null)
        {
            transport.Encodes++;
            string content;
            var personal = payload as PlayerUpdate;
            var map = payload as MapUpdate;
            if (personal != null) content = kind + ":" + personal.PlayerId + ":" + personal.Revision;
            else if (map != null) content = kind + ":" + map.CommitId + ":" + map.Revision;
            else throw new InvalidDataException("unsupported fake DTO");
            return Encoding.UTF8.GetBytes(content);
        }
        private static MapUpdate WireMapUpdate(MapUpdate value)
        { return new MapUpdate { CommitId = value.CommitId, Revision = value.Revision }; }

        internal FakeTransport TestTransport { get { return transport; } }
        internal void AddPeer(string peer) { peers[peer] = peer; }
        internal void ResetMessages() { transport.Messages.Clear(); transport.Events.Clear(); transport.Encodes = 0; }
        internal void PublishPersonal(string excluded, PlayerUpdate update)
        { BroadcastCommittedUpdate("personal", update, excluded); }
        internal void PublishMapTo(string peer, MapUpdate update)
        { SendCommittedMapToObserver(peer, update); }
        internal void PublishPersonalTo(string peer, PlayerUpdate update)
        { BroadcastCommittedPersonalToRecipient(peer, update); }
        internal void SetHost(bool value) { IsHost = value; }
        internal MapUpdate BuildReceipt(MapUpdate map) { return CommittedMapReceipt(map); }
    }
}
