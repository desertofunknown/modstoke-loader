// SPDX-License-Identifier: GPL-3.0-or-later
// Compatibility lifecycle dispatch adapted from RedLoader.
// Copyright (c) RedLoader and BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System.Diagnostics;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using RedLoader;
using RedLoader.Scripting;
using RedLoader.Support;
using RedLoader.Unity.IL2CPP.UnityEngine;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace Modstoke.Loader;

internal sealed class RedGlobalBehaviourFallback : IGlobalBehaviourFallbackDriver
{
    private static readonly RedGlobalBehaviourFallback Driver = new();

    internal static IGlobalBehaviourFallbackDriver Create() => Driver;

    public void Initialize()
    {
        ClassInjector.RegisterTypeInIl2Cpp<GlobalBehaviour>();
        GlobalBehaviour.SetAsLastSiblingDelegateField = IL2CPP.ResolveICall<GlobalBehaviour.SetAsLastSiblingDelegate>("UnityEngine.Transform::SetAsLastSibling");
        if (GlobalBehaviour.Instance != null)
            return;

        GlobalBehaviour.Create();
    }

    void IGlobalBehaviourHost.Create()
    {
        var go = new GameObject();
        UnityObject.DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.DontSave;
        GlobalBehaviour.Instance = go.AddComponent(Il2CppType.Of<GlobalBehaviour>()).TryCast<GlobalBehaviour>();
        GlobalBehaviour.Instance.SiblingFix();
    }

    public void Maintain(GlobalBehaviour behaviour)
    {
        GlobalBehaviour.SetAsLastSiblingDelegateField(IL2CPP.Il2CppObjectBaseToPtrNotNull(behaviour.gameObject.transform));
        GlobalBehaviour.SetAsLastSiblingDelegateField(IL2CPP.Il2CppObjectBaseToPtrNotNull(behaviour.transform));
    }

    public void OnStart(GlobalBehaviour behaviour)
    {
        if (GlobalBehaviour.Instance != null && GlobalBehaviour.Instance != behaviour)
            return;

        behaviour.SiblingFix();
        if (!GlobalBehaviour.HasExternalLifecycle)
            GlobalEvents.OnApplicationLateStart.Invoke();
    }

    public void OnUpdate(GlobalBehaviour behaviour)
    {
        if (GlobalBehaviour.Instance != null && GlobalBehaviour.Instance != behaviour)
            return;

        behaviour.isQuitting = false;
        behaviour.SiblingFix();
        if (!GlobalBehaviour.HasExternalLifecycle)
        {
            SceneHandler.OnUpdate();
            GlobalEvents.OnUpdate.Invoke();
        }

        if (CorePreferences.EnableScriptLoader.Value)
            RedScriptManager.Update();
    }

    public void OnDestroy(GlobalBehaviour behaviour)
    {
        if (GlobalBehaviour.Instance != null && GlobalBehaviour.Instance != behaviour)
            return;

        if (!behaviour.isQuitting)
        {
            GlobalBehaviour.Create();
            return;
        }

        behaviour.OnApplicationDefiniteQuit();
    }

    public void OnApplicationQuit(GlobalBehaviour behaviour)
    {
        if (GlobalBehaviour.Instance != null && GlobalBehaviour.Instance != behaviour)
            return;

        behaviour.isQuitting = true;
        if (!GlobalBehaviour.HasExternalLifecycle)
            GlobalEvents.OnApplicationQuit.Invoke();
    }

    public void OnApplicationDefiniteQuit(GlobalBehaviour behaviour)
    {
        if (GlobalBehaviour.HasExternalLifecycle)
            return;

        ConfigSystem.Save();
        GlobalEvents.OnApplicationDefiniteQuit.Invoke();
        System.Threading.Thread.Sleep(200);
        Process.GetCurrentProcess().Kill();
    }

    public void OnFixedUpdate(GlobalBehaviour behaviour)
    {
        if (GlobalBehaviour.HasExternalLifecycle)
            return;
        if (GlobalBehaviour.Instance != null && GlobalBehaviour.Instance != behaviour)
            return;

        GlobalEvents.OnFixedUpdate.Invoke();
    }

    public void OnLateUpdate(GlobalBehaviour behaviour)
    {
        if (GlobalBehaviour.HasExternalLifecycle)
            return;
        if (GlobalBehaviour.Instance != null && GlobalBehaviour.Instance != behaviour)
            return;

        GlobalEvents.OnLateUpdate.Invoke();
    }

    public void OnGui(GlobalBehaviour behaviour)
    {
        if (GlobalBehaviour.HasExternalLifecycle)
            return;
        if (GlobalBehaviour.Instance != null && GlobalBehaviour.Instance != behaviour)
            return;

        GlobalEvents.OnGUI.Invoke();
    }
}
