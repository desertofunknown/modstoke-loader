// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Owned catalog loading within the SDK's managed startup coroutine lifetime.

using System;
using System.Collections;
using System.Drawing;
using System.Linq;
using RedLoader;
using RedLoader.Utils;
using SonsSdk;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Modstoke.Loader
{
    internal static class RedLoaderCatalogs
    {
        internal static IEnumerator LoadCatalogs(SonsMod mod)
        {
            if (!mod.Registered)
                yield break;

            string[] catalogs;
            try
            {
                var directory = LoaderEnvironment.GetModDataPath(mod);
                if (!directory.DirectoryExists())
                    yield break;
                catalogs = directory.GetFiles("catalog*.json").ToArray();
            }
            catch (Exception error)
            {
                ReportError("Couldn't enumerate addressables catalogs for " + mod.ModAssembly.FullName + ".", error);
                yield break;
            }

            foreach (var catalog in catalogs)
            {
                if (!mod.Registered)
                    yield break;
                yield return LoadCatalog(mod, catalog);
            }
        }

        private static IEnumerator LoadCatalog(SonsMod mod, string path)
        {
            if (!mod.Registered)
                yield break;

            try { RLog.Msg(Color.Orange, "Loading addressables catalog " + path); }
            catch { /* A diagnostic exception must not prevent the native load from starting. */ }

            // Log subscribers can unregister the mod before the native operation starts.
            if (!mod.Registered)
                yield break;

            AsyncOperationHandle<IResourceLocator> operation;
            try
            {
                operation = Addressables.LoadContentCatalogAsync(path, false);
            }
            catch (Exception error)
            {
                ReportError("Couldn't start loading addressables catalog " + path + ".", error);
                yield break;
            }

            try
            {
                while (true)
                {
                    // Preserve the SDK's next-frame observation, even for an already-complete load.
                    yield return null;
                    if (!mod.Registered)
                        yield break;

                    try
                    {
                        if (operation == null || !operation.IsValid())
                        {
                            ReportError("Addressables catalog " + path + " returned an invalid operation handle.");
                            break;
                        }

                        if (!operation.IsDone)
                            continue;

                        if (operation.Status != AsyncOperationStatus.Succeeded)
                            ReportError("Couldn't load addressables catalog " + path + ": " + operation.OperationException);
                    }
                    catch (Exception error)
                    {
                        ReportError("Couldn't observe addressables catalog " + path + ".", error);
                    }
                    break;
                }
            }
            finally
            {
                // The SDK disposes nested managed routines on completion, failure or cancellation.
                try
                {
                    if (operation != null && operation.IsValid())
                        Addressables.Release<IResourceLocator>(operation);
                }
                catch (Exception error)
                {
                    ReportError("Couldn't release the operation for addressables catalog " + path + ".", error);
                }
            }
        }

        private static void ReportError(string message, Exception error = null)
        {
            try { RLog.Error(error == null ? message : message + "\n" + error); }
            catch { /* A failing log listener cannot stop other catalogs or handle cleanup. */ }
        }
    }
}
