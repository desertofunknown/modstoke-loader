// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using global::Sons.Gameplay.GameSetup;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using global::Sons.StatSystem;
using UnityEngine;

namespace Modstoke.Sons
{
    internal readonly record struct ConsumptionNumbers(float Hydration, float Fullness, float Rest,
        float HydrationBuff, float FullnessBuff, float RestBuff, float CachedHydration, float CachedFullness, float CachedRest,
        float TargetHealth, float Sickness, float Stamina, int CookingRecipeId, float CookingSeconds,
        float Stealth, float TreeChopBoost, float StrengthBoost, float Comfort, float WaterResistance,
        float ThermalRating, float SoundRangeDampFactor)
    {
        internal const int WireLength = 84;
        internal bool IsFinite => float.IsFinite(Hydration) && float.IsFinite(Fullness) && float.IsFinite(Rest)
            && float.IsFinite(HydrationBuff) && float.IsFinite(FullnessBuff) && float.IsFinite(RestBuff)
            && float.IsFinite(CachedHydration) && float.IsFinite(CachedFullness) && float.IsFinite(CachedRest)
            && float.IsFinite(TargetHealth) && float.IsFinite(Sickness) && float.IsFinite(Stamina)
            && float.IsFinite(CookingSeconds) && float.IsFinite(Stealth) && float.IsFinite(TreeChopBoost)
            && float.IsFinite(StrengthBoost) && float.IsFinite(Comfort) && float.IsFinite(WaterResistance)
            && float.IsFinite(ThermalRating) && float.IsFinite(SoundRangeDampFactor);
        internal static ConsumptionNumbers From(ConsumptionVitals s)
            => new(s.Hydration, s.Fullness, s.Rest, s.HydrationBuff, s.FullnessBuff, s.RestBuff,
                s.CachedHydration, s.CachedFullness, s.CachedRest, s.TargetHealth, s.Sickness, s.Stamina,
                s.CookingRecipeId, s.CookingSeconds, s.Stealth, s.TreeChopBoost, s.StrengthBoost,
                s.Comfort, s.WaterResistance, s.ThermalRating, s.SoundRangeDampFactor);

        internal void Write(Span<byte> bytes)
        {
            if (bytes.Length != WireLength) throw new ArgumentException("An exact consumption snapshot is required.", nameof(bytes));
            ReadOnlySpan<float> values = stackalloc float[] { Hydration, Fullness, Rest, HydrationBuff,
                FullnessBuff, RestBuff, CachedHydration, CachedFullness, CachedRest, TargetHealth,
                Sickness, Stamina, CookingSeconds, Stealth, TreeChopBoost, StrengthBoost, Comfort,
                WaterResistance, ThermalRating, SoundRangeDampFactor };
            for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes[(i * 4)..], values[i]);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[80..], CookingRecipeId);
        }

        internal static ConsumptionNumbers Read(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length != WireLength) throw new ArgumentException("An exact consumption snapshot is required.", nameof(bytes));
            Span<float> values = stackalloc float[20];
            for (int i = 0; i < values.Length; i++) values[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes[(i * 4)..]);
            return new(values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7], values[8],
                values[9], values[10], values[11], BinaryPrimitives.ReadInt32LittleEndian(bytes[80..]), values[12],
                values[13], values[14], values[15], values[16], values[17], values[18], values[19]);
        }
    }

    public readonly record struct ConsumptionVitals(float TargetHealth, float Sickness,
        float Stamina, float Hydration, float Fullness, float Rest, float HydrationBuff,
        float FullnessBuff, float RestBuff, float CachedHydration, float CachedFullness,
        float CachedRest, int CookingRecipeId, float CookingSeconds, float Stealth,
        float TreeChopBoost, float StrengthBoost, float Comfort, float WaterResistance,
        float ThermalRating, float SoundRangeDampFactor);

    internal readonly record struct ConsumptionDebit(int BeforeQuantity, int AfterQuantity, int HeldSlot,
        bool UsesVolume, float Portion, float MaximumVolume, float BeforeVolume, float AfterVolume, int BeforeRecipeId, int AfterRecipeId,
        int PerishableState, string BeforeModuleFingerprint)
    {
        internal const int WireLength = 76;

        internal void Write(Span<byte> bytes)
        {
            if (bytes.Length != WireLength) throw new ArgumentException("An exact consumption debit is required.", nameof(bytes));
            bytes.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(bytes, BeforeQuantity);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], AfterQuantity);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[8..], HeldSlot);
            bytes[12] = (byte)((UsesVolume ? 1 : 0) | (BeforeModuleFingerprint != null ? 2 : 0));
            BinaryPrimitives.WriteSingleLittleEndian(bytes[16..], Portion);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[20..], MaximumVolume);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[24..], BeforeVolume);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[28..], AfterVolume);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[32..], BeforeRecipeId);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[36..], AfterRecipeId);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[40..], PerishableState);
            if (BeforeModuleFingerprint != null)
            {
                if (BeforeModuleFingerprint.Length != 64) throw new InvalidOperationException("Invalid consumption module fingerprint.");
                Convert.FromHexString(BeforeModuleFingerprint).CopyTo(bytes[44..]);
            }
        }

        internal static bool TryRead(ReadOnlySpan<byte> bytes, out ConsumptionDebit debit)
        {
            debit = default;
            if (bytes.Length != WireLength || bytes[12] > 3 || bytes[13] != 0 || bytes[14] != 0 || bytes[15] != 0) return false;
            bool modules = (bytes[12] & 2) != 0;
            if (!modules)
                foreach (byte value in bytes[44..]) if (value != 0) return false;
            debit = new(BinaryPrimitives.ReadInt32LittleEndian(bytes), BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]),
                BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]), (bytes[12] & 1) != 0,
                BinaryPrimitives.ReadSingleLittleEndian(bytes[16..]), BinaryPrimitives.ReadSingleLittleEndian(bytes[20..]),
                BinaryPrimitives.ReadSingleLittleEndian(bytes[24..]), BinaryPrimitives.ReadSingleLittleEndian(bytes[28..]),
                BinaryPrimitives.ReadInt32LittleEndian(bytes[32..]), BinaryPrimitives.ReadInt32LittleEndian(bytes[36..]),
                BinaryPrimitives.ReadInt32LittleEndian(bytes[40..]), modules ? Convert.ToHexString(bytes[44..]) : null);
            // Requests and pre-reservation refusals carry no approved debit yet.
            return debit == default || debit.IsValid;
        }

        internal bool IsValid => BeforeQuantity > 0 && HeldSlot >= -1
            && float.IsFinite(Portion) && float.IsFinite(MaximumVolume) && float.IsFinite(BeforeVolume) && float.IsFinite(AfterVolume)
            && (PerishableState == -1 || Enum.IsDefined(typeof(PerishableItemData.State), PerishableState))
            && (BeforeModuleFingerprint == null || BeforeModuleFingerprint.Length == 64)
            && (UsesVolume
                ? BeforeModuleFingerprint != null && BeforeQuantity == 1 && AfterQuantity == BeforeQuantity
                    && PerishableState == -1 && MaximumVolume > 0 && BeforeVolume > 0 && BeforeVolume <= MaximumVolume
                    && Portion > 0 && Portion <= MaximumVolume && AfterVolume < BeforeVolume
                    && AfterVolume == Mathf.Max(BeforeVolume - Portion, 0f)
                : HeldSlot == -1 && AfterQuantity == BeforeQuantity - 1 && Portion == 0 && MaximumVolume == 0 && BeforeVolume == 0
                    && AfterVolume == 0 && BeforeRecipeId == -1 && AfterRecipeId == -1);
    }

    public sealed partial class InventoryConsumption
    {
        private const int MaximumEffects = 64;

        internal static bool SupportsPlainIntake(ItemData data)
        {
            if (data == null || data.WasCollected || !data || data._eachInstanceIsUnique || data._canNotBeStored
                || data._isVolumeContainer || data._isPerishable || (data._type & global::Sons.Items.Core.Types.Edible) == 0
                || data._usedStatEffect == null || data._usedStatEffect.WasCollected
                || data._usedStatEffect.Length is <= 0 or > MaximumEffects) return false;
            try
            {
                foreach (var effect in data._usedStatEffect)
                {
                    var value = ReadEffect(effect);
                    if (value.Linked) return false;
                    ValidateEffect(value);
                }
                return true;
            }
            catch (InvalidOperationException) { return false; }
        }

        internal static bool TryReadIntake(PlayerStats stats, out ConsumptionNumbers numbers)
        {
            numbers = default;
            if (stats == null || stats.WasCollected || !stats || stats.enabled || stats._playerIsDowned
                || stats._dyingState != PlayerStats.DyingState.Alive) return false;
            var v = stats._vitals;
            if (v == null || v.WasCollected || !v || v.enabled || v._hasDied
                || !SameStat(v._health, v._health) || v._health._currentValue <= 0
                || !SameStat(v._sickness, v._sickness) || !SameStat(v._stamina, v._stamina)
                || !SameStat(v._vitality, v._vitality)
                || !SameStat(v._hydration, v._hydration) || !SameStat(v._fullness, v._fullness)
                || !SameStat(v._rested, v._rested) || Combat.StrictCombatLifeHooks.HasConsumptionHealthOwner(v)) return false;
            var recipe = v._activeCookingBuffRecipe;
            if (recipe is not null && (recipe.WasCollected || !recipe || !recipe._isACookingBuff)) return false;
            numbers = ReadProjectionNumbers(stats);
            return numbers.IsFinite;
        }

        // The stat stream separately validates exact actor/stat lifetimes and supports life
        // transitions. Unlike intake admission, reading authoritative numbers need not be alive.
        internal static ConsumptionNumbers ReadProjectionNumbers(PlayerStats stats)
        {
            var v = stats._vitals;
            var recipe = v._activeCookingBuffRecipe;
            if (recipe is not null && (recipe.WasCollected || !recipe || !recipe._isACookingBuff))
                throw new InvalidOperationException("The projected cooking recipe is unavailable.");
            return new(v._hydration._currentValue, v._fullness._currentValue, v._rested._currentValue,
                v._currentHydrationBuffFactor, v._currentFullnessBuffFactor, v._currentRestedBuffFactor,
                v._lastCachedHydrationFactor, v._lastCachedFullnessFactor, v._lastCachedRestedFactor,
                v._targetHealth, v._sickness._currentValue, v._stamina._currentValue,
                recipe is null ? -1 : recipe.Id, v._remainingCookingBuffTime, stats._stealthRating,
                stats._treeCutDamageMultiplier, v._strengthBoostModifier, stats._comfortRating,
                stats._waterResistRating, stats._thermalRating, stats.SoundRangeDampFactor);
        }

        internal static bool CanProjectIntake(PlayerStats stats, ConsumptionNumbers before, ConsumptionNumbers after)
        {
            if (!before.IsFinite || !after.IsFinite || !TryReadIntake(stats, out var current) || current != before) return false;
            var v = stats._vitals;
            return Clamp(v._hydration, after.Hydration) == after.Hydration && Clamp(v._fullness, after.Fullness) == after.Fullness
                && Clamp(v._rested, after.Rest) == after.Rest && Clamp(v._sickness, after.Sickness) == after.Sickness
                && Clamp(v._stamina, after.Stamina) == after.Stamina
                && after.TargetHealth >= 0 && after.TargetHealth <= v._health._max
                && TryResolveCookingRecipe(v, after.CookingRecipeId, out _);
        }

        // Called only for an authenticated server projection or the server's own prepared plan.
        internal static bool TryProjectIntake(PlayerStats stats, ConsumptionNumbers before, ConsumptionNumbers after)
        {
            if (!CanProjectIntake(stats, before, after)) return false;
            return TryWriteProjectionNumbers(stats, after) && TryReadIntake(stats, out var current) && current == after;
        }

        internal static bool CanResolveProjectionRecipe(PlayerStats stats, int recipeId)
            => TryResolveCookingRecipe(stats._vitals, recipeId, out _);

        internal static bool TryWriteProjectionNumbers(PlayerStats stats, ConsumptionNumbers after)
        {
            if (!after.IsFinite) return false;
            var v = stats._vitals;
            if (!TryResolveCookingRecipe(v, after.CookingRecipeId, out var recipe)) return false;
            // Native Hydrate/GainNutrients/GainRest write these exact scalar, buff and cached
            // factor fields. HUD readers keep the original objects; never replay their deltas.
            v._hydration._currentValue = after.Hydration;
            v._fullness._currentValue = after.Fullness;
            v._rested._currentValue = after.Rest;
            v._currentHydrationBuffFactor = after.HydrationBuff;
            v._currentFullnessBuffFactor = after.FullnessBuff;
            v._currentRestedBuffFactor = after.RestBuff;
            v._lastCachedHydrationFactor = after.CachedHydration;
            v._lastCachedFullnessFactor = after.CachedFullness;
            v._lastCachedRestedFactor = after.CachedRest;
            v._targetHealth = after.TargetHealth;
            v._sickness._currentValue = after.Sickness;
            v._stamina._currentValue = after.Stamina;
            v._activeCookingBuffRecipe = recipe;
            v._remainingCookingBuffTime = after.CookingSeconds;
            stats._stealthRating = after.Stealth;
            stats._treeCutDamageMultiplier = after.TreeChopBoost;
            v._strengthBoostModifier = after.StrengthBoost;
            stats._comfortRating = after.Comfort;
            stats._waterResistRating = after.WaterResistance;
            stats._thermalRating = after.ThermalRating;
            stats.SoundRangeDampFactor = after.SoundRangeDampFactor;
            return ReadProjectionNumbers(stats) == after;
        }

        private static bool TryResolveCookingRecipe(Vitals vitals, int id, out StatRecipe recipe)
        {
            recipe = null;
            if (id == -1) return true;
            var active = vitals._activeCookingBuffRecipe;
            if (active is not null && !active.WasCollected && active && active.Id == id && active._isACookingBuff)
            { recipe = active; return true; }
            var database = vitals._cookingRecipeDatabase;
            if (database == null || database.WasCollected || !database) return false;
            recipe = database.FindRecipeById(id);
            return recipe != null && !recipe.WasCollected && recipe && recipe.Id == id && recipe._isACookingBuff;
        }

        internal static bool CanDebitVolume(ItemInstance item, in ConsumptionDebit debit)
        {
            if (!debit.IsValid || !debit.UsesVolume || item == null || item.WasCollected
                || item._modules == null || item._modules.WasCollected || item._modules.Count != 1) return false;
            var module = item._modules[0]?.TryCast<VolumeContainerItemInstanceModule>();
            return module != null && !module.WasCollected && module._isInstantiated
                && module.OnVolumeChanged != null && !module.OnVolumeChanged.WasCollected
                && module._MaxVolume_k__BackingField == debit.MaximumVolume
                && module._CurrentVolume_k__BackingField <= module._MaxVolume_k__BackingField
                && module._CurrentVolume_k__BackingField == debit.BeforeVolume
                && (module._statRecipe == null ? -1 : module._statRecipe.Id) == debit.BeforeRecipeId
                && (module._MaxVolume_k__BackingField - (module._MaxVolume_k__BackingField - debit.AfterVolume) < Mathf.Epsilon
                    ? -1 : debit.BeforeRecipeId) == debit.AfterRecipeId
                && PlayerInventoryWire.HashModules(PlayerInventoryBaseline.CaptureModules(item)) == debit.BeforeModuleFingerprint;
        }

        internal static bool TrySelectVolumePortion(ItemInstance item, int approvedNumberOfUses, out float portion)
        {
            portion = 0;
            if (approvedNumberOfUses <= 0 || item == null || item.WasCollected || item._modules == null
                || item._modules.WasCollected || item._modules.Count != 1) return false;
            var module = item._modules[0]?.TryCast<VolumeContainerItemInstanceModule>();
            if (module == null || module.WasCollected || !module._isInstantiated) return false;
            // InventoryEdibleItem.OnConsumeComplete uses the authored number of uses, not
            // current volume or a client-selected amount. The last serving can be smaller.
            portion = module._MaxVolume_k__BackingField / approvedNumberOfUses;
            return float.IsFinite(portion) && portion > 0;
        }

        // The authenticated endpoint must reserve its attempt first and admit this exact Empty
        // call through its native volume guard. Empty performs the proven single volume event.
        internal static bool TryDebitVolume(ItemInstance item, in ConsumptionDebit debit)
        {
            if (!CanDebitVolume(item, in debit)) return false;
            var module = item._modules[0].TryCast<VolumeContainerItemInstanceModule>();
            var maximum = module._MaxVolume_k__BackingField;
            module.Empty(debit.Portion);
            return !item.WasCollected && item._modules?.Count == 1 && item._modules[0]?.Pointer == module.Pointer
                && !module.WasCollected && module._MaxVolume_k__BackingField == maximum
                && ConfirmsVolumeDebit(item, in debit);
        }

        internal static bool ConfirmsVolumeDebit(ItemInstance item, in ConsumptionDebit debit)
        {
            if (!debit.IsValid || !debit.UsesVolume || item == null || item.WasCollected
                || item._modules == null || item._modules.WasCollected || item._modules.Count != 1) return false;
            var module = item._modules[0]?.TryCast<VolumeContainerItemInstanceModule>();
            return module != null && !module.WasCollected && module._isInstantiated
                && module._MaxVolume_k__BackingField == debit.MaximumVolume
                && module._CurrentVolume_k__BackingField == debit.AfterVolume
                && (module._statRecipe == null ? -1 : module._statRecipe.Id) == debit.AfterRecipeId;
        }
        private readonly record struct Effect(StatEffect.Types Type, float Amount, bool Buff, bool Linked);

        private sealed class ConsumptionPlan
        {
            internal ItemData Data;
            internal ItemInstance Item;
            internal string ModulesBefore;
            internal Effect[] SourceEffects;
            internal Func<Effect[]> ReadSourceEffects;
            internal int PerishableState = -1;
            internal VolumeContainerItemInstanceModule Volume;
            internal float VolumeBefore, VolumeAfter, VolumeMaximum;
            internal StatRecipe VolumeRecipeBefore, VolumeRecipeAfter, CookingRecipe;
            internal StatRecipe IntakeCookingRecipe;
            internal float IntakeCookingSeconds;
            internal Effect[] CookingEffects;
            internal ConsumptionVitals Before, After;
        }

        private ConsumptionPlan Plan(ItemData data, ItemInstance item, float volumeToConsume,
            PerishableItemInstanceModule authoritativePerishable)
        {
            Require(data != null && !data.WasCollected && data && (data._type & global::Sons.Items.Core.Types.Edible) != 0,
                "Only native edible definitions are consumption actions.");
            var result = new ConsumptionPlan { Before = ReadVitals(), CookingRecipe = _cookingRecipe,
                Data = data, Item = item };
            result.After = result.Before;
            result.CookingEffects = _cookingEffects;
            VolumeContainerItemInstanceModule volume = null;
            PerishableItemInstanceModule perishable = null;
            if (item != null)
            {
                Require(!item.WasCollected && item._itemID == data.Id && item.Data?.Pointer == data.Pointer
                    && item._modules != null && !item._modules.WasCollected && item._modules.Count <= 32,
                    "The exact server item and its bounded module list are required.");
                foreach (var module in item._modules)
                {
                    Require(module != null && !module.WasCollected, "An item module is unavailable.");
                    var candidateVolume = module.TryCast<VolumeContainerItemInstanceModule>();
                    if (candidateVolume != null)
                    {
                        Require(volume == null, "Duplicate volume modules are not supported.");
                        volume = candidateVolume;
                    }
                    var candidatePerishable = module.TryCast<PerishableItemInstanceModule>();
                    if (candidatePerishable != null)
                    {
                        Require(perishable == null, "Duplicate perishable modules are not supported.");
                        perishable = candidatePerishable;
                    }
                    Require(candidateVolume != null || candidatePerishable != null,
                        "This module has no proven native consumption debit path.");
                }
                result.ModulesBefore = PlayerInventoryBaseline.CaptureModules(item);
            }
            Require(!data._isVolumeContainer || volume != null, "A volume definition needs its exact server module.");
            Require(!data._isPerishable || perishable != null, "A perishable definition needs its exact server module.");
            Require(volume == null || perishable == null,
                "Combined volume/perishable items require synchronized passive-copy volume custody.");
            if (perishable != null)
            {
                Require(authoritativePerishable != null && !authoritativePerishable.WasCollected
                    && authoritativePerishable._isInstantiated && authoritativePerishable._itemObject == null
                    && authoritativePerishable._currentState == perishable._currentState,
                    "Perishable consumption needs the exact detached server decay clock.");
                perishable = authoritativePerishable;
                result.PerishableState = (int)perishable._currentState;
            }

            Effect[] effects;
            if (volume != null)
            {
                float current = volume._CurrentVolume_k__BackingField;
                float maximum = volume._MaxVolume_k__BackingField;
                Require(volume._isInstantiated && float.IsFinite(current) && float.IsFinite(maximum)
                    && maximum > 0 && current > 0 && current <= maximum && float.IsFinite(volumeToConsume)
                    && maximum - (maximum - current) >= Mathf.Epsilon
                    && volumeToConsume > 0 && volumeToConsume <= maximum,
                    "Consumption needs an initialized nonempty volume and a server-approved native serving.");
                result.Volume = volume;
                result.VolumeMaximum = maximum;
                result.VolumeBefore = current;
                result.VolumeAfter = Mathf.Max(current - volumeToConsume, 0f);
                Require(result.VolumeAfter < current, "The selected volume portion is too small to debit native float custody.");
                var recipe = volume._statRecipe;
                Require(recipe is null || (!recipe.WasCollected && recipe), "The volume recipe is unavailable.");
                result.VolumeRecipeBefore = recipe;
                // Native Empty tests max - (max - current), not a rounded serialized integer.
                result.VolumeRecipeAfter = maximum - (maximum - result.VolumeAfter) < Mathf.Epsilon ? null : recipe;
                result.ReadSourceEffects = () => recipe is null ? Array.Empty<Effect>() : ReadEffects(recipe._statEffects);
                effects = result.SourceEffects = result.ReadSourceEffects();
                if (recipe is not null && recipe._isACookingBuff)
                {
                    ValidateExpiry(effects);
                    float seconds = recipe._cookingBuffDuration * StatRecipe.CookingBuffDurationToSeconds;
                    Require(float.IsFinite(seconds) && seconds > 0, "The cooking duration is unavailable.");
                    result.IntakeCookingRecipe = recipe;
                    result.IntakeCookingSeconds = seconds;
                    RebaseEffects(result);
                    return result;
                }
            }
            else
            {
                Require(volumeToConsume == 0, "Non-volume consumption debits exactly one item, not a volume.");
                if (perishable != null)
                {
                    var state = perishable._CurrentStateData_k__BackingField;
                    Require(state != null && !state.WasCollected && state.State == perishable._currentState,
                        "The exact perishable state and effects are unavailable.");
                    // The reserved state selects the effects once. The independent server decay
                    // clock may continue advancing while this exact intake waits for its receipt.
                    result.ReadSourceEffects = () => ReadEffects(state.StatEffects);
                    effects = result.ReadSourceEffects();
                }
                else
                {
                    result.ReadSourceEffects = () => ReadDefinitionEffects(data);
                    effects = result.ReadSourceEffects();
                }
            }
            result.SourceEffects = effects;
            RebaseEffects(result);
            return result;
        }

        // Custody and selected perishable effects remain reserved. Only the numeric baseline
        // moves forward, so stamina spent during the network round trip is not restored.
        private void RebaseEffects(ConsumptionPlan plan)
        {
            plan.Before = ReadVitals();
            plan.After = plan.Before;
            plan.CookingRecipe = _cookingRecipe;
            plan.CookingEffects = _cookingEffects;
            if (plan.IntakeCookingRecipe != null)
            {
                if (_cookingRecipe is not null && plan.Before.CookingSeconds > 0)
                    plan.After = Apply(plan.After, _cookingEffects, false, true);
                plan.CookingRecipe = plan.IntakeCookingRecipe;
                plan.CookingEffects = plan.SourceEffects;
                plan.After = Apply(plan.After, plan.SourceEffects, true, false) with
                { CookingRecipeId = plan.IntakeCookingRecipe.Id, CookingSeconds = plan.IntakeCookingSeconds };
            }
            else plan.After = Apply(plan.After, plan.SourceEffects, true, false);
        }

        private static Effect[] ReadDefinitionEffects(ItemData data)
        {
            Require(data != null && !data.WasCollected && data, "The definition is unavailable.");
            var source = data._usedStatEffect;
            Require(source != null && !source.WasCollected && source.Length <= MaximumEffects,
                "The definition effects are unavailable or exceed the bounded effect count.");
            var effects = new Effect[source.Length];
            for (int i = 0; i < effects.Length; i++) effects[i] = ReadEffect(source[i]);
            return effects;
        }

        private static bool SourceCurrent(ConsumptionPlan plan)
        {
            if (plan.Data == null || plan.Data.WasCollected || !plan.Data
                || ItemDatabaseManager.ItemById(plan.Data.Id)?.Pointer != plan.Data.Pointer
                || !plan.SourceEffects.AsSpan().SequenceEqual(plan.ReadSourceEffects())) return false;
            if (plan.Item != null && (plan.Item.WasCollected || plan.Item.Data?.Pointer != plan.Data.Pointer
                || !PlayerInventoryBaseline.JsonEqual(plan.ModulesBefore,
                    PlayerInventoryBaseline.CaptureModules(plan.Item)))) return false;
            if (plan.VolumeRecipeBefore != null && (plan.VolumeRecipeBefore.WasCollected || !plan.VolumeRecipeBefore
                || plan.VolumeRecipeBefore._isACookingBuff != (plan.IntakeCookingRecipe != null)
                || (plan.IntakeCookingRecipe != null && plan.VolumeRecipeBefore._cookingBuffDuration
                    * StatRecipe.CookingBuffDurationToSeconds != plan.IntakeCookingSeconds))) return false;
            return plan.Volume == null || (!plan.Volume.WasCollected
                && plan.Volume._MaxVolume_k__BackingField == plan.VolumeMaximum
                && plan.Volume._CurrentVolume_k__BackingField == plan.VolumeBefore
                && SameRecipe(plan.VolumeRecipeBefore, plan.Volume._statRecipe));
        }

        private static Effect[] ReadEffects(Il2CppSystem.Collections.Generic.List<StatEffect> source)
        {
            Require(source != null && !source.WasCollected && source.Count is >= 0 and <= MaximumEffects,
                "Recipe effects are unavailable or exceed the bounded effect count.");
            var effects = new Effect[source.Count];
            for (int i = 0; i < effects.Length; i++) effects[i] = ReadEffect(source[i]);
            return effects;
        }

        private static Effect ReadEffect(StatEffect effect)
        {
            Require(effect != null && !effect.WasCollected && float.IsFinite(effect._amount),
                "An effect is unavailable or has a nonfinite amount.");
            return new(effect._type, effect._amount, effect._buff, effect._linkedToCookingBuff);
        }

        private static void ValidateExpiry(Effect[] effects)
        {
            foreach (var effect in effects)
                if (effect.Linked) ValidateEffect(effect with { Amount = -effect.Amount });
        }

        private static void ValidateEffect(Effect effect)
        {
            switch (effect.Type)
            {
                case StatEffect.Types.Health:
                    Require(effect.Amount > 0, "Nonpositive health effects require actor-specific Hit/life routing.");
                    break;
                case StatEffect.Types.Stamina:
                case StatEffect.Types.Rest:
                case StatEffect.Types.Sickness:
                case StatEffect.Types.Fullness:
                case StatEffect.Types.Hydration:
                case StatEffect.Types.Stealth:
                case StatEffect.Types.TreeChopBoostModifier:
                case StatEffect.Types.StrengthBoostModifier:
                case StatEffect.Types.ComfortRating:
                case StatEffect.Types.WaterResistRating:
                case StatEffect.Types.ThermalRating:
                case StatEffect.Types.SoundRangeDampFactor:
                    break;
                default:
                    throw new InvalidOperationException("Consumption effect has no actor-specific implementation: " + effect.Type);
            }
        }

        // Mirrors ItemUtils' scalar dispatcher and Vitals' inlined operations. Never call its
        // LocalPlayer dispatcher or difficulty helpers, including on a dedicated server.
        private ConsumptionVitals Apply(ConsumptionVitals state,
            Effect[] effects, bool add, bool linkedOnly)
        {
            bool hard = GameSetupManager.GetConsumableEffectsSetting()._intValue == SurvivalSetting.Hard._intValue;
            float negative = hard ? _stats._hardSurvivalConsumableNegativeMultiplier : 1f;
            float healing = hard ? _stats._hardSurvivalConsumablePositiveMultiplier : 1f;
            Require(float.IsFinite(negative) && negative >= 0 && float.IsFinite(healing) && healing >= 0,
                "The actor's survival multipliers are unavailable.");
            foreach (var effect in effects)
            {
                if (linkedOnly && !effect.Linked) continue;
                float amount = add ? effect.Amount : -effect.Amount;
                ValidateEffect(effect with { Amount = amount });
                float value = amount * (effect.Type == StatEffect.Types.Sickness
                    ? (amount > 0 ? negative : 1f) : amount > 0 ? (effect.Type == StatEffect.Types.Health ? healing : 1f) : negative);
                Require(float.IsFinite(value), "A scaled consumption effect is nonfinite.");
                switch (effect.Type)
                {
                    case StatEffect.Types.Health:
                        state = state with { TargetHealth = Mathf.Min(Mathf.Clamp(state.TargetHealth + value,
                            0f, _health._max), _health._max - state.Sickness) };
                        break;
                    case StatEffect.Types.Sickness:
                        state = state with { Sickness = Clamp(_sickness, state.Sickness + value) };
                        break;
                    case StatEffect.Types.Stamina:
                        float next = state.Stamina + value;
                        if (value > 0 && next >= _vitality._currentValue) next = _vitality._currentValue;
                        state = state with { Stamina = Clamp(_stamina, next) };
                        break;
                    case StatEffect.Types.Hydration:
                        ChangeSurvival(_hydration, state.Hydration, state.HydrationBuff, value, effect.Buff,
                            _vitals._hydrationDrainBuffModifier, false, out var hydration, out var hydrationBuff, out var hydrationFactor);
                        state = state with { Hydration = hydration, HydrationBuff = hydrationBuff, CachedHydration = hydrationFactor };
                        break;
                    case StatEffect.Types.Fullness:
                        ChangeSurvival(_fullness, state.Fullness, state.FullnessBuff, value, effect.Buff,
                            _vitals._fullnessDrainBuffModifier, false, out var fullness, out var fullnessBuff, out var fullnessFactor);
                        state = state with { Fullness = fullness, FullnessBuff = fullnessBuff, CachedFullness = fullnessFactor };
                        break;
                    case StatEffect.Types.Rest:
                        // The native negative branch passes a signed amount to SpendRest, which
                        // clamps that amount to zero. Preserve this, including buff/cache updates.
                        ChangeSurvival(_rested, state.Rest, state.RestBuff, value, effect.Buff,
                            _vitals._restedDrainBuffModifier, true, out var rest, out var restBuff, out var restFactor);
                        state = state with { Rest = rest, RestBuff = restBuff, CachedRest = restFactor };
                        break;
                    // These native branches add the unscaled signed amount directly. Difficulty
                    // scaling is only for the survival-stat branches above.
                    case StatEffect.Types.Stealth:
                        state = state with { Stealth = state.Stealth + amount }; break;
                    case StatEffect.Types.TreeChopBoostModifier:
                        state = state with { TreeChopBoost = state.TreeChopBoost + amount }; break;
                    case StatEffect.Types.StrengthBoostModifier:
                        state = state with { StrengthBoost = state.StrengthBoost + amount }; break;
                    case StatEffect.Types.ComfortRating:
                        state = state with { Comfort = state.Comfort + amount }; break;
                    case StatEffect.Types.WaterResistRating:
                        state = state with { WaterResistance = state.WaterResistance + amount }; break;
                    case StatEffect.Types.ThermalRating:
                        state = state with { ThermalRating = state.ThermalRating + amount }; break;
                    case StatEffect.Types.SoundRangeDampFactor:
                        state = state with { SoundRangeDampFactor = Mathf.Max(0.7f, state.SoundRangeDampFactor) + amount }; break;
                }
            }
            Require(Finite(state), "The staged actor effects are nonfinite.");
            return state;
        }

        private static void ChangeSurvival(Stat stat, float current, float buff, float value, bool addBuff,
            float drainModifier, bool rest, out float result, out float resultBuff, out float factor)
        {
            Require(float.IsFinite(drainModifier), "A survival drain modifier is nonfinite.");
            if (value > 0)
            {
                result = Clamp(stat, current + value);
                resultBuff = buff + (addBuff ? value / stat._max : 0f);
            }
            else
            {
                float loss = (rest ? Mathf.Max(0f, value) : Mathf.Abs(value)) * (buff > 0 ? drainModifier : 1f);
                result = Clamp(stat, current - loss);
                resultBuff = Mathf.Clamp01(buff - loss / stat._max);
            }
            factor = (result - stat._min) / (stat._max - stat._min);
        }

        private static float Clamp(Stat stat, float value) => Mathf.Clamp(value, stat._min, stat._max);

        private ConsumptionVitals ReadVitals(bool allowPrepared = false,
            PlayerSurvivalRuntime cookingRuntime = null)
        {
            Require(ActorCurrent(allowPrepared, cookingRuntime), "The actor's retained stat objects are no longer current.");
            var result = new ConsumptionVitals(_vitals._targetHealth, _sickness._currentValue,
                _stamina._currentValue, _hydration._currentValue, _fullness._currentValue, _rested._currentValue,
                _vitals._currentHydrationBuffFactor, _vitals._currentFullnessBuffFactor, _vitals._currentRestedBuffFactor,
                _vitals._lastCachedHydrationFactor, _vitals._lastCachedFullnessFactor, _vitals._lastCachedRestedFactor,
                _cookingRecipe is null ? -1 : _cookingRecipe.Id, _vitals._remainingCookingBuffTime,
                _stats._stealthRating, _stats._treeCutDamageMultiplier, _vitals._strengthBoostModifier,
                _stats._comfortRating, _stats._waterResistRating, _stats._thermalRating, _stats.SoundRangeDampFactor);
            Require(Finite(result), "The actor's survival state is nonfinite.");
            return result;
        }

        private static bool Finite(ConsumptionVitals state)
            => ConsumptionNumbers.From(state).IsFinite;

        private void WriteVitals(ConsumptionVitals state, StatRecipe cookingRecipe, Effect[] effects)
        {
            _vitals._targetHealth = state.TargetHealth;
            Combat.StrictCombatLifeHooks.WriteConsumptionStat(_sickness, state.Sickness);
            Combat.StrictCombatLifeHooks.WriteConsumptionStat(_stamina, state.Stamina);
            Combat.StrictCombatLifeHooks.WriteConsumptionStat(_hydration, state.Hydration);
            Combat.StrictCombatLifeHooks.WriteConsumptionStat(_fullness, state.Fullness);
            Combat.StrictCombatLifeHooks.WriteConsumptionStat(_rested, state.Rest);
            _vitals._currentHydrationBuffFactor = state.HydrationBuff;
            _vitals._currentFullnessBuffFactor = state.FullnessBuff;
            _vitals._currentRestedBuffFactor = state.RestBuff;
            _vitals._lastCachedHydrationFactor = state.CachedHydration;
            _vitals._lastCachedFullnessFactor = state.CachedFullness;
            _vitals._lastCachedRestedFactor = state.CachedRest;
            _vitals._activeCookingBuffRecipe = cookingRecipe;
            _vitals._remainingCookingBuffTime = state.CookingSeconds;
            _stats._stealthRating = state.Stealth;
            _stats._treeCutDamageMultiplier = state.TreeChopBoost;
            _vitals._strengthBoostModifier = state.StrengthBoost;
            _stats._comfortRating = state.Comfort;
            _stats._waterResistRating = state.WaterResistance;
            _stats._thermalRating = state.ThermalRating;
            _stats.SoundRangeDampFactor = state.SoundRangeDampFactor;
            _cookingRecipe = cookingRecipe;
            _cookingEffects = effects;
        }

        private static void Require(bool condition, string detail)
        { if (!condition) throw new InvalidOperationException(detail); }
    }
}

namespace Modstoke.Sons.Combat
{
    internal static partial class StrictCombatLifeHooks
    {
        internal static bool HasConsumptionHealthOwner(Vitals vitals) => FindVitalsOwner(vitals) != null;
        internal static void WriteConsumptionStat(Stat stat, float value)
        {
            if (stat._currentValue != value) SetStatCurrent(stat, value);
        }
    }
}
