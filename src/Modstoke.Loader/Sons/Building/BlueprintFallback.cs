// SPDX-License-Identifier: GPL-3.0-or-later
// Blueprint fallback adapted from SonsSdk.
// Copyright (c) RedLoader contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bolt;
using Construction;
using Endnight.Localization;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using RedLoader;
using RedLoader.Utils;
using global::Sons.Crafting;
using global::Sons.Crafting.Structures;
using global::Sons.Weapon;
using SonsSdk;
using SonsSdk.Building;
using SonsSdk.Networking;
using UnityEngine;
using UnityGLTF;
using Color = System.Drawing.Color;
using Object = UnityEngine.Object;

namespace Modstoke.Loader;

internal sealed partial class BlueprintFallback : ISdkBlueprintFallbackDriver
{
    private static readonly BlueprintFallback Driver = new();

    internal static ISdkBlueprintFallbackDriver Create() => Driver;

    public void TryRegister(ScrewStructureRegistration reg)
    {
        BlueprintRegistration.Instance.Register(reg);
    }

    public void OnGameActivated()
    {
        BlueprintRegistration.Instance.OnGameActivated();
    }

    public Task Init()
    {
        SdkEvents.OnSonsSceneInitialized.Subscribe(CustomBlueprintManager.OnSceneInit);

        return CustomBlueprintManager.LoadGltfObjectsFromDirectory();
    }

    public void OnSceneInit(ESonsScene scene)
    {
        if (scene != ESonsScene.Game) return;
        
        CustomBlueprintManager.OnGameActivated();
    }

    public async Task LoadGltfObjectsFromDirectory(string directory = null)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = LoaderEnvironment.BlueprintDirectory;
        }

        foreach (var file in Directory.GetFiles(directory))
        {
            if (!file.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
                continue;
            
            var fileName = Path.GetFileNameWithoutExtension(file);
            var recipeId = CustomBlueprintManager.GetRecipeIdFromName(fileName);

            if (fileName.Contains(" "))
            {
                RLog.Error($"Name of blueprint cannot contain spaces ({file})");
                continue;
            }

            if (recipeId == null)
            {
                RLog.Error("Couldn't setup recipe id from name");
                continue;
            }
            
            var loader = new GLTFSceneImporter(file, new());
            ScrewStructureRegistration registration = null;
            try
            {
                loader.ProcessMaterials = !CustomBlueprintManager.ServerMode;
                await loader.LoadSceneAsync();

                registration = new(loader.CreatedObject.HideAndDontSave(), recipeId.Value, fileName + "Recipe");
                CustomBlueprintManager.TryRegister(registration);
            }
            catch
            {
                // Registration publishes before notifying subscribers; a published prefab is no longer ours to discard.
                if (registration == null
                    || !CustomBlueprintManager.LoadedObjects.TryGetValue(registration.recipeId, out var published)
                    || !ReferenceEquals(published, registration))
                    DiscardPrefabCandidate(loader.CreatedObject);
                throw;
            }
            finally
            {
                try { loader.Dispose(); }
                catch (Exception exception) { ReportPrefabCleanupFailure(exception); }
            }
            
            RLog.Msg(Color.PaleGreen, $"Loaded {file}");
        }
    }
}
