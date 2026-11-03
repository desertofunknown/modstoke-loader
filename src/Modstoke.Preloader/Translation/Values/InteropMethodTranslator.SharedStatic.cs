// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Il2CppInterop.Runtime.Injection;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private sealed record SharedStaticFactory(TypeSignature Storage, TypeSignature Value,
            TypeSignature[] Contexts, bool[] ManagedContexts);

        private readonly Dictionary<CilInstruction, SharedStaticFactory> sharedStaticFactories =
            new(ReferenceEqualityComparer.Instance);

        private bool FindSharedStaticFactories(CilMethodBody source)
        {
            var plan = InteropRestorationPlan.For(context);
            for (var index = 0; index < source.Instructions.Count; index++)
            {
                var instruction = source.Instructions[index];
                if (instruction.OpCode.Code != CilCode.Call || instruction.Operand is not IMethodDescriptor method ||
                    !InteropSharedStaticSupport.TryDescribeFactoryCall(method, out var storage, out var contexts))
                    continue;
                var managed = contexts.Select(plan.IsManagedOnlyDefinition).ToArray();
                if (!managed.Any(value => value)) continue;
                currentInstruction = instruction;
                if (index > 0 && IsAddressPrefix(source.Instructions[index - 1].OpCode.Code))
                    return Fail("shared-static-factory-prefix", "A prefixed factory call cannot expand into a managed cache bridge.", instruction);
                var owner = FindMappedSourceOwner(storage);
                var identityReason = plan.GetNativeIdentityRejection(storage);
                if (owner?.OriginalType == null || owner.NewType.IsValueType || identityReason != null)
                    return Fail("shared-static-native-storage", identityReason ??
                        "SharedStatic storage must retain its existing native wrapper identity.", instruction);
                if (!ValidateManagedGenericUse(storage)) return false;
                var rewrittenStorage = resolver.Resolve(storage);
                var rewrittenValue = resolver.Resolve(storage.TypeArguments[0]);
                if (rewrittenStorage == null || rewrittenValue == null)
                    return Fail("shared-static-storage-unresolved", "The native storage or payload type could not be resolved.", instruction);
                var rewrittenContexts = new TypeSignature[contexts.Length];
                for (var contextIndex = 0; contextIndex < contexts.Length; contextIndex++)
                {
                    var sourceContext = contexts[contextIndex];
                    if (!ValidateManagedGenericUse(sourceContext)) return false;
                    if (!managed[contextIndex] && plan.GetNativeIdentityRejection(sourceContext) is string reason)
                        return Fail("shared-static-context-identity", reason, instruction);
                    rewrittenContexts[contextIndex] = resolver.Resolve(sourceContext);
                    if (rewrittenContexts[contextIndex] == null)
                        return Fail("shared-static-context-unresolved", "The shared cache context could not be resolved.", instruction);
                }
                if (!TryGetStackValue(instruction, 0, out var alignment) || alignment.IsNull ||
                    NumericStackValue(alignment.Type).Type?.ElementType != ElementType.I4)
                    return Fail("shared-static-alignment-unproven", "SharedStatic alignment must be a proven 32-bit stack value.", instruction);
                sharedStaticFactories.Add(instruction,
                    new SharedStaticFactory(rewrittenStorage, rewrittenValue, rewrittenContexts, managed));
            }
            return true;
        }

        private bool TryTranslateSharedStaticFactory(CilInstruction instruction, out CilInstruction translated)
        {
            translated = null;
            if (!sharedStaticFactories.TryGetValue(instruction, out var factory)) return false;
            var alignment = new CilLocalVariable(imports.Module.CorLibTypeFactory.UInt32);
            target.CilMethodBody.LocalVariables.Add(alignment);
            // Keep the caller's evaluated alignment and any lower stack values. Labels and EH
            // boundaries for the original call map to this first instruction of the expansion.
            translated = body.Add(CilOpCodes.Stloc, alignment);
            var importer = imports.Module.DefaultImporter;
            for (var index = 0; index < factory.Contexts.Length; index++)
            {
                var name = factory.ManagedContexts[index] ? nameof(NativeSharedMemory.GetManagedContextHash) :
                    nameof(NativeSharedMemory.GetNativeContextHash);
                var hash = (IMethodDefOrRef)importer.ImportMethod(typeof(NativeSharedMemory).GetMethod(name));
                body.Add(CilOpCodes.Call, hash.MakeGenericInstanceMethod(factory.Contexts[index]));
            }
            if (factory.Contexts.Length == 1) body.Add(CilOpCodes.Ldc_I8, 0L);
            body.Add(CilOpCodes.Ldloc, alignment);
            var create = (IMethodDefOrRef)importer.ImportMethod(typeof(NativeSharedMemory)
                .GetMethod(nameof(NativeSharedMemory.CreateSharedStatic)));
            body.Add(CilOpCodes.Call, create.MakeGenericInstanceMethod(factory.Storage, factory.Value));
            return true;
        }
    }
}
