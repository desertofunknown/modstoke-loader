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
using GameBackButton = Sons.Gui.Options.BackButton;
using Object = UnityEngine.Object;

namespace Modstoke.Loader
{
    /// <summary>
    /// The Modstoke Join screen, drawn over the game's browser scene. The game's lobby and dedicated server
    /// browsers keep running underneath and its own server row does the joining, so passwords, version checks
    /// and lobby joins behave as in the game; this screen lists, searches, sorts, explains and asks.
    /// </summary>
    internal static partial class MultiplayerScreen
    {
        private enum Tab { All, Hosted, Dedicated, Lan }

        private enum Zone { List, Headers, Chips }

        private enum SortKey { None, Name, Mods, Players, Ping }

        // Ordered best first, which is also the order "sort by mods" lists them in.
        private enum ModState { Match, Needs, AddsItems, Differs, Modded, Checking, Vanilla, Hosted, OtherVersion }

        private static readonly string[] TabNames = { "ALL", "HOSTED", "DEDICATED", "LAN" };
        private static readonly string[] ChipNames = { "MODSTOKE", "HAS PLAYERS", "NOT FULL", "NO PASSWORD" };
        private const float DesignHeight = 860f;
        private const float DoubleClickSeconds = 0.4f;
        private const float InputPauseSeconds = 0.3f;
        private const int MaxSearchLength = 40;
        private static readonly Regex Tags = new("<[^>]*>", RegexOptions.Compiled);

        private sealed class Entry
        {
            internal IServerInfo Info;
            internal DedicatedServerInfo Dedicated;
            internal IntPtr Id;
            internal string Key, Name, Mode;
            internal int Order, Players, Limit, Ping;
            internal bool Password, VersionMatch, Modded;
        }

        // A clickable area of the screen. Clicks and hover are tested against these directly, so nothing the game
        // draws above or below can swallow or receive them.
        private sealed class Hot
        {
            internal RectTransform Rect;
            internal Action Click;
            internal Action<bool> Hover;
        }

        private static GameServerBrowserGui _gui;
        private static CoopLobbyDialogGui _lobbyDialog;
        private static GameObject _canvasRoot, _rootObject;
        private static CanvasGroup _gameList, _stripButtons;
        private static readonly List<GameObject> StripHidden = new();
        private static readonly List<GameBackButton> Backs = new();
        private static Button _back;
        private static Canvas _canvas;
        private static ServerInfoGuiItem _joiner;
        private static float _s = 1f;
        private static Tab _tab;
        private static Zone _zone;
        private static SortKey _sort;
        private static bool _descending;
        private static readonly bool[] Chips = new bool[4];
        private static readonly List<Entry> Listed = new();
        private static readonly List<Entry> Entries = new();
        private static readonly List<Hot> Hots = new();
        private static readonly HashSet<string> FailedInputs = new();
        private static readonly bool[] PadDown = new bool[8];
        private static Hot _hover, _searchHot;
        private static string _search = "", _mine = "", _steerDevice;
        private static int _selected, _top, _tool, _hoverRow = -1, _repeatDir, _lastClickRow = -1;
        private static IntPtr _selectedId;
        private static float _nextRefresh, _repeatAt, _lastClick, _resumeInputAt, _backResumeAt;
        private static bool _dirty, _filterDirty, _acting, _searching, _backsPaused, _pad;

        internal static void Open(Scene scene)
        {
            Close();
            _canvasRoot = scene.GetRootGameObjects().FirstOrDefault(root => root != null && root.name == "MultiplayerMenuCanvas");
            _gui = _canvasRoot != null ? _canvasRoot.GetComponentInChildren<GameServerBrowserGui>(true) : null;
            var canvasRect = _canvasRoot != null ? _canvasRoot.GetComponent<RectTransform>() : null;
            if (_gui == null || !_gui || canvasRect == null)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The game's server browser was not found; its own screen stays.");
                _gui = null;
                return;
            }
            _lobbyDialog = _canvasRoot.GetComponentInChildren<CoopLobbyDialogGui>(true);
            var layout = _gui.transform.Find("Layout");
            if (layout != null) _gameList = Group(layout);
            var strip = _gui.transform.Find("GameServerBottomStrip");
            var options = strip != null ? strip.Find("OptionsPanel") : null;
            if (options != null)
            {
                _stripButtons = Group(options);
                _back = options.GetComponentsInChildren<Button>(true).FirstOrDefault(button => button != null && button.name == "BackButton");
            }
            foreach (var back in _canvasRoot.GetComponentsInChildren<GameBackButton>(true))
                if (back != null) Backs.Add(back);

