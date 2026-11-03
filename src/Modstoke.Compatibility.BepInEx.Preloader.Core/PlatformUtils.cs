// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (C) 2022 BepInEx Team
// Copyright (C) 2026 Alex Cooper

using System;
using System.Runtime.InteropServices;
using MonoMod.Utils;

namespace BepInEx.Preloader.Core;

internal static class PlatformUtils
{
    public static readonly bool ProcessIs64Bit = Environment.Is64BitProcess;
    public static Version WindowsVersion { get; set; }
    public static string WineVersion { get; set; }
    public static string LinuxArchitecture { get; set; }
    public static string LinuxKernelVersion { get; set; }

    public static void SetPlatform()
    {
        // The current MonoMod compatibility property delegates to PlatformDetection;
        // assigning it no longer changes platform state.
        _ = PlatformHelper.Current;
        WindowsVersion = OperatingSystem.IsWindows() ? Environment.OSVersion.Version : null;
        WineVersion = OperatingSystem.IsWindows() ? ReadWineVersion() : null;
        LinuxArchitecture = OperatingSystem.IsLinux() ? RuntimeInformation.OSArchitecture.ToString() : null;
        LinuxKernelVersion = OperatingSystem.IsLinux() ? Environment.OSVersion.Version.ToString() : null;
    }

    private static string ReadWineVersion()
    {
        if (!NativeLibrary.TryLoad("ntdll.dll", out var library))
            return null;

        try
        {
            if (!NativeLibrary.TryGetExport(library, "wine_get_version", out var address))
                return null;
            var getVersion = Marshal.GetDelegateForFunctionPointer<GetWineVersion>(address);
            return Marshal.PtrToStringAnsi(getVersion());
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetWineVersion();
}
