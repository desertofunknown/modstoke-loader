// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Localization;
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
    /// The Modstoke Join screen, drawn on the front end's own layer while the game's browser scene is loaded.
    /// The game's lobby and dedicated server browsers keep running underneath as the source of servers and its
    /// own server row does the joining, so passwords, version checks and lobby joins behave as in the game;
    /// this screen lists, searches, sorts, explains and asks.
    /// </summary>
    internal static partial class MultiplayerScreen
    {
        private enum Tab { All, Hosted, Dedicated, Lan }

        private enum Zone { List, Headers, Chips }

        private enum SortKey { None, Name, Mods, Players, Ping }

        // Ordered best first, which is also the order "sort by mods" lists them in.
        private enum ModState { Match, Needs, AddsItems, Differs, Modded, Checking, Vanilla, Hosted, OtherVersion }

        private static readonly string[] TabKeys = { "all", "hosted", "dedicated", "lan" };
        private static readonly string[] ChipKeys = { "modstoke", "hasPlayers", "notFull", "noPassword" };
        private const float DoubleClickSeconds = 0.4f;
        private const float InputPauseSeconds = 0.3f;
        private const int MaxSearchLength = 40;
        // New servers are read for this long per frame, so a list of thousands fills in without a hitch.
        private const double ReadBudgetMs = 3;
        private static readonly Regex Tags = new("<[^>]*>", RegexOptions.Compiled);

        private sealed class Entry
        {
            internal IServerInfo Info;
            internal DedicatedServerInfo Dedicated;
            internal IntPtr Id;
            internal string Key, Name, Mode;
            internal int Order, Players, Limit, Ping, Scan;
            internal float RecheckAt;
            internal bool Valid, Password, VersionMatch, Modded;
        }

        // A clickable area of the screen. Clicks and hover are tested against these directly, so nothing the game
        // draws underneath can swallow or receive them.
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
        private static ServerInfoGuiItem _joiner;
        private static Tab _tab;
        private static Zone _zone;
        private static SortKey _sort;
        private static bool _descending;
        private static readonly bool[] Chips = new bool[4];
        // Every server read so far by the address of the game's own record of it, then those in the game's
        // order, then what the search and filters leave.
        private static readonly Dictionary<IntPtr, Entry> Known = new();
        private static readonly List<Entry> Listed = new();
        private static readonly List<Entry> Entries = new();
        private static readonly List<Hot> Hots = new();
        private static readonly HashSet<string> FailedInputs = new();
        private static readonly Dictionary<string, string> ModeNames = new(StringComparer.OrdinalIgnoreCase);
        private static readonly bool[] PadDown = new bool[8];
        private static Hot _hover, _searchHot;
        private static string _search = "", _mine = "", _steerDevice;
        private static int _selected, _top, _tool, _hoverRow = -1, _repeatDir, _lastClickRow = -1, _scan, _layout;
        private static long _listedHash;
        private static IntPtr _selectedId;
        private static float _nextScan, _nextPaint, _repeatAt, _lastClick, _resumeInputAt, _backResumeAt;
        private static bool _dirty, _filterDirty, _backlog, _rebuild, _acting, _searching, _backsPaused, _pad;

        private static Strings T => FrontEnd.Text;

        internal static void Open(Scene scene)
        {
            Close();
            _canvasRoot = scene.GetRootGameObjects().FirstOrDefault(root => root != null && root.name == "MultiplayerMenuCanvas");
            _gui = _canvasRoot != null ? _canvasRoot.GetComponentInChildren<GameServerBrowserGui>(true) : null;
            if (_gui == null || !_gui)
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
            _pad = FrontInput.PadActive;
            Build(FrontEnd.Canvas());
            HideGame();
            FailedInputs.Clear();
            _resumeInputAt = Time.unscaledTime + InputPauseSeconds;
            ServerBrowserBadges.Answered += OnAnswered;
            Languages.Changed += OnLanguageChanged;
            SetTab(Tab.All);
            RuntimeEntryPoint.LogSource?.LogInfo("Showing the Modstoke multiplayer screen.");
        }

        internal static void Close()
        {
            ServerBrowserBadges.Answered -= OnAnswered;
            Languages.Changed -= OnLanguageChanged;
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
            Known.Clear();
            Listed.Clear();
            Entries.Clear();
            ModeNames.Clear();
            _hover = _searchHot = null;
            _rootObject = _canvasRoot = null;
            _joiner = null;
            _gui = null;
            _lobbyDialog = null;
            _gameList = _stripButtons = null;
            _back = null;
            _rebuild = false;
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
            if (_rebuild || _layout != FrontEnd.LayoutVersion) Rebuild();
            // The game's lobby and its dialogs are drawn under this layer, which steps aside for them.
            var away = _lobbyDialog != null && _lobbyDialog && _lobbyDialog.gameObject.activeInHierarchy
                || !_gui.gameObject.activeInHierarchy || GameDialogOpen();
            if (_rootObject.activeSelf == away) _rootObject.SetActive(!away);
            // The press that closes a lobby or a dialog must not also act on this screen.
            var now = Time.unscaledTime;
            // The console and the mods menu draw over this screen and take the keyboard while open.
            if (away || ConsoleOverlay.IsVisible || MenuController.IsVisible) _resumeInputAt = now + InputPauseSeconds;
            _acting = now >= _resumeInputAt;
            if (away)
            {
                ReadDevice("gamepad", ReadPad);
                return;
            }
            HideGame();
            ClearSelection();
            if (_backsPaused && !_searching && now >= _backResumeAt) PauseBacks(false);

            var pad = FrontInput.PadActive;
            if (pad != _pad)
            {
                _pad = pad;
                if (pad) FocusSearch(false);
                PaintHints();
            }
            ReadDevice("gamepad", ReadPad);
            ReadDevice("keyboard", ReadKeys);
            ReadDevice("mouse", ReadMouse);

            if (_dirty || _backlog || now >= _nextScan)
            {
                var refreshing = Refreshing();
                _nextScan = now + (refreshing ? 0.25f : 1f);
                if (ReadServers() || _dirty) _filterDirty = true;
                _dirty = false;
            }
            // While servers are still pouring in, the list is redrawn a few times a second rather than every frame.
            if (_filterDirty && (!_backlog || now >= _nextPaint))
            {
                _filterDirty = false;
                _nextPaint = now + 0.2f;
                Apply();
                Paint();
            }
            PaintCaret();
            PaintProgress();
        }

        private static void Rebuild()
        {
            _rebuild = false;
            if (_rootObject != null && _rootObject) Object.Destroy(_rootObject);
            _hover = null;
            _hoverRow = -1;
            Build(FrontEnd.Canvas());
            Apply();
            Paint();
        }

        private static void OnLanguageChanged()
        {
            ModeNames.Clear();
            _rebuild = true;
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

        private static bool Refreshing()
        {
            try
            {
                return _gui._dedicatedBrowser.RefreshInProgress || _gui._lobbyBrowser.RefreshInProgress;
            }
            catch (Exception)
            {
                // The browsers report nothing between refreshes.
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
            bool host = Pressed(2, pad.rightTrigger.isPressed || pad.leftTrigger.isPressed), act = Pressed(3, pad.buttonSouth.isPressed);
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
            Hot over = null;
            foreach (var hot in Hots)
                if (hot.Rect && hot.Rect.gameObject.activeInHierarchy && RectTransformUtility.RectangleContainsScreenPoint(hot.Rect, pointer, null))
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
                || !RectTransformUtility.RectangleContainsScreenPoint(_listRect, pointer, null)) return;
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
                RuntimeEntryPoint.LogSource?.LogWarning("The server list could not switch to " + tab + ": " + error.Message);
            }
            _dirty = true;
        }

        private static void CycleTab(int step) => SetTab((Tab)(((int)_tab + step + TabKeys.Length) % TabKeys.Length));

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

        // Hosting is set up on the title scene, so this leaves for it and the Host screen opens there.
        private static void Host()
        {
            FrontEnd.HostWhenTitleLoads();
            if (!BackOut()) FrontEnd.CancelHost();
        }

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
                    if (ElementAddress(all, i) == entry.Id) index = i;
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

        /// <summary>
        /// Brings the list up to date with the game's browsers; true when what is listed changed. Servers
        /// already read are recognized by address without touching them, and new ones are read for a few
        /// milliseconds per frame, the rest on the following frames.
        /// </summary>
        private static bool ReadServers()
        {
            _mine = ContentAgreement.Fingerprint;
            _scan++;
            _backlog = false;
            Listed.Clear();
            var watch = Stopwatch.StartNew();
            var changed = false;
            long hash = 17;
            var seen = 0;
            try
            {
                if (_tab is Tab.All or Tab.Hosted) Read(_gui._lobbyBrowser.Entries, watch, ref changed, ref hash, ref seen);
                var dedicated = _gui._dedicatedBrowser;
                var internet = dedicated._isLastAnInternetDedicatedRequest;
                if (_tab is Tab.Dedicated or Tab.All && internet || _tab == Tab.Lan && !internet)
                    Read(dedicated.Entries, watch, ref changed, ref hash, ref seen);
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The server list could not be read: " + error.Message);
            }
            // Servers the game no longer lists are dropped once a pass has seen everything it does list.
            if (!_backlog && seen != Known.Count)
            {
                foreach (var gone in Known.Where(pair => pair.Value.Scan != _scan).Select(pair => pair.Key).ToArray())
                    Known.Remove(gone);
            }
            changed |= RefreshShown() | hash != _listedHash;
            _listedHash = hash;
            return changed;
        }

        private static void Read(Il2CppReferenceArray<IServerInfo> infos, Stopwatch watch, ref bool changed, ref long hash, ref int seen)
        {
            if (infos == null) return;
            var now = Time.unscaledTime;
            var count = infos.Length;
            for (var i = 0; i < count; i++)
            {
                var id = ElementAddress(infos, i);
                if (id == IntPtr.Zero) continue;
                if (!Known.TryGetValue(id, out var entry))
                {
                    if (watch.Elapsed.TotalMilliseconds > ReadBudgetMs)
                    {
                        _backlog = true;
                        continue;
                    }
                    entry = new Entry { Info = infos[i], Id = id };
                    Known[id] = entry;
                }
                entry.Scan = _scan;
                seen++;
                // A server that has not answered Steam yet is looked at again about once a second.
                if (!entry.Valid && now >= entry.RecheckAt && watch.Elapsed.TotalMilliseconds <= ReadBudgetMs)
                {
                    entry.RecheckAt = now + 1f;
                    if (Fill(entry)) changed = true;
                }
                if (!entry.Valid) continue;
                entry.Order = Listed.Count;
                Listed.Add(entry);
                hash = unchecked(hash * 31 + id.ToInt64());
            }
        }

        // The array's elements follow its four-word header; reading the addresses directly means a pass over
        // thousands of servers creates no objects.
        private static IntPtr ElementAddress(Il2CppReferenceArray<IServerInfo> array, int index)
            => Marshal.ReadIntPtr(array.Pointer, (4 + index) * IntPtr.Size);

        private static bool Fill(Entry entry)
        {
            try
            {
                var info = entry.Info;
                if (info == null || !info.IsValid) return false;
                var raw = info.Name ?? "";
                var clean = info.CleanName;
                entry.Dedicated = info.TryCast<DedicatedServerInfo>();
                entry.Name = CleanName(string.IsNullOrWhiteSpace(clean) ? raw : clean);
                entry.Mode = info.GameMode ?? "";
                entry.Players = info.CurrentMembers;
                entry.Limit = info.MemberLimit;
                entry.Ping = info.Ping;
                entry.Password = info.IsPasswordProtected;
                entry.VersionMatch = info.VersionMatch;
                // Modstoke and other loaders mark a dedicated server's name; only those are asked for details.
                entry.Modded = raw.Contains("[MODDED]", StringComparison.Ordinal);
                if (entry.Dedicated != null)
                {
                    var address = entry.Dedicated.NetAddr;
                    entry.Key = ServerBrowserBadges.IpText(address.GetIP()) + ":" + address.GetQueryPort();
                    if (entry.Modded) ServerBrowserBadges.Ask(entry.Key, address.GetIP(), address.GetQueryPort());
                }
                entry.Valid = true;
                return true;
            }
            catch (Exception)
            {
                // A server that changes while it is read is read again on a later pass.
                return false;
            }
        }

        // Player counts and pings move while the list is open; the rows on screen are kept current.
        private static bool RefreshShown()
        {
            var changed = false;
            for (var i = 0; i <= _rows.Length; i++)
            {
                var index = i < _rows.Length ? _top + i : _selected;
                if (index < 0 || index >= Entries.Count) continue;
                var entry = Entries[index];
                if (entry.Scan != _scan) continue;
                try
                {
                    int players = entry.Info.CurrentMembers, limit = entry.Info.MemberLimit, ping = entry.Info.Ping;
                    if (players != entry.Players || limit != entry.Limit || ping != entry.Ping) changed = true;
                    entry.Players = players;
                    entry.Limit = limit;
                    entry.Ping = ping;
                    if (entry.Modded) Ask(entry);
                }
                catch (Exception)
                {
                    // The row keeps what it showed.
                }
            }
            return changed;
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
            return text;
        }

        // The game's mode names in the player's language; a mode Modstoke has no text for is shown as words.
        private static string ModeName(string mode)
        {
            if (string.IsNullOrWhiteSpace(mode)) return "";
            if (ModeNames.TryGetValue(mode, out var known)) return known;
            var compact = mode.Replace("_", "").Replace(" ", "");
            var key = compact.Length == 0 ? "" : "mode." + char.ToLowerInvariant(compact[0]) + compact.Substring(1);
            return ModeNames[mode] = key.Length > 0 && T.Has(key) ? T[key] : Words(mode);
        }

        // "HardSurvival" and "hard_survival" both read "Hard survival".
        private static string Words(string text)
        {
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
