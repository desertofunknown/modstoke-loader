// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned mod source identity. Legacy loaders are compatibility
// families here, not the runtime identity.

using System;

namespace OpenACAI.Loader
{
    public static class OpenACAIModSourceFamilies
    {
        public const string OpenAcai = "openacai";
        public const string LegacyRedLoader = "legacy-redloader";
        public const string LegacyBepInEx = "legacy-bepinex";
        public const string Unknown = "unknown";
    }

    public static class OpenACAIModLoaderTypes
    {
        public const string OpenAcaiMod = "openacai-mod";
        public const string OpenAcaiLibrary = "openacai-library";
        public const string RedLoaderMod = "redloader-mod";
        public const string RedLoaderLibrary = "redloader-library";
        public const string BepInExPlugin = "bepinex-plugin";

        public static bool IsOpenAcai(string loaderType)
        {
            return string.Equals(loaderType, OpenAcaiMod, StringComparison.OrdinalIgnoreCase)
                || string.Equals(loaderType, OpenAcaiLibrary, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsLegacyRedLoader(string loaderType)
        {
            return string.Equals(loaderType, RedLoaderMod, StringComparison.OrdinalIgnoreCase)
                || string.Equals(loaderType, RedLoaderLibrary, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsLibrary(string loaderType)
        {
            return string.Equals(loaderType, OpenAcaiLibrary, StringComparison.OrdinalIgnoreCase)
                || string.Equals(loaderType, RedLoaderLibrary, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsLegacyBepInEx(string loaderType)
        {
            return string.Equals(loaderType, BepInExPlugin, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static class OpenACAIModSourceClassifier
    {
        internal static string Classify(string loaderType)
        {
            if (OpenACAIModLoaderTypes.IsOpenAcai(loaderType))
            {
                return OpenACAIModSourceFamilies.OpenAcai;
            }

            if (OpenACAIModLoaderTypes.IsLegacyRedLoader(loaderType))
            {
                return OpenACAIModSourceFamilies.LegacyRedLoader;
            }

            if (OpenACAIModLoaderTypes.IsLegacyBepInEx(loaderType))
            {
                return OpenACAIModSourceFamilies.LegacyBepInEx;
            }

            return OpenACAIModSourceFamilies.Unknown;
        }
    }
}
