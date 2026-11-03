// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Bolt;
using HarmonyLib;
using UdpKit;

namespace Modstoke.Sons
{
    /// <summary>
    /// Canonicalizes late event identities for completed player bridges. Native transport still
    /// owns ordering, dispatch and storage. This does not buffer effects during preparation or
    /// certify action-fence admission, inventory custody, or a replacement receiver's behavior.
    /// </summary>
    internal static partial class PlayerEventRouting
    {
        private static readonly Dictionary<ulong, Route> Routes = new();
        [ThreadStatic] private static DecodeScope _decode;
        private static int _thread;
        private static bool _installed;
        private static bool _reported;
        internal static bool IsSelected => _installed && Environment.CurrentManagedThreadId == _thread;

        internal static void Install(HarmonyLib.Harmony harmony,
            Func<Type, string, int, bool, Type, Type[], System.Reflection.MethodInfo> qualify)
        {
            PrepareReferenceSchemas();
            var read = qualify(typeof(EventChannel), nameof(EventChannel.ReadEvent), 0x94C600,
                false, typeof(Bolt.Event), new[] { typeof(UdpPacket), typeof(uint).MakeByRefType() });
            var entity = qualify(typeof(BoltUtils), nameof(BoltUtils.ReadEntity), 0x7CAF70,
                true, typeof(Bolt.Entity), new[] { typeof(UdpPacket) });
            var find = qualify(typeof(BoltCore), nameof(BoltCore.FindEntity), 0x7B3610,
                true, typeof(Bolt.Entity), new[] { typeof(NetworkId) });
            var dispatch = qualify(typeof(EventDispatcher), nameof(EventDispatcher.Dispatch), 0x94D560,
                true, typeof(void), new[] { typeof(Bolt.Event) });
            harmony.Patch(read, prefix: Hook(nameof(BeforeRead)), postfix: Hook(nameof(AfterRead)),
                finalizer: Hook(nameof(FinishRead)));
            harmony.Patch(entity, prefix: Hook(nameof(BeforeReadEntity)), finalizer: Hook(nameof(FinishReadEntity)));
            // Observe the lookup argument; never change global registry lookup semantics.
            harmony.Patch(find, prefix: Hook(nameof(BeforeFindEntity)));
            harmony.Patch(dispatch, prefix: Hook(nameof(BeforeDispatch)), finalizer: Hook(nameof(FinishDispatch)));
            InstallProtection(harmony, qualify);
            _thread = Environment.CurrentManagedThreadId;
            _installed = true;
        }

        internal static bool TryBind(PlayerBridge bridge,
            BoltMigrationSession session, out IDisposable binding, out string detail)
        {
            binding = null;
            detail = "Completed player event routing requires the selected hooks and exact completed session.";
            if (!_installed || Environment.CurrentManagedThreadId != _thread || bridge == null
                || session == null || Routes.Count >= ControlTransport.MaxPeers
                || bridge.Plan != session.Snapshot?.Plan || bridge.NetworkId != session.NewNetworkId
                || Routes.ContainsKey(bridge.Plan.OldNetworkId)
                || !session.TryReadPlayerBridgeTarget(out _, out detail)) return false;
            var route = new Route(bridge, session);
            Routes.Add(bridge.Plan.OldNetworkId, route);
            binding = route;
            detail = "Generated event entity references follow the exact completed player bridge; payload data and permissions are unchanged.";
            return true;
        }

        internal static void ReleaseAfterStop()
        {
            if (Routes.Count != 0) throw new InvalidOperationException("Player event routes still own live bridges.");
            ReleaseProtectionAfterStop();
            _installed = false;
            _reported = false;
            _decode = default;
        }

        internal static bool TryBindObserver(ControlPeer peer,
            BoltMigrationSession session, out IDisposable binding, out string detail)
        {
            binding = null;
            detail = "Observer routing requires its exact completed replica and selected event hooks.";
            if (!IsSelected || peer == null || !peer.IsCurrent || peer.ServerSession != null
                || session == null || !ReferenceEquals(peer, session.Peer) || session.Snapshot == null
                || Routes.Count >= ControlTransport.MaxPeers
                || Routes.ContainsKey(session.Snapshot.Plan.OldNetworkId)
                || !session.TryReadCompletedPlayerReplica(out _, out detail)) return false;
            var route = new Route(null, session);
            Routes.Add(route.OldId, route);
            binding = route;
            detail = "Observer event references bound to the authenticated server's completed replacement.";
            return true;
        }

        private static HarmonyMethod Hook(string name) => new(typeof(PlayerEventRouting), name)
        {
            // Canonical identity must be visible to the existing policies, not changed after approval.
            priority = HarmonyLib.Priority.First + 200
        };

        private static void BeforeRead(UdpPacket __0, bool __runOriginal, out DecodeState __state)
        {
            __state = default;
            if (!__runOriginal || Routes.Count == 0 || Environment.CurrentManagedThreadId != _thread) return;
            __state = new DecodeState { Active = true, Parent = _decode };
            _decode = new DecodeScope { Packet = __0?.Pointer ?? IntPtr.Zero, Active = true };
        }

        private static void BeforeReadEntity(UdpPacket __0, bool __runOriginal, out bool __state)
        {
            __state = false;
            if (!__runOriginal || !_decode.Active || _decode.EntitySeen || __0 == null
                || _decode.Packet != __0.Pointer) return;
            _decode.EntitySeen = true;
            _decode.InEnvelope = true;
            __state = true;
        }

