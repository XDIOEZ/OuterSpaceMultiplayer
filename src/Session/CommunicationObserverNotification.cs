using System;

namespace FreeWorldMultiplayer
{
    // UI and other optional observers must not control the fate of an already
    // applied chat/visit transition. Business validation and cloning happen
    // BEFORE this boundary; only observer invocation errors are isolated.
    internal static class CommunicationObserverNotification
    {
        internal static void Publish<T>(Action<T> observers, T snapshot, string stage)
        {
            if (observers == null) return;
            // Preserve registration order and the single shared snapshot used
            // by the previous multicast invocation, even under reentrancy.
            foreach (Action<T> observer in observers.GetInvocationList())
            {
                try { observer(snapshot); }
                catch (Exception error)
                {
                    // Never log chat text, player identity, grant or payload.
                    // Diagnostics are optional too and may be unavailable.
                    try
                    {
                        Logger.LogWarningEngine("[FreeWorldMultiplayer][communication-observer] stage="
                            + stage + " exceptionType=" + error.GetType().Name);
                    }
                    catch (Exception) { }
                }
            }
        }
    }
}
