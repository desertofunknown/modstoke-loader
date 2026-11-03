// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using UnityEngine;

namespace Modstoke.Sons
{
    // All distances are world metres. The player rig scales Hips by 0.4, so bone-local units are never used here.
    public sealed partial class PreviewStage
    {
        private const float DefaultYaw = 15f;
        private const float DefaultPitch = 5f;
        private const float DefaultRadius = 1f;
        private const float ZoomRate = .15f;
        private const float OrbitTime = .08f;
        private const float ZoomTime = .15f;
        private const float FocusTime = .25f;
        private const float SettleGap = 1e-3f;
        private const float FaceLift = .03f;
        private const float FaceRadius = .17f;
        private const float HandRadius = .12f;
        // Half the size each bone adds to the body bounds, so skin and clothing around the bones stay in frame.
        private const float BonePad = .12f;
        private const float PivotLiftLimit = .3f;
        private const string HeadBone = "Head";
        private const string LeftHandBone = "LeftHand";
        private const string RightHandBone = "RightHand";
        private static readonly Vector3 DefaultPivot = new(0f, .9f, 0f);

        private PreviewFocus _focus;
        private string _focusBone;
        private float _focusBoneRadius;
        private float _yaw = DefaultYaw;
        private float _yawTarget = DefaultYaw;
        private float _pitch = DefaultPitch;
        private float _pitchTarget = DefaultPitch;
        private float _distance;
        private float _distanceTarget;
        private Vector3 _pivot = DefaultPivot;
        private Vector3 _pivotTarget = DefaultPivot;
        private float _radius = DefaultRadius;
        private float _radiusTarget = DefaultRadius;
        private float _bodyRadius = DefaultRadius;
        // Share of the framed radius the pivot is raised by, from the host's middle-mouse drag.
        private float _pivotLift;
        private bool _settling;
        private bool _snapped;

        public PreviewCameraState CameraState
        {
            get
            {
                DistanceRange(_radiusTarget, out float minimum, out float maximum);
                return new PreviewCameraState(_focus, Mathf.Repeat(_yaw + 180f, 360f) - 180f, _pitch,
                    _distance, minimum, maximum, _settling);
            }
        }

        public void Orbit(float yawDeltaDegrees, float pitchDeltaDegrees)
        {
            if (!Usable() || !float.IsFinite(yawDeltaDegrees) || !float.IsFinite(pitchDeltaDegrees)) return;
            _yawTarget += yawDeltaDegrees;
            // Yaw wraps freely; rebasing both values keeps them small without a visible jump.
            if (Mathf.Abs(_yawTarget) > 720f)
            {
                float turns = Mathf.Round(_yawTarget / 360f) * 360f;
                _yawTarget -= turns;
                _yaw -= turns;
            }
            PitchLimits(out float lowest, out float highest);
            _pitchTarget = Mathf.Clamp(_pitchTarget + pitchDeltaDegrees, lowest, highest);
            _dirty = true;
        }

        /// <summary>Positive steps move closer. Each step scales the distance logarithmically.</summary>
        public void Zoom(float steps)
        {
            if (!Usable() || !float.IsFinite(steps) || steps == 0f) return;
            DistanceRange(_radiusTarget, out float minimum, out float maximum);
            _distanceTarget = Mathf.Clamp(_distanceTarget * Mathf.Exp(-ZoomRate * steps), minimum, maximum);
            _dirty = true;
        }

        /// <summary>Frames a region of the content by the skeleton's Head and hand bones. Without them, Face uses
        /// the top of the content bounds and the hand focuses frame the whole body.</summary>
        public void SetFocus(PreviewFocus focus, bool instant = false)
        {
            if (!Usable()) return;
            _focus = focus;
            _focusBone = null;
            Refocus(instant);
        }

        /// <summary>Frames a sphere of <paramref name="radius"/> metres around a skeleton bone. The camera state
        /// keeps reporting the last <see cref="PreviewFocus"/>; <see cref="SetFocus"/> ends the bone focus.</summary>
        public void SetFocusBone(string boneName, float radius, bool instant = false)
        {
            if (!Usable() || string.IsNullOrWhiteSpace(boneName) || !float.IsFinite(radius) || radius <= 0f) return;
            _focusBone = boneName.Trim();
            _focusBoneRadius = Mathf.Clamp(radius, .02f, 3f);
            Refocus(instant);
        }

        public void ResetView(bool instant = false)
        {
            if (!Usable()) return;
            _yawTarget = _yaw + Mathf.DeltaAngle(_yaw, DefaultYaw);
            PitchLimits(out float lowest, out float highest);
            _pitchTarget = Mathf.Clamp(DefaultPitch, lowest, highest);
            _pivotLift = 0f;
            Frame(_focus, out _pivotTarget, out _radiusTarget);
            _distanceTarget = ClampedFramingDistance(_radiusTarget);
            if (instant) Snap();
            _dirty = true;
        }

