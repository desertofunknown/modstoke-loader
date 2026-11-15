// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Threading;
using Modstoke.Loader;
using global::Sons.Multiplayer;
using global::Sons.Wearable.Race;

namespace Modstoke.Sons
{
    /// <summary>Server-authored spawn assignments using the game's existing client protocol.</summary>
    public static class ServerPlayerSpawns
    {
        public const string ServiceKey = "openacai.sons.serverPlayerSpawns";
        public const float DefaultArrivalRadius = 5;
        public static readonly TimeSpan DefaultRelocationTimeout = TimeSpan.FromSeconds(15);

        private static PlayerSpawnState _state = new(
            PlayerSpawnHookStatus.NotAttempted, "The player-spawn authority guard has not been installed.");
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedSendFailure;
        private static int _reportedGuardFailure;

        public static PlayerSpawnState State => Volatile.Read(ref _state);

        /// <summary>
        /// Queues a reliable spawn assignment for an exact remote session, preserving its observed player race.
        /// Vanilla handling moves a loaded local player, changes look direction, reapplies race and caches the
        /// assignment for native spawn handling. This is not a pure teleport or a persistent respawn setting.
        /// The caller owns authorization and destination safety. Queued does not confirm client application.
        /// </summary>
        public static PlayerSpawnStatus QueueSpawnAssignment(ServerSession recipient,
            Vector3 position, Quaternion rotation)
        {
            if (State.Status != PlayerSpawnHookStatus.Installed || !ServerSessions.HooksInstalled)
                return PlayerSpawnStatus.Unavailable;
            if (Environment.CurrentManagedThreadId != Volatile.Read(ref _ownerThreadId))
                return PlayerSpawnStatus.WrongThread;
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
                return PlayerSpawnStatus.InvalidPosition;
            if (!TryNormalizeRotation(rotation, out var normalized))
                return PlayerSpawnStatus.InvalidRotation;

            SetPlayerSpawnPoint request = null;
            var sendAttempted = false;
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                    return PlayerSpawnStatus.NotServer;
                var connection = ServerSessions.ResolveCurrent(recipient);
                if (connection == null)
                    return PlayerSpawnStatus.SessionUnavailable;
                var player = MultiplayerUtilities.GetEntityFromBoltConnectionForServer(connection);
                if (player is null || player.WasCollected || !player || !player.isAttached)
                    return PlayerSpawnStatus.PlayerUnavailable;
                var source = player.source;
                if (source == null || source.Pointer != connection.Pointer || source.ConnectionId != connection.ConnectionId)
                    return PlayerSpawnStatus.PlayerUnavailable;
                var playerState = player.GetState<IPlayerState>();
                if (playerState is null || playerState.WasCollected)
                    return PlayerSpawnStatus.RaceUnavailable;
                var race = playerState.PlayerRace;
                if (!Enum.IsDefined((PlayerRace.Race)race))
                    return PlayerSpawnStatus.RaceUnavailable;

                request = SetPlayerSpawnPoint.Create(connection, Bolt.ReliabilityModes.ReliableOrdered);
                request.Position = new UnityEngine.Vector3(position.X, position.Y, position.Z);
                request.Rotation = new UnityEngine.Quaternion(normalized.X, normalized.Y, normalized.Z, normalized.W);
                request.Race = race;

                var current = ServerSessions.ResolveCurrent(recipient);
                if (current == null || current.Pointer != connection.Pointer || current.ConnectionId != connection.ConnectionId)
                    return PlayerSpawnStatus.SessionUnavailable;
                var currentPlayer = MultiplayerUtilities.GetEntityFromBoltConnectionForServer(current);
                if (currentPlayer is null || currentPlayer.WasCollected || !currentPlayer || !currentPlayer.isAttached
                    || currentPlayer.Pointer != player.Pointer)
                    return PlayerSpawnStatus.PlayerUnavailable;

                // Send can queue before throwing. Never free or retry a possibly queued event.
                sendAttempted = true;
                request.Send();
                return PlayerSpawnStatus.Queued;
            }
            catch (Exception error)
            {
                ReportSendFailure(error);
                return sendAttempted ? PlayerSpawnStatus.SendUncertain : PlayerSpawnStatus.Failed;
            }
            finally
            {
                if (request != null && !sendAttempted)
                {
                    try { request.FreeStorage(); }
                    catch (Exception error) { ReportSendFailure(error); }
                }
            }
        }

