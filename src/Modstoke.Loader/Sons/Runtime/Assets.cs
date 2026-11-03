// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Loader;
using UnityEngine;

namespace Modstoke.Sons
{
    public static class Assets
    {
        public const string ServiceKey = "openacai.sons.assets";

        internal const string OpenACAIAssetsDirectoryName = "Assets";
        internal const string OpenACAIAudioDirectoryName = "Audio";
        internal const string OpenACAIModAssetsDirectoryName = "Mods";
        internal const int DefaultMaxListedFiles = 256;
        internal const int MaxListedFiles = 1024;

        private static readonly string[] AssetExtensions =
        {
            ".assetbundle",
            ".bundle",
            ".prefab",
            ".mat",
            ".png",
            ".jpg",
            ".jpeg",
            ".json",
            ".txt"
        };

        private static readonly string[] AudioExtensions =
        {
            ".wav",
            ".ogg",
            ".mp3",
            ".aiff",
            ".aif",
            ".bank"
        };

        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, LoadedAssetBundleHandle> LoadedAssetBundles =
            new Dictionary<string, LoadedAssetBundleHandle>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, LoadedAudioClipHandle> LoadedAudioClips =
            new Dictionary<string, LoadedAudioClipHandle>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, PendingAudioClipLoadHandle> PendingAudioClipLoads =
            new Dictionary<string, PendingAudioClipLoadHandle>(StringComparer.OrdinalIgnoreCase);
        private static AssetsSnapshot _state = AssetsSnapshot.CreateUnknown();
        private static long _refreshedAt;
        private static bool _initialized;
        private static int _ownerThreadId;
        private static int _assetBundleLoadAttemptCount;
        private static int _assetBundleLoadFailureCount;
        private static int _assetBundleUnloadCount;
        private static int _audioClipLoadAttemptCount;
        private static int _audioClipLoadFailureCount;
        private static int _audioClipLoadCompleteCount;
        private static int _audioClipUnloadCount;
        private static string _lastAssetOperation = "initialize";
        private static string _lastAssetError = "";

        public static AssetsSnapshot State
        {
            get
            {
                lock (SyncRoot)
                {
                    return _state;
                }
            }
        }

        public static bool Ready => State.Ready;

        public static ModAssetPath GetModAssetPath(string modId)
        {
            var state = State;
            var normalizedId = NormalizeModId(modId);
            var directory = CombineUnderRoot(state.ModstokeModAssetsDirectory, normalizedId, "asset");
            return new ModAssetPath(
                "asset",
                modId,
                normalizedId,
                directory,
                Directory.Exists(directory));
        }

        public static string GetModAssetDirectory(string modId)
        {
            return GetModAssetPath(modId).Directory;
        }

        public static string EnsureModAssetDirectory(string modId)
        {
            var path = GetModAssetPath(modId);
            EnsureDirectory(path.Directory);
            Refresh();
            return path.Directory;
        }

        public static string GetModAssetFilePath(string modId, string relativePath)
        {
            return ResolveModFilePath(GetModAssetDirectory(modId), relativePath, "asset");
        }

        public static ModAssetPath GetModAudioPath(string modId)
        {
            var state = State;
            var normalizedId = NormalizeModId(modId);
            var directory = CombineUnderRoot(state.ModstokeModAudioDirectory, normalizedId, "audio");
            return new ModAssetPath(
                "audio",
                modId,
                normalizedId,
                directory,
                Directory.Exists(directory));
        }

        public static string GetModAudioDirectory(string modId)
        {
            return GetModAudioPath(modId).Directory;
        }

        public static string EnsureModAudioDirectory(string modId)
        {
            var path = GetModAudioPath(modId);
            EnsureDirectory(path.Directory);
            Refresh();
            return path.Directory;
        }

        public static string GetModAudioFilePath(string modId, string relativePath)
        {
            return ResolveModFilePath(GetModAudioDirectory(modId), relativePath, "audio");
        }

        public static IReadOnlyList<AssetFileSnapshot> ListModAssetFiles(string modId)
        {
            return ListModAssetFiles(modId, "*", DefaultMaxListedFiles);
        }

        public static IReadOnlyList<AssetFileSnapshot> ListModAssetFiles(string modId, string searchPattern, int maxFiles)
        {
            return ListFiles(GetModAssetDirectory(modId), searchPattern, maxFiles, "asset");
        }

        public static IReadOnlyList<AssetFileSnapshot> ListModAudioFiles(string modId)
        {
            return ListModAudioFiles(modId, "*", DefaultMaxListedFiles);
        }

        public static IReadOnlyList<AssetFileSnapshot> ListModAudioFiles(string modId, string searchPattern, int maxFiles)
        {
            return ListFiles(GetModAudioDirectory(modId), searchPattern, maxFiles, "audio");
        }

        public static bool TryFindModAssetFile(string modId, string relativePath, out AssetFileSnapshot file)
        {
            return TryFindModFile(GetModAssetDirectory(modId), relativePath, "asset", out file);
        }

        public static bool TryFindModAudioFile(string modId, string relativePath, out AssetFileSnapshot file)
        {
            return TryFindModFile(GetModAudioDirectory(modId), relativePath, "audio", out file);
        }

        public static IReadOnlyList<LoadedAudioClipSnapshot> GetLoadedAudioClips()
        {
            lock (SyncRoot)
            {
                var snapshots = new List<LoadedAudioClipSnapshot>(LoadedAudioClips.Count);
                foreach (var handle in LoadedAudioClips.Values)
                {
                    if (handle != null)
                    {
                        snapshots.Add(handle.ToSnapshot());
                    }
                }

                snapshots.Sort((left, right) => string.Compare(left.Key, right.Key, StringComparison.OrdinalIgnoreCase));
                return snapshots.ToArray();
            }
        }

        public static IReadOnlyList<PendingAudioClipLoadSnapshot> GetPendingAudioClipLoads()
        {
            lock (SyncRoot)
            {
                var snapshots = new List<PendingAudioClipLoadSnapshot>(PendingAudioClipLoads.Count);
                foreach (var handle in PendingAudioClipLoads.Values)
                {
                    if (handle != null)
                    {
                        snapshots.Add(handle.ToSnapshot());
                    }
                }

                snapshots.Sort((left, right) => string.Compare(left.Key, right.Key, StringComparison.OrdinalIgnoreCase));
                return snapshots.ToArray();
            }
        }

        public static bool TryBeginLoadAudioClip(
            string modId,
            string relativePath,
            out AssetLoadResult result)
        {
            var normalizedId = NormalizeModId(modId);
            lock (SyncRoot)
            {
                _audioClipLoadAttemptCount++;
                _lastAssetOperation = "begin-load-audioclip";
                _lastAssetError = "";
            }

            string normalizedRelativePath;
            try
            {
                EnsureRuntimeThread();
                normalizedRelativePath = NormalizeRelativeAssetPath(relativePath, "audio");
            }
            catch (Exception ex)
            {
                result = FailAudioClipLoad("begin-load-audioclip", modId, normalizedId, relativePath, null, ex.Message);
                return false;
            }

            var key = CreateAssetKey(normalizedId, normalizedRelativePath);
            lock (SyncRoot)
            {
                LoadedAudioClipHandle loaded;
                if (LoadedAudioClips.TryGetValue(key, out loaded) && loaded != null && loaded.Clip != null)
                {
                    result = AssetLoadResult.Succeeded(
                        "begin-load-audioclip",
                        modId,
                        normalizedId,
                        normalizedRelativePath,
                        loaded.FullPath,
                        true,
                        "AudioClip is already loaded by Modstoke.");
                    return true;
                }

                PendingAudioClipLoadHandle pending;
                if (PendingAudioClipLoads.TryGetValue(key, out pending) && pending != null)
                {
                    if (pending.Canceled)
                    {
                        result = AssetLoadResult.Failed("begin-load-audioclip", modId, normalizedId,
                            normalizedRelativePath, pending.FullPath, "The previous AudioClip request still needs disposal. Retry unloading it before loading again.");
                        return false;
                    }
                    result = AssetLoadResult.Pending(
                        "begin-load-audioclip",
                        modId,
                        normalizedId,
                        normalizedRelativePath,
                        pending.FullPath,
                        true,
                        "AudioClip load is already pending in Modstoke.");
                    return true;
                }
            }

            string fullPath;
            try
            {
                fullPath = ResolveModFilePath(GetModAudioDirectory(modId), normalizedRelativePath, "audio");
            }
            catch (Exception ex)
            {
                result = FailAudioClipLoad("begin-load-audioclip", modId, normalizedId, normalizedRelativePath, null, ex.Message);
                return false;
            }

            if (!File.Exists(fullPath))
            {
                result = FailAudioClipLoad("begin-load-audioclip", modId, normalizedId, normalizedRelativePath, fullPath, "Audio file does not exist.");
                return false;
            }

            if (!IsAudioClipFile(fullPath))
            {
                result = FailAudioClipLoad("begin-load-audioclip", modId, normalizedId, normalizedRelativePath, fullPath, "Only .wav, .ogg, .mp3, .aiff, and .aif files can be loaded as AudioClips; use AudioBanks for .bank files.");
                return false;
            }

            var audioType = GetAudioType(fullPath);
            var request = new AudioClipFileRequest();
            try
            {
                request.Start(fullPath);
            }
            catch (Exception ex)
            {
                var cleanupError = DisposeAudioRequest(request);
                if (cleanupError != null)
                {
                    lock (SyncRoot)
                    {
                        PendingAudioClipLoads[key] = new PendingAudioClipLoadHandle(key, modId, normalizedId,
                            normalizedRelativePath, fullPath, audioType.ToString(), request, DateTimeOffset.UtcNow.ToString("O"))
                        { Canceled = true };
                    }
                }
                result = FailAudioClipLoad("begin-load-audioclip", modId, normalizedId, normalizedRelativePath, fullPath,
                    CombineErrors(ex.Message, cleanupError));
                return false;
            }

            lock (SyncRoot)
            {
                PendingAudioClipLoads[key] = new PendingAudioClipLoadHandle(
                    key,
                    modId,
                    normalizedId,
                    normalizedRelativePath,
                    fullPath,
                    audioType.ToString(),
                    request,
                    DateTimeOffset.UtcNow.ToString("O"));
                _lastAssetOperation = "begin-load-audioclip";
                _lastAssetError = "";
            }

            Refresh();
            result = AssetLoadResult.Pending(
                "begin-load-audioclip",
                modId,
                normalizedId,
                normalizedRelativePath,
                fullPath,
                false,
                "AudioClip load request started through the Modstoke audio service.");
            return true;
        }

