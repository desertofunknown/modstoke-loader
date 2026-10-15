// SPDX-License-Identifier: GPL-3.0-or-later
// Native method bodies adapted from Il2CppInterop Pass50GenerateMethods (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System.Collections.Generic;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal sealed partial class InteropMethodBodies
    {
        private readonly InteropMethodContext method;
        private readonly InteropTypeContext type;
        private readonly MethodDefinition original;
        private readonly MethodDefinition target;
        private readonly InteropRuntimeReferences imports;
        private readonly CilInstructionCollection body;
        private readonly CilLocalVariable nativeException;
        private readonly CilLocalVariable arguments;
        private readonly CilLocalVariable nativeResult;
        private readonly int argumentOffset;
        private readonly List<(int Index, CilLocalVariable Pointer)> writebacks = new();

        private InteropMethodBodies(InteropMethodContext method, InteropTypeContext type,
            InteropRuntimeReferences imports)
        {
            this.method = method;
            this.type = type;
            this.imports = imports;
            original = method.OriginalMethod;
            target = method.NewMethod;
            body = target.CilMethodBody.Instructions;
            argumentOffset = original.IsStatic ? 0 : 1;
            nativeException = new CilLocalVariable(imports.Module.CorLibTypeFactory.IntPtr);
            arguments = new CilLocalVariable(imports.Module.CorLibTypeFactory.IntPtr.MakePointerType());
            nativeResult = new CilLocalVariable(imports.Module.CorLibTypeFactory.IntPtr);
            target.CilMethodBody.LocalVariables.Add(nativeException);
            target.CilMethodBody.LocalVariables.Add(arguments);
            target.CilMethodBody.LocalVariables.Add(nativeResult);
        }

        internal static void Generate(InteropRewriteContext context)
        {
            foreach (var assembly in context.Assemblies)
                foreach (var type in assembly.Types)
                    foreach (var method in type.Methods)
                        new InteropMethodBodies(method, type, assembly.Imports).Generate();
        }

        private void Generate()
        {
            InitializeParamsArrays();
            InitializeInstance();
            AllocateArguments();
            FillArguments();
            EmitInvocation();
            WriteBackReferences();
            InteropNativeValues.EmitPointerToObject(body, original.Signature.ReturnType, target.Signature.ReturnType,
                type, nativeResult, false, true);
            body.Add(CilOpCodes.Ret);
        }

        private void InitializeInstance()
        {
            if (type.ComputedTypeSpecifics == InteropTypeContext.TypeSpecifics.BlittableStruct)
                return;
            if (original.IsConstructor)
            {
                body.Add(CilOpCodes.Ldarg_0);
                body.Add(CilOpCodes.Ldsfld, type.ClassPointerFieldRef);
                body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_object_new.Value);
                body.Add(CilOpCodes.Call, new MemberReference(type.SelfSubstitutedRef, ".ctor",
                    MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void,
                        imports.Module.CorLibTypeFactory.IntPtr)));
            }
            else if (!original.IsStatic)
            {
                body.Add(CilOpCodes.Ldarg_0);
                body.Add(CilOpCodes.Call, imports.IL2CPP_Il2CppObjectBaseToPtrNotNull.Value);
                body.Add(CilOpCodes.Pop);
            }
        }

        private void AllocateArguments()
        {
            if (original.Parameters.Count == 0)
            {
                body.Add(CilOpCodes.Ldc_I4_0);
                body.Add(CilOpCodes.Conv_U);
            }
            else
            {
                EmitPointerOffset(original.Parameters.Count);
                body.Add(CilOpCodes.Localloc);
            }
            body.Add(CilOpCodes.Stloc, arguments);
        }

        private void EmitPointerOffset(int count)
        {
            body.Add(CilOpCodes.Ldc_I4, count);
            body.Add(CilOpCodes.Conv_U);
            body.Add(CilOpCodes.Sizeof, imports.Module.CorLibTypeFactory.IntPtr.ToTypeDefOrRef());
            body.Add(CilOpCodes.Mul_Ovf_Un);
        }

        private void EmitInvocation()
        {
            var virtualDispatch = !original.DeclaringType.IsSealed && !original.IsFinal &&
                ((original.IsVirtual && !InteropTypeReferences.IsValueType(original.DeclaringType)) || original.IsAbstract);
            if (virtualDispatch)
            {
                body.Add(CilOpCodes.Ldarg_0);
                body.Add(CilOpCodes.Call, imports.IL2CPP_Il2CppObjectBaseToPtr.Value);
            }
            IFieldDescriptor methodPointer = method.GenericInstantiationsStoreSelfSubstRef != null
                ? new MemberReference(method.GenericInstantiationsStoreSelfSubstMethodRef, "Pointer",
                    new FieldSignature(imports.Module.CorLibTypeFactory.IntPtr))
                : method.NonGenericMethodInfoPointerField;
            body.Add(CilOpCodes.Ldsfld, methodPointer);
            if (virtualDispatch)
                body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_object_get_virtual_method.Value);

            if (original.IsStatic)
                body.Add(CilOpCodes.Ldc_I4_0);
            else
                InteropNativeValues.EmitObjectToPointer(body, original.DeclaringType.ToTypeSignature(), target.DeclaringType.ToTypeSignature(),
                    type, 0, true, false, true, true, out _);
            body.Add(CilOpCodes.Ldloc, arguments);
            body.Add(CilOpCodes.Ldloca, nativeException);
            body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_runtime_invoke.Value);
            body.Add(CilOpCodes.Stloc, nativeResult);
            body.Add(CilOpCodes.Ldloc, nativeException);
            body.Add(CilOpCodes.Call, imports.Il2CppException_RaiseExceptionIfNecessary.Value);
        }
    }
}
