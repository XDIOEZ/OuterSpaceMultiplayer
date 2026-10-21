using System;
using System.Threading;

namespace FreeWorldMultiplayer
{
    public sealed partial class MultiplayerRuntime
    {
        private int worldStorageMutation;
        internal bool IsWorldStorageMutating => Interlocked.CompareExchange(ref worldStorageMutation, 0, 0) != 0;
        // Acquired on Unity's main thread before dispatch; releasing from a worker
        // never touches Unity. All connection start paths use the same gate.
        internal IDisposable BeginWorldStorageMutation()
        {
            if (Session != null || IsStarting || pendingStart != null || awaitingClose || awaitingMenuReturn || nativeReturnQueued || quitting || unloading
                || (Transport != null && Transport.IsRunning)
                || Interlocked.CompareExchange(ref worldStorageMutation, 1, 0) != 0)
                throw new InvalidOperationException("当前联机会话尚未退出，或存档操作尚未完成。");
            return new WorldStorageMutationLease(this);
        }
        private void EnsureNoWorldStorageMutation()
        {
            if (IsWorldStorageMutating) throw new InvalidOperationException("正在删除联机存档，请等待完成后再创建或加入房间。");
        }
        private sealed class WorldStorageMutationLease : IDisposable
        {
            private MultiplayerRuntime owner;
            internal WorldStorageMutationLease(MultiplayerRuntime value) { owner = value; }
            public void Dispose()
            {
                MultiplayerRuntime runtime = Interlocked.Exchange(ref owner, null);
                if (!ReferenceEquals(runtime, null)) Interlocked.Exchange(ref runtime.worldStorageMutation, 0);
            }
        }
    }
}