        public static bool TryGetLoadedAudioClip(
            string modId,
            string relativePath,
            out AudioClip audioClip,
            out AssetLoadResult result)
        {
            audioClip = null;
            var normalizedId = NormalizeModId(modId);
            string normalizedRelativePath;
            try
            {
                EnsureRuntimeThread();
                normalizedRelativePath = NormalizeRelativeAssetPath(relativePath, "audio");
            }
            catch (Exception ex)
            {
                result = AssetLoadResult.Failed("get-audioclip", modId, normalizedId, relativePath, null, ex.Message);
                return false;
            }

            var key = CreateAssetKey(normalizedId, normalizedRelativePath);
            lock (SyncRoot)
            {
                LoadedAudioClipHandle loaded;
                if (LoadedAudioClips.TryGetValue(key, out loaded) && loaded != null && loaded.Clip != null)
                {
                    audioClip = loaded.Clip;
                    result = AssetLoadResult.Succeeded(
                        "get-audioclip",
                        modId,
                        normalizedId,
                        normalizedRelativePath,
                        loaded.FullPath,
                        true,
                        "AudioClip is loaded by Modstoke.");
                    return true;
                }

                PendingAudioClipLoadHandle pending;
                if (PendingAudioClipLoads.TryGetValue(key, out pending) && pending != null)
                {
                    if (pending.Canceled)
                    {
                        result = AssetLoadResult.Failed("get-audioclip", modId, normalizedId,
                            normalizedRelativePath, pending.FullPath, "AudioClip load was canceled; request disposal is still pending.");
                        return false;
                    }
                    result = AssetLoadResult.Pending(
                        "get-audioclip",
                        modId,
                        normalizedId,
                        normalizedRelativePath,
                        pending.FullPath,
                        true,
                        "AudioClip load is still pending.");
                    return false;
                }
            }

            result = AssetLoadResult.Failed(
                "get-audioclip",
                modId,
                normalizedId,
                normalizedRelativePath,
                null,
                "AudioClip is not loaded by Modstoke.");
            return false;
        }

        public static bool TryUnloadAudioClip(
            string modId,
            string relativePath,
            out AssetLoadResult result)
        {
            var normalizedId = NormalizeModId(modId);
            string normalizedRelativePath;
            try
            {
                EnsureRuntimeThread();
                normalizedRelativePath = NormalizeRelativeAssetPath(relativePath, "audio");
            }
            catch (Exception ex)
            {
                result = AssetLoadResult.Failed("unload-audioclip", modId, normalizedId, relativePath, null, ex.Message);
                return false;
            }

            var key = CreateAssetKey(normalizedId, normalizedRelativePath);
            LoadedAudioClipHandle handle = null;
            PendingAudioClipLoadHandle pending = null;
            lock (SyncRoot)
            {
                _lastAssetOperation = "unload-audioclip";
                _lastAssetError = "";
                LoadedAudioClips.TryGetValue(key, out handle);

                PendingAudioClipLoads.TryGetValue(key, out pending);
            }

            var cleanupError = CancelAudioClipLoad(pending);

            if (handle == null)
            {
                if (cleanupError != null)
                {
                    result = RecordAudioClipUnloadFailure(modId, normalizedId, normalizedRelativePath, pending.FullPath, cleanupError);
                    return false;
                }

                if (pending == null)
                {
                    result = RecordAudioClipUnloadFailure(modId, normalizedId, normalizedRelativePath, null,
                        "AudioClip was not loaded by Modstoke.");
                    return false;
                }
                result = AssetLoadResult.Succeeded("unload-audioclip", modId, normalizedId,
                    normalizedRelativePath, pending.FullPath, false, "Pending AudioClip load was canceled before completion.");
                Refresh();
                return true;
            }

            if (!TryUnloadAudioClipHandle(handle, out var unloadError))
            {
                result = RecordAudioClipUnloadFailure(modId, normalizedId, normalizedRelativePath, handle.FullPath,
                    CombineErrors(unloadError, cleanupError));
                return false;
            }

            if (cleanupError != null)
            {
                result = RecordAudioClipUnloadFailure(modId, normalizedId, normalizedRelativePath, handle.FullPath, cleanupError);
                return false;
            }

            Refresh();
            result = AssetLoadResult.Succeeded(
                "unload-audioclip",
                modId,
                normalizedId,
                normalizedRelativePath,
                handle.FullPath,
                false,
                "AudioClip unloaded through the Modstoke audio service.");
            return true;
        }

        public static int UnloadModAudioClips(string modId)
        {
            EnsureRuntimeThread();
            var normalizedId = NormalizeModId(modId);
            var loaded = new List<LoadedAudioClipHandle>();
            var pendingRemoved = new List<PendingAudioClipLoadHandle>();

            lock (SyncRoot)
            {
                foreach (var pair in LoadedAudioClips)
                {
                    if (pair.Value != null
                        && string.Equals(pair.Value.NormalizedModId, normalizedId, StringComparison.OrdinalIgnoreCase))
                    {
                        loaded.Add(pair.Value);
                    }
                }

                foreach (var pair in PendingAudioClipLoads)
                {
                    if (pair.Value != null
                        && string.Equals(pair.Value.NormalizedModId, normalizedId, StringComparison.OrdinalIgnoreCase))
                    {
                        pendingRemoved.Add(pair.Value);
                    }
                }

                _lastAssetOperation = "unload-mod-audioclips";
                _lastAssetError = "";
            }

            foreach (var handle in pendingRemoved)
            {
                var error = CancelAudioClipLoad(handle);
                if (error != null)
                {
                    lock (SyncRoot)
                    {
                        _lastAssetError = error;
                    }
                }
            }

            var unloaded = 0;
            foreach (var handle in loaded)
            {
                if (TryUnloadAudioClipHandle(handle, out var error))
                {
                    unloaded++;
                }
                else
                {
                    lock (SyncRoot)
                    {
                        _lastAssetError = error;
                    }
                }
            }

            Refresh();

            return unloaded;
        }

        public static IReadOnlyList<LoadedAssetBundleSnapshot> GetLoadedAssetBundles()
        {
            lock (SyncRoot)
            {
                var snapshots = new List<LoadedAssetBundleSnapshot>(LoadedAssetBundles.Count);
                foreach (var handle in LoadedAssetBundles.Values)
                {
                    if (handle != null)
                    {
                        snapshots.Add(handle.ToSnapshot());
                    }
                }

                snapshots.Sort((left, right) => string.Compare(left.Key, right.Key, StringComparison.OrdinalIgnoreCase));
                return snapshots.ToArray();
            }
        }

        /// <summary>
        /// Inspects the native path index of one bundle already loaded by this service.
        /// Requires the Unity lifecycle thread. Does not load assets or scenes, scan files,
        /// or take ownership of resources. Each successful inspection checks bundle liveness;
        /// the immutable path index is read once per loaded bundle instance.
        /// A nonpositive maxPaths lists up to 256 paths; all listings are capped at 1024.
        /// Exact-path queries on the returned snapshot use the complete index.
        /// </summary>
        public static bool TryInspectLoadedAssetBundle(
            string modId,
            string relativePath,
            int maxPaths,
            out AssetBundleContentsSnapshot contents,
            out AssetLoadResult result)
        {
            const string operation = "inspect-assetbundle";
            contents = null;
            string normalizedId = null;
            string normalizedRelativePath = null;
            LoadedAssetBundleHandle handle = null;
            try
            {
                EnsureRuntimeThread();
                normalizedId = NormalizeModId(modId);
                normalizedRelativePath = NormalizeRelativeAssetPath(relativePath, "asset");
                lock (SyncRoot)
                {
                    LoadedAssetBundles.TryGetValue(CreateAssetKey(normalizedId, normalizedRelativePath), out handle);
                }

                if (handle == null || handle.Bundle == null)
                {
                    result = AssetLoadResult.Unavailable(
                        operation, modId, normalizedId, normalizedRelativePath, handle?.FullPath,
                        handle == null
                            ? "AssetBundle is not currently loaded by the Modstoke asset service."
                            : "The registered AssetBundle no longer has a live native object.");
                    return false;
                }

                handle.EnsureContentIndex();
                var limit = Math.Max(1, Math.Min(MaxListedFiles, maxPaths <= 0 ? DefaultMaxListedFiles : maxPaths));
                contents = new AssetBundleContentsSnapshot(
                    handle.ToSnapshot(), handle.IsSceneBundle, handle.ContentPaths, limit,
                    handle.ContentIndexUtc, DateTimeOffset.UtcNow.ToString("O"));
                result = AssetLoadResult.Succeeded(
                    operation, modId, normalizedId, normalizedRelativePath, handle.FullPath, true,
                    "Native bundle is available. Paths describe bundle contents, not loaded asset objects or scenes."
                    + (contents.Truncated ? " The returned path list is truncated; exact-path queries use the complete index." : ""));
                return true;
            }
            catch (Exception ex)
            {
                contents = null;
                result = AssetLoadResult.Failed(
                    operation, modId, normalizedId, normalizedRelativePath ?? relativePath, handle?.FullPath, ex.Message);
                return false;
            }
        }

        /// <summary>Loads one named mesh from a registered bundle and copies its metadata.</summary>
        /// <remarks>
        /// Requires the Unity lifecycle thread. The asset path must match the complete bundle
        /// index exactly, including case and slashes. Loading is a side effect, even if later
        /// inspection fails. The mesh is borrowed from the bundle; this operation never destroys
        /// it or unloads the bundle. Explicit bundle unload policy still controls loaded assets.
        /// The returned snapshot contains no Unity objects and does not track later changes.
        /// </remarks>
        public static bool TryInspectMeshAsset(
            string modId,
            string bundlePath,
            string assetPath,
            out MeshAssetSnapshot snapshot,
            out AssetLoadResult result)
        {
            const string operation = "inspect-mesh-asset";
            snapshot = null;
            string normalizedId = null;
            string normalizedBundlePath = null;
            LoadedAssetBundleHandle handle = null;
            try
            {
                EnsureRuntimeThread();
                normalizedId = NormalizeModId(modId);
                normalizedBundlePath = NormalizeRelativeAssetPath(bundlePath, "asset");
                lock (SyncRoot)
                {
                    LoadedAssetBundles.TryGetValue(CreateAssetKey(normalizedId, normalizedBundlePath), out handle);
                }

                if (handle == null || handle.Bundle == null)
                {
                    result = AssetLoadResult.Unavailable(
                        operation, modId, normalizedId, normalizedBundlePath, handle?.FullPath,
                        handle == null
                            ? "AssetBundle is not currently loaded by the asset service."
                            : "The registered AssetBundle no longer has a live native object.");
                    return false;
                }

                handle.EnsureContentIndex();
                if (handle.IsSceneBundle)
                {
                    result = AssetLoadResult.Unavailable(
                        operation, modId, normalizedId, normalizedBundlePath, handle.FullPath,
                        "Scene AssetBundles do not support named mesh asset inspection.");
                    return false;
                }

                if (assetPath == null || Array.BinarySearch(handle.ContentPaths, assetPath, StringComparer.Ordinal) < 0)
                {
                    result = AssetLoadResult.Unavailable(
                        operation, modId, normalizedId, normalizedBundlePath, handle.FullPath,
                        "The exact asset path is absent from the complete bundle index: " + assetPath);
                    return false;
                }

                var mesh = handle.Bundle.LoadAsset<Mesh>(assetPath);
                if (mesh == null)
                {
                    result = AssetLoadResult.Unavailable(
                        operation, modId, normalizedId, normalizedBundlePath, handle.FullPath,
                        "The indexed asset did not load as a live Mesh: " + assetPath);
                    return false;
                }

                var vertexCount = mesh.vertexCount;
                var subMeshCount = mesh.subMeshCount;
                var isReadable = mesh.isReadable;
                var bounds = mesh.bounds;
                var center = bounds.center;
                var extents = bounds.extents;
                snapshot = new MeshAssetSnapshot(
                    handle.ToSnapshot(), assetPath, vertexCount, subMeshCount, isReadable,
                    new System.Numerics.Vector3(center.x, center.y, center.z),
                    new System.Numerics.Vector3(extents.x, extents.y, extents.z),
                    DateTimeOffset.UtcNow.ToString("O"));
                result = AssetLoadResult.Succeeded(
                    operation, modId, normalizedId, normalizedBundlePath, handle.FullPath, false,
                    "Mesh metadata copied. The loaded asset remains subject to explicit bundle unload policy.");
                return true;
            }
            catch (Exception ex)
            {
                snapshot = null;
                result = AssetLoadResult.Failed(
                    operation, modId, normalizedId, normalizedBundlePath ?? bundlePath, handle?.FullPath, ex.Message);
                return false;
            }
        }

