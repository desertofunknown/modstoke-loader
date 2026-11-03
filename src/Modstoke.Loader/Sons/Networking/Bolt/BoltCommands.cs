// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection;
using System.Threading;
using Bolt;
using HarmonyLib;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    /// <summary>Command framework observation and optional predecode channel rejection with disconnect.</summary>
    public static class BoltCommands
    {
        public const string ServiceKey = "openacai.sons.boltCommands";
        private static BoltCommandHookState _state = new(
            BoltEventHookStatus.NotAttempted, "Command framework hooks have not been registered.");
        private static int _installationAttempted;
        private static int _admissionInstalled;
        private static int _reportedFailure;
        private static int _reportedAdmissionFailure;
        [ThreadStatic] private static bool _receiving;
        [ThreadStatic] private static bool _executing;
        [ThreadStatic] private static bool _admittingChannel;

        /// <summary>
        /// Server-only framework admission before CommandChannel.Read decodes either results or inputs.
        /// The recognized game does not put CommandChannel in its receive channel array and has no
        /// concrete Bolt.Command subclasses. Registration does not establish live command traffic.
        /// RejectAndDisconnect aborts the remaining packet and disconnects after native receive
        /// cleanup; it is not per-command rejection and cannot undo earlier channels. No target
        /// entity or decoded command is available at this boundary. No subscribers means no reads
        /// or allocations here. Callbacks run only in the shared guard's exact receive scope;
        /// exceptions or recursive policy entry request the same abort. Unsubscribe on mod unload.
        /// </summary>
        public static event Action<BoltCommandChannelRequest> ChannelReceiving;

        internal static bool HasAdmissionSubscribers => ChannelReceiving != null && CanRejectAndDisconnect;

        /// <summary>Hook/guard readiness only, not evidence that the game routes command channels.</summary>
        public static bool CanRejectAndDisconnect => State.Status == BoltEventHookStatus.Installed
            && Volatile.Read(ref _admissionInstalled) != 0 && BoltReceiveGuard.IsReady;

        /// <summary>
        /// Synchronous notification after a complete, normally returning input/result deserializer.
        /// Received does not mean admitted, queued, authorized, or executed. The target entity is unknown.
        /// Result reads can describe temporary commands that the caller immediately frees.
        /// </summary>
        public static event Action<BoltCommandReceived> Received;

        /// <summary>
        /// Synchronous notification after Entity.ExecuteCommand returns normally, including local
        /// prediction and replay. Current controller/source are not proof of the original sender.
        /// No subscribers means no observation reads or allocations. Observer errors are isolated;
        /// observer-triggered re-entry into the same observation family runs without notification.
        /// These rules also apply to Received. Unsubscribe on mod unload.
        /// </summary>
        public static event Action<BoltCommandExecuted> Executed;

        /// <summary>Registration status, not evidence of concrete game commands or live traffic.</summary>
        public static BoltCommandHookState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0) return;
            HarmonyLib.Harmony harmony = null;
            try
            {
                if (support == null || !support.SafeForHarmonyPatches || !GameBuild.SupportsServerCore)
                {
                    SetState(BoltEventHookStatus.Disabled,
                        "Command framework hooks require safe Harmony support and a recognized game build.");
                    return;
                }

                var readParameters = new[] { typeof(BoltConnection), typeof(UdpKit.UdpPacket) };
                var readInput = Target(typeof(Command), nameof(Command.ReadInput), readParameters);
                var readResult = Target(typeof(Command), nameof(Command.ReadResult), readParameters);
                var execute = Target(typeof(Entity), nameof(Entity.ExecuteCommand),
                    new[] { typeof(Command), typeof(bool) });
                harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-bolt-commands");
                BoltReceiveGuard.Install(support, warning);
                if (BoltReceiveGuard.IsReady)
                {
                    var channel = typeof(EntityChannel.CommandChannel);
                    var parameters = new[] { typeof(Packet) };
                    harmony.Patch(Target(channel, nameof(EntityChannel.CommandChannel.Read), parameters),
                        prefix: Hook(nameof(BeforeChannelRead), HarmonyLib.Priority.Last));
                    harmony.Patch(Target(channel, nameof(EntityChannel.CommandChannel.ReadResult), parameters),
                        prefix: Hook(nameof(BeforeChannelSection), HarmonyLib.Priority.First));
                    harmony.Patch(Target(channel, nameof(EntityChannel.CommandChannel.ReadInput), parameters),
                        prefix: Hook(nameof(BeforeChannelSection), HarmonyLib.Priority.First));
                    Volatile.Write(ref _admissionInstalled, 1);
                }
                harmony.Patch(readInput, postfix: Hook(nameof(AfterReadInput), HarmonyLib.Priority.First));
                harmony.Patch(readResult, postfix: Hook(nameof(AfterReadResult), HarmonyLib.Priority.First));
                harmony.Patch(execute,
                    prefix: Hook(nameof(BeforeExecute), HarmonyLib.Priority.Last),
                    postfix: Hook(nameof(AfterExecute), HarmonyLib.Priority.First));
                SetState(BoltEventHookStatus.Installed,
                    "Command framework hooks registered. Predecode channel rejection requires the receive guard and disconnects; no per-command denial. The current game has no concrete Bolt.Command subclasses and does not route CommandChannel. Receipt is not admission; live traffic is unverified.");
            }
            catch (Exception error)
            {
                // Disable the entire group before rollback; residual hooks must remain inert.
                SetState(BoltEventHookStatus.Failed,
                    "Command framework registration failed; callbacks are inactive.");
                try { harmony?.UnpatchSelf(); }
                catch (Exception cleanup)
                {
                    SetState(BoltEventHookStatus.Failed,
                        "Command framework registration and rollback failed; callbacks are inactive, native patch state is uncertain.");
                    Warn(warning, "Command framework rollback failed: " + cleanup);
                }
                Warn(warning, "Command framework registration failed: " + error);
            }
        }

        private static MethodInfo Target(Type type, string name, Type[] parameters)
        {
            var method = AccessTools.DeclaredMethod(type, name, parameters)
                ?? throw new MissingMethodException(type.FullName, name);
            if (method.IsStatic || method.ReturnType != typeof(void))
                throw new InvalidOperationException("Unsupported command hook signature: " + method);
            return method;
        }

        private static HarmonyMethod Hook(string name, int priority) =>
            new(typeof(BoltCommands), name) { priority = priority };

        private static bool BeforeChannelRead(EntityChannel.CommandChannel __instance, Packet __0, bool __runOriginal)
        {
            if (!__runOriginal || BoltReceiveGuard.IsAborted(__0)) return false;
            var handlers = ChannelReceiving;
            if (handlers == null || !CanRejectAndDisconnect) return true;

            BoltConnection source = null;
            BoltCommandChannelRequest request = null;
            bool entered = false;
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer) return true;
                source = __instance?._cn;
                if (source == null || !BoltReceiveGuard.CanAbort(__0, source)) return true;
                if (_admittingChannel) return !BoltReceiveGuard.TryAbort(__0, source);
                _admittingChannel = entered = true;
                request = new BoltCommandChannelRequest(__instance, __0, source);
                if (!request.MatchesCurrentContext()) return !request.TryAbortReceive();
                foreach (var handler in Delegate.EnumerateInvocationList(handlers))
                {
                    handler(request);
                    if (request.IsRejected) return false;
                    if (!request.MatchesCurrentContext())
                        return !request.TryAbortReceive();
                }
                return true;
            }
            catch (Exception error)
            {
                ReportAdmissionFailure(error);
                if (request?.IsRejected == true) return false;
                // Never leave a live packet's cursor at an unread channel without an accepted abort.
                if (request != null) return !request.TryAbortReceive();
                return source == null || !BoltReceiveGuard.TryAbort(__0, source);
            }
            finally
            {
                request?.Close();
                if (entered) _admittingChannel = false;
            }
        }

        private static bool BeforeChannelSection(Packet __0, bool __runOriginal) =>
            __runOriginal && !BoltReceiveGuard.IsAborted(__0);

        // Normal-return postfixes only: native exceptions propagate and never become observations.
        // The packet parameter is deliberately not bound or inspected by these hooks.
        private static void AfterReadInput(Command __instance, BoltConnection __0, bool __runOriginal) =>
            ObserveReceived(__instance, __0, BoltCommandReadKind.Input, __runOriginal);

        private static void AfterReadResult(Command __instance, BoltConnection __0, bool __runOriginal) =>
            ObserveReceived(__instance, __0, BoltCommandReadKind.Result, __runOriginal);

        private static void ObserveReceived(Command command, BoltConnection connection,
            BoltCommandReadKind kind, bool ranOriginal)
        {
            var handlers = Received;
            if (handlers == null || _receiving || !ranOriginal
                || State.Status != BoltEventHookStatus.Installed) return;

            _receiving = true;
            try
            {
                var observation = new BoltCommandReceived(kind, connection?.ConnectionId, Capture(command));
                foreach (var handler in Delegate.EnumerateInvocationList(handlers))
                {
                    try { handler(observation); }
                    catch (Exception error) { ReportFailure(error); }
                }
            }
            catch (Exception error) { ReportFailure(error); }
            finally { _receiving = false; }
        }

        private static void BeforeExecute(Command __0, bool __runOriginal, out ExecutionObservation __state)
        {
            __state = default;
            var handlers = Executed;
            if (handlers == null || _executing || !__runOriginal
                || State.Status != BoltEventHookStatus.Installed) return;

            _executing = true;
            try
            {
                __state = new ExecutionObservation
                {
                    IsFirstExecutionBeforeCall = __0.IsFirstExecution,
                    Handlers = handlers
                };
            }
            catch (Exception error) { ReportFailure(error); }
            finally { _executing = false; }
        }

        private static void AfterExecute(Entity __instance, Command __0, bool __1,
            bool __runOriginal, ExecutionObservation __state)
        {
            if (__state.Handlers == null || Executed == null || _executing || !__runOriginal
                || State.Status != BoltEventHookStatus.Installed) return;

            _executing = true;
            try
            {
                var observation = new BoltCommandExecuted(Capture(__0), __1,
                    __state.IsFirstExecutionBeforeCall, __instance.NetworkId.PackedValue,
                    __instance.Controller?.ConnectionId, __instance.Source?.ConnectionId);
                foreach (var handler in Delegate.EnumerateInvocationList(__state.Handlers))
                {
                    try { handler(observation); }
                    catch (Exception error) { ReportFailure(error); }
                }
            }
            catch (Exception error) { ReportFailure(error); }
            finally { _executing = false; }
        }

        private static BoltCommandSnapshot Capture(Command command) =>
            new(command.Meta.TypeId.Value, command.Sequence, command._ServerFrame_k__BackingField);

        private static void SetState(BoltEventHookStatus status, string detail)
        {
            var state = new BoltCommandHookState(status, detail);
            Volatile.Write(ref _state, state);
            try { Modding.SetService(ServiceKey, state); }
            catch (Exception error) { ReportFailure(error); }
        }

        private static void Warn(Action<string> warning, string message)
        {
            try { warning?.Invoke(message); }
            catch (Exception) { }
        }

        private static void ReportFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0) return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Command observation failed ("
                    + error.GetType().FullName + "). Native work is unchanged; further observation failures will not be logged.");
            }
            catch (Exception) { }
        }

        private static void ReportAdmissionFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedAdmissionFailure, 1) != 0) return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Command channel admission failed ("
                    + error.GetType().FullName + "). Rejection requires an accepted receive-guard abort; further admission failures will not be logged.");
            }
            catch (Exception) { }
        }

        private struct ExecutionObservation
        {
            internal Action<BoltCommandExecuted> Handlers;
            internal bool IsFirstExecutionBeforeCall;
        }
    }

    /// <summary>Hook registration only; Installed does not establish live command delivery.</summary>
    public sealed record BoltCommandHookState(BoltEventHookStatus Status, string Detail);

    /// <summary>
    /// Callback-scoped command-channel request, before any command decode or queue change. The
    /// sender is the actual channel connection, not an entity's source/controller or a wire token.
    /// A channel may contain multiple target entities, so this request intentionally has no entity
    /// identity. Rejection terminates the connection; it does not acknowledge or synthesize results.
    /// </summary>
    public sealed class BoltCommandChannelRequest
    {
        private EntityChannel.CommandChannel _channel;
        private Packet _packet;
        private BoltConnection _source;
        private UdpKit.UdpPacket _transportPacket;
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly int _position;
        private readonly int _size;
        private readonly IntPtr _data;
        private bool _sessionResolved;
        private ServerSession _session;

        internal BoltCommandChannelRequest(EntityChannel.CommandChannel channel, Packet packet,
            BoltConnection source)
        {
            _channel = channel;
            _packet = packet;
            _source = source;
            _transportPacket = packet.UdpPacket ?? throw new InvalidOperationException("Missing command-channel packet.");
            _position = _transportPacket.Position;
            _size = _transportPacket.Size;
            _data = _transportPacket.Data?.Pointer ?? IntPtr.Zero;
            SourceConnectionId = source.ConnectionId;
        }

        /// <summary>Transport ID copied from the exact sender; not an authenticated player identity.</summary>
        public uint SourceConnectionId { get; }

        /// <summary>True only after the shared guard accepted abort and deferred disconnect.</summary>
        public bool IsRejected { get; private set; }

        /// <summary>
        /// Abort this channel and the rest of the packet, then disconnect after native packet cleanup.
        /// False means no rejection was accepted. Earlier channels are not rolled back. This method
        /// is valid only synchronously inside ChannelReceiving, on the receiving thread.
        /// </summary>
        public bool RejectAndDisconnect()
        {
            if (!IsOpen) return false;
            if (IsRejected) return true;
            if (!MatchesCurrentContext()) return false;
            IsRejected = TryAbortReceive();
            return IsRejected;
        }

        /// <summary>Optional current sender-session lookup; no roster work occurs unless requested.</summary>
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

        private bool IsOpen => _channel != null && _packet != null && _source != null && _transportPacket != null
            && Environment.CurrentManagedThreadId == _thread;

        internal bool MatchesCurrentContext()
        {
            if (!IsOpen || IsRejected || !BoltNetwork.isRunning || !BoltNetwork.isServer
                || _channel.WasCollected || _source.WasCollected || _packet.WasCollected || _packet.Pooled
                || _transportPacket.WasCollected || _transportPacket.IsPooled
                || _packet.UdpPacket?.Pointer != _transportPacket.Pointer
                || _transportPacket.Position != _position || _transportPacket.Size != _size
                || _transportPacket.Data?.Pointer != _data || _data == IntPtr.Zero
                || _channel._cn?.Pointer != _source.Pointer || _source.ConnectionId != SourceConnectionId
                || !BoltCommands.CanRejectAndDisconnect
                || BoltReceiveGuard.IsAborted(_packet)
                || !BoltReceiveGuard.CanAbort(_transportPacket, _source)
                || !BoltReceiveGuard.IsCurrent(_transportPacket, _source)) return false;
            return _session == null || (_session.IsCurrent
                && ReferenceEquals(_session, ServerSessions.ResolveSource(_source)));
        }

        internal bool TryAbortReceive() => IsOpen && BoltReceiveGuard.TryAbort(_transportPacket, _source);

        internal void Close() { _channel = null; _packet = null; _source = null; _transportPacket = null; _session = null; }
    }

    public enum BoltCommandReadKind { Input, Result }

    /// <summary>
    /// Copied metadata, not command payload. Sequence is not a unique command/lifetime identifier.
    /// No packet, token, storage, command, entity, or connection wrappers are retained.
    /// </summary>
    public sealed record BoltCommandSnapshot(int TypeId, ushort Sequence, int ServerFrame);

    /// <summary>
    /// Full deserializer return, not native admission. ConnectionId is copied from the exact read
    /// connection; IDs may be reused and are not authenticated player identities. Entity is unknown.
    /// Neither an input nor a result observation guarantees subsequent execution or acceptance.
    /// </summary>
    public sealed record BoltCommandReceived(BoltCommandReadKind Kind,
        uint? ConnectionId, BoltCommandSnapshot Command);

    /// <summary>
    /// Normal execution return, not remote-only or proof that any game behavior ran. ResetState is
    /// the native argument; IsFirstExecutionBeforeCall is sampled before execution updates flags.
    /// Other fields are sampled after execution. Replays can report the same command repeatedly.
    /// Current controller/source IDs are not the original sender; no receipt association is inferred.
    /// </summary>
    public sealed record BoltCommandExecuted(BoltCommandSnapshot Command,
        bool ResetState, bool IsFirstExecutionBeforeCall, ulong EntityNetworkId,
        uint? CurrentControllerConnectionId, uint? CurrentSourceConnectionId);
}
