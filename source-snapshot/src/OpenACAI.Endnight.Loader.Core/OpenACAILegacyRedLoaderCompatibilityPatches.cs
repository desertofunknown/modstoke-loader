// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned Harmony compatibility patches for RedLoader runtime helpers.

using System;
using System.Collections;
using System.IO;
using System.Reflection;
using HarmonyLib;
using RedLoader;
using SUI;
using UnityEngine;

namespace OpenACAI.Endnight.Loader
{
    internal static class OpenACAILegacyRedLoaderCompatibilityPatches
    {
        private const string HarmonyPatchId = ".redloader-compatibility";

        private static bool _redLoaderPatchesInstalled;
        private static bool _axelModMenuGuardInstalled;
        private static bool _axelModMenuGuardNotReadyLogged;
        private static HarmonyLib.Harmony _redLoaderHarmony;
        private static FieldInfo _axelPanelsField;
        private static FieldInfo _axelCurrentPanelField;

        internal static void InstallRedLoaderCompatibilityPatches(
            Action<string> addStartupWarning,
            ref bool installed)
        {
            if (_redLoaderPatchesInstalled || installed)
            {
                installed = true;
                return;
            }

            try
            {
                _redLoaderHarmony = new HarmonyLib.Harmony(OpenACAIEndnightRuntime.PluginGuid + HarmonyPatchId);

                if (!Coroutines.HasDriver)
                {
                    var coroutinesType = typeof(Coroutines);
                    var startMethod = AccessTools.Method(coroutinesType, nameof(Coroutines.Start));
                    var stopMethod = AccessTools.Method(coroutinesType, nameof(Coroutines.Stop));
                    if (startMethod != null)
                    {
                        _redLoaderHarmony.Patch(startMethod, prefix: new HarmonyMethod(typeof(OpenACAILegacyRedLoaderCompatibilityPatches), nameof(CoroutinesStartPrefix)));
                    }

                    if (stopMethod != null)
                    {
                        _redLoaderHarmony.Patch(stopMethod, prefix: new HarmonyMethod(typeof(OpenACAILegacyRedLoaderCompatibilityPatches), nameof(CoroutinesStopPrefix)));
                    }
                }

                if (!MenuButtonRegistrationPolicy.HasFilter)
                {
                    var suiType = typeof(SUI.SUI);
                    var addTitleMethod = AccessTools.Method(suiType, "AddButtonToTitleMenu");
                    var addPauseMethod = AccessTools.Method(suiType, "AddButtonToPauseMenu");
                    if (addTitleMethod != null)
                    {
                        _redLoaderHarmony.Patch(
                            addTitleMethod,
                            prefix: new HarmonyMethod(typeof(OpenACAILegacyRedLoaderCompatibilityPatches), nameof(SuppressRedLoaderModsButtonPrefix)));
                    }

                    if (addPauseMethod != null)
                    {
                        _redLoaderHarmony.Patch(
                            addPauseMethod,
                            prefix: new HarmonyMethod(typeof(OpenACAILegacyRedLoaderCompatibilityPatches), nameof(SuppressRedLoaderModsButtonPrefix)));
                    }
                }

                _redLoaderPatchesInstalled = true;
                installed = true;
            }
            catch (Exception ex)
            {
                installed = false;
                addStartupWarning?.Invoke("OpenACAI RedLoader compatibility patches failed to install: " + ex.Message);
            }
        }

        internal static void InstallHostedModCompatibilityPatches(
            RedLoaderHostResult hostResult,
            Action<string> addStartupWarning)
        {
            if (hostResult == null || _redLoaderHarmony == null)
            {
                return;
            }

            var axelAssembly = FindHostedAssembly(hostResult, "AxelModMenu.dll");
            if (!_axelModMenuGuardInstalled && axelAssembly != null)
            {
                InstallAxelModMenuGuard(axelAssembly, addStartupWarning);
            }
        }

        public static bool CoroutinesStartPrefix(ref IEnumerator routine, ref Coroutine __result)
        {
            if (OpenACAICoroutines.PrepareStart(routine))
                return true;

            __result = null;
            return false;
        }

        public static bool CoroutinesStopPrefix(ref Coroutine coroutineToken)
        {
            return OpenACAICoroutines.CanStop();
        }

        public static bool SuppressRedLoaderModsButtonPrefix(ref Func<SUiElement> generator, ref string id, ref int index)
        {
            return OpenACAIMenuRegistrationPolicy.ShouldRegister(id);
        }

        public static bool AxelModMenuToggleMenuPrefix()
        {
            try
            {
                var panels = _axelPanelsField.GetValue(null) as ICollection;
                var currentPanel = (int)_axelCurrentPanelField.GetValue(null);
                if (panels == null || currentPanel < 0 || currentPanel >= panels.Count)
                {
                    if (!_axelModMenuGuardNotReadyLogged)
                    {
                        _axelModMenuGuardNotReadyLogged = true;
                        OpenACAIEndnightRuntime.TraceCompat("Suppressed AxelModMenu.ToggleMenu because its selected panel is not available yet.");
                    }

                    return false;
                }

                _axelModMenuGuardNotReadyLogged = false;
            }
            catch (Exception ex)
            {
                OpenACAIEndnightRuntime.TraceCompat("AxelModMenu.ToggleMenu guard failed open: " + ex.Message);
            }

            return true;
        }

        internal static bool IsRedLoaderModsButtonEnabled()
        {
            return OpenACAILegacyRedLoaderCompatibility.IsRedLoaderModsButtonEnabled();
        }

        private static Assembly FindHostedAssembly(RedLoaderHostResult hostResult, string fileName)
        {
            foreach (var mod in hostResult.HostedMods)
            {
                if (string.Equals(Path.GetFileName(mod?.ModAssembly?.Location), fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return mod.ModAssembly;
                }
            }

            return null;
        }

        private static void InstallAxelModMenuGuard(Assembly assembly, Action<string> addStartupWarning)
        {
            try
            {
                var type = assembly.GetType("AxelModMenu.AxelModMenu");
                var uiType = assembly.GetType("AxelModMenu.AxelModMenuUi");
                var toggleMenu = AccessTools.DeclaredMethod(type, "ToggleMenu", Type.EmptyTypes);
                var panelsField = AccessTools.Field(uiType, "SwitchablePanels");
                var currentPanelField = AccessTools.Field(uiType, "CurrentPanel");
                if (toggleMenu == null || toggleMenu.ReturnType != typeof(void)
                    || panelsField == null || !panelsField.IsStatic || !typeof(ICollection).IsAssignableFrom(panelsField.FieldType)
                    || currentPanelField == null || !currentPanelField.IsStatic || currentPanelField.FieldType != typeof(int))
                {
                    addStartupWarning?.Invoke("AxelModMenu does not match the known early-key guard contract; leaving the mod unpatched.");
                    return;
                }

                _axelPanelsField = panelsField;
                _axelCurrentPanelField = currentPanelField;
                _redLoaderHarmony.Patch(
                    toggleMenu,
                    prefix: new HarmonyMethod(typeof(OpenACAILegacyRedLoaderCompatibilityPatches), nameof(AxelModMenuToggleMenuPrefix)));
                _axelModMenuGuardInstalled = true;
                OpenACAIEndnightRuntime.TraceCompat("Installed AxelModMenu.ToggleMenu early-key guard.");
            }
            catch (Exception ex)
            {
                addStartupWarning?.Invoke("AxelModMenu compatibility guard failed to install: " + ex.Message);
            }
        }
    }
}
