// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Collections;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Il2CppInterop.Runtime.Injection;

namespace Modstoke.Preloader
{
    // Generator-only state. Typed arguments use handle locals; native locations also retain a scope.
    internal sealed partial class InteropNativeValueArgumentPins
    {
        private readonly MethodDefinition target;
        private readonly InteropRuntimeReferences imports;
        private readonly CilInstructionCollection body;
        private readonly int startIndex;
        private readonly List<CilLocalVariable> handles = new();
        private readonly List<CilLocalVariable> managedPins = new();
        private readonly List<Parameter> keepAlive = new();
        private CilLocalVariable valueReceiver;
        private CilLocalVariable referenceScope;
        private CilLocalVariable jobScope;

        internal InteropNativeValueArgumentPins(MethodDefinition target, InteropRuntimeReferences imports)
        {
            this.target = target;
            this.imports = imports;
            body = target.CilMethodBody.Instructions;
            startIndex = body.Count;
            if (!target.IsStatic && !target.DeclaringType.IsValueType)
                keepAlive.Add(target.Parameters.ThisParameter);
        }

        internal void PinValueReceiver()
        {
            if (target.IsStatic || !target.DeclaringType.IsValueType) return;
            var receiver = target.Parameters.ThisParameter;
            valueReceiver = PinArgumentLocation(receiver, byReference: true);
        }

        internal bool EmitValueReceiver()
        {
            if (valueReceiver == null) return false;
            body.Add(CilOpCodes.Ldloc, valueReceiver);
            body.Add(CilOpCodes.Conv_I);
            return true;
        }

        internal void RetainReference(Parameter parameter) => keepAlive.Add(parameter);

        internal CilLocalVariable CreateJobScope()
        {
            jobScope = AddArgumentLocal(imports.Module.DefaultImporter.ImportTypeSignature(typeof(NativeJobHandles.Scope)));
            return jobScope;
        }

        internal CilLocalVariable CreateReferenceScope(int capacity)
        {
            referenceScope = AddArgumentLocal(imports.Module.DefaultImporter.ImportTypeSignature(typeof(InteropNativeReferenceScope)));
            body.Add(CilOpCodes.Ldc_I4, capacity);
            body.Add(CilOpCodes.Newobj, imports.Module.DefaultImporter.ImportMethod(
                typeof(InteropNativeReferenceScope).GetConstructor(new[] { typeof(int) })));
            body.Add(CilOpCodes.Stloc, referenceScope);
            return referenceScope;
        }

