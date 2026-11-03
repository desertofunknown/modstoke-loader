// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Bolt;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppInterop.Runtime.Runtime;
using Modstoke.Loader;
using UdpKit;

namespace Modstoke.Sons
{
    /// <summary>Optional server policy for successfully decoded Bolt scalar state properties.</summary>
    public static class BoltProperties
    {
        public const string ServiceKey = "openacai.sons.boltProperties";
        private static BoltPropertyHookState _state = new(BoltEventHookStatus.NotAttempted,
            "Scalar property policy has not been registered.");
        private static readonly object SubscriptionSync = new();
        private static Subscription[] _subscriptions = Array.Empty<Subscription>();
        private static int _attempted;
        private static int _reported;
        [ThreadStatic] private static ReadScope _current;
        [ThreadStatic] private static bool _deciding;

        /// <summary>
        /// Runs synchronously after a qualified scalar reader consumes its wire value, before it
        /// returns to the state reader. Reject restores the previous destination value; a typed
        /// server value replaces the proposal. Neither operation transfers ownership or sends a
        /// correction to the owner. Policies must not re-enter Bolt, mutate native state, or
        /// detach entities. Unsubscribe on unload. No subscriber means no property snapshots.
        /// </summary>
        /// <remarks>Every received scalar of every state reaches this event. Prefer TrySubscribe for one property.</remarks>
        public static event Action<BoltPropertyRequest> Receiving;

        public static BoltPropertyHookState State => Volatile.Read(ref _state);
        internal static bool HasSubscribers => (Receiving != null || Volatile.Read(ref _subscriptions).Length != 0)
            && State.Status == BoltEventHookStatus.Installed;

        /// <summary>
        /// Runs a Receiving-style policy for one qualified root scalar property. While no Receiving handler
        /// exists, other properties are read natively: they are not captured, allocate nothing and cannot
        /// abandon a packet on this subscriber's behalf.
        /// </summary>
        /// <remarks>Receiving's callback rules apply. Receiving handlers run first, and a rollback stops later
        /// policies. Any subscription turns on the receive guard for every packet. Dispose the subscription on
        /// the game thread when unloading; disposed from another thread, the handler can run once more.</remarks>
        public static bool TrySubscribe(BoltPropertySelector selector, Action<BoltPropertyRequest> handler,
            out IDisposable subscription)
        {
            subscription = null;
            if (handler == null || !selector.IsValid || State.Status != BoltEventHookStatus.Installed) return false;
            var created = new Subscription(selector, handler);
            lock (SubscriptionSync)
            {
                var current = _subscriptions;
                var next = new Subscription[current.Length + 1];
                current.CopyTo(next, 0);
                next[current.Length] = created;
                Volatile.Write(ref _subscriptions, next);
            }
            subscription = created;
            return true;
        }

