// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using AdvancedTerrainGrass;
using HarmonyLib;
using Il2CppInterop.Common;
using Modstoke.Sons;
using Sons.Atmosphere;
using Sons.Electricity;
using Sons.Environment;
using Sons.Gui;
using TheForest;
using TheForest.World;
using UnityEngine;
using VailWorldSimulation = Sons.Ai.Vail.VailWorldSimulation;

namespace Modstoke.Loader
{
    /// <summary>
    /// Game hooks for photo mode. The grass culls itself against Camera.main in LateUpdate, before any render
    /// callback, so its draw runs with the photo pose on the camera too, or grass outside the player's own view
    /// would be missing from the shot; a photo is taken just after it, as the grass is queued for the frame only
    /// then. Photo mode puts everything back before the pause menu closes, so the game turns its HUD back on
    /// over a camera that is already where it belongs. The debug console's keys stay quiet while it is open. And
    /// while the World tab has changed the time or weather, the game updates that would act on them are held.
    /// </summary>
    internal static class PhotoPatches
    {
        private static int _installationAttempted;
        private static bool _grassApplied, _redrawLogged;

        /// <summary>Whether the grass draw follows the photo camera on this install.</summary>
        internal static bool GrassFollows { get; private set; }

        /// <summary>The last frame the grass was queued for drawing in, or -1.</summary>
        internal static int GrassFrame { get; private set; } = -1;

        /// <summary>Whether the game's weather update, ambient temperature, AI clock, power grids and time triggers can be held for the World tab.</summary>
        internal static bool WorldHooks { get; private set; }

        /// <summary>
        /// Whether the grass has to be drawn again before each further render of <paramref name="camera"/> this
        /// frame. Set to draw in the main camera alone, the grass queues its draws for that camera only, and a
        /// camera's own draws are used up by its first render; draws for every camera last the whole frame.
        /// </summary>
        internal static bool GrassNeedsRedraw(Camera camera)
        {
            if (!GrassFollows || camera == null || !camera) return false;
            var grass = GrassManager._instance;
            if (grass == null || !grass || !grass.DoRenderGrass) return false;
            var only = grass.CameraInWichGrassWillBeDrawn;
            var redraw = only != null && only && only.Pointer == camera.Pointer;
            if (!_redrawLogged)
            {
                _redrawLogged = true;
                RuntimeEntryPoint.LogSource?.LogInfo(redraw
                    ? "Photo mode: the grass draws for the main camera only, so each photo render draws it again."
                    : "Photo mode: the grass draws for every camera, so one draw serves every photo render.");
            }
            return redraw;
        }

        /// <summary>Queues the grass again for this frame, culled for the photo pose through the draw's own hook.</summary>
        internal static void RedrawGrass()
        {
            var grass = GrassManager._instance;
            if (grass != null && grass && grass.DoRenderGrass) grass.DrawGrass();
        }

