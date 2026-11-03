// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Numerics;

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        /// <summary>
        /// Copies the host's current transform and Bolt frame counters for an exact player attachment on the
        /// server game thread. A pull read with no hooks; call it inside a request handler and compare
        /// UnityFrame with the request's ReceivedUnityFrame. For a remote player this is the host's interpolated
        /// or extrapolated proxy pose, not the client's own latest sample or where it was when it sent the
        /// event, and no pose decoded from a specific packet is available. Not an atomic replication sample.
        /// </summary>
        public static bool TryReadPlayerPose(PlayerHandle binding,
            out PlayerPoseSnapshot snapshot, out PlayerPoseReadStatus status)
        {
            snapshot = null;
            try
            {
                if (!TryValidatePlayerBinding(binding, out var bindingStatus))
                {
                    status = PlayerPoseStatus(bindingStatus);
                    return false;
                }
                if (!WorldRecords.TryGetCurrent(out var world, out _))
                {
                    status = PlayerPoseReadStatus.WorldUnavailable;
                    return false;
                }
                var frame = UnityEngine.Time.frameCount;
                var player = binding.Player;
                var attachment = binding.Attachment;
                var transform = player.transform;
                if (attachment is null || attachment.WasCollected || transform is null || transform.WasCollected
                    || !transform)
                {
                    status = PlayerPoseReadStatus.EntityUnavailable;
                    return false;
                }

                var position = transform.position;
                var rotation = transform.rotation;
                var boltFrame = BoltNetwork.frame;
                var boltServerFrame = BoltNetwork.serverFrame;
                var lastFrameReceived = attachment.LastFrameReceived;
                var isFrozen = attachment.IsFrozen;
                var updateRate = attachment.UpdateRate;
                var isOwner = attachment.IsOwner;
                var source = player.source;
                int? remoteFrame = source == null || source.WasCollected ? null : source.RemoteFrame;

                if (!TryValidatePlayerBinding(binding, out bindingStatus))
                {
                    status = PlayerPoseStatus(bindingStatus);
                    return false;
                }
                var currentTransform = player.transform;
                var currentSource = player.source;
                if (attachment.WasCollected || transform.WasCollected || currentTransform is null
                    || currentTransform.WasCollected || currentTransform.Pointer != transform.Pointer
                    || (source == null ? currentSource != null
                        : currentSource == null || currentSource.Pointer != source.Pointer)
                    || !world.IsCurrent || UnityEngine.Time.frameCount != frame)
                {
                    status = PlayerPoseReadStatus.ContextChanged;
                    return false;
                }
                if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z)
                    || !float.IsFinite(rotation.x) || !float.IsFinite(rotation.y) || !float.IsFinite(rotation.z)
                    || !float.IsFinite(rotation.w))
                {
                    status = PlayerPoseReadStatus.NonFiniteValues;
                    return false;
                }

                snapshot = new PlayerPoseSnapshot(binding, world, frame,
                    new Vector3(position.x, position.y, position.z),
                    new Quaternion(rotation.x, rotation.y, rotation.z, rotation.w),
                    boltFrame, boltServerFrame, lastFrameReceived, remoteFrame, isFrozen, updateRate, isOwner);
                status = PlayerPoseReadStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                RetireEndedPlayerBinding(binding);
                status = PlayerPoseReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        private static PlayerPoseReadStatus PlayerPoseStatus(WorldReadStatus status) => status switch
        {
            WorldReadStatus.Success => PlayerPoseReadStatus.Success,
            WorldReadStatus.WrongThread => PlayerPoseReadStatus.WrongThread,
            WorldReadStatus.NotServer => PlayerPoseReadStatus.NotServer,
            WorldReadStatus.BindingChanged => PlayerPoseReadStatus.BindingChanged,
            WorldReadStatus.Failed => PlayerPoseReadStatus.Failed,
            _ => PlayerPoseReadStatus.Unavailable
        };
    }

    public enum PlayerPoseReadStatus
    {
        Unavailable,
        Success,
        WrongThread,
        NotServer,
        WorldUnavailable,
        BindingChanged,
        /// <summary>The attachment or transform was missing or destroyed.</summary>
        EntityUnavailable,
        /// <summary>The world, Unity frame, transform or source connection changed during the read.</summary>
        ContextChanged,
        NonFiniteValues,
        Failed
    }

    /// <summary>
    /// The host's transform for one player and the Bolt counters beside it, copied in one Unity frame.
    /// Frozen proxies keep a stale pose, and on-raft players are positioned by the host. Frame numbering of
    /// the raw received-frame fields is not established, so no derived age is offered.
    /// </summary>
    public sealed class PlayerPoseSnapshot
    {
        internal PlayerPoseSnapshot(PlayerHandle binding, WorldRecordContext world, int unityFrame,
            Vector3 position, Quaternion rotation, int boltFrame, int boltServerFrame,
            int rawEntityLastFrameReceived, int? rawConnectionRemoteFrame, bool isFrozen, int rawUpdateRate,
            bool isLocallyOwned)
        {
            Binding = binding;
            World = world;
            UnityFrame = unityFrame;
            Position = position;
            Rotation = rotation;
            BoltFrame = boltFrame;
            BoltServerFrame = boltServerFrame;
            RawEntityLastFrameReceived = rawEntityLastFrameReceived;
            RawConnectionRemoteFrame = rawConnectionRemoteFrame;
            IsFrozen = isFrozen;
            RawUpdateRate = rawUpdateRate;
            IsLocallyOwned = isLocallyOwned;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>The exact input attachment. May retire after the read.</summary>
        public PlayerHandle Binding { get; }
        /// <summary>Host transform position. For a remote player, the proxy's smoothed position.</summary>
        public Vector3 Position { get; }
        public Quaternion Rotation { get; }
        /// <summary>The world lifetime observed during the read, not a reusable save ID.</summary>
        public WorldRecordContext World { get; }
        /// <summary>Unity frame of the read.</summary>
        public int UnityFrame { get; }
        /// <summary>Host BoltNetwork.frame. Bolt fixed steps and Unity frames advance at different rates.</summary>
        public int BoltFrame { get; }
        /// <summary>Raw BoltNetwork.serverFrame.</summary>
        public int BoltServerFrame { get; }
        /// <summary>Raw Bolt entity LastFrameReceived. Meaningless for the locally owned host player.</summary>
        public int RawEntityLastFrameReceived { get; }
        /// <summary>
        /// Raw RemoteFrame of the entity's source connection, which reads _remoteFrameEstimated: a host-stepped
        /// estimate derived from client packet frames, not the client's own counter and not a pose timestamp.
        /// ConnectionSnapshot.RemoteFrameLatest holds the client-written value. Null for the local host player.
        /// </summary>
        public int? RawConnectionRemoteFrame { get; }
        public bool IsFrozen { get; }
        /// <summary>Raw Bolt entity UpdateRate, not a measured send rate.</summary>
        public int RawUpdateRate { get; }
        /// <summary>True for the listen host's own player, whose pose is the local simulation.</summary>
        public bool IsLocallyOwned { get; }
        /// <summary>UTC read completion time, not a simulation or replication timestamp.</summary>
        public DateTimeOffset ObservedAt { get; }
    }
}
