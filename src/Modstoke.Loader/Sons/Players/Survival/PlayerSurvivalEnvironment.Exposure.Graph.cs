// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Endnight.Utilities;
using global::Sons.Areas;
using global::Sons.Gameplay;
using global::Sons.Physics;
using TheForest.World;
using UnityEngine;
using UnityEngine.SceneManagement;
using static Modstoke.Sons.PlayerSurvivalEnvironmentExposure;

namespace Modstoke.Sons
{
    // This is the exposure projection of the native scene graph, not a second Unity
    // scene. It never invokes scene UnityEvents or writes host-local activation state.
    internal sealed partial class PlayerExposureGraph
    {
        internal const int MaximumBytes = 2 * 1024 * 1024;
        private readonly Dictionary<nint, Direction> _directions = new();
        private readonly Dictionary<nint, Filter> _filters = new();
        private readonly Dictionary<nint, Cave> _caves = new();
        private readonly Dictionary<nint, Proxy> _proxies = new();
        private readonly Dictionary<nint, Area> _areas = new();
        private readonly Dictionary<nint, Activation> _activation = new();
        private readonly Dictionary<nint, bool> _activeSelf = new();
        private readonly List<Action> _validate = new();
        private readonly Dictionary<nint, List<Action>> _checks = new();
        private readonly List<Action> _globalChecks = new();
        private readonly List<WeatherSystemWetnessOverride> _order = new();
        private readonly Dictionary<string, nint> _identities = new(StringComparer.Ordinal);
        private readonly HashSet<int> _scenes = new();
        private readonly int _sceneCount = SceneManager.sceneCount;
        private readonly Collider _actor;
        private readonly string _tag;
        private readonly int _layer;
        private readonly BinaryWriter _definition, _state;
        private readonly BinaryReader _import;
        private readonly bool _source;
        private Action<int> _deepChanged;
        private Action<int, int> _areaChanged;
        private int _depth;
        internal int AreaMask { get; private set; }
        private int _caveArea;
        internal bool InCaves { get; private set; }
        internal bool InDeepCaves { get; private set; }
        internal ulong Revision { get; private set; }

        private sealed class Direction
        {
            internal DirectionalTrigger Native;
            internal bool Inner, Transition;
            internal int A, B, RemoveA, RemoveB, First;
            internal Action<int> ExitA, ExitB;
            internal void Enter(bool a)
            {
                if (a) { A++; if (B > 0) Transition = true; }
                else { B++; if (A > 0) Transition = true; }
                if (First == -1) First = a ? 0 : 1;
            }
            internal void Exit(bool a)
            {
                if (a) RemoveA++; else RemoveB++;
                if (Inner)
                {
                    if (!Transition) return;
                    Transition = false;
                    // Native inner boundary exits dispatch the opposite event.
                    (a ? ExitB : ExitA)(0);
                }
                else if ((a ? B : A) == 0)
                {
                    if (First == (a ? 1 : 0)) (a ? ExitA : ExitB)(0);
                    First = -1;
                }
            }
            internal void Late()
            {
                B = Math.Max(0, B - RemoveB); A = Math.Max(0, A - RemoveA);
                RemoveA = RemoveB = 0;
            }
            // Native ResetTracking clears the six lists, not _inTransitions.
            internal void Reset() { A = B = RemoveA = RemoveB = 0; First = -1; }
        }
        private sealed class Filter
        {
            internal FilterTriggerCollider Native;
            internal bool Tracked;
            internal int Mask;
            internal string Tag;
            internal Action<int> Enter, Exit;
        }
        private sealed class Cave
        {
            internal CaveSystem Native;
            internal readonly List<TriggerProxy> Active = new();
            internal int Due = -1;
            internal Action<int> ForceExit;
        }
        private sealed class Proxy
        {
            internal TriggerProxy Native;
            internal bool Block;
            internal string Tag;
            internal Action<int>[] Calls;
        }
        private sealed class Area
        {
            internal LocalPlayerAreaMaskTrigger Native;
            internal int Count, Previous, ReattachEnters;
        }
        private sealed class Activation
        {
            internal GameObject Target;
            internal bool Active;
            internal WeatherSystemWetnessOverride Override;
            internal bool OverrideEnabled;
            internal GameObject[] Ancestors;
        }

