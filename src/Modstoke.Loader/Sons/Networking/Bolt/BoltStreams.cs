// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Loader;
using UdpKit;

namespace Modstoke.Sons
{
    /// <summary>Optional reassembled-stream observation and rejection before Bolt listener dispatch.</summary>
    public static class BoltStreams
    {
        public const string ServiceKey = "openacai.sons.boltStreams";
        private static BoltStreamHookState _state = new(BoltEventHookStatus.NotAttempted,
            "Stream observation and rejection have not been registered.");
        private static int _installationAttempted;
        private static int _ownerThread;
        private static int _reportedFailure;
        [ThreadStatic] private static bool _receiving;
        [ThreadStatic] private static bool _enforcing;

        /// <summary>
        /// Synchronous game-thread observation only. Each request expires when its subscriber returns.
        /// Runs before Rejection. Observer failures are isolated and cannot reject a stream.
        /// Without Rejection subscribers, re-entry and stale identities skip observation, not native dispatch.
        /// No subscribers means no additional native reads or allocations. Unsubscribe on unload.
        /// This is after reassembly, not allocation protection, transport ACK denial, or BlobTransport.
        /// The SDK control channel is consumed earlier by the existing control transport.
        /// </summary>
        public static event Action<BoltStreamRequest> Receiving;

        /// <summary>Observes streams from the exact current server connection without rejecting them.</summary>
        /// <remarks>
        /// Runs before ClientRejection; requests expire when the callback returns.
        /// Without ClientRejection subscribers, invalid context skips observation, not native dispatch.
        /// Does not run server policies on a client. Unsubscribe on unload.
        /// </remarks>
        public static event Action<BoltStreamRequest> ClientReceiving;

        // Internal protocol consumers must not act on messages either side's policy rejects.
        internal static event Action<BoltStreamRequest> PolicyAccepted;

        /// <summary>
        /// Opt-in synchronous server policy after Receiving, before native listeners. Call Reject to
        /// suppress all listeners for this stream. Each subscriber gets an expiring request sharing
        /// one irreversible decision; the first rejection stops policy dispatch. Subscriber exceptions,
        /// invalidated lifetimes, wrong-thread dispatch and callback re-entry reject while policy is active.
        /// The subscriber snapshot applies to this dispatch even if subscriptions change during callbacks.
        /// No default rule, rate limit, payload copy limit, disconnect, ACK denial or preallocation guard.
        /// Native payload storage is neither freed nor returned to a pool. Unsubscribe on unload.
        /// </summary>
        public static event Action<BoltStreamRequest> Rejection;

        /// <summary>Opt-in client policy before internal stream consumers and native listeners.</summary>
        /// <remarks>
        /// Runs synchronously after ClientReceiving and requires the exact current server connection
        /// throughout dispatch. Uses the same
        /// expiring requests, irreversible rejection and fail-closed handling as Rejection, but never
        /// runs on a server. Rejection is after reassembly/allocation; it does not disconnect, undo ACKs
        /// or free native payload storage. Unsubscribe on unload.
        /// </remarks>
        public static event Action<BoltStreamRequest> ClientRejection;

