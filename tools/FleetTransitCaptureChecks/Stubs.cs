using System;
using System.Collections;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    internal sealed class CountedList<T> : List<T>
    {
        internal long Enumerations, Visited;
        public new IEnumerator<T> GetEnumerator()
        {
            Enumerations++;
            return new CountingEnumerator(this, ((IEnumerable<T>)this).GetEnumerator());
        }
        private sealed class CountingEnumerator : IEnumerator<T>
        {
            private readonly CountedList<T> parent;
            private readonly IEnumerator<T> inner;
            internal CountingEnumerator(CountedList<T> parent, IEnumerator<T> inner)
            { this.parent = parent; this.inner = inner; }
            public bool MoveNext()
            {
                if (!inner.MoveNext()) return false;
                parent.Visited++;
                return true;
            }
            public T Current { get { return inner.Current; } }
            object IEnumerator.Current { get { return inner.Current; } }
            public void Reset() { inner.Reset(); }
            public void Dispose() { inner.Dispose(); }
        }
    }

    internal abstract class FWBaseBean
    {
        internal long Id;
        internal string Payload;
        internal long getUID() { return Id; }
    }
    internal sealed class FWExpedition : FWBaseBean
    {
        internal readonly List<long> roleUidList = new List<long>();
        internal readonly List<long> animalUidList = new List<long>();
    }
    internal sealed class FWHeroBean : FWBaseBean
    {
        internal long starMapId = -1, fleetUid = -1;
    }
    internal sealed class FWAnimalBean : FWBaseBean
    {
        internal long starMapId = -1, fleetUid = -1;
    }
    internal sealed class FWGameData
    {
        internal readonly CountedList<FWExpedition> expeditionSeq = new CountedList<FWExpedition>();
        internal readonly CountedList<FWHeroBean> heros = new CountedList<FWHeroBean>();
        internal readonly CountedList<FWAnimalBean> animals = new CountedList<FWAnimalBean>();
    }
    internal static class NativeWire
    {
        internal sealed class CachedValue
        {
            internal readonly string Encoded;
            internal CachedValue(string value) { Encoded = value; }
        }
        internal static long Captures, CacheHits;
        internal static long Uid(FWBaseBean bean) { return bean.getUID(); }
        internal static CachedValue CaptureValue(FWBaseBean bean, CachedValue previous)
        {
            Captures++;
            if (previous != null && previous.Encoded == bean.Payload)
            { CacheHits++; return previous; }
            return new CachedValue(bean.Payload);
        }
    }
    public sealed partial class GameBridge
    {
        internal sealed class TransitState
        {
            internal string PlayerId;
            internal readonly List<string> Expeditions = new List<string>();
            internal readonly List<string> Heroes = new List<string>();
            internal readonly List<string> Animals = new List<string>();
        }
        internal readonly Dictionary<string, FWGameData> Players = new Dictionary<string, FWGameData>();
        private readonly HashSet<long> sampledTransitIds = new HashSet<long>();
        private readonly Dictionary<long, NativeWire.CachedValue> transitValues = new Dictionary<long, NativeWire.CachedValue>();

        internal TransitState TestCapture(string playerId) { return CaptureCachedTransit(playerId); }
        internal bool CapturedId(long uid) { return sampledTransitIds.Contains(uid); }
        internal NativeWire.CachedValue Cached(long uid) { return transitValues[uid]; }
        internal int CapturedCount { get { return sampledTransitIds.Count; } }
        internal void BeginFrame() { sampledTransitIds.Clear(); }
    }
}
