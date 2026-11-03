// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using Bolt;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Loader;
using UdpKit;

namespace Modstoke.Sons
{
    /// <summary>Optional server policy before an entity replication header has native side effects.</summary>
    public static partial class BoltStateAdmission
    {
        public const string ServiceKey = "openacai.sons.boltStateAdmission";
        private static BoltStateAdmissionStatus _state = new(BoltEventHookStatus.NotAttempted,
            "Replication admission has not been registered.");
        private static int _attempted;
        private static int _reported;
        private static int _ownerThread;
        [ThreadStatic] private static bool _deciding;

        /// <summary>
        /// Runs before native entity creation, control-token reads, detach or property decoding.
        /// Rejection abandons this update and the remaining packet, then disconnects its source.
        /// Earlier channels/updates are not rolled back. Policy failures use the same rejection.
        /// This is not payload validation or silent delta rejection. Unsubscribe on unload.
        /// </summary>
        public static event Action<BoltStateAdmissionRequest> Receiving;
        // Only policy subscribers enable the receive guard; Observed never does.
        internal static bool HasPolicySubscribers => Receiving != null && State.Status == BoltEventHookStatus.Installed;
        public static BoltStateAdmissionStatus State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (Interlocked.Exchange(ref _attempted, 1) != 0) return;
            HarmonyLib.Harmony harmony = null;
            try
            {
                _ownerThread = Environment.CurrentManagedThreadId;
                BoltReceiveGuard.Install(support, warning);
                if (!BoltReceiveGuard.IsReady)
                {
                    SetState(BoltEventHookStatus.Disabled, "Replication admission requires the matched receive guard.");
                    return;
                }
                var target = AccessTools.DeclaredMethod(typeof(EntityChannel), nameof(EntityChannel.ReadUpdate), new[] { typeof(Packet) })
                    ?? throw new MissingMethodException("EntityChannel.ReadUpdate(Packet)");
                harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-bolt-state-admission");
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(BoltStateAdmission), nameof(BeforeUpdate))
                    { priority = HarmonyLib.Priority.First + 100 });
                SetState(BoltEventHookStatus.Installed,
                    "Pre-header admission registered on guarded receive packets; rejection requires disconnect, not delta rollback. Runtime qualification pending.");
            }
            catch (Exception error)
            {
                SetState(BoltEventHookStatus.Failed, "Replication admission registration failed; policy is inactive.");
                try { harmony?.UnpatchSelf(); } catch (Exception cleanup) { Report(cleanup); }
                try { warning?.Invoke("Replication admission failed: " + error); } catch (Exception) { }
            }
        }

        private static bool BeforeUpdate(EntityChannel __instance, Packet __0, bool __runOriginal, ref bool __result)
        {
            if (!__runOriginal) return false;
            if (BoltReceiveGuard.IsAborted(__0)) { __result = false; return false; }
            var handlers = Receiving;
            var observers = Observed;
            if (handlers == null && observers == null || State.Status != BoltEventHookStatus.Installed) return true;
            var source = __instance._cn;
            var guarded = BoltReceiveGuard.CanAbort(__0, source);
            if (handlers == null || !guarded)
            {
                if (observers != null) Observe(__instance, __0, source, guarded, observers);
                return true;
            }
            __result = false;
            var admitted = Admit(__instance, __0, source, handlers);
            if (admitted && observers != null) Observe(__instance, __0, source, guarded, observers);
            return admitted;
        }

        private static bool Admit(EntityChannel channel, Packet update, BoltConnection source,
            Action<BoltStateAdmissionRequest> handlers)
        {
            if (_deciding) return Abort();
            BoltStateAdmissionRequest request = null;
            _deciding = true;
            try
            {
                var packet = update.UdpPacket;
                if (!PeekId(packet, out bool hasUpdate, out uint connection, out uint entity)) return Abort();
                if (!hasUpdate) return true;
                var id = new NetworkId(connection, entity);
                BoltEntitySnapshot existing = null;
                EntityProxy proxy = null;
                var incoming = channel._incommingDict;
                if (incoming != null && incoming.TryGetValue(id, out proxy) && proxy?.Entity != null)
                    existing = Snapshot(proxy.Entity);
                request = new(source, packet, channel, id, proxy, existing);
                foreach (var handler in Delegate.EnumerateInvocationList(handlers))
                {
                    handler(request);
                    if (request.IsRejected) return Abort();
                }
                return request.MatchesContext() || Abort();
            }
            catch (Exception error) { Report(error); return Abort(); }
            finally { request?.Close(); _deciding = false; }

            bool Abort()
            {
                // false terminates EntityChannel.Read's loop, not just one property decoder.
                return !BoltReceiveGuard.TryAbort(update, source);
            }
        }

        private static BoltEntitySnapshot Snapshot(Entity native)
            => new(native.NetworkId.PackedValue, native.PrefabId.Value, native.IsOwner,
                native.HasControl, native.IsAttached, native.Controller?.ConnectionId, native.Source?.ConnectionId);

        private static bool PeekId(UdpPacket packet, out bool hasUpdate, out uint connection, out uint entity)
        {
            hasUpdate = false;
            connection = entity = 0;
            int bit = packet.Position, end = packet.Size;
            var data = packet.Data;
            if (data == null || bit < 0 || end < bit || end > data.Length * 8L) return false;
            var bytes = data.AsSpan();
            if (!ReadBits(bytes, ref bit, end, 1, out uint marker)) return false;
            if (marker == 0) return true;
            hasUpdate = true;
            return ReadVarUInt(bytes, ref bit, end, out connection) && ReadVarUInt(bytes, ref bit, end, out entity);
        }

        private static bool ReadVarUInt(ReadOnlySpan<byte> bytes, ref int bit, int end, out uint value)
        {
            value = 0;
            for (int shift = 0; shift <= 28; shift += 7)
            {
                if (!ReadBits(bytes, ref bit, end, 8, out uint part) || (shift == 28 && part > 15)) return false;
                value |= (part & 127) << shift;
                if ((part & 128) == 0) return true;
            }
            return false;
        }

        private static bool ReadBits(ReadOnlySpan<byte> bytes, ref int bit, int end, int count, out uint value)
        {
            value = 0;
            if (count > end - bit) return false;
            for (int index = 0; index < count; index++, bit++)
                value |= (uint)((bytes[bit >> 3] >> (bit & 7)) & 1) << index;
            return true;
        }

        private static void SetState(BoltEventHookStatus status, string detail)
        {
            var state = new BoltStateAdmissionStatus(status, detail);
            Volatile.Write(ref _state, state);
            try { Modding.SetService(ServiceKey, state); } catch (Exception error) { Report(error); }
        }

        private static void Report(Exception error)
        {
            if (Interlocked.Exchange(ref _reported, 1) != 0) return;
            try { RuntimeEntryPoint.LogSource?.LogWarning("Replication admission failed ("
                + error.GetType().FullName + "). Further failures will not be logged."); } catch (Exception) { }
        }
    }

    public sealed record BoltStateAdmissionStatus(BoltEventHookStatus Status, string Detail);

    public sealed class BoltStateAdmissionRequest
    {
        private BoltConnection _source;
        private UdpPacket _packet;
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly int _position;
        private readonly int _size;
        private readonly IntPtr _data;
        private EntityChannel _channel;
        private readonly NetworkId _id;
        private readonly IntPtr _proxy;
        private readonly IntPtr _entity;
        private readonly IntPtr _controller;
        private readonly IntPtr _entitySource;
        private bool _sessionResolved;
        private ServerSession _session;

        internal BoltStateAdmissionRequest(BoltConnection source, UdpPacket packet,
            EntityChannel channel, NetworkId id, EntityProxy proxy, BoltEntitySnapshot existing)
        {
            _source = source; _packet = packet;
            _position = packet.Position; _size = packet.Size;
            _data = packet.Data?.Pointer ?? IntPtr.Zero;
            SourceConnectionId = source.ConnectionId;
            _channel = channel; _id = id;
            _proxy = proxy?.Pointer ?? IntPtr.Zero;
            _entity = proxy?.Entity?.Pointer ?? IntPtr.Zero;
            _controller = proxy?.Entity?.Controller?.Pointer ?? IntPtr.Zero;
            _entitySource = proxy?.Entity?.Source?.Pointer ?? IntPtr.Zero;
            ClaimedNetworkId = id.PackedValue;
            ExistingIncomingEntity = existing;
        }

        public uint SourceConnectionId { get; }
        /// <summary>The wire header's claim, not proof of a known or authorized entity.</summary>
        public ulong ClaimedNetworkId { get; }
        /// <summary>The source connection's incoming proxy at entry, or null. No creation occurs here.</summary>
        public BoltEntitySnapshot ExistingIncomingEntity { get; }
        /// <summary>
        /// True when the claim names no incoming proxy of the source: a creation, a stale update after a
        /// local detach, a reused ID or a claim against an entity the source does not own. Not a verdict.
        /// </summary>
        public bool IsUnknownIncomingClaim => ExistingIncomingEntity == null;
        public bool IsRejected { get; private set; }

        public bool RejectAndDisconnect()
        {
            if (!IsOpen || !BoltReceiveGuard.CanAbort(_packet, _source)) return false;
            IsRejected = true;
            return true;
        }

        public bool TryGetSourceSession(out ServerSession session)
        {
            session = null;
            try
            {
                if (!MatchesContext()) return false;
                if (!_sessionResolved) { _session = ServerSessions.ResolveSource(_source); _sessionResolved = true; }
                if (_session == null || !_session.IsCurrent) return false;
                session = _session;
                return true;
            }
            catch (Exception) { return false; }
        }

        private bool IsOpen => _packet != null && _source != null && Environment.CurrentManagedThreadId == _thread;
        internal bool MatchesContext() => IsOpen && !_packet.WasCollected && !_packet.IsPooled
            && _packet.Position == _position && _packet.Size == _size
            && (_packet.Data?.Pointer ?? IntPtr.Zero) == _data
            && BoltReceiveGuard.IsCurrent(_packet, _source)
            && MatchesTarget()
            && (_session == null || (_session.IsCurrent && ReferenceEquals(_session, ServerSessions.ResolveSource(_source))));

        private bool MatchesTarget()
        {
            var incoming = _channel._incommingDict;
            EntityProxy proxy = null;
            incoming?.TryGetValue(_id, out proxy);
            if ((proxy?.Pointer ?? IntPtr.Zero) != _proxy) return false;
            var entity = proxy?.Entity;
            if ((entity?.Pointer ?? IntPtr.Zero) != _entity) return false;
            var before = ExistingIncomingEntity;
            return before == null ? entity == null : entity != null && !entity.WasCollected
                && entity.NetworkId.PackedValue == before.NetworkId && entity.PrefabId.Value == before.PrefabId
                && entity.IsOwner == before.IsOwner && entity.HasControl == before.HasControl
                && entity.IsAttached == before.IsAttached && entity.Source?.ConnectionId == before.SourceConnectionId
                && entity.Controller?.ConnectionId == before.ControllerConnectionId
                && (entity.Controller?.Pointer ?? IntPtr.Zero) == _controller
                && (entity.Source?.Pointer ?? IntPtr.Zero) == _entitySource;
        }
        internal void Close() { _source = null; _packet = null; _channel = null; }
    }
}
