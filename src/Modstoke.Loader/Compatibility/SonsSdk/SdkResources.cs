// SPDX-License-Identifier: GPL-3.0-or-later
// Resource and importer setup adapted from SonsSdk.
// Copyright (c) RedLoader contributors. Copyright (C) 2026 Alex Cooper.

using System.Collections.Generic;
using RedLoader.Utils;
using SonsSdk;
using SonsSdk.AssetImporting.GLTF;
using TMPro;
using UnityEngine;
using UnityGLTF;

namespace Modstoke.Loader;

// SonsSdk binds Load and RegisterAssetImporters by name through reflection.
internal static class SdkResources
{
    private static bool _importersRegistered;

    internal static void Load()
    {
        var timing = new TimingLogger(nameof(GameResources));
        // Discovery can fail during scene teardown; keep the previous caches until all scans succeed.
        var materials = Collect<Material>();
        var sprites = Collect<Sprite>();
        var textures = Collect<Texture2D>();
        var shaders = Collect<Shader>();
        var fonts = Collect<TMP_FontAsset>();
        ReplaceContents(GameResources.Materials, materials);
        ReplaceContents(GameResources.Sprites, sprites);
        ReplaceContents(GameResources.Textures, textures);
        ReplaceContents(GameResources.Shaders, shaders);
        ReplaceContents(GameResources.Fonts, fonts);

        timing.Stop("Loading resources");
    }

    private static Dictionary<string, T> Collect<T>() where T : UnityEngine.Object
    {
        var result = new Dictionary<string, T>();
        foreach (var asset in Resources.FindObjectsOfTypeAll<T>())
        {
            if (asset)
                result[asset.name] = asset;
        }
        return result;
    }

    private static void ReplaceContents<T>(Dictionary<string, T> target, Dictionary<string, T> source)
    {
        // Keep the public cache object stable for mods that retained its reference.
        target.Clear();
        foreach (var entry in source)
            target.Add(entry.Key, entry.Value);
    }

    internal static void RegisterAssetImporters()
    {
        if (_importersRegistered)
            return;
        GLTFSettings.RegisterPlugin<IngredientImport>();
        _importersRegistered = true;
    }
}
