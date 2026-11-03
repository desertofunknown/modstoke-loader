// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using UnityEngine;

namespace Modstoke.Sons
{
    public static partial class Inventory
    {
        private static readonly Dictionary<IntPtr, InventoryModelReplacement> InventoryModelOwners = new();

        /// <summary>
        /// Replaces the visible geometry of currently loaded local inventory layout items.
        /// Requires the initialized Unity thread and a live visual prefab containing only
        /// transforms, mesh filters, mesh/skinned mesh renderers and optional colliders.
        /// Clone colliders are disabled; original hit areas, callbacks, ItemRenderable,
        /// ItemInstance and modules remain untouched. Scripts, animation and physics bodies
        /// are rejected before cloning. Native outline/gray-out/sheen caches are not rebound.
        /// The prefab root is placed at the loaded model's local origin with identity rotation
        /// and its authored scale. Child transforms and shared mesh/material assets are retained.
        /// This is a one-shot operation, not registration: dispose and explicitly reapply after
        /// native layout/model recreation. Held, world and crafting models are not changed.
        /// Retain and dispose the returned handle on the Unity thread before unloading assets.
        /// On failure, a non-null handle means rollback needs another TryRestore call.
        /// </summary>
        public static bool TryReplaceInventoryModel(GameObject prefab, int itemId,
            out InventoryModelReplacement replacement, out string status)
        {
            replacement = null;
            if (!TryGetCurrentInventory(itemId, out var inventory, out status)) return false;

            InventoryModelReplacement pending = null;
            try
            {
                InventoryModelReplacement.ValidatePrefab(prefab);
                int layer = LayerMask.NameToLayer("Inventory");
                if (layer < 0) throw new InvalidOperationException("The Inventory layer is unavailable.");

                var slots = new List<InventoryModelReplacement.ModelSlot>();
                var seen = new HashSet<IntPtr>();
                foreach (var group in inventory.GetComponentsInChildren<InventoryLayoutItemGroup>(true))
                {
                    if (!InventoryModelReplacement.Live(group) || group._itemId != itemId) continue;
                    var items = group._layoutItems;
                    if (items is null || items.WasCollected)
                        throw new InvalidOperationException("The inventory layout item list is unavailable.");
                    foreach (var item in items)
                    {
                        if (!InventoryModelReplacement.Live(item)
                            || !item.transform.IsChildOf(group.transform))
                            throw new InvalidOperationException("An inventory layout item is unavailable or detached.");
                        var renderables = item.GetComponentsInChildren<ItemRenderable>(true);
                        if (renderables.Length != 1 || !InventoryModelReplacement.Live(renderables[0]))
                            throw new InvalidOperationException("Each layout item must have exactly one native ItemRenderable.");
                        var renderable = renderables[0];
                        if (!seen.Add(renderable.Pointer)) continue;
                        if (InventoryModelOwners.ContainsKey(renderable.Pointer))
                            throw new InvalidOperationException("This layout model already has a replacement. Restore its handle first.");
                        slots.Add(new InventoryModelReplacement.ModelSlot(inventory, group, item, renderable));
                    }
                }
                if (slots.Count == 0)
                    throw new InvalidOperationException("No existing inventory layout models were found for this item ID.");

                pending = new InventoryModelReplacement(itemId, slots, Environment.CurrentManagedThreadId);
                foreach (var slot in slots) InventoryModelOwners.Add(slot.Key, pending);
                pending.Apply(prefab, layer);
                replacement = pending;
                status = "";
                return true;
            }
            catch (Exception error)
            {
                status = "Could not replace the inventory model: " + error.Message;
                if (pending is not null && !pending.TryRestore(out var rollbackStatus))
                {
                    replacement = pending;
                    status += " Rollback is incomplete; retain the returned handle and retry restoration. " + rollbackStatus;
                }
                return false;
            }
        }

        internal static void ReleaseInventoryModelOwner(IntPtr key, InventoryModelReplacement owner)
        {
            if (InventoryModelOwners.TryGetValue(key, out var current) && ReferenceEquals(current, owner))
                InventoryModelOwners.Remove(key);
        }
    }

