// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Sons;

namespace Modstoke.Sons.Combat
{
    /// <summary>
    /// Observes one enrolled server peer. The mode owner drives Update and TryReceive on the
    /// game thread and must retain terminal retirement until that exact peer closes. Creating
    /// this adapter does not select enrollment, subscribe to events or enable combat enforcement.
    /// </summary>
    public sealed partial class StrictCombatClient
    {
        private readonly ControlPeer _peer;
        private readonly bool _requiresArmourReceipts;
        private readonly bool _requiresRebreatherReceipts;
        private readonly bool _requiresRebreatherWearReceipts;
        private readonly bool _requiresRebreatherProgress;
        private readonly bool _requiresBreathingReceipts;
        private readonly StrictCombatClientState _state = new StrictCombatClientState();
        private readonly int _ownerThreadId;
        private readonly long _started;
        private long _lastObservation;
        private StrictCombatLocalPlayer _localPlayer;
        private StrictCombatLifeBarrier _nativeLifeBarrier;
        private bool _projectingHealth;
        private static int _healthProjectionFailureReported;

        private StrictCombatClient(ControlPeer peer, long now, bool requiresArmourReceipts,
            bool requiresRebreatherReceipts, bool requiresRebreatherWearReceipts,
            bool requiresRebreatherProgress, bool requiresBreathingReceipts)
        {
            _peer = peer;
            _requiresArmourReceipts = requiresArmourReceipts;
            _requiresRebreatherReceipts = requiresRebreatherReceipts;
            _requiresRebreatherWearReceipts = requiresRebreatherWearReceipts;
            _requiresRebreatherProgress = requiresRebreatherProgress;
            _requiresBreathingReceipts = requiresBreathingReceipts;
            _requiresReadiness = ReadinessNegotiated(peer);
            _ownerThreadId = Environment.CurrentManagedThreadId;
            _started = now;
            _lastObservation = now;
        }

        public bool IsRetired => _state.IsRetired;

        /// <summary>
        /// Prepares native life interception from the menu, before network startup.
        /// Does not enroll a peer, acquire a player, write health or enable strict combat.
        /// </summary>
        public static StrictCombatLifeStatus PrepareNativeLifeBarrierForNextStart()
            => StrictCombatLifeHooks.PrepareForNextStart();

        /// <summary>
        /// Suppresses known native health, death, healing and respawn paths for this exact capture.
        /// This is a building block for a managed life controller, not a playable combat mode.
        /// There is no in-world fallback: retirement keeps old native callbacks suppressed.
        /// </summary>
        public bool TryAcquireNativeLifeBarrier(StrictCombatLocalSnapshot observation,
            out StrictCombatLifeStatus status)
            => AcquireNativeLife(observation, false, out status);

        /// <summary>
        /// Acquires life ownership only after observed normal native vitals initialization.
        /// An active initialization or save load returns Busy; missing initialization returns
        /// NativeInitializationPending. Earlier native life transitions remain disqualifying.
        /// This does not prove all startup work finished or apply accepted server health.
        /// </summary>
        public bool TryAcquireInitializedNativeLifeBarrier(StrictCombatLocalSnapshot observation,
            out StrictCombatLifeStatus status)
            => AcquireNativeLife(observation, true, out status);

        private bool AcquireNativeLife(StrictCombatLocalSnapshot observation,
            bool requireCompletedInitialization, out StrictCombatLifeStatus status)
        {
            status = StrictCombatLifeStatus.WrongThread;
            if (Environment.CurrentManagedThreadId != _ownerThreadId) return false;
            status = StrictCombatLifeStatus.Unavailable;
            if (_projectingArmour) return false;
            if (!StrictCombatLifeHooks.TryAcquire(this, observation, requireCompletedInitialization,
                out var barrier, out status))
                return false;
            _nativeLifeBarrier = barrier;
            return true;
        }

