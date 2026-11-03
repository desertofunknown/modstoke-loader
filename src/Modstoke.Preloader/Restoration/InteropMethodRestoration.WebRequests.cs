// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodRestoration
    {
        private bool TryRestoreWebRequestConstructor(MethodDefinition source, MethodDefinition constructor,
            InteropTypeContext owner, InteropRuntimeReferences imports, InteropUnstripTypeResolver resolver,
            InteropRestorationEntry entry)
        {
            entry.Set("signature-rejected", "web-request-owned-constructor-contract",
                "The request constructor requires its original creation prefix, native defaults, setters and cleanup methods.");
            if (!InteropWebRequestConstruction.TryGetPointerConstructor(source, owner, imports, resolver, out var pointerConstructor) ||
                !IsReferenceConstructorDeclaration(constructor) || constructor.Attributes != source.Attributes ||
                !SignatureComparer.Default.Equals(constructor.Signature, MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void,
                    source.Signature.ParameterTypes.Select(resolver.Resolve).ToArray())) ||
                constructor.CilMethodBody is not { } constructorBody || constructorBody.Instructions.Count != 0 ||
                constructorBody.LocalVariables.Count != 0 || constructorBody.ExceptionHandlers.Count != 0 ||
                owner.TryGetRestoredReferenceConstructor(source) != null || owner.TryGetRestoredBody(source) != null) return false;

            var originalBody = source.CilMethodBody;
            var tail = new CilMethodBody();
            foreach (var instruction in originalBody.Instructions.Skip(7))
                tail.Instructions.Add(new CilInstruction(instruction.OpCode, instruction.Operand));
            try
            {
                // Defaults and setters retain the source order and native exception behavior after ownership is established.
                source.CilMethodBody = tail;
                if (!InteropMethodTranslator.TranslateMethod(source, constructor, owner, imports, out var failure))
                {
                    entry.Set("body-rejected", failure?.Reason, failure?.Detail);
                    entry.Failure = failure;
                    return false;
                }
            }
            finally { source.CilMethodBody = originalBody; }

            var importer = imports.Module.DefaultImporter;
            var initialize = importer.ImportMethod(typeof(InteropWebRequestSupport)
                .GetMethod(nameof(InteropWebRequestSupport.InitializeOwned)));
            var body = constructor.CilMethodBody.Instructions;
            body.Insert(0, new CilInstruction(CilOpCodes.Ldarg_0));
            body.Insert(1, new CilInstruction(CilOpCodes.Call, initialize));
            RetainEventTraceArguments(constructor, imports);
            CilInstruction[] prefix =
            [
                new(CilOpCodes.Ldarg_0),
                new(CilOpCodes.Ldsfld, owner.ClassPointerFieldRef),
                new(CilOpCodes.Call, imports.IL2CPP_il2cpp_object_new.Value),
                new(CilOpCodes.Call, importer.ImportMethod(pointerConstructor))
            ];
            for (var index = 0; index < prefix.Length; index++) body.Insert(index, prefix[index]);
            owner.NewType.Methods.Add(constructor);
            owner.AddRestoredReferenceConstructor(source, constructor);
            owner.AddRestoredBody(source, constructor);
            completedBodies.Add((constructor, entry));
            entry.Set("native-web-request-constructor-adapter");
            return true;
        }
    }
}
