// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Sons;
using Sons.Gui;
using Sons.Gui.Multiplayer;
using Sons.Multiplayer;
using Sons.Multiplayer.Dedicated;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Modstoke.Loader
{
    /// <summary>
    /// The Modstoke Join screen, drawn over the game's browser scene. The game's lobby and dedicated server
    /// browsers keep running underneath and its own server row does the joining, so passwords, version checks
    /// and lobby joins behave as in the game; this screen only lists, explains and asks.
    /// </summary>
    internal static partial class MultiplayerScreen
    {
        private enum Tab { All, Hosted, Dedicated, Lan }

        private static readonly string[] TabNames = { "ALL", "HOSTED", "DEDICATED", "LAN" };
        private const float DesignHeight = 860f;
        private const float RefreshSeconds = 0.5f;
        private const float DoubleClickSeconds = 0.4f;
        private static readonly Regex Tags = new("<[^>]*>", RegexOptions.Compiled);

        private sealed class Entry
        {
            internal IServerInfo Info;
            internal DedicatedServerInfo Dedicated;
            internal IntPtr Id;
            internal string Key, Name, Mode;
            internal int Players, Limit, Ping;
            internal bool Password, VersionMatch, LegacyModded;
        }

        private static GameServerBrowserGui _gui;
        private static CoopLobbyDialogGui _lobbyDialog;
        private static CanvasGroup _gameList;
        private static Button _back;
        private static GameObject _rootObject;
        private static Canvas _canvas;
        private static ServerInfoGuiItem _joiner;
        private static float _s = 1f;
        private static Tab _tab;
        private static readonly List<Entry> Entries = new();
        private static int _selected, _top;
        private static IntPtr _selectedId;
        private static float _nextRefresh, _repeatAt, _lastClick;
        private static int _repeatDir, _lastClickRow = -1;
        private static bool _dirty;

        internal static void Open(Scene scene)
        {
            Close();
            var canvasRoot = scene.GetRootGameObjects().FirstOrDefault(root => root != null && root.name == "MultiplayerMenuCanvas");
            _gui = canvasRoot != null ? canvasRoot.GetComponentInChildren<GameServerBrowserGui>(true) : null;
            if (_gui == null || !_gui)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The game's server browser was not found; its own screen stays.");
                return;
            }
            _lobbyDialog = canvasRoot.GetComponentInChildren<CoopLobbyDialogGui>(true);
            var layout = _gui.transform.Find("Layout");
            if (layout != null)
            {
                _gameList = layout.GetComponent<CanvasGroup>();
                if (_gameList == null || !_gameList) _gameList = layout.gameObject.AddComponent<CanvasGroup>();
                HideGameList();
            }
            var strip = _gui.transform.Find("GameServerBottomStrip");
            _back = strip != null ? strip.GetComponentsInChildren<Button>(true).FirstOrDefault(button => button.name == "BackButton") : null;
            Build((RectTransform)canvasRoot.transform, strip != null ? strip.GetComponent<RectTransform>() : null);
            ServerBrowserBadges.Answered += OnAnswered;
            SetTab(Tab.All);
            RuntimeEntryPoint.LogSource?.LogInfo("Showing the Modstoke multiplayer screen.");
        }

        internal static void Close()
        {
            ServerBrowserBadges.Answered -= OnAnswered;
            if (_gameList != null && _gameList)
            {
                _gameList.alpha = 1;
                _gameList.interactable = true;
                _gameList.blocksRaycasts = true;
            }
            if (_rootObject != null && _rootObject) Object.Destroy(_rootObject);
            if (_joiner != null && _joiner) Object.Destroy(_joiner.gameObject);
            _rootObject = null;
            _joiner = null;
            _gui = null;
            _lobbyDialog = null;
            _gameList = null;
            _back = null;
            Entries.Clear();
        }

        internal static void Tick()
        {
            if (_rootObject == null) return;
            if (!_rootObject || _gui == null || !_gui)
            {
                Close();
                return;
            }
            // Joining a hosted game opens the game's own lobby, which this screen stays out of.
            var inLobby = _lobbyDialog != null && _lobbyDialog && _lobbyDialog.gameObject.activeInHierarchy;
            if (_rootObject.activeSelf == inLobby) _rootObject.SetActive(!inLobby);
            if (inLobby) return;
            HideGameList();
            KeepSelectionOurs();
            if (!GameDialogOpen()) ReadInput();
            if (_dirty || Time.unscaledTime >= _nextRefresh)
            {
                _dirty = false;
                _nextRefresh = Time.unscaledTime + RefreshSeconds;
                Collect();
                Paint();
            }
        }

        private static void HideGameList()
        {
            if (_gameList == null || !_gameList) return;
            _gameList.alpha = 0;
            _gameList.interactable = false;
            _gameList.blocksRaycasts = false;
        }

        // The game's hidden list selects its own rows on refresh; the controller must act on this screen instead.
        private static void KeepSelectionOurs()
        {
            var system = EventSystem.current;
            var selected = system != null ? system.currentSelectedGameObject : null;
            if (selected != null && selected && !selected.transform.IsChildOf(_rootObject.transform))
                system.SetSelectedGameObject(null);
        }

        private static bool GameDialogOpen()
        {
            var manager = ModalDialogManager._instance;
            var current = manager != null && manager ? manager.Current : null;
            return current != null && current && current.gameObject.activeInHierarchy;
        }

        private static void ReadInput()
        {
            var pad = Gamepad.current;
            var keys = Keyboard.current;
            var now = Time.unscaledTime;
            var up = pad != null && (pad.dpad.up.isPressed || pad.leftStick.y.ReadValue() > 0.6f) || keys != null && keys.upArrowKey.isPressed;
            var down = pad != null && (pad.dpad.down.isPressed || pad.leftStick.y.ReadValue() < -0.6f) || keys != null && keys.downArrowKey.isPressed;
            var direction = up == down ? 0 : up ? -1 : 1;
            if (direction != _repeatDir)
            {
                _repeatDir = direction;
                _repeatAt = now + 0.35f;
                if (direction != 0) Move(direction);
            }
            else if (direction != 0 && now >= _repeatAt)
            {
                _repeatAt = now + 0.07f;
                Move(direction);
            }

            if (pad != null && pad.leftShoulder.wasPressedThisFrame || keys != null && keys.qKey.wasPressedThisFrame) CycleTab(-1);
            if (pad != null && pad.rightShoulder.wasPressedThisFrame || keys != null && keys.eKey.wasPressedThisFrame) CycleTab(1);
            if (pad != null && pad.rightTrigger.wasPressedThisFrame) Host();
            if (pad != null && pad.buttonWest.wasPressedThisFrame || keys != null && keys.f5Key.wasPressedThisFrame) Refresh();
            if (pad != null && pad.buttonSouth.wasPressedThisFrame || keys != null && (keys.enterKey.wasPressedThisFrame || keys.numpadEnterKey.wasPressedThisFrame))
                Join();

            var mouse = Mouse.current;
            if (mouse == null || _listRect == null) return;
            var scroll = mouse.scroll.ReadValue().y;
            if (scroll != 0 && RectTransformUtility.RectangleContainsScreenPoint(_listRect, mouse.position.ReadValue(), Camera()))
            {
                _top = Mathf.Clamp(_top - Math.Sign(scroll) * 3, 0, Math.Max(0, Entries.Count - _rows.Length));
                Paint();
            }
        }

        private static UnityEngine.Camera Camera()
            => _canvas != null && _canvas && _canvas.rootCanvas.renderMode != RenderMode.ScreenSpaceOverlay ? _canvas.rootCanvas.worldCamera : null;

        private static void SetTab(Tab tab)
        {
            _tab = tab;
            _top = 0;
            _selected = 0;
            _selectedId = IntPtr.Zero;
            try
            {
                var mode = tab switch
                {
                    Tab.Hosted => ServerBrowserRefreshType.PeerToPeer,
                    Tab.Lan => ServerBrowserRefreshType.DedicatedLan,
                    _ => ServerBrowserRefreshType.DedicatedInternet
                };
                if (_gui.GetBrowserMode() != mode) _gui.ChangeBrowserMode((int)mode);
                // All lists both kinds, so the lobby browser refreshes beside the dedicated one.
                if (tab == Tab.All) _gui._lobbyBrowser.RefreshLobbiesListAsync(ServerBrowserRefreshType.PeerToPeer);
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The server list could not switch to " + TabNames[(int)tab] + ": " + error.Message);
            }
            _dirty = true;
        }

        private static void CycleTab(int step) => SetTab((Tab)(((int)_tab + step + TabNames.Length) % TabNames.Length));

        private static void Refresh()
        {
            try
            {
                _gui.Refresh();
                if (_tab == Tab.All) _gui._lobbyBrowser.RefreshLobbiesListAsync(ServerBrowserRefreshType.PeerToPeer);
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The server list could not refresh: " + error.Message);
            }
            _dirty = true;
        }

        private static void Host()
        {
            if (_back == null || !_back)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Host is unavailable: the game's Back button was not found.");
                return;
            }
            MultiplayerTakeover.RequestHost(_back);
        }

        private static void Move(int step)
        {
            if (Entries.Count == 0) return;
            Select(Mathf.Clamp(_selected + step, 0, Entries.Count - 1));
        }

        private static void Select(int index)
        {
            if (index < 0 || index >= Entries.Count) return;
            _selected = index;
            _selectedId = Entries[index].Id;
            if (_selected < _top) _top = _selected;
            else if (_selected >= _top + _rows.Length) _top = _selected - _rows.Length + 1;
            Paint();
        }

        private static void OnRowClicked(int slot)
        {
            var index = _top + slot;
            var now = Time.unscaledTime;
            if (index == _selected && slot == _lastClickRow && now - _lastClick < DoubleClickSeconds) Join();
            else Select(index);
            _lastClick = now;
            _lastClickRow = slot;
        }

        private static void Join()
        {
            if (_selected < 0 || _selected >= Entries.Count) return;
            var entry = Entries[_selected];
            try
            {
                if (_joiner == null || !_joiner)
                {
                    var template = _gui._serverInfoGuiItemTemplate;
                    // The hidden list holds it, so the game's own join code runs without the row showing.
                    _joiner = Object.Instantiate(template, _gameList != null ? _gameList.transform : _gui.transform, false);
                    _joiner.gameObject.name = "ModstokeJoinRow";
                }
                IServerBrowser browser = entry.Dedicated != null
                    ? _gui._dedicatedBrowser.Cast<IServerBrowser>()
                    : _gui._lobbyBrowser.Cast<IServerBrowser>();
                var all = browser.Entries;
                var index = 0;
                for (var i = 0; i < all.Length; i++)
                    if (all[i] != null && all[i].Pointer == entry.Id) index = i;
                _joiner.Setup(entry.Info, index, all, browser);
                _joiner.DoJoin();
                RuntimeEntryPoint.LogSource?.LogInfo("Joining " + entry.Name + ".");
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Could not join " + entry.Name + ": " + error.Message);
            }
        }

        private static void OnAnswered(string key) => _dirty = true;

        private static void Collect()
        {
            Entries.Clear();
            try
            {
                if (_tab is Tab.All or Tab.Hosted) Add(_gui._lobbyBrowser.Cast<IServerBrowser>().Entries);
                var dedicated = _gui._dedicatedBrowser;
                var internet = dedicated._isLastAnInternetDedicatedRequest;
                if (_tab == Tab.Dedicated && internet || _tab == Tab.All && internet || _tab == Tab.Lan && !internet)
                    Add(dedicated.Cast<IServerBrowser>().Entries);
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The server list could not be read: " + error.Message);
            }
            var keep = _selectedId == IntPtr.Zero ? -1 : Entries.FindIndex(entry => entry.Id == _selectedId);
            _selected = keep >= 0 ? keep : Mathf.Clamp(_selected, 0, Math.Max(0, Entries.Count - 1));
            if (Entries.Count > 0) _selectedId = Entries[_selected].Id;
            _top = Mathf.Clamp(_top, 0, Math.Max(0, Entries.Count - _rows.Length));
            if (_selected < _top || _selected >= _top + _rows.Length) _top = Mathf.Clamp(_selected - _rows.Length / 2, 0, Math.Max(0, Entries.Count - _rows.Length));
        }

        private static void Add(Il2CppReferenceArray<IServerInfo> infos)
        {
            if (infos == null) return;
            foreach (var info in infos)
            {
                if (info == null || !info.IsValid) continue;
                var raw = info.Name ?? "";
                var entry = new Entry
                {
                    Info = info,
                    Id = info.Pointer,
                    Dedicated = info.TryCast<DedicatedServerInfo>(),
                    Name = CleanName(string.IsNullOrWhiteSpace(info.CleanName) ? raw : info.CleanName),
                    Mode = TitleCase(info.GameMode),
                    Players = info.CurrentMembers,
                    Limit = info.MemberLimit,
                    Ping = info.Ping,
                    Password = info.IsPasswordProtected,
                    VersionMatch = info.VersionMatch,
                    LegacyModded = raw.Contains("[MODDED]", StringComparison.Ordinal)
                };
                if (entry.Dedicated != null)
                {
                    var address = entry.Dedicated.NetAddr;
                    entry.Key = ServerBrowserBadges.IpText(address.GetIP()) + ":" + address.GetQueryPort();
                }
                Entries.Add(entry);
            }
        }

        private static void Ask(Entry entry)
        {
            if (entry?.Dedicated == null || entry.Key == null) return;
            var address = entry.Dedicated.NetAddr;
            ServerBrowserBadges.Ask(entry.Key, address.GetIP(), address.GetQueryPort());
        }

        private static string CleanName(string name)
        {
            var text = Tags.Replace(name ?? "", "").Trim();
            return text.StartsWith("[MODDED]", StringComparison.Ordinal) ? text.Substring(8).Trim() : text;
        }

        private static string TitleCase(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var clean = text.Replace('_', ' ').Trim();
            return char.ToUpperInvariant(clean[0]) + clean.Substring(1).ToLowerInvariant();
        }
    }
}