        /// <summary>
        /// Queues a spawn assignment under a <see cref="MovementGrantKind.Teleport"/> grant, so movement checks know
        /// the jump is the host's own. The grant ends as Arrived once the host's copy of the player is within
        /// <paramref name="arrivalRadius"/> metres of the destination, or as TimedOut after <paramref name="timeout"/>
        /// (<see cref="DefaultRelocationTimeout"/> when null).
        /// </summary>
        /// <remarks>
        /// Arrival is judged from position replication, so it works for vanilla clients and trusts the client no
        /// further than replication already does. The grant explains only the arrival: until the player is at the
        /// destination, movement checks keep their usual rules, so a client that ignores the move gains nothing.
        /// A SendUncertain assignment keeps its grant and times out if it never went out.
        /// </remarks>
        public static PlayerSpawnStatus QueueRelocation(PlayerHandle player, Vector3 position, Quaternion rotation,
            string owner, string reason, out MovementGrant relocation, float arrivalRadius = DefaultArrivalRadius,
            TimeSpan? timeout = null)
        {
            relocation = null;
            if (player == null || player.IsLocalPlayer || player.PlayerSession == null)
                return PlayerSpawnStatus.PlayerUnavailable;
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
                return PlayerSpawnStatus.InvalidPosition;
            if (!float.IsFinite(arrivalRadius) || arrivalRadius <= 0)
                return PlayerSpawnStatus.InvalidRequest;

            var limit = timeout ?? DefaultRelocationTimeout;
            var created = ServerMovementGrants.TryCreate(player, MovementGrantKind.Teleport, owner, reason, limit,
                () => position, arrivalRadius, null, true, out var grant);
            if (created != MovementGrantStatus.Opened)
                return created switch
                {
                    MovementGrantStatus.WrongThread => PlayerSpawnStatus.WrongThread,
                    MovementGrantStatus.NotServer => PlayerSpawnStatus.NotServer,
                    MovementGrantStatus.PlayerUnavailable => PlayerSpawnStatus.PlayerUnavailable,
                    MovementGrantStatus.InvalidRequest => PlayerSpawnStatus.InvalidRequest,
                    MovementGrantStatus.LimitReached => PlayerSpawnStatus.LimitReached,
                    _ => PlayerSpawnStatus.Unavailable
                };

            var status = QueueSpawnAssignment(player.PlayerSession, position, rotation);
            if (status is not (PlayerSpawnStatus.Queued or PlayerSpawnStatus.SendUncertain))
                return status;
            ServerMovementGrants.Add(grant);
            RuntimeEntryPoint.LogSource?.LogInfo(string.Create(CultureInfo.InvariantCulture,
                $"Relocating connection {player.PlayerSession.ConnectionId} to ({position.X:0.0}, {position.Y:0.0}, "
                + $"{position.Z:0.0}) for {owner}: {reason}; watching for arrival within {arrivalRadius:0.#} m "
                + $"for {limit.TotalSeconds:0.#} s."));
            relocation = grant;
            return status;
        }

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                var detail = support?.Detail ?? "Harmony support information is unavailable.";
                PublishState(PlayerSpawnHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Player-spawn authority guard was not installed: " + detail);
                return;
            }

            _ownerThreadId = Environment.CurrentManagedThreadId;
            PublishState(PlayerSpawnHookStatus.Installing, "Registering the player-spawn authority guard.");
            try
            {
                ServerDispatchGate.Register(DispatchRoute.PlayerSpawns);
            }
            catch (Exception error)
            {
                PublishState(PlayerSpawnHookStatus.Failed,
                    "Player-spawn authority guard registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Player-spawn authority guard failed: " + error);
                return;
            }

