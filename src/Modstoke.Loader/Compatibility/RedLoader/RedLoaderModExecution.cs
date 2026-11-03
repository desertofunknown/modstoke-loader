// SPDX-License-Identifier: GPL-3.0-or-later
// Invocation behavior adapted from RedLoader's ModBase, derived from MelonLoader.
// Copyright (c) RedLoader and MelonLoader contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.Reflection;
using RedLoader;

// Reflection dispatch intentionally uses the legacy mod metadata contract.
#pragma warning disable CS0618

namespace Modstoke.Loader;

internal static class RedLoaderModExecution
{
    internal static void ExecuteList<T>(LemonAction<T> callback, List<T> mods, bool unregisterOnFail, string reason) where T : ModBase
    {
        List<(T Mod, List<Action> Lifetime)> failures = null;
        foreach (var mod in mods.ToArray())
        {
            var lifetime = RedLoaderRegistration.CaptureLifetime(mod);
            if (lifetime == null)
                continue;
            try { callback(mod); }
            catch (Exception error)
            {
                ReportError(mod, error);
                if (unregisterOnFail)
                    (failures ??= new List<(T Mod, List<Action> Lifetime)>()).Add((mod, lifetime));
            }
        }

        // Unregistration starts only after every eligible entry in the snapshot was visited.
        if (failures != null)
            foreach (var failure in failures)
            {
                if (!RedLoaderRegistration.IsLifetimeActive(failure.Mod, failure.Lifetime))
                    continue;
                try { failure.Mod.Unregister(reason); }
                catch (Exception error) { ReportError(failure.Mod, error); }
            }
    }

    internal static void SendMessageAll(string name, object[] arguments)
    {
        foreach (var mod in ModBase._registeredMelons.ToArray())
        {
            if (RedLoaderRegistration.CaptureLifetime(mod) == null)
                continue;
            try { mod.SendMessage(name, arguments); }
            catch (Exception error) { ReportError(mod, error); }
        }
    }

    internal static object SendMessage(ModBase mod, string name, object[] arguments)
    {
        var method = mod.Info.SystemType.GetMethod(name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        return method == null ? null : method.Invoke(method.IsStatic ? null : mod, arguments);
    }

    private static void ReportError(ModBase mod, Exception error)
    {
        try { mod.LoggerInstance.Error(error.ToString()); }
        catch { /* A diagnostic failure must not skip other mods or their teardown. */ }
    }
}
