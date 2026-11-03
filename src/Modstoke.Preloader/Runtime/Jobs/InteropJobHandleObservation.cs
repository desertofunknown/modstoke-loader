// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Il2CppInterop.Runtime.Injection;

namespace Modstoke.Preloader
{
    // Generator state only. Native input addresses are consumed while the wrapper's pins are live.
    internal sealed class InteropJobHandleObservation
    {
        private const string HandleName = "Unity.Jobs.JobHandle";
        private enum Inputs { Receiver, Locations, Buffer, Container }
        private readonly MethodDefinition original;
        private readonly MethodDefinition target;
        private readonly InteropRuntimeReferences imports;
        private readonly CilInstructionCollection body;
        private readonly Inputs inputs;
        private readonly int inputCount;
        private readonly int outputIndex;
        private readonly bool completes;
        private readonly CilLocalVariable scope;
        private readonly CilLocalVariable[] directArguments;

        private InteropJobHandleObservation(MethodDefinition original, MethodDefinition target,
            InteropRuntimeReferences imports, InteropNativeValueArgumentPins pins,
            Inputs inputs, int inputCount, int outputIndex, bool completes)
        {
            this.original = original;
            this.target = target;
            this.imports = imports;
            this.inputs = inputs;
            this.inputCount = inputCount;
            this.outputIndex = outputIndex;
            this.completes = completes;
            body = target.CilMethodBody.Instructions;
            scope = pins.CreateJobScope();
            directArguments = new CilLocalVariable[original.Parameters.Count];
        }

        internal static InteropJobHandleObservation TryCreate(MethodDefinition original, MethodDefinition target,
            InteropTypeContext owner, InteropRuntimeReferences imports, InteropNativeValueArgumentPins pins,
            bool directCall = false)
        {
            if (original.DeclaringModule.Assembly.Name != "UnityEngine.CoreModule" ||
                original.DeclaringType.FullName != HandleName || original.GenericParameters.Count != 0 ||
                !InteropDelegateContracts.HasStandardConvention(original.Signature) ||
                original.Signature.GenericParameterCount != 0 || original.Signature.HasThis == original.IsStatic ||
                directCall && !original.IsStatic || !HandleLayout(owner.NewType)) return null;

            var parameters = original.Signature.ParameterTypes;
            var count = parameters.Count;
            var returnsVoid = original.Signature.ReturnType.ElementType == ElementType.Void;
            var returnsHandle = IsHandle(original.Signature.ReturnType);
            if (returnsHandle && !IsHandle(target.Signature.ReturnType) ||
                returnsVoid && target.Signature.ReturnType.ElementType != ElementType.Void) return null;
            Inputs inputs;
            var inputsCount = count;
            var output = -1;
            var completes = false;
            var name = (string)original.Name;
            if (!original.IsStatic)
            {
                if (name != "Complete" || count != 0 || !returnsVoid) return null;
                inputs = Inputs.Receiver;
                completes = true;
            }
            else if ((name == "ScheduleBatchedJobsAndComplete" && count == 1 ||
                      name == "CompleteAll" && count is 2 or 3) && returnsVoid &&
                     parameters.All(IsHandleReference))
            {
                inputs = Inputs.Locations;
                completes = true;
            }
            else if (name == "ScheduleBatchedJobsAndCompleteAll" && returnsVoid &&
                     count == 2 && IsBuffer(parameters[0], parameters[1]))
            {
                inputs = Inputs.Buffer;
                completes = true;
            }
            else if (count == 1 && IsContainer(parameters[0]) &&
                     (name == "CombineDependencies" && returnsHandle ||
                      name == "CompleteAll" && returnsVoid &&
                      ((GenericInstanceTypeSignature)parameters[0]).GenericType.FullName == "Unity.Collections.NativeArray`1"))
            {
                // Container icalls have a different native delegate ABI; only native-invoke wrappers use this path.
                if (directCall) return null;
                inputs = Inputs.Container;
                completes = returnsVoid;
            }
            else if (name == "CombineDependencies" && returnsHandle && count is 2 or 3 && parameters.All(IsHandle))
                inputs = Inputs.Locations;
            else if ((name == "CombineDependenciesInternal2" && count == 2 ||
                      name == "CombineDependenciesInternal3" && count == 3) && returnsHandle &&
                     parameters.All(IsHandleReference))
                inputs = Inputs.Locations;
            else if (name == "CombineDependenciesInternalPtr" && returnsHandle && count == 2 &&
                     IsBuffer(parameters[0], parameters[1]))
                inputs = Inputs.Buffer;
            else if (returnsVoid && count >= 3 && IsHandleReference(parameters[count - 1]) &&
                     original.Parameters[count - 1].Definition.IsOut)
            {
                output = count - 1;
                inputsCount = output;
                if ((name == "CombineDependenciesInternal2_Injected" && count == 3 ||
                     name == "CombineDependenciesInternal3_Injected" && count == 4) &&
                    parameters.Take(output).All(IsHandleReference)) inputs = Inputs.Locations;
                else if (name == "CombineDependenciesInternalPtr_Injected" && count == 3 &&
                         IsBuffer(parameters[0], parameters[1])) inputs = Inputs.Buffer;
                else return null;
            }
            else return null;

            if (original.Parameters.Take(inputsCount).Any(parameter => parameter.Definition.IsOut)) return null;
            return new InteropJobHandleObservation(original, target, imports, pins, inputs, inputsCount, output, completes);
        }

