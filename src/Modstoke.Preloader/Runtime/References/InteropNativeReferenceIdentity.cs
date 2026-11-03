// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection.Emit;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.Runtime;

namespace Modstoke.Preloader
{
    public static class InteropNativeReferenceIdentity
    {
        /// <summary>Copies a native reference into an independently rooted wrapper, preserving null.</summary>
        public static T CopyOwned<T>(T value) where T : Il2CppObjectBase
        {
            if (value is null) return null;
            var handle = InteropGenericValueSupport.PinReferenceArgument(value);
            try { return WrapOwned<T>(InteropGenericValueSupport.PinnedReferenceArgumentAddress(handle)); }
            finally { InteropNativeValueStorage.ReleaseArgument(handle); GC.KeepAlive(value); }
        }

        // The caller must retain its pin until this constructor has acquired the new strong handle.
        internal static T WrapOwned<T>(IntPtr pointer) where T : Il2CppObjectBase => OwnedWrapper<T>.Create(pointer);

        private static class OwnedWrapper<T> where T : Il2CppObjectBase
        {
            internal static readonly Func<IntPtr, T> Create = Bind();

            private static Func<IntPtr, T> Bind()
            {
                var constructor = typeof(T).GetConstructor(new[] { typeof(IntPtr) }) ??
                    throw new NotSupportedException("An owned native reference requires its generated pointer constructor.");
                // Pool/cast helpers may return an injected object with only a weak native handle.
                var method = new DynamicMethod("OwnedReference", typeof(T), new[] { typeof(IntPtr) });
                var il = method.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Newobj, constructor);
                il.Emit(OpCodes.Ret);
                return method.CreateDelegate<Func<IntPtr, T>>();
            }
        }

        internal static T WrapReturned<T>(IntPtr pointer) where T : Il2CppObjectBase
        {
            if (pointer == IntPtr.Zero) return null;
            // A concurrent writer may replace the slot before wrapper creation finishes.
            var handle = IL2CPP.il2cpp_gchandle_new(pointer, true);
            if (handle == IntPtr.Zero)
                throw new InvalidOperationException("The returned native reference could not be rooted.");
            try { return Il2CppObjectPool.Get<T>(IL2CPP.il2cpp_gchandle_get_target(handle)); }
            finally { IL2CPP.il2cpp_gchandle_free(handle); }
        }

        public static bool AreEqual(Il2CppObjectBase left, Il2CppObjectBase right)
        {
            try
            {
                // IL reference identity is neither wrapper identity nor Unity's overloaded null test.
                return IL2CPP.Il2CppObjectBaseToPtr(left) == IL2CPP.Il2CppObjectBaseToPtr(right);
            }
            finally
            {
                GC.KeepAlive(left);
                GC.KeepAlive(right);
            }
        }
    }
}
