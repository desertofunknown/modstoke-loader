// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Reflection;

namespace RedLoader;

internal static class ModExecutionDriver
{
    private static readonly Lazy<Type> Owner = new(() => Assembly.Load("Modstoke.Loader")
        .GetType("Modstoke.Loader.RedLoaderModExecution", true));
    private static readonly Lazy<Action<string, object[]>> Broadcast = new(() =>
        Bind<Action<string, object[]>>("SendMessageAll", typeof(string), typeof(object[])));
    private static readonly Lazy<Func<ModBase, string, object[], object>> Message = new(() =>
        Bind<Func<ModBase, string, object[], object>>("SendMessage", typeof(ModBase), typeof(string), typeof(object[])));

    internal static void ExecuteList<T>(LemonAction<T> callback, List<T> mods, bool unregisterOnFail, string reason) where T : ModBase =>
        Execution<T>.Invoke.Value(callback, mods, unregisterOnFail, reason);

    internal static void SendMessageAll(string name, object[] arguments) => Broadcast.Value(name, arguments);
    internal static object SendMessage(ModBase mod, string name, object[] arguments) => Message.Value(mod, name, arguments);

    private static T Bind<T>(string name, params Type[] parameters) where T : Delegate
    {
        var owner = Owner.Value;
        var method = owner.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
            null, parameters, null) ?? throw new MissingMethodException(owner.FullName, name);
        return method.CreateDelegate<T>();
    }

    private static class Execution<T> where T : ModBase
    {
        internal static readonly Lazy<Action<LemonAction<T>, List<T>, bool, string>> Invoke = new(BindExecution);

        private static Action<LemonAction<T>, List<T>, bool, string> BindExecution()
        {
            var owner = Owner.Value;
            var method = owner.GetMethod("ExecuteList", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                ?? throw new MissingMethodException(owner.FullName, "ExecuteList");
            return method.MakeGenericMethod(typeof(T)).CreateDelegate<Action<LemonAction<T>, List<T>, bool, string>>();
        }
    }
}
