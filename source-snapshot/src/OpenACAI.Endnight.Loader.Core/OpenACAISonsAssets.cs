// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned Sons asset and audio path surface. This starts with safe,
// deterministic per-mod directories; actual bundle/audio loading can build on
// this without inheriting another loader's runtime identity.

using System;
using System.Collections.Generic;
using System.IO;
using OpenACAI.Endnight.Loader;
using OpenACAI.Loader;
using UnityEngine;
using UnityEngine.Networking;

namespace OpenACAI.Sons
{
    public static class OpenACAISonsAssets
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
        private static OpenACAISonsAssetsSnapshot _state = OpenACAISonsAssetsSnapshot.CreateUnknown();
        private static bool _initialized;
        private static int _assetBundleLoadAttemptCount;
        private static int _assetBundleLoadFailureCount;
        private static int _assetBundleUnloadCount;
        private static int _audioClipLoadAttemptCount;
        private static int _audioClipLoadFailureCount;
        private static int _audioClipLoadCompleteCount;
        private static int _audioClipUnloadCount;
        private static string _lastAssetOperation = "initialize";
        private static string _lastAssetError = "";

        public static OpenACAISonsAssetsSnapshot State
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

        public static OpenACAISonsModAssetPath GetModAssetPath(string modId)
        {
            var state = State;
            var normalizedId = NormalizeModId(modId);
            var directory = CombineUnderRoot(state.OpenAcaiModAssetsDirectory, normalizedId, "asset");
            return new OpenACAISonsModAssetPath(
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

        public static OpenACAISonsModAssetPath GetModAudioPath(string modId)
        {
            var state = State;
            var normalizedId = NormalizeModId(modId);
            var directory = CombineUnderRoot(state.OpenAcaiModAudioDirectory, normalizedId, "audio");
            return new OpenACAISonsModAssetPath(
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

        public static IReadOnlyList<OpenACAISonsAssetFileSnapshot> ListModAssetFiles(string modId)
        {
            return ListModAssetFiles(modId, "*", DefaultMaxListedFiles);
        }

        public static IReadOnlyList<OpenACAISonsAssetFileSnapshot> ListModAssetFiles(string modId, string searchPattern, int maxFiles)
        {
            return ListFiles(GetModAssetDirectory(modId), searchPattern, maxFiles, "asset");
        }

        public static IReadOnlyList<OpenACAISonsAssetFileSnapshot> ListModAudioFiles(string modId)
        {
            return ListModAudioFiles(modId, "*", DefaultMaxListedFiles);
        }

        public static IReadOnlyList<OpenACAISonsAssetFileSnapshot> ListModAudioFiles(string modId, string searchPattern, int maxFiles)
        {
            return ListFiles(GetModAudioDirectory(modId), searchPattern, maxFiles, "audio");
        }

        public static bool TryFindModAssetFile(string modId, string relativePath, out OpenACAISonsAssetFileSnapshot file)
        {
            return TryFindModFile(GetModAssetDirectory(modId), relativePath, "asset", out file);
        }

        public static bool TryFindModAudioFile(string modId, string relativePath, out OpenACAISonsAssetFileSnapshot file)
        {
            return TryFindModFile(GetModAudioDirectory(modId), relativePath, "audio", out file);
        }

        public static IReadOnlyList<OpenACAISonsLoadedAudioClipSnapshot> GetLoadedAudioClips()
        {
            lock (SyncRoot)
            {
                var snapshots = new List<OpenACAISonsLoadedAudioClipSnapshot>();
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

        public static IReadOnlyList<OpenACAISonsPendingAudioClipLoadSnapshot> GetPendingAudioClipLoads()
        {
            lock (SyncRoot)
            {
                var snapshots = new List<OpenACAISonsPendingAudioClipLoadSnapshot>();
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
            out OpenACAISonsAssetLoadResult result)
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
                    result = OpenACAISonsAssetLoadResult.Succeeded(
                        "begin-load-audioclip",
                        modId,
                        normalizedId,
                        normalizedRelativePath,
                        loaded.FullPath,
                        true,
                        "AudioClip is already loaded by OpenACAI.");
                    return true;
                }

                PendingAudioClipLoadHandle pending;
                if (PendingAudioClipLoads.TryGetValue(key, out pending) && pending != null)
                {
                    result = OpenACAISonsAssetLoadResult.Pending(
                        "begin-load-audioclip",
                        modId,
                        normalizedId,
                        normalizedRelativePath,
                        pending.FullPath,
                        true,
                        "AudioClip load is already pending in OpenACAI.");
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
                result = FailAudioClipLoad("begin-load-audioclip", modId, normalizedId, normalizedRelativePath, fullPath, "Only .wav, .ogg, .mp3, .aiff, and .aif files can be loaded as AudioClips; use OpenACAISonsAudioBanks for .bank files.");
                return false;
            }

            var audioType = GetAudioType(fullPath);
            UnityWebRequest request = null;
            try
            {
                request = UnityWebRequestMultimedia.GetAudioClip(new Uri(fullPath).AbsoluteUri, audioType);
                request.SendWebRequest();
            }
            catch (Exception ex)
            {
                var cleanupError = DisposeAudioRequest(request);
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
            result = OpenACAISonsAssetLoadResult.Pending(
                "begin-load-audioclip",
                modId,
                normalizedId,
                normalizedRelativePath,
                fullPath,
                false,
                "AudioClip load request started through the OpenACAI audio service.");
            return true;
        }

        public static bool TryGetLoadedAudioClip(
            string modId,
            string relativePath,
            out AudioClip audioClip,
            out OpenACAISonsAssetLoadResult result)
        {
            audioClip = null;
            var normalizedId = NormalizeModId(modId);
            string normalizedRelativePath;
            try
            {
                normalizedRelativePath = NormalizeRelativeAssetPath(relativePath, "audio");
            }
            catch (Exception ex)
            {
                result = OpenACAISonsAssetLoadResult.Failed("get-audioclip", modId, normalizedId, relativePath, null, ex.Message);
                return false;
            }

            var key = CreateAssetKey(normalizedId, normalizedRelativePath);
            lock (SyncRoot)
            {
                LoadedAudioClipHandle loaded;
                if (LoadedAudioClips.TryGetValue(key, out loaded) && loaded != null && loaded.Clip != null)
                {
                    audioClip = loaded.Clip;
                    result = OpenACAISonsAssetLoadResult.Succeeded(
                        "get-audioclip",
                        modId,
                        normalizedId,
                        normalizedRelativePath,
                        loaded.FullPath,
                        true,
                        "AudioClip is loaded by OpenACAI.");
                    return true;
                }

                PendingAudioClipLoadHandle pending;
                if (PendingAudioClipLoads.TryGetValue(key, out pending) && pending != null)
                {
                    result = OpenACAISonsAssetLoadResult.Pending(
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

            result = OpenACAISonsAssetLoadResult.Failed(
                "get-audioclip",
                modId,
                normalizedId,
                normalizedRelativePath,
                null,
                "AudioClip is not loaded by OpenACAI.");
            return false;
        }

        public static bool TryUnloadAudioClip(
            string modId,
            string relativePath,
            out OpenACAISonsAssetLoadResult result)
        {
            var normalizedId = NormalizeModId(modId);
            string normalizedRelativePath;
            try
            {
                normalizedRelativePath = NormalizeRelativeAssetPath(relativePath, "audio");
            }
            catch (Exception ex)
            {
                result = OpenACAISonsAssetLoadResult.Failed("unload-audioclip", modId, normalizedId, relativePath, null, ex.Message);
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

                if (PendingAudioClipLoads.TryGetValue(key, out pending))
                {
                    PendingAudioClipLoads.Remove(key);
                }
            }

            var cleanupError = DisposeAudioRequest(pending?.Request);

            if (handle == null)
            {
                if (cleanupError != null)
                {
                    result = RecordAudioClipUnloadFailure(modId, normalizedId, normalizedRelativePath, pending.FullPath, cleanupError);
                    return false;
                }

                result = OpenACAISonsAssetLoadResult.Failed(
                    "unload-audioclip",
                    modId,
                    normalizedId,
                    normalizedRelativePath,
                    null,
                    pending == null ? "AudioClip was not loaded by OpenACAI." : "Pending AudioClip load was canceled before completion.");
                Refresh();
                return false;
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
            result = OpenACAISonsAssetLoadResult.Succeeded(
                "unload-audioclip",
                modId,
                normalizedId,
                normalizedRelativePath,
                handle.FullPath,
                false,
                "AudioClip unloaded through the OpenACAI audio service.");
            return true;
        }

        public static int UnloadModAudioClips(string modId)
        {
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

                foreach (var handle in pendingRemoved)
                {
                    PendingAudioClipLoads.Remove(handle.Key);
                }

                _lastAssetOperation = "unload-mod-audioclips";
                _lastAssetError = "";
            }

            foreach (var handle in pendingRemoved)
            {
                var error = DisposeAudioRequest(handle.Request);
                if (error != null)
                {
                    lock (SyncRoot)
                    {
                        _audioClipLoadFailureCount++;
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
                        _audioClipLoadFailureCount++;
                        _lastAssetError = error;
                    }
                }
            }

            if (loaded.Count > 0 || pendingRemoved.Count > 0)
            {
                Refresh();
            }

            return unloaded;
        }

        public static IReadOnlyList<OpenACAISonsLoadedAssetBundleSnapshot> GetLoadedAssetBundles()
        {
            lock (SyncRoot)
            {
                var snapshots = new List<OpenACAISonsLoadedAssetBundleSnapshot>();
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

        public static bool TryLoadAssetBundle(
            string modId,
            string relativePath,
            out AssetBundle assetBundle,
            out OpenACAISonsAssetLoadResult result)
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
                    result = OpenACAISonsAssetLoadResult.Succeeded(
                        "load-assetbundle",
                        modId,
                        normalizedId,
                        normalizedRelativePath,
                        existing.FullPath,
                        true,
                        "AssetBundle is already loaded by OpenACAI.");
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
            result = OpenACAISonsAssetLoadResult.Succeeded(
                "load-assetbundle",
                modId,
                normalizedId,
                normalizedRelativePath,
                fullPath,
                false,
                "AssetBundle loaded through the OpenACAI asset service.");
            return true;
        }

        public static bool TryUnloadAssetBundle(
            string modId,
            string relativePath,
            bool unloadAllLoadedObjects,
            out OpenACAISonsAssetLoadResult result)
        {
            var normalizedId = NormalizeModId(modId);
            string normalizedRelativePath;
            try
            {
                normalizedRelativePath = NormalizeRelativeAssetPath(relativePath, "asset");
            }
            catch (Exception ex)
            {
                result = OpenACAISonsAssetLoadResult.Failed(
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
                result = OpenACAISonsAssetLoadResult.Failed(
                    "unload-assetbundle",
                    modId,
                    normalizedId,
                    normalizedRelativePath,
                    null,
                    "AssetBundle was not loaded by OpenACAI.");
                return false;
            }

            if (!TryUnloadAssetBundleHandle(handle, unloadAllLoadedObjects, out var error))
            {
                result = RecordUnloadFailure(modId, normalizedId, normalizedRelativePath, handle.FullPath, error);
                return false;
            }

            Refresh();
            result = OpenACAISonsAssetLoadResult.Succeeded(
                "unload-assetbundle",
                modId,
                normalizedId,
                normalizedRelativePath,
                handle.FullPath,
                false,
                "AssetBundle unloaded through the OpenACAI asset service.");
            return true;
        }

        public static int UnloadModAssetBundles(string modId, bool unloadAllLoadedObjects)
        {
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
                        _assetBundleLoadFailureCount++;
                        _lastAssetError = error;
                    }
                }
            }

            if (loaded.Count > 0)
            {
                Refresh();
            }

            return unloaded;
        }

        internal static void Tick()
        {
            PendingAudioClipLoadHandle[] pendingLoads;
            lock (SyncRoot)
            {
                if (PendingAudioClipLoads.Count == 0)
                {
                    return;
                }

                pendingLoads = new PendingAudioClipLoadHandle[PendingAudioClipLoads.Count];
                PendingAudioClipLoads.Values.CopyTo(pendingLoads, 0);
            }

            var changed = false;
            foreach (var pending in pendingLoads)
            {
                string pollError = null;
                try
                {
                    if (pending.Request != null && !pending.Request.isDone)
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
                    RecordAudioClipCompletionFailure(pending, CombineErrors(pollError, DisposeAudioRequest(pending.Request)));
                }
            }

            if (changed)
            {
                Refresh();
            }
        }

        public static OpenACAISonsAssetsSnapshot Refresh()
        {
            var snapshot = OpenACAISonsAssetsSnapshot.Create();
            Publish(snapshot);
            return snapshot;
        }

        internal static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            Publish(OpenACAISonsAssetsSnapshot.CreateUnknown());
            if (!OpenACAIMenu.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Modstoke SDK asset provider could not be registered.");
            }
        }

        internal static string NormalizeModId(string modId)
        {
            return OpenACAISonsSaveData.NormalizeModId(modId);
        }

        private static void Publish(OpenACAISonsAssetsSnapshot snapshot)
        {
            lock (SyncRoot)
            {
                _state = snapshot ?? OpenACAISonsAssetsSnapshot.CreateUnknown();
                OpenACAIModding.SetService(ServiceKey, _state);
            }
        }

        private static OpenACAIMenuContentSnapshot CreateContent()
        {
            var snapshot = Refresh();
            return new OpenACAIMenuContentSnapshot(
                "Sons Assets",
                "OpenACAI-owned per-mod asset and audio paths for Sons Of The Forest.",
                new[]
                {
                    CreateStorageSection(snapshot),
                    CreateSdkSection(snapshot),
                    CreateLoadedBundlesSection(snapshot)
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateStorageSection(OpenACAISonsAssetsSnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
                "Asset Storage",
                new[]
                {
                    Row("Service", snapshot.ServiceKey, snapshot.Status, snapshot.Detail),
                    Row("Storage", snapshot.StorageModel, snapshot.Ready ? "READY" : "CHECK", "Deterministic OpenACAI UserData asset roots."),
                    Row("Assets Root", ShortPath(snapshot.OpenAcaiAssetsDirectory), snapshot.OpenAcaiAssetsDirectoryExists ? "READY" : "CHECK", snapshot.OpenAcaiAssetsDirectory),
                    Row("Mod Assets", ShortPath(snapshot.OpenAcaiModAssetsDirectory), snapshot.OpenAcaiModAssetsDirectoryExists ? "READY" : "CHECK", snapshot.OpenAcaiModAssetsDirectory),
                    Row("Audio Root", ShortPath(snapshot.OpenAcaiAudioDirectory), snapshot.OpenAcaiAudioDirectoryExists ? "READY" : "CHECK", snapshot.OpenAcaiAudioDirectory),
                    Row("Mod Audio", ShortPath(snapshot.OpenAcaiModAudioDirectory), snapshot.OpenAcaiModAudioDirectoryExists ? "READY" : "CHECK", snapshot.OpenAcaiModAudioDirectory)
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateSdkSection(OpenACAISonsAssetsSnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
                "SDK Surface",
                new[]
                {
                    Row("Asset Pattern", ShortPath(snapshot.DefaultModAssetDirectoryPattern), "INFO", snapshot.DefaultModAssetDirectoryPattern),
                    Row("Audio Pattern", ShortPath(snapshot.DefaultModAudioDirectoryPattern), "INFO", snapshot.DefaultModAudioDirectoryPattern),
                    Row("Listing Limit", snapshot.DefaultMaxListedFiles + "/" + snapshot.MaxListedFiles, "INFO", "Bounded file listings protect menu/state refreshes."),
                    Row("Asset Files", snapshot.AssetFileCount + " files", snapshot.AssetFileLimitReached ? "CHECK" : "INFO", "Supported files discovered under the OpenACAI per-mod asset root."),
                    Row("AssetBundles", snapshot.AssetBundleFileCount + " candidates | " + snapshot.LoadedAssetBundleCount + " loaded", snapshot.AssetBundleLoadFailureCount == 0 ? "READY" : "CHECK", "AssetBundle load/unload is routed through OpenACAI helper methods."),
                    Row("Audio Files", snapshot.AudioFileCount + " files", snapshot.AudioFileLimitReached ? "CHECK" : "INFO", "Supported audio files discovered under the OpenACAI per-mod audio root."),
                    Row("Asset Loading", snapshot.RuntimeAssetLoadingEnabled ? "AssetBundle.LoadFromFile" : "Path-only", snapshot.RuntimeAssetLoadingEnabled ? "READY" : "INFO", "OpenACAI exposes safe per-mod AssetBundle load/unload helpers."),
                    Row("Audio Loading", snapshot.RuntimeAudioLoadingEnabled ? "UnityWebRequest AudioClip" : "Discovery-only", snapshot.RuntimeAudioLoadingEnabled ? "READY" : "INFO", "OpenACAI exposes non-blocking AudioClip helpers and a separate FMOD bank metadata service."),
                    Row("Audio Clips", snapshot.LoadedAudioClipCount + " loaded | " + snapshot.PendingAudioClipLoadCount + " pending", snapshot.AudioClipLoadFailureCount == 0 ? "READY" : "CHECK", "AudioClip loads are pumped by the OpenACAI runtime driver."),
                    Row("Load Attempts", snapshot.AssetBundleLoadAttemptCount.ToString(), "INFO", "OpenACAI AssetBundle load attempts this run."),
                    Row("Load Failures", snapshot.AssetBundleLoadFailureCount.ToString(), snapshot.AssetBundleLoadFailureCount == 0 ? "READY" : "CHECK", string.IsNullOrWhiteSpace(snapshot.LastAssetError) ? "No AssetBundle load/unload errors recorded." : snapshot.LastAssetError),
                    Row("Audio Attempts", snapshot.AudioClipLoadAttemptCount.ToString(), "INFO", "OpenACAI AudioClip load requests this run."),
                    Row("Audio Failures", snapshot.AudioClipLoadFailureCount.ToString(), snapshot.AudioClipLoadFailureCount == 0 ? "READY" : "CHECK", string.IsNullOrWhiteSpace(snapshot.LastAssetError) ? "No AudioClip load/unload errors recorded." : snapshot.LastAssetError),
                    Row("Unload Ops", snapshot.AssetBundleUnloadCount.ToString(), "INFO", "OpenACAI AssetBundle unload operations this run."),
                    Row("Audio Unloads", snapshot.AudioClipUnloadCount.ToString(), "INFO", "OpenACAI AudioClip unload operations this run."),
                    Row("Last Operation", string.IsNullOrWhiteSpace(snapshot.LastAssetOperation) ? "None" : snapshot.LastAssetOperation, "INFO", "Most recent OpenACAI asset helper operation.")
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateLoadedBundlesSection(OpenACAISonsAssetsSnapshot snapshot)
        {
            var rows = new List<OpenACAIMenuRowSnapshot>();
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
                rows.Add(Row("Entries", "None", "INFO", "No AssetBundles are loaded through OpenACAI."));
            }

            return new OpenACAIMenuSectionSnapshot("Loaded AssetBundles", rows);
        }

        private static IReadOnlyList<OpenACAISonsAssetFileSnapshot> ListFiles(string root, string searchPattern, int maxFiles, string kind)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return Array.Empty<OpenACAISonsAssetFileSnapshot>();
            }

            var pattern = NormalizeSearchPattern(searchPattern);
            var limit = Math.Max(0, Math.Min(maxFiles <= 0 ? DefaultMaxListedFiles : maxFiles, MaxListedFiles));
            var files = new List<OpenACAISonsAssetFileSnapshot>();
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

                files.Add(new OpenACAISonsAssetFileSnapshot(
                    kind,
                    fullPath.Substring(fullRoot.Length).Replace(Path.DirectorySeparatorChar, '/'),
                    fullPath,
                    info.Length,
                    info.LastWriteTimeUtc.ToString("O")));
            }

            files.Sort((left, right) => string.Compare(left.RelativePath, right.RelativePath, StringComparison.OrdinalIgnoreCase));
            return files.ToArray();
        }

        private static bool TryFindModFile(string modDirectory, string relativePath, string kind, out OpenACAISonsAssetFileSnapshot file)
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

            file = new OpenACAISonsAssetFileSnapshot(
                kind,
                normalizedFullPath.Substring(fullRoot.Length).Replace(Path.DirectorySeparatorChar, '/'),
                normalizedFullPath,
                info.Length,
                info.LastWriteTimeUtc.ToString("O"));
            return true;
        }

        private static OpenACAISonsAssetLoadResult FailAssetBundleLoad(
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
            return OpenACAISonsAssetLoadResult.Failed(
                operation,
                modId,
                normalizedModId,
                relativePath,
                fullPath,
                error);
        }

        private static OpenACAISonsAssetLoadResult RecordUnloadFailure(
            string modId,
            string normalizedModId,
            string relativePath,
            string fullPath,
            string error)
        {
            lock (SyncRoot)
            {
                _assetBundleLoadFailureCount++;
                _lastAssetOperation = "unload-assetbundle";
                _lastAssetError = error ?? "";
            }

            Refresh();
            return OpenACAISonsAssetLoadResult.Failed(
                "unload-assetbundle",
                modId,
                normalizedModId,
                relativePath,
                fullPath,
                error);
        }

        private static OpenACAISonsAssetLoadResult FailAudioClipLoad(
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
            return OpenACAISonsAssetLoadResult.Failed(
                operation,
                modId,
                normalizedModId,
                relativePath,
                fullPath,
                error);
        }

        private static OpenACAISonsAssetLoadResult RecordAudioClipUnloadFailure(
            string modId,
            string normalizedModId,
            string relativePath,
            string fullPath,
            string error)
        {
            lock (SyncRoot)
            {
                _audioClipLoadFailureCount++;
                _lastAssetOperation = "unload-audioclip";
                _lastAssetError = error ?? "";
            }

            Refresh();
            return OpenACAISonsAssetLoadResult.Failed(
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

        private static string DisposeAudioRequest(UnityWebRequest request)
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
                    error = "UnityWebRequest was null.";
                    return;
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    error = string.IsNullOrWhiteSpace(request.error) ? request.result.ToString() : request.error;
                    return;
                }

                clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip == null)
                {
                    error = "Unity returned null while loading the AudioClip.";
                    return;
                }

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

                error = CombineErrors(error, DisposeAudioRequest(pending.Request));
                if (error != null)
                {
                    RecordAudioClipCompletionFailure(pending, error);
                }
            }
        }

        private static void RecordAudioClipCompletionFailure(PendingAudioClipLoadHandle pending, string error)
        {
            lock (SyncRoot)
            {
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

            if (Path.IsPathRooted(relativePath))
            {
                throw new ArgumentException("Mod " + kind + " file paths must be relative.", nameof(relativePath));
            }

            var normalized = relativePath.Trim().Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
            while (normalized.StartsWith("./", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(2);
            }

            if (normalized.Length == 0 || normalized.IndexOf("..", StringComparison.Ordinal) >= 0)
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
            out IReadOnlyList<OpenACAISonsLoadedAssetBundleSnapshot> loadedAssetBundles,
            out IReadOnlyList<OpenACAISonsLoadedAudioClipSnapshot> loadedAudioClips,
            out IReadOnlyList<OpenACAISonsPendingAudioClipLoadSnapshot> pendingAudioClipLoads)
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

                var snapshots = new List<OpenACAISonsLoadedAssetBundleSnapshot>();
                foreach (var handle in LoadedAssetBundles.Values)
                {
                    if (handle != null)
                    {
                        snapshots.Add(handle.ToSnapshot());
                    }
                }

                snapshots.Sort((left, right) => string.Compare(left.Key, right.Key, StringComparison.OrdinalIgnoreCase));
                loadedAssetBundles = snapshots.ToArray();

                var audioSnapshots = new List<OpenACAISonsLoadedAudioClipSnapshot>();
                foreach (var handle in LoadedAudioClips.Values)
                {
                    if (handle != null)
                    {
                        audioSnapshots.Add(handle.ToSnapshot());
                    }
                }

                audioSnapshots.Sort((left, right) => string.Compare(left.Key, right.Key, StringComparison.OrdinalIgnoreCase));
                loadedAudioClips = audioSnapshots.ToArray();

                var pendingSnapshots = new List<OpenACAISonsPendingAudioClipLoadSnapshot>();
                foreach (var handle in PendingAudioClipLoads.Values)
                {
                    if (handle != null)
                    {
                        pendingSnapshots.Add(handle.ToSnapshot());
                    }
                }

                pendingSnapshots.Sort((left, right) => string.Compare(left.Key, right.Key, StringComparison.OrdinalIgnoreCase));
                pendingAudioClipLoads = pendingSnapshots.ToArray();
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
                throw new InvalidOperationException("OpenACAI mod " + kind + " directory is not available.");
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
                throw new InvalidOperationException("Resolved " + kind + " path escaped the OpenACAI " + kind + " root.");
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

        private static OpenACAIMenuRowSnapshot Row(string label, string value, string status, string detail)
        {
            return new OpenACAIMenuRowSnapshot(label, value, status, detail);
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

            internal OpenACAISonsLoadedAssetBundleSnapshot ToSnapshot()
            {
                return new OpenACAISonsLoadedAssetBundleSnapshot(
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

            internal OpenACAISonsLoadedAudioClipSnapshot ToSnapshot()
            {
                return new OpenACAISonsLoadedAudioClipSnapshot(
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

        private sealed class PendingAudioClipLoadHandle
        {
            internal PendingAudioClipLoadHandle(
                string key,
                string modId,
                string normalizedModId,
                string relativePath,
                string fullPath,
                string audioType,
                UnityWebRequest request,
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
            internal UnityWebRequest Request { get; }
            internal string RequestedUtc { get; }

            internal OpenACAISonsPendingAudioClipLoadSnapshot ToSnapshot()
            {
                return new OpenACAISonsPendingAudioClipLoadSnapshot(
                    Key,
                    ModId,
                    NormalizedModId,
                    RelativePath,
                    FullPath,
                    AudioType,
                    RequestedUtc);
            }
        }

        private sealed class Provider : IOpenACAIMenuContentProvider
        {
            public string Id => OpenACAIMenuProviderIds.SonsAssets;
            public string DisplayName => "Sons Assets";
            public string Kind => OpenACAIMenuProviderKinds.Status;
            public int Priority => 722;
            public string Source => "Modstoke Loader";
            public string Description => "Show OpenACAI-owned Sons asset and audio path helpers.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public OpenACAIMenuContentSnapshot CreateContent()
            {
                return OpenACAISonsAssets.CreateContent();
            }
        }

        internal static IReadOnlyList<string> SupportedAssetExtensions => AssetExtensions;
        internal static IReadOnlyList<string> SupportedAudioExtensions => AudioExtensions;
    }

    public sealed class OpenACAISonsAssetsSnapshot
    {
        private OpenACAISonsAssetsSnapshot(
            string generatedUtc,
            string serviceKey,
            bool ready,
            string status,
            string detail,
            string storageModel,
            string openAcaiAssetsDirectory,
            string openAcaiModAssetsDirectory,
            string openAcaiAudioDirectory,
            string openAcaiModAudioDirectory,
            bool openAcaiAssetsDirectoryExists,
            bool openAcaiModAssetsDirectoryExists,
            bool openAcaiAudioDirectoryExists,
            bool openAcaiModAudioDirectoryExists,
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
            IReadOnlyList<OpenACAISonsLoadedAssetBundleSnapshot> loadedAssetBundles,
            IReadOnlyList<OpenACAISonsLoadedAudioClipSnapshot> loadedAudioClips,
            IReadOnlyList<OpenACAISonsPendingAudioClipLoadSnapshot> pendingAudioClipLoads,
            bool runtimeAssetLoadingEnabled,
            bool runtimeAudioLoadingEnabled)
        {
            GeneratedUtc = generatedUtc ?? "";
            ServiceKey = serviceKey ?? OpenACAISonsAssets.ServiceKey;
            Ready = ready;
            Status = status ?? "";
            Detail = detail ?? "";
            StorageModel = storageModel ?? "";
            OpenAcaiAssetsDirectory = openAcaiAssetsDirectory ?? "";
            OpenAcaiModAssetsDirectory = openAcaiModAssetsDirectory ?? "";
            OpenAcaiAudioDirectory = openAcaiAudioDirectory ?? "";
            OpenAcaiModAudioDirectory = openAcaiModAudioDirectory ?? "";
            OpenAcaiAssetsDirectoryExists = openAcaiAssetsDirectoryExists;
            OpenAcaiModAssetsDirectoryExists = openAcaiModAssetsDirectoryExists;
            OpenAcaiAudioDirectoryExists = openAcaiAudioDirectoryExists;
            OpenAcaiModAudioDirectoryExists = openAcaiModAudioDirectoryExists;
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
            LoadedAssetBundles = loadedAssetBundles ?? Array.Empty<OpenACAISonsLoadedAssetBundleSnapshot>();
            LoadedAudioClips = loadedAudioClips ?? Array.Empty<OpenACAISonsLoadedAudioClipSnapshot>();
            PendingAudioClipLoads = pendingAudioClipLoads ?? Array.Empty<OpenACAISonsPendingAudioClipLoadSnapshot>();
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
        public string OpenAcaiAssetsDirectory { get; }
        public string OpenAcaiModAssetsDirectory { get; }
        public string OpenAcaiAudioDirectory { get; }
        public string OpenAcaiModAudioDirectory { get; }
        public bool OpenAcaiAssetsDirectoryExists { get; }
        public bool OpenAcaiModAssetsDirectoryExists { get; }
        public bool OpenAcaiAudioDirectoryExists { get; }
        public bool OpenAcaiModAudioDirectoryExists { get; }
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
        public IReadOnlyList<OpenACAISonsLoadedAssetBundleSnapshot> LoadedAssetBundles { get; }
        public IReadOnlyList<OpenACAISonsLoadedAudioClipSnapshot> LoadedAudioClips { get; }
        public IReadOnlyList<OpenACAISonsPendingAudioClipLoadSnapshot> PendingAudioClipLoads { get; }
        public bool RuntimeAssetLoadingEnabled { get; }
        public bool RuntimeAudioLoadingEnabled { get; }

        internal static OpenACAISonsAssetsSnapshot Create()
        {
            var environment = OpenACAISons.Environment;
            var userDataDirectory = !string.IsNullOrWhiteSpace(environment.OpenAcaiUserDataDirectory)
                ? environment.OpenAcaiUserDataDirectory
                : OpenACAIPaths.UserDataDirectory;
            var openAcaiAssetsDirectory = Combine(userDataDirectory, OpenACAISonsAssets.OpenACAIAssetsDirectoryName);
            var openAcaiModAssetsDirectory = Combine(openAcaiAssetsDirectory, OpenACAISonsAssets.OpenACAIModAssetsDirectoryName);
            var openAcaiAudioDirectory = Combine(userDataDirectory, OpenACAISonsAssets.OpenACAIAudioDirectoryName);
            var openAcaiModAudioDirectory = Combine(openAcaiAudioDirectory, OpenACAISonsAssets.OpenACAIModAssetsDirectoryName);

            SafeCreateDirectory(openAcaiAssetsDirectory);
            SafeCreateDirectory(openAcaiModAssetsDirectory);
            SafeCreateDirectory(openAcaiAudioDirectory);
            SafeCreateDirectory(openAcaiModAudioDirectory);

            var assetsReady = Directory.Exists(openAcaiAssetsDirectory) && Directory.Exists(openAcaiModAssetsDirectory);
            var audioReady = Directory.Exists(openAcaiAudioDirectory) && Directory.Exists(openAcaiModAudioDirectory);
            var ready = assetsReady && audioReady;
            bool assetFileLimitReached;
            bool assetBundleFileLimitReached;
            bool audioFileLimitReached;
            var assetFileCount = CountSupportedFiles(
                openAcaiModAssetsDirectory,
                OpenACAISonsAssets.SupportedAssetExtensions,
                OpenACAISonsAssets.MaxListedFiles,
                out assetFileLimitReached);
            var assetBundleFileCount = CountSupportedFiles(
                openAcaiModAssetsDirectory,
                new[] { ".assetbundle", ".bundle" },
                OpenACAISonsAssets.MaxListedFiles,
                out assetBundleFileLimitReached);
            var audioFileCount = CountSupportedFiles(
                openAcaiModAudioDirectory,
                OpenACAISonsAssets.SupportedAudioExtensions,
                OpenACAISonsAssets.MaxListedFiles,
                out audioFileLimitReached);
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
            IReadOnlyList<OpenACAISonsLoadedAssetBundleSnapshot> loadedAssetBundles;
            IReadOnlyList<OpenACAISonsLoadedAudioClipSnapshot> loadedAudioClips;
            IReadOnlyList<OpenACAISonsPendingAudioClipLoadSnapshot> pendingAudioClipLoads;
            OpenACAISonsAssets.GetRuntimeMetrics(
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
            return new OpenACAISonsAssetsSnapshot(
                DateTimeOffset.UtcNow.ToString("O"),
                OpenACAISonsAssets.ServiceKey,
                ready,
                ready ? "READY" : "CHECK",
                ready
                    ? "OpenACAI per-mod asset and audio directories are available."
                    : "OpenACAI per-mod asset or audio directories could not be verified.",
                "openacai-userdata-per-mod-assets",
                openAcaiAssetsDirectory,
                openAcaiModAssetsDirectory,
                openAcaiAudioDirectory,
                openAcaiModAudioDirectory,
                Directory.Exists(openAcaiAssetsDirectory),
                Directory.Exists(openAcaiModAssetsDirectory),
                Directory.Exists(openAcaiAudioDirectory),
                Directory.Exists(openAcaiModAudioDirectory),
                Combine(openAcaiModAssetsDirectory, "{normalized-mod-id}"),
                Combine(openAcaiModAudioDirectory, "{normalized-mod-id}"),
                OpenACAISonsAssets.SupportedAssetExtensions,
                OpenACAISonsAssets.SupportedAudioExtensions,
                OpenACAISonsAssets.DefaultMaxListedFiles,
                OpenACAISonsAssets.MaxListedFiles,
                assetFileCount,
                assetBundleFileCount,
                audioFileCount,
                assetFileLimitReached || assetBundleFileLimitReached,
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

        internal static OpenACAISonsAssetsSnapshot CreateUnknown()
        {
            return new OpenACAISonsAssetsSnapshot(
                DateTimeOffset.UtcNow.ToString("O"),
                OpenACAISonsAssets.ServiceKey,
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
                OpenACAISonsAssets.SupportedAssetExtensions,
                OpenACAISonsAssets.SupportedAudioExtensions,
                OpenACAISonsAssets.DefaultMaxListedFiles,
                OpenACAISonsAssets.MaxListedFiles,
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
                Array.Empty<OpenACAISonsLoadedAssetBundleSnapshot>(),
                Array.Empty<OpenACAISonsLoadedAudioClipSnapshot>(),
                Array.Empty<OpenACAISonsPendingAudioClipLoadSnapshot>(),
                false,
                false);
        }

        private static string Combine(string left, string right)
        {
            return string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)
                ? null
                : Path.Combine(left, right);
        }

        private static int CountSupportedFiles(string root, IEnumerable<string> extensions, int maxFiles, out bool limitReached)
        {
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

            var limit = Math.Max(1, maxFiles <= 0 ? OpenACAISonsAssets.MaxListedFiles : maxFiles);
            var count = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    if (extensionSet.Count > 0 && !extensionSet.Contains(Path.GetExtension(file)))
                    {
                        continue;
                    }

                    count++;
                    if (count > limit)
                    {
                        limitReached = true;
                        return limit;
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

    public sealed class OpenACAISonsModAssetPath
    {
        internal OpenACAISonsModAssetPath(string kind, string modId, string normalizedModId, string directory, bool exists)
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

    public sealed class OpenACAISonsAssetFileSnapshot
    {
        internal OpenACAISonsAssetFileSnapshot(string kind, string relativePath, string fullPath, long length, string lastWriteUtc)
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

    public sealed class OpenACAISonsLoadedAssetBundleSnapshot
    {
        internal OpenACAISonsLoadedAssetBundleSnapshot(
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

    public sealed class OpenACAISonsLoadedAudioClipSnapshot
    {
        internal OpenACAISonsLoadedAudioClipSnapshot(
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

    public sealed class OpenACAISonsPendingAudioClipLoadSnapshot
    {
        internal OpenACAISonsPendingAudioClipLoadSnapshot(
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

    public sealed class OpenACAISonsAssetLoadResult
    {
        private OpenACAISonsAssetLoadResult(
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

        internal static OpenACAISonsAssetLoadResult Succeeded(
            string operation,
            string modId,
            string normalizedModId,
            string relativePath,
            string fullPath,
            bool cached,
            string detail)
        {
            return new OpenACAISonsAssetLoadResult(
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

        internal static OpenACAISonsAssetLoadResult Pending(
            string operation,
            string modId,
            string normalizedModId,
            string relativePath,
            string fullPath,
            bool cached,
            string detail)
        {
            return new OpenACAISonsAssetLoadResult(
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

        internal static OpenACAISonsAssetLoadResult Failed(
            string operation,
            string modId,
            string normalizedModId,
            string relativePath,
            string fullPath,
            string error)
        {
            return new OpenACAISonsAssetLoadResult(
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
    }
}