        /// <summary>
        /// Applies only the latest accepted server health to the exact owned native player.
        /// Success is a readback of health fields, not a life transition or continuous write protection.
        /// </summary>
        public StrictCombatHealthProjectionStatus ProjectLocalHealth()
        {
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                return StrictCombatHealthProjectionStatus.WrongThread;
            if (_projectingHealth || _placingRespawn || _presentingWeapon || _projectingArmour || _projectingAmmunition || _updatingEquipment) return StrictCombatHealthProjectionStatus.Busy;
            _projectingHealth = true;
            try
            {
                if (!TryRead(out var binding, out var state))
                    return StrictCombatHealthProjectionStatus.Unavailable;
                var barrier = _nativeLifeBarrier;
                var player = _localPlayer;
                if (barrier == null || barrier.IsRetired || player == null
                    || !ReferenceEquals(barrier.Owner, this) || !ReferenceEquals(barrier.Player, player))
                    return StrictCombatHealthProjectionStatus.BarrierRequired;
                if (state.LifeGeneration == 0 || state.LifeGeneration != barrier.LifeGeneration)
                    return StrictCombatHealthProjectionStatus.LifeChanged;
                int frame = UnityEngine.Time.frameCount;
                return player.ProjectHealth(this, barrier, in binding, in state, frame);
            }
            catch (Exception error)
            {
                StrictCombatDiagnostics.ReportOnce(ref _healthProjectionFailureReported, "health projection", error);
                return StrictCombatHealthProjectionStatus.NativeStateUnavailable;
            }
            finally { _projectingHealth = false; }
        }

        internal bool IsCurrentHealthProjection(StrictCombatLocalPlayer player,
            StrictCombatLifeBarrier barrier, in StrictCombatBinding binding,
            in StrictCombatSnapshot state, int frame)
        {
            return _projectingHealth && ReferenceEquals(barrier, _nativeLifeBarrier) && !barrier.IsRetired
                && ReferenceEquals(player, _localPlayer) && ReferenceEquals(barrier.Player, player)
                && ReferenceEquals(barrier.Owner, this) && barrier.LifeGeneration == state.LifeGeneration
                && IsCurrent(out _) && _state.HasBinding && _state.HasSnapshot
                && _state.Binding.SameBinding(in binding)
                && _state.Snapshot.Revision == state.Revision
                && _state.Snapshot.LifeGeneration == state.LifeGeneration
                && UnityEngine.Time.frameCount == frame && player.Matches(in binding) && player.IsCurrent(out _);
        }

        internal bool IsCurrentNativeHealthOwner(StrictCombatLifeBarrier barrier)
        {
            return Environment.CurrentManagedThreadId == _ownerThreadId
                && ReferenceEquals(barrier, _nativeLifeBarrier) && !barrier.IsRetired
                && ReferenceEquals(barrier.Owner, this) && ReferenceEquals(barrier.Player, _localPlayer)
                && TryRead(out var binding, out var state)
                && (state.LifeGeneration == barrier.LifeGeneration || RetainsRespawnContainment(barrier, in state))
                && barrier.Player.Matches(in binding) && barrier.Player.IsCurrent(out _);
        }

        public static bool TryCreate(ControlPeer peer, out StrictCombatClient client)
        {
            client = null;
            long now = Environment.TickCount64;
            if (now < 0 || !IsEnrolled(peer, out _, out bool requiresArmourReceipts, out bool requiresRebreatherReceipts,
                    out bool requiresRebreatherWearReceipts, out bool requiresRebreatherProgress,
                    out bool requiresBreathingReceipts)) return false;
            client = new StrictCombatClient(peer, now, requiresArmourReceipts, requiresRebreatherReceipts,
                requiresRebreatherWearReceipts, requiresRebreatherProgress, requiresBreathingReceipts);
            return true;
        }

