using System;
using System.IO;

namespace FreeWorldMultiplayer
{
    public sealed partial class MultiplayerSession
    {
        // Native map capture can advance its own comparison baseline. Keep the
        // exact serialized delta while its FIRST enqueue is rejected: capturing
        // a new delta here could silently omit the changes that were not sent.
        // Only one unqueued large frame is retained per room at a time.
        private sealed class PendingMapOutbound
        {
            internal string SessionId;
            internal string WorldId;
            internal string PlayerId;
            internal MapUpdate Update;
            internal byte[] Frame;
            internal double RetrySeconds;
            internal double WaitingSeconds;
        }

        private PendingMapOutbound pendingMapOutbound;
        private const double MapOutboundRetrySeconds = 2;
        private const double MapOutboundFailureSeconds = 60;

        private void SendCapturedMapUpdate(MapUpdate update)
        {
            if (pendingMapOutbound != null && !StillPendingMap(pendingMapOutbound))
                ClearMapOutbound(); // A synchronous host view/ACK replaced the old lease.
            if (update == null || update.Checkpoint == null || IsHost || !IsConnected
                || World == null || LocalPlayerId == null || update.SessionId != SessionId
                || update.Checkpoint.MapId != update.MapId || pendingMapOutbound != null
                || inFlightUpdates.ContainsKey(update.MapId))
                throw new InvalidOperationException("地图差量出站租约与当前房间/检查点不一致。");

            var pending = new PendingMapOutbound
            {
                SessionId = SessionId, WorldId = World.WorldId,
                PlayerId = LocalPlayerId, Update = update
            };
            // Register before encoding/transport can synchronously reenter
            // ACK, reset or an entirely new transaction for the same map.
            inFlightUpdates[update.MapId] = update;
            pendingMapOutbound = pending;
            try { pending.Frame = EncodeMessage("map", WireMapUpdate(update), null); }
            catch (Exception error)
            {
                // A failed encoding does NOT restore the bridge's already
                // advanced native comparison baseline. Keep the in-flight
                // update and fail closed instead of silently re-capturing it.
                LockMapOutboundFault("map.encode", error);
                throw;
            }
            TryEnqueueCapturedMap(pending);
        }

        private bool StillPendingMap(PendingMapOutbound expected)
        {
            MapUpdate current;
            return expected != null && ReferenceEquals(expected, pendingMapOutbound)
                && inFlightUpdates.TryGetValue(expected.Update.MapId, out current)
                && ReferenceEquals(current, expected.Update);
        }

        private void TryEnqueueCapturedMap(PendingMapOutbound expected)
        {
            if (!StillPendingMap(expected) || expected.Frame == null || nativeFault != null) return;
            try
            {
                // Production TCP/Steam reject a closed/full outgoing FIFO
                // before cloning/enqueuing this byte array.
                transport.Send("host", expected.Frame);
                if (StillPendingMap(expected))
                {
                    // Transport accepted the frame, NOT the host authority.
                    // The original MapUpdate/checkpoint stays in inFlightUpdates
                    // until a matching acknowledgement or full host view.
                    pendingMapOutbound = null;
                    expected.Frame = null;
                }
            }
            catch (IOException)
            {
                if (StillPendingMap(expected)) expected.RetrySeconds = MapOutboundRetrySeconds;
            }
            catch (InvalidOperationException)
            {
                if (StillPendingMap(expected)) expected.RetrySeconds = MapOutboundRetrySeconds;
            }
            catch (Exception error)
            {
                // An exception outside the documented enqueue-before-mutation
                // failures has uncertain send status: NEVER auto-replay it.
                if (StillPendingMap(expected))
                {
                    expected.Frame = null;
                    LockMapOutboundFault("map.enqueue.uncertain", error);
                }
                throw;
            }
        }

        private void TickMapOutbound(double seconds)
        {
            PendingMapOutbound expected = pendingMapOutbound;
            if (expected == null) return;
            if (!StillPendingMap(expected))
            {
                // An ACK, full authoritative view or reset already consumed
                // this lease; never replay it over a newer transaction.
                if (ReferenceEquals(expected, pendingMapOutbound)) pendingMapOutbound = null;
                return;
            }
            if (nativeFault != null || !IsConnected || IsHost || closing || !transport.IsRunning) return;
            MapRecord map;
            if (World == null || expected.SessionId != SessionId
                || expected.WorldId != World.WorldId || expected.PlayerId != LocalPlayerId
                || !World.Maps.TryGetValue(expected.Update.MapId, out map)
                || map.SimulatorPlayerId != LocalPlayerId
                || map.AwaitingAuthorityReady || map.AuthorityEpoch != expected.Update.AuthorityEpoch
                || map.Revision != expected.Update.BaseRevision)
            {
                // No longer the same simulator/authority revision; only a
                // genuine host view/reset can reconcile the native baseline.
                LockMapOutboundFault("map.stale-authority", new InvalidOperationException());
                return;
            }
            expected.WaitingSeconds += seconds;
            expected.RetrySeconds -= seconds;
            if (expected.RetrySeconds <= 0) TryEnqueueCapturedMap(expected);
            if (StillPendingMap(expected) && expected.WaitingSeconds >= MapOutboundFailureSeconds
                && nativeFault == null)
                LockMapOutboundFault("map.enqueue.timeout", new IOException());
        }

        private void RetireMapOutbound(MapUpdate acknowledged)
        {
            PendingMapOutbound expected = pendingMapOutbound;
            if (expected != null && ReferenceEquals(expected.Update, acknowledged))
            {
                expected.Frame = null;
                pendingMapOutbound = null;
            }
        }

        private void ClearMapOutbound()
        {
            if (pendingMapOutbound != null) pendingMapOutbound.Frame = null;
            pendingMapOutbound = null;
        }

        private void LockMapOutboundFault(string stage, Exception error)
        {
            string reason = "地图差量出站无法安全确认，保留检查点并暂停本机提交；阶段="
                + stage + "；异常类型=" + error.GetType().Name;
            try { LockNativeFault(reason); }
            catch (Exception)
            {
                // LockNativeFault sets the barrier before a potentially
                // failing StatusChanged/clock notification.
                nativeFault = reason;
                Paused = true;
            }
        }
    }
}
