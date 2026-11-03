// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Bolt;
using Endnight.Extensions;
using HarmonyLib;
using Modstoke.Loader;
using global::Sons.Multiplayer;

namespace Modstoke.Sons
{
    /// <summary>Observed server-side remote-client lifetimes, including clients without Modstoke.</summary>
    public static partial class ServerSessions
    {
        public const string ServiceKey = "openacai.sons.serverSessions";

        /// <summary>SDK limit for a native disconnect localization key, measured in UTF-8 bytes.</summary>
        public const int MaxDisconnectMessageKeyBytes = 256;

        private const int MaxRecoveries = 3;
        private const int RecoveryIdle = 0;
        private const int RecoveryWaiting = 1;
        private const int RecoveryReady = 2;

        private static readonly UTF8Encoding DisconnectMessageKeyEncoding = new(false, true);
        private static readonly Dictionary<IntPtr, ServerSession> Current = new();
        private static readonly HashSet<IntPtr> Tombstones = new();
        private static readonly HashSet<ServerSession> DisconnectAttempts = new();
        private static ServerSessionState _state = new(ServerSessionHookStatus.NotAttempted,
            "Server session hooks have not been installed.");
        private static IReadOnlyList<ServerSession> _sessions = Array.Empty<ServerSession>();
        private static Queue<PendingEvent> _pending = new();
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedFailure;
        private static int _reportedExhaustion;
        private static int _reportedDisconnectFailure;
        private static int _failures;
        private static int _recovery;
        private static bool _patched;
        private static bool _hooksInstalled;
        private static bool _recovering;
        private static bool _shutdown;
        private static bool _ticking;
        private static long _nextRefresh;

        /// <summary>
        /// All hooks were installed and tracking is currently active. A failure suspends tracking until it
        /// recovers, so read this when deciding rather than caching it.
        /// </summary>
        public static bool HooksInstalled => Volatile.Read(ref _hooksInstalled);

        /// <summary>Hook registration and recovery state, also published under <see cref="ServiceKey"/>.</summary>
        public static ServerSessionState State => Volatile.Read(ref _state);

        /// <summary>Immutable membership snapshot of last-observed remote clients, not real-time network liveness.</summary>
        public static IReadOnlyList<ServerSession> Sessions => HooksInstalled
            ? Volatile.Read(ref _sessions) : Array.Empty<ServerSession>();

        /// <summary>Queued observed opening, not a native delivery timestamp. The handle may already be closed.</summary>
        public static event Action<ServerSession> Opened;
        /// <summary>Queued observed closing; no native disconnect reason is inferred.</summary>
        public static event Action<ServerSession> Closed;
        /// <summary>Queued metadata change. The handle exposes the latest observed registration.</summary>
        public static event Action<ServerSession> RegistrationChanged;

        /// <summary>
        /// Requests a native disconnect for this exact live session on the game thread.
        /// Requested means the native call returned, not that the client has disconnected.
        /// </summary>
        public static DisconnectResult Disconnect(ServerSession session) =>
            DisconnectCore(session, null);

        /// <summary>
        /// Requests the native kicked-from-game notice. The message is a Multiplayer localization key, not literal text.
        /// This does not ban the player, change their role or confirm client receipt or presentation.
        /// </summary>
        public static DisconnectResult DisconnectWithNotice(ServerSession session,
            string messageKey = "HOST_KICKED_YOU") =>
            IsDisconnectMessageKeyValid(messageKey) ? DisconnectCore(session, messageKey) : DisconnectResult.InvalidMessageKey;

        internal static bool IsOwnerThread => Environment.CurrentManagedThreadId == _ownerThreadId;

        internal static bool HasDisconnectAttempt(ServerSession session) => session != null
            && Environment.CurrentManagedThreadId == _ownerThreadId && DisconnectAttempts.Contains(session);

