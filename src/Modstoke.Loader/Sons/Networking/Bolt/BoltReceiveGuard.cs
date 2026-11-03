// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Bolt;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Modstoke.Loader;
using UdpKit;

namespace Modstoke.Sons
{
    // Admission may abandon the rest of a packet only while every remaining channel is guarded.
    // PacketReceived still owns its Packet/UdpPacket cleanup; disconnect occurs after that returns.
    internal static class BoltReceiveGuard
    {
        private static readonly HashSet<IntPtr> ChannelClasses = new();
        private static int _attempted;
        private static int _ready;
        private static int _ownerThread;
        private static int _reported;
        [ThreadStatic] private static ReceiveScope _current;

        internal static bool IsReady => Volatile.Read(ref _ready) != 0;

        internal static void Install(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (Interlocked.Exchange(ref _attempted, 1) != 0) return;
            HarmonyLib.Harmony harmony = null;
            try
            {
                if (support == null || !support.SafeForHarmonyPatches || !GameBuild.SupportsServerCore) return;
                _ownerThread = Environment.CurrentManagedThreadId;
                harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-bolt-receive-guard");
                RegisterChannel<SceneLoadChannel>();
                RegisterChannel<EventChannel>();
                RegisterChannel<EntityChannel>();
                RegisterChannel<EntityChannel.CommandChannel>();
                var receive = AccessTools.DeclaredMethod(typeof(BoltConnection), nameof(BoltConnection.PacketReceived),
                    new[] { typeof(UdpPacket) }) ?? throw new MissingMethodException("BoltConnection.PacketReceived");
                harmony.Patch(receive,
                    prefix: new HarmonyMethod(typeof(BoltReceiveGuard), nameof(BeforePacket))
                    { priority = HarmonyLib.Priority.Last },
                    finalizer: new HarmonyMethod(typeof(BoltReceiveGuard), nameof(FinishPacket)));
                Volatile.Write(ref _ready, 1);

                void RegisterChannel<T>() where T : BoltChannel
                {
                    RuntimeHelpers.RunClassConstructor(typeof(T).TypeHandle);
                    var target = AccessTools.DeclaredMethod(typeof(T), nameof(BoltChannel.Read), new[] { typeof(Packet) })
                        ?? throw new MissingMethodException(typeof(T).FullName, "Read");
                    harmony.Patch(target, prefix: new HarmonyMethod(typeof(BoltReceiveGuard), nameof(BeforeChannel))
                    { priority = HarmonyLib.Priority.First + 100 });
                    ChannelClasses.Add(Il2CppClassPointerStore<T>.NativeClassPtr);
                }
            }
            catch (Exception error)
            {
                Volatile.Write(ref _ready, 0);
                try { harmony?.UnpatchSelf(); } catch (Exception cleanup) { Report(cleanup); }
                try { warning?.Invoke("Bolt receive admission guard failed: " + error); } catch (Exception) { }
            }
        }

