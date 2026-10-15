// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (C) 2026 Alex Cooper
//
// RedLoader's rewrite branch still uses several MonoMod.Utils 22-era helpers.
// MonoMod 25 removed those helpers, so keep this tiny source-level bridge while
// OpenACAI ports RedLoader compatibility into OpenACAI-owned runtime modules.

using System;
using System.Runtime.InteropServices;

namespace MonoMod.Utils;

[Flags]
public enum Platform
{
    Unknown = 0,
    Windows = 1 << 0,
    Unix = 1 << 1,
    MacOS = Unix | (1 << 2),
    Linux = Unix | (1 << 3),
    Android = Linux | (1 << 4),
    iOS = Unix | (1 << 5),
    Bits64 = 1 << 6,
    Wine = 1 << 7,
    ARM = 1 << 8
}

public static class PlatformHelper
{
    private static Platform? currentOverride;

    public static Platform Current
    {
        get => currentOverride ?? DeterminePlatform();
        set => currentOverride = value;
    }

    public static string LibrarySuffix => DynDll.DllExtension;

    public static bool Is(Platform platform) => (Current & platform) == platform;

    private static Platform DeterminePlatform()
    {
        var detected = Platform.Unknown;
        if (PlatformDetection.OS.Is(OSKind.Windows))
            detected = Platform.Windows;
        else if (PlatformDetection.OS.Is(OSKind.OSX))
            detected = Platform.MacOS;
        else if (PlatformDetection.OS.Is(OSKind.Linux))
            detected = Platform.Linux;

        if (PlatformDetection.OS.Is(OSKind.Android))
            detected = Platform.Android;
        else if (PlatformDetection.OS.Is(OSKind.IOS))
            detected = Platform.iOS;

        if (PlatformDetection.OS.Is(OSKind.Wine))
            detected |= Platform.Wine;

        if (PlatformDetection.Architecture.HasFlag(ArchitectureKind.Bits64))
            detected |= Platform.Bits64;

        switch (PlatformDetection.Architecture)
        {
            case ArchitectureKind.Arm:
            case ArchitectureKind.Arm64:
                detected |= Platform.ARM;
                break;
        }

        return detected;
    }
}

public static class OpenACAIMonoModCompat
{
    public static TDelegate AsDelegate<TDelegate>(this IntPtr pointer)
        where TDelegate : Delegate
    {
        return Marshal.GetDelegateForFunctionPointer<TDelegate>(pointer);
    }
}
