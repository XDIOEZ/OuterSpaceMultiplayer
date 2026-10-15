using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // The only crossing between socket workers and the game main thread.
    internal sealed class TransportQueue
    {
        public const int MaxMessageBytes = 128 * 1024 * 1024;
        public const long MaxQueuedBytes = 256L * 1024 * 1024;
        private readonly object gate = new object();
        private readonly Queue<TransportEvent> events = new Queue<TransportEvent>();
        private int terminalCount;
        private bool terminalOverflow;
        private long queuedBytes;
        private long reservedBytes;

        public bool TryReserve(int length)
        {
            lock (gate)
            {
                if (length <= 0 || length > MaxMessageBytes || queuedBytes + reservedBytes + length > MaxQueuedBytes) return false;
                reservedBytes += length; return true;
            }
        }
        public void ReleaseReservation(int length) { lock (gate) reservedBytes -= length; }
        public bool EnqueueReserved(TransportEvent item)
        {
            lock (gate)
            {
                reservedBytes -= item.Data.Length;
                return Enqueue(item);
            }
        }

        public bool Enqueue(TransportEvent item)
        {
            lock (gate)
            {
                if (item.Kind == TransportEventKind.Disconnected || item.Kind == TransportEventKind.Error)
                {
                    // Preserve FIFO: successfully received final checkpoints precede a later FIN.
                    // Reserve control slots beyond the normal queue limit so overflow is never silent.
                    if (terminalCount < 256) { events.Enqueue(item); terminalCount++; }
                    else if (!terminalOverflow)
                    {
                        terminalOverflow = true;
                        events.Enqueue(new TransportEvent { Kind = TransportEventKind.Error, Error = "传输层终止事件过多，必须结束房间。" }); terminalCount++;
                    }
                    return true;
                }
                long length = item.Data == null ? 0 : item.Data.Length;
                if (events.Count >= 4096 || length > MaxMessageBytes || queuedBytes + reservedBytes + length > MaxQueuedBytes) return false;
                events.Enqueue(item);
                queuedBytes += length;
                return true;
            }
        }

        public bool TryDequeue(out TransportEvent item)
        {
            lock (gate)
            {
                if (events.Count == 0) { item = null; return false; }
                item = events.Dequeue();
                if (item.Kind == TransportEventKind.Disconnected || item.Kind == TransportEventKind.Error) terminalCount--;
                queuedBytes -= item.Data == null ? 0 : item.Data.Length;
                return true;
            }
        }

        public void Clear() { lock (gate) { events.Clear(); terminalCount = 0; terminalOverflow = false; queuedBytes = 0; } }
    }
}
