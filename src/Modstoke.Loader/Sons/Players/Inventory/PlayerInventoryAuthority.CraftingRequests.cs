// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Crafting;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryAuthority
    {
        private CraftingRecipeDatabase _requestCraftDatabase;
        private Func<int, bool> _authorizeCraftRecipe;
        private bool _reservingCraftRequest;

        /// <summary>
        /// Bind the approved actor's recipe database and current unlock/access decision. The
        /// callback runs on the game thread, must not mutate inventory, and is not client policy.
        /// Binding while prepared does not authorize gameplay or native crafting effects.
        /// </summary>
        public bool TryBindCraftRequests(CraftingRecipeDatabase database, Func<int, bool> authorizeRecipe,
            out string detail)
        {
            detail = "An approved idle enforced account, exact native database and server recipe decision are required.";
            if (!PreparedReady(out _) || !HasAcceptedBaseline || MutationEnforcement == null
                || _requestCraftDatabase != null || database == null || database.WasCollected || !database
                || database._recipes == null || database._recipes.WasCollected
                || database._recipes.Count is <= 0 or > 4096 || authorizeRecipe == null) return false;
            _requestCraftDatabase = database;
            _authorizeCraftRecipe = authorizeRecipe;
            detail = "Native craft requests bound to the server database and actor recipe decision; no craft reserved.";
            return true;
        }

        internal bool TryAuthorizeCraftRequest(in PlayerCraftFrame request,
            CraftIngredient[] ingredients, out CraftReceipt receipt, out string detail)
        {
            receipt = default;
            detail = "Native craft request requires a current server recipe binding and idle exact custody.";
            if (Environment.CurrentManagedThreadId != _thread || _busy || _reservingCraftRequest
                || _requestCraftDatabase == null || _authorizeCraftRecipe == null
                || _craftRouteDatabase == null || _craftRouteDatabase.Pointer != _requestCraftDatabase.Pointer || !CraftEffectsOwnerCurrent()
                || MutationEnforcement == null || !MutationEnforcement.CanQueue || _lastTransaction == ulong.MaxValue)
                return false;
            _reservingCraftRequest = true;
            try
            {
                if (!Ready(out _) || _pending != null || _stow != null || _equip != null
                    || HasPendingNativePowerSupply || request.MigrationId != _bridge.Plan.Id
                    || request.Generation != _bridge.Plan.Generation || request.NetworkId != _bridge.NetworkId
                    || request.BaselineFingerprint != AcceptedBaselineFingerprint
                    || ingredients.Length != request.SelectionCount
                    || PlayerCraftWire.HashSelections(ingredients) != request.SelectionFingerprint) return false;
                var recipe = InventoryCraftRecipe.Capture(_requestCraftDatabase, request.RecipeIndex);
                if (recipe.Fingerprint != request.RecipeFingerprint)
                { detail = "Native source/server recipe content differs before any custody mutation."; return false; }
                bool approved;
                _busy = true;
                try { approved = _authorizeCraftRecipe(request.RecipeIndex); }
                finally { _busy = false; }
                if (!approved || !recipe.Current()) { detail = "Server recipe unlock/access or identity declined this craft."; return false; }
                ulong transaction = _lastTransaction + 1;
                if (!TryReserveCraft(transaction, _requestCraftDatabase, request.RecipeIndex, ingredients, out detail))
                {
                    // Reservation may already own native instances even if later setup failed.
                    TryReadCraftReceipt(transaction, out receipt);
                    return false;
                }
                return TryReadCraftReceipt(transaction, out receipt);
            }
            catch (Exception error) { detail = "Native craft authorization failed: " + error.Message; return false; }
            finally { _reservingCraftRequest = false; }
        }
    }
}
