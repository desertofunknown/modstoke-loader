using Sons.Gui;
using Sons.Gui.Input;
using SUI;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SonsSdk;

public class SonsUiTools
{
    internal static List<CustomUiElement> CustomLinkElements = new();

    /// <summary>
    /// Registers a custom ui to the sons link ui manager. Should be called in the OnSdkInitialized callback.
    /// </summary>
    /// <param name="container"></param>
    /// <param name="id"></param>
    /// <param name="transformType">The transform mode used by the link UI element.</param>
    /// <param name="maxPooled">The maximum number of UI instances the link UI manager may pool.</param>
    public static void RegisterCustomLinkUi(SContainerOptions container,
        string id,
        UiElement.UiElementTransformType transformType = UiElement.UiElementTransformType.Auto,
        int maxPooled = 1)
    {
        Register(new()
        {
            Id = id,
            Container = container,
            IdHash = UiElement.GetHash(id),
            TransformType = transformType,
            MaxPooled = maxPooled
        });
    }
    
    /// <summary>
    /// Registers a custom 2d ui to the sons link ui manager. Should be called in the OnSdkInitialized callback.
    /// </summary>
    /// <param name="container"></param>
    /// <param name="id"></param>
    /// <param name="transformType">The transform mode used by the link UI element.</param>
    public static void RegisterCustom2DUi(SContainerOptions container,
        string id,
        UiElement.UiElementTransformType transformType = UiElement.UiElementTransformType.Auto)
    {
        Register(new()
        {
            Id = id,
            Container = container,
            IdHash = UiElement.GetHash(id),
            TransformType = transformType,
            MaxPooled = 1,
            Is2d = true
        });
    }

    private static readonly Lazy<Type> Owner = new(() => System.Reflection.Assembly.Load("Modstoke.Loader")
        .GetType("Modstoke.Loader.LinkUi", true));
    private static readonly Lazy<Action<CustomUiElement>> RegisterOwned = new(() => Bind<Action<CustomUiElement>>("Register"));
    private static readonly Lazy<Action<GameObject, string, float, bool>> AddOwned = new(() =>
        Bind<Action<GameObject, string, float, bool>>("AddLinkUi"));
    private static readonly Lazy<Func<Transform, string, float, bool, Transform>> CreateOwned = new(() =>
        Bind<Func<Transform, string, float, bool, Transform>>("CreateLinkUi"));
    private static readonly Lazy<Action> InitializeOwned = new(() => Bind<Action>("Initialize"));
    private static readonly Lazy<Action> BeforeLoadOwned = new(() => Bind<Action>("BeforeLoadSave"));

    internal static void Register(CustomUiElement element) => RegisterOwned.Value(element);

    public static void AddLinkUi(GameObject gameObject, string id, float distance = 15f, bool refresh = false) =>
        AddOwned.Value(gameObject, id, distance, refresh);

    public static Transform CreateLinkUi(Transform parent, string id, float distance = 15f, bool refresh = false) =>
        CreateOwned.Value(parent, id, distance, refresh);

    internal static void Init() => InitializeOwned.Value();

    internal static void BeforeLoadSave() => BeforeLoadOwned.Value();

    private static T Bind<T>(string name) where T : Delegate
    {
        var type = Owner.Value;
        var method = type.GetMethod(name, System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
            ?? throw new MissingMethodException(type.FullName, name);
        return (T)method.CreateDelegate(typeof(T));
    }

    public class CustomUiElement
    {
        public string Id;
        public SContainerOptions Container;
        public int IdHash;
        public UiElement.UiElementTransformType TransformType;
        public int MaxPooled;
        public bool Is2d;
    }
}
