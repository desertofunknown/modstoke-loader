// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodRestoration
    {
        private bool TryRestoreVfxSpawnerConstructor(MethodDefinition source, MethodDefinition constructor,
            InteropTypeContext owner, InteropRuntimeReferences imports, InteropUnstripTypeResolver resolver,
            InteropRestorationEntry entry)
        {
            entry.Set("signature-rejected", "vfx-spawner-owned-constructor-contract",
                "The original owned spawner constructor requires its native fields, cleanup methods and pointer-wrapping constructor.");
            if (!InteropVfxSpawnerConstruction.TryGetPointerConstructor(source, owner, imports, resolver, out var pointerConstructor) ||
                !IsReferenceConstructorDeclaration(constructor) || constructor.Attributes != source.Attributes ||
                constructor.Parameters.Count != 0 || constructor.Signature.ParameterTypes.Count != 0 ||
                constructor.CilMethodBody is not { } constructorBody || constructorBody.Instructions.Count != 0 ||
                constructorBody.LocalVariables.Count != 0 || constructorBody.ExceptionHandlers.Count != 0 ||
                owner.TryGetRestoredReferenceConstructor(source) != null || owner.TryGetRestoredBody(source) != null)
                return false;

            var importer = imports.Module.DefaultImporter;
            var initialize = importer.ImportMethod(typeof(InteropVfxSpawnerSupport)
                .GetMethod(nameof(InteropVfxSpawnerSupport.InitializeOwned)));
            var body = constructorBody.Instructions;
            body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Ldsfld, owner.ClassPointerFieldRef);
            body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_object_new.Value);
            body.Add(CilOpCodes.Call, importer.ImportMethod(pointerConstructor));
            // Initialize this rooted allocation; the generated two-argument constructor would allocate another object.
            body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Call, initialize);
            body.Add(CilOpCodes.Ret);
            owner.NewType.Methods.Add(constructor);
            owner.AddRestoredReferenceConstructor(source, constructor);
            owner.AddRestoredBody(source, constructor);
            completedBodies.Add((constructor, entry));
            entry.Set("native-vfx-spawner-constructor-adapter");
            return true;
        }
    }
}
