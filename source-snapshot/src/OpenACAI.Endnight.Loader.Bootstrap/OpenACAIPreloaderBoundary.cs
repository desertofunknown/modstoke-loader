// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection;

namespace OpenACAI.Endnight.Loader.Bootstrap
{
    // Bind once after resolver setup. Early bootstrap must not reference the typed runtime module.
    internal sealed class OpenACAIPreloaderBoundary
    {
        internal delegate IDisposable PrepareRuntimeInvokeHookDelegate(IntPtr original, Delegate callback, out Delegate trampoline, out Action apply);

        internal readonly Func<string> GetInteropAssemblyPath;
        internal readonly Func<string> GetGameAssemblyPath;
        internal readonly Func<object> AttachPreloaderLogging;
        internal readonly Action StartRuntimeLogging;
        internal readonly Action AttachNativeRuntimeLogging;
        internal readonly Func<string> CreateLoggingStateJson;
        internal readonly Action PrepareCompatibilityLogging;
        internal readonly Action<Exception> AbortPreloaderLogging;
        internal readonly Action InstallGameAssemblyResolver;
        internal readonly Action InitializeInterop;
        internal readonly Action RunAssemblyPatchers;
        internal readonly Action RunCompatibilityAssemblyPatchers;
        internal readonly Action PreloadInteropAssemblies;
        internal readonly PrepareRuntimeInvokeHookDelegate PrepareRuntimeInvokeHook;
        internal readonly Func<IntPtr, bool> CanUseCompatibilityRuntimeInvoke;
        internal readonly Func<string> CreateStateJson;

        internal OpenACAIPreloaderBoundary(Func<string, string, Type> loadRequiredType)
        {
            var type = loadRequiredType("OpenACAI.Endnight.Loader.Preloader",
                "OpenACAI.Endnight.Loader.Preloader.OpenACAIPreloaderServices");
            var getSharedInstance = type.GetMethod("GetSharedInstance", BindingFlags.Public | BindingFlags.Static,
                null, Type.EmptyTypes, null) ?? throw new MissingMethodException(type.FullName, "GetSharedInstance");
            var services = getSharedInstance.CreateDelegate<Func<object>>()();
            GetInteropAssemblyPath = Bind<Func<string>>(services, "get_InteropAssemblyPath");
            GetGameAssemblyPath = Bind<Func<string>>(services, "get_GameAssemblyPath");
            AttachPreloaderLogging = Bind<Func<object>>(services, "AttachPreloaderLogging");
            StartRuntimeLogging = Bind<Action>(services, "StartRuntimeLogging");
            AttachNativeRuntimeLogging = Bind<Action>(services, "AttachNativeRuntimeLogging");
            CreateLoggingStateJson = Bind<Func<string>>(services, "CreateLoggingStateJson");
            PrepareCompatibilityLogging = Bind<Action>(services, "PrepareCompatibilityLogging");
            AbortPreloaderLogging = Bind<Action<Exception>>(services, "AbortPreloaderLogging", typeof(Exception));
            InstallGameAssemblyResolver = Bind<Action>(services, "InstallGameAssemblyResolver");
            InitializeInterop = Bind<Action>(services, "InitializeInterop");
            RunAssemblyPatchers = Bind<Action>(services, "RunAssemblyPatchers");
            RunCompatibilityAssemblyPatchers = Bind<Action>(services, "RunCompatibilityAssemblyPatchers");
            PreloadInteropAssemblies = Bind<Action>(services, "PreloadInteropAssemblies");
            PrepareRuntimeInvokeHook = Bind<PrepareRuntimeInvokeHookDelegate>(services, "PrepareRuntimeInvokeHook",
                typeof(IntPtr), typeof(Delegate), typeof(Delegate).MakeByRefType(), typeof(Action).MakeByRefType());
            CanUseCompatibilityRuntimeInvoke = Bind<Func<IntPtr, bool>>(services, "CanUseCompatibilityRuntimeInvoke", typeof(IntPtr));
            CreateStateJson = Bind<Func<string>>(services, "CreateStateJson");
        }

        private static T Bind<T>(object instance, string name, params Type[] parameterTypes) where T : Delegate
        {
            var type = instance.GetType();
            var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, parameterTypes, null)
                ?? throw new MissingMethodException(type.FullName, name);
            return method.CreateDelegate<T>(instance);
        }
    }
}
