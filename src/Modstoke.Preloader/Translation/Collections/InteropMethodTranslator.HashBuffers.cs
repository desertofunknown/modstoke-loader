// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private FixedNativeArrayBuffer FindFixedHashArrayBuffer(CilMethodBody original)
        {
            var signature = sourceMethod.Signature;
            if (sourceMethod.Name != "ComputeHash128" || !sourceMethod.IsStatic || sourceMethod.GenericParameters.Count != 0 ||
                sourceMethod.DeclaringType.FullName != "UnityEngine.HashUtilities" ||
                sourceMethod.DeclaringModule.Assembly.Name != "UnityEngine.CoreModule" ||
                !InteropDelegateContracts.HasStandardConvention(signature) ||
                signature.ReturnType.ElementType != ElementType.Void || signature.ParameterTypes.Count != 2 ||
                signature.ParameterTypes[0] is not SzArrayTypeSignature { BaseType.ElementType: ElementType.U1 } array ||
                signature.ParameterTypes[1] is not ByReferenceTypeSignature output ||
                output.BaseType.FullName != "UnityEngine.Hash128" ||
                InteropRestoredTypeSupport.GetAssemblyName(output.BaseType) != "UnityEngine.CoreModule" ||
                original.ExceptionHandlers.Count != 0 || original.LocalVariables.Count != 4 ||
                (sourceMethod.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.Synchronized)) != MethodImplAttributes.IL)
                return null;

            // Match the whole fixed scope: one acquisition, one hash call, then release.
            var source = original.Instructions.Where(instruction => instruction.OpCode.Code != CilCode.Nop).ToArray();
            CilCode[] pattern =
            {
                CilCode.Ldarg, CilCode.Ldc_I4, CilCode.Ldelema, CilCode.Stloc, CilCode.Ldloc, CilCode.Conv_U,
                CilCode.Stloc, CilCode.Ldarg, CilCode.Ldlen, CilCode.Conv_I4, CilCode.Conv_I8, CilCode.Stloc,
                CilCode.Ldarg, CilCode.Call, CilCode.Stloc, CilCode.Ldloc, CilCode.Ldloc, CilCode.Ldloc,
                CilCode.Call, CilCode.Ldc_I4, CilCode.Conv_U, CilCode.Stloc, CilCode.Ret
            };
            if (source.Length != pattern.Length || source.Where((instruction, index) => instruction.OpCode.Code != pattern[index]).Any())
                return null;
            var corlib = sourceMethod.DeclaringModule.CorLibTypeFactory;
            if (!LocalType(0, array.BaseType.MakePointerType()) ||
                !LocalType(1, array.BaseType.MakeByReferenceType().MakePinnedType()) ||
                !LocalType(2, corlib.UInt64) || !LocalType(3, output.BaseType.MakePointerType()) ||
                !Local(0, 6, 15) || !Local(1, 3, 4, 21) || !Local(2, 11, 16) || !Local(3, 14, 17) ||
                !ReferenceEquals(source[0].Operand, sourceMethod.Parameters[0]) ||
                !ReferenceEquals(source[7].Operand, sourceMethod.Parameters[0]) ||
                !ReferenceEquals(source[12].Operand, sourceMethod.Parameters[1]) ||
                source[1].Operand is not int zero1 || zero1 != 0 || source[19].Operand is not int zero2 || zero2 != 0 ||
                source[2].Operand is not ITypeDefOrRef addressed ||
                !SignatureComparer.Default.Equals(addressed.ToTypeSignature(), array.BaseType) ||
                source[13].Operand is not IMethodDescriptor address || !TryGetValueAddressIntrinsic(address, out var valueType) ||
                !SignatureComparer.Default.Equals(valueType, output.BaseType) ||
                source[18].Operand is not IMethodDescriptor hash || !IsHashBufferConsumer(hash, output.BaseType))
                return null;

            var element = resolver.Resolve(array.BaseType);
            var rewrittenOutput = resolver.Resolve(output.BaseType);
            var rewrittenArray = resolver.Resolve(array);
            if (element?.ElementType != ElementType.U1 || rewrittenOutput == null ||
                !HasStackAddressLayout(output.BaseType, rewrittenOutput, new HashSet<TypeDefinition>()) ||
                !SignatureComparer.Default.Equals(GetEmittedAddressReceiverType(source[12]), rewrittenOutput.MakeByReferenceType()) ||
                rewrittenArray is not GenericInstanceTypeSignature vector || vector.TypeArguments.Count != 1 ||
                !SignatureComparer.Default.Equals(vector.GenericType, imports.Il2CppStructArray.ToTypeDefOrRef()) ||
                !SignatureComparer.Default.Equals(vector.TypeArguments[0], element))
                return null;

            var plan = new FixedNativeArrayBuffer
            {
                Producer = source[2], PointerConversion = source[5], SourcePin = original.LocalVariables[1],
                OwnerType = rewrittenArray, Element = element,
                OutputAddress = source[13], OutputElement = rewrittenOutput
            };
            plan.Clears.Add(source[21]);
            return plan;

            bool LocalType(int index, TypeSignature expected) =>
                SignatureComparer.Default.Equals(original.LocalVariables[index].VariableType, expected);
            bool Local(int index, params int[] positions) =>
                positions.All(position => ReferenceEquals(source[position].Operand, original.LocalVariables[index]));
        }

        private bool IsHashBufferConsumer(IMethodDescriptor method, TypeSignature hashType)
        {
            var corlib = sourceMethod.DeclaringModule.CorLibTypeFactory;
            return method.Name == "ComputeHash128" && method.DeclaringType.FullName == "UnityEngine.HashUnsafeUtilities" &&
                InteropRestoredTypeSupport.GetAssemblyName(method.DeclaringType.ToTypeSignature()) == "UnityEngine.CoreModule" &&
                SignatureComparer.Default.Equals(method.Signature, MethodSignature.CreateStatic(corlib.Void,
                    corlib.Void.MakePointerType(), corlib.UInt64, hashType.MakePointerType()));
        }
    }
}
