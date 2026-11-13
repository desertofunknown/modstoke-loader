// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using AsmResolver.DotNet;
using BepInEx.Logging;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodRestoration
    {
        private readonly List<(MethodDefinition Original, MethodDefinition Target, InteropTypeContext Type,
            InteropRuntimeReferences Imports, InteropRestorationEntry Entry)> pendingMathBodies = new();

        // A native wrapper of covered value math is queued for Unity's managed body; anything else keeps the wrapper.
        private bool TryQueueManagedMath(MethodDefinition original, MethodDefinition wrapper, InteropTypeContext type,
            InteropRuntimeReferences imports, InteropRestorationEntry entry)
        {
            if (wrapper == null || !InteropManagedMath.Covers(original.DeclaringType))
                return false;
            if (!InteropManagedMath.Qualifies(original, type, out var reason))
            {
                entry.Set("existing-wrapper", "managed-math-ineligible", reason);
                return true;
            }
            pendingMathBodies.Add((original, wrapper, type, imports, entry));
            entry.Set("managed-math-pending");
            return true;
        }

        // Runs after the restored bodies, so calls between covered methods bind to their final declarations.
        private void FillManagedMathBodies()
        {
            var translated = 0;
            var kept = 0;
            foreach (var method in pendingMathBodies)
            {
                var native = method.Target.CilMethodBody;
                var success = false;
                InteropTranslationFailure failure = null;
                try
                {
                    success = InteropMethodTranslator.TranslateMethod(method.Original, method.Target, method.Type,
                        method.Imports, out failure);
                }
                catch (Exception error)
                {
                    failure = new InteropTranslationFailure("managed-math-error", InteropGenerationDiagnostics.Message(error),
                        null, string.Empty, string.Empty);
                }
                if (success)
                {
                    translated++;
                    method.Entry.Set("managed-math-body");
                    continue;
                }
                // The native wrapper is still correct, only slower.
                method.Target.CilMethodBody = native;
                kept++;
                method.Entry.Set("existing-wrapper", "managed-math-rejected", failure?.Reason + ": " + failure?.Detail);
            }
            pendingMathBodies.Clear();
            InteropGenerationDiagnostics.Log(LogLevel.Info, InteropManagedMath.Enabled
                ? $"Unity value math: {translated} wrappers run managed bodies, {kept} kept native after translation."
                : "Unity value math keeps native wrappers (" + InteropManagedMath.DisableVariable + "=1).");
        }
    }
}
