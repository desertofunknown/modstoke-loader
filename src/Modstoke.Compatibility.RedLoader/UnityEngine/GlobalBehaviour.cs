using System;
using System.Reflection;
using UnityEngine;

namespace RedLoader.Unity.IL2CPP.UnityEngine;

internal interface IGlobalBehaviourHost
{
    void Initialize();
    void Create();
    void Maintain(GlobalBehaviour behaviour);
}

internal interface IGlobalBehaviourFallbackDriver : IGlobalBehaviourHost
{
    void OnStart(GlobalBehaviour behaviour);
    void OnUpdate(GlobalBehaviour behaviour);
    void OnDestroy(GlobalBehaviour behaviour);
    void OnApplicationQuit(GlobalBehaviour behaviour);
    void OnApplicationDefiniteQuit(GlobalBehaviour behaviour);
    void OnFixedUpdate(GlobalBehaviour behaviour);
    void OnLateUpdate(GlobalBehaviour behaviour);
    void OnGui(GlobalBehaviour behaviour);
}

public class GlobalBehaviour : MonoBehaviour
{
    public static GlobalBehaviour Instance;

    private static IGlobalBehaviourHost host;
    private static bool hostStarted;

    internal static void SetHost(IGlobalBehaviourHost value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (ReferenceEquals(host, value))
            return;
        if (host != null || hostStarted)
            throw new InvalidOperationException("Coroutine host ownership must be selected before initialization.");

        host = value;
    }

    internal static bool HasExternalLifecycle { get; private set; }

    internal static void UseExternalLifecycle()
    {
        HasExternalLifecycle = true;
    }
    
    internal bool isQuitting;

    private static readonly Lazy<IGlobalBehaviourFallbackDriver> FallbackDriver = new(() =>
    {
        var type = Assembly.Load("Modstoke.Loader")
            .GetType("Modstoke.Loader.RedGlobalBehaviourFallback", true);
        var method = type.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            ?? throw new MissingMethodException(type.FullName, "Create");
        return ((Func<IGlobalBehaviourFallbackDriver>)method.CreateDelegate(typeof(Func<IGlobalBehaviourFallbackDriver>)))();
    });
    private static IGlobalBehaviourFallbackDriver Fallback => FallbackDriver.Value;
    
    public static void Init()
    {
        hostStarted = true;
        if (host != null)
        {
            host.Initialize();
            return;
        }

        Fallback.Initialize();
    }

    internal static void Create()
    {
        hostStarted = true;
        if (host != null)
        {
            host.Create();
            return;
        }

        Fallback.Create();
    }

    internal void SiblingFix()
    {
        hostStarted = true;
        if (host != null)
        {
            host.Maintain(this);
            return;
        }

        Fallback.Maintain(this);
    }
    
    internal void Destroy()
    {
        Destroy(gameObject);
    }

    void Start()
    {
        Fallback.OnStart(this);
    }

    void Awake()
    {
        if ((Instance != null) && (Instance != this))
            return;
    }

    void Update()
    {
        Fallback.OnUpdate(this);
    }

    void OnDestroy()
    {
        Fallback.OnDestroy(this);
    }

    void OnApplicationQuit()
    {
        Fallback.OnApplicationQuit(this);
    }

    internal void OnApplicationDefiniteQuit()
    {
        Fallback.OnApplicationDefiniteQuit(this);
    }

    void FixedUpdate()
    {
        Fallback.OnFixedUpdate(this);
    }

    void LateUpdate()
    {
        Fallback.OnLateUpdate(this);
    }

    void OnGUI()
    {
        Fallback.OnGui(this);
    }

    internal delegate bool SetAsLastSiblingDelegate(IntPtr transformptr);
    internal static SetAsLastSiblingDelegate SetAsLastSiblingDelegateField;
}
