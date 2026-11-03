// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Diagnostics;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>
    /// Exact-peer absolute sensor projection onto the original retired sensor. No stats,
    /// movement, camera transforms, contact callbacks or provider queries run on the source.
    /// Exact original-consumer prefixes schedule source projection. Prepared movement's
    /// lifecycle hold owns stale input/physics; the parent owns activation and failures.
    /// </summary>
    internal sealed partial class PlayerWaterPresentation : IDisposable
    {
        private static readonly long SendInterval = Stopwatch.Frequency / 50;
        private static readonly long HeartbeatInterval = Stopwatch.Frequency / 10;
        private static readonly long SampleLifetime = Stopwatch.Frequency / 4;
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly PlayerBridge _bridge;
        private readonly PlayerWaterContactAuthority.ServerContinuation _server;
        private readonly PlayerWaterContactAuthority _source;
        private bool _active, _pumping, _registered, _hasPending;
        private ulong _sendSequence, _receiveSequence, _contactGeneration, _sentContactGeneration, _appliedContactGeneration;
        private long _nextSend, _sentAt, _receivedAt;
        private int _receiveFrame = -1;
        private Collider _hit;
        private int _hitId, _hitScene;
        private byte[] _sendContact;
        private ReadOnlyMemory<byte> _receiveContact, _appliedContact;
        private PlayerWaterContactSample _lastSent;
        private PlayerWaterPresentationFrame _pending;

        internal bool IsDisposed { get; private set; }
        internal bool IsActive => _active && !IsDisposed && FailureReason == null;
        internal string FailureReason { get; private set; }
        internal bool HasAppliedSample { get; private set; }
        internal PlayerWaterContactSample AppliedSample { get; private set; }
        internal ulong AppliedSequence { get; private set; }

        // A requirement for the lifecycle owner, NOT evidence that native input/physics
        // is held. In particular, stopping the movement observer would not hold the rig.
        internal bool RequiresSourceInputHold => !_bridge.IsServer
            && (!IsActive || !IsSourceSchedulePrepared || !HasAppliedSample || _hasPending || !Fresh());

        private PlayerWaterPresentation(PlayerBridge bridge,
            PlayerWaterContactAuthority.ServerContinuation server, PlayerWaterContactAuthority source)
        { _bridge = bridge; _server = server; _source = source; }

        internal static bool TryPrepareServer(PlayerBridge bridge,
            PlayerWaterContactAuthority.ServerContinuation contacts,
            out PlayerWaterPresentation presentation, out string detail)
        {
            presentation = null;
            detail = "Water publication requires the exact prepared bridge and native server sensor owner.";
            if (bridge == null || !bridge.IsServer || bridge.IsGameplayActive || contacts == null
                || contacts._presentation != null
                || contacts.Carrier.Profile.SourceNetworkId != bridge.Plan.OldNetworkId) return false;
            PlayerWaterPresentation candidate = null;
            try
            {
                candidate = new(bridge, contacts, null);
                candidate.RequireCurrent();
                candidate.Register();
                contacts._presentation = candidate;
                presentation = candidate;
                detail = "Native water publisher prepared; no sample sent and no simulation activated.";
                return true;
            }
            catch (Exception error)
            { candidate?.Dispose(); detail = "Water publisher preparation refused: " + error.Message; return false; }
        }

        internal static bool TryPrepareSource(PlayerBridge bridge,
            PlayerWaterContactAuthority retiredSource,
            out PlayerWaterPresentation presentation, out string detail)
        {
            presentation = null;
            detail = "Water presentation requires the same retired sensor, retained rig and prepared migration bridge.";
            if (bridge == null || bridge.IsServer || bridge.IsGameplayActive || retiredSource == null
                || !retiredSource.MatchesPresentationSource(bridge)) return false;
            PlayerWaterPresentation candidate = null;
            try
            {
                candidate = new(bridge, null, retiredSource);
                candidate.RequireCurrent();
                candidate.Register();
                if (!retiredSource.TryBindPresentation(candidate)) { candidate.Dispose(); return false; }
                presentation = candidate;
                detail = "Retired native sensor bound without the old attachment; controls remain held pending an authoritative sample.";
                return true;
            }
            catch (Exception error)
            { candidate?.Dispose(); detail = "Water source preparation refused: " + error.Message; return false; }
        }

        private void Register()
        {
            _bridge.Retired += Dispose;
            Datagrams.Received += Receive;
            _registered = true;
        }

        /// <summary>
        /// Server: after native sensor/carrier and bridge activation. Source: after bridge
        /// activation but BEFORE releasing controls. False with no failure means wait for
        /// the first fresh server sample, retaining the input hold; no local dry fallback.
        /// </summary>
        internal bool TryActivate(out string detail)
        {
            detail = FailureReason ?? "Water activation awaits the completed bridge and first fresh authoritative sample.";
            if (IsDisposed || FailureReason != null || _pumping || _consumerDepth != 0 || Time.inFixedTimeStep
                || Environment.CurrentManagedThreadId != _thread || !_bridge.IsGameplayActive
                || !SwimmingTransitionsPrepared) return false;
            if (IsActive) return TryPump(out detail);
            try
            {
                RequireCurrent();
                if (_bridge.IsServer)
                {
                    if (!_server.IsActive) return false;
                }
                else
                {
                    RequireSourceScheduleCurrent();
                    if (!_hasPending || !Fresh()) return false;
                    ApplyPending();
                    if (!Fresh()) return false;
                }
                _active = true;
                detail = _bridge.IsServer ? "Exact native water publisher activated."
                    : "First authoritative water sample applied; original-consumer scheduling is armed, but native body/input release remains parent-owned.";
                return true;
            }
            catch (Exception error) { Fail(error.Message); detail = FailureReason; return false; }
        }

        /// <summary>
        /// Source may project on the game thread before a render OR fixed-step consumer;
        /// it never advances a native sensor/provider. Server runs after its render update.
        /// False requires the bound movement/lifecycle gameplay hold; this endpoint does
        /// not itself freeze the body or input. False without FailureReason is transient: keep
        /// pumping outside native consumers, without replaying accepted work or disposing.
        /// Disposal never resumes the retired sensor.
        /// </summary>
        internal bool TryPump(out string detail)
        {
            detail = FailureReason ?? "Water presentation is not active.";
            if (!IsActive || _pumping || _consumerDepth != 0 || Environment.CurrentManagedThreadId != _thread
                || (_bridge.IsServer && Time.inFixedTimeStep)
                || !_bridge.IsGameplayActive) return false;
            _pumping = true;
            try
            {
                RequireCurrent();
                if (!PumpSwimmingTransitions(out detail)) return false;
                if (_bridge.IsServer) return Publish(out detail);
                RequireSourceScheduleCurrent();
                if (HasAppliedSample && !_source.PresentationValuesMatch(AppliedSample, _hit))
                    throw new InvalidOperationException("A competing writer or retired contact changed source water presentation.");
                if (!Fresh())
                { detail = "Authoritative water presentation expired; keep controls held and await a fresh sample."; return false; }
                if (_hasPending) ApplyPending();
                if (!HitCurrent()) throw new InvalidOperationException("The authoritative water contact retired before its replacement observation arrived.");
                if (!Fresh())
                { detail = "Authoritative water presentation expired during projection; retain the real source hold."; return false; }
                detail = "Current server water observation retained on the original sensor; no local sensor work resumed.";
                return true;
            }
            catch (Exception error) { Fail(error.Message); detail = FailureReason; return false; }
            finally { _pumping = false; }
        }

        private bool Publish(out string detail)
        {
            detail = "No water publication is due.";
            long now = Stopwatch.GetTimestamp();
            if (now < _nextSend) return true;
            _nextSend = now + SendInterval;
            if (!_server.TryRead(out var sample, out detail) || !_server.IsCurrentSample(sample))
                throw new InvalidOperationException(detail);
            var hit = _server._sensor._lastWaterCollider;
            if (hit is not null && !Live(hit))
            { detail = "Native last-contact cleanup is still draining; no stale collider was published."; return true; }
            if (sample.InWater != Live(hit))
                throw new InvalidOperationException("Native water membership and its last-contact identity disagree.");
            if (_sendContact == null || (_hit?.Pointer ?? 0) != (hit?.Pointer ?? 0) || !HitCurrent())
            {
                var contact = PlayerWaterContactAuthority.DescribePresentationContact(hit);
                if (_contactGeneration == ulong.MaxValue) throw new InvalidOperationException("Water contact sequence exhausted.");
                _contactGeneration++;
                _sendContact = contact;
                RetainHit(hit);
            }
            if (_sendSequence != 0 && _sentContactGeneration == _contactGeneration
                && (_lastSent with { Frame = 0 }) == (sample with { Frame = 0 }) && now - _sentAt < HeartbeatInterval)
                return true;
            if (_sendSequence == ulong.MaxValue) throw new InvalidOperationException("Water presentation sequence exhausted.");
            var frame = new PlayerWaterPresentationFrame(_bridge.Plan.Id, _bridge.Plan.Generation, _bridge.NetworkId,
                _sendSequence + 1, _contactGeneration, sample);
            Span<byte> bytes = stackalloc byte[PlayerWaterPresentationFrame.Length];
            frame.Write(bytes, _sendContact);
            if (!_server.IsCurrentSample(sample)) throw new InvalidOperationException("Native water changed during presentation capture.");
            var result = Datagrams.Send(_bridge.Peer, bytes);
            if (result == DatagramSendResult.WindowFull)
            { detail = "Water transport is busy; the next absolute observation remains pending."; return true; }
            // Only WindowFull proves no native send occurred. Any other failure retires
            // this stream; it never retries an uncertain sequence as a gameplay action.
            _sendSequence = frame.Sequence;
            if (result != DatagramSendResult.Queued)
                throw new InvalidOperationException("Water presentation transport unavailable: " + result);
            _lastSent = sample; _sentAt = now; _sentContactGeneration = _contactGeneration;
            detail = "Authoritative water sample queued on the existing peer datagram channel; delivery is not implied.";
            return true;
        }

        private void Receive(Datagram datagram)
        {
            if (IsDisposed || FailureReason != null || Environment.CurrentManagedThreadId != _thread || datagram == null
                || !ReferenceEquals(datagram.Peer, _bridge.Peer)) return;
            try
            {
                if (PlayerWaterTransitionFrame.Matches(datagram.Payload.Span))
                {
                    RequireCurrent();
                    ReceiveSwimmingTransition(datagram);
                    return;
                }
                if (_bridge.IsServer || !PlayerWaterPresentationFrame.Matches(datagram.Payload.Span)) return;
                RequireCurrent();
                if (!PlayerWaterPresentationFrame.TryRead(datagram.Payload.Span, out var frame)
                    || frame.MigrationId != _bridge.Plan.Id || frame.Generation != _bridge.Plan.Generation
                    || frame.NetworkId != _bridge.NetworkId || frame.Sequence <= _receiveSequence
                    || frame.Sample.Frame < _receiveFrame || frame.ContactGeneration < _contactGeneration) return;
                var contact = datagram.Payload.Slice(PlayerWaterPresentationFrame.ContactOffset);
                if (frame.ContactGeneration == _contactGeneration && !_receiveContact.Span.SequenceEqual(contact.Span))
                    throw new InvalidOperationException("An authoritative water contact generation changed identity.");
                _receiveSequence = frame.Sequence; _receiveFrame = frame.Sample.Frame;
                _contactGeneration = frame.ContactGeneration; _receiveContact = contact;
                _pending = frame; _hasPending = true; _receivedAt = Stopwatch.GetTimestamp();
            }
            catch (Exception error) { Fail("Water reception failed: " + error.Message); }
        }

        private void ApplyPending()
        {
            var frame = _pending;
            Collider hit = _hit;
            if (!HasAppliedSample || frame.ContactGeneration != _appliedContactGeneration)
                hit = PlayerWaterContactAuthority.ResolvePresentationContact(_receiveContact.Span);
            else if (!_appliedContact.Span.SequenceEqual(_receiveContact.Span) || !HitCurrent())
                throw new InvalidOperationException("The projected water contact identity changed.");
            // ApplyPresentation writes only retired sensor caches. Native PAC/FPC
            // consumers, not this projection, own swimming/ground/body changes. Keep
            // their live hold through both checks; restoration never copies sensor data.
            _sourceMovement.ValidateSourceWaterProjection(this);
            _source.ApplyPresentation(this, frame.Sample, hit);
            _sourceMovement.ValidateSourceWaterProjection(this);
            RetainHit(hit);
            _appliedContactGeneration = frame.ContactGeneration; _appliedContact = _receiveContact;
            AppliedSample = frame.Sample; AppliedSequence = frame.Sequence; HasAppliedSample = true;
            _hasPending = false;
        }

        private bool Fresh() => _receivedAt != 0 && Stopwatch.GetTimestamp() - _receivedAt < SampleLifetime;
        private void RetainHit(Collider hit)
        {
            _hit = hit; _hitId = hit is null ? 0 : hit.GetInstanceID();
            _hitScene = hit is null ? 0 : hit.gameObject.scene.handle;
        }
        private bool HitCurrent() => _hit is null || (Live(_hit) && _hit.GetInstanceID() == _hitId
            && _hit.gameObject.scene.handle == _hitScene && _hit.gameObject.scene.isLoaded);

        private void RequireCurrent()
        {
            if (IsDisposed || FailureReason != null || Environment.CurrentManagedThreadId != _thread
                || _bridge.IsDisposed || !_bridge.TryReadCurrentTarget(out var target))
                throw new InvalidOperationException("The exact authenticated water bridge retired.");
            if (_bridge.IsServer)
            {
                if (_server.Carrier.Entity?.Pointer != target.Pointer || _server.Carrier.Profile.SourceNetworkId != _bridge.Plan.OldNetworkId
                    || _server.Carrier.Profile.PrefabId != _bridge.Plan.PrefabId
                    || _server.Carrier.Profile.StateTypeId != _bridge.Plan.StateTypeId)
                    throw new InvalidOperationException("Water publisher lost the exact migrated server actor.");
                _server.RequireCurrent();
            }
            else if (!_source.MatchesPresentationSource(_bridge))
                throw new InvalidOperationException("The retained source sensor, original control rig or migration identity changed.");
        }

        private static bool Live(UnityEngine.Object value) => value is not null && !value.WasCollected && value;
        private void Fail(string reason) { FailureReason ??= reason; }

        public void Dispose()
        {
            if (Environment.CurrentManagedThreadId != _thread)
                throw new InvalidOperationException("Retire water presentation on its game thread.");
            if (IsDisposed) return;
            IsDisposed = true; _active = false; _hasPending = false;
            ReleaseSourceSchedule();
            if (_registered)
            {
                _bridge.Retired -= Dispose;
                Datagrams.Received -= Receive;
                _registered = false;
            }
            // The source gate retains the final read-only observation until actual sensor
            // destruction. Never enable its runner or replay contact callbacks on teardown.
        }
    }
}
