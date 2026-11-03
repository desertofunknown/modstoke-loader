// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using UnityEngine;

namespace Modstoke.Sons.Combat
{
    // These roots contain only the qualified stock particle branches. They never enter
    // the game's impact pool, and cleanup continues independently of the Client mod.
    internal static class StrictCombatImpactParticles
    {
        private const int MaximumEffects = 32;
        private const long LifetimeMs = 2000;
        private static readonly Effect[] Effects = new Effect[MaximumEffects];
        private static int _thread;
        private static bool _busy;

        internal static void TryPresent(StrictCombatClient owner, in StrictCombatImpactPresentation presentation, int frame)
        {
            if (_busy || owner == null || !owner.IsCurrentImpactPresentation(presentation.Audio, frame)) return;
            int thread = Environment.CurrentManagedThreadId;
            int registered = Interlocked.CompareExchange(ref _thread, thread, 0);
            if (registered != 0 && registered != thread) return;
            _busy = true;
            Effect effect = null;
            bool activated = false;
            try
            {
                long now = Environment.TickCount64;
                if (now < 0 || now > long.MaxValue - LifetimeMs
                    || !StrictCombatImpactParticlePrefab.TryCapture(presentation.Audio.Sound, out var prefab)
                    || !owner.TryReadImpactListenerPosition(out var listener)) return;
                var point = presentation.Audio.Position;
                double dx = (double)point.X - listener.x, dy = (double)point.Y - listener.y, dz = (double)point.Z - listener.z;
                if (dx * dx + dy * dy + dz * dz > (double)prefab.MaximumDistance * prefab.MaximumDistance) return;
                int slot = Array.FindIndex(Effects, value => value == null);
                if (slot < 0) return;

                effect = new Effect(owner, presentation.Audio, prefab, now + LifetimeMs);
                Effects[slot] = effect; // Retain before the first allocation, including uncertain creation.
                Require(owner.IsCurrentImpactPresentation(presentation.Audio, frame) && prefab.IsCurrent());
                effect.RootAttempted = true;
                effect.Root = new GameObject("Modstoke impact particles");
                Require(Live(effect.Root));
                effect.RootId = effect.Root.GetInstanceID();
                effect.Root.SetActive(false);
                Require(IsOwnedGraph(effect) && !effect.Root.activeInHierarchy);
                var root = effect.Root.transform;
                root.position = new Vector3(point.X, point.Y, point.Z);
                var normal = presentation.Normal;
                root.rotation = Quaternion.LookRotation(new Vector3(normal.X, normal.Y, normal.Z));
                root.localScale = prefab.RootScale;

                Require(owner.IsCurrentImpactPresentation(presentation.Audio, frame) && prefab.IsCurrent()
                    && IsOwnedGraph(effect) && !effect.Root.activeInHierarchy && Live(root));
                effect.CloneAttempted = true;
                effect.Clone = UnityEngine.Object.Instantiate<GameObject>(prefab.Source, root, false);
                Require(Live(effect.Clone));
                effect.CloneId = effect.Clone.GetInstanceID();
                Require(IsOwnedGraph(effect) && !effect.Clone.activeInHierarchy && prefab.IsCurrent()
                    && owner.IsCurrentImpactPresentation(presentation.Audio, frame));

                // Every validated system has playOnAwake and no callback-producing modules.
                // Activation starts the stock burst once, retaining each child's local transform.
                effect.Root.SetActive(true);
                activated = IsOwnedGraph(effect) && effect.Root.activeInHierarchy
                    && owner.IsCurrentImpactPresentation(presentation.Audio, frame);
            }
            catch (Exception)
            {
                // An uncertain cosmetic allocation keeps its reservation until cleanup can identify it.
            }
            finally
            {
                if (effect != null && !activated) DestroyOwned(effect);
                _busy = false;
            }
        }

        internal static void Tick()
        {
            if (Environment.CurrentManagedThreadId != Volatile.Read(ref _thread) || _busy) return;
            _busy = true;
            try
            {
                long now = Environment.TickCount64;
                for (int i = 0; i < Effects.Length; i++)
                {
                    var effect = Effects[i];
                    if (effect == null) continue;
                    try
                    {
                        if (!effect.RootAttempted || Gone(effect.Root))
                        { Effects[i] = null; continue; }
                        if (now < 0 || now >= effect.ExpiresAt || effect.DestroyRequested
                            || !effect.Owner.IsCurrentImpactParticleOwner(effect.Audio)) DestroyOwned(effect);
                        if (Gone(effect.Root)) Effects[i] = null;
                    }
                    catch (Exception) { /* Retain uncertain ownership; do not evict it to admit another effect. */ }
                }
            }
            finally { _busy = false; }
        }

        private static void DestroyOwned(Effect effect)
        {
            try
            {
                if (!IsOwnedGraph(effect)) return;
                effect.DestroyRequested = true;
                UnityEngine.Object.Destroy(effect.Root);
                // Destruction is deferred by Unity. Keep the exact identity until it is observed gone.
            }
            catch (Exception) { }
        }

        private static bool IsOwnedGraph(Effect effect)
        {
            var root = effect.Root;
            if (!Live(root) || root.GetInstanceID() != effect.RootId) return false;
            var components = root.GetComponents<Component>();
            if (components == null || components.Length != 1 || !Live(components[0])
                || components[0].GetIl2CppType().FullName != "UnityEngine.Transform") return false;
            var transform = root.transform;
            if (!Live(transform) || transform.parent != null || transform.childCount > 1) return false;
            if (transform.childCount == 0) return effect.Clone is null || Gone(effect.Clone);
            var child = transform.GetChild(0).gameObject;
            if (!effect.CloneAttempted || !Live(child) || !effect.Prefab.IsSafeClone(child)) return false;
            if (effect.Clone is null)
            {
                // An Instantiate call may throw after attaching the clone to our previously empty root.
                effect.Clone = child;
                effect.CloneId = child.GetInstanceID();
            }
            return Live(effect.Clone) && child.Pointer == effect.Clone.Pointer
                && child.GetInstanceID() == effect.CloneId;
        }

        private static bool Live(UnityEngine.Object value)
            => value is not null && !value.WasCollected && value.Pointer != IntPtr.Zero && value;
        private static bool Gone(UnityEngine.Object value)
            => value is not null && !value.WasCollected && value.Pointer != IntPtr.Zero && !value;
        private static void Require(bool condition)
        {
            if (!condition) throw new InvalidOperationException("Impact presentation context changed.");
        }

        private sealed class Effect
        {
            internal Effect(StrictCombatClient owner, StrictCombatImpactAudio audio,
                StrictCombatImpactParticlePrefab prefab, long expiresAt)
            { Owner = owner; Audio = audio; Prefab = prefab; ExpiresAt = expiresAt; }
            internal readonly StrictCombatClient Owner;
            internal readonly StrictCombatImpactAudio Audio;
            internal readonly StrictCombatImpactParticlePrefab Prefab;
            internal readonly long ExpiresAt;
            internal GameObject Root, Clone;
            internal int RootId, CloneId;
            internal bool RootAttempted, CloneAttempted, DestroyRequested;
        }
    }
}
