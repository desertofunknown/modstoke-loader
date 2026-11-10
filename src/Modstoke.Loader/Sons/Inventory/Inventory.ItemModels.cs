// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Endnight.Rendering;
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

        /// <summary>Keeps modeled items' mat layouts on the current inventory and in step with its stock.</summary>
        internal static void TickItemModels()
        {
            if (MatModels.Count == 0 || _ownerThreadId == 0 || Environment.CurrentManagedThreadId != _ownerThreadId)
                return;
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
                    // Opening the inventory changes the view, not the player, so only a new inventory waits for the world.
                    if (!LocalPlayer.IsInWorld || Time.unscaledTime < _matBindAt) return;
                    _matInventory = inventory;
                }
                foreach (var model in MatModels) model.Update(inventory);
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
            private int _count = -1;
            private float _retryAt;
            private string _status;
            private bool _placementLogged, _shownLogged;

            internal MatModel(ItemData item, ItemModel model)
            {
                _item = item;
                _model = model;
            }

            internal void Update(PlayerInventory inventory)
            {
                if (Time.unscaledTime < _retryAt || _layout == null && !TryCreate(inventory)) return;
                var count = inventory._itemInstanceManager.GetItemCount(_item._id);
                if (count == _count) return;
                if (!_layout.TryRefresh(out var status))
                {
                    Wait(status);
                    Release();
                    return;
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
            }

            private bool TryCreate(PlayerInventory inventory)
            {
                if (!InventoryLayout.Published(_item)) return false;
                InventoryLayoutItemGroup anchor = null;
                foreach (var group in inventory.GetComponentsInChildren<InventoryLayoutItemGroup>(true))
                    if (InventoryLayout.Live(group) && group._itemId == _model.MatAnchorItemId)
                    {
                        anchor = group;
                        break;
                    }
                if (anchor == null)
                {
                    Wait("Anchor item " + _model.MatAnchorItemId + " has no group on this inventory mat ("
                        + AnchorElsewhere(_model.MatAnchorItemId) + ").");
                    return false;
                }
                if (!TryCreateLayout(_item, anchor, out _layout, out var status))
                {
                    // A joining player's mat can refuse layouts for a while.
                    Wait(status);
                    Release();
                    return false;
                }
                try
                {
                    _layout.Root.transform.localPosition += _model.MatOffset;
                    var row = _layout.Item.transform;
                    row.localRotation = _model.MatRotation;
                    row.localScale = Vector3.one * _model.MatScale;
                    var visual = ItemModelCopies.Create(_model.Model, row, _layout.Root.layer);
                    foreach (var renderer in visual.GetComponentsInChildren<Renderer>(true))
                    {
                        // Native initialization hooks the mouse on renderer objects, so each gets its own hit box.
                        var box = renderer.gameObject.AddComponent<BoxCollider>();
                        var bounds = renderer is SkinnedMeshRenderer skinned ? skinned.localBounds
                            : renderer.GetComponent<MeshFilter>() is { } filter && filter && filter.sharedMesh is { } mesh && mesh
                                ? mesh.bounds : new Bounds(Vector3.zero, Vector3.one * 0.05f);
                        box.center = bounds.center;
                        box.size = Vector3.Max(bounds.size, Vector3.one * 0.005f);
                        // The new-item sheen swaps authored materials for a flat white until hover.
                        renderer.gameObject.AddComponent<ExcludeRenderableFrom>()._excludedFrom = ExcludeRenderableFrom.Type.Sheen;
                    }
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
                    RuntimeEntryPoint.LogSource?.LogInfo("Item " + _item._id + " has a mat layout under " + PathOf(root.parent)
                        + " at local " + root.localPosition.ToString("F3") + ", world " + root.position.ToString("F3")
                        + "; anchor item " + _model.MatAnchorItemId + " at local " + anchor.transform.localPosition.ToString("F3")
                        + "; parent up is world " + root.parent.up.ToString("F2") + ", lossy scale " + root.lossyScale.ToString("F3") + ".");
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

            // Where the anchor's group went, when it is not under the current inventory.
            private static string AnchorElsewhere(int itemId)
            {
                foreach (var group in Resources.FindObjectsOfTypeAll<InventoryLayoutItemGroup>())
                    if (InventoryLayout.Live(group) && group._itemId == itemId)
                        return "a group exists at " + PathOf(group.transform);
                return "no group exists anywhere";
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
                if (_layout == null) return;
                if (_layout.TryDispose(out var status)) _layout = null;
                else Wait("Could not release the mat layout: " + status);
            }
        }
    }
}