        /// <summary>
        /// Qualifies a root scalar property of a generated Bolt state from its shared metadata, by exact
        /// name and kind, for example <c>TryResolveRootProperty&lt;PlayerState_Meta&gt;("Dying", BoltScalarKind.Int32, ...)</c>.
        /// </summary>
        /// <remarks>Call it on the game thread after Bolt has created the state metadata. Nested and array
        /// properties, duplicates and other kinds are refused. Indexes are build-specific; never persist them.</remarks>
        public static unsafe bool TryResolveRootProperty<TMeta>(string propertyName, BoltScalarKind kind,
            out BoltPropertySelector selector, out string detail) where TMeta : NetworkState_Meta
        {
            selector = default;
            detail = "Only Boolean, Int32 and Single root properties can be selected.";
            if (string.IsNullOrEmpty(propertyName) || kind == BoltScalarKind.Unsupported) return false;
            try
            {
                RequireLayout();
                var meta = typeof(TMeta).GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                    ?.GetValue(null) as NetworkState_Meta;
                var properties = meta?.Properties;
                if (meta == null || meta.WasCollected || properties == null || properties.WasCollected)
                {
                    detail = "The state metadata has not been created.";
                    return false;
                }
                int found = -1;
                NetworkProperty property = null;
                for (int index = 0; index < properties.Length && index < 1024; index++)
                {
                    // Same inline descriptor layout as Capture: object index at 16, property at 24.
                    byte* info = (byte*)properties.Pointer + 32 + 32L * index;
                    IntPtr pointer = *(IntPtr*)(info + 24);
                    if (pointer == IntPtr.Zero) continue;
                    var candidate = Il2CppObjectPool.Get<NetworkProperty>(pointer);
                    if (candidate.PropertyName != propertyName) continue;
                    if (found >= 0)
                    {
                        detail = "The state has more than one property with that name.";
                        return false;
                    }
                    found = index;
                    property = candidate;
                }
                if (property == null)
                {
                    detail = "The state has no property with that name.";
                    return false;
                }
                // A root property belongs to object 0 and its own offset is its root index.
                if (*(int*)((byte*)properties.Pointer + 32 + 32L * found + 16) != 0
                    || property.OffsetProperties != found || KindOf(property) != kind)
                {
                    detail = "The property is nested or has a different kind.";
                    return false;
                }
                selector = new BoltPropertySelector(meta.TypeId.Value, found, kind);
                detail = "Root property qualified.";
                return true;
            }
            catch (Exception error)
            {
                detail = "The state metadata could not be read (" + error.GetType().Name + ").";
                return false;
            }
        }

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (Interlocked.Exchange(ref _attempted, 1) != 0) return;
            HarmonyLib.Harmony harmony = null;
            try
            {
                BoltReceiveGuard.Install(support, warning);
                if (!BoltReceiveGuard.IsReady)
                {
                    SetState(BoltEventHookStatus.Disabled, "Scalar policy requires the matched receive guard.");
                    return;
                }
                RequireLayout();
                harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-bolt-properties");
                var read = AccessTools.DeclaredMethod(typeof(NetworkState), nameof(NetworkState.Bolt_IEntitySerializer_Read),
                    new[] { typeof(BoltConnection), typeof(UdpPacket), typeof(int) })
                    ?? throw new MissingMethodException("NetworkState.Read");
                harmony.Patch(read,
                    prefix: new HarmonyMethod(typeof(BoltProperties), nameof(BeforeState))
                    { priority = HarmonyLib.Priority.First + 100 },
                    postfix: new HarmonyMethod(typeof(BoltProperties), nameof(AfterState))
                    { priority = HarmonyLib.Priority.First + 100 },
                    finalizer: new HarmonyMethod(typeof(BoltProperties), nameof(FinishState)));
                InstallReader<NetworkProperty_Integer>();
                InstallReader<NetworkProperty_Bool>();
                InstallReader<NetworkProperty_Float>();
                SetState(BoltEventHookStatus.Installed,
                    "Boolean, Int32 and Single state rollback/server-value policy registered; multiplayer acceptance pending. Other property kinds are not covered.");

                void InstallReader<T>() where T : NetworkProperty
                {
                    RuntimeHelpers.RunClassConstructor(typeof(T).TypeHandle);
                    var target = AccessTools.DeclaredMethod(typeof(T), nameof(NetworkProperty.Read),
                        new[] { typeof(BoltConnection), typeof(NetworkObj), typeof(NetworkStorage), typeof(UdpPacket) })
                        ?? throw new MissingMethodException(typeof(T).FullName, "Read");
                    if (target.ReturnType != typeof(void)) throw new InvalidOperationException("Unsupported scalar reader.");
                    harmony.Patch(target,
                        prefix: new HarmonyMethod(typeof(BoltProperties), nameof(BeforeProperty))
                        { priority = HarmonyLib.Priority.Last },
                        postfix: new HarmonyMethod(typeof(BoltProperties), nameof(AfterProperty))
                        { priority = HarmonyLib.Priority.First });
                }
            }
            catch (Exception error)
            {
                SetState(BoltEventHookStatus.Failed, "Scalar property policy registration failed; callbacks are inactive.");
                try { harmony?.UnpatchSelf(); } catch (Exception cleanup) { Report(cleanup); }
                try { warning?.Invoke("Scalar property policy registration failed: " + error); } catch (Exception) { }
            }
        }

