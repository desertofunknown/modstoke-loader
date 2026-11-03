// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Reflection;
using Bolt;
using Il2CppInterop.Runtime.InteropTypes;
using global::Sons.Ai.Vail;
using global::Sons.Animation;
using global::Sons.Gameplay;
using global::Sons.Items;
using global::Sons.Multiplayer;
using UnityEngine;

namespace Modstoke.Sons
{
    public static partial class VehiclePreservation
    {
        private sealed partial class CartOccupants
        {
            private sealed class CartConnectionsSnapshot
            {
                private readonly bool[] _local = new bool[4];
                private readonly IGolfCartAction[] _actions = new IGolfCartAction[4];
                private readonly VailActor[] _actors = new VailActor[4], _externalActors = new VailActor[4];
                private readonly BoltEntity[] _previous = new BoltEntity[4];
                private readonly Transform[] _transforms = new Transform[4];
                private readonly Animator[] _animators = new Animator[4];
                private readonly IGolfCartAction _driver;
                internal readonly IkTargetsController CurrentIk, ExternalIk;
                private readonly GameObject _ownedGhost;
                private readonly GolfCartGhostController _ownedController, _externalController;
                internal readonly BoltEntity OwnedGhostEntity, ExternalGhostEntity;
                private readonly IGolfCartState _ghostState;
                private readonly Transform _ghostTransform;
                private readonly bool _driverInitialized;
                private readonly IPlayerState _driverState;
                private readonly BoltEntity _driverEntity;
                private readonly BoltEntityDynamicSmooth _smooth;
                private readonly Il2CppSystem.Nullable<bool> _rootSynced;
                private readonly IntPtr _cartPointer;
                private readonly Il2CppObjectBase[] _storage;

                internal CartConnectionsSnapshot(GolfCartController cart, bool unprocessed = false)
                {
                    Require((CartConnections(cart) & VehiclePreservationBlockers.LocalConnectionUnavailable) == 0,
                        "All seven native connection arrays must contain exactly four entries.");
                    _cartPointer = cart.Pointer;
                    _storage = new Il2CppObjectBase[] { cart._isLocalConnectedSlots, cart._localConnectedActions,
                        cart._localConnectedActors, cart._previousProcessedConnectedSlots,
                        cart._externalEntityTransforms, cart._externalPlayerAnimators, cart._externalVailActors };
                    for (int i = 0; i < 4; i++)
                    {
                        _local[i] = cart._isLocalConnectedSlots[i]; _actions[i] = cart._localConnectedActions[i];
                        _actors[i] = cart._localConnectedActors[i]; _previous[i] = cart._previousProcessedConnectedSlots[i];
                        _transforms[i] = cart._externalEntityTransforms[i]; _animators[i] = cart._externalPlayerAnimators[i];
                        _externalActors[i] = cart._externalVailActors[i];
                        ulong id = cart.BoltEntityState.ConnectedSlots.GetValueNetworkId(i).PackedValue;
                        Require((unprocessed || id == 0) ? _previous[i] is null : Live(_previous[i]) && _previous[i].networkId.PackedValue == id,
                            "A seat notification is not yet processed; retry only after ordinary native settlement.");
                        Require(!unprocessed || (!_local[i] && _actions[i] == null && _actors[i] is null
                            && _transforms[i] is null && _animators[i] is null && _externalActors[i] is null),
                            "Replacement has already populated native occupant caches.");
                        Require(id != 0 || (!_local[i] && _actions[i] == null && _actors[i] is null
                            && _transforms[i] is null && _animators[i] is null && _externalActors[i] is null),
                            "An empty seat retains stale occupant references.");
                        RequireSurvivor(_transforms[i], cart);
                        RequireSurvivor(_animators[i], cart);
                        RequireSurvivor(_externalActors[i], cart);
                    }
                    _driver = cart._localPlayerDriverAction; CurrentIk = cart._currentDriverIkTargetsController;
                    _ownedGhost = cart._ownedGolfCartGhost; _ownedController = cart._ownedGolfCartGhostController;
                    OwnedGhostEntity = cart._ownedGolfCartGhostEntity;
                    _ghostState = cart._externalGolfCartGhostState; _externalController = cart._externalGolfCartGhostController;
                    ExternalGhostEntity = cart._externalGolfCartGhostEntity; _ghostTransform = cart._externalGolfCartGhostTransform;
                    _driverInitialized = cart._externalDriverPlayerInitialized; _driverState = cart._externalDriverPlayerState;
                    ExternalIk = cart._externalDriverIkTargetsController; _smooth = cart._externalDriverPlayerSmoothScript;
                    _rootSynced = cart._currentRootTransformSyncedValue;
                    RequireSurvivor(CurrentIk, cart); RequireSurvivor(ExternalIk, cart);
                    RequireSurvivor(_smooth, cart);
                    if (_driverState != null)
                    {
                        Require(_ghostState != null, "External player state has no ghost.");
                        _driverEntity = BoltNetwork.FindEntity(_ghostState.ConnectedSlots.GetValueNetworkId(0));
                        Require(Live(_driverEntity) && Ptr(_driverEntity._entity.Serializer) == Ptr(_driverState),
                            "External player state does not match the ghost's exact driver ID.");
                    }
                    Require(OwnedGhostEntity is null || ExternalGhostEntity is null, "Both owned and external ghost lifetimes are present.");
                    Require(OwnedGhostEntity is null ? _ownedGhost is null && _ownedController is null
                        : Live(OwnedGhostEntity) && Live(_ownedGhost) && Live(_ownedController)
                            && _ownedGhost.Pointer == OwnedGhostEntity.gameObject.Pointer
                            && SameEntity(_ownedController.entity, OwnedGhostEntity), "Owned ghost cache is inconsistent.");
                    Require(ExternalGhostEntity is null ? _externalController is null && _ghostState == null
                            && _ghostTransform is null && !_driverInitialized && _driverState == null && ExternalIk is null && _smooth is null
                        : Live(ExternalGhostEntity) && Live(_externalController) && Live(_ghostTransform)
                            && SameEntity(_externalController.entity, ExternalGhostEntity)
                            && Ptr(_ghostState) == Ptr(ExternalGhostEntity._entity.Serializer), "External ghost cache is inconsistent.");
                    Require(!unprocessed || (_driver == null && CurrentIk is null
                        && OwnedGhostEntity is null && ExternalGhostEntity is null),
                        "Replacement already owns native driver/ghost connections.");
                }

