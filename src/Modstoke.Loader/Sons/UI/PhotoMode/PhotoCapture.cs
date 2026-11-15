// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using AEG.FSR;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using Object = UnityEngine.Object;

namespace Modstoke.Loader
{
    /// <summary>
    /// One photo to take. <paramref name="Crop"/> is the part of the <paramref name="Width"/> × <paramref name="Height"/>
    /// frame that is saved, measured from the picture's top-left corner; an empty crop keeps the whole frame.
    /// <paramref name="Renders"/> is how many times the camera renders into the photo, each a step of the
    /// anti-aliasing jitter. <paramref name="Description"/> goes into the file and is the Steam location;
    /// <paramref name="MetadataJson"/> is stored as the file's Modstoke.Photo text.
    /// </summary>
    internal sealed record PhotoShotRequest(Camera Camera, int Width, int Height, int Renders, RectInt Crop, string Description,
        string MetadataJson, bool PicturesFolder, bool Steam);

    /// <summary>
    /// How a photo turned out. <paramref name="Error"/> is why it was not saved, the {reason} of photo.failed.
    /// <paramref name="Thumbnail"/> is the capture's small copy of its latest shot, so a newer photo replaces it,
    /// and is null once the capture is disposed.
    /// </summary>
    internal sealed record PhotoResult(bool Saved, string Path, string FileName, bool InSteam, string Error, Texture Thumbnail);

    /// <summary>
    /// Takes photos with the game's own camera, the way it renders its save thumbnails: the camera renders several
    /// times in a row into a large texture within one frame, each render a new step of the anti-aliasing jitter, so
    /// the photo matches the view and comes out clean. Upscalers and dynamic resolution stand down for those
    /// renders only. The pixels come back over the following frames without stalling the GPU and go to
    /// <see cref="PhotoWriter"/>; <see cref="PhotoLibrary.Tick"/> reports the outcome through <see cref="Completed"/>.
    /// </summary>
    internal sealed class PhotoCapture : IDisposable
    {
        private const int UiLayer = 5;
        private const int MaxRenders = 16;
        private const int ThumbnailEdge = 384;
        private const int StandardEdge = 3840, LargeEdge = 7680;
        // HDRP grows every internal buffer to the largest size it renders; above 4K that is several GB more.
        private const int LargeMemoryMb = 12000;
        private static readonly TimeSpan ReadbackTimeout = TimeSpan.FromSeconds(5);
        private static int _maxTextureSize = -1;
        private static bool? _largeAllowed;

        private sealed class Pending
        {
            internal RenderTexture Target;
            internal AsyncGPUReadbackRequest Request;
            internal bool Async;
            internal PhotoJob Job;
            internal long Started;
        }

        private Pending _pending;
        private RenderTexture _thumbnail;
        private int _developing;
        private string _status = "";
        private bool _disposed, _needsBufferReset;

        /// <summary>Raised on the game thread when a photo has been saved, or has failed, and Steam has had it.</summary>
        internal event Action<PhotoResult> Completed;

        internal PhotoCapture()
        {
            PhotoWriter.Start();
        }

        /// <summary>True while a new photo would be refused: one is still coming back from the GPU or the writer is full.</summary>
        internal bool Busy => _pending != null || !PhotoWriter.HasRoom;

        /// <summary>"Developing…" while a photo is on its way, then how the last one turned out; empty before the first.</summary>
        internal string Status => _pending != null || _developing > 0 ? FrontEnd.Text["photo.developing"] : _status;

        /// <summary>
        /// The photo sizes this PC is offered, as ids "screen", "4k", "2x" and "8k". Sizes larger than both 4K and
        /// the screen need 12 GB of video memory and DirectX 11; DirectX 12 stays at 4K until long bursts are proven
        /// there. A size that repeats an earlier entry is left out, so a remembered id can be missing; fall back to
        /// the nearest entry.
        /// </summary>
        internal static IReadOnlyList<(string Id, int Width, int Height)> Resolutions(int screenWidth, int screenHeight)
        {
            var sizes = new List<(string Id, int Width, int Height)>(4);
            if (screenWidth < 1 || screenHeight < 1) return sizes;
            Offer(sizes, "screen", screenWidth, screenHeight, screenWidth, screenHeight);
            var (width, height) = LongEdge(StandardEdge, screenWidth, screenHeight);
            Offer(sizes, "4k", width, height, screenWidth, screenHeight);
            Offer(sizes, "2x", screenWidth * 2, screenHeight * 2, screenWidth, screenHeight);
            (width, height) = LongEdge(LargeEdge, screenWidth, screenHeight);
            Offer(sizes, "8k", width, height, screenWidth, screenHeight);
            return sizes;
        }