        private static bool BeforeState(NetworkState __instance, BoltConnection __0, UdpPacket __1,
            int __2, bool __runOriginal, out ReadScope __state)
        {
            __state = null;
            if (!__runOriginal) return false;
            if (!BoltReceiveGuard.CanAbort(__1, __0)) return true;
            var handlers = Receiving;
            var selected = Volatile.Read(ref _subscriptions);
            if ((handlers == null && selected.Length == 0) || State.Status != BoltEventHookStatus.Installed) return true;
            // Without a Receiving handler, only states with a selected property are policed.
            if (handlers == null && !SelectsState(__instance, selected)) return true;
            if (_current != null)
            {
                _current.Abort();
                BoltReceiveGuard.TryAbort(__1, __0);
                return false;
            }
            try
            {
                var scope = new ReadScope(__instance, __0, __1, __2, handlers, selected);
                if (!scope.MatchesContext())
                {
                    BoltReceiveGuard.TryAbort(__1, __0);
                    return false;
                }
                __state = _current = scope;
                return true;
            }
            catch (Exception error)
            {
                Report(error);
                BoltReceiveGuard.TryAbort(__1, __0);
                return false;
            }
        }

        private static void AfterState(ReadScope __state)
        {
            // Run before state observers. The native caller marks a property dirty AFTER Read.
            if (__state != null) Flush(__state);
        }

        private static void FinishState(ReadScope __state)
        {
            if (__state == null) return;
            try { Flush(__state); }
            finally { _current = null; __state.Close(); }
            // A void finalizer leaves any original native exception intact.
        }

        private static void Flush(ReadScope scope)
        {
            try { scope.FlushDirty(); }
            catch (Exception error) { Report(error); scope.Abort(); }
        }

        private static bool BeforeProperty(NetworkProperty __instance, BoltConnection __0, NetworkObj __1,
            NetworkStorage __2, UdpPacket __3, bool __runOriginal, out PropertyRead __state)
        {
            __state = default;
            if (!__runOriginal) return false;
            var scope = _current;
            if (scope == null) return true;
            if (_deciding) { scope.Abort(); return false; }
            // Unselected properties are read natively, without capture or a chance to abort the packet.
            // A pending dirty-bit clear then waits for the next selected property or the end of the state.
            if (scope.Handlers == null && !SelectsProperty(scope, __instance, __1)) return true;
            try
            {
                scope.FlushDirty();
                if (scope.Aborted || !scope.MatchesContext() || __0?.Pointer != scope.Source.Pointer
                    || __3?.Pointer != scope.Packet.Pointer || __1?.RootState?.Pointer != scope.NativeState.Pointer)
                {
                    scope.Abort();
                    return true; // Still consume the value; the enclosing packet will be abandoned.
                }
                __state = Capture(scope, __instance, __1, __2);
            }
            catch (Exception error) { Report(error); scope.Abort(); }
            return true;
        }

        private static void AfterProperty(bool __runOriginal, PropertyRead __state)
        {
            if (!__runOriginal || __state.Scope == null) return;
            var scope = __state.Scope;
            BoltPropertyRequest request = null;
            try
            {
                if (!__state.MatchesStorage() || !scope.MatchesContext()) { scope.Abort(); return; }
                var proposed = __state.ReadValue();
                int position = scope.Packet.Position;
                request = new BoltPropertyRequest(__state, proposed);
                _deciding = true;
                if (scope.Handlers != null)
                    foreach (var handler in Delegate.EnumerateInvocationList(scope.Handlers))
                        if (Decide(handler, request)) break;
                if (request.Decision != BoltPropertyDecision.Rollback)
                    foreach (var subscription in scope.Selected)
                        if (subscription.Matches(scope.TypeId, __state.PropertyIndex, __state.Kind)
                            && Decide(subscription.Handler, request)) break;
                if (!__state.MatchesStorage() || !scope.MatchesContext() || scope.Packet.Position != position)
                { scope.Abort(); return; }
                var selected = request.Selected;
                if (scope.Aborted) selected = __state.Previous;
                if (request.Decision != BoltPropertyDecision.Accept || scope.Aborted)
                {
                    __state.WriteValue(selected);
                    // Do not create a false change notification for a rejected/no-op value.
                    // A pre-existing dirty bit (including an earlier accepted duplicate) survives.
                    if (selected.SameBits(__state.Previous) && !__state.WasDirty)
                        scope.PendingClear = __state;
                }
            }
            catch (Exception error)
            {
                Report(error);
                try
                {
                    if (__state.MatchesStorage())
                    {
                        __state.WriteValue(__state.Previous);
                        if (!__state.WasDirty) scope.PendingClear = __state;
                    }
                }
                catch (Exception cleanup) { Report(cleanup); }
                scope.Abort();
            }
            finally { request?.Close(); _deciding = false; }
        }

