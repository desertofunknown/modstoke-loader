// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using RedLoader.Unity.IL2CPP.UnityEngine;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace Modstoke.Loader
{
    internal sealed class CoroutineHost : IGlobalBehaviourHost
    {
        private static readonly CoroutineHost Host = new();
        private bool _registered;
        private bool _creating;

        private CoroutineHost() { }

        internal static void Install()
        {
            GlobalBehaviour.SetHost(Host);
        }

        public void Initialize()
        {
            global::Modstoke.Loader.Coroutines.RequireOwnerThread();
            if (GlobalBehaviour.Instance != null)
            {
                global::Modstoke.Loader.Coroutines.InitializeOwnerThread();
                return;
            }
            Create();
        }

        public void Create()
        {
            global::Modstoke.Loader.Coroutines.RequireOwnerThread();
            // OnDestroy can call Create while the old instance still compares alive.
            if (_creating)
                return;
            if (!_registered)
            {
                if (!ClassInjector.IsTypeRegisteredInIl2Cpp(typeof(GlobalBehaviour)))
                    ClassInjector.RegisterTypeInIl2Cpp<GlobalBehaviour>();
                _registered = true;
            }

            _creating = true;
            GameObject gameObject = null;
            try
            {
                gameObject = new GameObject();
                UnityObject.DontDestroyOnLoad(gameObject);
                gameObject.hideFlags = HideFlags.DontSave;
                var behaviour = gameObject.AddComponent(Il2CppType.Of<GlobalBehaviour>()).TryCast<GlobalBehaviour>()
                    ?? throw new InvalidOperationException("Unable to create the coroutine host component.");
                Maintain(behaviour);
                global::Modstoke.Loader.Coroutines.InitializeOwnerThread();
                GlobalBehaviour.Instance = behaviour;
            }
            catch (Exception error)
            {
                try
                {
                    if (gameObject != null)
                        UnityObject.Destroy(gameObject);
                }
                catch (Exception cleanupError)
                {
                    throw new AggregateException("Coroutine host creation and cleanup failed.", error, cleanupError);
                }
                throw;
            }
            finally { _creating = false; }
        }

        public void Maintain(GlobalBehaviour behaviour)
        {
            global::Modstoke.Loader.Coroutines.RequireOwnerThread();
            if (behaviour == null)
                return;
            // A component and its GameObject share one Transform.
            behaviour.transform.SetAsLastSibling();
        }
    }
}
