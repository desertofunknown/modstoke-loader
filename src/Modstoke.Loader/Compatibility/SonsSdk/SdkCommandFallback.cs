// SPDX-License-Identifier: GPL-3.0-or-later
// Compatibility command registration adapted from SonsSdk.
// Copyright (c) RedLoader contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Reflection;
using MonoMod.Utils;
using RedLoader;
using SonsSdk;
using SonsSdk.Attributes;
using TheForest;

namespace Modstoke.Loader;

internal sealed class SdkCommandFallback : ISdkCommandFallbackDriver
{
    private static readonly SdkCommandFallback Driver = new();
    private const long FailureReportIntervalMs = 5000;

    internal static ISdkCommandFallbackDriver Create() => Driver;

    public void RegisterCommands(Type targetType, object instance)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        var methods = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        foreach (MethodInfo method in methods)
        {
            try
            {
                var attribute = method.GetCustomAttribute<DebugCommandAttribute>();
                if (attribute == null)
                    continue;
                var parameters = method.GetParameters();
                if (method.ContainsGenericParameters || parameters.Length > 1
                    || (parameters.Length == 1 && !parameters[0].ParameterType.IsAssignableFrom(typeof(string)))
                    || (!method.IsStatic && (instance == null || !method.DeclaringType.IsInstanceOfType(instance))))
                    throw new ArgumentException("Debug command methods require zero parameters or one parameter accepting string, and a compatible instance for non-static methods.");

                var fastDelegate = method.GetFastInvoker();
                var target = method.IsStatic ? null : instance;

                bool Wrapper(string s)
                {
                    var ret = fastDelegate.Invoke(target, s);
                    if (ret is bool b)
                        return b;
                    return true;
                }

                RegisterCommand(attribute.Command, Wrapper);
                RLog.Msg($"Registered command '{attribute.Command}'");
            }
            catch (Exception error)
            {
                // One malformed attribute or method must not prevent other commands from registering.
                ReportFailure("registration for " + method.DeclaringType?.FullName + "." + method.Name, error);
            }
        }
    }

    public void RegisterCommand(string command, Func<string, bool> callback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(callback);
        var lastReport = 0L;
        var suppressed = 0;
        bool RunCommand(string arguments)
        {
            try { return callback(arguments); }
            catch (Exception error)
            {
                var now = Environment.TickCount64;
                if (lastReport != 0 && now - lastReport < FailureReportIntervalMs)
                {
                    suppressed++;
                    return false;
                }

                var operation = "'" + command + "'";
                if (suppressed > 0)
                    operation += " (" + suppressed + " earlier failures not shown)";
                ReportFailure(operation, error);
                lastReport = now;
                suppressed = 0;
                return false;
            }
        }
        DebugConsole.RegisterCommand(command, (Il2CppSystem.Func<string, bool>)RunCommand, DebugConsole.Instance);
    }

    private static void ReportFailure(string operation, Exception error)
    {
        try
        {
            RuntimeEntryPoint.LogSource?.LogWarning("Compatibility debug command " + operation + " failed: " + error);
        }
        catch (Exception) { }
    }
}