        // Returns true once the request is rolled back, which ends the policy chain.
        private static bool Decide(Action<BoltPropertyRequest> handler, BoltPropertyRequest request)
        {
            try { handler(request); }
            catch (Exception error) { request.Reject(); Report(error); }
            return request.Decision == BoltPropertyDecision.Rollback;
        }

        // Selector filtering never aborts: unreadable metadata leaves the state to the native reader.
        private static bool SelectsState(NetworkState state, Subscription[] subscriptions)
        {
            try
            {
                var meta = state?.Meta;
                return meta != null && Selects(subscriptions, meta.TypeId.Value, -1);
            }
            catch (Exception error) { Report(error); return false; }
        }

        private static bool SelectsProperty(ReadScope scope, NetworkProperty property, NetworkObj obj)
        {
            try
            {
                return property != null && obj != null
                    && Selects(scope.Selected, scope.TypeId, obj.OffsetProperties + property.OffsetProperties);
            }
            catch (Exception error) { Report(error); return false; }
        }

        // A negative index selects any property of the state type.
        private static bool Selects(Subscription[] subscriptions, int typeId, int propertyIndex)
        {
            foreach (var subscription in subscriptions)
                if (subscription.IsActive && subscription.Selector.StateTypeId == typeId
                    && (propertyIndex < 0 || subscription.Selector.PropertyIndex == propertyIndex))
                    return true;
            return false;
        }

        private static unsafe PropertyRead Capture(ReadScope scope, NetworkProperty property, NetworkObj obj,
            NetworkStorage storage)
        {
            var kind = KindOf(property);
            if (kind == BoltScalarKind.Unsupported)
                throw new InvalidOperationException("Unqualified scalar property class.");
            int propertyIndex = checked(obj.OffsetProperties + property.OffsetProperties);
            var properties = scope.NativeState.Meta.Properties;
            if (properties == null || (uint)propertyIndex >= (uint)properties.Length || propertyIndex >= 1024)
                throw new InvalidOperationException("Scalar property index is outside its state metadata.");
            // NetworkPropertyInfo is an inline value type containing references. Avoid its boxed
            // interop indexer and never copy/write its references. Layout is checked at installation.
            byte* info = (byte*)properties.Pointer + 32 + 32L * propertyIndex;
            int objectIndex = *(int*)(info + 16);
            var objects = scope.NativeState.RootObjects;
            if (*(IntPtr*)(info + 24) != property.Pointer || objects == null
                || (uint)objectIndex >= (uint)objects.Count || objects[objectIndex]?.Pointer != obj.Pointer)
                throw new InvalidOperationException("Scalar descriptor does not match its root property.");
            bool interpolated = kind == BoltScalarKind.Single && property.Interpolation.Enabled;
            int slot = checked(obj.OffsetStorage + property.OffsetStorage + (interpolated ? 1 : 0));
            var values = storage?.Values;
            if (values == null || (uint)slot >= (uint)values.Length)
                throw new InvalidOperationException("Scalar destination is outside its storage.");
            var read = new PropertyRead(scope, property, obj, storage, values, propertyIndex, slot,
                interpolated ? 4 : 0, kind, storage.IsSet(propertyIndex));
            if (!read.MatchesStorage()) throw new InvalidOperationException("Scalar destination is not a current state frame.");
            read.Previous = read.ReadValue();
            return read;
        }

        internal static BoltScalarKind KindOf(NetworkProperty property)
        {
            if (property == null || property.WasCollected) return BoltScalarKind.Unsupported;
            var type = IL2CPP.il2cpp_object_get_class(property.Pointer);
            return type == Il2CppClassPointerStore<NetworkProperty_Integer>.NativeClassPtr ? BoltScalarKind.Int32
                : type == Il2CppClassPointerStore<NetworkProperty_Bool>.NativeClassPtr ? BoltScalarKind.Boolean
                : type == Il2CppClassPointerStore<NetworkProperty_Float>.NativeClassPtr ? BoltScalarKind.Single
                : BoltScalarKind.Unsupported;
        }

