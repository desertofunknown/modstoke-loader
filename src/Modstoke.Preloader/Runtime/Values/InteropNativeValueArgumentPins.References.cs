// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Collections;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropNativeValueArgumentPins
    {
        private sealed record ReferenceArgument(TypeSignature Element, CilLocalVariable Location,
            CilLocalVariable Kind, CilLocalVariable Address, CilLocalVariable Slot,
            CilLocalVariable InitialValue, CilLocalVariable OwnsSlot);

        private readonly List<ReferenceArgument> referenceArguments = new();
        private CilLocalVariable invocationStarted;

        private CilLocalVariable AddArgumentLocal(TypeSignature type)
        {
            var local = new CilLocalVariable(type);
            target.CilMethodBody.LocalVariables.Add(local);
            return local;
        }

        private CilLocalVariable AddArgumentHandle()
        {
            var handle = AddArgumentLocal(imports.Module.CorLibTypeFactory.IntPtr);
            handles.Add(handle);
            return handle;
        }

        private CilLocalVariable PinArgumentLocation(Parameter parameter, bool byReference)
        {
            var type = byReference ? parameter.ParameterType : parameter.ParameterType.MakeByReferenceType();
            var location = AddArgumentLocal(type.MakePinnedType());
            managedPins.Add(location);
            body.Add(byReference ? CilOpCodes.Ldarg : CilOpCodes.Ldarga, parameter);
            body.Add(CilOpCodes.Stloc, location);
            return location;
        }

        private void EmitArgumentCall(string name, TypeSignature element) =>
            body.Add(CilOpCodes.Call, ((IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(
                typeof(InteropGenericValueSupport).GetMethod(name))).MakeGenericInstanceMethod(element));

        private void EmitPinnedAddress(CilLocalVariable handle, bool value)
        {
            body.Add(CilOpCodes.Stloc, handle);
            body.Add(CilOpCodes.Ldloc, handle);
            body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(value
                ? typeof(InteropNativeValueStorage).GetMethod(nameof(InteropNativeValueStorage.PinnedArgumentAddress))
                : typeof(InteropGenericValueSupport).GetMethod(nameof(InteropGenericValueSupport.PinnedReferenceArgumentAddress))));
        }

        private void EmitReferenceArgument(Parameter parameter, TypeSignature element)
        {
            var corlib = imports.Module.CorLibTypeFactory;
            var location = PinArgumentLocation(parameter, byReference: true);
            var state = new ReferenceArgument(element, location, AddArgumentLocal(corlib.Int32),
                AddArgumentLocal(corlib.IntPtr), AddArgumentLocal(corlib.IntPtr),
                AddArgumentLocal(corlib.IntPtr), AddArgumentLocal(corlib.Boolean));
            var handle = AddArgumentHandle();
            var clrValue = new CilInstructionLabel();
            var nativeValue = new CilInstructionLabel();
            var done = new CilInstructionLabel();
            EmitArgumentCall(nameof(InteropGenericValueSupport.ArgumentKind), element);
            body.Add(CilOpCodes.Stloc, state.Kind);
            body.Add(CilOpCodes.Ldloc, state.Kind);
            body.Add(CilOpCodes.Brfalse, clrValue);
            body.Add(CilOpCodes.Ldloc, state.Kind);
            body.Add(CilOpCodes.Ldc_I4_1);
            body.Add(CilOpCodes.Beq, nativeValue);

            // Pinned caller addresses remain comparable through subsequent allocations. Two
            // aliased arguments must expose one native slot, including mixed ref/out pairs.
            foreach (var previous in referenceArguments)
            {
                var next = new CilInstructionLabel();
                body.Add(CilOpCodes.Ldloc, previous.Kind);
                body.Add(CilOpCodes.Ldc_I4_2);
                body.Add(CilOpCodes.Bne_Un, next);
                body.Add(CilOpCodes.Ldloc, location);
                body.Add(CilOpCodes.Conv_I);
                body.Add(CilOpCodes.Ldloc, previous.Location);
                body.Add(CilOpCodes.Conv_I);
                body.Add(CilOpCodes.Bne_Un, next);
                body.Add(CilOpCodes.Ldloc, previous.Address);
                body.Add(CilOpCodes.Stloc, state.Address);
                body.Add(CilOpCodes.Br, done);
                next.Instruction = body.Add(CilOpCodes.Nop);
            }
            body.Add(CilOpCodes.Ldloc, location);
            body.Add(CilOpCodes.Ldobj, element.ToTypeDefOrRef());
            EmitArgumentCall(nameof(InteropGenericValueSupport.PinReferenceArgument), element);
            EmitPinnedAddress(handle, value: false);
            body.Add(CilOpCodes.Dup);
            body.Add(CilOpCodes.Stloc, state.InitialValue);
            body.Add(CilOpCodes.Stloc, state.Slot);
            body.Add(CilOpCodes.Ldloca, state.Slot);
            body.Add(CilOpCodes.Conv_I);
            body.Add(CilOpCodes.Stloc, state.Address);
            body.Add(CilOpCodes.Ldc_I4_1);
            body.Add(CilOpCodes.Stloc, state.OwnsSlot);
            body.Add(CilOpCodes.Br, done);

            clrValue.Instruction = body.Add(CilOpCodes.Ldloc, location);
            body.Add(CilOpCodes.Conv_I);
            body.Add(CilOpCodes.Stloc, state.Address);
            body.Add(CilOpCodes.Br, done);
            nativeValue.Instruction = body.Add(CilOpCodes.Ldloc, location);
            EmitArgumentCall(nameof(InteropGenericValueSupport.PinValueArgument), element);
            EmitPinnedAddress(handle, value: true);
            body.Add(CilOpCodes.Stloc, state.Address);
            done.Instruction = body.Add(CilOpCodes.Ldloc, state.Address);
            referenceArguments.Add(state);
        }

        private void EmitStringArgument(Parameter parameter)
        {
            var handle = AddArgumentHandle();
            body.Add(CilOpCodes.Ldarg, parameter);
            body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(
                typeof(InteropGenericValueSupport).GetMethod(nameof(InteropGenericValueSupport.PinStringArgument))));
            EmitPinnedAddress(handle, value: false);
        }

        private void EmitGenericValueArgument(Parameter parameter, TypeSignature element)
        {
            var location = PinArgumentLocation(parameter, byReference: false);
            var handle = AddArgumentHandle();
            var clrValue = new CilInstructionLabel();
            var nativeValue = new CilInstructionLabel();
            var referenceValue = new CilInstructionLabel();
            var done = new CilInstructionLabel();
            EmitArgumentCall(nameof(InteropGenericValueSupport.ArgumentKind), element);
            body.Add(CilOpCodes.Switch, new ICilLabel[] { clrValue, nativeValue, referenceValue });
            body.Add(CilOpCodes.Br, referenceValue);
            clrValue.Instruction = body.Add(CilOpCodes.Ldloc, location);
            body.Add(CilOpCodes.Conv_I);
            body.Add(CilOpCodes.Br, done);
            nativeValue.Instruction = body.Add(CilOpCodes.Ldloc, location);
            EmitArgumentCall(nameof(InteropGenericValueSupport.PinValueArgument), element);
            EmitPinnedAddress(handle, value: true);
            body.Add(CilOpCodes.Br, done);
            referenceValue.Instruction = body.Add(CilOpCodes.Ldloc, location);
            body.Add(CilOpCodes.Ldobj, element.ToTypeDefOrRef());
            EmitArgumentCall(nameof(InteropGenericValueSupport.PinReferenceArgument), element);
            EmitPinnedAddress(handle, value: false);
            done.Instruction = body.Add(CilOpCodes.Nop);
        }

        internal void MarkInvocation()
        {
            if (referenceArguments.Count == 0) return;
            invocationStarted ??= AddArgumentLocal(imports.Module.CorLibTypeFactory.Boolean);
            body.Add(CilOpCodes.Ldc_I4_1);
            body.Add(CilOpCodes.Stloc, invocationStarted);
        }

        private void EmitReferenceCleanup(CilLocalVariable primaryFailure)
        {
            if (invocationStarted == null)
                throw new InvalidOperationException("Native reference writeback requires an invocation boundary.");
            var failure = AddArgumentLocal(imports.Module.DefaultImporter.ImportTypeSignature(typeof(Exception)));
            var start = new CilInstructionLabel(body.Add(CilOpCodes.Ldloc, invocationStarted));
            var done = new CilInstructionLabel();
            var exit = new CilInstructionLabel();
            body.Add(CilOpCodes.Brfalse, done);
            foreach (var state in referenceArguments)
            {
                var next = new CilInstructionLabel();
                body.Add(CilOpCodes.Ldloc, state.OwnsSlot);
                body.Add(CilOpCodes.Brfalse, next);
                body.Add(CilOpCodes.Ldloc, state.Slot);
                body.Add(CilOpCodes.Ldloc, state.InitialValue);
                body.Add(CilOpCodes.Beq, next);
                EmitCleanupStep(failure, () =>
                {
                    body.Add(CilOpCodes.Ldloc, state.Location);
                    body.Add(CilOpCodes.Ldloc, state.Slot);
                    EmitArgumentCall(nameof(InteropGenericValueSupport.WriteReferenceArgument), state.Element);
                });
                next.Instruction = body.Add(CilOpCodes.Nop);
            }
            done.Instruction = body.Add(CilOpCodes.Leave, exit);
            var cleanup = new CilInstructionLabel(body.Add(CilOpCodes.Nop));
            EmitPinCleanup(failure);
            body.Add(CilOpCodes.Endfinally);
            exit.Instruction = body.Add(CilOpCodes.Nop);
            target.CilMethodBody.ExceptionHandlers.Add(new CilExceptionHandler
            {
                HandlerType = CilExceptionHandlerType.Finally,
                TryStart = start,
                TryEnd = cleanup,
                HandlerStart = cleanup,
                HandlerEnd = exit
            });
            EmitCleanupFailure(primaryFailure, failure);
        }

        private void EmitCleanupFailure(CilLocalVariable primaryFailure, CilLocalVariable failure)
        {
            var finished = new CilInstructionLabel();
            body.Add(CilOpCodes.Ldloc, failure);
            body.Add(CilOpCodes.Brfalse, finished);
            body.Add(CilOpCodes.Ldloc, primaryFailure);
            body.Add(CilOpCodes.Ldloc, failure);
            body.Add(CilOpCodes.Ldstr, target.FullName);
            body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(
                typeof(InteropExceptionSupport).GetMethod(nameof(InteropExceptionSupport.FinishNativeCleanup))));
            finished.Instruction = body.Add(CilOpCodes.Nop);
        }

        private void EmitCleanupStep(CilLocalVariable failure, Action emit)
        {
            if (failure == null)
            {
                emit();
                return;
            }
            var start = new CilInstructionLabel(body.Add(CilOpCodes.Nop));
            var done = new CilInstructionLabel();
            emit();
            body.Add(CilOpCodes.Leave, done);
            var handler = new CilInstructionLabel(body.Add(CilOpCodes.Ldloca, failure));
            body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(
                typeof(InteropExceptionSupport).GetMethod(nameof(InteropExceptionSupport.CollectNativeCleanupFailure))));
            body.Add(CilOpCodes.Leave, done);
            done.Instruction = body.Add(CilOpCodes.Nop);
            target.CilMethodBody.ExceptionHandlers.Add(new CilExceptionHandler
            {
                HandlerType = CilExceptionHandlerType.Exception,
                TryStart = start,
                TryEnd = handler,
                HandlerStart = handler,
                HandlerEnd = done,
                ExceptionType = imports.Module.DefaultImporter.ImportType(typeof(Exception))
            });
        }
    }
}
