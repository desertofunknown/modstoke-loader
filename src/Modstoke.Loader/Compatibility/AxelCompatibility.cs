// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using HarmonyLib;
using UnityEngine;

namespace Modstoke.Loader
{
    internal static class AxelCompatibility
    {
        private const string KnownAssemblySha256 = "BC8BAE4B9EF642D96A5609A208A8D5D6BBACE0CBA561D58485F98BA3426A3134";
        private static readonly Guid KnownModuleVersionId = new Guid("cb44b5ac-8211-40f3-afe7-a8980096025a");
        private static bool _installed;

        internal static void Install(RedLoaderHostResult hostResult, Action<string> addStartupWarning)
        {
            if (_installed || hostResult == null)
                return;

            HarmonyLib.Harmony harmony = null;
            MethodInfo init = null;
            bool patchAttempted = false;
            try
            {
                Assembly assembly = null;
                foreach (var mod in hostResult.HostedMods)
                {
                    var candidate = mod?.ModAssembly;
                    if (candidate != null && string.Equals(candidate.GetName().Name, "AxelModMenu", StringComparison.Ordinal))
                    {
                        assembly = candidate;
                        break;
                    }
                }

                if (assembly == null)
                    return;

                if (!MatchesKnownAssembly(assembly))
                {
                    addStartupWarning?.Invoke("AxelModMenu does not match the supported WorldEdit repair build; leaving its initialization unpatched.");
                    return;
                }

                var worldEdit = assembly.GetType("AxelModMenu.WorldEdit", throwOnError: true);
                init = AccessTools.DeclaredMethod(worldEdit, "Init", Type.EmptyTypes);
                if (init == null || !init.IsStatic || init.ReturnType != typeof(void) || init.ContainsGenericParameters)
                    throw new InvalidOperationException("AxelModMenu.WorldEdit.Init does not match the supported method contract.");

                FindTagOperand(PatchProcessor.GetOriginalInstructions(init));
                harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".axel-worldedit-compatibility");
                patchAttempted = true;
                harmony.Patch(init, transpiler: new HarmonyMethod(typeof(AxelCompatibility), nameof(WorldEditInitTranspiler)));
                _installed = true;
                RuntimeEntryPoint.TraceCompat("Installed exact-build AxelModMenu WorldEdit tag repair; original initialization retained.");
            }
            catch (Exception ex)
            {
                if (patchAttempted)
                {
                    try
                    {
                        harmony.Unpatch(init, HarmonyPatchType.Transpiler, harmony.Id);
                    }
                    catch (Exception rollbackError)
                    {
                        addStartupWarning?.Invoke("AxelModMenu WorldEdit repair cleanup failed: " + rollbackError.Message);
                    }
                }

                _installed = false;
                addStartupWarning?.Invoke("AxelModMenu WorldEdit repair failed: " + ex.Message);
            }
        }

        private static bool MatchesKnownAssembly(Assembly assembly)
        {
            if (assembly.ManifestModule.ModuleVersionId != KnownModuleVersionId || string.IsNullOrEmpty(assembly.Location))
                return false;

            using var stream = File.OpenRead(assembly.Location);
            return string.Equals(Convert.ToHexString(SHA256.HashData(stream)), KnownAssemblySha256, StringComparison.Ordinal);
        }

        private static IEnumerable<CodeInstruction> WorldEditInitTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            int tagOperand = FindTagOperand(code);

            // This exact build has no tag readers; WorldEdit retains its object and renderer directly.
            // Change only the tag value, preserving the object name, stack, labels and initialization.
            code[tagOperand].operand = "Untagged";
            return code;
        }

        private static int FindTagOperand(IReadOnlyList<CodeInstruction> code)
        {
            var tagSetter = AccessTools.PropertySetter(typeof(GameObject), nameof(GameObject.tag));
            if (tagSetter == null)
                throw new MissingMethodException("UnityEngine.GameObject.set_tag");

            int tagWrites = 0;
            int tagOperand = -1;
            for (int i = 0; i < code.Count; i++)
            {
                if (!code[i].Calls(tagSetter))
                    continue;

                tagWrites++;
                if (i >= 2 && i + 1 < code.Count
                    && code[i].opcode == OpCodes.Callvirt
                    && code[i - 2].opcode == OpCodes.Dup
                    && code[i - 1].opcode == OpCodes.Ldstr
                    && string.Equals(code[i - 1].operand as string, "AxelWorldEdit", StringComparison.Ordinal)
                    && code[i + 1].opcode == OpCodes.Stsfld
                    && code[i + 1].operand is FieldInfo field
                    && field.IsStatic && field.FieldType == typeof(GameObject)
                    && field.Name == "AxelWorldEdit" && field.DeclaringType?.FullName == "AxelModMenu.WorldEdit")
                {
                    tagOperand = i - 1;
                }
            }

            if (tagWrites != 1 || tagOperand < 0)
                throw new InvalidOperationException("AxelModMenu.WorldEdit.Init no longer has the single supported tag assignment.");

            return tagOperand;
        }
    }
}
