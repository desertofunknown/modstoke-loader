// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using RedLoader;
using System;
using System.Collections.Generic;
using SonsSdk;
using SUI;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

using static SUI.SUI;

namespace Modstoke.Loader
{
    internal sealed class Tooltips : ISdkTooltipDriver
    {
        internal static readonly Tooltips Instance = new();
        private SLabelOptions _label;
        private EventSystem _eventSystem;
        private PointerEventData _pointerData;
        private Il2CppSystem.Collections.Generic.List<RaycastResult> _raycastResults;

        private Tooltips() { }

        public void Setup()
        {
            if (HasLiveUi)
                return;

            ResetSetup();
            var createdObjects = new List<GameObject>(3);
            SLabelOptions label = null;
            try
            {
                var root = new GameObject("TooltipCanvas");
                createdObjects.Add(root);
                Object.DontDestroyOnLoad(root);
                root.hideFlags |= HideFlags.HideAndDontSave;
                root.layer = 5;

                var canvas = root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 999;

                var scaler = root.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                scaler.matchWidthOrHeight = 0;

                var tooltip = SContainer;
                createdObjects.Add(tooltip.Root);
                tooltip.Pivot(0, 0).Anchor(AnchorType.BottomLeft).Position(0, 0)
                    .Size(400, 100).Background(TooltipProvider.CardButtonBg);

                label = SLabel;
                createdObjects.Add(label.Root);
                label.Bind(TooltipProvider._tooltipText).Dock(EDockType.Fill).FontSize(18)
                    .Font(EFont.RobotoLight).FontColor("#ea2f4e60").Alignment(TextAlignmentOptions.Center);
                tooltip.Add(label);

                var fitter = label.Root.AddComponent<ContentSizeFitter>();
                fitter.verticalFit = fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
                tooltip.Root.AddComponent<FollowMouseHandler>();
                tooltip.SetParent(canvas.transform);
                tooltip.Root.SetActive(false);
                root.SetActive(false);

                // Publish only after every control is ready and hidden.
                TooltipProvider._canvas = canvas;
                TooltipProvider._tooltipObject = tooltip.Root;
                TooltipProvider._tooltipRect = tooltip.RectTransform;
                TooltipProvider._textRect = label.RectTransform;
                _label = label;
                TooltipProvider._initialized = true;
            }
            catch
            {
                DiscardFailedSetup(createdObjects, label);
                throw;
            }
        }

        private bool HasLiveUi => TooltipProvider._initialized && TooltipProvider._canvas
            && TooltipProvider._tooltipObject && TooltipProvider._tooltipRect && TooltipProvider._textRect
            && _label != null && _label.TextObject;

        private void ResetSetup()
        {
            StopRaycasting();
            var objects = new List<GameObject>(3);
            if (TooltipProvider._canvas)
                objects.Add(TooltipProvider._canvas.gameObject);
            if (TooltipProvider._tooltipObject)
                objects.Add(TooltipProvider._tooltipObject);
            if (_label != null && _label.Root)
                objects.Add(_label.Root);

            TooltipProvider._initialized = false;
            TooltipProvider._canvas = null;
            TooltipProvider._tooltipObject = null;
            TooltipProvider._tooltipRect = null;
            TooltipProvider._textRect = null;
            var label = _label;
            _label = null;
            DiscardFailedSetup(objects, label);
        }

        private static void DiscardFailedSetup(List<GameObject> createdObjects, SLabelOptions label)
        {
            try { label?.Unbind(); }
            catch (Exception exception) { ReportCleanupFailure(exception); }

            for (var i = createdObjects.Count - 1; i >= 0; i--)
            {
                var gameObject = createdObjects[i];
                if (!gameObject)
                    continue;
                try { gameObject.SetActive(false); }
                catch (Exception exception) { ReportCleanupFailure(exception); }

                try { Object.Destroy(gameObject); }
                catch (Exception exception) { ReportCleanupFailure(exception); }
            }
        }

        private static void ReportCleanupFailure(Exception exception)
        {
            try { RLog.Warning("Failed to clean up an incomplete tooltip: " + exception); }
            catch { /* Preserve the original setup failure. */ }
        }

        public void Show(string text)
        {
            Setup();
            TooltipProvider._tooltipText.Value = text;

            var mousePos = Input.mousePosition;
            TooltipProvider._tooltipRect.anchoredPosition = new Vector2(mousePos.x * (1920f / Screen.width), mousePos.y * (1080f / Screen.height));
            var rectWidth = TooltipProvider._textRect.rect.width;
            TooltipProvider._tooltipRect.sizeDelta = new Vector2(rectWidth + 100, 100);

            TooltipProvider._canvas.gameObject.SetActive(true);
            TooltipProvider._tooltipObject.SetActive(true);
        }

        public void Hide()
        {
            if (TooltipProvider._tooltipObject)
                TooltipProvider._tooltipObject.SetActive(false);
        }

        public void RaycastFor(GraphicRaycaster raycaster)
        {
            if (!raycaster)
            {
                StopRaycasting();
                return;
            }

            Setup();
            Hide();
            TooltipProvider._currentRaycaster = raycaster;
            TooltipProvider._canvas.gameObject.SetActive(true);

            // Keep the SDK delegate identity and unsubscribe/subscribe ordering.
            GlobalEvents.OnUpdate.Unsubscribe(TooltipProvider.OnUpdate);
            GlobalEvents.OnUpdate.Subscribe(TooltipProvider.OnUpdate);
        }

        public void StopRaycasting()
        {
            GlobalEvents.OnUpdate.Unsubscribe(TooltipProvider.OnUpdate);

            TooltipProvider._currentRaycaster = null;
            _raycastResults?.Clear();
            _raycastResults = null;
            _pointerData = null;
            _eventSystem = null;
            Hide();
            if (TooltipProvider._canvas)
                TooltipProvider._canvas.gameObject.SetActive(false);
        }

        public void Update()
        {
            if (!HasLiveUi)
            {
                ResetSetup();
                return;
            }

            if (!TooltipProvider._currentRaycaster)
            {
                StopRaycasting();
                return;
            }

            if (!TooltipProvider._currentRaycaster.isActiveAndEnabled)
            {
                Hide();
                return;
            }

            var eventSystem = EventSystem.current;
            if (!eventSystem)
            {
                _pointerData = null;
                _eventSystem = null;
                _raycastResults?.Clear();
                Hide();
                return;
            }

            if (_eventSystem != eventSystem || _pointerData == null)
            {
                _pointerData = new PointerEventData(eventSystem);
                _eventSystem = eventSystem;
            }
            _pointerData.Reset();
            _pointerData.position = Input.mousePosition;
            var results = _raycastResults ??= new Il2CppSystem.Collections.Generic.List<RaycastResult>();
            results.Clear();
            try
            {
                TooltipProvider._currentRaycaster.Raycast(_pointerData, results);
                for (var i = 0; i < results.Count; i++)
                {
                    var obj = results[i].gameObject;
                    if (obj && obj.TryGetComponent(out TooltipInfo tooltip))
                    {
                        Show(tooltip.Text);
                        return;
                    }
                }
                Hide();
            }
            finally
            {
                results.Clear();
            }
        }
    }
}