        /// <summary>
        /// Renders the photo now and starts bringing it back; call late in the frame, once the grass has been
        /// queued for it, with the render hooks armed for the photo pose. Returns false with a sentence to show
        /// when the photo cannot be taken. The camera and every setting changed for the renders are restored
        /// before this returns, whatever happens.
        /// </summary>
        internal bool TryShoot(PhotoShotRequest request, out string reason)
        {
            reason = null;
            if (!_disposed && Busy)
            {
                reason = FrontEnd.Text["photo.developing"];
                return false;
            }
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                ArgumentNullException.ThrowIfNull(request);
                var camera = request.Camera;
                if (camera == null || !camera) throw new InvalidOperationException("The camera is gone.");
                int width = request.Width, height = request.Height, screenWidth = Screen.width, screenHeight = Screen.height;
                if (width < 1 || height < 1 || !Allowed(width, height, screenWidth, screenHeight))
                    throw new InvalidOperationException(width + " × " + height + " is not offered on this PC.");

                var job = new PhotoJob
                {
                    Width = width,
                    Height = height,
                    Crop = CropOf(request.Crop, width, height),
                    BottomUp = true,
                    Folder = PhotoLibrary.Folder(request.PicturesFolder),
                    Fallback = PhotoLibrary.Folder(false),
                    Taken = DateTimeOffset.Now,
                    Description = request.Description,
                    MetadataJson = request.MetadataJson,
                    Steam = request.Steam,
                    Owner = this
                };
                var target = Burst(camera, width, height, Math.Clamp(request.Renders, 1, MaxRenders), job.Crop);
                try
                {
                    var pending = new Pending { Target = target, Job = job, Started = Stopwatch.GetTimestamp() };
                    if (SystemInfo.supportsAsyncGPUReadback)
                    {
                        pending.Request = AsyncGPUReadback.Request(target, 0, 0, width, 0, height, 0, 1, null);
                        pending.Async = true;
                    }
                    _pending = pending;
                }
                catch
                {
                    RenderTexture.ReleaseTemporary(target);
                    throw;
                }
                if (width > screenWidth || height > screenHeight) _needsBufferReset = true;
                return true;
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("A photo could not be taken: " + error);
                reason = FrontEnd.Text.Get("photo.failed", ("reason", error.Message));
                return false;
            }
        }

        /// <summary>Picks up the photo once the GPU has it; call once a frame on the game thread.</summary>
        internal void Tick()
        {
            var pending = _pending;
            if (pending == null) return;
            try
            {
                if (pending.Async && !pending.Request.done)
                {
                    if (Stopwatch.GetElapsedTime(pending.Started) < ReadbackTimeout) return;
                    RuntimeEntryPoint.LogSource?.LogWarning("The GPU did not return a photo in time; reading it directly.");
                    pending.Async = false;
                }
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The photo readback failed; reading it directly: " + error.Message);
                pending.Async = false;
            }
            Collect(pending, false);
        }

        /// <summary>
        /// Gives back the HDRP buffers a larger-than-screen photo grew, which resets the camera history once; call
        /// as photo mode closes. A photo still coming back is unaffected, as it renders into its own texture.
        /// </summary>
        internal void ShrinkBuffers()
        {
            if (!_needsBufferReset) return;
            _needsBufferReset = false;
            try
            {
                var pipeline = RenderPipelineManager.currentPipeline?.TryCast<HDRenderPipeline>();
                pipeline?.ResetRTHandleReferenceSize(Screen.width, Screen.height);
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("HDRP's buffers stay at the photo size until the screen size changes: " + error.Message);
            }
        }

