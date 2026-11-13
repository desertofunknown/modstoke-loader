// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Sons.Gui.Multiplayer;
using Sons.Multiplayer;
using Sons.Settings;
using UnityEngine;
using UnityEngine.SceneManagement;
using GameAudioSettings = Sons.Settings.AudioSettings;
using GameBackButton = Sons.Gui.Options.BackButton;

namespace Modstoke.Loader
{
    /// <summary>One player in the lobby.</summary>
    internal sealed record LobbyMember(ulong Id, string Name, Texture2D Avatar, bool Host, bool Self);

    /// <summary>
    /// The game's multiplayer lobby, as a source for Modstoke's lobby screen. The game's lobby dialog keeps
    /// running, since it creates the Steam lobby and the server and handles kicks and disconnects; it is only
    /// made invisible, with its Back keys paused, and its own start, invite and leave handlers are called.
    /// </summary>
    internal static class LobbyHost
    {
        // The host's lobby has its own scene; a player who joins sees the same dialog in the server browser's.
        private static readonly string[] Scenes = { "MultiplayerHostMenu", "MultiplayerMenu" };
        private static readonly string[] Covered = { "Dialog", "CoopLobbyBottomStripHost", "CoopLobbyBottomStripClient" };
        private static readonly List<CanvasGroup> Hidden = new();
        private static readonly List<GameBackButton> PausedBacks = new();
        private static CoopLobbyDialogGui _dialog;
        private static float _nextFind;
        private static int _hidden;

        /// <summary>Whether the game's lobby is showing, for the host or for a player who joined one.</summary>
        internal static bool Open
        {
            get
            {
                Find();
                return _dialog != null && _dialog && _dialog.gameObject.activeInHierarchy;
            }
        }

        internal static bool IsHost => Live && _dialog._hostMode;

        /// <summary>For the host: the lobby and server exist and the game can start.</summary>
        internal static bool Ready => Live && _dialog._startButton != null && _dialog._startButton && _dialog._startButton.interactable;

        internal static bool Starting => Live && Active(_dialog._hostBottomStatusStarting);

        /// <summary>For a player who joined: the host started and the game is loading for them.</summary>
        internal static bool Joining => Live && Active(_dialog._clientBottomStatusJoining);

        internal static string Name => Safe(() => CoopLobbyManager.ActiveName(), "");

        internal static int Limit => Safe(() => CoopLobbyManager.ActiveMemberLimit(), 0);

        private static bool Live => _dialog != null && _dialog;

        private static bool Active(GameObject target) => target != null && target && target.activeInHierarchy;

        internal static List<LobbyMember> Members()
        {
            var members = new List<LobbyMember>();
            try
            {
                var details = CoopLobbyManager.GetActiveMembersDetails();
                if (details == null) return members;
                var enumerator = details.GetEnumerator();
                var walker = enumerator.TryCast<Il2CppSystem.Collections.IEnumerator>();
                while (walker != null && walker.MoveNext())
                {
                    var member = enumerator.Current;
                    if (member == null) continue;
                    var avatar = member.Texture;
                    members.Add(new LobbyMember(member.Id.m_SteamID, member.Name ?? "", avatar != null && avatar ? avatar : null,
                        member.IsHost, member.IsSelf));
                }
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogDebug("Lobby members could not be read: " + error.Message);
            }
            members.Sort((a, b) => a.Host != b.Host ? (a.Host ? -1 : 1) : a.Self != b.Self ? (a.Self ? -1 : 1) : 0);
            return members;
        }

        /// <summary>What the lobby is for, as its Steam lobby data says: the mode, a saved world or a new one, a password.</summary>
        internal static (string Mode, bool Saved, bool Password) Game
            => Safe(() =>
            {
                var lobby = CoopLobbyManager.GetActiveInstance();
                return lobby == null ? ("", false, false) : (lobby.GameMode ?? "", lobby.HasSave, lobby.IsPasswordProtected);
            }, ("", false, false));

        internal static bool Private => Safe(MultiplayerUtilities.GetServerIsPrivate, false);

