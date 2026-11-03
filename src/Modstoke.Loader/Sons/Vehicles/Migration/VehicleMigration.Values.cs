// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Bolt;
using UnityEngine;
using Vector3 = System.Numerics.Vector3;
using Quaternion = System.Numerics.Quaternion;

namespace Modstoke.Sons
{
    public static partial class VehiclePreservation
    {
        // Independent local values, not a complete migration payload. In particular, these do not
        // contain native physics history, pending callbacks, timer state or inbound references.
        private sealed record CartHierarchyPose(int Parent, int Sibling, string Name, bool Active,
            Vector3 Position, Quaternion Rotation, Vector3 Scale);

        private sealed record CartTransformValues(int Simulate, int Render, Vector3 PreviousPosition,
            Vector3 CurrentPosition, Quaternion PreviousRotation, Quaternion CurrentRotation);

        private sealed class CartReplacementValues
        {
            private readonly VehicleValues _values;
            private readonly CartHierarchyPose[] _hierarchy;
            private readonly CartTransformValues[] _transforms;
            private readonly CartNetworkSeed[] _freshSeeds;

            private CartReplacementValues(VehicleValues values, CartHierarchyPose[] hierarchy,
                CartTransformValues[] transforms, CartNetworkSeed[] freshSeeds)
            { _values = values; _hierarchy = hierarchy; _transforms = transforms; _freshSeeds = freshSeeds; }

            internal VehiclePreservationBlockers Blockers => _values.Blockers;

            internal static CartReplacementValues CaptureFrom(Binding binding)
            {
                RequireSaveHandoffCart(binding);
                var hierarchy = ReadHierarchy(binding, out var nodes);
                var values = Capture(binding);
                var transforms = ReadTransforms(binding, nodes);
                Require(binding.Matches() && !_reentered, "Cart changed while capturing replacement values.");
                return new(values, hierarchy, transforms, CaptureCartNetworkSeeds(binding));
            }

            internal void RequireSourceUnchanged(Binding source)
            {
                var current = CaptureFrom(source);
                // Reservation changes only SaveActive. The source retains its own original record.
                var save = current._values.WorldItemSave;
                Require(SameWritable(_values, current._values)
                    && _values.NetworkId == current._values.NetworkId
                    && _values.PrefabId == current._values.PrefabId
                    && _values.StateTypeId == current._values.StateTypeId
                    && _values.WorldItemSave == (save with { SaveActive = _values.WorldItemSave.SaveActive })
                    && (_values.Blockers == current._values.Blockers
                        || ((_values.Blockers ^ current._values.Blockers) & ~VehiclePreservationBlockers.LocalConnectionObserved) == 0
                        && OccupantsFor(source.Cart)?.MatchesRestoredReferences(source) == true)
                    && (Equal(_values.References, current._values.References) || OccupantsFor(source.Cart)?.MatchesRestoredReferences(source) == true)
                    && Equal(_values.NetworkPoses, current._values.NetworkPoses)
                    && Equal(_hierarchy, current._hierarchy) && Equal(_transforms, current._transforms),
                    "Captured cart values, references or transform bindings changed after reservation.");
            }

            internal Transform[] ValidateReplacement(Binding target)
            {
                RequireSaveHandoffCart(target);
                Require(target.Entity.isOwner && target.Id != _values.NetworkId
                    && target.Prefab == _values.PrefabId && target.StateType == _values.StateTypeId,
                    "Replacement must be a new owned cart of the captured prefab and state type.");
                ValidateRestoreShape(target, _values);
                var hierarchy = ReadHierarchy(target, out var nodes);
                Require(hierarchy.Length == _hierarchy.Length, "Cart replacement hierarchy size differs.");
                for (int i = 0; i < hierarchy.Length; i++)
                {
                    var expected = _hierarchy[i];
                    var actual = hierarchy[i];
                    Require(actual.Parent == expected.Parent && actual.Sibling == expected.Sibling
                        && (i == 0 || actual.Name == expected.Name),
                        "Cart replacement hierarchy differs.");
                }
                var transforms = ReadTransforms(target, nodes);
                for (int i = 0; i < transforms.Length; i++)
                {
                    var actual = transforms[i];
                    var expected = _transforms[i];
                    Require((actual.Simulate == -1 || actual.Simulate == expected.Simulate)
                        && (actual.Render == -1 || actual.Render == expected.Render),
                        "Cart replacement has a different native transform binding.");
                }
                Require(target.Matches() && !_reentered, "Replacement changed during value preflight.");
                _ = PrepareCartNetworkSeeds(target, _freshSeeds);
                return nodes;
            }

