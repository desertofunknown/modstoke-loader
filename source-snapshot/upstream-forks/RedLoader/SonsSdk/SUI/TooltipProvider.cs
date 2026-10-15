using Il2CppInterop.Runtime.Injection;
using RedLoader;
using System;
using System.Collections.Generic;
using SUI;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using Color = UnityEngine.Color;

using static SUI.SUI;

namespace SonsSdk;

internal interface ISdkTooltipDriver
{
    void Setup();
    void Show(string text);
    void Hide();
    void RaycastFor(GraphicRaycaster raycaster);
    void StopRaycasting();
    void Update();
}

public class TooltipProvider
{
    internal static bool _initialized;
    
    internal static Canvas _canvas;
    internal static GameObject _tooltipObject;
    internal static RectTransform _tooltipRect;
    internal static RectTransform _textRect;
    internal static Observable<string> _tooltipText = new("");
    internal static GraphicRaycaster _currentRaycaster;

    private static readonly BackgroundDefinition TooltipBackground = new("#000", GetBackgroundSprite(EBackground.Round10), Image.Type.Sliced);
    
    internal static readonly BackgroundDefinition CardButtonBg = new(
        "#222", 
        GetBackgroundSprite(EBackground.ShadowPanel),
        Image.Type.Sliced);
    
    public static void Setup()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Tooltips.Setup();
            return;
        }

        if (_initialized)
            return;

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
                .Size(400, 100).Background(CardButtonBg);

            label = SLabel;
            createdObjects.Add(label.Root);
            label.Bind(_tooltipText).Dock(EDockType.Fill).FontSize(18)
                .Font(EFont.RobotoLight).FontColor("#ea2f4e60").Alignment(TextAlignmentOptions.Center);
            tooltip.Add(label);

            var fitter = label.Root.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            tooltip.Root.AddComponent<FollowMouseHandler>();
            tooltip.SetParent(canvas.transform);
            tooltip.Root.SetActive(false);
            root.SetActive(false);

            // Publish only after every control is ready and hidden.
            _canvas = canvas;
            _tooltipObject = tooltip.Root;
            _tooltipRect = tooltip.RectTransform;
            _textRect = label.RectTransform;
            _initialized = true;
        }
        catch
        {
            DiscardFailedSetup(createdObjects, label);
            throw;
        }
    }

    private static void DiscardFailedSetup(List<GameObject> createdObjects, SLabelOptions label)
    {
        try { label?.Unbind(); }
        catch (Exception exception) { ReportCleanupFailure(exception); }

        for (var i = createdObjects.Count - 1; i >= 0; i--)
        {
            var gameObject = createdObjects[i];
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
    
    public static void ShowTooltip(string text)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Tooltips.Show(text);
            return;
        }

        _tooltipText.Value = text;
        
        var mousePos = Input.mousePosition;
        _tooltipRect.anchoredPosition = new Vector2(mousePos.x * (1920f / Screen.width), mousePos.y * (1080f / Screen.height));
        var rectWidth = _textRect.rect.width;
        _tooltipRect.sizeDelta = new Vector2(rectWidth + 100, 100);
        
        _tooltipObject.SetActive(true);
    }
    
    public static void HideTooltip()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Tooltips.Hide();
            return;
        }

        if (_tooltipObject)
            _tooltipObject.SetActive(false);
    }

    public static void RaycastFor(GraphicRaycaster raycaster)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Tooltips.RaycastFor(raycaster);
            return;
        }

        if (!raycaster)
        {
            StopRaycasting();
            return;
        }

        HideTooltip();
        _currentRaycaster = raycaster;
        _canvas.gameObject.SetActive(true);
        
        GlobalEvents.OnUpdate.Unsubscribe(OnUpdate);
        GlobalEvents.OnUpdate.Subscribe(OnUpdate);
    }

    public static void StopRaycasting()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Tooltips.StopRaycasting();
            return;
        }

        GlobalEvents.OnUpdate.Unsubscribe(OnUpdate);
        
        _currentRaycaster = null;
        HideTooltip();
        if (_canvas)
            _canvas.gameObject.SetActive(false);
    }

    internal static void OnUpdate()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Tooltips.Update();
            return;
        }

        if (!_currentRaycaster)
        {
            StopRaycasting();
            return;
        }

        foreach (var obj in GetHoveredUiObjects(_currentRaycaster))
        {
            if(obj.TryGetComponent(out TooltipInfo tooltip))
            {
                ShowTooltip(tooltip.Text);
                return;
            }
        }
        
        HideTooltip();
    }
}