        public static bool TryLoadAssetBundle(
            string modId,
            string relativePath,
            out AssetBundle assetBundle,
            out AssetLoadResult result)
        {
            assetBundle = null;
            var normalizedId = NormalizeModId(modId);
            lock (SyncRoot)
            {
                _assetBundleLoadAttemptCount++;
                _lastAssetOperation = "load-assetbundle";
                _lastAssetError = "";
            }

            string normalizedRelativePath;
            try
            {
                EnsureRuntimeThread();
                normalizedRelativePath = NormalizeRelativeAssetPath(relativePath, "asset");
            }
            catch (Exception ex)
            {
                result = FailAssetBundleLoad("load-assetbundle", modId, normalizedId, relativePath, null, ex.Message);
                return false;
            }

            var key = CreateAssetKey(normalizedId, normalizedRelativePath);

            lock (SyncRoot)
            {
                LoadedAssetBundleHandle existing;
                if (LoadedAssetBundles.TryGetValue(key, out existing) && existing != null && existing.Bundle != null)
                {
                    assetBundle = existing.Bundle;
                    result = AssetLoadResult.Succeeded(
                        "load-assetbundle",
                        modId,
                        normalizedId,
                        normalizedRelativePath,
                        existing.FullPath,
                        true,
                        "AssetBundle is already loaded by Modstoke.");
                    return true;
                }
            }

            string fullPath;
            try
            {
                fullPath = ResolveModFilePath(GetModAssetDirectory(modId), normalizedRelativePath, "asset");
            }
            catch (Exception ex)
            {
                result = FailAssetBundleLoad("load-assetbundle", modId, normalizedId, normalizedRelativePath, null, ex.Message);
                return false;
            }

            if (!File.Exists(fullPath))
            {
                result = FailAssetBundleLoad("load-assetbundle", modId, normalizedId, normalizedRelativePath, fullPath, "Asset file does not exist.");
                return false;
            }

            if (!IsAssetBundleFile(fullPath))
            {
                result = FailAssetBundleLoad("load-assetbundle", modId, normalizedId, normalizedRelativePath, fullPath, "Only .assetbundle and .bundle files can be loaded as AssetBundles.");
                return false;
            }

            try
            {
                assetBundle = AssetBundle.LoadFromFile(fullPath);
            }
            catch (Exception ex)
            {
                result = FailAssetBundleLoad("load-assetbundle", modId, normalizedId, normalizedRelativePath, fullPath, ex.Message);
                return false;
            }

            if (assetBundle == null)
            {
                result = FailAssetBundleLoad("load-assetbundle", modId, normalizedId, normalizedRelativePath, fullPath, "Unity returned null while loading the AssetBundle.");
                return false;
            }

            lock (SyncRoot)
            {
                LoadedAssetBundles[key] = new LoadedAssetBundleHandle(
                    key,
                    modId,
                    normalizedId,
                    normalizedRelativePath,
                    fullPath,
                    assetBundle,
                    DateTimeOffset.UtcNow.ToString("O"));
                _lastAssetOperation = "load-assetbundle";
                _lastAssetError = "";
            }

            Refresh();
            result = AssetLoadResult.Succeeded(
                "load-assetbundle",
                modId,
                normalizedId,
                normalizedRelativePath,
                fullPath,
                false,
                "AssetBundle loaded through the Modstoke asset service.");
            return true;
        }

        public static bool TryUnloadAssetBundle(
            string modId,
            string relativePath,
            bool unloadAllLoadedObjects,
            out AssetLoadResult result)
        {
            var normalizedId = NormalizeModId(modId);
            string normalizedRelativePath;
            try
            {
                EnsureRuntimeThread();
                normalizedRelativePath = NormalizeRelativeAssetPath(relativePath, "asset");
            }
            catch (Exception ex)
            {
                result = AssetLoadResult.Failed(
                    "unload-assetbundle",
                    modId,
                    normalizedId,
                    relativePath,
                    null,
                    ex.Message);
                return false;
            }

            var key = CreateAssetKey(normalizedId, normalizedRelativePath);
            LoadedAssetBundleHandle handle = null;

            lock (SyncRoot)
            {
                _lastAssetOperation = "unload-assetbundle";
                _lastAssetError = "";
                LoadedAssetBundles.TryGetValue(key, out handle);
            }

            if (handle == null)
            {
                result = RecordUnloadFailure(modId, normalizedId, normalizedRelativePath, null,
                    "AssetBundle was not loaded by Modstoke.");
                return false;
            }

            if (!TryUnloadAssetBundleHandle(handle, unloadAllLoadedObjects, out var error))
            {
                result = RecordUnloadFailure(modId, normalizedId, normalizedRelativePath, handle.FullPath, error);
                return false;
            }

            Refresh();
            result = AssetLoadResult.Succeeded(
                "unload-assetbundle",
                modId,
                normalizedId,
                normalizedRelativePath,
                handle.FullPath,
                false,
                "AssetBundle unloaded through the Modstoke asset service.");
            return true;
        }

        public static int UnloadModAssetBundles(string modId, bool unloadAllLoadedObjects)
        {
            EnsureRuntimeThread();
            var normalizedId = NormalizeModId(modId);
            var loaded = new List<LoadedAssetBundleHandle>();

            lock (SyncRoot)
            {
                foreach (var pair in LoadedAssetBundles)
                {
                    if (pair.Value != null
                        && string.Equals(pair.Value.NormalizedModId, normalizedId, StringComparison.OrdinalIgnoreCase))
                    {
                        loaded.Add(pair.Value);
                    }
                }

                _lastAssetOperation = "unload-mod-assetbundles";
                _lastAssetError = "";
            }

            var unloaded = 0;
            foreach (var handle in loaded)
            {
                if (TryUnloadAssetBundleHandle(handle, unloadAllLoadedObjects, out var error))
                {
                    unloaded++;
                }
                else
                {
                    lock (SyncRoot)
                    {
                        _lastAssetError = error;
                    }
                }
            }

            Refresh();

            return unloaded;
        }

        internal static void Tick()
        {
            EnsureRuntimeThread();
            PendingAudioClipLoadHandle[] pendingLoads;
            lock (SyncRoot)
            {
                var activeCount = 0;
                foreach (var pending in PendingAudioClipLoads.Values)
                    if (!pending.Canceled)
                        activeCount++;
                if (activeCount == 0)
                    return;
                pendingLoads = new PendingAudioClipLoadHandle[activeCount];
                var index = 0;
                foreach (var pending in PendingAudioClipLoads.Values)
                    if (!pending.Canceled)
                        pendingLoads[index++] = pending;
            }

            var changed = false;
            foreach (var pending in pendingLoads)
            {
                if (pending.Canceled)
                {
                    // Retain failed disposal for explicit unload retry, without retrying a fault every frame.
                    continue;
                }
                string pollError = null;
                try
                {
                    if (pending.Request != null && !pending.Request.IsDone)
                    {
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    pollError = ex.Message;
                }

                lock (SyncRoot)
                {
                    if (!PendingAudioClipLoads.TryGetValue(pending.Key, out var current)
                        || !ReferenceEquals(current, pending))
                    {
                        continue;
                    }

                    PendingAudioClipLoads.Remove(pending.Key);
                }

                changed = true;
                if (pollError == null)
                {
                    CompleteAudioClipLoad(pending);
                }
                else
                {
                    RecordAudioClipCompletionFailure(pending, CombineErrors(pollError, FinishAudioRequest(pending)));
                }
            }

            if (changed)
            {
                Refresh();
            }
        }

        public static AssetsSnapshot Refresh()
        {
            var snapshot = AssetsSnapshot.Create();
            Publish(snapshot);
            _refreshedAt = Environment.TickCount64;
            return snapshot;
        }

        internal static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            _ownerThreadId = Environment.CurrentManagedThreadId;
            Publish(AssetsSnapshot.CreateUnknown());
            if (!MenuRegistry.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Modstoke SDK asset provider could not be registered.");
            }
        }

        internal static string NormalizeModId(string modId)
        {
            return SaveData.NormalizeModId(modId);
        }

        private static void EnsureRuntimeThread()
        {
            if (_ownerThreadId == 0 || Environment.CurrentManagedThreadId != _ownerThreadId)
                throw new InvalidOperationException("Asset runtime operations require the initialized Unity lifecycle thread.");
        }

        private static void Publish(AssetsSnapshot snapshot)
        {
            lock (SyncRoot)
            {
                _state = snapshot ?? AssetsSnapshot.CreateUnknown();
                Modding.SetService(ServiceKey, _state);
            }
        }

        private static MenuContentSnapshot CreateContent()
        {
            // Open menu pages poll this, so a scan from the last two seconds is reused instead of walking the folders again.
            var snapshot = Environment.TickCount64 - _refreshedAt < 2000 ? State : Refresh();
            return new MenuContentSnapshot(
                "Sons Assets",
                "Per-mod asset and audio paths for Sons Of The Forest.",
                new[]
                {
                    CreateStorageSection(snapshot),
                    CreateSdkSection(snapshot),
                    CreateLoadedBundlesSection(snapshot)
                });
        }

        private static MenuSectionSnapshot CreateStorageSection(AssetsSnapshot snapshot)
        {
            return new MenuSectionSnapshot(
                "Asset Storage",
                new[]
                {
                    Row("Service", snapshot.ServiceKey, snapshot.Status, snapshot.Detail),
                    Row("Storage", snapshot.StorageModel, snapshot.Ready ? "READY" : "CHECK", "Deterministic Modstoke UserData asset roots."),
                    Row("Assets Root", ShortPath(snapshot.ModstokeAssetsDirectory), snapshot.ModstokeAssetsDirectoryExists ? "READY" : "CHECK", snapshot.ModstokeAssetsDirectory),
                    Row("Mod Assets", ShortPath(snapshot.ModstokeModAssetsDirectory), snapshot.ModstokeModAssetsDirectoryExists ? "READY" : "CHECK", snapshot.ModstokeModAssetsDirectory),
                    Row("Audio Root", ShortPath(snapshot.ModstokeAudioDirectory), snapshot.ModstokeAudioDirectoryExists ? "READY" : "CHECK", snapshot.ModstokeAudioDirectory),
                    Row("Mod Audio", ShortPath(snapshot.ModstokeModAudioDirectory), snapshot.ModstokeModAudioDirectoryExists ? "READY" : "CHECK", snapshot.ModstokeModAudioDirectory)
                });
        }

