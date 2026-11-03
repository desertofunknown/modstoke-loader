// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using Endnight.Animation;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Loader;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using Object = UnityEngine.Object;

namespace Modstoke.Sons
{
    // Parts are rebuilt as fresh objects holding only a mesh renderer, never instantiated from game prefabs,
    // so no game script can wake on the stage. Each build is staged out of sight and swapped in whole.
    public sealed partial class PreviewStage
    {
        private const int MaxParts = 64;
        private const float LoadTimeout = 60f;

        private readonly List<PreviewPart> _parts = new();
        private readonly List<PreviewBuildResult> _completed = new();
        private GameObject _content;
        private GameObject _partsRoot;
        private PreviewSkeleton _skeleton;
        private BuildJob _job;
        private long _nextBuildId;

        /// <summary>Raised on the game thread after a build's swap, or when a build fails or is superseded.</summary>
        public event Action<PreviewBuildResult> BuildCompleted;

        public IReadOnlyList<PreviewPart> Parts => _disposed ? Array.Empty<PreviewPart>() : _parts.AsReadOnly();

        public bool TryGetPart(string partId, out PreviewPart part)
        {
            part = Usable() ? FindPart(partId) : null;
            return part != null;
        }

        /// <summary>Finds a bone of the stage's own skeleton copy, never a live game bone.</summary>
        public bool TryFindBone(string boneName, out Transform bone)
        {
            bone = null;
            return Usable() && _skeleton != null && _skeleton.TryFind(boneName, out bone);
        }

        /// <summary>Returns the skeleton to the bind pose of its meshes.</summary>
        public void ResetPose()
        {
            if (!Usable() || _skeleton == null) return;
            PoseRest();
            ContentChanged();
        }

        /// <summary>Samples a generic clip onto the skeleton copy. Humanoid clips need an Animator and do nothing.</summary>
        public bool TrySamplePose(AnimationClip clip, float time, out string status)
        {
            if (!Usable(out status)) return false;
            if (_skeleton == null || !Live(_skeleton.Root))
            {
                status = "The stage has no skeleton yet.";
                return false;
            }
            if (!Live(clip) || !float.IsFinite(time))
            {
                status = "A clip and a finite time are required.";
                return false;
            }
            try
            {
                clip.SampleAnimation(_skeleton.Root.gameObject, Mathf.Clamp(time, 0f, clip.length));
            }
            catch (Exception error)
            {
                status = "The clip could not be sampled: " + error.Message;
                return false;
            }
            ContentChanged();
            status = "";
            return true;
        }

        /// <summary>Starts an asynchronous build. Current parts stay visible until every new part is ready; a part
        /// whose ID and source are unchanged is kept with its edits. A newer build supersedes this one.</summary>
        public bool TryBuild(PreviewBodyRequest request, out long buildId, out string status)
        {
            buildId = 0;
            if (!Usable(out status)) return false;
            if (request == null || request.Parts.Count == 0)
            {
                status = "A build needs at least one part.";
                return false;
            }
            if (request.Parts.Count > MaxParts)
            {
                status = "A build takes at most " + MaxParts + " parts.";
                return false;
            }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var part in request.Parts)
            {
                if (part == null)
                {
                    status = "A part request is missing.";
                    return false;
                }
                if (!ids.Add(part.PartId))
                {
                    status = "Part " + part.PartId + " is requested twice.";
                    return false;
                }
            }
            if (request.SkeletonPartId != null && !ids.Contains(request.SkeletonPartId))
            {
                status = "The skeleton part " + request.SkeletonPartId + " is not in the request.";
                return false;
            }

            var job = new BuildJob(++_nextBuildId) { Request = request, Deadline = Time.unscaledTime + LoadTimeout };
            try
            {
                foreach (var part in request.Parts)
                {
                    var load = new PartLoad(part);
                    var current = FindPart(part.PartId);
                    if (current != null && current.Key == part.Key) load.Kept = current;
                    else if (part.Source == PreviewPartSource.Prefab)
                    {
                        if (Live(part.Prefab)) load.Prefab = part.Prefab;
                        else load.Failure = "its prefab was destroyed";
                    }
                    else load.Handle = Load(part.RuntimeKey);
                    job.Loads.Add(load);
                }
            }
            catch (Exception error)
            {
                DiscardJob(job);
                status = "The build could not start: " + error.Message;
                return false;
            }
            Supersede(job);
            buildId = job.Id;
            status = "building";
            return true;
        }

