// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Loader;
using global::Sons.Multiplayer;
using UdpKit;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>Connection-bound policy for native server voice relays and listen-host playback.</summary>
    public static partial class ServerVoice
    {
        public const int MaximumPayloadBytes = 1180;
        public const int MaximumCandidates = 256;
        private static ServerVoiceState _state = new(VoiceHookStatus.NotAttempted,
            "Server voice routing has not been installed.");
        private static int _installationAttempted;
        private static int _ownerThread;
        private static int _reportedFailure;
        [ThreadStatic] private static IncomingVoice _incoming;
        [ThreadStatic] private static OutgoingVoice _outgoing;
        [ThreadStatic] private static bool _publishing;

        /// <summary>
        /// Runs before each selected recipient's transport send or host playback. Any rejection or handler
        /// exception denies that delivery. Subscribing enables connection-bound validation and bounded routing;
        /// no subscribers leaves native behavior unchanged. Audio is never exposed through this event.
        /// </summary>
        public static event Action<VoiceDeliveryRequest> Routing;

        public static ServerVoiceState State => Volatile.Read(ref _state);
        private static bool PolicyActive => State.Status == VoiceHookStatus.Installed
            && (Routing != null || _incoming != null || _outgoing != null);

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0) return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                PublishState(VoiceHookStatus.Disabled, support?.Detail ?? "Harmony support is unavailable.");
                return;
            }
            _ownerThread = Environment.CurrentManagedThreadId;
            PublishState(VoiceHookStatus.Installing, "Registering native voice routing boundaries.");
            try
            {
                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-server-voice");
                Patch(typeof(VoiceChatBoltListener), nameof(VoiceChatBoltListener.SendVoiceData),
                    new[] { typeof(Il2CppStructArray<byte>), typeof(int), typeof(BoltConnection), typeof(ulong), typeof(bool) }, nameof(BeforeSend));
                Patch(typeof(CoopSteamVoicePlayer), nameof(CoopSteamVoicePlayer.DataReceived),
                    new[] { typeof(Il2CppStructArray<byte>), typeof(int), typeof(ulong), typeof(bool), typeof(Transform),
                        typeof(IPlayerState), typeof(bool), typeof(float).MakeByRefType() }, nameof(BeforePlayback));
                Patch(typeof(VoiceChatBoltListener), nameof(VoiceChatBoltListener.ForwardVoiceData),
                    new[] { typeof(Il2CppStructArray<byte>), typeof(int), typeof(Bolt.NetworkId), typeof(bool) }, nameof(BeforeForward));
                Patch(typeof(VoiceChatBoltListener), nameof(VoiceChatBoltListener.ReceiveVoiceData),
                    new[] { typeof(UdpStreamData) }, nameof(BeforeReceive), nameof(FinishReceive));
                Patch(typeof(VoiceChatBoltListener), nameof(VoiceChatBoltListener.StreamDataReceived),
                    new[] { typeof(BoltConnection), typeof(UdpStreamData) }, nameof(BeforeStream), nameof(FinishStream));

                void Patch(Type type, string name, Type[] arguments, string prefix, string finalizer = null)
                {
                    var target = AccessTools.Method(type, name, arguments) ?? throw new MissingMethodException(type.FullName, name);
                    harmony.Patch(target, prefix: new HarmonyMethod(typeof(ServerVoice), prefix) { priority = Priority.Last },
                        finalizer: finalizer == null ? null : new HarmonyMethod(typeof(ServerVoice), finalizer) { priority = Priority.Last });
                }
            }
            catch (Exception error)
            {
                PublishState(VoiceHookStatus.Failed, "Voice registration failed; partial hooks remain inactive.");
                addStartupWarning?.Invoke("Server voice routing registration failed: " + error);
                return;
            }
            PublishState(VoiceHookStatus.Installed,
                "Voice boundaries registered. Native relay, playback and dedicated operation require multiplayer qualification.");
        }

        private static bool BeforeStream(VoiceChatBoltListener __instance, BoltConnection __0, UdpStreamData __1,
            bool __runOriginal, out IncomingVoice __state)
        {
            __state = null;
            if (!__runOriginal) return false;
            if (!PolicyActive) return true;
            if (!OnOwnerThread()) return false;
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer) return true;
                if (_publishing || __instance == null || __1 == null) return false;
                var channel = VoiceChatBoltListener._voiceChannel;
                // Global listeners receive every stream; other registered channels are not voice envelopes.
                if (channel.Name != "Voice" || __1.Channel.Id != channel.Id || __1.Channel.Name != channel.Name) return false;
                var bytes = __1.Data;
                if (bytes == null || bytes.Length < 14 || bytes.Length > MaximumPayloadBytes + 13) return false;
                ulong sourceId = 0;
                for (int index = 0; index < 8; index++) sourceId |= (ulong)bytes[index] << (index * 8);
                uint size = (uint)(bytes[8] | bytes[9] << 8 | bytes[10] << 16 | bytes[11] << 24);
                if (size == 0 || size > MaximumPayloadBytes || size != bytes.Length - 13 || bytes[12] > 1) return false;
                var source = CaptureRemote(__0);
                if (source == null || source.NetworkId != sourceId) return false;
                var handlers = Routing;
                if (handlers == null) return false;
                __state = new IncomingVoice(_incoming, __instance, __1.Pointer, source, (int)size, bytes[12] == 1, handlers);
                _incoming = __state;
                return true;
            }
            catch (Exception error) { ReportFailure(error); return false; }
        }

        private static Exception FinishStream(Exception __exception, IncomingVoice __state)
        {
            if (__state != null) _incoming = __state.Parent;
            return __exception;
        }

        private static bool BeforeReceive(VoiceChatBoltListener __instance, UdpStreamData __0,
            bool __runOriginal, out IncomingVoice __state)
        {
            __state = null;
            if (!__runOriginal) return false;
            if (!PolicyActive) return true;
            if (!OnOwnerThread()) return false;
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer) return true;
                var frame = _incoming;
                if (_publishing || frame == null || frame.Receiving || __0 == null || __instance == null
                    || frame.Stream != __0.Pointer || frame.Listener.Pointer != __instance.Pointer || !frame.Source.IsCurrent) return false;
                frame.Receiving = true;
                __state = frame;
                return true;
            }
            catch (Exception error) { ReportFailure(error); return false; }
        }

        private static Exception FinishReceive(Exception __exception, IncomingVoice __state)
        {
            if (__state != null) __state.Receiving = false;
            return __exception;
        }

        private static VoiceParticipant CaptureRemote(BoltConnection connection)
        {
            var session = ServerSessions.ResolveSource(connection);
            if (session == null || !ServerWorld.TryCapturePlayerBinding(session, out var binding, out _)) return null;
            var player = new VoiceParticipant(binding, null);
            return player.IsCurrent ? player : null;
        }

        private static VoiceParticipant CaptureHost()
        {
            if (!LocalHost.TryGetPlayer(out var host)
                || !ServerWorld.TryCaptureLocalPlayerBinding(out var binding, out _)) return null;
            var player = new VoiceParticipant(binding, host);
            return player.IsCurrent ? player : null;
        }

        private static bool OnOwnerThread()
        {
            if (Environment.CurrentManagedThreadId == _ownerThread) return true;
            ReportFailure(new InvalidOperationException("Voice callback is outside the SDK game thread."));
            return false;
        }

        private static bool ValidBuffer(Il2CppStructArray<byte> bytes, int size) =>
            bytes != null && size > 0 && size <= MaximumPayloadBytes && size <= bytes.Length;

        private static void PublishState(VoiceHookStatus status, string detail) =>
            Volatile.Write(ref _state, new ServerVoiceState(status, detail));

        private static void ReportFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0) return;
            try { RuntimeEntryPoint.LogSource?.LogWarning("Voice routing denied an operation (" + error.GetType().FullName + "). Further failures will not be logged."); }
            catch (Exception) { }
        }

        private sealed class IncomingVoice
        {
            internal readonly IncomingVoice Parent;
            internal readonly VoiceChatBoltListener Listener;
            internal readonly IntPtr Stream;
            internal readonly VoiceParticipant Source;
            internal readonly int Size;
            internal readonly bool Positional;
            internal readonly Action<VoiceDeliveryRequest> Handlers;
            internal readonly DateTimeOffset ReceivedAt = DateTimeOffset.UtcNow;
            internal bool Receiving;
            internal bool Forwarded;
            internal bool PlaybackAttempted;
            internal IntPtr CompressedBuffer;

            internal IncomingVoice(IncomingVoice parent, VoiceChatBoltListener listener, IntPtr stream,
                VoiceParticipant source, int size, bool positional, Action<VoiceDeliveryRequest> handlers)
            {
                Parent = parent; Listener = listener; Stream = stream; Source = source;
                Size = size; Positional = positional; Handlers = handlers;
            }
        }
    }
}