        private static bool IsCoreType(ITypeDefOrRef type, string name) => type != null &&
            type.FullName == name && type.Scope?.GetAssembly()?.Name == "UnityEngine.CoreModule";

        private static bool IsHandle(TypeSignature type) => type is TypeDefOrRefSignature reference &&
            reference.IsValueType && IsCoreType(reference.Type, HandleName);

        private static bool IsHandleReference(TypeSignature type) =>
            type is ByReferenceTypeSignature reference && IsHandle(reference.BaseType);

        private static bool IsBuffer(TypeSignature pointer, TypeSignature count) =>
            pointer is PointerTypeSignature { BaseType.ElementType: ElementType.Void } && count.ElementType == ElementType.I4;

        private static bool IsContainer(TypeSignature type) => type is GenericInstanceTypeSignature container &&
            container.TypeArguments.Count == 1 && IsHandle(container.TypeArguments[0]) &&
            (IsCoreType(container.GenericType, "Unity.Collections.NativeArray`1") ||
             IsCoreType(container.GenericType, "Unity.Collections.NativeSlice`1"));

        private static bool HandleLayout(TypeDefinition type)
        {
            if (!type.IsValueType || !type.IsExplicitLayout || type.FullName != HandleName) return false;
            var fields = type.Fields.Where(field => !field.IsStatic).ToArray();
            return fields.Length == 2 &&
                fields.Any(field => field.Name == "jobGroup" && field.FieldOffset == 0 && field.Signature.FieldType.ElementType == ElementType.U8) &&
                fields.Any(field => field.Name == "version" && field.FieldOffset == 8 && field.Signature.FieldType.ElementType == ElementType.I4);
        }

        internal void CaptureInvocation(CilLocalVariable arguments, Action emitReceiver) =>
            Capture(index => EmitArgumentAddress(arguments, index), emitReceiver);

        internal void RecordDirectArgument(int index, TypeSignature nativeType)
        {
            var local = new CilLocalVariable(nativeType);
            target.CilMethodBody.LocalVariables.Add(local);
            directArguments[index] = local;
            body.Add(CilOpCodes.Dup);
            body.Add(CilOpCodes.Stloc, local);
        }

        internal void CaptureDirectInvocation() => Capture(EmitDirectAddress, null);

