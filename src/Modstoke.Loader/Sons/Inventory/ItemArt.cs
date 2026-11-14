// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using Object = UnityEngine.Object;

namespace Modstoke.Sons
{
    /// <summary>
    /// Authored item art from a mod's own asset bundle: a prefab without materials and its texture set
    /// (&lt;code&gt;_BaseColor, &lt;code&gt;_Mask, &lt;code&gt;_Normal), drawn with the game's HDRP/Lit, the way the optics
    /// are. A bundle is read once from beside the mod's assembly; models and materials are made once per code.
    /// </summary>
    /// <remarks>
    /// The template is inactive and survives scene loads, so it can back an <see cref="ItemModel"/> factory or be
    /// copied wherever the mod draws the item. Parts the mod draws itself, such as a live screen, keep an empty
    /// material when <c>ownMaterial</c> names them.
    /// </remarks>
    public static class ItemArt
    {
        private static readonly Dictionary<string, AssetBundle> Bundles = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, GameObject> Templates = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, Material> Materials = new(StringComparer.Ordinal);

        /// <summary>The bundle file beside an assembly (normally the mod's own), loaded once.</summary>
        public static bool TryLoadBundle(System.Reflection.Assembly owner, string fileName, out AssetBundle bundle, out string problem)
        {
            bundle = null;
            problem = null;
            string path;
            try { path = Path.Combine(Path.GetDirectoryName(owner.Location) ?? "", fileName); }
            catch (Exception error)
            {
                problem = "The mod's folder could not be read: " + error.Message;
                return false;
            }
            if (Bundles.TryGetValue(path, out var cached) && cached != null && cached)
            {
                bundle = cached;
                return true;
            }
            if (!File.Exists(path))
            {
                problem = fileName + " is not beside " + Path.GetFileName(owner.Location) + ".";
                return false;
            }
            var loaded = AssetBundle.LoadFromFile(path);
            if (loaded == null || !loaded)
            {
                problem = "Unity could not load " + path + ".";
                return false;
            }
            Bundles[path] = loaded;
            bundle = loaded;
            return true;
        }

        /// <summary>
        /// An inactive template of the art named <paramref name="code"/>, every mesh renderer drawing the code's
        /// texture set, except those whose object <paramref name="ownMaterial"/> accepts, which keep empty slots.
        /// Skinned renderers keep the slots the bundle gave them.
        /// </summary>
        public static bool TryGetTemplate(AssetBundle bundle, string code, out GameObject template, out string problem,
            Func<string, bool> ownMaterial = null)
        {
            template = null;
            problem = null;
            var key = bundle.GetInstanceID() + "/" + code;
            if (Templates.TryGetValue(key, out var cached) && cached != null && cached)
            {
                template = cached;
                return true;
            }
            var prefab = Load<GameObject>(bundle, code);
            if (prefab == null)
            {
                problem = code + " is not in the bundle.";
                return false;
            }
            if (!TryGetMaterial(bundle, code, out var material, out problem)) return false;

            var root = new GameObject("Modstoke " + code + " art");
            root.SetActive(false);
            Object.DontDestroyOnLoad(root);
            root.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            var model = Object.Instantiate(prefab, root.transform, false);
            model.name = code;
            foreach (var renderer in model.GetComponentsInChildren<MeshRenderer>(true))
            {
                var filter = renderer.GetComponent<MeshFilter>();
                var count = filter != null && filter && filter.sharedMesh != null && filter.sharedMesh ? filter.sharedMesh.subMeshCount : 1;
                var own = ownMaterial != null && ownMaterial(renderer.gameObject.name);
                var slots = new Material[count];
                for (var i = 0; i < count; i++) slots[i] = own ? null : material;
                renderer.sharedMaterials = slots;
                renderer.shadowCastingMode = ShadowCastingMode.On;
                renderer.receiveShadows = true;
            }
            Templates[key] = root;
            template = root;
            return true;
        }

        /// <summary>The HDRP/Lit material for a code's texture set, made once.</summary>
        public static bool TryGetMaterial(AssetBundle bundle, string code, out Material material, out string problem)
        {
            problem = null;
            var key = bundle.GetInstanceID() + "/" + code;
            if (Materials.TryGetValue(key, out material) && material != null && material) return true;
            var colour = Load<Texture2D>(bundle, code + "_BaseColor");
            var mask = Load<Texture2D>(bundle, code + "_Mask");
            var normal = Load<Texture2D>(bundle, code + "_Normal");
            if (colour == null || mask == null || normal == null)
            {
                problem = code + "'s textures are not all in the bundle.";
                return false;
            }
            var shader = Shader.Find("HDRP/Lit");
            if (shader == null || !shader)
            {
                problem = "The game's HDRP/Lit shader was not found.";
                return false;
            }
            material = new Material(shader) { name = "Modstoke " + code };
            material.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            material.SetColor("_BaseColor", Color.white);
            Map(material, "_BaseColorMap", colour);
            Map(material, "_MaskMap", mask);
            Map(material, "_NormalMap", normal);
            foreach (var unused in new[] { "_DetailMap", "_EmissiveColorMap", "_BentNormalMap", "_CoatMaskMap" }) Map(material, unused, null);
            Set(material, "_NormalScale", 1);
            Set(material, "_NormalMapSpace", 0);
            Set(material, "_UVBase", 0);
            if (material.HasProperty("_UVMappingMask")) material.SetVector("_UVMappingMask", new Vector4(1, 0, 0, 0));
            Set(material, "_MetallicRemapMin", 0);
            Set(material, "_MetallicRemapMax", 1);
            Set(material, "_SmoothnessRemapMin", 0);
            Set(material, "_SmoothnessRemapMax", 1);
            Set(material, "_AORemapMin", 0);
            Set(material, "_AORemapMax", 1);
            Set(material, "_AlphaCutoffEnable", 0);
            Set(material, "_EmissiveIntensity", 0);
            if (material.HasProperty("_EmissiveColor")) material.SetColor("_EmissiveColor", Color.black);
            material.DisableKeyword("_DETAIL_MAP");
            material.DisableKeyword("_EMISSIVE_COLOR_MAP");
            HDMaterial.ValidateMaterial(material);
            Materials[key] = material;
            return true;
        }

        /// <summary>A texture or other asset from the bundle by name, such as a code's _Icon, kept loaded.</summary>
        public static T Load<T>(AssetBundle bundle, string name) where T : Object
        {
            var asset = bundle.LoadAsset(name, Il2CppType.Of<T>())?.TryCast<T>();
            if (asset == null || !asset) return null;
            asset.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            return asset;
        }

        private static void Map(Material material, string name, Texture texture)
        {
            if (!material.HasProperty(name)) return;
            material.SetTexture(name, texture);
            material.SetTextureScale(name, Vector2.one);
            material.SetTextureOffset(name, Vector2.zero);
        }

        private static void Set(Material material, string name, float value)
        {
            if (material.HasProperty(name)) material.SetFloat(name, value);
        }
    }
}
