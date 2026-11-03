using System;
using System.Reflection;
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using RedLoader.Unity.IL2CPP.UnityEngine;
using UnityEngine.Events;
#if SM_Il2Cpp
using UnityEngine.Events;
#endif

namespace RedLoader.Support
{
    internal interface ISceneHandlerFallbackDriver
    {
        void Initialize();
        void OnSceneLoad(Scene scene, LoadSceneMode mode);
        void OnSceneUnload(Scene scene);
        void OnUpdate();
    }

    internal static class SceneHandler
    {
        internal class SceneInitEvent
        {
            internal int buildIndex;
            internal string name;
            internal bool wasLoadedThisTick;
        }

        internal static Queue<SceneInitEvent> scenesLoaded = new Queue<SceneInitEvent>();

        private static readonly Lazy<ISceneHandlerFallbackDriver> FallbackDriver = new(() =>
        {
            var type = Assembly.Load("Modstoke.Loader")
                .GetType("Modstoke.Loader.RedSceneFallback", true);
            var method = type.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
                ?? throw new MissingMethodException(type.FullName, "Create");
            return ((Func<ISceneHandlerFallbackDriver>)method.CreateDelegate(typeof(Func<ISceneHandlerFallbackDriver>)))();
        });
        private static ISceneHandlerFallbackDriver Fallback => FallbackDriver.Value;

        internal static void Init()
        {
            if (GlobalBehaviour.HasExternalLifecycle)
                return;

            Fallback.Initialize();
        }

        internal static void OnSceneLoad(Scene scene, LoadSceneMode mode)
        {
            if (GlobalBehaviour.HasExternalLifecycle)
                return;

            Fallback.OnSceneLoad(scene, mode);
        }

        internal static void OnSceneUnload(Scene scene)
        {
            if (GlobalBehaviour.HasExternalLifecycle)
                return;

            Fallback.OnSceneUnload(scene);
        }

        internal static void OnUpdate()
        {
            if (GlobalBehaviour.HasExternalLifecycle)
                return;

            Fallback.OnUpdate();
        }
    }
}
