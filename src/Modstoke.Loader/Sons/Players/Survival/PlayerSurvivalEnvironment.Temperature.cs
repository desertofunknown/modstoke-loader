// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime.Injection;
using Modstoke.Loader;
using global::Sons.Atmosphere;
using UnityEngine;

namespace Modstoke.Sons
{
    internal readonly record struct PlayerTemperatureHistory(
        ulong Sequence, int LastCallbackFrame, bool Inside, float Temperature);

    /// <summary>
    /// Folds native receiver callbacks in delivery order, independently of the survival
    /// consumer's fields. Native volumes retain their actual collider lists, bounds checks,
    /// LateUpdate ordering and disable/exit behavior. This is not an environment aggregate.
    /// </summary>
    internal sealed partial class PlayerSurvivalEnvironmentTemperature : IDisposable
    {
        private static readonly ConcurrentDictionary<nint, PlayerSurvivalEnvironmentTemperature> Owners = new();
        private static readonly ConditionalWeakTable<PlayerCarrier, PlayerSurvivalSnapshot> PreparedCarriers = new();
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly PlayerBridge _bridge;
        private readonly PlayerCarrier _carrier;
        private readonly PlayerSurvivalSnapshot _snapshot;
        private readonly BoltEntity _entity;
        private readonly PlayerStats _stats;
        private readonly CapsuleCollider _collider;
        private readonly Rigidbody _body;
        private readonly int _entityId, _statsId, _colliderId, _scene;
        private readonly nint _statsPointer;
        private ITemperatureModifierReceiver _receiver;
        private PlayerTemperatureReceiver _proxy;
        private PlayerTemperatureHistory _history;
        private bool _registered;

        internal bool IsDisposed { get; private set; }
        internal string FailureReason { get; private set; }

        private PlayerSurvivalEnvironmentTemperature(PlayerBridge bridge,
            PlayerCarrier carrier, PlayerStats stats)
        {
            _bridge = bridge; _carrier = carrier; _stats = stats;
            _snapshot = carrier.RestoredSurvival;
            _entity = carrier.Entity; _collider = carrier.PrimaryCollider; _body = carrier.Body;
            _entityId = _entity.GetInstanceID(); _statsId = stats.GetInstanceID();
            _colliderId = _collider.GetInstanceID(); _scene = _entity.gameObject.scene.handle;
            _statsPointer = stats.Pointer;
            _history = new(0, -1, _snapshot.TemperatureVolumeBaselineInside,
                _snapshot.ReadScalar(PlayerSurvivalScalar.TemperatureVolumeTemperature));
        }

        /// <summary>
        /// Prepare on the game thread after approved survival restore, before releasing
        /// carrier physics. No volume scan, synthetic enter, local-player lookup or clock.
        /// </summary>
        internal static bool TryPrepare(PlayerBridge bridge, PlayerCarrier carrier,
            HarmonySupportSnapshot support,
            out PlayerSurvivalEnvironmentTemperature producer, out string detail)
        {
            producer = null;
            detail = "Temperature history requires the exact held, restored server carrier before physics release.";
            if (bridge == null || carrier == null || bridge.IsDisposed || !bridge.IsServer
                || bridge.IsGameplayActive || carrier.IsDisposed || !carrier.IsSurvivalRestored
                || carrier.IsPhysicsActive || Time.inFixedTimeStep) return false;
            PlayerSurvivalEnvironmentTemperature candidate = null;
            try
            {
                if (PreparedCarriers.TryGetValue(carrier, out _))
                { detail = "A retired temperature history cannot restart from the same carrier's old baseline."; return false; }
                if (!carrier.TryReadRestoredServerStats(out var stats)
                    || !Alive(carrier.PrimaryCollider) || !Alive(carrier.Body)
                    || carrier.PrimaryCollider.enabled || carrier.Body.detectCollisions || !carrier.Body.isKinematic)
                    return false;
                if (!EnsureHooks(support, out detail)) return false;
                candidate = new(bridge, carrier, stats);
                candidate.BindReceiver();
                candidate.RequireCurrent();
                if (!float.IsFinite(candidate._history.Temperature))
                    throw new InvalidOperationException("The approved temperature-volume baseline is nonfinite.");
                if (!Owners.TryAdd(candidate._statsPointer, candidate))
                    throw new InvalidOperationException("This exact native stat receiver already owns temperature history.");
                candidate._registered = true;
                PreparedCarriers.Add(carrier, candidate._snapshot);
                bridge.Retired += candidate.Dispose;
                producer = candidate;
                detail = "Actor temperature history bound to actual native volume callbacks; physics and survival remain held.";
                return true;
            }
            catch (Exception error)
            {
                candidate?.Dispose();
                detail = "Temperature history preparation refused: " + error.Message;
                return false;
            }
        }

        private void BindReceiver()
        {
            _receiver = _collider.GetComponentInChildren<ITemperatureModifierReceiver>();
            if (_receiver != null)
            {
                if (_receiver.WasCollected || _receiver.Pointer != _statsPointer)
                    throw new InvalidOperationException("The carrier collider resolves a different native temperature receiver.");
                return;
            }
            // Native lookup is downward-only. A child capsule cannot find root PlayerStats.
            if (!ClassInjector.IsTypeRegisteredInIl2Cpp(typeof(PlayerTemperatureReceiver)))
                ClassInjector.RegisterTypeInIl2Cpp<PlayerTemperatureReceiver>(new()
                { Interfaces = new[] { typeof(ITemperatureModifierReceiver) } });
            _proxy = _collider.gameObject.AddComponent<PlayerTemperatureReceiver>();
            if (!Alive(_proxy)) throw new InvalidOperationException("The actor temperature receiver could not be created.");
            _proxy.Bind(this);
            _receiver = _collider.GetComponentInChildren<ITemperatureModifierReceiver>();
            if (_receiver == null || _receiver.WasCollected || _receiver.Pointer != _proxy.Pointer)
                throw new InvalidOperationException("Native collider lookup did not select the injected actor receiver.");
        }