        // Raises or lowers the pivot by a share of the framed radius, up to 0.3 of it either way.
        internal void LiftPivot(float radiusShare)
        {
            if (!Usable() || !float.IsFinite(radiusShare) || radiusShare == 0f) return;
            _pivotLift = Mathf.Clamp(_pivotLift + radiusShare, -PivotLiftLimit, PivotLiftLimit);
            Frame(_focus, out _pivotTarget, out _radiusTarget);
            _pivotTarget.y += _pivotLift * _radiusTarget;
            _dirty = true;
        }

        private void Refocus(bool instant)
        {
            _pivotLift = 0f;
            Frame(_focus, out _pivotTarget, out _radiusTarget);
            PitchLimits(out float lowest, out float highest);
            _pitchTarget = Mathf.Clamp(_pitchTarget, lowest, highest);
            _distanceTarget = ClampedFramingDistance(_radiusTarget);
            if (instant) Snap();
            _dirty = true;
        }

        // Content or aspect changed: keep the angles and the zoom ratio, reframe the pivot.
        private void RefreshFocus()
        {
            float previous = ClampedFramingDistance(_radiusTarget);
            Frame(_focus, out _pivotTarget, out _radiusTarget);
            _pivotTarget.y += _pivotLift * _radiusTarget;
            float framed = ClampedFramingDistance(_radiusTarget);
            DistanceRange(_radiusTarget, out float minimum, out float maximum);
            float ratio = previous > 0f && _distanceTarget > 0f ? _distanceTarget / previous : 1f;
            _distanceTarget = Mathf.Clamp(framed * ratio, minimum, maximum);
            _dirty = true;
        }

        private void Frame(PreviewFocus focus, out Vector3 pivot, out float radius)
        {
            bool any = TryGetContentBounds(out var bounds);
            _bodyRadius = any ? Mathf.Max(bounds.extents.magnitude, .05f) : DefaultRadius;
            if (_focusBone != null && TryBonePoint(_focusBone, out pivot))
            {
                radius = _focusBoneRadius;
                return;
            }
            switch (focus)
            {
                case PreviewFocus.Face when TryBonePoint(HeadBone, out pivot):
                    pivot.y += FaceLift;
                    radius = FaceRadius;
                    return;
                case PreviewFocus.Hands when TryBonePoint(LeftHandBone, out var left) && TryBonePoint(RightHandBone, out var right):
                    pivot = (left + right) * .5f;
                    radius = Vector3.Distance(left, right) * .5f + HandRadius;
                    return;
                case PreviewFocus.LeftHand when TryBonePoint(LeftHandBone, out pivot):
                case PreviewFocus.RightHand when TryBonePoint(RightHandBone, out pivot):
                    radius = HandRadius;
                    return;
            }
            if (!any)
            {
                pivot = DefaultPivot;
                radius = DefaultRadius;
                return;
            }
            if (focus == PreviewFocus.Face)
            {
                var size = bounds.size;
                var top = new Vector3(bounds.extents.x, size.y * .125f, bounds.extents.z);
                pivot = new Vector3(bounds.center.x, bounds.max.y - top.y, bounds.center.z);
                radius = Mathf.Max(top.magnitude, .05f);
                return;
            }
            pivot = bounds.center;
            radius = _bodyRadius;
        }

        private bool TryBonePoint(string name, out Vector3 point)
        {
            point = default;
            if (_skeleton == null || !_skeleton.TryFind(name, out var bone)) return false;
            point = bone.position - (Live(_root) ? _root.transform.position : StageOrigin);
            return true;
        }

