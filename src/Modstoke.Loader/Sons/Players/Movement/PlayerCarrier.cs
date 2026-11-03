// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Bolt;
using global::Sons.Multiplayer.Client;
using global::Sons.StatSystem;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>
    /// One replacement's native presentation initialization and exclusive physics preparation.
    /// The existing PlayerRoles creation scope must already have run RemoteAttached. This class
    /// never attaches a player, assigns LocalPlayer, invents inventory, or claims motor coverage.
    /// </summary>
    public sealed partial class PlayerCarrier : IDisposable
    {
        private static readonly Dictionary<IntPtr, PlayerCarrier> Carriers = new();
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly BoltEntity _entity;
        private readonly GameObject _root;
        private readonly Bolt.Entity _attachment;
        private readonly IntPtr _serializerPointer;
        private readonly IPlayerState _state;
        private readonly BoltPlayerSetup _setup;
        private readonly CoopPlayerRemoteSetup _remote;
        private readonly CoopMecanimReplicator _replicator;
        private readonly Animator _animator;
        private readonly int _instance;
        private readonly ulong _networkId;
        private PlayerBridge _bridge;
        private PhysicMaterial _material;
        private GameObject _collisionRoot;
        private bool _ownsBody, _ownsCapsule, _ownsContacts;
        private bool _disposed, _prepared, _physicsActive, _presentationHeld = true;
        private PlayerStats _serverStats;
        private Vitals _serverVitals;
        private StatsManager _serverStatsManager;
        private Stat[] _serverVitalStats;
        private int _serverStatsInstance, _serverVitalsInstance;
        private int _statsAwakeCount, _vitalsAwakeCount;
        private bool _statsPreparationAttempted, _initializingServerVitals, _serverVitalsInitialized;
        private bool _survivalRestoreAttempted, _restoringSurvival, _survivalRestored;
        private PlayerSurvivalSnapshot _survivalSnapshot;
        private PlayerAuthorityDynamics _authorityDynamics;

        public PlayerCarrierProfile Profile { get; }
        public BoltEntity Entity => _entity;
        public Rigidbody Body { get; private set; }
        public CapsuleCollider PrimaryCollider { get; private set; }
        public RigidBodyCollisionFlags Contacts { get; private set; }
        public bool IsDisposed => _disposed;
        public bool IsPhysicsActive => !_disposed && _physicsActive;
        public bool IsSurvivalRestored => !_disposed && FailureReason == null && _survivalRestored;
        public PlayerSurvivalSnapshot RestoredSurvival => IsSurvivalRestored ? _survivalSnapshot : null;
        public string FailureReason { get; private set; }

        /// <summary>
        /// Creates this carrier's native stat receivers without local-player lifecycle callbacks.
        /// The initial native values are NOT migrated actor state. Restore approved source values,
        /// rates, timers and survival configuration before handing the receivers to any authority.
        /// Both components remain disabled; no native Update or Start is run by this method.
        /// On a mutation failure this carrier remains held and cannot retry preparation.
        /// </summary>
        public bool TryPrepareServerStats(out PlayerStats serverStats, out string detail)
        {
            serverStats = null;
            detail = "An initialized, held carrier with no existing stat receivers is required.";
            if (Environment.CurrentManagedThreadId != _thread || _statsPreparationAttempted
                || _physicsActive || !_presentationHeld || PreparingServerStats != null
                || ReadReadiness(out detail) != BoltMigrationAdmission.Ready) return false;
            try
            {
                if (!_root.activeInHierarchy)
                { detail = "The carrier root must be active so native Awake isolation can be observed synchronously."; return false; }
                if (_entity.GetComponentsInChildren<PlayerStats>(true).Length != 0
                    || _entity.GetComponentsInChildren<Vitals>(true).Length != 0 || _setup._playerStats != null)
                { detail = "Pre-existing native stat receivers cannot be adopted after their lifecycle has run."; return false; }
                PruneServerStatOwners();
                _statsPreparationAttempted = true;
                PreparingServerStats = this;
                try
                {
                    // Awake is intercepted synchronously on this exact root, before it can copy
                    // LocalPlayer.Vitals or register console, save, sleep and clothing callbacks.
                    var vitals = _root.AddComponent<Vitals>();
                    if (_serverVitals == null && Alive(vitals))
                    {
                        _serverVitals = vitals; _serverVitalsInstance = vitals.GetInstanceID();
                        ServerVitalsOwners.Add(vitals.Pointer, this);
                    }
                    if (Alive(vitals)) vitals.enabled = false;
                    if (!Same(vitals, _serverVitals) || _vitalsAwakeCount != 1)
                        throw new InvalidOperationException("Carrier Vitals Awake isolation was not observed.");
                    var stats = _root.AddComponent<PlayerStats>();
                    if (_serverStats == null && Alive(stats))
                    {
                        _serverStats = stats; _serverStatsInstance = stats.GetInstanceID();
                        ServerStatsOwners.Add(stats.Pointer, this);
                    }
                    if (Alive(stats)) stats.enabled = false;
                    if (!Same(stats, _serverStats) || _statsAwakeCount != 1)
                        throw new InvalidOperationException("Carrier PlayerStats Awake isolation was not observed.");
                }
                finally { PreparingServerStats = null; }

                _serverStats._vitals = _serverVitals;
                _serverStats._playerEntityCache = _entity;
                _serverStats._playerStateCache = _state;
                _serverStats._animator = _animator;
                _serverStats._armourSystem = _setup._armourSystem;
                _setup._playerStats = _serverStats;
                _serverStatsManager = _serverVitals._statsManager;
                if (_serverStatsManager == null || _serverStatsManager.WasCollected)
                    throw new InvalidOperationException("The carrier Vitals constructor did not create a stat manager.");
                _initializingServerVitals = true;
                try
                {
                    _serverVitals.ValidateStats();
                    _serverVitalStats = new Stat[] { _serverVitals.Health, _serverVitals.Sickness,
                        _serverVitals.Stamina, _serverVitals.Vitality, _serverVitals.Hydration,
                        _serverVitals.Fullness, _serverVitals.Rested, _serverVitals.Strength, _serverVitals.Temperature };
                    _serverVitals.InitializeStats();
                }
                finally { _initializingServerVitals = false; }
                if (!_serverVitalsInitialized || !ServerStatsCurrent())
                    throw new InvalidOperationException("Carrier stat initialization or exclusive receiver binding was not confirmed.");
                serverStats = _serverStats;
                detail = "Actor-local native stat receivers initialized and held; approved source state restoration is still required.";
                return true;
            }
            catch (Exception error)
            {
                FailureReason = detail = error is InvalidOperationException ? error.Message
                    : "Carrier stat preparation failed (" + error.GetType().Name + ").";
                try { HoldServerStats(); HoldPhysics(); }
                catch (Exception holdError) { FailureReason = detail += " Carrier hold unconfirmed (" + holdError.GetType().Name + ")."; }
                return false;
            }
        }

        /// <summary>
        /// Reads the exact disabled stat receiver after preparation. This is an identity/lifecycle
        /// check, not approval of its values or evidence of complete survival/inventory authority.
        /// </summary>
        public bool TryReadServerStats(out PlayerStats serverStats)
        {
            serverStats = null;
            if (Environment.CurrentManagedThreadId != _thread) return false;
            try
            {
                if (!ServerStatsCurrent()) return false;
                serverStats = _serverStats;
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>Returns receivers only after this carrier's approved survival restore.</summary>
        public bool TryReadRestoredServerStats(out PlayerStats serverStats)
        {
            serverStats = null;
            return _survivalRestored && TryReadServerStats(out serverStats);
        }

        /// <summary>
        /// Restore the authenticated source baseline once, while all carrier writers are held.
        /// Approval must correlate this snapshot with the parent's exact migration and peer;
        /// matching source identifiers alone is not authorization. A failed restore is terminal
        /// for this lease, including when a callback throws after a partial native write.
        /// </summary>
        public bool TryRestoreSurvival(PlayerSurvivalSnapshot snapshot,
            Func<PlayerCarrier, PlayerSurvivalSnapshot, bool> approval,
            out string detail)
        {
            detail = "Survival restoration requires an approved source snapshot and held initialized receivers.";
            if (Environment.CurrentManagedThreadId != _thread || _disposed || _survivalRestoreAttempted
                || _physicsActive || !_presentationHeld || snapshot == null || approval == null
                || snapshot.SourceNetworkId != Profile.SourceNetworkId || snapshot.PrefabId != Profile.PrefabId
                || snapshot.StateTypeId != Profile.StateTypeId || _bridge?.IsGameplayActive == true) return false;
            bool approved = false;
            try
            {
                if (ReadReadiness(out detail) != BoltMigrationAdmission.Ready || !ServerStatsCurrent())
                { detail = "The carrier's prepared native receivers are unavailable or no longer held."; return false; }
                _survivalRestoreAttempted = true;
                _restoringSurvival = true;
                if (!snapshot.TryRestoreApproved(this, ApproveExact, out detail))
                    throw new InvalidOperationException(detail ?? "Approved survival restoration was not confirmed.");
                if (!approved || !Current() || !ServerStatsCurrent() || !PhysicsHeld() || !PreparedProfileMatches() || !_presentationHeld
                    || _bridge?.IsGameplayActive == true)
                    throw new InvalidOperationException("Carrier identity or writer hold changed during survival restoration.");
                _survivalSnapshot = snapshot;
                _survivalRestored = true;
                detail = "Approved source survival state restored to this exact carrier; native writers remain held.";
                return true;
            }
            catch (Exception error)
            {
                FailureReason = detail = error is InvalidOperationException ? error.Message
                    : "Carrier survival restoration failed (" + error.GetType().Name + ").";
                try { HoldPhysics(); HoldServerStats(); }
                catch (Exception holdError) { FailureReason = detail += " Carrier hold unconfirmed (" + holdError.GetType().Name + ")."; }
                return false;
            }
            finally { _restoringSurvival = false; }

            bool ApproveExact(PlayerCarrier target, PlayerSurvivalSnapshot source)
            {
                if (approved || !ReferenceEquals(target, this) || !ReferenceEquals(source, snapshot)
                    || !Current() || !ServerStatsCurrent() || !PhysicsHeld() || !_presentationHeld
                    || _bridge?.IsGameplayActive == true) return false;
                if (!approval(this, snapshot)) return false;
                approved = Current() && ServerStatsCurrent() && PhysicsHeld() && PreparedProfileMatches() && _presentationHeld
                    && _bridge?.IsGameplayActive != true;
                return approved;
            }
        }

        private bool ServerStatsCurrent()
        {
            if (!Current() || FailureReason != null || !_serverVitalsInitialized
                || !ServerStatsBindingsCurrent()
                || _serverStatsManager == null || _serverStatsManager.WasCollected
                || _serverVitals._statsManager?.Pointer != _serverStatsManager.Pointer
                || _serverVitalStats == null || _serverVitalStats.Length != 9) return false;
            var registered = _serverStatsManager._stats;
            if (registered == null || registered.WasCollected || registered.Count != _serverVitalStats.Length) return false;
            for (int i = 0; i < _serverVitalStats.Length; i++)
            {
                Stat cached = i switch
                {
                    0 => _serverVitals._health, 1 => _serverVitals._sickness,
                    2 => _serverVitals._stamina, 3 => _serverVitals._vitality,
                    4 => _serverVitals._hydration, 5 => _serverVitals._fullness,
                    6 => _serverVitals._rested, 7 => _serverVitals._strength,
                    _ => _serverVitals._temperature
                };
                var expected = _serverVitalStats[i];
                if (expected == null || expected.WasCollected || cached == null || cached.WasCollected
                    || cached.Pointer != expected.Pointer) return false;
                int matches = 0;
                for (int j = 0; j < registered.Count; j++)
                    if (registered[j] != null && !registered[j].WasCollected && registered[j].Pointer == expected.Pointer) matches++;
                if (matches != 1) return false;
            }
            return true;
        }

        private bool ServerStatsBindingsCurrent()
        {
            if (_statsAwakeCount != 1 || _vitalsAwakeCount != 1
                || !Alive(_serverStats) || !Alive(_serverVitals) || _serverStats.enabled || _serverVitals.enabled
                || _serverStats.GetInstanceID() != _serverStatsInstance || _serverVitals.GetInstanceID() != _serverVitalsInstance
                || !Same(_serverStats.gameObject, _root) || !Same(_serverVitals.gameObject, _root)
                || !Same(_serverStats._vitals, _serverVitals) || !Same(_setup._playerStats, _serverStats)
                || !Same(_serverStats._playerEntityCache, _entity) || _serverStats._playerStateCache?.Pointer != _state.Pointer
                || !Same(_serverStats._animator, _animator) || !Same(_serverStats._armourSystem, _setup._armourSystem)
                || Same(_serverStats, LocalPlayer.Stats) || Same(_serverVitals, LocalPlayer.Vitals)
                || !ServerStatsOwners.TryGetValue(_serverStats.Pointer, out var statsOwner) || !ReferenceEquals(statsOwner, this)
                || !ServerVitalsOwners.TryGetValue(_serverVitals.Pointer, out var vitalsOwner) || !ReferenceEquals(vitalsOwner, this)) return false;
            return true;
        }

        private void HoldServerStats()
        {
            if (Alive(_serverStats) && _serverStats.GetInstanceID() == _serverStatsInstance)
            { _serverStats.enabled = false; _serverStats.CancelInvoke(); _serverStats.StopAllCoroutines(); }
            if (Alive(_serverVitals) && _serverVitals.GetInstanceID() == _serverVitalsInstance)
            { _serverVitals.enabled = false; _serverVitals.CancelInvoke(); _serverVitals.StopAllCoroutines(); }
        }

        /// <summary>
        /// Motor liveness only, after full startup admission. Checks retained bindings and the
        /// held/active physics phase without hierarchy queries, delegate inspection or registry
        /// enumeration. It is not a substitute for ReadReadiness at admission/phase barriers.
        /// </summary>
        internal bool IsCurrentForMotor
        {
            get
            {
                if (_disposed || !_prepared || FailureReason != null || !HooksAvailable
                    || Environment.CurrentManagedThreadId != _thread || !PlayerRoles.IsAvailable)
                    return false;
                try
                {
                    if (_statsPreparationAttempted && !ServerStatsBindingsCurrent()) return false;
                    if (!BoltNetwork.isServer || !Alive(_entity) || !Alive(_root)
                        || _entity.GetInstanceID() != _instance || !_entity.isAttached || !_entity.isOwner
                        || _entity.source != null || _entity.networkId.PackedValue != _networkId
                        || _entity.prefabId.Value != Profile.PrefabId
                        || _attachment.WasCollected || _state.WasCollected || !_attachment.IsAttached
                        || _entity._entity?.Pointer != _attachment.Pointer
                        || _attachment.Serializer?.Pointer != _serializerPointer
                        || !Same(_entity.gameObject, _root) || Same(_entity, LocalPlayer.Entity)
                        || Same(_root, LocalPlayer.GameObject)
                        || !Carriers.TryGetValue(_entity.Pointer, out var registered) || !ReferenceEquals(registered, this)
                        || !Alive(Body) || !Alive(PrimaryCollider) || !Alive(Contacts)
                        || !Same(Body.gameObject, _root) || !Same(PrimaryCollider.attachedRigidbody, Body)
                        || !Same(Contacts._targetCollider, PrimaryCollider)
                        || !Same(Contacts._capsuleCollider, PrimaryCollider) || Contacts.enabled) return false;
                    return _physicsActive
                        ? _survivalRestored && _bridge?.IsGameplayActive == true
                            && !PrimaryCollider.isTrigger
                            && (IsYieldedBodyOwned()
                                || (!Body.isKinematic && Body.detectCollisions && PrimaryCollider.enabled
                                    && Body.collisionDetectionMode == Profile.Body.CollisionDetection))
                        : Body.isKinematic && !Body.detectCollisions && !PrimaryCollider.enabled;
                }
                catch (Exception) { return false; }
            }
        }

        internal bool TryBindAuthorityDynamics(PlayerAuthorityDynamics owner)
        {
            if (Environment.CurrentManagedThreadId != _thread || _authorityDynamics != null
                || !IsSurvivalRestored || _physicsActive || !IsCurrentForMotor
                || owner == null || !owner.OwnsCarrier(this, _bridge)) return false;
            _authorityDynamics = owner;
            return true;
        }

        internal void ReleaseAuthorityDynamics(PlayerAuthorityDynamics owner)
        {
            if (Environment.CurrentManagedThreadId == _thread && ReferenceEquals(_authorityDynamics, owner))
                _authorityDynamics = null;
        }

        // Deliberate native-mode yield is an activation phase, not permission to accept any
        // disabled body. Only the retained, registered motor and its exact adjacent keys qualify.
        private bool IsYieldedBodyOwned()
            => Body.isKinematic && !Body.detectCollisions && !PrimaryCollider.enabled
                && Body.collisionDetectionMode == CollisionDetectionMode.Discrete
                && _authorityDynamics != null && _authorityDynamics.OwnsYieldedMotor(this, _bridge);

        private PlayerCarrier(BoltEntity entity, PlayerCarrierProfile profile,
            BoltPlayerSetup setup, CoopPlayerRemoteSetup remote, CoopMecanimReplicator replicator)
        {
            _entity = entity; _root = entity.gameObject; _attachment = entity._entity;
            _serializerPointer = _attachment.Serializer.Pointer; _state = entity.GetState<IPlayerState>();
            _setup = setup; _remote = remote; _replicator = replicator;
            _animator = replicator.GetTargetAnimator(); _instance = entity.GetInstanceID();
            _networkId = entity.networkId.PackedValue; Profile = profile;
        }

        /// <summary>
        /// Call once immediately after owner recreation, before restoration. On mutation failure
        /// the returned lease remains non-null and held, and must not be retried or rolled back.
        /// Only the exact ordinary prefab topology is admitted; nested bodies/joints/extra solid
        /// collision shapes require their own adapter and are never deleted here.
        /// </summary>
        public static bool TryPrepare(BoltEntity replacement, PlayerCarrierProfile profile,
            out PlayerCarrier carrier, out string detail)
        {
            carrier = null;
            detail = "Carrier preparation requires installed startup observation and an exact server recreation.";
            if (!HooksAvailable || !PlayerRoles.IsAvailable
                || !PlayerCarrierProfile.ValidateProfile(profile, out detail)) return false;
            detail = "The replacement must be the exact current server carrier for the approved source profile.";
            try
            {
                if (!Alive(replacement) || !BoltNetwork.isServer || !PlayerRoles.IsCurrentCarrier(replacement)
                    || replacement.prefabId != BoltPrefabs.PlayerNetwork || replacement.prefabId.Value != profile.PrefabId
                    || replacement.networkId.PackedValue == profile.SourceNetworkId
                    || Same(replacement.gameObject, LocalPlayer.GameObject) || Same(replacement, LocalPlayer.Entity)
                    || Carriers.ContainsKey(replacement.Pointer)) return false;
                var root = replacement.transform;
                if (root.parent != null || !Unit(root.lossyScale)
                    || PhysicsSceneExtensions.GetPhysicsScene(replacement.gameObject.scene) != Physics.defaultPhysicsScene)
                { detail = "The replacement requires an unparented unit-scale native physics root."; return false; }
                var serializer = replacement._entity?.Serializer?.TryCast<NetworkState>();
                if (serializer?.Meta?.TypeId.Value != profile.StateTypeId) return false;
                if (!QualifyPrefab(replacement, out detail)) return false;
                var setup = replacement.GetComponent<BoltPlayerSetup>();
                var remotes = replacement.GetComponentsInChildren<CoopPlayerRemoteSetup>(true);
                var replicators = replacement.GetComponentsInChildren<CoopMecanimReplicator>(true);
                if (!Alive(setup) || !Same(setup.entity, replacement) || remotes.Length != 1 || replicators.Length != 1
                    || !Alive(remotes[0]) || !Alive(replicators[0]) || !Same(remotes[0].entity, replacement)
                    || !Same(replicators[0].entity, replacement) || !Alive(replicators[0].GetTargetAnimator())
                    || !Same(remotes[0]._playerAnimator, replicators[0].GetTargetAnimator()))
                { detail = "The replacement's native setup, remote equipment renderer and animator bindings differ."; return false; }
                if (replacement.GetComponentsInChildren<FirstPersonCharacter>(true).Length != 0
                    || replacement.GetComponentsInChildren<CharacterController>(true).Length != 0
                    || replacement.GetComponentsInChildren<Joint>(true).Length != 0)
                { detail = "A carrier cannot own a local FPC, CharacterController or joint-driven body."; return false; }
                var bodies = replacement.GetComponentsInChildren<Rigidbody>(true);
                var capsules = replacement.GetComponents<CapsuleCollider>();
                var contacts = replacement.GetComponentsInChildren<RigidBodyCollisionFlags>(true);
                if (bodies.Length > 1 || (bodies.Length == 1 && !Same(bodies[0].transform, root))
                    || capsules.Length > 1 || contacts.Length > 1
                    || (contacts.Length == 1 && !Same(contacts[0].transform, root)))
                { detail = "The prefab has unsupported nested or multiple physical bodies/capsules/contact trackers."; return false; }
                var capsule = capsules.Length == 1 ? capsules[0] : null;
                foreach (var collider in replacement.GetComponentsInChildren<Collider>(true))
                    if (!Alive(collider) || (!collider.isTrigger && !Same(collider, capsule)))
                    { detail = "An additional solid presentation collider needs a qualified collision adapter."; return false; }
                if (capsule != null && (profile.Geometry.LocalPosition != Vector3.zero
                    || Quaternion.Angle(profile.Geometry.LocalRotation, Quaternion.identity) > 0.0001f))
                { detail = "The existing root capsule cannot represent the captured non-root capsule transform."; return false; }

                carrier = new PlayerCarrier(replacement, profile, setup, remotes[0], replicators[0]);
                Carriers.Add(replacement.Pointer, carrier);
                carrier.Body = bodies.Length == 1 ? bodies[0] : null;
                carrier.PrimaryCollider = capsule;
                carrier.Contacts = contacts.Length == 1 ? contacts[0] : null;
                carrier.PreparePhysics();
                carrier._prepared = true;
                if (!carrier.Current() || !carrier.PhysicsHeld() || !carrier.PreparedProfileMatches())
                    throw new InvalidOperationException("Carrier preparation changed its binding, captured settings or physics hold.");
                detail = "Native remote carrier held; wait for read-only startup admission before restoration.";
                return true;
            }
            catch (Exception error)
            {
                detail = error is InvalidOperationException ? error.Message
                    : "Carrier preparation failed (" + error.GetType().Name + ").";
                if (carrier != null)
                {
                    carrier.FailureReason = detail;
                    try { carrier.HoldPhysics(); }
                    catch (Exception holdError) { carrier.FailureReason = detail += " Physics hold unconfirmed (" + holdError.GetType().Name + ")."; }
                }
                return false;
            }
        }

        /// <summary>Read-only. Does not replay Start, attach, restore, or advance an Animator.</summary>
        public BoltMigrationAdmission ReadReadiness(out string detail)
        {
            detail = FailureReason ?? "The carrier is no longer current or its startup hold changed.";
            if (_disposed || !_prepared || FailureReason != null || Environment.CurrentManagedThreadId != _thread)
                return BoltMigrationAdmission.Rejected;
            try
            {
                if (!Current() || (_physicsActive && !IsCurrentForMotor)
                    || (!_physicsActive && (!PhysicsHeld() || !PreparedProfileMatches())))
                    return BoltMigrationAdmission.Rejected;
                if (_statsPreparationAttempted && !ServerStatsCurrent())
                { detail = "The carrier's isolated native stat receivers are no longer current."; return BoltMigrationAdmission.Rejected; }
                return ReadNativeInitialization(_entity, out detail);
            }
            catch (Exception error)
            { detail = "Carrier startup inspection failed (" + error.GetType().Name + ")."; return BoltMigrationAdmission.Rejected; }
        }

        /// <summary>
        /// Bind the exact prepared endpoint without activating physics, stat writers or native
        /// publication. The aggregate retains responsibility for approving/restoring survival
        /// and installing all runtime participants before activating its bridge.
        /// </summary>
        public bool TryBindPrepared(PlayerBridge bridge, out string detail)
        {
            detail = "A held carrier and its exact prepared server bridge are required.";
            if (Environment.CurrentManagedThreadId != _thread || _disposed || _physicsActive || _restoringSurvival
                || !_presentationHeld || bridge == null || bridge.IsGameplayActive) return false;
            try
            {
                if (ReadReadiness(out detail) != BoltMigrationAdmission.Ready) return false;
                if (!MatchesBridge(bridge))
                { detail = "The prepared endpoint does not identify this exact carrier and source profile."; return false; }
                if (_bridge == null) { _bridge = bridge; bridge.Retired += Dispose; }
                detail = "Exact prepared endpoint bound; physics and native publication remain held.";
                return true;
            }
            catch (Exception error)
            { detail = "Prepared carrier binding failed (" + error.GetType().Name + ")."; return false; }
        }

        /// <summary>
        /// Called by the qualified native motor after it is installed on a completed bridge.
        /// Releases only ordinary active-body physics. Occupied/disabled/trigger-only source
        /// physics needs its vehicle or action participant, not an invented ground transition.
        /// This does not implement locomotion or promote source observations to live authority.
        /// </summary>
        public bool TryActivatePhysics(PlayerBridge bridge, out string detail)
        {
            detail = "Restore approved survival and bind the prepared motor before aggregate activation.";
            if (_physicsActive || !_survivalRestored || !ActiveBridgeCurrent(bridge)
                || ReadReadiness(out detail) != BoltMigrationAdmission.Ready)
                return false;
            if (Profile.Body.IsKinematic || !Profile.Body.DetectCollisions
                || !Profile.Geometry.Enabled || Profile.Geometry.IsTrigger)
            { detail = "The captured source physics belongs to an inactive or special mode; ground activation is not authorized."; return false; }
            try
            {
                Body.position = _entity.transform.position;
                Body.rotation = _entity.transform.rotation;
                Body.isKinematic = false;
                Body.collisionDetectionMode = Profile.Body.CollisionDetection;
                Body.velocity = Profile.Velocity;
                Body.angularVelocity = Profile.AngularVelocity;
                PrimaryCollider.enabled = true;
                Body.detectCollisions = true;
                // The movement receiver owns contact dispatch/reset, as in the native-body router.
                Contacts.enabled = false;
                if (!Current() || !ActiveBridgeCurrent(bridge) || !ServerStatsCurrent()
                    || Body.isKinematic || !Body.detectCollisions || !PrimaryCollider.enabled)
                    throw new InvalidOperationException("Carrier physics activation was not confirmed.");
                _physicsActive = true;
                detail = "Captured native body and capsule activated; the bound motor owns simulation.";
                return true;
            }
            catch (Exception error)
            {
                FailureReason = detail = "Carrier activation failed (" + error.GetType().Name + ").";
                try { HoldPhysics(); }
                catch (Exception holdError) { FailureReason = detail += " Physics hold unconfirmed (" + holdError.GetType().Name + ")."; }
                return false;
            }
        }

        /// <summary>
        /// Release owner animation publication only after aggregate completion. The adapter must
        /// already have restored the animator's state and resume speed. No native setup callback,
        /// equipment callback, Animator.Update, or LocalAttached is replayed here.
        /// </summary>
        public bool TryResumePresentation(PlayerBridge bridge, out string detail)
        {
            detail = "A completed exact server bridge and initialized native presentation are required.";
            if (!_presentationHeld || !_survivalRestored || !ActiveBridgeCurrent(bridge)
                || ReadReadiness(out detail) != BoltMigrationAdmission.Ready) return false;
            _presentationHeld = false;
            detail = "Native owner animation publication released; remote equipment subscriptions remain intact.";
            return true;
        }

        private bool MatchesBridge(PlayerBridge bridge)
        {
            if (Environment.CurrentManagedThreadId != _thread || _disposed || bridge?.IsServer != true
                || bridge.IsDisposed || !bridge.Plan.IsValid || bridge.NetworkId != _networkId
                || (_bridge != null && !ReferenceEquals(_bridge, bridge))
                || bridge.Plan.OldNetworkId != Profile.SourceNetworkId || bridge.Plan.PrefabId != Profile.PrefabId
                || bridge.Plan.StateTypeId != Profile.StateTypeId
                || !bridge.TryReadCurrentTarget(out var current) || !Same(current, _entity)) return false;
            return true;
        }

        private bool ActiveBridgeCurrent(PlayerBridge bridge)
            => ReferenceEquals(_bridge, bridge) && bridge?.IsGameplayActive == true && MatchesBridge(bridge);

        private void PreparePhysics()
        {
            _animator.speed = 0;
            _animator.applyRootMotion = false;
            foreach (var writer in _entity.GetComponentsInChildren<PlayerAnimatorControl>(true))
            {
                writer.enabled = false;
                writer.StopAllCoroutines();
            }
            if (Body == null) { _ownsBody = true; Body = _entity.gameObject.AddComponent<Rigidbody>(); }
            HoldPhysics();
            var b = Profile.Body;
            Body.mass = b.Mass; Body.drag = b.Drag; Body.angularDrag = b.AngularDrag;
            Body.maxDepenetrationVelocity = b.MaximumDepenetrationVelocity;
            Body.maxLinearVelocity = b.MaximumLinearVelocity; Body.maxAngularVelocity = b.MaximumAngularVelocity;
            Body.sleepThreshold = b.SleepThreshold; Body.constraints = b.Constraints;
            Body.interpolation = b.Interpolation; Body.useGravity = b.UseGravity;
            Body.solverIterations = b.SolverIterations; Body.solverVelocityIterations = b.SolverVelocityIterations;
            if (PrimaryCollider == null)
            {
                _ownsCapsule = true;
                if (Profile.Geometry.LocalPosition == Vector3.zero
                    && Quaternion.Angle(Profile.Geometry.LocalRotation, Quaternion.identity) <= 0.0001f)
                    PrimaryCollider = _entity.gameObject.AddComponent<CapsuleCollider>();
                else
                {
                    _collisionRoot = new GameObject("OpenACAIPlayerCollision");
                    _collisionRoot.transform.SetParent(_entity.transform, false);
                    _collisionRoot.layer = Profile.CapsuleLayer;
                    _collisionRoot.transform.localPosition = Profile.Geometry.LocalPosition;
                    _collisionRoot.transform.localRotation = Profile.Geometry.LocalRotation;
                    PrimaryCollider = _collisionRoot.AddComponent<CapsuleCollider>();
                }
            }
            PrimaryCollider.enabled = false;
            PrimaryCollider.gameObject.layer = Profile.CapsuleLayer;
            var g = Profile.Geometry;
            PrimaryCollider.direction = 1; PrimaryCollider.radius = g.Radius;
            PrimaryCollider.height = g.Height; PrimaryCollider.center = g.Center;
            PrimaryCollider.isTrigger = g.IsTrigger; PrimaryCollider.contactOffset = g.ContactOffset;
            if (Profile.Material.Present)
            {
                var p = Profile.Material;
                _material = new PhysicMaterial("OpenACAIPlayerCollision");
                _material.staticFriction = p.StaticFriction; _material.dynamicFriction = p.DynamicFriction;
                _material.bounciness = p.Bounciness; _material.frictionCombine = p.FrictionCombine;
                _material.bounceCombine = p.BounceCombine;
            }
            PrimaryCollider.sharedMaterial = _material;
            if (Contacts == null) { _ownsContacts = true; Contacts = _entity.gameObject.AddComponent<RigidBodyCollisionFlags>(); }
            Contacts.enabled = false;
            Contacts._targetCollider = PrimaryCollider; Contacts._capsuleCollider = PrimaryCollider;
            Contacts._transform = _entity.transform; Contacts._collType = RigidBodyCollisionFlags.ColliderType.Capsule;
            Contacts._heightOffset = Profile.Contacts.HeightOffset;
            Contacts._groundAngleMaxConstruction = Profile.Contacts.GroundAngleMaximumConstruction;
            Contacts._smoothedLayerMask = Profile.Contacts.SmoothedLayerMask;
        }

        private void HoldPhysics()
        {
            _physicsActive = false;
            if (Alive(PrimaryCollider)) PrimaryCollider.enabled = false;
            if (Alive(Contacts)) Contacts.enabled = false;
            if (!Alive(Body)) return;
            Body.detectCollisions = false;
            Body.collisionDetectionMode = CollisionDetectionMode.Discrete;
            if (!Body.isKinematic) { Body.velocity = Vector3.zero; Body.angularVelocity = Vector3.zero; }
            Body.isKinematic = true;
        }

        private bool PhysicsHeld() => Alive(Body) && Same(Body.gameObject, _entity.gameObject)
            && Body.isKinematic && !Body.detectCollisions
            && Alive(PrimaryCollider) && !PrimaryCollider.enabled && Same(PrimaryCollider.attachedRigidbody, Body)
            && Alive(Contacts) && !Contacts.enabled && Same(Contacts._targetCollider, PrimaryCollider);

        private bool PreparedProfileMatches()
        {
            var g = Profile.Geometry;
            var b = Profile.Body;
            var p = Profile.Material;
            var c = Profile.Contacts;
            var root = _entity.transform;
            var capsule = PrimaryCollider.transform;
            if (PrimaryCollider.direction != 1 || PrimaryCollider.radius != g.Radius
                || PrimaryCollider.height != g.Height || !PrimaryCollider.center.Equals(g.Center)
                || PrimaryCollider.isTrigger != g.IsTrigger || PrimaryCollider.contactOffset != g.ContactOffset
                || PrimaryCollider.gameObject.layer != Profile.CapsuleLayer || !Unit(capsule.lossyScale)
                || !capsule.IsChildOf(root)
                || (root.InverseTransformPoint(capsule.position) - g.LocalPosition).sqrMagnitude > 0.00000001f
                || Quaternion.Angle(Quaternion.Inverse(root.rotation) * capsule.rotation, g.LocalRotation) > 0.01f
                || Body.mass != b.Mass || Body.drag != b.Drag || Body.angularDrag != b.AngularDrag
                || Body.maxDepenetrationVelocity != b.MaximumDepenetrationVelocity
                || Body.maxLinearVelocity != b.MaximumLinearVelocity || Body.maxAngularVelocity != b.MaximumAngularVelocity
                || Body.sleepThreshold != b.SleepThreshold || Body.constraints != b.Constraints
                || Body.interpolation != b.Interpolation || Body.useGravity != b.UseGravity
                || Body.solverIterations != b.SolverIterations || Body.solverVelocityIterations != b.SolverVelocityIterations
                || !Same(Contacts._capsuleCollider, PrimaryCollider) || !Same(Contacts._transform, root)
                || Contacts._collType != RigidBodyCollisionFlags.ColliderType.Capsule
                || Contacts._heightOffset != c.HeightOffset || Contacts._groundAngleMaxConstruction != c.GroundAngleMaximumConstruction
                || Contacts._smoothedLayerMask.value != c.SmoothedLayerMask) return false;
            var material = PrimaryCollider.sharedMaterial;
            return p.Present
                ? Same(material, _material) && material.staticFriction == p.StaticFriction
                    && material.dynamicFriction == p.DynamicFriction && material.bounciness == p.Bounciness
                    && material.frictionCombine == p.FrictionCombine && material.bounceCombine == p.BounceCombine
                : material == null;
        }

        private bool Current() => !_disposed && HooksAvailable && PlayerRoles.IsCurrentCarrier(_entity)
            && Alive(_entity) && Alive(_root) && Same(_entity.gameObject, _root)
            && _entity.GetInstanceID() == _instance && _entity.networkId.PackedValue == _networkId
            && _entity.isAttached && _entity.isOwner && _entity.source == null
            && !_attachment.WasCollected && _attachment.IsAttached && !_state.WasCollected
            && _entity._entity?.Pointer == _attachment.Pointer && _attachment.Serializer?.Pointer == _serializerPointer
            && _entity.GetState<IPlayerState>()?.Pointer == _state.Pointer
            && Carriers.TryGetValue(_entity.Pointer, out var registered) && ReferenceEquals(registered, this)
            && Same(_setup.entity, _entity) && Same(_remote.entity, _entity) && Same(_replicator.entity, _entity)
            && Same(_replicator.GetTargetAnimator(), _animator) && !Same(_entity.gameObject, LocalPlayer.GameObject);

        /// <summary>
        /// Fail closed: never restore retired-source physics or native local-player writers.
        /// Native carrier components remain held. Only resources created by this lease are removed.
        /// The aggregate migration owner remains responsible for entity lifetime/failed migration policy.
        /// </summary>
        public void Dispose()
        {
            if (Environment.CurrentManagedThreadId != _thread)
                throw new InvalidOperationException("Dispose the player carrier on its game thread.");
            if (_disposed) return;
            _disposed = true; _presentationHeld = true; _survivalRestored = false;
            _authorityDynamics = null;
            if (_bridge != null) _bridge.Retired -= Dispose;
            try
            {
                if (Alive(_entity) && _entity.GetInstanceID() == _instance
                    && (_entity._entity == null || _entity._entity.Pointer == _attachment.Pointer))
                {
                    HoldPhysics();
                    if (Alive(_animator)) { _animator.speed = 0; _animator.applyRootMotion = false; }
                    HoldServerStats();
                    if (Same(_setup._playerStats, _serverStats)) _setup._playerStats = null;
                    if (Alive(_serverStats)) UnityEngine.Object.Destroy(_serverStats);
                    if (Alive(_serverVitals)) UnityEngine.Object.Destroy(_serverVitals);
                    if (_ownsContacts && Alive(Contacts)) UnityEngine.Object.Destroy(Contacts);
                    if (_ownsCapsule && Alive(PrimaryCollider)) UnityEngine.Object.Destroy(PrimaryCollider);
                    if (_ownsBody && Alive(Body)) UnityEngine.Object.Destroy(Body);
                    if (Alive(_collisionRoot)) UnityEngine.Object.Destroy(_collisionRoot);
                }
                if (Alive(_material)) UnityEngine.Object.Destroy(_material);
            }
            catch (Exception error)
            { FailureReason = "Carrier disposal could not be confirmed (" + error.GetType().Name + ")."; }
            // Keep publication fenced until the native Detached callback removes this exact entry.
        }

        private static bool QualifyPrefab(BoltEntity entity, out string detail)
        {
            detail = "The exact loaded PlayerNetwork prefab and replacement component topology must match.";
            var pool = BoltCore.PrefabPool;
            if (pool == null || pool.WasCollected) return false;
            var prefab = pool.LoadPrefab(BoltPrefabs.PlayerNetwork);
            if (!Alive(prefab) || Same(prefab, entity.gameObject)) return false;
            var definition = prefab.GetComponent<BoltEntity>();
            if (!Alive(definition) || definition.isAttached || definition._entity != null
                || definition.prefabId != BoltPrefabs.PlayerNetwork || !definition.sceneGuid.IsNone
                || definition.serializerGuid != entity.serializerGuid) return false;
            return SameTopology<Rigidbody>(prefab, entity.gameObject)
                && SameTopology<Collider>(prefab, entity.gameObject)
                && SameTopology<BoltPlayerSetup>(prefab, entity.gameObject)
                && SameTopology<CoopPlayerRemoteSetup>(prefab, entity.gameObject)
                && SameTopology<CoopMecanimReplicator>(prefab, entity.gameObject)
                && BoltCore.PrefabPool?.Pointer == pool.Pointer;
        }

        private static bool SameTopology<T>(GameObject prefab, GameObject instance) where T : Component
        {
            var expected = prefab.GetComponentsInChildren<T>(true);
            var actual = instance.GetComponentsInChildren<T>(true);
            if (expected.Length != actual.Length || actual.Length > 128) return false;
            for (int i = 0; i < actual.Length; i++)
                if (!Alive(actual[i]) || !Alive(expected[i])
                    || actual[i].GetIl2CppType().FullName != expected[i].GetIl2CppType().FullName
                    || Path(actual[i].transform, instance.transform) != Path(expected[i].transform, prefab.transform)) return false;
            return true;
        }

        private static string Path(Transform target, Transform root)
        {
            string path = "";
            for (int depth = 0; depth < 128; depth++)
            {
                if (Same(target, root)) return path;
                if (!Alive(target)) break;
                path = "/" + target.GetSiblingIndex().ToString(System.Globalization.CultureInfo.InvariantCulture) + path;
                target = target.parent;
            }
            throw new InvalidOperationException("A carrier component escaped its prefab hierarchy.");
        }

        private static bool Alive(UnityEngine.Object value) => value != null && !value.WasCollected && value;
        private static bool Same(UnityEngine.Object left, UnityEngine.Object right)
            => Alive(left) && Alive(right) && left.Pointer == right.Pointer;
        private static bool Unit(Vector3 value) => MathF.Abs(value.x - 1) < 0.0001f
            && MathF.Abs(value.y - 1) < 0.0001f && MathF.Abs(value.z - 1) < 0.0001f;
    }
}
