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

namespace Modstoke.Preloader
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
                    parameterType is not ByReferenceTypeSignature && InteropTypeReferences.IsValueType(parameterType)
                        ? parameterType : corlib.IntPtr,
                    parameter.Name, parameter.Definition.Attributes & ~ParameterAttributes.Optional);
            }
            return type;
        }

        internal static FieldDefinition AddInitializer(TypeDefinition enclosingType, TypeDefinition delegateType,
            MethodDefinition original, InteropRuntimeReferences imports)
        {
            var field = CreateDelegateField(enclosingType, delegateType);
            var body = enclosingType.GetOrCreateStaticConstructor().CilMethodBody.Instructions;
            body.Remove(body.Last());
            body.Add(CilOpCodes.Ldstr, original.DeclaringType.FullName + "::" + (string)original.Name);
            var resolve = InteropAnimationCallBoundary.ResolveInitializer(original, delegateType, imports);
            body.Add(CilOpCodes.Call, enclosingType.DeclaringModule.DefaultImporter.ImportMethod(resolve));
            body.Add(CilOpCodes.Stsfld, field);
            body.Add(CilOpCodes.Ret);
            return field;
        }

        internal static FieldDefinition CreateDelegateField(TypeDefinition enclosingType, TypeDefinition delegateType)
        {
            var field = new FieldDefinition(delegateType.Name + "Field",
                FieldAttributes.Static | FieldAttributes.Private | FieldAttributes.InitOnly,
                new FieldSignature(delegateType.ToTypeSignature()));
            enclosingType.Fields.Add(field);
            return field;
        }

        internal static void PrependInitializer(FieldDefinition field, TypeDefinition delegateType,
            MethodDefinition original, InteropRuntimeReferences imports)
        {
            var enclosingType = field.DeclaringType;
            var body = enclosingType.GetOrCreateStaticConstructor().CilMethodBody.Instructions;
            var resolve = InteropAnimationCallBoundary.ResolveInitializer(original, delegateType, imports);
            body.Insert(0, new CilInstruction(CilOpCodes.Stsfld, field));
            body.Insert(0, new CilInstruction(CilOpCodes.Call, enclosingType.DeclaringModule.DefaultImporter.ImportMethod(resolve)));
            body.Insert(0, new CilInstruction(CilOpCodes.Ldstr, original.DeclaringType.FullName + "::" + (string)original.Name));
            // Run before source initialization, which may itself call a restored native static method.
            if (body.Count == 3) body.Add(CilOpCodes.Ret);
        }

        internal static HashSet<int> FillBody(MethodDefinition original, MethodDefinition target, FieldDefinition delegateField,
            TypeDefinition delegateType, InteropTypeContext enclosingType, InteropRuntimeReferences imports,
            bool[] nativeArguments = null, MethodDefinition parameterTemplate = null)
        {
            var body = target.CilMethodBody.Instructions;
            var valuePins = new InteropNativeValueArgumentPins(target, imports);
            HashSet<int> inPlaceValueReferences = null;
            var parameterOffset = nativeArguments != null && !original.IsStatic ? 1 : 0;
            if (parameterOffset != 0) valuePins.RetainReference(target.Parameters[0]);
            var scope = nativeArguments == null ? null :
                valuePins.CreateReferenceScope(nativeArguments.Count(value => value));
            var writebacks = new List<(Parameter Parameter, int ArgumentIndex, CilLocalVariable Pointer)>();
            var invoke = delegateType.Methods.Single(method => method.Name == "Invoke");
            var jobObservation = InteropJobHandleObservation.TryCreate(original, target, enclosingType, imports, valuePins,
                directCall: true);
            body.Add(CilOpCodes.Ldsfld, delegateField);
            if (!original.IsStatic)
            {
                body.Add(CilOpCodes.Ldarg_0);
                body.Add(CilOpCodes.Call, imports.IL2CPP_Il2CppObjectBaseToPtrNotNull.Value);
            }

            var argumentOffset = original.IsStatic ? 0 : 1;
            for (var i = 0; i < original.Parameters.Count; i++)
            {
                var parameter = target.Parameters[i + parameterOffset];
                var type = parameter.ParameterType;
                if (nativeArguments != null && nativeArguments[i])
                {
                    var element = ((ByReferenceTypeSignature)parameterTemplate.Parameters[i].ParameterType).BaseType;
                    body.Add(CilOpCodes.Ldloc, scope);
                    body.Add(CilOpCodes.Ldarg, parameter);
                    body.Add(CilOpCodes.Call, ((IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(
                        typeof(InteropNativeReferenceValue).GetMethod(nameof(InteropNativeReferenceValue.ArgumentAddress))))
                        .MakeGenericInstanceMethod(element));
                    jobObservation?.RecordDirectArgument(i, invoke.Signature.ParameterTypes[i + argumentOffset]);
                    continue;
                }
                if (valuePins.TryEmit(original.Parameters[i].ParameterType, parameter))
                {
                    if (nativeArguments == null && original.Parameters[i].ParameterType is ByReferenceTypeSignature valueReference &&
                        InteropTypeReferences.IsValueType(valueReference.BaseType))
                        (inPlaceValueReferences ??= new()).Add(i);
                    jobObservation?.RecordDirectArgument(i, invoke.Signature.ParameterTypes[i + argumentOffset]);
                    continue;
                }
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
                jobObservation?.RecordDirectArgument(i, invoke.Signature.ParameterTypes[i + argumentOffset]);
            }

            jobObservation?.CaptureDirectInvocation();
            valuePins.MarkInvocation();
            body.Add(CilOpCodes.Call, invoke);
            if (jobObservation != null)
            {
                CilLocalVariable result = null;
                if (invoke.Signature.ReturnType.ElementType != ElementType.Void)
                {
                    result = new CilLocalVariable(invoke.Signature.ReturnType);
                    target.CilMethodBody.LocalVariables.Add(result);
                    body.Add(CilOpCodes.Stloc, result);
                }
                jobObservation.DirectInvocationSucceeded(result);
                if (result != null) body.Add(CilOpCodes.Ldloc, result);
            }
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
            valuePins.Complete();
            return inPlaceValueReferences;
        }
    }
}
