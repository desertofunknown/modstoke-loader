// SPDX-License-Identifier: GPL-3.0-or-later
// Type registration behavior adapted from RedLoader's RegisterTypeInIl2Cpp, derived from MelonLoader.
// Copyright (c) RedLoader and MelonLoader contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime.Injection;
using RedLoader;

namespace Modstoke.Loader;

internal static class RedLoaderTypeRegistration
{
    private static readonly object SyncRoot = new();
    private static readonly HashSet<Assembly> RegisteringAssemblies = new(ReferenceEqualityComparer.Instance);

    internal static void RegisterAssembly(Assembly asm)
    {
        ArgumentNullException.ThrowIfNull(asm);
        lock (SyncRoot)
        {
            if (!RegisterTypeInIl2Cpp.ready)
            {
                if (!RegisterTypeInIl2Cpp.registrationQueue.Exists(pending => ReferenceEquals(pending, asm)))
                    RegisterTypeInIl2Cpp.registrationQueue.Add(asm);
                return;
            }

            if (!RegisteringAssemblies.Add(asm))
                return;
        }

        try
        {
            foreach (var type in GetTypes(asm))
            {
                if (type == null)
                    continue;

                try
                {
                    var attributes = type.GetCustomAttributes(typeof(RegisterTypeInIl2Cpp), false);
                    if (attributes.Length == 0 || attributes[0] is not RegisterTypeInIl2Cpp attribute)
                        continue;

                    if (!ClassInjector.IsTypeRegisteredInIl2Cpp(type))
                        ClassInjector.RegisterTypeInIl2Cpp(type, new RegisterTypeOptions { LogSuccess = attribute.LogSuccess });
                }
                catch (Exception exception)
                {
                    ReportFailure("register type", asm, type, exception);
                }
            }
        }
        finally
        {
            lock (SyncRoot)
                RegisteringAssemblies.Remove(asm);
        }
    }

    internal static void SetReady()
    {
        Assembly[] pending;
        lock (SyncRoot)
        {
            if (RegisterTypeInIl2Cpp.ready)
                return;

            // Detach a stable queue before native registration or log callbacks can reenter.
            pending = RegisterTypeInIl2Cpp.registrationQueue?.ToArray();
            RegisterTypeInIl2Cpp.registrationQueue = null;
            RegisterTypeInIl2Cpp.ready = true;
        }

        if (pending == null)
            return;

        foreach (var asm in pending)
        {
            try
            {
                RegisterAssembly(asm);
            }
            catch (Exception exception)
            {
                ReportFailure("register assembly", asm, null, exception);
            }
        }
    }

    private static Type[] GetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            ReportFailure("load all types", assembly, null, exception);
            return exception.Types;
        }
        catch (Exception exception)
        {
            ReportFailure("load types", assembly, null, exception);
            return Array.Empty<Type>();
        }
    }

    private static void ReportFailure(string operation, Assembly assembly, Type type, Exception exception)
    {
        try
        {
            var target = type?.FullName ?? assembly?.FullName ?? "<unknown assembly>";
            RLog.Error($"Could not {operation} for IL2CPP: {target}", exception);
        }
        catch
        {
            // Logging callbacks must not interrupt the rest of the registration queue.
        }
    }
}
