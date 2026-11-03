// SPDX-License-Identifier: GPL-3.0-or-later
// Coroutine fallback adapted from RedLoader.
// Copyright (c) RedLoader contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections;
using RedLoader;
using RedLoader.Unity.IL2CPP.UnityEngine;
using UnityEngine;

namespace Modstoke.Loader;

internal sealed class RedCoroutineFallback : ICoroutineDriver
{
    private static readonly RedCoroutineFallback Driver = new();

    private RedCoroutineFallback() { }

    internal static ICoroutineDriver Create() => Driver;

    public Coroutine Start(IEnumerator routine)
    {
        return global::Modstoke.Loader.Coroutines.StartOnHost(routine);
    }

    public void Stop(Coroutine coroutineToken)
    {
        if (global::Modstoke.Loader.Coroutines.StopOwned(coroutineToken))
            return;
        if (GlobalBehaviour.Instance == null)
            throw new NotSupportedException("Support module must be initialized before stopping coroutines");
        GlobalBehaviour.Instance.StopCoroutine(coroutineToken);
    }
}
