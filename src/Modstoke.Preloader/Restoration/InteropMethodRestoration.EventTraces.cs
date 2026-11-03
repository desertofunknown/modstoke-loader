// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodRestoration
    {
        private readonly Dictionary<MethodDefinition, MethodDefinition> referenceConstructorBodies =
            new(ReferenceEqualityComparer.Instance);

        private bool TryRestoreEventTraceConstructor(MethodDefinition source, MethodDefinition constructor,
            InteropTypeContext owner, InteropRuntimeReferences imports, InteropUnstripTypeResolver resolver,
            InteropRestorationEntry entry)
        {
            entry.Set("signature-rejected", "event-trace-constructor-contract",
                "Debugger objects require their original constructor and a single pointer-wrapping base chain.");
            var expectedToken = (string)source.DeclaringType.Name switch
            {
                "EventDebugger" => 0x06000CE1,
                "EventDebuggerEventRecord" => 0x06000D37,
                "EventDebuggerTrace" => 0x06000D3E,
                "EventDebuggerCallTrace" => 0x06000D44,
                "EventDebuggerDefaultActionTrace" => 0x06000D47,
                "EventDebuggerPathTrace" => 0x06000D49,
                _ => InteropNativeEventDebuggerTypes.ReplayConstructorToken(source.DeclaringType)
            };
            if (source.DeclaringModule.Mvid != new Guid("b6e504b1-20f0-43e3-9923-7045b32ad8bb") ||
                expectedToken == 0 || source.MetadataToken.ToInt32() != expectedToken ||
                !IsReferenceConstructorDeclaration(source) || source.CilMethodBody is not { } sourceBody ||
                sourceBody.ExceptionHandlers.Count != 0 || sourceBody.LocalVariables.Count != 0 ||
                owner.OriginalType.BaseType?.FullName != source.DeclaringType.BaseType?.FullName)
                return false;

            if (expectedToken == 0x06000CE1 &&
                (!InteropEventDebuggerFields.HasOwner(source.DeclaringType, owner, imports, resolver) ||
                 source.Signature.ParameterTypes.Count != 0 || source.DeclaringType.Fields.Count != 13 ||
                 source.DeclaringType.Fields.Any(field => !InteropEventDebuggerFields.TryDescribe(field, out _))))
                return false;

            var baseCalls = sourceBody.Instructions.Where(instruction => instruction.Operand is IMethodDescriptor call &&
                call.Name == ".ctor" && instruction.OpCode.Code == CilCode.Call).ToArray();
            if (baseCalls.Length != 1 || baseCalls[0].Operand is not IMethodDescriptor baseCall ||
                baseCall.DeclaringType?.FullName != source.DeclaringType.BaseType.FullName ||
                baseCall is MethodSpecification)
                return false;
            var pointerSignature = MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void,
                imports.Module.CorLibTypeFactory.IntPtr);
            var pointers = owner.NewType.Methods.Where(method => method.Name == ".ctor" &&
                SignatureComparer.Default.Equals(method.Signature, pointerSignature)).ToArray();
            if (pointers.Length != 1 || pointers[0].CilMethodBody == null || !pointers[0].IsPublic)
                return false;

            var name = (expectedToken == 0x06000CE1 ? "__InitializeEventDebugger_" : "__InitializeEventTrace_") +
                expectedToken.ToString("X8");
            if (owner.NewType.Methods.Any(method => method.Name == name))
                return false;
            var initializer = new MethodDefinition(name, MethodAttributes.Family | MethodAttributes.HideBySig,
                MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void))
            {
                CilMethodBody = new CilMethodBody()
            };
            foreach (var parameter in constructor.Parameters)
                InteropMethodDeclarations.AddParameter(initializer, parameter.ParameterType, parameter.Name,
                    parameter.Definition?.Attributes ?? default);

            owner.NewType.Methods.Add(constructor);
            owner.NewType.Methods.Add(initializer);
            owner.AddRestoredReferenceConstructor(source, constructor, initializer);
            owner.AddRestoredBody(source, constructor);
            referenceConstructorBodies.Add(constructor, initializer);
            pendingBodies.Add((source, initializer, owner, imports, entry));
            entry.Set("body-pending");

            // Only newobj allocates. Derived initializers call the base body on this same native object.
            var body = constructor.CilMethodBody.Instructions;
            body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Ldsfld, owner.ClassPointerFieldRef);
            body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_object_new.Value);
            body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(pointers[0]));
            body.Add(CilOpCodes.Ldarg_0);
            foreach (var parameter in constructor.Parameters) body.Add(CilOpCodes.Ldarg, parameter);
            body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(initializer));
            body.Add(CilOpCodes.Ret);
            return true;
        }

        private static void RetainEventTraceArguments(MethodDefinition initializer, InteropRuntimeReferences imports)
        {
            var body = initializer.CilMethodBody.Instructions;
            var start = body[0];
            var finish = new CilInstruction(CilOpCodes.Ret);
            foreach (var instruction in body.Where(instruction => instruction.OpCode.Code == CilCode.Ret))
            {
                instruction.OpCode = CilOpCodes.Leave;
                instruction.Operand = finish.CreateLabel();
            }
            var keepAlive = imports.Module.DefaultImporter.ImportMethod(
                typeof(GC).GetMethod(nameof(GC.KeepAlive), new[] { typeof(object) }));
            var cleanup = body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Call, keepAlive);
            foreach (var parameter in initializer.Parameters.Where(parameter => !parameter.ParameterType.IsValueType))
            {
                body.Add(CilOpCodes.Ldarg, parameter);
                body.Add(CilOpCodes.Call, keepAlive);
            }
            body.Add(CilOpCodes.Endfinally);
            body.Add(finish);
            initializer.CilMethodBody.ExceptionHandlers.Add(new CilExceptionHandler
            {
                HandlerType = CilExceptionHandlerType.Finally,
                TryStart = start.CreateLabel(), TryEnd = cleanup.CreateLabel(),
                HandlerStart = cleanup.CreateLabel(), HandlerEnd = finish.CreateLabel()
            });
        }
    }
}
