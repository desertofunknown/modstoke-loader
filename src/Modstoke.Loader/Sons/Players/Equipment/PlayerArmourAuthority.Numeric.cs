// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class PlayerArmourAuthority
    {
        internal static Calculation Calculate(PlayerArmourAuthority owner, ulong transaction,
            ulong revision, ulong inventoryTransaction, State before, float damage, bool ignoreArmour, bool demonic)
        {
            float incoming = damage;
            bool golden = !ignoreArmour && before.WearingGoldenClothing;
            if (golden)
                damage = (demonic ? before.GoldenDemonicMultiplier : before.GoldenGeneralMultiplier) * damage;
            if (ignoreArmour || golden || !float.IsFinite(damage))
                return new(owner, transaction, revision, inventoryTransaction, before, before, incoming, damage, golden,
                    Array.Empty<SlotChange>(), ignoreArmour, demonic);

            var slots = before.CopySlots();
            var changes = new List<SlotChange>();
            int next = before.NextAvailableOrderIndex;
            // Native GetTopmostArmourSlotData scans backward on every exhaustion. Unique
            // configured slots let this scan advance backward without changing that result.
            for (int orderIndex = before.OrderCount - 1; orderIndex >= 0 && float.IsFinite(damage); orderIndex--)
            {
                int index = before.GetOrderSlot(orderIndex);
                var slot = slots[index];
                if (!slot.IsEquipped) continue;
                float points = float.IsFinite(slot.RemainingPoints) ? slot.RemainingPoints : 0f;
                float absorbed = damage > points ? points : damage;
                damage -= absorbed;
                bool broken = absorbed >= points;
                // TryUnequipSlot clears the definition, but leaves the old condition behind.
                var after = broken ? new Slot(index, 0, 0, slot.RemainingPoints)
                    : slot with { RemainingPoints = slot.RemainingPoints - absorbed };
                slots[index] = after;
                changes.Add(new(orderIndex, slot, after, absorbed, broken));
                if (!broken) break;
                next = orderIndex;
                // Do not stop at damage==0: the next zero-point piece also breaks, and a
                // positive-point piece receives the native subtract-zero partial write.
            }
            var result = changes.Count == 0 ? before : new State(slots, before.CopyOrder(), next,
                before.WearingGoldenClothing, before.GoldenGeneralMultiplier, before.GoldenDemonicMultiplier);
            return new(owner, transaction, revision, inventoryTransaction, before, result, incoming, damage, false,
                changes.ToArray(), ignoreArmour, demonic);
        }

        /// <summary>
        /// OnNetworkHit arithmetic only. Resolve the living/dead entry branch first; this is
        /// not an admission check. The integer low-health protection precedes block scaling.
        /// </summary>
        internal static int CalculateNetworkDamage(int incoming, float health, int lowHealthThreshold,
            bool blocked, float nativeBlockDamageMultiplier)
        {
            int damage = incoming;
            if (!(lowHealthThreshold >= health) && damage > health)
            {
                int maximum = unchecked(NativeTruncate(MathF.Floor(health)) - 1);
                damage = damage < 1 ? 1 : damage > maximum ? maximum : damage;
            }
            return blocked ? NativeTruncate(damage * nativeBlockDamageMultiplier) : damage;
        }

        /// <summary>
        /// OnNetworkHit's owner-side block predicate, using the consumer's exact actor pose
        /// and PlayerStats._damageController._isBlocking. No transform/LocalPlayer is read here.
        /// Projectile1 and explosion3 are never blocked by this receiver branch.
        /// </summary>
        internal static bool CalculateNetworkBlock(bool damageControllerBlocking, global::ImpactType impactType,
            Vector3 actorPosition, Vector3 actorForward, Vector3 attackerPosition)
        {
            if (!damageControllerBlocking || (int)impactType is 1 or 3) return false;
            // 0x1853F0F84, native Vector3 equality-to-zero threshold, strict comparison.
            float squared = attackerPosition.y * attackerPosition.y + attackerPosition.x * attackerPosition.x;
            squared += attackerPosition.z * attackerPosition.z;
            if (squared < 9.9999994E-11f) return true;
            float dot = actorForward.y * (attackerPosition.y - actorPosition.y)
                + actorForward.x * (attackerPosition.x - actorPosition.x);
            dot += actorForward.z * (attackerPosition.z - actorPosition.z);
            return dot > 0f;
        }

        internal readonly record struct HealthNumbers(float Current, float Target);

        /// <summary>
        /// The Hit post-armour gate and Vitals.ApplyDamage numeric write. No CheckDeath,
        /// adrenaline, hurt feedback, forced drop or survival revision is executed here.
        /// </summary>
        internal static HealthNumbers CalculateHitHealth(float remainingDamage, float health,
            float targetHealth, float minimum, float maximum, float sickness)
            => remainingDamage > 0f ? CalculateVitalsDamage(remainingDamage, health, minimum, maximum, sickness)
                : new(health, targetHealth);

        internal static HealthNumbers CalculateVitalsDamage(float damage, float health,
            float minimum, float maximum, float sickness)
        {
            // Native Add skips NaN damage, but ApplyDamage still updates targetHealth.
            if (!float.IsNaN(damage)) health = NativeClamp(health + -damage, minimum, maximum);
            float cap = maximum - sickness;
            // MINSS selects its second operand on equality or unordered comparison.
            return new(health, health < cap ? health : cap);
        }

        internal readonly record struct BlockStaminaNumbers(float Stamina, float ActionTime);

        /// <summary>
        /// ONLY after the consumer proves HandleBlockedImpact reached its stamina branch.
        /// OnNetworkHit passes impactWeight=1, not the incoming hit's weight. The native
        /// handler debits -(1+12) and updates PlayerStats._staminaActionTime even at zero.
        /// Animator/audio/noise/controller predicates and their effects are not supplied here.
        /// </summary>
        internal static BlockStaminaNumbers CalculateNetworkBlockedStamina(float stamina,
            float minimum, float maximum, float gameTime)
            => new(NativeClamp(stamina + -13f, minimum, maximum), gameTime);

        private static float NativeClamp(float value, float minimum, float maximum)
            => minimum > value ? minimum : value > maximum ? maximum : value;

        // CVTTSS2SI's invalid/overflow result differs from managed saturating conversions.
        private static int NativeTruncate(float value)
            => !float.IsFinite(value) || value < -2147483648f || value >= 2147483648f
                ? int.MinValue : (int)value;
    }
}
