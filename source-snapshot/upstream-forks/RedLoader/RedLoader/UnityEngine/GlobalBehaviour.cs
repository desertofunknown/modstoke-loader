using System;
using System.Diagnostics;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using RedLoader;
using RedLoader.Scripting;
using RedLoader.Support;
using UnityEngine;
using Color = System.Drawing.Color;

namespace RedLoader.Unity.IL2CPP.UnityEngine;

internal interface IGlobalBehaviourHost
{
    void Initialize();
    void Create();
    void Maintain(GlobalBehaviour behaviour);
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
    
    private bool isQuitting;
    
    public static void Init()
    {
        hostStarted = true;
        if (host != null)
        {
            host.Initialize();
            return;
        }

        ClassInjector.RegisterTypeInIl2Cpp<GlobalBehaviour>();
        SetAsLastSiblingDelegateField = Il2CppInterop.Runtime.IL2CPP.ResolveICall<SetAsLastSiblingDelegate>("UnityEngine.Transform::SetAsLastSibling");
        
        if (Instance != null)
            return;

        Create();
    }

    private static void Create()
    {
        hostStarted = true;
        if (host != null)
        {
            host.Create();
            return;
        }

        var go = new GameObject();
        DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.DontSave;
        Instance = go.AddComponent(Il2CppType.Of<GlobalBehaviour>()).TryCast<GlobalBehaviour>();
        Instance.SiblingFix();
    }

    private void SiblingFix()
    {
        hostStarted = true;
        if (host != null)
        {
            host.Maintain(this);
            return;
        }

        SetAsLastSiblingDelegateField(Il2CppInterop.Runtime.IL2CPP.Il2CppObjectBaseToPtrNotNull(gameObject.transform));
        SetAsLastSiblingDelegateField(Il2CppInterop.Runtime.IL2CPP.Il2CppObjectBaseToPtrNotNull(transform));
    }
    
    internal void Destroy()
        {
            Destroy(gameObject);
        }

        void Start()
        {
            if ((Instance!= null) && (Instance!= this))
                return;

            SiblingFix();
            if (!HasExternalLifecycle)
                GlobalEvents.OnApplicationLateStart.Invoke();
        }

        void Awake()
        {
            if ((Instance!= null) && (Instance!= this))
                return;

            // foreach (var queuedCoroutine in SupportModule_To.QueuedCoroutines)
            //     StartCoroutine(new Il2CppSystem.Collections.IEnumerator(new MonoEnumeratorWrapper(queuedCoroutine).Pointer));
            // SupportModule_To.QueuedCoroutines.Clear();
        }

        void Update()
        {
            if ((Instance!= null) && (Instance!= this))
                return;

            isQuitting = false;
            SiblingFix();

            if (!HasExternalLifecycle)
            {
                SceneHandler.OnUpdate();
                GlobalEvents.OnUpdate.Invoke();
            }
            
            if(CorePreferences.EnableScriptLoader.Value)
                RedScriptManager.Update();
        }

        void OnDestroy()
        {
            if ((Instance!= null) && (Instance!= this))
                return;

            if (!isQuitting)
            {
                Create();
                return;
            }

            OnApplicationDefiniteQuit();
        }

        void OnApplicationQuit()
        {
            if ((Instance!= null) && (Instance!= this))
                return;

            isQuitting = true;
            if (!HasExternalLifecycle)
                GlobalEvents.OnApplicationQuit.Invoke();
        }

        void OnApplicationDefiniteQuit()
        {
            if (HasExternalLifecycle)
                return;

            ConfigSystem.Save();
            GlobalEvents.OnApplicationDefiniteQuit.Invoke();
            
            System.Threading.Thread.Sleep(200);
            Process.GetCurrentProcess().Kill();
        }

        void FixedUpdate()
        {
            if (HasExternalLifecycle)
                return;

            if ((Instance!= null) && (Instance!= this))
                return;

            GlobalEvents.OnFixedUpdate.Invoke();
        }

        void LateUpdate()
        {
            if (HasExternalLifecycle)
                return;

            if ((Instance!= null) && (Instance!= this))
                return;

            GlobalEvents.OnLateUpdate.Invoke();
        }

        void OnGUI()
        {
            if (HasExternalLifecycle)
                return;

            if ((Instance!= null) && (Instance!= this))
                return;

            GlobalEvents.OnGUI.Invoke();
        }
        
        private delegate bool SetAsLastSiblingDelegate(IntPtr transformptr);
        private static SetAsLastSiblingDelegate SetAsLastSiblingDelegateField;
}
