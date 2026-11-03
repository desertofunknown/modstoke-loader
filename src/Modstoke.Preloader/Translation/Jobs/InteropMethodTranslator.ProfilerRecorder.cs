// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private bool TryTranslateProfilerRecorderArray(CilMethodBody original)
        {
            if (sourceMethod.Name != "ToArray" || sourceMethod.IsStatic || sourceMethod.GenericParameters.Count != 0 ||
                sourceMethod.Signature.ParameterTypes.Count != 0 ||
                sourceMethod.DeclaringModule.Assembly.Name != "UnityEngine.CoreModule" ||
                sourceMethod.DeclaringType.FullName != "Unity.Profiling.ProfilerRecorder" ||
                ownerType.OriginalType == null || ownerType.ComputedTypeSpecifics != InteropTypeContext.TypeSpecifics.BlittableStruct ||
                sourceMethod.Signature.ReturnType is not SzArrayTypeSignature sourceArray ||
                sourceArray.BaseType.FullName != "Unity.Profiling.ProfilerRecorderSample" ||
                original.ExceptionHandlers.Count != 0 || original.LocalVariables.Count != 5)
                return false;
            var sample = resolver.Resolve(sourceArray.BaseType);
            if (sample == null || !sample.IsValueType || sample.Resolve() is not { } sampleDefinition ||
                context.GetContextForNewType(sampleDefinition).ComputedTypeSpecifics != InteropTypeContext.TypeSpecifics.BlittableStruct ||
                !SignatureComparer.Default.Equals(target.Signature.ReturnType, resolver.Resolve(sourceArray))) return false;

            // Match the source's complete fixed-buffer flow. Other pointer lifetimes stay on the normal translator.
            var source = original.Instructions.Where(instruction => instruction.OpCode.Code != CilCode.Nop).ToArray();
            CilCode[] pattern =
            {
                CilCode.Ldarg, CilCode.Call, CilCode.Ldarg, CilCode.Call, CilCode.Stloc, CilCode.Ldloc,
                CilCode.Newarr, CilCode.Stloc, CilCode.Ldloc, CilCode.Dup, CilCode.Stloc, CilCode.Brfalse,
                CilCode.Ldloc, CilCode.Ldlen, CilCode.Conv_I4, CilCode.Brtrue, CilCode.Ldc_I4, CilCode.Conv_U,
                CilCode.Stloc, CilCode.Br, CilCode.Ldloc, CilCode.Ldc_I4, CilCode.Ldelema, CilCode.Conv_U,
                CilCode.Stloc, CilCode.Ldarg, CilCode.Ldobj, CilCode.Ldloc, CilCode.Ldloc, CilCode.Ldc_I4,
                CilCode.Call, CilCode.Pop, CilCode.Ldnull, CilCode.Stloc, CilCode.Ldloc, CilCode.Stloc,
                CilCode.Br, CilCode.Ldloc, CilCode.Ret
            };
            if (source.Length != pattern.Length || source.Where((instruction, index) => instruction.OpCode.Code != pattern[index]).Any())
                return false;
            if (!LocalType(0, sourceMethod.DeclaringModule.CorLibTypeFactory.Int32) ||
                !LocalType(1, sourceArray) || !LocalType(2, sourceArray.BaseType.MakePointerType()) ||
                !LocalType(3, sourceArray.MakePinnedType()) || !LocalType(4, sourceArray) ||
                !Local(0, 4, 5, 28) || !Local(1, 7, 8, 34) || !Local(2, 18, 24, 27) ||
                !Local(3, 10, 12, 20, 33) || !Local(4, 35, 37) ||
                !ReferenceEquals(source[0].Operand, sourceMethod.Parameters.ThisParameter) ||
                !ReferenceEquals(source[2].Operand, sourceMethod.Parameters.ThisParameter) ||
                !ReferenceEquals(source[25].Operand, sourceMethod.Parameters.ThisParameter) ||
                source[16].Operand is not int zero1 || zero1 != 0 ||
                source[21].Operand is not int zero2 || zero2 != 0 ||
                source[29].Operand is not int reset || reset != 0 ||
                !TypeOperand(6, sourceArray.BaseType) || !TypeOperand(22, sourceArray.BaseType) ||
                !TypeOperand(26, sourceMethod.DeclaringType.ToTypeSignature()) ||
                !Branch(11, 16) || !Branch(15, 20) || !Branch(19, 25) || !Branch(36, 37)) return false;
            var corlib = sourceMethod.DeclaringModule.CorLibTypeFactory;
            if (!Bind(1, "CheckInitializedAndThrow", MethodSignature.CreateInstance(corlib.Void), out var check) ||
                !Bind(3, "get_Count", MethodSignature.CreateInstance(corlib.Int32), out var countMethod) ||
                !Bind(30, "CopyTo_Pointer", MethodSignature.CreateStatic(corlib.Int32,
                    sourceMethod.DeclaringType.ToTypeSignature(), sourceArray.BaseType.MakePointerType(),
                    corlib.Int32, corlib.Boolean), out var copy)) return false;

            target.CilMethodBody.LocalVariables.Clear();
            target.CilMethodBody.InitializeLocals = true;
            var count = AddArrayLocal(imports.Module.CorLibTypeFactory.Int32);
            var array = AddArrayLocal(target.Signature.ReturnType);
            var handle = AddArrayLocal(imports.Module.CorLibTypeFactory.IntPtr);
            var pointer = AddArrayLocal(imports.Module.CorLibTypeFactory.IntPtr);
            var importer = imports.Module.DefaultImporter;
            body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Call, check);
            body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Call, countMethod);
            body.Add(CilOpCodes.Stloc, count);
            body.Add(CilOpCodes.Ldloc, count);
            body.Add(CilOpCodes.Conv_I8);
            body.Add(CilOpCodes.Newobj, importer.ImportMethod(imports.Il2CppStructArrayctor_size.Get(sample)));
            body.Add(CilOpCodes.Stloc, array);
            var start = new CilInstructionLabel(body.Add(CilOpCodes.Ldloc, array));
            body.Add(CilOpCodes.Ldloca, pointer);
            body.Add(CilOpCodes.Call, ((IMethodDefOrRef)importer.ImportMethod(
                typeof(InteropNativeArrayBuffer).GetMethod(nameof(InteropNativeArrayBuffer.Pin)))).MakeGenericInstanceMethod(sample));
            body.Add(CilOpCodes.Stloc, handle);
            body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Ldobj, target.DeclaringType);
            body.Add(CilOpCodes.Ldloc, pointer);
            body.Add(CilOpCodes.Conv_U);
            body.Add(CilOpCodes.Ldloc, count);
            body.Add(CilOpCodes.Ldc_I4_0);
            body.Add(CilOpCodes.Call, copy);
            body.Add(CilOpCodes.Pop);
            var exit = new CilInstructionLabel();
            body.Add(CilOpCodes.Leave, exit);
            var cleanup = new CilInstructionLabel(body.Add(CilOpCodes.Ldloc, handle));
            body.Add(CilOpCodes.Call, importer.ImportMethod(
                typeof(InteropNativeValueStorage).GetMethod(nameof(InteropNativeValueStorage.ReleaseArgument))));
            body.Add(CilOpCodes.Ldloc, array);
            body.Add(CilOpCodes.Call, importer.ImportMethod(typeof(GC).GetMethod(nameof(GC.KeepAlive))));
            body.Add(CilOpCodes.Endfinally);
            exit.Instruction = body.Add(CilOpCodes.Ldloc, array);
            body.Add(CilOpCodes.Ret);
            target.CilMethodBody.ExceptionHandlers.Add(new CilExceptionHandler
            {
                HandlerType = CilExceptionHandlerType.Finally, TryStart = start, TryEnd = cleanup,
                HandlerStart = cleanup, HandlerEnd = exit
            });
            return true;

            bool LocalType(int index, TypeSignature expected) =>
                SignatureComparer.Default.Equals(original.LocalVariables[index].VariableType, expected);
            bool Local(int index, params int[] positions) =>
                positions.All(position => ReferenceEquals(source[position].Operand, original.LocalVariables[index]));
            bool TypeOperand(int index, TypeSignature expected) => source[index].Operand is ITypeDefOrRef type &&
                SignatureComparer.Default.Equals(type.ToTypeSignature(), expected);
            bool Branch(int index, int destination)
            {
                if (source[index].Operand is not CilInstructionLabel { Instruction: not null } label) return false;
                var position = original.Instructions.IndexOf(label.Instruction);
                if (position < 0) return false;
                while (position < original.Instructions.Count && original.Instructions[position].OpCode.Code == CilCode.Nop) position++;
                return position < original.Instructions.Count && ReferenceEquals(original.Instructions[position], source[destination]);
            }
            bool Bind(int index, string name, MethodSignature signature, out IMethodDescriptor rewritten)
            {
                rewritten = null;
                if (source[index].Operand is not IMethodDescriptor method || method is MethodSpecification ||
                    method.Name != name || !SignatureComparer.Default.Equals(method.DeclaringType, sourceMethod.DeclaringType) ||
                    !SignatureComparer.Default.Equals(method.Signature, signature)) return false;
                var resolved = method.Resolve();
                if (resolved == null) return false;
                var mapped = ownerType.TryGetMethodByUnityAssemblyMethod(resolved)?.NewMethod;
                var expected = signature.HasThis ? MethodSignature.CreateInstance(resolver.Resolve(signature.ReturnType))
                    : MethodSignature.CreateStatic(resolver.Resolve(signature.ReturnType));
                foreach (var parameter in signature.ParameterTypes)
                {
                    var type = resolver.Resolve(parameter);
                    if (type == null) return false;
                    expected.ParameterTypes.Add(type);
                }
                mapped ??= target.DeclaringType.Methods.SingleOrDefault(candidate => candidate.Name == name &&
                    SignatureComparer.Default.Equals(candidate.Signature, expected));
                if (mapped == null || !SignatureComparer.Default.Equals(mapped.Signature, expected)) return false;
                rewritten = imports.Module.DefaultImporter.ImportMethod(mapped);
                return true;
            }
        }
    }
}
