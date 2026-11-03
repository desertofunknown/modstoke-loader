// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using global::Sons.Multiplayer.Client;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Modstoke.Sons
{
    public sealed partial class PlayerClientMovement
    {
        // Native SetPlayerVisibility changes linked GameObjects; SetPlayerActive also changes
        // colliders/AI. Neither is a renderer-only operation. Keep those native paths untouched.
        private sealed class ReplicaPresentation
        {
            private const int MaximumRenderers = 512;
            private readonly PlayerClientMovement _owner;
            private readonly BoltEntity _entity;
            private readonly Bolt.Entity _attachment;
            private readonly GameObject _root;
            private readonly Transform _transform;
            private readonly Transform _parent;
            private readonly int _instance;
            private readonly int _scene;
            private readonly CoopPlayerRemoteSetup _setup;
            private readonly HDRenderPipeline _pipeline;
            private readonly Il2CppSystem.Action<ScriptableRenderContext, Camera> _beforeCamera;
            private readonly Il2CppSystem.Collections.Generic.List<Renderer> _renderers = new(32);
            private readonly Dictionary<int, RendererSlot> _byInstance = new();
            private readonly Dictionary<int, Renderer> _retainedRenderers = new();
            private readonly List<RendererSlot> _slots = new();
            private bool _subscriptionAttempted;
            private bool _refreshing;
            private bool _released;

            internal ReplicaPresentation(PlayerClientMovement owner, BoltEntity entity)
            {
                _owner = owner;
                _entity = entity;
                _attachment = entity._entity;
                _root = entity.gameObject;
                _transform = _root.transform;
                _parent = _transform.parent;
                _instance = _root.GetInstanceID();
                _scene = _root.scene.handle;
                _pipeline = RenderPipelineManager.currentPipeline?.TryCast<HDRenderPipeline>();
                var setups = _root.GetComponentsInChildren<CoopPlayerRemoteSetup>(true);
                if (setups.Length != 1)
                    throw new InvalidOperationException("Replica presentation requires one native remote setup.");
                _setup = setups[0];
                _beforeCamera = (Action<ScriptableRenderContext, Camera>)BeforeCamera;
                Validate();
                _owner._rig.Root.GetComponentsInChildren<Renderer>(true, _renderers);
                for (int i = 0; i < _renderers.Count; i++)
                {
                    var renderer = _renderers[i];
                    if (Alive(renderer)) _retainedRenderers.Add(renderer.GetInstanceID(), renderer);
                }
                _renderers.Clear();
            }

            internal void Start()
            {
                Refresh();
                // In this build HDRenderPipeline.TryCull invokes BeginCameraRendering before
                // ScriptableRenderContext.Cull. The delegate catches late clothing/held-item changes.
                _subscriptionAttempted = true;
                RenderPipelineManager.add_beginCameraRendering(_beforeCamera);
                Validate();
            }

            private bool RootMatches() => Alive(_entity) && Alive(_root) && Alive(_transform)
                && _root.GetInstanceID() == _instance && Same(_entity.gameObject, _root)
                && Same(_root.transform, _transform) && Same(_transform.parent, _parent)
                && _root.scene.handle == _scene;

            private bool SeparateRig() => Alive(_owner._rig.Root)
                && !_transform.IsChildOf(_owner._rig.Root.transform)
                && !_owner._rig.Root.transform.IsChildOf(_transform);

            private void Validate()
            {
                // Current can retire the bridge synchronously and release this lease.
                if (_released || !_owner.Current() || _released || _owner._bridge.IsServer
                    || !RootMatches() || !SeparateRig() || !Same(_entity._entity, _attachment)
                    || !_entity.isAttached || _entity.isOwner || _entity.networkId.PackedValue != _owner._bridge.NetworkId
                    || !Live(_pipeline) || !Same(RenderPipelineManager.currentPipeline, _pipeline)
                    || !Alive(_setup) || !Same(_setup.entity, _entity)
                    || !_setup.transform.IsChildOf(_transform))
                    throw new InvalidOperationException("Replica presentation lifetime, native setup or HDRP binding changed.");
                RequireLocalLinks(_setup._visibilityLinkedGameObjects);
                RequireLocalLinks(_setup._activeLinkedObjects);
            }

            private void RequireLocalLinks(Il2CppSystem.Collections.Generic.List<GameObject> links)
            {
                if (!Live(links) || links.Count > MaximumRenderers)
                    throw new InvalidOperationException("Replica presentation links are unavailable or unbounded.");
                for (int i = 0; i < links.Count; i++)
                {
                    var link = links[i];
                    if (Alive(link) && !link.transform.IsChildOf(_transform))
                        throw new InvalidOperationException("Native replica presentation links outside its root are unsupported.");
                }
            }

            internal void Refresh()
            {
                if (_refreshing) throw new InvalidOperationException("Replica presentation reentered during a renderer update.");
                _refreshing = true;
                try
                {
                    Validate();
                    for (int i = _slots.Count - 1; i >= 0; i--)
                    {
                        var slot = _slots[i];
                        if (!Alive(slot.Renderer))
                        {
                            _byInstance.Remove(slot.Instance);
                            _slots.RemoveAt(i);
                        }
                        else if (!slot.Matches(_transform) || !slot.Renderer.forceRenderingOff)
                            throw new InvalidOperationException("A replica renderer moved or its suppression was changed externally.");
                    }
                    _root.GetComponentsInChildren<Renderer>(true, _renderers);
                    if (_renderers.Count == 0 || _renderers.Count > MaximumRenderers)
                        throw new InvalidOperationException("Replica renderer count is unsupported.");
                    for (int i = 0; i < _renderers.Count; i++)
                    {
                        var renderer = _renderers[i];
                        if (!Alive(renderer)) throw new InvalidOperationException("A replica renderer disappeared during capture.");
                        int id = renderer.GetInstanceID();
                        if (_retainedRenderers.TryGetValue(id, out var retained) && Same(renderer, retained))
                            throw new InvalidOperationException("An original-rig renderer moved into the replica hierarchy.");
                        if (_byInstance.TryGetValue(id, out var existing))
                        {
                            if (!Same(renderer, existing.Renderer))
                                throw new InvalidOperationException("Replica renderer identity was reused.");
                            continue;
                        }
                        if (_slots.Count == MaximumRenderers)
                            throw new InvalidOperationException("Replica renderer lifetime count is unsupported.");
                        var slot = new RendererSlot(renderer);
                        if (!slot.Matches(_transform) || _released)
                            throw new InvalidOperationException("Replica renderer ownership changed during capture.");
                        _slots.Add(slot);
                        _byInstance.Add(id, slot);
                        slot.Suppress();
                    }
                    Validate();
                }
                finally { _refreshing = false; }
            }

            private void BeforeCamera(ScriptableRenderContext context, Camera camera)
            {
                if (_released || _owner._disposed) return;
                // Never touch Unity objects from an unexpected callback thread.
                if (Environment.CurrentManagedThreadId != _owner._thread) return;
                try { Refresh(); }
                catch (Exception error) { _owner.Stop("Replica presentation stopped (" + error.Message + ")."); }
            }

            internal string Release()
            {
                if (_released) return null;
                _released = true;
                bool subscriptionFailed = false;
                if (_subscriptionAttempted)
                {
                    try { RenderPipelineManager.remove_beginCameraRendering(_beforeCamera); }
                    catch (Exception) { subscriptionFailed = true; }
                    _subscriptionAttempted = false;
                }
                int unrestored = 0;
                foreach (var slot in _slots)
                {
                    if (!slot.Written) continue;
                    try
                    {
                        if (!Alive(slot.Renderer)) continue;
                        // A false flag belongs to a subsequent writer. Never overwrite it. A
                        // reattached entity or reparented renderer is no longer this lease's target.
                        if (!slot.Renderer.forceRenderingOff) continue;
                        if (!RootMatches() || (Alive(_owner._rig.Root) && !SeparateRig())
                            || (_entity._entity != null && !Same(_entity._entity, _attachment))
                            || !slot.Matches(_transform)) { unrestored++; continue; }
                        slot.Renderer.forceRenderingOff = false;
                        if (slot.Renderer.forceRenderingOff) unrestored++;
                    }
                    catch (Exception) { unrestored++; }
                }
                _slots.Clear();
                _byInstance.Clear();
                _retainedRenderers.Clear();
                _renderers.Clear();
                return unrestored == 0 && !subscriptionFailed ? null
                    : "Replica presentation cleanup unconfirmed: " + unrestored + " renderer(s), callback removal "
                        + (subscriptionFailed ? "failed." : "confirmed.");
            }

            private sealed class RendererSlot
            {
                internal readonly Renderer Renderer;
                internal readonly int Instance;
                private readonly GameObject _object;
                private readonly Transform _transform;
                private readonly Transform _parent;
                internal bool Written { get; private set; }

                internal RendererSlot(Renderer renderer)
                {
                    Renderer = renderer;
                    Instance = renderer.GetInstanceID();
                    _object = renderer.gameObject;
                    _transform = renderer.transform;
                    _parent = _transform.parent;
                }

                internal bool Matches(Transform root) => Alive(Renderer) && Renderer.GetInstanceID() == Instance
                    && Alive(_object) && Alive(_transform) && Same(Renderer.gameObject, _object)
                    && Same(Renderer.transform, _transform) && Same(_transform.parent, _parent)
                    && _transform.IsChildOf(root);

                internal void Suppress()
                {
                    if (Renderer.forceRenderingOff) return;
                    // Record the attempted write before crossing the native boundary so partial
                    // startup can conditionally restore it. Preexisting suppression is never ours.
                    Written = true;
                    Renderer.forceRenderingOff = true;
                    if (!Renderer.forceRenderingOff)
                        throw new InvalidOperationException("Replica renderer suppression did not read back.");
                }
            }
        }
    }
}
