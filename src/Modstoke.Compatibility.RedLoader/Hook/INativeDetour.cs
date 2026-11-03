// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (c) RedLoader and BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Reflection;
using MonoMod.RuntimeDetour;

namespace RedLoader.Unity.IL2CPP.Hook;

public interface INativeDetour : IDetour
{
    private static readonly Lazy<Func<nint, Delegate, INativeDetour>> Factory = new(() =>
    {
        var adapter = Assembly.Load("Modstoke.Preloader")
            .GetType("Modstoke.Preloader.RedNativeDetourCompatibility", true);
        var create = adapter.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly,
            null, new[] { typeof(IntPtr), typeof(Delegate) }, null)
            ?? throw new MissingMethodException(adapter.FullName, "Create");
        return create.CreateDelegate<Func<nint, Delegate, INativeDetour>>();
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
            catch (Exception cleanupError)
            {
                try
                {
                    RLog.Warning($"Failed to clean up native detour after setup failure: {cleanupError}");
                }
                catch
                {
                    // Logging must not replace the original setup failure.
                }
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
