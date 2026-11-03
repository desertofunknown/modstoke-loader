// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using global::Sons.Weapon;
using UnityEngine;

namespace Modstoke.Sons.Combat
{
    // Only the particle-only descendants are eligible; their impact parents run game logic.
    internal sealed class StrictCombatImpactParticlePrefab
    {
        private static readonly string[] WoodChildNames = { "SmokeA", "SmokeC", "ChipsA" };
        private static readonly string[] HardSurfaceChildNames = { "ShardsImpact", "GrenadeExplosionSmokeA" };
        private readonly BallisticImpactSound _sound;
        private readonly ImpactEffectManager _manager;
        private readonly Il2CppReferenceArray<ImpactEffectManager.ParticleTypeDefinition> _definitions;
        private readonly ImpactEffectManager.ParticleTypeDefinition _definition;
        private readonly Il2CppReferenceArray<GameObject> _prefabs;
        private readonly GameObject _root;
        private readonly Transform _rootTransform, _intermediate;
        private readonly global::ProjectileImpactObject _impact;
        private readonly Node[] _nodes;

        private sealed class Node
        {
            internal GameObject Object;
            internal Transform Transform;
            internal ParticleSystem Particles;
            internal ParticleSystemRenderer Renderer;
            internal Material Material;
            internal Mesh Mesh;
            internal Vector3 Position, Scale;
            internal Quaternion Rotation;
        }

        private StrictCombatImpactParticlePrefab(BallisticImpactSound sound,
            ImpactEffectManager manager, Il2CppReferenceArray<ImpactEffectManager.ParticleTypeDefinition> definitions,
            ImpactEffectManager.ParticleTypeDefinition definition, Il2CppReferenceArray<GameObject> prefabs,
            GameObject root, Transform rootTransform, Transform intermediate,
            global::ProjectileImpactObject impact, Node[] nodes, Vector3 scale, float maximumDistance)
        {
            _sound = sound;
            _manager = manager;
            _definitions = definitions;
            _definition = definition;
            _prefabs = prefabs;
            _root = root;
            _rootTransform = rootTransform;
            _intermediate = intermediate;
            _impact = impact;
            _nodes = nodes;
            Source = nodes[0].Object;
            RootScale = scale;
            MaximumDistance = maximumDistance;
        }

        internal GameObject Source { get; }
        internal Vector3 RootScale { get; }
        internal float MaximumDistance { get; }

        internal static bool TryCapture(BallisticImpactSound sound,
            out StrictCombatImpactParticlePrefab capture)
        {
            capture = null;
            try
            {
                if (!TryReadRoute(sound, out var manager, out var definitions, out var definition, out var prefabs,
                    out var root, out var rootTransform, out var intermediate, out var impact, out var source,
                    out var scale, out float distance) || !TryReadGraph(source, sound, false, out var nodes)) return false;
                var candidate = new StrictCombatImpactParticlePrefab(sound, manager, definitions, definition, prefabs,
                    root, rootTransform, intermediate, impact, nodes, scale, distance);
                if (!candidate.IsCurrent()) return false;
                capture = candidate;
                return true;
            }
            catch (Exception) { return false; }
        }

        internal bool IsCurrent()
        {
            try
            {
                if (!TryReadRoute(_sound, out var manager, out var definitions, out var definition, out var prefabs,
                    out var root, out var rootTransform, out var intermediate, out var impact, out var source,
                    out var scale, out float distance)
                    || !Same(_manager, manager) || !Same(_definitions, definitions) || !Same(_definition, definition)
                    || !Same(_prefabs, prefabs) || !Same(_root, root) || !Same(_rootTransform, rootTransform)
                    || !Same(_impact, impact) || !Same(Source, source)
                    || (_intermediate is null ? intermediate is not null : !Same(_intermediate, intermediate))
                    || !Exact(scale, RootScale) || distance != MaximumDistance
                    || !TryReadGraph(source, _sound, false, out var nodes)) return false;
                return Matches(nodes);
            }
            catch (Exception) { return false; }
        }

        internal bool IsSafeClone(GameObject clone)
        {
            try
            {
                return TryReadGraph(clone, _sound, true, out _);
            }
            catch (Exception) { return false; }
        }