                internal void RequireStorage(GolfCartController cart)
                {
                    foreach (var storage in _storage)
                        Require(storage != null && !storage.WasCollected, "Retained occupant cache storage is unavailable.");
                    Require(cart.Pointer == _cartPointer
                        && Ptr(cart._isLocalConnectedSlots) == Ptr(_storage[0]) && Ptr(cart._localConnectedActions) == Ptr(_storage[1])
                        && Ptr(cart._localConnectedActors) == Ptr(_storage[2]) && Ptr(cart._previousProcessedConnectedSlots) == Ptr(_storage[3])
                        && Ptr(cart._externalEntityTransforms) == Ptr(_storage[4]) && Ptr(cart._externalPlayerAnimators) == Ptr(_storage[5])
                        && Ptr(cart._externalVailActors) == Ptr(_storage[6]), "Native occupant cache storage was replaced.");
                }

                internal void Apply(GolfCartController cart, Func<BoltEntity, BoltEntity> remap)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        cart._isLocalConnectedSlots[i] = _local[i]; cart._localConnectedActions[i] = _actions[i];
                        cart._localConnectedActors[i] = _actors[i]; cart._previousProcessedConnectedSlots[i] = remap(_previous[i]);
                        cart._externalEntityTransforms[i] = _transforms[i]; cart._externalPlayerAnimators[i] = _animators[i];
                        cart._externalVailActors[i] = _externalActors[i];
                    }
                    cart._localPlayerDriverAction = _driver; cart._currentDriverIkTargetsController = CurrentIk;
                    cart._ownedGolfCartGhost = _ownedGhost; cart._ownedGolfCartGhostController = _ownedController;
                    cart._ownedGolfCartGhostEntity = remap(OwnedGhostEntity);
                    cart._externalGolfCartGhostState = _ghostState; cart._externalGolfCartGhostController = _externalController;
                    cart._externalGolfCartGhostEntity = remap(ExternalGhostEntity); cart._externalGolfCartGhostTransform = _ghostTransform;
                    cart._externalDriverPlayerInitialized = _driverInitialized; cart._externalDriverPlayerState = DriverState(remap);
                    cart._externalDriverIkTargetsController = ExternalIk; cart._externalDriverPlayerSmoothScript = _smooth;
                    cart._currentRootTransformSyncedValue = _rootSynced;
                }

