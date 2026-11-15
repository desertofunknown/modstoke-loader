// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;
using UnityEngine.InputSystem;
using GameInput = Sons.Input.InputSystem;
using InputState = TheForest.Utils.InputState;
using RebindingOperation = UnityEngine.InputSystem.InputActionRebindingExtensions.RebindingOperation;

namespace Modstoke.Loader
{
    /// <summary>
    /// Waits for one key or button to bind. While it listens the game's input is switched to its own binding state,
    /// the menus hear nothing and Modstoke's and mods' keybinds are held, so nothing but this acts on the press.
    /// </summary>
    /// <remarks>
    /// The operation only records the path; the screen checks it and applies it. Escape and the controller's Start
    /// cancel and never bind, and neither do the Windows keys, the right stick (the camera's) or anything that is not
    /// a button. A stick direction is a synthetic control and still binds; it only loses to a real button pressed
    /// with it.
    /// </remarks>
    internal sealed class BindingListener
    {
        internal enum Phase { Arming, Listening, Captured, Cancelled, Failed }

        internal const float Timeout = 5f;
        private const float ArmLimit = 1.5f, Settle = 0.1f, Release = 0.15f, Magnitude = 0.5f;
        private const string FocusOwner = "modstoke.bindings";
        private static readonly string[] KeyboardReserved = { "<Keyboard>/anyKey", "<Keyboard>/leftMeta", "<Keyboard>/rightMeta", "<Mouse>/press" };
        private static readonly string[] PadReserved =
        {
            "<Gamepad>/rightStick/up", "<Gamepad>/rightStick/down", "<Gamepad>/rightStick/left", "<Gamepad>/rightStick/right",
            "<DualShockGamepad>/touchpadButton"
        };
        private static bool _stateOn;
        private static float _stateOffAt;

        private IDisposable _focus;
        private RebindingOperation _operation;
        // Held for the operation's lifetime, so the delegate it calls is never collected under it.
        private Il2CppSystem.Action<RebindingOperation, string> _apply;
        private float _armedAt, _startedAt;
        private int _steppedFrame;
        private bool _east, _start;

        private BindingListener(BindingSlot slot, IDisposable focus)
        {
            Slot = slot;
            _focus = focus;
        }

        internal static BindingListener Active { get; private set; }
        internal BindingSlot Slot { get; }
        internal Phase State { get; private set; }
        /// <summary>The control pressed, once <see cref="State"/> is <see cref="Phase.Captured"/>.</summary>
        internal string Path { get; private set; }
        internal bool Ended { get; private set; }

        /// <summary>Seconds before it stops by itself; the whole timeout while it waits for the press that started it to end.</summary>
        internal float Left => State == Phase.Listening ? Mathf.Max(0, Timeout - (Time.unscaledTime - _startedAt)) : Timeout;

        /// <summary>Starts waiting, or null when something else holds Modstoke's input focus.</summary>
        internal static BindingListener Begin(BindingSlot slot)
        {
            var active = Active;
            if (active != null)
            {
                active.Cancel();
                active.End();
            }
            if (!InputBindings.TryAcquireFocus(FocusOwner, out var focus)) return null;
            var listener = new BindingListener(slot, focus) { _armedAt = Time.unscaledTime, _steppedFrame = Time.frameCount };
            // A B or Start already down as listening starts does not cancel it at once.
            (listener._east, listener._start) = PadButtons();
            FrontInput.Listening = true;
            return Active = listener;
        }

        /// <summary>Once a frame from the screen; the phase after this frame.</summary>
        internal Phase Step()
        {
            if (Ended) return State;
            _steppedFrame = Time.frameCount;
            if (State == Phase.Arming)
            {
                // The press that started listening must not become the binding: the operation starts once it is let go.
                if (CancelPressed()) Cancel();
                else if (!StillHeld() || Time.unscaledTime - _armedAt >= ArmLimit) Start();
                return State;
            }
            if (State != Phase.Listening) return State;
            try
            {
                if (CancelPressed()) Cancel();
                else if (_operation.completed) State = Cancels(Path) ? Phase.Cancelled : Phase.Captured;
                else if (_operation.canceled) State = Phase.Cancelled;
            }
            catch (Exception error)
            {
                GameBindings.Report("a key capture failed: " + error.Message);
                State = Phase.Failed;
            }
            return State;
        }

        internal void Cancel()
        {
            if (Ended || State is not (Phase.Arming or Phase.Listening)) return;
            try
            {
                if (Running) _operation.Cancel();
            }
            catch (Exception error)
            {
                GameBindings.Report("a key capture could not be cancelled: " + error.Message);
            }
            State = Phase.Cancelled;
        }

        /// <summary>Gives everything back; the game's own input follows a moment later, so the press's release lands nowhere.</summary>
        internal void End()
        {
            if (Ended) return;
            Ended = true;
            try
            {
                if (Running) _operation.Cancel();
                _operation?.Dispose();
            }
            catch (Exception error)
            {
                GameBindings.Report("a key capture did not close cleanly: " + error.Message);
            }
            _operation = null;
            _apply = null;
            _focus?.Dispose();
            _focus = null;
            if (!ReferenceEquals(Active, this)) return;
            Active = null;
            FrontInput.Listening = false;
            FrontInput.Pause(0.15f);
            // A key or button still held from the capture must be let go before the menus act again.
            FrontInput.AwaitRelease();
            // The game's own screen gives its input back 150 ms after a capture too.
            _stateOffAt = Time.unscaledTime + Release;
        }

