using Il2CppInterop.Runtime.Injection;
using RedLoader;
using System;
using System.Reflection;
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
    private static readonly System.Lazy<ISdkTooltipDriver> FallbackDriver = new(() =>
    {
        var type = Assembly.Load("Modstoke.Loader")
            .GetType("Modstoke.Loader.TooltipFallback", true);
        var method = type.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            ?? throw new System.MissingMethodException(type.FullName, "Create");
        return ((System.Func<ISdkTooltipDriver>)method.CreateDelegate(typeof(System.Func<ISdkTooltipDriver>)))();
    });
    private static ISdkTooltipDriver Fallback => FallbackDriver.Value;

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

        Fallback.Setup();
    }

    public static void ShowTooltip(string text)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Tooltips.Show(text);
            return;
        }

        Fallback.Show(text);
    }
    
    public static void HideTooltip()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Tooltips.Hide();
            return;
        }

        Fallback.Hide();
    }

    public static void RaycastFor(GraphicRaycaster raycaster)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Tooltips.RaycastFor(raycaster);
            return;
        }

        Fallback.RaycastFor(raycaster);
    }

    public static void StopRaycasting()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Tooltips.StopRaycasting();
            return;
        }

        Fallback.StopRaycasting();
    }

    internal static void OnUpdate()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Tooltips.Update();
            return;
        }

        Fallback.Update();
    }
}
