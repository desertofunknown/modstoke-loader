// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Crest;
using UnityEngine;

namespace Modstoke.Loader
{
    /// <summary>
    /// The photo camera's pose: where it is, which way it looks, its roll and its zoom, moved by the player and
    /// kept within reach of the player. The game's systems that follow the camera before it renders (the ocean's
    /// detail, world streaming, distant trees) still see the player's own view, so the camera stays within a few
    /// metres of the player and on the same side of the water surface.
    /// </summary>
    internal sealed class PhotoCamera
    {
        internal const float Reach = 12f;
        private const float BaseSpeed = 2.5f, VelocitySeconds = 0.15f, MouseSeconds = 0.06f, PadSeconds = 0.10f, ZoomRate = 10f;
        private const float RollSpeed = 45f, Radius = 0.12f, Skin = 0.02f, SurfaceMargin = 0.4f, FocusReach = 300f;
        // Everything solid: not triggers, interface, shadow-only casters, the HUD, held items, the player, the
        // inventory or the camera's own layer.
        internal const int SolidMask = ~((1 << 3) | (1 << 5) | (1 << 6) | (1 << 8) | (1 << 15) | (1 << 18) | (1 << 23) | (1 << 27));
        private const int HeldLayer = 1 << 15, PlayerLayer = 1 << 18;

        private readonly HashSet<string> _hitLayers = new();
        private Vector3 _velocity;
        private float _yaw, _pitch;

        internal PhotoCamera(Transform view, float fov)
        {
            Anchor = view.position;
            Position = view.position;
            Aim(view.rotation);
            Focal = PhotoLens.Focal(fov);
        }

        internal Vector3 Position { get; private set; }
        internal float Yaw { get; private set; }
        internal float Pitch { get; private set; }
        internal float Roll { get; private set; }
        /// <summary>The focal length as rendered, easing towards the panel's.</summary>
        internal float Focal { get; private set; }
        /// <summary>The player's own eye, which the camera stays within reach of.</summary>
        internal Vector3 Anchor { get; private set; }
        internal Quaternion Rotation => Quaternion.Euler(Pitch, Yaw, 0) * Quaternion.Euler(0, 0, Roll);
        internal float Fov => PhotoLens.Fov(Focal);

        /// <summary>Layer names of what the camera first bumped into, for tuning what counts as solid.</summary>
        internal string HitLayers => string.Join(", ", _hitLayers);

        /// <summary>Moves the camera for this frame. <paramref name="eye"/> is the player's view as the game left it.</summary>
        internal void Tick(PhotoInputFrame input, PhotoSettings settings, Transform eye, float dt)
        {
            if (eye != null && eye) Anchor = eye.position;
            dt = Mathf.Min(dt, 0.1f);

            // The panel's roll is the target; the keys turn it.
            if (input.Roll != 0) settings.Roll = Mathf.Clamp(settings.Roll + input.Roll * RollSpeed * dt, -45f, 45f);
            Roll = settings.Roll;
            if (input.Zoom != 0) settings.Focal = Mathf.Clamp(settings.Focal * Mathf.Pow(1.12f, input.Zoom), PhotoLens.MinFocal, PhotoLens.MaxFocal);
            if (!float.IsNaN(settings.Focal)) Focal = Mathf.Lerp(Focal, settings.Focal, 1f - Mathf.Exp(-ZoomRate * dt));

            _yaw += input.Look.x;
            _pitch = Mathf.Clamp(_pitch - input.Look.y, -89f, 89f);
            var follow = 1f - Mathf.Exp(-dt / (input.PadLook ? PadSeconds : MouseSeconds));
            Yaw = Mathf.LerpAngle(Yaw, _yaw, follow);
            Pitch = Mathf.Lerp(Pitch, _pitch, follow);

            var look = Quaternion.Euler(Pitch, Yaw, 0);
            var want = (look * new Vector3(input.Move.x, 0, input.Move.z) + Vector3.up * input.Move.y) * (BaseSpeed * settings.Speed * input.Pace);
            // Outward speed fades over the last metre of reach.
            var outward = Position - Anchor;
            var distance = outward.magnitude;
            if (distance > Reach - 1f && distance > 0.001f)
            {
                var direction = outward / distance;
                var along = Vector3.Dot(want, direction);
                if (along > 0) want -= direction * along * Mathf.Clamp01(distance - (Reach - 1f));
            }
            _velocity = Vector3.Lerp(_velocity, want, 1f - Mathf.Exp(-dt / VelocitySeconds));
            Position = Constrain(Collide(Position, Position + _velocity * dt));
        }

