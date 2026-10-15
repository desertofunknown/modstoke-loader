// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection;
using MonoMod.Utils;
using RedLoader;
using SonsSdk.Attributes;
using TheForest;

namespace OpenACAI.Endnight.Loader
{
    internal sealed partial class OpenACAISonsSdkLifecycle
    {
        public void RegisterCommands(Type targetType, object instance)
        {
            var methods = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var method in methods)
            {
                var attribute = method.GetCustomAttribute<DebugCommandAttribute>();
                if (attribute == null)
                    continue;

                var invoke = method.GetFastInvoker();

                bool RunCommand(string arguments)
                {
                    var result = invoke(instance, arguments);
                    return result is bool value ? value : true;
                }

                RegisterCommand(attribute.Command, RunCommand);
                RLog.Msg($"Registered command '{attribute.Command}'");
            }
        }

        public void RegisterCommand(string command, Func<string, bool> callback)
        {
            DebugConsole.RegisterCommand(command, callback, DebugConsole.Instance);
        }
    }
}
