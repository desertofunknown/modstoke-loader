// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using Il2CppInterop.Common;
using global::Sons.Gameplay;
using global::Sons.Inventory;
using TheForest.Items.Inventory;
using TheForest.Utils;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryEnforcement
    {
        internal static int WorldWriterDepth => _writerDepth;
        internal static bool LocalDropReady => _hooksInstalled && Environment.CurrentManagedThreadId == _hookThread
            && _writerDepth == 0;
        private static int _worldBoundaryFailureLoad = -1;

        private static (MethodInfo Method, string Prefix)[] QualifyWorldHooks()
        {
            var pickup = typeof(PickUp);
            var group = typeof(InWorldLayoutItemGroup);
            var storage = typeof(StructureStorage);
            var context = new[] { typeof(InputAction.CallbackContext) };
            var storageArgs = new[] { typeof(int), typeof(int), typeof(ItemInstance) };
            var hooks = new (MethodInfo Method, string Prefix, int Rva, int Length, string Hash)[]
            {
                (AccessTools.Method(pickup, "Collect", Type.EmptyTypes), nameof(BeforeWorldPickup),
                    0x33561B0, 656, "F655409E39256D77C0DAA4DECAF88DAEB27A5B7F4FFDC2AEBAFF3504191B003C"),
                (AccessTools.Method(typeof(ClothingPickUp), "Collect", Type.EmptyTypes), nameof(BeforeWorldPickup),
                    0x334B3C0, 192, "FA6B5138FFBD72D5E2114BEBB8688B02B834A38AA63C7FBC80952482B9C0E6A3"),
                (AccessTools.Method(typeof(GatherablePickup), "Collect", Type.EmptyTypes), nameof(BeforeWorldPickup),
                    0x3350EE0, 32, "DB0C467CD0A8044A0130AB134E5C916EBCC903C9112CB0096DC4C8D5D69CD649"),
                (AccessTools.Method(typeof(ZiplinePickup), "Collect", Type.EmptyTypes), nameof(BeforeWorldPickup),
                    0x3362E50, 256, "2144BE07F610E7FF12CBABD8138ECE6B269F1D937905905DB9E31E449C42CFD1"),
                (AccessTools.Method(group, "AddItem", context), nameof(BeforeWorldStorageAdd),
                    0x31DD570, 1024, "4F37A14DDDC1AC2F4F541A7AF5771CEC3289F123882D5D2221B6826C9576853B"),
                (AccessTools.Method(group, "RemoveItem", context), nameof(BeforeWorldStorageRemove),
                    0x31DDAC0, 288, "506A1C5BD2F91E41D9DC5695C81CC54404F8DAAB4E2206764138F07F476ADF4B"),
                (AccessTools.Method(group, "RemoveItem", new[] { typeof(bool) }), nameof(BeforeWorldStorageRemoveExplicit),
                    0x31DD970, 336, "F77D8E472019C8B4EC974FAC7EFF9C874EFF340572E3621F9DDE02696ACFB4F9"),
                (AccessTools.Method(storage, "ClientRequestAddItems", storageArgs), nameof(BlockWorldStorageRequest),
                    0x31F0420, 1216, "46855C23ADEBF8206E33E491C0F916628A8BB57B8AA6CBC919952613D3BC067E"),
                (AccessTools.Method(storage, "ClientRequestRemoveItems", storageArgs), nameof(BlockWorldStorageRequest),
                    0x31F08E0, 1120, "8BEEF6104499C0C69D0C9B7B44BE54C251668E421811B528DD5E22806F16AA68")
            };
            var result = new (MethodInfo, string)[hooks.Length + 1];
            for (int i = 0; i < hooks.Length; i++)
            {
                var hook = hooks[i];
                QualifyBoundary(hook.Method, (typeof(void), hook.Rva, hook.Length, hook.Hash));
                result[i] = (hook.Method, hook.Prefix);
            }
            // Do not detour DroppedInventoryItemsPickup.Collect: its empty shared RVA is
            // not a unique action boundary. Death-bag collection has a different owner.
            var drop = AccessTools.Method(typeof(PlayerInventory), "DropItemInternal", new[]
                { typeof(int), typeof(ItemInstance), typeof(bool), typeof(GameObject).MakeByRefType(), typeof(int) });
            if (drop == null || !drop.IsStatic || drop.ReturnType != typeof(void) || !drop.GetParameters()[3].IsOut)
                throw new MissingMethodException("Static native drop boundary differs.");
            var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(drop);
            if (field?.GetValue(null) is not nint info || info == 0)
                throw new InvalidOperationException("Static native drop metadata unavailable.");
            nint entry = (nint)BitConverter.ToInt64(ReadNativeMemory(info, IntPtr.Size), 0);
            nint module = GetModuleHandle("GameAssembly.dll");
            if (module == 0 || entry != module + 0x30E7370 || Convert.ToHexString(SHA256.HashData(ReadNativeMemory(entry, 2064)))
                != "6E80C7F2D07EBBE9D5628E3321B035B185B8B3483EE8B126D9F17E788933250F")
                throw new InvalidOperationException("Static native drop body differs or is already patched.");
            result[^1] = (drop, nameof(BlockWorldDropInternal));
            return result;
        }

        private static bool BeforeWorldPickup(PickUp __instance, bool __runOriginal, out bool __state)
        {
            EnterWriter(out __state);
            try
            {
                if (InventoryStorage.HasWorldCustody
                    && __instance != null && !__instance.WasCollected && __instance
                    && InventoryStorage.IsWorldHeld(__instance.GetComponent<BoltEntity>())) return false;
                if (!ProtectsInventory(LocalPlayer.Inventory)) return true;
                if (__runOriginal && !IsNativeInventoryLifecycleHeld(LocalPlayer.Inventory)) _guard?.RequestWorldPickup(__instance);
            }
            catch (Exception error) { FailWorldBoundary("Pickup", error); }
            return false;
        }

        private static bool BeforeWorldStorageAdd(InWorldLayoutItemGroup __instance, bool __runOriginal, out bool __state)
        {
            EnterWriter(out __state);
            return CaptureWorldStorage(__instance, true, __runOriginal);
        }

        private static bool BeforeWorldStorageRemove(InWorldLayoutItemGroup __instance, bool __runOriginal, out bool __state)
        {
            EnterWriter(out __state);
            return CaptureWorldStorage(__instance, false, __runOriginal);
        }

        private static bool BeforeWorldStorageRemoveExplicit(InWorldLayoutItemGroup __instance, bool __0,
            bool __runOriginal, out bool __state)
        {
            EnterWriter(out __state);
            return CaptureWorldStorage(__instance, false, __runOriginal && !__0);
        }

        private static bool CaptureWorldStorage(InWorldLayoutItemGroup group, bool deposit, bool runOriginal)
        {
            try
            {
                if (InventoryStorage.HasWorldCustody
                    && group != null && !group.WasCollected && group && group._storage != null
                    && InventoryStorage.IsWorldHeld(group._storage._boltEntity, group._storage.StorageSlotIndex)) return false;
                if (!ProtectsInventory(LocalPlayer.Inventory)) return true;
                if (runOriginal && !IsNativeInventoryLifecycleHeld(LocalPlayer.Inventory)) _guard?.RequestWorldStorage(group, deposit);
            }
            catch (Exception error) { FailWorldBoundary("Storage", error); }
            return false;
        }

        private static bool BlockWorldStorageRequest(StructureStorage __instance, out bool __state)
        {
            EnterWriter(out __state);
            if (ProtectsInventory(LocalPlayer.Inventory)) return false;
            try
            {
                return !InventoryStorage.HasWorldCustody
                    || __instance == null || __instance.WasCollected || !__instance
                    || !InventoryStorage.IsWorldHeld(__instance._boltEntity, __instance.StorageSlotIndex);
            }
            catch (Exception error) { FailWorldBoundary("Storage request", error); return false; }
        }

        private static void FailWorldBoundary(string boundary, Exception error)
        {
            string reason = boundary + " boundary failed closed: " + error.Message;
            int load = SaveLifecycle.State.BeforeLoadCount;
            if (_worldBoundaryFailureLoad != load)
            {
                _worldBoundaryFailureLoad = load;
                try
                {
                    global::Modstoke.Loader.RuntimeEntryPoint.LogSource?.LogWarning(
                        reason + " (" + error.GetType().FullName
                        + "). Further world boundary failures will not be logged for this world generation.");
                }
                catch (Exception) { }
            }
            _guard?.FailWorldIntent(reason);
        }

        private readonly record struct DropWriterState(bool Entered, Inventory.StoredDrop Drop);

        private static bool BlockWorldDropInternal(int __0, ItemInstance __1, bool __2, ref GameObject __3,
            int __4, bool __runOriginal, out DropWriterState __state)
        {
            EnterWriter(out bool entered);
            __state = new(entered, null);
            var drop = Inventory.StoredDrop.Active;
            if (drop != null)
            {
                if (__runOriginal && drop.PermitSpawn(__0, __1, __2, __4, _writerDepth))
                { __state = new(entered, drop); return true; }
                __3 = null;
                return false;
            }
            if (!ProtectsInventory(LocalPlayer.Inventory)) return true;
            __3 = null;
            return false;
        }

        private static Exception AfterWorldDrop(DropWriterState __state, bool __runOriginal, Exception __exception)
        {
            try { __state.Drop?.ObserveSpawn(__runOriginal, __exception); }
            finally { if (__state.Entered) _writerDepth--; }
            return __exception;
        }
    }
}