        internal static void RequireLayout()
        {
            if (IntPtr.Size != 8) throw new InvalidOperationException("Scalar policy requires the qualified x64 layout.");
            RuntimeHelpers.RunClassConstructor(typeof(NetworkValue).TypeHandle);
            RuntimeHelpers.RunClassConstructor(typeof(NetworkPropertyInfo).TypeHandle);
            RequireValueLayout(Il2CppClassPointerStore<NetworkValue>.NativeClassPtr, 24);
            RequireField(Il2CppClassPointerStore<NetworkValue>.NativeClassPtr, "Int0", 16);
            RequireField(Il2CppClassPointerStore<NetworkValue>.NativeClassPtr, "Bool", 16);
            RequireField(Il2CppClassPointerStore<NetworkValue>.NativeClassPtr, "Float0", 16);
            RequireField(Il2CppClassPointerStore<NetworkValue>.NativeClassPtr, "Float1", 20);
            RequireValueLayout(Il2CppClassPointerStore<NetworkPropertyInfo>.NativeClassPtr, 32);
            RequireField(Il2CppClassPointerStore<NetworkPropertyInfo>.NativeClassPtr, "OffsetObjects", 32);
            RequireField(Il2CppClassPointerStore<NetworkPropertyInfo>.NativeClassPtr, "Property", 40);
        }

        private static void RequireValueLayout(IntPtr type, int size)
        {
            uint alignment = 0;
            if (type == IntPtr.Zero || !IL2CPP.il2cpp_class_is_valuetype(type)
                || IL2CPP.il2cpp_class_value_size(type, ref alignment) != size
                || IL2CPP.il2cpp_class_instance_size(type) != size + 16)
                throw new InvalidOperationException("Unsupported Bolt value layout.");
        }

        private static void RequireField(IntPtr type, string name, uint offset)
        {
            var field = IL2CPP.il2cpp_class_get_field_from_name(type, name);
            if (field == IntPtr.Zero || IL2CPP.il2cpp_field_get_offset(field) != offset)
                throw new InvalidOperationException("Unsupported Bolt field layout: " + name);
        }

        private static void SetState(BoltEventHookStatus status, string detail)
        {
            var state = new BoltPropertyHookState(status, detail);
            Volatile.Write(ref _state, state);
            try { Modding.SetService(ServiceKey, state); } catch (Exception error) { Report(error); }
        }

        private static void Report(Exception error)
        {
            if (Interlocked.Exchange(ref _reported, 1) != 0) return;
            try { RuntimeEntryPoint.LogSource?.LogWarning("Bolt scalar property policy failed ("
                + error.GetType().FullName + "). Further failures will not be logged."); } catch (Exception) { }
        }

        internal sealed class ReadScope
        {
            internal readonly NetworkState NativeState;
            internal readonly BoltConnection Source;
            internal readonly UdpPacket Packet;
            internal readonly Entity Entity;
            internal readonly Action<BoltPropertyRequest> Handlers;
            internal readonly Subscription[] Selected;
            internal readonly ulong EntityId;
            internal readonly uint SourceId;
            internal readonly int TypeId;
            internal readonly int WireFrame;
            internal readonly bool IsOwner;
            internal readonly bool IsSourceOwner;
            // Bolt reads a new proxy's first state before Attach, so the scope keeps the state it opened with.
            private readonly bool _attached;
            private readonly IntPtr _owner;
            private readonly IntPtr _controller;
            private readonly IntPtr _frames;
            private readonly IntPtr _meta;
            private readonly IntPtr _data;
            private readonly int _size;
            private readonly int _thread = Environment.CurrentManagedThreadId;
            private bool _closed;
            internal bool Aborted;
            internal PropertyRead PendingClear;

