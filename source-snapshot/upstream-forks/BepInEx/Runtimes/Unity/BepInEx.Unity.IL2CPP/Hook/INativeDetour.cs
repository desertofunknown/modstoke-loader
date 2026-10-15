// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Reflection;
using BepInEx.Logging;
using MonoMod.RuntimeDetour;

namespace BepInEx.Unity.IL2CPP.Hook;

public interface INativeDetour : IDetour
{
    private static readonly ManualLogSource Logger = BepInEx.Logging.Logger.CreateLogSource("NativeDetour");

    private static readonly Lazy<Func<nint, Delegate, INativeDetour>> Factory = new(() =>
    {
        var adapter = Assembly.Load("OpenACAI.Endnight.Loader.Preloader")
                              .GetType("OpenACAI.Endnight.Loader.Preloader.OpenACAINativeDetourCompatibility", true);
        var create = adapter.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static,
                                       null, new[] { typeof(IntPtr), typeof(Delegate) }, null)
                     ?? throw new MissingMethodException(adapter.FullName, "Create");
        return (Func<nint, Delegate, INativeDetour>)create.CreateDelegate(typeof(Func<nint, Delegate, INativeDetour>));
    });

    public nint OriginalMethodPtr { get; }
    public nint DetourMethodPtr { get; }
    public nint TrampolinePtr { get; }
    public void Free();
    public MethodBase GenerateTrampoline(MethodBase signature = null);
    public TDelegate GenerateTrampoline<TDelegate>() where TDelegate : Delegate;

    public static INativeDetour Create<T>(nint original, T target) where T : Delegate =>
        Factory.Value(original, target);

    public static INativeDetour CreateAndApply<T>(nint from, T to, out T original)
        where T : Delegate
    {
        original = null;
        var detour = Create(from, to);
        try
        {
            original = detour.GenerateTrampoline<T>();
            detour.Apply();
        }
        catch
        {
            try
            {
                detour.Dispose();
            }
            catch (Exception cleanupEx)
            {
                try
                {
                    Logger.LogWarning($"Failed to clean up native detour after setup failure: {cleanupEx}");
                }
                catch { }
            }

            original = null;
            throw;
        }

        return detour;
    }

    internal enum DetourProvider
    {
        Default,
        Dobby,
        Funchook
    }
}
