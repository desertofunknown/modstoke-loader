// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Bolt;
using Endnight.Extensions;
using HarmonyLib;
using OpenACAI.Endnight.Loader;
using OpenACAI.Loader;
using Sons.Multiplayer;

namespace OpenACAI.Sons
{
    /// <summary>Observed server-side remote-client lifetimes, including clients without OpenACAI.</summary>
    public static partial class OpenACAISonsServerSessions
    {
        /// <summary>SDK limit for a native disconnect localization key, measured in UTF-8 bytes.</summary>
        public const int MaxDisconnectMessageKeyBytes = 256;

        private static readonly UTF8Encoding DisconnectMessageKeyEncoding = new(false, true);
        private static readonly Dictionary<IntPtr, OpenACAISonsServerSession> Current = new();
        private static readonly HashSet<IntPtr> Tombstones = new();
        private static readonly HashSet<OpenACAISonsServerSession> DisconnectAttempts = new();
        private static IReadOnlyList<OpenACAISonsServerSession> _sessions = Array.Empty<OpenACAISonsServerSession>();
        private static Queue<PendingEvent> _pending = new();
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedFailure;
        private static int _reportedDisconnectFailure;
        private static bool _hooksInstalled;
        private static bool _shutdown;
        private static bool _ticking;
        private static long _nextRefresh;

        /// <summary>All hooks were installed and tracking has not been disabled by a failure.</summary>
        public static bool HooksInstalled => Volatile.Read(ref _hooksInstalled);

        /// <summary>Immutable membership snapshot of last-observed remote clients, not real-time network liveness.</summary>
        public static IReadOnlyList<OpenACAISonsServerSession> Sessions => HooksInstalled
            ? Volatile.Read(ref _sessions) : Array.Empty<OpenACAISonsServerSession>();

        /// <summary>Queued observed opening, not a native delivery timestamp. The handle may already be closed.</summary>
        public static event Action<OpenACAISonsServerSession> Opened;
        /// <summary>Queued observed closing; no native disconnect reason is inferred.</summary>
        public static event Action<OpenACAISonsServerSession> Closed;
        /// <summary>Queued metadata change. The handle exposes the latest observed registration.</summary>
        public static event Action<OpenACAISonsServerSession> RegistrationChanged;

        /// <summary>
        /// Requests a native disconnect for this exact live session on the game thread.
        /// Requested means the native call returned, not that the client has disconnected.
        /// </summary>
        public static OpenACAISonsDisconnectResult Disconnect(OpenACAISonsServerSession session) =>
            DisconnectCore(session, null);

        /// <summary>
        /// Requests the native kicked-from-game notice. The message is a Multiplayer localization key, not literal text.
        /// This does not ban the player, change their role or confirm client receipt or presentation.
        /// </summary>
        public static OpenACAISonsDisconnectResult DisconnectWithNotice(OpenACAISonsServerSession session,
            string messageKey = "HOST_KICKED_YOU") =>
            IsDisconnectMessageKeyValid(messageKey) ? DisconnectCore(session, messageKey) : OpenACAISonsDisconnectResult.InvalidMessageKey;

        private static OpenACAISonsDisconnectResult DisconnectCore(OpenACAISonsServerSession session, string messageKey)
        {
            if (!HooksInstalled)
                return OpenACAISonsDisconnectResult.Unavailable;
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                return OpenACAISonsDisconnectResult.WrongThread;

            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                    return OpenACAISonsDisconnectResult.NotServer;
                var connection = ResolveCurrent(session);
                if (connection == null)
                    return HooksInstalled ? OpenACAISonsDisconnectResult.SessionUnavailable : OpenACAISonsDisconnectResult.Unavailable;
                if (DisconnectAttempts.Contains(session))
                    return OpenACAISonsDisconnectResult.AlreadyAttempted;

                IProtocolToken token = null;
                if (messageKey != null)
                    token = new CoopKickToken { Banned = false, KickMessage = messageKey }.Cast<IProtocolToken>();

                // Reserve before invoking native code: an exception does not prove that nothing was queued.
                if (!DisconnectAttempts.Add(session))
                    return OpenACAISonsDisconnectResult.AlreadyAttempted;
                if (messageKey == null)
                    connection.Disconnect();
                else
                    connection.Disconnect(token);
                return OpenACAISonsDisconnectResult.Requested;
            }
            catch (Exception error)
            {
                if (Interlocked.Exchange(ref _reportedDisconnectFailure, 1) == 0)
                {
                    try
                    {
                        OpenACAIEndnightRuntime.LogSource?.LogWarning("Server disconnect request failed ("
                            + error.GetType().FullName + "). Further disconnect failures will not be logged.");
                    }
                    catch (Exception) { }
                }
                return OpenACAISonsDisconnectResult.Failed;
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

        internal static void InstallHarmonyPatches(OpenACAIHarmonySupportSnapshot support, Action<string> warning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;

            _ownerThreadId = Environment.CurrentManagedThreadId;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                warning?.Invoke("Optional server session tracking was not installed: Harmony support is unavailable.");
                return;
            }

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

                var harmony = new HarmonyLib.Harmony(OpenACAIEndnightRuntime.PluginGuid + ".sons-server-sessions");
                harmony.Patch(connected, prefix: Prefix(nameof(BeforeConnected)));
                harmony.Patch(disconnected, prefix: Prefix(nameof(BeforeDisconnected)));
                harmony.Patch(start, prefix: Prefix(nameof(BeforeStart)));
                harmony.Patch(shutdown, prefix: Prefix(nameof(BeforeShutdown)));
                harmony.Patch(immediate, prefix: Prefix(nameof(BeforeShutdown)));
                harmony.Patch(update, postfix: new HarmonyMethod(typeof(OpenACAISonsServerSessions), nameof(Tick)));
                Volatile.Write(ref _hooksInstalled, true);
            }
            catch (Exception error)
            {
                // Partial native patches remain installed, but their callbacks cannot activate tracking.
                Disable(error, warning);
            }
        }

