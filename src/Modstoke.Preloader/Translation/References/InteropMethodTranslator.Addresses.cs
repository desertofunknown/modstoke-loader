// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Collections;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private sealed class AddressOperation
        {
            public TypeSignature SourceElement;
            public TypeSignature Element;
            public TypeSignature OwnerType;
            public TypeSignature StaticOwnerType;
            public TypeSignature IndexType;
            public IMethodDescriptor Getter;
            public IMethodDescriptor Setter;
            public string NativeFieldPointerName;
            public int RectangularRank;
            public bool IsNestedField;
            public int ScopedParameterIndex = -1;
            public bool ScopedAggregateRoot;
            public bool ScopedAggregateField;
            public AddressOperation ReturnedOwnerField;
            public string ReturnedMethodPointerName;
            public CilInstruction Consumer;
            public bool DereferenceOwner;
            public bool ReadOnly;
            public CilLocalVariable Owner;
            public CilLocalVariable Index;
            public bool IsArray => IndexType != null;
            public bool IsScopedParameter => ScopedParameterIndex >= 0;
            public bool UsesNativeValueAccess => RectangularRank != 0 || IsNestedField || IsScopedParameter;
            public bool IsWrite => Consumer.OpCode.Code is CilCode.Initobj or CilCode.Stobj;
        }

        private readonly Dictionary<CilInstruction, AddressOperation> addressOperations =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<CilInstruction, AddressOperation> addressConsumers =
            new(ReferenceEqualityComparer.Instance);

        private bool TryTranslateAddressOperation(CilInstruction instruction, out CilInstruction copy)
        {
            if (TryTranslateScopedReference(instruction, out copy))
                return true;
            if (addressOperations.TryGetValue(instruction, out var producer))
            {
                copy = EmitAddressOwner(producer);
                return true;
            }
            if (addressConsumers.TryGetValue(instruction, out var consumer))
            {
                copy = EmitAddressConsumer(consumer);
                return true;
            }
            copy = null;
            return false;
        }

        private CilInstruction EmitAddressOwner(AddressOperation operation)
        {
            if (operation.IsArray)
            {
                operation.Owner = AddArrayLocal(operation.OwnerType);
                operation.Index = AddArrayLocal(operation.IndexType);
                var first = body.Add(CilOpCodes.Stloc, operation.Index);
                body.Add(CilOpCodes.Stloc, operation.Owner);
                // Bounds/null failures belong to ldelema, before the value producer of a store.
                if (IsNativeReferenceArrayAddress(operation))
                {
                    body.Add(CilOpCodes.Ldloc, operation.Owner);
                    body.Add(CilOpCodes.Ldloc, operation.Index);
                    body.Add(CilOpCodes.Conv_I8);
                    body.Add(CilOpCodes.Ldc_I4, operation.ReadOnly ? 1 : 0);
                    var validate = (IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(
                        typeof(InteropNativeReferenceArray).GetMethod(nameof(InteropNativeReferenceArray.Validate)));
                    body.Add(CilOpCodes.Call, validate.MakeGenericInstanceMethod(operation.Element));
                }
                else EmitArrayBoundsCheck(operation.Owner, operation.Index);
                return first;
            }
            if (operation.OwnerType == null)
            {
                // Initialize both wrapper metadata and native statics at address acquisition,
                // even if the reference is only stored or discarded before any getter runs.
                var first = body.Add(CilOpCodes.Ldtoken, operation.StaticOwnerType.ToTypeDefOrRef());
                body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(
                    typeof(RuntimeHelpers).GetMethod(nameof(RuntimeHelpers.RunClassConstructor))));
                var classStore = imports.Il2CppClassPointerStore.MakeGenericInstanceType(operation.StaticOwnerType);
                body.Add(CilOpCodes.Ldsfld, imports.Module.DefaultImporter.ImportField(new MemberReference(
                    classStore.ToTypeDefOrRef(), "NativeClassPtr", new FieldSignature(imports.Module.CorLibTypeFactory.IntPtr))));
                body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(imports.IL2CPP_il2cpp_runtime_class_init.Value));
                return first;
            }

            operation.Owner = AddArrayLocal(operation.OwnerType);
            var start = body.Add(CilOpCodes.Nop);
            if (operation.DereferenceOwner)
            {
                if (context.GetContextForNewType(operation.OwnerType.Resolve()).ComputedTypeSpecifics ==
                    InteropTypeContext.TypeSpecifics.NonBlittableStruct)
                    EmitValueStorageCall(nameof(InteropNativeValueStorage.Receiver), operation.OwnerType);
                else body.Add(CilOpCodes.Ldind_Ref);
            }
            body.Add(CilOpCodes.Stloc, operation.Owner);
            body.Add(CilOpCodes.Ldloc, operation.Owner);
            body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(
                imports.IL2CPP_Il2CppObjectBaseToPtrNotNull.Value));
            body.Add(CilOpCodes.Pop);
            return start;
        }

        private CilInstruction EmitAddressConsumer(AddressOperation operation)
        {
            var first = body.Add(CilOpCodes.Nop);
            CilLocalVariable value = null;
            if (operation.IsWrite)
            {
                value = AddArrayLocal(operation.Element);
                if (operation.Consumer.OpCode.Code == CilCode.Initobj)
                {
                    // Explicit initialization is required even in a source body without initlocals.
                    // Boxed native value types never enter this path: null is not their default value.
                    body.Add(CilOpCodes.Ldloca, value);
                    body.Add(CilOpCodes.Initobj, operation.Element.ToTypeDefOrRef());
                }
                else
                    body.Add(CilOpCodes.Stloc, value);
            }

            if (operation.Owner != null)
                body.Add(CilOpCodes.Ldloc, operation.Owner);
            if (operation.IsArray)
            {
                body.Add(CilOpCodes.Ldloc, operation.Index);
                body.Add(CilOpCodes.Conv_I4);
            }
            if (value != null)
                body.Add(CilOpCodes.Ldloc, value);

            if (IsNativeReferenceArrayAddress(operation))
            {
                if (!operation.IsWrite) body.Add(CilOpCodes.Ldc_I4, operation.ReadOnly ? 1 : 0);
                EmitReferenceArrayAccessor(operation.IsWrite, operation.Element);
            }
            else if (operation.IsArray)
                EmitArrayAccessor(operation.IsWrite, operation.Element);
            else
                body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(
                    operation.IsWrite ? operation.Setter : operation.Getter));

            if (operation.Consumer.OpCode.Code == CilCode.Ldfld)
            {
                var snapshot = AddArrayLocal(operation.Element);
                body.Add(CilOpCodes.Stloc, snapshot);
                body.Add(operation.Element.IsValueType ? CilOpCodes.Ldloca : CilOpCodes.Ldloc, snapshot);
                if (TranslateField(CilOpCodes.Ldfld, (IFieldDescriptor)operation.Consumer.Operand) == null)
                    return null;
                if (!operation.Element.IsValueType)
                    EmitAddressKeepAlive(snapshot);
            }

            // Keep the actual owning wrapper alive through native access, not only until its
            // pointer was extracted. No interior pointer or copied mutable lvalue escapes.
            if (operation.Owner != null)
                EmitAddressKeepAlive(operation.Owner);
            if (value != null && !operation.Element.IsValueType)
                EmitAddressKeepAlive(value);
            return first;
        }

        private void EmitAddressKeepAlive(CilLocalVariable owner)
        {
            body.Add(CilOpCodes.Ldloc, owner);
            body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(
                typeof(GC).GetMethod(nameof(GC.KeepAlive))));
        }

        private void EmitReferenceArrayAccessor(bool setter, TypeSignature element)
        {
            var accessor = (IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(
                typeof(InteropNativeReferenceArray).GetMethod(setter
                    ? nameof(InteropNativeReferenceArray.Write) : nameof(InteropNativeReferenceArray.Read)));
            body.Add(CilOpCodes.Call, accessor.MakeGenericInstanceMethod(element));
        }

        private bool IsNativeReferenceArrayAddress(AddressOperation operation) => operation.IsArray &&
            !InteropTypeReferences.IsValueType(operation.SourceElement) && IsNativeArrayReference(operation.Element);

        private bool TryGetFieldAddress(CilInstruction instruction, CilInstruction receiverProducer,
            out AddressOperation operation, bool allowGenericElement = false)
        {
            operation = null;
            if (instruction.Operand is not IFieldDescriptor field || field.Signature == null)
                return false;
            if (InteropManagedCollectionLowering.IsStorage(context, field.DeclaringType?.ToTypeSignature())) return false;
            if (InteropArraySegmentLowering.TryGetStorage(context, field.DeclaringType?.ToTypeSignature(), out _, out _)) return false;
            if (InteropSpanLowering.TryGetSpan(context, field.DeclaringType?.ToTypeSignature(), out _, out _)) return false;
            if (InteropNativeArrayLowering.TryGetStorage(context, field.DeclaringType?.ToTypeSignature(), out _, out _))
                return false;
            var isStatic = instruction.OpCode.Code == CilCode.Ldsflda;
            var declaringType = resolver.ResolveRaw(field.DeclaringType.ToTypeSignature(),
                field.Signature.HasThis);
            var definition = declaringType?.Resolve();
            if (definition == null)
                return false;
            var mappedType = context.GetContextForNewType(definition);
            var mappedField = mappedType.Fields.SingleOrDefault(candidate =>
                candidate.OriginalField.Name == field.Name &&
                candidate.OriginalField.Signature.FieldType.FullName == field.Signature.FieldType.FullName);
            if (mappedField == null || mappedField.PointerField == null ||
                mappedField.OriginalField.IsStatic != isStatic ||
                definition.Fields.Any(candidate => candidate.Name == field.Name ||
                    candidate.Name == mappedField.UnmangledName))
                return false;

            // A genuine managed field (including one restored by the parent) keeps its real address.
            var sourceElement = field.Signature.FieldType.InstantiateGenericTypes(GenericContext.FromField(field));
            TypeSignature element;
            if (allowGenericElement && sourceElement is GenericParameterSignature)
                element = resolver.Resolve(sourceElement);
            else if (!TryGetAddressElement(sourceElement, out element))
                return false;
            if (element == null) return false;
            var property = definition.Properties.SingleOrDefault(candidate => candidate.Name == mappedField.UnmangledName);
            var getter = AddressAccessor(declaringType, property?.GetMethod);
            var setter = AddressAccessor(declaringType, property?.SetMethod);
            if (getter != null && (getter.Signature.HasThis == isStatic ||
                getter.Signature.ParameterTypes.Count != 0 || !SignatureComparer.Default.Equals(element,
                    getter.Signature.ReturnType.InstantiateGenericTypes(GenericContext.FromMethod(getter)))))
                return false;
            if (setter != null && (setter.Signature.HasThis == isStatic ||
                setter.Signature.ParameterTypes.Count != 1 ||
                setter.Signature.ReturnType.ElementType != ElementType.Void ||
                !SignatureComparer.Default.Equals(element, setter.Signature.ParameterTypes[0]
                    .InstantiateGenericTypes(GenericContext.FromMethod(setter)))))
                return false;

            var dereference = false;
            if (!isStatic)
            {
                if (declaringType.IsValueType || !IsNativeArrayReference(declaringType) ||
                    !TryGetStackValue(instruction, 0, out var receiver) || receiver.IsNull)
                    return false;
                var receiverType = resolver.Resolve(receiver.Type);
                if (receiver.Type is ByReferenceTypeSignature)
                {
                    // A value-type source's this is a byref, but a native boxed wrapper's this
                    // is an object. Accept only direct producers whose emitted type is known.
                    receiverType = GetEmittedAddressReceiverType(receiverProducer);
                    if (receiverType is ByReferenceTypeSignature reference)
                    {
                        dereference = true;
                        receiverType = reference.BaseType;
                    }
                }
                if (!SignatureComparer.Default.Equals(receiverType, declaringType))
                    return false;
            }
            operation = new AddressOperation
            {
                SourceElement = sourceElement,
                Element = element,
                OwnerType = isStatic ? null : declaringType,
                StaticOwnerType = isStatic ? declaringType : null,
                DereferenceOwner = dereference,
                ReadOnly = mappedField.OriginalField.IsInitOnly,
                Getter = getter,
                Setter = setter,
                NativeFieldPointerName = mappedField.PointerField.Name
            };
            return true;
        }

        private TypeSignature GetEmittedAddressReceiverType(CilInstruction producer)
        {
            if (producer?.Operand is Parameter argument)
            {
                var type = TargetParameter(argument).ParameterType;
                return producer.OpCode.Code switch
                {
                    CilCode.Ldarg => IsNativeValueThis(argument) ? type.MakeByReferenceType() : type,
                    CilCode.Ldarga => type.MakeByReferenceType(),
                    _ => null
                };
            }
            if (producer?.Operand is CilLocalVariable local && locals.TryGetValue(local, out var rewritten))
                return producer.OpCode.Code switch
                {
                    CilCode.Ldloc => rewritten.VariableType,
                    CilCode.Ldloca => rewritten.VariableType.MakeByReferenceType(),
                    _ => null
                };
            return null;
        }

        private static IMethodDescriptor AddressAccessor(TypeSignature declaringType, MethodDefinition accessor) =>
            accessor == null ? null : new MemberReference(declaringType.ToTypeDefOrRef(), accessor.Name, accessor.Signature);

        private bool TryGetArrayAddress(CilInstruction instruction, out AddressOperation operation)
        {
            operation = null;
            if (instruction.Operand is not ITypeDefOrRef addressed ||
                !TryGetArrayOperand(instruction, 1, out var sourceArray, out var rewrittenArray, out var element) ||
                !SignatureComparer.Default.Equals(addressed.ToTypeSignature(), sourceArray.BaseType) ||
                !TryGetAddressElement(sourceArray.BaseType, out var resolvedElement) ||
                !SignatureComparer.Default.Equals(resolvedElement, element) ||
                !InteropTypeReferences.IsValueType(sourceArray.BaseType) && !IsNativeArrayReference(element) ||
                !TryGetStackValue(instruction, 0, out var index))
                return false;
            var indexType = NumericStackValue(index.Type).Type;
            if (indexType?.ElementType is not (ElementType.I4 or ElementType.I))
                return false;
            // Reference vectors validate their native element class at acquisition, including
            // discarded addresses and null stores. Readonly references retain read covariance.
            operation = new AddressOperation
            {
                SourceElement = sourceArray.BaseType,
                Element = element,
                OwnerType = rewrittenArray,
                IndexType = indexType
            };
            return true;
        }

        private bool TryGetAddressElement(TypeSignature source, out TypeSignature element)
        {
            element = null;
            if (source is GenericParameterSignature or PointerTypeSignature or ByReferenceTypeSignature or
                CustomModifierTypeSignature or FunctionPointerTypeSignature ||
                InteropRestorationPlan.For(context).ContainsManagedOnlyStorage(source) ||
                InteropRestorationPlan.For(context).GetNativeIdentityRejection(source) != null)
                return false;
            element = resolver.Resolve(source);
            if (element == null || element is GenericParameterSignature or PointerTypeSignature or ByReferenceTypeSignature)
                return false;
            return element.IsValueType || element.FullName == "System.String" || IsNativeArrayReference(element);
        }
    }
}
