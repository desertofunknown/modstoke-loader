// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using Modstoke.Sons;
using UnityEngine;

namespace Modstoke.Loader
{
    public enum ScreenHostOrigin
    {
        ModsMenu,
        Tablet,
        Lifecycle,
        OtherMod
    }

    public enum ScreenHostCloseReason
    {
        Requested,
        Back,
        SceneUnloaded,
        WorldExited,
        InputLost,
        Replaced,
        Failed
    }

    public enum ScreenHostState
    {
        Pending,
        Open,
        Closed
    }

    public sealed class ScreenHostRequest
    {
        /// <summary>Required, e.g. "modstoke.client.character". It also owns the input lease and the UI surface.</summary>
        public string OwnerId;
        public string Title;
        public string Subtitle;
        public ScreenHostOrigin Origin;
        /// <summary>SDK keybinds that keep working while the screen holds input. Back is always allowed.</summary>
        public string[] AllowedKeybindIds;
        /// <summary>Allows opening outside the world. Otherwise the screen closes when the world is exited.</summary>
        public bool AllowTitleScene;
        /// <summary>With <see cref="ScreenHostOrigin.ModsMenu"/>, shows the mods menu again at its page on close.</summary>
        public bool ResumeModsMenu = true;
        /// <summary>The caller's own back stack, asked first on Back; true when it handled the press.</summary>
        public Func<bool> TryBack;
        /// <summary>An unsaved-changes guard for Back and <see cref="ScreenHostSession.RequestClose"/>; false keeps the screen open.</summary>
        public Func<bool> CanClose;
        /// <summary>Raised once, first in the close, while the panel and any attached stage still exist.</summary>
        public Action<ScreenHostCloseReason> Closed;

        internal ScreenHostRequest Copy()
        {
            var copy = (ScreenHostRequest)MemberwiseClone();
            copy.AllowedKeybindIds = AllowedKeybindIds?.ToArray();
            return copy;
        }
    }

    /// <summary>One screen opened by <see cref="ScreenHost.TryOpen"/>. Main thread only; calls from other threads do nothing.</summary>
    public sealed class ScreenHostSession : IDisposable
    {
        internal ScreenHostSession(ScreenHostRequest request, string ownerId)
        {
            OwnerId = ownerId;
            Title = string.IsNullOrWhiteSpace(request.Title) ? ownerId : request.Title.Trim();
            Subtitle = request.Subtitle;
            Origin = request.Origin;
            AllowTitleScene = request.AllowTitleScene;
            ResumeModsMenu = request.ResumeModsMenu;
            TryBack = request.TryBack;
            CanClose = request.CanClose;
            Closed = request.Closed;
            var allowed = new List<string> { KeybindIds.Back };
            if (request.AllowedKeybindIds != null)
            {
                allowed.AddRange(request.AllowedKeybindIds.Where(id => !string.IsNullOrWhiteSpace(id)));
            }

            AllowedKeybindIds = allowed.ToArray();
        }

        internal string OwnerId { get; }
        internal string Title { get; }
        internal string Subtitle { get; }
        internal ScreenHostOrigin Origin { get; }
        internal bool AllowTitleScene { get; }
        internal bool ResumeModsMenu { get; }
        internal Func<bool> TryBack { get; }
        internal Func<bool> CanClose { get; }
        internal Action<ScreenHostCloseReason> Closed { get; }
        internal string[] AllowedKeybindIds { get; }
        internal string SurfaceId => ScreenHost.SurfacePrefix + OwnerId;
        internal string Tier { get; set; } = "plain";
        internal int SceneHandle { get; set; }
        internal string SceneName { get; set; }
        internal int OpenedFrame { get; set; }
        internal int PendingFrames { get; set; }
        internal bool SurfaceShown { get; set; }
        internal bool HandoffQueued { get; set; }
        internal bool HandedOff { get; set; }
        internal string ResumePage { get; set; }
        internal bool Closing { get; set; }
        internal bool DisposeRequested { get; set; }
        internal IDisposable Focus { get; set; }
        internal ScreenStageView Stage { get; set; }

        public ScreenHostState State { get; internal set; }

        /// <summary>Raised when a Pending session opens. A session that opens inside TryOpen is already Open on return.</summary>
        public event Action<ScreenHostSession> Opened;

        /// <summary>The themed left pane: header, scrolling rows and footer. Null once closed.</summary>
        public RectTransform LeftPanel => State == ScreenHostState.Closed ? null : ScreenHostView.LeftPanel;

        /// <summary>The unmasked, unshaded area beside the pane, where an attached stage is shown. Null once closed.</summary>
        public RectTransform ContentRect => State == ScreenHostState.Closed ? null : ScreenHostView.ContentRect;