        public bool TryQueue(StrictCombatAction action, float directionX = 0,
            float directionY = 0, float directionZ = 0)
        {
            return !_projectingArmour && !_updatingEquipment && IsCurrent(out long now) && _state.TryBegin(action, now, directionX, directionY, directionZ);
        }

        public bool TryRead(out StrictCombatBinding binding, out StrictCombatSnapshot snapshot)
        {
            binding = default;
            snapshot = default;
            if (!IsCurrent(out _) || !_state.HasBinding || !_state.HasSnapshot) return false;
            binding = _state.Binding;
            snapshot = _state.Snapshot;
            return true;
        }

        /// <summary>
        /// Reads local native life state only when it matches the accepted server binding.
        /// A lost capture stays unavailable until a newer server binding establishes a new epoch.
        /// </summary>
        public bool TryReadLocalPlayer(out StrictCombatLocalSnapshot snapshot, out StrictCombatLocalReadStatus status)
        {
            snapshot = null;
            status = StrictCombatLocalReadStatus.WrongThread;
            if (Environment.CurrentManagedThreadId != _ownerThreadId) return false;
            if (!TryRead(out var binding, out var state))
            {
                status = IsRetired ? StrictCombatLocalReadStatus.Retired : StrictCombatLocalReadStatus.StateUnavailable;
                return false;
            }
            if (state.LifeGeneration == 0)
            { status = StrictCombatLocalReadStatus.StateUnavailable; return false; }
            if (_localPlayer == null && !StrictCombatLocalPlayer.TryCapture(in binding, out _localPlayer, out status))
                return false;
            if (!_localPlayer.TryRead(this, in binding, in state, out var observation, out status)) return false;
            if (!IsCurrentLocalPlayerSnapshot(observation))
            { status = StrictCombatLocalReadStatus.Unavailable; return false; }
            snapshot = observation;
            status = StrictCombatLocalReadStatus.Success;
            return true;
        }

        /// <summary>
        /// Checks peer, attachment, frame, binding, accepted revision and life generation.
        /// Native numeric values can change within the frame; read again when current values are needed.
        /// </summary>
        public bool IsCurrentLocalPlayerSnapshot(StrictCombatLocalSnapshot snapshot)
        {
            return snapshot != null && ReferenceEquals(snapshot.Owner, this)
                && IsCurrent(out _) && _state.HasBinding && _state.HasSnapshot
                && ReferenceEquals(snapshot.Player, _localPlayer) && _localPlayer != null
                && _localPlayer.Matches(_state.Binding)
                && _state.Snapshot.Revision == snapshot.ServerState.Revision
                && _state.Snapshot.LifeGeneration == snapshot.ServerState.LifeGeneration
                && UnityEngine.Time.frameCount == snapshot.Frame && _localPlayer.IsCurrent(out _);
        }

