// SPDX-License-Identifier: GPL-3.0-or-later
// Restored IL translation adapted from Il2CppInterop UnstripTranslator (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private CilInstruction TranslateField(CilOpCode code, IFieldDescriptor field)
        {
            if (TryTranslateWebFormField(code, field, out var formField))
                return formField;
            if (TryTranslateEventDebuggerField(code, field, out var debuggerField))
                return debuggerField;
            if (InteropManagedCollectionLowering.IsStorage(context, field.DeclaringType?.ToTypeSignature()))
                return FailInstruction("managed-collection-private-field", "Collection operations require the public CLR contract, not runtime-specific fields.");
            if (InteropArraySegmentLowering.TryGetStorage(context, field.DeclaringType?.ToTypeSignature(), out _, out _))
                return FailInstruction("managed-array-segment-private-field", "Array segment operations require the public CLR contract, not runtime-specific fields.");
            if (InteropSpanLowering.TryGetSpan(context, field.DeclaringType?.ToTypeSignature(), out _, out _))
                return FailInstruction("managed-span-private-field", "Span operations must use the public CLR contract, not runtime-specific fields.");
            if (InteropNativeArrayLowering.TryGetStorage(context, field.DeclaringType?.ToTypeSignature(), out _, out _))
                return FailInstruction("native-array-storage-private-field", "Array internals require the explicit storage accessor contract.");
            var declarer = resolver.ResolveRaw(field.DeclaringType.ToTypeSignature(), field.Signature?.HasThis ?? true);
            var definition = declarer?.Resolve();
            if (definition == null)
                return FailInstruction("unresolved-field-declaring-type", "Could not resolve the field declaring type: " +
                    FormatDiagnostic(field.DeclaringType));

            var type = context.GetContextForNewType(definition);
            var propertyName = type.Fields.SingleOrDefault(candidate => candidate.OriginalField.Name == field.Name &&
                candidate.OriginalField.Signature.FieldType.FullName == field.Signature.FieldType.FullName)?.UnmangledName;
            var rewrittenField = definition.Fields.SingleOrDefault(candidate => candidate.Name == field.Name)
                ?? definition.Fields.SingleOrDefault(candidate => candidate.Name == propertyName);
            if (rewrittenField == null && InteropManagedCollectionLowering.IsStorage(context,
                field.Signature?.FieldType.InstantiateGenericTypes(GenericContext.FromField(field))))
                return FailInstruction("managed-collection-native-field",
                    "A CLR collection requires an actual managed field, not a native field accessor.");
            if (rewrittenField != null)
                // Importing the definition alone discards a constructed owner's generic arguments.
                return body.Add(code, imports.Module.DefaultImporter.ImportField(new MemberReference(
                    declarer.ToTypeDefOrRef(), rewrittenField.Name, rewrittenField.Signature)));
            if (propertyName == null)
                return FailInstruction("unresolved-field", "No rewritten field or property mapping was found.");

            MethodDefinition accessor;
            if (code.Code is CilCode.Ldfld or CilCode.Ldsfld)
                accessor = definition.Properties.SingleOrDefault(property => property.Name == propertyName)?.GetMethod;
            else if (code.Code is CilCode.Stfld or CilCode.Stsfld)
                accessor = definition.Properties.SingleOrDefault(property => property.Name == propertyName)?.SetMethod;
            else
                return FailInstruction(code.Code is CilCode.Ldflda or CilCode.Ldsflda
                    ? "unsupported-field-address" : "unsupported-field-opcode",
                    "The rewritten property cannot represent this field operation.");

            return accessor == null
                ? FailInstruction("unresolved-field-accessor", "No " +
                    (code.Code is CilCode.Ldfld or CilCode.Ldsfld ? "getter" : "setter") +
                    " was found for rewritten property " + FormatDiagnostic(propertyName) + ".")
                : body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(new MemberReference(
                    declarer.ToTypeDefOrRef(), accessor.Name, accessor.Signature)));
        }

        private CilInstruction TranslateMethod(CilOpCode code, IMethodDescriptor method)
        {
            if (TryTranslateClrMath(code, method, out var mathCall)) return mathCall;
            if (TryTranslateMovieTextureBaseConstructor(code, method, out var movieInitializer)) return movieInitializer;
            if (TryTranslateWwwBaseConstructor(code, method, out var wwwInitializer)) return wwwInitializer;
            if (TryTranslateWebFormBaseConstructor(code, method, out var formInitializer)) return formInitializer;
            if (TryTranslateTilemapBaseConstructor(code, method, out var tilemapInitializer)) return tilemapInitializer;
            if (TryTranslateEventTraceBaseConstructor(code, method, out var traceInitializer)) return traceInitializer;
            if (TryTranslateStackValueAddress(code, method, out var stackAddress)) return stackAddress;
            if (TryTranslateManagedDictionaryMethod(code, method, out var dictionaryCall)) return dictionaryCall;
            if (TryTranslateManagedCollectionMethod(code, method, out var collectionCall)) return collectionCall;
            if (TryTranslateSpanMethod(code, method, out var spanCall)) return spanCall;
            if (TryTranslateArraySegmentMethod(code, method, out var segmentCall)) return segmentCall;
            if (TryTranslateNativeArrayStorageMethod(code, method, out var storageCall)) return storageCall;
            // Native location descriptors are handled before this ordinary CLR-reference path.
            if (code.Code == CilCode.Call && InteropAtomicBridge.TryBind(method, imports.Module,
                element => resolver.Resolve(element), out var atomicBridge))
                return body.Add(CilOpCodes.Call, atomicBridge);
            if (IsManagedObjectConstructor(code, method))
                return body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(typeof(object).GetConstructor(System.Type.EmptyTypes)));
            if (IsManagedExceptionConstructor(code, method))
                return body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(typeof(System.Exception).GetConstructor(
                    method.Signature.ParameterTypes.Count == 0 ? System.Type.EmptyTypes : new[] { typeof(string) })));
            if (ownerType.OriginalType == null && code.Code == CilCode.Call &&
                method is MethodSpecification factory && method.Name == "CreateInstance" &&
                method.DeclaringType.FullName == "System.Activator" && method.Signature.ParameterTypes.Count == 0 &&
                method.Signature.GenericParameterCount == 1 && factory.Signature.TypeArguments.Count == 1 &&
                InteropRestoredTypeSupport.GetAssemblyName(method.DeclaringType.ToTypeSignature()) is
                    "mscorlib" or "netstandard" or "System.Runtime" or "System.Private.CoreLib")
            {
                var argument = resolver.Resolve(factory.Signature.TypeArguments[0]);
                if (argument == null) return FailInstruction("managed-factory-type-unavailable", "The managed new() argument cannot be resolved.");
                var factoryMethod = typeof(System.Activator).GetMethods().Single(candidate =>
                    candidate.Name == "CreateInstance" && candidate.IsGenericMethodDefinition && candidate.GetParameters().Length == 0);
                return body.Add(CilOpCodes.Call,
                    ((IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(factoryMethod)).MakeGenericInstanceMethod(argument));
            }
            var declarer = resolver.Resolve(method.DeclaringType?.ToTypeSignature(), method.Signature?.HasThis ?? true);
            if (declarer == null)
                return FailInstruction("unresolved-method-declaring-type", "Could not resolve the method declaring type: " +
                    FormatDiagnostic(method.DeclaringType));
            var returnType = resolver.Resolve(method.Signature?.ReturnType);
            if (returnType == null)
                return FailInstruction("unresolved-method-return-type", "Could not resolve the method return type: " +
                    FormatDiagnostic(method.Signature?.ReturnType));

            var signature = method.Signature.HasThis
                ? MethodSignature.CreateInstance(returnType, method.Signature.GenericParameterCount)
                : MethodSignature.CreateStatic(returnType, method.Signature.GenericParameterCount);
            foreach (var parameter in method.Signature.ParameterTypes)
            {
                var parameterType = resolver.Resolve(parameter);
                if (parameterType == null)
                    return FailInstruction("unresolved-method-parameter-type", "Could not resolve parameter " +
                        signature.ParameterTypes.Count + " type: " + FormatDiagnostic(parameter));
                signature.ParameterTypes.Add(parameterType);
            }

            var methodName = method.Name;
            var originalDefinition = method.Resolve();
            var resolvedDeclarer = declarer.Resolve();
            InteropTypeContext declaredContext = null;
            var hasMappedNativeTarget = false;
            var mappedWithoutSource = false;
            if (resolvedDeclarer != null)
            {
                var mappedType = originalDefinition == null ? FindMappedSourceOwner(method.DeclaringType?.ToTypeSignature()) :
                    context.TryGetAssemblyByName(originalDefinition.DeclaringModule.Assembly.Name)
                        ?.TryGetTypeByName(originalDefinition.DeclaringType.FullName);
                var mapped = mappedType?.NewType == resolvedDeclarer
                    ? originalDefinition == null ? FindMappedNativeMethod(method, mappedType) :
                        mappedType.TryGetMethodByUnityAssemblyMethod(originalDefinition) : null;
                if (mappedType?.NewType == resolvedDeclarer) declaredContext = mappedType;
                if (mapped != null)
                {
                    hasMappedNativeTarget = true;
                    var target = mapped.NewMethod;
                    if (code.Code == CilCode.Call && mapped.OriginalMethod.IsVirtual &&
                        !mapped.OriginalMethod.IsFinal && !mapped.OriginalMethod.DeclaringType.IsSealed &&
                        !InteropTypeReferences.IsValueType(mapped.OriginalMethod.DeclaringType))
                    {
                        target = InteropMethodBodies.GetDirectCallAdapter(mapped);
                        if (target == null)
                            return FailInstruction("native-direct-call-unsupported",
                                "The native base call requires a concrete, non-generic reference method: " + method.FullName);
                    }
                    methodName = target.Name;
                    signature = target.Signature;
                    mappedWithoutSource = originalDefinition == null;
                }
                else if (originalDefinition == null && declaredContext?.OriginalType != null)
                {
                    return FailInstruction("native-method-unavailable",
                        "The mapped native owner has no unique matching method declaration: " + method.FullName);
                }
                else if (declaredContext?.ComputedTypeSpecifics == InteropTypeContext.TypeSpecifics.NonBlittableStruct &&
                    originalDefinition?.IsConstructor == true && signature.ParameterTypes.Count == 1 &&
                    signature.ParameterTypes[0] is CorLibTypeSignature { ElementType: ElementType.I })
                {
                    return FailInstruction("native-pointer-constructor-reserved",
                        "A source value constructor cannot use the runtime's IntPtr wrapping constructor.");
                }
                else if (declaredContext?.OriginalType != null && originalDefinition?.IsConstructor == true &&
                    !originalDefinition.DeclaringType.IsValueType)
                {
                    var constructor = declaredContext.TryGetRestoredReferenceConstructor(originalDefinition);
                    if (constructor == null || constructor.DeclaringType != resolvedDeclarer ||
                        constructor.Name != methodName ||
                        !SignatureComparer.Default.Equals(constructor.Signature, signature))
                        return FailInstruction("native-constructor-unavailable",
                            "The native wrapper has no proven constructor delegation: " + method.FullName);
                    // The adapter reaches an allocating wrapper, not an in-place native initializer.
                    if (code.Code != CilCode.Newobj)
                        return FailInstruction("native-constructor-use-unsupported",
                            "A restored reference constructor requires newobj: " + method.FullName);
                }
                else if (declaredContext?.OriginalType != null && originalDefinition?.IsConstructor == true &&
                    !resolvedDeclarer.Methods.Any(candidate => candidate.Name == methodName &&
                        SignatureComparer.Default.Equals(candidate.Signature, signature)))
                {
                    // Only explicitly generated constructors establish native instance storage.
                    return FailInstruction("native-constructor-unavailable",
                        "The native wrapper has no matching constructor: " + method.FullName);
                }
            }
            var reference = new MemberReference(declarer.ToTypeDefOrRef(), methodName, signature);
            MethodDefinition managedTarget = declaredContext?.TryGetRestoredManagedMethod(originalDefinition);
            if (managedTarget == null && declaredContext?.TryGetRestoredBody(originalDefinition) is { } restoredBody)
            {
                if (restoredBody.DeclaringType != resolvedDeclarer || restoredBody.Name != methodName ||
                    !SignatureComparer.Default.Equals(restoredBody.Signature, signature))
                    return FailInstruction("restored-method-signature-mismatch",
                        "The restored body does not match its rewritten call declaration: " + method.FullName);
                // A native declaring type can also own restored CLR methods. Their bodies
                // enforce native identity at actual native operations, not at this CLR call.
                managedTarget = restoredBody;
            }
            if (declaredContext != null)
            {
                if (declaredContext.OriginalType == null && managedTarget == null)
                {
                    managedTarget = resolvedDeclarer.Methods.FirstOrDefault(candidate =>
                        candidate.Name == methodName && SignatureComparer.Default.Equals(candidate.Signature, signature));
                    if (managedTarget == null)
                        return FailInstruction("managed-helper-method-unavailable", "The restored helper has no matching method declaration: " + method.FullName);
                }
                if (managedTarget == null && declaredContext.OriginalType != null)
                {
                    if (!hasMappedNativeTarget && originalDefinition?.IsAbstract == true &&
                        originalDefinition.DeclaringType.IsInterface)
                        return FailInstruction("native-interface-method-unavailable",
                            "The native interface has no mapped or restored method declaration: " + method.FullName);
                    if (!NativeMethodStorageCompatible(code, method)) return null;
                }
            }
            IMethodDescriptor rewrittenMethod = reference;
            if (method is MethodSpecification genericMethod)
            {
                if (genericMethod.Signature == null)
                    return FailInstruction("missing-method-generic-signature", "The generic method has no instantiation signature.");
                var arguments = new TypeSignature[genericMethod.Signature.TypeArguments.Count];
                if (arguments.Length != signature.GenericParameterCount)
                    return FailInstruction("method-generic-arity-mismatch", "The native/restored method has a different generic arity.");
                for (var index = 0; index < arguments.Length; index++)
                {
                    if (managedTarget == null && declaredContext?.OriginalType != null)
                    {
                        var reason = InteropRestorationPlan.For(context).GetNativeIdentityRejection(genericMethod.Signature.TypeArguments[index]);
                        if (reason != null) return FailInstruction("native-generic-argument-unavailable", reason);
                    }
                    arguments[index] = resolver.Resolve(genericMethod.Signature.TypeArguments[index]);
                    if (arguments[index] == null)
                        return FailInstruction("unresolved-method-generic-argument", "Could not resolve generic argument " +
                            index + " type: " + FormatDiagnostic(genericMethod.Signature.TypeArguments[index]));
                }
                rewrittenMethod = reference.MakeGenericInstanceMethod(arguments);
            }
            NativeFacadeCallConversions conversions = null;
            if (mappedWithoutSource && !PlanMappedNativeCall(code, method, rewrittenMethod, out conversions)) return null;
            if (managedTarget != null && !ValidateManagedMethodUse(managedTarget, rewrittenMethod)) return null;
            var first = managedTarget == null ? EmitNativeGenericIdentityChecks(method, declaredContext) : null;
            var convertedArguments = EmitFacadeCallArguments(conversions);
            first ??= convertedArguments;
            var call = body.Add(code, imports.Module.DefaultImporter.ImportMethod(rewrittenMethod));
            EmitFacadeCallResult(conversions);
            return first ?? call;
        }

        private CilInstruction TranslateType(CilOpCode code, ITypeDefOrRef type)
        {
            if (code.Code is CilCode.Box or CilCode.Unbox or CilCode.Unbox_Any or CilCode.Castclass or CilCode.Isinst &&
                TryGetStackValue(currentInstruction, 0, out var collectionValue) &&
                InteropManagedCollectionLowering.IsStorage(context, collectionValue.Type))
                return FailInstruction("managed-collection-native-conversion",
                    "A CLR collection reference cannot use native boxing, unboxing or object/interface conversions.");
            var rewrittenType = resolver.Resolve(type.ToTypeSignature());
            if (rewrittenType == null)
                return FailInstruction("unresolved-instruction-type", "Could not resolve the instruction type operand.");
            if (code.Code == CilCode.Sizeof)
                return TranslateSizeOf(type.ToTypeSignature(), rewrittenType);
            if (InteropManagedDictionaryLowering.TryGetStorage(context, type.ToTypeSignature(), out _, out _, out var dictionaryType) &&
                dictionaryType.IsValueType && code.Code is not (CilCode.Initobj or CilCode.Ldobj or CilCode.Stobj or CilCode.Cpobj))
                return FailInstruction("managed-dictionary-type-operation",
                    "Dictionary pairs and enumerators require CLR value storage, not native identity or private-layout operations.");
            if (InteropManagedCollectionLowering.TryGetStorage(context, type.ToTypeSignature(), out _, out var collectionType) &&
                collectionType.IsValueType && code.Code is not (CilCode.Initobj or CilCode.Ldobj or CilCode.Stobj or CilCode.Cpobj))
                return FailInstruction("managed-collection-type-operation",
                    "A collection enumerator requires CLR value storage, not native identity or private-layout operations.");
            if (InteropArraySegmentLowering.TryGetStorage(context, type.ToTypeSignature(), out _, out _) &&
                code.Code is not (CilCode.Initobj or CilCode.Ldobj or CilCode.Stobj or CilCode.Cpobj))
                return FailInstruction("managed-array-segment-type-operation",
                    "An array segment supports CLR value storage, not native identity, boxing or private-layout operations.");
            if (InteropSpanLowering.TryGetSpan(context, type.ToTypeSignature(), out _, out _) &&
                code.Code is not (CilCode.Initobj or CilCode.Ldobj or CilCode.Stobj or CilCode.Cpobj))
                return FailInstruction("managed-span-type-operation",
                    "A span supports CLR value storage, not native identity, boxing or private-layout operations.");
            if (TryTranslateNativeTypeOperation(code, type.ToTypeSignature(), rewrittenType, out var nativeTypeOperation))
                return nativeTypeOperation;

            if (code.Code is CilCode.Newarr or CilCode.Box or CilCode.Unbox or CilCode.Unbox_Any
                or CilCode.Castclass or CilCode.Isinst)
            {
                var identityFailure = InteropRestorationPlan.For(context).GetNativeIdentityRejection(type.ToTypeSignature());
                if (identityFailure != null)
                    return FailInstruction("native-type-identity-unavailable", identityFailure);
            }

            if ((code.Code == CilCode.Castclass && !rewrittenType.IsValueType) ||
                (code.Code == CilCode.Unbox_Any && rewrittenType is GenericParameterSignature))
                return body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(
                    imports.Il2CppObjectBase_Cast.Value.MakeGenericInstanceMethod(rewrittenType)));
            if (code.Code == CilCode.Isinst && !rewrittenType.IsValueType)
                return body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(
                    imports.Il2CppObjectBase_TryCast.Value.MakeGenericInstanceMethod(rewrittenType)));

            switch (code.Code)
            {
                case CilCode.Unbox:
                    return FailInstruction("unsupported-native-box-address",
                        "An interior native boxed-value address cannot escape as a CLR managed reference.");
                case CilCode.Initobj when type.ToTypeSignature() is GenericParameterSignature ||
                    type.ToTypeSignature().IsValueType && !rewrittenType.IsValueType:
                    if (!TryGetStackValue(currentInstruction, 0, out var initialized) ||
                        initialized.Type is not ByReferenceTypeSignature address ||
                        !SignatureComparer.Default.Equals(address.BaseType, type.ToTypeSignature()))
                        return FailInstruction("unsupported-native-generic-default-address",
                            "A generic default requires an exact managed location; native aliases use the reference lowering.");
                    return body.Add(CilOpCodes.Call,
                        ((IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(typeof(InteropGenericValueSupport)
                            .GetMethod(nameof(InteropGenericValueSupport.InitializeDefault))))
                        .MakeGenericInstanceMethod(rewrittenType));
                case CilCode.Newarr:
                {
                    if (rewrittenType is GenericParameterSignature)
                    {
                        var allocation = body.Add(CilOpCodes.Conv_I8);
                        body.Add(CilOpCodes.Call,
                            ((IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(typeof(InteropGenericValueSupport)
                                .GetMethod(nameof(InteropGenericValueSupport.AllocateArray))))
                            .MakeGenericInstanceMethod(rewrittenType));
                        return allocation;
                    }
                    var constructor = rewrittenType.IsValueType
                        ? imports.Il2CppStructArrayctor_size.Get(rewrittenType)
                        : rewrittenType.FullName == "System.String"
                            ? imports.Il2CppStringArrayctor_size.Value
                            : imports.Il2CppRefrenceArrayctor_size.Get(rewrittenType);
                    var first = body.Add(CilOpCodes.Conv_I8);
                    body.Add(CilOpCodes.Newobj, imports.Module.DefaultImporter.ImportMethod(constructor));
                    return first;
                }
                case CilCode.Ldelema:
                    return FailInstruction("unsupported-array-element-address", "Native array element addresses are not supported.");
                case CilCode.Ldelem:
                    return TranslateArrayAccess(false, rewrittenType);
                case CilCode.Stelem:
                    return TranslateArrayAccess(true, rewrittenType);
                default:
                    return body.Add(code, rewrittenType.ToTypeDefOrRef());
            }
        }

        private CilInstruction TranslateToken(object operand)
        {
            if (operand is not ITypeDefOrRef type)
                return FailInstruction(operand is IMethodDescriptor ? "unsupported-method-token" :
                    operand is IFieldDescriptor ? "unsupported-field-token" : "unsupported-token",
                    "Only type tokens are supported.");
            var rewrittenType = resolver.Resolve(type.ToTypeSignature());
            if (rewrittenType == null)
                return FailInstruction("unresolved-token-type", "Could not resolve the type token.");
            var identityFailure = InteropRestorationPlan.For(context).GetNativeIdentityRejection(type.ToTypeSignature());
            if (identityFailure != null)
                return FailInstruction("native-type-identity-unavailable", identityFailure);
            return body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(
                imports.Il2CppSystemRuntimeTypeHandleGetRuntimeTypeHandle.Value.MakeGenericInstanceMethod(rewrittenType)));
        }
    }
}
