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
        private CilInstruction FindHash128ValueBuffer(CilMethodBody original)
        {
            var append = sourceMethod.Name == "Append";
            var signature = sourceMethod.Signature;
            var hash = sourceMethod.DeclaringType.ToTypeSignature();
            if ((!append && sourceMethod.Name != "Compute") || sourceMethod.IsStatic == append ||
                hash.FullName != "UnityEngine.Hash128" || !hash.IsValueType ||
                sourceMethod.DeclaringModule.Assembly.Name != "UnityEngine.CoreModule" ||
                sourceMethod.DeclaringType.GenericParameters.Count != 0 || sourceMethod.GenericParameters.Count != 1 ||
                !InteropDelegateContracts.HasStandardConvention(signature) || signature.GenericParameterCount != 1 ||
                signature.ParameterTypes.Count != 1 || signature.ParameterTypes[0] is not ByReferenceTypeSignature
                    { BaseType: GenericParameterSignature { ParameterType: GenericParameterType.Method, Index: 0 } } input ||
                !(append ? signature.ReturnType.ElementType == ElementType.Void :
                    SignatureComparer.Default.Equals(signature.ReturnType, hash)) ||
                original.ExceptionHandlers.Count != 0 || original.LocalVariables.Count != (append ? 2 : 4) ||
                (sourceMethod.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.Synchronized)) != MethodImplAttributes.IL)
                return null;

            var source = original.Instructions.Where(instruction => instruction.OpCode.Code != CilCode.Nop).ToArray();
            CilCode[] pattern = append ? new[]
            {
                CilCode.Ldarg, CilCode.Stloc, CilCode.Ldloc, CilCode.Conv_U, CilCode.Stloc,
                CilCode.Ldloc, CilCode.Call, CilCode.Ldc_I4, CilCode.Ldc_I4, CilCode.Call,
                CilCode.Ldarg, CilCode.Call, CilCode.Ldc_I4, CilCode.Conv_U, CilCode.Stloc, CilCode.Ret
            } : new[]
            {
                CilCode.Ldarg, CilCode.Stloc, CilCode.Ldloc, CilCode.Conv_U, CilCode.Stloc,
                CilCode.Ldloca, CilCode.Initobj, CilCode.Ldloc, CilCode.Call, CilCode.Ldc_I4,
                CilCode.Ldc_I4, CilCode.Call, CilCode.Ldloca, CilCode.Call, CilCode.Ldloc,
                CilCode.Stloc, CilCode.Br, CilCode.Ldloc, CilCode.Ret
            };
            if (source.Length != pattern.Length || source.Where((instruction, index) => instruction.OpCode.Code != pattern[index]).Any())
                return null;
            var corlib = sourceMethod.DeclaringModule.CorLibTypeFactory;
            var pointerCall = append ? 6 : 8;
            var sizeCall = append ? 9 : 11;
            var nativeCall = append ? 11 : 13;
            if (!LocalType(0, corlib.Void.MakePointerType()) || !LocalType(1, input.MakePinnedType()) ||
                !Local(0, 4, append ? 5 : 7) || !Local(1, 1, 2) ||
                !ReferenceEquals(source[0].Operand, sourceMethod.Parameters[0]) ||
                source[pointerCall].Operand is not IMethodDescriptor pointer ||
                pointer.Name != "op_Explicit" ||
                !InteropRestoredUnmanagedDelegateSupport.IsSystemType(pointer.DeclaringType, "System.IntPtr") ||
                !SignatureComparer.Default.Equals(pointer.Signature,
                    MethodSignature.CreateStatic(corlib.IntPtr, corlib.Void.MakePointerType())) ||
                source[pointerCall + 1].Operand is not int zero || zero != 0 ||
                source[pointerCall + 2].Operand is not int one || one != 1 ||
                source[sizeCall].Operand is not IMethodDescriptor size || !IsValueSizeIntrinsic(size, input.BaseType) ||
                source[nativeCall].Operand is not IMethodDescriptor consumer || consumer.Name != "ComputeFromPtr" ||
                !SignatureComparer.Default.Equals(consumer.DeclaringType, sourceMethod.DeclaringType) ||
                !SignatureComparer.Default.Equals(consumer.Signature, MethodSignature.CreateStatic(corlib.Void,
                    corlib.IntPtr, corlib.Int32, corlib.Int32, corlib.Int32, hash.MakeByReferenceType())))
                return null;
            if (append)
            {
                if (!ReferenceEquals(source[10].Operand, sourceMethod.Parameters.ThisParameter) ||
                    source[12].Operand is not int clear || clear != 0 || !Local(1, 14)) return null;
            }
            else if (!LocalType(2, hash) || !LocalType(3, hash) || !Local(2, 5, 12, 14) || !Local(3, 15, 17) ||
                source[6].Operand is not ITypeDefOrRef initialized ||
                !SignatureComparer.Default.Equals(initialized, sourceMethod.DeclaringType) ||
                source[16].Operand is not CilInstructionLabel exit || !ReferenceEquals(exit.Instruction, source[17]))
                return null;

            var rewrittenHash = resolver.Resolve(hash);
            if (rewrittenHash == null || !HasStackAddressLayout(hash, rewrittenHash, new HashSet<TypeDefinition>()) ||
                target.IsStatic != sourceMethod.IsStatic || target.GenericParameters.Count != 1 ||
                target.Signature.GenericParameterCount != 1 || target.Signature.ParameterTypes.Count != 1 ||
                !SignatureComparer.Default.Equals(target.Signature.ParameterTypes[0], resolver.Resolve(input)) ||
                !(append ? target.Signature.ReturnType.ElementType == ElementType.Void &&
                    SignatureComparer.Default.Equals(target.Parameters.ThisParameter.ParameterType, rewrittenHash.MakeByReferenceType()) :
                    SignatureComparer.Default.Equals(target.Signature.ReturnType, rewrittenHash)))
                return null;
            return source[nativeCall];

            bool LocalType(int index, TypeSignature expected) =>
                SignatureComparer.Default.Equals(original.LocalVariables[index].VariableType, expected);
            bool Local(int index, params int[] positions) =>
                positions.All(position => ReferenceEquals(source[position].Operand, original.LocalVariables[index]));
        }

        private bool TranslateHash128ValueBuffer(CilMethodBody original, CilInstruction hashCall)
        {
            var append = !target.IsStatic;
            var inputType = ((ByReferenceTypeSignature)target.Signature.ParameterTypes[0]).BaseType;
            var hashType = target.DeclaringType.ToTypeSignature();
            var inputPin = locals[original.LocalVariables[1]];
            var outputPin = AddArrayLocal(hashType.MakeByReferenceType().MakePinnedType());
            var handle = AddArrayLocal(imports.Module.CorLibTypeFactory.IntPtr);
            var input = locals[original.LocalVariables[0]];
            var result = append ? null : locals[original.LocalVariables[2]];
            var importer = imports.Module.DefaultImporter;
            target.CilMethodBody.InitializeLocals = true;

            var start = new CilInstructionLabel(PinHashValue(inputPin, handle, input, inputType, requireUnmanaged: true));
            if (append) body.Add(CilOpCodes.Ldarg, target.Parameters.ThisParameter);
            else body.Add(CilOpCodes.Ldloca, result);
            body.Add(CilOpCodes.Stloc, outputPin);
            body.Add(CilOpCodes.Ldloc, input);
            body.Add(CilOpCodes.Conv_I);
            body.Add(CilOpCodes.Ldc_I4_0);
            body.Add(CilOpCodes.Ldc_I4_1);
            CallBuffer(nameof(InteropGenericValueSupport.ValueBufferSize));
            body.Add(CilOpCodes.Ldloc, outputPin);
            currentInstruction = hashCall;
            if (TranslateValueAwareMethod(CilOpCodes.Call, (IMethodDescriptor)hashCall.Operand) == null) return false;

            // Keep both locations pinned through native invocation, including callbacks and failure unwinding.
            var exit = new CilInstructionLabel();
            body.Add(CilOpCodes.Leave, exit);
            var cleanup = new CilInstructionLabel(body.Add(CilOpCodes.Ldloc, handle));
            body.Add(CilOpCodes.Call, importer.ImportMethod(
                typeof(InteropNativeValueStorage).GetMethod(nameof(InteropNativeValueStorage.ReleaseArgument))));
            body.Add(CilOpCodes.Ldc_I4_0);
            body.Add(CilOpCodes.Conv_U);
            body.Add(CilOpCodes.Stloc, inputPin);
            body.Add(CilOpCodes.Ldc_I4_0);
            body.Add(CilOpCodes.Conv_U);
            body.Add(CilOpCodes.Stloc, outputPin);
            body.Add(CilOpCodes.Endfinally);
            exit.Instruction = append ? body.Add(CilOpCodes.Ret) : body.Add(CilOpCodes.Ldloc, result);
            if (!append) body.Add(CilOpCodes.Ret);
            target.CilMethodBody.ExceptionHandlers.Add(new CilExceptionHandler
            {
                HandlerType = CilExceptionHandlerType.Finally, TryStart = start, TryEnd = cleanup,
                HandlerStart = cleanup, HandlerEnd = exit
            });
            return true;

            void CallBuffer(string name) => body.Add(CilOpCodes.Call,
                ((IMethodDefOrRef)importer.ImportMethod(typeof(InteropGenericValueSupport).GetMethod(name)))
                    .MakeGenericInstanceMethod(inputType));
        }
    }
}
