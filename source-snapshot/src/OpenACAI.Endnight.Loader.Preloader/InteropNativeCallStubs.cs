// SPDX-License-Identifier: GPL-3.0-or-later
// Native-call stubs adapted from Il2CppInterop UnstripGenerator (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Collections;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static class InteropNativeCallStubs
    {
        internal static TypeDefinition CreateDelegateType(MethodDefinition original, MethodDefinition target,
            InteropRuntimeReferences imports)
        {
            var module = imports.Module;
            var corlib = module.CorLibTypeFactory;
            var delegateBase = module.DefaultImporter.ImportTypeSignature(typeof(MulticastDelegate)).ToTypeDefOrRef();
            var type = new TypeDefinition("", original.Name + "Delegate",
                TypeAttributes.Sealed | TypeAttributes.NestedPrivate, delegateBase);
            type.Methods.Add(new MethodDefinition(".ctor",
                MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RuntimeSpecialName |
                MethodAttributes.Public, MethodSignature.CreateInstance(corlib.Void, corlib.Object, corlib.IntPtr))
            {
                ImplAttributes = MethodImplAttributes.Runtime
            });

            var returnType = target.Signature.ReturnType;
            var invoke = new MethodDefinition("Invoke",
                MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.Public,
                MethodSignature.CreateInstance(InteropTypeReferences.IsValueType(returnType) ? returnType : corlib.IntPtr))
            {
                ImplAttributes = MethodImplAttributes.Runtime
            };
            type.Methods.Add(invoke);
            if (!target.IsStatic)
                InteropMethodDeclarations.AddParameter(invoke, corlib.IntPtr, "@this", default);
            foreach (var parameter in target.Parameters)
            {
                var parameterType = parameter.ParameterType;
                InteropMethodDeclarations.AddParameter(invoke,
                    InteropTypeReferences.IsValueType(parameterType) ? parameterType : corlib.IntPtr,
                    parameter.Name, parameter.Definition.Attributes & ~ParameterAttributes.Optional);
            }
            return type;
        }

        internal static FieldDefinition AddInitializer(TypeDefinition enclosingType, TypeDefinition delegateType,
            MethodDefinition original, InteropRuntimeReferences imports)
        {
            var field = new FieldDefinition(delegateType.Name + "Field",
                FieldAttributes.Static | FieldAttributes.Private | FieldAttributes.InitOnly,
                new FieldSignature(delegateType.ToTypeSignature()));
            enclosingType.Fields.Add(field);

            var body = enclosingType.GetOrCreateStaticConstructor().CilMethodBody.Instructions;
            body.Remove(body.Last());
            body.Add(CilOpCodes.Ldstr, original.DeclaringType.FullName + "::" + (string)original.Name);
            var resolve = imports.IL2CPP_ResolveICall.Value.MakeGenericInstanceMethod(delegateType.ToTypeSignature());
            body.Add(CilOpCodes.Call, enclosingType.DeclaringModule.DefaultImporter.ImportMethod(resolve));
            body.Add(CilOpCodes.Stsfld, field);
            body.Add(CilOpCodes.Ret);
            return field;
        }

        internal static void FillBody(MethodDefinition target, FieldDefinition delegateField,
            TypeDefinition delegateType, InteropTypeContext enclosingType, InteropRuntimeReferences imports)
        {
            var body = target.CilMethodBody.Instructions;
            var writebacks = new List<(Parameter Parameter, int ArgumentIndex, CilLocalVariable Pointer)>();
            body.Add(CilOpCodes.Ldsfld, delegateField);
            if (!target.IsStatic)
            {
                body.Add(CilOpCodes.Ldarg_0);
                body.Add(CilOpCodes.Call, imports.IL2CPP_Il2CppObjectBaseToPtrNotNull.Value);
            }

            var argumentOffset = target.IsStatic ? 0 : 1;
            for (var i = 0; i < target.Parameters.Count; i++)
            {
                var parameter = target.Parameters[i];
                var type = parameter.ParameterType;
                if (InteropTypeReferences.IsValueType(type) ||
                    (type is ByReferenceTypeSignature reference && InteropTypeReferences.IsValueType(reference.BaseType)))
                    body.Add(CilOpCodes.Ldarg, parameter);
                else
                {
                    InteropNativeValues.EmitObjectToPointer(body, type, type, enclosingType, i + argumentOffset,
                        false, true, true, true, out var pointer);
                    if (pointer != null)
                        writebacks.Add((parameter, i + argumentOffset, pointer));
                }
            }

            var invoke = delegateType.Methods.Single(method => method.Name == "Invoke");
            body.Add(CilOpCodes.Call, invoke);
            if (writebacks.Count != 0)
            {
                // Writeback emits calls and branches, so keep the native result in a typed local.
                CilLocalVariable result = null;
                if (invoke.Signature.ReturnType.ElementType != ElementType.Void)
                {
                    result = new CilLocalVariable(invoke.Signature.ReturnType);
                    target.CilMethodBody.LocalVariables.Add(result);
                    body.Add(CilOpCodes.Stloc, result);
                }
                foreach (var writeback in writebacks)
                    InteropNativeValues.EmitUpdateRef(body, writeback.Parameter, writeback.ArgumentIndex, writeback.Pointer, imports);
                if (result != null)
                    body.Add(CilOpCodes.Ldloc, result);
            }

            var returnType = target.Signature.ReturnType;
            if (!returnType.IsValueType && returnType is not PointerTypeSignature and not ByReferenceTypeSignature)
            {
                var pointer = new CilLocalVariable(imports.Module.CorLibTypeFactory.IntPtr);
                target.CilMethodBody.LocalVariables.Add(pointer);
                body.Add(CilOpCodes.Stloc, pointer);
                InteropNativeValues.EmitPointerToObject(body, returnType, returnType, enclosingType, pointer, false, false);
            }
            body.Add(CilOpCodes.Ret);
        }
    }
}