        internal static PlayerExposureGraph Create(Collider actor,
            IReadOnlyList<WeatherSystemWetnessOverride> order, byte[] imported, out byte[] captured)
        {
            if (!Alive(actor)) throw new InvalidOperationException("Exposure has no exact actor collider.");
            using var manifest = new MemoryStream();
            using var state = new MemoryStream();
            using var definitionWriter = new BinaryWriter(manifest, Encoding.UTF8, true);
            using var stateWriter = new BinaryWriter(state, Encoding.UTF8, true);
            using var input = imported == null ? null : new MemoryStream(imported, false);
            using var reader = input == null ? null : new BinaryReader(input, new UTF8Encoding(false, true));
            byte[] expected = null;
            if (reader != null)
            {
                if (imported.Length > MaximumBytes || reader.ReadUInt32() != 0x31475045u)
                    throw new InvalidDataException("Invalid EPG1 exposure graph.");
                int length = reader.ReadInt32();
                if (length < 0 || length > imported.Length - 8) throw new InvalidDataException("Invalid exposure graph length.");
                expected = reader.ReadBytes(length);
            }
            var graph = new PlayerExposureGraph(actor, order, definitionWriter, stateWriter, reader);
            graph.Build();
            definitionWriter.Flush(); stateWriter.Flush();
            if (manifest.Length + state.Length + 8 > MaximumBytes)
                throw new InvalidOperationException("Exposure graph exceeds its transfer bound.");
            byte[] actual = manifest.ToArray();
            if (reader != null && (!actual.AsSpan().SequenceEqual(expected) || input.Position != input.Length))
                throw new InvalidOperationException("Source and destination exposure graph definitions, callback order or exact scene identities differ.");
            using var result = new MemoryStream();
            using var writer = new BinaryWriter(result, Encoding.UTF8, true);
            writer.Write(0x31475045u); writer.Write(actual.Length); writer.Write(actual); writer.Write(state.ToArray());
            captured = imported == null ? result.ToArray() : (byte[])imported.Clone();
            graph.RequireCurrent();
            foreach (var check in graph._validate) check();
            return graph;
        }

        private PlayerExposureGraph(Collider actor, IReadOnlyList<WeatherSystemWetnessOverride> order,
            BinaryWriter definition, BinaryWriter state, BinaryReader import)
        {
            _actor = actor; _definition = definition; _state = state; _import = import; _source = import == null;
            _tag = StateText(_source ? actor.tag : null);
            _layer = State(_source ? actor.gameObject.layer : 0);
            _caveArea = State(_source ? (int)CaveEntranceManager.CurrentAreaMask : 0);
            AreaMask = State(_source ? PlayerLocation.GetLocalPlayerAreaMask() : 0);
            InCaves = Flag(_source && CaveEntranceManager.IsInCaves);
            InDeepCaves = Flag(_source && CaveEntranceManager.IsInDeepCaves);
            if (_layer < 0 || _layer > 31) throw new InvalidDataException("Invalid source exposure collider layer.");
            if (!_source && actor.gameObject.layer != _layer)
                throw new InvalidOperationException("The restored exposure collider does not use the source physics layer.");
            foreach (var value in order) { Pin(value); _order.Add(value); }
        }

