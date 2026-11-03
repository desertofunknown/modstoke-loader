// SPDX-License-Identifier: GPL-3.0-or-later
// Derived from RedLoader's BaseChainloader and IL2CPPChainloader.
// Copyright (c) RedLoader and BepInEx contributors. Copyright (C) 2026 Alex Cooper.

extern alias RedCompatibility;

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using RedCompatibility::RedLoader;
using RedCompatibility::RedLoader.Unity.IL2CPP;
using RedCompatibility::RedLoader.Utils;

namespace Modstoke.Preloader;

internal static class RedChainloaderBootstrap
{
    private static readonly RuntimeInvokeDetourDelegate InvokeMethodDetour = OnInvokeMethod;
    private static RuntimeInvokeDetourDelegate originalInvoke;
    private static RedNativeDetourCompatibility runtimeInvokeDetour;
    private static Action startCallback;
    private static int startClaimed;

    internal static void Initialize(string gameExePath, Action initializeLoggers, Action markInitialized)
    {
        if (gameExePath != null)
            LoaderEnvironment.SetExecutablePath(gameExePath);

        initializeLoggers();
        markInitialized();
        RLog.Msg("Chainloader initialized");
    }

    internal static void InitializeLoggers(Func<string> consoleTitle)
    {
        if (ConsoleManager.ConsoleEnabled && !ConsoleManager.ConsoleActive)
            ConsoleManager.CreateConsole();

        if (ConsoleManager.ConsoleActive)
            ConsoleManager.SetConsoleTitle(consoleTitle());
    }

    internal static void InstallRuntimeInvokeHook(Action start)
    {
        startCallback = start;

        if (!NativeLibrary.TryLoad("GameAssembly", typeof(IL2CPPChainloader).Assembly, null, out var il2CppHandle))
        {
            RLog.Error("Could not locate Il2Cpp game assembly (GameAssembly.dll, UserAssembly.dll or libil2cpp.so). The game might be obfuscated or use a yet unsupported build of Unity.");
            return;
        }

        var runtimeInvokePtr = NativeLibrary.GetExport(il2CppHandle, "il2cpp_runtime_invoke");
        RLog.Debug($"Runtime invoke pointer: 0x{runtimeInvokePtr.ToInt64():X}");
        var detour = (RedNativeDetourCompatibility)RedNativeDetourCompatibility.Create(runtimeInvokePtr, InvokeMethodDetour);
        var activationAttempted = false;
        try
        {
            // Publish the trampoline before activation: another thread can enter immediately.
            Volatile.Write(ref originalInvoke, detour.GenerateTrampoline<RuntimeInvokeDetourDelegate>());
            activationAttempted = true;
            detour.ApplyForProcessLifetime();
        }
        catch
        {
            // An attempted activation may already be reachable by native callers. Never release it.
            if (!activationAttempted)
                TryDispose(detour);
            throw;
        }

        runtimeInvokeDetour = detour;
        RLog.Debug("Runtime invoke patched");
    }

    // Unity, the interop layer and mod threads all enter il2cpp_runtime_invoke, so no point is
    // provably free of callers, and Dobby restores the entry bytes non-atomically over any hook
    // layered on top. The gateway stays for the process lifetime; after startup it is one read
    // in front of the original.
    private static IntPtr OnInvokeMethod(IntPtr method, IntPtr obj, IntPtr parameters, IntPtr exc)
    {
        if (Volatile.Read(ref startClaimed) == 0)
            TryStart(method);

        try
        {
            return originalInvoke(method, obj, parameters, exc);
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
            return IntPtr.Zero;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TryStart(IntPtr method)
    {
        try
        {
            if (method != IntPtr.Zero
                && Marshal.PtrToStringAnsi(Il2CppInterop.Runtime.IL2CPP.il2cpp_method_get_name(method)) == "Internal_ActiveSceneChanged"
                && Interlocked.CompareExchange(ref startClaimed, 1, 0) == 0)
            {
                var start = startCallback;
                startCallback = null;
                start();
            }
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
    }

    private static void TryDispose(RedNativeDetourCompatibility detour)
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
    }

    private static void ReportFailure(Exception error)
    {
        try
        {
            RLog.Error("Unable to execute IL2CPP chainloader");
            RLog.Error(error);
        }
        catch
        {
            // Exceptions must not cross back into il2cpp_runtime_invoke.
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr RuntimeInvokeDetourDelegate(IntPtr method, IntPtr obj, IntPtr parameters, IntPtr exc);
}