                private IPlayerState DriverState(Func<BoltEntity, BoltEntity> remap)
                {
                    if (_driverState == null) return null;
                    // The resolved state must come from the explicit ghost-driver identity, never
                    // from a new nearest-player lookup or the physical cart's raw driver slot.
                    Require(_ghostState != null, "External driver state has no ghost state.");
                    var replacement = remap(_driverEntity);
                    if (replacement?.Pointer == _driverEntity.Pointer) return _driverState;
                    Require(replacement.TryFindState<IPlayerState>(out var state), "Mapped external driver has no player state.");
                    return state;
                }

                internal void RequireCurrent(GolfCartController cart) => RequireMapped(cart, value => value);

                internal void RequireMapped(GolfCartController cart, Func<BoltEntity, BoltEntity> remap)
                {
                    if (cart.Pointer == _cartPointer) RequireStorage(cart);
                    Require((CartConnections(cart) & VehiclePreservationBlockers.LocalConnectionUnavailable) == 0,
                        "Cart connection arrays changed shape.");
                    for (int i = 0; i < 4; i++)
                    {
                        RequireSurvivor(_transforms[i], cart);
                        RequireSurvivor(_animators[i], cart);
                        RequireSurvivor(_externalActors[i], cart);
                        Require(cart._isLocalConnectedSlots[i] == _local[i]
                            && Ptr(cart._localConnectedActions[i]) == Ptr(_actions[i])
                            && Ptr(cart._localConnectedActors[i]) == Ptr(_actors[i])
                            && Ptr(cart._previousProcessedConnectedSlots[i]) == Ptr(remap(_previous[i]))
                            && Ptr(cart._externalEntityTransforms[i]) == Ptr(_transforms[i])
                            && Ptr(cart._externalPlayerAnimators[i]) == Ptr(_animators[i])
                            && Ptr(cart._externalVailActors[i]) == Ptr(_externalActors[i]), "Cart seat cache readback differs.");
                    }
                    RequireSurvivor(CurrentIk, cart); RequireSurvivor(ExternalIk, cart);
                    RequireSurvivor(_smooth, cart);
                    RequireSurvivor(_ownedController, cart); RequireSurvivor(_externalController, cart);
                    RequireSurvivor(_ghostTransform, cart);
                    Require(Ptr(cart._localPlayerDriverAction) == Ptr(_driver)
                        && Ptr(cart._currentDriverIkTargetsController) == Ptr(CurrentIk)
                        && Ptr(cart._ownedGolfCartGhost) == Ptr(_ownedGhost)
                        && Ptr(cart._ownedGolfCartGhostController) == Ptr(_ownedController)
                        && Ptr(cart._ownedGolfCartGhostEntity) == Ptr(remap(OwnedGhostEntity))
                        && Ptr(cart._externalGolfCartGhostState) == Ptr(_ghostState)
                        && Ptr(cart._externalGolfCartGhostController) == Ptr(_externalController)
                        && Ptr(cart._externalGolfCartGhostEntity) == Ptr(remap(ExternalGhostEntity))
                        && Ptr(cart._externalGolfCartGhostTransform) == Ptr(_ghostTransform)
                        && cart._externalDriverPlayerInitialized == _driverInitialized
                        && Ptr(cart._externalDriverPlayerState) == Ptr(DriverState(remap))
                        && Ptr(cart._externalDriverIkTargetsController) == Ptr(ExternalIk)
                        && Ptr(cart._externalDriverPlayerSmoothScript) == Ptr(_smooth)
                        && cart._currentRootTransformSyncedValue.HasValue == _rootSynced.HasValue
                        && (!_rootSynced.HasValue || cart._currentRootTransformSyncedValue.Value == _rootSynced.Value)
                        && cart._pendingDriverIkTargetsController == null && cart._driverConnectIkCoroutine == null,
                        "Cart ghost/driver cache readback differs.");
                }

