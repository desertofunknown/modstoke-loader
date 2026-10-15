// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (C) 2026 Alex Cooper
//
// MonoMod.Utils 25.x no longer exposes the old PlatformHelper surface used by
// BepInEx 6. Keep the small compatibility surface here while OpenACAI carries
// the Sons Of The Forest .NET 11 branch forward.

using System;

namespace MonoMod.Utils
{
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
        public static Platform Current
        {
            get
            {
                return DeterminePlatform();
            }
            set
            {
                // BepInEx 6 assigned MonoMod's old PlatformHelper.Current during
                // preloader startup. MonoMod 25 now owns platform state through
                // PlatformDetection, so assignments are intentionally ignored.
            }
        }

        public static string LibrarySuffix
        {
            get
            {
                return DynDll.DllExtension;
            }
        }

        public static bool Is(Platform platform)
        {
            return (Current & platform) == platform;
        }

        private static Platform DeterminePlatform()
        {
            var detected = Platform.Unknown;
            if (PlatformDetection.OS.Is(OSKind.Windows))
            {
                detected = Platform.Windows;
            }
            else if (PlatformDetection.OS.Is(OSKind.OSX))
            {
                detected = Platform.MacOS;
            }
            else if (PlatformDetection.OS.Is(OSKind.Linux))
            {
                detected = Platform.Linux;
            }

            if (PlatformDetection.OS.Is(OSKind.Android))
            {
                detected = Platform.Android;
            }
            else if (PlatformDetection.OS.Is(OSKind.IOS))
            {
                detected = Platform.iOS;
            }

            if (PlatformDetection.OS.Is(OSKind.Wine))
            {
                detected |= Platform.Wine;
            }

            if (PlatformDetection.Architecture.HasFlag(ArchitectureKind.Bits64))
            {
                detected |= Platform.Bits64;
            }

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
}
