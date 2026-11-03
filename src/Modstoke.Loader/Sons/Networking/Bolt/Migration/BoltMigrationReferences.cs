// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Bolt;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppInterop.Runtime.Runtime;
using Modstoke.Loader;
using UdpKit;

namespace Modstoke.Sons
{
    /// <summary>An explicitly registered root-state property, including nested/array offsets.</summary>
    public readonly record struct BoltMigrationReferenceSlot(
        ulong HolderNetworkId, int StateTypeId, int PropertyIndex);

    /// <summary>The exact native property class, not an inference about a NetworkId's meaning.</summary>
    public enum BoltMigrationReferenceKind { Entity, NetworkId }

    /// <summary>
    /// Repairs only registered, locally owned Bolt Entity/NetworkId properties. Unity cached
    /// references and parenting remain the entity adapter's responsibility.
    /// </summary>
    public static class BoltMigrationReferences
    {
        [ThreadStatic] private static bool _busy;
        [ThreadStatic] private static bool _reentered;

        public static bool TryCapture(ulong oldNetworkId,
            IReadOnlyList<BoltMigrationReferenceSlot> slots,
            out IBoltMigrationReferenceRepair repair, out string detail)
        {
            repair = null;
            detail = "Reference capture requires the initialized, selected enrollment game thread.";
            if (!OnSelectedThread()) return false;
            if (_busy) { _reentered = true; detail = "Reference capture cannot re-enter repair."; return false; }
            _busy = true;
            _reentered = false;
            try
            {
                return TryCaptureSlots(oldNetworkId, slots, out repair, out detail);
            }
            catch (Exception error)
            {
                detail = "Reference capture rejected: " + error.GetType().Name + ".";
                return false;
            }
            finally { _busy = false; }
        }

        private static bool TryCaptureSlots(ulong oldNetworkId,
            IReadOnlyList<BoltMigrationReferenceSlot> slots,
            out IBoltMigrationReferenceRepair repair, out string detail)
        {
            repair = null;
            if (oldNetworkId == 0 || slots == null)
            { detail = "A nonzero old ID and an explicit slot list are required."; return false; }
            if (!Supported())
            { detail = "Reference repair requires the recognized, Harmony-safe game build."; return false; }
            RequireLayout();
            var run = new RunBinding();
            if (!run.Matches())
            { detail = "Bolt is not running in a stable network role."; return false; }

            var unique = new HashSet<BoltMigrationReferenceSlot>();
            var bindings = new List<SlotBinding>();
            var views = new List<StorageView>();
            var addresses = new HashSet<(IntPtr, int)>();
            int count = slots.Count;
            for (int i = 0; i < count; i++)
            {
                var slot = slots[i];
                if (!unique.Add(slot)) continue;
                if (slot.HolderNetworkId == 0 || slot.HolderNetworkId == oldNetworkId
                    || slot.PropertyIndex < 0 || slot.PropertyIndex >= 1024)
                { detail = "A reference slot has an invalid holder or root property index."; return false; }
                var binding = SlotBinding.Capture(slot);
                var view = binding.CurrentStorage();
                if (!view.Matches(oldNetworkId) || !addresses.Add((view.Values.Pointer, binding.ValueIndex)))
                { detail = "A reference slot does not contain the old ID, or aliases another slot."; return false; }
                bindings.Add(binding);
                views.Add(view);
            }
            if (slots.Count != count || !run.Matches() || _reentered)
            { detail = "The reference capture context changed."; return false; }
            foreach (var view in views)
                if (!view.Matches(oldNetworkId) || _reentered)
                { detail = "A reference binding changed during capture."; return false; }
            if (!run.Matches() || _reentered)
            { detail = "The network role changed during reference capture."; return false; }

            repair = new Repair(oldNetworkId, run, bindings.ToArray());
            detail = bindings.Count == 0 ? "Captured zero explicit reference slots."
                : "Captured " + bindings.Count + " explicit Bolt reference slots.";
            return true;
        }