                private static void RequireSurvivor(Component component, GolfCartController cart)
                    => Require(component is null || (Live(component) && !component.transform.IsChildOf(cart.entity.transform)),
                        "An occupant cache is destroyed or owned by the retiring cart hierarchy.");
            }

            // Keep native solver/operation objects, weights, activation clocks and target identity.
            // SetupIkChains would restart these; native SetupIkChain (0x18352C900) parents the
            // generated hand target below the cart, so that target must move BEFORE destruction.
            private sealed class CartIkBinding
            {
                private readonly IkTargetsController _controller;
                private readonly List<IkNode> _nodes = new();
                private readonly Il2CppObjectBase _operations;
                private readonly IntPtr[] _operationIds;
                private readonly List<TransformReference> _references = new();
                private bool _parked;
                internal CartIkBinding(IkTargetsController controller, Binding source, Binding target)
                {
                    Require(Live(controller) && !controller.IsSmoothTransitioning(), "Driver IK is unavailable or still transitioning.");
                    _controller = controller;
                    var operations = controller._activeOperations;
                    Require(operations != null && operations.Count <= 16, "Unbounded or absent driver IK operations.");
                    _operations = operations; _operationIds = new IntPtr[operations.Count];
                    for (int i = 0; i < operations.Count; i++)
                    {
                        var operation = operations[i];
                        Require(operation != null && !operation.WasCollected, "An active IK operation is unavailable.");
                        _operationIds[i] = operation.Pointer;
                        Add(operation._ikTarget);
                    }
                    for (int i = 0; i < operations.Count; i++)
                    {
                        var operation = operations[i];
                        _references.Add(new TransformReference(() => operation._relativeTo, value => operation._relativeTo = value, source, target, _nodes));
                        _references.Add(new TransformReference(() => operation._relativeTo2, value => operation._relativeTo2 = value, source, target, _nodes));
                    }
                    // RootMotion is not a Core compile reference. Read only the exact generated
                    // LimbIK.solver/IKSolverLimb fields, not a guessed memory offset or field scan.
                    foreach (string limb in new[] { "_leftHandLimb", "_rightHandLimb" })
                    {
                        var limbObject = ReadNativeProperty(controller, limb);
                        var solver = ReadNativeProperty(limbObject, "solver");
                        foreach (string field in new[] { "target", "bendGoal" })
                        {
                            var property = solver.GetType().GetProperty(field, BindingFlags.Instance | BindingFlags.Public);
                            Require(property != null && property.PropertyType == typeof(Transform) && property.CanRead && property.CanWrite,
                                "The qualified IK target API is unavailable.");
                            Transform Read() => (Transform)property.GetValue(solver);
                            void Write(Transform value) => property.SetValue(solver, value);
                            var node = Read(); Add(node);
                            _references.Add(new TransformReference(Read, Write, source, target, _nodes));
                        }
                    }

                    void Add(Transform node)
                    {
                        if (!Live(node) || !node.IsChildOf(source.Entity.transform)) return;
                        if (Contains(node)) return;
                        // Only generated targets may be transplanted. Serialized cart targets are
                        // remapped in references below and remain owned by their original prefab.
                        bool generated = false;
                        foreach (var op in operations) generated |= Ptr(op._ikTarget) == node.Pointer;
                        if (!generated) return;
                        Require(node.childCount == 0 && node.GetComponents<Component>().Length == 1,
                            "An IK target has unsupported owned children/components.");
                        _nodes.Add(new IkNode(node, source.Entity.transform, target?.Entity.transform));
                    }
                }
                private static Il2CppObjectBase ReadNativeProperty(Il2CppObjectBase instance, string name)
                {
                    Require(instance != null && !instance.WasCollected, "IK object is unavailable.");
                    var property = instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                    var result = property?.GetValue(instance) as Il2CppObjectBase;
                    Require(result != null && !result.WasCollected, "Qualified IK property unavailable: " + name);
                    return result;
                }
                internal bool Contains(Transform node)
                {
                    foreach (var entry in _nodes) if (entry.Node.Pointer == node.Pointer) return true;
                    return false;
                }
                internal void Move(bool forward)
                {
                    foreach (var reference in _references) reference.Write(forward);
                    foreach (var entry in _nodes) entry.Move(forward);
                    _parked = false;
                }
                internal void BindTarget(Binding target)
                {
                    foreach (var reference in _references) reference.BindTarget(target);
                    foreach (var entry in _nodes) entry.BindTarget(target.Entity.transform);
                }
                internal void Park()
                {
                    RequireCurrent(false);
                    foreach (var entry in _nodes) entry.Park(_controller.transform);
                    _parked = true;
                }
                internal void RequireCurrent(bool moved)
                {
                    Require(Live(_controller) && Ptr(_controller._activeOperations) == Ptr(_operations)
                        && _controller._activeOperations.Count == _operationIds.Length, "Driver IK operation lifetime changed.");
                    for (int i = 0; i < _operationIds.Length; i++)
                        Require(Ptr(_controller._activeOperations[i]) == _operationIds[i], "Driver IK operation was replaced.");
                    foreach (var reference in _references) reference.RequireCurrent(moved, _parked);
                    foreach (var entry in _nodes)
                        if (_parked) entry.RequireParked(_controller.transform); else entry.RequireCurrent(moved);
                }
            }

