// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using System.Reflection;

namespace RedLoader;

internal static class TypeRegistrationDriver
{
    private static readonly Lazy<Bindings> Owner = new(() => new Bindings());

    internal static void RegisterAssembly(Assembly assembly) => Owner.Value.RegisterAssembly(assembly);
    internal static void SetReady() => Owner.Value.SetReady();

    private sealed class Bindings
    {
        internal readonly Action<Assembly> RegisterAssembly;
        internal readonly Action SetReady;

        internal Bindings()
        {
            var owner = Assembly.Load("Modstoke.Loader")
                .GetType("Modstoke.Loader.RedLoaderTypeRegistration", true);
            RegisterAssembly = Bind<Action<Assembly>>(owner, "RegisterAssembly");
            SetReady = Bind<Action>(owner, "SetReady");
        }

        private static T Bind<T>(Type owner, string name) where T : Delegate
        {
            var parameters = typeof(T).GetMethod("Invoke").GetParameters().Select(parameter => parameter.ParameterType).ToArray();
            var method = owner.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic, null, parameters, null)
                ?? throw new MissingMethodException(owner.FullName, name);
            return (T)method.CreateDelegate(typeof(T));
        }
    }
}