        public bool TryReceive(Datagram message, out StrictCombatReceipt receipt,
            out bool requestCompleted)
        {
            receipt = default;
            requestCompleted = false;
            if (message == null || !ReferenceEquals(message.Peer, _peer) || !IsCurrent(out long now))
                return false;
            // Resource decisions use their own result APIs, never a fabricated pistol receipt.
            StrictCombatRebreatherWearResponse wear;
            if (_requiresRebreatherWearReceipts
                && (_requiresRebreatherProgress
                    ? StrictCombatRebreatherWearProtocol.TryReadProgressResponse(message.Payload.Span, out wear)
                    : StrictCombatRebreatherWearProtocol.TryReadResponse(message.Payload.Span, out wear)))
                return AcceptRebreatherWearResponse(in wear, now);
            StrictCombatRebreatherRefillResponse refill;
            if (_requiresRebreatherReceipts
                && (_requiresRebreatherProgress
                    ? StrictCombatRebreatherRefillProtocol.TryReadProgressResponse(message.Payload.Span, out refill)
                    : StrictCombatRebreatherRefillProtocol.TryReadResponse(message.Payload.Span, out refill)))
                return AcceptRebreatherRefillResponse(in refill, now);
            if (StrictCombatProtocol.TryReadBinding(message.Payload.Span, out var binding))
            {
                bool changed = !_state.HasBinding || binding.Generation != _state.Binding.Generation;
                if (!_state.TryAcceptBinding(in binding, now))
                {
                    if (_state.IsRetired) ReleaseLocalPlayer();
                    return false;
                }
                if (changed)
                {
                    ReleaseLocalPlayer();
                    ResetRespawn();
                    _localPlayer = null;
                    _lastObservation = now;
                    _state.TryBegin(StrictCombatAction.ReadState, now);
                }
                ObserveLocalPlayer();
                return true;
            }
            StrictCombatReceipt received;
            StrictCombatOwnedArmourState armour = null;
            StrictCombatOwnedRebreatherState rebreather = null;
            StrictCombatOwnedBreathingState breathing = null;
            if (_requiresBreathingReceipts)
            {
                if (!TryReceiveBreathingReceipt(message.Payload.Span, out received, out armour,
                    out rebreather, out breathing, out bool ready)) return false;
                if (!ready) return true;
                if (!CanAcceptOwnedBreathing(in received, breathing))
                { Retire(); return false; }
            }
            else if (_requiresRebreatherReceipts)
            {
                if (!TryReceiveRebreatherReceipt(message.Payload.Span, out received, out armour,
                    out rebreather, out bool ready)) return false;
                if (!ready) return true;
            }
            else if (_requiresArmourReceipts)
            {
                if (!TryReceiveArmourPage(message.Payload.Span, out received, out armour, out bool ready))
                    return false;
                if (!ready) return true;
            }
            else if (!StrictCombatProtocol.TryReadReceipt(message.Payload.Span, out received)) return false;
            if (received.Result == StrictCombatResult.NotReady && !_requiresReadiness) return false;
            if (_requiresRebreatherReceipts && !CanAcceptOwnedRebreather(in received, rebreather))
            { Retire(); return false; }
            if (_requiresArmourReceipts && !CanAcceptOwnedArmour(in received, armour))
            { Retire(); return false; }
            if (!_state.TryAcceptReceipt(in received, now))
            {
                if (_state.IsRetired) ReleaseLocalPlayer();
                return false;
            }
            if (_state.IsRetired) ReleaseLocalPlayer();
            else
            {
                if (armour != null) AcceptOwnedArmour(in received, armour);
                if (rebreather != null) AcceptOwnedRebreather(in received, rebreather);
                if (breathing != null) AcceptOwnedBreathing(in received, breathing);
                ObserveWeaponReceipt(in received);
            }
            if (!_state.HasPendingRequest)
            {
                ResetArmourFragments();
                ResetRebreatherFragments();
                ResetBreathingFragments();
            }
            receipt = received;
            requestCompleted = !_state.HasPendingRequest && !_state.IsRetired;
            _lastObservation = now;
            return true;
        }