            private sealed class IkNode
            {
                internal readonly Transform Node;
                private readonly Transform _before;
                private Transform _after;
                private readonly CartTransformPath _parentPath;
                private readonly UnityEngine.Vector3 _position, _scale;
                private readonly UnityEngine.Quaternion _rotation;
                private readonly int _sibling;
                internal IkNode(Transform node, Transform source, Transform target)
                {
                    Node = node; _before = node.parent; _parentPath = CartTransformPath.Capture(_before, source);
                    Require(_parentPath != null, "Generated IK target must have a source-cart parent.");
                    if (target is not null) BindTarget(target);
                    _position = node.localPosition; _rotation = node.localRotation; _scale = node.localScale;
                    _sibling = node.GetSiblingIndex();
                }
                internal void Move(bool forward)
                {
                    Require(Live(forward ? _after : _before), "An IK destination parent is unavailable.");
                    Node.SetParent(forward ? _after : _before, false);
                    Node.localPosition = _position; Node.localRotation = _rotation; Node.localScale = _scale;
                    if (!forward) Node.SetSiblingIndex(_sibling);
                }
                internal void BindTarget(Transform target) => _after = _parentPath.Resolve(target);
                internal void Park(Transform retainedRoot)
                {
                    Require(Live(retainedRoot) && !retainedRoot.IsChildOf(Node), "IK parking root is invalid.");
                    Node.SetParent(retainedRoot, true);
                    RequireParked(retainedRoot);
                }
                internal void RequireParked(Transform retainedRoot)
                    => Require(Live(Node) && Live(retainedRoot) && Ptr(Node.parent) == Ptr(retainedRoot),
                        "A retained IK target left its independent parking root.");
                internal void RequireCurrent(bool moved)
                    => Require(Live(Node) && Ptr(Node.parent) == Ptr(moved ? _after : _before)
                        && Node.localPosition == _position && Node.localRotation == _rotation && Node.localScale == _scale,
                        "Retained IK target parent/pose changed.");
            }

            private sealed class TransformReference
            {
                private readonly Func<Transform> _read;
                private readonly Action<Transform> _write;
                private readonly Transform _before;
                private Transform _after;
                private readonly CartTransformPath _path;
                private bool _clearedForParking;
                internal TransformReference(Func<Transform> read, Action<Transform> write,
                    Binding source, Binding target, List<IkNode> retained = null)
                {
                    _read = read; _write = write; _before = read(); _after = _before;
                    if (!Live(_before)) { Require(_before is null, "A reference points at a destroyed transform."); return; }
                    if (retained != null)
                        foreach (var node in retained) if (node.Node.Pointer == _before.Pointer) return;
                    _path = CartTransformPath.Capture(_before, source.Entity.transform);
                    if (target != null) BindTarget(target);
                }
                internal void BindTarget(Binding target)
                    => _after = _path == null ? _before : _path.Resolve(target.Entity.transform);
                internal void Write(bool forward)
                {
                    Require(!forward || _path == null || Live(_after), "A cart reference has not resolved its replacement path.");
                    if (_clearedForParking || Ptr(_before) != Ptr(_after)) _write(forward ? _after : _before);
                    _clearedForParking = false;
                }
                internal void Park()
                {
                    if (_path == null) return;
                    _clearedForParking = true;
                    _write(null);
                    Require(_read() is null, "A player constraint still points into the retiring cart.");
                }
                internal void RequireCurrent(bool moved, bool parked = false)
                {
                    if (_clearedForParking)
                    {
                        Require(parked && !moved && _read() is null, "A parked player constraint changed.");
                        return;
                    }
                    var expected = moved ? _after : _before;
                    Require((expected is null || (parked && !moved && _path != null) || Live(expected)) && Ptr(_read()) == Ptr(expected),
                        "An inbound cart transform reference changed or was destroyed.");
                }
            }

