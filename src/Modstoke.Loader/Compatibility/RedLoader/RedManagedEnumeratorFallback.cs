// SPDX-License-Identifier: GPL-3.0-or-later
// Managed enumerator fallback adapted from RedLoader and BepInEx.
// Copyright (c) RedLoader and BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppSystem;
using RedLoader.Unity.IL2CPP.Utils.Collections;
using IntPtr = System.IntPtr;
using NotSupportedException = System.NotSupportedException;
using Type = System.Type;

namespace Modstoke.Loader;

internal sealed class RedManagedEnumeratorFallback : IManagedEnumeratorFallback
{
    private static readonly RedManagedEnumeratorFallback Driver = new();
    private static readonly ConditionalWeakTable<Il2CppManagedEnumerator, ManagedEnumerator> Enumerators = new();

    private RedManagedEnumeratorFallback() { }

    internal static IManagedEnumeratorFallback Create() => Driver;

    public Object Current(Il2CppManagedEnumerator wrapper)
    {
        return GetEnumerator(wrapper).Current;
    }

    public bool MoveNext(Il2CppManagedEnumerator wrapper)
    {
        return GetEnumerator(wrapper).MoveNext();
    }

    public void Reset(Il2CppManagedEnumerator wrapper)
    {
        GetEnumerator(wrapper).Reset();
    }

    private static ManagedEnumerator GetEnumerator(Il2CppManagedEnumerator wrapper)
        => Enumerators.GetValue(wrapper, value => ManagedEnumerator.CreateRedLoader(value.enumerator));

    public System.Func<object, Object> GetValueBoxer(Type t)
    {
        if (Il2CppManagedEnumerator.boxers.TryGetValue(t, out var conv))
            return conv;

        var dm = new DynamicMethod($"Il2CppUnbox_{t.FullDescription()}", typeof(Object),
                                   new[] { typeof(object) });
        var il = dm.GetILGenerator();
        var loc = il.DeclareLocal(t);
        var classField = typeof(Il2CppClassPointerStore<>).MakeGenericType(t)
                                                          .GetField(nameof(Il2CppClassPointerStore<int>
                                                                               .NativeClassPtr));
        il.Emit(OpCodes.Ldsfld, classField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Unbox_Any, t);
        il.Emit(OpCodes.Stloc, loc);
        il.Emit(OpCodes.Ldloca, loc);
        il.Emit(OpCodes.Call,
                typeof(Il2CppInterop.Runtime.IL2CPP).GetMethod(nameof(Il2CppInterop.Runtime.IL2CPP.il2cpp_value_box)));
        il.Emit(OpCodes.Newobj, typeof(Object).GetConstructor(new[] { typeof(IntPtr) }));
        il.Emit(OpCodes.Ret);

        var converter = dm.CreateDelegate(typeof(System.Func<object, Object>)) as System.Func<object, Object>;
        Il2CppManagedEnumerator.boxers[t] = converter;
        return converter;
    }

    public Object ManagedToIl2CppObject(object obj)
    {
        var t = obj.GetType();
        if (obj is string s)
            return new Object(Il2CppInterop.Runtime.IL2CPP.ManagedStringToIl2Cpp(s));
        if (t.IsPrimitive)
            return Il2CppManagedEnumerator.GetValueBoxer(t)(obj);
        throw new NotSupportedException($"Type {t} cannot be converted directly to an Il2Cpp object");
    }
}