        private void Capture(Action<int> emitAddress, Action emitReceiver)
        {
            var done = new CilInstructionLabel();
            body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(
                typeof(NativeJobHandles).GetProperty(nameof(NativeJobHandles.TrackedHandleCount)).GetMethod));
            body.Add(CilOpCodes.Brfalse, done);
            if (inputs == Inputs.Receiver)
            {
                emitReceiver();
                body.Add(CilOpCodes.Ldc_I4_1);
                EmitRuntimeCall(nameof(NativeJobHandles.CaptureBuffer));
            }
            else if (inputs == Inputs.Locations)
            {
                for (var index = 0; index < 3; index++)
                    if (index < inputCount) emitAddress(index);
                    else { body.Add(CilOpCodes.Ldc_I4_0); body.Add(CilOpCodes.Conv_I); }
                EmitRuntimeCall(nameof(NativeJobHandles.CaptureLocations));
            }
            else if (inputs == Inputs.Buffer)
            {
                emitAddress(0);
                emitAddress(1);
                body.Add(CilOpCodes.Ldind_I4);
                EmitRuntimeCall(nameof(NativeJobHandles.CaptureBuffer));
            }
            else
            {
                emitAddress(0);
                var capture = (IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(
                    typeof(InteropNativeJobContainers).GetMethod(nameof(InteropNativeJobContainers.Capture)));
                body.Add(CilOpCodes.Call, capture.MakeGenericInstanceMethod(target.Parameters[0].ParameterType));
            }
            body.Add(CilOpCodes.Stloc, scope);
            done.Instruction = body.Add(CilOpCodes.Nop);
        }

        internal void InvocationSucceeded(CilLocalVariable arguments, CilLocalVariable result) =>
            Succeeded(() =>
            {
                if (outputIndex >= 0) EmitArgumentAddress(arguments, outputIndex);
                else
                {
                    // Copy out of the native return box before observation can allocate or wait.
                    body.Add(CilOpCodes.Ldloca, result);
                    body.Add(CilOpCodes.Conv_I);
                }
            });

        internal void DirectInvocationSucceeded(CilLocalVariable result) => Succeeded(() =>
        {
            if (outputIndex >= 0) EmitDirectAddress(outputIndex);
            else { body.Add(CilOpCodes.Ldloca, result); body.Add(CilOpCodes.Conv_I); }
        });

        private void Succeeded(Action emitResult)
        {
            var done = new CilInstructionLabel();
            body.Add(CilOpCodes.Ldloc, scope);
            body.Add(CilOpCodes.Brfalse, done);
            body.Add(CilOpCodes.Ldloc, scope);
            if (!completes) emitResult();
            body.Add(CilOpCodes.Callvirt, imports.Module.DefaultImporter.ImportMethod(typeof(NativeJobHandles.Scope).GetMethod(
                completes ? nameof(NativeJobHandles.Scope.Complete) : nameof(NativeJobHandles.Scope.CombinedBuffer))));
            done.Instruction = body.Add(CilOpCodes.Nop);
        }

        private void EmitDirectAddress(int index)
        {
            var type = original.Parameters[index].ParameterType;
            body.Add(type is ByReferenceTypeSignature or PointerTypeSignature ? CilOpCodes.Ldloc : CilOpCodes.Ldloca,
                directArguments[index]);
            body.Add(CilOpCodes.Conv_I);
        }

        private void EmitArgumentAddress(CilLocalVariable arguments, int index)
        {
            body.Add(CilOpCodes.Ldloc, arguments);
            if (index != 0)
            {
                body.Add(CilOpCodes.Ldc_I4, index);
                body.Add(CilOpCodes.Sizeof, imports.Module.CorLibTypeFactory.IntPtr.ToTypeDefOrRef());
                body.Add(CilOpCodes.Mul);
                body.Add(CilOpCodes.Add);
            }
            body.Add(CilOpCodes.Ldind_I);
        }

        private void EmitRuntimeCall(string name) => body.Add(CilOpCodes.Call,
            imports.Module.DefaultImporter.ImportMethod(typeof(NativeJobHandles).GetMethod(name)));
    }
}
