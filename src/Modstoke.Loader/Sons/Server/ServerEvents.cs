// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;
using Bolt;
using Il2CppInterop.Runtime;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    /// <summary>Optional server policy for every event reaching Bolt's dispatch boundary.</summary>
    public static class ServerEvents
    {
        public const string ServiceKey = "openacai.sons.serverEvents";
        private const int MaxCachedClasses = 1024;
        private static readonly object HandlerLock = new();
        private static Action<BoltEventRequest>[] _handlers = Array.Empty<Action<BoltEventRequest>>();
        private static readonly Dictionary<IntPtr, EventClass> Classes = new();
        private static readonly Dictionary<string, BoltEventSchema> Schemas = new(StringComparer.Ordinal);
        private static IReadOnlyList<BoltEventSchema> _catalog = Array.Empty<BoltEventSchema>();
        private static BoltEventHookState _state = new(BoltEventHookStatus.NotAttempted,
            "The common event policy has not been registered.");
        private static int _installationAttempted;
        private static int _ownerThread;
        private static int _reportedFailure;

        /// <summary>
        /// Synchronous pre-routing decisions on the server. No subscribers means no additional reads
        /// or restrictions. Rejection and handler exceptions stop this dispatch, not prior prediction.
        /// Unsubscribe on mod unload. A transport sender is not proof of original authorship or honesty.
        /// </summary>
        public static event Action<BoltEventRequest> Receiving
        {
            add
            {
                if (value == null) return;
                lock (HandlerLock)
                {
                    Action<BoltEventRequest> combined = null;
                    foreach (var handler in _handlers) combined += handler;
                    combined += value;
                    PublishHandlers(combined);
                }
            }
            remove
            {
                if (value == null) return;
                lock (HandlerLock)
                {
                    Action<BoltEventRequest> combined = null;
                    foreach (var handler in _handlers) combined += handler;
                    combined -= value;
                    PublishHandlers(combined);
                }
            }
        }

        public static BoltEventHookState State => Volatile.Read(ref _state);

        /// <summary>
        /// Event schemas in the installed generated game assembly, not an inventory of live factories
        /// or proof of native handlers. Unknown custom event classes still reach Receiving.
        /// </summary>
        public static IReadOnlyList<BoltEventSchema> KnownEventSchemas => Volatile.Read(ref _catalog);

        private static void PublishHandlers(Action<BoltEventRequest> combined)
        {
            var invocation = combined?.GetInvocationList();
            var next = invocation == null ? Array.Empty<Action<BoltEventRequest>>()
                : new Action<BoltEventRequest>[invocation.Length];
            for (int index = 0; index < next.Length; index++)
                next[index] = (Action<BoltEventRequest>)invocation[index];
            Volatile.Write(ref _handlers, next);
        }

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0) return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                SetState(BoltEventHookStatus.Disabled,
                    "The common event policy requires Harmony support and a recognized game build.");
                return;
            }
            try
            {
                if (!GameBuild.SupportsServerCore)
                {
                    SetState(BoltEventHookStatus.Disabled, "The common event policy is not qualified for this native game build.");
                    return;
                }
                _ownerThread = Environment.CurrentManagedThreadId;
                var catalog = new List<BoltEventSchema>();
                foreach (var type in typeof(HitPlayer).Assembly.GetTypes())
                {
                    if (type.IsAbstract || !type.IsSubclassOf(typeof(Bolt.Event))) continue;
                    var schema = BoltEventSchema.Create(type);
                    Schemas.Add(schema.Name, schema);
                    catalog.Add(schema);
                }
                catalog.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
                Volatile.Write(ref _catalog, catalog.AsReadOnly());
                ServerDispatchGate.Register(DispatchRoute.Events);
                SetState(BoltEventHookStatus.Installed,
                    "Common pre-routing policy registered; native delivery and rejection require multiplayer qualification.");
            }
            catch (Exception error)
            {
                SetState(BoltEventHookStatus.Failed, "Common event policy registration failed; callbacks are inactive.");
                ServerDispatchGate.Remove(DispatchRoute.Events);
                warning?.Invoke("Common event policy registration failed: " + error);
            }
        }

        /// <summary>
        /// First route of the shared dispatch gate. A rejection keeps storage in the returned scope,
        /// which the gate finalizer releases after the remaining prefixes and the skipped original.
        /// </summary>
        internal static bool Route(Bolt.Event source, out ServerDispatchGate.DispatchScope scope)
        {
            scope = null;
            var handlers = Volatile.Read(ref _handlers);
            if (handlers.Length == 0 || State.Status != BoltEventHookStatus.Installed)
                return true;
            BoltEventRequest request = null;
            try
            {
                // Policy is server-only; clients never take ownership of an event here.
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer) return true;
                // The outer call retains storage until the finalizer, even if a callback re-enters Dispatch.
                scope = ServerDispatchGate.Retain(source);
                if (scope == null) return false;
                if (Environment.CurrentManagedThreadId != _ownerThread)
                    throw new InvalidOperationException("Common event policy called outside the game thread.");
                var nativeClass = IL2CPP.il2cpp_object_get_class(source.Pointer);
                if (!Classes.TryGetValue(nativeClass, out var descriptor))
                {
                    string name = source.GetIl2CppType().FullName;
                    Schemas.TryGetValue(name, out var schema);
                    if (schema != null && !schema.MatchesNativeClass(nativeClass)) schema = null;
                    descriptor = new EventClass(name, schema);
                    if (Classes.Count < MaxCachedClasses) Classes.Add(nativeClass, descriptor);
                }
                request = new BoltEventRequest(source, descriptor.Name, descriptor.Schema);
                foreach (var handler in handlers)
                {
                    handler(request);
                    if (request.IsRejected) return false;
                }
                // A callback can disconnect a sender or stop the server. Never apply its decision
                // to a replacement connection, a different route, or a reused native event.
                if (!request.MatchesCurrentContext()) return false;
                scope.Rejected = false;
                return true;
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return false;
            }
            finally { request?.Close(); }
        }

        private static void SetState(BoltEventHookStatus status, string detail)
        {
            var state = new BoltEventHookState(status, detail);
            Volatile.Write(ref _state, state);
            Modding.SetService(ServiceKey, state);
        }

        internal static void ReportFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0) return;
            try { RuntimeEntryPoint.LogSource?.LogWarning("Common event policy could not complete ("
                + error.GetType().FullName + "). Further failures will not be logged."); }
            catch (Exception) { }
        }

        private sealed record EventClass(string Name, BoltEventSchema Schema);
    }

    public enum BoltEventHookStatus { NotAttempted, Disabled, Installed, Failed }

    public sealed record BoltEventHookState(BoltEventHookStatus Status, string Detail)
    {
        public bool CoversEventDispatch => Status == BoltEventHookStatus.Installed;
        public bool CoversPacketDeserialization => false;
        public bool CoversEntityReplication => false;
        public bool CoversCommandReplication => false;
        public bool CoversBlobTransfers => false;
        public bool ProvesClientIntegrity => false;
    }

    public enum BoltEventOrigin { ServerLocal, RemoteConnection }

    /// <summary>
    /// Copied routing context and callback-scoped payload access. All payload fields are claims,
    /// not validated possession, health, authority or proof that a requested operation completed.
    /// </summary>
    public sealed class BoltEventRequest
    {
        private Bolt.Event _source;
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly IntPtr _sourcePointer;
        private readonly IntPtr _storage;
        private readonly IntPtr _meta;
        private readonly IntPtr _sender;
        private readonly IntPtr _targetEntity;
        private readonly IntPtr _targetConnection;

        internal BoltEventRequest(Bolt.Event source, string name, BoltEventSchema schema)
        {
            _source = source;
            _sourcePointer = source.Pointer;
            _storage = source.storage?.Pointer ?? IntPtr.Zero;
            var meta = source.Meta ?? throw new InvalidOperationException("An event has no protocol metadata.");
            _meta = meta.Pointer;
            EventTypeId = meta.TypeId.Value;
            EventName = name;
            Schema = schema;
            RoutingTargets = source.Targets;
            Sequence = source.Sequence;
            Reliability = (int)source.Reliability;
            var sender = source.RaisedBy;
            _sender = sender?.Pointer ?? IntPtr.Zero;
            SourceConnectionId = sender?.ConnectionId;
            Origin = sender == null ? BoltEventOrigin.ServerLocal : BoltEventOrigin.RemoteConnection;
            SourceSession = ServerSessions.ResolveSource(sender);
            var targetEntity = source.TargetEntity;
            _targetEntity = targetEntity?.Pointer ?? IntPtr.Zero;
            RoutedEntityNetworkId = targetEntity?.NetworkId.PackedValue;
            var targetConnection = source.TargetConnection;
            _targetConnection = targetConnection?.Pointer ?? IntPtr.Zero;
            RoutedConnectionId = targetConnection?.ConnectionId;
        }

        public string EventName { get; }
        public int EventTypeId { get; }
        public uint Sequence { get; }
        public int Reliability { get; }
        public int RoutingTargets { get; }
        public BoltEventOrigin Origin { get; }
        public uint? SourceConnectionId { get; }
        public ServerSession SourceSession { get; }
        public ulong? RoutedEntityNetworkId { get; }
        public uint? RoutedConnectionId { get; }
        public BoltEventSchema Schema { get; }
        public bool IsRejected { get; private set; }

        /// <summary>Returns false once this callback has ended or from another thread.</summary>
        public bool Reject()
        {
            if (_source == null || Environment.CurrentManagedThreadId != _thread) return false;
            IsRejected = true;
            return true;
        }

        /// <summary>
        /// Reads only the requested supported field. The value is a managed copy, never a native
        /// event/entity/token. False is unavailable, not zero. Access expires at callback completion.
        /// </summary>
        public bool TryReadField(string name, out object value, out BoltFieldReadStatus status)
        {
            value = null;
            status = BoltFieldReadStatus.CallbackClosed;
            if (_source == null) return false;
            status = BoltFieldReadStatus.WrongThread;
            if (Environment.CurrentManagedThreadId != _thread) return false;
            status = BoltFieldReadStatus.UnknownEventType;
            if (Schema == null) return false;
            try
            {
                status = BoltFieldReadStatus.ContextChanged;
                // Routing and session are revalidated once after all callbacks. Field reads only
                // need the retained payload lifetime, avoiding a roster scan per requested value.
                if (_source.Pointer != _sourcePointer || (_source.storage?.Pointer ?? IntPtr.Zero) != _storage
                    || (_source.Meta?.Pointer ?? IntPtr.Zero) != _meta) return false;
                return Schema.TryRead(_source.Pointer, name, out value, out status);
            }
            catch (Exception)
            {
                status = BoltFieldReadStatus.Failed;
                return false;
            }
        }

        internal bool MatchesCurrentContext()
        {
            if (_source == null || !BoltNetwork.isRunning || !BoltNetwork.isServer) return false;
            var sender = _source.RaisedBy;
            var targetEntity = _source.TargetEntity;
            var targetConnection = _source.TargetConnection;
            var meta = _source.Meta;
            return _source.Pointer == _sourcePointer && (_source.storage?.Pointer ?? IntPtr.Zero) == _storage
                && meta != null && meta.Pointer == _meta && meta.TypeId.Value == EventTypeId
                && _source.Targets == RoutingTargets && _source.Sequence == Sequence && (int)_source.Reliability == Reliability
                && (sender?.Pointer ?? IntPtr.Zero) == _sender && sender?.ConnectionId == SourceConnectionId
                && (targetEntity?.Pointer ?? IntPtr.Zero) == _targetEntity && targetEntity?.NetworkId.PackedValue == RoutedEntityNetworkId
                && (targetConnection?.Pointer ?? IntPtr.Zero) == _targetConnection && targetConnection?.ConnectionId == RoutedConnectionId
                && (SourceSession == null || (SourceSession.IsCurrent
                    && ReferenceEquals(SourceSession, ServerSessions.ResolveSource(sender))));
        }

        internal void Close() => _source = null;
    }
}