        /// <summary>
        /// Read after native volume LateUpdate callbacks in the parent's settled render
        /// phase. Sequence 0 / frame -1 denotes the approved baseline, not a callback.
        /// </summary>
        internal bool TryRead(out PlayerTemperatureHistory history, out string detail)
        {
            history = default;
            detail = FailureReason ?? "Temperature history requires a live game-thread render phase.";
            if (IsDisposed || FailureReason != null || Environment.CurrentManagedThreadId != _thread || Time.inFixedTimeStep)
                return false;
            try
            {
                RequireCurrent();
                if (!_registered || !Owners.TryGetValue(_statsPointer, out var current) || !ReferenceEquals(current, this))
                    throw new InvalidOperationException("The exact temperature history registration was lost.");
                history = _history;
                detail = "Ordered native temperature callback history read for this server actor.";
                return true;
            }
            catch (Exception error) { Fail(error.Message); detail = FailureReason; return false; }
        }

        internal void Receive(nint receiver, bool inside, float temperature)
        {
            if (IsDisposed || FailureReason != null) return;
            try
            {
                // Stay fires per overlapping volume per frame; the full binding check runs in TryRead,
                // which every consumer passes before it can see this history.
                if (Environment.CurrentManagedThreadId != _thread || _receiver == null || _receiver.WasCollected)
                    throw new InvalidOperationException("The exact actor temperature binding is no longer current.");
                if (!_registered || (receiver != _statsPointer && receiver != _receiver.Pointer))
                    throw new InvalidOperationException("A temperature callback did not target this bound actor receiver.");
                // Exit ignores its temperature argument, including nonfinite values, exactly
                // as native PlayerStats does. Do not restore another overlapping volume.
                if (inside && !float.IsFinite(temperature))
                    throw new InvalidOperationException("An entered/stayed temperature is nonfinite.");
                _history = new(checked(_history.Sequence + 1), Time.frameCount, inside,
                    inside ? temperature : _history.Temperature);
            }
            catch (Exception error) { Fail(error.Message); }
        }

        internal void ReceiverDestroyed() => Fail("The actor's native temperature receiver was destroyed before retirement.");

        private void Fail(string detail) => FailureReason ??= detail;

        private void RequireCurrent()
        {
            if (IsDisposed || FailureReason != null || Environment.CurrentManagedThreadId != _thread
                || _bridge.IsDisposed || !_bridge.IsServer || !_bridge.TryReadCurrentTarget(out var target)
                || !Same(target, _entity) || _entity.GetInstanceID() != _entityId
                || _entity.gameObject.scene.handle != _scene || !_entity.gameObject.activeInHierarchy
                || _carrier.IsDisposed || !_carrier.IsSurvivalRestored || !Same(_carrier.Entity, _entity)
                || !ReferenceEquals(_carrier.RestoredSurvival, _snapshot)
                || !_carrier.TryReadRestoredServerStats(out var stats) || !Same(stats, _stats)
                || _stats.GetInstanceID() != _statsId || !Same(_stats.transform, _entity.transform)
                || !Same(_carrier.PrimaryCollider, _collider) || _collider.GetInstanceID() != _colliderId
                || !Same(_carrier.Body, _body) || !Same(_collider.attachedRigidbody, _body)
                || !Same(_body.gameObject, _entity.gameObject) || !_collider.gameObject.activeInHierarchy
                || (!Same(_collider.transform, _entity.transform) && !_collider.transform.IsChildOf(_entity.transform))
                || _receiver == null || _receiver.WasCollected)
                throw new InvalidOperationException("The exact actor temperature binding is no longer current.");
            var selected = _collider.GetComponentInChildren<ITemperatureModifierReceiver>();
            if (selected == null || selected.WasCollected || selected.Pointer != _receiver.Pointer
                || (_proxy is not null && (!Alive(_proxy) || !_proxy.IsBoundTo(this))))
                throw new InvalidOperationException("Native temperature receiver resolution changed.");
        }

        private static bool Alive(UnityEngine.Object value) => value is not null && !value.WasCollected && value;
        private static bool Same(UnityEngine.Object left, UnityEngine.Object right)
            => Alive(left) && Alive(right) && left.Pointer == right.Pointer;

        public void Dispose()
        {
            if (Environment.CurrentManagedThreadId != _thread)
                throw new InvalidOperationException("Retire temperature history on the actor's game thread.");
            if (IsDisposed) return;
            IsDisposed = true;
            _bridge.Retired -= Dispose;
            if (_registered)
            {
                Owners.TryRemove(_statsPointer, out _);
                _registered = false;
            }
            if (Alive(_proxy))
            {
                _proxy.Unbind(this);
                UnityEngine.Object.Destroy(_proxy);
            }
        }
    }

    public sealed partial class PlayerSurvivalSnapshot
    {
        internal bool TemperatureVolumeBaselineInside => (_flags & 64u) != 0;
    }
}
