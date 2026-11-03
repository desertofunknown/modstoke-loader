// SPDX-License-Identifier: GPL-3.0-or-later
// Native method bodies adapted from Il2CppInterop Pass50GenerateMethods (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
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
        private readonly bool initializeConstructor;
        private readonly IFieldDescriptor valueCallValidationField;
        private readonly bool[] nativeArguments;
        private readonly bool nativeReceiver;
        private readonly bool directCall;
        private readonly int parameterOffset;
        private CilLocalVariable validatedValueMethod;
        private CilLocalVariable referenceScope;
        private CilLocalVariable observedJobReceiver;
        private readonly List<(int Index, CilLocalVariable Pointer)> writebacks = new();
        private InteropNativeValueArgumentPins valuePins;

        private InteropMethodBodies(InteropMethodContext method, InteropTypeContext type,
            InteropRuntimeReferences imports, MethodDefinition initializer = null,
            IFieldDescriptor valueCallValidationField = null, bool[] nativeArguments = null, bool nativeReceiver = false,
            bool directCall = false)
        {
            this.method = method;
            this.type = type;
            this.imports = imports;
            original = method.OriginalMethod;
            target = initializer ?? method.NewMethod;
            initializeConstructor = initializer == null;
            this.valueCallValidationField = valueCallValidationField;
            this.nativeArguments = nativeArguments;
            this.nativeReceiver = nativeReceiver;
            this.directCall = directCall;
            parameterOffset = nativeArguments == null || original.IsStatic ? 0 : 1;
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
            if (InteropAtomicBridge.TryFillBody(original, target, imports)) return;
            if (nativeArguments == null && !nativeReceiver &&
                InteropSharedStaticSupport.TryFillBody(original, target, type, imports)) return;
            InitializeParamsArrays();
            InitializeInstance();
            AllocateArguments();
            valuePins = new InteropNativeValueArgumentPins(target, imports);
            valuePins.PinValueReceiver();
            if (parameterOffset != 0 && !nativeReceiver) valuePins.RetainReference(target.Parameters[0]);
            if (valueCallValidationField != null) InitializeValueCallMetadata();
            if (nativeArguments != null)
                referenceScope = valuePins.CreateReferenceScope(nativeArguments.Count(value => value) + (nativeReceiver ? 1 : 0));
            FillArguments();
            var jobObservation = InteropJobHandleObservation.TryCreate(original, target, type, imports, valuePins);
            if (jobObservation != null && !original.IsStatic)
            {
                observedJobReceiver = new CilLocalVariable(imports.Module.CorLibTypeFactory.IntPtr);
                target.CilMethodBody.LocalVariables.Add(observedJobReceiver);
                if (nativeReceiver) EmitNativeReferenceReceiver();
                else if (!valuePins.EmitValueReceiver())
                {
                    // A scoped static adapter receives the blittable receiver by value.
                    if (!target.IsStatic || parameterOffset != 1 ||
                        !InteropTypeReferences.IsValueType(target.Parameters[0].ParameterType))
                        throw new System.InvalidOperationException("Job completion observation requires a stable value receiver.");
                    body.Add(CilOpCodes.Ldarga, target.Parameters[0]);
                    body.Add(CilOpCodes.Conv_I);
                }
                body.Add(CilOpCodes.Stloc, observedJobReceiver);
            }
            jobObservation?.CaptureInvocation(arguments, () =>
            {
                body.Add(CilOpCodes.Ldloc, observedJobReceiver);
            });
            EmitInvocation();
            WriteBackReferences();
            InteropNativeValues.EmitPointerToObject(body, original.Signature.ReturnType, target.Signature.ReturnType,
                type, nativeResult, false, true);
            if (jobObservation != null)
            {
                CilLocalVariable result = null;
                if (target.Signature.ReturnType.ElementType != ElementType.Void)
                {
                    result = new CilLocalVariable(target.Signature.ReturnType);
                    target.CilMethodBody.LocalVariables.Add(result);
                    body.Add(CilOpCodes.Stloc, result);
                }
                jobObservation.InvocationSucceeded(arguments, result);
                if (result != null) body.Add(CilOpCodes.Ldloc, result);
            }
            body.Add(CilOpCodes.Ret);
            valuePins.Complete();
        }

        private void InitializeInstance()
        {
            if (type.ComputedTypeSpecifics == InteropTypeContext.TypeSpecifics.BlittableStruct || nativeReceiver)
                return;
            if (original.IsConstructor && initializeConstructor)
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

        private void InitializeValueCallMetadata()
        {
            validatedValueMethod = new CilLocalVariable(imports.Module.CorLibTypeFactory.IntPtr);
            target.CilMethodBody.LocalVariables.Add(validatedValueMethod);
            var closedGeneric = method.GenericInstantiationsStoreSelfSubstMethodRef != null;
            if (closedGeneric)
                body.Add(CilOpCodes.Ldsfld, new MemberReference(method.GenericInstantiationsStoreSelfSubstMethodRef,
                    "Pointer", new FieldSignature(imports.Module.CorLibTypeFactory.IntPtr)));
            body.Add(CilOpCodes.Ldsfld, method.NonGenericMethodInfoPointerField);
            if (!original.IsConstructor) body.Add(CilOpCodes.Ldstr, (string)original.Name);
            body.Add(CilOpCodes.Ldc_I4, original.Parameters.Count);
            body.Add(CilOpCodes.Ldsflda, valueCallValidationField);
            var validationMethod = closedGeneric ? nameof(InteropNativeValueStorage.ClosedGenericStaticMethod) :
                original.IsConstructor ? nameof(InteropNativeValueStorage.Constructor) :
                original.IsStatic ? nameof(InteropNativeValueStorage.StaticMethod) :
                InteropTypeReferences.IsValueType(original.DeclaringType)
                    ? nameof(InteropNativeValueStorage.Method) : nameof(InteropNativeValueStorage.ReferenceMethod);
            body.Add(CilOpCodes.Call, ((IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(
                typeof(InteropNativeValueStorage).GetMethod(validationMethod)))
                .MakeGenericInstanceMethod(type.SelfSubstitutedRef.ToTypeSignature()));
            body.Add(CilOpCodes.Stloc, validatedValueMethod);
        }

        private void EmitInvocation()
        {
            var virtualDispatch = !directCall && !original.DeclaringType.IsSealed && !original.IsFinal &&
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
            if (validatedValueMethod != null) body.Add(CilOpCodes.Ldloc, validatedValueMethod);
            else body.Add(CilOpCodes.Ldsfld, methodPointer);
            if (virtualDispatch)
                body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_object_get_virtual_method.Value);

            if (original.IsStatic)
                body.Add(CilOpCodes.Ldc_I4_0);
            else if (observedJobReceiver != null) body.Add(CilOpCodes.Ldloc, observedJobReceiver);
            else if (nativeReceiver) EmitNativeReferenceReceiver();
            else if (!valuePins.EmitValueReceiver())
                InteropNativeValues.EmitObjectToPointer(body, original.DeclaringType.ToTypeSignature(), target.DeclaringType.ToTypeSignature(),
                    type, 0, true, false, true, true, out _);
            body.Add(CilOpCodes.Ldloc, arguments);
            body.Add(CilOpCodes.Ldloca, nativeException);
            valuePins.MarkInvocation();
            body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_runtime_invoke.Value);
            body.Add(CilOpCodes.Stloc, nativeResult);
            body.Add(CilOpCodes.Ldloc, nativeException);
            body.Add(CilOpCodes.Call, imports.Il2CppException_RaiseExceptionIfNecessary.Value);
        }

        private void EmitNativeReferenceReceiver()
        {
            body.Add(CilOpCodes.Ldloc, referenceScope);
            body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Ldloc, validatedValueMethod);
            body.Add(CilOpCodes.Callvirt, imports.Module.DefaultImporter.ImportMethod(
                typeof(InteropNativeReferenceScope).GetMethod(nameof(InteropNativeReferenceScope.ValueReceiver))));
        }
    }
}
