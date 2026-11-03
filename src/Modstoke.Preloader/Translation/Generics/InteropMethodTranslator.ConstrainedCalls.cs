// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private readonly Dictionary<CilInstruction, TypeSignature> convertedConstrainedReceivers =
            new(ReferenceEqualityComparer.Instance);

        private CilInstruction TranslateConstrainedReference(CilInstruction prefix)
        {
            var source = sourceMethod.CilMethodBody;
            var position = source.Instructions.IndexOf(prefix);
            if (prefix.Operand is not ITypeDefOrRef constraint || position + 1 >= source.Instructions.Count ||
                source.Instructions[position + 1] is not { OpCode.Code: CilCode.Callvirt, Operand: IMethodDescriptor method } call ||
                method.Signature?.HasThis != true || position > 0 && IsAddressPrefix(source.Instructions[position - 1].OpCode.Code))
                return FailInstruction("unsupported-constrained-contract", "The constrained call has an unsupported prefix layout.");

            // Conversion replaces the prefix and must run for every entry to its call.
            foreach (var instruction in source.Instructions)
                if (instruction.Operand is ICilLabel label && PointsToCall(label) ||
                    instruction.Operand is IReadOnlyList<ICilLabel> labels && System.Linq.Enumerable.Any(labels, PointsToCall))
                    return FailInstruction("constrained-interior-entry", "A branch enters the call after its receiver conversion.");
            foreach (var handler in source.ExceptionHandlers)
                if (PointsToCall(handler.TryStart) || PointsToCall(handler.TryEnd) || PointsToCall(handler.HandlerStart) ||
                    PointsToCall(handler.HandlerEnd) || PointsToCall(handler.FilterStart))
                    return FailInstruction("constrained-interior-entry", "An exception boundary splits the receiver conversion and call.");

            var receiver = constraint.ToTypeSignature();
            var plan = InteropRestorationPlan.For(context);
            if (TryTranslateManagedCollectionConstrainedCall(prefix, receiver, method, call, out var collectionCall))
                return collectionCall;
            if (TryTranslateArraySegmentConstrainedCall(prefix, receiver, method, call, out var segmentCall))
                return segmentCall;
            if (TryTranslateNativeArrayConstrainedCall(prefix, receiver, method, call, out var storageCall))
                return storageCall;
            if (TryTranslateManagedInterface(prefix, receiver, method, out var managedInterface))
                return managedInterface;
            if (TryPlanRestoredValueCall(prefix, receiver, method, out var restoredImplementation))
            {
                constrainedValueCalls.Add(call, restoredImplementation);
                return body.Add(CilOpCodes.Nop);
            }
            if (TryTranslateConstrainedGetType(prefix, receiver, method, out var typeConversion))
            {
                convertedConstrainedReceivers.Add(call, context.CorLib.GetTypeByName("System.Object").NewType.ToTypeSignature());
                return typeConversion;
            }
            if (TryTranslateConstrainedObjectToString(prefix, receiver, method, out var formatting))
            {
                constrainedGenericCalls.Add(call);
                return formatting;
            }
            if (TryTranslateConstrainedEnumObjectCall(prefix, receiver, method, out var enumConversion))
            {
                convertedConstrainedReceivers.Add(call, context.CorLib.GetTypeByName("System.Object").NewType.ToTypeSignature());
                return enumConversion;
            }
            if (TryTranslateConstrainedGenericValue(prefix, receiver, method, out var genericConversion))
            {
                constrainedGenericCalls.Add(call);
                return genericConversion;
            }
            if (TryTranslateConstrainedInterface(prefix, receiver, method, out var interfaceConversion))
            {
                constrainedGenericCalls.Add(call);
                return interfaceConversion;
            }
            if (TryPlanConstrainedValueCall(prefix, receiver, method, out var implementation))
            {
                constrainedValueCalls.Add(call, implementation);
                return body.Add(CilOpCodes.Nop);
            }
            if (receiver.IsValueType || receiver is GenericParameterSignature generic && !IsReferenceGeneric(generic) ||
                plan.GetNativeIdentityRejection(receiver) != null ||
                !TryGetStackValue(prefix, method.Signature.ParameterTypes.Count, out var stack) ||
                stack.Type is not ByReferenceTypeSignature address || !SignatureComparer.Default.Equals(address.BaseType, receiver))
                return FailInstruction("unsupported-constrained-receiver",
                    "No identity-preserving constrained dispatch is available for this receiver and method.");
            var rewrittenReceiver = resolver.Resolve(receiver);
            var sourceDeclarer = method.DeclaringType.ToTypeSignature();
            var rewrittenDeclarer = resolver.Resolve(sourceDeclarer);
            var definition = rewrittenDeclarer?.Resolve();
            if (rewrittenReceiver == null || definition == null || rewrittenDeclarer.IsValueType ||
                rewrittenDeclarer.FullName == "System.String" || plan.GetNativeIdentityRejection(sourceDeclarer) != null)
                return FailInstruction("unsupported-constrained-declarer", "The target requires a native reference wrapper.");

            var arguments = new CilLocalVariable[method.Signature.ParameterTypes.Count];
            for (var index = 0; index < arguments.Length; index++)
            {
                var type = resolver.Resolve(method.Signature.ParameterTypes[index].InstantiateGenericTypes(GenericContext.FromMethod(method)));
                if (type == null || type is ByReferenceTypeSignature or PointerTypeSignature)
                    return FailInstruction("unsupported-constrained-argument", "This constrained call cannot spill a native alias or unresolved argument.");
                if (!TryGetStackValue(prefix, arguments.Length - index - 1, out var value) ||
                    !value.IsNull && !ProvesManagedBase(resolver.Resolve(value.Type), type, new HashSet<string>(), 0) &&
                    !(NumericStackValue(value.Type).Type != null &&
                        SameStackValue(NumericStackValue(value.Type), NumericStackValue(
                            method.Signature.ParameterTypes[index].InstantiateGenericTypes(GenericContext.FromMethod(method))))))
                    return FailInstruction("unsupported-constrained-argument", "The receiver conversion requires directly assignable argument representations.");
                arguments[index] = AddArrayLocal(type);
            }
            CilInstruction first = null;
            for (var index = arguments.Length - 1; index >= 0; index--)
            {
                var store = body.Add(CilOpCodes.Stloc, arguments[index]);
                first ??= store;
            }
            var helper = (IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(
                typeof(InteropGenericValueSupport).GetMethod(nameof(InteropGenericValueSupport.ReferenceReceiver)));
            var conversion = body.Add(CilOpCodes.Call, helper.MakeGenericInstanceMethod(rewrittenReceiver, rewrittenDeclarer));
            foreach (var argument in arguments) body.Add(CilOpCodes.Ldloc, argument);
            convertedConstrainedReceivers.Add(call, rewrittenDeclarer);
            return first ?? conversion;

            bool PointsToCall(ICilLabel label) => label is CilInstructionLabel targetLabel && ReferenceEquals(targetLabel.Instruction, call);
        }

        private bool TryTranslateConstrainedGetType(CilInstruction prefix, TypeSignature receiver,
            IMethodDescriptor method, out CilInstruction conversion)
        {
            conversion = null;
            var signature = method.Signature;
            if (receiver is not GenericParameterSignature || method is MethodSpecification || method.Name != "GetType" ||
                !SignatureComparer.Default.Equals(method.DeclaringType, sourceMethod.DeclaringModule.CorLibTypeFactory.Object.Type) ||
                !InteropDelegateContracts.HasStandardConvention(signature) || !signature.HasThis ||
                signature.GenericParameterCount != 0 || signature.ParameterTypes.Count != 0 ||
                !TryGetStackValue(prefix, 0, out var stack) || stack.Type is not ByReferenceTypeSignature address ||
                !SignatureComparer.Default.Equals(address.BaseType, receiver)) return false;

            var nativeObject = context.CorLib.GetTypeByName("System.Object");
            var nativeType = context.CorLib.GetTypeByName("System.Type");
            var result = resolver.Resolve(signature.ReturnType);
            var rewritten = resolver.Resolve(receiver);
            if (rewritten == null || nativeObject?.OriginalType == null || nativeType?.OriginalType == null ||
                !SignatureComparer.Default.Equals(result, nativeType.NewType.ToTypeSignature())) return false;
            var mapped = FindMappedNativeMethod(method, nativeObject);
            if (mapped?.NonGenericMethodInfoPointerField == null || mapped.OriginalMethod.IsStatic ||
                mapped.OriginalMethod.IsVirtual || mapped.OriginalMethod.IsAbstract) return false;

            // GetType cannot mutate the value receiver. Box using native semantics, then
            // retain the normal mapped call so null and runtime subtype behavior agree.
            var helper = (IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(
                typeof(InteropGenericValueSupport).GetMethod(nameof(InteropGenericValueSupport.TypeQueryReceiver)));
            conversion = body.Add(CilOpCodes.Call, helper.MakeGenericInstanceMethod(rewritten, nativeObject.NewType.ToTypeSignature()));
            return true;
        }

        private bool TryTranslateConstrainedObjectToString(CilInstruction prefix, TypeSignature receiver,
            IMethodDescriptor method, out CilInstruction conversion)
        {
            conversion = null;
            var signature = method.Signature;
            var corlib = sourceMethod.DeclaringModule.CorLibTypeFactory;
            if (receiver is not GenericParameterSignature || method is MethodSpecification || method.Name != "ToString" ||
                !SignatureComparer.Default.Equals(method.DeclaringType, corlib.Object.Type) ||
                !InteropDelegateContracts.HasStandardConvention(signature) || !signature.HasThis ||
                signature.GenericParameterCount != 0 || signature.ParameterTypes.Count != 0 ||
                !SignatureComparer.Default.Equals(signature.ReturnType, corlib.String) ||
                !TryGetStackValue(prefix, 0, out var stack) || stack.Type is not ByReferenceTypeSignature address ||
                !SignatureComparer.Default.Equals(address.BaseType, receiver)) return false;

            var nativeObject = context.CorLib.GetTypeByName("System.Object");
            var rewritten = resolver.Resolve(receiver);
            if (rewritten == null || nativeObject?.OriginalType == null) return false;
            var mapped = FindMappedNativeMethod(method, nativeObject);
            if (mapped?.NonGenericMethodInfoPointerField == null || mapped.OriginalMethod.IsStatic ||
                !mapped.OriginalMethod.IsVirtual || mapped.OriginalMethod.IsAbstract) return false;

            var helper = (IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(
                typeof(InteropGenericValueSupport).GetMethod(nameof(InteropGenericValueSupport.ConstrainedObjectToString)));
            conversion = body.Add(CilOpCodes.Ldstr, (string)mapped.NonGenericMethodInfoPointerField.Name);
            body.Add(CilOpCodes.Call, helper.MakeGenericInstanceMethod(rewritten, nativeObject.NewType.ToTypeSignature()));
            return true;
        }

        private bool TryTranslateConstrainedEnumObjectCall(CilInstruction prefix, TypeSignature receiver,
            IMethodDescriptor method, out CilInstruction conversion)
        {
            conversion = null;
            var signature = method.Signature;
            var corlib = sourceMethod.DeclaringModule.CorLibTypeFactory;
            TypeSignature result = (string)method.Name switch
            {
                "ToString" => corlib.String,
                "GetHashCode" => corlib.Int32,
                _ => null
            };
            if (receiver is not TypeDefOrRefSignature || receiver.Resolve() is not { IsEnum: true } ||
                method is MethodSpecification || result == null ||
                !SignatureComparer.Default.Equals(method.DeclaringType, corlib.Object.Type) ||
                !InteropDelegateContracts.HasStandardConvention(signature) || !signature.HasThis ||
                signature.GenericParameterCount != 0 || signature.ParameterTypes.Count != 0 ||
                !SignatureComparer.Default.Equals(signature.ReturnType, result) ||
                InteropRestorationPlan.For(context).GetNativeIdentityRejection(receiver) != null ||
                !TryGetStackValue(prefix, 0, out var stack) || stack.Type is not ByReferenceTypeSignature address ||
                !SignatureComparer.Default.Equals(address.BaseType, receiver))
                return false;

            var rewritten = resolver.Resolve(receiver);
            if (rewritten?.Resolve() is not { IsEnum: true }) return false;

            // Native dispatch retains the game's enum formatting and hash semantics.
            // Neither method mutates the original enum location.
            var nativeObject = context.CorLib.GetTypeByName("System.Object").NewType.ToTypeSignature();
            var helper = (IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(
                typeof(InteropGenericValueSupport).GetMethod(nameof(InteropGenericValueSupport.Box)));
            conversion = body.Add(CilOpCodes.Ldobj, rewritten.ToTypeDefOrRef());
            body.Add(CilOpCodes.Call, helper.MakeGenericInstanceMethod(rewritten, nativeObject));
            return true;
        }
    }
}
