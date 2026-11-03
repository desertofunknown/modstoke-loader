// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using global::Sons.Gameplay.TreeCutting;
using UnityEngine;

namespace Modstoke.Sons
{
    internal static class LocatorPrefabReads
    {
        private const int MaxTransformNodes = 128;

        internal static LocatorPrefabObservation[] Capture(WorldObjectLocatorData data)
        {
            var observations = new LocatorPrefabObservation[3];
            for (var lodIndex = 0; lodIndex < observations.Length; lodIndex++)
                observations[lodIndex] = CaptureLod(data, lodIndex);
            return observations;
        }

        private static LocatorPrefabObservation CaptureLod(WorldObjectLocatorData data, int lodIndex)
        {
            var issues = LocatorPrefabIssues.None;
            List<FallingPrefabCandidate> candidates = null;
            List<(TreeCutManager Manager, GameObject Fractured, BoltEntity Entity, int? PrefabId)> pending = null;
            GameObject source = null;
            TreeSwapper swapper = null;
            GameObject cutPrefab = null;
            var sourceRead = false;
            var cutRead = false;

            try
            {
                if (!IsLive(data))
                    issues |= LocatorPrefabIssues.ReadFailed;
                else
                {
                    source = ReadLod(data, lodIndex);
                    sourceRead = true;
                    if (!IsLive(source))
                        issues |= LocatorPrefabIssues.MissingLodPrefab;
                    else
                    {
                        swapper = source.GetComponent<TreeSwapper>();
                        if (!IsLive(swapper))
                            issues |= LocatorPrefabIssues.MissingSwapper;
                        else
                        {
                            cutPrefab = swapper._treeToCutDownPrefab;
                            cutRead = true;
                            if (!IsLive(cutPrefab))
                                issues |= LocatorPrefabIssues.MissingCutPrefab;
                            else
                            {
                                pending = new();
                                Traverse(cutPrefab, pending, ref issues);
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                issues |= LocatorPrefabIssues.ReadFailed;
            }

            // No candidate escapes until its source chain and fractured reference have been reread.
            try
            {
                var stable = sourceRead && IsLive(data);
                if (sourceRead && (!stable || !SameReference(source, ReadLod(data, lodIndex))))
                {
                    issues |= LocatorPrefabIssues.ReferencesChanged;
                    stable = false;
                }
                if (cutRead && (!IsLive(source) || !IsLive(swapper)
                    || !SameReference(swapper, source.GetComponent<TreeSwapper>())
                    || !SameReference(cutPrefab, swapper._treeToCutDownPrefab)))
                {
                    issues |= LocatorPrefabIssues.ReferencesChanged;
                    stable = false;
                }
                if (stable && pending is { Count: > 0 })
                {
                    HashSet<int> ids = null;
                    foreach (var entry in pending)
                    {
                        try
                        {
                            if (!IsLive(entry.Manager)
                                || !SameReference(entry.Fractured, entry.Manager._trunkFractured))
                            {
                                issues |= LocatorPrefabIssues.ReferencesChanged;
                                continue;
                            }
                            var root = cutPrefab.transform;
                            var current = entry.Manager.transform;
                            if (!SameReference(root, current) && !current.IsChildOf(root))
                            {
                                issues |= LocatorPrefabIssues.ReferencesChanged;
                                continue;
                            }
                            if (entry.PrefabId.HasValue && (!SameReference(entry.Entity, entry.Fractured.GetComponent<BoltEntity>())
                                || entry.Entity.prefabId.Value != entry.PrefabId.Value))
                            {
                                issues |= LocatorPrefabIssues.ReferencesChanged;
                                continue;
                            }
                            if (entry.PrefabId is int prefabId && (ids ??= new()).Add(prefabId))
                                (candidates ??= new()).Add(new FallingPrefabCandidate(prefabId));
                        }
                        catch (Exception)
                        {
                            issues |= LocatorPrefabIssues.ReadFailed;
                        }
                    }
                }
            }
            catch (Exception)
            {
                issues |= LocatorPrefabIssues.ReadFailed;
                candidates = null;
            }

            return new LocatorPrefabObservation(lodIndex, issues,
                candidates is null ? Array.Empty<FallingPrefabCandidate>() : candidates);
        }

        private static void Traverse(GameObject cutPrefab,
            List<(TreeCutManager Manager, GameObject Fractured, BoltEntity Entity, int? PrefabId)> pending,
            ref LocatorPrefabIssues issues)
        {
            var stack = new Stack<Transform>();
            stack.Push(cutPrefab.transform);
            var scheduled = 1;
            var foundManager = false;
            while (stack.Count != 0)
            {
                var node = stack.Pop();
                try
                {
                    if (!IsLive(node))
                    {
                        issues |= LocatorPrefabIssues.ReadFailed;
                        continue;
                    }
                    // The game's component lookup selects one manager per object.
                    var manager = node.GetComponent<TreeCutManager>();
                    if (IsLive(manager))
                    {
                        foundManager = true;
                        ReadManager(manager, pending, ref issues);
                    }

                    var childCount = node.childCount;
                    var take = Math.Min(childCount, MaxTransformNodes - scheduled);
                    if (take < childCount)
                        issues |= LocatorPrefabIssues.TraversalLimit;
                    // Reserve the budget before reading children, including failed reads.
                    scheduled += take;
                    for (var i = take - 1; i >= 0; i--)
                    {
                        try
                        {
                            stack.Push(node.GetChild(i));
                        }
                        catch (Exception)
                        {
                            issues |= LocatorPrefabIssues.ReadFailed;
                        }
                    }
                }
                catch (Exception)
                {
                    issues |= LocatorPrefabIssues.ReadFailed;
                }
            }
            if (!foundManager && (issues & (LocatorPrefabIssues.ReadFailed
                | LocatorPrefabIssues.TraversalLimit)) == 0)
                issues |= LocatorPrefabIssues.MissingManager;
        }

        private static void ReadManager(TreeCutManager manager,
            List<(TreeCutManager Manager, GameObject Fractured, BoltEntity Entity, int? PrefabId)> pending,
            ref LocatorPrefabIssues issues)
        {
            var fractured = manager._trunkFractured;
            BoltEntity entity = null;
            int? prefabId = null;
            try
            {
                if (!IsLive(fractured))
                {
                    issues |= LocatorPrefabIssues.MissingFracturedObject;
                    return;
                }
                entity = fractured.GetComponent<BoltEntity>();
                if (!IsLive(entity))
                {
                    issues |= LocatorPrefabIssues.MissingBoltEntity;
                    return;
                }
                prefabId = entity.prefabId.Value;
            }
            finally
            {
                pending.Add((manager, fractured, entity, prefabId));
            }
        }

        private static GameObject ReadLod(WorldObjectLocatorData data, int lodIndex) => lodIndex switch
        {
            0 => data._lod0Prefab,
            1 => data._lod1Prefab,
            _ => data._lod2Prefab
        };

        private static bool IsLive(UnityEngine.Object value) =>
            value is not null && !value.WasCollected && value;

        private static bool SameReference(UnityEngine.Object before, UnityEngine.Object after)
        {
            if (before is null)
                return after is null;
            return IsLive(before) && IsLive(after) && before.Pointer == after.Pointer;
        }
    }
}
