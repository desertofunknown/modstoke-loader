// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons.Combat
{
    public sealed partial class StrictCombatClient
    {
        private StrictCombatLocalArmourBinding _localArmourBinding;

        /// <summary>
        /// Binds accepted resources to live native pieces once per player life. A binding
        /// does not grant resources, acquire a write barrier or change native durability.
        /// </summary>
        public bool TryBindLocalArmour(out StrictCombatLocalArmourBinding binding,
            out StrictCombatArmourBindingStatus status)
        {
            binding = null;
            if (!BeginArmourRead(out var readStatus))
            { status = ArmourBindingStatus(readStatus); return false; }
            try
            {
                if (!TryRead(out var acceptedBinding, out var state))
                { status = StrictCombatArmourBindingStatus.StateUnavailable; return false; }
                if (_localArmourBinding != null
                    && (!acceptedBinding.SameBinding(_localArmourBinding.Binding)
                        || state.LifeGeneration != _localArmourBinding.LifeGeneration))
                {
                    if (RetainsArmourAcrossRespawn(_localArmourBinding, in acceptedBinding, in state))
                    { status = StrictCombatArmourBindingStatus.LifeChanged; return false; }
                    ReleaseLocalArmourBinding();
                }
                if (_localArmourBinding != null && _localArmourBinding.IsRetired)
                { status = StrictCombatArmourBindingStatus.Retired; return false; }
                if (!TryReadArmourBindingInputs(out var owned, out var native, out status)) return false;
                if (_localArmourBinding == null)
                {
                    if (!StrictCombatLocalArmourBinding.TryCreate(this, owned, native, out var candidate, out status))
                        return false;
                    if (!HasCurrentArmourBindingInputs(owned, native, out status))
                    { candidate.Retire(); return false; }
                    _localArmourBinding = candidate;
                }
                else if (!ValidateLocalArmourBinding(_localArmourBinding, owned, native, out status))
                    return false;
                binding = _localArmourBinding;
                status = StrictCombatArmourBindingStatus.Success;
                return true;
            }
            catch (Exception)
            {
                _localArmourBinding?.Retire();
                status = StrictCombatArmourBindingStatus.Failed;
                return false;
            }
            finally { _readingArmour = false; }
        }

        /// <summary>
        /// Checks the retained pieces against current accepted resources and native identity.
        /// Failure after observing a replacement retires the binding for that life.
        /// </summary>
        public bool IsCurrentLocalArmourBinding(StrictCombatLocalArmourBinding binding,
            out StrictCombatArmourBindingStatus status)
        {
            if (!BeginArmourRead(out var readStatus))
            { status = ArmourBindingStatus(readStatus); return false; }
            try
            {
                status = StrictCombatArmourBindingStatus.Unavailable;
                if (binding == null || !ReferenceEquals(binding.Owner, this)
                    || !ReferenceEquals(binding, _localArmourBinding)) return false;
                status = StrictCombatArmourBindingStatus.Retired;
                if (binding.IsRetired) return false;
                if (!TryRead(out var acceptedBinding, out var state))
                { status = StrictCombatArmourBindingStatus.StateUnavailable; return false; }
                if (!acceptedBinding.SameBinding(binding.Binding) || state.LifeGeneration != binding.LifeGeneration)
                {
                    if (!RetainsArmourAcrossRespawn(binding, in acceptedBinding, in state)) binding.Retire();
                    status = StrictCombatArmourBindingStatus.LifeChanged;
                    return false;
                }
                return TryReadArmourBindingInputs(out var owned, out var native, out status)
                    && ValidateLocalArmourBinding(binding, owned, native, out status);
            }
            catch (Exception)
            {
                if (ReferenceEquals(binding, _localArmourBinding)) binding?.Retire();
                status = StrictCombatArmourBindingStatus.Failed;
                return false;
            }
            finally { _readingArmour = false; }
        }

        private bool TryReadArmourBindingInputs(out StrictCombatOwnedArmourState owned,
            out StrictCombatArmourSnapshot native, out StrictCombatArmourBindingStatus status)
        {
            native = null;
            status = StrictCombatArmourBindingStatus.StateUnavailable;
            if (!TryReadOwnedArmour(out var binding, out var state, out owned)) return false;
            status = StrictCombatArmourBindingStatus.Unprovisioned;
            if (!owned.IsProvisioned) return false;
            if (!TryReadArmourSnapshot(out native, out var readStatus))
            {
                status = ArmourBindingStatus(readStatus);
                // Incomplete synchronization and a stale frame can be read again. Loss
                // of native identity cannot authorize adopting a replacement this life.
                if (readStatus != StrictCombatArmourReadStatus.StaleSnapshot
                    && readStatus != StrictCombatArmourReadStatus.StateUnavailable)
                    _localArmourBinding?.Retire();
                return false;
            }
            status = StrictCombatArmourBindingStatus.StaleObservation;
            return binding.SameBinding(native.Binding) && state.Revision == native.ServerState.Revision
                && state.LifeGeneration == native.ServerState.LifeGeneration;
        }

        private bool ValidateLocalArmourBinding(StrictCombatLocalArmourBinding binding,
            StrictCombatOwnedArmourState owned, StrictCombatArmourSnapshot native,
            out StrictCombatArmourBindingStatus status)
        {
            if (!binding.Matches(owned, native, out status))
            { binding.Retire(); return false; }
            if (HasCurrentArmourBindingInputs(owned, native, out status)) return true;
            if (status == StrictCombatArmourBindingStatus.NativeStateUnavailable
                || status == StrictCombatArmourBindingStatus.Retired
                || status == StrictCombatArmourBindingStatus.Failed)
                binding.Retire();
            return false;
        }

        private bool HasCurrentArmourBindingInputs(StrictCombatOwnedArmourState owned,
            StrictCombatArmourSnapshot native, out StrictCombatArmourBindingStatus status)
        {
            status = StrictCombatArmourBindingStatus.StaleObservation;
            if (!HasCurrentArmourOwner(native, out var readStatus))
            { status = ArmourBindingStatus(readStatus); return false; }
            if (!TryReadOwnedArmour(out var binding, out var state, out var current)
                || !ReferenceEquals(owned, current) || !binding.SameBinding(native.Binding)
                || state.Revision != native.ServerState.Revision || state.LifeGeneration != native.ServerState.LifeGeneration)
                return false;
            status = StrictCombatArmourBindingStatus.Success;
            return true;
        }

        private void ReleaseLocalArmourBinding()
        {
            _localArmourBinding?.Retire();
            _localArmourBinding = null;
        }

        private static StrictCombatArmourBindingStatus ArmourBindingStatus(StrictCombatArmourReadStatus status)
            => status switch
            {
                StrictCombatArmourReadStatus.Success => StrictCombatArmourBindingStatus.Success,
                StrictCombatArmourReadStatus.WrongThread => StrictCombatArmourBindingStatus.WrongThread,
                StrictCombatArmourReadStatus.Retired => StrictCombatArmourBindingStatus.Retired,
                StrictCombatArmourReadStatus.StateUnavailable => StrictCombatArmourBindingStatus.StateUnavailable,
                StrictCombatArmourReadStatus.StaleSnapshot => StrictCombatArmourBindingStatus.StaleObservation,
                StrictCombatArmourReadStatus.LimitReached => StrictCombatArmourBindingStatus.LimitReached,
                StrictCombatArmourReadStatus.Failed => StrictCombatArmourBindingStatus.Failed,
                StrictCombatArmourReadStatus.Unavailable => StrictCombatArmourBindingStatus.Unavailable,
                _ => StrictCombatArmourBindingStatus.NativeStateUnavailable
            };
    }
}