        /// <summary>
        /// Discovers matching Entity/NetworkId fields in loaded, attached, exact-registry, locally
        /// owned Bolt state, excluding the old entity. Every match requires explicit semantic
        /// approval, including NetworkId fields. The policy must be read-only and non-reentrant.
        /// Rejects snapshots exceeding 4096 loaded BoltEntities or 1000000 examined properties.
        /// The returned single-use repair rescans once before writing; it does not scan per frame
        /// or cover the full Unity reference graph, cached references, or parenting.
        /// </summary>
        public static bool TryCaptureLoadedReferences(ulong oldNetworkId,
            Func<BoltMigrationReferenceSlot, string, BoltMigrationReferenceKind, bool> policy,
            out IBoltMigrationReferenceRepair repair, out string detail)
        {
            repair = null;
            detail = "Loaded reference capture requires the selected enrollment game thread.";
            if (!OnSelectedThread()) return false;
            if (_busy) { _reentered = true; detail = "Loaded reference capture cannot re-enter capture or repair."; return false; }
            _busy = true;
            _reentered = false;
            try
            {
                if (oldNetworkId == 0 || policy == null)
                { detail = "A nonzero old ID and an explicit reference policy are required."; return false; }
                if (!Supported())
                { detail = "Loaded reference capture requires the recognized, Harmony-safe game build."; return false; }
                RequireLayout();
                var run = new RunBinding();
                if (!run.Matches())
                { detail = "Bolt is not running in a stable network role."; return false; }
                var oldEntity = BoltNetwork.FindEntity(new NetworkId(oldNetworkId));
                var oldBinding = oldEntity is null ? null : new EntityBinding(oldEntity, oldNetworkId);
                var discovered = DiscoverLoadedReferences(oldNetworkId);
                var slots = new List<BoltMigrationReferenceSlot>(discovered.Length);
                foreach (var reference in discovered)
                {
                    if (!CaptureContextMatches() || !reference.Matches(oldNetworkId))
                    { detail = "The loaded reference capture context changed before policy approval."; return false; }
                    bool approved = policy(reference.Binding.Slot, reference.Name, reference.Kind);
                    if (!CaptureContextMatches() || !reference.Matches(oldNetworkId))
                    { detail = "The policy changed or re-entered the loaded reference capture context."; return false; }
                    if (!approved)
                    { detail = "The policy did not approve a matching loaded Bolt reference: " + reference.Binding.Slot + "."; return false; }
                    slots.Add(reference.Binding.Slot);
                }
                foreach (var reference in discovered)
                    if (!reference.Matches(oldNetworkId))
                    { detail = "A loaded reference changed during policy approval."; return false; }
                if (!CaptureContextMatches())
                { detail = "The old entity or network run changed during loaded reference capture."; return false; }
                if (!TryCaptureSlots(oldNetworkId, slots, out var captured, out detail)) return false;
                if (!CaptureContextMatches())
                { detail = "The old entity or network run changed during explicit slot validation."; return false; }
                repair = new LoadedRepair((Repair)captured, new LoadedCapture(oldNetworkId, discovered));
                detail = "Captured " + discovered.Length + " policy-approved loaded Bolt references; Unity references are not covered.";
                return true;

                bool CaptureContextMatches() => !_reentered && run.Matches()
                    && (oldBinding == null ? BoltNetwork.FindEntity(new NetworkId(oldNetworkId)) is null : oldBinding.Matches())
                    && !_reentered;
            }
            catch (Exception error)
            {
                detail = "Loaded reference capture rejected: " + error.GetType().Name + ": " + error.Message;
                return false;
            }
            finally { _busy = false; }
        }

