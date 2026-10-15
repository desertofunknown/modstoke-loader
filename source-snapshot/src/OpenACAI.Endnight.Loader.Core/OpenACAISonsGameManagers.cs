// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using SonsSdk;
using TheForest;
using UnityEngine;

namespace OpenACAI.Endnight.Loader
{
    internal sealed class OpenACAISonsGameManagers : ISdkGameManagerDriver
    {
        internal static readonly OpenACAISonsGameManagers Instance = new();

        private OpenACAISonsGameManagers() { }

        public void Initialize()
        {
            SdkEvents.OnGameActivated.Subscribe(GameManagers.Create);
        }

        public void Create()
        {
            var root = DebugConsole.Instance.transform.parent;
            GameManagers._instance = new GameManagers(root);
        }

        public T GetManager<T>(string managerName) where T : Component
        {
            var managers = GameManagers._instance;
            if (managers == null || !managers._gameManagersTr)
                return null;

            var manager = managers._gameManagersTr.Find(managerName);
            return manager ? manager.GetComponent<T>() : null;
        }
    }
}
