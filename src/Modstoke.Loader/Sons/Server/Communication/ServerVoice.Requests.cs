// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;

namespace Modstoke.Sons
{
    public enum VoiceHookStatus
    {
        NotAttempted,
        Disabled,
        Installing,
        Installed,
        Failed
    }

    public sealed class ServerVoiceState
    {
        internal ServerVoiceState(VoiceHookStatus status, string detail)
        {
            Status = status;
            Detail = detail;
        }

        public VoiceHookStatus Status { get; }
        public string Detail { get; }
    }

    /// <summary>
    /// Retains one exact live player attachment. Use on the SDK's game thread.
    /// The remote transport account is separate from this attachment; neither grants permissions or proves integrity.
    /// </summary>
    public sealed class VoiceParticipant
    {
        internal VoiceParticipant(PlayerHandle binding, HostPlayer localPlayer)
        {
            Binding = binding;
            LocalPlayer = localPlayer;
        }

        internal PlayerHandle Binding { get; }
        public ServerSession Session => Binding.SourceSession;
        public HostPlayer LocalPlayer { get; }
        public ulong NetworkId => Binding.NetworkId;
        public bool IsLocalHost => Binding.IsLocalPlayer;

        public bool IsCurrent
        {
            get
            {
                if (!ServerWorld.TryValidatePlayerBinding(Binding, out _))
                    return false;
                return IsLocalHost
                    ? LocalPlayer != null && LocalPlayer.IsCurrent
                    : Session != null && Session.IsCurrent;
            }
        }
    }

    /// <summary>
    /// One native-route recipient send or host playback attempt, with a synchronous decision window.
    /// No audio bytes are exposed or recorded by this request. It is not a delivery receipt.
    /// </summary>
    public sealed class VoiceDeliveryRequest
    {
        private readonly int _dispatchThreadId = Environment.CurrentManagedThreadId;
        private bool _acceptingDecisions = true;
        private bool _rejected;

        internal VoiceDeliveryRequest(VoiceParticipant source,
            VoiceParticipant recipient, bool positional, int payloadBytes, DateTimeOffset receivedAt)
        {
            Source = source;
            Recipient = recipient;
            IsPositional = positional;
            PayloadBytes = payloadBytes;
            ReceivedAt = receivedAt;
        }

        public VoiceParticipant Source { get; }
        public VoiceParticipant Recipient { get; }
        public bool IsPositional { get; }
        public int PayloadBytes { get; }
        public DateTimeOffset ReceivedAt { get; }
        public bool IsRejected => Volatile.Read(ref _rejected);
        public bool IsLocalPlayback => Recipient.IsLocalHost;

        /// <summary>
        /// Skips only this delivery before send or decode; it cannot recall already queued audio.
        /// Returns false after synchronous dispatch or on another thread. Rejection cannot be undone.
        /// </summary>
        public bool Reject()
        {
            if (Environment.CurrentManagedThreadId != _dispatchThreadId || !_acceptingDecisions)
                return false;
            Volatile.Write(ref _rejected, true);
            return true;
        }

        internal void Close() => _acceptingDecisions = false;
    }
}