    /// <summary>
    /// Retains exact native references and owns only its cloned visuals and renderer visibility
    /// overrides. No polling, finalizer, asynchronous cleanup or automatic rebinding is performed.
    /// Dispose is idempotent and throws if cleanup fails; TryRestore reports retryable failures.
    /// Disposing after a scene change does not search for or modify the successor inventory.
    /// </summary>
    public sealed class InventoryModelReplacement : IDisposable
    {
        private readonly List<ModelSlot> _slots;
        private readonly int _threadId;
        private GameObject _staging;

        internal InventoryModelReplacement(int itemId, List<ModelSlot> slots, int threadId)
        {
            ItemId = itemId;
            ModelCount = slots.Count;
            _slots = slots;
            _threadId = threadId;
        }

        public int ItemId { get; }
        /// <summary>Number of layout models originally captured, not a live availability check.</summary>
        public int ModelCount { get; }
        public bool IsDisposed { get; private set; }

        internal static bool Live(UnityEngine.Object value)
            => value is not null && !value.WasCollected && value;

        private static bool Same(UnityEngine.Object first, UnityEngine.Object second)
            => Live(first) && Live(second) && first.Pointer == second.Pointer;

        internal static void ValidatePrefab(GameObject prefab)
        {
            if (!Live(prefab)) throw new ArgumentException("A live visual prefab is required.");
            int rendererCount = 0;
            foreach (var component in prefab.GetComponentsInChildren<Component>(true))
            {
                if (!Live(component)) throw new ArgumentException("The visual prefab contains a missing component.");
                // A disabled script still receives Awake when activated. Reject scripts rather
                // than copying item controllers or trying to suppress their lifecycle later.
                if (component.TryCast<MeshRenderer>() is not null || component.TryCast<SkinnedMeshRenderer>() is not null)
                    rendererCount++;
                else if (component.TryCast<Transform>() is null && component.TryCast<MeshFilter>() is null
                    && component.TryCast<Collider>() is null)
                    throw new ArgumentException("The visual prefab contains an unsupported component: " + component.GetIl2CppType().FullName);
            }
            if (rendererCount == 0) throw new ArgumentException("The visual prefab has no mesh renderers.");
        }