            internal ReadScope(NetworkState state, BoltConnection source, UdpPacket packet, int frame,
                Action<BoltPropertyRequest> handlers, Subscription[] selected)
            {
                NativeState = state; Source = source; Packet = packet; Handlers = handlers; Selected = selected;
                Entity = state.Entity ?? throw new InvalidOperationException("Missing state entity.");
                EntityId = Entity.NetworkId.PackedValue; SourceId = source.ConnectionId;
                TypeId = state.Meta.TypeId.Value; WireFrame = frame; IsOwner = Entity.IsOwner; _attached = Entity.IsAttached;
                _owner = Entity.Source?.Pointer ?? IntPtr.Zero;
                _controller = Entity.Controller?.Pointer ?? IntPtr.Zero;
                IsSourceOwner = !IsOwner && _owner == source.Pointer;
                _frames = state.Frames?.Pointer ?? IntPtr.Zero; _meta = state.Meta.Pointer;
                _data = packet.Data?.Pointer ?? IntPtr.Zero; _size = packet.Size;
            }

            internal bool MatchesContext() => !_closed && Environment.CurrentManagedThreadId == _thread
                && !NativeState.WasCollected && !Entity.WasCollected && Entity.IsAttached == _attached
                && NativeState.Entity?.Pointer == Entity.Pointer && Entity.NetworkId.PackedValue == EntityId
                && Entity.Serializer?.Pointer == NativeState.Pointer && Entity.IsOwner == IsOwner
                && (Entity.Source?.Pointer ?? IntPtr.Zero) == _owner
                && (Entity.Controller?.Pointer ?? IntPtr.Zero) == _controller
                && NativeState.Meta?.Pointer == _meta && NativeState.Frames?.Pointer == _frames
                && _frames != IntPtr.Zero && !Packet.WasCollected && !Packet.IsPooled
                && Packet.Size == _size && Packet.Data?.Pointer == _data && _data != IntPtr.Zero
                && Packet.Position >= 0 && Packet.Position <= _size
                && BoltReceiveGuard.IsCurrent(Packet, Source);

            internal void FlushDirty()
            {
                var pending = PendingClear;
                PendingClear = default;
                if (pending.Scope == null) return;
                if (!MatchesContext() || !pending.MatchesStorage()) { Abort(); return; }
                // Another native writer must not lose its change notification.
                if (pending.ReadValue().SameBits(pending.Previous)) pending.Storage.Clear(pending.PropertyIndex);
            }

            internal void Abort()
            {
                Aborted = true;
                BoltReceiveGuard.TryAbort(Packet, Source);
            }

            internal void Close() { _closed = true; PendingClear = default; }
        }

        internal sealed class Subscription : IDisposable
        {
            internal readonly BoltPropertySelector Selector;
            internal readonly Action<BoltPropertyRequest> Handler;
            private int _disposed;

            internal Subscription(BoltPropertySelector selector, Action<BoltPropertyRequest> handler)
            {
                Selector = selector;
                Handler = handler;
            }

            internal bool IsActive => Volatile.Read(ref _disposed) == 0;

            internal bool Matches(int typeId, int propertyIndex, BoltScalarKind kind) => IsActive
                && Selector.StateTypeId == typeId && Selector.PropertyIndex == propertyIndex && Selector.Kind == kind;

