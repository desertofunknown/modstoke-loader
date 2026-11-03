// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using global::Sons.Multiplayer;

namespace Modstoke.Sons
{
    public static partial class ServerNotices
    {
        private const long HostIdentityObservationMilliseconds = 250;
        private static HostCommandSession _hostCommandSession;
        private static long _nextHostCommandSequence;
        private static int _activeHostCommands;

        // Native validation belongs to the game thread. Other threads observe the last published snapshot.
        internal static bool HostCommandsSupported
        {
            get
            {
                if (Environment.CurrentManagedThreadId == _ownerThreadId)
                    RefreshHostCommandSession();
                return HooksInstalled && Commands.HostHooksInstalled
                    && Volatile.Read(ref _hostCommandSession) != null;
            }
        }

        internal static void RefreshHostCommandSession(bool freshIdentity = false)
        {
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                return;
            try
            {
                if (!HooksInstalled || !Commands.HostHooksInstalled
                    || !BoltNetwork.isRunning || !BoltNetwork.isServer
                    || !GameServerManager.HasDedicatedState || GameServerManager.IsDedicatedServer)
                {
                    ClearHostCommandSession();
                    return;
                }
                var current = _hostCommandSession;
                if (current != null && HostIdentityCurrent(current.Player, current.World,
                    ref current.NextIdentityCheck, freshIdentity))
                    return;
                // Retire before capture: a transient unavailable world cannot resurrect the old cache identity.
                ClearHostCommandSession();
                if (TryCaptureHostIdentity(out var player, out var world))
                    Volatile.Write(ref _hostCommandSession, new HostCommandSession(player, world));
            }
            catch (Exception error)
            {
                ClearHostCommandSession();
                ReportFailure(error);
            }
        }

        internal static void ClearHostCommandSession() => Volatile.Write(ref _hostCommandSession, null);

        // Shared by host commands and the host messenger. Availability may reuse the Steam
        // observation, never the attachment/world check.
        private static bool HostIdentityCurrent(HostPlayer player, WorldRecordContext world,
            ref long nextIdentityCheck, bool freshIdentity)
        {
            var now = Environment.TickCount64;
            if (!freshIdentity && now < nextIdentityCheck)
                return player.AttachmentCurrent && world.IsCurrent;
            if (!player.IsCurrent || !world.IsCurrent)
                return false;
            nextIdentityCheck = now + HostIdentityObservationMilliseconds;
            return true;
        }

        private static bool TryCaptureHostIdentity(out HostPlayer player, out WorldRecordContext world)
        {
            player = null;
            return WorldRecords.TryGetCurrent(out world, out _)
                && LocalHost.TryGetPlayer(out player)
                && player.IsCurrent && world.IsCurrent;
        }

        private static bool TryGetHostCommandSession(out HostCommandSession session)
        {
            session = null;
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                return false;
            RefreshHostCommandSession(freshIdentity: true);
            session = _hostCommandSession;
            return session != null;
        }

        private static ClientCommandSendResult SubmitHostClientCommand(string command, Guid? expectedSessionId,
            out ClientCommand request)
        {
            request = null;
            if (!TryGetHostCommandSession(out var session))
                return ClientCommandSendResult.ServerUnavailable;
            if (expectedSessionId.HasValue && expectedSessionId.Value != session.Id)
                return ClientCommandSendResult.ServerUnavailable;
            // Synchronous callbacks can submit another command. Bound that outstanding depth as well.
            if (_activeHostCommands >= global::Modstoke.Sons.ClientCommands.MaxPendingCommands)
                return ClientCommandSendResult.PendingLimitReached;
            if (_nextHostCommandSequence == long.MaxValue)
                return ClientCommandSendResult.SequenceExhausted;

            request = new ClientCommand(++_nextHostCommandSequence);
            _activeHostCommands++;
            try
            {
                var outcome = Commands.DispatchHostClientCommand(command, session.Player, session.World,
                    out var reply, out var replyTruncated);
                if (!session.Player.IsCurrent || !session.World.IsCurrent)
                {
                    outcome = ClientCommandOutcome.Unavailable;
                    reply = null;
                    replyTruncated = false;
                }
                request.Update(ClientCommandProgress.ServerReported, outcome, reply, replyTruncated);
                return ClientCommandSendResult.Queued;
            }
            finally
            {
                _activeHostCommands--;
                RefreshHostCommandSession(freshIdentity: true);
            }
        }

        private sealed class HostCommandSession
        {
            internal HostCommandSession(HostPlayer player, WorldRecordContext world)
            {
                Player = player;
                World = world;
                NextIdentityCheck = Environment.TickCount64 + HostIdentityObservationMilliseconds;
                Id = Guid.NewGuid();
            }

            internal Guid Id { get; }
            internal HostPlayer Player { get; }
            internal WorldRecordContext World { get; }
            internal long NextIdentityCheck;
        }
    }
}