        private bool Matches(Node[] nodes)
        {
            if (nodes.Length != _nodes.Length) return false;
            for (int i = 0; i < nodes.Length; i++)
            {
                var first = _nodes[i];
                var second = nodes[i];
                if (!Same(first.Object, second.Object) || !Same(first.Transform, second.Transform)
                    || !Same(first.Particles, second.Particles) || !Same(first.Renderer, second.Renderer)
                    || !Same(first.Material, second.Material)
                    || (first.Mesh is null ? second.Mesh is not null : !Same(first.Mesh, second.Mesh))
                    || !Exact(first.Position, second.Position) || !Exact(first.Scale, second.Scale)
                    || !Exact(first.Rotation, second.Rotation)) return false;
            }
            return true;
        }

        private static bool TryReadRoute(BallisticImpactSound sound,
            out ImpactEffectManager manager, out Il2CppReferenceArray<ImpactEffectManager.ParticleTypeDefinition> definitions,
            out ImpactEffectManager.ParticleTypeDefinition definition, out Il2CppReferenceArray<GameObject> prefabs,
            out GameObject root, out Transform rootTransform, out Transform intermediate,
            out global::ProjectileImpactObject impact, out GameObject source, out Vector3 scale, out float distance)
        {
            manager = null; definitions = null; definition = null; prefabs = null; root = null;
            rootTransform = null; intermediate = null; impact = null; source = null;
            scale = default; distance = 0;
            bool wood = sound == BallisticImpactSound.Wood;
            if (!wood && sound != BallisticImpactSound.HardSurface) return false;
            int type = wood ? 5 : 9;
            manager = ImpactEffectManager._instance;
            if (!Live(manager)) return false;
            distance = manager._maxActiveDistance;
            definitions = manager._particleTypeDefinitions;
            if (!float.IsFinite(distance) || distance <= 0 || distance > 100
                || !Live(definitions) || definitions.Length == 0 || definitions.Length > 32) return false;
            for (int i = 0; i < definitions.Length; i++)
            {
                var current = definitions[i];
                if (!Live(current)) return false;
                if ((int)current._type != type) continue;
                if (definition is not null) return false;
                definition = current;
            }
            if (!Live(definition)) return false;
            prefabs = definition._prefabs;
            if (!Live(prefabs) || prefabs.Length != 1) return false;
            root = prefabs[0];
            if (!Live(root) || !Name(root, wood ? "HitWood" : "HitHardSurface")) return false;
            rootTransform = root.transform;
            impact = root.GetComponent<global::ProjectileImpactObject>();
            if (!Live(rootTransform) || !Live(impact) || !impact.enabled || impact._alignToVelocity
                || (int)impact._impactReplicationType != type
                || !string.Equals(impact._impactAudioEvent, wood
                    ? "event:/SotF Events/Combat/Bullets/bullet_dirt_impact"
                    : "event:/SotF Events/Combat/Bullets/bullet_impact_generic", StringComparison.Ordinal)) return false;
            scale = rootTransform.localScale;
            if (!PositiveScale(scale)) return false;
            var child = OnlyChild(rootTransform, wood ? "WoodProjectileImpactA" : "SparksImpact");
            if (!Live(child)) return false;
            if (wood)
            {
                intermediate = child;
                if (!Exact(child.localPosition, Vector3.zero) || !Exact(child.localRotation, Quaternion.identity)
                    || !Exact(child.localScale, Vector3.one)) return false;
                child = OnlyChild(child, "SplintersA");
            }
            if (!Live(child)) return false;
            source = child.gameObject;
            return Live(source);
        }

        private static bool TryReadGraph(GameObject source, BallisticImpactSound sound,
            bool clone, out Node[] nodes)
        {
            nodes = null;
            bool wood = sound == BallisticImpactSound.Wood;
            string rootName = wood ? "SplintersA" : "SparksImpact";
            if (!Live(source) || (!Name(source, rootName) && (!clone || !Name(source, rootName + "(Clone)")))) return false;
            string[] children = wood ? WoodChildNames : HardSurfaceChildNames;
            if (!TryReadNode(source, children.Length, out var parent)) return false;
            nodes = new Node[children.Length + 1];
            nodes[0] = parent;
            for (int i = 0; i < children.Length; i++)
            {
                Transform selected = null;
                for (int j = 0; j < children.Length; j++)
                {
                    var child = parent.Transform.GetChild(j);
                    if (!Live(child) || !Live(child.gameObject) || !Same(child.parent, parent.Transform)) return false;
                    if (!Name(child.gameObject, children[i])) continue;
                    if (selected is not null) return false;
                    selected = child;
                }
                if (!Live(selected) || !TryReadNode(selected.gameObject, 0, out var node)) return false;
                for (int j = 0; j <= i; j++) if (Same(nodes[j].Object, node.Object)) return false;
                nodes[i + 1] = node;
            }
            return true;
        }

