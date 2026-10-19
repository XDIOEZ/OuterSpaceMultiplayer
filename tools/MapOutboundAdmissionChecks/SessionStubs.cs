using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FreeWorldMultiplayer
{
    internal sealed class MapSnapshot { internal long MapId, Revision; }
    internal sealed class MapUpdate
    {
        internal string CommitId, SessionId, NativeDeltaJson;
        internal long MapId, AuthorityEpoch, BaseRevision, Revision;
        internal MapSnapshot Checkpoint;
    }
    internal sealed class MapRecord
    {
        internal long MapId, AuthorityEpoch, Revision;
        internal bool AwaitingAuthorityReady;
        internal string SimulatorPlayerId;
    }
    internal sealed class WorldState
    {
        internal string WorldId = "world";
        internal readonly Dictionary<long, MapRecord> Maps = new Dictionary<long, MapRecord>();
    }
    internal sealed class FakeTransport
    {
        internal bool IsRunning = true;
        internal int Failures, InvalidFailures;
        internal Exception UnexpectedFailure;
        internal Action AfterSend;
        internal readonly List<byte[]> Attempted = new List<byte[]>();
        internal readonly List<byte[]> Accepted = new List<byte[]>();

        internal void Send(string peer, byte[] frame)
        {
            if (peer != "host") throw new Exception("Wrong peer.");
            Attempted.Add(frame);
            if (Failures > 0)
            {
                Failures--;
                if (InvalidFailures > 0)
                {
                    InvalidFailures--;
                    throw new InvalidOperationException("synthetic closed queue");
                }
                throw new IOException("synthetic TCP or Steam queue full");
            }
            if (UnexpectedFailure != null) throw UnexpectedFailure;
            Accepted.Add((byte[])frame.Clone());
            if (AfterSend != null) AfterSend();
        }
    }
    public sealed partial class MultiplayerSession
    {
        private readonly Dictionary<long, MapUpdate> inFlightUpdates = new Dictionary<long, MapUpdate>();
        private readonly FakeTransport transport = new FakeTransport();
        private WorldState World = new WorldState();
        private string SessionId = "S1", LocalPlayerId = "local", nativeFault;
        private bool IsConnected = true, IsHost, Paused, closing;
        internal bool FailEncode, FailNativeDiagnostic;
        internal int EncodeCalls, Locks, CaptureCalls;
        internal readonly List<string> ErrorKinds = new List<string>();

        internal MultiplayerSession()
        {
            World.Maps.Add(7, new MapRecord
            {
                MapId = 7, AuthorityEpoch = 3, Revision = 5,
                SimulatorPlayerId = "local"
            });
        }
        private static MapUpdate WireMapUpdate(MapUpdate update)
        {
            return new MapUpdate
            {
                CommitId = update.CommitId, SessionId = update.SessionId,
                MapId = update.MapId, AuthorityEpoch = update.AuthorityEpoch,
                BaseRevision = update.BaseRevision, Revision = update.Revision,
                NativeDeltaJson = update.NativeDeltaJson
            };
        }
        private byte[] EncodeMessage(string kind, object payload, string requestId)
        {
            EncodeCalls++;
            if (FailEncode) throw new FormatException("native-payload-is-private");
            MapUpdate update = (MapUpdate)payload;
            return Encoding.UTF8.GetBytes(kind + "|" + SessionId + "|" + update.MapId + "|"
                + update.AuthorityEpoch + "|" + update.BaseRevision + "|" + update.Revision
                + "|" + update.CommitId + "|" + update.NativeDeltaJson);
        }
        private void LockNativeFault(string reason)
        {
            Locks++;
            nativeFault = reason;
            Paused = true;
            if (FailNativeDiagnostic) throw new IOException("synthetic status delivery failed");
        }
        internal FakeTransport Network { get { return transport; } }
        internal bool Pending { get { return pendingMapOutbound != null; } }
        internal string PendingCommit { get { return pendingMapOutbound?.Update.CommitId; } }
        internal byte[] PendingFrame { get { return pendingMapOutbound?.Frame; } }
        internal int FlightCount { get { return inFlightUpdates.Count; } }
        internal int InFlightMapCount(long mapId) { return inFlightUpdates.ContainsKey(mapId) ? 1 : 0; }
        internal bool Faulted { get { return nativeFault != null && Paused; } }
        internal string Fault { get { return nativeFault; } }
        internal string Room { get { return SessionId; } }
        internal long Revision { get { return World.Maps[7].Revision; } }
        internal string Commit { get { return World.Maps[7].MapId.ToString(); } }

        internal MapUpdate CaptureMap(string id, long mapId = 7)
        {
            CaptureCalls++;
            MapRecord map = World.Maps[mapId];
            return new MapUpdate
            {
                CommitId = id, SessionId = SessionId, MapId = mapId,
                AuthorityEpoch = map.AuthorityEpoch, BaseRevision = map.Revision,
                Revision = map.Revision + 1, NativeDeltaJson = "private-map-delta",
                Checkpoint = new MapSnapshot { MapId = mapId, Revision = map.Revision + 1 }
            };
        }
        internal MapUpdate PublishOnce(string id)
        {
            if (pendingMapOutbound != null && StillPendingMap(pendingMapOutbound)) return null;
            if (inFlightUpdates.ContainsKey(7)) return null;
            MapUpdate update = CaptureMap(id);
            SendCapturedMapUpdate(update);
            return update;
        }
        internal void SendUpdate(MapUpdate update) { SendCapturedMapUpdate(update); }
        internal void Clock(double seconds) { TickMapOutbound(seconds); }
        internal void Ack(long mapId)
        {
            MapUpdate update;
            if (!inFlightUpdates.TryGetValue(mapId, out update)) return;
            var map = World.Maps[mapId];
            map.Revision = update.Revision;
            inFlightUpdates.Remove(mapId);
            RetireMapOutbound(update);
        }
        internal void ReplaceView(long mapId, long revision = 20)
        {
            World.Maps[mapId].Revision = revision;
            inFlightUpdates.Remove(mapId);
        }
        internal void ChangeAuthority(long epoch) { World.Maps[7].AuthorityEpoch = epoch; }
        internal void ChangeSession(string id) { SessionId = id; }
        internal void ChangeWorld(string id) { World.WorldId = id; }
        internal void ChangeSimulator(string id) { World.Maps[7].SimulatorPlayerId = id; }
        internal void MarkAwaiting() { World.Maps[7].AwaitingAuthorityReady = true; }
        internal void Leave() { ClearMapOutbound(); IsConnected = false; }
        internal void Close() { closing = true; }
        internal void HostMode() { IsHost = true; }
        internal void UnlockFaultForTest() { nativeFault = null; Paused = false; }
    }
}
