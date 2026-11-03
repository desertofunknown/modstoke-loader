// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    /// <summary>
    /// Native-origin reservations for one exact migrated server carrier. A dequeue transfers
    /// an accepted network hit, not proof of armour, health or source presentation completion.
    /// </summary>
    internal sealed partial class PlayerCombatIngress : IDisposable
    {
        private const int MaximumPendingPerOwner = 64;
        private const int MaximumEventLifetimes = 2048;
        private static readonly Dictionary<IntPtr, PlayerCombatIngress> Owners = new();
        private static readonly Dictionary<IntPtr, NativeEvent> Events = new();
        private static readonly List<IntPtr> CollectedEvents = new();
        private static int _uncreatedReservations;
        private readonly PlayerBridge _bridge;
        private readonly BoltPlayerSetup _setup;
        private readonly BoltEntity _target;
        private readonly Bolt.Entity _attachment;
        private readonly global::Sons.Weapon.DamageController _controller;
        private readonly Il2CppSystem.Delegate _callback;
        private readonly Queue<AcceptedHit> _accepted = new();
        private int _pending, _producerDepth;
        private ulong _sequence;
        private long _lastProgress = Stopwatch.GetTimestamp();
        private bool _retired, _disposed;

        internal string Failure { get; private set; }
        internal Guid Migration => _bridge.Plan.Id;
        internal ulong TargetNetworkId { get; }
        internal int PendingCount => _pending;

        private PlayerCombatIngress(PlayerBridge bridge, BoltPlayerSetup setup, BoltEntity target)
        {
            _bridge = bridge;
            _setup = setup;
            _target = target;
            _attachment = target._entity;
            _controller = setup._damageController;
            _callback = _controller?._impactReceivers;
            TargetNetworkId = _attachment.NetworkId.PackedValue;
        }

        internal static bool TryBind(PlayerBridge bridge, BoltPlayerSetup exactSetup,
            HarmonySupportSnapshot support, Action<string> warning,
            out PlayerCombatIngress owner, out string detail)
        {
            owner = null;
            detail = "Native hit ingress needs installed producer hooks and the exact migrated server carrier.";
            if (bridge?.IsServer != true || bridge.IsDisposed) return false;
            // Only the future concrete combat consumer may bind this owner. Merely wiring
            // the dispatch observation point must not install hooks or take native hits.
            Install(support, warning);
            if (!OnThread || !_installed
                || !Live(exactSetup) || Owners.Count >= ControlTransport.MaxPeers
                || Owners.ContainsKey(exactSetup.Pointer)) return false;
            try
            {
                if (!bridge.TryReadCurrentTarget(out var target) || !Live(target)
                    || !Same(exactSetup._entity, target) || !Same(exactSetup.gameObject, target.gameObject) || !Native(target._entity)
                    || !target._entity.IsOwner || target._entity.NetworkId.PackedValue != bridge.NetworkId)
                    return false;
                foreach (var existing in Owners.Values)
                    if (ReferenceEquals(existing._bridge, bridge) || Same(existing._attachment, target._entity)) return false;
                var candidate = new PlayerCombatIngress(bridge, exactSetup, target);
                if (!candidate.CurrentTarget() || !candidate.HasNativeReceiver()) return false;
                Owners.Add(exactSetup.Pointer, candidate);
                bridge.Retired += candidate.Retire;
                owner = candidate;
                detail = "Exact native producer bound; the parent must connect dispatch and drain accepted hits into its combat owner.";
                return true;
            }
            catch (Exception error) { detail = "Native hit ingress binding refused: " + error.Message; return false; }
        }

        /// <summary>
        /// Call before ServerPlayerHits' subscriber/owner fast exit and before its release-owning
        /// try/finally, after __runOriginal. True means skip native Dispatch; this method alone
        /// has made the single FreeStorage attempt. Do not run Receiving for that event.
        /// </summary>
        internal static bool TryDispatch(Bolt.Event value)
        {
            if (Events.Count == 0 || !OnThread || !Native(value) || !Events.TryGetValue(value.Pointer, out var record)
                || !record.IsSameObject(value.Pointer)) return false;
            // A completed event can still be rooted by native callers. Its weak identity
            // blocks reentrant/duplicate dispatch without keeping the native object alive.
            if (record.Consumed) return true;
            var owner = record.Owner;
            try
            {
                if (!record.Queued || !record.ProducerExited || record.Payload == null
                    || !record.Payload.Matches(value))
                {
                    owner.Fail("Native hit dispatch lost its exact producer, enqueue or immutable event identity.");
                    // No trusted owner transfer was made. Leave the accepted native event alone.
                    AbandonObservation(record);
                    return false;
                }
                var hit = new AcceptedHit(owner, checked(++owner._sequence),
                    record.AcceptedAtTimestamp, record.Origin, record.Payload);
                owner._accepted.Enqueue(hit);
                record.Consumed = true;
                owner._lastProgress = Stopwatch.GetTimestamp();
                // Dispatch is now owned, before any native callback or forwarding. This is
                // a transfer into the retained queue, never a claim that Hit was applied.
                try { value.FreeStorage(); }
                catch (Exception error) { owner.Fail("Accepted hit storage release is uncertain: " + error.GetType().Name); }
                record.ReleaseStrongRoot();
                record.Owner = null;
                record.Origin = null;
                record.Payload = null;
                return true;
            }
            catch (Exception error)
            {
                owner?.Fail("Native hit owner dispatch could not be retained: " + error.Message);
                if (record.Consumed) return true;
                AbandonObservation(record);
                return false;
            }
        }

        /// <summary>
        /// One-way, game-thread handoff. The caller must already have capacity to retain this
        /// exact accepted hit until its numeric/armour/presentation transaction settles.
        /// Failure or retirement does not discard the old owner's accepted work.
        /// </summary>
        internal bool TryTake(out AcceptedHit hit)
        {
            RequireThread();
            hit = null;
            if (_disposed || _accepted.Count == 0) return false;
            hit = _accepted.Dequeue();
            _pending--;
            _lastProgress = Stopwatch.GetTimestamp();
            return true;
        }

        internal BoltMigrationAdmission ReadDrainState(out string detail)
        {
            RequireThread();
            if (_pending != 0 && Stopwatch.GetTimestamp() - _lastProgress > 5 * Stopwatch.Frequency)
                Fail("Native hit ingress has made no drain progress for five seconds; accepted work remains retained.");
            detail = Failure ?? (_disposed ? "Native hit ingress is disposed." : null);
            if (Failure != null || _disposed) return BoltMigrationAdmission.Rejected;
            if (_pending != 0 || _producerDepth != 0)
            { detail = "Waiting for native hit producers, dispatch and retained owner intake."; return BoltMigrationAdmission.Pending; }
            detail = "Native ingress drained; downstream combat completion must be checked separately.";
            return BoltMigrationAdmission.Ready;
        }

        private bool CurrentTarget() => !_retired && !_disposed && _bridge.IsServer && !_bridge.IsDisposed
            && _bridge.Peer.IsCurrent && _bridge.TryReadCurrentTarget(out var target) && Same(target, _target)
            && Live(_setup) && Same(_setup._entity, _target) && Same(_setup.gameObject, _target.gameObject) && _target.isAttached
            && Same(_target._entity, _attachment) && _attachment.IsOwner
            && Same(_attachment.UnityObject, _target) && _attachment.NetworkId.PackedValue == TargetNetworkId;

        private void Retire() { RequireThread(); _retired = true; }
        private void Fail(string detail) => Failure ??= detail;
        private static bool OnThread => Environment.CurrentManagedThreadId == _thread;
        private static void RequireThread()
        {
            if (!OnThread) throw new InvalidOperationException("Native hit ingress belongs to the installed game thread.");
        }

        public void Dispose()
        {
            RequireThread();
            if (_disposed) return;
            if (_pending != 0 || _producerDepth != 0)
                throw new InvalidOperationException("Drain accepted native hits before disposing their ingress owner.");
            _bridge.Retired -= Retire;
            Owners.Remove(_setup.Pointer);
            _disposed = true;
            PruneCollectedEvents();
        }

        private static void AbandonObservation(NativeEvent record)
        {
            // Only used while native routing still owns the event. Never remove an accepted
            // managed hit on timeout, failure, retirement or a changed scene/connection.
            if (record.Consumed || record.Abandoned) return;
            record.Abandoned = true;
            if (record.FactorySlotReserved) { _uncreatedReservations--; record.FactorySlotReserved = false; }
            if (record.EventPointer != IntPtr.Zero && Events.TryGetValue(record.EventPointer, out var existing)
                && ReferenceEquals(existing, record)) Events.Remove(record.EventPointer);
            record.Owner._pending--;
            record.ReleaseRoots();
        }

        private static void PruneCollectedEvents()
        {
            CollectedEvents.Clear();
            foreach (var pair in Events)
                if (pair.Value.Consumed && !pair.Value.IsSameObject(pair.Key)) CollectedEvents.Add(pair.Key);
            foreach (var pointer in CollectedEvents)
            { Events[pointer].ReleaseRoots(); Events.Remove(pointer); }
            CollectedEvents.Clear();
        }

        private static bool Native(Il2CppObjectBase value) => value is not null && !value.WasCollected && value.Pointer != IntPtr.Zero;
        private static bool Live(UnityEngine.Object value) => Native(value) && value;
        private static bool Same(Il2CppObjectBase left, Il2CppObjectBase right)
            => Native(left) && Native(right) && left.Pointer == right.Pointer;

        private sealed class NativeEvent
        {
            internal PlayerCombatIngress Owner;
            internal Origin Origin;
            internal EventPayload Payload;
            internal IntPtr EventPointer, FactoryStorage, FactoryMeta;
            internal long AcceptedAtTimestamp;
            internal bool Queued, ProducerExited, Consumed, Abandoned, FactorySlotReserved;
            internal nint StrongRoot, WeakRoot;
            internal bool IsSameObject(IntPtr pointer) => WeakRoot != 0 && IL2CPP.il2cpp_gchandle_get_target(WeakRoot) == pointer;
            internal void ReleaseStrongRoot()
            { if (StrongRoot != 0) { IL2CPP.il2cpp_gchandle_free(StrongRoot); StrongRoot = 0; } }
            internal void ReleaseRoots()
            {
                ReleaseStrongRoot();
                if (WeakRoot != 0) { IL2CPP.il2cpp_gchandle_free(WeakRoot); WeakRoot = 0; }
            }
        }
    }
}
