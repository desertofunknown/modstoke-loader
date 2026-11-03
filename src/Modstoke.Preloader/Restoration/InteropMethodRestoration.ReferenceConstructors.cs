// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

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
    internal sealed partial class InteropMethodRestoration
    {
        private bool TryRestoreReferenceConstructor(MethodDefinition original, MethodDefinition constructor,
            InteropTypeContext type, InteropRuntimeReferences imports, InteropUnstripTypeResolver resolver,
            InteropRestorationEntry entry)
        {
            entry.Set("signature-rejected", "native-reference-constructor-delegation",
                "The source must delegate through only argument or constant loads to an existing same-type native constructor.");
            if (type.OriginalType == null || type.OriginalType.IsValueType || type.NewType.IsValueType ||
                type.NewType.IsInterface || type.ClassPointerFieldRef == null ||
                type.ComputedTypeSpecifics != InteropTypeContext.TypeSpecifics.ReferenceType ||
                original.DeclaringType.GenericParameters.Count != 0 || type.OriginalType.GenericParameters.Count != 0 ||
                type.NewType.GenericParameters.Count != 0 ||
                resolver.Resolve(original.DeclaringType.ToTypeSignature())?.Resolve() != type.NewType)
                return false;

            if (constructor.Signature.ParameterTypes is [CorLibTypeSignature { ElementType: ElementType.I }])
            {
                entry.Set("signature-rejected", "native-pointer-constructor-reserved",
                    "The IntPtr constructor is reserved for wrapping an existing native object.");
                return false;
            }
            if (type.NewType.Methods.Any(method => method.Name == ".ctor" &&
                SignatureComparer.Default.Equals(method.Signature, constructor.Signature)))
            {
                entry.Set("signature-rejected", "native-reference-constructor-collision",
                    "A generated constructor already uses the restored signature.");
                return false;
            }

            if (InteropNativeStyleEnumType.IsPrepared(type.OriginalType))
                return TryRestoreStyleEnumConstructor(original, constructor, type, imports, resolver, entry);

            if (InteropNativeMovieTextureType.IsPrepared(type.OriginalType))
                return TryRestoreMovieTextureConstructor(original, constructor, type, imports, resolver, entry);

            if (InteropNativeWwwType.IsPrepared(type.OriginalType))
                return TryRestoreWwwConstructor(original, constructor, type, imports, resolver, entry);

            if (InteropWebFormFields.IsOwner(original.DeclaringType))
                return TryRestoreWebFormConstructor(original, constructor, type, imports, resolver, entry);

            if (InteropTilemapConstruction.IsOwner(original.DeclaringType))
                return TryRestoreTilemapConstructor(original, constructor, type, imports, resolver, entry);

            if (InteropVfxSpawnerConstruction.IsOwner(original.DeclaringType))
                return TryRestoreVfxSpawnerConstructor(original, constructor, type, imports, resolver, entry);

            if (InteropWebRequestConstruction.IsOwner(original.DeclaringType))
                return TryRestoreWebRequestConstructor(original, constructor, type, imports, resolver, entry);

            if (InteropNativeEventDebuggerTypes.IsPrepared(type.OriginalType) ||
                InteropEventDebuggerFields.IsOwner(original.DeclaringType))
                return TryRestoreEventTraceConstructor(original, constructor, type, imports, resolver, entry);

            if (original.DeclaringModule.Assembly.Name == "UnityEngine.UIElementsModule" &&
                original.DeclaringType.FullName == "UnityEngine.UIElements.PropagationPaths" &&
                original.Signature.ParameterTypes.Count == 1 &&
                SignatureComparer.Default.Equals(original.Signature.ParameterTypes[0], original.DeclaringType.ToTypeSignature()))
                return TryRestorePropagationPathsCopy(original, constructor, type, imports, resolver, entry);

            var arguments = constructor.Parameters.Select(parameter => new CilInstruction(CilOpCodes.Ldarg, parameter)).ToArray();
            var visited = new HashSet<MethodDefinition>(ReferenceEqualityComparer.Instance);
            var current = original;
            var plan = InteropRestorationPlan.For(type.AssemblyContext.GlobalContext);
            for (var depth = 0; depth < 32 && visited.Add(current); depth++)
            {
                if (!IsReferenceConstructorDeclaration(current) || current.DeclaringType != original.DeclaringType ||
                    current.Signature.ParameterTypes.Any(parameter => !IsReferenceConstructorParameter(parameter) ||
                        plan.GetNativeIdentityRejection(parameter) != null))
                    return false;

                var mapped = type.TryGetMethodByUnityAssemblyMethod(current);
                if (mapped != null)
                {
                    var native = mapped.NewMethod;
                    var parameters = current.Signature.ParameterTypes.Select(parameter => resolver.Resolve(parameter)).ToArray();
                    if (mapped.NonGenericMethodInfoPointerField == null || native.DeclaringType != type.NewType ||
                        !IsReferenceConstructorDeclaration(native) || parameters.Any(parameter => parameter == null) ||
                        !SignatureComparer.Default.Equals(native.Signature,
                            MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void, parameters)) ||
                        type.NewType.Methods.Count(method => method.Name == ".ctor" &&
                            SignatureComparer.Default.Equals(method.Signature, native.Signature)) != 1)
                        return false;

                    var body = constructor.CilMethodBody.Instructions;
                    body.Add(CilOpCodes.Ldarg_0);
                    foreach (var argument in arguments)
                        body.Add(new CilInstruction(argument.OpCode, argument.Operand));
                    // The native wrapper alone allocates, initializes the pointer/base and runs native field initialization.
                    body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(native));
                    body.Add(CilOpCodes.Ret);
                    for (var index = 0; index < constructor.Parameters.Count; index++)
                        constructor.Parameters[index].Definition.Constant = original.Parameters[index].Definition?.Constant;
                    type.NewType.Methods.Add(constructor);
                    type.AddRestoredReferenceConstructor(original, constructor);
                    entry.Set("native-reference-constructor-adapter");
                    return true;
                }

                if (!current.HasMethodBody || current.CilMethodBody.ExceptionHandlers.Count != 0 ||
                    current.CilMethodBody.LocalVariables.Count != 0)
                    return false;
                var code = current.CilMethodBody.Instructions.Where(instruction => instruction.OpCode.Code != CilCode.Nop).ToArray();
                if (code.Length < 3 || ReferenceConstructorArgumentIndex(code[0]) != 0 ||
                    code[^1].OpCode.Code != CilCode.Ret || code[^2].OpCode.Code != CilCode.Call ||
                    code[^2].Operand is not IMethodDescriptor call || call is MethodSpecification ||
                    call.DeclaringType?.Resolve() != original.DeclaringType ||
                    call.Resolve() is not { } next || !IsReferenceConstructorDeclaration(next) ||
                    !SignatureComparer.Default.Equals(call.Signature, next.Signature) ||
                    code.Length != next.Signature.ParameterTypes.Count + 3)
                    return false;

                var forwarded = new CilInstruction[next.Signature.ParameterTypes.Count];
                for (var index = 0; index < forwarded.Length; index++)
                {
                    var load = code[index + 1];
                    var parameterType = next.Signature.ParameterTypes[index];
                    var argumentIndex = ReferenceConstructorArgumentIndex(load);
                    if (argumentIndex > 0 && argumentIndex <= arguments.Length)
                    {
                        if (!SignatureComparer.Default.Equals(current.Signature.ParameterTypes[argumentIndex - 1], parameterType))
                            return false;
                        forwarded[index] = arguments[argumentIndex - 1];
                    }
                    else if (!TryReferenceConstructorConstant(current, load, parameterType, resolver, out forwarded[index]))
                        return false;
                }
                // Loads cannot mutate parameters; substitution preserves caller evaluation and repeated/reordered arguments.
                arguments = forwarded;
                current = next;
            }
            entry.Set("signature-rejected", "native-reference-constructor-chain",
                "The delegation chain is cyclic or exceeds 32 constructors.");
            return false;
        }

        private static bool IsReferenceConstructorDeclaration(MethodDefinition method) =>
            method.Name == ".ctor" && method.IsConstructor && !method.IsStatic && !method.IsAbstract &&
            !method.IsVirtual && !method.IsPInvokeImpl && method.Signature.HasThis &&
            method.GenericParameters.Count == 0 && method.Signature.GenericParameterCount == 0 &&
            method.Signature.ReturnType.ElementType == ElementType.Void &&
            InteropDelegateContracts.HasStandardConvention(method.Signature) &&
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
                MethodImplAttributes.Synchronized | MethodImplAttributes.InternalCall)) == MethodImplAttributes.IL;

        private static bool IsReferenceConstructorParameter(TypeSignature parameter) =>
            parameter is CorLibTypeSignature
                { ElementType: >= ElementType.Boolean and <= ElementType.R8 or ElementType.I or ElementType.U or
                    ElementType.String or ElementType.Object } ||
            parameter is TypeDefOrRefSignature && parameter.Resolve() is
                { IsValueType: false, IsInterface: false, GenericParameters.Count: 0 };

        private static int ReferenceConstructorArgumentIndex(CilInstruction instruction) => instruction.OpCode.Code switch
        {
            CilCode.Ldarg_0 => 0,
            CilCode.Ldarg_1 => 1,
            CilCode.Ldarg_2 => 2,
            CilCode.Ldarg_3 => 3,
            CilCode.Ldarg or CilCode.Ldarg_S when instruction.Operand is Parameter parameter => parameter.MethodSignatureIndex,
            _ => -1
        };

        private static bool TryReferenceConstructorConstant(MethodDefinition method, CilInstruction load,
            TypeSignature parameter, InteropUnstripTypeResolver resolver, out CilInstruction constant)
        {
            constant = null;
            var rewritten = resolver.Resolve(parameter);
            if (rewritten == null) return false;
            if (load.OpCode.Code == CilCode.Ldnull && !parameter.IsValueType && !rewritten.IsValueType &&
                IsReferenceConstructorParameter(parameter))
                constant = new CilInstruction(CilOpCodes.Ldnull);
            else if (parameter is CorLibTypeSignature { ElementType: ElementType.String } &&
                rewritten is CorLibTypeSignature { ElementType: ElementType.String })
            {
                if (load.OpCode.Code == CilCode.Ldstr && load.Operand is string text)
                    constant = new CilInstruction(CilOpCodes.Ldstr, text);
                // String.Empty is the sole permitted field read; arbitrary static fields can run initializers.
                else if (load.OpCode.Code == CilCode.Ldsfld && load.Operand is IFieldDescriptor field &&
                    field.Name == "Empty" && SignatureComparer.Default.Equals(field.DeclaringType,
                        method.DeclaringModule.CorLibTypeFactory.String.Type) &&
                    SignatureComparer.Default.Equals(field.Signature?.FieldType, method.DeclaringModule.CorLibTypeFactory.String))
                    constant = new CilInstruction(CilOpCodes.Ldstr, string.Empty);
            }
            else if (parameter is CorLibTypeSignature && SignatureComparer.Default.Equals(parameter, rewritten))
            {
                var matches = load.OpCode.Code switch
                {
                    CilCode.Ldc_I4_M1 or CilCode.Ldc_I4_0 or CilCode.Ldc_I4_1 or CilCode.Ldc_I4_2 or
                        CilCode.Ldc_I4_3 or CilCode.Ldc_I4_4 or CilCode.Ldc_I4_5 or CilCode.Ldc_I4_6 or
                        CilCode.Ldc_I4_7 or CilCode.Ldc_I4_8 or CilCode.Ldc_I4 or CilCode.Ldc_I4_S =>
                        parameter.ElementType is >= ElementType.Boolean and <= ElementType.U4,
                    CilCode.Ldc_I8 => parameter.ElementType is ElementType.I8 or ElementType.U8,
                    CilCode.Ldc_R4 => parameter.ElementType == ElementType.R4,
                    CilCode.Ldc_R8 => parameter.ElementType == ElementType.R8,
                    _ => false
                };
                if (matches) constant = new CilInstruction(load.OpCode, load.Operand);
            }
            return constant != null;
        }
    }
}
