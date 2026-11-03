// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Sons.Combat
{
    public sealed partial class StrictCombatClient
    {
        private bool _readingArmour;
        private bool _hasArmourReadFrame;
        private int _armourReadFrame;
        private int _armourReads;

        /// <summary>
        /// Copies the exact local player's native armour under the latest accepted life state.
        /// Durability, item IDs and multipliers are observations, not server-owned resources.
        /// </summary>
        public bool TryReadLocalArmour(out StrictCombatArmourSnapshot snapshot,
            out StrictCombatArmourReadStatus status)
        {
            snapshot = null;
            if (!BeginArmourRead(out status)) return false;
            try { return TryReadArmourSnapshot(out snapshot, out status); }
            catch (Exception) { status = StrictCombatArmourReadStatus.Failed; return false; }
            finally { _readingArmour = false; }
        }

        // Called only while this adapter holds an armour-read budget entry.
        private bool TryReadArmourSnapshot(out StrictCombatArmourSnapshot snapshot,
            out StrictCombatArmourReadStatus status)
        {
            snapshot = null;
            if (!TryReadLocalPlayer(out var player, out var localStatus))
            { status = ArmourReadStatus(localStatus); return false; }
            if (!player.Player.TryGetArmour(out var system, out localStatus))
            { status = ArmourReadStatus(localStatus); return false; }
            if (!StrictCombatArmour.TryCapture(system, out var native, out status)) return false;
            var candidate = new StrictCombatArmourSnapshot(player, native);
            if (!IsCurrentArmourSnapshot(candidate, out status)) return false;
            snapshot = candidate;
            return true;
        }

        /// <summary>
        /// Rechecks the accepted revision/life, native attachment, frame, ordered slots,
        /// copied values and retained object identities. Consumes one armour-read budget entry.
        /// </summary>
        public bool IsCurrentLocalArmourSnapshot(StrictCombatArmourSnapshot snapshot,
            out StrictCombatArmourReadStatus status)
        {
            if (!BeginArmourRead(out status)) return false;
            try { return IsCurrentArmourSnapshot(snapshot, out status); }
            catch (Exception) { status = StrictCombatArmourReadStatus.Failed; return false; }
            finally { _readingArmour = false; }
        }

        /// <summary>
        /// Compares a retained piece with a fresh snapshot in the same player life. Requires
        /// the same live spawned instance; an item ID or empty slot cannot establish identity.
        /// Old copied values remain stale. This does not authorize native armour changes.
        /// </summary>
        public bool IsSameLocalArmourPiece(StrictCombatArmourSnapshot original,
            StrictCombatArmourSnapshot current, int slot, out StrictCombatArmourReadStatus status)
        {
            if (!BeginArmourRead(out status)) return false;
            try
            {
                if (original == null || !ReferenceEquals(original.Player.Owner, this)
                    || !IsCurrentArmourSnapshot(current, out status)) return false;
                status = StrictCombatArmourReadStatus.AttachmentChanged;
                if (!ReferenceEquals(original.Player.Player, current.Player.Player)
                    || !original.Binding.SameBinding(current.Binding)
                    || original.ServerState.LifeGeneration != current.ServerState.LifeGeneration) return false;
                status = StrictCombatArmourReadStatus.IdentityUnavailable;
                if (!original.Native.HasSamePiece(current.Native, slot)) return false;
                return HasCurrentArmourOwner(current, out status);
            }
            catch (Exception) { status = StrictCombatArmourReadStatus.Failed; return false; }
            finally { _readingArmour = false; }
        }

        private bool IsCurrentArmourSnapshot(StrictCombatArmourSnapshot snapshot,
            out StrictCombatArmourReadStatus status)
        {
            status = StrictCombatArmourReadStatus.StaleSnapshot;
            if (snapshot == null || !IsCurrentLocalPlayerSnapshot(snapshot.Player)) return false;
            if (!snapshot.Player.Player.TryGetArmour(out var system, out var localStatus))
            { status = ArmourReadStatus(localStatus); return false; }
            if (!StrictCombatArmour.TryCapture(system, out var current, out status)) return false;
            status = StrictCombatArmourReadStatus.StaleSnapshot;
            if (!snapshot.Native.Matches(current)) return false;
            return HasCurrentArmourOwner(snapshot, out status);
        }

        private bool HasCurrentArmourOwner(StrictCombatArmourSnapshot snapshot,
            out StrictCombatArmourReadStatus status)
        {
            status = StrictCombatArmourReadStatus.AttachmentChanged;
            if (!snapshot.Player.Player.TryGetArmour(out var system, out _)
                || system.Pointer != snapshot.Native.System.Pointer
                || system.GetInstanceID() != snapshot.ArmourSystemInstanceId) return false;
            status = StrictCombatArmourReadStatus.StaleSnapshot;
            if (!IsCurrentLocalPlayerSnapshot(snapshot.Player)) return false;
            status = StrictCombatArmourReadStatus.Success;
            return true;
        }

        private bool BeginArmourRead(out StrictCombatArmourReadStatus status)
        {
            status = StrictCombatArmourReadStatus.WrongThread;
            if (Environment.CurrentManagedThreadId != _ownerThreadId) return false;
            status = StrictCombatArmourReadStatus.Unavailable;
            if (_readingArmour || _projectingHealth || _placingRespawn || _presentingWeapon
                || _projectingArmour || _projectingAmmunition || _updatingEquipment) return false;
            try
            {
                if (!IsCurrent(out _))
                { status = IsRetired ? StrictCombatArmourReadStatus.Retired : StrictCombatArmourReadStatus.StateUnavailable; return false; }
                int frame = Time.frameCount;
                if (!_hasArmourReadFrame || frame != _armourReadFrame)
                {
                    _hasArmourReadFrame = true;
                    _armourReadFrame = frame;
                    _armourReads = 0;
                }
                status = StrictCombatArmourReadStatus.LimitReached;
                if (_armourReads >= 64) return false;
                _armourReads++;
                _readingArmour = true;
                status = StrictCombatArmourReadStatus.Unavailable;
                return true;
            }
            catch (Exception) { status = StrictCombatArmourReadStatus.Failed; return false; }
        }

        private static StrictCombatArmourReadStatus ArmourReadStatus(StrictCombatLocalReadStatus status)
            => status switch
            {
                StrictCombatLocalReadStatus.WrongThread => StrictCombatArmourReadStatus.WrongThread,
                StrictCombatLocalReadStatus.Retired => StrictCombatArmourReadStatus.Retired,
                StrictCombatLocalReadStatus.StateUnavailable => StrictCombatArmourReadStatus.StateUnavailable,
                StrictCombatLocalReadStatus.InvalidState => StrictCombatArmourReadStatus.InvalidState,
                StrictCombatLocalReadStatus.ActorMismatch or StrictCombatLocalReadStatus.AttachmentChanged
                    or StrictCombatLocalReadStatus.WorldChanged => StrictCombatArmourReadStatus.AttachmentChanged,
                StrictCombatLocalReadStatus.Failed => StrictCombatArmourReadStatus.Failed,
                _ => StrictCombatArmourReadStatus.NativeUnavailable
            };
    }
}