        private static unsafe LoadedReference[] DiscoverLoadedReferences(ulong oldId)
        {
            var entities = UnityEngine.Resources.FindObjectsOfTypeAll<BoltEntity>();
            if (entities == null || entities.WasCollected || entities.Length > 4096)
                throw new InvalidOperationException("Loaded Bolt entity snapshot is unavailable or exceeds 4096 entities.");
            var found = new List<LoadedReference>();
            var holders = new HashSet<ulong>();
            int propertyCount = 0;
            foreach (var entity in entities)
            {
                if (entity is null || entity.WasCollected || !entity || !entity.isAttached || !entity.isOwner) continue;
                ulong id = entity.networkId.PackedValue;
                if (id == 0 || id == oldId) continue;
                var registered = BoltNetwork.FindEntity(new NetworkId(id));
                if (registered is null || registered.WasCollected || !registered || registered.Pointer != entity.Pointer) continue;
                var holder = new EntityBinding(entity, id);
                if (!holder.Matches() || !holder.Backing.IsOwner)
                    throw new InvalidOperationException("Loaded reference holder changed during discovery.");
                if (!holders.Add(id)) throw new InvalidOperationException("Duplicate loaded reference holder.");
                var state = holder.Backing.Serializer?.TryCast<NetworkState>();
                if (state == null || state.WasCollected)
                    throw new InvalidOperationException("Loaded reference holder has no state.");
                var meta = state.Meta;
                if (meta == null || meta.WasCollected)
                    throw new InvalidOperationException("Loaded reference holder has no root metadata.");
                var properties = meta.Properties;
                if (properties == null || properties.WasCollected || properties.Length > 1000000 - propertyCount)
                    throw new InvalidOperationException("Root properties are unavailable or exceed the 1000000 property budget.");
                propertyCount += properties.Length;
                int typeId = meta.TypeId.Value;
                for (int index = 0; index < properties.Length; index++)
                {
                    // The inline descriptor contains native references; do not box it.
                    byte* info = (byte*)properties.Pointer + 32 + 32L * index;
                    IntPtr pointer = *(IntPtr*)(info + 24);
                    if (pointer == IntPtr.Zero)
                        throw new InvalidOperationException("Missing loaded root property descriptor.");
                    if (!IsReferenceClass(IL2CPP.il2cpp_object_get_class(pointer))) continue;
                    var binding = SlotBinding.Capture(new(id, typeId, index));
                    var view = binding.CurrentStorage();
                    if (!binding.MatchesStorage(view))
                        throw new InvalidOperationException("Loaded reference storage changed during discovery.");
                    if (view.Matches(oldId)) found.Add(new LoadedReference(binding));
                }
                if (!holder.Matches() || state.Entity?.Pointer != holder.Backing.Pointer
                    || state.Meta?.Pointer != meta.Pointer || meta.Properties?.Pointer != properties.Pointer
                    || _reentered)
                    throw new InvalidOperationException("Loaded reference metadata changed during discovery.");
            }
            GC.KeepAlive(entities);
            return found.ToArray();
        }

        private sealed class LoadedReference
        {
            internal readonly SlotBinding Binding;
            internal readonly string Name;
            internal readonly BoltMigrationReferenceKind Kind;

            internal LoadedReference(SlotBinding binding)
            { Binding = binding; Name = binding.PropertyName; Kind = binding.Kind; }

            internal bool Matches(ulong oldId) => Binding.CurrentStorage().Matches(oldId)
                && string.Equals(Binding.PropertyName, Name, StringComparison.Ordinal) && Binding.Kind == Kind;
        }

        private sealed class LoadedCapture
        {
            private readonly ulong _oldId;
            private readonly LoadedReference[] _references;

            internal LoadedCapture(ulong oldId, LoadedReference[] references)
            { _oldId = oldId; _references = references; }

            internal bool MatchesCurrent()
            {
                var current = DiscoverLoadedReferences(_oldId);
                if (current.Length != _references.Length || _reentered) return false;
                var membership = new HashSet<BoltMigrationReferenceSlot>();
                foreach (var reference in current)
                    if (!membership.Add(reference.Binding.Slot)) return false;
                foreach (var reference in _references)
                    if (!membership.Remove(reference.Binding.Slot) || !reference.Matches(_oldId) || _reentered) return false;
                return membership.Count == 0 && !_reentered;
            }
        }

        private sealed class LoadedRepair : IBoltMigrationReferenceRepair
        {
            private readonly Repair _repair;
            private readonly LoadedCapture _capture;

            internal LoadedRepair(Repair repair, LoadedCapture capture)
            { _repair = repair; _capture = capture; }

            public BoltMigrationMutationResult TryRepair(BoltEntity replacement, out string detail)
                => _repair.TryRepair(replacement, _capture, out detail);
        }

        // A null peer performs only managed enrollment checks, including initialization and
        // thread affinity. Do not touch any native class or Unity object before this guard.
        private static bool OnSelectedThread() => ClientEnrollment.TryRead(
            (ControlPeer)null, out _) == EnrollmentReadStatus.PeerUnavailable;