        internal bool CanEdit() => Usable();

        // Content was shown, hidden or posed: reframe and render.
        internal void ContentChanged()
        {
            RefreshFocus();
            _dirty = true;
        }

        internal static bool Live(Object value) => value is not null && !value.WasCollected && value;

        internal static string ReleaseLoad(AsyncOperationHandle<GameObject> handle)
        {
            if (handle == null) return null;
            try
            {
                if (handle.IsValid()) Addressables.Release<GameObject>(handle);
                return null;
            }
            catch (Exception error)
            {
                return error.Message;
            }
        }

        // Sets target's local pose to source's pose relative to space; target's parent stands in for space.
        internal static void PlaceRelative(Transform target, Transform source, Transform space)
        {
            var local = space != null ? space.worldToLocalMatrix * source.localToWorldMatrix : source.localToWorldMatrix;
            if (!TryDecompose(local, out var position, out var rotation, out var scale)) return;
            target.localPosition = position;
            target.localRotation = rotation;
            target.localScale = scale;
        }

        internal static bool TryDecompose(Matrix4x4 matrix, out Vector3 position, out Quaternion rotation, out Vector3 scale)
        {
            var x = new Vector3(matrix.m00, matrix.m10, matrix.m20);
            var y = new Vector3(matrix.m01, matrix.m11, matrix.m21);
            var z = new Vector3(matrix.m02, matrix.m12, matrix.m22);
            position = new Vector3(matrix.m03, matrix.m13, matrix.m23);
            scale = new Vector3(x.magnitude, y.magnitude, z.magnitude);
            rotation = Quaternion.identity;
            if (!(scale.x > 1e-8f && scale.y > 1e-8f && scale.z > 1e-8f) || !float.IsFinite(scale.x + scale.y + scale.z)
                || !float.IsFinite(position.x + position.y + position.z))
                return false;
            // A mirrored basis keeps its handedness in the x scale.
            if (Vector3.Dot(Vector3.Cross(x, y), z) < 0f)
            {
                scale.x = -scale.x;
                x = -x;
            }
            rotation = Quaternion.LookRotation(z / scale.z, y / scale.y);
            return true;
        }

        private void CreateContent()
        {
            _content = new GameObject("Content") { layer = _layer };
            _content.transform.SetParent(_root.transform, false);
            _partsRoot = new GameObject("Parts") { layer = _layer };
            _partsRoot.transform.SetParent(_content.transform, false);
        }

        private PreviewPart FindPart(string partId)
        {
            if (string.IsNullOrWhiteSpace(partId)) return null;
            string id = partId.Trim();
            foreach (var part in _parts)
                if (string.Equals(part.PartId, id, StringComparison.Ordinal)) return part;
            return null;
        }

        private static AsyncOperationHandle<GameObject> Load(object key)
        {
            Il2CppSystem.Object native;
            if (key is Il2CppSystem.Object value) native = value;
            else native = (Il2CppSystem.String)(key as string ?? key.ToString());
            var handle = Addressables.LoadAssetAsync<GameObject>(native);
            if (handle == null || !handle.IsValid()) throw new InvalidOperationException("Addressables refused the key " + key + ".");
            return handle;
        }

        private void Supersede(BuildJob job)
        {
            var previous = _job;
            _job = job;
            if (previous == null) return;
            string failure = DiscardJob(previous);
            if (failure != null) RuntimeEntryPoint.LogSource?.LogWarning("Preview stage build cleanup failed: " + failure);
            _completed.Add(PreviewBuildResult.Create(false, previous.Id, "superseded by build " + job.Id, null, null));
        }