        private static void BeforeFindEntity(NetworkId __0, bool __runOriginal)
        {
            if (__runOriginal && _decode.InEnvelope && _decode.RawId == 0)
                _decode.RawId = __0.PackedValue;
        }

        private static void FinishReadEntity(bool __state)
        {
            if (__state) _decode.InEnvelope = false;
        }

        private static void AfterRead(Bolt.Event __result, DecodeState __state)
        {
            if (!__state.Active || __result == null) return;
            try
            {
                if (!Supported(__result)) return;
                // In this build the reliable branch contains a sequence instead of an envelope.
                // Its first entity lookup can belong to the payload and must not become a route.
                if ((int)__result.Reliability != 0 || !EntityRoute(__result.Targets)) return;
                var existing = __result.TargetEntity;
                if (existing != null && existing.NetworkId.PackedValue != _decode.RawId) return;
                if (TryResolve(_decode.RawId, __result, out var target)) __result.TargetEntity = target._entity;
            }
            // An unchanged TargetEntity leaves the event unrouted; Bolt keeps the decoded event.
            catch (Exception error) { Report(error); }
        }

        private static void FinishRead(DecodeState __state)
        {
            if (__state.Active) _decode = __state.Parent;
        }

        private struct DispatchState
        {
            internal bool ReleaseStorage;
            internal ProtectionDispatchState Protection;
        }

        private static bool BeforeDispatch(Bolt.Event __0, bool __runOriginal, out DispatchState __state)
        {
            __state = default;
            BeforeProtectionDispatch(__0, __runOriginal, out __state.Protection);
            if (!__runOriginal) return false;
            // The outer repair still owns storage. A setter callback may not dispatch or
            // free the same event while its identities are only partly rewritten.
            if (__0 != null && RewritingReferences.Contains(__0.Pointer)) return false;
            if (Environment.CurrentManagedThreadId != _thread || __0 == null) return true;
            try
            {
                if (PlayerInventoryEnforcement.RejectNativeWorldRequest(__0))
                { __state.ReleaseStorage = true; return false; }
                if (Routes.Count == 0 || !Supported(__0)) return true;
                // Already queued envelopes retain their old attachment even after it leaves
                // the registry. Payload references are independent and must be remapped separately.
                var envelope = __0.TargetEntity;
                if (envelope != null && TryResolve(envelope.NetworkId.PackedValue, __0, out var routed))
                    __0.TargetEntity = routed._entity;
                RewriteEntityReferences(__0);
                return true;
            }
            catch (Exception error)
            {
                // No partially canonicalized event may pass a policy under mixed identities.
                // Later policies see __runOriginal=false and do not own this storage release.
                __state.ReleaseStorage = true;
                Report(error);
                return false;
            }
        }

        private static void FinishDispatch(Bolt.Event __0, DispatchState __state, bool __runOriginal, Exception __exception)
        {
            try
            {
                if (__state.ReleaseStorage)
                    try { __0.FreeStorage(); }
                    catch (Exception error) { Report(error); }
            }
            finally { FinishProtectionDispatch(__state.Protection, __runOriginal, __exception); }
        }

        private static void Report(Exception error)
        {
            if (_reported) return;
            _reported = true;
            try
            {
                global::Modstoke.Loader.RuntimeEntryPoint.LogSource?.LogWarning(
                    "Completed player event routing failed (" + error.GetType().Name
                    + "); no mixed-identity dispatch was accepted. Further failures are suppressed.");
            }
            catch (Exception) { }
        }

        private static bool Supported(Bolt.Event value) => TryReadReferenceSchema(value, out _);

        private static bool EntityRoute(int targets) => targets is 1 or 3 or 5 or 7 or 9 or 11 or 13;

        private static bool TryResolve(ulong oldId, Bolt.Event value, out BoltEntity target)
        {
            target = null;
            if (oldId == 0 || !Routes.TryGetValue(oldId, out var route) || route.Disposed
                || !route.Session.Peer.IsCurrent || route.Bridge?.IsDisposed == true) return false;
            var sender = value.RaisedBy;
            if (route.Bridge?.IsServer == true)
            {
                if (sender != null && ServerSessions.ResolveSource(sender)?.IsCurrent != true) return false;
            }
            else if (sender == null || sender.WasCollected
                || sender.Pointer != route.Session.Peer.ConnectionPointer
                || sender.ConnectionId != route.Session.Peer.ConnectionId) return false;
            bool current = route.Bridge == null
                ? route.Session.TryReadCompletedPlayerReplica(out target, out _)
                : route.Session.TryReadPlayerBridgeTarget(out target, out _);
            return current && target != null && target.networkId.PackedValue == route.NewId;
        }

        private struct DecodeScope
        {
            internal IntPtr Packet;
            internal ulong RawId;
            internal bool Active, EntitySeen, InEnvelope;
        }

        private struct DecodeState
        {
            internal bool Active;
            internal DecodeScope Parent;
        }

        private sealed class Route(PlayerBridge bridge,
            BoltMigrationSession session) : IDisposable
        {
            internal readonly PlayerBridge Bridge = bridge;
            internal readonly BoltMigrationSession Session = session;
            internal readonly ulong OldId = session.Snapshot.Plan.OldNetworkId;
            internal readonly ulong NewId = session.NewNetworkId;
            internal bool Disposed;

            public void Dispose()
            {
                if (Environment.CurrentManagedThreadId != _thread)
                    throw new InvalidOperationException("Release player event routing on its game thread.");
                if (Disposed) return;
                Disposed = true;
                if (Routes.TryGetValue(OldId, out var route) && ReferenceEquals(route, this))
                    Routes.Remove(OldId);
            }
        }
    }
}