        private static bool Supported() => HarmonySupport.CreateState()?.SafeForHarmonyPatches == true
            && GameBuild.IsRecognized;

        private static void RequireLayout()
        {
            BoltProperties.RequireLayout();
            var field = IL2CPP.il2cpp_class_get_field_from_name(
                Il2CppClassPointerStore<NetworkValue>.NativeClassPtr, "NetworkId");
            if (field == IntPtr.Zero || IL2CPP.il2cpp_field_get_offset(field) != 16)
                throw new InvalidOperationException("Unsupported NetworkValue.NetworkId layout.");
            RuntimeHelpers.RunClassConstructor(typeof(NetworkProperty_Entity).TypeHandle);
            RuntimeHelpers.RunClassConstructor(typeof(NetworkProperty_NetworkId).TypeHandle);
        }

        private static bool IsReferenceClass(IntPtr type) => type != IntPtr.Zero
            && (type == Il2CppClassPointerStore<NetworkProperty_Entity>.NativeClassPtr
                || type == Il2CppClassPointerStore<NetworkProperty_NetworkId>.NativeClassPtr);

        private sealed class Repair : IBoltMigrationReferenceRepair
        {
            private readonly int _thread = Environment.CurrentManagedThreadId;
            private readonly ulong _oldId;
            private readonly RunBinding _run;
            private readonly SlotBinding[] _slots;
            private bool _used;
            private bool _hasIdentity;
            private IntPtr _replacementPointer;
            private ulong _replacementId;
            private BoltMigrationMutationResult _result;
            private string _detail;

            internal Repair(ulong oldId, RunBinding run, SlotBinding[] slots)
            { _oldId = oldId; _run = run; _slots = slots; }

            public BoltMigrationMutationResult TryRepair(BoltEntity replacement, out string detail)
                => TryRepair(replacement, null, out detail);

