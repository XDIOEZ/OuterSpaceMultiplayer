using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace FreeWorldMultiplayer
{
    // Native editing temporarily removes/changes committed plans before the UI submits
    // its command. Network baselines must describe the committed plans, not that draft.
    internal static class NativeMapPlanBaseline
    {
        private static readonly FieldInfo NewPlans = RequiredCache("newPlans");
        private static readonly FieldInfo RemovedPlans = RequiredCache("removeOldPlans");

        private static FieldInfo RequiredCache(string name)
        {
            FieldInfo field = typeof(LocalCoreHandle).GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
            if (field == null) throw new MissingFieldException(typeof(LocalCoreHandle).FullName, name);
            return field;
        }

        private static IEnumerable<RolePlan> Cached(FieldInfo field)
        { return ((IEnumerable)field.GetValue(null)).Cast<RolePlan>(); }

        internal static bool HasDraft
        {
            get { return Cached(NewPlans).Any() || Cached(RemovedPlans).Any() || LocalCoreHandle.updateOldPlans.Count != 0; }
        }

        internal static List<FWPlanBean> CommittedPlans(UserFWStarSpread spread)
        {
            if (!HasDraft) return spread.plans;
            var plans = new List<FWPlanBean>(spread.plans);
            var restored = new HashSet<long>();
            foreach (RolePlan removed in Cached(RemovedPlans))
            {
                FWPlanBean original = removed.planBean;
                foreach (var changed in LocalCoreHandle.updateOldPlans)
                    if (ReferenceEquals(changed.Item1, removed)) { original = changed.Item2; break; }
                plans.RemoveAll(plan => plan.getUID() == original.getUID());
                plans.Add(original); restored.Add(original.getUID());
            }
            foreach (var changed in LocalCoreHandle.updateOldPlans)
            {
                FWPlanBean original = changed.Item2;
                if (restored.Contains(original.getUID())) continue;
                plans.RemoveAll(plan => plan.getUID() == original.getUID());
                plans.Add(original);
            }
            return plans;
        }

        internal static string PackCommittedMap(FWStarMapInfo map, bool currentReady)
        {
            if (!currentReady || !HasDraft) return NativeWire.Pack(map);
            List<FWPlanBean> draftPlans = map.starSpread.plans;
            map.starSpread.plans = CommittedPlans(map.starSpread);
            try { return NativeWire.Pack(map); }
            finally { map.starSpread.plans = draftPlans; }
        }

        internal static void ClearDraft()
        {
            // Called only after the whole transaction preflight succeeded. Clearing the
            // native caches now prevents a later UI cancel from resurrecting an old plan.
            if (HasDraft) using (NativeMapProjectionGuard.Enter()) LocalCoreHandle.Plan_CacheClear_LN();
        }
    }
}
