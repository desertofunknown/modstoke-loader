// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using HarmonyLib;
using Modstoke.Loader;
using global::Sons.Gameplay;
using UnityEngine;

namespace Modstoke.Loader
{
    internal static class ImpactAudioPatch
    {
        private static int _installationAttempted;
        private static int _reportedFailure;
        [ThreadStatic] private static CollisionContext _collision;

        private struct CollisionContext
        {
            internal IntPtr Owner;
            internal Collision Collision;
        }

        internal static void InstallHarmonyPatch(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                warning?.Invoke("Impact audio fix is unavailable: Harmony support is not ready.");
                return;
            }
            var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-impact-audio");
            try
            {
                if (!GameBuild.IsRecognized)
                {
                    // The dedicated-server startup warning already covers client-pinned fixes.
                    if (!GameBuild.IsDedicatedServer)
                        warning?.Invoke("Impact audio fix skipped for an unrecognized game build; native impact audio remains unchanged.");
                    return;
                }
                var collision = AccessTools.Method(typeof(ObjectPhysicsInteractionSfx), nameof(ObjectPhysicsInteractionSfx.OnCollisionEnter),
                    new[] { typeof(Collision) }) ?? throw new MissingMethodException(nameof(ObjectPhysicsInteractionSfx.OnCollisionEnter));
                var impact = AccessTools.Method(typeof(ObjectPhysicsInteractionSfx), nameof(ObjectPhysicsInteractionSfx.TryTriggerHardSurfaceImpact),
                    new[] { typeof(Collider) }) ?? throw new MissingMethodException(nameof(ObjectPhysicsInteractionSfx.TryTriggerHardSurfaceImpact));
                harmony.Patch(collision,
                    prefix: new HarmonyMethod(typeof(ImpactAudioPatch), nameof(BeforeCollision)),
                    finalizer: new HarmonyMethod(typeof(ImpactAudioPatch), nameof(AfterCollision)));
                harmony.Patch(impact, prefix: new HarmonyMethod(typeof(ImpactAudioPatch), nameof(BeforeImpact)) { priority = Priority.Last });
                RuntimeEntryPoint.LogSource?.LogInfo("Contact-aware impact audio fix registered for the recognized game build.");
            }
            catch (Exception error)
            {
                harmony.UnpatchSelf();
                warning?.Invoke("Impact audio fix registration did not complete: " + error);
            }
        }

        private static void BeforeCollision(ObjectPhysicsInteractionSfx __instance, Collision __0, out CollisionContext __state)
        {
            __state = _collision;
            _collision = new CollisionContext { Owner = __instance.Pointer, Collision = __0 };
        }

        private static void AfterCollision(CollisionContext __state)
        {
            _collision = __state;
        }

        private static bool BeforeImpact(ObjectPhysicsInteractionSfx __instance, Collider __0, bool __runOriginal)
        {
            if (!__runOriginal)
                return false;
            try
            {
                TriggerImpact(__instance, __0);
            }
            catch (Exception error)
            {
                // Retrying the original would count this contact twice and repeat the invalid query.
                if (Interlocked.Exchange(ref _reportedFailure, 1) == 0)
                {
                    try { RuntimeEntryPoint.LogSource?.LogWarning("Impact audio could not be played for a contact: " + error); }
                    catch (Exception) { }
                }
            }
            return false;
        }

        private static void TriggerImpact(ObjectPhysicsInteractionSfx source, Collider collider)
        {
            var body = source._rigidBody;
            if (!body)
            {
                source.ValidateRigidBody();
                body = source._rigidBody;
                if (!body)
                {
                    source.enabled = false;
                    return;
                }
            }
            if (!collider || collider.gameObject == body.gameObject)
                return;
            if (body.isKinematic || body.IsSleeping())
            {
                source._contactPointCount = 0;
                return;
            }

            // Sliding cleanup depends on counting even contacts that do not produce an impact sound.
            var target = collider.gameObject;
            if (!target.GetComponentInParent<DisableSlideSFX>())
                source._contactPointCount++;
            if (source._inWaterCount > 0)
                return;
            var threshold = source._requiredVelocityToTriggerSfx;
            var thresholdSquared = threshold * threshold;
            if (body.velocity.sqrMagnitude < thresholdSquared)
                return;
            source.RefreshVelocityCache();
            if (source._velocitySqrMagnitudeCache < thresholdSquared)
                return;

            var data = source._physicsSfxData;
            string eventName;
            if (UnderfootSurfaceDetector.GetSurfaceType(collider) == UnderfootSurfaceDetector.SurfaceType.Wood)
            {
                eventName = data._woodImpact;
            }
            else
            {
                if ((data._subtleImpactSfxLayerMask.value & (1 << target.layer)) == 0)
                    return;
                eventName = data._subtleImpact;
                if (source._isAnItem)
                {
                    var item = source.GetItemData();
                    if (!item)
                        return;
                    eventName = ObjectPhysicsInteractionSfx.MouseoverEventPath + item._name;
                }
            }

            // Native playback retains event overlap suppression, speed parameters and authoritative noise.
            source.PlayEvent(eventName, ImpactPosition(source, collider));
        }

        private static Vector3 ImpactPosition(ObjectPhysicsInteractionSfx source, Collider collider)
        {
            var position = source.transform.position;
            var collision = _collision.Collision;
            // The collider is known alive here, so identity is a pointer compare without Unity's native null checks.
            var target = collider.Pointer;
            if (_collision.Owner == source.Pointer && collision != null && collision.collider?.Pointer == target)
            {
                var closest = position;
                var distance = float.PositiveInfinity;
                var count = collision.contactCount;
                for (var i = 0; i < count; i++)
                {
                    var contact = collision.GetContact(i);
                    if (contact.otherCollider?.Pointer != target && contact.thisCollider?.Pointer != target)
                        continue;
                    var point = contact.point;
                    var squared = (point - position).sqrMagnitude;
                    if (float.IsFinite(squared) && squared < distance)
                    {
                        closest = point;
                        distance = squared;
                    }
                }
                if (float.IsFinite(distance))
                    return closest;
            }

            if (collider.TryCast<BoxCollider>() != null || collider.TryCast<SphereCollider>() != null
                || collider.TryCast<CapsuleCollider>() != null)
                return collider.ClosestPoint(position);
            var mesh = collider.TryCast<MeshCollider>();
            if (mesh != null && mesh.convex)
                return collider.ClosestPoint(position);

            // Trigger/direct calls have no contact manifold. Keep sound local without querying unsupported geometry.
            return position;
        }
    }
}
