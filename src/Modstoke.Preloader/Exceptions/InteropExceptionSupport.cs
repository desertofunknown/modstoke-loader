// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace Modstoke.Preloader
{
    // Public because generated wrappers and restored bodies execute in interop assemblies.
    // Those caches depend on this already-loaded Preloader assembly, not just Il2CppInterop.Runtime.
    public static class InteropExceptionSupport
    {
        private static readonly Lazy<Func<Il2CppException, Il2CppObjectBase>> RuntimeNativeException =
            new(BindRuntimeNativeException);

        public static void RaiseExceptionIfNecessary(IntPtr returnedException)
        {
            if (returnedException != IntPtr.Zero)
            {
                // The installed public constructor acquires the native GC handle; its finalizer
                // releases it. Root before Il2CppException formats the message or invokes its hook.
                throw new NativeException(new Il2CppObjectBase(returnedException));
            }
        }

        public static Exception WrapNativeException(Il2CppObjectBase exception) =>
            exception == null ? null : new NativeException(exception);

        public static void CollectNativeCleanupFailure(Exception error, ref Exception failure) =>
            failure = failure == null ? error : new AggregateException("Native argument cleanup failed.", failure, error);

        public static void FinishNativeCleanup(Exception primary, Exception failure, string method)
        {
            if (failure == null) return;
            if (primary == null)
                ExceptionDispatchInfo.Throw(failure);

            // Preserve native identity and catch/filter behavior when cleanup also fails.
            try
            {
                const string key = "Modstoke.NativeArgumentCleanupFailure";
                var previous = primary.Data[key] as Exception;
                primary.Data[key] = previous == null ? failure :
                    new AggregateException("Native argument cleanup failed.", previous, failure);
            }
            catch
            {
                // An exception with custom Data behavior must not replace the original failure.
            }
            InteropGenerationDiagnostics.Log(BepInEx.Logging.LogLevel.Error,
                "Native argument cleanup failed while unwinding " + method + ".", failure);
        }

        // Generated callers bind TException to the metadata-backed native System.Exception.
        // A boxed reference's CLR wrapper type alone cannot establish that it is throwable.
        public static Exception WrapNativeExceptionObject<TException>(Il2CppObjectBase exception)
            where TException : Il2CppObjectBase
        {
            if (exception == null)
                return null;
            try
            {
                var exceptionClass = Il2CppClassPointerStore<TException>.NativeClassPtr;
                if (exceptionClass == IntPtr.Zero)
                    throw new NotSupportedException("The native System.Exception class is unavailable.");
                if (!IL2CPP.il2cpp_class_is_assignable_from(exceptionClass, exception.ObjectClass))
                    throw new NotSupportedException("The thrown native object is not a System.Exception.");
                return new NativeException(exception);
            }
            finally
            {
                GC.KeepAlive(exception);
            }
        }

        // A true result means either a native match or a refusal to be raised at handler entry.
        // Throwing a diagnostic inside a CLR filter would silently turn it into a non-match.
        // Ordinary CLR infrastructure exceptions never match a native catch.
        public static bool TryMatchNativeException<T>(object exception, out Exception rejection)
            where T : Il2CppObjectBase
        {
            rejection = null;
            if (exception is not Il2CppException carrier)
                return false;

            try
            {
                var native = GetRetainedNativeException(carrier);
                if (native == null)
                {
                    rejection = new NotSupportedException(
                        "The Il2CppException has no supported retained native identity. Native catch matching requires " +
                        "a runtime with the native identity contract or wrappers generated with " +
                        "InteropExceptionSupport.RaiseExceptionIfNecessary.", carrier);
                    return true;
                }
                var catchClass = Il2CppClassPointerStore<T>.NativeClassPtr;
                if (catchClass == IntPtr.Zero)
                    throw new NotSupportedException("The native catch type has no IL2CPP class: " + typeof(T).FullName);
                return IL2CPP.il2cpp_class_is_assignable_from(catchClass, native.ObjectClass);
            }
            catch (Exception failure)
            {
                rejection = new InvalidOperationException(
                    "Cannot match the native exception against " + typeof(T).FullName + ".",
                    new AggregateException(carrier, failure));
                return true;
            }
            finally
            {
                GC.KeepAlive(carrier);
            }
        }

        public static T GetNativeException<T>(object exception, Exception rejection) where T : Il2CppObjectBase
        {
            if (rejection != null)
                ExceptionDispatchInfo.Throw(rejection);
            try
            {
                var native = GetRetainedNativeException(exception) ?? throw new NotSupportedException(
                    "The caught exception has no retained native identity.", exception as Exception);
                if (native is T retained)
                    return retained;
                // Cast constructs a wrapper for the same pointer with its own native GC handle.
                // It neither allocates a native exception nor copies its fields or stack trace.
                return native.Cast<T>();
            }
            finally
            {
                GC.KeepAlive(exception);
            }
        }

        public static T GetNativeFilterException<T>(object exception, Exception rejection, T filterValue)
            where T : Il2CppObjectBase
        {
            if (rejection != null)
                ExceptionDispatchInfo.Throw(rejection);
            try
            {
                var native = GetRetainedNativeException(exception) ?? throw new NotSupportedException(
                    "The caught exception has no retained native identity.", exception as Exception);
                // The predicate and handler see the same wrapper, including through ceq.
                // Validate against the carrier supplied by CLR EH, never a source predicate local.
                if (filterValue == null || filterValue.Pointer != native.Pointer)
                    throw new InvalidOperationException("The filter value does not retain the original native exception.");
                return filterValue;
            }
            finally
            {
                GC.KeepAlive(exception);
                GC.KeepAlive(filterValue);
            }
        }

        private static Il2CppObjectBase GetRetainedNativeException(object exception)
        {
            if (exception is NativeException native)
                return native.NativeObject;
            return exception is Il2CppException carrier ? RuntimeNativeException.Value?.Invoke(carrier) : null;
        }

        private static Func<Il2CppException, Il2CppObjectBase> BindRuntimeNativeException()
        {
            // The compile reference predates this contract. Bind only the known runtime owner,
            // never members on an exception's concrete type or a virtual identity accessor.
            var owner = typeof(Il2CppException);
            const BindingFlags flags = BindingFlags.Public | BindingFlags.DeclaredOnly;
            var version = owner.GetField("NativeIdentityVersion", flags | BindingFlags.Static);
            if (version == null || !version.IsLiteral || version.FieldType != typeof(int) ||
                version.GetRawConstantValue() is not int identityVersion || identityVersion != 1)
                return null;
            var method = owner.GetMethod("GetNativeExceptionObject", flags | BindingFlags.Instance,
                null, Type.EmptyTypes, null);
            if (method == null || method.DeclaringType != owner || method.IsVirtual || method.IsGenericMethod ||
                method.ReturnType != typeof(Il2CppObjectBase))
                return null;
            return method.CreateDelegate<Func<Il2CppException, Il2CppObjectBase>>();
        }

        public sealed class NativeException : Il2CppException
        {
            internal NativeException(Il2CppObjectBase exception) : base(exception.Pointer)
            {
                NativeObject = exception;
            }

            // Keep the existing wrapper and its native GC handle alive; do not copy native state.
            // The CLR EH record retains this carrier for rethrow; converted handler values also
            // own a native handle, so a value escaping the handler remains independently rooted.
            public Il2CppObjectBase NativeObject { get; }
        }
    }
}
