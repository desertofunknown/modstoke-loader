// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (C) 2022 BepInEx Team
// Copyright (C) 2026 Alex Cooper
//
// Modstoke implementation of the BepInEx Unity metadata contract. Manager-file
// offsets follow BepInEx's lookup, adapted from SamboyCoding/Cpp2IL.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using AssetRipper.Primitives;

[assembly: InternalsVisibleTo("BepInEx.Unity.Mono.Preloader")]
[assembly: InternalsVisibleTo("BepInEx.Unity.Mono")]
[assembly: InternalsVisibleTo("BepInEx.Unity.IL2CPP")]

namespace BepInEx.Unity.Common;

/// <summary>Describes the Unity player used by the current process.</summary>
public static class UnityInfo
{
    // The bootstrap publishes these properties and this flag by reflection.
    private static bool initialized;

    /// <summary>Gets the absolute path to the player executable.</summary>
    public static string PlayerPath { get; private set; }

    /// <summary>Gets the absolute path to the game's data directory.</summary>
    public static string GameDataPath { get; private set; }

    /// <summary>Gets the detected Unity version, or an estimate during early startup.</summary>
    public static UnityVersion Version { get; private set; }

    internal static void Initialize(string unityPlayerPath, string gameDataPath)
    {
        if (initialized)
            return;

        var playerPath = Path.GetFullPath(unityPlayerPath ?? throw new ArgumentNullException(nameof(unityPlayerPath)));
        var dataPath = Path.GetFullPath(gameDataPath ?? throw new ArgumentNullException(nameof(gameDataPath)));
        var version = ReadVersion(playerPath, dataPath);
        PlayerPath = playerPath;
        GameDataPath = dataPath;
        Version = version;
        initialized = true;
    }

    internal static void SetRuntimeUnityVersion(string version) => Version = UnityVersion.Parse(version);

    private static UnityVersion ReadVersion(string playerPath, string dataPath)
    {
        if (TryReadManagerVersion(Path.Combine(dataPath, "globalgamemanagers"), out var version, 0x14, 0x30)
            || TryReadManagerVersion(Path.Combine(dataPath, "data.unity3d"), out version, 0x12)
            || TryReadManagerVersion(Path.Combine(dataPath, "mainData"), out version, 0x14))
            return version;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(playerPath);
                if (TryParseVersion(info.ProductVersion, out version) || TryParseVersion(info.FileVersion, out version))
                    return version;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        return File.Exists(Path.Combine(dataPath, "Managed", "UnityEngine.CoreModule.dll"))
            ? new UnityVersion(2017, 0, 0, UnityVersionType.Experimental)
            : default;
    }

    private static bool TryReadManagerVersion(string path, out UnityVersion version, params int[] offsets)
    {
        version = default;
        try
        {
            using var stream = File.OpenRead(path);
            foreach (var offset in offsets)
            {
                if (offset >= stream.Length)
                    continue;

                stream.Position = offset;
                var text = ReadVersionText(stream);
                if (TryParseVersion(text, out version))
                    return true;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return false;
    }

    private static string ReadVersionText(Stream stream)
    {
        const int maximumLength = 64;
        var text = new StringBuilder();
        for (var index = 0; index <= maximumLength; index++)
        {
            var value = stream.ReadByte();
            if (value == 0)
                return text.ToString();
            if (value < 0x20 || value > 0x7e || index == maximumLength)
                return null;
            text.Append((char)value);
        }

        return null;
    }

    private static bool TryParseVersion(string text, out UnityVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        try
        {
            return UnityVersion.TryParse(text.Trim(), out version, out _);
        }
        // AssetRipper's TryParse uses numeric Parse internally for matched fields.
        catch (FormatException) { return false; }
        catch (OverflowException) { return false; }
    }
}
