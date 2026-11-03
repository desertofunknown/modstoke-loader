// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;
using Object = UnityEngine.Object;

namespace Modstoke.Sons
{
    /// <summary>One part on a preview stage. Its renderers are stage-owned copies, never live game renderers.
    /// Main thread only; a part replaced by a later build stops responding.</summary>
    public sealed class PreviewPart
    {
        private readonly PreviewStage _stage;
        private readonly List<Renderer> _renderers = new();
        private readonly ReadOnlyCollection<Renderer> _view;
        private bool _visible = true;

        internal PreviewPart(PreviewStage stage, string partId, string key, int expressionOffset)
        {
            _stage = stage;
            PartId = partId;
            Key = key;
            ExpressionBlendShapeOffset = expressionOffset;
            _view = _renderers.AsReadOnly();
        }

        public string PartId { get; }
        public IReadOnlyList<Renderer> Renderers => Removed ? Array.Empty<Renderer>() : _view;
        /// <summary>First expression blend shape on the race head; -1 on every other part.</summary>
        public int ExpressionBlendShapeOffset { get; }

        public bool Visible
        {
            get => _visible;
            set
            {
                if (!Editable() || _visible == value) return;
                _visible = value;
                foreach (var entry in Entries) entry.Apply(value);
                _stage.ContentChanged();
            }
        }

        internal string Key { get; }
        internal GameObject Node { get; set; }
        // The prefab asset a requested part was built from; null for a captured part.
        internal GameObject Prefab { get; set; }
        internal List<PartRenderer> Entries { get; } = new();
        // The part node plus any static meshes parented to skeleton bones.
        internal List<GameObject> Objects { get; } = new();
        internal AsyncOperationHandle<GameObject> Handle { get; set; }
        internal bool Removed { get; private set; }

        public IReadOnlyList<string> GetBlendShapeNames(int rendererIndex)
        {
            if (!Editable() || !TryEntry(rendererIndex, out var entry)) return Array.Empty<string>();
            return entry.ShapeNames();
        }

        /// <summary>Weight is 0-100 as in Unity. A captured part keeps this weight instead of the player's.</summary>
        public bool TrySetBlendShape(int rendererIndex, string name, float weight)
        {
            if (!Editable() || !float.IsFinite(weight) || string.IsNullOrEmpty(name)
                || !TryEntry(rendererIndex, out var entry) || entry.Skinned == null) return false;
            var mesh = entry.Skinned.sharedMesh;
            int index = PreviewStage.Live(mesh) ? mesh.GetBlendShapeIndex(name) : -1;
            if (index < 0 || index >= entry.Weights.Length) return false;
            entry.Skinned.SetBlendShapeWeight(index, weight);
            entry.Weights[index] = weight;
            entry.Overrides ??= new bool[entry.Weights.Length];
            entry.Overrides[index] = true;
            _stage.MarkDirty();
            return true;
        }

        /// <summary>Lazily copies the shared material for this slot. The stage owns and destroys it.
        /// Never edit sharedMaterials.</summary>
        public Material GetOwnedMaterial(int rendererIndex, int slot)
        {
            if (!Editable() || !TryEntry(rendererIndex, out var entry)) return null;
            if (slot < 0 || slot >= entry.Owned.Length) return null;
            var owned = entry.Owned[slot];
            if (PreviewStage.Live(owned)) return owned;
            var materials = entry.Clone.sharedMaterials;
            if (slot >= materials.Length || !PreviewStage.Live(materials[slot])) return null;
            owned = new Material(materials[slot]) { name = "Preview " + materials[slot].name };
            entry.Owned[slot] = owned;
            materials[slot] = owned;
            entry.Clone.sharedMaterials = materials;
            _stage.MarkDirty();
            return owned;
        }

        internal void Add(PartRenderer entry)
        {
            Entries.Add(entry);
            _renderers.Add(entry.Clone);
        }

        // Called once the part leaves the stage. Destroy is deferred by Unity, so the objects are also hidden now.
        internal string Remove()
        {
            if (Removed) return null;
            Removed = true;
            string failure = null;
            foreach (var node in Objects)
            {
                if (!PreviewStage.Live(node)) continue;
                try
                {
                    node.SetActive(false);
                    Object.Destroy(node);
                }
                catch (Exception error) { failure ??= error.Message; }
            }
            foreach (var entry in Entries)
            {
                foreach (var material in entry.Owned)
                {
                    if (!PreviewStage.Live(material)) continue;
                    try { Object.Destroy(material); }
                    catch (Exception error) { failure ??= error.Message; }
                }
            }
            failure ??= ReleaseHandle();
            Objects.Clear();
            return failure;
        }

