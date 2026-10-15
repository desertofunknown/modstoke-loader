// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using OpenACAI.Loader;
using SonsSdk;
using SonsSdk.Attributes;
using SUI;
using UnityEngine;
using UnityEngine.SceneManagement;
using static SUI.SUI;

namespace OpenACAI.Endnight.Loader
{
    internal sealed class OpenAcaiModActions
    {
        private static OpenAcaiModActions _current;
        private readonly string _panelId = "OpenACAI.ModActions." + Guid.NewGuid().ToString("N");
        private readonly int _sceneHandle = SceneManager.GetActiveScene().handle;
        private readonly Dictionary<string, Action<SBgButtonOptions>> _actions;
        private SonsMod _mod;
        private SPanelOptions _panel;
        private SLabelOptions _status;
        private Action<bool> _onClosed;

        private OpenAcaiModActions(SonsMod mod, Dictionary<string, Action<SBgButtonOptions>> actions, Action<bool> onClosed)
        {
            _mod = mod;
            _actions = actions;
            _onClosed = onClosed;
        }

        internal static bool HasActions(ModInventoryEntry entry)
        {
            try
            {
                return FindMod(entry)?.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Any(method => method.IsDefined(typeof(ModPanelActionAttribute), inherit: true)) == true;
            }
            catch (Exception exception)
            {
                ReportFailure("Could not inspect mod actions for " + entry?.Id, exception);
                return false;
            }
        }

        internal static bool TryOpen(ModInventoryEntry entry, Action<bool> onClosed)
        {
            if (_current != null || !IsInitialized || !SUIViewport)
            {
                return false;
            }

            var mod = FindMod(entry);
            if (mod == null)
            {
                return false;
            }

            var actions = mod.GetModPanelActions();
            if (actions.Count == 0 || !mod.Registered)
            {
                return false;
            }

            var session = new OpenAcaiModActions(mod, actions, onClosed);
            _current = session;
            try
            {
                session.CreatePanel(entry.DisplayName);
                return true;
            }
            catch
            {
                try
                {
                    session.Close(returnToOwner: false);
                }
                catch (Exception exception)
                {
                    ReportFailure("Could not finish closing mod actions", exception);
                }

                throw;
            }
        }

        internal static void Close()
        {
            _current?.Close(returnToOwner: true);
        }

        // Return true when this frame belonged to the action panel, even if it just closed.
        internal static bool Update()
        {
            var session = _current;
            if (session == null)
            {
                return false;
            }

            if (!session.IsValid())
            {
                session.Close(returnToOwner: false);
            }
            else if (OpenACAIInput.IsPressed(OpenACAIKeybindIds.Back))
            {
                session.Close(returnToOwner: true);
            }

            return true;
        }

        private void CreatePanel(string title)
        {
            _panel = RegisterNewPanel(_panelId, enableInput: true);
            _panel.Active(false);
            _panel.OverrideSorting(200).Dock(EDockType.Fill).Size(-160, -100)
                .Background(new Color(0.07f, 0.07f, 0.08f, 0.98f));
            var heading = SLabel.Text(title).Dock(EDockType.Top).Size(-120, 60).Position(0, -30)
                .FontSize(28).FontColor(Color.white).FontSpacing(0);
            heading.TextObject.enableAutoSizing = true;
            heading.TextObject.fontSizeMin = 16;
            heading.TextObject.fontSizeMax = 28;
            _panel.Add(heading);

            var scroll = SScrollContainer;
            scroll.Dock(EDockType.Fill).Margin(60, 60, 110, 150);
            scroll.ContainerObject.Spacing(6);
            _panel.Add(scroll);
            foreach (var name in _actions.Keys)
            {
                var button = SBgButton.Text(name).PHeight(48).MHeight(48).FlexHeight(0).FontSize(20)
                    .Color(new Color(0.16f, 0.17f, 0.18f)).FontColor(Color.white);
                scroll.Add(button);
                button.TextObject.enableAutoSizing = true;
                button.TextObject.fontSizeMin = 12;
                button.TextObject.fontSizeMax = 20;
                button.TextObject.margin = new Vector4(12, 4, 12, 4);
                button.Notify(context => Invoke(name, context));
            }

            _status = SLabel.Text(string.Empty).Dock(EDockType.Bottom).Size(-120, 36).Position(0, 100)
                .FontSize(18).FontColor(new Color(1f, 0.7f, 0.65f)).FontSpacing(0);
            _panel.Add(_status);
            _panel.Add(SBgButton.Text("Back").Pivot(0, 0).Anchor(AnchorType.BottomLeft)
                .Position(60, 40).Size(180, 48).FontSize(20)
                .Color(new Color(0.35f, 0.08f, 0.09f)).FontColor(Color.white)
                .Notify(() => Close(returnToOwner: true)));
            _panel.Active(true);
        }

        private void Invoke(string name, SBgButtonOptions button)
        {
            if (!IsValid())
            {
                Close(returnToOwner: false);
                return;
            }

            if (!_actions.TryGetValue(name, out var action))
            {
                return;
            }

            try
            {
                _status.Text(string.Empty);
                action(button);
            }
            catch (Exception exception)
            {
                ReportFailure("Mod action '" + name + "' failed", exception);
                if (IsValid())
                {
                    _status.Text("Action failed. See the loader log.");
                }
            }
        }

        private bool IsValid()
        {
            return ReferenceEquals(_current, this) && _mod?.Registered == true
                && SonsMod.RegisteredMods.Any(mod => ReferenceEquals(mod, _mod))
                && SceneManager.GetActiveScene().handle == _sceneHandle
                && _panel?.Root && _panel.Root.activeInHierarchy;
        }

        private void Close(bool returnToOwner)
        {
            if (!ReferenceEquals(_current, this))
            {
                return;
            }

            var canReturn = returnToOwner && IsValid();
            _current = null;
            var onClosed = _onClosed;
            _onClosed = null;
            _mod = null;
            _actions.Clear();
            try
            {
                _panel?.Active(false);
            }
            catch (Exception exception)
            {
                canReturn = false;
                ReportFailure("Could not hide mod actions", exception);
            }
            finally
            {
                try
                {
                    RemovePanel(_panelId);
                }
                catch (Exception exception)
                {
                    canReturn = false;
                    ReportFailure("Could not remove mod actions", exception);
                }

                _panel = null;
                _status = null;
                onClosed?.Invoke(canReturn);
            }
        }

        private static SonsMod FindMod(ModInventoryEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.AssemblyPath))
            {
                return null;
            }

            var path = Path.GetFullPath(entry.AssemblyPath);
            SonsMod match = null;
            foreach (var mod in SonsMod.RegisteredMods)
            {
                if (!mod.Registered || !string.Equals(mod.ID, entry.Id, StringComparison.Ordinal)
                    || !string.Equals(Path.GetFullPath(mod.ModAssembly.Location), path, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (match != null)
                {
                    return null;
                }

                match = mod;
            }

            return match;
        }

        private static void ReportFailure(string message, Exception exception)
        {
            try
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning(message + ": " + exception);
            }
            catch
            {
                // Diagnostics must not prevent panel cleanup or another action.
            }
        }
    }
}