        private void UpdateBuild()
        {
            var job = _job;
            if (job == null) return;
            if (!job.Staged)
            {
                bool pending = false;
                bool late = Time.unscaledTime > job.Deadline;
                foreach (var load in job.Loads)
                {
                    if (load.Kept != null || load.Prefab != null || load.Failure != null) continue;
                    var handle = load.Handle;
                    if (handle == null || !handle.IsValid())
                        load.Failure = "its load handle is invalid";
                    else if (!handle.IsDone)
                    {
                        if (late) load.Failure = "loading timed out";
                        else pending = true;
                    }
                    else if (handle.Status != AsyncOperationStatus.Succeeded || !Live(handle.Result))
                        load.Failure = "it could not be loaded";
                    else load.Prefab = handle.Result;
                }
                if (pending) return;
                try
                {
                    StageRequest(job);
                }
                catch (Exception error)
                {
                    _job = null;
                    DiscardJob(job);
                    _completed.Add(PreviewBuildResult.Create(false, job.Id, "The build failed: " + error.Message, null, null));
                    return;
                }
            }
            _job = null;
            Commit(job);
        }

        private void FlushCompleted()
        {
            if (_completed.Count == 0) return;
            var results = _completed.ToArray();
            _completed.Clear();
            foreach (var result in results)
            {
                if (_disposed) return;
                try { BuildCompleted?.Invoke(result); }
                catch (Exception error)
                {
                    RuntimeEntryPoint.LogSource?.LogWarning("Preview stage BuildCompleted handler failed: " + error.Message);
                }
            }
        }

        private void StageRequest(BuildJob job)
        {
            job.Staged = true;
            PartLoad skeletonLoad = null;
            foreach (var load in job.Loads)
            {
                var prefab = load.Prefab ?? load.Kept?.Prefab;
                if (!Live(prefab)) continue;
                bool chosen = job.Request.SkeletonPartId != null
                    ? load.Request.PartId == job.Request.SkeletonPartId && CarriesBones(prefab)
                    : CarriesBones(prefab);
                if (!chosen) continue;
                skeletonLoad = load;
                break;
            }

            Transform source, space;
            string key;
            if (skeletonLoad != null)
            {
                var prefab = skeletonLoad.Prefab ?? skeletonLoad.Kept.Prefab;
                source = space = prefab.transform;
                key = "part:" + skeletonLoad.Request.PartId + "|" + skeletonLoad.Request.Key;
            }
            else if (PlayerAppearanceCapture.TryReadRig(out _, out source, out space, out string rigStatus))
                key = "live:" + source.Pointer.ToString("X");
            else
            {
                foreach (var load in job.Loads)
                    job.Failed[load.Request.PartId] = load.Failure ?? "no part carries bones to bind to (" + rigStatus + ")";
                return;
            }

            var skeleton = _skeleton;
            if (skeleton == null || skeleton.Key != key || !Live(skeleton.Root))
            {
                job.Staging = CreateStaging();
                skeleton = job.Skeleton = new PreviewSkeleton(key, source, space, job.Staging.transform, _layer);
                // A new skeleton rebinds everything, so kept parts are rebuilt from the prefabs they already hold.
                foreach (var load in job.Loads)
                {
                    if (load.Kept == null) continue;
                    load.Rebuild = load.Kept;
                    load.Prefab = load.Kept.Prefab;
                    load.Kept = null;
                }
            }

            foreach (var load in job.Loads)
            {
                string id = load.Request.PartId;
                if (load.Kept != null)
                {
                    job.Parts.Add(load.Kept);
                    job.Kept.Add(load.Kept);
                    continue;
                }
                if (load.Failure != null || !Live(load.Prefab))
                {
                    job.Failed[id] = load.Failure ?? "its prefab was unloaded";
                    continue;
                }
                job.Staging ??= CreateStaging();
                var part = NewPart(job, id, load.Request.Key, -1);
                part.Prefab = load.Prefab;
                string failure;
                try
                {
                    TryBuildPrefabPart(part, load.Prefab, load.Request.AttachBone, skeleton, out failure);
                }
                catch (Exception error)
                {
                    failure = error.Message;
                }
                if (failure != null)
                {
                    part.Remove();
                    job.Failed[id] = failure;
                    continue;
                }
                load.Built = part;
                job.Parts.Add(part);
                job.Rebuilt.Add(id);
            }
        }

