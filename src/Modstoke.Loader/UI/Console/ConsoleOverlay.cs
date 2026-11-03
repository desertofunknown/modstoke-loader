// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Modstoke.Diagnostics;
using Sons.Input;
using SonsSdk;
using TheForest;
using TheForest.Utils;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace Modstoke.Loader
{
    internal static partial class ConsoleOverlay
    {
        private const string KeybindId = "modstoke.loader.console";
        private static readonly HarmonyLib.Harmony Patches = new("modstoke.loader.console-input");
        private static readonly UnityAction<Scene> SceneUnloaded = new Action<Scene>(OnSceneUnloaded);
        private static readonly UiEventSystem EventSystemLease = new UiEventSystem("Modstoke Console Event System");
        private static MethodInfo _consoleKeys;
        private static IDisposable _focus;
        private static GameObject _capture;
        private static GameObject _root;
        private static GameObject _returnSelection;
        private static EventSystem _eventSystem;
        private static bool _initialized;
        private static bool _visible;
        private static bool _cursorVisible;
        private static CursorLockMode _cursorLock;
        private static int _sceneHandle;
        private static float _nextRefresh;
        private static int _releaseFrame = -1;

        // Called after InputBindings.InitializeDefaults, on Unity's owner thread.
        internal static void Initialize()
        {
            if (_initialized) return;
            var registered = false;
            try
            {
                _consoleKeys = AccessTools.Method(typeof(DebugConsole), nameof(DebugConsole.HandleConsoleKeys), Type.EmptyTypes)
                    ?? throw new MissingMethodException("The game's console input handler is unavailable.");
                Patches.Patch(_consoleKeys, prefix: new HarmonyMethod(typeof(ConsoleOverlay), nameof(BeforeConsoleKeys)));
                // F5 avoids the Loader menu and the Client and Admin default keys (F3-F4, F6-F11).
                if (!InputBindings.TryRegisterKeybind(KeybindId, "Toggle Console", "Modstoke Loader", KeyCode.F5,
                    "Open or close the Modstoke log and command console."))
                    throw new InvalidOperationException("The Modstoke console keybind is already registered.");
                registered = true;
                SceneManager.sceneUnloaded = ReferenceEquals(SceneManager.sceneUnloaded, null)
                    ? SceneUnloaded
                    : Il2CppSystem.Delegate.Combine(SceneManager.sceneUnloaded, SceneUnloaded).Cast<UnityAction<Scene>>();
                _initialized = true;
                var key = InputBindings.Snapshot().TryGetValue(KeybindId, out var keybind) ? keybind.KeyName : KeyCode.F5.ToString();
                ConsoleHub.Append("Info", "Console", "Press " + key
                    + " for the in-game console. Both views share logs and registered commands.");
            }
            catch (Exception error)
            {
                if (registered) InputBindings.TryUnregisterKeybind(KeybindId);
                if (_consoleKeys != null) Patches.Unpatch(_consoleKeys, HarmonyPatchType.Prefix, Patches.Id);
                _consoleKeys = null;
                RuntimeEntryPoint.LogSource?.LogWarning("Could not initialize the Modstoke console: " + error);
            }
        }

        internal static void Update()
        {
            if (!_initialized) return;
            try
            {
                if (InputBindings.IsPressed(KeybindId))
                {
                    if (_visible) Hide();
                    else Show();
                }
                if (!_visible) return;
                if (_root == null || _capture == null || !_capture.activeSelf
                    || !InputSystem.GetState(InputState.Console) || _eventSystem == null || !_eventSystem.isActiveAndEnabled
                    || EventSystem.current != _eventSystem || SceneManager.GetActiveScene().handle != _sceneHandle)
                {
                    Hide();
                    return;
                }
                if (Input.GetKeyDown(KeyCode.Escape))
                {
                    if (_pendingCommand != null) CancelConfirmation();
                    else Hide();
                    return;
                }
                UpdateSize();
                UpdateHistory();
                if (Time.unscaledTime < _nextRefresh) return;
                _nextRefresh = Time.unscaledTime + 0.2f;
                RefreshSnapshot();
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The Modstoke console could not update: " + error);
                Hide();
            }
        }

        private static void Show()
        {
            if (GameState.IsInConsole || InputBindings.HasFocus || MenuRenderer.IsVisible
                || InputSystem.GetState(InputState.Console)) return;
            if (!InputBindings.TryAcquireFocus(KeybindId, out _focus, KeybindId)) return;
            // In normal play the game has no active event system; the console then borrows SUI's.
            var system = EventSystemLease.Acquire();
            if (system == null)
            {
                _focus.Dispose();
                _focus = null;
                return;
            }
            try
            {
                EnsureView();
                if (_capture == null)
                {
                    _capture = new GameObject("Modstoke Console Input");
                    _capture.SetActive(false);
                    UnityEngine.Object.DontDestroyOnLoad(_capture);
                    _capture.AddComponent<InputActionMapState>()._applyState = InputState.Console;
                    var cursor = _capture.AddComponent<InputCursorState>();
                    cursor._enabled = true;
                    cursor._hardwareCursor = true;
                    cursor._priority = 100;
                }
                _eventSystem = system;
                _returnSelection = system.currentSelectedGameObject;
                _sceneHandle = SceneManager.GetActiveScene().handle;
                _cursorVisible = Cursor.visible;
                _cursorLock = Cursor.lockState;
                _visible = true;
                _capture.SetActive(true);
                _root.SetActive(true);
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                RefreshSnapshot();
                RenderRows();
                _command.Select();
                _command.ActivateInputField();
            }
            catch
            {
                Hide();
                if (!_viewReady && _root != null)
                {
                    UnityEngine.Object.Destroy(_root);
                    _root = null;
                }
                throw;
            }
        }

        internal static void Hide()
        {
            var wasVisible = _visible;
            _visible = false;
            _resizing = false;
            _pendingCommand = null;
            var focus = _focus;
            _focus = null;
            if (focus != null) _releaseFrame = Time.frameCount;
            try
            {
                try
                {
                    if (_confirmation != null) _confirmation.SetActive(false);
                    if (_panelInteraction != null) _panelInteraction.interactable = true;
                    if (_root != null) _root.SetActive(false);
                    var system = _eventSystem;
                    if (system != null && system == EventSystem.current)
                    {
                        var selected = system.currentSelectedGameObject;
                        if (selected == null || (_root != null && selected.transform.IsChildOf(_root.transform)))
                            system.SetSelectedGameObject(_returnSelection != null && _returnSelection.activeInHierarchy
                                ? _returnSelection : null);
                    }
                }
                finally
                {
                    if (_capture != null && _capture.activeSelf) _capture.SetActive(false);
                }
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Could not fully close the Modstoke console: " + error);
            }
            finally
            {
                focus?.Dispose();
                EventSystemLease.Release();
                _eventSystem = null;
                _returnSelection = null;
                // Another UI may have changed the cursor while the overlay was open.
                if (wasVisible && Cursor.visible && Cursor.lockState == CursorLockMode.None)
                {
                    Cursor.lockState = _cursorLock;
                    Cursor.visible = _cursorVisible;
                }
            }
        }

        private static void OnSceneUnloaded(Scene scene)
        {
            if (!_visible || scene.handle != _sceneHandle) return;
            _returnSelection = null;
            Hide();
        }

        private static bool BeforeConsoleKeys(ref bool __result)
        {
            if (_focus == null && _releaseFrame != Time.frameCount) return true;
            __result = false;
            return false;
        }

        internal static void Shutdown()
        {
            Hide();
            if (_initialized)
            {
                if (!ReferenceEquals(SceneManager.sceneUnloaded, null))
                    SceneManager.sceneUnloaded = Il2CppSystem.Delegate.Remove(SceneManager.sceneUnloaded, SceneUnloaded)?.Cast<UnityAction<Scene>>();
                InputBindings.TryUnregisterKeybind(KeybindId);
            }
            _initialized = false;
            if (_consoleKeys != null) Patches.Unpatch(_consoleKeys, HarmonyPatchType.Prefix, Patches.Id);
            _consoleKeys = null;
            if (_capture != null) UnityEngine.Object.Destroy(_capture);
            _capture = null;
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
            _viewReady = false;
            Rows.Clear();
            Entries.Clear();
            Filtered.Clear();
            _cursor = 0;
            _sessionId = null;
        }
    }
}
