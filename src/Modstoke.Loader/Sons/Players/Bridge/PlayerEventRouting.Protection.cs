// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using TheForest.Utils;

namespace Modstoke.Sons
{
    internal static partial class PlayerEventRouting
    {
        private const int MaximumProtectionEvents = 4096;
        private static ProtectionLease _protection;
        private static bool _protectionPrepared, _protectionCapturing;
        private static string _protectionFault;

        internal static bool IsProtectionSelected => _protectionPrepared && IsSelected
            && System.Threading.Volatile.Read(ref _protectionFault) == null;

        /// <summary>
        /// Observes a finite queue cutoff without taking ownership of dispatch or storage. The
        /// caller closes input, drains native actions, then freezes on this same game thread.
        /// </summary>
        internal static bool TryBeginProtection(BoltEntity exactOriginal,
            out ProtectionLease lease, out string detail)
        {
            lease = null;
            detail = "Native event cohort capture requires the selected game-thread hooks and no existing lease.";
            if (!IsProtectionSelected || _protection != null || _protectionCapturing) return false;
            if (_protectionPumpDepth != 0 || _protectionDispatchToken != 0)
            {
                detail = "Wait for the active native event pump and its callbacks before capturing the cutoff.";
                return false;
            }
            ProtectionLease candidate = null;
            _protectionCapturing = true;
            try
            {
                candidate = new ProtectionLease(exactOriginal);
                if (!candidate.OriginalMatches())
                { detail = "The exact retained local PlayerNetwork attachment is unavailable."; return false; }
                CaptureProtectionQueue(candidate);
                if (!IsProtectionSelected || !candidate.OriginalMatches())
                    throw new InvalidOperationException("The source or dispatcher changed during cutoff capture.");
                _protection = candidate;
                lease = candidate;
                detail = "Captured " + candidate.Pending.Count
                    + " native queued events; dispatch and storage remain entirely native-owned.";
                return true;
            }
            catch (Exception error)
            {
                detail = "Native event cutoff capture refused: " + error.Message;
                return false;
            }
            finally
            {
                _protectionCapturing = false;
                if (lease == null) candidate?.ReleaseRoots();
            }
        }

        internal static void ReleaseProtectionAfterStop()
        {
            if (_protection != null || _protectionCapturing || _protectionPumpDepth != 0
                || _protectionDispatchToken != 0)
                throw new InvalidOperationException("Native event protection still owns a lease or callback scope.");
            _protectionPrepared = false;
            System.Threading.Volatile.Write(ref _protectionFault, null);
            _protectionDispatchSequence = 0;
        }

        internal sealed class ProtectionLease : IDisposable
        {
            private readonly BoltEntity _original;
            private readonly IntPtr _originalPointer, _attachmentPointer, _rootPointer;
            private readonly ulong _networkId;
            internal readonly Dictionary<IntPtr, ProtectionEvent> Pending = new();
            internal IntPtr QueuePointer;
            internal int NextOrdinal;
            internal bool Disposed;
            internal string Failure;

            internal ProtectionLease(BoltEntity original)
            {
                _original = original;
                if (original == null || original.WasCollected || !original) return;
                _originalPointer = original.Pointer;
                _attachmentPointer = original._entity?.Pointer ?? IntPtr.Zero;
                _rootPointer = original.gameObject?.Pointer ?? IntPtr.Zero;
                _networkId = original.networkId.PackedValue;
            }

            // Observation only; this does not authorize gameplay ingress.
            internal bool IsDispatchingAcceptedEvent => !Disposed && IsSelected
                && ReferenceEquals(_protection, this) && ReferenceEquals(_protectionDispatchLease, this)
                && _protectionDispatchEvent != null;

            internal bool CanRelease => Environment.CurrentManagedThreadId == _thread
                && (Disposed || (!_protectionCapturing && _protectionPumpDepth == 0 && _protectionDispatchToken == 0));

            internal BoltMigrationAdmission ReadAdmission(out string detail)
            {
                detail = Failure ?? "Native event cohort requires its active selected game-thread lease.";
                if (Disposed || !ReferenceEquals(_protection, this) || !IsProtectionSelected || Failure != null)
                    return BoltMigrationAdmission.Rejected;
                try
                {
                    if (!OriginalMatches() || !ProtectionQueueMatches(QueuePointer))
                    {
                        Fail("The captured player attachment or native dispatcher queue was replaced.");
                        detail = Failure;
                        return BoltMigrationAdmission.Rejected;
                    }
                    if (Pending.Count != 0 || _protectionDispatchToken != 0 || _protectionPumpDepth != 0)
                    {
                        detail = "Waiting for " + Pending.Count
                            + " pre-cutoff native events and the current dispatch callback stack.";
                        return BoltMigrationAdmission.Pending;
                    }
                    detail = "All captured native dispatches returned normally; native action continuations require separate admission.";
                    return BoltMigrationAdmission.Ready;
                }
                catch (Exception error)
                {
                    Fail("Native event cohort admission is unreadable (" + error.GetType().Name + ").");
                    detail = Failure;
                    return BoltMigrationAdmission.Rejected;
                }
            }

            internal bool OriginalMatches() => _original != null && !_original.WasCollected && _original
                && _originalPointer != IntPtr.Zero && _attachmentPointer != IntPtr.Zero && _networkId != 0
                && _original.Pointer == _originalPointer && _original.isAttached && _original.isOwner
                && _original.prefabId == BoltPrefabs.PlayerNetwork
                && _original._entity?.Pointer == _attachmentPointer && _original.networkId.PackedValue == _networkId
                && _original.gameObject?.Pointer == _rootPointer && LocalPlayer.Entity?.Pointer == _originalPointer
                && LocalPlayer.GameObject?.Pointer == _rootPointer;

            internal void Fail(string detail) => Failure ??= detail;

            internal void ReleaseRoots()
            {
                foreach (var pending in Pending.Values) pending.ReleaseRoot();
            }

            /// <summary>
            /// Caller owns cancellation/completed-repair proof. Cancellation can release a
            /// pending cohort: its events are still in the untouched native queue.
            /// </summary>
            public void Dispose()
            {
                if (Environment.CurrentManagedThreadId != _thread)
                    throw new InvalidOperationException("Release native event protection on its game thread.");
                if (Disposed) return;
                if (!CanRelease)
                    throw new InvalidOperationException("Wait for native event callbacks before releasing their cohort.");
                ReleaseRoots();
                Pending.Clear();
                Disposed = true;
                if (ReferenceEquals(_protection, this)) _protection = null;
            }
        }
    }
}
