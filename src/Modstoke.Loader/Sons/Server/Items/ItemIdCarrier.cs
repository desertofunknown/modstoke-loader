// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Unity.IL2CPP.Hook;
using Bolt;
using HarmonyLib;
using Il2CppInterop.Common;
using Il2CppInterop.Runtime;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    /// <summary>
    /// Carries mod item ids through game events whose generated ItemId setters clamp them.
    /// Bolt sends these properties as full 32-bit integers and receivers read them unclamped; only the setters cap
    /// the value (PlayerAddItem at 2047, the storage add and remove events at 1023). The setters are wrapped so a
    /// reserved mod id is written into the event's storage after the original setter ran. Game item ids and
    /// unreserved values keep the original behaviour.
    /// </summary>
    internal static class ItemIdCarrier
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void IntegerSetter(nint receiver, int value, nint methodInfo);

        private const int PlayerAddItemClamp = 2047;
        private const int LowestClamp = 1023;
        private static readonly List<Setter> Installed = new();
        private static readonly Dictionary<(nint Meta, string Name), int> PropertySlots = new();
        private static int _installState;
        private static int _failureReported;
        private static int _elementSize;
        private static int _int0Offset = -1;

        /// <summary>Lets a diagnostic run one unreserved id through the patched setters on its own thread.</summary>
        [ThreadStatic] internal static int ProbeItemId;

        internal static bool Active => Volatile.Read(ref _installState) == 2;

        internal static void Install()
        {
            if (Interlocked.CompareExchange(ref _installState, 1, 0) != 0)
                return;
            try
            {
                if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                    throw new PlatformNotSupportedException("the patch needs the Windows x64 game");

                var targets = new (Type Type, int Clamp)[]
                {
                    (typeof(PlayerAddItem), PlayerAddItemClamp),
                    (typeof(ItemStorageAddItemEvent), LowestClamp),
                    (typeof(ItemStorageRemoveItemEvent), LowestClamp)
                };
                // Identical setter bodies can be folded into one native function, so one wrapper serves every
                // target class that shares an entry. Other classes calling that body pass straight through.
                var byEntry = new Dictionary<nint, Setter>();
                foreach (var (type, clamp) in targets)
                {
                    var entry = SetterEntry(type, out var nativeClass);
                    if (!byEntry.TryGetValue(entry, out var setter))
                        byEntry.Add(entry, setter = new Setter(entry));
                    setter.Classes[nativeClass] = clamp;
                }
                lock (Installed)
                {
                    foreach (var setter in byEntry.Values)
                    {
                        Installed.Add(setter);
                        setter.Install();
                    }
                }
                Volatile.Write(ref _installState, 2);
                RuntimeEntryPoint.LogSource?.LogInfo("Item id carrier wrapped " + byEntry.Count
                    + " event setter(s); mod item ids up to " + ItemIds.NetworkedMaxItemId + " can be sent.");
            }
            catch (Exception error)
            {
                // Applied wrappers stay rooted; they only forward when the carrier is inactive.
                Volatile.Write(ref _installState, 3);
                RuntimeEntryPoint.LogSource?.LogWarning("Item id carrier is not installed: " + error.Message
                    + ". Mod item ids above 1023 may arrive clamped in storage and grant events.");
            }
        }

        /// <summary>Sets PlayerAddItem.ItemId and confirms the event holds exactly that id.</summary>
        internal static bool TrySetItemId(PlayerAddItem request, int itemId)
        {
            request.ItemId = itemId;
            var current = request.ItemId;
            return current == itemId
                || TryWriteInteger(request, "ItemId", itemId, current) && request.ItemId == itemId;
        }

        /// <summary>
        /// Writes an integer property of a Bolt object straight into its storage slot. The slot must hold
        /// <paramref name="current"/> first, which confirms the computed address before anything is written.
        /// </summary>
        internal static unsafe bool TryWriteInteger(NetworkObj target, string propertyName, int value, int current)
        {
            if (!TryGetLayout(out var size, out var offset))
                return false;
            var storage = target?.Storage;
            var values = storage?.Values;
            var property = PropertySlot(target, propertyName);
            if (values == null || property < 0)
                return false;
            var index = target.OffsetStorage + property;
            if (index < 0 || index >= values.Length)
                return false;
            var slot = (int*)((byte*)values.Pointer + 4 * IntPtr.Size + (long)index * size + offset);
            if (*slot != current)
                return false;
            *slot = value;
            return true;
        }

        private static bool Carries(int itemId) =>
            itemId <= ItemIds.NetworkedMaxItemId
            && (itemId == ProbeItemId || ItemIds.TryGetOwner(itemId, out _));

        private static nint SetterEntry(Type type, out nint nativeClass)
        {
            var method = AccessTools.PropertySetter(type, "ItemId");
            var field = method == null ? null : Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
            if (field?.GetValue(null) is not nint methodInfo || methodInfo == 0)
                throw new MissingMethodException(type.Name + ".ItemId has no native setter");
            var entry = Marshal.ReadIntPtr(methodInfo);
            nativeClass = (nint)typeof(Il2CppClassPointerStore<>).MakeGenericType(type)
                .GetField(nameof(Il2CppClassPointerStore<object>.NativeClassPtr))!.GetValue(null)!;
            if (entry == 0 || nativeClass == 0)
                throw new InvalidOperationException(type.Name + ".ItemId setter or class is unavailable");
            return entry;
        }

        // NetworkValue is stored inline in the storage array; IL2CPP field offsets include the boxed object header.
        private static bool TryGetLayout(out int size, out int offset)
        {
            size = Volatile.Read(ref _elementSize);
            offset = Volatile.Read(ref _int0Offset);
            if (size > 0)
                return offset >= 0;
            var type = Il2CppClassPointerStore<NetworkValue>.NativeClassPtr;
            var field = type == 0 ? 0 : IL2CPP.il2cpp_class_get_field_from_name(type, "Int0");
            size = type == 0 ? 0 : IL2CPP.il2cpp_class_array_element_size(type);
            offset = field == 0 ? -1 : (int)IL2CPP.il2cpp_field_get_offset(field) - 2 * IntPtr.Size;
            if (size < sizeof(int) || size > 512 || offset < 0 || offset > size - sizeof(int))
                offset = -1;
            Volatile.Write(ref _int0Offset, offset);
            Volatile.Write(ref _elementSize, System.Math.Max(size, 1));
            return offset >= 0;
        }

        private static int PropertySlot(NetworkObj target, string propertyName)
        {
            // Events keep their own meta reference; plain Bolt objects use the base one.
            NetworkObj_Meta meta = target?.TryCast<Event>()?.Meta;
            meta ??= target?.Meta;
            if (meta == null)
                return -1;
            lock (PropertySlots)
            {
                if (PropertySlots.TryGetValue((meta.Pointer, propertyName), out var known))
                    return known;
            }
            var slot = -1;
            var properties = meta.Properties;
            for (int i = 0; properties != null && i < properties.Length; i++)
            {
                var integer = properties[i]?.Property?.TryCast<NetworkProperty_Integer>();
                if (integer != null && integer.PropertyName == propertyName)
                {
                    slot = integer.OffsetStorage;
                    break;
                }
            }
            lock (PropertySlots)
                PropertySlots[(meta.Pointer, propertyName)] = slot;
            return slot;
        }

        private static void ReportFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _failureReported, 1) != 0)
                return;
            try { RuntimeEntryPoint.LogSource?.LogWarning("Item id carrier skipped an event: " + error.Message); }
            catch { }
        }

        // Keep every detour, callback and trampoline rooted once applied.
        private sealed class Setter
        {
            private readonly nint _entry;
            private INativeDetour _detour;
            private IntegerSetter _callback, _original;

            internal Setter(nint entry) => _entry = entry;

            internal Dictionary<nint, int> Classes { get; } = new();

            internal void Install()
            {
                _callback = Invoke;
                _detour = INativeDetour.Create(_entry, _callback);
                Volatile.Write(ref _original, _detour.GenerateTrampoline<IntegerSetter>());
                _detour.Apply();
            }

            private void Invoke(nint receiver, int value, nint methodInfo)
            {
                Volatile.Read(ref _original)(receiver, value, methodInfo);
                if (value <= LowestClamp || receiver == 0 || !Active)
                    return;
                try
                {
                    if (Classes.TryGetValue(IL2CPP.il2cpp_object_get_class(receiver), out var clamp)
                        && value > clamp && Carries(value))
                        TryWriteInteger(new NetworkObj(receiver), "ItemId", value, clamp);
                }
                catch (Exception error)
                {
                    ReportFailure(error);
                }
            }
        }
    }
}