        internal void Apply(GameObject prefab, int layer)
        {
            _staging = new GameObject("Inventory model staging");
            _staging.SetActive(false);
            foreach (var slot in _slots)
            {
                if (!slot.Matches()) throw new InvalidOperationException("A native inventory model changed before replacement.");
                slot.Clone = UnityEngine.Object.Instantiate(prefab, _staging.transform, false);
                slot.Clone.SetActive(false);
                foreach (var collider in slot.Clone.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
                foreach (var transform in slot.Clone.GetComponentsInChildren<Transform>(true)) transform.gameObject.layer = layer;
                var root = slot.Clone.transform;
                root.SetParent(slot.Loaded.transform, false);
                root.localPosition = Vector3.zero;
                root.localRotation = Quaternion.identity;
                foreach (var renderer in slot.Renderers) renderer.Hide();
                slot.Clone.SetActive(true);
            }
            UnityEngine.Object.Destroy(_staging);
            _staging = null;
        }

        /// <summary>
        /// Restores only captured renderers still belonging to the exact original native model,
        /// and only while their forceRenderingOff value still equals this handle's write.
        /// Changed/destroyed native owners are skipped, never resolved again by item ID/name.
        /// Removes owned clones still parented to the captured model or staging root. A clone
        /// reparented externally is relinquished. Native renderer/material caches are untouched.
        /// False retains unresolved cleanup and reservations for an explicit retry on this thread.
        /// </summary>
        public bool TryRestore(out string status)
        {
            status = "";
            if (IsDisposed) return true;
            if (Environment.CurrentManagedThreadId != _threadId)
            {
                status = "Inventory model restoration requires its creating Unity lifecycle thread.";
                return false;
            }

            List<string> failures = null;
            foreach (var slot in _slots)
            {
                try
                {
                    if (Live(slot.Clone))
                    {
                        var parent = slot.Clone.transform.parent;
                        if ((Live(slot.Loaded) && Same(parent, slot.Loaded.transform))
                            || (Live(_staging) && Same(parent, _staging.transform)))
                        {
                            slot.Clone.SetActive(false);
                            UnityEngine.Object.Destroy(slot.Clone);
                        }
                    }
                    slot.Clone = null;
                }
                catch (Exception error)
                {
                    (failures ??= new()).Add("Clone cleanup: " + error.Message);
                }

                foreach (var renderer in slot.Renderers)
                {
                    try
                    {
                        renderer.Restore(slot);
                    }
                    catch (Exception error)
                    {
                        (failures ??= new()).Add("Renderer restoration: " + error.Message);
                    }
                }
            }
            try
            {
                if (Live(_staging)) UnityEngine.Object.Destroy(_staging);
                _staging = null;
            }
            catch (Exception error)
            {
                (failures ??= new()).Add("Staging cleanup: " + error.Message);
            }

            if (failures != null)
            {
                status = string.Join(" ", failures);
                return false;
            }
            foreach (var slot in _slots) Inventory.ReleaseInventoryModelOwner(slot.Key, this);
            _slots.Clear();
            IsDisposed = true;
            return true;
        }

        public void Dispose()
        {
            if (!TryRestore(out var status)) throw new InvalidOperationException(status);
        }

        internal sealed class ModelSlot
        {
            private readonly TheForest.Items.Inventory.PlayerInventory _inventory;
            private readonly InventoryLayoutItemGroup _group;
            private readonly InventoryLayoutItem _item;
            private readonly ItemRenderable _renderable;
            private readonly Transform _renderableParent;
            private readonly Transform _loadedParent;
            internal readonly IntPtr Key;
            internal readonly GameObject Loaded;
            internal readonly List<RendererState> Renderers = new();
            internal GameObject Clone;

            internal ModelSlot(TheForest.Items.Inventory.PlayerInventory inventory, InventoryLayoutItemGroup group,
                InventoryLayoutItem item, ItemRenderable renderable)
            {
                _inventory = inventory;
                _group = group;
                _item = item;
                _renderable = renderable;
                Key = renderable.Pointer;
                Loaded = renderable._cachedLoadedObject;
                if (!Live(Loaded) || !Loaded.transform.IsChildOf(renderable.transform)
                    || Same(Loaded, renderable.gameObject))
                    throw new InvalidOperationException("A native inventory model is not loaded. Retry explicitly after it loads.");
                _renderableParent = renderable.transform.parent;
                _loadedParent = Loaded.transform.parent;
                foreach (var renderer in Loaded.GetComponentsInChildren<Renderer>(true))
                    if (Live(renderer)) Renderers.Add(new RendererState(renderer));
                if (Renderers.Count == 0) throw new InvalidOperationException("A native inventory model has no loaded renderers.");
                if (!Matches()) throw new InvalidOperationException("The native inventory model is detached from its layout.");
            }

            internal bool Matches()
                => Live(_inventory) && Live(_group) && Live(_item) && Live(_renderable) && Live(Loaded)
                    && _group.transform.IsChildOf(_inventory.transform) && _item.transform.IsChildOf(_group.transform)
                    && _renderable.transform.IsChildOf(_item.transform)
                    && Same(_renderable.transform.parent, _renderableParent)
                    && Same(_renderable._cachedLoadedObject, Loaded) && Same(Loaded.transform.parent, _loadedParent);
        }

        internal sealed class RendererState
        {
            private readonly Renderer _renderer;
            private readonly Transform _parent;
            private readonly bool _original;
            private bool _written;

            internal RendererState(Renderer renderer)
            {
                _renderer = renderer;
                _parent = renderer.transform.parent;
                _original = renderer.forceRenderingOff;
            }

            internal void Hide()
            {
                if (!Live(_renderer) || !Same(_renderer.transform.parent, _parent))
                    throw new InvalidOperationException("An original inventory renderer changed before replacement.");
                if (_original) return;
                _written = true;
                _renderer.forceRenderingOff = true;
            }

            internal void Restore(ModelSlot slot)
            {
                if (!_written) return;
                if (Live(_renderer) && slot.Matches() && Same(_renderer.transform.parent, _parent)
                    && _renderer.transform.IsChildOf(slot.Loaded.transform) && _renderer.forceRenderingOff)
                    _renderer.forceRenderingOff = _original;
                _written = false;
            }
        }
    }
}