        private static bool TryReadNode(GameObject target, int children, out Node node)
        {
            node = null;
            if (!Live(target) || !target.activeSelf) return false;
            var components = target.GetComponents<Component>();
            if (!Live(components) || components.Length != 3) return false;
            Transform transform = null;
            ParticleSystem particles = null;
            ParticleSystemRenderer renderer = null;
            foreach (var component in components)
            {
                if (!Live(component) || !Same(component.gameObject, target)) return false;
                switch (component.GetIl2CppType().FullName)
                {
                    case "UnityEngine.Transform":
                        if (transform is not null) return false;
                        transform = component.TryCast<Transform>(); break;
                    case "UnityEngine.ParticleSystem":
                        if (particles is not null) return false;
                        particles = component.TryCast<ParticleSystem>(); break;
                    case "UnityEngine.ParticleSystemRenderer":
                        if (renderer is not null) return false;
                        renderer = component.TryCast<ParticleSystemRenderer>(); break;
                    default: return false;
                }
            }
            if (!Live(transform) || !Live(particles) || !Live(renderer) || transform.childCount != children
                || !StrictCombatParticleEffects.TryReadImpactSafety(particles, out bool safe) || !safe) return false;
            var main = particles.main;
            float duration = main.duration;
            if (main.maxParticles != 1000 || !float.IsFinite(duration) || duration <= 0 || duration > 5.01f
                || main.startDelayMultiplier != 0 || main.simulationSpeed != 1 || !main.playOnAwake) return false;
            var materials = renderer.sharedMaterials;
            if (!Live(materials) || materials.Length != 1 || !Live(materials[0])) return false;
            Mesh mesh = null;
            if (renderer.renderMode == ParticleSystemRenderMode.Mesh)
            {
                mesh = renderer.mesh;
                if (!Live(mesh)) return false;
            }
            Vector3 position = transform.localPosition, scale = transform.localScale;
            Quaternion rotation = transform.localRotation;
            if (!Finite(position) || !Finite(scale) || !float.IsFinite(rotation.x) || !float.IsFinite(rotation.y)
                || !float.IsFinite(rotation.z) || !float.IsFinite(rotation.w)) return false;
            node = new Node { Object = target, Transform = transform, Particles = particles, Renderer = renderer,
                Material = materials[0], Mesh = mesh, Position = position, Scale = scale, Rotation = rotation };
            return true;
        }

        private static Transform OnlyChild(Transform parent, string name)
        {
            if (!Live(parent) || parent.childCount != 1) return null;
            var child = parent.GetChild(0);
            return Live(child) && Live(child.gameObject) && Same(child.parent, parent) && Name(child.gameObject, name)
                ? child : null;
        }

        private static bool Name(GameObject target, string name)
            => string.Equals(target.name, name, StringComparison.Ordinal);
        private static bool Live(Il2CppObjectBase value)
            => value is not null && !value.WasCollected && value.Pointer != IntPtr.Zero;
        private static bool Live(UnityEngine.Object value) => Live((Il2CppObjectBase)value) && value;
        private static bool Same(Il2CppObjectBase first, Il2CppObjectBase second)
            => Live(first) && Live(second) && first.Pointer == second.Pointer;
        private static bool Same(UnityEngine.Object first, UnityEngine.Object second)
            => Live(first) && Live(second) && first.Pointer == second.Pointer;
        private static bool Finite(Vector3 value)
            => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        private static bool PositiveScale(Vector3 value)
            => Finite(value) && value.x > 0 && value.x <= 4 && value.y > 0 && value.y <= 4 && value.z > 0 && value.z <= 4;
        private static bool Exact(Vector3 first, Vector3 second)
            => first.x == second.x && first.y == second.y && first.z == second.z;
        private static bool Exact(Quaternion first, Quaternion second)
            => first.x == second.x && first.y == second.y && first.z == second.z && first.w == second.w;
    }
}
