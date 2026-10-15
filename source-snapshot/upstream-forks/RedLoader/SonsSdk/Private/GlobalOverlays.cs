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
    public static GameObject OverlayContainer;
    public static Image BlackScreenImage;
    public static TextMeshProUGUI OverlayText;
    public static ProgressBarContainer ProgressBar;
    
    public static GameObject CreateBlackScreen()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
            return driver.Overlay.CreateBlackScreen();

        var createdObjects = new List<GameObject>(5);
        try
        {
            var go = new GameObject("BlackScreenCanvas");
            createdObjects.Add(go);
            go.DontDestroyOnLoad().HideAndDontSave();
            go.layer = 5;

            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 999;
            var blackScreen = CreateChild("BlackScreen", canvas.transform, createdObjects);
            var rect = blackScreen.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            var textGo = blackScreen.Instantiate();
            createdObjects.Add(textGo);
            textGo.SetParent(blackScreen.transform);
            var blackScreenImage = blackScreen.AddComponent<Image>();
            blackScreenImage.color = Color.black;
            var overlayText = textGo.AddComponent<TextMeshProUGUI>();
            overlayText.fontSize = 30;
            overlayText.color = System.Drawing.Color.PaleVioletRed.ToUnityColor();
            overlayText.alignment = TextAlignmentOptions.Center;
            var progressBar = CreateProgressbar(go.GetComponent<RectTransform>(), createdObjects);

            // Publish only complete overlays; failed attempts keep the previous references.
            BlackScreenImage = blackScreenImage;
            OverlayText = overlayText;
            ProgressBar = progressBar;
            OverlayContainer = go;
            return go;
        }
        catch
        {
            DiscardCreatedObjects(createdObjects);
            throw;
        }
    }

    private static ProgressBarContainer CreateProgressbar(RectTransform container, List<GameObject> createdObjects)
    {
        var bar = CreateChild("ProgressBarBackground", container, createdObjects);
        var barRect = bar.AddComponent<RectTransform>();
        barRect.anchorMin = new(0.1f, 0f);
        barRect.anchorMax = new(0.9f, 0f);
        barRect.sizeDelta = new(0, 30);
        barRect.pivot = new(0.5f, 0);
        barRect.anchoredPosition = new(0, 200);
        var barImage = bar.AddComponent<Image>();
        barImage.color = Color.grey;

        var fore = CreateChild("ProgressBarForeground", bar.transform, createdObjects);
        var foreRect = fore.AddComponent<RectTransform>();
        foreRect.anchorMin = Vector2.zero;
        foreRect.anchorMax = new(0, 1);
        foreRect.sizeDelta = Vector2.zero;
        foreRect.pivot = new(0, 0.5f);
        var foreImage = fore.AddComponent<Image>();
        foreImage.color = new(0.735849f, 0.1087575f, 0.3132702f);

        return new ProgressBarContainer
        {
            ProgressbarBackground = barRect,
            ProgressbarBackgroundImage = barImage,
            ProgressbarForeground = foreRect,
            ProgressbarForegroundImage = foreImage
        };
    }

    private static GameObject CreateChild(string name, Transform parent, List<GameObject> createdObjects)
    {
        var child = new GameObject(name);
        createdObjects.Add(child);
        child.transform.SetParent(parent, false);
        return child;
    }

    private static void DiscardCreatedObjects(List<GameObject> createdObjects)
    {
        for (var i = createdObjects.Count - 1; i >= 0; i--)
        {
            var gameObject = createdObjects[i];
            try { gameObject.SetActive(false); }
            catch (Exception exception) { ReportCleanupFailure(exception); }

            try { UnityEngine.Object.Destroy(gameObject); }
            catch (Exception exception) { ReportCleanupFailure(exception); }
        }
    }

    private static void ReportCleanupFailure(Exception exception)
    {
        try { RLog.Warning("Failed to clean up an incomplete startup overlay: " + exception); }
        catch { /* Keep the original construction failure. */ }
    }

    public static void ToggleOverlay(bool enable)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Overlay.ToggleOverlay(enable);
            return;
        }

        if (!OverlayContainer)
        {
            return;
        }
        
        OverlayContainer.SetActive(enable);
    }

    public static void SetProgressbarOffset(float y)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Overlay.SetProgressbarOffset(y);
            return;
        }

        if (ProgressBar == null)
        {
            return;
        }

        ProgressBar.ProgressbarBackground.anchoredPosition = new(0, y);
    }

    public static void ToggleBackground(bool enable)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Overlay.ToggleBackground(enable);
            return;
        }

        if (!BlackScreenImage)
        {
            return;
        }

        BlackScreenImage.enabled = enable;
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

            ProgressbarForeground.anchorMax = new(progress, 1);
        }

        public void AddProgress(float progress)
        {
            CurrentProgress = Mathf.Clamp01(CurrentProgress + progress);
            SetProgress(CurrentProgress);
        }
    }
}