        private void Build()
        {
            var directions = Loaded<DirectionalTrigger>();
            foreach (var value in directions)
            {
                Pin(value); Geometry(value._colliderA); Geometry(value._colliderB);
                _definition.Write(value._useInnerBoundary);
                var node = new Direction { Native = value, Inner = value._useInnerBoundary };
                node.A = State(_source ? Count(value._trackedA, _actor.gameObject) : 0);
                node.B = State(_source ? Count(value._trackedB, _actor.gameObject) : 0);
                node.RemoveA = State(_source ? Count(value._toRemoveA, _actor.gameObject) : 0);
                node.RemoveB = State(_source ? Count(value._toRemoveB, _actor.gameObject) : 0);
                int index = _source ? value._trackedObjects.IndexOf(_actor.gameObject) : -1;
                node.First = State(index < 0 ? -1 : (int)value._trackedDirections[index]);
                node.Transition = Flag(_source && value._inTransitions.Contains(_actor.gameObject));
                if (node.A < 0 || node.B < 0 || node.RemoveA < 0 || node.RemoveB < 0 || node.First < -1 || node.First > 1)
                    throw new InvalidDataException("Invalid directional trigger history.");
                _directions.Add(value.Pointer, node);
                var a = value._colliderA; var b = value._colliderB;
                var exitA = value._onExitedAEvent; var exitB = value._onExitedBEvent;
                AddCheck(value, () =>
                {
                    if (value._useInnerBoundary != node.Inner || !Same(value._colliderA, a) || !Same(value._colliderB, b)
                        || !SameDelegate(value._onExitedAEvent, exitA) || !SameDelegate(value._onExitedBEvent, exitB))
                        throw new InvalidOperationException("Directional exposure definition changed.");
                });
            }
            foreach (var value in Loaded<CaveSystem>())
            {
                Pin(value);
                _definition.Write((int)value._areaId);
                var node = new Cave { Native = value };
                int remaining = State(_source && value._requestedCheckLeftExtentsFrame >= 0
                    ? Math.Max(0, value._requestedCheckLeftExtentsFrame - Time.frameCount) : -1);
                if (remaining < -1) throw new InvalidDataException("Invalid cave exit deadline.");
                node.Due = remaining < 0 ? -1 : checked(Time.frameCount + remaining);
                if (value._extents == null) throw new InvalidOperationException("Cave extents are unavailable.");
                _definition.Write(value._extents.Count);
                foreach (var extent in value._extents) Geometry(extent);
                int activeCount = State(_source ? value._activeExtents?.Count ?? 0 : 0);
                if (activeCount < 0 || activeCount > value._extents.Count) throw new InvalidDataException("Invalid active cave extent history.");
                for (int i = 0; i < activeCount; i++)
                {
                    int extentIndex = State(_source ? value._extents.IndexOf(value._activeExtents[i]) : 0);
                    if (extentIndex < 0 || extentIndex >= value._extents.Count || node.Active.Contains(value._extents[extentIndex]))
                        throw new InvalidDataException("Cave extent history does not map to the exact scene definition.");
                    node.Active.Add(value._extents[extentIndex]);
                }
                foreach (var extent in value._extents)
                {
                    bool registered = value._extentsCallbacks != null && value._extentsCallbacks.ContainsKey(extent);
                    bool overlapping = registered && !extent._block
                        && (string.IsNullOrEmpty(extent._tagFilter) || extent._tagFilter == _tag)
                        && HasProxyContact(extent);
                    if (node.Active.Contains(extent) != overlapping)
                        throw new InvalidOperationException("Cave extent history is not attributable to the exact actor collider at the captured pose.");
                }
                _caves.Add(value.Pointer, node);
            }
            _definition.Write(CaveSystem._requestCheckLeftExtentsDelayFrames);
            int delay = CaveSystem._requestCheckLeftExtentsDelayFrames;
            _globalChecks.Add(() =>
            {
                if (CaveSystem._requestCheckLeftExtentsDelayFrames != delay)
                    throw new InvalidOperationException("Native cave extent delay changed.");
            });
            foreach (var value in Loaded<LocalPlayerAreaMaskTrigger>())
            {
                Pin(value); Geometry(value); _definition.Write((int)value._areaMask);
                var node = new Area { Native = value,
                    Count = State(_source ? value._appliedCount : 0), Previous = State(_source ? (int)value._previousMask : 0) };
                if (node.Count < 0) throw new InvalidDataException("Negative area contact count.");
                // The native counter is global to this component. Only import it when
                // its contacts are accounted for by this exact source collider.
                if (node.Count != CountAreaContacts(value))
                    throw new InvalidOperationException("Area trigger contact history is not attributable to the exact source collider.");
                if (!_source) node.ReattachEnters = node.Count;
                _areas.Add(value.Pointer, node);
                int mask = (int)value._areaMask;
                AddCheck(value, () =>
                {
                    if ((int)value._areaMask != mask) throw new InvalidOperationException("Native area trigger definition changed.");
                });
            }
            foreach (var value in directions)
            {
                var node = _directions[value.Pointer];
                node.ExitA = Delegates(value._onExitedAEvent); node.ExitB = Delegates(value._onExitedBEvent);
                AddFilter(value._colliderA); AddFilter(value._colliderB);
            }
            foreach (var node in _caves.Values) node.ForceExit = ForcedCave(node.Native, false);
            foreach (var node in _caves.Values)
                foreach (var extent in node.Native._extents) AddProxy(extent);
            _areaChanged = AreaListeners(CaveEntranceManager._onAreaMaskChanged,
                CaveEntranceManager._onAreaMaskChangedPersistent);
            _deepChanged = Delegates(CaveEntranceManager._onDeepCavesChanged);
            var areaEvent = CaveEntranceManager._onAreaMaskChanged;
            var persistentAreaEvent = CaveEntranceManager._onAreaMaskChangedPersistent;
            var deepEvent = CaveEntranceManager._onDeepCavesChanged;
            _globalChecks.Add(() =>
            {
                if (!SameDelegate(CaveEntranceManager._onAreaMaskChanged, areaEvent)
                    || !SameDelegate(CaveEntranceManager._onAreaMaskChangedPersistent, persistentAreaEvent)
                    || !SameDelegate(CaveEntranceManager._onDeepCavesChanged, deepEvent))
                    throw new InvalidOperationException("Native cave callback registration changed.");
            });
        }

