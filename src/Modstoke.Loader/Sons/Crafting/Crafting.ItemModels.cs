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

        // Where modeled items lie on the mat, in the frame of its ingredient root (metres; X to the player's right,
        // Z away from the player, Y up; the cloth lies about 35 mm above the root, where the game's own flat items
        // rest, and spans about X -0.5..0.35 and Z 0..0.45). Weapon
        // attachments take the weapon mods' own spots in the column on the left, where the game lays its laser
        // sight, flashlight and rails; other parts go side by side along two rows through the middle, clear of
        // that column and the cog; a crafted result lies in the middle once its ingredients are gone.
        private static readonly Vector2[] AttachmentSpots = { new(-0.397f, 0.110f), new(-0.389f, 0.229f), new(-0.390f, 0.361f), new(-0.470f, 0.082f) };
        private static readonly float[] LayoutRows = { 0.29f, 0.17f };
        private const float LayoutStart = -0.30f, LayoutEnd = 0.22f, LayoutGap = 0.035f, MatCloth = 0.036f;
        private static readonly Vector2 ResultSpot = new(-0.04f, 0.23f);
        private static int _attachments;

        private static readonly List<MatIngredient> MatIngredients = new();
        private static IntPtr _modelMat;
        private static bool _modelMatSettled, _modelMatFailureLogged;
        private static float _layoutCursor = LayoutStart;
        private static int _layoutRow;

        internal static void AddItemModel(ItemData item, ItemModel model)
        {
            if (model.MatAction != ItemMatAction.Combine) return;
            MatIngredients.Add(new MatIngredient(item, model));
            _modelMatSettled = false;
        }

        // The next free place along the rows for an item this wide; once the rows are full, places repeat.
        private static Vector2 TakeLayoutSpot(float width)
        {
            if (_layoutCursor + width > LayoutEnd && _layoutCursor > LayoutStart)
            {
                _layoutRow = (_layoutRow + 1) % LayoutRows.Length;
                _layoutCursor = LayoutStart;
            }
            var spot = new Vector2(_layoutCursor + width * 0.5f, LayoutRows[_layoutRow]);
            _layoutCursor += width + LayoutGap;
            return spot;
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
                    _layoutCursor = LayoutStart;
                    _layoutRow = 0;
                    _attachments = 0;
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
        // where it lies when a craft hands it back, each with a single row that carries the model. The rows borrow
        // the laser sight's settings; the item lies as on the inventory mat, turned lengthwise across the mat, on
        // the cloth at its own place along the layout rows, or in the middle as a result.
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

            private Vector2? _spot;

            internal bool Settled => _abandoned || _ingredient && _result;

            internal void Forget()
            {
                _ingredient = _result = _abandoned = _reported = false;
                _retryAt = 0;
                _status = null;
                _spot = null;
            }

            // Turns the item lengthwise across the mat (its longer side along X) and sets it on the cloth at its
            // place. Bounds are taken in the holder's frame, which is the mat root's.
            private void Lay(Transform space, Transform place, GameObject visual, bool result)
            {
                var bounds = ItemModelCopies.LocalBounds(space, visual);
                if (bounds.size.z > bounds.size.x * 1.1f)
                {
                    place.localRotation = Quaternion.Euler(0, 90, 0) * place.localRotation;
                    bounds = ItemModelCopies.LocalBounds(space, visual);
                }
                var target = result ? ResultSpot : _spot ??= WeaponAttachments.IsAttachment(_item._id)
                    ? AttachmentSpots[_attachments++ % AttachmentSpots.Length] : TakeLayoutSpot(bounds.size.x);
                place.localPosition += new Vector3(target.x - bounds.center.x, MatCloth - bounds.min.y, target.y - bounds.center.z);
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
                        if (CraftExecutionLive(group) && group._itemId == ItemDatabase.LaserSightMod
                            && group._layoutItems is { Count: > 0 } rows && CraftExecutionLive(rows[0]))
                        {
                            donor = group;
                            break;
                        }
                var donorRows = donor?._layoutItems;
                if (!CraftExecutionLive(root) || donorRows is null || donorRows.WasCollected || donorRows.Count == 0
                    || !CraftExecutionLive(donorRows[0]))
                {
                    Wait("the mat has no laser sight " + role + " group to take its settings from.");
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
                    holder.transform.localPosition = Vector3.zero;
                    holder.transform.localRotation = Quaternion.identity;
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
                    place.transform.rotation = stance.rotation;
                    var parent = holder.transform.lossyScale;
                    var wanted = stance.lossyScale;
                    if (Math.Abs(parent.x) > 1e-5f && Math.Abs(parent.y) > 1e-5f && Math.Abs(parent.z) > 1e-5f)
                        place.transform.localScale = new Vector3(wanted.x / parent.x, wanted.y / parent.y, wanted.z / parent.z);
                    var row = place.AddComponent<TLayout>();
                    row._doNotSnap = true;
                    row._setToInventoryLayer = donorRow._setToInventoryLayer;
                    row._uiTriggerDelay = donorRow._uiTriggerDelay;
                    var visual = ItemModelCopies.CreateForMat(_model.Model, place.transform, place.layer);
                    Lay(holder.transform, place.transform, visual, role == "result");
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
