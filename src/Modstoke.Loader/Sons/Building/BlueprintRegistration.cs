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

namespace Modstoke.Loader
{
    internal sealed partial class BlueprintRegistration : ISdkBlueprintRegistrationDriver
    {
        internal static readonly BlueprintRegistration Instance = new();

        private BlueprintRegistration() { }

        public void Register(ScrewStructureRegistration registration)
        {
            ArgumentNullException.ThrowIfNull(registration);
            if (!registration.prefab)
                throw new ArgumentException("A live blueprint prefab is required.", nameof(registration));
            if (registration.recipeName == null)
                throw new ArgumentException("A blueprint recipe name is required.", nameof(registration));

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
                if (!file.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)
                    && !file.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
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
                ScrewStructureRegistration registration = null;
                try
                {
                    loader.ProcessMaterials = !CustomBlueprintManager.ServerMode;
                    await loader.LoadSceneAsync();

                    registration = new(loader.CreatedObject.HideAndDontSave(), recipeId.Value, fileName + "Recipe");
                    Register(registration);
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

        public int? GetRecipeIdFromName(string name)
        {
            var separator = name.LastIndexOf('.');
            if (separator < 0)
                return LoaderUtils.HashString(name);

            if (!int.TryParse(name.AsSpan(separator + 1), out var id))
                return LoaderUtils.HashString(name);

            return id;
        }
    }
}