        private void TryBuildPrefabPart(PreviewPart part, GameObject prefab, string attachBone, PreviewSkeleton skeleton,
            out string failure)
        {
            failure = null;
            Transform attach = null;
            if (attachBone != null)
            {
                if (!skeleton.TryMapPath(attachBone, out int bone))
                {
                    failure = "bone " + attachBone + " not found";
                    return;
                }
                attach = skeleton.Node(bone);
            }
            var root = prefab.transform;
            foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
            {
                if (!Live(renderer) || !Shown(renderer, root)) continue;
                var skinned = renderer.TryCast<SkinnedMeshRenderer>();
                if (skinned != null)
                {
                    if (!Live(skinned.sharedMesh)) continue;
                    if (!TryPrefabBones(skinned, root, skeleton, out var bones, out int rootBone, out failure)) return;
                    CreateSkinned(part, skinned, null, bones, rootBone, root, skeleton);
                }
                else if (renderer.TryCast<MeshRenderer>() != null)
                {
                    if (attach != null) CreateStatic(part, renderer, null, attach, root, true);
                    else CreateStatic(part, renderer, null, part.Node.transform, root, false);
                }
            }
            if (part.Entries.Count == 0) failure = "it has no visible mesh";
        }

        // Head, arms and clothing prefabs carry no bones: their bone paths sit in a remap cache instead.
        private static bool TryPrefabBones(SkinnedMeshRenderer source, Transform prefab, PreviewSkeleton skeleton,
            out int[] bones, out int rootBone, out string failure)
        {
            bones = null;
            rootBone = -1;
            failure = null;
            var own = source.bones;
            if (own.Length > 0)
            {
                bones = new int[own.Length];
                for (int i = 0; i < own.Length; i++)
                {
                    var bone = own[i];
                    if (bone == null || !skeleton.TryMapPath(PathFrom(prefab, bone), out bones[i]))
                    {
                        failure = "bone " + (bone != null ? bone.name : "#" + i) + " not found";
                        return false;
                    }
                }
                if (source.rootBone != null && !skeleton.TryMapPath(PathFrom(prefab, source.rootBone), out rootBone))
                {
                    failure = "root bone " + source.rootBone.name + " not found";
                    return false;
                }
                return true;
            }

            var cache = FindRemapCache(source, prefab);
            var paths = cache != null ? cache.CachedBonePaths : null;
            if (paths == null || paths.Count == 0)
            {
                failure = "it has no bones or bone paths";
                return false;
            }
            if (paths.Count != source.sharedMesh.bindposes.Length)
            {
                failure = "its bone paths do not match its mesh";
                return false;
            }
            bones = new int[paths.Count];
            for (int i = 0; i < bones.Length; i++)
            {
                if (skeleton.TryMapPath(paths[i], out bones[i])) continue;
                failure = "bone " + paths[i] + " not found";
                return false;
            }
            string rootPath = cache.CachedRootBonePath;
            if (!string.IsNullOrEmpty(rootPath) && !skeleton.TryMapPath(rootPath, out rootBone))
            {
                failure = "root bone " + rootPath + " not found";
                return false;
            }
            return true;
        }

        private static SkinnedMeshBoneRemapCache FindRemapCache(SkinnedMeshRenderer renderer, Transform prefab)
        {
            var own = renderer.GetComponent<SkinnedMeshBoneRemapCache>();
            if (own != null) return own;
            foreach (var cache in prefab.GetComponentsInChildren<SkinnedMeshBoneRemapCache>(true))
            {
                var target = cache != null ? cache._skinnedMeshRenderer : null;
                if (target != null && target.Pointer == renderer.Pointer) return cache;
            }
            return null;
        }

