using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppInterop.Runtime.Runtime;
using Il2CppSystem;
using RedLoader;
using RedLoader.Utils;
using Sons.Gui;
using Sons.Input;
using SUI;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Action = System.Action;
using Object = UnityEngine.Object;

namespace SonsSdk;

internal interface ISdkModalDialogDriver
{
    void Setup();
    GenericModalDialog Show(string title, string message);
    void Close();
    void Open(GenericModalDialog dialog, string title, string message);
    void Close(GenericModalDialog dialog);
    void Complete(GenericModalDialog dialog, Action callback);
    void SetOption1(GenericModalDialog dialog, string text, Action action);
    void SetOption2(GenericModalDialog dialog, string text, Action action);
    void SetOption3(GenericModalDialog dialog, string text, Action action);
    void SetCancel(GenericModalDialog dialog, Action action);
}

public class GenericModalDialog : MonoBehaviour
{
    static GenericModalDialog()
    {
        ClassInjector.RegisterTypeInIl2Cpp<GenericModalDialog>();
    }

    private static readonly System.Lazy<ISdkModalDialogDriver> FallbackDriver = new(() =>
    {
        var type = Assembly.Load("Modstoke.Loader")
            .GetType("Modstoke.Loader.ModalFallback", true);
        var method = type.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            ?? throw new System.MissingMethodException(type.FullName, "Create");
        return ((System.Func<ISdkModalDialogDriver>)method.CreateDelegate(typeof(System.Func<ISdkModalDialogDriver>)))();
    });
    private static ISdkModalDialogDriver Fallback => FallbackDriver.Value;

    internal static GenericModalDialog Instance;
    
    internal LinkTextGui _titleTextObject;
    internal TextMeshProUGUI _contentTextObject;
    
    internal LinkTextGui _cancelTextObject;
    internal LinkTextGui _option1TextObject;
    internal LinkTextGui _option2TextObject;
    internal LinkTextGui _option3TextObject;

    internal Button _option1Button;
    internal Button _option2Button;
    internal Button _option3Button;
    internal Button _cancelButton;

    internal Image _background;

    internal TMP_InputField _inputField;
    
    internal InputGamepadMenuHelper _gamepadMenuHelper;
    internal Canvas _canvas;

    [HideFromIl2Cpp]
    internal event Action OnCancel;
    [HideFromIl2Cpp]
    internal event Action OnOption1;
    [HideFromIl2Cpp]
    internal event Action OnOption2;
    [HideFromIl2Cpp]
    internal event Action OnOption3;
    
    internal void Option1Clicked()
    {
        Complete(OnOption1);
    }
    
    internal void Option2Clicked()
    {
        Complete(OnOption2);
    }
    
    internal void Option3Clicked()
    {
        Complete(OnOption3);
    }
    
    internal void CancelClicked()
    {
        Complete(OnCancel);
    }

    [HideFromIl2Cpp]
    private void Complete(Action callback)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Dialogs.Complete(this, callback);
            return;
        }

        Fallback.Complete(this, callback);
    }

    internal void SetData(string title, string message)
    {
        _titleTextObject.SetText(title);
        _contentTextObject.text = message;
    }
    
    [HideFromIl2Cpp]
    public void SetOption1(string text, Action action)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Dialogs.SetOption1(this, text, action);
            return;
        }

        Fallback.SetOption1(this, text, action);
    }
    
    [HideFromIl2Cpp]
    public void SetOption2(string text, Action action)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Dialogs.SetOption2(this, text, action);
            return;
        }

        Fallback.SetOption2(this, text, action);
    }
    
    [HideFromIl2Cpp]
    public void SetOption3(string text, Action action)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Dialogs.SetOption3(this, text, action);
            return;
        }

        Fallback.SetOption3(this, text, action);
    }
    
    [HideFromIl2Cpp]
    public void SetCancel(Action action)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Dialogs.SetCancel(this, action);
            return;
        }

        Fallback.SetCancel(this, action);
    }

    internal void CloseInternal()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Dialogs.Close(this);
            return;
        }

        Fallback.Close(this);
    }

    [HideFromIl2Cpp]
    internal void ClearCallbacks()
    {
        OnCancel = null;
        OnOption1 = null;
        OnOption2 = null;
        OnOption3 = null;
    }
    
    internal void OpenInternal(string title, string message)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Dialogs.Open(this, title, message);
            return;
        }

        Fallback.Open(this, title, message);
    }

    internal void ResetGui()
    {
        _option1Button.gameObject.SetActive(false);
        _option2Button.gameObject.SetActive(false);
        _option3Button.gameObject.SetActive(false);
        _inputField.gameObject.SetActive(false);
        
        _cancelTextObject.SetText("Cancel");
    }

    internal static void LogShown()
    {
        RLog.Debug("Shown dialog");
    }

    public static GenericModalDialog ShowDialog(string title, string message)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
            return driver.Dialogs.Show(title, message);

        return Fallback.Show(title, message);
    }

    public static void Close()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Dialogs.Close();
            return;
        }

        Fallback.Close();
    }

    internal static void Setup()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Dialogs.Setup();
            return;
        }

        Fallback.Setup();
    }
}