        private static DisconnectResult DisconnectCore(ServerSession session, string messageKey)
        {
            if (!HooksInstalled)
                return DisconnectResult.Unavailable;
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                return DisconnectResult.WrongThread;

            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                    return DisconnectResult.NotServer;
                var connection = ResolveCurrent(session);
                if (connection == null)
                    return HooksInstalled ? DisconnectResult.SessionUnavailable : DisconnectResult.Unavailable;
                if (DisconnectAttempts.Contains(session))
                    return DisconnectResult.AlreadyAttempted;

                IProtocolToken token = null;
                if (messageKey != null)
                    token = new CoopKickToken { Banned = false, KickMessage = messageKey }.Cast<IProtocolToken>();

                // Reserve before invoking native code: an exception does not prove that nothing was queued.
                if (!DisconnectAttempts.Add(session))
                    return DisconnectResult.AlreadyAttempted;
                if (messageKey == null)
                    connection.Disconnect();
                else
                    connection.Disconnect(token);
                return DisconnectResult.Requested;
            }
            catch (Exception error)
            {
                if (Interlocked.Exchange(ref _reportedDisconnectFailure, 1) == 0)
                {
                    try
                    {
                        RuntimeEntryPoint.LogSource?.LogWarning("Server disconnect request failed ("
                            + error.GetType().FullName + "). Further disconnect failures will not be logged.");
                    }
                    catch (Exception) { }
                }
                return DisconnectResult.Failed;
            }
        }

        private static bool IsDisconnectMessageKeyValid(string messageKey)
        {
            if (string.IsNullOrWhiteSpace(messageKey) || messageKey.Length > MaxDisconnectMessageKeyBytes)
                return false;
            foreach (var character in messageKey)
                if (char.IsControl(character) || character is '<' or '>' or '\u2028' or '\u2029')
                    return false;

            try { return DisconnectMessageKeyEncoding.GetByteCount(messageKey) <= MaxDisconnectMessageKeyBytes; }
            catch (EncoderFallbackException) { return false; }
        }

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;