        public bool PointerOverContent => ScreenHost.IsCurrent(this) && State == ScreenHostState.Open && ScreenHostView.PointerOverContent();

        /// <summary>Replaces the pane's rows. Rows with the same ID and kind keep their view, focus and drag.</summary>
        public void SetSections(IEnumerable<ScreenPanelSection> sections)
        {
            if (ScreenHost.IsUsable(this))
            {
                ScreenHostView.Panel?.Bind(sections);
            }
        }

        /// <summary>
        /// Updates one row in place: a number for a slider, an option or index for a choice, true or false for a toggle,
        /// an index (or -1) for swatches, or the label for a button. False when the row is missing or the value does not fit.
        /// No callback runs.
        /// </summary>
        public bool TrySetValue(string rowId, string value)
        {
            return ScreenHost.IsUsable(this) && ScreenHostView.Panel != null && ScreenHostView.Panel.TrySetValue(rowId, value);
        }

        public void SetStatus(string text)
        {
            if (ScreenHost.IsUsable(this))
            {
                ScreenHostView.SetStatus(text);
            }
        }

        /// <summary>Replaces the footer's key hints; null restores the default ones.</summary>
        public void SetHints(string text)
        {
            if (ScreenHost.IsUsable(this))
            {
                ScreenHostView.SetHints(this, text);
            }
        }

        /// <summary>
        /// Shows the stage texture in <see cref="ContentRect"/>, feeds it the viewport size and pointer orbit, zoom and
        /// double-click reset, and keeps <see cref="PreviewStage.Visible"/> in step with the screen. Null detaches.
        /// </summary>
        public void AttachStage(PreviewStage stage, bool disposeStageOnClose = true)
        {
            if (ScreenHost.IsUsable(this))
            {
                ScreenHost.AttachStage(this, stage, disposeStageOnClose);
            }
        }

        /// <summary>Closes after <see cref="ScreenHostRequest.CanClose"/> agrees.</summary>
        public void RequestClose()
        {
            if (ScreenHost.IsUsable(this))
            {
                ScreenHost.RequestClose(this);
            }
        }

        /// <summary>Closes without asking <see cref="ScreenHostRequest.CanClose"/>. Idempotent; off the game thread it closes on the next Loader tick.</summary>
        public void Dispose()
        {
            ScreenHost.Dispose(this);
        }

        internal void RaiseOpened()
        {
            var handlers = Opened;
            if (handlers == null)
            {
                return;
            }

            foreach (var handler in Delegate.EnumerateInvocationList(handlers))
            {
                try
                {
                    handler(this);
                }
                catch (Exception error)
                {
                    RuntimeEntryPoint.LogSource?.LogWarning("Screen host Opened handler for " + OwnerId + " failed: " + error.Message);
                }
            }
        }
    }

    public sealed class ScreenPanelSection
    {
        public ScreenPanelSection(string title, IEnumerable<ScreenPanelRow> rows)
        {
            Title = title;
            Rows = rows?.Where(row => row != null).ToArray() ?? Array.Empty<ScreenPanelRow>();
        }

        public string Title { get; }
        public IReadOnlyList<ScreenPanelRow> Rows { get; }
    }

    internal enum ScreenPanelRowKind
    {
        Header,
        Slider,
        Choice,
        Toggle,
        Button,
        Swatches
    }

    /// <summary>One row of a screen panel. Callbacks run on the main thread; an exception is logged and shown in the status line.</summary>
    public sealed class ScreenPanelRow
    {
        private ScreenPanelRow(ScreenPanelRowKind kind, string id, string label, MenuControl control)
        {
            Kind = kind;
            Id = id;
            Label = label ?? "";
            Control = control;
        }

        public string Id { get; }
        public string Label { get; internal set; }
        internal ScreenPanelRowKind Kind { get; }
        internal MenuControl Control { get; }
        internal IReadOnlyList<string> Options { get; private set; } = Array.Empty<string>();
        internal float Number { get; set; }
        internal int Index { get; set; }
        internal bool On { get; set; }
        internal Action<float> Live { get; private set; }
        internal Action<float> Commit { get; private set; }
        internal Action<int> IndexChanged { get; private set; }
        internal Action<bool> ToggleChanged { get; private set; }
        internal Action Clicked { get; private set; }

        /// <summary>A section heading inside a section's rows.</summary>
        public static ScreenPanelRow Header(string text)
        {
            return new ScreenPanelRow(ScreenPanelRowKind.Header, null, text, MenuControl.None);
        }