        /// <summary>A photo still coming back is collected first, so it is saved even though the menus are stopping.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_pending != null) Collect(_pending, true);
            ShrinkBuffers();
            try
            {
                if (_thumbnail != null && _thumbnail) Object.Destroy(_thumbnail);
            }
            catch (Exception)
            {
            }
            _thumbnail = null;
        }

        /// <summary>Called by <see cref="PhotoLibrary"/> with the outcome of a photo this capture handed to the writer.</summary>
        internal void Finish(bool saved, string path, bool inSteam, string error)
        {
            if (_developing > 0) _developing--;
            Report(saved, path, inSteam, error);
        }

        private void Report(bool saved, string path, bool inSteam, string error)
        {
            var text = FrontEnd.Text;
            var fileName = saved ? Path.GetFileName(path) : null;
            if (!saved) error = string.IsNullOrWhiteSpace(error) ? "?" : error.Trim();
            _status = saved
                ? text["photo.saved"] + " · " + fileName + (inSteam ? " · " + text["photo.steamAdded"] : "")
                : text.Get("photo.failed", ("reason", error));
            var thumbnail = !_disposed && _thumbnail != null && _thumbnail ? _thumbnail : null;
            var completed = Completed;
            if (completed == null) return;
            try
            {
                completed(new PhotoResult(saved, saved ? path : null, fileName, inSteam, saved ? null : error, thumbnail));
            }
            catch (Exception failure)
            {
                RuntimeEntryPoint.LogSource?.LogError("A photo result handler failed: " + failure);
            }
        }

        private RenderTexture Burst(Camera camera, int width, int height, int renders, (int X, int Y, int Width, int Height) crop)
        {
            var started = Stopwatch.GetTimestamp();
            using var timing = Timings.MeasureCapture("Photo.Burst");
            var descriptor = new RenderTextureDescriptor(width, height, GraphicsFormat.R8G8B8A8_SRGB, 24)
            {
                msaaSamples = 1,
                useDynamicScale = false
            };
            var redraw = PhotoPatches.GrassNeedsRedraw(camera);
            var target = RenderTexture.GetTemporary(descriptor);
            var kept = false;
            var rendered = 0;
            try
            {
                using (var scope = RenderScope.Enter(camera))
                {
                    camera.targetTexture = target;
                    // A size change does not reset the temporal history, so without this the preview's frame,
                    // upscaled from the screen, would be most of a short burst.
                    var history = HDCamera.GetOrCreate(camera, 0);
                    history.resetPostProcessingHistory = true;
                    for (var i = 0; i < renders; i++)
                    {
                        if (i > 0 && redraw) PhotoPatches.RedrawGrass();
                        // The n-th render keeps 1 - 1/n of the history, so the photo is the even average of them all.
                        scope.Blend(1f - 1f / (i + 1));
                        camera.Render();
                        rendered++;
                    }
                    MakeThumbnail(target, crop);
                    // And the preview starts afresh rather than reprojecting the photo-sized history.
                    history.resetPostProcessingHistory = true;
                }
                kept = true;
                return target;
            }
            finally
            {
                if (!kept) RenderTexture.ReleaseTemporary(target);
                // The burst used up the main camera's grass; the frame's own render gets it again.
                if (redraw && rendered > 0) RedrawGrass();
                Timings.RecordDuration("Photo", "Burst", Stopwatch.GetElapsedTime(started));
            }
        }

        private static void RedrawGrass()
        {
            try { PhotoPatches.RedrawGrass(); }
            catch (Exception error) { RuntimeEntryPoint.LogSource?.LogWarning("The grass could not be drawn again after the photo: " + error.Message); }
        }

        // Halving steps each average 2×2 texels, so the small copy keeps foliage and wires instead of aliasing them.
        private void MakeThumbnail(RenderTexture source, (int X, int Y, int Width, int Height) crop)
        {
            RenderTexture step = null;
            try
            {
                var scale = Math.Min(1f, (float)ThumbnailEdge / Math.Max(crop.Width, crop.Height));
                int width = Math.Max(1, (int)Math.Round(crop.Width * scale)), height = Math.Max(1, (int)Math.Round(crop.Height * scale));
                if (_thumbnail == null || !_thumbnail || _thumbnail.width != width || _thumbnail.height != height)
                {
                    if (_thumbnail != null && _thumbnail) Object.Destroy(_thumbnail);
                    _thumbnail = new RenderTexture(new RenderTextureDescriptor(width, height, GraphicsFormat.R8G8B8A8_SRGB, 0)) { name = "Modstoke photo thumbnail" };
                    _thumbnail.Create();
                }

                // Texture coordinates start at the bottom, the crop at the top.
                var uvScale = new Vector2((float)crop.Width / source.width, (float)crop.Height / source.height);
                var uvOffset = new Vector2((float)crop.X / source.width, 1f - (float)(crop.Y + crop.Height) / source.height);
                Texture from = source;
                int w = crop.Width, h = crop.Height;
                while (w >= width * 2 && h >= height * 2)
                {
                    w /= 2;
                    h /= 2;
                    var next = RenderTexture.GetTemporary(new RenderTextureDescriptor(w, h, GraphicsFormat.R8G8B8A8_SRGB, 0));
                    Graphics.Blit(from, next, uvScale, uvOffset);
                    if (step != null) RenderTexture.ReleaseTemporary(step);
                    from = step = next;
                    uvScale = Vector2.one;
                    uvOffset = Vector2.zero;
                }
                Graphics.Blit(from, _thumbnail, uvScale, uvOffset);
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The photo thumbnail could not be made: " + error.Message);
            }
            finally
            {
                if (step != null) RenderTexture.ReleaseTemporary(step);
            }
        }

        private void Collect(Pending pending, bool wait)
        {
            _pending = null;
            var job = pending.Job;
            var size = job.Width * job.Height * 4;
            byte[] pixels = null;
            try
            {
                using (Timings.MeasureCapture("Photo.Readback"))
                {
                    pixels = ArrayPool<byte>.Shared.Rent(size);
                    if (!pending.Async || !CopyReadback(pending.Request, pixels, size, wait))
                        ReadPixels(pending.Target, pixels, job.Width, job.Height);
                }
                Timings.RecordDuration("Photo", "Readback", Stopwatch.GetElapsedTime(pending.Started));
                job.Pixels = pixels;
                if (!PhotoWriter.TryEnqueue(job))
                {
                    job.Pixels = null;
                    throw new InvalidOperationException("The photo writer is full.");
                }
                pixels = null;
                _developing++;
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("A photo could not be read back: " + error);
                Report(false, null, false, error.Message);
            }
            finally
            {
                if (pixels != null) ArrayPool<byte>.Shared.Return(pixels);
                try { RenderTexture.ReleaseTemporary(pending.Target); }
                catch (Exception) { }
            }
        }

        private static bool CopyReadback(AsyncGPUReadbackRequest request, byte[] pixels, int size, bool wait)
        {
            if (wait && !request.done) request.WaitForCompletion();
            if (!request.done || request.hasError || request.layerDataSize < size) return false;
            var data = request.GetDataRaw(0);
            if (data == IntPtr.Zero) return false;
            Marshal.Copy(data, pixels, 0, size);
            return true;
        }

        // The fallback waits for the GPU on this frame, as the game's own save thumbnail does.
        private static void ReadPixels(RenderTexture target, byte[] pixels, int width, int height)
        {
            var size = width * height * 4;
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                var raw = texture.GetRawTextureData();
                if (raw == null || raw.Length < size) throw new InvalidOperationException("The GPU returned no photo.");
                // The engine's array data starts after its object header, bounds and length words.
                Marshal.Copy(IntPtr.Add(raw.Pointer, 4 * IntPtr.Size), pixels, 0, size);
            }
            finally
            {
                RenderTexture.active = previous;
                Object.Destroy(texture);
            }
        }

        private static (int X, int Y, int Width, int Height) CropOf(RectInt crop, int width, int height)
        {
            if (crop.width < 1 || crop.height < 1) return (0, 0, width, height);
            int x = Math.Clamp(crop.x, 0, width - 1), y = Math.Clamp(crop.y, 0, height - 1);
            return (x, y, Math.Min(crop.width, width - x), Math.Min(crop.height, height - y));
        }

        private static (int Width, int Height) LongEdge(int edge, int width, int height) => width >= height
            ? (edge, Math.Max(1, (int)Math.Round((double)edge * height / width)))
            : (Math.Max(1, (int)Math.Round((double)edge * width / height)), edge);

        private static void Offer(List<(string Id, int Width, int Height)> sizes, string id, int width, int height, int screenWidth, int screenHeight)
        {
            if (!Allowed(width, height, screenWidth, screenHeight)) return;
            foreach (var size in sizes)
                if (size.Width == width && size.Height == height) return;
            sizes.Add((id, width, height));
        }

        // The screen's own size is always fine: HDRP already renders it every frame.
        private static bool Allowed(int width, int height, int screenWidth, int screenHeight)
        {
            if (_maxTextureSize < 0) _maxTextureSize = SystemInfo.maxTextureSize;
            var edge = Math.Max(width, height);
            if (edge > _maxTextureSize) return false;
            if (edge <= Math.Max(StandardEdge, Math.Max(screenWidth, screenHeight))) return true;
            _largeAllowed ??= SystemInfo.graphicsMemorySize >= LargeMemoryMb && SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11;
            return _largeAllowed.Value;
        }

        /// <summary>
        /// The camera settings changed for the photo renders, as the game's save thumbnail changes them, each put
        /// back on dispose even when a later one failed: target texture, the UI volume layer (the pause blur lives
        /// there), FSR 3 to anti-aliasing only, DLSS and dynamic resolution off, dithering against banded skies, and
        /// the anti-aliasing's history weight, set render by render.
        /// </summary>
        private sealed class RenderScope : IDisposable
        {
            private readonly Camera _camera;
            private RenderTexture _target, _active;
            private bool _targetSaved;
            private HDAdditionalCameraData _data;
            private LayerMask _volumes;
            private float _blend;
            private bool _dlss, _dataDynamic, _dithering, _dataSaved;
            private bool _cameraDynamic, _cameraSaved;
            private FSR3_HDRP _fsr;
            private FSR_Quality _fsrQuality;

            private RenderScope(Camera camera) => _camera = camera;

            internal static RenderScope Enter(Camera camera)
            {
                var scope = new RenderScope(camera);
                try
                {
                    scope.Apply();
                    return scope;
                }
                catch
                {
                    scope.Dispose();
                    throw;
                }
            }

            private void Apply()
            {
                _target = _camera.targetTexture;
                _active = RenderTexture.active;
                _targetSaved = true;

                var data = _camera.GetComponent<HDAdditionalCameraData>();
                if (data != null)
                {
                    _volumes = data.volumeLayerMask;
                    _dlss = data.allowDeepLearningSuperSampling;
                    _dataDynamic = data.allowDynamicResolution;
                    _dithering = data.dithering;
                    _blend = data.taaBaseBlendFactor;
                    _data = data;
                    _dataSaved = true;
                    data.volumeLayerMask = (LayerMask)(_volumes.value & ~(1 << UiLayer));
                    data.allowDeepLearningSuperSampling = false;
                    data.allowDynamicResolution = false;
                    data.dithering = true;
                }

                _cameraDynamic = _camera.allowDynamicResolution;
                _cameraSaved = true;
                _camera.allowDynamicResolution = false;

                var fsr = _camera.GetComponent<FSR3_HDRP>();
                if (fsr != null && fsr.enabled)
                {
                    _fsrQuality = fsr.FSRQuality;
                    _fsr = fsr;
                    fsr.OnSetQuality(FSR_Quality.TemporalAntiAliasingOnly);
                }
            }

            /// <summary>How much of the anti-aliasing's history the next render keeps, 0 to 1.</summary>
            internal void Blend(float history)
            {
                if (_dataSaved && _data) _data.taaBaseBlendFactor = history;
            }

            public void Dispose()
            {
                if (_fsr != null)
                {
                    var fsr = _fsr;
                    _fsr = null;
                    Restore("FSR 3 quality", () => { if (fsr) fsr.OnSetQuality(_fsrQuality); });
                }
                if (_cameraSaved)
                {
                    _cameraSaved = false;
                    Restore("camera dynamic resolution", () => { if (_camera) _camera.allowDynamicResolution = _cameraDynamic; });
                }
                if (_dataSaved)
                {
                    _dataSaved = false;
                    Restore("HDRP camera settings", () =>
                    {
                        if (!_data) return;
                        _data.volumeLayerMask = _volumes;
                        _data.allowDeepLearningSuperSampling = _dlss;
                        _data.allowDynamicResolution = _dataDynamic;
                        _data.dithering = _dithering;
                        _data.taaBaseBlendFactor = _blend;
                    });
                }
                if (_targetSaved)
                {
                    _targetSaved = false;
                    Restore("camera target", () => { if (_camera) _camera.targetTexture = _target; });
                    Restore("active render texture", () => RenderTexture.active = _active);
                }
            }

            private static void Restore(string what, Action restore)
            {
                try { restore(); }
                catch (Exception error)
                {
                    RuntimeEntryPoint.LogSource?.LogError("The photo could not put back the " + what + ": " + error.Message);
                }
            }
        }
    }
}
