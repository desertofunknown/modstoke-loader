// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using NativeCameras = Il2CppSystem.Collections.Generic.List<UnityEngine.Camera>;

namespace Modstoke.Loader
{
    /// <summary>
    /// Puts the game's main camera at the photo pose for the frame's rendering only. HDRP takes each camera's
    /// pose and culls it before beginCameraRendering, so the pose and field of view go on in beginContextRendering
    /// and come off when the main camera has rendered, or at the end of the context if it never did; in between,
    /// the player's own renderers are gated for its cull. Gameplay, aim, audio and the networked look direction
    /// never see the camera anywhere but where the game put it, and if anything here fails the next frame simply
    /// renders from there. A callback never throws into the engine: the first fault is kept for the session.
    /// </summary>
    internal sealed class PhotoRenderHooks : IDisposable
    {
        private readonly Camera _main;
        private readonly IntPtr _mainPointer;
        private readonly PhotoCamera _rig;
        private readonly PhotoSubject _subject;
        private Il2CppSystem.Action<ScriptableRenderContext, NativeCameras> _beginContext, _endContext;
        private Il2CppSystem.Action<ScriptableRenderContext, Camera> _beginCamera, _endCamera;
        private Vector3 _savedPosition;
        private Quaternion _savedRotation;
        private float _savedFov;
        private bool _applied, _disposed;

        internal PhotoRenderHooks(Camera main, PhotoCamera rig, PhotoSubject subject)
        {
            _main = main;
            _mainPointer = main.Pointer;
            _rig = rig;
            _subject = subject;
            _beginContext = (Action<ScriptableRenderContext, NativeCameras>)BeginContext;
            _endContext = (Action<ScriptableRenderContext, NativeCameras>)EndContext;
            _beginCamera = (Action<ScriptableRenderContext, Camera>)BeginCamera;
            _endCamera = (Action<ScriptableRenderContext, Camera>)EndCamera;
            RenderPipelineManager.add_beginContextRendering(_beginContext);
            RenderPipelineManager.add_endContextRendering(_endContext);
            RenderPipelineManager.add_beginCameraRendering(_beginCamera);
            RenderPipelineManager.add_endCameraRendering(_endCamera);
            Current = this;
        }

        /// <summary>The hooks of the open session, for the grass patch.</summary>
        internal static PhotoRenderHooks Current { get; private set; }

        /// <summary>Whether frames render from the photo pose.</summary>
        internal bool Armed { get; set; }

        /// <summary>The first exception a render callback caught, for the session to end on.</summary>
        internal Exception Fault { get; private set; }

        internal Camera Main => _main;

        /// <summary>Puts the photo pose on the camera until <see cref="RestoreIfApplied"/>; false when it was not applied.</summary>
        internal bool ApplyTemporary()
        {
            if (!Armed || _applied || _disposed || !Live(_main)) return false;
            var transform = _main.transform;
            // Marked first, so a throw part way through is still undone.
            _applied = true;
            transform.GetLocalPositionAndRotation(out _savedPosition, out _savedRotation);
            _savedFov = _main.fieldOfView;
            transform.SetPositionAndRotation(_rig.Position, _rig.Rotation);
            _main.fieldOfView = _rig.Fov;
            return true;
        }

        internal void RestoreIfApplied()
        {
            if (!_applied) return;
            _applied = false;
            if (!Live(_main)) return;
            _main.transform.SetLocalPositionAndRotation(_savedPosition, _savedRotation);
            _main.fieldOfView = _savedFov;
        }

        /// <summary>Starts the camera's temporal history afresh, after the view jumps.</summary>
        internal void ResetHistory()
        {
            if (Live(_main)) HDCamera.GetOrCreate(_main, 0).Reset();
        }

        private void BeginContext(ScriptableRenderContext context, NativeCameras cameras)
        {
            try
            {
                if (!Armed || cameras == null) return;
                for (var i = 0; i < cameras.Count; i++)
                {
                    var camera = cameras[i];
                    if (camera == null || camera.Pointer != _mainPointer) continue;
                    ApplyTemporary();
                    return;
                }
            }
            catch (Exception error)
            {
                Fault ??= error;
                SafeRestore();
            }
        }

        private void BeginCamera(ScriptableRenderContext context, Camera camera)
        {
            try
            {
                _subject.Gate(Armed && camera != null && camera.Pointer == _mainPointer);
            }
            catch (Exception error)
            {
                Fault ??= error;
                SafeClose();
            }
        }

        private void EndCamera(ScriptableRenderContext context, Camera camera)
        {
            if (camera == null || camera.Pointer != _mainPointer) return;
            try
            {
                RestoreIfApplied();
            }
            catch (Exception error)
            {
                Fault ??= error;
            }
            finally
            {
                SafeClose();
            }
        }

        private void EndContext(ScriptableRenderContext context, NativeCameras cameras)
        {
            try
            {
                RestoreIfApplied();
            }
            catch (Exception error)
            {
                Fault ??= error;
            }
            finally
            {
                SafeClose();
            }
        }

        private void SafeRestore()
        {
            try { RestoreIfApplied(); }
            catch (Exception error) { Fault ??= error; }
        }

        private void SafeClose()
        {
            try { _subject.CloseGate(); }
            catch (Exception error) { Fault ??= error; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            Armed = false;
            SafeRestore();
            SafeClose();
            _disposed = true;
            if (ReferenceEquals(Current, this)) Current = null;
            Remove(() => RenderPipelineManager.remove_beginContextRendering(_beginContext));
            Remove(() => RenderPipelineManager.remove_endContextRendering(_endContext));
            Remove(() => RenderPipelineManager.remove_beginCameraRendering(_beginCamera));
            Remove(() => RenderPipelineManager.remove_endCameraRendering(_endCamera));
            _beginContext = _endContext = null;
            _beginCamera = _endCamera = null;
        }

        private static void Remove(Action remove)
        {
            try { remove(); }
            catch (Exception error) { RuntimeEntryPoint.LogSource?.LogWarning("Photo mode could not remove a render callback: " + error.Message); }
        }

        private static bool Live(UnityEngine.Object value) => value is not null && !value.WasCollected && value;
    }
}