            // Captured before retirement. Resolving a new cart never traverses the old native
            // hierarchy or assumes that its old Bolt identity is still attached.
            private sealed class CartTransformPath
            {
                private readonly (int Index, string Name)[] _steps;
                private CartTransformPath((int, string)[] steps) => _steps = steps;
                internal static CartTransformPath Capture(Transform value, Transform root)
                {
                    if (value is null) return null;
                    Require(Live(value) && Live(root), "Cart transform schema is unavailable.");
                    if (!value.IsChildOf(root)) return null;
                    var path = new List<(int, string)>();
                    while (value.Pointer != root.Pointer)
                    {
                        Require(Live(value.parent) && path.Count < 64, "Cart transform path exceeds its bound.");
                        path.Add((value.GetSiblingIndex(), value.name)); value = value.parent;
                    }
                    path.Reverse();
                    return new(path.ToArray());
                }
                internal Transform Resolve(Transform root)
                {
                    Require(Live(root), "Replacement cart root is unavailable.");
                    foreach (var step in _steps)
                    {
                        Require(step.Index >= 0 && step.Index < root.childCount, "Replacement cart path is missing.");
                        root = root.GetChild(step.Index);
                        Require(Live(root) && root.name == step.Name, "Replacement cart path differs from captured source schema.");
                    }
                    return root;
                }
            }

            private static Transform MapCartTransform(Transform value, Transform source, Transform target)
            {
                if (value is null) return null;
                Require(Live(value), "A cart transform reference was destroyed.");
                if (!value.IsChildOf(source)) return value;
                var path = new List<(int Index, string Name)>();
                var cursor = value;
                while (cursor.Pointer != source.Pointer)
                {
                    Require(Live(cursor.parent) && path.Count < 64, "Invalid source cart transform path.");
                    path.Add((cursor.GetSiblingIndex(), cursor.name)); cursor = cursor.parent;
                }
                cursor = target;
                for (int i = path.Count - 1; i >= 0; i--)
                {
                    var step = path[i];
                    Require(step.Index < cursor.childCount, "Replacement cart transform path is missing.");
                    cursor = cursor.GetChild(step.Index);
                    Require(cursor.name == step.Name, "Replacement cart transform path differs.");
                }
                return cursor;
            }

