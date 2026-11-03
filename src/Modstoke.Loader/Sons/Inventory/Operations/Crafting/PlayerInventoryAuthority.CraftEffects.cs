// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using global::Sons.Crafting;
using CraftPlayerState = global::Sons.Player.PlayerState;

namespace Modstoke.Sons
{
    public readonly record struct CraftCounter(int ItemId, int Before, int After);

    /// <summary>Authoritative actor effects after every native ingredient/result custody receipt.</summary>
    public sealed class CraftEffectsSnapshot
    {
        public ulong Transaction { get; }
        public string RecipeFingerprint { get; }
        public IReadOnlyList<CraftCounter> Counters { get; }
        public bool RecipeWasNew { get; }

        internal CraftEffectsSnapshot(ulong transaction, string fingerprint,
            CraftCounter[] counters, bool recipeWasNew)
        {
            Transaction = transaction; RecipeFingerprint = fingerprint; RecipeWasNew = recipeWasNew;
            Counters = Array.AsReadOnly((CraftCounter[])counters.Clone());
        }
    }

    public sealed partial class PlayerInventoryAuthority
    {
        private CraftingRecipeDatabase _craftRouteDatabase;
        private CraftPlayerState _craftCounters;
        private IntPtr _craftCounterData;
        private CraftEffectsSnapshot _nativeCraftEffects;
        private bool _craftEffectsAwaitingSource;
        private string _craftEffectsRecipeKey;

        public bool HasPendingNativeCraftEffects => _craftEffectsAwaitingSource;

        /// <summary>
        /// Bind an independently approved/restored actor-local PlayerState, never the host's
        /// LocalPlayer. The caller must exclude competing counter/save writers for this lifetime.
        /// Does not authorize recipes, source mat custody, animation or client notifications.
        /// </summary>
        public bool TryBindCraftingRoute(CraftingRecipeDatabase database, CraftPlayerState counters, out string detail)
        {
            detail = "Craft route needs an idle approved account, trusted recipe database and restored disabled actor counters.";
            if (!PreparedReady(out var target) || !HasAcceptedBaseline || _craftRouteDatabase != null || _requestCraftDatabase == null
                || _authorizeCraftRecipe == null || database?.Pointer != _requestCraftDatabase.Pointer
                || _pending != null || _stow != null || _equip != null
                || HasPendingNativePowerSupply || MutationEnforcement == null
                || MutationEnforcement.IsDisposed || MutationEnforcement.IsFaulted
                || MutationEnforcement.HasPendingCustody || database == null || database.WasCollected || !database
                || counters == null || counters.WasCollected || !counters || counters.enabled
                || counters._data == null || counters._data.WasCollected
                || !counters.transform.IsChildOf(target.transform)) return false;
            _craftRouteDatabase = database;
            _craftCounters = counters;
            _craftCounterData = counters._data.Pointer;
            MutationEnforcement.BindCraftEffectTransport();
            detail = "Trusted server recipe database and exact actor counter storage bound.";
            return true;
        }

        public bool TryReadCraftEffects(ulong transaction, out CraftEffectsSnapshot effects)
        {
            effects = null;
            if (Environment.CurrentManagedThreadId != _thread || IsDisposed || IsFaulted || !CraftEffectsOwnerCurrent()
                || _nativeCraftReceipt is not { State: CraftState.Applied, EffectsApplied: true } receipt
                || receipt.Transaction != transaction || _nativeCraftEffects?.Transaction != transaction) return false;
            effects = _nativeCraftEffects;
            return true;
        }

        // Only the authenticated source presentation receipt may release this reservation.
        // A custody Settled packet and a native CraftComplete event are both insufficient.
        internal bool TryAcknowledgeCraftEffects(ulong transaction, string recipeFingerprint)
        {
            if (Environment.CurrentManagedThreadId != _thread || _busy || _suspended || !CraftEffectsOwnerCurrent()
                || _nativeCraft != null || _nativeCraftEffects?.Transaction != transaction
                || _nativeCraftEffects.RecipeFingerprint != recipeFingerprint
                || _nativeCraftReceipt is not { State: CraftState.Applied, EffectsApplied: true }) return false;
            foreach (var counter in _nativeCraftEffects.Counters)
                if (_craftCounters.GetCrafted(counter.ItemId) != counter.After) return false;
            if (_craftCounters.Get(_craftEffectsRecipeKey, true)) return false;
            _craftEffectsAwaitingSource = false;
            return true;
        }

        private bool CraftEffectsOwnerCurrent()
            => Current(out var target) && _craftRouteDatabase != null && !_craftRouteDatabase.WasCollected && _craftRouteDatabase
                && _craftCounters != null && !_craftCounters.WasCollected && _craftCounters && !_craftCounters.enabled
                && _craftCounters.transform.IsChildOf(target.transform)
                && _craftCounters._data != null && !_craftCounters._data.WasCollected
                && _craftCounters._data.Pointer == _craftCounterData;

        private NativeCraftEffects CaptureCraftEffects(InventoryCraftRecipe recipe)
        {
            var amounts = new Dictionary<int, int>();
            foreach (int id in recipe.Results) { amounts.TryGetValue(id, out int n); amounts[id] = checked(n + 1); }
            var counters = new CraftCounter[amounts.Count];
            int index = 0;
            foreach (var pair in amounts)
            {
                int before = _craftCounters.GetCrafted(pair.Key);
                NativeCraftRequire(before >= 0, "Native crafted count is negative.");
                counters[index++] = new(pair.Key, before, checked(before + pair.Value));
            }
            string key = "isNewCraftingRecipe_" + recipe.Name;
            return new(counters, key, _craftCounters.Get(key, false));
        }

        private void ApplyCraftEffects(NativeCraftReservation craft)
        {
            if (craft.Effects == null) return;
            NativeCraftRequire(!craft.EffectsAttempted && CraftEffectsOwnerCurrent(), "Craft effects owner or once-only boundary changed.");
            var effects = craft.Effects;
            foreach (var counter in effects.Counters)
                NativeCraftRequire(_craftCounters.GetCrafted(counter.ItemId) == counter.Before,
                    "Crafted count changed during reserved custody.");
            NativeCraftRequire(_craftCounters.Get(effects.NewRecipeKey, false) == effects.RecipeWasNew,
                "Recipe notification state changed during reserved custody.");
            // Native OnCraftCompleteInternal increments once per result row, then clears this
            // key. Defer those effects until custody is real; never publish host-global events
            // for a remote actor or call the native completion body (which would craft again).
            craft.EffectsAttempted = true;
            foreach (int id in craft.Recipe.Results) _craftCounters.IncCrafted(id);
            _craftCounters.Set(effects.NewRecipeKey, false);
            NativeCraftRequire(CraftEffectsOwnerCurrent(), "Craft effects storage changed during native setters.");
            foreach (var counter in effects.Counters)
                NativeCraftRequire(_craftCounters.GetCrafted(counter.ItemId) == counter.After, "Crafted count readback differs.");
            NativeCraftRequire(!_craftCounters.Get(effects.NewRecipeKey, true), "Recipe notification clear was not confirmed.");
            craft.EffectsApplied = true;
            _nativeCraftEffects = new(craft.Transaction, craft.Recipe.Fingerprint, effects.Counters, effects.RecipeWasNew);
            _craftEffectsRecipeKey = effects.NewRecipeKey;
            _craftEffectsAwaitingSource = true;
        }

        private sealed record NativeCraftEffects(CraftCounter[] Counters, string NewRecipeKey, bool RecipeWasNew);
    }
}