        private static MenuSectionSnapshot CreateSdkSection(AssetsSnapshot snapshot)
        {
            return new MenuSectionSnapshot(
                "SDK Surface",
                new[]
                {
                    Row("Asset Pattern", ShortPath(snapshot.DefaultModAssetDirectoryPattern), "INFO", snapshot.DefaultModAssetDirectoryPattern),
                    Row("Audio Pattern", ShortPath(snapshot.DefaultModAudioDirectoryPattern), "INFO", snapshot.DefaultModAudioDirectoryPattern),
                    Row("Listing Limit", snapshot.DefaultMaxListedFiles + "/" + snapshot.MaxListedFiles, "INFO", "Bounded file listings protect menu/state refreshes."),
                    Row("Asset Files", snapshot.AssetFileCount + " files", snapshot.AssetFileLimitReached ? "CHECK" : "INFO", "Supported files discovered under the Modstoke per-mod asset root."),
                    Row("AssetBundles", snapshot.AssetBundleFileCount + " candidates | " + snapshot.LoadedAssetBundleCount + " loaded", snapshot.AssetBundleLoadFailureCount == 0 ? "READY" : "CHECK", "AssetBundle load/unload is routed through Modstoke helper methods."),
                    Row("Audio Files", snapshot.AudioFileCount + " files", snapshot.AudioFileLimitReached ? "CHECK" : "INFO", "Supported audio files discovered under the Modstoke per-mod audio root."),
                    Row("Asset Loading", snapshot.RuntimeAssetLoadingEnabled ? "AssetBundle.LoadFromFile" : "Path-only", snapshot.RuntimeAssetLoadingEnabled ? "READY" : "INFO", "Modstoke exposes safe per-mod AssetBundle load/unload helpers."),
                    Row("Audio Loading", snapshot.RuntimeAudioLoadingEnabled ? "FMOD-decoded AudioClip" : "Discovery-only", snapshot.RuntimeAudioLoadingEnabled ? "READY" : "INFO", "Modstoke exposes non-blocking AudioClip helpers and a separate FMOD bank metadata service."),
                    Row("Audio Clips", snapshot.LoadedAudioClipCount + " loaded | " + snapshot.PendingAudioClipLoadCount + " pending", snapshot.AudioClipLoadFailureCount == 0 ? "READY" : "CHECK", "AudioClip loads are pumped by the Modstoke runtime driver."),
                    Row("Load Attempts", snapshot.AssetBundleLoadAttemptCount.ToString(), "INFO", "Modstoke AssetBundle load attempts this run."),
                    Row("Load Failures", snapshot.AssetBundleLoadFailureCount.ToString(), snapshot.AssetBundleLoadFailureCount == 0 ? "READY" : "CHECK", string.IsNullOrWhiteSpace(snapshot.LastAssetError) ? "No AssetBundle load/unload errors recorded." : snapshot.LastAssetError),
                    Row("Audio Attempts", snapshot.AudioClipLoadAttemptCount.ToString(), "INFO", "Modstoke AudioClip load requests this run."),
                    Row("Audio Failures", snapshot.AudioClipLoadFailureCount.ToString(), snapshot.AudioClipLoadFailureCount == 0 ? "READY" : "CHECK", string.IsNullOrWhiteSpace(snapshot.LastAssetError) ? "No AudioClip load/unload errors recorded." : snapshot.LastAssetError),
                    Row("Unload Ops", snapshot.AssetBundleUnloadCount.ToString(), "INFO", "Modstoke AssetBundle unload operations this run."),
                    Row("Audio Unloads", snapshot.AudioClipUnloadCount.ToString(), "INFO", "Modstoke AudioClip unload operations this run."),
                    Row("Last Operation", string.IsNullOrWhiteSpace(snapshot.LastAssetOperation) ? "None" : snapshot.LastAssetOperation, "INFO", "Most recent asset helper operation.")
                });
        }

        private static MenuSectionSnapshot CreateLoadedBundlesSection(AssetsSnapshot snapshot)
        {
            var rows = new List<MenuRowSnapshot>();
            foreach (var bundle in snapshot.LoadedAssetBundles)
            {
                rows.Add(Row(
                    bundle.RelativePath,
                    bundle.NormalizedModId,
                    "READY",
                    string.IsNullOrWhiteSpace(bundle.LoadedUtc) ? bundle.FullPath : bundle.FullPath + " loaded " + bundle.LoadedUtc));
            }

            if (rows.Count == 0)
            {
                rows.Add(Row("Entries", "None", "INFO", "No AssetBundles are loaded through Modstoke."));
            }

            return new MenuSectionSnapshot("Loaded AssetBundles", rows);
        }

        private static IReadOnlyList<AssetFileSnapshot> ListFiles(string root, string searchPattern, int maxFiles, string kind)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return Array.Empty<AssetFileSnapshot>();
            }

            var pattern = NormalizeSearchPattern(searchPattern);
            var limit = Math.Max(0, Math.Min(maxFiles <= 0 ? DefaultMaxListedFiles : maxFiles, MaxListedFiles));
            var files = new List<AssetFileSnapshot>();
            var fullRoot = EnsureTrailingSeparator(Path.GetFullPath(root));

            foreach (var file in Directory.EnumerateFiles(fullRoot, pattern, SearchOption.AllDirectories))
            {
                if (files.Count >= limit)
                {
                    break;
                }

                var fullPath = Path.GetFullPath(file);
                if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                FileInfo info;
                try
                {
                    info = new FileInfo(fullPath);
                }
                catch
                {
                    continue;
                }

                files.Add(new AssetFileSnapshot(
                    kind,
                    fullPath.Substring(fullRoot.Length).Replace(Path.DirectorySeparatorChar, '/'),
                    fullPath,
                    info.Length,
                    info.LastWriteTimeUtc.ToString("O")));
            }

