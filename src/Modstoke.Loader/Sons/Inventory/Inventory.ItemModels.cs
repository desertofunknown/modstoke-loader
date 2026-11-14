// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Modstoke.Loader;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using TheForest.Items.Inventory;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    public static partial class Inventory
    {
        private static readonly List<MatModel> MatModels = new();
        private static PlayerInventory _matInventory;
        private static float _matBindAt;
        private static bool _matFailureLogged;

        internal static void AddItemModel(ItemData item, ItemModel model) => MatModels.Add(new MatModel(item, model));

        /// <summary>How a modeled item stands on the current inventory mat, once built.</summary>
        /// <remarks>Its rotation and scale leave out the turn the gear page gives an item to fit a pocket.</remarks>
        internal static bool TryGetItemModelRow(int itemId, out Transform stance)
        {
            foreach (var model in MatModels)
                if (model.TryGetStance(itemId, out stance)) return true;
            stance = null;
            return false;
        }

        /// <summary>Keeps modeled items' mat layouts on the current inventory and in step with its stock.</summary>
        internal static void TickItemModels()
        {
            if (_ownerThreadId == 0 || Environment.CurrentManagedThreadId != _ownerThreadId) return;
            // The gear page also carries layouts mods build themselves, so it follows the inventory either way.
            TickGearPage();
            if (MatModels.Count == 0) return;
            try
            {
                if (!TryGetCurrentInventory(out var inventory, out _))
                {
                    ReleaseMatModels();
                    return;
                }
                if (!InventoryLayout.Same(inventory, _matInventory))
                {
                    ReleaseMatModels();
                    // Opening the inventory changes the view, not the player, so only a new inventory waits for the world;
                    // the black seconds before a new world appears count, so the models are built where nobody sees it.
                    if (!LocalPlayer.IsInWorld && !GameRuntime.IsWorldStarting || Time.unscaledTime < _matBindAt) return;
                    _matInventory = inventory;
                }
                // At most one model is built a frame; four together cost about 90 ms in one frame.
                var built = false;
                foreach (var model in MatModels) built |= model.Update(inventory, !built);
            }
            catch (Exception error)
            {
                ReleaseMatModels();
                _matBindAt = Time.unscaledTime + 5;
                if (_matFailureLogged) return;
                _matFailureLogged = true;
                RuntimeEntryPoint.LogSource?.LogWarning("Modeled items could not be shown on the inventory mat: " + error);
            }
        }

        private static void ReleaseMatModels()
        {
            if (_matInventory is null) return;
            _matInventory = null;
            _matBindAt = Time.unscaledTime + 1;
            foreach (var model in MatModels) model.Release();
        }

        private sealed class MatModel
        {
            private readonly ItemData _item;
            private readonly ItemModel _model;
            private InventoryLayout _layout;
            // A child of the layout root that keeps the item's rotation and scale as they are before any turn.
            private Transform _stance;
            private int _count = -1;
            private float _retryAt;
            private string _status;
            private bool _placementLogged, _shownLogged, _dropRequested;
            private InventoryPrimaryActionBinding _binding;
            private int _clickFrame = -1;

            private ItemMatAction Action => _model.MatAction == ItemMatAction.Drop && !_item._canBeSpawned
                ? ItemMatAction.None : _model.MatAction;

            // The game only drops held items, so a drop click closes the inventory and drops one once the player is
            // back in the world view. An equip click equips with the inventory open, as the game's own mat does.
            private void OnMatClick(InventoryLayoutItemGroup group)
            {
                if (_clickFrame == Time.frameCount || !TryGetCurrentInventory(out var inventory, out _)) return;
                var highlighted = false;
                foreach (var row in group._layoutItems)
                    if (InventoryLayout.Live(row) && row.IsHighlighted)
                    {
                        highlighted = true;
                        break;
                    }
                if (!highlighted || inventory._itemInstanceManager.GetItemCount(_item._id) < 1) return;
                _clickFrame = Time.frameCount;
                if (Action == ItemMatAction.Attach)
                {
                    // Attach with the inventory open, as native weapon-mod crafting does: the held weapon is put
                    // away, and closing the inventory re-equips it and publishes its mods to other players.
                    if (!WeaponAttachments.TryAttach(_item._id, out var attach))
                        RuntimeEntryPoint.LogSource?.LogWarning("Item " + _item._id + " could not be attached: " + attach);
                    return;
                }
                if (Action == ItemMatAction.Equip)
                {
                    if (!HeldItems.TryEquip(_item._id, out var equip))
                        RuntimeEntryPoint.LogSource?.LogInfo("Item " + _item._id + " was not equipped: " + equip);
                    return;
                }
                _dropRequested = true;
                if (inventory._currentView == PlayerInventory.PlayerViews.Inventory) inventory.Close();
            }

            private void DropOne()
            {
                // A strict server owns the client's inventory: the native inventory drop becomes its own request,
                // which removes the item and places the pickup server-side.
                if (BoltNetwork.isRunning && !BoltNetwork.isServer && PlayerInventoryEnforcement.StrictInventoryArmed)
                {
                    LocalPlayer.Inventory.DropItemFromInventory(_item._id, out _);
                    return;
                }
                // The native drop keeps the game's own custody checks; fall back to remove-and-spawn only when it
                // refused before touching the inventory.
                if (TryDropCountedItem(_item._id, out _, out var drop)) return;
                if (drop.DebitAttempted || drop.SpawnAttempted)
                {
                    RuntimeEntryPoint.LogSource?.LogWarning("Dropping item " + _item._id + " stopped partway: " + drop.Detail);
                    return;
                }
                var camera = Camera.main;
                if (camera == null || !camera) return;
                var view = camera.transform;
                var forward = Vector3.ProjectOnPlane(view.forward, Vector3.up);
                if (forward.sqrMagnitude < 0.01f) forward = view.up;
                var position = view.position + forward.normalized * 0.8f - Vector3.up * 0.4f;
                if (!TryRemoveItem(_item._id, 1, out var removal))
                {
                    RuntimeEntryPoint.LogSource?.LogWarning("Item " + _item._id + " could not be dropped (" + drop.Detail
                        + "), and removing it failed: " + removal.Detail);
                    return;
                }
                if (TrySpawnItem(_item._id, 1, position, Quaternion.LookRotation(forward), out _, out var spawn, false)) return;
                if (!spawn.InvocationAttempted && TryAddItem(_item._id, 1, out _)) return;
                RuntimeEntryPoint.LogSource?.LogWarning("Item " + _item._id + " was removed but its pickup did not spawn: " + spawn.Detail);
            }

            internal MatModel(ItemData item, ItemModel model)
            {
                _item = item;
                _model = model;
            }

            internal bool TryGetStance(int itemId, out Transform stance)
            {
                stance = null;
                if (_item._id != itemId || _layout == null || !InventoryLayout.Live(_layout.Item) || !InventoryLayout.Live(_stance))
                    return false;
                // The gear page turns the root to fit a pocket, and may turn it again when other items register.
                _stance.localRotation = Quaternion.Inverse(_layout.Root.transform.localRotation) * _model.MatRotation;
                stance = _stance;
                return true;
            }

            // True when this call built the model, so the caller can leave the others to later frames.
            internal bool Update(PlayerInventory inventory, bool mayBuild)
            {
                if (_dropRequested && inventory._currentView == PlayerInventory.PlayerViews.World)
                {
                    _dropRequested = false;
                    DropOne();
                }
                var built = false;
                if (Time.unscaledTime < _retryAt) return false;
                if (_layout == null)
                {
                    if (!mayBuild || !TryCreate(inventory)) return false;
                    built = true;
                }
                var count = inventory._itemInstanceManager.GetItemCount(_item._id);
                if (count == _count) return built;
                if (!_layout.TryRefresh(out var status))
                {
                    Wait(status);
                    Release();
                    return built;
                }
                _count = count;
                if (count > 0 && !_shownLogged)
                {
                    _shownLogged = true;
                    var renderers = _layout.Item.GetComponentsInChildren<Renderer>(true);
                    var visible = 0;
                    foreach (var renderer in renderers)
                        if (renderer.enabled && renderer.gameObject.activeInHierarchy && !renderer.forceRenderingOff) visible++;
                    RuntimeEntryPoint.LogSource?.LogInfo("Item " + _item._id + " is shown on the mat: group active "
                        + _layout.Root.activeInHierarchy + ", " + visible + " of " + renderers.Length + " renderers drawing, layer "
                        + LayerMask.LayerToName(_layout.Item.gameObject.layer) + ".");
                }
                return built;
            }

            private bool TryCreate(PlayerInventory inventory)
            {
                if (!InventoryLayout.Published(_item)) return false;
                if (!TryCreateGearLayout(_item, out _layout, out var status))
                {
                    // A joining player's mat can refuse layouts for a while.
                    Wait(status);
                    Release();
                    return false;
                }
                try
                {
                    var row = _layout.Item.transform;
                    row.localPosition = _model.MatOffset;
                    row.localRotation = _model.MatRotation;
                    row.localScale = Vector3.one * _model.MatScale;
                    _stance = new GameObject("Stance").transform;
                    _stance.SetParent(_layout.Root.transform, false);
                    _stance.localScale = row.localScale;
                    var visual = ItemModelCopies.CreateForMat(_model.Model, row, _layout.Root.layer);
                    // Its place is cut to the model as it lies, and the offset then moves it within that place.
                    var bounds = ItemModelCopies.LocalBounds(_layout.Root.transform, visual);
                    var category = _model.MatCategory is { } chosen && Enum.IsDefined(chosen) ? chosen
                        : _model.MatAction is ItemMatAction.Attach or ItemMatAction.Combine ? MatCategory.WeaponPart : MatCategory.Other;
                    SetGearPlace(_item._id, category, new Vector2(bounds.size.x, bounds.size.z),
                        new Vector2(bounds.center.x - _model.MatOffset.x, bounds.center.z - _model.MatOffset.z), bounds.size.y);
                    // Bound while the group is still inactive, as the binding requires. An item that combines
                    // keeps the game's own right click onto the crafting mat and takes no click of the SDK's.
                    if (Action is ItemMatAction.Drop or ItemMatAction.Attach or ItemMatAction.Equip
                        && !TryBindPrimaryAction(_layout.Group, OnMatClick, out _binding, out var bindStatus))
                        throw new InvalidOperationException(bindStatus);
                }
                catch (Exception error)
                {
                    Wait("Could not build the mat model: " + error.Message);
                    Release();
                    _retryAt = Time.unscaledTime + 30;
                    return false;
                }
                _count = -1;
                _status = null;
                if (!_placementLogged)
                {
                    _placementLogged = true;
                    var root = _layout.Root.transform;
                    RuntimeEntryPoint.LogSource?.LogInfo("Item " + _item._id + " has a place on the mod gear page at "
                        + root.localPosition.ToString("F3") + " under " + PathOf(root.parent) + ", world "
                        + root.position.ToString("F3") + "; parent up is world " + root.parent.up.ToString("F2")
                        + ", lossy scale " + root.lossyScale.ToString("F3") + ".");
                }
                return true;
            }

            private static string PathOf(Transform transform)
            {
                var path = transform.name;
                for (var parent = transform.parent; parent != null && parent; parent = parent.parent)
                    path = parent.name + "/" + path;
                return path;
            }

            private void Wait(string status)
            {
                _retryAt = Math.Max(_retryAt, Time.unscaledTime + 1);
                if (status == _status) return;
                _status = status;
                RuntimeEntryPoint.LogSource?.LogWarning("Item " + _item._id + " is waiting for the inventory mat: " + status);
            }

            internal void Release()
            {
                _count = -1;
                _dropRequested = false;
                if (_layout == null) return;
                // A bound group must be inactive before its binding releases, and unbound before disposal.
                if (!_layout.TryHide(out var status)
                    || _binding != null && !_binding.TryUnbind(out status))
                {
                    Wait("Could not release the mat layout: " + status);
                    return;
                }
                _binding = null;
                if (_layout.TryDispose(out status)) _layout = null;
                else Wait("Could not release the mat layout: " + status);
            }
        }
    }
}
