using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // Minimal test-only surrounding session/transport definitions. The expiration
    // implementation itself is the production src/Session/MapCacheExpiry.cs.
    public sealed partial class MultiplayerSession
    {
        private sealed class OfferedMapView
        {
            internal double Elapsed;
            internal long RetainedBytes;
            internal string Player;
        }
        private sealed class WaitingView { internal long MapId; }
        private readonly Dictionary<string, OfferedMapView> offeredMapViews = new Dictionary<string, OfferedMapView>();
        private readonly Dictionary<string, WaitingView> waitingMapCaches = new Dictionary<string, WaitingView>();
        private readonly Dictionary<string, double> waitingMapCacheSeconds = new Dictionary<string, double>();
        private readonly Dictionary<string, string> playerPeers = new Dictionary<string, string>();
        private readonly CacheExpiryTestTransport transport = new CacheExpiryTestTransport();
        private string mapCacheWarning;
        private bool mapCacheWarningReported;
        private long retainedMapViewBytes;
        internal event Action<string> MessageFaulted;
        internal Action<string, string, object> SendHandler;

        private void Send(string peer, string kind, object payload) { SendHandler?.Invoke(peer, kind, payload); }
        private void FailMapLoading(Exception fault) { throw fault; }

        internal void SeedOffer(string key, string player, double elapsed, long bytes)
        {
            offeredMapViews[key] = new OfferedMapView { Player = player, Elapsed = elapsed, RetainedBytes = bytes };
            retainedMapViewBytes += bytes;
        }
        internal void SeedPeer(string player, string peer) { playerPeers[player] = peer; }
        internal void SeedWaiting(string key, long mapId, double elapsed)
        {
            waitingMapCaches[key] = new WaitingView { MapId = mapId };
            waitingMapCacheSeconds[key] = elapsed;
        }
        internal void SeedWarning(string warning) { mapCacheWarning = warning; }
        internal void OnDisconnect(Action<string, string> handler) { transport.DisconnectHandler = handler; }
        internal void AdvanceCache(double seconds) { TickMapCache(seconds); }
        internal int PendingOffers => offeredMapViews.Count;
        internal long RetainedBytes => retainedMapViewBytes;
        internal int PendingWaits => waitingMapCaches.Count;
    }

    internal sealed class CacheExpiryTestTransport
    {
        internal Action<string, string> DisconnectHandler;
        internal void Disconnect(string peer, string reason) { DisconnectHandler?.Invoke(peer, reason); }
    }
}