        public bool Update()
        {
            if (!IsCurrent(out long now)) return false;
            ObserveLocalPlayer();
            if (_state.HasBinding && !_state.HasPendingRequest && now - _lastObservation >= 1000)
            {
                if (_state.TryBegin(StrictCombatAction.ReadState, now)) _lastObservation = now;
            }
            if (_state.HasPendingRebreatherWearRequest)
            {
                if (!_state.TryTakeRebreatherWearOutbound(now, out var wear)) return !_state.IsRetired;
                if (wear.AllowsTimedProgress != _requiresRebreatherProgress) { Retire(); return false; }
                Span<byte> wearPayload = stackalloc byte[StrictCombatRebreatherWearProtocol.RequestLength];
                int wearLength;
                bool written = _requiresRebreatherProgress
                    ? StrictCombatRebreatherWearProtocol.TryWriteProgressRequest(in wear, wearPayload, out wearLength)
                    : StrictCombatRebreatherWearProtocol.TryWriteRequest(in wear, wearPayload, out wearLength);
                if (!written)
                { Retire(); return false; }
                Datagrams.Send(_peer, wearPayload.Slice(0, wearLength));
                return true;
            }
            if (_state.HasPendingRebreatherRequest)
            {
                if (!_state.TryTakeRebreatherOutbound(now, out var refill)) return !_state.IsRetired;
                if (refill.AllowsTimedProgress != _requiresRebreatherProgress) { Retire(); return false; }
                Span<byte> refillPayload = stackalloc byte[StrictCombatRebreatherRefillProtocol.RequestLength];
                int refillLength;
                bool written = _requiresRebreatherProgress
                    ? StrictCombatRebreatherRefillProtocol.TryWriteProgressRequest(in refill, refillPayload, out refillLength)
                    : StrictCombatRebreatherRefillProtocol.TryWriteRequest(in refill, refillPayload, out refillLength);
                if (!written)
                { Retire(); return false; }
                Datagrams.Send(_peer, refillPayload.Slice(0, refillLength));
                return true;
            }
            if (!_state.TryTakeOutbound(now, out var intent)) return !_state.IsRetired;
            Span<byte> payload = stackalloc byte[Datagrams.MaxPayloadBytes];
            if (!StrictCombatProtocol.TryWriteIntent(in intent, payload, out int length))
            {
                _state.Retire();
                ReleaseLocalPlayer();
                return false;
            }
            // Queuing is not acknowledgement. Retry uses the same sequence and payload.
            Datagrams.Send(_peer, payload.Slice(0, length));
            return true;
        }

        public void Retire()
        {
            if (Environment.CurrentManagedThreadId != _ownerThreadId) return;
            _state.Retire();
            ReleaseLocalPlayer();
        }

        private void ReleaseLocalPlayer()
        {
            ReleaseReadiness();
            ReleaseNativeArmourBarrier();
            ReleaseLocalArmourBinding();
            ResetOwnedArmour();
            ResetOwnedRebreather();
            ResetOwnedBreathing();
            _lastRebreatherResponse = default;
            _lastRebreatherWearResponse = default;
            ReleaseLocalWeapon();
            _nativeRebreatherBarrier?.Retire();
            _nativeLifeBarrier?.Retire();
            _localPlayer?.Retire();
        }

        private void ObserveLocalPlayer()
        {
            if (!_state.HasBinding || _state.IsRetired) return;
            if (_localPlayer == null)
            {
                var binding = _state.Binding;
                StrictCombatLocalPlayer.TryCapture(in binding, out _localPlayer, out _);
            }
            else if (!_localPlayer.IsCurrent(out _)) _nativeLifeBarrier?.Retire();
        }

        private bool IsCurrent(out long now)
        {
            now = 0;
            if (Environment.CurrentManagedThreadId != _ownerThreadId) return false;
            if (_state.IsRetired) { ReleaseLocalPlayer(); return false; }
            if (!IsEnrolled(_peer, out var enrollment, out bool requiresArmourReceipts, out bool requiresRebreatherReceipts,
                    out bool requiresRebreatherWearReceipts, out bool requiresRebreatherProgress,
                    out bool requiresBreathingReceipts)
                || requiresArmourReceipts != _requiresArmourReceipts
                || requiresRebreatherReceipts != _requiresRebreatherReceipts
                || requiresRebreatherWearReceipts != _requiresRebreatherWearReceipts
                || requiresRebreatherProgress != _requiresRebreatherProgress
                || requiresBreathingReceipts != _requiresBreathingReceipts
                || ReadinessNegotiated(enrollment) != _requiresReadiness
                || (_state.HasPendingRebreatherWearRequest && !RebreatherWearNegotiated(enrollment))
                || (_state.HasPendingRebreatherRequest && !RebreatherRefillNegotiated(enrollment)))
            {
                _state.Retire();
                ReleaseLocalPlayer();
                return false;
            }
            now = Environment.TickCount64;
            if (!_state.Tick(now))
            {
                if (_state.IsRetired) ReleaseLocalPlayer();
                return false;
            }
            if (!_state.HasBinding && now - _started >= 8000)
            {
                _state.Retire();
                ReleaseLocalPlayer();
                return false;
            }
            return true;
        }