        /// <summary>Back to the player's own eye and view.</summary>
        internal void Return(Transform eye)
        {
            if (eye == null || !eye) return;
            Anchor = eye.position;
            Position = eye.position;
            _velocity = Vector3.zero;
            Aim(eye.rotation);
        }

        /// <summary>
        /// How far away the thing at the centre of the frame is, or null when nothing is within reach. The player
        /// and the held item count while they are in the shot; a ray starting inside the player's own colliders
        /// passes out of them, so focus from the eye is unchanged.
        /// </summary>
        internal float? FocusDistance(PhotoSettings settings)
        {
            var forward = Rotation * Vector3.forward;
            var mask = SolidMask | (settings.ShowPlayer ? PlayerLayer : 0) | (settings.ShowHeld ? HeldLayer : 0);
            return Physics.Raycast(Position, forward, out var hit, FocusReach, mask, QueryTriggerInteraction.Ignore)
                ? Mathf.Max(hit.distance, 0.1f)
                : null;
        }

        private void Aim(Quaternion rotation)
        {
            var forward = rotation * Vector3.forward;
            _yaw = Yaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            _pitch = Pitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(forward.y, -1f, 1f)) * Mathf.Rad2Deg, -89f, 89f);
        }

        // A small sphere swept along the move; on a hit it stops just short and slides once along the surface.
        private Vector3 Collide(Vector3 from, Vector3 to)
        {
            var step = to - from;
            var length = step.magnitude;
            if (length < 1e-5f) return from;
            var direction = step / length;
            if (!Physics.SphereCast(from, Radius, direction, out var hit, length, SolidMask, QueryTriggerInteraction.Ignore)) return to;
            Note(hit);
            var stop = from + direction * Mathf.Max(0, hit.distance - Skin);
            var slide = Vector3.ProjectOnPlane(to - stop, hit.normal);
            var slideLength = slide.magnitude;
            if (slideLength < 1e-5f) return stop;
            var slideDirection = slide / slideLength;
            return Physics.SphereCast(stop, Radius, slideDirection, out var second, slideLength, SolidMask, QueryTriggerInteraction.Ignore)
                ? stop + slideDirection * Mathf.Max(0, second.distance - Skin)
                : stop + slide;
        }

        private Vector3 Constrain(Vector3 position)
        {
            var offset = position - Anchor;
            if (offset.magnitude > Reach) position = Anchor + offset.normalized * Reach;
            // The ocean decides above or below water from the player's view, so the camera keeps to that side, and
            // never closer to the surface than the player's eye already is, so swimming it starts where the eye is.
            try
            {
                var ocean = OceanRenderer.Instance;
                if (ocean != null && ocean)
                {
                    var height = ocean.ViewerHeightAboveWater;
                    if (Math.Abs(height) < 50f)
                    {
                        var surface = Anchor.y - height;
                        position.y = height >= 0
                            ? Mathf.Max(position.y, Mathf.Min(surface + SurfaceMargin, Anchor.y))
                            : Mathf.Min(position.y, Mathf.Max(surface - SurfaceMargin, Anchor.y));
                    }
                }
            }
            catch (Exception)
            {
                // Without the ocean's reading the camera simply keeps within reach.
            }
            return position;
        }

        private void Note(RaycastHit hit)
        {
            if (_hitLayers.Count >= 8 || hit.collider == null) return;
            var name = LayerMask.LayerToName(hit.collider.gameObject.layer);
            if (_hitLayers.Add(name)) RuntimeEntryPoint.LogSource?.LogInfo("Photo camera met " + hit.collider.name + " on layer " + name + ".");
        }
    }
}
