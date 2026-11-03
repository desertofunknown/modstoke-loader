// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace Modstoke.Preloader
{
    // Called by generated assemblies. DelegateSupport owns the reverse trampoline and its native target.
    public static class InteropDelegateSupport
    {
        public static TNative Convert<TNative>(Delegate callback) where TNative : Il2CppObjectBase
        {
            if (callback == null) return null;
            lock (Cache<TNative>.Gate)
            {
                try
                {
                    var callbackType = callback.GetType();
                    if (!Cache<TNative>.BridgeTypes.TryGetValue(callbackType, out var bridgeType))
                    {
                        bridgeType = Validate<TNative>(callbackType);
                        Cache<TNative>.BridgeTypes.Add(callbackType, bridgeType);
                    }
                    Cache<TNative>.Prune();
                    if (Cache<TNative>.Roots.TryGetValue(callback, out var own))
                    {
                        var existing = own.GetTarget();
                        if (existing != null) return existing;
                        Cache<TNative>.Roots.Remove(callback);
                    }
                    // Equivalent method-group factories must also unsubscribe the same native callback.
                    // Weak handles avoid a CLR -> native -> CLR root cycle when the subscriber releases it.
                    var hash = GetCallbackHash(callback);
                    if (Cache<TNative>.Entries.TryGetValue(hash, out var entries))
                    {
                        var node = entries.Last;
                        while (node != null)
                        {
                            var candidate = node;
                            node = node.Previous;
                            if (!candidate.Value.TryGetTarget(out var entry) ||
                                !entry.Callback.TryGetTarget(out var source))
                            {
                                Cache<TNative>.Remove(hash, candidate);
                                continue;
                            }
                            if (!callback.Equals(source)) continue;
                            var existing = entry.GetTarget();
                            if (existing == null)
                            {
                                Cache<TNative>.Remove(hash, candidate);
                                continue;
                            }
                            Cache<TNative>.Roots.Add(callback, entry);
                            return existing;
                        }
                    }
                    // CLR variance can leave the original delegate's runtime type unchanged. Bind its
                    // Invoke, not its last Method/Target, so multicast order and closed binding survive.
                    var bridge = bridgeType == callbackType ? callback : Delegate.CreateDelegate(bridgeType,
                        callback, callbackType.GetMethod("Invoke", BindingFlags.Public | BindingFlags.Instance), true);
                    var converted = DelegateSupport.ConvertDelegate<TNative>(bridge);
                    if (converted == null)
                        throw new InvalidOperationException("The native delegate bridge returned null for a non-null callback.");
                    try
                    {
                        var entry = new NativeEntry<TNative>(callback, converted.Pointer);
                        try { Cache<TNative>.Add(hash, callback, entry); }
                        catch
                        {
                            entry.Dispose();
                            throw;
                        }
                        return converted;
                    }
                    finally { GC.KeepAlive(converted); }
                }
                finally { GC.KeepAlive(callback); }
            }
        }

        private static int GetCallbackHash(Delegate callback)
        {
            var hash = new HashCode();
            hash.Add(callback.GetType());
            foreach (var invocation in Delegate.EnumerateInvocationList(callback))
            {
                hash.Add(invocation.GetType());
                hash.Add(invocation.Method);
                // Delegate.GetHashCode can ignore the target for fresh closures of the same method.
                var target = invocation.Target;
                hash.Add(target == null ? 0 : RuntimeHelpers.GetHashCode(target));
            }
            return hash.ToHashCode();
        }

        private static Type Validate<TNative>(Type managedType) where TNative : Il2CppObjectBase
        {
            if (!OperatingSystem.IsWindows() || IntPtr.Size != 8)
                throw new PlatformNotSupportedException("The delegate bridge is bound to the pinned Windows x64 runtime.");
            var invoke = managedType.GetMethod("Invoke", BindingFlags.Public | BindingFlags.Instance);
            ValidateInvoke(invoke);
            var nativeClass = Il2CppClassPointerStore<TNative>.NativeClassPtr;
            if (nativeClass == IntPtr.Zero)
                throw new NotSupportedException("The delegate has no native class identity: " + typeof(TNative).FullName + ".");
            var wrapperInvoke = typeof(TNative).GetMethod("Invoke", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            ValidateInvoke(wrapperInvoke);
            var parameters = wrapperInvoke.GetParameters();
            var sourceParameters = invoke.GetParameters();
            if (parameters.Length != sourceParameters.Length)
                throw new NotSupportedException("The callback and native delegate require the same parameter count.");
            var nativeInvoke = IL2CPP.il2cpp_class_get_method_from_name(nativeClass, "Invoke", parameters.Length);
            if (nativeInvoke == IntPtr.Zero || IL2CPP.il2cpp_method_get_param_count(nativeInvoke) != parameters.Length)
                throw new MissingMethodException("The native delegate has no matching Invoke declaration.");
            // The native trampoline still receives an exact signature. Only the CLR binding may vary.
            ValidateParameter(wrapperInvoke.ReturnParameter, IL2CPP.il2cpp_method_get_return_type(nativeInvoke), true);
            for (var index = 0; index < parameters.Length; index++)
            {
                ValidateParameter(parameters[index], IL2CPP.il2cpp_method_get_param(nativeInvoke, (uint)index), false);
                if (parameters[index].ParameterType.IsByRef &&
                    (parameters[index].IsOut && !parameters[index].IsIn) !=
                    (sourceParameters[index].IsOut && !sourceParameters[index].IsIn))
                    throw new NotSupportedException("The callback and native delegate require the same ref/out direction.");
            }

            if (!CanAssignReference(invoke.ReturnType, wrapperInvoke.ReturnType) ||
                parameters.Where((parameter, index) =>
                    !CanAssignReference(parameter.ParameterType, sourceParameters[index].ParameterType)).Any())
                throw new NotSupportedException("The callback requires an unsupported parameter or return conversion.");
            if (invoke.ReturnType == wrapperInvoke.ReturnType && !parameters.Where((parameter, index) =>
                    parameter.ParameterType != sourceParameters[index].ParameterType).Any())
                return managedType;

            if (parameters.Any(parameter => parameter.ParameterType.IsByRef))
                return ManagedBridgeTypes.Create(wrapperInvoke);

            var hasReturn = wrapperInvoke.ReturnType != typeof(void);
            var arguments = parameters.Select(parameter => parameter.ParameterType).ToList();
            if (hasReturn) arguments.Add(wrapperInvoke.ReturnType);
            var name = hasReturn ? "System.Func" : "System.Action";
            if (arguments.Count != 0) name += "`" + arguments.Count;
            var definition = typeof(Action).Assembly.GetType(name, true);
            return arguments.Count == 0 ? definition : definition.MakeGenericType(arguments.ToArray());
        }

        private static bool CanAssignReference(Type source, Type destination) => source == destination ||
            !source.IsByRef && !destination.IsByRef && !source.IsValueType && !destination.IsValueType &&
            destination.IsAssignableFrom(source);

        private static void ValidateInvoke(MethodInfo invoke)
        {
            if (invoke == null || invoke.IsStatic || invoke.ContainsGenericParameters ||
                (invoke.CallingConvention & (CallingConventions.VarArgs | CallingConventions.ExplicitThis)) != 0)
                throw new NotSupportedException("The delegate must have a closed non-vararg instance Invoke signature.");
            var parameters = invoke.GetParameters();
            if (parameters.Length > 16)
                throw new NotSupportedException("The supported callback contract has at most sixteen parameters.");
            ValidateManagedParameter(invoke.ReturnParameter, true);
            foreach (var parameter in parameters) ValidateManagedParameter(parameter, false);
        }

        private static void ValidateManagedParameter(ParameterInfo parameter, bool isReturn)
        {
            var type = parameter.ParameterType;
            var element = type.IsByRef ? type.GetElementType() : type;
            if (isReturn && type.IsByRef || element.IsPointer || element.IsFunctionPointer || element.IsByRefLike || element.ContainsGenericParameters ||
                parameter.GetRequiredCustomModifiers().Length != 0 || parameter.GetOptionalCustomModifiers().Length != 0 ||
                parameter.IsDefined(typeof(MarshalAsAttribute), false))
                throw new NotSupportedException("The callback has an unsupported reference, pointer or marshalling contract: " + type + ".");
            if (type == typeof(void) && !isReturn)
                throw new NotSupportedException("Void is not a callback parameter type.");
        }

        private static void ValidateParameter(ParameterInfo parameter, IntPtr nativeType, bool isReturn)
        {
            ValidateManagedParameter(parameter, isReturn);
            var type = parameter.ParameterType;
            if (nativeType == IntPtr.Zero || IL2CPP.il2cpp_type_is_byref(nativeType) != type.IsByRef)
                throw new NotSupportedException("The native callback has missing or mismatched reference passing metadata.");
            if (type.IsByRef) type = type.GetElementType();
            var nativeClass = Il2CppClassPointerStore.GetNativeClassPointer(type);
            if (nativeClass == IntPtr.Zero || nativeClass != IL2CPP.il2cpp_class_from_type(nativeType))
                throw new NotSupportedException("The callback parameter or return type has a different native identity: " + type + ".");
            if (type == typeof(void)) return;
            if (type == typeof(string)) return;
            if (!type.IsValueType)
            {
                if (!typeof(Il2CppObjectBase).IsAssignableFrom(type) ||
                    type.GetConstructor(new[] { typeof(IntPtr) }) == null)
                    throw new NotSupportedException("The callback reference needs a native wrapper with an IntPtr constructor: " + type + ".");
                var nativeValue = IL2CPP.il2cpp_class_is_valuetype(nativeClass);
                if (nativeValue && (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
                    RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
                    IL2CPP.il2cpp_class_is_generic(nativeClass) && !IL2CPP.il2cpp_class_is_inflated(nativeClass)))
                    throw new NotSupportedException("Native value callbacks require a Windows x64, fully constructed value proxy: " + type + ".");
                return;
            }

            // The pinned trampoline maps top-level Boolean to Byte. Char and nested Boolean do not
            // have that transformation; reject those layouts instead of trusting default CLR marshalling.
            if (type != typeof(bool) && !IsBlittableValue(type, 0))
                throw new NotSupportedException("The callback value has no proven blittable ABI: " + type + ".");
            uint alignment = 0;
            var size = type == typeof(bool) ? 1 : Marshal.SizeOf(type.IsEnum ? Enum.GetUnderlyingType(type) : type);
            if (!IL2CPP.il2cpp_class_is_valuetype(nativeClass) || IL2CPP.il2cpp_class_has_references(nativeClass) ||
                IL2CPP.il2cpp_class_value_size(nativeClass, ref alignment) != size)
                throw new NotSupportedException("The callback value layout differs from its native declaration: " + type + ".");
        }

        private static bool IsBlittableValue(Type type, int depth)
        {
            if (depth > 32 || type == typeof(bool) || type == typeof(char) || type.IsByRefLike ||
                type.ContainsGenericParameters || Nullable.GetUnderlyingType(type) != null)
                return false;
            if (type.IsEnum) return IsBlittableValue(Enum.GetUnderlyingType(type), depth + 1);
            if (type.IsPrimitive) return true;
            return type.IsValueType && (type.IsLayoutSequential || type.IsExplicitLayout) &&
                type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).All(field =>
                    !field.IsDefined(typeof(MarshalAsAttribute), false) && IsBlittableValue(field.FieldType, depth + 1));
        }

        private static class Cache<TNative> where TNative : Il2CppObjectBase
        {
            internal static readonly object Gate = new();
            internal static readonly Dictionary<Type, Type> BridgeTypes = new();
            internal static readonly ConditionalWeakTable<Delegate, NativeEntry<TNative>> Roots = new();
            internal static readonly Dictionary<int, LinkedList<WeakReference<NativeEntry<TNative>>>> Entries = new();
            private static readonly Queue<(int Hash, LinkedListNode<WeakReference<NativeEntry<TNative>>> Node)> pending = new();

            internal static void Add(int hash, Delegate callback, NativeEntry<TNative> entry)
            {
                var node = new LinkedListNode<WeakReference<NativeEntry<TNative>>>(new(entry));
                if (!Entries.TryGetValue(hash, out var entries))
                {
                    entries = new LinkedList<WeakReference<NativeEntry<TNative>>>();
                    Entries.Add(hash, entries);
                }
                try
                {
                    entries.AddLast(node);
                    pending.Enqueue((hash, node));
                    // Publish the exact-instance root only after the weak index is registered.
                    Roots.Add(callback, entry);
                }
                catch
                {
                    Remove(hash, node);
                    throw;
                }
            }

            internal static void Remove(int hash, LinkedListNode<WeakReference<NativeEntry<TNative>>> node)
            {
                var entries = node.List;
                entries.Remove(node);
                if (entries.Count == 0) Entries.Remove(hash);
            }

            internal static void Prune()
            {
                // Visit more nodes than one conversion can add, including on exact-instance hits.
                // Detached nodes are discarded, so neither empty buckets nor stale queue records accumulate.
                var remaining = Math.Min(4, pending.Count);
                while (remaining-- > 0)
                {
                    var item = pending.Dequeue();
                    if (item.Node.List == null) continue;
                    try
                    {
                        if (!item.Node.Value.TryGetTarget(out var entry) ||
                            !entry.Callback.TryGetTarget(out _) || !entry.IsAlive)
                            Remove(item.Hash, item.Node);
                    }
                    finally
                    {
                        if (item.Node.List != null) pending.Enqueue(item);
                    }
                }
            }
        }

        private static class ManagedBridgeTypes
        {
            private static readonly ModuleBuilder Module = AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName("Modstoke.ManagedDelegateBridges"), AssemblyBuilderAccess.Run)
                .DefineDynamicModule("Modstoke.ManagedDelegateBridges");
            private static int sequence;

            internal static Type Create(MethodInfo invoke)
            {
                lock (Module)
                {
                    var type = Module.DefineType("Callback_" + ++sequence,
                        TypeAttributes.Public | TypeAttributes.Sealed, typeof(MulticastDelegate));
                    type.DefineConstructor(MethodAttributes.Public | MethodAttributes.HideBySig |
                        MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, CallingConventions.Standard,
                        new[] { typeof(object), typeof(IntPtr) }).SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);
                    var parameters = invoke.GetParameters();
                    var method = type.DefineMethod("Invoke", MethodAttributes.Public | MethodAttributes.HideBySig |
                        MethodAttributes.NewSlot | MethodAttributes.Virtual, CallingConventions.HasThis,
                        invoke.ReturnType, parameters.Select(parameter => parameter.ParameterType).ToArray());
                    method.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);
                    for (var index = 0; index < parameters.Length; index++)
                        method.DefineParameter(index + 1, parameters[index].Attributes & (ParameterAttributes.In | ParameterAttributes.Out),
                            parameters[index].Name);
                    return type.CreateType();
                }
            }
        }

        private sealed class NativeEntry<TNative> where TNative : Il2CppObjectBase
        {
            internal readonly WeakReference<Delegate> Callback;
            private readonly NativeWeakHandle native;

            internal NativeEntry(Delegate callback, IntPtr pointer)
            {
                Callback = new WeakReference<Delegate>(callback);
                native = new NativeWeakHandle(pointer);
            }

            internal bool IsAlive
            {
                get
                {
                    try { return IL2CPP.il2cpp_gchandle_get_target(native.DangerousGetHandle()) != IntPtr.Zero; }
                    finally { GC.KeepAlive(this); }
                }
            }

            internal void Dispose() => native.Dispose();

            internal TNative GetTarget()
            {
                try
                {
                    var pointer = IL2CPP.il2cpp_gchandle_get_target(native.DangerousGetHandle());
                    return pointer == IntPtr.Zero ? null : IL2CPP.PointerToValueGeneric<TNative>(pointer, false, true);
                }
                finally { GC.KeepAlive(this); }
            }
        }

        private sealed class NativeWeakHandle : SafeHandle
        {
            internal NativeWeakHandle(IntPtr pointer) : base(IntPtr.Zero, true)
            {
                SetHandle(IL2CPP.il2cpp_gchandle_new_weakref(pointer, false));
                if (IsInvalid) throw new InvalidOperationException("Could not create the native delegate's weak lifetime handle.");
            }

            public override bool IsInvalid => handle == IntPtr.Zero;

            protected override bool ReleaseHandle()
            {
                IL2CPP.il2cpp_gchandle_free(handle);
                return true;
            }
        }
    }
}
