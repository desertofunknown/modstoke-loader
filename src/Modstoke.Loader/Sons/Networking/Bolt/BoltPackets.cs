// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;
using Bolt;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Loader;
using UdpKit;

namespace Modstoke.Sons
{
    /// <summary>Optional server admission of complete Bolt game packets before channel decoding.</summary>
    public static class BoltPackets
    {
        public const string ServiceKey = "openacai.sons.boltPackets";
        private static BoltPacketHookState _state = new(BoltEventHookStatus.NotAttempted,
            "Game-packet admission has not been registered.");
        private static int _installationAttempted;
        private static int _ownerThread;
        private static int _reportedFailure;
        [ThreadStatic] private static HashSet<IntPtr> _active;
        [ThreadStatic] private static bool _callingPolicy;

        /// <summary>
        /// Runs after SDK enrollment and custom-datagram handling, before Bolt acquires its channel
        /// packet. Rejection drops the entire game payload, including unrelated channel updates.
        /// Exceptions reject this packet. No default policy, disconnect, or rate limit is imposed.
        /// Unsubscribe on unload. Never recursively feed packets from a policy callback.
        /// </summary>
        public static event Action<BoltPacketRequest> Receiving;

        public static BoltPacketHookState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0) return;
            HarmonyLib.Harmony harmony = null;
            try
            {
                if (support == null || !support.SafeForHarmonyPatches || !GameBuild.SupportsServerCore)
                {
                    SetState(BoltEventHookStatus.Disabled,
                        "Game-packet admission requires safe Harmony support and a recognized game build.");
                    return;
                }
                _ownerThread = Environment.CurrentManagedThreadId;
                var target = AccessTools.DeclaredMethod(typeof(BoltConnection), nameof(BoltConnection.PacketReceived),
                    new[] { typeof(UdpPacket) }) ?? throw new MissingMethodException("BoltConnection.PacketReceived(UdpPacket)");
                harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-bolt-packets");
                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(BoltPackets), nameof(BeforeReceive))
                    {
                        priority = HarmonyLib.Priority.Normal, before = new[] { "sonssdk_harmony" },
                        after = new[] { RuntimeEntryPoint.PluginGuid + ".sons-enrollment-gate",
                            RuntimeEntryPoint.PluginGuid + ".sons-datagrams" }
                    },
                    finalizer: new HarmonyMethod(typeof(BoltPackets), nameof(FinishReceive)));
                SetState(BoltEventHookStatus.Installed,
                    "Whole game-packet admission registered; transport parsing and live multiplayer behavior are not qualified by registration.");
            }
            catch (Exception error)
            {
                SetState(BoltEventHookStatus.Failed, "Game-packet admission registration failed; callbacks are inactive.");
                try { harmony?.UnpatchSelf(); }
                catch (Exception cleanup) { Warn(warning, "Game-packet rollback failed: " + cleanup); }
                Warn(warning, "Game-packet admission registration failed: " + error);
            }
        }

        private static bool BeforeReceive(BoltConnection __instance, UdpPacket __0, bool __runOriginal,
            out PacketScope __state)
        {
            __state = null;
            if (!__runOriginal) return false;
            if (__0 != null && _active?.Contains(__0.Pointer) == true) return false;
            var handlers = Receiving;
            if (handlers == null || State.Status != BoltEventHookStatus.Installed) return true;
            BoltPacketRequest request = null;
            bool enteredPolicy = false;
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer || __0 == null) return true;
                var active = _active ??= new HashSet<IntPtr>();
                __state = new PacketScope(__0, active);
                active.Add(__state.Pointer);
                if (_callingPolicy) return false;
                if (Environment.CurrentManagedThreadId != _ownerThread)
                    throw new InvalidOperationException("Game-packet admission requires the game thread.");
                request = new BoltPacketRequest(__instance, __0);
                if (!request.MatchesCurrentContext()) return false;
                _callingPolicy = enteredPolicy = true;
                foreach (var handler in Delegate.EnumerateInvocationList(handlers))
                {
                    handler(request);
                    if (request.IsRejected) return false;
                }
                if (!request.MatchesCurrentContext()) return false;
                __state.Rejected = false;
                return true;
            }
            catch (Exception error)
            {
                ReportFailure(error);
                // Before taking ownership, leave cleanup to the native receiver.
                return __state == null;
            }
            finally
            {
                request?.Close();
                if (enteredPolicy) _callingPolicy = false;
            }
        }

        private static void FinishReceive(PacketScope __state)
        {
            if (__state == null) return;
            try
            {
                // Native PacketReceived has not acquired Bolt.Packet on our rejection. Its normal
                // disposer returns this same UdpPacket to Pool; IsPooled prevents a second return.
                if (__state.Rejected && !__state.Packet.WasCollected && !__state.Packet.IsPooled)
                    __state.Packet.Dispose();
            }
            catch (Exception error) { ReportFailure(error); }
            finally { __state.Active.Remove(__state.Pointer); }
        }

        private static void SetState(BoltEventHookStatus status, string detail)
        {
            var state = new BoltPacketHookState(status, detail);
            Volatile.Write(ref _state, state);
            try { Modding.SetService(ServiceKey, state); }
            catch (Exception error) { ReportFailure(error); }
        }

        private static void Warn(Action<string> warning, string message)
        {
            try { warning?.Invoke(message); } catch (Exception) { }
        }

        private static void ReportFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0) return;
            try { RuntimeEntryPoint.LogSource?.LogWarning("Game-packet admission failed ("
                + error.GetType().FullName + "). Further failures will not be logged."); }
            catch (Exception) { }
        }

        private sealed class PacketScope(UdpPacket packet, HashSet<IntPtr> active)
        {
            internal readonly UdpPacket Packet = packet;
            internal readonly IntPtr Pointer = packet.Pointer;
            internal readonly HashSet<IntPtr> Active = active;
            internal bool Rejected = true;
        }
    }

    public sealed record BoltPacketHookState(BoltEventHookStatus Status, string Detail);

    /// <summary>Callback-scoped read-only packet access. Neither bytes nor connection IDs prove honest clients.</summary>
    public sealed class BoltPacketRequest
    {
        private BoltConnection _source;
        private UdpPacket _packet;
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly IntPtr _transport;
        private readonly IntPtr _socket;
        private readonly IntPtr _data;
        private bool _sessionResolved;
        private ServerSession _session;

        internal BoltPacketRequest(BoltConnection source, UdpPacket packet)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _packet = packet;
            var udp = source._udp ?? throw new InvalidOperationException("Packet source has no transport.");
            _transport = udp.Pointer;
            _socket = udp.Socket?.Pointer ?? IntPtr.Zero;
            _data = packet.Data?.Pointer ?? IntPtr.Zero;
            SourceConnectionId = source.ConnectionId;
            SizeBits = packet.Size;
            InitialBitPosition = packet.Position;
        }

        public uint SourceConnectionId { get; }
        public int SizeBits { get; }
        public int InitialBitPosition { get; }
        /// <summary>Whole payload bytes only; a final partial byte is deliberately not exposed.</summary>
        public int ReadableByteCount => System.Math.Max(0, SizeBits / 8);
        public bool IsRejected { get; private set; }

        public bool Reject()
        {
            if (!IsOpen) return false;
            IsRejected = true;
            return true;
        }

        /// <summary>Resolve identity only when requested; normal packet callbacks do not scan the roster.</summary>
        public bool TryGetSourceSession(out ServerSession session)
        {
            session = null;
            try
            {
                if (!MatchesCurrentContext()) return false;
                if (!_sessionResolved)
                {
                    _session = ServerSessions.ResolveSource(_source);
                    _sessionResolved = true;
                }
                session = _session;
                return session != null && session.IsCurrent;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Copies exactly destination.Length bytes from a byte offset without advancing the cursor.
        /// No allocation or native buffer escapes. False means no bytes were copied. Only whole
        /// payload bytes are readable, so unused tail bits and pool capacity are never disclosed.
        /// </summary>
        public bool TryCopyBytes(int byteOffset, Span<byte> destination)
        {
            try
            {
                if (!IsOpen || byteOffset < 0 || byteOffset > ReadableByteCount
                    || destination.Length > ReadableByteCount - byteOffset || !MatchesPacket()) return false;
                _packet.Data.AsSpan().Slice(byteOffset, destination.Length).CopyTo(destination);
                return true;
            }
            catch (Exception) { return false; }
        }

        private bool IsOpen => _packet != null && _source != null && Environment.CurrentManagedThreadId == _thread;

        private bool MatchesPacket()
        {
            if (_packet.WasCollected || _packet.IsPooled || _packet.Size != SizeBits
                || _packet.Position != InitialBitPosition || SizeBits < 0
                || InitialBitPosition < 0 || InitialBitPosition > SizeBits) return false;
            var data = _packet.Data;
            return data != null && data.Pointer == _data && SizeBits <= data.Length * 8L;
        }

        internal bool MatchesCurrentContext()
        {
            if (!IsOpen || !BoltNetwork.isRunning || !BoltNetwork.isServer || _source.WasCollected
                || _source.ConnectionId != SourceConnectionId || !MatchesPacket()) return false;
            var udp = _source._udp;
            var socket = BoltCore._udpSocket;
            return udp != null && !udp.WasCollected && udp.Pointer == _transport
                && udp.ConnectionId == SourceConnectionId && udp.State == UdpConnectionState.Connected
                && udp.UserToken?.Pointer == _source.Pointer
                && _socket != IntPtr.Zero && socket != null && !socket.WasCollected && socket.Pointer == _socket
                && udp.Socket?.Pointer == _socket
                && (_session == null || (_session.IsCurrent
                    && ReferenceEquals(_session, ServerSessions.ResolveSource(_source))));
        }

        internal void Close() { _source = null; _packet = null; }
    }
}