        private static HarmonyMethod Prefix(string name) => new(typeof(OpenACAISonsServerSessions), name)
        {
            priority = HarmonyLib.Priority.First
        };

        internal static OpenACAISonsServerSession ResolveSource(BoltConnection connection)
        {
            if (!HooksInstalled || Environment.CurrentManagedThreadId != _ownerThreadId)
                return null;
            try
            {
                if (!CanObserve() || connection == null)
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

        internal static BoltConnection ResolveCurrent(OpenACAISonsServerSession session)
        {
            if (!HooksInstalled || Environment.CurrentManagedThreadId != _ownerThreadId || session == null)
                return null;
            try
            {
                if (!CanObserve() || !session.IsCurrent)
                    return null;

                var pointer = IntPtr.Zero;
                foreach (var pair in Current)
                {
                    if (ReferenceEquals(pair.Value, session))
                    {
                        pointer = pair.Key;
                        break;
                    }
                }
                if (pointer == IntPtr.Zero || Tombstones.Contains(pointer))
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

        private static BoltConnection FindClient(IntPtr pointer, uint connectionId)
        {
            BoltConnection found = null;
            BoltNetwork.clients.ForEach((Action<BoltConnection>)(candidate =>
            {
                if (candidate != null && candidate.Pointer == pointer && candidate.ConnectionId == connectionId)
                    found = candidate;
            }));
            return found;
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

        private static void BeforeConnected(BoltConnection __0)
        {
            if (!EnterCallback())
                return;
            try
            {
                if (__0 == null)
                    return;
                var pointer = __0.Pointer;
                Retire(pointer);
                Tombstones.Remove(pointer);
                if (CanObserve())
                {
                    var id = __0.ConnectionId;
                    var observations = ReadClients();
                    foreach (var observation in observations)
                    {
                        if (observation.Pointer == pointer && observation.ConnectionId == id)
                        {
                            Observe(observation);
                            break;
                        }
                    }
                }
                Publish();
            }
            catch (Exception error) { Disable(error); }
        }

        private static void BeforeDisconnected(BoltConnection __0)
        {
            if (!EnterCallback())
                return;
            try
            {
                if (__0 == null)
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
            }
            catch (Exception error) { Disable(error); }
        }

        private static void BeforeShutdown()
        {
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

                // Reentrant handlers append to a fresh queue and are delivered on a later tick.
                var batch = Interlocked.Exchange(ref _pending, new Queue<PendingEvent>());
                foreach (var item in batch)
                {
                    if (!HooksInstalled)
                        break;
                    switch (item.Kind)
                    {
                        case EventKind.Opened:
                            OpenACAIModding.Dispatch("OpenACAISonsServerSessions.Opened", Opened, item.Session);
                            break;
                        case EventKind.Closed:
                            OpenACAIModding.Dispatch("OpenACAISonsServerSessions.Closed", Closed, item.Session);
                            break;
                        case EventKind.RegistrationChanged:
                            OpenACAIModding.Dispatch("OpenACAISonsServerSessions.RegistrationChanged", RegistrationChanged, item.Session);
                            break;
                    }
                }
            }
            catch (Exception error) { Disable(error); }
            finally { _ticking = false; }
        }

        private static bool CanObserve()
        {
            if (!HooksInstalled || _shutdown)
                return false;
            if (BoltNetwork.isRunning && BoltNetwork.isServer)
                return true;
            RetireAll();
            Tombstones.Clear();
            return false;
        }

        private static List<Observation> ReadClients()
        {
            var observations = new List<Observation>();
            BoltNetwork.clients.ForEach((Action<BoltConnection>)(connection =>
            {
                if (connection == null)
                    return;
                observations.Add(ReadClient(connection));
            }));
            return observations;
        }

        private static Observation ReadClient(BoltConnection connection)
        {
            ulong? registeredSteamId = null;
            if (GameServerManager.GetRegisteredClient(connection, out var steamId) && steamId.m_SteamID != 0)
                registeredSteamId = steamId.m_SteamID;
            return new Observation(connection.Pointer, connection.ConnectionId, registeredSteamId);
        }

        private static void RefreshRegistry(List<Observation> observations)
        {
            var live = new Dictionary<IntPtr, Observation>();
            foreach (var observation in observations)
                live[observation.Pointer] = observation;
            Tombstones.RemoveWhere(pointer => !live.ContainsKey(pointer));

            var stale = new List<IntPtr>();
            foreach (var pair in Current)
            {
                if (!live.TryGetValue(pair.Key, out var observation) || pair.Value.ConnectionId != observation.ConnectionId)
                    stale.Add(pair.Key);
            }
            foreach (var pointer in stale)
                Retire(pointer);
            foreach (var observation in live.Values)
            {
                if (!Tombstones.Contains(observation.Pointer))
                    Observe(observation);
            }
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
            session = new OpenACAISonsServerSession(observation.ConnectionId, observation.RegisteredSteamId);
            Current.Add(observation.Pointer, session);
            Enqueue(EventKind.Opened, session);
        }

        private static void Retire(IntPtr pointer)
        {
            if (Current.Remove(pointer, out var session))
            {
                DisconnectAttempts.Remove(session);
                session.Close();
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
            DisconnectAttempts.Clear();
            Volatile.Write(ref _sessions, Array.Empty<OpenACAISonsServerSession>());
        }

        private static void Publish() => Volatile.Write(ref _sessions,
            new List<OpenACAISonsServerSession>(Current.Values).AsReadOnly());

        private static void Enqueue(EventKind kind, OpenACAISonsServerSession session)
        {
            if (HooksInstalled)
                Volatile.Read(ref _pending).Enqueue(new PendingEvent(kind, session));
        }

        private static void Disable(Exception error, Action<string> warning = null)
        {
            // May run off-thread: never touch the game-thread registry or individual handles here.
            Volatile.Write(ref _hooksInstalled, false);
            Volatile.Write(ref _sessions, Array.Empty<OpenACAISonsServerSession>());
            Interlocked.Exchange(ref _pending, new Queue<PendingEvent>());
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0)
                return;
            try
            {
                var message = "Optional server session tracking is disabled (" + error.GetType().FullName
                    + "). Further failures are suppressed.";
                if (warning != null)
                    warning(message);
                else
                    OpenACAIEndnightRuntime.LogSource?.LogWarning(message);
            }
            catch (Exception) { }
        }

        private enum EventKind { Opened, Closed, RegistrationChanged }
        private readonly record struct PendingEvent(EventKind Kind, OpenACAISonsServerSession Session);
        private readonly record struct Observation(IntPtr Pointer, uint ConnectionId, ulong? RegisteredSteamId);
    }

    public enum OpenACAISonsDisconnectResult
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

    /// <summary>A managed, reference-identity remote-client lifetime. Closed handles are never reactivated.</summary>
    public sealed class OpenACAISonsServerSession
    {
        private Metadata _metadata;

        internal OpenACAISonsServerSession(uint connectionId, ulong? registeredSteamId)
        {
            SessionId = Guid.NewGuid();
            ConnectionId = connectionId;
            FirstObservedAt = DateTimeOffset.UtcNow;
            _metadata = new Metadata(registeredSteamId, null);
        }

        /// <summary>Lifetime correlation only, not an authentication token.</summary>
        public Guid SessionId { get; }
        public uint ConnectionId { get; }
        public DateTimeOffset FirstObservedAt { get; }
        /// <summary>Latest game-table registration, not separate authentication or permission. May change or be absent.</summary>
        public ulong? RegisteredSteamId => Volatile.Read(ref _metadata).RegisteredSteamId;
        /// <summary>Last-observed liveness while tracking is enabled, not a real-time network guarantee.</summary>
        public bool IsCurrent => OpenACAISonsServerSessions.HooksInstalled && Volatile.Read(ref _metadata).ClosedAt == null;
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