            _ownerThreadId = Environment.CurrentManagedThreadId;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                PublishState(ServerSessionHookStatus.Disabled, support?.Detail ?? "Harmony support is unavailable.");
                warning?.Invoke("Optional server session tracking was not installed: Harmony support is unavailable.");
                return;
            }

            PublishState(ServerSessionHookStatus.Installing, "Registering server session hooks.");
            try
            {
                var connected = AccessTools.Method(typeof(BoltInternal.GlobalEventListenerBase), "ConnectedInvoke", new[] { typeof(BoltConnection) });
                var disconnected = AccessTools.Method(typeof(BoltInternal.GlobalEventListenerBase), "DisconnectedInvoke", new[] { typeof(BoltConnection) });
                var start = AccessTools.Method(typeof(BoltCore), "BeginStart", new[] { typeof(Bolt.ControlCommandStart) });
                var shutdown = AccessTools.Method(typeof(BoltCore), "BeginShutdown", new[] { typeof(Bolt.ControlCommandShutdown) });
                var immediate = AccessTools.Method(typeof(BoltCore), "ShutdownImmediate", Type.EmptyTypes);
                var update = AccessTools.Method(typeof(BoltPoll), nameof(BoltPoll.Update), Type.EmptyTypes);
                if (connected == null || disconnected == null || start == null || shutdown == null || immediate == null || update == null)
                    throw new MissingMethodException();

                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-server-sessions");
                harmony.Patch(connected, prefix: Prefix(nameof(BeforeConnected)));
                harmony.Patch(disconnected, prefix: Prefix(nameof(BeforeDisconnected)));
                harmony.Patch(start, prefix: Prefix(nameof(BeforeStart)));
                harmony.Patch(shutdown, prefix: Prefix(nameof(BeforeShutdown)));
                harmony.Patch(immediate, prefix: Prefix(nameof(BeforeShutdown)));
                harmony.Patch(update, postfix: new HarmonyMethod(typeof(ServerSessions), nameof(Tick)));
            }
            catch (Exception error)
            {
                // Partial native patches remain installed, but their callbacks cannot activate tracking.
                PublishState(ServerSessionHookStatus.Failed, "Server session hook registration failed; tracking stays inactive.");
                warning?.Invoke("Optional server session tracking is disabled (" + error.GetType().FullName + ").");
                return;
            }
            Volatile.Write(ref _patched, true);
            Volatile.Write(ref _hooksInstalled, true);
            PublishState(ServerSessionHookStatus.Installed, "Server session tracking is active.");
        }

        private static HarmonyMethod Prefix(string name) => new(typeof(ServerSessions), name)
        {
            priority = HarmonyLib.Priority.First
        };

        internal static ServerSession ResolveSource(BoltConnection connection)
        {
            if (!HooksInstalled || Environment.CurrentManagedThreadId != _ownerThreadId)
                return null;
            try
            {
                if (!CanObserve() || connection == null || connection.WasCollected)
                    return null;
                var pointer = connection.Pointer;
                var id = connection.ConnectionId;
                if (Tombstones.Contains(pointer))
                    return null;

                var found = FindClient(pointer, id);
                if (found == null)
                {
                    if (Current.TryGetValue(pointer, out var departed) && departed.ConnectionId == id)
                    {
                        Retire(pointer);
                        Publish();
                    }
                    return null;
                }

                var observation = ReadClient(found);
                var membershipChanged = !Current.TryGetValue(pointer, out var previous);
                if (previous != null && previous.ConnectionId != id)
                {
                    Retire(pointer);
                    membershipChanged = true;
                }
                Observe(observation);
                if (membershipChanged)
                    Publish();
                return HooksInstalled && Current.TryGetValue(pointer, out var session) && session.ConnectionId == id
                    ? session : null;
            }
            catch (Exception error)
            {
                Disable(error);
                return null;
            }
        }

        // Receive policies already validate the exact live transport. Use only observed membership
        // here; unlike ResolveSource this never scans clients or creates/revives a session.
        internal static ServerSession ResolveObserved(BoltConnection connection)
        {
            if (!HooksInstalled || Environment.CurrentManagedThreadId != _ownerThreadId
                || connection == null || connection.WasCollected || !CanObserve()) return null;
            var pointer = connection.Pointer;
            return !Tombstones.Contains(pointer) && Current.TryGetValue(pointer, out var session)
                && session.IsCurrent && session.ConnectionId == connection.ConnectionId ? session : null;
        }

        internal static BoltConnection ResolveCurrent(ServerSession session)
        {
            if (!HooksInstalled || Environment.CurrentManagedThreadId != _ownerThreadId || session == null)
                return null;
            try
            {
                if (!CanObserve() || !session.IsCurrent)
                    return null;

                var pointer = session.ConnectionPointer;
                if (pointer == IntPtr.Zero || Tombstones.Contains(pointer)
                    || !Current.TryGetValue(pointer, out var current) || !ReferenceEquals(current, session))
                    return null;

                var connection = FindClient(pointer, session.ConnectionId);
                if (connection == null)
                {
                    Retire(pointer);
                    Publish();
                    return null;
                }
                return HooksInstalled && session.IsCurrent ? connection : null;
            }
            catch (Exception error)
            {
                Disable(error);
                return null;
            }
        }

        // Tracking failed but resumes after a clean frame, so open handles are paused rather than departed.
        internal static bool Recovering => !HooksInstalled && Volatile.Read(ref _patched)
            && Volatile.Read(ref _failures) <= MaxRecoveries;

        // While tracking recovers, an open handle whose exact connection is still in the native list stays
        // usable to the control transport. A disconnect observed meanwhile still wins. Held means the
        // native read itself failed, so the caller can neither keep nor drop the connection yet.
        internal static BoltConnection ResolveRecovering(ServerSession session, out bool held)
        {
            held = false;
            if (!Recovering || Environment.CurrentManagedThreadId != _ownerThreadId
                || session == null || session.ClosedAt != null)
                return null;
            try
            {
                if (_shutdown || !BoltNetwork.isRunning || !BoltNetwork.isServer)
                    return null;
                var pointer = session.ConnectionPointer;
                if (pointer == IntPtr.Zero || Tombstones.Contains(pointer)
                    || !Current.TryGetValue(pointer, out var current) || !ReferenceEquals(current, session))
                    return null;
                return FindClient(pointer, session.ConnectionId);
            }
            catch (Exception)
            {
                // Tracking's own reads decide whether it recovers; this one only holds the peer.
                held = true;
                return null;
            }
        }

        private static BoltConnection FindClient(IntPtr pointer, uint connectionId)
        {
            // BoltNetwork.clients filters this live list by the transport's server-side role.
            var connections = BoltCore._connections;
            var count = connections.count;
            if (count == 0) return null;
            var candidate = connections.first;
            for (var i = 0; i < count; i++)
            {
                if (candidate != null && !candidate.WasCollected && candidate._udp.IsServer
                    && candidate.Pointer == pointer && candidate.ConnectionId == connectionId)
                    return candidate;
                if (i + 1 < count) candidate = connections.Next(candidate);
            }
            return null;
        }

        private static bool EnterCallback()
        {
            if (!HooksInstalled)
                return false;
            if (Environment.CurrentManagedThreadId == _ownerThreadId)
                return true;
            Disable(new InvalidOperationException());
            return false;
        }

        // Suspended tracking still follows native connect and disconnect callbacks, so recovery
        // cannot revive a connection that departed in the meantime.
        private static void TrackSuspended(BoltConnection connection, bool disconnected)
        {
            if (!Volatile.Read(ref _patched) || Environment.CurrentManagedThreadId != _ownerThreadId)
                return;
            try
            {
                if (connection == null || connection.WasCollected)
                    return;
                if (disconnected)
                    Tombstones.Add(connection.Pointer);
                else
                    Tombstones.Remove(connection.Pointer);
            }
            catch (Exception error) { Disable(error); }
        }

        private static void BeforeConnected(BoltConnection __0)
        {
            if (!HooksInstalled)
            {
                TrackSuspended(__0, false);
                return;
            }
            if (!EnterCallback())
                return;
            try
            {
                if (__0 == null || __0.WasCollected)
                    return;
                var pointer = __0.Pointer;
                Retire(pointer);
                Tombstones.Remove(pointer);
                if (CanObserve())
                {
                    var id = __0.ConnectionId;
                    var connection = FindClient(pointer, id);
                    if (connection != null)
                        Observe(ReadClient(connection));
                }
                Publish();
            }
            catch (Exception error) { Disable(error); }
        }

        private static void BeforeDisconnected(BoltConnection __0)
        {
            if (!HooksInstalled)
            {
                TrackSuspended(__0, true);
                return;
            }
            if (!EnterCallback())
                return;
            try
            {
                if (__0 == null || __0.WasCollected)
                    return;
                var pointer = __0.Pointer;
                // Native callbacks run while this connection is still in BoltNetwork.clients.
                Tombstones.Add(pointer);
                Retire(pointer);
                Publish();
            }
            catch (Exception error) { Disable(error); }
        }

        private static void BeforeStart()
        {
            if (!HooksInstalled)
            {
                Restart();
                return;
            }
            if (!EnterCallback())
                return;
            try
            {
                // Bolt rejects duplicate starts without ending the existing lifetimes.
                if (BoltNetwork.isRunning)
                    return;
                RetireAll();
                Tombstones.Clear();
                _shutdown = false;
                _nextRefresh = 0;
                Volatile.Write(ref _failures, 0);
            }
            catch (Exception error) { Disable(error); }
        }

        // A fresh Bolt start is a clean world: it ends a suspension even after the retry budget ran out.
        private static void Restart()
        {
            if (!Volatile.Read(ref _patched) || Environment.CurrentManagedThreadId != _ownerThreadId)
                return;
            try
            {
                if (BoltNetwork.isRunning)
                    return;
                _shutdown = false;
                Volatile.Write(ref _failures, 0);
                Rearm(true, "Server session tracking resumed at server start.");
            }
            catch (Exception error) { Disable(error); }
        }

        private static void BeforeShutdown()
        {
            if (!HooksInstalled)
            {
                if (Volatile.Read(ref _patched) && Environment.CurrentManagedThreadId == _ownerThreadId)
                    _shutdown = true;
                return;
            }
            if (!EnterCallback())
                return;
            try
            {
                _shutdown = true;
                RetireAll();
            }
            catch (Exception error) { Disable(error); }
        }

        private static void Tick()
        {
            if (!HooksInstalled && !Recover())
                return;
            if (!EnterCallback() || _ticking)
                return;
            _ticking = true;
            try
            {
                if (CanObserve() && Environment.TickCount64 >= _nextRefresh)
                {
                    _nextRefresh = Environment.TickCount64 + 1000;
                    RefreshRegistry(ReadClients());
                }

                if (Volatile.Read(ref _pending).Count == 0)
                    return;

                // Reentrant handlers append to a fresh queue and are delivered on a later tick.
                var batch = Interlocked.Exchange(ref _pending, new Queue<PendingEvent>());
                while (batch.Count > 0)
                {
                    if (!HooksInstalled)
                    {
                        // Keep the undelivered remainder for recovery, ahead of anything queued since.
                        foreach (var later in Volatile.Read(ref _pending))
                            batch.Enqueue(later);
                        Volatile.Write(ref _pending, batch);
                        break;
                    }
                    var item = batch.Dequeue();
                    switch (item.Kind)
                    {
                        case EventKind.Opened:
                            Modding.Dispatch("ServerSessions.Opened", Opened, item.Session);
                            break;
                        case EventKind.Closed:
                            Modding.Dispatch("ServerSessions.Closed", Closed, item.Session);
                            break;
                        case EventKind.RegistrationChanged:
                            Modding.Dispatch("ServerSessions.RegistrationChanged", RegistrationChanged, item.Session);
                            break;
                    }
                }
            }
            catch (Exception error) { Disable(error); }
            finally { _ticking = false; }
        }

        private static bool Recover()
        {
            if (!Volatile.Read(ref _patched) || Environment.CurrentManagedThreadId != _ownerThreadId
                || Volatile.Read(ref _failures) > MaxRecoveries)
                return false;
            // The failing frame may still be running callbacks; wait for one complete poll without a new failure.
            if (Interlocked.CompareExchange(ref _recovery, RecoveryReady, RecoveryWaiting) != RecoveryReady)
                return false;
            try { Rearm(false, "Server session tracking recovered after a failure."); }
            catch (Exception error) { Disable(error); }
            return HooksInstalled;
        }

        // Runs with tracking inactive, so off-thread callbacks return early instead of racing this rebuild.
        private static void Rearm(bool restart, string detail)
        {
            _recovering = true;
            try
            {
                if (restart || _shutdown || !BoltNetwork.isRunning || !BoltNetwork.isServer)
                {
                    RetireAll();
                    Tombstones.Clear();
                    _nextRefresh = 0;
                }
                else
                {
                    // Handles whose exact connection is still live stay valid; departed ones close as usual.
                    List<IntPtr> departed = null;
                    foreach (var pointer in Current.Keys)
                        if (Tombstones.Contains(pointer))
                            (departed ??= new List<IntPtr>()).Add(pointer);
                    if (departed != null)
                        foreach (var pointer in departed)
                            Retire(pointer);
                    RefreshRegistry(ReadClients());
                    _nextRefresh = Environment.TickCount64 + 1000;
                }
                Publish();
            }
            finally { _recovering = false; }
            Volatile.Write(ref _recovery, RecoveryIdle);
            Volatile.Write(ref _hooksInstalled, true);
            PublishState(ServerSessionHookStatus.Installed, detail);
        }

        private static bool CanObserve()
        {
            if (!HooksInstalled || _shutdown)
                return false;
            if (BoltNetwork.isRunning && BoltNetwork.isServer)
                return true;
            // Polled every frame while not hosting; only the first idle check has lifetimes to end.
            if (Current.Count > 0 || Tombstones.Count > 0 || DisconnectAttempts.Count > 0
                || Volatile.Read(ref _sessions).Count > 0)
            {
                RetireAll();
                Tombstones.Clear();
            }
            return false;
        }

        private static Dictionary<IntPtr, Observation> ReadClients()
        {
            var observations = new Dictionary<IntPtr, Observation>();
            var clients = BoltNetwork.clients.GetEnumerator();
            try
            {
                var iterator = clients.Cast<Il2CppSystem.Collections.IEnumerator>();
                while (iterator.MoveNext())
                {
                    var connection = clients.Current;
                    if (connection == null || connection.WasCollected) continue;
                    var observation = ReadClient(connection);
                    observations[observation.Pointer] = observation;
                }
            }
            finally { clients.Cast<Il2CppSystem.IDisposable>().Dispose(); }
            return observations;
        }

        private static Observation ReadClient(BoltConnection connection)
        {
            ulong? registeredSteamId = null;
            if (GameServerManager.GetRegisteredClient(connection, out var steamId) && steamId.m_SteamID != 0)
                registeredSteamId = steamId.m_SteamID;
            return new Observation(connection.Pointer, connection.ConnectionId, registeredSteamId);
        }

        private static void RefreshRegistry(Dictionary<IntPtr, Observation> live)
        {
            Tombstones.RemoveWhere(pointer => !live.ContainsKey(pointer));

            List<IntPtr> stale = null;
            foreach (var pair in Current)
            {
                if (!live.TryGetValue(pair.Key, out var observation) || pair.Value.ConnectionId != observation.ConnectionId)
                    (stale ??= new List<IntPtr>()).Add(pair.Key);
            }
            var membershipChanged = stale != null;
            if (stale != null)
                foreach (var pointer in stale)
                    Retire(pointer);
            var retainedCount = Current.Count;
            foreach (var observation in live.Values)
            {
                if (!Tombstones.Contains(observation.Pointer))
                    Observe(observation);
            }
            if (membershipChanged || Current.Count != retainedCount)
                Publish();
        }

        private static void Observe(Observation observation)
        {
            if (Current.TryGetValue(observation.Pointer, out var session))
            {
                if (session.RefreshRegistration(observation.RegisteredSteamId))
                    Enqueue(EventKind.RegistrationChanged, session);
                return;
            }
            session = new ServerSession(observation.Pointer, observation.ConnectionId, observation.RegisteredSteamId);
            Current.Add(observation.Pointer, session);
            Enqueue(EventKind.Opened, session);
        }

        private static void Retire(IntPtr pointer)
        {
            if (Current.Remove(pointer, out var session))
            {
                DisconnectAttempts.Remove(session);
                session.Close();
                ServerWorld.ForgetPlayerIdentity(session);
                Enqueue(EventKind.Closed, session);
            }
        }

        private static void RetireAll()
        {
            foreach (var session in Current.Values)
            {
                session.Close();
                Enqueue(EventKind.Closed, session);
            }
            Current.Clear();
            ServerWorld.ResetPlayerIdentities();
            DisconnectAttempts.Clear();
            Publish();
        }

        // Migration cohorts use snapshot identity to detect membership changes, even if the same
        // clients later return. Keep each publication distinct, including empty membership.
        private static void Publish() => Volatile.Write(ref _sessions,
            new List<ServerSession>(Current.Values).AsReadOnly());

        private static void Enqueue(EventKind kind, ServerSession session)
        {
            if (HooksInstalled || _recovering)
                Volatile.Read(ref _pending).Enqueue(new PendingEvent(kind, session));
        }

        private static void Disable(Exception error)
        {
            // May run off-thread: never touch the game-thread registry or individual handles here.
            // Undelivered notifications stay queued for delivery after recovery.
            Volatile.Write(ref _hooksInstalled, false);
            Volatile.Write(ref _sessions, Array.Empty<ServerSession>());
            Volatile.Write(ref _recovery, RecoveryWaiting);
            var exhausted = Interlocked.Increment(ref _failures) > MaxRecoveries;
            try
            {
                var name = error.GetType().FullName;
                if (exhausted)
                {
                    PublishState(ServerSessionHookStatus.Suspended,
                        "Server session tracking failed repeatedly (" + name + ") and resumes at the next server start.");
                    if (Interlocked.Exchange(ref _reportedExhaustion, 1) == 0)
                        RuntimeEntryPoint.LogSource?.LogWarning("Optional server session tracking failed repeatedly ("
                            + name + ") and stays suspended until the next server start.");
                    return;
                }
                PublishState(ServerSessionHookStatus.Recovering,
                    "Server session tracking failed (" + name + ") and resumes after a clean frame.");
                if (Interlocked.Exchange(ref _reportedFailure, 1) == 0)
                    RuntimeEntryPoint.LogSource?.LogWarning("Optional server session tracking is suspended (" + name
                        + ") and resumes after a clean frame. Later recoverable failures will not be logged.");
            }
            catch (Exception) { }
        }

        private static void PublishState(ServerSessionHookStatus status, string detail)
        {
            var state = new ServerSessionState(status, detail);
            Volatile.Write(ref _state, state);
            Modding.SetService(ServiceKey, state);
        }

        private enum EventKind { Opened, Closed, RegistrationChanged }
        private readonly record struct PendingEvent(EventKind Kind, ServerSession Session);
        private readonly record struct Observation(IntPtr Pointer, uint ConnectionId, ulong? RegisteredSteamId);
    }

    public enum ServerSessionHookStatus
    {
        NotAttempted,
        Disabled,
        Installing,
        Installed,
        /// <summary>Tracking failed and resumes after one clean frame, up to three times per server start.</summary>
        Recovering,
        /// <summary>Tracking failed repeatedly and resumes when the next server starts.</summary>
        Suspended,
        Failed
    }

    public sealed class ServerSessionState
    {
        internal ServerSessionState(ServerSessionHookStatus status, string detail)
        {
            Status = status;
            Detail = detail;
        }

        public ServerSessionHookStatus Status { get; }
        public string Detail { get; }
    }

    public enum DisconnectResult
    {
        Requested,
        Unavailable,
        WrongThread,
        NotServer,
        SessionUnavailable,
        AlreadyAttempted,
        Failed,
        /// <summary>The key is blank, malformed, multiline, contains markup delimiters or exceeds the SDK limit.</summary>
        InvalidMessageKey
    }

    /// <summary>
    /// A managed, reference-identity remote-client lifetime. Closed handles are never reactivated; an open
    /// handle is current again after tracking recovers if its exact connection is still live.
    /// </summary>
    public sealed class ServerSession
    {
        private Metadata _metadata;

        internal ServerSession(IntPtr connectionPointer, uint connectionId, ulong? registeredSteamId)
        {
            SessionId = Guid.NewGuid();
            ConnectionPointer = connectionPointer;
            ConnectionId = connectionId;
            FirstObservedAt = DateTimeOffset.UtcNow;
            _metadata = new Metadata(registeredSteamId, null);
        }

        // Registry key only; native address reuse still requires an exact handle match.
        internal IntPtr ConnectionPointer { get; }

        /// <summary>Lifetime correlation only, not an authentication token.</summary>
        public Guid SessionId { get; }
        public uint ConnectionId { get; }
        public DateTimeOffset FirstObservedAt { get; }
        /// <summary>Latest game-table registration, not separate authentication or permission. May change or be absent.</summary>
        public ulong? RegisteredSteamId => Volatile.Read(ref _metadata).RegisteredSteamId;
        /// <summary>Last-observed liveness while tracking is enabled, not a real-time network guarantee.</summary>
        public bool IsCurrent => ServerSessions.HooksInstalled && Volatile.Read(ref _metadata).ClosedAt == null;
        /// <summary>Observed retirement time, or null if no retirement was observed before tracking became unavailable.</summary>
        public DateTimeOffset? ClosedAt => Volatile.Read(ref _metadata).ClosedAt;

        internal bool RefreshRegistration(ulong? registeredSteamId)
        {
            var previous = Volatile.Read(ref _metadata);
            if (previous.ClosedAt != null || previous.RegisteredSteamId == registeredSteamId)
                return false;
            Volatile.Write(ref _metadata, new Metadata(registeredSteamId, null));
            return true;
        }

        internal void Close()
        {
            var previous = Volatile.Read(ref _metadata);
            if (previous.ClosedAt == null)
                Volatile.Write(ref _metadata, new Metadata(previous.RegisteredSteamId, DateTimeOffset.UtcNow));
        }

        private sealed record Metadata(ulong? RegisteredSteamId, DateTimeOffset? ClosedAt);
    }
}