        /// <summary>Every frame from the front end: ends a capture nobody steps and turns the game's state back when due.</summary>
        internal static void Watch()
        {
            // A game dialog, a hidden layer or a closed screen stops the stepping; the capture ends rather than holding
            // the game's input, whose binding state would leave a game dialog deaf.
            var active = Active;
            if (active != null && Time.frameCount - active._steppedFrame > 2)
            {
                active.Cancel();
                active.End();
            }
            if (!_stateOn || Active != null || Time.unscaledTime < _stateOffAt) return;
            _stateOn = false;
            try
            {
                GameInput.SetState(InputState.InputBinding, false);
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Bindings: the game's input could not be given back: " + error.Message);
            }
        }

        private bool Running => _operation != null && _operation.started && !_operation.completed && !_operation.canceled;

        // The game's binding state turns every other action map off, the title's menu events and the pause menu's
        // keys with them. The candidates are filtered by device as the game's own screen filters them, so the keyboard
        // column never takes a controller button and the controller column never takes a key. Unlike the game's
        // screen this leaves the press in the devices' state: suppressing it only delays it, as a controller's next
        // report after the capture shows a button still held as a new press, which the menus would take.
        private void Start()
        {
            try
            {
                GameInput.SetState(InputState.InputBinding, true);
                _stateOn = true;
                var pad = Slot.Device == BindingDevice.Pad;
                _apply = (Action<RebindingOperation, string>)((_, path) => Path = path);
                _operation = new RebindingOperation();
                _operation.WithExpectedControlType("Button");
                _operation.WithControlsHavingToMatchPath(pad ? "<Gamepad>" : "<Keyboard>");
                if (!pad) _operation.WithControlsHavingToMatchPath("<Mouse>");
                foreach (var reserved in pad ? PadReserved : KeyboardReserved) _operation.WithControlsExcluding(reserved);
                _operation.WithCancelingThrough(pad ? "<Gamepad>/start" : "<Keyboard>/escape");
                // Stick drift and a light touch on a trigger do not bind.
                _operation.WithMagnitudeHavingToBeGreaterThan(Magnitude);
                _operation.WithTimeout(Timeout);
                // AltGr arrives as Left Ctrl and Right Alt; the stronger of the two wins.
                _operation.OnMatchWaitForAnother(Settle);
                _operation.OnApplyBinding(_apply);
                StartOperation();
                _startedAt = Time.unscaledTime;
                State = Phase.Listening;
            }
            catch (Exception error)
            {
                GameBindings.Report("a key capture could not start: " + error.Message);
                State = Phase.Failed;
            }
        }

        // With its binding applied by a callback the operation needs no action. Should it want one after all, the
        // first target's is given, now that the binding state has its map switched off, as the game's own screen does.
        private void StartOperation()
        {
            try
            {
                _operation.Start();
            }
            catch (Exception error) when (error.Message.Contains("action", StringComparison.OrdinalIgnoreCase) && Slot.Targets.Count > 0)
            {
                _operation.WithAction(Slot.Targets[0].Action);
                _operation.Start();
            }
        }

        // Escape and Start from either column; the device not listened to cancels too, a click on the controller
        // column and B on the keyboard one. A click on the prompt bar, which shows only Cancel now, cancels on both
        // columns before the operation can take it as the left button.
        private bool CancelPressed()
        {
            var (east, start) = PadButtons();
            var eastDown = east && !_east;
            var startDown = start && !_start;
            _east = east;
            _start = start;
            if (UnityEngine.Input.GetKeyDown(KeyCode.Escape) || startDown) return true;
            if (FrontEnd.OverPrompt && UnityEngine.Input.GetMouseButtonDown(0)) return true;
            return Slot.Device == BindingDevice.Pad
                ? UnityEngine.Input.GetMouseButtonDown(0) || UnityEngine.Input.GetMouseButtonDown(1)
                : eastDown;
        }

        private static bool Cancels(string path)
            => string.IsNullOrEmpty(path) || string.Equals(path, "<Keyboard>/escape", StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, "<Gamepad>/start", StringComparison.OrdinalIgnoreCase);

        private static bool StillHeld()
        {
            if (UnityEngine.Input.GetKey(KeyCode.Return) || UnityEngine.Input.GetKey(KeyCode.KeypadEnter) || UnityEngine.Input.GetKey(KeyCode.Space)
                || UnityEngine.Input.GetMouseButton(0) || UnityEngine.Input.GetMouseButton(1) || UnityEngine.Input.GetMouseButton(2))
                return true;
            try { return Gamepad.current?.buttonSouth.isPressed == true; }
            catch (Exception) { return false; }
        }

        private static (bool East, bool Start) PadButtons()
        {
            try
            {
                var pad = Gamepad.current;
                return pad == null ? default : (pad.buttonEast.isPressed, pad.startButton.isPressed);
            }
            catch (Exception)
            {
                return default;
            }
        }
    }
}
