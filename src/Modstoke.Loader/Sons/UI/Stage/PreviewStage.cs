// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Modstoke.Loader;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Modstoke.Sons
{
    /// <summary>Isolated client-only HDRP preview of character parts, rendered to a texture. Main thread only.
    /// Never networked or saved, never parented to the player. Dispose with the UI that created it.</summary>
    public sealed partial class PreviewStage : IDisposable
    {
        private const string RootName = "Modstoke.PreviewStage";
        private static int _ownerThread;
        private static bool _initialized;
        private static PreviewStage _instance;
        private static volatile PreviewStageSnapshot _current;

        private readonly string _ownerId;
        private readonly bool _allowTitleScene;
        private readonly string _ownerScene;
        private readonly Color _background;
        private readonly float _qualityScale;
        private readonly int _continuousMaxHz;
        private readonly PreviewLightRig _lightRig;
        private readonly bool _volumeOverridesRequested;
        private readonly List<Renderer> _renderers = new();
        private GameObject _root;
        private bool _disposed;
        private bool _disposeRequested;
        private bool _visible = true;
        private PreviewRenderMode _renderMode;
        private string _status = "ready";
        private int _tickedFrame = -1;

        private PreviewStage(PreviewStageOptions options)
        {
            _ownerId = options.OwnerId.Trim();
            _allowTitleScene = options.AllowTitleScene;
            _ownerScene = SceneManager.GetActiveScene().name;
            _background = options.Background;
            _qualityScale = Mathf.Clamp(float.IsFinite(options.QualityScale) ? options.QualityScale : 1f, .5f, 1.25f);
            _continuousMaxHz = Math.Clamp(options.ContinuousMaxHz, 1, 60);
            _lightRig = options.LightRig;
            _volumeOverridesRequested = options.VolumeOverrides;
        }

        /// <summary>The live stage's snapshot, or null when no stage is alive.</summary>
        public static PreviewStageSnapshot Current => _current;

        public string OwnerId => _ownerId;
        public bool IsAlive => !_disposed && !_disposeRequested && Live(_root);

        /// <summary>Replaced when the viewport size changes; listen to <see cref="TextureChanged"/>.</summary>
        public Texture Texture => _disposed ? null : _target;

        public event Action<Texture> TextureChanged;

        public PreviewRenderMode RenderMode
        {
            get => _renderMode;
            set
            {
                if (!Usable() || _renderMode == value) return;
                _renderMode = value;
                _dirty = true;
            }
        }

        /// <summary>False stops all rendering.</summary>
        public bool Visible
        {
            get => _visible;
            set
            {
                if (!Usable() || _visible == value) return;
                _visible = value;
                _dirty = true;
                Publish();
            }
        }

        internal static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            _ownerThread = Environment.CurrentManagedThreadId;
            GameRuntime.WorldChanged += OnWorldChanged;
            GameRuntime.SceneChanged += OnSceneChanged;
        }

        public static bool TryCreate(PreviewStageOptions options, out PreviewStage stage, out string status)
        {
            stage = null;
            if (!OnOwnerThread(out status)) return false;
            if (options == null || string.IsNullOrWhiteSpace(options.OwnerId))
            {
                status = "A preview stage needs an owner ID.";
                return false;
            }
            if (GameBuild.IsDedicatedServer)
            {
                status = "A dedicated server has no renderer for a preview stage.";
                return false;
            }
            if (_instance != null)
            {
                status = "A preview stage is already open for " + _instance._ownerId + ".";
                return false;
            }
            if (!options.AllowTitleScene && !GameRuntime.IsInWorld)
            {
                status = "This preview stage is available in the world only.";
                return false;
            }
            if (RenderPipelineManager.currentPipeline?.TryCast<HDRenderPipeline>() == null)
            {
                status = "HDRP is not active.";
                return false;
            }

            var created = new PreviewStage(options);
            try
            {
                created.Build();
            }
            catch (Exception error)
            {
                created._disposed = true;
                created.Release();
                status = "The preview stage could not be created: " + error.Message;
                RuntimeEntryPoint.LogSource?.LogWarning(status);
                return false;
            }
            _instance = created;
            created.Publish();
            stage = created;
            status = created._status;
            RuntimeEntryPoint.LogSource?.LogInfo("Preview stage opened for " + created._ownerId + " · layer "
                + LayerMask.LayerToName(created._layer) + " · light layers " + (created._lightLayers ? "on" : "off")
                + " · frame settings " + created._frameSource + " · " + created.DescribeDirectionalLights() + ".");
            return true;
        }

        public PreviewStageSnapshot Snapshot() => PreviewStageSnapshot.Create(_ownerId, IsAlive, _visible,
            _width, _height, PreviewStageTiers.Base, _lightLayers, _layer, _frameSource, _litShaderMode, _exposure,
            _parts.Count, _lastRender, _status);

        /// <summary>Requests a frame after renderer or material edits the stage cannot see.</summary>
        public void MarkDirty()
        {
            if (Usable()) _dirty = true;
        }

        public void Dispose() => Close("disposed");

        internal static void Tick() => _instance?.Update();

        internal static void Shutdown() => _instance?.Close("loader shutdown");

        // A throwaway cube with a native material proves the camera, isolation and lights before parts exist.
        internal bool TryShowDebugCube(Material material, out string status)
        {
            if (!Usable(out status)) return false;
            if (!Live(material))
            {
                status = "The debug cube needs a native material.";
                return false;
            }
            try
            {
                var primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Mesh mesh;
                try
                {
                    mesh = primitive.GetComponent<MeshFilter>().sharedMesh;
                }
                finally
                {
                    Object.DestroyImmediate(primitive);
                }
                var node = new GameObject("Debug cube");
                node.transform.SetParent(_root.transform, false);
                node.transform.localPosition = DefaultPivot;
                node.transform.localRotation = Quaternion.Euler(0f, 30f, 0f);
                node.transform.localScale = Vector3.one * .5f;
                node.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = node.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                PrepareRenderer(renderer);
                RefreshFocus();
            }
            catch (Exception error)
            {
                status = "The debug cube could not be created: " + error.Message;
                return false;
            }
            status = "";
            return true;
        }

        private void Build()
        {
            _root = new GameObject(RootName) { hideFlags = HideFlags.HideAndDontSave };
            _root.SetActive(false);
            if (_allowTitleScene) Object.DontDestroyOnLoad(_root);
            _root.transform.SetPositionAndRotation(StageOrigin, Quaternion.identity);
            ChooseIsolation();
            _root.layer = _layer;
            CreateContent();
            CreateCamera();
            if (_lightRig == PreviewLightRig.Neutral) CreateLights();
            if (_volumeOverridesRequested) _status = "volume-overrides-unavailable";
            SetFocus(PreviewFocus.Body, true);
            ApplyRig();
            _root.SetActive(true);
            _dirty = true;
        }

        private void Update()
        {
            if (_disposed || Time.frameCount == _tickedFrame) return;
            _tickedFrame = Time.frameCount;
            if (_disposeRequested)
            {
                Close("disposed");
                return;
            }
            if (_renderFailure != null)
            {
                Close("render callback failed: " + _renderFailure.Message);
                return;
            }
            if (!Live(_root) || !Live(_camera) || !Live(_target))
            {
                Close("its root, camera or texture was destroyed");
                return;
            }
            try
            {
                UpdateFrame();
                // A readback or build handler may have closed the stage.
                if (_disposed) return;
                UpdateBuild();
                FlushCompleted();
                if (_disposed) return;
                UpdateTarget();
                if (StepRig(Time.unscaledDeltaTime))
                {
                    ApplyRig();
                    _dirty = true;
                }
                ScheduleFrame();
            }
            catch (Exception error)
            {
                Close("update failed: " + error.Message);
            }
        }

        private void Close(string reason)
        {
            if (_disposed) return;
            if (_ownerThread == 0 || Environment.CurrentManagedThreadId != _ownerThread)
            {
                if (_disposeRequested) return;
                _disposeRequested = true;
                RuntimeEntryPoint.LogSource?.LogWarning("Preview stage for " + _ownerId
                    + " was disposed off the game thread; it closes on the next Loader tick.");
                return;
            }
            _disposed = true;
            _status = "closed: " + reason;
            string failure = Release();
            if (_instance == this) _instance = null;
            _current = null;
            var final = Snapshot();
            if (failure == null)
                RuntimeEntryPoint.LogSource?.LogInfo("Preview stage for " + _ownerId + " closed (" + reason + ").");
            else
                RuntimeEntryPoint.LogSource?.LogWarning("Preview stage for " + _ownerId + " closed (" + reason
                    + ") with cleanup errors: " + failure);
            if (_readback != null) CompleteReadback("the stage closed (" + final.Status + ")");
        }

        // Each step is guarded so a partial setup still releases everything it made.
        private string Release()
        {
            string failure = null;
            void Step(Action action)
            {
                try { action(); }
                catch (Exception error) { failure ??= error.Message; }
            }
            Step(Unsubscribe);
            Step(ReleaseHeldLights);
            if (Live(_camera))
            {
                Step(() => _camera.enabled = false);
                Step(() => _camera.targetTexture = null);
            }
            if (Live(_root)) Step(() => Object.Destroy(_root));
            Step(ReleaseVolume);
            string content = null;
            Step(() => content = ReleaseContent());
            failure ??= content;
            if (Live(_target))
            {
                Step(() => _target.Release());
                Step(() => Object.Destroy(_target));
            }
            _renderers.Clear();
            _lightData.Clear();
            _directionalLights.Clear();
            _root = null;
            _cameraObject = null;
            _camera = null;
            _cameraPointer = IntPtr.Zero;
            _lights = null;
            _target = null;
            _pending = false;
            return failure;
        }

        private void Publish()
        {
            if (_instance == this && !_disposed) _current = Snapshot();
        }

        private bool Usable() => Usable(out _);

        private bool Usable(out string status)
        {
            if (!OnOwnerThread(out status)) return false;
            if (_disposed || _disposeRequested)
            {
                status = "The preview stage is closed.";
                return false;
            }
            return true;
        }

        private static bool OnOwnerThread(out string status)
        {
            if (_ownerThread == 0 || Environment.CurrentManagedThreadId != _ownerThread)
            {
                status = "The preview stage runs on the game thread only.";
                return false;
            }
            status = "";
            return true;
        }

        private static void OnWorldChanged(WorldEvent world)
        {
            var stage = _instance;
            if (stage != null && !stage._allowTitleScene && !world.IsInWorld)
                stage.Close("world exited");
        }

        private static void OnSceneChanged(SceneEvent scene)
        {
            var stage = _instance;
            if (stage != null && string.Equals(scene.Phase, "Unloaded", StringComparison.OrdinalIgnoreCase)
                && string.Equals(scene.SceneName, stage._ownerScene, StringComparison.OrdinalIgnoreCase))
                stage.Close(scene.SceneName + " unloaded");
        }
    }
}