        private static bool BeforePacket(BoltConnection __instance, UdpPacket __0, bool __runOriginal,
            out ReceiveScope __state)
        {
            __state = null;
            if (!__runOriginal) return false;
            for (var ancestor = _current; ancestor != null; ancestor = ancestor.Parent)
                if (__0 != null && ancestor.Packet.Pointer == __0.Pointer) return false;
            if (!IsReady || (!BoltStateAdmission.HasPolicySubscribers
                && !BoltCommands.HasAdmissionSubscribers && !BoltProperties.HasSubscribers)
                || Environment.CurrentManagedThreadId != _ownerThread) return true;
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer || __instance == null || __0 == null) return true;
                var channels = __instance._channels;
                if (channels == null) return true;
                // Unknown channel overrides cannot safely be skipped. No policies run on such packets.
                for (int index = 0; index < channels.Length; index++)
                {
                    var channel = channels[index];
                    if (channel == null || !ChannelClasses.Contains(IL2CPP.il2cpp_object_get_class(channel.Pointer)))
                    {
                        Report(new InvalidOperationException("Unqualified channel layout; state/command admission is inactive for this packet."));
                        return true;
                    }
                }
                var scope = new ReceiveScope(__instance, __0, _current);
                if (!scope.MatchesSource()) return true;
                __state = scope;
                _current = scope;
            }
            catch (Exception error) { Report(error); }
            return true;
        }

        private static bool BeforeChannel(Packet __0, bool __runOriginal) => __runOriginal && !IsAborted(__0);

        private static void FinishPacket(ReceiveScope __state)
        {
            if (__state == null) return;
            _current = __state.Parent;
            if (!__state.Aborted) return;
            try
            {
                // Do not free either packet here. The original receiver's finally has done that.
                // A closed/replaced transport must never receive this connection's rejection.
                if (__state.MatchesSource()) __state.Source.Disconnect();
            }
            catch (Exception error) { Report(error); }
        }

        internal static bool IsAborted(Packet packet) => _current?.Aborted == true
            && packet != null && Match(packet.UdpPacket, null)?.Aborted == true;
        internal static bool CanAbort(Packet packet, BoltConnection source) => packet != null && CanAbort(packet.UdpPacket, source);
        internal static bool CanAbort(UdpPacket packet, BoltConnection source) => IsReady && Match(packet, source) != null;
        internal static bool IsCurrent(UdpPacket packet, BoltConnection source) => Match(packet, source)?.MatchesSource() == true;
        internal static bool TryAbort(Packet packet, BoltConnection source) => packet != null && TryAbort(packet.UdpPacket, source);

        internal static bool TryAbort(UdpPacket packet, BoltConnection source)
        {
            var scope = Match(packet, source);
            if (!IsReady || scope == null) return false;
            scope.Aborted = true;
            for (var parent = scope.Parent; parent != null; parent = parent.Parent)
                if (parent.Source.Pointer == scope.Source.Pointer) parent.Aborted = true;
            return true;
        }

        private static ReceiveScope Match(UdpPacket packet, BoltConnection source)
        {
            var scope = _current;
            return scope != null && packet != null && scope.Packet.Pointer == packet.Pointer
                && (source == null || source.Pointer == scope.Source.Pointer) ? scope : null;
        }

        private static void Report(Exception error)
        {
            if (Interlocked.Exchange(ref _reported, 1) != 0) return;
            try { RuntimeEntryPoint.LogSource?.LogWarning("Bolt receive admission could not complete ("
                + error.GetType().FullName + "). Further guard failures will not be logged."); } catch (Exception) { }
        }

        private sealed class ReceiveScope
        {
            internal readonly ReceiveScope Parent;
            internal readonly BoltConnection Source;
            internal readonly UdpPacket Packet;
            private readonly IntPtr _transport;
            private readonly IntPtr _socket;
            private readonly uint _id;
            internal bool Aborted;

            internal ReceiveScope(BoltConnection source, UdpPacket packet, ReceiveScope parent)
            {
                Source = source;
                Packet = packet;
                Parent = parent;
                _id = source.ConnectionId;
                var transport = source._udp ?? throw new InvalidOperationException("Missing receive transport.");
                _transport = transport.Pointer;
                _socket = transport.Socket?.Pointer ?? IntPtr.Zero;
            }

            internal bool MatchesSource()
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer || Source.WasCollected || Source.ConnectionId != _id) return false;
                var transport = Source._udp;
                var socket = BoltCore._udpSocket;
                return transport != null && !transport.WasCollected && transport.Pointer == _transport
                    && transport.ConnectionId == _id && transport.State == UdpConnectionState.Connected
                    && transport.UserToken?.Pointer == Source.Pointer && _socket != IntPtr.Zero
                    && transport.Socket?.Pointer == _socket && socket != null && !socket.WasCollected && socket.Pointer == _socket;
            }
        }
    }
}
