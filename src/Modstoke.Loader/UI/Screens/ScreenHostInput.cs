// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using global::Sons.Input;
using TheForest.Utils;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Modstoke.Loader
{
    // Unlike the mods menu, the screen host fails to open without its focus lease, and re-applies a lost Console map
    // only once before it closes.
    internal static class ScreenHostInput
    {
        private const string SurfaceKind = "screen-host";
        private static EventSystem _eventSystem;
        private static GameObject _returnSelection;
        private static TMP_InputField _focusedInput;
        private static bool _inputWasFocused;
        private static bool _leavingInput;
        private static int _reassertFrame = -1;
        private static bool _cursorVisible;
        private static CursorLockMode _cursorLock;

        internal static bool TryAcquire(ScreenHostSession session)
        {
            if (session.Focus != null)
                return true;
            if (!InputBindings.TryAcquireFocus(session.OwnerId, out var focus, session.AllowedKeybindIds))
                return false;
            session.Focus = focus;
            return true;
        }

        // Any visible Loader surface also silences keybinds that opt out while Modstoke UI is up, and closes the tablet.
        internal static void MarkSurface(ScreenHostSession session, string view)
        {
            UiState.MarkVisible(session.SurfaceId, session.Title, SurfaceKind, view, "theme=" + session.Tier);
            session.SurfaceShown = true;
        }

        internal static void Enter(ScreenHostSession session)
        {
            var system = EventSystem.current;
            _eventSystem = system;
            _returnSelection = system != null ? system.currentSelectedGameObject : null;
            _focusedInput = null;
            _inputWasFocused = false;
            _leavingInput = false;
            _reassertFrame = -1;
            _cursorVisible = Cursor.visible;
            _cursorLock = Cursor.lockState;
            ScreenHostView.SetCapture(true);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            MarkSurface(session, "open:" + session.Origin);
        }

        /// <summary>Per-frame check that the screen still owns the pointer and keyboard; false closes it.</summary>
        internal static bool Hold(ScreenHostSession session)
        {
            var system = EventSystem.current;
            if (system == null || system != _eventSystem || !system.isActiveAndEnabled)
                return false;
            TrackTextField(system);
            // The recipe gets a frame to apply after the root opens, and again after each re-assert.
            if (Time.frameCount <= session.OpenedFrame + 1 || InputSystem.GetState(InputState.Console))
            {
                _reassertFrame = -1;
                return true;
            }
            if (_reassertFrame >= 0)
                return Time.frameCount <= _reassertFrame + 1;

            // Another UI dropped the Console map; re-enabling the capture applies it again, as Admin does.
            _reassertFrame = Time.frameCount;
            ScreenHostView.SetCapture(false);
            ScreenHostView.SetCapture(true);
            return true;
        }

        internal static bool BackPressed() => InputBindings.IsPressed(KeybindIds.Back);

        /// <summary>The first Back only leaves a text field, even when the field saw the key first.</summary>
        internal static bool LeaveTextField()
        {
            if (!_leavingInput)
                return false;
            if (_focusedInput != null)
                _focusedInput.DeactivateInputField();
            _focusedInput = null;
            _inputWasFocused = false;
            _leavingInput = false;
            return true;
        }

        internal static void Exit(ScreenHostSession session, bool wasOpen, ScreenHostCloseReason reason)
        {
            var focus = session.Focus;
            session.Focus = null;
            try
            {
                focus?.Dispose();
            }
            finally
            {
                if (session.SurfaceShown)
                    UiState.MarkHidden(session.SurfaceId, session.Title, SurfaceKind, "closed", "Closed: " + reason + ".");
                session.SurfaceShown = false;
                if (wasOpen)
                {
                    RestoreSelection();
                    // Another UI may have changed the cursor while the screen was open.
                    if (Cursor.visible && Cursor.lockState == CursorLockMode.None)
                    {
                        Cursor.lockState = _cursorLock;
                        Cursor.visible = _cursorVisible;
                    }
                }

                _eventSystem = null;
                _returnSelection = null;
                _focusedInput = null;
                _inputWasFocused = false;
                _leavingInput = false;
            }
        }

        private static void TrackTextField(EventSystem system)
        {
            var selected = system.currentSelectedGameObject;
            var input = selected != null && ScreenHostView.Contains(selected) ? selected.GetComponent<TMP_InputField>() : null;
            _focusedInput = input != null && input.isFocused ? input : null;
            _leavingInput = _focusedInput != null || _inputWasFocused;
            _inputWasFocused = _focusedInput != null;
        }

        private static void RestoreSelection()
        {
            var system = _eventSystem;
            if (system == null || system != EventSystem.current)
                return;
            var selected = system.currentSelectedGameObject;
            if (selected == null || ScreenHostView.Contains(selected))
                system.SetSelectedGameObject(_returnSelection != null && _returnSelection.activeInHierarchy ? _returnSelection : null);
        }
    }
}