        internal static void InstallHarmonyPatch(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0 || GameBuild.IsDedicatedServer) return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                warning?.Invoke("Photo mode hooks are unavailable: Harmony support is not ready.");
                return;
            }
            var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-photo-mode");
            try
            {
                var close = AccessTools.Method(typeof(PauseMenu), nameof(PauseMenu.Close))
                    ?? throw new MissingMethodException(nameof(PauseMenu.Close));
                harmony.Patch(close, prefix: new HarmonyMethod(typeof(PhotoPatches), nameof(PauseClosePrefix)) { priority = Priority.First });
            }
            catch (Exception error)
            {
                warning?.Invoke("Photo mode will not see the pause menu close: " + error.Message);
            }
            try
            {
                var keys = AccessTools.Method(typeof(DebugConsole), nameof(DebugConsole.HandleConsoleKeys), Type.EmptyTypes)
                    ?? throw new MissingMethodException(nameof(DebugConsole.HandleConsoleKeys));
                harmony.Patch(keys, prefix: new HarmonyMethod(typeof(PhotoPatches), nameof(ConsoleKeysPrefix)));
                // The console's silent-input coroutine reads the debug keys itself, without the method above.
                var debug = AccessTools.Method(typeof(DebugConsole), nameof(DebugConsole.CheckDebugInputs), Type.EmptyTypes)
                    ?? throw new MissingMethodException(nameof(DebugConsole.CheckDebugInputs));
                harmony.Patch(debug, prefix: new HarmonyMethod(typeof(PhotoPatches), nameof(DebugInputsPrefix)));
            }
            catch (Exception error)
            {
                warning?.Invoke("Photo mode cannot keep the game's debug keys from reacting to its own: " + error.Message);
            }
            InstallWorldHooks(harmony, warning);
            try
            {
                if (!GameBuild.IsRecognized)
                {
                    RuntimeEntryPoint.LogSource?.LogInfo("Photo mode leaves the grass to the player's view on an unrecognized game build.");
                    return;
                }
                var draw = AccessTools.Method(typeof(GrassManager), nameof(GrassManager.DrawGrass), Type.EmptyTypes)
                    ?? throw new MissingMethodException(nameof(GrassManager.DrawGrass));
                harmony.Patch(draw, prefix: new HarmonyMethod(typeof(PhotoPatches), nameof(DrawGrassPrefix)),
                    postfix: new HarmonyMethod(typeof(PhotoPatches), nameof(DrawGrassPostfix)));
                GrassFollows = true;
                RuntimeEntryPoint.LogSource?.LogInfo("Photo mode: grass is drawn for the photo camera.");
            }
            catch (Exception error)
            {
                warning?.Invoke("Photo mode cannot draw grass for its camera; grass follows the player's view: " + error.Message);
            }
        }

        // Each would act on the photo's hour or clouds: the weather stops rain at its end time and starts it once the
        // locked clouds read as grown, the ambient temperature rolls again at dusk and dawn, and the AI spawns for the
        // hour. A power grid adds up its solar panels by the shown hour, and would switch its devices off and on and
        // play its power sound after photo mode; a time trigger fires its events and switches its objects at its hours.
        // So what the game switches by the hour stays as it is in the photo, night-only objects included. All are
        // skipped only while PhotoWorld holds the world, the time going back included; nothing of theirs is lost, as
        // they run on elapsed time or the real hour, and the world is paused.
        private static void InstallWorldHooks(HarmonyLib.Harmony harmony, Action<string> warning)
        {
            if (!GameBuild.IsRecognized)
            {
                RuntimeEntryPoint.LogSource?.LogInfo("Photo mode leaves the time and weather alone on an unrecognized game build.");
                return;
            }
            try
            {
                var weather = Entry(typeof(WeatherSystem), nameof(WeatherSystem.Update), 0x3064100);
                var temperature = Entry(typeof(TemperatureManager), nameof(TemperatureManager.UpdateTime), 0x2A215B0, typeof(TimeOfDay), typeof(TimeOfDay));
                var vail = Entry(typeof(VailWorldSimulation), nameof(VailWorldSimulation.UpdateTimeVars), 0x2819D80);
                var power = Entry(typeof(PowerGrid), nameof(PowerGrid.UpdateTime), 0x2CCCEB0, typeof(TimeOfDay), typeof(TimeOfDay));
                var trigger = Entry(typeof(TimeOfDayTrigger), nameof(TimeOfDayTrigger.UpdateTime), 0x2A0A060, typeof(TimeOfDay), typeof(TimeOfDay));
                var hold = new HarmonyMethod(typeof(PhotoPatches), nameof(WorldUpdatePrefix)) { priority = Priority.First };
                foreach (var method in new[] { weather, temperature, vail, power, trigger }) harmony.Patch(method, prefix: hold);
                WorldHooks = true;
            }
            catch (Exception error)
            {
                warning?.Invoke("Photo mode cannot hold the game's weather and clocks, so its World tab is off: " + error.Message);
            }
        }

        private static bool WorldUpdatePrefix() => !PhotoWorld.Held;

        // The exact native entry this build was checked against, before Harmony takes it over.
        private static MethodInfo Entry(Type type, string name, int rva, params Type[] arguments)
        {
            var method = AccessTools.DeclaredMethod(type, name, arguments);
            if (method == null || method.IsStatic || method.ReturnType != typeof(void)) throw new MissingMethodException(type.FullName, name);
            var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
            if (field?.GetValue(null) is not nint info || info == 0) throw new InvalidOperationException("no native method for " + name);
            var module = GetModuleHandle("GameAssembly.dll");
            if (module == 0 || Marshal.ReadIntPtr(info) != module + rva)
                throw new InvalidOperationException(name + " is not the one this build was checked against");
            return method;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
        private static extern nint GetModuleHandle(string name);

        private static void DrawGrassPrefix()
        {
            try
            {
                _grassApplied = PhotoRenderHooks.Current?.ApplyTemporary() == true;
            }
            catch (Exception error)
            {
                _grassApplied = false;
                PhotoSession.Abort(error);
            }
        }

        private static void DrawGrassPostfix()
        {
            try
            {
                GrassFrame = Time.frameCount;
                if (_grassApplied)
                {
                    _grassApplied = false;
                    PhotoRenderHooks.Current?.RestoreIfApplied();
                }
                PhotoSession.DevelopQueued(true);
            }
            catch (Exception error)
            {
                PhotoSession.Abort(error);
            }
        }

        // The game's debug console reads its keys directly, F2 among them for its stats overlay, which is photo
        // mode's capture key; while photo mode is open the keys are its own.
        private static bool ConsoleKeysPrefix(ref bool __result)
        {
            if (!PhotoMode.Active) return true;
            __result = false;
            return false;
        }

        private static bool DebugInputsPrefix() => !PhotoMode.Active;

        private static void PauseClosePrefix()
        {
            if (!PhotoMode.Active && PhotoSession.Current == null) return;
            PhotoSession.EndCurrent("the pause menu closed");
        }
    }
}