            internal BoltMigrationMutationResult TryRepair(BoltEntity replacement,
                LoadedCapture loaded, out string detail)
            {
                detail = "Reference repair requires its captured enrollment game thread.";
                if (Environment.CurrentManagedThreadId != _thread || !OnSelectedThread())
                    return BoltMigrationMutationResult.Rejected;
                if (_busy)
                {
                    _reentered = true;
                    detail = "Reference repair cannot re-enter capture or repair.";
                    return BoltMigrationMutationResult.Rejected;
                }
                _busy = true;
                _reentered = false;
                bool attempted = false;
                bool repeat = _used;
                StorageView[] views = null;
                EntityBinding newBinding = null;
                try
                {
                    if (repeat)
                    {
                        if (_hasIdentity && Supported() && replacement is not null && !replacement.WasCollected
                            && replacement.Pointer == _replacementPointer
                            && replacement.networkId.PackedValue == _replacementId && !_reentered)
                        { detail = _detail; return _result; }
                        detail = "This repair was already consumed by another replacement or unavailable identity.";
                        return BoltMigrationMutationResult.Rejected;
                    }
                    _used = true;
                    if (!_run.Matches() || replacement is null || replacement.WasCollected || !replacement
                        || !replacement.isAttached)
                        return Finish(BoltMigrationMutationResult.Rejected,
                            "The replacement or captured network run is unavailable.", out detail);
                    _replacementPointer = replacement.Pointer;
                    _replacementId = replacement.networkId.PackedValue;
                    _hasIdentity = true;
                    if (_replacementId == 0 || _replacementId == _oldId)
                        return Finish(BoltMigrationMutationResult.Rejected,
                            "The replacement must have a different nonzero network ID.", out detail);
                    newBinding = new EntityBinding(replacement, _replacementId);
                    if (!newBinding.Matches())
                        return Finish(BoltMigrationMutationResult.Rejected,
                            "The replacement is not its exact live registry binding.", out detail);

                    // Run inside the same single-use/reentrancy guard, never on cached retries.
                    if (loaded != null && !loaded.MatchesCurrent())
                        return Finish(BoltMigrationMutationResult.Rejected,
                            "Loaded reference membership or a captured native holder changed before repair.", out detail);

                    views = new StorageView[_slots.Length];
                    var addresses = new HashSet<(IntPtr, int)>();
                    for (int i = 0; i < _slots.Length; i++)
                    {
                        views[i] = _slots[i].CurrentStorage();
                        if (!views[i].Matches(_oldId)
                            || !addresses.Add((views[i].Values.Pointer, _slots[i].ValueIndex)))
                            return Finish(BoltMigrationMutationResult.Rejected,
                                "A registered reference diverged or its storage binding changed.", out detail);
                    }
                    // Freeze and validate every current destination before the first mutation.
                    if (!_run.Matches() || !newBinding.Matches() || _reentered)
                        return Finish(BoltMigrationMutationResult.Rejected,
                            "The replacement or network role changed before repair.", out detail);
                    foreach (var view in views)
                        if (!view.Matches(_oldId) || _reentered)
                            return Finish(BoltMigrationMutationResult.Rejected,
                                "A registered reference changed before repair.", out detail);

                    foreach (var view in views)
                    {
                        if (!_run.Matches() || !newBinding.Matches() || !view.Matches(_oldId) || _reentered)
                            return Finish(attempted ? BoltMigrationMutationResult.Unconfirmed
                                : BoltMigrationMutationResult.Rejected,
                                "A binding changed before a reference write; repair stopped.", out detail);
                        attempted = true;
                        view.Write(_replacementId);
                        if (!_run.Matches() || !newBinding.Matches() || !view.Matches(_replacementId) || _reentered)
                            return Finish(BoltMigrationMutationResult.Unconfirmed,
                                "A binding changed after a reference write; no rollback attempted.", out detail);
                        // PropertyChanged sets this root bit without clearing any pending changes.
                        // Do not invoke callbacks or SetDynamic(Entity), which compares Unity liveness.
                        view.Storage.PropertyChanged(view.Binding.Slot.PropertyIndex);
                        if (!_run.Matches() || !newBinding.Matches() || !view.Matches(_replacementId)
                            || !view.Storage.IsSet(view.Binding.Slot.PropertyIndex) || _reentered)
                            return Finish(BoltMigrationMutationResult.Unconfirmed,
                                "Reference notification could not be confirmed; repair stopped.", out detail);
                    }
                    foreach (var view in views)
                        if (!view.Matches(_replacementId) || !view.Storage.IsSet(view.Binding.Slot.PropertyIndex) || _reentered)
                            return Finish(BoltMigrationMutationResult.Unconfirmed,
                                "A repaired reference changed before completion.", out detail);
                    if (!_run.Matches() || !newBinding.Matches() || _reentered)
                        return Finish(attempted ? BoltMigrationMutationResult.Unconfirmed
                            : BoltMigrationMutationResult.Rejected,
                            "The replacement or network role changed before completion.", out detail);
                    return Finish(views.Length == 0 ? BoltMigrationMutationResult.Unchanged
                        : BoltMigrationMutationResult.Applied,
                        views.Length == 0 ? "No explicit reference slots were registered."
                            : "Registered Bolt references repaired; Unity cached references and parenting are not covered.",
                        out detail);
                }
                catch (Exception error)
                {
                    detail = "Reference repair stopped: " + error.GetType().Name + ".";
                    if (repeat) return BoltMigrationMutationResult.Rejected;
                    return Finish(attempted ? BoltMigrationMutationResult.Unconfirmed
                        : BoltMigrationMutationResult.Rejected, detail, out detail);
                }
                finally
                {
                    _busy = false;
                    GC.KeepAlive(views); GC.KeepAlive(newBinding); GC.KeepAlive(this);
                }
            }

            private BoltMigrationMutationResult Finish(BoltMigrationMutationResult result,
                string message, out string detail)
            { _result = result; _detail = detail = message; return result; }
        }

        private sealed class RunBinding
        {
            private readonly int _thread = Environment.CurrentManagedThreadId;
            private readonly UdpSocket _socket = BoltCore._udpSocket;
            private readonly bool _server = BoltNetwork.isServer;
            private readonly bool _client = BoltNetwork.isClient;

            internal bool Matches() => Environment.CurrentManagedThreadId == _thread && OnSelectedThread()
                && Supported() && BoltNetwork.isRunning && (_server || _client)
                && BoltNetwork.isServer == _server && BoltNetwork.isClient == _client
                && _socket != null && !_socket.WasCollected && _socket.State != UdpSocketState.Shutdown
                && BoltCore._udpSocket?.Pointer == _socket.Pointer;
        }

