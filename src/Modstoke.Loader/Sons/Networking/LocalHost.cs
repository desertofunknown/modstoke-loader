// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using Steamworks;

namespace Modstoke.Sons
{
    /// <summary>Identity of the actual local player on a listen host. Dedicated servers have no such player.</summary>
    public static class LocalHost
    {
        /// <summary>
        /// Captures a live local player attachment and the local Steam account on the game thread.
        /// This does not create a remote session, validate a ticket, or grant administration permissions.
        /// </summary>
        public static bool TryGetPlayer(out HostPlayer player)
        {
            player = null;
            if (!ServerWorld.TryCaptureLocalPlayerBinding(out var binding, out _))
                return false;
            try
            {
                var steamId = SteamUser.GetSteamID().m_SteamID;
                if (steamId == 0)
                    return false;
                var candidate = new HostPlayer(binding, steamId);
                if (!candidate.IsCurrent)
                    return false;
                player = candidate;
                return true;
            }
            catch (Exception) { return false; }
        }
    }

    /// <summary>
    /// Retains one local player attachment and its observed Steam account. Use only on the game thread.
    /// A replacement player attachment requires a fresh handle; life transitions within it are not tracked here.
    /// </summary>
    public sealed class HostPlayer
    {
        private readonly PlayerHandle _binding;
        private int _retired;

        internal HostPlayer(PlayerHandle binding, ulong steamId)
        {
            _binding = binding;
            SteamId = steamId;
        }

        /// <summary>Local Steam account observation, not integrity evidence or an SDK permission grant.</summary>
        public ulong SteamId { get; }

        internal bool AttachmentCurrent => Volatile.Read(ref _retired) == 0
            && ServerWorld.IsLocalPlayerBindingCurrent(_binding);

        public bool IsCurrent
        {
            get
            {
                if (!AttachmentCurrent)
                    return false;
                ulong steamId;
                // A Steam API failure is not an account change; only a different account retires the host.
                try { steamId = SteamUser.GetSteamID().m_SteamID; }
                catch (Exception) { return false; }
                if (steamId == SteamId)
                    return true;
                Interlocked.Exchange(ref _retired, 1);
                return false;
            }
        }
    }
}
