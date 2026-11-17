// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

extern alias SonsGame;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Loader;
using global::Sons.Multiplayer;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>
    /// Who is talking on the game's voice chat as this PC hears it, and the local microphone. Read on the game thread.
    /// It is presentation only: a client decides what it sends, so none of this is evidence about a player.
    /// </summary>
    public static class PlayerVoice
    {
        // Steam hands voice over in bursts a few times a second while someone speaks; this bridges the gaps.
        private const float HoldSeconds = 0.35f, CheckSeconds = 1f, RangeSeconds = 0.2f, LocalSeconds = 2f;
        // How long an always-on microphone may stay closed in the world before it counts as down.
        private const float DownSeconds = 2f, ExpectSeconds = 0.5f;
        // Voice timing runs on a managed clock, since every read of Unity's time is a call into the game.
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly List<Talker> Heard = new();
        private static int _attempted;
        private static bool _world, _server, _open, _alwaysOn, _logged, _toldOpen, _toldClosed, _toldDown;
        private static ulong _localId;
        private static float _sentAt = float.NegativeInfinity, _quietSince, _nextLocal, _nextExpect;
        private static VoiceChatBoltListener _listener;
        private static VoiceChatController _controller;
        private static Transform _localTransform;

        /// <summary>Whether this player's voice is reaching this PC now. A player muted here reads false.</summary>
        /// <remarks>
        /// On a dedicated server it is whether the server is relaying their voice, by the game's own talking monitor,
        /// which lets go about a second after they stop. For the local player it is <see cref="Transmitting"/>.
        /// </remarks>
        public static bool IsTalking(BoltEntity player)
        {
            if (player == null || !player || !player.isAttached) return false;
            return IsTalking(player.networkId.PackedValue);
        }

        /// <summary>The same, by the player entity's packed network ID.</summary>
        public static bool IsTalking(ulong networkId)
        {
            if (networkId == 0) return false;
            if (GameBuild.IsDedicatedServer) return Relaying(networkId);
            if (networkId == LocalId()) return Transmitting;
            var talker = Find(networkId);
            return talker != null && talker.Speaking;
        }

        /// <summary>
        /// Whether the game is recording the local microphone in a world: push to talk held, the always-on microphone,
        /// or a radio in hand. Nobody needs to be near for it to record; only sending waits for a listener.
        /// </summary>
        public static bool MicOpen => _open;

        /// <summary>
        /// The always-on microphone is chosen in a multiplayer world, but the game has not been recording it for a couple
        /// of seconds in the world, so nothing the player says goes anywhere.
        /// </summary>
        internal static bool MicDown { get; private set; }

        /// <summary>Whether the local player's voice is going out to another player right now.</summary>
        /// <remarks>
        /// The game sends nothing while nobody is in voice range, so an open microphone with nobody near reads false.
        /// </remarks>
        public static bool Transmitting => _open && Now - _sentAt < HoldSeconds;

        internal static float Now => (float)Clock.Elapsed.TotalSeconds;

        /// <summary>Whether the microphone is push to talk rather than always on.</summary>
        internal static bool PushToTalk { get; private set; }

        /// <summary>Whether the open microphone goes out over a radio rather than into the air around the player.</summary>
        internal static bool Radio { get; private set; }

        /// <summary>Everyone heard this session, talking or not; a List so it can be walked without allocating.</summary>
        internal static List<Talker> Talkers => Heard;

        /// <summary>Goes up whenever a player is muted or unmuted here.</summary>
        internal static int MuteVersion { get; private set; }

        /// <summary>A player was muted or unmuted on this PC; who is muted is read again at once.</summary>
        internal static void MuteChanged()
        {
            MuteVersion++;
            foreach (var talker in Heard) talker.NextCheck = 0;
        }

        /// <summary>Whether the player known by Steam ID, or without one by entity network ID, is speaking.</summary>
        internal static bool Speaking(ulong steamId, ulong networkId)
        {
            foreach (var talker in Heard)
                if (talker.Speaking && (steamId != 0 ? talker.SteamId == steamId : talker.Id == networkId)) return true;
            return false;
        }

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (Interlocked.Exchange(ref _attempted, 1) != 0 || GameBuild.IsDedicatedServer) return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                warning?.Invoke("Voice presence is unavailable: Harmony support is not ready.");
                return;
            }
            try
            {
                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-player-voice");
                Patch(harmony, typeof(CoopSteamVoicePlayer), nameof(CoopSteamVoicePlayer.DataReceived),
                    new[] { typeof(Il2CppStructArray<byte>), typeof(int), typeof(ulong), typeof(bool), typeof(Transform),
                        typeof(IPlayerState), typeof(bool), typeof(float).MakeByRefType() }, nameof(AfterPlayback));
                Patch(harmony, typeof(VoiceChatBoltListener), nameof(VoiceChatBoltListener.SendVoiceData),
                    new[] { typeof(Il2CppStructArray<byte>), typeof(int), typeof(BoltConnection), typeof(ulong), typeof(bool) }, nameof(AfterSend));
            }
            catch (Exception error)
            {
                warning?.Invoke("Voice presence could not follow the game's voice chat: " + error.Message);
            }
        }

        private static void Patch(HarmonyLib.Harmony harmony, Type type, string name, Type[] arguments, string postfix)
        {
            var target = AccessTools.Method(type, name, arguments) ?? throw new MissingMethodException(type.FullName, name);
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(PlayerVoice), postfix));
        }

        // Each voice block this PC plays, once the server voice policy has let it through.
        private static void AfterPlayback(ulong __2, bool __3, Transform __4, bool __runOriginal)
        {
            if (!__runOriginal || __2 == 0) return;
            var talker = Find(__2);
            if (talker == null) Heard.Add(talker = new Talker(__2));
            talker.HeardAt = Now;
            talker.Positional = __3;
            talker.Source = __4;
        }

        // A client only ever sends its own voice, to the server; a listen host sends its own and relays everyone else's.
        private static void AfterSend(ulong __3, bool __runOriginal)
        {
            if (__runOriginal && (!_server || __3 == _localId)) _sentAt = Now;
        }

        internal static void Tick()
        {
            if (GameBuild.IsDedicatedServer) return;
            if (!GameRuntime.IsGameScene)
            {
                if (_world) Leave();
                return;
            }
            var now = Now;
            if (!_world)
            {
                _world = true;
                _quietSince = now;
            }
            TickMicrophone(now);
            if (Heard.Count == 0) return;
            foreach (var talker in Heard)
            {
                var recent = now - talker.HeardAt < HoldSeconds;
                if (recent && now >= talker.NextCheck) Check(talker, now);
                if (recent && talker.Positional && _server && now >= talker.NextRange) Range(talker, now);
                talker.Talking = recent && (!talker.Positional || !_server || talker.InRange);
            }
        }

        // The game's own recording flag: its voice controller starts Steam's recorder every frame while always-on is
        // chosen or push to talk is held, nobody near or not, once Bolt is connected, the player's inventory exists, the
        // view is past waking up and not hidden, and Steam is up. Sending is what waits for a listener in range.
        private static void TickMicrophone(float now)
        {
            var open = VoiceChatBoltListener._recording;
            var changed = open != _open;
            if (open)
            {
                if (changed || now >= _nextLocal) ReadLocal(now, changed);
                // Plain field reads each frame; a destroyed object still reads its last values until it is replaced.
                PushToTalk = _controller is not null && _controller._pushToTalkActive;
                _alwaysOn = !PushToTalk;
                Radio = _listener is not null && !_listener._positionalVoice;
            }
            if (changed) Tell(open);
            _open = open;
            if (open)
            {
                MicDown = false;
                _quietSince = now;
                return;
            }
            if (now >= _nextExpect) Expect(now);
            // Counted only in the world: the game closes the recorder through loading and while the view is hidden.
            if (!_alwaysOn || !GameRuntime.IsInWorld) _quietSince = now;
            MicDown = now - _quietSince > DownSeconds;
            if (MicDown && !_toldDown) Explain();
        }

        // Whether the player has chosen the always-on microphone in a multiplayer world, read while the microphone is
        // closed. The game's voice controller holds the choice; without one, the setting itself.
        private static void Expect(float now)
        {
            try
            {
                if (_controller != null && _controller)
                {
                    _nextExpect = now + ExpectSeconds;
                    _alwaysOn = !_controller._pushToTalkActive;
                    return;
                }
                _nextExpect = now + 5f;
                _alwaysOn = false;
                if (!BoltNetwork.isRunning) return;
                _listener = VoiceChatBoltListener._instance;
                _controller = _listener != null && _listener ? _listener.GetComponent<VoiceChatController>() : null;
                _alwaysOn = _controller != null ? !_controller._pushToTalkActive : LobbyHost.MicAlwaysOn;
            }
            catch (Exception error)
            {
                Report("could not read the microphone setting", error);
            }
        }

        // The first time the microphone opens and closes in each world, so a test run shows it in the log.
        private static void Tell(bool open)
        {
            if (open ? _toldOpen : _toldClosed) return;
            if (open) _toldOpen = true;
            else _toldClosed = true;
            RuntimeEntryPoint.LogSource?.LogInfo(open
                ? "Voice: the game is recording the microphone (" + (Radio ? "radio" : PushToTalk ? "push to talk" : "always on") + ")."
                : "Voice: the game stopped recording the microphone.");
        }

        // Once a world, when always-on is chosen and the game isn't recording: the conditions its voice controller waits
        // for, so the log names the one missing. It reads the right hand's item data without a null check while looking
        // for the walkie-talkie, so an item there without data stops it before it starts the recorder.
        private static void Explain()
        {
            _toldDown = true;
            RuntimeEntryPoint.LogSource?.LogWarning("Voice: always-on is chosen but the game is not recording the microphone (Bolt connected "
                + Read(() => BoltNetwork.isConnected) + ", Steam " + Read(() => SteamManager.Initialized)
                + ", view " + Read(() => LocalPlayer.CurrentView) + ", inventory " + Read(() => LocalPlayer.Inventory != null)
                + ", right hand " + Read(RightHand) + ", voice listener " + Read(() => VoiceChatBoltListener._instance != null)
                + ", voice controller " + Read(() => _controller != null && _controller && _controller.isActiveAndEnabled) + ").");

            static string Read(Func<object> read)
            {
                try { return read()?.ToString() ?? "none"; }
                catch (Exception error) { return error.GetType().Name; }
            }

            static object RightHand()
            {
                var slots = LocalPlayer.Inventory._equipmentSlots;
                if (slots == null || slots.Length < 2) return "no slot";
                var item = slots[1];
                if (item == null) return "empty";
                return item.Data == null ? "item " + item._itemID + " without data" : "item " + item._itemID;
            }
        }

        // A world ended: who was heard, who this PC was and whether it hosted all belong to that session.
        private static void Leave()
        {
            _world = _open = _server = _alwaysOn = false;
            _toldOpen = _toldClosed = _toldDown = false;
            MicDown = false;
            _localId = 0;
            _sentAt = float.NegativeInfinity;
            Heard.Clear();
        }

        // What can change while someone talks: whether this PC mutes them, who they are, and whether this PC hosts.
        private static void Check(Talker talker, float now)
        {
            talker.NextCheck = now + CheckSeconds;
            try
            {
                _server = BoltNetwork.isServer;
                if (talker.SteamId == 0 && SonsGame::CoopServerInfo.TryGetSteamIdFromConnectionId((uint)talker.Id, out var steamId))
                    talker.SteamId = steamId;
                // The game's own test for its playback: a muted player plays silent, and so does one it can't name.
                talker.Muted = CoopSteamVoicePlayer.IsPlayerSourceMuted(talker.Id);
            }
            catch (Exception error)
            {
                Report("could not read who is talking", error);
            }
        }

        // A listen host plays every client wherever they are, however far off; a client is only sent the players in
        // voice range, so the host counts only those too.
        private static void Range(Talker talker, float now)
        {
            talker.NextRange = now + RangeSeconds;
            try
            {
                var source = talker.Source;
                var local = LocalTransform();
                var listener = Listener();
                if (source == null || !source || local == null || listener == null)
                {
                    talker.InRange = true;
                    return;
                }
                var range = listener._positionalVoiceAreaOfInterest;
                Vector3 from = source.position, to = local.position;
                float x = from.x - to.x, y = from.y - to.y, z = from.z - to.z;
                talker.InRange = x * x + y * y + z * z <= range * range;
            }
            catch (Exception error)
            {
                talker.InRange = true;
                Report("could not measure voice range", error);
            }
        }

        // Who this PC is in the session, read when the microphone opens and every few seconds while it stays open.
        private static void ReadLocal(float now, bool opened)
        {
            _nextLocal = now + LocalSeconds;
            // A host's relays of other players' voice before it knew it was hosting don't count as its own.
            if (opened) _sentAt = float.NegativeInfinity;
            try
            {
                _server = BoltNetwork.isServer;
                _localId = LocalId();
                _listener = VoiceChatBoltListener._instance;
                // The listener adds the controller to its own object as it wakes.
                if (opened && (_controller == null || !_controller) && _listener != null && _listener)
                    _controller = _listener.GetComponent<VoiceChatController>();
            }
            catch (Exception error)
            {
                Report("could not read the local player", error);
            }
        }

        // A dedicated server plays nothing; the game's own talking monitor follows the voice it relays.
        private static bool Relaying(ulong networkId)
        {
            try
            {
                var monitor = VoiceChatBoltListener.TalkingPlayers;
                return monitor != null && monitor.IsPlayerTalking(new Bolt.NetworkId(networkId), out _);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // The local player's entity as it is now, which a replaced character or a new session changes.
        private static ulong LocalId()
        {
            try
            {
                var entity = LocalPlayer.Entity;
                return entity != null && entity && entity.isAttached ? entity.networkId.PackedValue : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static Transform LocalTransform()
        {
            if (_localTransform == null || !_localTransform) _localTransform = LocalPlayer.Transform;
            return _localTransform != null && _localTransform ? _localTransform : null;
        }

        private static VoiceChatBoltListener Listener()
        {
            if (_listener == null || !_listener) _listener = VoiceChatBoltListener._instance;
            return _listener != null && _listener ? _listener : null;
        }

        private static Talker Find(ulong id)
        {
            foreach (var talker in Heard)
                if (talker.Id == id) return talker;
            return null;
        }

        private static void Report(string what, Exception error)
        {
            if (_logged) return;
            _logged = true;
            RuntimeEntryPoint.LogSource?.LogWarning("Voice presence " + what + ": " + error.Message);
        }

        /// <summary>One player this PC has heard, by the network ID of the entity their voice comes from.</summary>
        internal sealed class Talker
        {
            internal readonly ulong Id;
            internal float HeardAt = float.NegativeInfinity, NextCheck, NextRange;
            internal bool Positional, Talking, Muted, InRange = true;
            internal Transform Source;
            /// <summary>Their Steam ID, or 0 where this PC has not been told it.</summary>
            internal ulong SteamId;

            internal Talker(ulong id) => Id = id;

            /// <summary>Talking and not muted here: what the speaking marks show.</summary>
            internal bool Speaking => Talking && !Muted;
        }
    }
}
