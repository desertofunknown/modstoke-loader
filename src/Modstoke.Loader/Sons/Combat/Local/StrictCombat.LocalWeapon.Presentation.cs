// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Endnight.Utilities;
using global::Sons.Gameplay;
using global::Sons.Utilities;
using global::Sons.Weapon;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons.Combat
{
    internal sealed partial class StrictCombatLocalWeapon
    {
        private const string PistolShotAudio = "event:/SotF Events/player sounds/Weapons/PistolTactical/PistolTacticalFire";
        private static readonly string[] MuzzleComponentTypes =
        {
            "UnityEngine.Transform", "Sons.Gameplay.MuzzleFlash"
        };
        private static readonly string[] MuzzleVisualComponentTypes =
        {
            "UnityEngine.Transform", "UnityEngine.MeshFilter", "UnityEngine.MeshRenderer"
        };
        private static readonly string[] ParticleRootComponentTypes =
        {
            "UnityEngine.Transform", "Sons.Utilities.DestroyAfter", "Endnight.Utilities.SetTargetsActive"
        };
        private static readonly string[] ParticleOffsetComponentTypes = { "UnityEngine.Transform" };
        private static readonly string[] ParticleComponentTypes =
        {
            "UnityEngine.Transform", "UnityEngine.ParticleSystem", "UnityEngine.ParticleSystemRenderer"
        };
        private static readonly string[] FlashLightComponentTypes =
        {
            "UnityEngine.Transform", "UnityEngine.Light",
            "UnityEngine.Rendering.HighDefinition.HDAdditionalLightData", "TheForest.Utils.DisableAfterDelay"
        };
        private static readonly string[] CasingComponentTypes =
        {
            "UnityEngine.Transform", "UnityEngine.MeshFilter", "UnityEngine.MeshRenderer",
            "UnityEngine.BoxCollider", "UnityEngine.Rigidbody", "Sons.Utilities.DestroyAfter"
        };
        private static readonly string[] MuzzleParticleNames =
        {
            "FireballSideB", "FireballBackA", "FireballBackB", "SmokeA", "SmokeB"
        };
        private readonly List<GameObject> _presentedParticles = new();
        private MuzzleFlash _presentedMuzzle;

        internal StrictCombatShotPresentationStatus PresentShot(StrictCombatClient client,
            StrictCombatLocalShot shot, in StrictCombatBinding binding, in StrictCombatSnapshot state, int frame)
        {
            bool attempted = false;
            try
            {
                if (!ReferenceEquals(client, Owner) || shot == null || !ReferenceEquals(shot.Owner.Capture, this)
                    || !client.IsCurrentShotPresentation(shot, in binding, in state, frame)
                    || !IsHeldCurrent(in binding, in state)) return StrictCombatShotPresentationStatus.UnsupportedContext;
                var pistol = Controller.TryCast<CompactPistolWeaponController>();
                var animator = Controller._playerAnimator;
                var muzzle = Controller._muzzleFlashPrefab;
                var muzzleInstance = Controller._muzzleFlashInstance;
                var muzzleParticles = muzzleInstance is null ? null : muzzleInstance._particlesInstance;
                var casing = Controller._ammoSpawn;
                var mount = Controller._ammoEmpty;
                var transform = Controller.transform;
                if (!Live(pistol)
                    || !string.Equals(pistol.GetIl2CppType().FullName, "Sons.Weapon.CompactPistolWeaponController", StringComparison.Ordinal)
                    || !Live(animator) || !Live(muzzle) || !Live(casing)
                    || !Live(mount) || !Live(transform)) return StrictCombatShotPresentationStatus.UnsupportedContext;
                _presentedParticles.RemoveAll(particle => !Live(particle));
                if (_presentedParticles.Count >= 32
                    || (muzzleInstance is not null && !Same(_presentedMuzzle, muzzleInstance)))
                    return StrictCombatShotPresentationStatus.UnsupportedContext;
                var particles = muzzle._particlesPrefab;
                string audioEvent = Controller._gunShotAudioEvent;
                Vector3 position = transform.position;
                Vector3 velocity = Controller._casingVelocity, angularVelocity = Controller._casingAngularVelocity;
                bool suppressed = pistol.HasSuppressor();
                if (!Finite(position) || !Exact(velocity, new Vector3(-.25f, 2f, -.75f))
                    || !Exact(angularVelocity, new Vector3(0f, 1f, 3f))
                    || !string.Equals(audioEvent, PistolShotAudio, StringComparison.Ordinal))
                    return StrictCombatShotPresentationStatus.UnsupportedContext;

                // Local functions retain copies, never a later accepted revision or a new life.
                var acceptedBinding = binding;
                var acceptedState = state;
                void RequireCurrent()
                {
                    if (!client.IsCurrentShotPresentation(shot, in acceptedBinding, in acceptedState, frame)
                        || !Same(animator, Controller._playerAnimator)
                        || !Same(animator, LocalPlayer._Animator_k__BackingField)
                        || !Same(animator, Life.Stats._animator) || !animator.isActiveAndEnabled || animator.layerCount <= 2
                        || !Same(muzzle, Controller._muzzleFlashPrefab) || !Same(particles, muzzle._particlesPrefab)
                        || !Same(casing, Controller._ammoSpawn) || !Same(mount, Controller._ammoEmpty)
                        || !Same(transform, Controller.transform) || !mount.activeInHierarchy
                        || !Finite(transform.position) || !Finite(mount.transform.position)
                        || !Exact(velocity, Controller._casingVelocity)
                        || !Exact(angularVelocity, Controller._casingAngularVelocity)
                        || !string.Equals(audioEvent, Controller._gunShotAudioEvent, StringComparison.Ordinal)
                        || Controller._playFireAudio != RangedWeaponController.FireAudio.OnAnimShootCallback
                        || pistol.HasSuppressor() != suppressed
                        || !StockMuzzle(muzzle, particles) || !StockCasing(casing)
                        || muzzle._particlesInstance is not null
                        || !SameOptionalMuzzle(muzzleInstance, Controller._muzzleFlashInstance, particles, muzzleParticles, transform)
                        || !client.IsCurrentShotPresentation(shot, in acceptedBinding, in acceptedState, frame))
                        throw new InvalidOperationException("The accepted shot lost its captured presentation context.");
                }

                RequireCurrent();
                var parameters = new Il2CppSystem.Object[]
                {
                    (Il2CppSystem.String)"upgrade",
                    System.Runtime.CompilerServices.Unsafe.BitCast<float, Il2CppSystem.Single>(suppressed ? .2f : 0f).BoxIl2CppObject()
                };

                RequireCurrent();
                if (!IsHeldCurrent(in binding, in state)) return StrictCombatShotPresentationStatus.UnsupportedContext;
                attempted = true;
                Controller.OnMuzzleFlash();
                // This helper may create the captured prefab's first instance. Only this call
                // may establish that result; later calls must retain exactly the same instance.
                var createdMuzzle = Controller._muzzleFlashInstance;
                if (muzzleInstance != null && !Same(muzzleInstance, createdMuzzle))
                    throw new InvalidOperationException("The muzzle instance changed during presentation.");
                var createdParticles = createdMuzzle is null ? null : createdMuzzle._particlesInstance;
                if (Live(createdParticles) && !Same(createdParticles, muzzleParticles))
                {
                    if (!StockParticles(createdParticles))
                        throw new InvalidOperationException("The new muzzle particles do not match the captured effect.");
                    _presentedParticles.Add(createdParticles);
                }
                _presentedMuzzle = createdMuzzle;
                muzzleInstance = createdMuzzle;
                muzzleParticles = createdParticles;
                RequireCurrent();

                Controller.SpawnCasing();
                RequireCurrent();

                // The compact-pistol implementation only crossfades this animator. The
                // owned callback gates suppress native ammo, projectile, noise and network work.
                pistol.PlayFireAnimNetwork();
                RequireCurrent();

                FMODCommon.PlayOneshot(audioEvent, position, FMODCommon.NetworkRole.None, parameters);
                RequireCurrent();
                return StrictCombatShotPresentationStatus.Presented;
            }
            catch (Exception)
            {
                if (!attempted) return StrictCombatShotPresentationStatus.UnsupportedContext;
                shot.Owner.Retire();
                return StrictCombatShotPresentationStatus.MutationUncertain;
            }
        }

        private bool SameOptionalMuzzle(MuzzleFlash expected, MuzzleFlash current,
            GameObject particles, GameObject activeParticles, Transform controllerTransform)
            => expected is null ? current is null
                : Same(expected, current) && Same(expected.transform.parent, controllerTransform)
                    && StockMuzzle(expected, particles, allowPresentedParticles: true)
                    && SameParticleReference(activeParticles, expected._particlesInstance)
                    && (!Live(activeParticles) || IsPresentedParticle(activeParticles));

        private bool StockMuzzle(MuzzleFlash muzzle, GameObject particles, bool allowPresentedParticles = false)
        {
            if (!Live(muzzle) || !Live(particles) || !Same(particles, muzzle._particlesPrefab)
                || !muzzle._hideTempVisual || !muzzle._attached || muzzle._linkedDestroy || muzzle._autoFire
                || muzzle._particleLifetime != 5f || muzzle._detachAfter != 1f
                || !ComponentsMatch(muzzle.gameObject, MuzzleComponentTypes)) return false;
            var sphere = muzzle._tempVisual;
            if (!Live(sphere) || !Same(sphere.transform.parent, muzzle.transform)) return false;
            for (int i = 0; i < muzzle.transform.childCount; i++)
            {
                var child = muzzle.transform.GetChild(i).gameObject;
                if (!Same(child, sphere) && (!allowPresentedParticles || !IsPresentedParticle(child))) return false;
            }
            if (allowPresentedParticles)
            {
                // Native Fire creates a fresh child for each shot; delayed detach reads the
                // newest field. Retain every live effect until its stock timed destruction.
                foreach (var presented in _presentedParticles)
                {
                    if (!Live(presented)) continue;
                    var parent = presented.transform.parent;
                    if ((parent != null && !Same(parent, muzzle.transform)) || !StockParticles(presented)) return false;
                }
            }
            return ComponentsMatch(sphere, MuzzleVisualComponentTypes)
                && sphere.transform.childCount == 0 && StockParticles(particles);
        }

        private bool IsPresentedParticle(GameObject particle)
        {
            foreach (var presented in _presentedParticles)
                if (Same(particle, presented)) return true;
            return false;
        }

        // Fire leaves its last field pointing at a destroyed particle object until the
        // next shot. Compare that retained reference without treating it as a live target.
        private static bool SameParticleReference(GameObject first, GameObject second)
            => first is null ? second is null : second is not null
                && !first.WasCollected && !second.WasCollected && first.Pointer == second.Pointer;

        private static bool StockParticles(GameObject root)
        {
            if (!Live(root) || root.transform.childCount != 1
                || !ComponentsMatch(root, ParticleRootComponentTypes)
                || !StockDestroy(root, 1f)) return false;
            var offset = root.transform.GetChild(0);
            if (!ComponentsMatch(offset.gameObject, ParticleOffsetComponentTypes) || offset.childCount != 3) return false;
            var fireball = offset.Find("FireballSideA");
            var light = offset.Find("FireLight");
            var backLight = offset.Find("FireLightBack");
            if (!Live(fireball) || !Live(light) || !Live(backLight) || Same(light, backLight)
                || fireball.childCount != 5 || !StockParticle(fireball.gameObject)
                || !StockFlashLight(light.gameObject) || !StockFlashLight(backLight.gameObject)) return false;
            foreach (string name in MuzzleParticleNames)
            {
                var particle = fireball.Find(name);
                if (!Live(particle) || particle.childCount != 0 || !StockParticle(particle.gameObject)) return false;
            }
            var active = root.GetComponent<SetTargetsActive>();
            var targets = active._targets;
            return (int)active._triggerEvent == 2 && !active._mirrorTrigger && Live(targets) && targets.Count == 2
                && ((Same(targets[0], light.gameObject) && Same(targets[1], backLight.gameObject))
                    || (Same(targets[1], light.gameObject) && Same(targets[0], backLight.gameObject)));
        }

        private static bool StockParticle(GameObject target)
        {
            if (!ComponentsMatch(target, ParticleComponentTypes))
                return false;
            var particles = target.GetComponent<ParticleSystem>();
            return Live(particles) && StrictCombatParticleEffects.TryReadSafety(particles, out bool safe) && safe;
        }

        private static bool StockFlashLight(GameObject target)
            => target.transform.childCount == 0
                && ComponentsMatch(target, FlashLightComponentTypes)
                && target.GetComponent<DisableAfterDelay>()._delay == .15f;

        private static bool StockCasing(GameObject casing)
            => Live(casing) && casing.transform.childCount == 0
                && ComponentsMatch(casing, CasingComponentTypes)
                && StockDestroy(casing, 10f);

        private static bool StockDestroy(GameObject target, float lifetime)
        {
            var destroy = target.GetComponent<DestroyAfter>();
            return Live(destroy) && !destroy._randomizeDelayTime && destroy._destroyTime == lifetime
                && destroy._destroyDistance == 0f && !destroy._useUnscaledTime
                && !destroy._ignoreIfChildOfAttachedBoltEntity && !destroy._despawnFromPool
                && !destroy._destroyOnDisable && string.IsNullOrEmpty(destroy._poolName);
        }

        // Exact counts and native type names reject extra behaviours and missing scripts.
        // This describes only the known pistol graphs; it is not a general prefab validator.
        private static bool ComponentsMatch(GameObject target, params string[] types)
        {
            if (!Live(target)) return false;
            var components = target.GetComponents<Component>();
            if (!Live(components) || components.Length != types.Length) return false;
            foreach (string type in types)
            {
                int matches = 0;
                foreach (var component in components)
                {
                    if (!Live(component)) return false;
                    if (string.Equals(component.GetIl2CppType().FullName, type, StringComparison.Ordinal)) matches++;
                }
                if (matches != 1) return false;
            }
            return true;
        }

        private static bool Finite(Vector3 value)
            => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

        private static bool Exact(Vector3 first, Vector3 second)
            => first.x == second.x && first.y == second.y && first.z == second.z;
    }
}