            private sealed class CartRadioBinding
            {
                private readonly GolfCartRadio _radio;
                private readonly bool _local;
                private readonly IPlayerState _networkDriver;
                private readonly BoltEntity _driver;
                private readonly IPlayerState _publishedState;
                private readonly bool _publishedOn;
                private readonly int _publishedTrack;
                private readonly CartRadioValues _values;
                private bool _paused;
                private CartRadioBinding _restoredFrom;
                private IPlayerState _restoredDriverState;
                internal CartRadioBinding(GolfCartController cart)
                {
                    var radios = cart.GetComponentsInChildren<GolfCartRadio>(true);
                    Require(radios.Length <= 1, "Multiple cart radios require independent lifetime repair.");
                    _radio = radios.Length == 0 ? null : radios[0];
                    if (_radio is not null)
                    {
                        RequireExactSaveType<GolfCartRadio>(_radio);
                        var player = _radio._radioPlayer;
                        RequireExactSaveType<RadioPlayer>(player);
                        Require(Live(player) && !_radio.IsInvoking() && !player.IsInvoking() && !player._isPlayingAdvert,
                            "Pending radio callbacks or adverts cannot be restarted as a known station.");
                        Require(Live(_radio._radioScreenVisuals), "Radio screen binding is unavailable.");
                        _local = _radio._hasLocalDriver; _networkDriver = _radio._driverState;
                        Require(!_local || _networkDriver == null, "Radio has both local and network driver subscriptions.");
                        if (_local || _networkDriver != null)
                        {
                            var ghost = RequireCartEndpoint(cart.BoltEntityState.ConnectedSlots.GetValueNetworkId(0).PackedValue);
                            Require(ghost.TryFindState<IGolfCartState>(out var state), "Radio driver ghost is unavailable.");
                            _driver = RequireCartEndpoint(state.ConnectedSlots.GetValueNetworkId(0).PackedValue);
                            Require(_driver.TryFindState<IPlayerState>(out var driverState)
                                && (_networkDriver == null || Ptr(driverState) == Ptr(_networkDriver)),
                                "Radio subscription does not belong to the exact ghost driver.");
                            _publishedState = driverState;
                            _publishedOn = driverState.RadioIsOn; _publishedTrack = driverState.RadioTrackHash;
                            Require(player.IsOn == _publishedOn && (!player.IsOn || player.GetCurrentTrackHash == _publishedTrack),
                                "Radio playback and published driver state have not settled.");
                            if (_local) RequireLocalDriver();
                        }
                        Require(!player.IsOn || _driver is not null, "An active radio has no exact driver lifecycle.");
                        _values = new(true, player.IsOn, player.GetCurrentTrackHash, Finite(player._volume),
                            player._continuousPlayback, _radio._radioScreenVisuals.activeSelf,
                            _radio._areRadioControlsActive, _radio._uiIsActive);
                        RequirePlaylist(player, _values);
                    }
                    else _values = new(false, false, 0, 0, false, false, false, false);
                    RequireCallbacks(cart);
                }
                internal CartRadioValues Values => _values;
                internal void RequireCompatible(CartRadioBinding other)
                {
                    Require((_radio is null) == (other._radio is null), "Replacement radio topology differs.");
                    if (_radio is not null) RequirePlaylist(other._radio._radioPlayer, _values);
                }
                private void RequireLocalDriver() => RequireLocalDriver(_driver);

                private static void RequireLocalDriver(BoltEntity driver)
                    => Require(Live(driver) && driver.isAttached && driver.isOwner && SameEntity(TheForest.Utils.LocalPlayer.Entity, driver),
                        "Local radio callbacks require their exact local driver, never a server's unrelated host player.");

                internal void Pause()
                {
                    if (_radio is null) { _paused = true; return; }
                    Require(!_paused, "Radio pause is single-use.");
                    // Qualified native callbacks remove their own input/IPlayerState subscriptions.
                    // OnDisable alone removes only the four cart delegates and is insufficient.
                    if (_local) { RequireLocalDriver(); _radio.OnLocalDriverDisconnected(); }
                    else if (_networkDriver != null) _radio.OnNetworkDriverDisconnected();
                    _radio._radioPlayer.SetRadioOn(false, false);
                    _radio.ShowUiButtons(false);
                    // Local disconnect publishes radio-off as part of ordinary dismount. This
                    // is not a dismount: retain the authoritative station/play intent so peers
                    // capture the same snapshot while each stops its own native playback.
                    if (_local)
                    {
                        RequireLocalDriver();
                        _publishedState.RadioIsOn = _publishedOn;
                        _publishedState.RadioTrackHash = _publishedTrack;
                    }
                    _paused = true;
                    RequireCallbacks(_radio._golfCartController);
                }