        internal string ReleaseHandle() => PreviewStage.ReleaseLoad(TakeHandle());

        internal AsyncOperationHandle<GameObject> TakeHandle()
        {
            var handle = Handle;
            Handle = null;
            return handle;
        }

        private bool TryEntry(int index, out PartRenderer entry)
        {
            entry = index >= 0 && index < Entries.Count ? Entries[index] : null;
            return entry != null && PreviewStage.Live(entry.Clone);
        }

        private bool Editable() => !Removed && _stage.CanEdit();
    }

    // A stage renderer and, for a captured part, the live renderer it mirrors.
    internal sealed class PartRenderer
    {
        private string[] _shapeNames;

        internal PartRenderer(Renderer clone, Renderer source, int[] bones)
        {
            Clone = clone;
            Skinned = clone.TryCast<SkinnedMeshRenderer>();
            Source = source;
            SourceSkinned = source != null ? source.TryCast<SkinnedMeshRenderer>() : null;
            Bones = bones;
            var mesh = Skinned != null ? Skinned.sharedMesh : null;
            Weights = new float[PreviewStage.Live(mesh) ? mesh.blendShapeCount : 0];
            for (int i = 0; i < Weights.Length; i++) Weights[i] = Skinned.GetBlendShapeWeight(i);
            var materials = clone.sharedMaterials;
            Materials = new IntPtr[materials.Length];
            for (int i = 0; i < materials.Length; i++) Materials[i] = materials[i] != null ? materials[i].Pointer : IntPtr.Zero;
            Owned = new Material[materials.Length];
        }

        internal Renderer Clone { get; }
        internal SkinnedMeshRenderer Skinned { get; }
        internal Renderer Source { get; }
        internal SkinnedMeshRenderer SourceSkinned { get; }
        // Skeleton node per bone slot, or null for a static mesh.
        internal int[] Bones { get; }
        internal float[] Weights { get; }
        internal bool[] Overrides { get; set; }
        internal IntPtr[] Materials { get; private set; }
        internal Material[] Owned { get; private set; }
        internal bool SourceEnabled { get; private set; } = true;

        internal void Apply(bool partVisible) => Clone.enabled = partVisible && SourceEnabled;

        internal string[] ShapeNames()
        {
            if (_shapeNames != null) return _shapeNames;
            var mesh = Skinned != null ? Skinned.sharedMesh : null;
            _shapeNames = new string[Weights.Length];
            for (int i = 0; i < _shapeNames.Length; i++) _shapeNames[i] = PreviewStage.Live(mesh) ? mesh.GetBlendShapeName(i) : "";
            return _shapeNames;
        }

        // Copies enabled state, blend weights and materials from the live source. Edited weights and owned
        // material slots stay as the caller set them.
        internal bool Sync(bool partVisible)
        {
            bool changed = false;
            bool enabled = Source.enabled && Source.gameObject.activeInHierarchy;
            if (enabled != SourceEnabled)
            {
                SourceEnabled = enabled;
                Apply(partVisible);
                changed = true;
            }
            if (SourceSkinned != null && Skinned != null)
            {
                for (int i = 0; i < Weights.Length; i++)
                {
                    if (Overrides != null && Overrides[i]) continue;
                    float weight = SourceSkinned.GetBlendShapeWeight(i);
                    if (weight == Weights[i]) continue;
                    Weights[i] = weight;
                    Skinned.SetBlendShapeWeight(i, weight);
                    changed = true;
                }
            }
            var shared = Source.sharedMaterials;
            bool same = shared.Length == Materials.Length;
            for (int i = 0; same && i < shared.Length; i++)
                same = (shared[i] != null ? shared[i].Pointer : IntPtr.Zero) == Materials[i];
            if (same) return changed;
            var owned = new Material[shared.Length];
            var pointers = new IntPtr[shared.Length];
            var slots = new Material[shared.Length];
            for (int i = 0; i < shared.Length; i++)
            {
                pointers[i] = shared[i] != null ? shared[i].Pointer : IntPtr.Zero;
                owned[i] = i < Owned.Length ? Owned[i] : null;
                slots[i] = PreviewStage.Live(owned[i]) ? owned[i] : shared[i];
            }
            Clone.sharedMaterials = slots;
            for (int i = shared.Length; i < Owned.Length; i++)
                if (PreviewStage.Live(Owned[i])) Object.Destroy(Owned[i]);
            Materials = pointers;
            Owned = owned;
            return true;
        }
    }
}
