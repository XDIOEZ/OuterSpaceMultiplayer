using System;
using System.Reflection;
using HarmonyLib;

namespace FreeWorldMultiplayer
{
    internal static class NativeMapProjectionGuard
    {
        [ThreadStatic] private static int depth;

        internal static void Install(Harmony harmony)
        {
            MethodInfo removePlan = AccessTools.Method(typeof(PlanManager), "removePlan", new[] { typeof(FWPlanBean), typeof(bool) });
            if (removePlan == null) throw new MissingMethodException(typeof(PlanManager).FullName, "removePlan(FWPlanBean, bool)");
            harmony.Patch(removePlan, prefix: new HarmonyMethod(typeof(NativeMapProjectionGuard), nameof(KeepReplicaTasksPassive)));
            foreach (MethodInfo method in typeof(Stub_PlanRemove).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                if (method.ReturnType == typeof(void) && !method.IsGenericMethodDefinition)
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(NativeMapProjectionGuard), nameof(AllowPlanSideEffects)));
        }

        private static bool AllowPlanSideEffects() { return depth == 0; }
        private static void KeepReplicaTasksPassive(ref bool __1)
        {
            // Interrupting a replica's stale AI can return carried materials/create plans.
            // Those persistent effects arrive in the simulator's transaction instead.
            if (depth != 0) __1 = false;
        }

        internal static IDisposable Enter() { return new Scope(); }

        private sealed class Scope : IDisposable
        {
            private bool disposed;
            internal Scope() { depth++; }
            public void Dispose() { if (!disposed) { disposed = true; depth--; } }
        }
    }
}