        private sealed class EntityBinding
        {
            internal readonly BoltEntity UnityEntity;
            internal readonly Entity Backing;
            private readonly ulong _id;
            private readonly bool _owner;
            private readonly bool _control;
            private readonly IntPtr _serializer;
            private readonly ConnectionBinding _source;
            private readonly ConnectionBinding _controller;

            internal EntityBinding(BoltEntity entity, ulong id)
            {
                UnityEntity = entity; _id = id;
                if (entity is null || entity.WasCollected || !entity || !entity.isAttached)
                    throw new InvalidOperationException("Missing live attached entity.");
                Backing = entity._entity;
                if (Backing == null || Backing.WasCollected) throw new InvalidOperationException("Missing entity backing.");
                _owner = Backing.IsOwner; _control = Backing.HasControl;
                _serializer = Backing.Serializer?.Pointer ?? IntPtr.Zero;
                _source = new ConnectionBinding(Backing.Source);
                _controller = new ConnectionBinding(Backing.Controller);
            }

            internal bool Matches()
            {
                var registered = BoltNetwork.FindEntity(new NetworkId(_id));
                return registered is not null && !registered.WasCollected && registered
                    && registered.Pointer == UnityEntity.Pointer && !UnityEntity.WasCollected && UnityEntity
                    && UnityEntity.isAttached && !Backing.WasCollected && Backing.IsAttached
                    && UnityEntity._entity?.Pointer == Backing.Pointer && Backing.UnityObject?.Pointer == UnityEntity.Pointer
                    && UnityEntity.networkId.PackedValue == _id && Backing.NetworkId.PackedValue == _id
                    && UnityEntity.isOwner == _owner && Backing.IsOwner == _owner
                    && Backing.HasControl == _control
                    && (Backing.Serializer?.Pointer ?? IntPtr.Zero) == _serializer
                    && (Backing.Source?.Pointer ?? IntPtr.Zero) == _source.Pointer
                    && (Backing.Controller?.Pointer ?? IntPtr.Zero) == _controller.Pointer
                    && (!_owner || _source.Pointer == IntPtr.Zero) && _source.Matches() && _controller.Matches();
            }
        }

        private sealed class SlotBinding
        {
            internal readonly BoltMigrationReferenceSlot Slot;
            internal readonly int ValueIndex;
            internal string PropertyName => _property.PropertyName;
            internal BoltMigrationReferenceKind Kind =>
                _propertyClass == Il2CppClassPointerStore<NetworkProperty_Entity>.NativeClassPtr
                    ? BoltMigrationReferenceKind.Entity : BoltMigrationReferenceKind.NetworkId;
            private readonly EntityBinding _holder;
            private readonly NetworkState _state;
            private readonly NetworkObj_Meta _meta;
            private readonly Il2CppReferenceArray<NetworkPropertyInfo> _properties;
            private readonly Il2CppSystem.Collections.Generic.List<NetworkObj> _objects;
            private readonly NetworkProperty _property;
            private readonly NetworkObj _object;
            private readonly NetworkObj_Meta _objectMeta;
            private readonly NetworkObj_Meta _propertyMeta;
            private readonly IntPtr _stateClass;
            private readonly IntPtr _metaClass;
            private readonly IntPtr _objectClass;
            private readonly IntPtr _propertyClass;
            private readonly int _objectIndex;
            private readonly int _objectStorage;
            private readonly int _objectProperties;
            private readonly int _objectObjects;
            private readonly int _propertyStorage;
            private readonly int _propertyProperties;

