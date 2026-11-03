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
    internal static class InteropWebRequestConstruction
    {
        private const string ModuleName = "UnityEngine.UnityWebRequestModule";
        private const string OwnerName = "UnityEngine.Networking.UnityWebRequest";

        internal static bool IsOwner(TypeDefinition type) =>
            type?.FullName == OwnerName && type.DeclaringModule.Assembly.Name == ModuleName;

        internal static bool TryGetPointerConstructor(MethodDefinition source, InteropTypeContext owner,
            InteropRuntimeReferences imports, InteropUnstripTypeResolver resolver, out MethodDefinition pointerConstructor)
        {
            pointerConstructor = null;
            var type = source?.DeclaringType;
            var count = source?.MetadataToken.ToInt32() switch { 0x06000091 => 0, 0x06000093 => 1, 0x06000095 => 2, _ => -1 };
            if (count < 0 || !IsOwner(type) || type.DeclaringModule.Mvid != new Guid("e3e15492-8f90-4bae-ba37-bfac955d6ba2") ||
                type.MetadataToken.ToInt32() != 0x0200000E || type.Attributes != (TypeAttributes.Public | TypeAttributes.SequentialLayout | TypeAttributes.BeforeFieldInit) ||
                type.DeclaringType != null || type.GenericParameters.Count != 0 || type.ClassLayout != null ||
                !IsSystemType(type.BaseType, "System.Object") || type.Interfaces.Count != 1 ||
                !IsSystemType(type.Interfaces[0].Interface, "System.IDisposable") ||
                !HasSourceMethod(source, ".ctor", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RuntimeSpecialName, count) ||
                source.Signature.ReturnType.ElementType != ElementType.Void || !HasSimpleBody(source) || source.CilMethodBody.InitializeLocals ||
                count > 0 && !IsSystemType(source.Signature.ParameterTypes[0].ToTypeDefOrRef(), "System.Uri") ||
                count == 2 && source.Signature.ParameterTypes[1] is not CorLibTypeSignature { ElementType: ElementType.String } ||
                source.Parameters.Any(parameter => parameter.Definition is not { Attributes: 0, Constant: null, MarshalDescriptor: null }) ||
                owner.OriginalType is not { IsValueType: false, IsInterface: false, IsAbstract: false, IsSealed: false } native ||
                !IsOwner(native) || native.Attributes != type.Attributes || native.DeclaringType != null || native.GenericParameters.Count != 0 ||
                native.BaseType?.FullName != "System.Object" || native.Interfaces.Count != 1 || native.Interfaces[0].Interface?.FullName != "System.IDisposable" ||
                owner.ClassPointerFieldRef == null || owner.ComputedTypeSpecifics != InteropTypeContext.TypeSpecifics.ReferenceType ||
                owner.NewType.FullName != OwnerName || owner.NewType.DeclaringType != null || owner.NewType.IsSealed ||
                owner.NewType.IsValueType || owner.NewType.IsInterface || owner.NewType.IsAbstract || owner.NewType.GenericParameters.Count != 0 ||
                owner.NewType.DeclaringModule != imports.Module || owner.NewType.BaseType?.FullName != "Il2CppSystem.Object" ||
                resolver.ResolveRaw(type.ToTypeSignature())?.Resolve() != owner.NewType || owner.TryGetMethodByUnityAssemblyMethod(source) != null)
                return false;

            var corlib = type.DeclaringModule.CorLibTypeFactory;
            var create = FindMethod(type, 0x0600008D);
            var defaults = FindMethod(type, 0x06000090);
            var uri = FindMethod(type, 0x060000B0);
            var method = FindMethod(type, 0x060000A6);
            if (create == null || create.Name != "Create" ||
                create.Attributes != (MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig) ||
                create.ImplAttributes != MethodImplAttributes.InternalCall || create.CilMethodBody != null || create.GenericParameters.Count != 0 || create.CustomAttributes.Count != 0 ||
                !SignatureComparer.Default.Equals(create.Signature, MethodSignature.CreateStatic(corlib.IntPtr)) ||
                !HasSourceMethod(defaults, "InternalSetDefaults", MethodAttributes.Private, 0) || !HasSimpleBody(defaults) ||
                !HasSourceMethod(uri, "set_uri", MethodAttributes.Public | MethodAttributes.SpecialName, 1) ||
                !IsSystemType(uri.Signature.ParameterTypes[0].ToTypeDefOrRef(), "System.Uri") ||
                !HasSourceMethod(method, "set_method", MethodAttributes.Public | MethodAttributes.SpecialName, 1) ||
                method.Signature.ParameterTypes[0] is not CorLibTypeSignature { ElementType: ElementType.String } ||
                defaults.Signature.ReturnType.ElementType != ElementType.Void || uri.Signature.ReturnType.ElementType != ElementType.Void ||
                method.Signature.ReturnType.ElementType != ElementType.Void || !HasNativeMethod(defaults, owner, resolver) ||
                !HasNativeMethod(uri, owner, resolver) || !HasNativeMethod(method, owner, resolver) ||
                !HasFields(type, owner, resolver) || !HasCleanup(type, owner, resolver)) return false;
            if (count > 0 && (resolver.Resolve(source.Signature.ParameterTypes[0]) is not TypeDefOrRefSignature { IsValueType: false } nativeUri ||
                nativeUri.FullName != "Il2CppSystem.Uri" || nativeUri.Resolve() == null)) return false;

            var defaultCode = WithoutNops(defaults);
            if (!HasOpcodes(defaultCode, [CilCode.Ldarg_0, CilCode.Ldc_I4_1, CilCode.Call, CilCode.Ldarg_0,
                CilCode.Ldc_I4_1, CilCode.Call, CilCode.Ldarg_0, CilCode.Ldc_I4_1, CilCode.Call, CilCode.Ret])) return false;
            int[] setters = [0x06000087, 0x06000089, 0x06000085];
            string[] names = ["set_disposeDownloadHandlerOnDispose", "set_disposeUploadHandlerOnDispose", "set_disposeCertificateHandlerOnDispose"];
            for (var index = 0; index < setters.Length; index++)
            {
                var setter = FindMethod(type, setters[index]);
                if (!HasSourceMethod(setter, names[index], MethodAttributes.Public | MethodAttributes.SpecialName, 1, true) ||
                    !SignatureComparer.Default.Equals(setter.Signature, MethodSignature.CreateInstance(corlib.Void, corlib.Boolean)) ||
                    !Calls(defaultCode[index * 3 + 2], setter) || !HasNativeMethod(setter, owner, resolver)) return false;
            }

            var code = source.CilMethodBody.Instructions;
            CilCode[] prefix = [CilCode.Ldarg_0, CilCode.Call, CilCode.Nop, CilCode.Nop, CilCode.Ldarg_0, CilCode.Call,
                CilCode.Stfld, CilCode.Ldarg_0, CilCode.Call, CilCode.Nop];
            CilCode[] suffix = count switch
            {
                0 => [CilCode.Ret],
                1 => [CilCode.Ldarg_0, CilCode.Ldarg_1, CilCode.Call, CilCode.Nop, CilCode.Ret],
                _ => [CilCode.Ldarg_0, CilCode.Ldarg_1, CilCode.Call, CilCode.Nop, CilCode.Ldarg_0, CilCode.Ldarg_2, CilCode.Call, CilCode.Nop, CilCode.Ret]
            };
            if (!code.Select(instruction => instruction.OpCode.Code).SequenceEqual(prefix.Concat(suffix)) ||
                !CallsSystem(code[1], "System.Object", ".ctor", true, corlib.Void) || !Calls(code[5], create) ||
                code[6].Operand is not IFieldDescriptor pointer || pointer.Resolve() != type.Fields[0] ||
                !SignatureComparer.Default.Equals(pointer.Signature, type.Fields[0].Signature) || code[6].Offset != 0x0E ||
                code[7].Offset != 0x13 || !Calls(code[8], defaults) ||
                count > 0 && (!Calls(code[12], uri) || code[12].Offset != 0x1C) ||
                count == 2 && (!Calls(code[16], method) || code[16].Offset != 0x24)) return false;

            var signature = MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void, imports.Module.CorLibTypeFactory.IntPtr);
            var pointers = owner.NewType.Methods.Where(candidate => candidate.Name == ".ctor" &&
                SignatureComparer.Default.Equals(candidate.Signature, signature)).ToArray();
            if (pointers.Length != 1 || pointers[0] is not { IsPublic: true, IsStatic: false, IsVirtual: false, IsAbstract: false, IsConstructor: true } target ||
                target.IsPInvokeImpl || target.ImplAttributes != MethodImplAttributes.IL || target.GenericParameters.Count != 0 || !HasSimpleBody(target)) return false;
            var wrapper = WithoutNops(target);
            if (!HasOpcodes(wrapper, [CilCode.Ldarg_0, CilCode.Ldarg_1, CilCode.Call, CilCode.Ret]) ||
                wrapper[2].Operand is not IMethodDescriptor baseCall || baseCall is MethodSpecification || baseCall.Name != ".ctor" ||
                !SignatureComparer.Default.Equals(baseCall.DeclaringType, owner.NewType.BaseType) ||
                !SignatureComparer.Default.Equals(baseCall.Signature, signature)) return false;
            pointerConstructor = target;
            return true;
        }

        private static bool HasFields(TypeDefinition source, InteropTypeContext owner, InteropUnstripTypeResolver resolver)
        {
            string[] names = ["m_Ptr", "m_DownloadHandler", "m_UploadHandler", "m_CertificateHandler", "m_Uri",
                "kHttpVerbGET", "kHttpVerbHEAD", "kHttpVerbPOST", "kHttpVerbPUT", "kHttpVerbCREATE", "kHttpVerbDELETE",
                "<disposeCertificateHandlerOnDispose>k__BackingField", "<disposeDownloadHandlerOnDispose>k__BackingField", "<disposeUploadHandlerOnDispose>k__BackingField"];
            string[] types = ["System.IntPtr", "UnityEngine.Networking.DownloadHandler", "UnityEngine.Networking.UploadHandler",
                "UnityEngine.Networking.CertificateHandler", "System.Uri"];
            if (source.Fields.Count != names.Length || owner.OriginalType.Fields.Count != names.Length) return false;
            for (var index = 0; index < names.Length; index++)
            {
                var field = source.Fields[index];
                var literal = index is >= 5 and <= 10;
                var attributes = literal ? FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault
                    : index < 5 ? FieldAttributes.Assembly | FieldAttributes.NotSerialized : FieldAttributes.Private;
                if (field.MetadataToken.ToInt32() != 0x04000024 + index || field.Name != names[index] || field.Attributes != attributes ||
                    field.Signature == null || field.Signature.HasThis || field.FieldRva != null || field.FieldOffset != null || field.HasFieldMarshal ||
                    field.MarshalDescriptor != null || (literal ? field.Constant == null : field.Constant != null) ||
                    field.Signature.FieldType.FullName != (index < 5 ? types[index] : literal ? "System.String" : "System.Boolean")) return false;
                if (index >= 11)
                {
                    if (field.CustomAttributes.Count != 2 ||
                        !field.CustomAttributes.Any(attribute => attribute.Constructor?.DeclaringType?.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute") ||
                        !field.CustomAttributes.Any(attribute => attribute.Constructor?.DeclaringType?.FullName == "System.Diagnostics.DebuggerBrowsableAttribute")) return false;
                }
                else if (field.CustomAttributes.Count != 0) return false;
                var matches = owner.OriginalType.Fields.Where(candidate => candidate.Name == field.Name).ToArray();
                if (matches.Length != 1 || matches[0].Attributes != attributes || matches[0].Signature?.FieldType.FullName != field.Signature.FieldType.FullName) return false;
                if (literal) continue;
                var mapped = owner.TryGetFieldByUnityAssemblyField(field);
                if (mapped?.PointerField == null || mapped.DeclaringType != owner || mapped.OriginalField != matches[0] ||
                    owner.NewType.Fields.Any(candidate => candidate.Name == field.Name || candidate.Name == mapped.UnmangledName) ||
                    resolver.Resolve(field.Signature.FieldType) == null) return false;
            }
            return true;
        }

        private static bool HasCleanup(TypeDefinition type, InteropTypeContext owner, InteropUnstripTypeResolver resolver)
        {
            var destroy = FindMethod(type, 0x0600008F);
            var finalizer = FindMethod(type, 0x06000098);
            var dispose = FindMethod(type, 0x06000099);
            var handlers = FindMethod(type, 0x0600009A);
            if (!HasSourceMethod(destroy, "InternalDestroy", MethodAttributes.Assembly, 0) ||
                !HasSourceMethod(finalizer, "Finalize", MethodAttributes.Family | MethodAttributes.Virtual, 0) ||
                !HasSourceMethod(dispose, "Dispose", MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.Virtual | MethodAttributes.NewSlot, 0) ||
                !HasSourceMethod(handlers, "DisposeHandlers", MethodAttributes.Private, 0) ||
                new[] { destroy, finalizer, dispose, handlers }.Any(method => method.Signature.ReturnType.ElementType != ElementType.Void ||
                    !HasNativeMethod(method, owner, resolver)) || !owner.TryGetMethodByUnityAssemblyMethod(finalizer).OriginalMethod.IsVirtual ||
                owner.OriginalType.Methods.Count(method => method.Name == "Finalize" && !method.IsStatic) != 1) return false;
            var corlib = type.DeclaringModule.CorLibTypeFactory;
            var code = WithoutNops(finalizer);
            if (finalizer.CilMethodBody.LocalVariables.Count != 0 || finalizer.CilMethodBody.ExceptionHandlers.Count != 1 ||
                !HasOpcodes(code, [CilCode.Ldarg_0, CilCode.Call, CilCode.Ldarg_0, CilCode.Call, CilCode.Leave_S,
                    CilCode.Ldarg_0, CilCode.Call, CilCode.Endfinally, CilCode.Ret]) ||
                !Calls(code[1], handlers) || !Calls(code[3], destroy) || !Targets(code[4], code[8]) ||
                !CallsSystem(code[6], "System.Object", "Finalize", true, corlib.Void)) return false;
            var cleanup = finalizer.CilMethodBody.ExceptionHandlers[0];
            if (cleanup.HandlerType != CilExceptionHandlerType.Finally || cleanup.ExceptionType != null || cleanup.FilterStart != null ||
                cleanup.TryStart?.Offset != 1 || cleanup.TryEnd?.Offset != 0x12 || cleanup.HandlerStart?.Offset != 0x12 || cleanup.HandlerEnd?.Offset != 0x1A) return false;
            code = WithoutNops(dispose);
            if (!HasSimpleBody(dispose) || !HasOpcodes(code, [CilCode.Ldarg_0, CilCode.Call, CilCode.Ldarg_0, CilCode.Call,
                    CilCode.Ldarg_0, CilCode.Call, CilCode.Ret]) || !Calls(code[1], handlers) || !Calls(code[3], destroy) ||
                !CallsSystem(code[5], "System.GC", "SuppressFinalize", false, corlib.Void, corlib.Object)) return false;

            code = WithoutNops(destroy);
            if (destroy.CilMethodBody.LocalVariables.Count != 1 || destroy.CilMethodBody.ExceptionHandlers.Count != 0 ||
                destroy.CilMethodBody.LocalVariables[0].VariableType is not CorLibTypeSignature { ElementType: ElementType.Boolean } ||
                !HasOpcodes(code, [CilCode.Ldarg_0, CilCode.Ldfld, CilCode.Ldsfld, CilCode.Call, CilCode.Stloc_0, CilCode.Ldloc_0,
                    CilCode.Brfalse_S, CilCode.Ldarg_0, CilCode.Call, CilCode.Ldarg_0, CilCode.Call, CilCode.Ldarg_0,
                    CilCode.Ldsfld, CilCode.Stfld, CilCode.Ret]) || !Targets(code[6], code[14]) ||
                code[1].Operand is not IFieldDescriptor read || read.Resolve() != type.Fields[0] ||
                code[13].Operand is not IFieldDescriptor write || write.Resolve() != type.Fields[0] ||
                !IsZero(code[2]) || !IsZero(code[12]) ||
                !CallsSystem(code[3], "System.IntPtr", "op_Inequality", false, corlib.Boolean, corlib.IntPtr, corlib.IntPtr)) return false;
            foreach (var index in new[] { 8, 10 })
                if (code[index].Operand is not IMethodDescriptor call || call is MethodSpecification ||
                    call.Name != (index == 8 ? "Abort" : "Release") || call.Resolve() is not { } target || target.DeclaringType != type ||
                    !SignatureComparer.Default.Equals(call.Signature, MethodSignature.CreateInstance(corlib.Void)) ||
                    !HasNativeMethod(target, owner, resolver)) return false;

            // The retained native handler cleanup must still select and dispose the same three handler kinds in order.
            string[] calls = ["get_disposeDownloadHandlerOnDispose", "get_downloadHandler", "Dispose",
                "get_disposeUploadHandlerOnDispose", "get_uploadHandler", "Dispose",
                "get_disposeCertificateHandlerOnDispose", "get_certificateHandler", "Dispose"];
            var referenced = handlers.CilMethodBody.Instructions.Where(instruction => instruction.OpCode.Code is CilCode.Call or CilCode.Callvirt).ToArray();
            if (handlers.CilMethodBody.ExceptionHandlers.Count != 0 || handlers.CilMethodBody.LocalVariables.Count != 9 || referenced.Length != calls.Length) return false;
            for (var index = 0; index < calls.Length; index++)
            {
                if (referenced[index].Operand is not IMethodDescriptor call || call is MethodSpecification || call.Name != calls[index] ||
                    call.Resolve() is not { } target || !SignatureComparer.Default.Equals(call.Signature, target.Signature)) return false;
                if (index % 3 != 2)
                {
                    if (target.DeclaringType != type || !HasNativeMethod(target, owner, resolver)) return false;
                }
                else if (call.DeclaringType?.FullName != new[] { "UnityEngine.Networking.DownloadHandler", "UnityEngine.Networking.UploadHandler",
                    "UnityEngine.Networking.CertificateHandler" }[index / 3] ||
                    !SignatureComparer.Default.Equals(call.Signature, MethodSignature.CreateInstance(corlib.Void))) return false;
            }
            return true;
        }

        private static bool HasNativeMethod(MethodDefinition source, InteropTypeContext owner, InteropUnstripTypeResolver resolver)
        {
            var mapped = owner.TryGetMethodByUnityAssemblyMethod(source);
            if (mapped?.NonGenericMethodInfoPointerField == null || mapped.OriginalMethod.DeclaringType != owner.OriginalType ||
                mapped.OriginalMethod.Attributes != source.Attributes || mapped.OriginalMethod.GenericParameters.Count != 0 ||
                mapped.NewMethod.DeclaringType != owner.NewType || mapped.NewMethod.IsStatic || mapped.NewMethod.IsAbstract || mapped.NewMethod.IsPInvokeImpl ||
                mapped.NewMethod.CilMethodBody == null || mapped.NewMethod.GenericParameters.Count != 0 ||
                owner.NewType.Methods.Count(method => method.Name == mapped.NewMethod.Name &&
                    SignatureComparer.Default.Equals(method.Signature, mapped.NewMethod.Signature)) != 1) return false;
            var arguments = source.Signature.ParameterTypes.Select(resolver.Resolve).ToArray();
            var result = resolver.Resolve(source.Signature.ReturnType);
            return result != null && arguments.All(argument => argument != null) && SignatureComparer.Default.Equals(mapped.NewMethod.Signature,
                MethodSignature.CreateInstance(result, arguments));
        }

        private static bool HasSourceMethod(MethodDefinition method, string name, MethodAttributes attributes, int parameters, bool compilerGenerated = false) =>
            method != null && method.Name == name && method.Attributes == (attributes | MethodAttributes.HideBySig) &&
            method.ImplAttributes == MethodImplAttributes.IL && method.CilMethodBody != null && method.ImplementationMap == null &&
            method.GenericParameters.Count == 0 && (compilerGenerated
                ? method.CustomAttributes.Count == 1 && method.CustomAttributes[0].Constructor?.DeclaringType?.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute"
                : method.CustomAttributes.Count == 0) &&
            InteropDelegateContracts.HasStandardConvention(method.Signature) && method.Signature.HasThis &&
            method.Signature.GenericParameterCount == 0 && method.Signature.ParameterTypes.Count == parameters;

        private static bool HasSimpleBody(MethodDefinition method) => method.CilMethodBody is { } body &&
            body.LocalVariables.Count == 0 && body.ExceptionHandlers.Count == 0;
        private static MethodDefinition FindMethod(TypeDefinition type, int token) => type.Methods.FirstOrDefault(method => method.MetadataToken.ToInt32() == token);
        private static CilInstruction[] WithoutNops(MethodDefinition method) => method.CilMethodBody.Instructions.Where(instruction => instruction.OpCode.Code != CilCode.Nop).ToArray();
        private static bool HasOpcodes(CilInstruction[] code, CilCode[] expected) => code.Select(instruction => instruction.OpCode.Code).SequenceEqual(expected);
        private static bool Calls(CilInstruction instruction, MethodDefinition method) => instruction.Operand is IMethodDescriptor call && call is not MethodSpecification &&
            call.Resolve() == method && SignatureComparer.Default.Equals(call.Signature, method.Signature);
        private static bool Targets(CilInstruction instruction, CilInstruction target) => instruction.Operand is CilInstructionLabel label && label.Instruction == target;
        private static bool IsSystemType(ITypeDescriptor type, string name) => type is ITypeDefOrRef reference && reference.FullName == name && InteropArraySegmentLowering.HasSourceContract(reference);
        private static bool IsZero(CilInstruction instruction) => instruction.Operand is IFieldDescriptor field && field.Name == "Zero" &&
            IsSystemType(field.DeclaringType, "System.IntPtr") && field.Signature?.FieldType is CorLibTypeSignature { ElementType: ElementType.I };
        private static bool CallsSystem(CilInstruction instruction, string type, string name, bool hasThis, TypeSignature result, params TypeSignature[] parameters) =>
            instruction.Operand is IMethodDescriptor call && call is not MethodSpecification && call.Name == name && IsSystemType(call.DeclaringType, type) &&
            SignatureComparer.Default.Equals(call.Signature, hasThis ? MethodSignature.CreateInstance(result, parameters) : MethodSignature.CreateStatic(result, parameters));
    }
}
