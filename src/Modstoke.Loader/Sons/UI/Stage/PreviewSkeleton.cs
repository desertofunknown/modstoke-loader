// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Modstoke.Sons
{
    // A bare transform copy of a source rig, grown on demand. Bones are matched by their path below the source
    // root, so bones a mod adds under rig bones resolve like native ones, and nothing is read from bone caches.
    internal sealed class PreviewSkeleton
    {
        private const int MaxNodes = 2048;
        private readonly Dictionary<string, int> _byPath = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);
        private readonly HashSet<string> _repeatedNames = new(StringComparer.Ordinal);
        private readonly List<Transform> _nodes = new();
        private readonly List<int> _parents = new();
        private readonly List<Transform> _chain = new();
        private readonly Transform _source;
        private readonly Transform _sourceHipsParent;
        private readonly int _layer;
        private Dictionary<string, Transform> _sourceNames;

        // The root copy sits where the source root sits relative to space, so parts placed relative to the
        // same space line up with it.
        internal PreviewSkeleton(string key, Transform source, Transform space, Transform parent, int layer)
        {
            Key = key;
            _source = source;
            _layer = layer;
            var root = new GameObject(source.name) { layer = layer };
            root.transform.SetParent(parent, false);
            if (space != null && space.Pointer != source.Pointer) PreviewStage.PlaceRelative(root.transform, source, space);
            Add(root.transform, "", -1);
            var hips = source.name == "Hips" ? source : FindChild(source, "Hips");
            _sourceHipsParent = hips != null && hips.Pointer != source.Pointer ? hips.parent : null;
        }

        internal string Key { get; }
        internal Transform Root => _nodes[0];
        internal int Count => _nodes.Count;
        internal Transform Node(int index) => _nodes[index];
        internal int ParentOf(int index) => _parents[index];

        internal bool TryMap(Transform sourceBone, out int index)
        {
            index = -1;
            if (!PreviewStage.Live(sourceBone) || !PreviewStage.Live(_source)) return false;
            _chain.Clear();
            var current = sourceBone;
            while (current != null && current.Pointer != _source.Pointer)
            {
                _chain.Add(current);
                current = current.parent;
            }
            if (current == null) return false;
            index = 0;
            string path = "";
            for (int i = _chain.Count - 1; i >= 0; i--)
            {
                var node = _chain[i];
                path = path.Length == 0 ? node.name : path + "/" + node.name;
                if (_byPath.TryGetValue(path, out int known))
                {
                    index = known;
                    continue;
                }
                if (_nodes.Count >= MaxNodes) return false;
                var copy = new GameObject(node.name) { layer = _layer };
                copy.transform.SetParent(_nodes[index], false);
                copy.transform.localPosition = node.localPosition;
                copy.transform.localRotation = node.localRotation;
                copy.transform.localScale = node.localScale;
                index = Add(copy.transform, path, index);
            }
            return true;
        }

        // Remap caches and prefab bones store paths from varying bases, so the path is tried from the source
        // root and from Hips' parent, dropping leading segments, before a name that occurs once in the rig.
        internal bool TryMapPath(string path, out int index)
        {
            index = -1;
            if (string.IsNullOrWhiteSpace(path) || !PreviewStage.Live(_source)) return false;
            string trimmed = path.Trim().Trim('/');
            for (int start = 0; start < trimmed.Length;)
            {
                string candidate = trimmed.Substring(start);
                var found = _source.Find(candidate);
                if (found == null && _sourceHipsParent != null) found = _sourceHipsParent.Find(candidate);
                if (found != null) return TryMap(found, out index);
                int slash = trimmed.IndexOf('/', start);
                if (slash < 0) break;
                start = slash + 1;
            }
            int last = trimmed.LastIndexOf('/');
            string name = last >= 0 ? trimmed.Substring(last + 1) : trimmed;
            return SourceNames().TryGetValue(name, out var named) && named != null && TryMap(named, out index);
        }

        /// <summary>Finds a bone already in the copy by name or path.</summary>
        internal bool TryFind(string boneName, out Transform bone)
        {
            bone = null;
            if (string.IsNullOrWhiteSpace(boneName)) return false;
            string key = boneName.Trim();
            if ((_byPath.TryGetValue(key, out int index) || !_repeatedNames.Contains(key) && _byName.TryGetValue(key, out index))
                && PreviewStage.Live(_nodes[index]))
                bone = _nodes[index];
            return bone != null;
        }

        // Rest world of bone i is rendererToWorld * bindposes[i].inverse. The first renderer to bind a bone
        // wins; nodes are visited parents first, so each local pose is solved against its final parent.
        internal void ResetPose(IReadOnlyList<(SkinnedMeshRenderer Renderer, int[] Bones)> renderers)
        {
            var rest = new Matrix4x4[_nodes.Count];
            var known = new bool[_nodes.Count];
            foreach (var (renderer, bones) in renderers)
            {
                if (!PreviewStage.Live(renderer) || bones == null) continue;
                var mesh = renderer.sharedMesh;
                if (!PreviewStage.Live(mesh)) continue;
                var poses = mesh.bindposes;
                int count = Math.Min(poses.Length, bones.Length);
                var meshToWorld = renderer.transform.localToWorldMatrix;
                for (int i = 0; i < count; i++)
                {
                    int bone = bones[i];
                    if (bone < 0 || bone >= known.Length || known[bone]) continue;
                    var pose = poses[i];
                    if (Mathf.Abs(pose.determinant) < 1e-12f) continue;
                    rest[bone] = meshToWorld * pose.inverse;
                    known[bone] = true;
                }
            }
            for (int i = 0; i < _nodes.Count; i++)
            {
                var node = _nodes[i];
                if (!known[i] || !PreviewStage.Live(node)) continue;
                var parent = node.parent;
                var local = parent != null ? parent.worldToLocalMatrix * rest[i] : rest[i];
                if (PreviewStage.TryDecompose(local, out var position, out var rotation, out var scale))
                {
                    node.localPosition = position;
                    node.localRotation = rotation;
                    node.localScale = scale;
                }
            }
        }

        private int Add(Transform node, string path, int parent)
        {
            int index = _nodes.Count;
            _nodes.Add(node);
            _parents.Add(parent);
            _byPath[path] = index;
            string name = node.name;
            if (_byName.ContainsKey(name)) _repeatedNames.Add(name);
            else _byName[name] = index;
            return index;
        }

        private Dictionary<string, Transform> SourceNames()
        {
            if (_sourceNames != null) return _sourceNames;
            _sourceNames = new Dictionary<string, Transform>(StringComparer.Ordinal);
            var repeated = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in _source.GetComponentsInChildren<Transform>(true))
            {
                if (node == null) continue;
                string name = node.name;
                if (!_sourceNames.TryAdd(name, node)) repeated.Add(name);
            }
            foreach (var name in repeated) _sourceNames[name] = null;
            return _sourceNames;
        }

        internal static Transform FindChild(Transform parent, string name)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child.name == name) return child;
                var found = FindChild(child, name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
