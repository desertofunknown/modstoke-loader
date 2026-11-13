// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Sons;
using Sons.Gui;
using Sons.Gui.Multiplayer;
using Sons.Multiplayer;
using Sons.Multiplayer.Dedicated;
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
        private static readonly HashSet<string> FailedInputs = new();
        private static int _selected, _top;
        private static IntPtr _selectedId;
        private static float _nextRefresh, _repeatAt, _lastClick;
        private static int _repeatDir, _lastClickRow = -1;
        private static bool _dirty, _acting;
        private const float InputPauseSeconds = 0.3f;
        private static float _resumeInputAt;

        internal static void Open(Scene scene)
        {
            Close();
            var canvasRoot = scene.GetRootGameObjects().FirstOrDefault(root => root != null && root.name == "MultiplayerMenuCanvas");
            _gui = canvasRoot != null ? canvasRoot.GetComponentInChildren<GameServerBrowserGui>(true) : null;
            var canvasRect = canvasRoot != null ? canvasRoot.GetComponent<RectTransform>() : null;
            if (_gui == null || !_gui || canvasRect == null)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The game's server browser was not found; its own screen stays.");
                _gui = null;
                return;
            }
            _lobbyDialog = canvasRoot.GetComponentInChildren<CoopLobbyDialogGui>(true);
            var layout = _gui.transform.Find("Layout");
            if (layout != null)
            {
                _gameList = layout.GetComponent<CanvasGroup>();
                if (_gameList == null || !_gameList) _gameList = layout.gameObject.AddComponent<CanvasGroup>();
            }
            var strip = _gui.transform.Find("GameServerBottomStrip");
            _back = strip != null ? strip.GetComponentsInChildren<Button>(true).FirstOrDefault(button => button != null && button.name == "BackButton") : null;
            Build(canvasRect, strip != null ? strip.GetComponent<RectTransform>() : null);
            HideGameList();
            FailedInputs.Clear();
            _resumeInputAt = Time.unscaledTime + InputPauseSeconds;
            ServerBrowserBadges.Answered += OnAnswered;
            SetTab(Tab.All);
            RuntimeEntryPoint.LogSource?.LogInfo("Showing the Modstoke multiplayer screen.");
        }

        internal static void Close()
        {
            ServerBrowserBadges.Answered -= OnAnswered;
            try
            {
                if (_gameList != null && _gameList)
                {
                    _gameList.alpha = 1;
                    _gameList.interactable = true;
                    _gameList.blocksRaycasts = true;
                }
                if (_rootObject != null && _rootObject) Object.Destroy(_rootObject);
                if (_joiner != null && _joiner) Object.Destroy(_joiner.gameObject);
            }
            catch (Exception)
            {
                // The scene may already be unloading with these objects.
            }
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
            var away = _lobbyDialog != null && _lobbyDialog && _lobbyDialog.gameObject.activeInHierarchy
                || !_gui.gameObject.activeInHierarchy;
            if (_rootObject.activeSelf == away) _rootObject.SetActive(!away);
            // The press that closes a lobby or a dialog must not also act on this screen.
            var dialog = !away && GameDialogOpen();
            if (away || dialog) _resumeInputAt = Time.unscaledTime + InputPauseSeconds;
            _acting = Time.unscaledTime >= _resumeInputAt;
            if (away)
            {
                ReadDevice("gamepad", ReadPad);
                return;
            }
            HideGameList();
            if (!dialog) ClearSelection();
            ReadDevice("gamepad", ReadPad);
            ReadDevice("keyboard", ReadKeys);
            ReadDevice("mouse", ReadMouse);
            if (!_dirty && Time.unscaledTime < _nextRefresh) return;
            _dirty = false;
            // A long server list takes a moment to read, so it is read less often the longer it takes.
            var watch = Stopwatch.StartNew();
            Collect();
            _nextRefresh = Time.unscaledTime + Mathf.Clamp((float)watch.Elapsed.TotalSeconds * 25f, 0.5f, 5f);
            Paint();
        }

        private static void HideGameList()
        {
            if (_gameList == null || !_gameList || _gameList.alpha == 0 && !_gameList.interactable) return;
            _gameList.alpha = 0;
            _gameList.interactable = false;
            _gameList.blocksRaycasts = false;
        }

        // The game's hidden list selects its own rows on refresh; this screen moves its own focus instead.
        private static void ClearSelection()
        {
            var system = EventSystem.current;
            if (system == null || !system) return;
            var selected = system.currentSelectedGameObject;
            if (selected != null && selected) system.SetSelectedGameObject(null);
        }

        private static bool GameDialogOpen()
        {
            try
            {
                var manager = ModalDialogManager._instance;
                var current = manager != null && manager ? manager.Current : null;
                return current != null && current && current.gameObject.activeInHierarchy;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // One input device failing must not take the screen down; the others keep working.
        private static void ReadDevice(string device, Action read)
        {
            if (FailedInputs.Contains(device)) return;
            try
            {
                read();
            }
            catch (Exception error)
            {
                FailedInputs.Add(device);
                RuntimeEntryPoint.LogSource?.LogWarning("The multiplayer screen stopped reading the " + device + ": " + error.Message);
            }
        }

        private static readonly bool[] PadDown = new bool[4];

        // Buttons are tracked even while input is paused, so one held through a dialog does not fire afterwards.
        private static void ReadPad()
        {
            var pad = Gamepad.current;
            if (pad == null) return;
            bool previous = Pressed(0, pad.leftShoulder.isPressed), next = Pressed(1, pad.rightShoulder.isPressed);
            bool host = Pressed(2, pad.rightTrigger.isPressed), join = Pressed(3, pad.buttonSouth.isPressed);
            if (!_acting) return;
            Steer("pad", pad.dpad.up.isPressed || pad.leftStick.up.isPressed, pad.dpad.down.isPressed || pad.leftStick.down.isPressed);
            if (previous) CycleTab(-1);
            if (next) CycleTab(1);
            if (host) Host();
            if (join) Join();
        }

        private static bool Pressed(int index, bool down)
        {
            var pressed = down && !PadDown[index];
            PadDown[index] = down;
            return pressed;
        }

        private static void ReadKeys()
        {
            if (!_acting) return;
            Steer("keys", UnityEngine.Input.GetKey(KeyCode.UpArrow), UnityEngine.Input.GetKey(KeyCode.DownArrow));
            if (UnityEngine.Input.GetKeyDown(KeyCode.Q)) CycleTab(-1);
            if (UnityEngine.Input.GetKeyDown(KeyCode.E)) CycleTab(1);
            if (UnityEngine.Input.GetKeyDown(KeyCode.F5)) Refresh();
            if (UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter)) Join();
        }

        private static void ReadMouse()
        {
            if (!_acting || _listRect == null || _rows.Length == 0) return;
            var scroll = UnityEngine.Input.mouseScrollDelta.y;
            if (scroll == 0) return;
            var pointer = UnityEngine.Input.mousePosition;
            if (!RectTransformUtility.RectangleContainsScreenPoint(_listRect, new Vector2(pointer.x, pointer.y), UiCamera())) return;
            _top = Mathf.Clamp(_top - Math.Sign(scroll) * 3, 0, Math.Max(0, Entries.Count - _rows.Length));
            Paint();
        }

        private static string _steerDevice;

        // Held up or down repeats after a pause, like the game's menus.
        private static void Steer(string device, bool up, bool down)
        {
            var direction = up == down ? 0 : up ? -1 : 1;
            if (direction == 0)
            {
                if (_steerDevice == device)
                {
                    _steerDevice = null;
                    _repeatDir = 0;
                }
                return;
            }
            var now = Time.unscaledTime;
            if (_steerDevice != device || direction != _repeatDir)
            {
                _steerDevice = device;
                _repeatDir = direction;
                _repeatAt = now + 0.35f;
                Move(direction);
            }
            else if (now >= _repeatAt)
            {
                _repeatAt = now + 0.07f;
                Move(direction);
            }
        }

        private static Camera UiCamera()
        {
            if (_canvas == null || !_canvas) return null;
            var root = _canvas.rootCanvas;
            return root != null && root && root.renderMode != RenderMode.ScreenSpaceOverlay ? root.worldCamera : null;
        }

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
                var browser = entry.Dedicated != null
                    ? _gui._dedicatedBrowser.Cast<IServerBrowser>()
                    : _gui._lobbyBrowser.Cast<IServerBrowser>();
                var all = browser.Entries;
                var index = 0;
                for (var i = 0; i < all.Length; i++)
                    if (all[i] != null && all[i].Pointer == entry.Id) index = i;
                var row = JoinRow();
                row.Setup(entry.Info, index, all, browser);
                row.DoJoin();
                RuntimeEntryPoint.LogSource?.LogInfo("Joining " + entry.Name + ".");
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Could not join " + entry.Name + ": " + error);
            }
        }

        // One of the game's own server rows, kept out of sight: its join code handles passwords, version checks,
        // lobbies and dedicated servers exactly as a click in the game's list would.
        private static ServerInfoGuiItem JoinRow()
        {
            if (_joiner != null && _joiner) return _joiner;
            ServerInfoGuiItem row = null;
            try
            {
                row = _gui.CreateServerInfoGuiItem();
            }
            catch (Exception)
            {
                // Falls back to a plain copy of the row template below.
            }
            if (row == null || !row) row = Object.Instantiate(_gui._serverInfoGuiItemTemplate);
            row.gameObject.name = "ModstokeJoinRow";
            // Outside the game's list, so its row pool never recycles it, and ignored by the hidden layout.
            row.transform.SetParent(_gameList != null && _gameList ? _gameList.transform : _gui.transform, false);
            var element = row.gameObject.GetComponent<LayoutElement>();
            if (element == null || !element) element = row.gameObject.AddComponent<LayoutElement>();
            element.ignoreLayout = true;
            row.gameObject.SetActive(true);
            return _joiner = row;
        }

        private static void OnAnswered(string key) => _dirty = true;

        private static void Collect()
        {
            Entries.Clear();
            try
            {
                if (_tab is Tab.All or Tab.Hosted) Add(_gui._lobbyBrowser.Entries);
                var dedicated = _gui._dedicatedBrowser;
                var internet = dedicated._isLastAnInternetDedicatedRequest;
                if (_tab is Tab.Dedicated or Tab.All && internet || _tab == Tab.Lan && !internet)
                    Add(dedicated.Entries);
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The server list could not be read: " + error.Message);
            }
            var rows = Math.Max(1, _rows.Length);
            var keep = _selectedId == IntPtr.Zero ? -1 : Entries.FindIndex(entry => entry.Id == _selectedId);
            _selected = keep >= 0 ? keep : Mathf.Clamp(_selected, 0, Math.Max(0, Entries.Count - 1));
            if (Entries.Count > 0) _selectedId = Entries[_selected].Id;
            _top = Mathf.Clamp(_top, 0, Math.Max(0, Entries.Count - rows));
        }

        private static void Add(Il2CppReferenceArray<IServerInfo> infos)
        {
            if (infos == null) return;
            for (var i = 0; i < infos.Length; i++)
            {
                try
                {
                    var info = infos[i];
                    if (info == null || !info.IsValid) continue;
                    var raw = info.Name ?? "";
                    var clean = info.CleanName;
                    var entry = new Entry
                    {
                        Info = info,
                        Id = info.Pointer,
                        Dedicated = info.TryCast<DedicatedServerInfo>(),
                        Name = CleanName(string.IsNullOrWhiteSpace(clean) ? raw : clean),
                        Mode = Words(info.GameMode),
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
                catch (Exception)
                {
                    // A server that changes while it is read shows up on the next pass.
                }
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
            if (text.StartsWith("[MODDED]", StringComparison.Ordinal)) text = text.Substring(8).Trim();
            return text.Length > 0 ? text : "Unnamed server";
        }

        // "HardSurvival" and "hard_survival" both read "Hard survival".
        private static string Words(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var words = new StringBuilder();
            var afterLower = false;
            foreach (var c in text.Trim())
            {
                if (c is '_' or ' ')
                {
                    if (words.Length > 0 && words[words.Length - 1] != ' ') words.Append(' ');
                }
                else
                {
                    // A capital after a lower-case letter starts a new word.
                    if (char.IsUpper(c) && afterLower) words.Append(' ');
                    words.Append(words.Length == 0 ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
                }
                afterLower = char.IsLower(c);
            }
            return words.ToString();
        }
    }
}