            files.Sort((left, right) => string.Compare(left.RelativePath, right.RelativePath, StringComparison.OrdinalIgnoreCase));
            return files.ToArray();
        }

        private static bool TryFindModFile(string modDirectory, string relativePath, string kind, out AssetFileSnapshot file)
        {
            file = null;
            string fullPath;
            try
            {
                fullPath = ResolveModFilePath(modDirectory, relativePath, kind);
            }
            catch
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
            {
                return false;
            }

            var fullRoot = EnsureTrailingSeparator(Path.GetFullPath(modDirectory));
            var normalizedFullPath = Path.GetFullPath(fullPath);
            if (!normalizedFullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            FileInfo info;
            try
            {
                info = new FileInfo(normalizedFullPath);
            }
            catch
            {
                return false;
            }

            file = new AssetFileSnapshot(
                kind,
                normalizedFullPath.Substring(fullRoot.Length).Replace(Path.DirectorySeparatorChar, '/'),
                normalizedFullPath,
                info.Length,
                info.LastWriteTimeUtc.ToString("O"));
            return true;
        }

        private static AssetLoadResult FailAssetBundleLoad(
            string operation,
            string modId,
            string normalizedModId,
            string relativePath,
            string fullPath,
            string error)
        {
            lock (SyncRoot)
            {
                _assetBundleLoadFailureCount++;
                _lastAssetOperation = operation ?? "load-assetbundle";
                _lastAssetError = error ?? "";
            }

            Refresh();
            return AssetLoadResult.Failed(
                operation,
                modId,
                normalizedModId,
                relativePath,
                fullPath,
                error);
        }

        private static AssetLoadResult RecordUnloadFailure(
            string modId,
            string normalizedModId,
            string relativePath,
            string fullPath,
            string error)
        {
            lock (SyncRoot)
            {
                _lastAssetOperation = "unload-assetbundle";
                _lastAssetError = error ?? "";
            }

            Refresh();
            return AssetLoadResult.Failed(
                "unload-assetbundle",
                modId,
                normalizedModId,
                relativePath,
                fullPath,
                error);
        }

        private static AssetLoadResult FailAudioClipLoad(
            string operation,
            string modId,
            string normalizedModId,
            string relativePath,
            string fullPath,
            string error)
        {
            lock (SyncRoot)
            {
                _audioClipLoadFailureCount++;
                _lastAssetOperation = operation ?? "begin-load-audioclip";
                _lastAssetError = error ?? "";
            }

            Refresh();
            return AssetLoadResult.Failed(
                operation,
                modId,
                normalizedModId,
                relativePath,
                fullPath,
                error);
        }

        private static AssetLoadResult RecordAudioClipUnloadFailure(
            string modId,
            string normalizedModId,
            string relativePath,
            string fullPath,
            string error)
        {
            lock (SyncRoot)
            {
                _lastAssetOperation = "unload-audioclip";
                _lastAssetError = error ?? "";
            }

            Refresh();
            return AssetLoadResult.Failed(
                "unload-audioclip",
                modId,
                normalizedModId,
                relativePath,
                fullPath,
                error);
        }

        private static bool TryUnloadAssetBundleHandle(LoadedAssetBundleHandle handle, bool unloadAllLoadedObjects, out string error)
        {
            error = null;
            try
            {
                if (handle.Bundle != null)
                {
                    handle.Bundle.Unload(unloadAllLoadedObjects);
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            lock (SyncRoot)
            {
                if (LoadedAssetBundles.TryGetValue(handle.Key, out var current) && ReferenceEquals(current, handle))
                {
                    LoadedAssetBundles.Remove(handle.Key);
                    _assetBundleUnloadCount++;
                }
            }

            return true;
        }

        private static bool TryUnloadAudioClipHandle(LoadedAudioClipHandle handle, out string error)
        {
            error = null;
            try
            {
                if (handle.Clip != null)
                {
                    UnityEngine.Object.Destroy(handle.Clip);
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            lock (SyncRoot)
            {
                if (LoadedAudioClips.TryGetValue(handle.Key, out var current) && ReferenceEquals(current, handle))
                {
                    LoadedAudioClips.Remove(handle.Key);
                    _audioClipUnloadCount++;
                }
            }

            return true;
        }

        private static string DisposeAudioRequest(AudioClipFileRequest request)
        {
            try
            {
                request?.Dispose();
                return null;
            }
            catch (Exception ex)
            {
                return "Audio request disposal failed: " + ex.Message;
            }
        }

        private static string CancelAudioClipLoad(PendingAudioClipLoadHandle pending)
        {
            if (pending == null)
                return null;
            pending.Canceled = true;
            var error = DisposeAudioRequest(pending.Request);
            if (error == null)
            {
                lock (SyncRoot)
                {
                    if (PendingAudioClipLoads.TryGetValue(pending.Key, out var current) && ReferenceEquals(current, pending))
                        PendingAudioClipLoads.Remove(pending.Key);
                }
            }
            return error;
        }

        private static string FinishAudioRequest(PendingAudioClipLoadHandle pending)
        {
            var error = DisposeAudioRequest(pending.Request);
            if (error != null)
            {
                pending.Canceled = true;
                lock (SyncRoot)
                    PendingAudioClipLoads[pending.Key] = pending;
            }
            return error;
        }

        private static string CombineErrors(string first, string second)
        {
            return first == null ? second : second == null ? first : first + " " + second;
        }

        private static void CompleteAudioClipLoad(PendingAudioClipLoadHandle pending)
        {
            if (pending == null)
            {
                return;
            }

            string error = null;
            AudioClip clip = null;
            var retained = false;
            try
            {
                var request = pending.Request;
                if (request == null)
                {
                    error = "Audio file request was null.";
                    return;
                }

                var samples = request.ReadSamples(out var channels, out var frequency);
                // The callback-free native path does not need the stripped DownloadHandlerAudioClip type.
                clip = AudioClip.Construct_Internal();
                if (clip == null)
                {
                    error = "Unity returned null while creating the AudioClip.";
                    return;
                }
                clip.CreateUserSound(Path.GetFileNameWithoutExtension(pending.FullPath),
                    samples.Length / channels, channels, frequency, false);
                if (!clip.SetData(samples, 0))
                    throw new InvalidOperationException("Unity could not upload the decoded AudioClip samples.");

                lock (SyncRoot)
                {
                    LoadedAudioClips[pending.Key] = new LoadedAudioClipHandle(
                        pending.Key,
                        pending.ModId,
                        pending.NormalizedModId,
                        pending.RelativePath,
                        pending.FullPath,
                        pending.AudioType,
                        clip,
                        DateTimeOffset.UtcNow.ToString("O"));
                    retained = true;
                    _audioClipLoadCompleteCount++;
                    _lastAssetOperation = "complete-load-audioclip";
                    _lastAssetError = "";
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            finally
            {
                if (!retained)
                {
                    try
                    {
                        if (clip != null)
                        {
                            UnityEngine.Object.Destroy(clip);
                        }
                    }
                    catch (Exception ex)
                    {
                        error = CombineErrors(error, "Unregistered AudioClip cleanup failed: " + ex.Message);
                    }
                }

                error = CombineErrors(error, FinishAudioRequest(pending));
                if (error != null)
                {
                    RecordAudioClipCompletionFailure(pending, error, !retained);
                }
            }
        }

        private static void RecordAudioClipCompletionFailure(PendingAudioClipLoadHandle pending, string error, bool loadFailed = true)
        {
            lock (SyncRoot)
            {
                if (loadFailed)
                    _audioClipLoadFailureCount++;
                _lastAssetOperation = "complete-load-audioclip";
                _lastAssetError = error ?? "";
            }
        }

        private static string NormalizeRelativeAssetPath(string relativePath, string kind)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new ArgumentException("A relative " + kind + " file path is required.", nameof(relativePath));
            }

            relativePath = relativePath.Trim();
            if (Path.IsPathRooted(relativePath))
            {
                throw new ArgumentException("Mod " + kind + " file paths must be relative.", nameof(relativePath));
            }

            var segments = relativePath.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries);
            var parts = new List<string>();
            foreach (var segment in segments)
            {
                if (segment == "..")
                    throw new ArgumentException("Mod " + kind + " file paths must stay under the mod " + kind + " directory.", nameof(relativePath));
                if (segment != ".")
                    parts.Add(segment);
            }

            var normalized = string.Join("/", parts);
            if (normalized.Length == 0)
            {
                throw new ArgumentException("Mod " + kind + " file paths must stay under the mod " + kind + " directory.", nameof(relativePath));
            }

            return normalized;
        }

        private static string CreateAssetKey(string normalizedModId, string normalizedRelativePath)
        {
            return (normalizedModId ?? "unknown-mod") + ":" + (normalizedRelativePath ?? "").ToLowerInvariant();
        }

        private static bool IsAssetBundleFile(string path)
        {
            var extension = Path.GetExtension(path);
            return string.Equals(extension, ".assetbundle", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".bundle", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAudioClipFile(string path)
        {
            var extension = Path.GetExtension(path);
            return string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".ogg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".mp3", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".aiff", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".aif", StringComparison.OrdinalIgnoreCase);
        }

        private static AudioType GetAudioType(string path)
        {
            var extension = Path.GetExtension(path);
            if (string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase))
            {
                return AudioType.WAV;
            }

            if (string.Equals(extension, ".ogg", StringComparison.OrdinalIgnoreCase))
            {
                return AudioType.OGGVORBIS;
            }

            if (string.Equals(extension, ".mp3", StringComparison.OrdinalIgnoreCase))
            {
                return AudioType.MPEG;
            }

            if (string.Equals(extension, ".aiff", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".aif", StringComparison.OrdinalIgnoreCase))
            {
                return AudioType.AIFF;
            }

            return AudioType.UNKNOWN;
        }

        internal static void GetRuntimeMetrics(
            out int loadedAssetBundleCount,
            out int assetBundleLoadAttemptCount,
            out int assetBundleLoadFailureCount,
            out int assetBundleUnloadCount,
            out int loadedAudioClipCount,
            out int pendingAudioClipLoadCount,
            out int audioClipLoadAttemptCount,
            out int audioClipLoadFailureCount,
            out int audioClipLoadCompleteCount,
            out int audioClipUnloadCount,
            out string lastAssetOperation,
            out string lastAssetError,
            out IReadOnlyList<LoadedAssetBundleSnapshot> loadedAssetBundles,
            out IReadOnlyList<LoadedAudioClipSnapshot> loadedAudioClips,
            out IReadOnlyList<PendingAudioClipLoadSnapshot> pendingAudioClipLoads)
        {
            lock (SyncRoot)
            {
                loadedAssetBundleCount = LoadedAssetBundles.Count;
                assetBundleLoadAttemptCount = _assetBundleLoadAttemptCount;
                assetBundleLoadFailureCount = _assetBundleLoadFailureCount;
                assetBundleUnloadCount = _assetBundleUnloadCount;
                loadedAudioClipCount = LoadedAudioClips.Count;
                pendingAudioClipLoadCount = PendingAudioClipLoads.Count;
                audioClipLoadAttemptCount = _audioClipLoadAttemptCount;
                audioClipLoadFailureCount = _audioClipLoadFailureCount;
                audioClipLoadCompleteCount = _audioClipLoadCompleteCount;
                audioClipUnloadCount = _audioClipUnloadCount;
                lastAssetOperation = _lastAssetOperation ?? "";
                lastAssetError = _lastAssetError ?? "";

                // Keep the outer lock so counters and all three lists describe the same state.
                loadedAssetBundles = GetLoadedAssetBundles();
                loadedAudioClips = GetLoadedAudioClips();
                pendingAudioClipLoads = GetPendingAudioClipLoads();
            }
        }

        private static string ResolveModFilePath(string modDirectory, string relativePath, string kind)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new ArgumentException("A relative " + kind + " file path is required.", nameof(relativePath));
            }

            if (string.IsNullOrWhiteSpace(modDirectory))
            {
                throw new InvalidOperationException("Modstoke mod " + kind + " directory is not available.");
            }

            if (Path.IsPathRooted(relativePath))
            {
                throw new ArgumentException("Mod " + kind + " file paths must be relative.", nameof(relativePath));
            }

            var fullPath = Path.GetFullPath(Path.Combine(modDirectory, relativePath));
            var fullRoot = EnsureTrailingSeparator(Path.GetFullPath(modDirectory));
            if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Mod " + kind + " file paths must stay under the mod " + kind + " directory.", nameof(relativePath));
            }

            return fullPath;
        }

        private static string CombineUnderRoot(string root, string child, string kind)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                return null;
            }

            var fullRoot = EnsureTrailingSeparator(Path.GetFullPath(root));
            var fullPath = Path.GetFullPath(Path.Combine(fullRoot, child ?? "unknown-mod"));
            if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Resolved " + kind + " path escaped the Modstoke " + kind + " root.");
            }

            return fullPath;
        }

        private static string NormalizeSearchPattern(string searchPattern)
        {
            var pattern = string.IsNullOrWhiteSpace(searchPattern) ? "*" : searchPattern.Trim();
            if (pattern.IndexOf(Path.DirectorySeparatorChar) >= 0
                || pattern.IndexOf(Path.AltDirectorySeparatorChar) >= 0
                || pattern.IndexOf(':') >= 0)
            {
                throw new ArgumentException("Search patterns must be file patterns, not paths.", nameof(searchPattern));
            }

            return pattern;
        }

        private static string EnsureTrailingSeparator(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            var separator = Path.DirectorySeparatorChar.ToString();
            return path.EndsWith(separator, StringComparison.Ordinal)
                ? path
                : path + separator;
        }

        private static void EnsureDirectory(string path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                Directory.CreateDirectory(path);
            }
        }

        private static string ShortPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "Unavailable";
            }

            var fileName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.IsNullOrWhiteSpace(fileName) ? path : fileName;
        }

        private static MenuRowSnapshot Row(string label, string value, string status, string detail)
        {
            return new MenuRowSnapshot(label, value, status, detail);
        }

        private sealed class LoadedAssetBundleHandle
        {
            internal LoadedAssetBundleHandle(
                string key,
                string modId,
                string normalizedModId,
                string relativePath,
                string fullPath,
                AssetBundle bundle,
                string loadedUtc)
            {
                Key = key ?? "";
                ModId = modId ?? "";
                NormalizedModId = normalizedModId ?? "";
                RelativePath = relativePath ?? "";
                FullPath = fullPath ?? "";
                Bundle = bundle;
                LoadedUtc = loadedUtc ?? "";
            }

            internal string Key { get; }
            internal string ModId { get; }
            internal string NormalizedModId { get; }
            internal string RelativePath { get; }
            internal string FullPath { get; }
            internal AssetBundle Bundle { get; }
            internal string LoadedUtc { get; }
            internal string[] ContentPaths { get; private set; }
            internal bool IsSceneBundle { get; private set; }
            internal string ContentIndexUtc { get; private set; }

            internal void EnsureContentIndex()
            {
                if (ContentPaths != null)
                    return;

                var isSceneBundle = Bundle.isStreamedSceneAssetBundle;
                var nativePaths = isSceneBundle ? Bundle.GetAllScenePaths() : Bundle.GetAllAssetNames();
                if (nativePaths == null)
                    throw new InvalidOperationException("Unity did not return a bundle path index; contents are unknown.");

                var paths = new string[nativePaths.Length];
                for (var index = 0; index < paths.Length; index++)
                {
                    var path = nativePaths[index];
                    if (string.IsNullOrEmpty(path))
                        throw new InvalidOperationException("Unity returned an incomplete bundle path index; contents are unknown.");
                    paths[index] = path;
                }
                Array.Sort(paths, StringComparer.Ordinal);

                // Publish only a complete managed index. Unload/reload replaces this handle.
                IsSceneBundle = isSceneBundle;
                ContentIndexUtc = DateTimeOffset.UtcNow.ToString("O");
                ContentPaths = paths;
            }

            internal LoadedAssetBundleSnapshot ToSnapshot()
            {
                return new LoadedAssetBundleSnapshot(
                    Key,
                    ModId,
                    NormalizedModId,
                    RelativePath,
                    FullPath,
                    LoadedUtc);
            }
        }

        private sealed class LoadedAudioClipHandle
        {
            internal LoadedAudioClipHandle(
                string key,
                string modId,
                string normalizedModId,
                string relativePath,
                string fullPath,
                string audioType,
                AudioClip clip,
                string loadedUtc)
            {
                Key = key ?? "";
                ModId = modId ?? "";
                NormalizedModId = normalizedModId ?? "";
                RelativePath = relativePath ?? "";
                FullPath = fullPath ?? "";
                AudioType = audioType ?? "";
                Clip = clip;
                LoadedUtc = loadedUtc ?? "";
                Length = clip.length;
                Channels = clip.channels;
                Frequency = clip.frequency;
                Samples = clip.samples;
            }

            internal string Key { get; }
            internal string ModId { get; }
            internal string NormalizedModId { get; }
            internal string RelativePath { get; }
            internal string FullPath { get; }
            internal string AudioType { get; }
            internal AudioClip Clip { get; }
            internal string LoadedUtc { get; }
            private float Length { get; }
            private int Channels { get; }
            private int Frequency { get; }
            private int Samples { get; }

            internal LoadedAudioClipSnapshot ToSnapshot()
            {
                return new LoadedAudioClipSnapshot(
                    Key,
                    ModId,
                    NormalizedModId,
                    RelativePath,
                    FullPath,
                    AudioType,
                    LoadedUtc,
                    Length,
                    Channels,
                    Frequency,
                    Samples);
            }
        }

        private sealed class AudioClipFileRequest : IDisposable
        {
            // 256 MiB of float samples, converted on the main thread from a fully decoded sound.
            private const uint MaxDecodedSamples = 1u << 26;
            private FMOD.Studio.System _system;
            private FMOD.Sound _sound;

            internal void Start(string fullPath)
            {
                if (!FMOD_StudioSystem.TryGetInstance(out var host, false) || host == null || !host.isInitialized)
                    throw new InvalidOperationException("The game's FMOD Studio system is not initialized.");
                _system = host.System;
                EnsureSystem();
                Check(_system.getCoreSystem(out var core), "get the audio decoder");
                Check(core.createSound(fullPath,
                    FMOD.MODE.CREATESAMPLE | FMOD.MODE.NONBLOCKING | FMOD.MODE.ACCURATETIME
                        | FMOD.MODE._2D | FMOD.MODE.LOOP_OFF, out _sound), "open the audio file");
                if (!_sound.hasHandle())
                    throw new InvalidOperationException("FMOD did not return an audio file handle.");
            }

            internal bool IsDone
            {
                get
                {
                    EnsureSystem();
                    Check(_sound.getOpenState(out var state, out _, out _, out _), "poll audio decoding");
                    if (state == FMOD.OPENSTATE.ERROR)
                        throw new InvalidDataException("FMOD could not decode the audio file.");
                    return state == FMOD.OPENSTATE.READY;
                }
            }

            internal Il2CppStructArray<float> ReadSamples(out int channels, out int frequency)
            {
                if (!IsDone)
                    throw new InvalidOperationException("Audio decoding is not complete.");
                Check(_sound.getFormat(out _, out var format, out channels, out var bits), "read the audio format");
                Check(_sound.getDefaults(out var sampleRate, out _), "read the audio sample rate");
                Check(_sound.getLength(out var byteLength, FMOD.TIMEUNIT.PCMBYTES), "read the decoded audio length");
                var sampleBytes = format switch
                {
                    FMOD.SOUND_FORMAT.PCM8 => 1,
                    FMOD.SOUND_FORMAT.PCM16 => 2,
                    FMOD.SOUND_FORMAT.PCM24 => 3,
                    FMOD.SOUND_FORMAT.PCM32 => 4,
                    FMOD.SOUND_FORMAT.PCMFLOAT => 4,
                    _ => throw new InvalidDataException("FMOD returned non-PCM audio data: " + format)
                };
                if (channels <= 0 || bits != sampleBytes * 8 || byteLength == 0
                    || byteLength > int.MaxValue || byteLength % checked(channels * sampleBytes) != 0
                    || !float.IsFinite(sampleRate) || sampleRate <= 0)
                    throw new InvalidDataException("FMOD returned invalid decoded audio dimensions.");
                frequency = checked((int)Math.Round(sampleRate));
                if (frequency <= 0)
                    throw new InvalidDataException("FMOD returned an invalid audio sample rate.");
                if (byteLength / (uint)sampleBytes > MaxDecodedSamples)
                    throw new InvalidDataException("Decoded audio exceeds the AudioClip limit of " + MaxDecodedSamples
                        + " samples (about 11 minutes of 48 kHz stereo). Use an FMOD bank for longer audio.");

                var samples = new Il2CppStructArray<float>(byteLength / sampleBytes);
                Check(_sound.@lock(0, byteLength, out var first, out var second,
                    out var firstLength, out var secondLength), "access the decoded audio samples");
                try
                {
                    if ((ulong)firstLength + secondLength != byteLength
                        || firstLength % sampleBytes != 0 || secondLength % sampleBytes != 0
                        || (firstLength != 0 && first == IntPtr.Zero)
                        || (secondLength != 0 && second == IntPtr.Zero))
                        throw new InvalidDataException("FMOD returned an incomplete decoded audio buffer.");
                    var data = samples.AsSpan();
                    var firstSamples = checked((int)firstLength / sampleBytes);
                    CopyPcm(first, firstLength, format, data.Slice(0, firstSamples));
                    CopyPcm(second, secondLength, format, data.Slice(firstSamples));
                }
                finally
                {
                    // Keep the IL2CPP array's owner alive while its data is accessed through a span.
                    GC.KeepAlive(samples);
                    Check(_sound.unlock(first, second, firstLength, secondLength), "release the decoded audio buffer");
                }
                return samples;
            }

            private static unsafe void CopyPcm(IntPtr source, uint byteLength,
                FMOD.SOUND_FORMAT format, Span<float> destination)
            {
                var bytes = new ReadOnlySpan<byte>(source.ToPointer(), checked((int)byteLength));
                // FMOD exposes signed, little-endian PCM even for WAV8 and big-endian AIFF sources.
                // Integer formats always scale into [-1, 1]; only float input needs validation.
                switch (format)
                {
                    case FMOD.SOUND_FORMAT.PCM8:
                        for (var index = 0; index < destination.Length; index++)
                            destination[index] = unchecked((sbyte)bytes[index]) / 128f;
                        break;
                    case FMOD.SOUND_FORMAT.PCM16:
                        for (var index = 0; index < destination.Length; index++)
                            destination[index] = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(index * 2)) / 32768f;
                        break;
                    case FMOD.SOUND_FORMAT.PCM24:
                        for (var index = 0; index < destination.Length; index++)
                        {
                            var offset = index * 3;
                            destination[index] = ((bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16) << 8 >> 8) / 8388608f;
                        }
                        break;
                    case FMOD.SOUND_FORMAT.PCM32:
                        for (var index = 0; index < destination.Length; index++)
                            destination[index] = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(index * 4)) / 2147483648f;
                        break;
                    case FMOD.SOUND_FORMAT.PCMFLOAT:
                        for (var index = 0; index < destination.Length; index++)
                        {
                            var value = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(index * 4));
                            if (!float.IsFinite(value))
                                throw new InvalidDataException("Decoded audio contains a non-finite sample.");
                            destination[index] = Math.Clamp(value, -1f, 1f);
                        }
                        break;
                    default:
                        throw new InvalidDataException("Unexpected decoded audio format.");
                }
            }

            public void Dispose()
            {
                if (!_sound.hasHandle())
                    return;
                // Releasing the borrowed Studio system also releases its sounds. Never release the system here.
                if (_system.isValid())
                {
                    // FMOD joins an in-flight NONBLOCKING load before releasing its sound.
                    var result = _sound.release();
                    if (result != FMOD.RESULT.ERR_INVALID_HANDLE)
                        Check(result, "release the audio file");
                }
                _sound.clearHandle();
            }

            private void EnsureSystem()
            {
                if (!_system.isValid())
                    throw new InvalidOperationException("The FMOD system owning this audio request is no longer valid.");
            }

            private static void Check(FMOD.RESULT result, string operation)
            {
                if (result != FMOD.RESULT.OK)
                    throw new InvalidOperationException("Could not " + operation + ": " + result + ".");
            }
        }

        private sealed class PendingAudioClipLoadHandle
        {
            internal PendingAudioClipLoadHandle(
                string key,
                string modId,
                string normalizedModId,
                string relativePath,
                string fullPath,
                string audioType,
                AudioClipFileRequest request,
                string requestedUtc)
            {
                Key = key ?? "";
                ModId = modId ?? "";
                NormalizedModId = normalizedModId ?? "";
                RelativePath = relativePath ?? "";
                FullPath = fullPath ?? "";
                AudioType = audioType ?? "";
                Request = request;
                RequestedUtc = requestedUtc ?? "";
            }

            internal string Key { get; }
            internal string ModId { get; }
            internal string NormalizedModId { get; }
            internal string RelativePath { get; }
            internal string FullPath { get; }
            internal string AudioType { get; }
            internal AudioClipFileRequest Request { get; }
            internal bool Canceled { get; set; }
            internal string RequestedUtc { get; }

            internal PendingAudioClipLoadSnapshot ToSnapshot()
            {
                return new PendingAudioClipLoadSnapshot(
                    Key,
                    ModId,
                    NormalizedModId,
                    RelativePath,
                    FullPath,
                    AudioType,
                    RequestedUtc);
            }
        }

        private sealed class Provider : IMenuContentProvider
        {
            public string Id => MenuProviderIds.SonsAssets;
            public string DisplayName => "Sons Assets";
            public string Kind => MenuProviderKinds.Status;
            public int Priority => 722;
            public string Source => "Modstoke Loader";
            public string Description => "Show Sons asset and audio path helpers.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public MenuContentSnapshot CreateContent()
            {
                return Assets.CreateContent();
            }
        }

        internal static IReadOnlyList<string> SupportedAssetExtensions => AssetExtensions;
        internal static IReadOnlyList<string> SupportedAudioExtensions => AudioExtensions;
    }

    public sealed class AssetsSnapshot
    {
        private const int MaxVisitedFiles = 16384;
        private static readonly HashSet<string> AssetBundleExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".assetbundle",
            ".bundle"
        };

        private AssetsSnapshot(
            string generatedUtc,
            string serviceKey,
            bool ready,
            string status,
            string detail,
            string storageModel,
            string modstokeAssetsDirectory,
            string modstokeModAssetsDirectory,
            string modstokeAudioDirectory,
            string modstokeModAudioDirectory,
            bool modstokeAssetsDirectoryExists,
            bool modstokeModAssetsDirectoryExists,
            bool modstokeAudioDirectoryExists,
            bool modstokeModAudioDirectoryExists,
            string defaultModAssetDirectoryPattern,
            string defaultModAudioDirectoryPattern,
            IReadOnlyList<string> supportedAssetExtensions,
            IReadOnlyList<string> supportedAudioExtensions,
            int defaultMaxListedFiles,
            int maxListedFiles,
            int assetFileCount,
            int assetBundleFileCount,
            int audioFileCount,
            bool assetFileLimitReached,
            bool audioFileLimitReached,
            int loadedAssetBundleCount,
            int assetBundleLoadAttemptCount,
            int assetBundleLoadFailureCount,
            int assetBundleUnloadCount,
            int loadedAudioClipCount,
            int pendingAudioClipLoadCount,
            int audioClipLoadAttemptCount,
            int audioClipLoadFailureCount,
            int audioClipLoadCompleteCount,
            int audioClipUnloadCount,
            string lastAssetOperation,
            string lastAssetError,
            IReadOnlyList<LoadedAssetBundleSnapshot> loadedAssetBundles,
            IReadOnlyList<LoadedAudioClipSnapshot> loadedAudioClips,
            IReadOnlyList<PendingAudioClipLoadSnapshot> pendingAudioClipLoads,
            bool runtimeAssetLoadingEnabled,
            bool runtimeAudioLoadingEnabled)
        {
            GeneratedUtc = generatedUtc ?? "";
            ServiceKey = serviceKey ?? Assets.ServiceKey;
            Ready = ready;
            Status = status ?? "";
            Detail = detail ?? "";
            StorageModel = storageModel ?? "";
            ModstokeAssetsDirectory = modstokeAssetsDirectory ?? "";
            ModstokeModAssetsDirectory = modstokeModAssetsDirectory ?? "";
            ModstokeAudioDirectory = modstokeAudioDirectory ?? "";
            ModstokeModAudioDirectory = modstokeModAudioDirectory ?? "";
            ModstokeAssetsDirectoryExists = modstokeAssetsDirectoryExists;
            ModstokeModAssetsDirectoryExists = modstokeModAssetsDirectoryExists;
            ModstokeAudioDirectoryExists = modstokeAudioDirectoryExists;
            ModstokeModAudioDirectoryExists = modstokeModAudioDirectoryExists;
            DefaultModAssetDirectoryPattern = defaultModAssetDirectoryPattern ?? "";
            DefaultModAudioDirectoryPattern = defaultModAudioDirectoryPattern ?? "";
            SupportedAssetExtensions = supportedAssetExtensions ?? Array.Empty<string>();
            SupportedAudioExtensions = supportedAudioExtensions ?? Array.Empty<string>();
            DefaultMaxListedFiles = defaultMaxListedFiles;
            MaxListedFiles = maxListedFiles;
            AssetFileCount = Math.Max(0, assetFileCount);
            AssetBundleFileCount = Math.Max(0, assetBundleFileCount);
            AudioFileCount = Math.Max(0, audioFileCount);
            AssetFileLimitReached = assetFileLimitReached;
            AudioFileLimitReached = audioFileLimitReached;
            LoadedAssetBundleCount = Math.Max(0, loadedAssetBundleCount);
            AssetBundleLoadAttemptCount = Math.Max(0, assetBundleLoadAttemptCount);
            AssetBundleLoadFailureCount = Math.Max(0, assetBundleLoadFailureCount);
            AssetBundleUnloadCount = Math.Max(0, assetBundleUnloadCount);
            LoadedAudioClipCount = Math.Max(0, loadedAudioClipCount);
            PendingAudioClipLoadCount = Math.Max(0, pendingAudioClipLoadCount);
            AudioClipLoadAttemptCount = Math.Max(0, audioClipLoadAttemptCount);
            AudioClipLoadFailureCount = Math.Max(0, audioClipLoadFailureCount);
            AudioClipLoadCompleteCount = Math.Max(0, audioClipLoadCompleteCount);
            AudioClipUnloadCount = Math.Max(0, audioClipUnloadCount);
            LastAssetOperation = lastAssetOperation ?? "";
            LastAssetError = lastAssetError ?? "";
            LoadedAssetBundles = loadedAssetBundles ?? Array.Empty<LoadedAssetBundleSnapshot>();
            LoadedAudioClips = loadedAudioClips ?? Array.Empty<LoadedAudioClipSnapshot>();
            PendingAudioClipLoads = pendingAudioClipLoads ?? Array.Empty<PendingAudioClipLoadSnapshot>();
            RuntimeAssetLoadingEnabled = runtimeAssetLoadingEnabled;
            RuntimeAudioLoadingEnabled = runtimeAudioLoadingEnabled;
        }

        public int Schema => 3;
        public string GeneratedUtc { get; }
        public string ServiceKey { get; }
        public bool Ready { get; }
        public string Status { get; }
        public string Detail { get; }
        public string StorageModel { get; }
        public string ModstokeAssetsDirectory { get; }
        public string ModstokeModAssetsDirectory { get; }
        public string ModstokeAudioDirectory { get; }
        public string ModstokeModAudioDirectory { get; }
        public bool ModstokeAssetsDirectoryExists { get; }
        public bool ModstokeModAssetsDirectoryExists { get; }
        public bool ModstokeAudioDirectoryExists { get; }
        public bool ModstokeModAudioDirectoryExists { get; }
        public string DefaultModAssetDirectoryPattern { get; }
        public string DefaultModAudioDirectoryPattern { get; }
        public IReadOnlyList<string> SupportedAssetExtensions { get; }
        public IReadOnlyList<string> SupportedAudioExtensions { get; }
        public int DefaultMaxListedFiles { get; }
        public int MaxListedFiles { get; }
        public int AssetFileCount { get; }
        public int AssetBundleFileCount { get; }
        public int AudioFileCount { get; }
        public bool AssetFileLimitReached { get; }
        public bool AudioFileLimitReached { get; }
        public int LoadedAssetBundleCount { get; }
        public int AssetBundleLoadAttemptCount { get; }
        public int AssetBundleLoadFailureCount { get; }
        public int AssetBundleUnloadCount { get; }
        public int LoadedAudioClipCount { get; }
        public int PendingAudioClipLoadCount { get; }
        public int AudioClipLoadAttemptCount { get; }
        public int AudioClipLoadFailureCount { get; }
        public int AudioClipLoadCompleteCount { get; }
        public int AudioClipUnloadCount { get; }
        public string LastAssetOperation { get; }
        public string LastAssetError { get; }
        public IReadOnlyList<LoadedAssetBundleSnapshot> LoadedAssetBundles { get; }
        public IReadOnlyList<LoadedAudioClipSnapshot> LoadedAudioClips { get; }
        public IReadOnlyList<PendingAudioClipLoadSnapshot> PendingAudioClipLoads { get; }
        public bool RuntimeAssetLoadingEnabled { get; }
        public bool RuntimeAudioLoadingEnabled { get; }

        [Obsolete("Use ModstokeAssetsDirectory.")]
        public string OpenAcaiAssetsDirectory => ModstokeAssetsDirectory;
        [Obsolete("Use ModstokeModAssetsDirectory.")]
        public string OpenAcaiModAssetsDirectory => ModstokeModAssetsDirectory;
        [Obsolete("Use ModstokeAudioDirectory.")]
        public string OpenAcaiAudioDirectory => ModstokeAudioDirectory;
        [Obsolete("Use ModstokeModAudioDirectory.")]
        public string OpenAcaiModAudioDirectory => ModstokeModAudioDirectory;
        [Obsolete("Use ModstokeAssetsDirectoryExists.")]
        public bool OpenAcaiAssetsDirectoryExists => ModstokeAssetsDirectoryExists;
        [Obsolete("Use ModstokeModAssetsDirectoryExists.")]
        public bool OpenAcaiModAssetsDirectoryExists => ModstokeModAssetsDirectoryExists;
        [Obsolete("Use ModstokeAudioDirectoryExists.")]
        public bool OpenAcaiAudioDirectoryExists => ModstokeAudioDirectoryExists;
        [Obsolete("Use ModstokeModAudioDirectoryExists.")]
        public bool OpenAcaiModAudioDirectoryExists => ModstokeModAudioDirectoryExists;

        internal static AssetsSnapshot Create()
        {
            var environment = GameRuntime.Environment;
            var userDataDirectory = !string.IsNullOrWhiteSpace(environment.ModstokeUserDataDirectory)
                ? environment.ModstokeUserDataDirectory
                : LoaderPaths.UserDataDirectory;
            var modstokeAssetsDirectory = Combine(userDataDirectory, Assets.OpenACAIAssetsDirectoryName);
            var modstokeModAssetsDirectory = Combine(modstokeAssetsDirectory, Assets.OpenACAIModAssetsDirectoryName);
            var modstokeAudioDirectory = Combine(userDataDirectory, Assets.OpenACAIAudioDirectoryName);
            var modstokeModAudioDirectory = Combine(modstokeAudioDirectory, Assets.OpenACAIModAssetsDirectoryName);

            SafeCreateDirectory(modstokeAssetsDirectory);
            SafeCreateDirectory(modstokeModAssetsDirectory);
            SafeCreateDirectory(modstokeAudioDirectory);
            SafeCreateDirectory(modstokeModAudioDirectory);

            var assetsReady = Directory.Exists(modstokeAssetsDirectory) && Directory.Exists(modstokeModAssetsDirectory);
            var audioReady = Directory.Exists(modstokeAudioDirectory) && Directory.Exists(modstokeModAudioDirectory);
            var ready = assetsReady && audioReady;
            var assetFileCount = CountSupportedFiles(
                modstokeModAssetsDirectory,
                Assets.SupportedAssetExtensions,
                AssetBundleExtensions,
                Assets.MaxListedFiles,
                out var assetBundleFileCount,
                out var assetFileLimitReached);
            var audioFileCount = CountSupportedFiles(
                modstokeModAudioDirectory,
                Assets.SupportedAudioExtensions,
                null,
                Assets.MaxListedFiles,
                out _,
                out var audioFileLimitReached);
            int loadedAssetBundleCount;
            int assetBundleLoadAttemptCount;
            int assetBundleLoadFailureCount;
            int assetBundleUnloadCount;
            int loadedAudioClipCount;
            int pendingAudioClipLoadCount;
            int audioClipLoadAttemptCount;
            int audioClipLoadFailureCount;
            int audioClipLoadCompleteCount;
            int audioClipUnloadCount;
            string lastAssetOperation;
            string lastAssetError;
            IReadOnlyList<LoadedAssetBundleSnapshot> loadedAssetBundles;
            IReadOnlyList<LoadedAudioClipSnapshot> loadedAudioClips;
            IReadOnlyList<PendingAudioClipLoadSnapshot> pendingAudioClipLoads;
            Assets.GetRuntimeMetrics(
                out loadedAssetBundleCount,
                out assetBundleLoadAttemptCount,
                out assetBundleLoadFailureCount,
                out assetBundleUnloadCount,
                out loadedAudioClipCount,
                out pendingAudioClipLoadCount,
                out audioClipLoadAttemptCount,
                out audioClipLoadFailureCount,
                out audioClipLoadCompleteCount,
                out audioClipUnloadCount,
                out lastAssetOperation,
                out lastAssetError,
                out loadedAssetBundles,
                out loadedAudioClips,
                out pendingAudioClipLoads);
            return new AssetsSnapshot(
                DateTimeOffset.UtcNow.ToString("O"),
                Assets.ServiceKey,
                ready,
                ready ? "READY" : "CHECK",
                ready
                    ? "Modstoke per-mod asset and audio directories are available."
                    : "Modstoke per-mod asset or audio directories could not be verified.",
                "openacai-userdata-per-mod-assets",
                modstokeAssetsDirectory,
                modstokeModAssetsDirectory,
                modstokeAudioDirectory,
                modstokeModAudioDirectory,
                Directory.Exists(modstokeAssetsDirectory),
                Directory.Exists(modstokeModAssetsDirectory),
                Directory.Exists(modstokeAudioDirectory),
                Directory.Exists(modstokeModAudioDirectory),
                Combine(modstokeModAssetsDirectory, "{normalized-mod-id}"),
                Combine(modstokeModAudioDirectory, "{normalized-mod-id}"),
                Assets.SupportedAssetExtensions,
                Assets.SupportedAudioExtensions,
                Assets.DefaultMaxListedFiles,
                Assets.MaxListedFiles,
                assetFileCount,
                assetBundleFileCount,
                audioFileCount,
                assetFileLimitReached,
                audioFileLimitReached,
                loadedAssetBundleCount,
                assetBundleLoadAttemptCount,
                assetBundleLoadFailureCount,
                assetBundleUnloadCount,
                loadedAudioClipCount,
                pendingAudioClipLoadCount,
                audioClipLoadAttemptCount,
                audioClipLoadFailureCount,
                audioClipLoadCompleteCount,
                audioClipUnloadCount,
                lastAssetOperation,
                lastAssetError,
                loadedAssetBundles,
                loadedAudioClips,
                pendingAudioClipLoads,
                true,
                true);
        }

        internal static AssetsSnapshot CreateUnknown()
        {
            return new AssetsSnapshot(
                DateTimeOffset.UtcNow.ToString("O"),
                Assets.ServiceKey,
                false,
                "PENDING",
                "Modstoke SDK asset paths have not refreshed yet.",
                "openacai-userdata-per-mod-assets",
                null,
                null,
                null,
                null,
                false,
                false,
                false,
                false,
                null,
                null,
                Assets.SupportedAssetExtensions,
                Assets.SupportedAudioExtensions,
                Assets.DefaultMaxListedFiles,
                Assets.MaxListedFiles,
                0,
                0,
                0,
                false,
                false,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                "initialize",
                "",
                Array.Empty<LoadedAssetBundleSnapshot>(),
                Array.Empty<LoadedAudioClipSnapshot>(),
                Array.Empty<PendingAudioClipLoadSnapshot>(),
                false,
                false);
        }

        private static string Combine(string left, string right)
        {
            return string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)
                ? null
                : Path.Combine(left, right);
        }

        // Refresh runs on the main thread after every load and unload, so each root gets one walk
        // and unrelated files count toward a visit cap.
        private static int CountSupportedFiles(
            string root,
            IEnumerable<string> extensions,
            HashSet<string> subsetExtensions,
            int maxFiles,
            out int subsetCount,
            out bool limitReached)
        {
            subsetCount = 0;
            limitReached = false;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return 0;
            }

            var extensionSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var extension in extensions ?? Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(extension))
                {
                    extensionSet.Add(extension.Trim());
                }
            }

            var limit = Math.Max(1, maxFiles <= 0 ? Assets.MaxListedFiles : maxFiles);
            var count = 0;
            var visited = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    if (++visited > MaxVisitedFiles)
                    {
                        limitReached = true;
                        return count;
                    }

                    var extension = Path.GetExtension(file);
                    if (extensionSet.Count > 0 && !extensionSet.Contains(extension))
                    {
                        continue;
                    }

                    count++;
                    if (count > limit)
                    {
                        limitReached = true;
                        return limit;
                    }

                    if (subsetExtensions != null && subsetExtensions.Contains(extension))
                    {
                        subsetCount++;
                    }
                }
            }
            catch
            {
                return count;
            }

            return count;
        }

        private static void SafeCreateDirectory(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    Directory.CreateDirectory(path);
                }
            }
            catch
            {
            }
        }
    }

    public sealed class ModAssetPath
    {
        internal ModAssetPath(string kind, string modId, string normalizedModId, string directory, bool exists)
        {
            Kind = kind ?? "";
            ModId = modId ?? "";
            NormalizedModId = normalizedModId ?? "";
            Directory = directory ?? "";
            Exists = exists;
        }

        public string Kind { get; }
        public string ModId { get; }
        public string NormalizedModId { get; }
        public string Directory { get; }
        public bool Exists { get; }
    }

    public sealed class AssetFileSnapshot
    {
        internal AssetFileSnapshot(string kind, string relativePath, string fullPath, long length, string lastWriteUtc)
        {
            Kind = kind ?? "";
            RelativePath = relativePath ?? "";
            FullPath = fullPath ?? "";
            Length = Math.Max(0, length);
            LastWriteUtc = lastWriteUtc ?? "";
        }

        public string Kind { get; }
        public string RelativePath { get; }
        public string FullPath { get; }
        public long Length { get; }
        public string LastWriteUtc { get; }
    }

    public sealed class LoadedAssetBundleSnapshot
    {
        internal LoadedAssetBundleSnapshot(
            string key,
            string modId,
            string normalizedModId,
            string relativePath,
            string fullPath,
            string loadedUtc)
        {
            Key = key ?? "";
            ModId = modId ?? "";
            NormalizedModId = normalizedModId ?? "";
            RelativePath = relativePath ?? "";
            FullPath = fullPath ?? "";
            LoadedUtc = loadedUtc ?? "";
        }

        public string Key { get; }
        public string ModId { get; }
        public string NormalizedModId { get; }
        public string RelativePath { get; }
        public string FullPath { get; }
        public string LoadedUtc { get; }
    }

    /// <summary>
    /// A point-in-time native bundle inspection, with no native object references.
    /// Listed paths are available bundle entries, not proof of loaded objects, loaded scenes,
    /// resolved dependencies, or compatibility with a requested asset type.
    /// </summary>
    public sealed class AssetBundleContentsSnapshot
    {
        private readonly string[] _allPaths;

        internal AssetBundleContentsSnapshot(
            LoadedAssetBundleSnapshot bundle,
            bool isSceneBundle,
            string[] allPaths,
            int maxPaths,
            string indexGeneratedUtc,
            string generatedUtc)
        {
            Bundle = bundle;
            IsSceneBundle = isSceneBundle;
            _allPaths = allPaths;
            var listed = new string[Math.Min(allPaths.Length, maxPaths)];
            Array.Copy(allPaths, listed, listed.Length);
            Paths = Array.AsReadOnly(listed);
            IndexGeneratedUtc = indexGeneratedUtc;
            GeneratedUtc = generatedUtc;
        }

        public LoadedAssetBundleSnapshot Bundle { get; }
        public bool IsSceneBundle { get; }
        public IReadOnlyList<string> Paths { get; }
        public int TotalPathCount => _allPaths.Length;
        public bool Truncated => Paths.Count < TotalPathCount;
        public string Status => Truncated ? "TRUNCATED" : "READY";
        public string IndexGeneratedUtc { get; }
        public string GeneratedUtc { get; }

        /// <summary>
        /// Searches the full captured index, including paths omitted from Paths.
        /// Matching is ordinal and case-sensitive, with no basename, slash or case conversion.
        /// This is historical data: inspect the bundle again to check current availability.
        /// </summary>
        public bool ContainsExactPath(string path)
        {
            return path != null && Array.BinarySearch(_allPaths, path, StringComparer.Ordinal) >= 0;
        }
    }

    /// <summary>Detached mesh metadata at observation time; bounds are in mesh-local space.</summary>
    public sealed class MeshAssetSnapshot
    {
        internal MeshAssetSnapshot(
            LoadedAssetBundleSnapshot bundle,
            string assetPath,
            int vertexCount,
            int subMeshCount,
            bool isReadable,
            System.Numerics.Vector3 boundsCenter,
            System.Numerics.Vector3 boundsExtents,
            string generatedUtc)
        {
            Bundle = bundle;
            AssetPath = assetPath;
            VertexCount = vertexCount;
            SubMeshCount = subMeshCount;
            IsReadable = isReadable;
            BoundsCenter = boundsCenter;
            BoundsExtents = boundsExtents;
            GeneratedUtc = generatedUtc;
        }

        public LoadedAssetBundleSnapshot Bundle { get; }
        public string AssetPath { get; }
        public int VertexCount { get; }
        public int SubMeshCount { get; }
        public bool IsReadable { get; }
        public System.Numerics.Vector3 BoundsCenter { get; }
        public System.Numerics.Vector3 BoundsExtents { get; }
        public string GeneratedUtc { get; }
    }

    public sealed class LoadedAudioClipSnapshot
    {
        internal LoadedAudioClipSnapshot(
            string key,
            string modId,
            string normalizedModId,
            string relativePath,
            string fullPath,
            string audioType,
            string loadedUtc,
            float lengthSeconds,
            int channels,
            int frequency,
            int samples)
        {
            Key = key ?? "";
            ModId = modId ?? "";
            NormalizedModId = normalizedModId ?? "";
            RelativePath = relativePath ?? "";
            FullPath = fullPath ?? "";
            AudioType = audioType ?? "";
            LoadedUtc = loadedUtc ?? "";
            LengthSeconds = Math.Max(0f, lengthSeconds);
            Channels = Math.Max(0, channels);
            Frequency = Math.Max(0, frequency);
            Samples = Math.Max(0, samples);
        }

        public string Key { get; }
        public string ModId { get; }
        public string NormalizedModId { get; }
        public string RelativePath { get; }
        public string FullPath { get; }
        public string AudioType { get; }
        public string LoadedUtc { get; }
        public float LengthSeconds { get; }
        public int Channels { get; }
        public int Frequency { get; }
        public int Samples { get; }
    }

    public sealed class PendingAudioClipLoadSnapshot
    {
        internal PendingAudioClipLoadSnapshot(
            string key,
            string modId,
            string normalizedModId,
            string relativePath,
            string fullPath,
            string audioType,
            string requestedUtc)
        {
            Key = key ?? "";
            ModId = modId ?? "";
            NormalizedModId = normalizedModId ?? "";
            RelativePath = relativePath ?? "";
            FullPath = fullPath ?? "";
            AudioType = audioType ?? "";
            RequestedUtc = requestedUtc ?? "";
        }

        public string Key { get; }
        public string ModId { get; }
        public string NormalizedModId { get; }
        public string RelativePath { get; }
        public string FullPath { get; }
        public string AudioType { get; }
        public string RequestedUtc { get; }
    }

    public sealed class AssetLoadResult
    {
        private AssetLoadResult(
            bool success,
            string operation,
            string status,
            string detail,
            string modId,
            string normalizedModId,
            string relativePath,
            string fullPath,
            bool cached,
            string error,
            string generatedUtc)
        {
            Success = success;
            Operation = operation ?? "";
            Status = status ?? "";
            Detail = detail ?? "";
            ModId = modId ?? "";
            NormalizedModId = normalizedModId ?? "";
            RelativePath = relativePath ?? "";
            FullPath = fullPath ?? "";
            Cached = cached;
            Error = error ?? "";
            GeneratedUtc = generatedUtc ?? "";
        }

        public bool Success { get; }
        public string Operation { get; }
        public string Status { get; }
        public string Detail { get; }
        public string ModId { get; }
        public string NormalizedModId { get; }
        public string RelativePath { get; }
        public string FullPath { get; }
        public bool Cached { get; }
        public string Error { get; }
        public string GeneratedUtc { get; }

        internal static AssetLoadResult Succeeded(
            string operation,
            string modId,
            string normalizedModId,
            string relativePath,
            string fullPath,
            bool cached,
            string detail)
        {
            return new AssetLoadResult(
                true,
                operation,
                "READY",
                detail,
                modId,
                normalizedModId,
                relativePath,
                fullPath,
                cached,
                "",
                DateTimeOffset.UtcNow.ToString("O"));
        }

        internal static AssetLoadResult Pending(
            string operation,
            string modId,
            string normalizedModId,
            string relativePath,
            string fullPath,
            bool cached,
            string detail)
        {
            return new AssetLoadResult(
                false,
                operation,
                "PENDING",
                detail,
                modId,
                normalizedModId,
                relativePath,
                fullPath,
                cached,
                "",
                DateTimeOffset.UtcNow.ToString("O"));
        }

        internal static AssetLoadResult Failed(
            string operation,
            string modId,
            string normalizedModId,
            string relativePath,
            string fullPath,
            string error)
        {
            return new AssetLoadResult(
                false,
                operation,
                "FAILED",
                error,
                modId,
                normalizedModId,
                relativePath,
                fullPath,
                false,
                error,
                DateTimeOffset.UtcNow.ToString("O"));
        }

        internal static AssetLoadResult Unavailable(
            string operation,
            string modId,
            string normalizedModId,
            string relativePath,
            string fullPath,
            string detail)
        {
            return new AssetLoadResult(
                false,
                operation,
                "UNAVAILABLE",
                detail,
                modId,
                normalizedModId,
                relativePath,
                fullPath,
                false,
                detail,
                DateTimeOffset.UtcNow.ToString("O"));
        }
    }
}
