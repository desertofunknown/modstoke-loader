// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using System.Threading.Tasks;
using RedLoader;
using RedLoader.Utils;
using SonsSdk;
using SonsSdk.Building;
using UnityGLTF;
using Color = System.Drawing.Color;

namespace OpenACAI.Endnight.Loader
{
    internal sealed partial class OpenACAISonsBlueprintRegistration : ISdkBlueprintRegistrationDriver
    {
        internal static readonly OpenACAISonsBlueprintRegistration Instance = new();

        private OpenACAISonsBlueprintRegistration() { }

        public void Register(ScrewStructureRegistration registration)
        {
            if (!CustomBlueprintManager.LoadedObjects.TryAdd(registration.recipeId, registration))
            {
                throw new Exception($"Recipe Collision!!! Blueprint with id {registration.recipeId} already registered!");
            }

            CustomBlueprintManager.OnPrefabLoaded.Invoke(registration);
        }

        public async Task LoadGltfObjectsFromDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
                directory = LoaderEnvironment.BlueprintDirectory;

            foreach (var file in Directory.GetFiles(directory))
            {
                if (!file.EndsWith(".gltf") && !file.EndsWith(".glb"))
                    continue;

                var fileName = Path.GetFileNameWithoutExtension(file);
                var recipeId = GetRecipeIdFromName(fileName);

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
                loader.ProcessMaterials = !CustomBlueprintManager.ServerMode;
                await loader.LoadSceneAsync();

                Register(new(loader.CreatedObject.HideAndDontSave(), recipeId.Value, fileName + "Recipe"));
                RLog.Msg(Color.PaleGreen, $"Loaded {file}");
            }
        }

        public int? GetRecipeIdFromName(string name)
        {
            if (!name.Contains('.'))
                return LoaderUtils.HashString(name);

            var split = name.Split('.');
            if (!int.TryParse(split[^1], out var id))
                return LoaderUtils.HashString(name);

            return id;
        }
    }
}
