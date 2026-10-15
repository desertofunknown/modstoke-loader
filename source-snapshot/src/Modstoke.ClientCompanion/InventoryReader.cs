using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;
using OpenACAI.Sons;

namespace Modstoke.ClientCompanion;

internal static class InventoryReader
{
    internal static bool TryRead(out Dictionary<int, int> items)
    {
        items = new Dictionary<int, int>();
        try
        {
            object? inventory = CurrentInventory();
            if (inventory is not UnityEngine.Object liveInventory || liveInventory == null ||
                inventory is not Il2CppObjectBase nativeInventory || nativeInventory.Pointer == IntPtr.Zero)
                return false;
            IntPtr inventoryPointer = nativeInventory.Pointer;

            if (!Call(inventory, "GetAllItems", out object? allItems) || allItems is not Il2CppObjectBase nativeItems)
                return false;
            Type mapType = allItems.GetType();
            Type[] mapArguments = mapType.GetGenericArguments();
            if (!mapType.IsGenericType ||
                mapType.GetGenericTypeDefinition() != typeof(Il2CppSystem.Collections.Generic.IReadOnlyDictionary<,>) ||
                mapArguments.Length != 2 || mapArguments[0] != typeof(int)) return false;

            Type pairType = typeof(Il2CppSystem.Collections.Generic.KeyValuePair<,>).MakeGenericType(mapArguments);
            object? countView = CastInterface(nativeItems,
                typeof(Il2CppSystem.Collections.Generic.IReadOnlyCollection<>).MakeGenericType(pairType));
            object? enumerableView = CastInterface(nativeItems,
                typeof(Il2CppSystem.Collections.Generic.IEnumerable<>).MakeGenericType(pairType));
            if (countView == null || enumerableView == null || !EntryCount(countView, out int beforeCount) ||
                !Call(enumerableView, "GetEnumerator", out object? enumerator) ||
                enumerator is not Il2CppObjectBase nativeEnumerator) return false;

            var ids = new HashSet<int>();
            int visited = 0;
            try
            {
                var mover = nativeEnumerator.TryCast<Il2CppSystem.Collections.IEnumerator>();
                if (mover == null) return false;
                while (true)
                {
                    if (!Call(mover, "MoveNext", out object? moved) || moved is not bool movedValue) return false;
                    if (!movedValue) break;
                    if (visited >= beforeCount || !Call(enumerator, "get_Current", out object? current) ||
                        current == null || !Call(current, "get_Key", out object? key) ||
                        key is not int id || id <= 0 || !ids.Add(id)) return false;
                    visited++;
                }
            }
            finally
            {
                var disposable = nativeEnumerator.TryCast<Il2CppSystem.IDisposable>();
                disposable?.Dispose();
            }
            if (visited != beforeCount || !EntryCount(countView, out int afterCount) || afterCount != beforeCount)
                return false;

            foreach (int id in ids)
            {
                if (!OpenACAISonsInventory.TryGetItemCount(id, out int count, out _, true, false) || count < 0)
                    return false;
                if (count > 0) items.Add(id, count);
            }
            if (!EntryCount(countView, out afterCount) || afterCount != beforeCount ||
                CurrentInventory() is not Il2CppObjectBase currentInventory || currentInventory.Pointer != inventoryPointer)
                return false;
            return true;
        }
        catch
        {
            items.Clear();
            return false;
        }
    }

    private static object? CurrentInventory()
    {
        Type? localPlayer = FindType("TheForest.Utils.LocalPlayer") ?? FindType("LocalPlayer");
        if (localPlayer == null) return null;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        foreach (string member in new[] { "Inventory", "_Inventory_k__BackingField" })
        {
            object? found = localPlayer.GetProperty(member, flags)?.GetValue(null) ??
                localPlayer.GetField(member, flags)?.GetValue(null);
            if (found != null) return found;
        }
        return localPlayer.GetMethod("get_Inventory", flags, null, Type.EmptyTypes, null)?.Invoke(null, null);
    }

    private static Type? FindType(string name)
    {
        Type? direct = Type.GetType(name, false);
        if (direct != null) return direct;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                Type? found = assembly.GetType(name, false);
                if (found != null) return found;
            }
            catch { }
        }
        return null;
    }

    private static object? CastInterface(Il2CppObjectBase instance, Type interfaceType)
    {
        MethodInfo? cast = typeof(Il2CppObjectBase).GetMethod("TryCast",
            BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        return cast?.IsGenericMethodDefinition == true ? cast.MakeGenericMethod(interfaceType).Invoke(instance, null) : null;
    }

    private static bool EntryCount(object entries, out int count)
    {
        count = 0;
        if (!Call(entries, "get_Count", out object? result) || result is not int value || value < 0) return false;
        count = value;
        return true;
    }

    private static bool Call(object instance, string name, out object? result)
    {
        result = null;
        for (Type? type = instance.GetType(); type != null; type = type.BaseType)
        {
            MethodInfo? method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (method == null || method.ContainsGenericParameters) continue;
            result = method.Invoke(instance, null);
            return true;
        }
        return false;
    }
}