        public static BoltStreamHookState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0) return;
            HarmonyLib.Harmony harmony = null;
            try
            {
                if (support == null || !support.SafeForHarmonyPatches || !GameBuild.SupportsServerCore)
                {
                    SetState(BoltEventHookStatus.Disabled,
                        "Stream observation and rejection require safe Harmony support and a recognized game build.");
                    return;
                }
                _ownerThread = Environment.CurrentManagedThreadId;
                // Native dispatcher: client 0x1809382E0 (BoltCore caller 0x1807B5D00), dedicated server 0x1808415C0 (caller 0x1806F0F70).
                var target = AccessTools.DeclaredMethod(typeof(BoltInternal.GlobalEventListenerBase),
                    nameof(BoltInternal.GlobalEventListenerBase.StreamDataReceivedInvoke),
                    new[] { typeof(BoltConnection), typeof(UdpStreamData) })
                    ?? throw new MissingMethodException("GlobalEventListenerBase.StreamDataReceivedInvoke(BoltConnection,UdpStreamData)");
                if (!target.IsStatic || target.ReturnType != typeof(void))
                    throw new InvalidOperationException("The stream dispatcher signature is unsupported.");
                harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-bolt-streams");
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(BoltStreams), nameof(BeforeReceive))
                    { priority = HarmonyLib.Priority.Last });
                SetState(BoltEventHookStatus.Installed,
                    "Pre-listener observation and opt-in rejection registered; live receipt and rejection remain unverified.");
            }
            catch (Exception error)
            {
                SetState(BoltEventHookStatus.Failed, "Stream registration failed; callbacks are inactive.");
                try { harmony?.UnpatchSelf(); }
                catch (Exception cleanup)
                {
                    SetState(BoltEventHookStatus.Failed,
                        "Stream registration and rollback failed; callbacks are inactive, native patch state is uncertain.");
                    Warn(warning, "Stream hook rollback failed: " + cleanup);
                }
                Warn(warning, "Stream hook registration failed: " + error);
            }
        }

        private static bool BeforeReceive(BoltConnection __0, UdpStreamData __1, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            // Preserve the outer policy even if a callback unsubscribes before recursive dispatch.
            if (_enforcing) return false;
            var handlers = Receiving;
            var policies = Rejection;
            var clientHandlers = ClientReceiving;
            var clientPolicies = ClientRejection;
            var acceptedHandlers = PolicyAccepted;
            if ((handlers == null && policies == null && clientHandlers == null && clientPolicies == null
                && acceptedHandlers == null)
                || State.Status != BoltEventHookStatus.Installed) return true;
            var enforce = policies != null || clientPolicies != null;
            if (!enforce && (_receiving || Environment.CurrentManagedThreadId != _ownerThread)) return true;
            var entered = false;
            StreamDecision decision = null;
            try
            {
                if (!BoltNetwork.isRunning) return true;
                if (!BoltNetwork.isServer)
                {
                    if (!BoltNetwork.isClient) return true;
                    handlers = clientHandlers;
                    policies = clientPolicies;
                }
                enforce = policies != null;
                if (handlers == null && policies == null && acceptedHandlers == null) return true;
                if (_receiving || Environment.CurrentManagedThreadId != _ownerThread) return !enforce;
                _receiving = entered = true;
                _enforcing = enforce;
                if (__0 == null || __1 == null || __0.WasCollected || __1.WasCollected) return !enforce;
                decision = enforce ? new StreamDecision() : null;
                var scope = new StreamScope(__0, __1, decision);
                if (!scope.MatchesCurrentContext()) { decision?.Reject(); return !enforce; }
                if (handlers != null && !DispatchObservers(handlers, scope))
                {
                    decision?.Reject();
                    return !enforce;
                }
                if (policies != null)
                {
                    foreach (var policy in Delegate.EnumerateInvocationList(policies))
                    {
                        var request = new BoltStreamRequest(scope, canReject: true);
                        try { policy(request); }
                        finally { request.Close(); }
                        if (decision.IsRejected || !scope.MatchesCurrentContext()) { decision.Reject(); return false; }
                    }
                }
                if (acceptedHandlers != null && !DispatchObservers(acceptedHandlers, scope))
                {
                    decision?.Reject();
                    return !enforce;
                }
                // Both native receive paths allocate GC storage; the dispatcher only disposes its
                // listener enumerator. Skipping it transfers no cleanup obligation to this hook.
                return true;
            }
            catch (Exception error)
            {
                decision?.Reject();
                ReportFailure(error);
                return !enforce;
            }
            finally
            {
                if (entered) { _enforcing = false; _receiving = false; }
            }
        }

        private static bool DispatchObservers(Action<BoltStreamRequest> handlers, StreamScope scope)
        {
            foreach (var handler in Delegate.EnumerateInvocationList(handlers))
            {
                var request = new BoltStreamRequest(scope, canReject: false);
                try { handler(request); }
                catch (Exception error) { ReportFailure(error); }
                finally { request.Close(); }
                if (!scope.MatchesCurrentContext()) return false;
            }
            return true;
        }

        private static void SetState(BoltEventHookStatus status, string detail)
        {
            var state = new BoltStreamHookState(status, detail);
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
            try { RuntimeEntryPoint.LogSource?.LogWarning("Stream hook callback or context failed ("
                + error.GetType().FullName + "). Policy-covered dispatch fails closed; observation alone does not reject. Further failures will not be logged."); }
            catch (Exception) { }
        }

        internal sealed class StreamDecision
        {
            private int _rejected;
            internal bool IsRejected => Volatile.Read(ref _rejected) != 0;
            internal void Reject() => Interlocked.Exchange(ref _rejected, 1);
        }

        internal sealed class StreamScope
        {
            internal readonly BoltConnection Source;
            internal readonly UdpStreamData Stream;
            internal readonly UdpConnection Transport;
            internal readonly UdpSocket Socket;
            internal readonly Il2CppStructArray<byte> Payload;
            internal readonly BoltStreamContext Context;
            internal readonly StreamDecision Decision;
            private readonly bool _server;
            internal bool SessionResolved;
            internal ServerSession Session;

            internal StreamScope(BoltConnection source, UdpStreamData stream, StreamDecision decision)
            {
                Source = source;
                Stream = stream;
                Decision = decision;
                _server = BoltNetwork.isServer;
                Transport = source._udp ?? throw new InvalidOperationException("Stream source has no transport.");
                if (Transport.WasCollected) throw new InvalidOperationException("Stream transport has expired.");
                Socket = Transport.Socket ?? throw new InvalidOperationException("Stream source has no socket.");
                Payload = stream.Data ?? throw new InvalidOperationException("Stream payload is unavailable.");
                var channel = stream.Channel ?? throw new InvalidOperationException("Stream channel is unavailable.");
                if (Socket.WasCollected || Payload.WasCollected || channel.WasCollected)
                    throw new InvalidOperationException("Stream context has expired.");
                Context = new BoltStreamContext(source.ConnectionId, channel.Id, channel.Name, Payload.Length);
            }

            internal bool MatchesCurrentContext()
            {
                if (!BoltNetwork.isRunning || BoltNetwork.isServer != _server
                    || Source.WasCollected || Stream.WasCollected
                    || Transport.WasCollected || Socket.WasCollected || Payload.WasCollected) return false;
                if (!_server)
                {
                    var server = BoltNetwork.server;
                    if (!BoltNetwork.isClient || server == null || server.WasCollected || server.Pointer != Source.Pointer)
                        return false;
                }
                var socket = BoltCore._udpSocket;
                var transport = Source._udp;
                if (socket == null || socket.WasCollected || socket.Pointer != Socket.Pointer
                    || transport == null || transport.WasCollected || transport.Pointer != Transport.Pointer
                    || Source.ConnectionId != Context.SourceConnectionId
                    || Transport.ConnectionId != Context.SourceConnectionId || Transport.State != UdpConnectionState.Connected
                    || Payload.Length != Context.PayloadByteCount
                    || (Session != null && !Session.IsCurrent)) return false;
                var transportSocket = Transport.Socket;
                var token = Transport.UserToken;
                var payload = Stream.Data;
                if (transportSocket == null || transportSocket.WasCollected || transportSocket.Pointer != Socket.Pointer
                    || token == null || token.WasCollected || token.Pointer != Source.Pointer
                    || payload == null || payload.WasCollected || payload.Pointer != Payload.Pointer) return false;
                var channel = Stream.Channel;
                // Channel is an inline value type, boxed afresh by interop; compare its copied fields.
                return channel != null && !channel.WasCollected && channel.Id == Context.ChannelId
                    && string.Equals(channel.Name, Context.ChannelName, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>Registration status only; no live multiplayer acceptance is implied.</summary>
    public sealed record BoltStreamHookState(BoltEventHookStatus Status, string Detail)
    {
        /// <summary>Registered capability, not active policy or verified runtime receipt.</summary>
        public bool CanReject => Status == BoltEventHookStatus.Installed;
    }

    /// <summary>
    /// Immutable copied metadata, with no native handles. SourceConnectionId is the immediate
    /// dispatcher connection, not an authenticated player identity; IDs may be reused after closure.
    /// Channel metadata and payload length do not establish payload validity or gameplay authority.
    /// </summary>
    public sealed record BoltStreamContext
    {
        internal BoltStreamContext(uint sourceConnectionId, int channelId, string channelName, int payloadByteCount)
        {
            SourceConnectionId = sourceConnectionId;
            ChannelId = channelId;
            ChannelName = channelName;
            PayloadByteCount = payloadByteCount;
        }

        public uint SourceConnectionId { get; }
        public int ChannelId { get; }
        public string ChannelName { get; }
        public int PayloadByteCount { get; }
    }

    /// <summary>Callback-scoped payload reads and optional rejection; native storage never escapes.</summary>
    public sealed class BoltStreamRequest
    {
        private BoltStreams.StreamScope _scope;
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly bool _canReject;
        private readonly BoltStreams.StreamDecision _decision;
        private int _accessState; // 0: open, 1: closed, 2: invalidated.

        internal BoltStreamRequest(BoltStreams.StreamScope scope, bool canReject)
        {
            _scope = scope;
            _canReject = canReject;
            _decision = scope.Decision;
            Context = scope.Context;
        }

        public BoltStreamContext Context { get; }

        /// <summary>Shared irreversible decision. Reading it does not extend callback lifetime.</summary>
        public bool IsRejected => _decision?.IsRejected == true;

        /// <summary>Only live Rejection or ClientRejection callbacks may reject; observers cannot.</summary>
        public bool Reject()
        {
            var scope = GetScope();
            if (scope == null || !_canReject) return false;
            try
            {
                if (!scope.MatchesCurrentContext()) { Invalidate(); return false; }
                if (!ReferenceEquals(scope, GetScope())) return false;
                _decision.Reject();
                return true;
            }
            catch (Exception) { Invalidate(); return false; }
        }

        /// <summary>
        /// Copies exactly destination.Length bytes within the payload without changing native storage.
        /// The caller supplies the buffer and chooses whether to read a header, a slice or the payload.
        /// False means unavailable, expired, wrong thread or invalid range.
        /// A wrong-thread access permanently closes this request. Retain Context or copied bytes instead.
        /// A live policy request invalidated by a lifetime/read failure or wrong thread rejects the stream.
        /// </summary>
        public bool TryCopyBytes(int byteOffset, Span<byte> destination)
        {
            var scope = GetScope();
            if (scope == null || byteOffset < 0 || byteOffset > Context.PayloadByteCount
                || destination.Length > Context.PayloadByteCount - byteOffset) return false;
            try
            {
                if (!scope.MatchesCurrentContext()) { Invalidate(); return false; }
                if (!ReferenceEquals(scope, GetScope())) return false;
                scope.Payload.AsSpan().Slice(byteOffset, destination.Length).CopyTo(destination);
                return true;
            }
            catch (Exception) { Invalidate(); return false; }
        }

        /// <summary>Optional lazy session lookup; payload reads never scan the connection roster.</summary>
        public bool TryGetSourceSession(out ServerSession session)
        {
            session = null;
            var scope = GetScope();
            if (scope == null) return false;
            try
            {
                if (!scope.MatchesCurrentContext()) { Invalidate(); return false; }
                if (!scope.SessionResolved)
                {
                    scope.Session = ServerSessions.ResolveSource(scope.Source);
                    scope.SessionResolved = true;
                }
                if (!scope.MatchesCurrentContext()) { Invalidate(); return false; }
                if (!ReferenceEquals(scope, GetScope()) || scope.Session == null) return false;
                session = scope.Session;
                return true;
            }
            catch (Exception) { Invalidate(); return false; }
        }

        /// <summary>
        /// Resolve the exact selected transport peer, not merely its reusable connection number.
        /// This is lifetime correlation only, not enrollment, authority or player authentication.
        /// </summary>
        public bool TryGetSourcePeer(out ControlPeer peer)
        {
            peer = null;
            var scope = GetScope();
            if (scope == null) return false;
            try
            {
                if (!scope.MatchesCurrentContext()) { Invalidate(); return false; }
                var candidate = ControlTransport.FindPeer(scope.Source);
                if (candidate == null || candidate.ConnectionPointer != scope.Source.Pointer
                    || candidate.ConnectionId != Context.SourceConnectionId
                    || candidate.TransportPointer != scope.Transport.Pointer
                    || candidate.SocketPointer != scope.Socket.Pointer || !candidate.IsCurrent
                    || !ReferenceEquals(scope, GetScope()) || !scope.MatchesCurrentContext()) return false;
                peer = candidate;
                return true;
            }
            catch (Exception) { Invalidate(); return false; }
        }

        private BoltStreams.StreamScope GetScope()
        {
            if (Environment.CurrentManagedThreadId != _thread) { Invalidate(); return null; }
            return Volatile.Read(ref _accessState) == 0 ? Volatile.Read(ref _scope) : null;
        }

        private void Invalidate()
        {
            if (Interlocked.CompareExchange(ref _accessState, 2, 0) != 0) return;
            if (_canReject) _decision.Reject();
            Interlocked.Exchange(ref _scope, null);
        }

        internal void Close()
        {
            // Publish a concurrent wrong-thread invalidation before the dispatcher reads its decision.
            if (Interlocked.Exchange(ref _accessState, 1) == 2 && _canReject) _decision.Reject();
            Interlocked.Exchange(ref _scope, null);
        }
    }
}
