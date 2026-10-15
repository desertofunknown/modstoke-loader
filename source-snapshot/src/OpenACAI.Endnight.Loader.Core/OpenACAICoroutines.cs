// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections;
using RedLoader;
using RedLoader.Unity.IL2CPP.UnityEngine;
using RedLoader.Unity.IL2CPP.Utils.Collections;
using UnityEngine;

namespace OpenACAI.Endnight.Loader
{
    internal sealed class OpenACAICoroutines : ICoroutineDriver
    {
        private static readonly OpenACAICoroutines Driver = new();

        private OpenACAICoroutines() { }

        internal static void Install()
        {
            Coroutines.SetDriver(Driver);
        }

        public Coroutine Start(IEnumerator routine)
        {
            if (!PrepareStart(routine))
                return null;

            if (GlobalBehaviour.Instance == null)
                throw new NotSupportedException("Support module must be initialized before starting coroutines");

            return GlobalBehaviour.Instance.StartCoroutine(routine.WrapToIl2Cpp());
        }

        public void Stop(Coroutine coroutineToken)
        {
            if (!CanStop())
                return;

            if (GlobalBehaviour.Instance == null)
                throw new NotSupportedException("Support module must be initialized before starting coroutines");

            GlobalBehaviour.Instance.StopCoroutine(coroutineToken);
        }

        internal static bool PrepareStart(IEnumerator routine)
        {
            if (OpenACAILegacyRedLoaderCompatibility.IsSupportModuleReady())
                return true;

            OpenACAILegacyRedLoaderCompatibility.TryInitializeSupportModule(null);
            if (OpenACAILegacyRedLoaderCompatibility.IsSupportModuleReady())
                return true;

            OpenACAILegacyRedLoaderCompatibility.QueueCoroutineStart(routine);
            OpenACAIEndnightRuntime.TraceCompat("Queued RedLoader coroutine start because support module was not initialized.");
            return false;
        }

        internal static bool CanStop()
        {
            if (OpenACAILegacyRedLoaderCompatibility.IsSupportModuleReady())
                return true;

            OpenACAIEndnightRuntime.TraceCompat("Blocked RedLoader coroutine stop because support module is not initialized yet.");
            return false;
        }
    }
}
