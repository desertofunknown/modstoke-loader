// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using HarmonyLib;
using Modstoke.Loader;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using TheForest.Items.Inventory;
using UnityEngine.InputSystem;

namespace Modstoke.Sons
{
    public static partial class Inventory
    {
        private static readonly Dictionary<IntPtr, InventoryPrimaryActionBinding> PrimaryActionBindings = new();
        private static bool _primaryActionHookAttempted, _primaryActionHookInstalled;
        private static string _primaryActionHookFailure = "";

        /// <summary>Replaces primary input for one caller-owned inventory group on the Unity lifecycle thread.</summary>
        /// <remarks>
        /// The group must belong to the current inventory and reference its exact published item definition.
        /// Bind before activating it. Only performed input invokes the callback; every input phase suppresses
        /// the group's native primary action. An earlier patch suppressing the original also suppresses this
        /// callback. The caller still checks selection, item counts and action permissions. No items are
        /// granted or consumed, and no equipment, save or server authority is provided. Retain the handle,
        /// deactivate the group before releasing it, and bind anew for each inventory lifetime.
        /// </remarks>
        public static bool TryBindPrimaryAction(InventoryLayoutItemGroup group,
            Action<InventoryLayoutItemGroup> callback, out InventoryPrimaryActionBinding binding, out string status)
        {
            binding = null;
            if (!TryGetCurrentInventory(out var inventory, out status)) return false;
            if (callback == null)
            {
                status = "An inventory primary-action callback is required.";
                return false;
            }

            try
            {
                if (!InventoryPrimaryActionBinding.Live(group) || group.gameObject.activeSelf)
                {
                    status = "A live, explicitly inactive inventory group is required.";
                    return false;
                }
                var pending = new InventoryPrimaryActionBinding(inventory, group, callback, _ownerThreadId);
                if (!pending.MatchesInventory(inventory))
                {
                    status = "The group must belong to the current inventory and its exact published item definition.";
                    return false;
                }
                if (PrimaryActionBindings.ContainsKey(group.Pointer))
                {
                    status = "This inventory group already has a primary-action binding. Release its handle first.";
                    return false;
                }
                if (!EnsurePrimaryActionHook(out status)) return false;
                if (!TryGetCurrentInventory(out var current, out status)) return false;
                if (!pending.MatchesInventory(current) || group.gameObject.activeSelf)
                {
                    status = "The inventory group changed before its primary action could be bound.";
                    return false;
                }
                PrimaryActionBindings.Add(group.Pointer, pending);
                binding = pending;
                status = "";
                return true;
            }
            catch (Exception error)
            {
                status = "Could not bind the inventory primary action: " + error.Message;
                return false;
            }
        }

        private static bool EnsurePrimaryActionHook(out string status)
        {
            if (!_primaryActionHookAttempted)
            {
                if (HarmonySupport.CreateState()?.SafeForHarmonyPatches != true)
                {
                    status = "Harmony support is not ready for inventory primary actions.";
                    return false;
                }
                _primaryActionHookAttempted = true;
                try
                {
                    var target = AccessTools.DeclaredMethod(typeof(InventoryLayoutItemGroup),
                        nameof(InventoryLayoutItemGroup.OnInteractWithItem), new[] { typeof(InputAction.CallbackContext) })
                        ?? throw new MissingMethodException(typeof(InventoryLayoutItemGroup).FullName,
                            nameof(InventoryLayoutItemGroup.OnInteractWithItem));
                    var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-inventory-primary-actions");
                    harmony.Patch(target, prefix: new HarmonyMethod(typeof(Inventory), nameof(BeforePrimaryAction))
                        { priority = Priority.Last });
                    _primaryActionHookInstalled = true;
                }
                catch (Exception error)
                {
                    _primaryActionHookFailure = "Could not install the inventory primary-action hook: " + error.Message;
                    RuntimeEntryPoint.LogSource?.LogWarning(_primaryActionHookFailure + "\n" + error);
                }
            }
            status = _primaryActionHookFailure;
            return _primaryActionHookInstalled;
        }

        private static bool BeforePrimaryAction(InventoryLayoutItemGroup __instance,
            InputAction.CallbackContext __0, bool __runOriginal)
        {
            if (__instance is null || !PrimaryActionBindings.TryGetValue(__instance.Pointer, out var binding))
                return true;
            return binding.BeforeInteract(__instance, __0, __runOriginal);
        }

        internal static bool PrimaryActionInventoryCurrent(InventoryPrimaryActionBinding binding)
            => TryGetCurrentInventory(out var inventory, out _) && binding.MatchesInventory(inventory);

