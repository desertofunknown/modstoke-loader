// SPDX-License-Identifier: GPL-3.0-or-later
// Native contracts adapted from BepInEx's Hook/Dobby/DobbyLib.cs and Hook/Funchook/FunchookLib.cs.
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.IO;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Unity.IL2CPP.Hook;
using MonoMod.Utils;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal sealed class DobbyLibrary
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int PrepareDelegate(IntPtr target, IntPtr replacement, out IntPtr trampoline);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int CommitDelegate(IntPtr target);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int DestroyDelegate(IntPtr target);

        // Retained by the factory for process lifetime; successful loads are never unloaded.
        private readonly IntPtr _moduleHandle;
        internal readonly PrepareDelegate Prepare;
        internal readonly CommitDelegate Commit;
        internal readonly DestroyDelegate Destroy;

        private DobbyLibrary(IntPtr moduleHandle)
        {
            _moduleHandle = moduleHandle;
            Prepare = Marshal.GetDelegateForFunctionPointer<PrepareDelegate>(NativeLibrary.GetExport(_moduleHandle, "DobbyPrepare"));
            Commit = Marshal.GetDelegateForFunctionPointer<CommitDelegate>(NativeLibrary.GetExport(_moduleHandle, "DobbyCommit"));
            Destroy = Marshal.GetDelegateForFunctionPointer<DestroyDelegate>(NativeLibrary.GetExport(_moduleHandle, "DobbyDestroy"));
        }

        internal static bool TryLoad(out DobbyLibrary library, out string error)
        {
            library = null;
            error = null;
            IntPtr handle = IntPtr.Zero;
            try
            {
                if (!NativeDetourLibraryLoader.TryLoad("dobby", out handle))
                    throw new DllNotFoundException("Could not load dobby from the BepInEx assembly directory or legacy native library lookup.");

                library = new DobbyLibrary(handle);
                return true;
            }
            catch (Exception exception)
            {
                error = exception.ToString();
                if (handle != IntPtr.Zero)
                    NativeLibrary.Free(handle);
                return false;
            }
        }
    }

    internal sealed class FunchookLibrary
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr CreateDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int PrepareDelegate(IntPtr handle, ref IntPtr target, IntPtr replacement);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int InstallDelegate(IntPtr handle, int flags);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int UninstallDelegate(IntPtr handle, int flags);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int DestroyDelegate(IntPtr handle);

        // The returned const char* is borrowed from Funchook; callers must not free it.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr ErrorMessageDelegate(IntPtr handle);

        // Retained by the factory for process lifetime; successful loads are never unloaded.
        private readonly IntPtr _moduleHandle;
        internal readonly CreateDelegate Create;
        internal readonly PrepareDelegate Prepare;
        internal readonly InstallDelegate Install;
        internal readonly UninstallDelegate Uninstall;
        internal readonly DestroyDelegate Destroy;
        internal readonly ErrorMessageDelegate ErrorMessage;

        private FunchookLibrary(IntPtr moduleHandle)
        {
            _moduleHandle = moduleHandle;
            Create = Marshal.GetDelegateForFunctionPointer<CreateDelegate>(NativeLibrary.GetExport(_moduleHandle, "funchook_create"));
            Prepare = Marshal.GetDelegateForFunctionPointer<PrepareDelegate>(NativeLibrary.GetExport(_moduleHandle, "funchook_prepare"));
            Install = Marshal.GetDelegateForFunctionPointer<InstallDelegate>(NativeLibrary.GetExport(_moduleHandle, "funchook_install"));
            Uninstall = Marshal.GetDelegateForFunctionPointer<UninstallDelegate>(NativeLibrary.GetExport(_moduleHandle, "funchook_uninstall"));
            Destroy = Marshal.GetDelegateForFunctionPointer<DestroyDelegate>(NativeLibrary.GetExport(_moduleHandle, "funchook_destroy"));
            ErrorMessage = Marshal.GetDelegateForFunctionPointer<ErrorMessageDelegate>(NativeLibrary.GetExport(_moduleHandle, "funchook_error_message"));
        }

        internal static bool TryLoad(out FunchookLibrary library, out string error)
        {
            library = null;
            error = null;
            IntPtr handle = IntPtr.Zero;
            try
            {
                if (!NativeDetourLibraryLoader.TryLoad("funchook", out handle))
                    throw new DllNotFoundException("Could not load funchook from the BepInEx assembly directory or legacy native library lookup.");

                library = new FunchookLibrary(handle);
                return true;
            }
            catch (Exception exception)
            {
                error = exception.ToString();
                if (handle != IntPtr.Zero)
                    NativeLibrary.Free(handle);
                return false;
            }
        }
    }

    internal static class NativeDetourLibraryLoader
    {
        internal static bool TryLoad(string name, out IntPtr handle)
        {
            string path = Path.Combine(Paths.BepInExAssemblyDirectory, name + "." + PlatformHelper.LibrarySuffix);
            return NativeLibrary.TryLoad(path, out handle)
                || NativeLibrary.TryLoad(name, typeof(INativeDetour).Assembly, null, out handle);
        }
    }
}
