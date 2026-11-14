// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using Modstoke.Loader;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using Object = UnityEngine.Object;

namespace Modstoke.Sons
{
    public static partial class HeldItems
    {
        // Armed by held-selftest-next-launch.flag in the user data folder: three client-local boxes that exercise held
        // models before a mod uses them, in single player (client-local items can't be held in multiplayer). A is a
        // left-hand two-handed item with the GPS tracker's pose, B a right-hand item with the radio's, and C is A's
        // twin whose hand definition is withheld, to check that refused equips keep the item.
        private const string SelfTestArm = "held-selftest-next-launch.flag";
        private const string SelfTestModId = "modstoke.held-selftest";
        private const int SelfTestA = 60001, SelfTestB = 60002, SelfTestC = 60003;
        private static readonly HashSet<int> Withheld = new();
        private static bool? _selfTestArmed;
        private static GameObject _selfTestBox;

        private static void TickSelfTest()
        {
            if (_selfTestArmed == false) return;
            try
            {
                _selfTestArmed ??= File.Exists(Path.Combine(LoaderPaths.UserDataDirectory, SelfTestArm));
                if (_selfTestArmed != true || !ItemRegistration.RuntimeRegistrationReady) return;
                ItemIds.TryReserve(SelfTestModId, SelfTestA, SelfTestC, ItemIdBand.ClientLocal);
                Withheld.Add(SelfTestC);
                var registered = new List<string>();
                // The GPS tracker's hand holds a device upright in the palm, its screen to the face.
                var palm = new Vector3(0f, 0.02f, -0.07f);
                foreach (var (id, key, donor, both, grip) in new[]
                {
                    (SelfTestA, "tablet", 412, (bool?)true, palm),
                    (SelfTestB, "controller", 590, null, Vector3.zero),
                    (SelfTestC, "withheld", 412, true, palm)
                })
                {
                    var definition = new CustomItemDefinition(ItemRegistrationKind.CustomItem, SelfTestModId, id, key,
                        "Held test " + key, "", 0, true, false, "", "", Array.Empty<string>());
                    var model = new ItemModel(SelfTestBox)
                    {
                        Description = "A box that tests held models.",
                        MatCategory = MatCategory.Device,
                        Held = new HeldModel(donor) { RequiresBothHands = both, GripOffset = grip }
                    };
                    var result = ItemRegistration.ExecuteItemRegistration(definition, model);
                    // The Items table or another registration may still be busy; try again next frame.
                    if (result.ReasonCode == "registration-busy" || result.ReasonCode.EndsWith("-not-ready", StringComparison.Ordinal))
                        return;
                    registered.Add(id + " " + (result.Registered || result.ReasonCode == "runtime-registration-exists" ? "ok" : result.ReasonCode + ": " + result.Detail));
                }
                _selfTestArmed = false;
                File.Delete(Path.Combine(LoaderPaths.UserDataDirectory, SelfTestArm));
                RuntimeEntryPoint.LogSource?.LogInfo("Held self-test items: " + string.Join(", ", registered) + ".");
            }
            catch (Exception error)
            {
                _selfTestArmed = false;
                RuntimeEntryPoint.LogSource?.LogWarning("The held self-test items could not be registered: " + error);
            }
        }

        // A small tablet-sized box, its long side along Z.
        private static GameObject SelfTestBox()
        {
            if (Live(_selfTestBox)) return _selfTestBox;
            var root = new GameObject("Modstoke held self-test box");
            root.SetActive(false);
            Object.DontDestroyOnLoad(root);
            var primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
            primitive.transform.SetParent(root.transform, false);
            primitive.transform.localScale = new Vector3(0.09f, 0.012f, 0.15f);
            var shader = Shader.Find("HDRP/Lit");
            if (shader != null && shader)
            {
                var material = new Material(shader) { name = "Modstoke held self-test" };
                material.SetColor("_BaseColor", new Color(0.95f, 0.45f, 0.1f));
                HDMaterial.ValidateMaterial(material);
                primitive.GetComponent<MeshRenderer>().sharedMaterial = material;
            }
            return _selfTestBox = root;
        }
    }
}