        /// <summary>The player's Steam profile, in Steam's overlay.</summary>
        internal static void ShowProfile(ulong id)
            => Steamworks.SteamFriends.ActivateGameOverlayToUser("steamid", new Steamworks.CSteamID(id));

        internal static void Start() => _dialog?.OnStartClicked();

        internal static void Invite() => _dialog?.OnInviteClicked();

        internal static void Leave() => _dialog?.OnLeaveClicked();

        /// <summary>Asks a player to leave, as the game's own kick button in the lobby does.</summary>
        internal static void Remove(ulong id) => CoopLobbyManager.RequestPlayerLeave(new Steamworks.CSteamID(id));

        /// <summary>Whether the microphone is always on rather than push to talk.</summary>
        internal static bool MicAlwaysOn
            => Safe(() => string.Equals(GameSettingsManager.GetSetting(GameAudioSettings.ProximityVoiceChatSettingKey,
                GameAudioSettings.ProximityVoiceChatSettingDefault), GameAudioSettings.ProximityVoiceChatAlwaysOnOption,
                StringComparison.OrdinalIgnoreCase), true);

        /// <summary>Switches the microphone mode through the lobby's own panel, which keeps its voice meter in step.</summary>
        internal static void ToggleMic()
        {
            var panel = Live ? _dialog._prepareMicrophonePanel : null;
            if (panel != null && panel) panel.ToggleMicrophoneMode();
        }

        /// <summary>Makes the game's lobby invisible and pauses its Back keys, while Modstoke's lobby shows over it.</summary>
        internal static void HideNative()
        {
            if (!Live || _hidden == _dialog.GetInstanceID()) return;
            _hidden = _dialog.GetInstanceID();
            foreach (var name in Covered)
            {
                var part = _dialog.transform.Find(name);
                if (part == null) continue;
                var group = part.GetComponent<CanvasGroup>();
                if (group == null || !group) group = part.gameObject.AddComponent<CanvasGroup>();
                group.alpha = 0f;
                group.interactable = false;
                group.blocksRaycasts = false;
                Hidden.Add(group);
            }
            foreach (var back in _dialog.GetComponentsInChildren<GameBackButton>(true))
            {
                if (back == null || !back.enabled) continue;
                back.enabled = false;
                PausedBacks.Add(back);
            }
        }

        /// <summary>Gives the game's lobby back, as when Modstoke's menus stop.</summary>
        internal static void Release()
        {
            _hidden = 0;
            foreach (var group in Hidden)
            {
                if (group == null || !group) continue;
                group.alpha = 1f;
                group.interactable = true;
                group.blocksRaycasts = true;
            }
            Hidden.Clear();
            foreach (var back in PausedBacks)
                if (back != null && back) back.enabled = true;
            PausedBacks.Clear();
        }

        /// <summary>
        /// A lobby scene just loaded: its dialog is found and made invisible at once, before the scene draws, whether
        /// or not the dialog is showing yet, so the game's lobby never appears.
        /// </summary>
        internal static void SceneLoaded(Scene scene)
        {
            if (Array.IndexOf(Scenes, scene.name) < 0) return;
            foreach (var root in scene.GetRootGameObjects())
            {
                var found = root.GetComponentInChildren<CoopLobbyDialogGui>(true);
                if (found == null) continue;
                _dialog = found;
                _hidden = 0;
                Hidden.Clear();
                PausedBacks.Clear();
                HideNative();
                return;
            }
        }

        private static void Find()
        {
            if (Live && _dialog.gameObject.scene.isLoaded) return;
            if (_dialog != null)
            {
                _dialog = null;
                _hidden = 0;
                Hidden.Clear();
                PausedBacks.Clear();
            }
            if (Time.unscaledTime < _nextFind) return;
            _nextFind = Time.unscaledTime + 1f;
            foreach (var name in Scenes)
            {
                var scene = SceneManager.GetSceneByName(name);
                if (!scene.IsValid() || !scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    var found = root.GetComponentInChildren<CoopLobbyDialogGui>(true);
                    if (found == null) continue;
                    _dialog = found;
                    return;
                }
            }
        }

        private static T Safe<T>(Func<T> read, T fallback)
        {
            try { return read(); }
            catch (Exception) { return fallback; }
        }
    }
}