                internal void RestoreFrom(CartRadioBinding source, Func<BoltEntity, BoltEntity> remap)
                {
                    source.RequireCompatible(this);
                    if (_radio is null) return;
                    Require(_restoredFrom == null && !_radio._hasLocalDriver && _radio._driverState == null,
                        "Radio subscriptions were already restored or replaced.");
                    var values = source._values;
                    var player = _radio._radioPlayer;
                    RequirePlaylist(player, values);
                    var driver = source._driver is null ? null : remap(source._driver);
                    IPlayerState driverState = null;
                    if (source._driver is not null)
                        Require(Live(driver) && driver.isAttached && driver.TryFindState<IPlayerState>(out driverState)
                            && driverState.RadioIsOn == source._publishedOn && driverState.RadioTrackHash == source._publishedTrack,
                            "Mapped radio driver/state is unavailable or its authoritative radio intent differs.");
                    if (source._local)
                    {
                        RequireLocalDriver(driver);
                        _radio.OnLocalDriverConnected();
                    }
                    else if (source._networkDriver != null)
                    {
                        _radio.OnNetworkDriverConnected(driverState);
                    }
                    // These are native restart operations, not copied FMOD handles or callbacks.
                    // Local connection can randomize a playlist; validate the exact hash again.
                    RequirePlaylist(player, values);
                    player.SetRadioOn(false, false);
                    player._volume = values.Volume;
                    player.SetContinuousPlayback(values.ContinuousPlayback);
                    if (values.IsOn)
                    {
                        player.SetRadioOn(true, false);
                        player.PlayTrackWithHash(values.TrackHash);
                    }
                    else player._currentTrackHash = values.TrackHash;
                    _radio._radioScreenVisuals.SetActive(values.ScreenActive);
                    _radio._areRadioControlsActive = values.ControlsActive;
                    _radio.ShowUiButtons(values.UiActive);
                    if (source._local)
                    {
                        _radio.PushLocalPlayerRadioDataToNetwork();
                        RequireLocalDriver(driver);
                        driverState.RadioIsOn = source._publishedOn;
                        driverState.RadioTrackHash = source._publishedTrack;
                    }
                    _restoredDriverState = driverState; _restoredFrom = source; _paused = false;
                    RequireCallbacks(_radio._golfCartController);
                }

                private static void RequirePlaylist(RadioPlayer player, CartRadioValues values)
                {
                    Require(Live(player) && player._orderedEvents != null && player._orderedEvents.Count > 0
                        && player._orderedEvents.Count <= 256, "Radio's native playlist must be initialized and bounded.");
                    if (!values.IsOn) return;
                    int matches = 0;
                    foreach (var name in player._orderedEvents)
                    {
                        Require(name != null && name.Length <= 512, "Radio playlist contains an unknown event.");
                        if (Animator.StringToHash(name) == values.TrackHash) matches++;
                    }
                    Require(matches == 1, "Radio track hash is absent or ambiguous; native fallback-to-first-track is forbidden.");
                }
                internal void RequireCallbacks(GolfCartController cart)
                {
                    if (_radio is not null)
                    {
                        var expected = _restoredFrom ?? this;
                        Require(Live(_radio) && _radio._golfCartController?.Pointer == cart.Pointer
                            && _radio._hasLocalDriver == (!_paused && expected._local)
                            && Ptr(_radio._driverState) == (_paused || expected._networkDriver == null ? IntPtr.Zero
                                : Ptr(_restoredFrom == null ? expected._networkDriver : _restoredDriverState))
                            && _radio._radioPlayer.IsOn == (!_paused && expected._values.IsOn)
                            && (_paused || !expected._values.IsOn || _radio._radioPlayer.GetCurrentTrackHash == expected._values.TrackHash),
                            "Radio driver subscription or retained station/play state changed.");
                        var published = _restoredFrom == null ? expected._publishedState : _restoredDriverState;
                        if (published != null)
                            Require(published.RadioIsOn == expected._publishedOn
                                && published.RadioTrackHash == expected._publishedTrack,
                                "Published driver radio intent changed during handoff.");
                    }
                    Check(cart.OnLocalDriverConnected, "OnLocalDriverConnected");
                    Check(cart.OnLocalDriverDisconnected, "OnLocalDriverDisconnected");
                    Check(cart.OnNetworkDriverConnected, "OnNetworkDriverConnected");
                    Check(cart.OnNetworkDriverDisconnected, "OnNetworkDriverDisconnected");
                }
                private void Check(Il2CppSystem.Delegate callback, string method)
                {
                    if (callback == null) return;
                    var list = callback.GetInvocationList();
                    Require(_radio != null && list.Length == 1 && list[0].Target?.Pointer == _radio.Pointer
                        && list[0].Method?.Name == method,
                        "Opaque driver subscriber cannot be copied across the old cart lifetime.");
                }
            }
        }
    }
}