            private SlotBinding(BoltMigrationReferenceSlot slot, EntityBinding holder,
                NetworkState state, NetworkObj_Meta meta, Il2CppReferenceArray<NetworkPropertyInfo> properties,
                Il2CppSystem.Collections.Generic.List<NetworkObj> objects, int objectIndex,
                NetworkProperty property, NetworkObj obj)
            {
                Slot = slot; _holder = holder; _state = state; _meta = meta;
                _properties = properties; _objects = objects; _objectIndex = objectIndex;
                _property = property; _object = obj; _objectMeta = obj.Meta; _propertyMeta = property.PropertyMeta;
                _stateClass = IL2CPP.il2cpp_object_get_class(state.Pointer);
                _metaClass = IL2CPP.il2cpp_object_get_class(meta.Pointer);
                _objectClass = IL2CPP.il2cpp_object_get_class(obj.Pointer);
                _propertyClass = IL2CPP.il2cpp_object_get_class(property.Pointer);
                _objectStorage = obj.OffsetStorage; _objectProperties = obj.OffsetProperties;
                _objectObjects = obj.OffsetObjects;
                _propertyStorage = property.OffsetStorage; _propertyProperties = property.OffsetProperties;
                ValueIndex = checked(_objectStorage + _propertyStorage);
            }

            internal static unsafe SlotBinding Capture(BoltMigrationReferenceSlot slot)
            {
                var entity = BoltNetwork.FindEntity(new NetworkId(slot.HolderNetworkId));
                var holder = new EntityBinding(entity, slot.HolderNetworkId);
                if (!holder.Matches() || !holder.Backing.IsOwner)
                    throw new InvalidOperationException("Reference holder is not locally owned.");
                var state = holder.Backing.Serializer?.TryCast<NetworkState>();
                if (state == null || state.WasCollected) throw new InvalidOperationException("Missing holder state.");
                var meta = state.Meta;
                if (meta == null || meta.WasCollected || meta.TypeId.Value != slot.StateTypeId)
                    throw new InvalidOperationException("Holder state type changed.");
                var properties = meta.Properties;
                var objects = state.RootObjects;
                if (properties == null || properties.WasCollected || objects == null || objects.WasCollected
                    || (uint)slot.PropertyIndex >= (uint)properties.Length)
                    throw new InvalidOperationException("Missing root property metadata.");
                // Read the inline descriptor, never its reference-containing boxing indexer.
                byte* info = (byte*)properties.Pointer + 32 + 32L * slot.PropertyIndex;
                int objectIndex = *(int*)(info + 16);
                IntPtr pointer = *(IntPtr*)(info + 24);
                if (pointer == IntPtr.Zero || (uint)objectIndex >= (uint)objects.Count)
                    throw new InvalidOperationException("Invalid root property descriptor.");
                var property = Il2CppObjectPool.Get<NetworkProperty>(pointer);
                var obj = objects[objectIndex];
                if (property == null || property.WasCollected || obj == null || obj.WasCollected
                    || !IsReferenceClass(IL2CPP.il2cpp_object_get_class(property.Pointer)))
                    throw new InvalidOperationException("Only exact Entity and NetworkId properties are supported.");
                return new SlotBinding(slot, holder, state, meta, properties, objects, objectIndex, property, obj);
            }

            private unsafe bool Matches()
            {
                if (!_holder.Matches() || !_holder.Backing.IsOwner || _state.WasCollected
                    || _state.Entity?.Pointer != _holder.Backing.Pointer
                    || _holder.Backing.Serializer?.Pointer != _state.Pointer
                    || IL2CPP.il2cpp_object_get_class(_state.Pointer) != _stateClass
                    || _meta.WasCollected || _state.Meta?.Pointer != _meta.Pointer
                    || IL2CPP.il2cpp_object_get_class(_meta.Pointer) != _metaClass || _meta.TypeId.Value != Slot.StateTypeId
                    || _properties.WasCollected || _meta.Properties?.Pointer != _properties.Pointer
                    || (uint)Slot.PropertyIndex >= (uint)_properties.Length
                    || _objects.WasCollected || _state.RootObjects?.Pointer != _objects.Pointer
                    || (uint)_objectIndex >= (uint)_objects.Count || _object.WasCollected
                    || _objects[_objectIndex]?.Pointer != _object.Pointer
                    || IL2CPP.il2cpp_object_get_class(_object.Pointer) != _objectClass
                    || _object.RootState?.Pointer != _state.Pointer || !MatchesMeta(_object.Meta, _objectMeta)
                    || _property.WasCollected || !MatchesMeta(_property.PropertyMeta, _propertyMeta)
                    || IL2CPP.il2cpp_object_get_class(_property.Pointer) != _propertyClass || !IsReferenceClass(_propertyClass)
                    || _object.OffsetStorage != _objectStorage || _object.OffsetProperties != _objectProperties
                    || _object.OffsetObjects != _objectObjects || _property.OffsetStorage != _propertyStorage
                    || _property.OffsetProperties != _propertyProperties
                    || _objectStorage < 0 || _propertyStorage < 0 || _objectProperties < 0 || _propertyProperties < 0
                    || (long)_objectProperties + _propertyProperties != Slot.PropertyIndex) return false;
                byte* info = (byte*)_properties.Pointer + 32 + 32L * Slot.PropertyIndex;
                return *(int*)(info + 16) == _objectIndex && *(IntPtr*)(info + 24) == _property.Pointer;
            }

