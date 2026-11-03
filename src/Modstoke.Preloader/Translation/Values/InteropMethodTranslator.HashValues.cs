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
        private CilInstruction FindGenericHashBuffer(CilMethodBody original)
        {
            var signature = sourceMethod.Signature;
            if (sourceMethod.Name != "ComputeHash128" || !sourceMethod.IsStatic || sourceMethod.GenericParameters.Count != 1 ||
                sourceMethod.DeclaringType.FullName != "UnityEngine.HashUtilities" ||
                sourceMethod.DeclaringModule.Assembly.Name != "UnityEngine.CoreModule" ||
                !InteropDelegateContracts.HasStandardConvention(signature) || signature.GenericParameterCount != 1 ||
                signature.ReturnType.ElementType != ElementType.Void || signature.ParameterTypes.Count != 2 ||
                signature.ParameterTypes[0] is not ByReferenceTypeSignature
                    { BaseType: GenericParameterSignature { ParameterType: GenericParameterType.Method, Index: 0 } } input ||
                signature.ParameterTypes[1] is not ByReferenceTypeSignature output ||
                output.BaseType.FullName != "UnityEngine.Hash128" ||
                InteropRestoredTypeSupport.GetAssemblyName(output.BaseType) != "UnityEngine.CoreModule" ||
                original.ExceptionHandlers.Count != 0 || original.LocalVariables.Count != 3 ||
                (sourceMethod.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.Synchronized)) != MethodImplAttributes.IL)
                return null;

            var source = original.Instructions.Where(instruction => instruction.OpCode.Code != CilCode.Nop).ToArray();
            CilCode[] pattern =
            {
                CilCode.Ldarg, CilCode.Call, CilCode.Stloc, CilCode.Call, CilCode.Conv_I8, CilCode.Stloc,
                CilCode.Ldarg, CilCode.Call, CilCode.Stloc, CilCode.Ldloc, CilCode.Ldloc, CilCode.Ldloc,
                CilCode.Call, CilCode.Ret
            };
            if (source.Length != pattern.Length || source.Where((instruction, index) => instruction.OpCode.Code != pattern[index]).Any())
                return null;
            var corlib = sourceMethod.DeclaringModule.CorLibTypeFactory;
            if (!LocalType(0, corlib.Void.MakePointerType()) || !LocalType(1, corlib.UInt64) ||
                !LocalType(2, output.BaseType.MakePointerType()) ||
                !Local(0, 2, 9) || !Local(1, 5, 10) || !Local(2, 8, 11) ||
                !ReferenceEquals(source[0].Operand, sourceMethod.Parameters[0]) ||
                !ReferenceEquals(source[6].Operand, sourceMethod.Parameters[1]) ||
                source[1].Operand is not IMethodDescriptor inputAddress ||
                !TryGetValueAddressIntrinsic(inputAddress, out var inputType) ||
                !SignatureComparer.Default.Equals(inputType, input.BaseType) ||
                source[3].Operand is not IMethodDescriptor size || !IsValueSizeIntrinsic(size, input.BaseType) ||
                source[7].Operand is not IMethodDescriptor outputAddress ||
                !TryGetValueAddressIntrinsic(outputAddress, out var outputType) ||
                !SignatureComparer.Default.Equals(outputType, output.BaseType) ||
                source[12].Operand is not IMethodDescriptor consumer || !IsHashBufferConsumer(consumer, output.BaseType))
                return null;

            var rewrittenOutput = resolver.Resolve(output.BaseType);
            if (!target.IsStatic || target.GenericParameters.Count != 1 || target.Signature.GenericParameterCount != 1 ||
                !InteropDelegateContracts.HasStandardConvention(target.Signature) ||
                target.Signature.ReturnType.ElementType != ElementType.Void || target.Signature.ParameterTypes.Count != 2 ||
                !SignatureComparer.Default.Equals(target.Signature.ParameterTypes[0], resolver.Resolve(input)) ||
                rewrittenOutput == null || !HasStackAddressLayout(output.BaseType, rewrittenOutput, new HashSet<TypeDefinition>()) ||
                !SignatureComparer.Default.Equals(target.Signature.ParameterTypes[1], rewrittenOutput.MakeByReferenceType()))
                return null;
            return source[12];

            bool LocalType(int index, TypeSignature expected) =>
                SignatureComparer.Default.Equals(original.LocalVariables[index].VariableType, expected);
            bool Local(int index, params int[] positions) =>
                positions.All(position => ReferenceEquals(source[position].Operand, original.LocalVariables[index]));
        }

        private static bool IsValueSizeIntrinsic(IMethodDescriptor method, TypeSignature argument)
        {
            if (method is not MethodSpecification specification || method.Name != "SizeOf" ||
                method.DeclaringType.FullName != "Unity.Collections.LowLevel.Unsafe.UnsafeUtility" ||
                InteropRestoredTypeSupport.GetAssemblyName(method.DeclaringType.ToTypeSignature()) != "UnityEngine.CoreModule" ||
                specification.Signature?.TypeArguments.Count != 1 ||
                !SignatureComparer.Default.Equals(specification.Signature.TypeArguments[0], argument) ||
                method.Signature is not { HasThis: false, GenericParameterCount: 1 } signature ||
                !InteropDelegateContracts.HasStandardConvention(signature) || signature.ParameterTypes.Count != 0 ||
                signature.ReturnType.ElementType != ElementType.I4 ||
                method.Resolve() is not { IsStatic: true, HasMethodBody: true } definition ||
                definition.GenericParameters.Count != 1 || !SignatureComparer.Default.Equals(definition.Signature, signature) ||
                definition.CilMethodBody.ExceptionHandlers.Count != 0 ||
                (definition.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.Synchronized)) != MethodImplAttributes.IL)
                return false;
            var source = definition.CilMethodBody.Instructions.Where(instruction => instruction.OpCode.Code != CilCode.Nop).ToArray();
            return source.Length == 2 && source[0].OpCode.Code == CilCode.Sizeof &&
                source[0].Operand is ITypeDefOrRef type && type.ToTypeSignature() is
                    GenericParameterSignature { ParameterType: GenericParameterType.Method, Index: 0 } &&
                source[1].OpCode.Code == CilCode.Ret;
        }

        private CilInstruction PinHashValue(CilLocalVariable inputPin, CilLocalVariable handle,
            CilLocalVariable address, TypeSignature inputType, bool requireUnmanaged)
        {
            var importer = imports.Module.DefaultImporter;
            var clrValue = new CilInstructionLabel();
            var addressReady = new CilInstructionLabel();
            var start = body.Add(CilOpCodes.Ldarg, target.Parameters[0]);
            body.Add(CilOpCodes.Stloc, inputPin);
            CallBuffer(requireUnmanaged ? nameof(InteropGenericValueSupport.UnmanagedValueBufferKind) :
                nameof(InteropGenericValueSupport.ValueBufferKind));
            body.Add(CilOpCodes.Brfalse, clrValue);
            body.Add(CilOpCodes.Ldloc, inputPin);
            CallBuffer(nameof(InteropGenericValueSupport.PinValueArgument));
            body.Add(CilOpCodes.Stloc, handle);
            body.Add(CilOpCodes.Ldloc, handle);
            body.Add(CilOpCodes.Call, importer.ImportMethod(
                typeof(InteropNativeValueStorage).GetMethod(nameof(InteropNativeValueStorage.PinnedArgumentAddress))));
            body.Add(CilOpCodes.Br, addressReady);
            clrValue.Instruction = body.Add(CilOpCodes.Ldloc, inputPin);
            body.Add(CilOpCodes.Conv_U);
            addressReady.Instruction = body.Add(CilOpCodes.Stloc, address);
            return start;

            void CallBuffer(string name) => body.Add(CilOpCodes.Call,
                ((IMethodDefOrRef)importer.ImportMethod(typeof(InteropGenericValueSupport).GetMethod(name)))
                    .MakeGenericInstanceMethod(inputType));
        }

        private bool TranslateGenericHashBuffer(CilMethodBody original, CilInstruction hashCall)
        {
            var inputType = ((ByReferenceTypeSignature)target.Signature.ParameterTypes[0]).BaseType;
            var inputPin = AddArrayLocal(target.Signature.ParameterTypes[0].MakePinnedType());
            var outputPin = AddArrayLocal(target.Signature.ParameterTypes[1].MakePinnedType());
            var handle = AddArrayLocal(imports.Module.CorLibTypeFactory.IntPtr);
            var input = locals[original.LocalVariables[0]];
            var size = locals[original.LocalVariables[1]];
            var output = locals[original.LocalVariables[2]];
            var importer = imports.Module.DefaultImporter;
            target.CilMethodBody.InitializeLocals = true;

            var start = new CilInstructionLabel(PinHashValue(inputPin, handle, input, inputType, requireUnmanaged: false));
            CallBuffer(nameof(InteropGenericValueSupport.ValueBufferSize));
            body.Add(CilOpCodes.Conv_I8);
            body.Add(CilOpCodes.Stloc, size);
            body.Add(CilOpCodes.Ldarg, target.Parameters[1]);
            body.Add(CilOpCodes.Stloc, outputPin);
            body.Add(CilOpCodes.Ldloc, outputPin);
            body.Add(CilOpCodes.Conv_U);
            body.Add(CilOpCodes.Stloc, output);
            body.Add(CilOpCodes.Ldloc, input);
            body.Add(CilOpCodes.Ldloc, size);
            body.Add(CilOpCodes.Ldloc, output);
            currentInstruction = hashCall;
            if (TranslateValueAwareMethod(CilOpCodes.Call, (IMethodDescriptor)hashCall.Operand) == null) return false;

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
            exit.Instruction = body.Add(CilOpCodes.Ret);
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
