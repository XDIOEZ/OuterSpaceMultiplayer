using System;
using System.Collections.Generic;
using System.IO;

namespace FreeWorldMultiplayer
{
    public sealed class UidRange
    {
        public long First, Last, Next;
        public string SessionId;
    }
    public sealed class PlayerRecord
    {
        public string PlayerId;
        public UidRange Uids;
        public readonly List<UidRange> IssuedUidRanges = new List<UidRange>();
    }
    internal sealed class FakeWorld
    {
        internal long UidHighWater;
        internal readonly Dictionary<string, PlayerRecord> Players = new Dictionary<string, PlayerRecord>();
    }
    internal sealed class WireMessage
    {
        internal string RequestId;
        internal object Payload;
    }
    internal static class JsonData
    {
        internal static T Payload<T>(object payload) { return (T)payload; }
    }
    internal sealed class MessageRejectionException : InvalidOperationException
    {
        internal MessageRejectionException(string text) : base(text) { }
    }
    public sealed partial class MultiplayerSession
    {
        private readonly FakeWorld World = new FakeWorld();
        private bool IsHost;
        private bool IsConnected = true;
        private string LocalPlayerId;
        private string SessionId;
        internal int ReserveCalls, SaveCalls, SendCalls;
        internal int FailSaves, FailSends;
        internal Action<string, string, object, string> OnSend;
        internal readonly List<string> SentKeys = new List<string>();
        internal readonly List<UidRange> SentRanges = new List<UidRange>();

        internal MultiplayerSession(bool host, string session = "room-20261018")
        {
            IsHost = host;
            SessionId = session;
            LocalPlayerId = host ? "host" : "guest";
            var range = new UidRange { First = 1, Last = 1000000, Next = 1, SessionId = SessionId };
            World.UidHighWater = range.Last;
            World.Players.Add("guest", new PlayerRecord { PlayerId = "guest", Uids = range });
            World.Players["guest"].IssuedUidRanges.Add(range);
        }
        internal static UidRange Copy(UidRange old)
        { return new UidRange { First = old.First, Last = old.Last, Next = old.Next, SessionId = old.SessionId }; }

        private UidRange ReserveUidRange(bool persist)
        {
            ReserveCalls++;
            long first = checked(World.UidHighWater + 1);
            World.UidHighWater = checked(World.UidHighWater + 1000000);
            var range = new UidRange { First = first, Last = World.UidHighWater, Next = first, SessionId = SessionId };
            if (persist) SaveOrThrow();
            return range;
        }
        private void SaveOrThrow()
        {
            SaveCalls++;
            if (FailSaves > 0) { FailSaves--; throw new IOException("synthetic durable save failed"); }
        }
        private void Send(string peer, string kind, object data, string requestId = null)
        {
            SendCalls++;
            if (FailSends > 0) { FailSends--; throw new IOException("synthetic bounded queue full"); }
            SentKeys.Add(requestId);
            if (data is UidRange) SentRanges.Add(Copy((UidRange)data));
            OnSend?.Invoke(peer, kind, data is UidRange ? Copy((UidRange)data) : data, requestId);
        }

        internal PlayerRecord GuestRecord { get { return World.Players["guest"]; } }
        internal long HighWater { get { return World.UidHighWater; } }
        internal int IssuedCount { get { return GuestRecord.IssuedUidRanges.Count; } }
        internal void AskHost(string requestKey)
        { ReceiveUidRangeRequest("guest-peer", "guest", requestKey); }
        internal void Request()
        { RequestUidRenewal(GuestRecord); }
        internal void Tick(double seconds)
        { TickUidRenewal(seconds); }
        internal void Incoming(UidRange range, string requestKey)
        { ReceiveUidRenewal(new WireMessage { Payload = range, RequestId = requestKey }); }
        internal string PendingKey { get { return uidClientRequests.PendingKey; } }
        internal void ResetClientRange(UidRange range) { GuestRecord.Uids = Copy(range); }
        internal void SetCursor(long next) { GuestRecord.Uids.Next = next; }
    }
}