        private void AddFilter(FilterTriggerCollider value)
        {
            if (_filters.ContainsKey(value.Pointer)) return;
            Pin(value);
            var node = new Filter { Native = value, Mask = value._layerMask, Tag = value._tagFilter,
                Tracked = Flag(_source && Count(value._trackedObjects, _actor.gameObject) != 0) };
            _definition.Write(node.Mask); Text(node.Tag ?? "");
            node.Enter = Join(Delegates(value._onEnter), UnityCalls(value._onEnterUnityEvent));
            node.Exit = Join(Delegates(value._onExit), UnityCalls(value._onExitUnityEvent));
            _filters.Add(value.Pointer, node);
            var enter = value._onEnter; var exit = value._onExit;
            AddCheck(value, () =>
            {
                if ((int)value._layerMask != node.Mask || value._tagFilter != node.Tag
                    || !SameDelegate(value._onEnter, enter) || !SameDelegate(value._onExit, exit))
                    throw new InvalidOperationException("Native exposure filter definition changed.");
            });
        }

        internal void FilterContact(FilterTriggerCollider value, int kind)
        {
            if (!_filters.TryGetValue(value.Pointer, out var node))
                throw new InvalidOperationException("Unmapped native exposure filter entered this actor's path.");
            RequireCurrent();
            Check(value);
            if (!string.IsNullOrEmpty(node.Tag) && node.Tag != _tag) return;
            if (kind == 1)
            {
                if (!node.Tracked) return;
                node.Tracked = false; Run(node.Exit, 0);
            }
            else if (!node.Tracked)
            {
                if (node.Mask != 0 && (node.Mask & (1 << _layer)) == 0) return;
                node.Tracked = true; Run(node.Enter, 0);
            }
        }

        internal bool HasFilter(nint pointer) => _filters.ContainsKey(pointer);
        internal bool HasProxy(nint pointer) => _proxies.ContainsKey(pointer);
        internal void ProxyContact(TriggerProxy value, int kind)
        {
            RequireCurrent();
            Check(value);
            if (!_proxies.TryGetValue(value.Pointer, out var node)) throw new InvalidOperationException("Unmapped cave trigger proxy.");
            if (node.Block || (!string.IsNullOrEmpty(node.Tag) && node.Tag != _tag)) return;
            Run(node.Calls[kind], 0);
        }
        internal void AreaContact(LocalPlayerAreaMaskTrigger value, bool enter)
        {
            RequireCurrent();
            Check(value);
            if (!_areas.TryGetValue(value.Pointer, out var node)) throw new InvalidOperationException("Unmapped area trigger.");
            if (_tag != "Player") return;
            if (enter)
            {
                // Unity enters the restored collider's existing overlaps once. They
                // already exist in the imported history and must not increment twice.
                if (node.ReattachEnters > 0) { node.ReattachEnters--; return; }
                if (++node.Count <= 1 && AreaMask != (int)value._areaMask)
                { node.Previous = AreaMask; AreaMask = (int)value._areaMask; Revision++; }
            }
            else if (node.Count != 0 && --node.Count <= 0 && AreaMask == (int)value._areaMask)
            { AreaMask = node.Previous; Revision++; }
        }
        internal void ExtentContact(CaveSystem value, TriggerProxy extent, bool exit)
        {
            RequireCurrent();
            Check(value); Check(extent);
            if (!_caves.TryGetValue(value.Pointer, out var node) || !Alive(extent) || !value._extents.Contains(extent))
                throw new InvalidOperationException("Unmapped cave extent callback.");
            if (exit)
            {
                node.Active.Remove(extent);
                node.Due = checked(Time.frameCount + CaveSystem._requestCheckLeftExtentsDelayFrames);
            }
            else if (!node.Active.Contains(extent)) node.Active.Add(extent);
            Revision++;
        }
        internal void DirectionLate(DirectionalTrigger value)
        {
            if (_directions.TryGetValue(value.Pointer, out var node) && (node.RemoveA != 0 || node.RemoveB != 0))
            { RequireCurrent(); Check(value); node.Late(); }
        }
        internal void CaveLate(CaveSystem value)
        {
            if (!_caves.TryGetValue(value.Pointer, out var node) || node.Due < 0 || Time.frameCount < node.Due) return;
            RequireCurrent();
            Check(value);
            node.Active.RemoveAll(x => !Alive(x));
            if (node.Active.Count == 0) Run(node.ForceExit, 0);
            node.Due = -1;
        }
        internal void RequireCurrent()
        {
            if (!Alive(_actor)) throw new InvalidOperationException("Exposure actor collider retired.");
            if (SceneManager.sceneCount != _sceneCount)
                throw new InvalidOperationException("Loaded scene membership changed; the exposure graph needs a new exact mapping.");
            foreach (var check in _globalChecks) check();
        }
        internal WeatherSystemWetnessOverride Selected()
        {
            RequireCurrent();
            for (int i = _order.Count - 1; i >= 0; i--) if (Alive(_order[i]))
            { Check(_order[i]); return _order[i]; }
            return null;
        }

