// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal static class InteropOverlayRepaintContract
    {
        private const string ModuleName = "UnityEngine.UIElementsModule";
        private const string OwnerName = "UnityEngine.UIElements.UIElementsRuntimeUtility";

        internal static bool CanRestoreField(FieldDefinition field, InteropTypeContext owner,
            InteropUnstripTypeResolver resolver)
        {
            if (!HasEventField(field) || owner.OriginalType == null || owner.ClassPointerFieldRef == null ||
                owner.OriginalType.FullName != OwnerName || owner.OriginalType.DeclaringType != null ||
                owner.OriginalType.DeclaringModule.Assembly.Name != ModuleName ||
                owner.OriginalType.IsValueType || owner.OriginalType.IsInterface ||
                owner.OriginalType.GenericParameters.Count != 0 || owner.NewType.FullName != OwnerName ||
                owner.NewType.DeclaringType != null || owner.NewType.IsValueType || owner.NewType.IsInterface ||
                owner.NewType.DeclaringModule != owner.AssemblyContext.Imports.Module ||
                owner.NewType.GenericParameters.Count != 0 ||
                resolver.ResolveRaw(field.DeclaringType.ToTypeSignature())?.Resolve() != owner.NewType ||
                owner.OriginalType.Fields.Any(candidate => candidate.Name == field.Name || candidate.Name == "repaintUpdateCallback") ||
                owner.NewType.Fields.Any(candidate => candidate.Name == field.Name || candidate.Name == "repaintUpdateCallback") ||
                owner.NewType.Properties.Any(candidate => candidate.Name == field.Name || candidate.Name == "repaintUpdateCallback"))
                return false;

            var action = field.Signature.FieldType;
            var nativeAction = owner.AssemblyContext.GlobalContext.TryGetTypeByName("mscorlib", "System.Action");
            if (nativeAction?.OriginalType == null || nativeAction.ClassPointerFieldRef == null ||
                nativeAction.OriginalType.FullName != "System.Action" ||
                nativeAction.OriginalType.DeclaringModule.Assembly.Name != "mscorlib" ||
                nativeAction.OriginalType.GenericParameters.Count != 0 ||
                nativeAction.NewType.FullName != "Il2CppSystem.Action" ||
                !InteropDelegateContracts.CanStoreNativeDelegate(nativeAction.OriginalType, nativeAction.NewType) ||
                resolver.ResolveRaw(action)?.Resolve() != nativeAction.NewType) return false;
            var invoke = nativeAction.NewType.Methods.Single(method => method.Name == "Invoke").Signature;
            if (invoke.ParameterTypes.Count != 0 ||
                invoke.ReturnType is not CorLibTypeSignature { ElementType: ElementType.Void }) return false;

            var type = field.DeclaringType;
            var add = FindMethod(type, 100670822);
            var remove = FindMethod(type, 100670823);
            if (!TryGetAccessor(add, out var addField, out var panels, out _, out var register) ||
                !TryGetAccessor(remove, out var removeField, out var removePanels, out _, out var unregister) ||
                addField != field || removeField != field || panels != removePanels ||
                !HasRepaintBody(FindMethod(type, 100670832), field) ||
                !HasNativeLoopMethod(owner, register) || !HasNativeLoopMethod(owner, unregister)) return false;
            var nativePanels = owner.TryGetFieldByUnityAssemblyField(panels);
            if (nativePanels?.PointerField == null || nativePanels.OriginalField.DeclaringType != owner.OriginalType ||
                !nativePanels.OriginalField.IsStatic || nativePanels.OriginalField.IsLiteral ||
                nativePanels.OriginalField.Signature?.FieldType is not CorLibTypeSignature { ElementType: ElementType.Boolean })
                return false;

            // Only restored callers see this CLR static; native callbacks and Canvas wiring remain untouched.
            var references = 0;
            foreach (var method in type.DeclaringModule.GetAllTypes().SelectMany(candidate => candidate.Methods))
            {
                if (method.CilMethodBody == null) continue;
                foreach (var instruction in method.CilMethodBody.Instructions)
                {
                    if (instruction.OpCode.Code == CilCode.Ldstr && instruction.Operand is string literal &&
                        literal.Contains("s_onRepaintOverlayPanels", StringComparison.Ordinal)) return false;
                    if (instruction.Operand is not IFieldDescriptor reference || reference.Name != field.Name ||
                        reference.DeclaringType?.FullName != OwnerName) continue;
                    if (method.DeclaringType != type || !IsField(instruction, field)) return false;
                    var allowed = (method.MetadataToken.ToInt32(), instruction.Offset, instruction.OpCode.Code) switch
                    {
                        (100670820 or 100670821, 0x00, CilCode.Ldsfld) => true,
                        (100670820 or 100670821, 0x15, CilCode.Ldsflda) => true,
                        (100670822, 0x01, CilCode.Ldsfld) => true,
                        (100670823, 0x08, CilCode.Ldsfld) => true,
                        (100670832, 0x4E or 0x5A, CilCode.Ldsfld) => true,
                        _ => false
                    };
                    if (!allowed) return false;
                    references++;
                }
            }
            return references == 8;
        }

        internal static bool TryGetAccessor(MethodDefinition source, out FieldDefinition eventField,
            out FieldDefinition panelsRegistered, out MethodDefinition innerAccessor, out MethodDefinition playerLoopMethod)
        {
            eventField = null;
            panelsRegistered = null;
            innerAccessor = null;
            playerLoopMethod = null;
            if (source == null || !HasSourceOwner(source.DeclaringType)) return false;
            var add = source.MetadataToken.ToInt32() == 100670822;
            if (!add && source.MetadataToken.ToInt32() != 100670823) return false;
            var type = source.DeclaringType;
            var field = type.Fields.FirstOrDefault(candidate => candidate.MetadataToken.ToInt32() == 67112001);
            var panels = type.Fields.FirstOrDefault(candidate => candidate.MetadataToken.ToInt32() == 67112003);
            var inner = FindMethod(type, add ? 100670820 : 100670821);
            var loop = FindMethod(type, add ? 100670846 : 100670847);
            if (!HasEventField(field) || panels?.Name != "s_RegisteredPlayerloopCallback" ||
                !IsStaticStorage(panels) || panels.CustomAttributes.Count != 0 ||
                panels.Signature.FieldType is not CorLibTypeSignature { ElementType: ElementType.Boolean } ||
                !HasSourceMethod(source, add ? "add_onRepaintOverlayPanels" : "remove_onRepaintOverlayPanels",
                    MethodAttributes.Assembly | MethodAttributes.SpecialName, field.Signature.FieldType) ||
                source.CustomAttributes.Count != 0 || !HasInnerAccessor(inner, field, add) ||
                !HasSourceMethod(loop, add ? "RegisterPlayerloopCallback" : "UnregisterPlayerloopCallback",
                    MethodAttributes.Public) || loop.CustomAttributes.Count != 0) return false;
            var body = source.CilMethodBody;
            if (!body.InitializeLocals || body.LocalVariables.Count != 1 || body.ExceptionHandlers.Count != 0 ||
                body.LocalVariables[0].VariableType is not CorLibTypeSignature { ElementType: ElementType.Boolean }) return false;
            var code = WithoutNops(body);
            CilCode[] expected = add
                ? [CilCode.Ldsfld, CilCode.Ldnull, CilCode.Ceq, CilCode.Stloc_0, CilCode.Ldloc_0,
                    CilCode.Brfalse_S, CilCode.Call, CilCode.Ldarg_0, CilCode.Call, CilCode.Ret]
                : [CilCode.Ldarg_0, CilCode.Call, CilCode.Ldsfld, CilCode.Ldnull, CilCode.Ceq,
                    CilCode.Stloc_0, CilCode.Ldloc_0, CilCode.Brfalse_S, CilCode.Call, CilCode.Ret];
            if (!HasOpcodes(code, expected) || !IsField(code[add ? 0 : 2], field) ||
                !IsMethod(code[add ? 8 : 1], inner) || !IsMethod(code[add ? 6 : 8], loop) ||
                !IsTarget(code, code[add ? 5 : 7].Operand, add ? 7 : 9)) return false;

            // Preserve this order. Native registration is not reference-counted; the emitter must also guard native panels.
            eventField = field;
            panelsRegistered = panels;
            innerAccessor = inner;
            playerLoopMethod = loop;
            return true;
        }

        private static bool HasSourceOwner(TypeDefinition type) =>
            type != null && type.FullName == OwnerName && type.MetadataToken.ToInt32() == 33555308 &&
            type.DeclaringModule?.Assembly?.Name == ModuleName && type.DeclaringModule.Assembly.Modules.Count == 1 &&
            type.DeclaringModule.Mvid == new Guid("b6e504b1-20f0-43e3-9923-7045b32ad8bb") &&
            type.Attributes == (TypeAttributes.Abstract | TypeAttributes.Sealed) && type.DeclaringType == null &&
            type.GenericParameters.Count == 0 && type.Interfaces.Count == 0 && type.CustomAttributes.Count == 0 &&
            IsCoreType(type.BaseType?.ToTypeSignature(), "System.Object");

        private static bool HasEventField(FieldDefinition field)
        {
            if (field == null || !HasSourceOwner(field.DeclaringType) || field.MetadataToken.ToInt32() != 67112001 ||
                field.Name != "s_onRepaintOverlayPanels" || !IsStaticStorage(field) ||
                field.Signature.FieldType is not TypeDefOrRefSignature { IsValueType: false } ||
                !IsCoreType(field.Signature.FieldType, "System.Action") || field.CustomAttributes.Count != 2 ||
                !IsCompilerGenerated(field.CustomAttributes[0])) return false;
            var attribute = field.CustomAttributes[1];
            return attribute.Constructor is { } constructor &&
                IsCoreType(constructor.DeclaringType?.ToTypeSignature(), "System.Diagnostics.DebuggerBrowsableAttribute") &&
                HasCallSignature(constructor.Signature, true, 0) && constructor.Name == ".ctor" &&
                constructor.Signature.ReturnType is CorLibTypeSignature { ElementType: ElementType.Void } &&
                constructor.Signature.ParameterTypes.Count == 1 &&
                IsCoreType(constructor.Signature.ParameterTypes[0], "System.Diagnostics.DebuggerBrowsableState") &&
                attribute.Signature is { } signature && signature.NamedArguments.Count == 0 &&
                signature.FixedArguments.Count == 1 && signature.FixedArguments[0].Element is int value && value == 0;
        }

        private static bool IsStaticStorage(FieldDefinition field) =>
            field.Attributes == (FieldAttributes.Private | FieldAttributes.Static) &&
            field.Constant == null && field.FieldRva == null && field.FieldOffset == null && field.MarshalDescriptor == null &&
            field.Signature is { HasThis: false };

        private static bool HasInnerAccessor(MethodDefinition method, FieldDefinition field, bool add)
        {
            var action = field.Signature.FieldType;
            if (!HasSourceMethod(method, add ? "add_s_onRepaintOverlayPanels" : "remove_s_onRepaintOverlayPanels",
                    MethodAttributes.Private | MethodAttributes.SpecialName, action) ||
                method.CustomAttributes.Count != 1 || !IsCompilerGenerated(method.CustomAttributes[0])) return false;
            var body = method.CilMethodBody;
            var code = WithoutNops(body);
            if (!body.InitializeLocals || body.ExceptionHandlers.Count != 0 || body.LocalVariables.Count != 3 ||
                body.LocalVariables.Any(local => !SignatureComparer.Default.Equals(local.VariableType, action)) ||
                !HasOpcodes(code, [CilCode.Ldsfld, CilCode.Stloc_0, CilCode.Ldloc_0, CilCode.Stloc_1,
                    CilCode.Ldloc_1, CilCode.Ldarg_0, CilCode.Call, CilCode.Castclass, CilCode.Stloc_2,
                    CilCode.Ldsflda, CilCode.Ldloc_2, CilCode.Ldloc_1, CilCode.Call, CilCode.Stloc_0,
                    CilCode.Ldloc_0, CilCode.Ldloc_1, CilCode.Bne_Un_S, CilCode.Ret]) ||
                !IsField(code[0], field) || !IsField(code[9], field) || !IsTarget(code, code[16].Operand, 2) ||
                code[7].Operand is not ITypeDefOrRef cast || !SignatureComparer.Default.Equals(cast.ToTypeSignature(), action) ||
                code[6].Operand is not IMethodDescriptor combine || combine is MethodSpecification ||
                combine.Name != (add ? "Combine" : "Remove") ||
                !IsCoreType(combine.DeclaringType?.ToTypeSignature(), "System.Delegate") ||
                !HasCallSignature(combine.Signature, false, 0) ||
                !IsCoreType(combine.Signature.ReturnType, "System.Delegate") || combine.Signature.ParameterTypes.Count != 2 ||
                combine.Signature.ParameterTypes.Any(parameter => !IsCoreType(parameter, "System.Delegate")) ||
                code[12].Operand is not MethodSpecification exchange || exchange.Signature.TypeArguments.Count != 1 ||
                !SignatureComparer.Default.Equals(exchange.Signature.TypeArguments[0], action) ||
                exchange.Method?.Name != "CompareExchange" ||
                !IsCoreType(exchange.Method.DeclaringType?.ToTypeSignature(), "System.Threading.Interlocked") ||
                !HasCallSignature(exchange.Method.Signature, false, 1)) return false;
            var signature = exchange.Method.Signature;
            return IsMethodParameter(signature.ReturnType) && signature.ParameterTypes.Count == 3 &&
                signature.ParameterTypes[0] is ByReferenceTypeSignature address && IsMethodParameter(address.BaseType) &&
                IsMethodParameter(signature.ParameterTypes[1]) && IsMethodParameter(signature.ParameterTypes[2]);
        }

        private static bool HasRepaintBody(MethodDefinition method, FieldDefinition field)
        {
            if (!HasSourceMethod(method, "RepaintOverlayPanels", MethodAttributes.Public) ||
                method.CustomAttributes.Count != 0) return false;
            var body = method.CilMethodBody;
            var code = WithoutNops(body);
            if (!body.InitializeLocals || body.LocalVariables.Count != 4 || body.ExceptionHandlers.Count != 1 ||
                body.LocalVariables[0].VariableType is not GenericInstanceTypeSignature enumerator ||
                !enumerator.IsValueType || enumerator.TypeArguments.Count != 1 ||
                !IsCoreType(enumerator.GenericType.ToTypeSignature(), "System.Collections.Generic.List`1/Enumerator") ||
                enumerator.TypeArguments[0].FullName != "UnityEngine.UIElements.Panel" ||
                enumerator.TypeArguments[0].Resolve()?.DeclaringModule != method.DeclaringModule ||
                body.LocalVariables[1].VariableType.FullName != "UnityEngine.UIElements.BaseRuntimePanel" ||
                body.LocalVariables[1].VariableType.Resolve()?.DeclaringModule != method.DeclaringModule ||
                body.LocalVariables.Skip(2).Any(local => local.VariableType is not CorLibTypeSignature { ElementType: ElementType.Boolean }) ||
                !HasOpcodes(code, [CilCode.Call, CilCode.Callvirt, CilCode.Stloc_0, CilCode.Br_S,
                    CilCode.Ldloca_S, CilCode.Call, CilCode.Castclass, CilCode.Stloc_1, CilCode.Ldloc_1,
                    CilCode.Callvirt, CilCode.Ldc_I4_0, CilCode.Ceq, CilCode.Stloc_2, CilCode.Ldloc_2,
                    CilCode.Brfalse_S, CilCode.Ldloc_1, CilCode.Call, CilCode.Ldloca_S, CilCode.Call,
                    CilCode.Brtrue_S, CilCode.Leave_S, CilCode.Ldloca_S, CilCode.Constrained, CilCode.Callvirt,
                    CilCode.Endfinally, CilCode.Ldsfld, CilCode.Ldnull, CilCode.Cgt_Un, CilCode.Stloc_3,
                    CilCode.Ldloc_3, CilCode.Brfalse_S, CilCode.Ldsfld, CilCode.Callvirt, CilCode.Ret]) ||
                !IsTarget(code, code[3].Operand, 17) || !IsTarget(code, code[14].Operand, 17) ||
                !IsTarget(code, code[19].Operand, 4) || !IsTarget(code, code[20].Operand, 25) ||
                !IsTarget(code, code[30].Operand, 33) || !IsField(code[25], field) || !IsField(code[31], field) ||
                code[4].Operand != body.LocalVariables[0] || code[17].Operand != body.LocalVariables[0] ||
                code[21].Operand != body.LocalVariables[0] || code[6].Operand is not ITypeDefOrRef panel ||
                !SignatureComparer.Default.Equals(panel.ToTypeSignature(), body.LocalVariables[1].VariableType) ||
                code[22].Operand is not ITypeDefOrRef constraint ||
                !SignatureComparer.Default.Equals(constraint.ToTypeSignature(), enumerator)) return false;
            var cleanup = body.ExceptionHandlers[0];
            if (cleanup.HandlerType != CilExceptionHandlerType.Finally || cleanup.ExceptionType != null || cleanup.FilterStart != null ||
                !IsTarget(code, cleanup.TryStart, 3) || !IsTarget(code, cleanup.TryEnd, 21) ||
                !IsTarget(code, cleanup.HandlerStart, 21) || !IsTarget(code, cleanup.HandlerEnd, 25)) return false;

            var sorted = FindMethod(method.DeclaringType, 100670849);
            var repaint = FindMethod(method.DeclaringType, 100670833);
            var draws = panel.Resolve()?.Methods.FirstOrDefault(candidate => candidate.MetadataToken.ToInt32() == 100668037);
            if (sorted?.Name != "GetSortedPlayerPanels" || !HasCallSignature(sorted.Signature, false, 0) ||
                sorted.Signature.ParameterTypes.Count != 0 || sorted.Signature.ReturnType is not GenericInstanceTypeSignature list ||
                list.IsValueType || list.TypeArguments.Count != 1 ||
                !IsCoreType(list.GenericType.ToTypeSignature(), "System.Collections.Generic.List`1") ||
                !SignatureComparer.Default.Equals(list.TypeArguments[0], enumerator.TypeArguments[0]) ||
                !HasSourceMethod(repaint, "RepaintOverlayPanel", MethodAttributes.Public, panel.ToTypeSignature()) ||
                draws?.Name != "get_drawToCameras" || !HasCallSignature(draws.Signature, true, 0) ||
                draws.Signature.ParameterTypes.Count != 0 ||
                draws.Signature.ReturnType is not CorLibTypeSignature { ElementType: ElementType.Boolean } ||
                !IsMethod(code[0], sorted) || !IsMethod(code[9], draws) || !IsMethod(code[16], repaint)) return false;
            var voidType = method.DeclaringModule.CorLibTypeFactory.Void;
            var boolType = method.DeclaringModule.CorLibTypeFactory.Boolean;
            var parameter = new GenericParameterSignature(GenericParameterType.Type, 0);
            var result = new GenericInstanceTypeSignature(enumerator.GenericType, true, parameter);
            return IsInstanceCall(code[1], list, "GetEnumerator", result) &&
                IsInstanceCall(code[5], enumerator, "get_Current", parameter) &&
                IsInstanceCall(code[18], enumerator, "MoveNext", boolType) &&
                code[23].Operand is IMethodDescriptor dispose &&
                IsCoreType(dispose.DeclaringType?.ToTypeSignature(), "System.IDisposable") &&
                IsInstanceCall(code[23], dispose.DeclaringType.ToTypeSignature(), "Dispose", voidType) &&
                IsInstanceCall(code[32], field.Signature.FieldType, "Invoke", voidType);
        }

        private static bool HasNativeLoopMethod(InteropTypeContext owner, MethodDefinition source)
        {
            var mapped = owner.TryGetMethodByUnityAssemblyMethod(source);
            return mapped?.NewMethod is { } method && mapped.NonGenericMethodInfoPointerField != null &&
                mapped.OriginalMethod.DeclaringType == owner.OriginalType &&
                mapped.OriginalMethod.IsStatic && method.DeclaringType == owner.NewType && method.IsStatic && method.IsPublic &&
                !method.IsAbstract && !method.IsPInvokeImpl && method.GenericParameters.Count == 0 &&
                HasCallSignature(method.Signature, false, 0) && method.Signature.ParameterTypes.Count == 0 &&
                method.Signature.ReturnType is CorLibTypeSignature { ElementType: ElementType.Void };
        }

        private static bool HasSourceMethod(MethodDefinition method, string name, MethodAttributes access,
            params TypeSignature[] parameters) =>
            method != null && method.Name == name && method.Attributes == (access | MethodAttributes.Static | MethodAttributes.HideBySig) &&
            method.ImplAttributes == MethodImplAttributes.IL && method.CilMethodBody != null && method.GenericParameters.Count == 0 &&
            HasCallSignature(method.Signature, false, 0) &&
            method.Signature.ReturnType is CorLibTypeSignature { ElementType: ElementType.Void } &&
            method.Signature.ParameterTypes.Count == parameters.Length && method.Parameters.Count == parameters.Length &&
            method.Signature.ParameterTypes.Select((parameter, index) =>
                SignatureComparer.Default.Equals(parameter, parameters[index])).All(matches => matches) &&
            method.Parameters.All(parameter => parameter.Definition is { } definition && definition.Attributes == 0 &&
                definition.Constant == null && definition.MarshalDescriptor == null);

        private static bool IsCompilerGenerated(CustomAttribute attribute) =>
            attribute.Constructor is { } constructor && constructor.Name == ".ctor" &&
            IsCoreType(constructor.DeclaringType?.ToTypeSignature(), "System.Runtime.CompilerServices.CompilerGeneratedAttribute") &&
            HasCallSignature(constructor.Signature, true, 0) && constructor.Signature.ParameterTypes.Count == 0 &&
            constructor.Signature.ReturnType is CorLibTypeSignature { ElementType: ElementType.Void } &&
            attribute.Signature is { } signature && signature.FixedArguments.Count == 0 && signature.NamedArguments.Count == 0;

        private static bool IsCoreType(TypeSignature type, string name)
        {
            var assembly = type?.Scope?.GetAssembly();
            return type?.FullName == name && assembly?.Name == "netstandard" && assembly.Version == new Version(2, 1, 0, 0) &&
                assembly.GetPublicKeyToken() is { } token && token.SequenceEqual(new byte[] { 0xcc, 0x7b, 0x13, 0xff, 0xcd, 0x2d, 0xdd, 0x51 });
        }

        private static bool HasCallSignature(MethodSignature signature, bool hasThis, int arity) =>
            InteropDelegateContracts.HasStandardConvention(signature) && signature.HasThis == hasThis && signature.GenericParameterCount == arity;

        private static bool IsMethodParameter(TypeSignature type) =>
            type is GenericParameterSignature { ParameterType: GenericParameterType.Method, Index: 0 };

        private static MethodDefinition FindMethod(TypeDefinition type, int token) =>
            type.Methods.FirstOrDefault(method => method.MetadataToken.ToInt32() == token);

        private static CilInstruction[] WithoutNops(CilMethodBody body) =>
            body.Instructions.Where(instruction => instruction.OpCode.Code != CilCode.Nop).ToArray();

        private static bool HasOpcodes(CilInstruction[] code, CilCode[] expected) =>
            code.Select(instruction => instruction.OpCode.Code).SequenceEqual(expected);

        private static bool IsTarget(CilInstruction[] code, object target, int index) =>
            target is CilInstructionLabel { Instruction: { } instruction } &&
            code.FirstOrDefault(candidate => candidate.Offset >= instruction.Offset) == code[index];

        private static bool IsField(CilInstruction instruction, FieldDefinition field) =>
            instruction.Operand is IFieldDescriptor reference && reference.Resolve() == field &&
            SignatureComparer.Default.Equals(reference.Signature, field.Signature);

        private static bool IsMethod(CilInstruction instruction, MethodDefinition method) =>
            method != null && instruction.Operand is IMethodDescriptor reference && reference is not MethodSpecification &&
            reference.Resolve() == method && SignatureComparer.Default.Equals(reference.Signature, method.Signature);

        private static bool IsInstanceCall(CilInstruction instruction, TypeSignature owner, string name, TypeSignature result) =>
            instruction.Operand is IMethodDescriptor method && method is not MethodSpecification && method.Name == name &&
            SignatureComparer.Default.Equals(method.DeclaringType?.ToTypeSignature(), owner) &&
            HasCallSignature(method.Signature, true, 0) && method.Signature.ParameterTypes.Count == 0 &&
            SignatureComparer.Default.Equals(method.Signature.ReturnType, result);
    }
}
