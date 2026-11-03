// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection;

namespace Modstoke.Bootstrap
{
    // Bind once after resolver setup. Early bootstrap must not reference the typed runtime module.
    internal sealed class PreloaderBoundary
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

        internal PreloaderBoundary(Func<string, string, Type> loadRequiredType)
        {
            ArgumentNullException.ThrowIfNull(loadRequiredType);
            var type = loadRequiredType("Modstoke.Preloader",
                "Modstoke.Preloader.PreloaderServices");
            var getSharedInstance = type.GetMethod("GetSharedInstance", BindingFlags.Public | BindingFlags.Static,
                null, Type.EmptyTypes, null) ?? throw new MissingMethodException(type.FullName, "GetSharedInstance");
            var services = CreateDelegate<Func<object>>(getSharedInstance, null)()
                ?? throw new InvalidOperationException(type.AssemblyQualifiedName + ".GetSharedInstance returned null.");
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
            return CreateDelegate<T>(method, instance);
        }

        private static T CreateDelegate<T>(MethodInfo method, object instance) where T : Delegate
        {
            try
            {
                return instance == null ? method.CreateDelegate<T>() : method.CreateDelegate<T>(instance);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException("Preloader binding failed for "
                    + method.DeclaringType?.AssemblyQualifiedName + "." + method
                    + "; expected delegate " + typeof(T).FullName + ".", ex);
            }
        }
    }
}