        private void Run(Action<int> action, int argument)
        {
            if (++_depth > 64) { _depth--; throw new InvalidOperationException("Exposure scene activation cycle."); }
            try { action(argument); Revision++; }
            finally { _depth--; }
        }
        private void SetCave(bool enter, int area)
        {
            if (!enter && (!InCaves || _caveArea != area)) return;
            InCaves = enter;
            int next = enter ? area : 0;
            if (_caveArea != next) _areaChanged(_caveArea, next);
            _caveArea = next; AreaMask = next;
        }
        private void SetDeep(bool deep)
        {
            if (InDeepCaves == deep) return;
            InDeepCaves = deep; _deepChanged(deep ? 1 : 0);
        }
        private int State(int current) { int value = _source ? current : _import.ReadInt32(); _state.Write(value); return value; }
        private bool Flag(bool current)
        {
            int value = _source ? (current ? 1 : 0) : _import.ReadByte();
            if (value > 1) throw new InvalidDataException("Invalid exposure graph flag.");
            _state.Write((byte)value); return value != 0;
        }
        private string StateText(string current)
        {
            string value = _source ? current : _import.ReadString();
            if (value == null || value.Length > 4096) throw new InvalidDataException("Invalid exposure graph text.");
            _state.Write(value); return value;
        }
        private void Text(string text)
        {
            if (text == null || text.Length > 8192) throw new InvalidDataException("Invalid exposure graph identity.");
            _definition.Write(text);
        }
        private static int Count(Il2CppSystem.Collections.Generic.List<GameObject> list, GameObject actor)
        {
            if (list == null || list.WasCollected) throw new InvalidOperationException("Native exposure contact history is missing.");
            int count = 0;
            foreach (var item in list) if (Same(item, actor)) count++;
            return count;
        }
        private int CountAreaContacts(LocalPlayerAreaMaskTrigger value)
        {
            if (_tag != "Player" || !value.isActiveAndEnabled) return 0;
            int count = 0;
            foreach (var collider in value.GetComponents<Collider>())
                if (HasContact(collider)) count++;
            return count;
        }
        private bool HasProxyContact(TriggerProxy proxy)
        {
            foreach (var collider in proxy.GetColliders()) if (HasContact(collider)) return true;
            return false;
        }
        private bool HasContact(Collider collider)
            => Alive(collider) && collider.enabled && collider.isTrigger && collider.gameObject.activeInHierarchy
                && !Physics.GetIgnoreLayerCollision(_layer, collider.gameObject.layer) && !Physics.GetIgnoreCollision(_actor, collider)
                && Physics.ComputePenetration(_actor, _actor.transform.position, _actor.transform.rotation,
                    collider, collider.transform.position, collider.transform.rotation, out _, out _);
        private T[] Loaded<T>() where T : Component
        {
            var result = new List<T>();
            foreach (var value in Resources.FindObjectsOfTypeAll<T>())
                if (Alive(value) && value.gameObject.scene.IsValid() && value.gameObject.scene.isLoaded) result.Add(value);
            if (result.Count > 2048) throw new InvalidOperationException("Exposure scene graph exceeds its node bound.");
            result.Sort((a, b) => StringComparer.Ordinal.Compare(Key(a), Key(b)));
            _definition.Write(typeof(T).FullName); _definition.Write(result.Count);
            return result.ToArray();
        }
        private static string Key(Component value)
        {
            if (!Alive(value)) throw new InvalidOperationException("Exposure graph references a missing scene component.");
            var scene = value.gameObject.scene;
            if (!scene.IsValid() || !scene.isLoaded || string.IsNullOrEmpty(scene.path))
                throw new InvalidOperationException("Exposure graph has no loaded scene identity.");
            var names = new Stack<string>();
            for (var t = value.transform; Alive(t); t = t.parent)
            {
                if (names.Count == 128) throw new InvalidOperationException("Exposure graph hierarchy is too deep.");
                names.Push(t.name);
            }
            var key = new StringBuilder(scene.path.Length + 64).Append(scene.path.Length).Append(':').Append(scene.path);
            foreach (string name in names) key.Append(name.Length).Append(':').Append(name);
            var all = value.GetComponents<Component>();
            for (int i = 0; i < all.Length; i++) if (Same(value, all[i]))
                return key.Append('#').Append(i).ToString();
            throw new InvalidOperationException("Exposure graph component identity was lost.");
        }
        private void Pin(Component value)
        {
            string key = Key(value); Text(key); Text(value.GetIl2CppType().FullName);
            if (_identities.TryGetValue(key, out var previous))
            {
                if (previous != value.Pointer) throw new InvalidOperationException("Ambiguous exposure scene identity.");
                return;
            }
            _identities.Add(key, value.Pointer);
            _checks.Add(value.Pointer, new List<Action>());
            int instance = value.GetInstanceID(), scene = value.gameObject.scene.handle;
            if (_scenes.Add(scene))
            {
                var capturedScene = value.gameObject.scene;
                _globalChecks.Add(() =>
                {
                    if (!capturedScene.IsValid() || !capturedScene.isLoaded)
                        throw new InvalidOperationException("An exposure graph scene was unloaded.");
                });
            }
            var behaviour = value.TryCast<Behaviour>();
            if (value.TryCast<WeatherSystemWetnessOverride>() is { } weatherOverride)
            {
                float wetness = weatherOverride._wetnessOverride, rain = weatherOverride._rainOverride;
                _definition.Write(wetness); _definition.Write(rain);
                AddCheck(value, () =>
                {
                    if (!Alive(weatherOverride) || weatherOverride._wetnessOverride != wetness || weatherOverride._rainOverride != rain)
                        throw new InvalidOperationException("Native weather override definition changed.");
                });
                behaviour = null; // Host-local activation never selects this actor's override.
            }
            bool active = behaviour != null && behaviour.isActiveAndEnabled;
            _definition.Write(active);
            AddCheck(value, () =>
            {
                if (!Alive(value) || value.GetInstanceID() != instance || value.gameObject.scene.handle != scene
                    || !value.gameObject.scene.isLoaded || (behaviour != null && behaviour.isActiveAndEnabled != active))
                    throw new InvalidOperationException("Exposure scene producer lifetime or activation changed; captured graph must be retired.");
            });
        }
        private void Geometry(Component value)
        {
            Pin(value);
            var colliders = value.GetComponents<Collider>();
            if (value.TryCast<TriggerProxy>() is { } proxy)
            {
                var owned = proxy.GetColliders();
                if (owned == null) throw new InvalidOperationException("Cave extent geometry is missing.");
                _definition.Write(owned.Count);
                foreach (var collider in owned)
                { ColliderDefinition(collider); var captured = collider; AddCheck(value, () => Check(captured)); }
            }
            else
            {
                _definition.Write(colliders.Length);
                foreach (var collider in colliders)
                { ColliderDefinition(collider); var captured = collider; AddCheck(value, () => Check(captured)); }
            }
        }
        private void ColliderDefinition(Collider value)
        {
            Pin(value);
            for (int i = 0; i < 16; i++) _definition.Write(value.transform.localToWorldMatrix[i]);
            _definition.Write(value.isTrigger); _definition.Write(value.enabled); _definition.Write(value.gameObject.layer);
            if (value.TryCast<BoxCollider>() is { } box)
            {
                var center = box.center; var size = box.size; Vector(center); Vector(size);
                AddCheck(value, () =>
                {
                    if (!box.center.Equals(center) || !box.size.Equals(size)) throw new InvalidOperationException("Exposure box shape changed.");
                });
            }
            else if (value.TryCast<SphereCollider>() is { } sphere)
            {
                var center = sphere.center; float radius = sphere.radius; Vector(center); _definition.Write(radius);
                AddCheck(value, () =>
                {
                    if (!sphere.center.Equals(center) || sphere.radius != radius) throw new InvalidOperationException("Exposure sphere shape changed.");
                });
            }
            else if (value.TryCast<CapsuleCollider>() is { } capsule)
            {
                var center = capsule.center; float radius = capsule.radius, height = capsule.height; int direction = capsule.direction;
                Vector(center); _definition.Write(radius); _definition.Write(height); _definition.Write(direction);
                AddCheck(value, () =>
                {
                    if (!capsule.center.Equals(center) || capsule.radius != radius || capsule.height != height || capsule.direction != direction)
                        throw new InvalidOperationException("Exposure capsule shape changed.");
                });
            }
            else if (value.TryCast<MeshCollider>() is { } meshCollider)
            {
                var mesh = meshCollider.sharedMesh;
                if (!Alive(mesh) || !mesh.isReadable)
                    throw new InvalidOperationException("Exposure mesh collider has no readable exact source/destination geometry.");
                _definition.Write(meshCollider.convex); _definition.Write((int)meshCollider.cookingOptions);
                var vertices = mesh.vertices; var triangles = mesh.triangles;
                if (vertices == null || triangles == null || vertices.Length > 65536 || triangles.Length > 262144)
                    throw new InvalidOperationException("Exposure mesh geometry exceeds its mapping bound.");
                _definition.Write(vertices.Length);
                _definition.Write(triangles.Length);
                using var geometry = new MemoryStream();
                using (var writer = new BinaryWriter(geometry, Encoding.UTF8, true))
                {
                    foreach (var vertex in vertices)
                    {
                        if (!float.IsFinite(vertex.x) || !float.IsFinite(vertex.y) || !float.IsFinite(vertex.z))
                            throw new InvalidOperationException("Nonfinite exposure mesh vertex.");
                        writer.Write(vertex.x); writer.Write(vertex.y); writer.Write(vertex.z);
                    }
                    foreach (int index in triangles) writer.Write(index);
                }
                _definition.Write(SHA256.HashData(geometry.GetBuffer().AsSpan(0, checked((int)geometry.Length))));
                bool convex = meshCollider.convex; var cooking = meshCollider.cookingOptions;
                AddCheck(value, () =>
                {
                    if (!Alive(meshCollider) || !Same(meshCollider.sharedMesh, mesh) || meshCollider.convex != convex || meshCollider.cookingOptions != cooking)
                        throw new InvalidOperationException("Exposure mesh identity changed.");
                });
            }
            else throw new InvalidOperationException("Exposure scene collider has no exact supported shape mapping: " + value.GetIl2CppType().FullName);
            Matrix4x4 matrix = value.transform.localToWorldMatrix;
            bool enabled = value.enabled, trigger = value.isTrigger; int layer = value.gameObject.layer;
            AddCheck(value, () =>
            {
                if (!Alive(value) || value.enabled != enabled || value.isTrigger != trigger || value.gameObject.layer != layer
                    || !value.transform.localToWorldMatrix.Equals(matrix))
                    throw new InvalidOperationException("Exposure trigger geometry changed during actor continuation.");
            });
        }
        private void Vector(Vector3 value)
        {
            if (!float.IsFinite(value.x) || !float.IsFinite(value.y) || !float.IsFinite(value.z))
                throw new InvalidOperationException("Nonfinite exposure geometry.");
            _definition.Write(value.x); _definition.Write(value.y); _definition.Write(value.z);
        }
        private void AddCheck(Component value, Action check)
        { _checks[value.Pointer].Add(check); _validate.Add(check); }
        private void Check(Component value)
        {
            if (!Alive(value) || !_checks.TryGetValue(value.Pointer, out var checks))
                throw new InvalidOperationException("Unmapped or retired exposure graph component.");
            foreach (var check in checks) check();
        }
        private static bool SameDelegate(Il2CppSystem.Delegate left, Il2CppSystem.Delegate right)
            => left == null ? right == null : right != null && !left.WasCollected && !right.WasCollected && left.Pointer == right.Pointer;
    }
}