            // A state read that already copied the array skips this subscription once it is inactive.
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                lock (SubscriptionSync)
                    Volatile.Write(ref _subscriptions, Array.FindAll(_subscriptions, other => !ReferenceEquals(other, this)));
            }
        }

        internal struct PropertyRead
        {
            internal readonly ReadScope Scope;
            internal readonly NetworkProperty Property;
            private readonly NetworkObj _object;
            internal readonly NetworkStorage Storage;
            private readonly Il2CppReferenceArray<NetworkValue> _values;
            internal readonly int PropertyIndex;
            private readonly int _slot;
            private readonly int _offset;
            private readonly int _frame;
            private readonly int _objectStorage;
            private readonly int _objectProperties;
            private readonly int _propertyStorage;
            private readonly int _propertyProperties;
            internal readonly BoltScalarKind Kind;
            internal readonly bool WasDirty;
            internal BoltScalar Previous;

            internal PropertyRead(ReadScope scope, NetworkProperty property, NetworkObj obj, NetworkStorage storage,
                Il2CppReferenceArray<NetworkValue> values, int propertyIndex, int slot, int offset,
                BoltScalarKind kind, bool wasDirty)
            {
                Scope = scope; Property = property; _object = obj; Storage = storage; _values = values;
                PropertyIndex = propertyIndex; _slot = slot; _offset = offset; Kind = kind; WasDirty = wasDirty;
                _frame = storage.Frame; _objectStorage = obj.OffsetStorage; _objectProperties = obj.OffsetProperties;
                _propertyStorage = property.OffsetStorage; _propertyProperties = property.OffsetProperties;
                Previous = default;
            }

            internal bool MatchesStorage() => Scope != null && !Storage.WasCollected && !Property.WasCollected
                && !_object.WasCollected && !_values.WasCollected && Storage.Frame == _frame
                // State initialization leaves Storage.Root unset. The owning frame list is the
                // native lifetime authority, including newly cloned and predicted-control frames.
                && Storage._IBoltListNode_list_k__BackingField?.Pointer == Scope.NativeState.Frames?.Pointer
                && Storage.Values?.Pointer == _values.Pointer && (uint)_slot < (uint)_values.Length
                && _object.RootState?.Pointer == Scope.NativeState.Pointer
                && _object.OffsetStorage == _objectStorage && _object.OffsetProperties == _objectProperties
                && Property.OffsetStorage == _propertyStorage && Property.OffsetProperties == _propertyProperties
                && KindOf(Property) == Kind
                && (Kind != BoltScalarKind.Single || Property.Interpolation.Enabled == (_offset == 4));

            internal unsafe BoltScalar ReadValue()
            {
                byte* value = (byte*)_values.Pointer + 32 + 24L * _slot + _offset;
                var result = new BoltScalar(Kind, Kind == BoltScalarKind.Boolean ? *value : *(uint*)value);
                GC.KeepAlive(_values);
                return result;
            }

            internal unsafe void WriteValue(BoltScalar value)
            {
                // Write only the primitive, never the whole NetworkValue: it also contains a GC reference.
                byte* destination = (byte*)_values.Pointer + 32 + 24L * _slot + _offset;
                if (Kind == BoltScalarKind.Boolean) *destination = (byte)value.Bits;
                else *(uint*)destination = value.Bits;
                GC.KeepAlive(_values);
            }
        }
    }

    public sealed record BoltPropertyHookState(BoltEventHookStatus Status, string Detail);
    public enum BoltScalarKind { Unsupported, Boolean, Int32, Single }

    /// <summary>A qualified root scalar property. Obtain one from BoltProperties.TryResolveRootProperty.</summary>
    public readonly struct BoltPropertySelector
    {
        internal BoltPropertySelector(int stateTypeId, int propertyIndex, BoltScalarKind kind)
        {
            StateTypeId = stateTypeId;
            PropertyIndex = propertyIndex;
            Kind = kind;
        }

        public int StateTypeId { get; }
        public int PropertyIndex { get; }
        public BoltScalarKind Kind { get; }
        /// <summary>False for default, which selects nothing.</summary>
        public bool IsValid => Kind != BoltScalarKind.Unsupported;
    }
    public enum BoltPropertyDecision { Accept, Rollback, ServerValue }

    /// <summary>A copied primitive. Single preserves incoming float bits, including non-finite proposals.</summary>
    public readonly struct BoltScalar
    {
        public BoltScalarKind Kind { get; }
        internal uint Bits { get; }
        internal BoltScalar(BoltScalarKind kind, uint bits) { Kind = kind; Bits = bits; }
        public static BoltScalar FromBoolean(bool value) => new(BoltScalarKind.Boolean, value ? 1U : 0U);
        public static BoltScalar FromInt32(int value) => new(BoltScalarKind.Int32, unchecked((uint)value));
        public static BoltScalar FromSingle(float value) => new(BoltScalarKind.Single, BitConverter.SingleToUInt32Bits(value));
        public bool TryGetBoolean(out bool value) { value = Bits != 0; return Kind == BoltScalarKind.Boolean; }
        public bool TryGetInt32(out int value) { value = unchecked((int)Bits); return Kind == BoltScalarKind.Int32; }
        public bool TryGetSingle(out float value) { value = BitConverter.UInt32BitsToSingle(Bits); return Kind == BoltScalarKind.Single; }
        internal bool SameBits(BoltScalar other) => Kind == other.Kind && Bits == other.Bits;
        internal bool IsValidServerValue => Kind != BoltScalarKind.Unsupported
            && (Kind != BoltScalarKind.Single || float.IsFinite(BitConverter.UInt32BitsToSingle(Bits)));
    }

    /// <summary>Callback-only decisions; scalar observations remain readable after the callback closes.</summary>
    public sealed class BoltPropertyRequest
    {
        private BoltProperties.PropertyRead _read;
        private readonly int _thread = Environment.CurrentManagedThreadId;
        internal BoltPropertyRequest(BoltProperties.PropertyRead read, BoltScalar proposed)
        {
            _read = read; Previous = read.Previous; Proposed = Selected = proposed;
            EntityNetworkId = read.Scope.EntityId; SourceConnectionId = read.Scope.SourceId;
            StateTypeId = read.Scope.TypeId; PropertyIndex = read.PropertyIndex; WireFrame = read.Scope.WireFrame;
            IsLocallyOwned = read.Scope.IsOwner; IsSourceEntityOwner = read.Scope.IsSourceOwner;
        }

        public ulong EntityNetworkId { get; }
        public uint SourceConnectionId { get; }
        public int StateTypeId { get; }
        /// <summary>Root-state property index, including nested/array offsets. Leaf names alone are not identities.</summary>
        public int PropertyIndex { get; }
        public int WireFrame { get; }
        public bool IsLocallyOwned { get; }
        /// <summary>Actual sender is the nonlocal entity owner, not merely its controller.</summary>
        public bool IsSourceEntityOwner { get; }
        /// <summary>Previous destination value, not necessarily server-authoritative truth or the displayed value.</summary>
        public BoltScalar Previous { get; }
        public BoltScalar Proposed { get; }
        public BoltScalar Selected { get; private set; }
        public BoltPropertyDecision Decision { get; private set; }
        private bool IsOpen => _read.Scope != null && Environment.CurrentManagedThreadId == _thread;

        /// <summary>Irreversible for this proposal. Preserves packet consumption and existing dirty notifications.</summary>
        public bool Reject()
        {
            if (!IsOpen) return false;
            Decision = BoltPropertyDecision.Rollback; Selected = Previous; return true;
        }

        /// <summary>
        /// Supply a committed server-ledger value of the same type. Non-finite replacements are refused.
        /// Conflicting server values from multiple subscribers roll back instead of choosing a last writer.
        /// This changes the received server replica only; owner correction uses the mode's existing protocol.
        /// </summary>
        public bool TryUseServerValue(BoltScalar value)
        {
            if (!IsOpen || Decision == BoltPropertyDecision.Rollback
                || value.Kind != Proposed.Kind || !value.IsValidServerValue) return false;
            if (Decision == BoltPropertyDecision.ServerValue && !Selected.SameBits(value))
            { Reject(); return false; }
            Selected = value; Decision = BoltPropertyDecision.ServerValue; return true;
        }

        /// <summary>Exact observed session match without scanning the player roster on every property.</summary>
        public bool IsFromSession(ServerSession expected)
        {
            return expected != null && TryGetSourceSession(out var session)
                && ReferenceEquals(expected, session);
        }

        /// <summary>Read the already-observed session; null/unobserved membership is never inferred from an ID.</summary>
        public bool TryGetSourceSession(out ServerSession session)
        {
            session = null;
            if (!IsOpen) return false;
            try
            {
                if (!_read.Scope.MatchesContext()) return false;
                session = ServerSessions.ResolveObserved(_read.Scope.Source);
                return session != null;
            }
            catch (Exception) { return false; }
        }

        public bool TryGetPropertyName(out string name)
        {
            name = null;
            if (!IsOpen) return false;
            try
            {
                if (!_read.Scope.MatchesContext() || !_read.MatchesStorage()) return false;
                name = _read.Property.PropertyName;
                return name != null;
            }
            catch (Exception) { return false; }
        }

        internal void Close() { _read = default; }
    }
}