        // Stage-local bounds of the shown content. Skinned copies are measured by their bones, which stay in world
        // metres after the rig's Hips scale, rather than by bounds that update only when skinned.
        private bool TryGetContentBounds(out Bounds bounds)
        {
            var result = default(Bounds);
            bool any = false;
            var origin = Live(_root) ? _root.transform.position : StageOrigin;
            void Add(Bounds local)
            {
                if (any) result.Encapsulate(local);
                else result = local;
                any = true;
            }
            for (int i = 0; i < _renderers.Count; i++)
            {
                var renderer = _renderers[i];
                if (!Live(renderer) || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                if (renderer.TryCast<SkinnedMeshRenderer>() != null) continue;
                var world = renderer.bounds;
                Add(new Bounds(world.center - origin, world.size));
            }
            var used = _skeleton != null ? new bool[_skeleton.Count] : null;
            foreach (var part in _parts)
            {
                if (!part.Visible) continue;
                foreach (var entry in part.Entries)
                {
                    if (entry.Skinned == null || !Live(entry.Clone) || !entry.Clone.enabled) continue;
                    if (used == null || entry.Bones == null || entry.Bones.Length == 0)
                    {
                        var world = entry.Clone.bounds;
                        Add(new Bounds(world.center - origin, world.size));
                        continue;
                    }
                    foreach (int bone in entry.Bones)
                        if (bone >= 0 && bone < used.Length) used[bone] = true;
                }
            }
            for (int i = 0; used != null && i < used.Length; i++)
            {
                var node = used[i] ? _skeleton.Node(i) : null;
                if (Live(node)) Add(new Bounds(node.position - origin, Vector3.one * (2f * BonePad)));
            }
            bounds = result;
            return any;
        }

        private void PitchLimits(out float lowest, out float highest)
        {
            switch (_focusBone != null ? PreviewFocus.Hands : _focus)
            {
                case PreviewFocus.Body:
                    lowest = -15f;
                    highest = 40f;
                    break;
                case PreviewFocus.Face:
                    lowest = -25f;
                    highest = 25f;
                    break;
                default:
                    lowest = -40f;
                    highest = 60f;
                    break;
            }
        }

        private float FramingDistance(float radius)
        {
            float aspect = _height > 0 ? (float)_width / _height : 1f;
            float vertical = FieldOfView * Mathf.Deg2Rad;
            float horizontal = 2f * Mathf.Atan(Mathf.Tan(vertical * .5f) * aspect);
            return 1.1f * radius / Mathf.Sin(Mathf.Min(vertical, horizontal) * .5f);
        }

        private float ClampedFramingDistance(float radius)
        {
            DistanceRange(radius, out float minimum, out float maximum);
            return Mathf.Clamp(FramingDistance(radius), minimum, maximum);
        }

        private void DistanceRange(float radius, out float minimum, out float maximum)
        {
            float framed = FramingDistance(radius);
            minimum = Mathf.Max(.25f, .6f * framed);
            maximum = Mathf.Min(6f, 2.5f * framed);
            if (minimum > maximum) minimum = maximum;
        }

        private void Snap()
        {
            _yaw = _yawTarget;
            _pitch = _pitchTarget;
            _distance = _distanceTarget;
            _pivot = _pivotTarget;
            _radius = _radiusTarget;
            _settling = false;
            _snapped = true;
        }

        // Unscaled time: the pause menu may stop the game clock while the stage is in use.
        private bool StepRig(float deltaTime)
        {
            if (_yaw == _yawTarget && _pitch == _pitchTarget && _distance == _distanceTarget
                && _pivot == _pivotTarget && _radius == _radiusTarget)
            {
                _settling = false;
                bool snapped = _snapped;
                _snapped = false;
                return snapped;
            }
            float dt = Mathf.Clamp(deltaTime, 0f, .1f);
            float orbit = 1f - Mathf.Exp(-dt / OrbitTime);
            float zoom = 1f - Mathf.Exp(-dt / ZoomTime);
            float focus = 1f - Mathf.Exp(-dt / FocusTime);
            _yaw += (_yawTarget - _yaw) * orbit;
            _pitch += (_pitchTarget - _pitch) * orbit;
            _distance += (_distanceTarget - _distance) * zoom;
            _pivot += (_pivotTarget - _pivot) * focus;
            _radius += (_radiusTarget - _radius) * focus;
            _settling = Mathf.Abs(_yawTarget - _yaw) > SettleGap || Mathf.Abs(_pitchTarget - _pitch) > SettleGap
                || Mathf.Abs(_distanceTarget - _distance) > SettleGap || (_pivotTarget - _pivot).magnitude > SettleGap
                || Mathf.Abs(_radiusTarget - _radius) > SettleGap;
            if (!_settling) Snap();
            _snapped = false;
            return true;
        }

        private void ApplyRig()
        {
            var pivot = _root.transform.position + _pivot;
            // Positive pitch looks down from above; yaw 0 faces the content's +Z front.
            var position = pivot + Quaternion.Euler(-_pitch, _yaw, 0f) * new Vector3(0f, 0f, _distance);
            _cameraObject.transform.SetPositionAndRotation(position, Quaternion.LookRotation(pivot - position, Vector3.up));
            float near = Mathf.Clamp(.05f * _distance, .01f, .1f);
            _camera.nearClipPlane = near;
            _camera.farClipPlane = Mathf.Max(Mathf.Min(_distance + 3f * _bodyRadius + 1f, 12f), near + .5f);
            FollowLights(pivot);
        }
    }
}
