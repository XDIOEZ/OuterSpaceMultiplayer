using System;
using System.IO;

namespace FreeWorldMultiplayer
{
    public sealed partial class MultiplayerSession
    {
        // CapturePlayerUpdates advances the native comparison baseline before
        // the frame is sent. Never ask the bridge to re-capture a rejected send:
        // hold the original encoded delta until transport accepts that exact frame.
        private sealed class PendingPersonalOutbound
        {
            internal string SessionId;
            internal PlayerUpdate Update;
            internal byte[] Frame;
            internal bool Enqueued;
            internal double RetrySeconds;
            internal double WaitingSeconds;
        }

        private PendingPersonalOutbound pendingPersonalOutbound;
        private const double PersonalOutboundRetrySeconds = 2;

        private void SendCapturedPersonalUpdate(PlayerUpdate update)
        {
            if (update == null || update.PlayerId != LocalPlayerId || IsHost
                || !IsConnected || World == null || pendingPersonalOutbound != null
                || personalInFlight.Count != 0)
                throw new InvalidOperationException("个人进度出站租约与当前玩家或房间状态不一致。");

            // Register before encoding or transport callbacks can reenter.
            var pending = new PendingPersonalOutbound { SessionId = SessionId, Update = update };
            pendingPersonalOutbound = pending;
            personalInFlight.Add(update.PlayerId);
            try { pending.Frame = EncodeMessage("personalUpdate", update, null); }
            catch (Exception error)
            {
                // Capture already advanced its native baseline. A serialization
                // error cannot silently discard the only captured delta.
                string reason = "个人进度差量编码失败，保留当前现场；异常类型=" + error.GetType().Name;
                try { LockNativeFault(reason); }
                catch (Exception) { nativeFault = reason; Paused = true; }
                throw;
            }
            TryEnqueueCapturedPersonal(pending);
        }

        private void TryEnqueueCapturedPersonal(PendingPersonalOutbound expected)
        {
            if (expected == null || !ReferenceEquals(expected, pendingPersonalOutbound)
                || expected.Enqueued || expected.Frame == null) return;
            try
            {
                transport.Send("host", expected.Frame);
                if (ReferenceEquals(expected, pendingPersonalOutbound)) expected.Enqueued = true;
            }
            catch (IOException)
            {
                if (ReferenceEquals(expected, pendingPersonalOutbound))
                    expected.RetrySeconds = PersonalOutboundRetrySeconds;
            }
            catch (InvalidOperationException)
            {
                // TCP/Steam explicitly reject a closed peer or full send queue
                // before retaining the frame. Keep the same delta and lease.
                if (ReferenceEquals(expected, pendingPersonalOutbound))
                    expected.RetrySeconds = PersonalOutboundRetrySeconds;
            }
        }

        private void TickPersonalOutbound(double seconds)
        {
            PendingPersonalOutbound expected = pendingPersonalOutbound;
            if (expected == null || expected.Enqueued || nativeFault != null
                || !IsConnected || IsHost || closing || !transport.IsRunning) return;
            // A frame from an older room must never be sent to the next host.
            if (expected.SessionId != SessionId || expected.Update.PlayerId != LocalPlayerId) return;
            expected.WaitingSeconds += seconds;
            expected.RetrySeconds -= seconds;
            if (expected.RetrySeconds <= 0) TryEnqueueCapturedPersonal(expected);
            if (ReferenceEquals(expected, pendingPersonalOutbound) && !expected.Enqueued
                && expected.WaitingSeconds >= 60 && nativeFault == null)
            {
                string reason = "个人进度差量连续 60 秒未能进入发送队列；保留现场，需重连核对房主状态。";
                try { LockNativeFault(reason); }
                catch (Exception) { nativeFault = reason; Paused = true; }
            }
        }

        private void ReceivePersonalAcknowledgement(PlayerUpdate acknowledgement)
        {
            if (acknowledgement == null || acknowledgement.PlayerId != LocalPlayerId)
                throw new MessageRejectionException("个人进度回执必须属于当前本机玩家。");
            PendingPersonalOutbound expected = pendingPersonalOutbound;
            if (expected == null || expected.SessionId != SessionId
                || expected.Update.PlayerId != acknowledgement.PlayerId
                || expected.Update.BaseRevision != acknowledgement.BaseRevision
                || expected.Update.Revision != acknowledgement.Revision)
                return; // An old or duplicate ACK cannot clear a newer in-flight delta.
            PlayerRecord player;
            if (!World.Players.TryGetValue(LocalPlayerId, out player))
                throw new InvalidDataException("个人进度回执缺少本地玩家记录。");
            if (player.PersonalRevision < acknowledgement.Revision)
                player.PersonalRevision = acknowledgement.Revision;
            pendingPersonalOutbound = null;
            personalInFlight.Remove(acknowledgement.PlayerId);
        }

        private void ClearPersonalOutbound()
        {
            pendingPersonalOutbound = null;
            personalInFlight.Clear();
        }

        private void CompletePersonalReset(PlayerFieldState reset)
        {
            if (reset == null || reset.PlayerId != LocalPlayerId)
                throw new MessageRejectionException("个人进度恢复基准不属于当前本机玩家。");
            // The native apply must succeed before abandoning its old captured
            // delta. Do not release this gate for a failed half-applied reset.
            Require(bridge.ApplyPlayerFields(reset), "重新同步个人进度白名单基准");
            World.Players[reset.PlayerId].PersonalRevision = reset.PersonalRevision;
            ClearPersonalOutbound();
        }
    }
}
