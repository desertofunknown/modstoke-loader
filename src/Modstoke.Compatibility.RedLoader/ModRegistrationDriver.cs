// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace RedLoader;

internal interface IModRegistrationDriver
{
    List<Action> Callbacks { get; }
    bool Register();
    bool IsActive(List<Action> callbacks);
    void InitializeMod();
    void Unregister(string reason, bool silent);
    void UnregisterInstance(string reason, bool silent);
}

internal interface IModRegistrationCallbacks
{
    void SetRegistered(bool registered);
    void RegisterCallbacks();
    void EarlyInitialize();
    bool RegisterInternal();
    void UnregisterInternal();
    void Initialize();
    void LateInitialize();
    void Deinitialize();
    void PrintLoadInfo();
    void PrintUnloadInfo(string reason);
    void SubscribeInitialization();
    void SubscribeLateInitialization();
}

internal static class ModRegistrationDriverFactory
{
    private static readonly Lazy<Bindings> Owner = new(() => new Bindings());

    internal static IModRegistrationDriver Create(ModBase mod) => Owner.Value.Create(mod);
    internal static ModBase Find(string name, string author) => Owner.Value.Find(name, author);

    private sealed class Bindings
    {
        internal readonly Func<ModBase, IModRegistrationDriver> Create;
        internal readonly Func<string, string, ModBase> Find;

        internal Bindings()
        {
            var owner = Assembly.Load("Modstoke.Loader")
                .GetType("Modstoke.Loader.RedLoaderRegistration", true);
            Create = Bind<Func<ModBase, IModRegistrationDriver>>(owner, "Create");
            Find = Bind<Func<string, string, ModBase>>(owner, "Find");
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