            internal void Restore(Binding target, Transform[] nodes)
            {
                var prepared = PrepareCartNetworkSeeds(target, _freshSeeds);
                // Only the new lifetime is written. No old component, callback or save-data pointer
                // is installed on it. Keeping all objects inactive avoids activation side effects.
                for (int i = 0; i < nodes.Length; i++)
                {
                    nodes[i].localPosition = U(_hierarchy[i].Position);
                    nodes[i].localRotation = U(_hierarchy[i].Rotation);
                    nodes[i].localScale = U(_hierarchy[i].Scale);
                    if (i != 0) nodes[i].gameObject.SetActive(_hierarchy[i].Active);
                }
                RestoreCart(target.Cart, target.CartState, _values.GolfCart);
                RestoreBody(target.Body, _values.Body);
                Require(target.Matches() && !_reentered, "Replacement changed during cart/body restoration.");
                RestoreCartNetworkSeeds(target, _freshSeeds, prepared);
                RequireRestored(target);
            }

            internal void RequireRestored(Binding target)
            {
                var current = CaptureFrom(target);
                Require(target.Entity.isOwner && target.Id != _values.NetworkId
                    && target.Prefab == _values.PrefabId && target.StateType == _values.StateTypeId
                    && SameWritable(_values, current._values)
                    && (Equal(_values.References, current._values.References) || OccupantsFor(target.Cart)?.MatchesRestoredReferences(target) == true)
                    && _transforms.Length == current._transforms.Length,
                    "Replacement cart/body/network-transform readback differs; do not replay restoration.");
                for (int i = 0; i < _transforms.Length; i++)
                    Require(_transforms[i].Simulate == current._transforms[i].Simulate
                        && _transforms[i].Render == current._transforms[i].Render, "Replacement cart transform binding differs.");
                RequireCartNetworkSeeds(target, _freshSeeds);
                Require(current._hierarchy.Length == _hierarchy.Length, "Replacement hierarchy changed during restore.");
                for (int i = 0; i < _hierarchy.Length; i++)
                {
                    // Native instantiation may give the root a different name; child paths are exact.
                    var actual = current._hierarchy[i];
                    if (i == 0) actual = actual with { Name = _hierarchy[i].Name };
                    // Network-bound nodes use the fresh authoritative pose, not an old render
                    // interpolation sample. RequireCartNetworkSeeds checked their new pose above.
                    foreach (var binding in _transforms)
                        if (binding.Simulate == i || binding.Render == i)
                        {
                            actual = actual with { Position = _hierarchy[i].Position, Rotation = _hierarchy[i].Rotation };
                            break;
                        }
                    Require(actual == _hierarchy[i], "Replacement hierarchy/pose readback differs.");
                }
            }

            private static CartHierarchyPose[] ReadHierarchy(Binding binding, out Transform[] nodes)
            {
                var root = binding.Entity.transform;
                Require(Live(root) && root.parent == null && !root.gameObject.activeSelf,
                    "Cart replacement values require an unparented, explicitly inactive root.");
                var objects = new List<Transform>();
                var poses = new List<CartHierarchyPose>();
                Read(root, -1, 0);
                nodes = objects.ToArray();
                return poses.ToArray();

                void Read(Transform node, int parent, int depth)
                {
                    Require(Live(node) && depth <= 64 && objects.Count < MaximumArrayLength,
                        "Cart hierarchy is unavailable or exceeds the supported bound.");
                    int index = objects.Count;
                    objects.Add(node);
                    poses.Add(new(parent, parent < 0 ? 0 : node.GetSiblingIndex(), node.name,
                        node.gameObject.activeSelf, V(node.localPosition), Q(node.localRotation), V(node.localScale)));
                    int count = node.childCount;
                    for (int i = 0; i < count; i++)
                    {
                        var child = node.GetChild(i);
                        if (OccupantsFor(binding.Cart)?.IsRetainedIkNode(child) != true) Read(child, index, depth + 1);
                    }
                    Require(node.childCount == count, "Cart hierarchy changed during capture.");
                }
            }

            private static CartTransformValues[] ReadTransforms(Binding binding, Transform[] nodes)
            {
                var indices = new Dictionary<IntPtr, int>();
                for (int i = 0; i < nodes.Length; i++) indices.Add(nodes[i].Pointer, i);
                var transforms = CartNetworks(binding.CartState);
                var values = new CartTransformValues[transforms.Length];
                for (int i = 0; i < transforms.Length; i++)
                {
                    var transform = transforms[i];
                    Require(transform != null && !transform.WasCollected && (transform.Clamper == null
                        || transform.Clamper.Pointer == NetworkTransform.__c.__9__14_0?.Pointer),
                        "Missing cart network transform or unsupported transform-clamper callback.");
                    var positions = transform.RenderDoubleBufferPosition;
                    var rotations = transform.RenderDoubleBufferRotation;
                    Require(positions != null && rotations != null, "Missing cart network render buffers.");
                    int simulate = Index(transform.Simulate);
                    int render = Index(transform.Render);
                    Require(render < 0 || simulate >= 0, "Cart render transform has no simulation binding.");
                    values[i] = new(simulate, render, V(positions.Previous), V(positions.Current),
                        Q(rotations.Previous), Q(rotations.Current));
                }
                return values;

                int Index(Transform node)
                {
                    if (node is null) return -1;
                    int index = -1;
                    Require(Live(node) && indices.TryGetValue(node.Pointer, out index),
                        "Cart network transform references an unavailable or external hierarchy.");
                    return index;
                }
            }
        }
    }
}
