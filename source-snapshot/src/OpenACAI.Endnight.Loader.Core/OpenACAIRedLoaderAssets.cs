// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Owned deferred loading for the asset attributes prepared by the managed host.
// The SDK retains the public attribute identities and startup scheduling.

using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using Il2CppInterop.Runtime;
using RedLoader;
using RedLoader.Utils;
using SonsSdk.Attributes;
using UnityEngine;
using Color = System.Drawing.Color;
using UnityObject = UnityEngine.Object;

namespace OpenACAI.Endnight.Loader
{
    internal static class OpenACAIRedLoaderAssets
    {
        private static readonly Dictionary<Type, Func<IntPtr, UnityObject>> Initializers = new();

        internal static bool LoadBundle(AssetBundleAttribute bundle, Assembly assembly)
        {
            if (bundle.LoadedBundle)
                return true;

            var path = LoaderEnvironment.GetModDataPath(assembly) / bundle.AssetBundleName;
            if (!path.FileExists())
            {
                ReportError("Couldn't find asset bundle " + path.Path);
                return false;
            }

            bundle.LoadedBundle = AssetBundle.LoadFromFile(path.Path);
            if (!bundle.LoadedBundle)
            {
                ReportError("Couldn't load asset bundle " + path.Path);
                return false;
            }

            var mapped = 0;
            foreach (var reference in bundle.AssetReferences)
            {
                if (TryMapAsset(reference))
                    mapped++;
            }

            try
            {
                RLog.Msg(Color.Orange, "Loaded bundle " + bundle.AssetBundleName
                    + "; mapped " + mapped + "/" + bundle.AssetReferences.Count + " assets");
            }
            catch { /* A diagnostic listener cannot reject an already-loaded bundle. */ }
            return true;
        }

        private static bool TryMapAsset(AssetReferenceAttribute reference)
        {
            var bundle = reference.AssetBundle;
            var property = reference.Property;
            try
            {
                if (!bundle.LoadedBundle)
                {
                    ReportError("Asset bundle " + bundle.AssetBundleName + " is not loaded.");
                    return false;
                }

                // This default is observable through the shared attribute's public getter.
                if (string.IsNullOrEmpty(reference.AssetName))
                    reference.AssetName = property.Name;

                if (property.SetMethod == null || !property.SetMethod.IsStatic || property.GetIndexParameters().Length != 0)
                {
                    ReportMappingError(reference, "the property must have a static setter and no index parameters.");
                    return false;
                }

                if (!typeof(UnityObject).IsAssignableFrom(property.PropertyType))
                {
                    ReportMappingError(reference, "the property type must derive from UnityEngine.Object.");
                    return false;
                }

                var asset = bundle.LoadedBundle.LoadAsset(reference.AssetName, Il2CppType.From(property.PropertyType));
                if (!asset)
                {
                    ReportMappingError(reference, "the bundle did not return a usable asset.");
                    return false;
                }

                asset.hideFlags = HideFlags.HideAndDontSave;
                var value = property.PropertyType == typeof(UnityObject)
                    ? asset
                    : GetInitializer(property.PropertyType)(asset.Pointer);
                property.SetValue(null, value);
                return true;
            }
            catch (Exception error)
            {
                ReportMappingError(reference, error.ToString());
                return false;
            }
        }

        private static Func<IntPtr, UnityObject> GetInitializer(Type type)
        {
            if (Initializers.TryGetValue(type, out var initializer))
                return initializer;

            var constructor = type.GetConstructor(new[] { typeof(IntPtr) });
            if (constructor == null)
                throw new MissingMethodException(type.FullName, ".ctor(IntPtr)");

            var pointer = Expression.Parameter(typeof(IntPtr), "pointer");
            var instance = Expression.Convert(Expression.New(constructor, pointer), typeof(UnityObject));
            initializer = Expression.Lambda<Func<IntPtr, UnityObject>>(instance, pointer).Compile();
            Initializers.Add(type, initializer);
            return initializer;
        }

        private static void ReportMappingError(AssetReferenceAttribute reference, string reason)
        {
            var property = reference.Property;
            ReportError("Couldn't map asset " + reference.AssetName + " from " + reference.AssetBundle.AssetBundleName
                + " into " + property.DeclaringType?.FullName + "." + property.Name + ": " + reason);
        }

        private static void ReportError(string message)
        {
            try { RLog.Error(message); }
            catch { /* A mod's log listener must not stop the remaining asset mappings. */ }
        }
    }
}
