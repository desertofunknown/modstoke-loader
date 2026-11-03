using System.Reflection;
using RedLoader;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SonsSdk.Private;

internal interface ISdkOverlayDriver
{
    GameObject CreateBlackScreen();
    void ToggleOverlay(bool enable);
    void SetProgressbarOffset(float y);
    void ToggleBackground(bool enable);
    void SetProgress(RectTransform foreground, float progress);
}

internal class GlobalOverlays
{
    private static readonly Lazy<ISdkOverlayDriver> FallbackDriver = new(() =>
    {
        var type = Assembly.Load("Modstoke.Loader")
            .GetType("Modstoke.Loader.SdkOverlay", true);
        var method = type.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            ?? throw new MissingMethodException(type.FullName, "Create");
        return ((Func<ISdkOverlayDriver>)method.CreateDelegate(typeof(Func<ISdkOverlayDriver>)))();
    });
    internal static ISdkOverlayDriver Fallback => FallbackDriver.Value;

    public static GameObject OverlayContainer;
    public static Image BlackScreenImage;
    public static TextMeshProUGUI OverlayText;
    public static ProgressBarContainer ProgressBar;
    
    public static GameObject CreateBlackScreen()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
            return driver.Overlay.CreateBlackScreen();

        return Fallback.CreateBlackScreen();
    }

    public static void ToggleOverlay(bool enable)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Overlay.ToggleOverlay(enable);
            return;
        }

        Fallback.ToggleOverlay(enable);
    }

    public static void SetProgressbarOffset(float y)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Overlay.SetProgressbarOffset(y);
            return;
        }

        Fallback.SetProgressbarOffset(y);
    }

    public static void ToggleBackground(bool enable)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Overlay.ToggleBackground(enable);
            return;
        }

        Fallback.ToggleBackground(enable);
    }

    internal class ProgressBarContainer
    {
        public RectTransform ProgressbarBackground;
        public Image ProgressbarBackgroundImage;
        public RectTransform ProgressbarForeground;
        public Image ProgressbarForegroundImage;

        public float CurrentProgress { get; private set; }

        public void SetProgress(float progress)
        {
            var driver = SdkEvents.BeginLifecycleSetup();
            if (driver != null)
            {
                driver.Overlay.SetProgress(ProgressbarForeground, progress);
                return;
            }

            Fallback.SetProgress(ProgressbarForeground, progress);
        }

        public void AddProgress(float progress)
        {
            CurrentProgress = Mathf.Clamp01(CurrentProgress + progress);
            SetProgress(CurrentProgress);
        }
    }
}