        private static bool CarriesBones(GameObject prefab)
        {
            foreach (var renderer in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!Live(renderer)) continue;
                var bones = renderer.bones;
                if (bones.Length > 0 && bones[0] != null) return true;
            }
            return false;
        }

        // Prefab assets are outside any scene, so activeInHierarchy is not meaningful for them.
        private static bool Shown(Renderer renderer, Transform root)
        {
            if (!renderer.enabled) return false;
            for (var node = renderer.transform; node != null; node = node.parent)
            {
                if (!node.gameObject.activeSelf) return false;
                if (node.Pointer == root.Pointer) break;
            }
            return true;
        }

        private static string PathFrom(Transform root, Transform node)
        {
            var names = new List<string>();
            for (var current = node; current != null && current.Pointer != root.Pointer; current = current.parent)
                names.Add(current.name);
            names.Reverse();
            return string.Join("/", names);
        }

        private GameObject CreateStaging()
        {
            var staging = new GameObject("Staging") { layer = _layer };
            staging.SetActive(false);
            staging.transform.SetParent(_root.transform, false);
            return staging;
        }

        private PreviewPart NewPart(BuildJob job, string id, string key, int expressionOffset)
        {
            var part = new PreviewPart(this, id, key, expressionOffset);
            var node = new GameObject(id) { layer = _layer };
            node.transform.SetParent(job.Staging.transform, false);
            part.Node = node;
            part.Objects.Add(node);
            return part;
        }

        private void CreateSkinned(PreviewPart part, SkinnedMeshRenderer source, Renderer live, int[] bones, int rootBone,
            Transform space, PreviewSkeleton skeleton)
        {
            var node = new GameObject(source.name) { layer = _layer };
            node.transform.SetParent(part.Node.transform, false);
            PlaceRelative(node.transform, source.transform, space);
            var clone = node.AddComponent<SkinnedMeshRenderer>();
            var mesh = source.sharedMesh;
            clone.sharedMesh = mesh;
            var transforms = new Transform[bones.Length];
            for (int i = 0; i < bones.Length; i++) transforms[i] = skeleton.Node(bones[i]);
            clone.bones = new Il2CppReferenceArray<Transform>(transforms);
            clone.rootBone = rootBone >= 0 ? skeleton.Node(rootBone) : null;
            clone.localBounds = source.localBounds;
            clone.quality = source.quality;
            clone.skinnedMotionVectors = false;
            clone.sharedMaterials = source.sharedMaterials;
            // Copying every weight keeps the armour-fit and body shapes the source shows.
            int shapes = mesh.blendShapeCount;
            for (int i = 0; i < shapes; i++) clone.SetBlendShapeWeight(i, source.GetBlendShapeWeight(i));
            ConfigureRenderer(clone);
            part.Add(new PartRenderer(clone, live, bones));
        }

        // A mesh on a bone is parented to the bone copy and kept hidden until the swap.
        private void CreateStatic(PreviewPart part, Renderer source, Renderer live, Transform parent, Transform space,
            bool onBone)
        {
            var filter = source.GetComponent<MeshFilter>();
            var mesh = filter != null ? filter.sharedMesh : null;
            if (!Live(mesh)) return;
            var node = new GameObject(source.name) { layer = _layer };
            if (onBone)
            {
                node.SetActive(false);
                part.Objects.Add(node);
            }
            node.transform.SetParent(parent, false);
            if (space != null) PlaceRelative(node.transform, source.transform, space);
            else
            {
                node.transform.localPosition = source.transform.localPosition;
                node.transform.localRotation = source.transform.localRotation;
                node.transform.localScale = source.transform.localScale;
            }
            node.AddComponent<MeshFilter>().sharedMesh = mesh;
            var clone = node.AddComponent<MeshRenderer>();
            clone.sharedMaterials = source.sharedMaterials;
            ConfigureRenderer(clone);
            part.Add(new PartRenderer(clone, live, null));
        }

        private void Commit(BuildJob job)
        {
            if (job.Parts.Count == 0)
            {
                string reason = job.Failed.Count > 0
                    ? "No part could be built: " + string.Join("; ", job.Failed.Select(entry => entry.Key + ": " + entry.Value))
                    : "The build had nothing to show.";
                DiscardJob(job);
                _completed.Add(PreviewBuildResult.Create(false, job.Id, reason, null, job.Failed));
                return;
            }

            string failure = null;
            // Handles move only once the swap is certain, before the parts that held them are removed.
            foreach (var load in job.Loads)
            {
                if (load.Built == null) continue;
                load.Built.Handle = load.Handle ?? load.Rebuild?.TakeHandle();
                load.Handle = null;
            }
            var keep = new HashSet<PreviewPart>(job.Parts);
            foreach (var old in _parts)
            {
                if (keep.Contains(old)) continue;
                Untrack(old);
                failure ??= old.Remove();
            }
            if (job.Skeleton != null)
            {
                var previous = _skeleton;
                job.Skeleton.Root.SetParent(_content.transform, false);
                _skeleton = job.Skeleton;
                if (previous != null && Live(previous.Root))
                {
                    previous.Root.gameObject.SetActive(false);
                    Object.Destroy(previous.Root.gameObject);
                }
            }
            foreach (var part in job.Parts)
            {
                if (job.Kept.Contains(part)) continue;
                part.Node.transform.SetParent(_partsRoot.transform, false);
                foreach (var node in part.Objects) node.SetActive(true);
                foreach (var entry in part.Entries) Track(entry.Clone);
            }
            _parts.Clear();
            _parts.AddRange(job.Parts);
            if (Live(job.Staging)) Object.Destroy(job.Staging);
            job.Staging = null;
            foreach (var load in job.Loads) failure ??= ReleaseLoad(load.Handle);
            _capture = job.Capture;
            _captureOptions = job.CaptureOptions;
            if (!(_captureOptions?.LivePose ?? false)) PoseRest();
            ContentChanged();
            Publish();
            if (failure != null) RuntimeEntryPoint.LogSource?.LogWarning("Preview stage swap cleanup failed: " + failure);
            string status = job.Failed.Count == 0 ? "built " + job.Parts.Count + " parts"
                : "built " + job.Parts.Count + " parts; left out " + string.Join("; ", job.Failed.Select(entry => entry.Key + ": " + entry.Value));
            _completed.Add(PreviewBuildResult.Create(job.Failed.Count == 0, job.Id, status, job.Rebuilt, job.Failed));
        }

        // Undoes an uncommitted job. Its staged parts hold no handles yet; the loads still do.
        private string DiscardJob(BuildJob job)
        {
            string failure = null;
            foreach (var part in job.Parts)
                if (!job.Kept.Contains(part)) failure ??= part.Remove();
            foreach (var load in job.Loads)
            {
                failure ??= ReleaseLoad(load.Handle);
                load.Handle = null;
            }
            if (Live(job.Staging))
            {
                try { Object.Destroy(job.Staging); }
                catch (Exception error) { failure ??= error.Message; }
            }
            job.Staging = null;
            return failure;
        }

        private void PoseRest()
        {
            if (_skeleton == null) return;
            var renderers = new List<(SkinnedMeshRenderer Renderer, int[] Bones)>();
            foreach (var part in _parts)
                foreach (var entry in part.Entries)
                    if (entry.Skinned != null && entry.Bones != null && entry.Bones.Length > 0)
                        renderers.Add((entry.Skinned, entry.Bones));
            // The renderer binding the most bones is the master; others only fill bones it lacks.
            _skeleton.ResetPose(renderers.OrderByDescending(item => item.Bones.Length).ToList());
        }

        private void Untrack(PreviewPart part)
        {
            foreach (var entry in part.Entries) _renderers.Remove(entry.Clone);
        }

        private string ReleaseContent()
        {
            string failure = null;
            if (_job != null)
            {
                failure = DiscardJob(_job);
                _job = null;
            }
            foreach (var part in _parts) failure ??= part.Remove();
            _parts.Clear();
            _completed.Clear();
            _skeleton = null;
            _capture = null;
            _content = null;
            _partsRoot = null;
            return failure;
        }

        private sealed class BuildJob
        {
            internal BuildJob(long id) => Id = id;

            internal long Id { get; }
            internal PreviewBodyRequest Request { get; init; }
            internal float Deadline { get; init; }
            internal PlayerAppearanceCapture Capture { get; init; }
            internal PreviewCaptureOptions CaptureOptions { get; init; }
            internal bool Staged { get; set; }
            internal GameObject Staging { get; set; }
            // Set when this job brings its own skeleton; otherwise the stage's is reused.
            internal PreviewSkeleton Skeleton { get; set; }
            internal List<PartLoad> Loads { get; } = new();
            internal List<PreviewPart> Parts { get; } = new();
            internal HashSet<PreviewPart> Kept { get; } = new();
            internal List<string> Rebuilt { get; } = new();
            internal Dictionary<string, string> Failed { get; } = new(StringComparer.Ordinal);
        }

        private sealed class PartLoad
        {
            internal PartLoad(PreviewPartRequest request) => Request = request;

            internal PreviewPartRequest Request { get; }
            internal AsyncOperationHandle<GameObject> Handle { get; set; }
            internal PreviewPart Kept { get; set; }
            // A kept part whose skeleton changed; its prefab and handle move to the new part.
            internal PreviewPart Rebuild { get; set; }
            internal PreviewPart Built { get; set; }
            internal GameObject Prefab { get; set; }
            internal string Failure { get; set; }
        }
    }
}
