using System;

namespace FreeWorldMultiplayer
{
    // Narrow domain stubs only; TCP framing/transport/queue/policy compile
    // unchanged from real production sources.
    public enum TransportEventKind { Connected, Disconnected, Message, Error }
    public sealed class TransportEvent
    {
        public TransportEventKind Kind;
        public string PeerId;
        public byte[] Data;
        public string Error;
    }
    public sealed class TransportOptions
    {
        public string Address = "127.0.0.1";
        public int Port;
        public int MaxPlayers = 4;
    }
    public interface ITransport : IDisposable
    {
        string Name { get; }
        string LocalPeerId { get; }
        bool IsRunning { get; }
        void StartHost(TransportOptions options);
        void Connect(TransportOptions options);
        void Send(string peerId, byte[] data);
        void Disconnect(string peerId, string reason);
        bool TryDequeue(out TransportEvent item);
    }
    public interface ITransportDrain
    {
        bool IsDrained { get; }
        void BeginDrain();
    }
}