            internal StorageView CurrentStorage()
            {
                if (!Matches()) throw new InvalidOperationException("Reference holder binding changed.");
                return new StorageView(this, _state.Frames, _state.Storage);
            }

            internal bool MatchesStorage(StorageView view) => Matches()
                && !view.Frames.WasCollected && _state.Frames?.Pointer == view.Frames.Pointer
                && !view.Storage.WasCollected && _state.Storage?.Pointer == view.Storage.Pointer
                && _object.Storage?.Pointer == view.Storage.Pointer && view.Frames.first?.Pointer == view.Storage.Pointer
                && view.Storage._IBoltListNode_list_k__BackingField?.Pointer == view.Frames.Pointer
                && view.Storage.Frame == view.Frame && !view.Values.WasCollected
                && view.Storage.Values?.Pointer == view.Values.Pointer && (uint)ValueIndex < (uint)view.Values.Length;

            private static bool MatchesMeta(NetworkObj_Meta current, NetworkObj_Meta captured) => captured == null
                ? current == null : !captured.WasCollected && current != null && !current.WasCollected
                    && current.Pointer == captured.Pointer;
        }

        private sealed class StorageView
        {
            internal readonly SlotBinding Binding;
            internal readonly BoltDoubleList<NetworkStorage> Frames;
            internal readonly NetworkStorage Storage;
            internal readonly Il2CppReferenceArray<NetworkValue> Values;
            internal readonly int Frame;

            internal StorageView(SlotBinding binding, BoltDoubleList<NetworkStorage> frames, NetworkStorage storage)
            {
                Binding = binding; Frames = frames; Storage = storage;
                if (frames == null || frames.WasCollected || storage == null || storage.WasCollected)
                    throw new InvalidOperationException("Missing current owner storage.");
                Values = storage.Values; Frame = storage.Frame;
                if (Values == null || Values.WasCollected) throw new InvalidOperationException("Missing reference values.");
            }

            internal unsafe bool Matches(ulong expected)
            {
                if (!Binding.MatchesStorage(this)) return false;
                bool matches = *(ulong*)((byte*)Values.Pointer + 32 + 24L * Binding.ValueIndex) == expected;
                GC.KeepAlive(this);
                return matches;
            }

            internal unsafe void Write(ulong id)
            {
                // Only the packed ID changes; the remaining value bytes include a GC reference.
                *(ulong*)((byte*)Values.Pointer + 32 + 24L * Binding.ValueIndex) = id;
                GC.KeepAlive(this);
            }
        }

        private readonly struct ConnectionBinding
        {
            private readonly BoltConnection _connection;
            private readonly UdpConnection _transport;
            private readonly UdpSocket _socket;
            private readonly uint _id;
            internal IntPtr Pointer => _connection?.Pointer ?? IntPtr.Zero;

            internal ConnectionBinding(BoltConnection connection)
            {
                _connection = connection; _transport = connection?._udp;
                _socket = _transport?.Socket; _id = connection?.ConnectionId ?? 0;
            }

            internal bool Matches() => _connection == null || (!_connection.WasCollected
                && _connection.ConnectionId == _id && _transport != null && !_transport.WasCollected
                && _connection._udp?.Pointer == _transport.Pointer && _transport.ConnectionId == _id
                && _transport.State == UdpConnectionState.Connected && _transport.UserToken?.Pointer == _connection.Pointer
                && _socket != null && !_socket.WasCollected && _socket.State != UdpSocketState.Shutdown
                && _transport.Socket?.Pointer == _socket.Pointer && BoltCore._udpSocket?.Pointer == _socket.Pointer);
        }
    }
}
