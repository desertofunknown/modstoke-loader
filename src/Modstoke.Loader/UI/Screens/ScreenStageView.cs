// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Sons;
using UnityEngine;
using UnityEngine.UI;

namespace Modstoke.Loader
{
    /// <summary>
    /// Shows a preview stage in the screen host's content area, feeds it the area's pixel size and routes the pointer
    /// to its camera: left drag orbits, the wheel zooms, a double-click resets and a middle drag raises the pivot.
    /// The wheel over the content area never reaches the pane's scroll region, which only scrolls under the pointer.
    /// </summary>
    internal sealed class ScreenStageView
    {
        private const float YawPerPixel = 0.25f;
        private const float PitchPerPixel = 0.2f;
        // A middle drag over the whole area moves the pivot by this share of the framed radius.
        private const float LiftPerHeight = 0.6f;
        private const float DoubleClickSeconds = 0.3f;
        private const float DoubleClickSlop = 6f;
        private readonly RawImage _image;
        private readonly RectTransform _content;
        private readonly Canvas _canvas;
        private readonly Action<Texture> _textureChanged;
        private int _textureWidth;
        private int _textureHeight;
        private Rect _uv = new Rect(0f, 0f, 1f, 1f);
        private bool _orbiting;
        private bool _lifting;
        private Vector2 _orbitFrom;
        private Vector2 _liftFrom;
        private float _lastClick = float.NegativeInfinity;
        private Vector2 _lastClickAt;
        private bool _detached;

        internal ScreenStageView(PreviewStage stage, bool disposeOnClose, RawImage image, RectTransform content, Canvas canvas)
        {
            Target = stage;
            DisposeOnClose = disposeOnClose;
            _image = image;
            _content = content;
            _canvas = canvas;
            _textureChanged = Bind;
            stage.TextureChanged += _textureChanged;
            _image.uvRect = _uv;
            Bind(stage.Texture);
            _image.gameObject.SetActive(true);
        }

        internal PreviewStage Target { get; }
        internal bool DisposeOnClose { get; }

        internal void SetVisible(bool visible)
        {
            if (!_detached && Target.IsAlive)
            {
                Target.Visible = visible;
            }
        }

        /// <summary>Runs each frame the screen is open; false once the stage has closed itself and the view let go of it.</summary>
        internal bool Tick()
        {
            if (_detached)
            {
                return false;
            }

            if (!Target.IsAlive)
            {
                Detach(false);
                return false;
            }

            var scale = _canvas != null ? _canvas.scaleFactor : 1f;
            var rect = _content.rect;
            var width = Mathf.RoundToInt(rect.width * scale);
            var height = Mathf.RoundToInt(rect.height * scale);
            if (width > 0 && height > 0)
            {
                Target.SetViewportPixels(width, height);
                Fit(rect.width / rect.height);
            }

            Route(height);
            return true;
        }

        internal void Detach(bool dispose)
        {
            if (_detached)
            {
                return;
            }

            _detached = true;
            _orbiting = false;
            _lifting = false;
            Target.TextureChanged -= _textureChanged;
            if (_image != null)
            {
                _image.texture = null;
                _image.gameObject.SetActive(false);
            }

            if (!Target.IsAlive)
            {
                return;
            }

            if (dispose)
            {
                Target.Dispose();
            }
            else
            {
                Target.Visible = false;
            }
        }

        private void Bind(Texture texture)
        {
            if (_detached || _image == null)
            {
                return;
            }

            _image.texture = texture;
            _textureWidth = texture != null ? texture.width : 0;
            _textureHeight = texture != null ? texture.height : 0;
        }

        // Crops rather than stretches while a resized texture is still on its debounce.
        private void Fit(float viewAspect)
        {
            if (_textureWidth <= 0 || _textureHeight <= 0 || !float.IsFinite(viewAspect) || viewAspect <= 0f)
            {
                return;
            }

            var textureAspect = (float)_textureWidth / _textureHeight;
            var uv = textureAspect > viewAspect
                ? new Rect((1f - viewAspect / textureAspect) * 0.5f, 0f, viewAspect / textureAspect, 1f)
                : new Rect(0f, (1f - textureAspect / viewAspect) * 0.5f, 1f, textureAspect / viewAspect);
            if (uv.x != _uv.x || uv.y != _uv.y || uv.width != _uv.width || uv.height != _uv.height)
            {
                _uv = uv;
                _image.uvRect = uv;
            }
        }

        private void Route(int heightPixels)
        {
            Vector2 pointer = Input.mousePosition;
            var over = RectTransformUtility.RectangleContainsScreenPoint(_content, pointer, null);
            if (over && Input.GetMouseButtonDown(0))
            {
                var now = Time.unscaledTime;
                if (now - _lastClick <= DoubleClickSeconds && (pointer - _lastClickAt).sqrMagnitude <= DoubleClickSlop * DoubleClickSlop)
                {
                    _lastClick = float.NegativeInfinity;
                    _orbiting = false;
                    Target.ResetView();
                }
                else
                {
                    _lastClick = now;
                    _lastClickAt = pointer;
                    _orbiting = true;
                    _orbitFrom = pointer;
                }
            }

            // A drag that started over the stage keeps going outside it until the button is released.
            if (_orbiting)
            {
                if (!Input.GetMouseButton(0))
                {
                    _orbiting = false;
                }
                else
                {
                    var delta = pointer - _orbitFrom;
                    _orbitFrom = pointer;
                    if (delta.sqrMagnitude > 0f)
                    {
                        Target.Orbit(delta.x * YawPerPixel, -delta.y * PitchPerPixel);
                    }
                }
            }

            if (over && Input.GetMouseButtonDown(2))
            {
                _lifting = true;
                _liftFrom = pointer;
            }

            if (_lifting)
            {
                if (!Input.GetMouseButton(2))
                {
                    _lifting = false;
                }
                else
                {
                    var rise = pointer.y - _liftFrom.y;
                    _liftFrom = pointer;
                    if (rise != 0f && heightPixels > 0)
                    {
                        Target.LiftPivot(-rise / heightPixels * LiftPerHeight);
                    }
                }
            }

            if (over)
            {
                var notches = MenuScroll.ReadWheel();
                if (notches != 0f)
                {
                    Target.Zoom(notches);
                }
            }
        }
    }
}
