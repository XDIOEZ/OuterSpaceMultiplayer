using System;
using System.Collections.Generic;

namespace FreeWorldMultiplayer
{
    // The old duplicate check allocated a new "shipUid:slotId" string for
    // every weapon in each received battle/fighter frame. A typed key keeps
    // the exact ship UID and ordinal slot identity without constructing text.
    // Hash collisions still use the full 64-bit UID and ordinal string in Equals.
    internal struct BattleWeaponSlotKey : IEquatable<BattleWeaponSlotKey>
    {
        internal readonly long Ship;
        internal readonly string Slot;

        internal BattleWeaponSlotKey(long ship, string slot)
        { Ship = ship; Slot = slot; }

        public bool Equals(BattleWeaponSlotKey other)
        {
            return Ship == other.Ship && String.Equals(Slot, other.Slot, StringComparison.Ordinal);
        }
        public override bool Equals(object obj)
        { return obj is BattleWeaponSlotKey && Equals((BattleWeaponSlotKey)obj); }
        public override int GetHashCode()
        {
            unchecked
            {
                return ((int)(Ship ^ (Ship >> 32)) * 397) ^
                    (Slot == null ? 0 : StringComparer.Ordinal.GetHashCode(Slot));
            }
        }
    }

    public sealed partial class GameBridge
    {
        // Both a complete battle-effects frame and each native fighter snapshot
        // use this exact weapon/beam contract. A fighter has no projectile, VFX
        // or event fields: constructing BattleEffectsState merely to validate
        // its weapon list created four empty lists and unrelated validators for
        // every fighter in every checkpoint.
        private static void ValidateBattleWeaponEffects(List<BattleWeaponEffect> weapons)
        {
            if (weapons == null || weapons.Count > 8192)
                throw new InvalidOperationException("玩家舰战表现快照结构或数量无效。");
            if (weapons.Count == 0) return;
            // A single weapon cannot collide with a different weapon's slot;
            // do not allocate a HashSet or construct its comparison key.
            var slots = weapons.Count > 1 ? new HashSet<BattleWeaponSlotKey>() : null;
            foreach (BattleWeaponEffect value in weapons)
            {
                if (value == null || value.Beams == null || value.Beams.Count > 128 || String.IsNullOrEmpty(value.Slot) || value.Slot.Length > 128
                    || String.IsNullOrEmpty(value.Resource) || value.Resource.Length > 128 || float.IsNaN(value.Angle) || float.IsInfinity(value.Angle)
                    || slots != null && !slots.Add(new BattleWeaponSlotKey(value.Ship, value.Slot)))
                    throw new InvalidOperationException("舰战武器表现字段或插槽身份无效。");
                foreach (BattleBeamLine line in value.Beams)
                    if (line == null || !BattleEffectFiniteFields.ValidBeam(line.X, line.Y, line.Z,
                        line.EndX, line.EndY, line.EndZ, line.Width, line.EndWidth))
                        throw new InvalidOperationException("舰战光束端点或宽度无效。");
            }
        }
    }
}
