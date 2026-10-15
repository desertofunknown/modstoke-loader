// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using RedLoader.Unity.IL2CPP.UnityEngine;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace OpenACAI.Endnight.Loader
{
    internal sealed class OpenACAICoroutineHost : IGlobalBehaviourHost
    {
        private static readonly OpenACAICoroutineHost Host = new();
        private SetAsLastSiblingDelegate _setAsLastSibling;

        private OpenACAICoroutineHost() { }

        internal static void Install()
        {
            GlobalBehaviour.SetHost(Host);
        }

        public void Initialize()
        {
            ClassInjector.RegisterTypeInIl2Cpp<GlobalBehaviour>();
            _setAsLastSibling = IL2CPP.ResolveICall<SetAsLastSiblingDelegate>("UnityEngine.Transform::SetAsLastSibling");

            if (GlobalBehaviour.Instance != null)
                return;

            Create();
        }

        public void Create()
        {
            var gameObject = new GameObject();
            UnityObject.DontDestroyOnLoad(gameObject);
            gameObject.hideFlags = HideFlags.DontSave;
            GlobalBehaviour.Instance = gameObject.AddComponent(Il2CppType.Of<GlobalBehaviour>()).TryCast<GlobalBehaviour>();
            Maintain(GlobalBehaviour.Instance);
        }

        public void Maintain(GlobalBehaviour behaviour)
        {
            _setAsLastSibling(IL2CPP.Il2CppObjectBaseToPtrNotNull(behaviour.gameObject.transform));
            _setAsLastSibling(IL2CPP.Il2CppObjectBaseToPtrNotNull(behaviour.transform));
        }

        private delegate bool SetAsLastSiblingDelegate(IntPtr transformPointer);
    }
}