        /// <param name="live">Called on every move of the slider.</param>
        /// <param name="commit">Called once the slider has been still for a moment or the pointer lets go.</param>
        /// <param name="step">Snaps the value to this step from <paramref name="min"/>; 0 leaves it continuous.</param>
        public static ScreenPanelRow Slider(string id, string label, float min, float max, float value,
            Action<float> live, Action<float> commit, float step = 0f)
        {
            if (!float.IsFinite(min) || !float.IsFinite(max) || min == max)
                throw new ArgumentException("A slider needs two different finite limits.", nameof(max));
            if (min > max)
                (min, max) = (max, min);
            var control = new MenuControl
            {
                Kind = MenuControlKind.Slider,
                Min = min,
                Max = max,
                Step = float.IsFinite(step) && step > 0f ? (double?)step : null,
                Editable = true,
                ValueKind = ConfigValueKinds.Number
            };
            return new ScreenPanelRow(ScreenPanelRowKind.Slider, RequireId(id), label, control)
            {
                Number = Mathf.Clamp(float.IsFinite(value) ? value : min, min, max),
                Live = live,
                Commit = commit
            };
        }

        /// <param name="options">Labels shown in turn; they should be unique, as a choice is matched back by its label.</param>
        public static ScreenPanelRow Choice(string id, string label, IReadOnlyList<string> options, int index, Action<int> changed)
        {
            var labels = options?.Select(option => string.IsNullOrWhiteSpace(option) ? "-" : option.Trim()).ToArray();
            if (labels == null || labels.Length == 0)
                throw new ArgumentException("A choice needs at least one option.", nameof(options));
            var control = new MenuControl { Kind = MenuControlKind.Choice, Options = labels, Editable = true, ValueKind = ConfigValueKinds.Choice };
            return new ScreenPanelRow(ScreenPanelRowKind.Choice, RequireId(id), label, control)
            {
                Options = labels,
                Index = Math.Clamp(index, 0, labels.Length - 1),
                IndexChanged = changed
            };
        }

        public static ScreenPanelRow Toggle(string id, string label, bool value, Action<bool> changed)
        {
            var control = new MenuControl { Kind = MenuControlKind.Toggle, Editable = true, ValueKind = ConfigValueKinds.Boolean };
            return new ScreenPanelRow(ScreenPanelRowKind.Toggle, RequireId(id), label, control)
            {
                On = value,
                ToggleChanged = changed
            };
        }

        public static ScreenPanelRow Button(string id, string label, Action clicked)
        {
            var control = new MenuControl { Kind = MenuControlKind.Open, Editable = true };
            return new ScreenPanelRow(ScreenPanelRowKind.Button, RequireId(id), label, control) { Clicked = clicked };
        }

        /// <param name="index">The selected swatch, or -1 for none.</param>
        public static ScreenPanelRow Swatches(string id, string label, IReadOnlyList<Color> colors, int index, Action<int> changed)
        {
            if (colors == null || colors.Count == 0)
                throw new ArgumentException("A swatch row needs at least one colour.", nameof(colors));
            var encoded = colors.Select(SwatchView.Encode).ToArray();
            var control = new MenuControl { Kind = MenuControlKind.Swatches, Options = encoded, Editable = true };
            return new ScreenPanelRow(ScreenPanelRowKind.Swatches, RequireId(id), label, control)
            {
                Options = encoded,
                Index = index >= 0 && index < encoded.Length ? index : -1,
                IndexChanged = changed
            };
        }

        private static string RequireId(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("A screen panel row needs an ID.", nameof(id));
            return id.Trim();
        }
    }

    public sealed class ScreenHostSnapshot
    {
        private ScreenHostSnapshot(string ownerId, ScreenHostState state, ScreenHostOrigin origin, string tier, string detail, string updatedUtc)
        {
            OwnerId = ownerId;
            State = state;
            Origin = origin;
            Tier = tier;
            Detail = detail;
            UpdatedUtc = updatedUtc;
        }

        /// <summary>The owner of the current or last screen, or null before the first one.</summary>
        public string OwnerId { get; }
        public ScreenHostState State { get; }
        public ScreenHostOrigin Origin { get; }
        /// <summary>The mods menu theme tier the screen was built with: native, mixed or plain.</summary>
        public string Tier { get; }
        public string Detail { get; }
        public string UpdatedUtc { get; }

        internal static ScreenHostSnapshot Create(string ownerId, ScreenHostState state, ScreenHostOrigin origin, string tier, string detail)
        {
            return new ScreenHostSnapshot(ownerId, state, origin, string.IsNullOrWhiteSpace(tier) ? "plain" : tier, detail,
                DateTimeOffset.UtcNow.ToString("O"));
        }
    }
}
