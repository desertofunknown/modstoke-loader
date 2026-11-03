// SPDX-License-Identifier: GPL-3.0-or-later
// Native detour contracts adapted from RedLoader and BepInEx.
// Copyright (c) RedLoader and BepInEx contributors. Copyright (C) 2026 Alex Cooper.

extern alias RedCompatibility;

using System;
using System.Reflection;
using MonoMod.RuntimeDetour;
using RedNativeDetour = RedCompatibility::RedLoader.Unity.IL2CPP.Hook.INativeDetour;

namespace Modstoke.Preloader;

internal sealed class RedNativeDetourCompatibility : RedNativeDetour
{
    private static readonly Lazy<DobbyLibrary> Library = new(CreateLibrary);
    private readonly NativeDetour _native;
    private MethodBase _trampolineMethod;

    private RedNativeDetourCompatibility(NativeDetour native) => _native = native;

    internal static RedNativeDetour Create(IntPtr original, Delegate target)
    {
        DobbyLibrary library;
        try { library = Library.Value; }
        catch (Exception error)
        {
            NativeDetourFactory.RecordFailure("RedLoader provider binding", error);
            throw;
        }
        return new RedNativeDetourCompatibility(NativeDetourFactory.CreateRedLoader(original, target, library));
    }

    public IntPtr OriginalMethodPtr => _native.Target;
    public IntPtr DetourMethodPtr => _native.Detour;
    public DetourConfig Config => null;

    public bool IsValid
    {
        get { lock (NativeDetourFactory.Sync) return _native.IsValid; }
    }

    public bool IsApplied
    {
        get { lock (NativeDetourFactory.Sync) return _native.IsApplied; }
    }

    public IntPtr TrampolinePtr
    {
        get { lock (NativeDetourFactory.Sync) return _native.OriginalTrampoline; }
    }

    public MethodBase GenerateTrampoline(MethodBase signature = null)
    {
        lock (NativeDetourFactory.Sync)
        {
            _native.PrepareTrampoline();
            _trampolineMethod ??= signature;
            return _trampolineMethod;
        }
    }

    public TDelegate GenerateTrampoline<TDelegate>() where TDelegate : Delegate
    {
        lock (NativeDetourFactory.Sync)
        {
            var trampoline = _native.GenerateTrampoline<TDelegate>();
            _trampolineMethod ??= typeof(TDelegate).GetMethod("Invoke");
            return trampoline;
        }
    }

    public void Apply() => _native.Apply();
    internal void ApplyForProcessLifetime() => _native.ApplyForProcessLifetime();
    public void Undo() => _native.Undo();
    public void Free() => Dispose();
    public void Dispose()
    {
        lock (NativeDetourFactory.Sync)
        {
            _native.Dispose();
            _trampolineMethod = null;
        }
    }

    private static unsafe DobbyLibrary CreateLibrary()
    {
        // Keep P/Invoke resolution anchored in RedLoader, including any registered resolver.
        var shim = typeof(RedNativeDetour).Assembly.GetType("RedLoader.Unity.IL2CPP.Hook.Dobby.DobbyLib", true);
        var prepare = Bind<RedPrepareDelegate>(shim, "Prepare", typeof(IntPtr), typeof(IntPtr), typeof(IntPtr*));
        var commit = Bind<DobbyLibrary.CommitDelegate>(shim, "Commit", typeof(IntPtr));
        var destroy = Bind<DobbyLibrary.DestroyDelegate>(shim, "Destroy", typeof(IntPtr));

        return new DobbyLibrary(Prepare, commit, destroy);

        int Prepare(IntPtr target, IntPtr replacement, out IntPtr trampoline)
        {
            IntPtr result = IntPtr.Zero;
            var status = prepare(target, replacement, &result);
            trampoline = result;
            return status;
        }
    }

    private static T Bind<T>(Type owner, string name, params Type[] parameters) where T : Delegate
    {
        var method = owner.GetMethod(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly,
            null, parameters, null) ?? throw new MissingMethodException(owner.FullName, name);
        return method.CreateDelegate<T>();
    }

    private unsafe delegate int RedPrepareDelegate(IntPtr target, IntPtr replacement, IntPtr* trampoline);
}
