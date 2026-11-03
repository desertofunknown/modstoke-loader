// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class PreviewStage
    {
        private const float RecaptureInterval = .5f;

        private PlayerAppearanceCapture _capture;
        private PreviewCaptureOptions _captureOptions;
        private float _recaptureAt;

        /// <summary>Copies the local player's visible renderers (custom body, race head and arms, clothing and
        /// armour) with their blend-shape weights onto a copy of the live rig. Hidden renderers are left out and
        /// every copy casts shadows, whatever the first-person state. In-world only; the swap follows next frame.</summary>
        public bool TryCaptureLocalPlayer(PreviewCaptureOptions options, out long buildId, out string status)
        {
            buildId = 0;
            if (!Usable(out status)) return false;
            if (!PlayerAppearanceCapture.TryRead(out var capture, out status)) return false;
            var job = new BuildJob(++_nextBuildId)
            {
                Capture = capture,
                CaptureOptions = new PreviewCaptureOptions { LivePose = options?.LivePose ?? false },
                Staged = true
            };
            try
            {
                StageCapture(job);
            }
            catch (Exception error)
            {
                DiscardJob(job);
                status = "The local player could not be captured: " + error.Message;
                return false;
            }
            if (job.Parts.Count == 0)
            {
                DiscardJob(job);
                status = "No renderer of the local player could be copied: " + string.Join("; ", job.Failed.Values);
                return false;
            }
            Supersede(job);
            buildId = job.Id;
            status = "building";
            return true;
        }

        /// <summary>Recopies enabled flags, blend-shape weights and shared materials from the captured player.
        /// Cheap enough for every frame, and edits made through <see cref="PreviewPart"/> survive it. When a source
        /// appears, disappears or changes mesh it starts a new capture and returns false; that capture builds new
        /// parts, so apply edits again to the <see cref="PreviewBuildResult.RebuiltPartIds"/> of
        /// <see cref="BuildCompleted"/>.</summary>
        public bool Sync() => Sync(out _);

        public bool Sync(out string status)
        {
            if (!Usable(out status)) return false;
            var capture = _capture;
            if (capture == null)
            {
                status = "Nothing on the stage was captured from the local player.";
                return false;
            }
            string reason = capture.Changed() ? "the player's look changed" : null;
            bool changed = false;
            foreach (var part in _parts)
            {
                foreach (var entry in part.Entries)
                {
                    if (entry.Source == null || !Live(entry.Clone)) continue;
                    if (!Live(entry.Source))
                    {
                        reason ??= "a source renderer was destroyed";
                        continue;
                    }
                    if (entry.SourceSkinned != null && entry.Skinned != null
                        && !SameMesh(entry.SourceSkinned.sharedMesh, entry.Skinned.sharedMesh))
                    {
                        reason ??= "a mesh changed";
                        continue;
                    }
                    changed |= entry.Sync(part.Visible);
                }
            }
            if (reason == null)
            {
                foreach (var source in capture.Sources)
                {
                    foreach (var renderer in source.Skipped)
                    {
                        if (!Live(renderer) || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                        reason = "a hidden renderer was shown";
                        break;
                    }
                    if (reason != null) break;
                }
            }
            if (changed) _dirty = true;
            if (reason == null)
            {
                status = "";
                return true;
            }
            status = "rebuilding: " + reason;
            if (_job == null && Time.unscaledTime >= _recaptureAt)
            {
                _recaptureAt = Time.unscaledTime + RecaptureInterval;
                if (!TryCaptureLocalPlayer(_captureOptions, out _, out string captureStatus))
                    status = "rebuild waiting: " + captureStatus;
            }
            return false;
        }

        private void StageCapture(BuildJob job)
        {
            var capture = job.Capture;
            job.Staging = CreateStaging();
            var skeleton = job.Skeleton = new PreviewSkeleton("capture:" + job.Id, capture.SkeletonRoot, capture.Space,
                job.Staging.transform, _layer);
            foreach (var source in capture.Sources)
            {
                if (source.Enabled.Count == 0) continue;
                var part = NewPart(job, source.PartId, null, source.ExpressionOffset);
                string failure = null;
                foreach (var renderer in source.Enabled)
                {
                    try
                    {
                        if (Live(renderer)) failure = CloneLive(part, renderer, capture.Space, skeleton);
                        else failure = "a renderer was destroyed during the capture";
                    }
                    catch (Exception error)
                    {
                        failure = error.Message;
                    }
                    if (failure != null) break;
                }
                if (failure == null && part.Entries.Count == 0) failure = "it has no mesh to show";
                if (failure != null)
                {
                    part.Remove();
                    job.Failed[source.PartId] = failure;
                    continue;
                }
                job.Parts.Add(part);
                job.Rebuilt.Add(source.PartId);
            }
        }

        private string CloneLive(PreviewPart part, Renderer renderer, Transform space, PreviewSkeleton skeleton)
        {
            var skinned = renderer.TryCast<SkinnedMeshRenderer>();
            if (skinned == null)
            {
                var parent = renderer.transform.parent;
                if (parent != null && skeleton.TryMap(parent, out int bone))
                    CreateStatic(part, renderer, renderer, skeleton.Node(bone), null, true);
                else CreateStatic(part, renderer, renderer, part.Node.transform, space, false);
                return null;
            }
            if (!Live(skinned.sharedMesh)) return null;
            var live = skinned.bones;
            var bones = new int[live.Length];
            for (int i = 0; i < live.Length; i++)
                if (!skeleton.TryMap(live[i], out bones[i]))
                    return "bone " + (live[i] != null ? live[i].name : "#" + i) + " not found";
            int rootBone = -1;
            var root = skinned.rootBone;
            if (root != null && !skeleton.TryMap(root, out rootBone)) return "root bone " + root.name + " not found";
            CreateSkinned(part, skinned, renderer, bones, rootBone, space, skeleton);
            return null;
        }

        private static bool SameMesh(Mesh first, Mesh second) =>
            first is not null && second is not null && !first.WasCollected && !second.WasCollected
            && first.Pointer == second.Pointer;
    }
}
