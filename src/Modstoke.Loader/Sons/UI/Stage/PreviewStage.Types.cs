// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace Modstoke.Sons
{
    public sealed class PreviewStageOptions
    {
        /// <summary>Required, e.g. "modstoke.client.character".</summary>
        public string OwnerId;
        /// <summary>Allows creation outside the world. Otherwise the stage disposes itself when the world is exited.</summary>
        public bool AllowTitleScene;
        public Color Background = new(0.11f, 0.11f, 0.12f, 1f);
        /// <summary>Render scale of the viewport, clamped to 0.5-1.25. The texture never exceeds the screen size.</summary>
        public float QualityScale = 1f;
        public int ContinuousMaxHz = 30;
        public PreviewLightRig LightRig = PreviewLightRig.Neutral;
        /// <summary>Opt-in volume overrides. When they cannot be installed the stage renders at the base tier.</summary>
        public bool VolumeOverrides;
    }

    public enum PreviewLightRig
    {
        Neutral,
        None
    }

    public enum PreviewFocus
    {
        Body,
        Face,
        Hands,
        LeftHand,
        RightHand
    }

    public enum PreviewRenderMode
    {
        OnDemand,
        Continuous
    }

    public readonly record struct PreviewCameraState(PreviewFocus Focus, float Yaw, float Pitch,
        float Distance, float MinDistance, float MaxDistance, bool Settling);

    public sealed class PreviewCaptureOptions
    {
        /// <summary>Keeps the pose the player had when captured. Otherwise the copy rests in the rig's bind pose.</summary>
        public bool LivePose;
    }

    public sealed class PreviewBodyRequest
    {
        private readonly PreviewPartRequest[] _parts;

        public PreviewBodyRequest(IEnumerable<PreviewPartRequest> parts, string skeletonPartId = null)
        {
            _parts = parts?.ToArray() ?? Array.Empty<PreviewPartRequest>();
            SkeletonPartId = string.IsNullOrWhiteSpace(skeletonPartId) ? null : skeletonPartId.Trim();
        }

        public IReadOnlyList<PreviewPartRequest> Parts => _parts;
        /// <summary>The part whose own bones become the skeleton. By default the first part that carries bones;
        /// when no part does, parts bind to the local player's rig.</summary>
        public string SkeletonPartId { get; }
    }

    public sealed class PreviewPartRequest
    {
        private PreviewPartRequest(string partId, PreviewPartSource source, object runtimeKey, GameObject prefab,
            string key, string attachBone)
        {
            PartId = partId;
            Source = source;
            RuntimeKey = runtimeKey;
            Prefab = prefab;
            Key = key;
            AttachBone = attachBone;
        }

        public string PartId { get; }
        internal PreviewPartSource Source { get; }
        internal object RuntimeKey { get; }
        internal GameObject Prefab { get; }
        // Parts with the same ID and key are kept across builds.
        internal string Key { get; }
        internal string AttachBone { get; }

        public static PreviewPartRequest FromAddressable(string partId, object runtimeKey)
        {
            string text = runtimeKey switch
            {
                null => null,
                string value => value,
                Il2CppSystem.Object native => native.ToString(),
                _ => runtimeKey.ToString()
            };
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("An Addressables runtime key is required.", nameof(runtimeKey));
            return new PreviewPartRequest(RequireId(partId), PreviewPartSource.Addressable, runtimeKey, null, "key:" + text, null);
        }

        /// <summary>Loads by the reference's RuntimeKey, so the game's own handle on the reference is never touched.</summary>
        public static PreviewPartRequest FromAssetReference(string partId, AssetReference reference)
        {
            if (reference == null) throw new ArgumentNullException(nameof(reference));
            return FromAddressable(partId, reference.RuntimeKey);
        }

        public static PreviewPartRequest FromPrefab(string partId, GameObject prefabAsset)
        {
            if (prefabAsset == null || prefabAsset.WasCollected || !prefabAsset)
                throw new ArgumentNullException(nameof(prefabAsset));
            return new PreviewPartRequest(RequireId(partId), PreviewPartSource.Prefab, null, prefabAsset,
                "prefab:" + prefabAsset.GetInstanceID().ToString(CultureInfo.InvariantCulture), null);
        }

        /// <summary>Parents the part's static meshes to this skeleton bone. Skinned meshes keep their own bones.</summary>
        public PreviewPartRequest AttachTo(string boneName)
        {
            string bone = string.IsNullOrWhiteSpace(boneName) ? null : boneName.Trim();
            return new PreviewPartRequest(PartId, Source, RuntimeKey, Prefab, Key + (bone != null ? "@" + bone : ""), bone);
        }

        private static string RequireId(string partId)
        {
            if (string.IsNullOrWhiteSpace(partId)) throw new ArgumentException("A part ID is required.", nameof(partId));
            return partId.Trim();
        }
    }

    internal enum PreviewPartSource
    {
        Addressable,
        Prefab
    }

    public sealed class PreviewBuildResult
    {
        private PreviewBuildResult(bool succeeded, long buildId, string status, IReadOnlyList<string> rebuiltPartIds,
            IReadOnlyDictionary<string, string> failedParts)
        {
            Succeeded = succeeded;
            BuildId = buildId;
            Status = status;
            RebuiltPartIds = rebuiltPartIds;
            FailedParts = failedParts;
        }

        /// <summary>True when every requested part was built.</summary>
        public bool Succeeded { get; }
        public long BuildId { get; }
        public string Status { get; }
        /// <summary>Parts made new by this build. Kept parts keep their blend-shape and material edits.</summary>
        public IReadOnlyList<string> RebuiltPartIds { get; }
        /// <summary>Part ID to the reason it was left out.</summary>
        public IReadOnlyDictionary<string, string> FailedParts { get; }

        internal static PreviewBuildResult Create(bool succeeded, long buildId, string status,
            IEnumerable<string> rebuiltPartIds, IDictionary<string, string> failedParts)
        {
            return new PreviewBuildResult(succeeded, buildId, status ?? "",
                Array.AsReadOnly(rebuiltPartIds?.ToArray() ?? Array.Empty<string>()),
                new ReadOnlyDictionary<string, string>(failedParts != null
                    ? new Dictionary<string, string>(failedParts, StringComparer.Ordinal)
                    : new Dictionary<string, string>(StringComparer.Ordinal)));
        }
    }

    public static class PreviewStageTiers
    {
        public const string Base = "base";
        public const string VolumeOverrides = "volume-overrides";
    }

    public sealed class PreviewStageSnapshot
    {
        private PreviewStageSnapshot(
            string ownerId,
            bool alive,
            bool visible,
            int width,
            int height,
            string tier,
            string lightLayers,
            int layer,
            string frameSource,
            string litShaderMode,
            string exposure,
            int partCount,
            string lastRenderUtc,
            string status,
            string updatedUtc)
        {
            OwnerId = ownerId;
            Alive = alive;
            Visible = visible;
            Width = width;
            Height = height;
            Tier = tier;
            LightLayers = lightLayers;
            Layer = layer;
            FrameSource = frameSource;
            LitShaderMode = litShaderMode;
            Exposure = exposure;
            PartCount = partCount;
            LastRenderUtc = lastRenderUtc;
            Status = status;
            UpdatedUtc = updatedUtc;
        }

        public string OwnerId { get; }
        public bool Alive { get; }
        public bool Visible { get; }
        public int Width { get; }
        public int Height { get; }
        /// <summary>"base" or "volume-overrides".</summary>
        public string Tier { get; }
        /// <summary>"on" when the stage and its lights use LightLayer7, otherwise "off".</summary>
        public string LightLayers { get; }
        public int Layer { get; }
        /// <summary>"inventory-camera" or "main-camera" for the game camera whose frame settings and volume layers
        /// the stage follows, otherwise "default". A " (asset defaults)" suffix means that camera has no custom
        /// frame settings, so only its volume layers were copied.</summary>
        public string FrameSource { get; }
        /// <summary>"deferred" or "forward" as HDRP rendered the stage, or empty before the first frame.</summary>
        public string LitShaderMode { get; }
        /// <summary>The stage camera's exposure as HDRP rendered it, such as "fixed 10 EV", or empty before the first frame.</summary>
        public string Exposure { get; }
        public int PartCount { get; }
        /// <summary>Round-trip UTC time of the last completed frame, or empty before the first.</summary>
        public string LastRenderUtc { get; }
        public string Status { get; }
        public string UpdatedUtc { get; }

        internal static PreviewStageSnapshot Create(
            string ownerId,
            bool alive,
            bool visible,
            int width,
            int height,
            string tier,
            bool lightLayers,
            int layer,
            string frameSource,
            string litShaderMode,
            string exposure,
            int partCount,
            DateTimeOffset? lastRender,
            string status)
        {
            return new PreviewStageSnapshot(
                ownerId,
                alive,
                visible,
                width,
                height,
                string.IsNullOrWhiteSpace(tier) ? PreviewStageTiers.Base : tier,
                lightLayers ? "on" : "off",
                layer,
                string.IsNullOrWhiteSpace(frameSource) ? "default" : frameSource,
                litShaderMode ?? "",
                exposure ?? "",
                partCount,
                lastRender.HasValue ? lastRender.Value.ToString("O") : "",
                status ?? "",
                DateTimeOffset.UtcNow.ToString("O"));
        }
    }
}
