// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using Modstoke.Sons;
using SonsSdk;
using TheForest;
using UnityEngine;

namespace Modstoke.Loader
{
    internal sealed class GameManagers : ISdkGameManagerDriver
    {
        internal static readonly global::Modstoke.Loader.GameManagers Instance = new();

        private int _ownerThreadId;
        private bool _gameActivated;

        private GameManagers() { }

        public void Initialize()
        {
            if (Interlocked.CompareExchange(ref _ownerThreadId, Environment.CurrentManagedThreadId, 0) != 0)
                return;

            SdkEvents.OnGameActivated.Subscribe(global::SonsSdk.GameManagers.Create);
            SdkEvents.OnWorldExited.Subscribe(Reset);
            SdkEvents.OnSonsSceneInitialized.Subscribe(OnSceneInitialized);
        }

        public void Create()
        {
            if (CheckThread() != ManagerAccessStatus.Ready)
                return;

            global::SonsSdk.GameManagers._instance = null;
            _gameActivated = true;
            TryGetRoot(out _, out _);
        }

        public T GetManager<T>(string managerName) where T : Component
        {
            var managers = global::SonsSdk.GameManagers._instance;
            if (managers == null || !managers._gameManagersTr)
                return null;

            var manager = managers._gameManagersTr.Find(managerName);
            return manager ? manager.GetComponent<T>() : null;
        }

        internal bool TryGetManager<T>(string managerPath, out T manager,
            out ManagerAccessStatus status) where T : Component
        {
            manager = null;
            status = CheckThread();
            if (status != ManagerAccessStatus.Ready)
                return false;

            if (string.IsNullOrWhiteSpace(managerPath) || managerPath[0] == '/')
            {
                status = ManagerAccessStatus.InvalidPath;
                return false;
            }

            if (!TryGetRoot(out var root, out status))
                return false;

            try
            {
                var child = root.Find(managerPath);
                if (child is null || child.WasCollected || !child)
                {
                    status = ManagerAccessStatus.ManagerNotFound;
                    return false;
                }

                var component = child.GetComponent<T>();
                if (component is null || component.WasCollected || !component)
                {
                    status = ManagerAccessStatus.ComponentNotFound;
                    return false;
                }

                manager = component;
                return true;
            }
            catch (Exception)
            {
                status = ManagerAccessStatus.Failed;
                return false;
            }
        }

        internal ManagerAccessStatus GetReadiness()
        {
            TryGetRoot(out _, out var status);
            return status;
        }

        private bool TryGetRoot(out Transform root, out ManagerAccessStatus status)
        {
            root = null;
            status = CheckThread();
            if (status != ManagerAccessStatus.Ready)
                return false;
            if (!_gameActivated)
            {
                status = ManagerAccessStatus.WorldUnavailable;
                return false;
            }

            try
            {
                var cachedRoot = global::SonsSdk.GameManagers._instance?._gameManagersTr;
                if (IsLoadedRoot(cachedRoot))
                {
                    root = cachedRoot;
                    return true;
                }

                global::SonsSdk.GameManagers._instance = null;
                status = ManagerAccessStatus.RootUnavailable;
                // Read the existing console field, never GetInstance or a scene-wide object search.
                var console = DebugConsole.Instance;
                if (console is null || console.WasCollected || !console)
                    return false;
                var consoleTransform = console.transform;
                if (consoleTransform is null || consoleTransform.WasCollected || !consoleTransform)
                    return false;
                var candidate = consoleTransform.parent;
                if (!IsLoadedRoot(candidate))
                    return false;

                global::SonsSdk.GameManagers._instance = new global::SonsSdk.GameManagers(candidate);
                root = candidate;
                status = ManagerAccessStatus.Ready;
                return true;
            }
            catch (Exception)
            {
                global::SonsSdk.GameManagers._instance = null;
                status = ManagerAccessStatus.Failed;
                return false;
            }
        }

        private ManagerAccessStatus CheckThread()
        {
            var owner = Volatile.Read(ref _ownerThreadId);
            if (owner == 0)
                return ManagerAccessStatus.Unavailable;
            return Environment.CurrentManagedThreadId == owner
                ? ManagerAccessStatus.Ready : ManagerAccessStatus.WrongThread;
        }

        private static bool IsLoadedRoot(Transform root)
        {
            if (root is null || root.WasCollected || !root)
                return false;
            var scene = root.gameObject.scene;
            return scene.IsValid() && scene.isLoaded;
        }

        private void OnSceneInitialized(ESonsScene scene)
        {
            if (scene == ESonsScene.Title || scene == ESonsScene.Loading)
                Reset();
        }

        private void Reset()
        {
            if (CheckThread() != ManagerAccessStatus.Ready)
                return;
            _gameActivated = false;
            global::SonsSdk.GameManagers._instance = null;
        }
    }
}

namespace Modstoke.Sons
{
    /// <summary>Typed, main-thread access to the current world's existing SDK manager hierarchy.</summary>
    public static class ManagerAccess
    {
        /// <summary>
        /// Reports whether the manager root can be read now. Ready does not guarantee that a particular
        /// manager exists, is enabled or has completed its own initialization. No objects are created in the game.
        /// </summary>
        public static ManagerAccessStatus GetReadiness() =>
            global::Modstoke.Loader.GameManagers.Instance.GetReadiness();

        /// <summary>
        /// Looks up an exact root-relative Transform.Find path and reads its component, including inactive
        /// children. The returned native object is borrowed: use it on the Unity main thread, do not destroy it,
        /// and reacquire it after world changes. Success grants no multiplayer authority or mutation guarantee.
        /// </summary>
        public static bool TryGetManager<T>(string managerPath, out T manager,
            out ManagerAccessStatus status) where T : Component =>
            global::Modstoke.Loader.GameManagers.Instance.TryGetManager(managerPath, out manager, out status);
    }

    /// <summary>Availability of a local manager lookup, not the manager's internal gameplay state.</summary>
    public enum ManagerAccessStatus
    {
        /// <summary>The requested root or component is currently available.</summary>
        Ready,
        /// <summary>The SDK manager service has not initialized.</summary>
        Unavailable,
        /// <summary>The caller is not on the SDK's Unity main thread; no native objects were inspected.</summary>
        WrongThread,
        /// <summary>The current world has not activated or has already exited.</summary>
        WorldUnavailable,
        /// <summary>No live manager root is available through the SDK's existing root discovery route.</summary>
        RootUnavailable,
        /// <summary>The path is empty, whitespace or absolute instead of root-relative.</summary>
        InvalidPath,
        /// <summary>No live transform was found at the exact path.</summary>
        ManagerNotFound,
        /// <summary>The transform exists but has no live component of the requested type.</summary>
        ComponentNotFound,
        /// <summary>A native read failed; absence or readiness could not be established.</summary>
        Failed
    }
}