        internal static void ReleasePrimaryAction(IntPtr key, InventoryPrimaryActionBinding binding)
        {
            if (PrimaryActionBindings.TryGetValue(key, out var owner) && ReferenceEquals(owner, binding))
                PrimaryActionBindings.Remove(key);
        }
    }

    /// <summary>Exclusive local input binding for one exact native inventory group and item definition.</summary>
    public sealed class InventoryPrimaryActionBinding : IDisposable
    {
        private PlayerInventory _inventory;
        private InventoryLayoutItemGroup _group;
        private ItemData _definition;
        private readonly IntPtr _key;
        private readonly int _instanceId, _threadId;
        private Action<InventoryLayoutItemGroup> _callback;
        private bool _failureReported;

        internal InventoryPrimaryActionBinding(PlayerInventory inventory, InventoryLayoutItemGroup group,
            Action<InventoryLayoutItemGroup> callback, int threadId)
        {
            _inventory = inventory;
            _group = group;
            _definition = group._itemData;
            _key = group.Pointer;
            _instanceId = group.GetInstanceID();
            _threadId = threadId;
            _callback = callback;
            ItemId = group._itemId;
        }

        public int ItemId { get; }
        public bool IsDisposed { get; private set; }

        internal static bool Live(UnityEngine.Object value)
            => value is not null && !value.WasCollected && value;

        internal bool MatchesInventory(PlayerInventory inventory)
        {
            if (!Live(_inventory) || !Live(inventory) || inventory.Pointer != _inventory.Pointer
                || !Live(_group) || _group.GetInstanceID() != _instanceId || !Live(_definition)
                || ItemId <= 0 || _group._itemId != ItemId || _definition._id != ItemId)
                return false;
            var owner = _group._inventory;
            var definition = _group._itemData;
            var cache = ItemDatabaseManager._itemsCache;
            return Live(owner) && owner.Pointer == _inventory.Pointer
                && (_group.transform.IsChildOf(_inventory.transform)
                    || Inventory.MatContainer(_inventory) is { } mat && _group.transform.IsChildOf(mat)
                    || Inventory.GearRoot(_inventory) is { } gear && _group.transform.IsChildOf(gear))
                && Live(definition) && definition.Pointer == _definition.Pointer
                && cache is not null && !cache.WasCollected
                && cache.TryGetValue(ItemId, out var published) && Live(published)
                && published.Pointer == _definition.Pointer;
        }

        internal bool BeforeInteract(InventoryLayoutItemGroup group, InputAction.CallbackContext context, bool runOriginal)
        {
            try
            {
                if (!Live(_group) || !Live(group) || group.GetInstanceID() != _instanceId) return true;
                if (Environment.CurrentManagedThreadId != _threadId || _callback == null) return false;
                if (!Inventory.PrimaryActionInventoryCurrent(this))
                {
                    _callback = null;
                    return false;
                }
                if (runOriginal && context.performed && _group.isActiveAndEnabled) _callback(_group);
            }
            catch (Exception error)
            {
                // A failed callback must never fall through into native use of a custom item.
                _callback = null;
                if (!_failureReported)
                {
                    _failureReported = true;
                    RuntimeEntryPoint.LogSource?.LogWarning("Inventory primary action failed for item " + ItemId
                        + "; the binding now suppresses input until released.\n" + error);
                }
            }
            return false;
        }

        /// <summary>
        /// Stops callbacks and releases the binding on its creating Unity thread. A live group must have
        /// activeSelf=false, even if its parent inventory is closed. On failure, native input remains
        /// suppressed; retain this handle and retry after deactivation. Do not reactivate without rebinding.
        /// </summary>
        public bool TryUnbind(out string status)
        {
            status = "";
            if (IsDisposed) return true;
            if (Environment.CurrentManagedThreadId != _threadId)
            {
                status = "Inventory primary-action release requires its creating Unity lifecycle thread.";
                return false;
            }
            _callback = null;
            try
            {
                if (Live(_group) && _group.GetInstanceID() == _instanceId && _group.gameObject.activeSelf)
                {
                    status = "Deactivate the bound inventory group before releasing its primary action.";
                    return false;
                }
                Inventory.ReleasePrimaryAction(_key, this);
                _inventory = null;
                _group = null;
                _definition = null;
                IsDisposed = true;
                return true;
            }
            catch (Exception error)
            {
                status = "Could not release the inventory primary action: " + error.Message;
                return false;
            }
        }

        public void Dispose()
        {
            if (!TryUnbind(out var status)) throw new InvalidOperationException(status);
        }
    }
}