            PublishState(PlayerSpawnHookStatus.Installed,
                "Player-spawn authority guard registration succeeded. Multiplayer handling remains unverified.");
        }

        internal static DispatchVerdict RouteDispatch(Bolt.Event message)
        {
            if (message.TryCast<SetPlayerSpawnPoint>() == null)
                return DispatchVerdict.Forward;

            try
            {
                if (Environment.CurrentManagedThreadId != _ownerThreadId)
                {
                    ReportGuardFailure("A player-spawn event ran outside the loader's game thread.");
                    return DispatchVerdict.Reject;
                }
                var source = message.RaisedBy;
                if (source == null)
                    return DispatchVerdict.Forward;

                // Spawn assignments are host-authored state, not client requests to relay to other peers.
                if (!BoltNetwork.isRunning || BoltNetwork.isServer || !BoltNetwork.isClient)
                    return DispatchVerdict.Reject;
                var server = BoltNetwork.server;
                return server != null && source.Pointer == server.Pointer && source.ConnectionId == server.ConnectionId
                    ? DispatchVerdict.Forward : DispatchVerdict.Reject;
            }
            catch (Exception error)
            {
                ReportGuardFailure("Reading player-spawn authority failed (" + error.GetType().FullName + ").");
                return DispatchVerdict.Reject;
            }
        }

        internal static void ReportReleaseFailure(Exception error) =>
            ReportGuardFailure("Releasing a rejected player-spawn event failed (" + error.GetType().FullName + ").");

        private static bool TryNormalizeRotation(Quaternion rotation, out Quaternion normalized)
        {
            normalized = default;
            if (!float.IsFinite(rotation.X) || !float.IsFinite(rotation.Y)
                || !float.IsFinite(rotation.Z) || !float.IsFinite(rotation.W))
                return false;

            // Double precision avoids overflow/underflow when normalizing finite float components.
            var lengthSquared = (double)rotation.X * rotation.X + (double)rotation.Y * rotation.Y
                + (double)rotation.Z * rotation.Z + (double)rotation.W * rotation.W;
            if (lengthSquared == 0)
                return false;
            var inverseLength = 1 / Math.Sqrt(lengthSquared);
            normalized = new Quaternion((float)(rotation.X * inverseLength), (float)(rotation.Y * inverseLength),
                (float)(rotation.Z * inverseLength), (float)(rotation.W * inverseLength));
            return true;
        }

        private static void PublishState(PlayerSpawnHookStatus status, string detail)
        {
            var state = new PlayerSpawnState(status, detail);
            Volatile.Write(ref _state, state);
            Modding.SetService(ServiceKey, state);
        }

        private static void ReportSendFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedSendFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Server spawn assignment failed ("
                    + error.GetType().FullName + "). Further send failures will not be logged.");
            }
            catch (Exception) { }
        }

        private static void ReportGuardFailure(string detail)
        {
            if (Interlocked.Exchange(ref _reportedGuardFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Player-spawn event rejected: "
                    + detail + " Further guard failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum PlayerSpawnStatus
    {
        /// <summary>Send returned. This is not a client receipt, safe-arrival check or movement guarantee.</summary>
        Queued,
        Unavailable,
        WrongThread,
        NotServer,
        InvalidPosition,
        InvalidRotation,
        SessionUnavailable,
        PlayerUnavailable,
        /// <summary>The current player race could not be read or is not a defined native race.</summary>
        RaceUnavailable,
        /// <summary>Preparation failed before Send was attempted.</summary>
        Failed,
        /// <summary>Send threw and may have queued the assignment. Do not blindly retry.</summary>
        SendUncertain,
        /// <summary>A relocation's owner, reason, radius or timeout was blank or out of range.</summary>
        InvalidRequest,
        /// <summary>The player already has the most movement grants allowed.</summary>
        LimitReached
    }

    public enum PlayerSpawnHookStatus
    {
        NotAttempted,
        Disabled,
        Installing,
        Installed,
        Failed
    }

    public sealed class PlayerSpawnState
    {
        internal PlayerSpawnState(PlayerSpawnHookStatus status, string detail)
        {
            Status = status;
            Detail = detail;
        }

        /// <summary>Authority hook registration, not proof of enforcement or client spawn application.</summary>
        public PlayerSpawnHookStatus Status { get; }
        public string Detail { get; }
    }

    /// <summary>
    /// Host-side record of mods moving or carrying a player (a seat, flight, teleport or region change), so
    /// movement checks can ask who is moving a player and why instead of guessing from positions.
    /// </summary>
    /// <remarks>
    /// Only host code opens grants, on the SDK's game thread. A grant explains movement and grants nothing else.
    /// The owner renews it while the ride lasts and closes it when the ride ends; a lifetime of a few seconds,
    /// renewed every second, survives long frames. Grants also end when they expire, the player's session closes,
    /// the character is replaced or the server stops. A closed grant stays readable for <see cref="SettleTime"/>
    /// so a check can let the landing settle instead of judging it. A Teleport grant only ever explains a player
    /// at its destination; see <see cref="MovementGrant.Check"/>.
    /// </remarks>
    public static class ServerMovementGrants
    {
        public const int MaxGrantsPerPlayer = 8;
        public const int MaxTextLength = 128;
        public static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(SettleMs);
        public static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(10);

        internal const long SettleMs = 5000;
        private const int MaxGrants = 256;
        private const int ValidationMs = 100;
        private static readonly List<MovementGrant> Grants = new();
        private static IReadOnlyList<MovementGrant> _current = Array.Empty<MovementGrant>();
        private static long _nextValidation;
        private static int _reportedFailure;

        /// <summary>Open and settling grants for every player, oldest first.</summary>
        public static IReadOnlyList<MovementGrant> Current => Volatile.Read(ref _current);

        /// <summary>Raised on the game thread when a grant opens.</summary>
        public static event Action<MovementGrant> Opened;

        /// <summary>Raised on the game thread when a grant ends; <see cref="MovementGrant.End"/> says how.</summary>
        public static event Action<MovementGrant> Closed;

        /// <summary>
        /// Opens a grant for a validated player attachment. <paramref name="anchor"/> returns where the player should
        /// be right now (the seat on a moving carrier, a teleport destination), or null when unknown; checks treat a
        /// player within <paramref name="anchorTolerance"/> metres of it as explained. <paramref name="maxSpeed"/> is
        /// the fastest the player may move under this grant in metres per second, or null when the carrier sets it.
        /// </summary>
        public static MovementGrantStatus TryOpen(PlayerHandle player, MovementGrantKind kind, string owner, string reason,
            TimeSpan lifetime, out MovementGrant grant, Func<Vector3?> anchor = null, float anchorTolerance = 0,
            float? maxSpeed = null)
        {
            var status = TryCreate(player, kind, owner, reason, lifetime, anchor, anchorTolerance, maxSpeed, false, out grant);
            if (status == MovementGrantStatus.Opened)
                Add(grant);
            return status;
        }

        /// <summary>The open grant for this player opened most recently, else the latest one still settling.</summary>
        public static bool TryRead(PlayerHandle player, out MovementGrant grant)
        {
            grant = null;
            if (player == null)
                return false;
            // Indexed rather than foreach: movement checks call this per player and sample, and the interface
            // enumerator would allocate each time.
            var current = Current;
            for (var index = 0; index < current.Count; index++)
            {
                var candidate = current[index];
                if (!candidate.Concerns(player) || !candidate.IsOpen && !candidate.IsSettling)
                    continue;
                if (grant == null || candidate.IsOpen || !grant.IsOpen)
                    grant = candidate;
            }
            return grant != null;
        }

        /// <summary>Every open or settling grant for this player, oldest first, in a new list.</summary>
        public static IReadOnlyList<MovementGrant> Read(PlayerHandle player)
        {
            var grants = new List<MovementGrant>();
            var current = Current;
            if (player != null)
                for (var index = 0; index < current.Count; index++)
                    if (current[index].Concerns(player) && (current[index].IsOpen || current[index].IsSettling))
                        grants.Add(current[index]);
            return grants.AsReadOnly();
        }

        internal static MovementGrantStatus TryCreate(PlayerHandle player, MovementGrantKind kind, string owner,
            string reason, TimeSpan lifetime, Func<Vector3?> anchor, float anchorTolerance, float? maxSpeed,
            bool confirmsArrival, out MovementGrant grant)
        {
            grant = null;
            // The session tick ends grants, so none open without it.
            if (!ServerSessions.HooksInstalled)
                return MovementGrantStatus.Unavailable;
            if (!ServerSessions.IsOwnerThread)
                return MovementGrantStatus.WrongThread;
            if (!Enum.IsDefined(kind) || !IsValidText(owner) || !IsValidText(reason) || !IsValidLifetime(lifetime)
                || !float.IsFinite(anchorTolerance) || anchorTolerance < 0
                || maxSpeed.HasValue && (!float.IsFinite(maxSpeed.Value) || maxSpeed.Value <= 0))
                return MovementGrantStatus.InvalidRequest;
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                    return MovementGrantStatus.NotServer;
                if (player == null || !ServerWorld.TryValidatePlayerBinding(player, out _))
                    return MovementGrantStatus.PlayerUnavailable;
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return MovementGrantStatus.Unavailable;
            }

            Prune(Environment.TickCount64);
            var open = 0;
            foreach (var existing in Grants)
                if (existing.IsOpen && existing.Concerns(player))
                    open++;
            if (open >= MaxGrantsPerPlayer || Grants.Count >= MaxGrants)
                return MovementGrantStatus.LimitReached;

            grant = new MovementGrant(player, kind, owner, reason, lifetime, anchor, anchorTolerance, maxSpeed, confirmsArrival);
            return MovementGrantStatus.Opened;
        }

        internal static void Add(MovementGrant grant)
        {
            Grants.Add(grant);
            Publish();
            if (!grant.ConfirmsArrival)
                RuntimeEntryPoint.LogSource?.LogInfo("Movement grant opened: " + grant.Owner + " " + grant.Kind + " for "
                    + Describe(grant) + " (" + grant.Reason + "), " + Seconds(grant.Lifetime) + " s per renewal.");
            Modding.Dispatch("ServerMovementGrants.Opened", Opened, grant);
        }

        internal static bool IsValidLifetime(TimeSpan lifetime) => lifetime > TimeSpan.Zero && lifetime <= MaxLifetime;

        // Runs every frame from the session tick, without allocating unless a grant ends; arrival and attachment
        // checks run ten times a second. Iterates the published snapshot because anchors are mod code and may open
        // or close grants.
        internal static void Tick()
        {
            if (Grants.Count == 0)
                return;
            List<MovementGrant> ended = null;
            try
            {
                var now = Environment.TickCount64;
                var stopped = !BoltNetwork.isRunning || !BoltNetwork.isServer;
                var validate = now >= _nextValidation;
                if (validate)
                    _nextValidation = now + ValidationMs;

                var current = Current;
                for (var index = 0; index < current.Count; index++)
                {
                    var grant = current[index];
                    if (!grant.IsOpen)
                        continue;
                    var end = stopped ? MovementGrantEnd.ServerStopped
                        : grant.Session?.Status == ServerSessionStatus.Closed ? MovementGrantEnd.PlayerLeft
                        : validate ? Observe(grant) : MovementGrantEnd.None;
                    if (end == MovementGrantEnd.None && grant.HasExpired(now))
                        end = grant.ConfirmsArrival ? MovementGrantEnd.TimedOut : MovementGrantEnd.Expired;
                    if (end != MovementGrantEnd.None && grant.Finish(end))
                        (ended ??= new List<MovementGrant>()).Add(grant);
                }
                Prune(now);
            }
            catch (Exception error)
            {
                ReportFailure(error);
            }
            if (ended != null)
                foreach (var grant in ended)
                    Report(grant);
        }

        internal static void Report(MovementGrant grant)
        {
            var held = Seconds(grant.ClosedAt.Value - grant.OpenedAt);
            var log = RuntimeEntryPoint.LogSource;
            switch (grant.End)
            {
                case MovementGrantEnd.Arrived:
                    log?.LogInfo("Relocation of " + Describe(grant) + " arrived " + held + " s after it was sent, "
                        + Metres(grant.LastDistance) + " m from the destination (" + grant.Owner + ": " + grant.Reason + ").");
                    break;
                case MovementGrantEnd.TimedOut:
                    log?.LogWarning("Relocation of " + Describe(grant) + " was not seen to arrive within " + held
                        + " s; last seen " + (grant.LastDistance.HasValue ? Metres(grant.LastDistance) + " m from" : "away from")
                        + " the destination (" + grant.Owner + ": " + grant.Reason + ").");
                    break;
                default:
                    log?.LogInfo("Movement grant ended (" + grant.End + "): " + grant.Owner + " " + grant.Kind + " for "
                        + Describe(grant) + " after " + held + " s.");
                    break;
            }
            Modding.Dispatch("ServerMovementGrants.Closed", Closed, grant);
        }

        internal static void ReportAnchorFailure(MovementGrant grant, Exception error)
        {
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Movement grant anchor from " + grant.Owner + " failed ("
                    + error.GetType().FullName + "); checks of this grant read Unknown.");
            }
            catch (Exception) { }
        }

        private static MovementGrantEnd Observe(MovementGrant grant)
        {
            if (!grant.ConfirmsArrival)
                return !ServerWorld.TryValidatePlayerBinding(grant.Player, out _) && grant.Player.IsRetired
                    ? MovementGrantEnd.PlayerReplaced : MovementGrantEnd.None;
            if (!ServerWorld.TryReadPlayerPose(grant.Player, out var pose, out _))
                return grant.Player.IsRetired ? MovementGrantEnd.PlayerReplaced : MovementGrantEnd.None;
            if (!grant.TryReadAnchor(out var destination))
                return MovementGrantEnd.None;
            var distance = Vector3.Distance(pose.Position, destination);
            grant.LastDistance = distance;
            return distance <= grant.AnchorTolerance ? MovementGrantEnd.Arrived : MovementGrantEnd.None;
        }

        private static void Prune(long now)
        {
            var removed = false;
            for (var index = Grants.Count - 1; index >= 0; index--)
            {
                if (!Grants[index].HasSettled(now))
                    continue;
                Grants.RemoveAt(index);
                removed = true;
            }
            if (removed)
                Publish();
        }

        private static void Publish() => Volatile.Write(ref _current, new List<MovementGrant>(Grants).AsReadOnly());

        private static bool IsValidText(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength)
                return false;
            foreach (var character in text)
                if (char.IsControl(character))
                    return false;
            return true;
        }

        private static string Describe(MovementGrant grant) => grant.Session == null
            ? "the host player" : "connection " + grant.Session.ConnectionId;

        private static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);

        private static string Metres(float? distance) => distance?.ToString("0.0", CultureInfo.InvariantCulture) ?? "?";

        private static void ReportFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Movement grant upkeep failed (" + error.GetType().FullName
                    + "). Further failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    /// <summary>One mod's claim that it is moving a player. Game thread only, apart from the copied properties.</summary>
    public sealed class MovementGrant
    {
        private readonly Func<Vector3?> _anchor;
        // Expiry and settling run on Environment.TickCount64, so a wall-clock step neither ends every ride nor keeps
        // a dead grant open. The DateTimeOffset properties are derived from it for display.
        private readonly long _openedTick;
        private long _expiresTick;
        private bool _anchorFailed;
        private Ending _ending;

        internal MovementGrant(PlayerHandle player, MovementGrantKind kind, string owner, string reason, TimeSpan lifetime,
            Func<Vector3?> anchor, float anchorTolerance, float? maxSpeed, bool confirmsArrival)
        {
            Player = player;
            Kind = kind;
            Owner = owner;
            Reason = reason;
            Lifetime = lifetime;
            _anchor = anchor;
            AnchorTolerance = anchorTolerance;
            MaxSpeed = maxSpeed;
            ConfirmsArrival = confirmsArrival;
            OpenedAt = DateTimeOffset.UtcNow;
            _openedTick = Environment.TickCount64;
            _expiresTick = _openedTick + (long)lifetime.TotalMilliseconds;
        }

        /// <summary>The attachment the grant was opened for. It ends with PlayerReplaced if this handle retires.</summary>
        public PlayerHandle Player { get; }
        /// <summary>The remote player's session, or null for the listen host's own player.</summary>
        public ServerSession Session => Player.PlayerSession;
        public MovementGrantKind Kind { get; }
        /// <summary>The mod that opened the grant, as it named itself.</summary>
        public string Owner { get; }
        public string Reason { get; }
        /// <summary>Metres per second, or null when the carrier sets the speed.</summary>
        public float? MaxSpeed { get; }
        /// <summary>Metres from the anchor within which a player position counts as explained.</summary>
        public float AnchorTolerance { get; }
        public bool HasAnchor => _anchor != null;
        public DateTimeOffset OpenedAt { get; }
        /// <summary>The lifetime given at open or at the latest renewal.</summary>
        public TimeSpan Lifetime { get; private set; }
        public DateTimeOffset ExpiresAt => At(Interlocked.Read(ref _expiresTick));
        public DateTimeOffset? ClosedAt => Volatile.Read(ref _ending) is { } ending ? At(ending.Tick) : null;
        /// <summary>None while open.</summary>
        public MovementGrantEnd End => Volatile.Read(ref _ending)?.End ?? MovementGrantEnd.None;
        public bool IsOpen => Volatile.Read(ref _ending) == null;
        /// <summary>Closed less than <see cref="ServerMovementGrants.SettleTime"/> ago.</summary>
        public bool IsSettling => !IsOpen && !HasSettled(Environment.TickCount64);

        // Relocations close themselves on arrival, judged from the host's own copy of the player.
        internal bool ConfirmsArrival { get; }
        internal float? LastDistance { get; set; }

        /// <summary>Extends an open grant to now plus <paramref name="lifetime"/>. False once it has ended.</summary>
        public bool Renew(TimeSpan lifetime)
        {
            if (!IsOpen || !ServerMovementGrants.IsValidLifetime(lifetime) || !ServerSessions.IsOwnerThread)
                return false;
            Lifetime = lifetime;
            Interlocked.Exchange(ref _expiresTick, Environment.TickCount64 + (long)lifetime.TotalMilliseconds);
            return true;
        }

        /// <summary>Ends the grant because the ride ended. False if it had already ended.</summary>
        public bool Close() => Finish(MovementGrantEnd.Closed, true);

        /// <summary>
        /// Where the anchor says the player should be now. False without an anchor, when it returns null or a
        /// non-finite position, or when it throws.
        /// </summary>
        public bool TryReadAnchor(out Vector3 anchor)
        {
            anchor = default;
            if (_anchor == null || !ServerSessions.IsOwnerThread)
                return false;
            try
            {
                if (_anchor() is not { } value
                    || !float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
                    return false;
                anchor = value;
                return true;
            }
            catch (Exception error)
            {
                if (!_anchorFailed)
                {
                    _anchorFailed = true;
                    ServerMovementGrants.ReportAnchorFailure(this, error);
                }
                return false;
            }
        }

        /// <summary>
        /// Whether a host-side position is within <see cref="AnchorTolerance"/> of the anchor; Unknown when there is
        /// no readable anchor.
        /// </summary>
        /// <remarks>
        /// For a Seat, Flight or RegionChange grant, an Outside or Unknown sample while the grant is open or settling
        /// is a reason to re-baseline, not proof. A Teleport grant explains only an Inside sample, the arrival: a
        /// player still away from the destination is judged by the usual rules, so ignoring the move earns no slack.
        /// </remarks>
        public MovementGrantCheck Check(Vector3 position)
        {
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)
                || !TryReadAnchor(out var anchor))
                return MovementGrantCheck.Unknown;
            return Vector3.Distance(position, anchor) <= AnchorTolerance ? MovementGrantCheck.Inside : MovementGrantCheck.Outside;
        }

        internal bool Concerns(PlayerHandle player) => player.NetworkId == Player.NetworkId
            && (Player.IsLocalPlayer ? player.IsLocalPlayer : ReferenceEquals(player.PlayerSession, Player.PlayerSession));

        internal bool HasExpired(long now) => now >= Interlocked.Read(ref _expiresTick);

        internal bool HasSettled(long now) =>
            Volatile.Read(ref _ending) is { } ending && now >= ending.Tick + ServerMovementGrants.SettleMs;

        internal bool Finish(MovementGrantEnd end, bool report = false)
        {
            if (!ServerSessions.IsOwnerThread
                || Interlocked.CompareExchange(ref _ending, new Ending(end, Environment.TickCount64), null) != null)
                return false;
            if (report)
                ServerMovementGrants.Report(this);
            return true;
        }

        private DateTimeOffset At(long tick) => OpenedAt.AddMilliseconds(tick - _openedTick);

        private sealed record Ending(MovementGrantEnd End, long Tick);
    }

    public enum MovementGrantKind
    {
        /// <summary>Rides a seat on a moving carrier; the anchor is the seat.</summary>
        Seat,
        /// <summary>Flies under a mod's control without a seat, such as a jetpack.</summary>
        Flight,
        /// <summary>Moves to a destination in one step; the anchor is the destination, and only arriving there is explained.</summary>
        Teleport,
        /// <summary>Moves somewhere the island's ground and water rules do not describe, such as orbit or a station.</summary>
        RegionChange
    }

    public enum MovementGrantEnd
    {
        None,
        /// <summary>The owner closed it.</summary>
        Closed,
        /// <summary>The owner stopped renewing it.</summary>
        Expired,
        /// <summary>A relocation reached its destination on the host's copy of the player.</summary>
        Arrived,
        /// <summary>A relocation was not seen to arrive in time.</summary>
        TimedOut,
        PlayerLeft,
        PlayerReplaced,
        ServerStopped
    }

    public enum MovementGrantCheck
    {
        Unknown,
        Inside,
        Outside
    }

    public enum MovementGrantStatus
    {
        Opened,
        Unavailable,
        WrongThread,
        NotServer,
        /// <summary>The attachment could not be validated now, or has retired.</summary>
        PlayerUnavailable,
        /// <summary>Unknown kind, blank or overlong owner or reason, or an out-of-range lifetime, tolerance or speed.</summary>
        InvalidRequest,
        LimitReached
    }
}