            _tab = Tab.All;
            _zone = Zone.List;
            _sort = SortKey.None;
            _search = "";
            _searching = false;
            Array.Clear(Chips, 0, Chips.Length);
            _pad = PadActive();
            Build(canvasRect);
            HideGame();
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
                Show(_gameList);
                Show(_stripButtons);
                foreach (var hidden in StripHidden)
                    if (hidden != null && hidden) hidden.SetActive(true);
                PauseBacks(false);
                if (_rootObject != null && _rootObject) Object.Destroy(_rootObject);
                if (_joiner != null && _joiner) Object.Destroy(_joiner.gameObject);
            }
            catch (Exception)
            {
                // The scene may already be unloading with these objects.
            }
            StripHidden.Clear();
            Backs.Clear();
            Hots.Clear();
            Listed.Clear();
            Entries.Clear();
            _hover = _searchHot = null;
            _rootObject = _canvasRoot = null;
            _joiner = null;
            _gui = null;
            _lobbyDialog = null;
            _gameList = _stripButtons = null;
            _back = null;
        }

        /// <summary>Puts the game's browser back the way its own controls show it, before this screen leaves it.</summary>
        internal static void RestoreGameBrowser()
        {
            try
            {
                if (_gui != null && _gui && _gui.GetBrowserMode() != ServerBrowserRefreshType.PeerToPeer)
                    _gui.ChangeBrowserMode((int)ServerBrowserRefreshType.PeerToPeer);
            }
            catch (Exception)
            {
                // The game's list then shows whichever mode was last used.
            }
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
            var now = Time.unscaledTime;
            if (away || dialog) _resumeInputAt = now + InputPauseSeconds;
            _acting = now >= _resumeInputAt;
            if (away)
            {
                ReadDevice("gamepad", ReadPad);
                return;
            }
            HideGame();
            if (!dialog) ClearSelection();
            if (_backsPaused && !_searching && now >= _backResumeAt) PauseBacks(false);

            var pad = PadActive();
            if (pad != _pad)
            {
                _pad = pad;
                if (pad) FocusSearch(false);
                PaintHints();
            }
            ReadDevice("gamepad", ReadPad);
            ReadDevice("keyboard", ReadKeys);
            ReadDevice("mouse", ReadMouse);

            if (_dirty || now >= _nextRefresh)
            {
                _dirty = false;
                _filterDirty = false;
                // A long server list takes a moment to read, so it is read less often the longer it takes.
                var watch = Stopwatch.StartNew();
                ReadServers();
                _nextRefresh = Time.unscaledTime + Mathf.Clamp((float)watch.Elapsed.TotalSeconds * 25f, 0.5f, 5f);
                Apply();
                Paint();
            }
            else if (_filterDirty)
            {
                _filterDirty = false;
                Apply();
                Paint();
            }
            PaintCaret();
        }

        private static CanvasGroup Group(Transform transform)
        {
            var group = transform.GetComponent<CanvasGroup>();
            return group != null && group ? group : transform.gameObject.AddComponent<CanvasGroup>();
        }

        // The game's list and its strip buttons stay alive but out of sight; this screen draws its own.
        private static void HideGame()
        {
            if (_gameList != null && _gameList && (_gameList.alpha != 0 || _gameList.blocksRaycasts))
            {
                _gameList.alpha = 0;
                _gameList.interactable = false;
                _gameList.blocksRaycasts = false;
            }
            if (_stripButtons == null || !_stripButtons || _stripButtons.alpha == 0 && !_stripButtons.blocksRaycasts) return;
            // Still interactable, so the game's own Back keeps answering Escape and the controller.
            _stripButtons.alpha = 0;
            _stripButtons.blocksRaycasts = false;
            // Its other buttons carry controller bindings of their own, which this screen replaces.
            for (var i = 0; i < _stripButtons.transform.childCount; i++)
            {
                var child = _stripButtons.transform.GetChild(i).gameObject;
                if (child.name is not ("DirectJoinButton" or "RefreshButton") || !child.activeSelf) continue;
                child.SetActive(false);
                StripHidden.Add(child);
            }
        }

        private static void Show(CanvasGroup group)
        {
            if (group == null || !group) return;
            group.alpha = 1;
            group.interactable = true;
            group.blocksRaycasts = true;
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

        // The game's own flag for "a controller is the device in use", which its menus switch their prompts on.
        private static bool PadActive()
        {
            try
            {
                return global::Sons.Input.InputSystem.IsGamePad;
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

        // Buttons are tracked even while input is paused, so one held through a dialog does not fire afterwards.
        private static void ReadPad()
        {
            var pad = Gamepad.current;
            if (pad == null) return;
            bool previous = Pressed(0, pad.leftShoulder.isPressed), next = Pressed(1, pad.rightShoulder.isPressed);
            bool host = Pressed(2, pad.rightTrigger.isPressed), act = Pressed(3, pad.buttonSouth.isPressed);
            bool refresh = Pressed(4, pad.buttonWest.isPressed), direct = Pressed(5, pad.buttonNorth.isPressed);
            bool left = Pressed(6, pad.dpad.left.isPressed || pad.leftStick.left.isPressed);
            bool right = Pressed(7, pad.dpad.right.isPressed || pad.leftStick.right.isPressed);
            if (!_acting) return;
            Steer("pad", pad.dpad.up.isPressed || pad.leftStick.up.isPressed, pad.dpad.down.isPressed || pad.leftStick.down.isPressed);
            if (left) Sideways(-1);
            if (right) Sideways(1);
            if (previous) CycleTab(-1);
            if (next) CycleTab(1);
            if (host) Host();
            if (act) Activate();
            if (refresh) Refresh();
            if (direct) JoinByIp();
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
            if (_searching)
            {
                TypeSearch();
                return;
            }
            Steer("keys", UnityEngine.Input.GetKey(KeyCode.UpArrow), UnityEngine.Input.GetKey(KeyCode.DownArrow));
            if (UnityEngine.Input.GetKeyDown(KeyCode.LeftArrow)) Sideways(-1);
            if (UnityEngine.Input.GetKeyDown(KeyCode.RightArrow)) Sideways(1);
            if (UnityEngine.Input.GetKeyDown(KeyCode.Q)) CycleTab(-1);
            if (UnityEngine.Input.GetKeyDown(KeyCode.E)) CycleTab(1);
            if (UnityEngine.Input.GetKeyDown(KeyCode.Tab)) Host();
            if (UnityEngine.Input.GetKeyDown(KeyCode.F2)) JoinByIp();
            if (UnityEngine.Input.GetKeyDown(KeyCode.F3)) FocusSearch(true);
            if (UnityEngine.Input.GetKeyDown(KeyCode.F5)) Refresh();
            if (UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter)) Activate();
        }

        private static void TypeSearch()
        {
            if (UnityEngine.Input.GetKeyDown(KeyCode.Escape))
            {
                // Escape clears the search first; the game's own Back answers it only once the box is left.
                if (_search.Length > 0) _search = "";
                FocusSearch(false);
                _filterDirty = true;
                return;
            }
            var typed = UnityEngine.Input.inputString;
            if (string.IsNullOrEmpty(typed)) return;
            foreach (var c in typed)
            {
                if (c == '\b')
                {
                    if (_search.Length > 0) _search = _search.Substring(0, _search.Length - 1);
                }
                else if (c is '\n' or '\r') FocusSearch(false);
                else if (!char.IsControl(c) && _search.Length < MaxSearchLength) _search += c;
            }
            _filterDirty = true;
        }

        private static void FocusSearch(bool on)
        {
            if (_searching == on) return;
            _searching = on;
            if (on)
            {
                _zone = Zone.List;
                PauseBacks(true);
            }
            else
            {
                // The Escape that leaves the box must not also reach the game's Back.
                _backResumeAt = Time.unscaledTime + InputPauseSeconds;
            }
            PaintSearch();
        }

        private static readonly List<GameBackButton> PausedBacks = new();

        // While the search box has the keyboard, Escape belongs to it; only handlers paused here are resumed.
        private static void PauseBacks(bool paused)
        {
            _backsPaused = paused;
            if (paused)
            {
                foreach (var back in Backs)
                {
                    if (back == null || !back || !back.enabled) continue;
                    back.enabled = false;
                    PausedBacks.Add(back);
                }
                return;
            }
            foreach (var back in PausedBacks)
                if (back != null && back) back.enabled = true;
            PausedBacks.Clear();
        }

        private static void ReadMouse()
        {
            if (!_acting) return;
            var position = UnityEngine.Input.mousePosition;
            var pointer = new Vector2(position.x, position.y);
            var camera = UiCamera();
            Hot over = null;
            foreach (var hot in Hots)
                if (hot.Rect && hot.Rect.gameObject.activeInHierarchy && RectTransformUtility.RectangleContainsScreenPoint(hot.Rect, pointer, camera))
                    over = hot;
            if (!ReferenceEquals(over, _hover))
            {
                _hover?.Hover?.Invoke(false);
                _hover = over;
                over?.Hover?.Invoke(true);
            }
            if (UnityEngine.Input.GetMouseButtonDown(0))
            {
                if (_searching && !ReferenceEquals(over, _searchHot)) FocusSearch(false);
                over?.Click?.Invoke();
            }
            var scroll = UnityEngine.Input.mouseScrollDelta.y;
            if (scroll == 0 || _listRect == null || _rows.Length == 0
                || !RectTransformUtility.RectangleContainsScreenPoint(_listRect, pointer, camera)) return;
            _top = Mathf.Clamp(_top - Math.Sign(scroll) * 3, 0, Math.Max(0, Entries.Count - _rows.Length));
            Paint();
        }

        private static Hot Add(RectTransform rect, Action click, Action<bool> hover = null)
        {
            var hot = new Hot { Rect = rect, Click = click, Hover = hover };
            Hots.Add(hot);
            return hot;
        }

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

        private static void JoinByIp()
        {
            try
            {
                _gui.DirectJoin();
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Join by IP could not open: " + error.Message);
            }
        }

        private static void Host() => MultiplayerTakeover.RequestHost(BackOut);

        // Leaves through the game's own Back, the handler its Escape and controller button use.
        private static bool BackOut()
        {
            try
            {
                PauseBacks(false);
                foreach (var back in Backs)
                {
                    if (back == null || !back || !back.isActiveAndEnabled) continue;
                    back.OnBack();
                    return true;
                }
                if (_back != null && _back)
                {
                    _back.onClick.Invoke();
                    return true;
                }
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The game's Back could not be used: " + error.Message);
                return false;
            }
            RuntimeEntryPoint.LogSource?.LogWarning("The game's Back was not found.");
            return false;
        }

        private static void Activate()
        {
            if (_zone == Zone.Headers) ToggleSort((SortKey)(_tool + 1));
            else if (_zone == Zone.Chips) ToggleChip(_tool);
            else Join();
        }

        // First press sorts the natural way for that column, the second reverses it, the third returns to the
        // game's own order.
        private static void ToggleSort(SortKey key)
        {
            var natural = key == SortKey.Players;
            if (_sort != key)
            {
                _sort = key;
                _descending = natural;
            }
            else if (_descending == natural) _descending = !natural;
            else _sort = SortKey.None;
            _filterDirty = true;
        }

        private static void ToggleChip(int index)
        {
            Chips[index] = !Chips[index];
            _filterDirty = true;
        }

        private static void Move(int step)
        {
            switch (_zone)
            {
                case Zone.List when step < 0 && (_selected <= 0 || Entries.Count == 0):
                    SetZone(Zone.Headers);
                    break;
                case Zone.List:
                    Select(Mathf.Clamp(_selected + step, 0, Math.Max(0, Entries.Count - 1)));
                    break;
                case Zone.Headers:
                    SetZone(step < 0 ? Zone.Chips : Zone.List);
                    break;
                case Zone.Chips when step > 0:
                    SetZone(Zone.Headers);
                    break;
            }
        }

        private static void Sideways(int step)
        {
            if (_zone == Zone.List) return;
            _tool = Mathf.Clamp(_tool + step, 0, 3);
            Paint();
        }

        private static void SetZone(Zone zone)
        {
            _zone = zone;
            _tool = Mathf.Clamp(_tool, 0, 3);
            Paint();
        }

        private static void Select(int index)
        {
            if (index < 0 || index >= Entries.Count) return;
            _zone = Zone.List;
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

        private static void OnAnswered(string key) => _filterDirty = true;

        private static void ReadServers()
        {
            Listed.Clear();
            _mine = ContentAgreement.Fingerprint;
            try
            {
                if (_tab is Tab.All or Tab.Hosted) Read(_gui._lobbyBrowser.Entries);
                var dedicated = _gui._dedicatedBrowser;
                var internet = dedicated._isLastAnInternetDedicatedRequest;
                if (_tab is Tab.Dedicated or Tab.All && internet || _tab == Tab.Lan && !internet)
                    Read(dedicated.Entries);
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The server list could not be read: " + error.Message);
            }
        }

        private static void Read(Il2CppReferenceArray<IServerInfo> infos)
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
                        Order = Listed.Count,
                        Dedicated = info.TryCast<DedicatedServerInfo>(),
                        Name = CleanName(string.IsNullOrWhiteSpace(clean) ? raw : clean),
                        Mode = Words(info.GameMode),
                        Players = info.CurrentMembers,
                        Limit = info.MemberLimit,
                        Ping = info.Ping,
                        Password = info.IsPasswordProtected,
                        VersionMatch = info.VersionMatch,
                        // Modstoke and other loaders mark a dedicated server's name; only those are asked for details.
                        Modded = raw.Contains("[MODDED]", StringComparison.Ordinal)
                    };
                    if (entry.Dedicated != null)
                    {
                        var address = entry.Dedicated.NetAddr;
                        entry.Key = ServerBrowserBadges.IpText(address.GetIP()) + ":" + address.GetQueryPort();
                        if (entry.Modded) ServerBrowserBadges.Ask(entry.Key, address.GetIP(), address.GetQueryPort());
                    }
                    Listed.Add(entry);
                }
                catch (Exception)
                {
                    // A server that changes while it is read shows up on the next pass.
                }
            }
        }

        // Search, filters and sorting over what was last read; cheap enough to run on every keystroke.
        private static void Apply()
        {
            Entries.Clear();
            var needle = _search.Trim();
            foreach (var entry in Listed)
            {
                if (needle.Length > 0 && entry.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (Chips[0] && StateOf(entry, out _) is not (ModState.Match or ModState.Needs or ModState.AddsItems
                    or ModState.Differs or ModState.Modded or ModState.Checking)) continue;
                if (Chips[1] && entry.Players <= 0) continue;
                if (Chips[2] && entry.Limit > 0 && entry.Players >= entry.Limit) continue;
                if (Chips[3] && entry.Password) continue;
                Entries.Add(entry);
            }
            if (_sort != SortKey.None) Entries.Sort(Compare);
            _modstokeCount = 0;
            foreach (var entry in Listed)
                if (entry.Key != null && ServerBrowserBadges.TryRead(entry.Key, out _, out var details) && details != null)
                    _modstokeCount++;

            var rows = Math.Max(1, _rows.Length);
            var keep = _selectedId == IntPtr.Zero ? -1 : Entries.FindIndex(entry => entry.Id == _selectedId);
            _selected = keep >= 0 ? keep : Mathf.Clamp(_selected, 0, Math.Max(0, Entries.Count - 1));
            if (Entries.Count > 0) _selectedId = Entries[_selected].Id;
            _top = Mathf.Clamp(_top, 0, Math.Max(0, Entries.Count - rows));
        }

        private static int Compare(Entry a, Entry b)
        {
            var order = _sort switch
            {
                SortKey.Name => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
                SortKey.Mods => StateOf(a, out _).CompareTo(StateOf(b, out _)),
                SortKey.Players => a.Players.CompareTo(b.Players),
                // An unknown ping sorts as the slowest.
                SortKey.Ping => (a.Ping > 0 ? a.Ping : int.MaxValue).CompareTo(b.Ping > 0 ? b.Ping : int.MaxValue),
                _ => 0
            };
            if (_descending) order = -order;
            // Ties keep the game's own order, which also keeps the sort stable between refreshes.
            return order != 0 ? order : a.Order.CompareTo(b.Order);
        }

        private static ModState StateOf(Entry entry, out ServerDetails details)
        {
            details = null;
            if (!entry.VersionMatch) return ModState.OtherVersion;
            if (entry.Dedicated == null) return ModState.Hosted;
            if (entry.Key == null || !ServerBrowserBadges.TryRead(entry.Key, out _, out details))
                return entry.Modded ? ModState.Checking : ModState.Vanilla;
            if (details == null) return entry.Modded ? ModState.Modded : ModState.Vanilla;
            if (details.Content.Length > 0 && details.Content != _mine) return ModState.Differs;
            if (ServerBrowserBadges.Missing(details).Any()) return ModState.Needs;
            return details.Content.Length == 0 && _mine.Length > 0 ? ModState.AddsItems : ModState.Match;
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