        internal bool TryEmit(TypeSignature sourceType, Parameter parameter, bool allowByValue = false)
        {
            var byReference = sourceType is ByReferenceTypeSignature;
            var sourceElement = sourceType is ByReferenceTypeSignature source ? source.BaseType : sourceType;
            var rewrittenElement = parameter.ParameterType is ByReferenceTypeSignature rewritten
                ? rewritten.BaseType : parameter.ParameterType;
            if (!byReference && sourceElement is CorLibTypeSignature { ElementType: ElementType.String } &&
                rewrittenElement is CorLibTypeSignature { ElementType: ElementType.String })
            {
                EmitStringArgument(parameter);
                return true;
            }
            if (!byReference && sourceElement is not GenericParameterSignature &&
                !InteropTypeReferences.IsValueType(sourceElement) && !InteropTypeReferences.IsValueType(rewrittenElement) &&
                rewrittenElement is not (GenericParameterSignature or PointerTypeSignature or FunctionPointerTypeSignature or
                    ByReferenceTypeSignature or CustomModifierTypeSignature or PinnedTypeSignature or SentinelTypeSignature) &&
                rewrittenElement.ElementType != ElementType.TypedByRef)
                keepAlive.Add(parameter);
            if (byReference && parameter.ParameterType is ByReferenceTypeSignature &&
                (sourceElement is GenericParameterSignature || !InteropTypeReferences.IsValueType(sourceElement)) &&
                sourceElement is not PointerTypeSignature and not FunctionPointerTypeSignature)
            {
                EmitReferenceArgument(parameter, rewrittenElement);
                return true;
            }
            if (allowByValue && !byReference && sourceElement is GenericParameterSignature)
            {
                EmitGenericValueArgument(parameter, rewrittenElement);
                return true;
            }
            if (!byReference && !allowByValue || byReference != (parameter.ParameterType is ByReferenceTypeSignature) ||
                !InteropTypeReferences.IsValueType(sourceElement) ||
                rewrittenElement is GenericParameterSignature or PointerTypeSignature or FunctionPointerTypeSignature)
                return false;

            if (InteropTypeReferences.IsValueType(rewrittenElement))
            {
                if (!byReference) return false;
                // A preceding CLR field/array argument must not move while a later native
                // argument allocates its default box or while native code calls back into CLR.
                var pinned = PinArgumentLocation(parameter, byReference: true);
                body.Add(CilOpCodes.Ldloc, pinned);
                body.Add(CilOpCodes.Conv_I);
                return true;
            }

            var handle = AddArgumentHandle();
            body.Add(byReference ? CilOpCodes.Ldarg : CilOpCodes.Ldarga, parameter);
            body.Add(CilOpCodes.Call, ((IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(
                typeof(InteropNativeValueStorage).GetMethod(nameof(InteropNativeValueStorage.PinArgument))))
                .MakeGenericInstanceMethod(rewrittenElement));
            EmitPinnedAddress(handle, value: true);
            return true;
        }

        internal void Complete()
        {
            if (handles.Count == 0 && managedPins.Count == 0 && keepAlive.Count == 0 && referenceScope == null && jobScope == null) return;
            if (body.Count <= startIndex || body[body.Count - 1].OpCode.Code != CilCode.Ret)
                throw new InvalidOperationException("Native argument cleanup requires a single generated return.");
            target.CilMethodBody.InitializeLocals = true;
            var exit = new CilInstructionLabel();
            var last = body[body.Count - 1];
            CilLocalVariable result = null;
            if (target.Signature.ReturnType.ElementType != ElementType.Void)
            {
                result = AddArgumentLocal(target.Signature.ReturnType);
                last.OpCode = CilOpCodes.Stloc;
                last.Operand = result;
                body.Add(CilOpCodes.Leave, exit);
            }
            else
            {
                last.OpCode = CilOpCodes.Leave;
                last.Operand = exit;
            }
            CilLocalVariable primaryFailure = null;
            CilInstructionLabel failureHandler = null;
            if (referenceArguments.Count != 0 || jobScope != null)
            {
                primaryFailure = AddArgumentLocal(imports.Module.DefaultImporter.ImportTypeSignature(typeof(Exception)));
                failureHandler = new CilInstructionLabel(body.Add(CilOpCodes.Stloc, primaryFailure));
                body.Add(CilOpCodes.Rethrow);
            }
            var cleanup = new CilInstructionLabel(body.Add(CilOpCodes.Nop));
            if (failureHandler != null)
                target.CilMethodBody.ExceptionHandlers.Add(new CilExceptionHandler
                {
                    HandlerType = CilExceptionHandlerType.Exception,
                    TryStart = new CilInstructionLabel(body[startIndex]),
                    TryEnd = failureHandler,
                    HandlerStart = failureHandler,
                    HandlerEnd = cleanup,
                    ExceptionType = imports.Module.DefaultImporter.ImportType(typeof(Exception))
                });
            if (referenceArguments.Count != 0) EmitReferenceCleanup(primaryFailure);
            else if (jobScope != null)
            {
                var failure = AddArgumentLocal(imports.Module.DefaultImporter.ImportTypeSignature(typeof(Exception)));
                EmitPinCleanup(failure);
                EmitCleanupFailure(primaryFailure, failure);
            }
            else EmitPinCleanup();
            body.Add(CilOpCodes.Endfinally);
            exit.Instruction = result == null ? body.Add(CilOpCodes.Ret) : body.Add(CilOpCodes.Ldloc, result);
            if (result != null) body.Add(CilOpCodes.Ret);
            target.CilMethodBody.ExceptionHandlers.Add(new CilExceptionHandler
            {
                HandlerType = CilExceptionHandlerType.Finally,
                TryStart = new CilInstructionLabel(body[startIndex]),
                TryEnd = cleanup,
                HandlerStart = cleanup,
                HandlerEnd = exit
            });
        }

        private void EmitPinCleanup(CilLocalVariable failure = null)
        {
            var release = imports.Module.DefaultImporter.ImportMethod(
                typeof(InteropNativeValueStorage).GetMethod(nameof(InteropNativeValueStorage.ReleaseArgument)));
            for (var index = handles.Count - 1; index >= 0; index--)
            {
                var handle = handles[index];
                EmitCleanupStep(failure, () =>
                {
                    body.Add(CilOpCodes.Ldloc, handle);
                    body.Add(CilOpCodes.Call, release);
                });
            }
            if (referenceScope != null)
            {
                var done = new CilInstructionLabel();
                body.Add(CilOpCodes.Ldloc, referenceScope);
                body.Add(CilOpCodes.Brfalse, done);
                EmitCleanupStep(failure, () =>
                {
                    body.Add(CilOpCodes.Ldloc, referenceScope);
                    body.Add(CilOpCodes.Callvirt, imports.Module.DefaultImporter.ImportMethod(
                        typeof(InteropNativeReferenceScope).GetMethod(nameof(InteropNativeReferenceScope.Dispose))));
                });
                done.Instruction = body.Add(CilOpCodes.Nop);
            }
            foreach (var pinned in managedPins)
            {
                body.Add(CilOpCodes.Ldc_I4_0);
                body.Add(CilOpCodes.Conv_U);
                body.Add(CilOpCodes.Stloc, pinned);
            }
            // Existing wrappers already own native handles. Retain them through conversion
            // and cleanup without allocating another native handle on every call.
            var retain = imports.Module.DefaultImporter.ImportMethod(typeof(GC).GetMethod(nameof(GC.KeepAlive)));
            foreach (var parameter in keepAlive)
            {
                body.Add(CilOpCodes.Ldarg, parameter);
                body.Add(CilOpCodes.Call, retain);
            }
            // Keep the runtime operation lease through all native pin cleanup, even if a release fails.
            if (jobScope != null)
            {
                var done = new CilInstructionLabel();
                body.Add(CilOpCodes.Ldloc, jobScope);
                body.Add(CilOpCodes.Brfalse, done);
                EmitCleanupStep(failure, () =>
                {
                    body.Add(CilOpCodes.Ldloc, jobScope);
                    body.Add(CilOpCodes.Callvirt, imports.Module.DefaultImporter.ImportMethod(
                        typeof(NativeJobHandles.Scope).GetMethod(nameof(NativeJobHandles.Scope.Dispose))));
                });
                done.Instruction = body.Add(CilOpCodes.Nop);
            }
        }
    }
}