        private static bool IsEnrolled(ControlPeer peer, out EnrollmentSnapshot enrollment,
            out bool requiresArmourReceipts, out bool requiresRebreatherReceipts,
            out bool requiresRebreatherWearReceipts, out bool requiresRebreatherProgress,
            out bool requiresBreathingReceipts)
        {
            enrollment = null;
            requiresArmourReceipts = false;
            requiresRebreatherReceipts = false;
            requiresRebreatherWearReceipts = false;
            requiresRebreatherProgress = false;
            requiresBreathingReceipts = false;
            if (!(peer != null && peer.ServerSession == null
                && ClientEnrollment.TryRead(peer, out enrollment) == EnrollmentReadStatus.Success
                && ReferenceEquals(enrollment.Peer, peer) && enrollment.State == EnrollmentState.Enrolled
                && (enrollment.RequiredCapabilities & StrictCombatProtocol.RequiredCapabilities) == StrictCombatProtocol.RequiredCapabilities
                && (enrollment.OfferedCapabilities & StrictCombatProtocol.RequiredCapabilities) == StrictCombatProtocol.RequiredCapabilities))
                return false;
            requiresArmourReceipts = (enrollment.RequiredCapabilities & StrictCombatArmourProtocol.Capability) != 0;
            requiresRebreatherReceipts = (enrollment.RequiredCapabilities & StrictCombatRebreatherProtocol.Capability) != 0;
            requiresRebreatherWearReceipts = (enrollment.RequiredCapabilities & StrictCombatRebreatherWearProtocol.Capability) != 0;
            requiresRebreatherProgress = (enrollment.RequiredCapabilities & StrictCombatRebreatherProgressProtocol.Capability) != 0;
            requiresBreathingReceipts = (enrollment.RequiredCapabilities & StrictCombatBreathingProtocol.Capability) != 0;
            if (requiresRebreatherProgress
                && ((enrollment.RequiredCapabilities & StrictCombatRebreatherProgressProtocol.RequiredCapabilities)
                        != StrictCombatRebreatherProgressProtocol.RequiredCapabilities
                    || (enrollment.OfferedCapabilities & StrictCombatRebreatherProgressProtocol.RequiredCapabilities)
                        != StrictCombatRebreatherProgressProtocol.RequiredCapabilities)) return false;
            if ((enrollment.RequiredCapabilities & StrictCombatReadinessProtocol.Capability) != 0
                && (enrollment.OfferedCapabilities & StrictCombatReadinessProtocol.Capability) == 0) return false;
            return (!requiresArmourReceipts
                    || (enrollment.OfferedCapabilities & StrictCombatArmourProtocol.Capability) != 0)
                && (!requiresBreathingReceipts
                    || (enrollment.OfferedCapabilities & StrictCombatBreathingProtocol.Capability) != 0)
                && (!requiresRebreatherReceipts
                    || (enrollment.OfferedCapabilities & StrictCombatRebreatherProtocol.Capability) != 0)
                && (!requiresRebreatherWearReceipts || (requiresRebreatherReceipts
                    && (enrollment.OfferedCapabilities & StrictCombatRebreatherWearProtocol.RequiredCapabilities)
                        == StrictCombatRebreatherWearProtocol.RequiredCapabilities));
        }

        private static bool HasCapabilities(EnrollmentSnapshot enrollment, ulong required)
        {
            return (enrollment.RequiredCapabilities & required) == required
                && (enrollment.OfferedCapabilities & required) == required;
        }
    }
}
