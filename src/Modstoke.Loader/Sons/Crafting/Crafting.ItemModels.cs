// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Modstoke.Loader;
using global::Sons.Crafting;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using TheForest.Items.Inventory;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Modstoke.Sons
{
    public static partial class Crafting
    {
        private delegate bool AuthoredLayoutPublisher<TGroup>(ItemData item, TGroup group, out CraftingLayoutRegistrationResult result);

        private static readonly List<MatIngredient> MatIngredients = new();
        private static IntPtr _modelMat;
        private static bool _modelMatSettled, _modelMatFailureLogged;

        internal static void AddItemModel(ItemData item, ItemModel model)
        {
            if (model.MatAction != ItemMatAction.Combine) return;
            MatIngredients.Add(new MatIngredient(item, model));
            _modelMatSettled = false;
        }

        /// <summary>Gives combinable modeled items their places on the current crafting mat.</summary>
        internal static void TickItemModels()
        {
            if (MatIngredients.Count == 0 || !OnCraftExecutionThread) return;
            try
            {
                if (!TryGetCraftExecutionContext(out _, out var inventory, out var system, out _)) return;
                if (system.Pointer != _modelMat)
                {
                    // A new player brings a new mat; the groups made for the last one went with it.
                    _modelMat = system.Pointer;
                    _modelMatSettled = false;
                    foreach (var ingredient in MatIngredients) ingredient.Forget();
                }
                // Groups are published into an open, idle mat, as publication requires.
                if (_modelMatSettled || inventory._currentView != PlayerInventory.PlayerViews.Inventory || !system.isActiveAndEnabled)
                    return;
                var settled = true;
                foreach (var ingredient in MatIngredients)
                {
                    ingredient.Update(system);
                    settled &= ingredient.Settled;
                }
                _modelMatSettled = settled;
            }
            catch (Exception error)
            {
                if (_modelMatFailureLogged) return;
                _modelMatFailureLogged = true;
                RuntimeEntryPoint.LogSource?.LogWarning("Modeled items could not be placed on the crafting mat: " + error);
            }
        }

        // One combinable modeled item on one crafting mat: a group where it lies as an ingredient and a group
        // where it lies when a craft hands it back, each with a single row that carries the model. Both take
        // the spot of the game's laser sight, its nearest thing to a weapon mod of this kind, and stand the way
        // the item does on the inventory mat.
        private sealed class MatIngredient
        {
            private readonly ItemData _item;
            private readonly ItemModel _model;
            private bool _ingredient, _result, _abandoned, _reported;
            private float _retryAt;
            private string _status;

            internal MatIngredient(ItemData item, ItemModel model)
            {
                _item = item;
                _model = model;
            }

            internal bool Settled => _abandoned || _ingredient && _result;

            internal void Forget()
            {
                _ingredient = _result = _abandoned = _reported = false;
                _retryAt = 0;
                _status = null;
            }

            internal void Update(CraftingSystem system)
            {
                if (Settled || Time.unscaledTime < _retryAt || !ItemRegistration.IsPublishedRuntimeItem(_item)) return;
                if (!Inventory.TryGetItemModelRow(_item._id, out var stance))
                {
                    Wait("its inventory mat layout is not built yet.");
                    return;
                }
                if (!_ingredient)
                    _ingredient = Place<IngredientLayoutItemGroup, IngredientLayoutItem>(system._ingredientLayoutItemGroups,
                        system._ingredientLayoutGroups, stance, "ingredient", TryRegisterAuthoredIngredientLayout);
                if (_ingredient && !_result)
                    _result = Place<CraftingResultLayoutItemGroup, CraftingResultLayoutItem>(system._craftingResultLayoutItemGroups,
                        system._craftingResultLayoutGroups, stance, "result", TryRegisterAuthoredResultLayout);
                if (_ingredient && _result)
                    RuntimeEntryPoint.LogSource?.LogInfo("Item " + _item._id + " has its places on the crafting mat.");
            }

            private bool Place<TGroup, TLayout>(Il2CppSystem.Collections.Generic.List<TGroup> groups, Transform root, Transform stance,
                string role, AuthoredLayoutPublisher<TGroup> publish)
                where TGroup : LayoutItemGroup<TLayout> where TLayout : LayoutItem
            {
                TGroup donor = null;
                if (groups is not null && !groups.WasCollected)
                    foreach (var group in groups)
                        if (CraftExecutionLive(group) && group._itemId == ItemDatabase.LaserSightMod)
                        {
                            donor = group;
                            break;
                        }
                var donorRows = donor?._layoutItems;
                if (!CraftExecutionLive(root) || donorRows is null || donorRows.WasCollected || donorRows.Count == 0
                    || !CraftExecutionLive(donorRows[0]))
                {
                    Wait("the mat has no laser sight " + role + " group to take a place from.");
                    return false;
                }
                var donorRow = donorRows[0];
                GameObject holder = null;
                try
                {
                    holder = new GameObject(_item._name + " " + role + " group");
                    holder.SetActive(false);
                    holder.layer = donor.gameObject.layer;
                    holder.transform.SetParent(root, false);
                    holder.transform.localPosition = donor.transform.localPosition;
                    holder.transform.localRotation = donor.transform.localRotation;
                    var group = holder.AddComponent<TGroup>();
                    group._itemId = _item._id;
                    group._itemData = _item;
                    group._isOneToOneCollection = false;
                    group._layoutItems = new();
                    group._onInteractAudioEvent = donor._onInteractAudioEvent;
                    group._animationClip = null;
                    group._animationSpeedMultiplier = 1;
                    group._delayTime = 0;

                    var place = new GameObject(_item._name + " " + role);
                    place.SetActive(false);
                    place.layer = donorRow.gameObject.layer;
                    place.transform.SetParent(holder.transform, false);
                    place.transform.SetPositionAndRotation(donorRow.transform.position, stance.rotation);
                    var parent = holder.transform.lossyScale;
                    var wanted = stance.lossyScale;
                    if (Math.Abs(parent.x) > 1e-5f && Math.Abs(parent.y) > 1e-5f && Math.Abs(parent.z) > 1e-5f)
                        place.transform.localScale = new Vector3(wanted.x / parent.x, wanted.y / parent.y, wanted.z / parent.z);
                    var row = place.AddComponent<TLayout>();
                    row._doNotSnap = true;
                    row._setToInventoryLayer = donorRow._setToInventoryLayer;
                    row._uiTriggerDelay = donorRow._uiTriggerDelay;
                    ItemModelCopies.CreateForMat(_model.Model, place.transform, place.layer);
                    group._layoutItems.Add(row);
                    // The group wakes with its row still off: an empty place, as the game's own are.
                    holder.SetActive(true);

                    if (publish(_item, group, out var result)) return true;
                    if (result.NativeStateUncertain)
                    {
                        // Its entry may still be in the mat's list, so the objects stay and nothing is retried.
                        _abandoned = true;
                        RuntimeEntryPoint.LogSource?.LogWarning("Item " + _item._id + " cannot be combined on this mat: " + result.Detail);
                        return false;
                    }
                    Object.Destroy(holder);
                    Wait(result.Detail);
                    return false;
                }
                catch (Exception error)
                {
                    if (CraftExecutionLive(holder)) Object.Destroy(holder);
                    Wait("its " + role + " group could not be built: " + error.Message);
                    _retryAt = Time.unscaledTime + 30;
                    return false;
                }
            }

            // The first try often lands while the inventory is still opening, so a wait is only reported once
            // it has repeated.
            private void Wait(string status)
            {
                _retryAt = Math.Max(_retryAt, Time.unscaledTime + 2);
                if (status != _status)
                {
                    _status = status;
                    _reported = false;
                    return;
                }
                if (_reported) return;
                _reported = true;
                RuntimeEntryPoint.LogSource?.LogWarning("Item " + _item._id + " is waiting for the crafting mat: " + status);
            }
        }
    }
}
