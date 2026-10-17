using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // Execute the native resource preflight before ApplyPlayerBattleEffects
    // mutates any visual object. The original four-stage order was built with
    // Concat/Where/Select, allocating iterator objects on every received
    // checkpoint even after all wire input had passed validation.
    internal static class BattleEffectResourcePreflight
    {
        internal static void Check<TContext, TPose, TEvent>(
            TContext context, List<TPose> projectiles, List<TPose> vfx,
            List<TEvent> events, Func<TEvent, int> kindOf,
            Func<TEvent, TPose> poseOf, Action<TContext, TPose> projectileCheck,
            Action<TContext, TPose> vfxCheck)
        {
            // 1. Existing projectile poses, before any historical event.
            foreach (TPose pose in projectiles)
                projectileCheck(context, pose);

            // 2. All events except kind 3 (both spawn and end use a weapon).
            foreach (TEvent item in events)
                if (kindOf(item) != 3)
                    projectileCheck(context, poseOf(item));

            // 3. Existing visual effects, before kind-3 birth events.
            foreach (TPose pose in vfx)
                vfxCheck(context, pose);

            // 4. Historical VFX-birth events in their original sequence.
            foreach (TEvent item in events)
                if (kindOf(item) == 3)
                    vfxCheck(context, poseOf(item));
        }
    }
}
