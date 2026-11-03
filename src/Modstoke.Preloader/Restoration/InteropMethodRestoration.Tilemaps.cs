// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodRestoration
    {
        private bool TryRestoreTilemapConstructor(MethodDefinition source, MethodDefinition constructor,
            InteropTypeContext owner, InteropRuntimeReferences imports, InteropUnstripTypeResolver resolver,
            InteropRestorationEntry entry)
        {
            entry.Set("signature-rejected", "tilemap-constructor-contract",
                "Tilemap construction requires the original validation body, native fields and empty native default constructor.");
            if (!InteropTilemapConstruction.TryGetNativeDefault(source, owner, imports, resolver, out var nativeDefault) ||
                !IsReferenceConstructorDeclaration(constructor) || !constructor.IsPublic ||
                constructor.Parameters.Count != 1 || constructor.Signature.ParameterTypes.Count != 1 ||
                constructor.CilMethodBody is not { } constructorBody || constructorBody.Instructions.Count != 0 ||
                constructorBody.LocalVariables.Count != 0 || constructorBody.ExceptionHandlers.Count != 0)
                return false;
            var tilemap = resolver.Resolve(source.Signature.ParameterTypes[0]);
            if (tilemap == null || !SignatureComparer.Default.Equals(constructor.Signature,
                    MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void, tilemap)))
                return false;

            const string name = "__InitializeTilemap_06000017";
            if (owner.NewType.Methods.Any(method => method.Name == name || method.Name == ".ctor" &&
                    SignatureComparer.Default.Equals(method.Signature, constructor.Signature)) ||
                owner.TryGetRestoredBody(source) != null || owner.TryGetRestoredReferenceConstructor(source) != null)
            {
                entry.Set("signature-rejected", "tilemap-constructor-collision",
                    "A constructor, initializer or restored constructor mapping already exists.");
                return false;
            }

            var initializer = new MethodDefinition(name, MethodAttributes.Family | MethodAttributes.HideBySig,
                MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void))
            {
                CilMethodBody = new CilMethodBody()
            };
            InteropMethodDeclarations.AddParameter(initializer, tilemap, constructor.Parameters[0].Name,
                constructor.Parameters[0].Definition?.Attributes ?? default);
            owner.NewType.Methods.Add(constructor);
            owner.NewType.Methods.Add(initializer);
            owner.AddRestoredReferenceConstructor(source, constructor, initializer);
            owner.AddRestoredBody(source, constructor);
            referenceConstructorBodies.Add(constructor, initializer);
            pendingBodies.Add((source, initializer, owner, imports, entry));
            entry.Set("body-pending");

            var body = constructorBody.Instructions;
            body.Add(CilOpCodes.Ldarg_0);
            // The empty native constructor's wrapper allocates and roots the object exactly once.
            body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(nativeDefault));
            body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Ldarg, constructor.Parameters[0]);
            body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(initializer));
            body.Add(CilOpCodes.Ret);
            return true;
        }
    }
}
