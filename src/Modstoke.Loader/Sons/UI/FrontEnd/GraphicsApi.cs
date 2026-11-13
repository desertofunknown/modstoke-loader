// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace Modstoke.Loader
{
    /// <summary>
    /// The graphics API the game draws with: DirectX 11, as the game ships, or DirectX 12, which its engine also
    /// carries. Unity picks it from the command line as the game starts, so the choice is stored here and the
    /// native host adds the matching -force-d3d11 or -force-d3d12 at the next start. A start with the setting
    /// leaves a marker the native host writes; the menus clear it once they draw, and a start that never got that
    /// far, or a DirectX 12 device the engine could not create, puts the setting back to the game's own choice.
    /// </summary>
    internal static class GraphicsApi
    {
        internal const string Key = "graphicsApi", Default = "auto";
        private const string PendingName = "graphics-api-pending", FailedName = "graphics-api-failed";
        private static bool _confirmed;

        /// <summary>The stored choice: auto, d3d11 or d3d12.</summary>
        internal static string Choice => RuntimePreferences.ReadText(Key, Default);

        /// <summary>The API this start is drawing with, as the same names.</summary>
        internal static string Running => SystemInfo.graphicsDeviceType switch
        {
            GraphicsDeviceType.Direct3D12 => "d3d12",
            GraphicsDeviceType.Direct3D11 => "d3d11",
            _ => "other"
        };

        /// <summary>Set when the last start with DirectX 12 did not get going and the setting was put back.</summary>
        internal static bool FellBack { get; private set; }

        internal static bool Write(string value) => RuntimePreferences.WriteText(Key, value, out _);

        /// <summary>The menus are drawing, so this start's graphics work; called once from the front end.</summary>
        internal static void Confirm()
        {
            if (_confirmed) return;
            _confirmed = true;
            try
            {
                var directory = Path.GetDirectoryName(RuntimePreferences.SettingsPath) ?? "";
                var pending = Path.Combine(directory, PendingName);
                var failed = Path.Combine(directory, FailedName);
                var forced = File.Exists(pending) ? File.ReadAllText(pending).Trim() : null;
                if (forced != null) File.Delete(pending);
                // The engine falls back to DirectX 11 by itself when a DirectX 12 device cannot be created.
                var refused = forced == "-force-d3d12" && Running != "d3d12";
                if (File.Exists(failed) || refused)
                {
                    if (File.Exists(failed)) File.Delete(failed);
                    FellBack = true;
                    Write(Default);
                    RuntimeEntryPoint.LogSource?.LogWarning("The chosen graphics API did not start last time; the game's own choice is back.");
                }
                RuntimeEntryPoint.LogSource?.LogInfo("Drawing with " + SystemInfo.graphicsDeviceVersion + " (graphicsApi = " + Choice + ").");
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The graphics API start check failed: " + error.Message);
            }
        }
    }

    /// <summary>Closes the game and starts it again through Steam, for a setting the engine only reads at start.</summary>
    internal static class GameRestart
    {
        private const string SteamRun = "steam://rungameid/1326470";

        internal static void Now(Action save)
        {
            try
            {
                save?.Invoke();
                // Steam starts the game again only once this process has gone, so a small waiter outlives it.
                var id = System.Diagnostics.Process.GetCurrentProcess().Id;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"Wait-Process -Id " + id
                        + " -ErrorAction SilentlyContinue; Start-Process '" + SteamRun + "'\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                RuntimeEntryPoint.LogSource?.LogInfo("Restarting the game for a new graphics API.");
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The game could not arrange its restart, so it only closes: " + error.Message);
            }
            Application.Quit();
        }
    }
}
