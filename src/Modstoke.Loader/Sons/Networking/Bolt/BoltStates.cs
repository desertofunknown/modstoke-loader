// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using HarmonyLib;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    /// <summary>Opt-in observations of normal returns from Bolt's replicated-state reader.</summary>
    public static class BoltStates
    {
        public const string ServiceKey = "openacai.sons.boltStates";
        private static BoltStateHookState _state = new(
            BoltStateHookStatus.NotAttempted, "The replicated-state observation hook has not been registered.");
        private static int _installationAttempted;
        private static int _ownerThread;
        private static int _reportedFailure;
        [ThreadStatic] private static bool _observing;

        /// <summary>
        /// Synchronous game-thread notification after decoding and applying or queuing a state update.
        /// This is not completed simulation, a property snapshot, or proof of truthful payloads.
        /// Observer failures, off-thread calls and callback re-entry never cancel native work.
        /// Re-entered reads are not observed. Unsubscribe on mod unload.
        /// </summary>
        public static event Action<BoltStateReceived> Received;

        /// <summary>Registration status only, not evidence of live multiplayer coverage.</summary>
        public static BoltStateHookState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0) return;
            HarmonyLib.Harmony harmony = null;
            try
            {
                if (support == null || !support.SafeForHarmonyPatches || !GameBuild.SupportsServerCore)
                {
                    SetState(BoltStateHookStatus.Disabled,
                        "Replicated-state observation requires safe Harmony support and a recognized game build.");
                    return;
                }

                _ownerThread = Environment.CurrentManagedThreadId;
                SetState(BoltStateHookStatus.Installing, "Registering replicated-state observation.");
                // Native reader: client 0x180957DF0, dedicated server 0x180847E80. Scalar policy has its own qualified hooks.
                var target = AccessTools.DeclaredMethod(typeof(Bolt.NetworkState),
                    nameof(Bolt.NetworkState.Bolt_IEntitySerializer_Read),
                    new[] { typeof(BoltConnection), typeof(UdpKit.UdpPacket), typeof(int) })
                    ?? throw new MissingMethodException("Bolt.NetworkState.Bolt_IEntitySerializer_Read(BoltConnection,UdpPacket,int)");
                if (target.ReturnType != typeof(void))
                    throw new InvalidOperationException("The replicated-state reader signature is unsupported.");

                harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-bolt-states");
                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(BoltStates), nameof(BeforeRead))
                    { priority = HarmonyLib.Priority.Last },
                    postfix: new HarmonyMethod(typeof(BoltStates), nameof(AfterRead))
                    { priority = HarmonyLib.Priority.First });
                SetState(BoltStateHookStatus.Installed,
                    "Replicated-state observation registered; decoded/applied-or-queued, not completed simulation or validated payloads. Live receipt remains unverified.");
            }
            catch (Exception error)
            {
                // Disable callbacks before rollback, including when native unpatching itself fails.
                SetState(BoltStateHookStatus.Failed, "Replicated-state observation registration failed; callbacks are inactive.");
                try { harmony?.UnpatchSelf(); }
                catch (Exception cleanup)
                {
                    SetState(BoltStateHookStatus.Failed,
                        "Replicated-state observation registration and rollback failed; callbacks are inactive, native patch state is uncertain.");
                    Warn(warning, "Replicated-state observation rollback failed: " + cleanup);
                }
                Warn(warning, "Replicated-state observation registration failed: " + error);
            }
        }

        private static void BeforeRead(Bolt.NetworkState __instance, BoltConnection __0,
            UdpKit.UdpPacket __1, int __2, bool __runOriginal, out ReadSnapshot __state)
        {
            __state = default;
            var handlers = Received;
            if (handlers == null || _observing || !__runOriginal
                || State.Status != BoltStateHookStatus.Installed) return;
            if (Environment.CurrentManagedThreadId != _ownerThread) return;

            _observing = true;
            try
            {
                if (__instance == null || __1 == null) return;
                var entity = __instance.Entity;
                var meta = __instance.Meta;
                if (entity == null || meta == null) return;
                __state = new ReadSnapshot
                {
                    Handlers = handlers,
                    SourceConnectionId = __0?.ConnectionId,
                    EntityNetworkId = entity.NetworkId.PackedValue,
                    EntityPrefabId = entity.PrefabId.Value,
                    IsLocallyOwned = entity.IsOwner,
                    EntitySourceConnectionId = entity.Source?.ConnectionId,
                    EntityControllerConnectionId = entity.Controller?.ConnectionId,
                    StateTypeId = meta.TypeId.Value,
                    WireFrame = __2,
                    PacketBitPosition = __1.Ptr,
                    NativeFrameCount = __instance.Frames?._count
                };
            }
            catch (Exception error)
            {
                __state = default;
                ReportFailure(error);
            }
            finally { _observing = false; }
        }

        // A normal-return postfix only: no finalizer converts native exceptions into receipt.
        private static void AfterRead(Bolt.NetworkState __instance, UdpKit.UdpPacket __1,
            bool __runOriginal, ReadSnapshot __state)
        {
            if (__state.Handlers == null || Received == null || _observing || !__runOriginal
                || State.Status != BoltStateHookStatus.Installed) return;
            if (Environment.CurrentManagedThreadId != _ownerThread) return;

            _observing = true;
            try
            {
                var observation = new BoltStateReceived(__state.SourceConnectionId,
                    __state.EntityNetworkId, __state.EntityPrefabId, __state.IsLocallyOwned,
                    __state.EntitySourceConnectionId, __state.EntityControllerConnectionId,
                    __state.StateTypeId, __state.WireFrame, __state.PacketBitPosition, __1.Ptr,
                    __state.NativeFrameCount, __instance.Frames?._count);
                foreach (var handler in Delegate.EnumerateInvocationList(__state.Handlers))
                {
                    try { handler(observation); }
                    catch (Exception error) { ReportFailure(error); }
                }
            }
            catch (Exception error) { ReportFailure(error); }
            finally { _observing = false; }
        }

        private static void SetState(BoltStateHookStatus status, string detail)
        {
            var state = new BoltStateHookState(status, detail);
            Volatile.Write(ref _state, state);
            try { Modding.SetService(ServiceKey, state); }
            catch (Exception error) { ReportFailure(error); }
        }

        private static void Warn(Action<string> warning, string detail)
        {
            try { warning?.Invoke(detail); }
            catch (Exception) { }
        }

        private static void ReportFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0) return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Replicated-state observation failed ("
                    + error.GetType().FullName + "). Native work is unchanged; further observation failures will not be logged.");
            }
            catch (Exception) { }
        }

        private struct ReadSnapshot
        {
            internal Action<BoltStateReceived> Handlers;
            internal uint? SourceConnectionId;
            internal ulong EntityNetworkId;
            internal int EntityPrefabId;
            internal bool IsLocallyOwned;
            internal uint? EntitySourceConnectionId;
            internal uint? EntityControllerConnectionId;
            internal int StateTypeId;
            internal int WireFrame;
            internal int PacketBitPosition;
            internal int? NativeFrameCount;
        }
    }

    public enum BoltStateHookStatus { NotAttempted, Disabled, Installing, Installed, Failed }

    /// <summary>Hook registration only. Installed does not establish live receipt or simulation correctness.</summary>
    public sealed record BoltStateHookState(BoltStateHookStatus Status, string Detail);

    /// <summary>
    /// Immutable scalar copies from a normally returning replicated-state read: decoded/applied-or-queued.
    /// Identity and ownership fields are sampled before the read. Connection IDs may be reused; they
    /// are not authenticated identities. No live wrappers, tokens, packet, or state properties are retained.
    /// Native reading may duplicate/enqueue the last frame, initialize the first, or write the first
    /// predicted frame. Neither a frame count nor state.Storage identifies a latest decoded snapshot.
    /// This record does not establish completed simulation, truthful payloads, or gameplay authority.
    /// </summary>
    public sealed record BoltStateReceived
    {
        internal BoltStateReceived(uint? sourceConnectionId, ulong entityNetworkId,
            int entityPrefabId, bool isLocallyOwned, uint? entitySourceConnectionId,
            uint? entityControllerConnectionId, int stateTypeId, int wireFrame,
            int packetBitPositionBefore, int packetBitPositionAfter,
            int? nativeFrameCountBefore, int? nativeFrameCountAfter)
        {
            SourceConnectionId = sourceConnectionId;
            EntityNetworkId = entityNetworkId;
            EntityPrefabId = entityPrefabId;
            IsLocallyOwned = isLocallyOwned;
            EntitySourceConnectionId = entitySourceConnectionId;
            EntityControllerConnectionId = entityControllerConnectionId;
            StateTypeId = stateTypeId;
            WireFrame = wireFrame;
            PacketBitPositionBefore = packetBitPositionBefore;
            PacketBitPositionAfter = packetBitPositionAfter;
            NativeFrameCountBefore = nativeFrameCountBefore;
            NativeFrameCountAfter = nativeFrameCountAfter;
        }

        /// <summary>Immediate actual connection argument, never inferred from entity ownership/control.</summary>
        public uint? SourceConnectionId { get; }
        public ulong EntityNetworkId { get; }
        public int EntityPrefabId { get; }
        /// <summary>Native Entity.IsOwner at entry, not proof of remote authority.</summary>
        public bool IsLocallyOwned { get; }
        /// <summary>Raw Entity.Source owner route, distinct from the immediate sender; null is not an identity.</summary>
        public uint? EntitySourceConnectionId { get; }
        /// <summary>Raw Entity.Controller connection, distinct from both source connections.</summary>
        public uint? EntityControllerConnectionId { get; }
        public int StateTypeId { get; }
        /// <summary>The reader's wire frame argument, not a completed simulation frame.</summary>
        public int WireFrame { get; }
        public int PacketBitPositionBefore { get; }
        public int PacketBitPositionAfter { get; }
        /// <summary>Signed packet cursor delta for this read, not the size of a validated payload.</summary>
        public long ConsumedBits => (long)PacketBitPositionAfter - PacketBitPositionBefore;
        /// <summary>Native frame-list count at entry, or null when there is no list.</summary>
        public int? NativeFrameCountBefore { get; }
        /// <summary>Native frame-list count at normal return, not a count of completed updates.</summary>
        public int? NativeFrameCountAfter { get; }
    }
}
