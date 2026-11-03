// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Endnight.Utilities;
using global::Sons.Utilities;

namespace Modstoke.Sons.Combat
{
    public sealed partial class StrictCombatClient
    {
        private Guid _impactAudioEpoch;
        private ulong _impactAudioGeneration, _lastImpactAudioSequence;
        private bool _hasImpactAudioFrame;
        private int _impactAudioFrame, _impactSoundsThisFrame;

        /// <summary>
        /// Consumes a server sound observation on the game thread. True means the packet was recognized,
        /// not that sound was audible. Stale, repeated, unsupported and over-budget observations are dropped.
        /// </summary>
        public bool TryReceiveImpactAudio(Datagram message)
        {
            if (message == null || !ReferenceEquals(message.Peer, _peer) || !IsCurrent(out _)
                || !StrictCombatImpactAudioProtocol.TryRead(message.Payload.Span, out var audio)) return false;
            return ReceiveImpactAudio(in audio, null);
        }

        /// <summary>Consumes a server impact sound and normal for qualified local particle presentation.</summary>
        public bool TryReceiveImpactPresentation(Datagram message)
        {
            if (message == null || !ReferenceEquals(message.Peer, _peer) || !IsCurrent(out _)
                || !StrictCombatImpactPresentationProtocol.TryRead(message.Payload.Span, out var presentation)) return false;
            if (!HasImpactPresentationCapability()) return true;
            var audio = presentation.Audio;
            return ReceiveImpactAudio(in audio, presentation);
        }

        private bool ReceiveImpactAudio(in StrictCombatImpactAudio audio, StrictCombatImpactPresentation? presentation)
        {
            if (!HasImpactAudioCapability() || !TryRead(out var binding, out var state)
                || !MatchesImpactAudio(in audio, in binding, in state)) return true;

            if (_impactAudioEpoch != binding.Epoch || _impactAudioGeneration != binding.Generation)
            {
                _impactAudioEpoch = binding.Epoch;
                _impactAudioGeneration = binding.Generation;
                _lastImpactAudioSequence = 0;
            }
            if (audio.Sequence <= _lastImpactAudioSequence) return true;
            // Consume before any native work. Lost, reordered and uncertain sounds never retry.
            _lastImpactAudioSequence = audio.Sequence;
            if (_presentingWeapon || _projectingHealth || _placingRespawn || _projectingArmour
                || _projectingLife || _projectingAmmunition || _updatingEquipment || _readingWeaponInput) return true;

            try
            {
                int frame = UnityEngine.Time.frameCount;
                if (!_hasImpactAudioFrame || _impactAudioFrame != frame)
                {
                    _hasImpactAudioFrame = true;
                    _impactAudioFrame = frame;
                    _impactSoundsThisFrame = 0;
                }
                if (_impactSoundsThisFrame >= 2) return true;
                _impactSoundsThisFrame++;
                // Share the existing presentation exclusion with life, equipment and armour projection.
                _presentingWeapon = true;
                try
                {
                    try { PlayImpactAudio(in audio, frame); }
                    catch (Exception) { /* A sound failure does not prevent qualified particles. */ }
                    if (presentation.HasValue)
                    {
                        var value = presentation.Value;
                        StrictCombatImpactParticles.TryPresent(this, in value, frame);
                    }
                }
                finally { _presentingWeapon = false; }
            }
            catch (Exception)
            {
                // Presentation must not retire or mutate the authoritative combat state.
            }
            return true;
        }

        private void PlayImpactAudio(in StrictCombatImpactAudio audio, int frame)
        {
            string path = audio.Sound switch
            {
                BallisticImpactSound.HardSurface => "event:/SotF Events/Combat/Bullets/bullet_impact_generic",
                BallisticImpactSound.Flesh => "event:/SotF Events/Combat/Bullets/bullet_impact_body",
                BallisticImpactSound.Wood => "event:/SotF Events/Combat/Bullets/bullet_dirt_impact",
                _ => null
            };
            if (path == null || !ImpactAudioCurrent(in audio, frame)
                || !FMOD_StudioSystem.TryGetInstance(out var host, false) || host == null || !host.isInitialized)
                return;
            var system = host.System;
            if (!system.isValid()) return;
            // Use the same description cache as the native one-shot dispatcher. A changed bank
            // must still describe an event that ends on its own; no released handle is retained.
            var description = host.GetEventDescription(path, false);
            if (!description.isValid() || description.isOneshot(out bool oneShot) != FMOD.RESULT.OK || !oneShot)
                return;
            if (!ImpactAudioCurrent(in audio, frame)
                || !FMOD_StudioSystem.TryGetInstance(out var currentHost, false) || currentHost == null
                || currentHost.Pointer != host.Pointer || !currentHost.isInitialized
                || currentHost.System.handle != system.handle || !system.isValid()) return;

            // This position-only leaf starts and releases local audio. It does not instantiate
            // impact prefabs, dispatch receivers, create AI stimuli or forward another network event.
            FMODCommon.PlayOneshot(path, new UnityEngine.Vector3(audio.Position.X, audio.Position.Y, audio.Position.Z),
                FMODCommon.NetworkRole.None, Array.Empty<Il2CppSystem.Object>());
        }

        private bool ImpactAudioCurrent(in StrictCombatImpactAudio audio, int frame)
        {
            return _presentingWeapon && UnityEngine.Time.frameCount == frame
                && HasImpactAudioCapability() && TryRead(out var binding, out var state)
                && MatchesImpactAudio(in audio, in binding, in state)
                && _nativeLifeBarrier != null && IsCurrentNativeHealthOwner(_nativeLifeBarrier);
        }

        private bool HasImpactAudioCapability()
            => HasImpactCapability(StrictCombatImpactAudioProtocol.Capability);

        private bool HasImpactPresentationCapability()
            => HasImpactCapability(StrictCombatImpactPresentationProtocol.Capability);

        private bool HasImpactCapability(ulong capability)
        {
            return ClientEnrollment.TryRead(_peer, out var enrollment) == EnrollmentReadStatus.Success
                && ReferenceEquals(enrollment.Peer, _peer) && enrollment.State == EnrollmentState.Enrolled
                && (enrollment.RequiredCapabilities & capability) != 0 && (enrollment.OfferedCapabilities & capability) != 0;
        }

        internal bool IsCurrentImpactPresentation(in StrictCombatImpactAudio audio, int frame)
            => HasImpactPresentationCapability() && ImpactAudioCurrent(in audio, frame);

        internal bool IsCurrentImpactParticleOwner(in StrictCombatImpactAudio audio)
            => HasImpactAudioCapability() && HasImpactPresentationCapability()
                && TryRead(out var binding, out var state) && MatchesImpactAudio(in audio, in binding, in state)
                && _nativeLifeBarrier != null && IsCurrentNativeHealthOwner(_nativeLifeBarrier);

        internal bool TryReadImpactListenerPosition(out UnityEngine.Vector3 position)
        {
            position = default;
            return _localPlayer != null && _nativeLifeBarrier != null
                && IsCurrentNativeHealthOwner(_nativeLifeBarrier) && _localPlayer.TryReadPresentationPosition(out position);
        }

        private static bool MatchesImpactAudio(in StrictCombatImpactAudio audio, in StrictCombatBinding binding,
            in StrictCombatSnapshot state)
            => audio.ReceiverEpoch == binding.Epoch && audio.ReceiverGeneration == binding.Generation
                && audio.ReceiverActorNetworkId == binding.ActorNetworkId
                && audio.ReceiverLifeGeneration == state.LifeGeneration;
    }
}
