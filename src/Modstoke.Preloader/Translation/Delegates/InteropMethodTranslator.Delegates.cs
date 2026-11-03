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
    internal sealed partial class InteropMethodTranslator
    {
        private sealed record DelegateFactory(TypeSignature NativeType, TypeSignature ManagedType,
            IMethodDescriptor Callback, IMethodDescriptor SourceCallback, InteropTypeContext CallbackOwner,
            bool ManagedCallback, TypeSignature BoundTargetBox);

        private readonly Dictionary<CilInstruction, DelegateFactory> delegateConstructors =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<CilInstruction, DelegateFactory> delegatePointers =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<CilInstruction, TypeSignature> delegateTargetBoxes =
            new(ReferenceEqualityComparer.Instance);

        private bool IsBridgedDelegateConstructor(CilInstruction instruction) => delegateConstructors.ContainsKey(instruction);

        private bool FindDelegateFactories(CilMethodBody original)
        {
            var boundaries = CollectDelegateBoundaries(original);
            var source = original.Instructions;
            for (var index = 0; index < source.Count; index++)
            {
                var constructor = source[index];
                if (constructor.OpCode.Code != CilCode.Newobj || constructor.Operand is not IMethodDescriptor method)
                    continue;
                var sourceDelegate = method.DeclaringType?.Resolve() ??
                    FindMappedSourceOwner(method.DeclaringType?.ToTypeSignature())?.OriginalType;
                if (sourceDelegate?.BaseType?.FullName != "System.MulticastDelegate")
                    continue;
                currentInstruction = constructor;
                if (method.Name != ".ctor" || !InteropDelegateContracts.HasStandardConvention(method.Signature) ||
                    !method.Signature.HasThis || method.Signature.GenericParameterCount != 0 ||
                    method.Signature.ReturnType.FullName != "System.Void" || method.Signature.ParameterTypes.Count != 2 ||
                    method.Signature.ParameterTypes[0].FullName != "System.Object" ||
                    method.Signature.ParameterTypes[1].FullName != "System.IntPtr")
                    return Fail("delegate-constructor-contract", "The native delegate constructor is not the object/IntPtr factory.", constructor);

                var pointerIndex = index - 1;
                while (pointerIndex >= 0 && source[pointerIndex].OpCode.Code == CilCode.Nop) pointerIndex--;
                if (pointerIndex < 0 || source[pointerIndex].OpCode.Code is not (CilCode.Ldftn or CilCode.Ldvirtftn) ||
                    source[pointerIndex].Operand is not IMethodDescriptor callback)
                    return Fail("delegate-function-pointer-unproven", "Native delegate construction requires an adjacent proven managed callback.", constructor);
                // No alternate entry or EH boundary may supply another pointer to this constructor.
                for (var position = pointerIndex + 1; position <= index; position++)
                    if (boundaries.Contains(source[position]))
                        return Fail("delegate-factory-control-flow", "A branch or exception boundary enters the middle of a delegate factory.", source[position]);

                var pointer = source[pointerIndex];
                if (pointer.OpCode.Code == CilCode.Ldvirtftn)
                {
                    var duplicateIndex = pointerIndex - 1;
                    while (duplicateIndex >= 0 && source[duplicateIndex].OpCode.Code == CilCode.Nop) duplicateIndex--;
                    if (duplicateIndex < 0 || source[duplicateIndex].OpCode.Code != CilCode.Dup)
                        return Fail("delegate-virtual-target-unproven", "Virtual factory targets must use the same duplicated receiver.", pointer);
                    for (var position = duplicateIndex + 1; position <= pointerIndex; position++)
                        if (boundaries.Contains(source[position]))
                            return Fail("delegate-factory-control-flow", "A branch or exception boundary separates the virtual receiver from its pointer.", source[position]);
                }

                var nativeType = resolver.Resolve(method.DeclaringType.ToTypeSignature());
                var nativeDefinition = nativeType?.Resolve();
                var nativeOwner = nativeDefinition == null ? null : context.GetContextForNewType(nativeDefinition);
                var restoredDelegate = InteropRestoredManagedDelegateSupport.IsRestored(nativeOwner) ||
                    InteropRestoredUnmanagedDelegateSupport.IsRestored(nativeOwner);
                if (!restoredDelegate && (nativeOwner?.OriginalType?.BaseType?.FullName != "System.MulticastDelegate" ||
                    InteropRestorationPlan.For(context).GetNativeIdentityRejection(method.DeclaringType.ToTypeSignature()) != null))
                    return Fail("delegate-native-identity-unavailable", "The delegate must have an existing native definition and native generic arguments.", constructor);
                var invoke = nativeDefinition.Methods.SingleOrDefault(candidate => candidate.Name == "Invoke");
                var invokeSignature = invoke?.Signature.InstantiateGenericTypes(GenericContext.FromType(nativeType));
                TypeSignature managedType = nativeType;
                if (invokeSignature == null || !restoredDelegate &&
                    !InteropDelegateContracts.TryGetManagedDelegate(imports.Module, invokeSignature, out managedType))
                    return Fail("delegate-invoke-signature-unsupported", "The Invoke signature cannot be represented by a supported CLR Action/Func.", constructor);
                var sourceInvokeSignature = sourceDelegate.Methods.SingleOrDefault(candidate => candidate.Name == "Invoke")
                    ?.Signature.InstantiateGenericTypes(GenericContext.FromType(method.DeclaringType.ToTypeSignature()));

                currentInstruction = pointer;
                var rewrittenCallback = ResolveDelegateCallback(callback, out var callbackOwner, out var callbackDefinition,
                    out var managedCallback);
                if (rewrittenCallback == null) return false;
                var callbackSignature = rewrittenCallback.Signature.InstantiateGenericTypes(GenericContext.FromMethod(rewrittenCallback));
                var sourceCallbackSignature = callback.Signature.InstantiateGenericTypes(GenericContext.FromMethod(callback));
                if (restoredDelegate && !managedCallback &&
                    (invokeSignature.ReturnType is ByReferenceTypeSignature ||
                     invokeSignature.ParameterTypes.Any(parameter => parameter is ByReferenceTypeSignature)))
                    return Fail("delegate-native-byref-contract",
                        "A CLR byref delegate requires a managed callback; native wrapper aliases need an explicit callback lifetime contract.", pointer);
                var boundParameters = !callbackSignature.HasThis &&
                    callbackSignature.ParameterTypes.Count == invokeSignature.ParameterTypes.Count + 1 ? 1 : 0;
                if (!DelegateSignaturesCompatible(sourceInvokeSignature, invokeSignature,
                        sourceCallbackSignature, callbackSignature, boundParameters))
                    return Fail("delegate-invoke-signature-mismatch", "The substituted callback requires exact types or proven reference parameter contravariance and return covariance.", pointer);

                if (!TryGetStackValue(pointer, 0, out var receiver))
                    return Fail("delegate-target-unproven", "The callback target's stack type is unknown.", pointer);
                TypeSignature boundTargetBox = null;
                if (!callbackSignature.HasThis)
                {
                    if (pointer.OpCode.Code != CilCode.Ldftn || boundParameters == 0 && !receiver.IsNull)
                        return Fail("delegate-static-target-contract", "An unbound static callback requires ldftn and a null target.", pointer);
                    if (boundParameters != 0)
                    {
                        var sourceFirst = sourceCallbackSignature.ParameterTypes[0];
                        var first = callbackSignature.ParameterTypes[0];
                        var receiverType = receiver.IsNull ? null : resolver.Resolve(receiver.Type);
                        if (!IsDelegateReferenceType(sourceFirst, false) || !IsDelegateReferenceType(first, true) ||
                            !receiver.IsNull && (!IsDelegateReferenceType(receiver.Type, false) ||
                                !IsDelegateReferenceType(receiverType, true) ||
                                !DelegateTypeCompatible(receiver.Type, sourceFirst, receiverType, first)))
                            return Fail("delegate-static-bound-target-contract", "A closed static callback requires a compatible reference first argument; value and by-reference binding are unsupported.", pointer);
                        if (receiverType is GenericParameterSignature) boundTargetBox = receiverType;
                    }
                }
                else
                {
                    var receiverType = receiver.IsNull ? null : resolver.Resolve(receiver.Type);
                    if (callback.DeclaringType.ToTypeSignature().IsValueType)
                    {
                        if (!managedCallback || !PlanDelegateTargetBox(original, pointerIndex, boundaries,
                                callback.DeclaringType.ToTypeSignature(), rewrittenCallback.DeclaringType.ToTypeSignature()))
                            return Fail("delegate-instance-target-contract",
                                "A value callback requires a proven CLR box of its exact managed declaring type.", pointer);
                    }
                    else if (receiverType == null || receiver.Type is ByReferenceTypeSignature || receiverType.IsValueType ||
                        !ProvesManagedBase(receiverType, rewrittenCallback.DeclaringType.ToTypeSignature(), new HashSet<string>(), 0))
                        return Fail("delegate-instance-target-contract", "The callback needs a compatible reference target; open receivers are not supported.", pointer);
                }
                if (pointer.OpCode.Code == CilCode.Ldvirtftn && !callbackDefinition.IsVirtual)
                    return Fail("delegate-virtual-contract", "The rewritten callback has no CLR virtual slot.", pointer);
                if (pointer.OpCode.Code == CilCode.Ldftn && !managedCallback &&
                    callbackDefinition.IsVirtual && !callbackDefinition.IsFinal && !callbackOwner.NewType.IsSealed)
                    return Fail("delegate-nonvirtual-native-contract", "A native virtual wrapper dispatches virtually and cannot preserve an explicit nonvirtual ldftn binding.", pointer);

                var factory = new DelegateFactory(restoredDelegate ? null : nativeType, managedType,
                    rewrittenCallback, callback, callbackOwner, managedCallback, boundTargetBox);
                delegatePointers.Add(pointer, factory);
                delegateConstructors.Add(constructor, factory);
            }
            return true;
        }

        private bool PlanDelegateTargetBox(CilMethodBody source, int pointerIndex,
            HashSet<CilInstruction> boundaries, TypeSignature sourceOwner, TypeSignature owner)
        {
            if (!owner.IsValueType || !InteropDelegateContracts.CanBeArgument(owner, false) ||
                sourceOwner is GenericInstanceTypeSignature nullable && nullable.GenericType.FullName == "System.Nullable`1")
                return false;

            var boxIndex = pointerIndex - 1;
            while (boxIndex >= 0 && source.Instructions[boxIndex].OpCode.Code == CilCode.Nop) boxIndex--;
            if (source.Instructions[pointerIndex].OpCode.Code == CilCode.Ldvirtftn)
            {
                if (boxIndex < 0 || source.Instructions[boxIndex].OpCode.Code != CilCode.Dup) return false;
                boxIndex--;
                while (boxIndex >= 0 && source.Instructions[boxIndex].OpCode.Code == CilCode.Nop) boxIndex--;
            }
            if (boxIndex < 0) return false;
            var box = source.Instructions[boxIndex];
            if (box.OpCode.Code != CilCode.Box || box.Operand is not ITypeDefOrRef boxed ||
                !SignatureComparer.Default.Equals(boxed.ToTypeSignature(), sourceOwner) ||
                !SignatureComparer.Default.Equals(resolver.Resolve(boxed.ToTypeSignature()), owner) ||
                !TryGetStackValue(box, 0, out var value) || value.IsNull ||
                !SignatureComparer.Default.Equals(value.Type, sourceOwner))
                return false;
            for (var index = boxIndex + 1; index <= pointerIndex; index++)
                if (boundaries.Contains(source.Instructions[index])) return false;

            // This box flows only into the CLR delegate factory. Native boxing would
            // change the target's identity and lose mutations made by the callback.
            delegateTargetBoxes.Add(box, owner);
            return true;
        }

        private static HashSet<CilInstruction> CollectDelegateBoundaries(CilMethodBody source)
        {
            var boundaries = new HashSet<CilInstruction>(ReferenceEqualityComparer.Instance);
            foreach (var instruction in source.Instructions)
            {
                if (instruction.Operand is ICilLabel label) AddBoundary(label);
                else if (instruction.Operand is IReadOnlyList<ICilLabel> labels)
                    foreach (var branch in labels) AddBoundary(branch);
            }
            foreach (var handler in source.ExceptionHandlers)
            {
                AddBoundary(handler.TryStart);
                AddBoundary(handler.TryEnd);
                AddBoundary(handler.HandlerStart);
                AddBoundary(handler.HandlerEnd);
                AddBoundary(handler.FilterStart);
            }
            return boundaries;

            void AddBoundary(ICilLabel label)
            {
                if (label is CilInstructionLabel { Instruction: not null } entry) boundaries.Add(entry.Instruction);
            }
        }

        private bool DelegateSignaturesCompatible(MethodSignature sourceInvoke, MethodSignature invoke,
            MethodSignature sourceCallback, MethodSignature callback, int boundParameters)
        {
            if (sourceInvoke == null || sourceInvoke.ParameterTypes.Count != invoke.ParameterTypes.Count ||
                sourceCallback.ParameterTypes.Count != callback.ParameterTypes.Count ||
                callback.ParameterTypes.Count != invoke.ParameterTypes.Count + boundParameters ||
                !DelegateTypeCompatible(sourceCallback.ReturnType, sourceInvoke.ReturnType, callback.ReturnType, invoke.ReturnType))
                return false;
            for (var index = 0; index < invoke.ParameterTypes.Count; index++)
                if (!DelegateTypeCompatible(sourceInvoke.ParameterTypes[index], sourceCallback.ParameterTypes[index + boundParameters],
                        invoke.ParameterTypes[index], callback.ParameterTypes[index + boundParameters]))
                    return false;
            return true;
        }

        private bool DelegateTypeCompatible(TypeSignature sourceFrom, TypeSignature sourceTo, TypeSignature from, TypeSignature to)
        {
            if (sourceFrom is ByReferenceTypeSignature || sourceTo is ByReferenceTypeSignature ||
                from is ByReferenceTypeSignature || to is ByReferenceTypeSignature)
                return SignatureComparer.Default.Equals(sourceFrom, sourceTo) && SignatureComparer.Default.Equals(from, to);
            if (SignatureComparer.Default.Equals(from, to)) return true;
            // Native interfaces and array covariance do not imply CLR wrapper assignability. Prove the
            // source and emitted contracts, and never turn source value-type boxing into delegate variance.
            return IsDelegateReferenceType(sourceFrom, false) && IsDelegateReferenceType(sourceTo, false) &&
                IsDelegateReferenceType(from, true) && IsDelegateReferenceType(to, true) &&
                from is not ArrayBaseTypeSignature && to is not ArrayBaseTypeSignature &&
                ProvesManagedBase(sourceFrom, sourceTo, new HashSet<string>(), 0, rewritten: false) &&
                ProvesManagedBase(from, to, new HashSet<string>(), 0);
        }

        private bool IsDelegateReferenceType(TypeSignature type, bool rewritten)
        {
            if (type == null || type.IsValueType || !InteropDelegateContracts.CanBeArgument(type, false)) return false;
            if (type is not GenericParameterSignature generic) return true;
            if (!rewritten) return IsReferenceGeneric(generic);
            return ProvesDelegateReferenceParameter(generic, new HashSet<(GenericParameterType, int)>());
        }

        private bool ProvesDelegateReferenceParameter(GenericParameterSignature generic, HashSet<(GenericParameterType, int)> visited)
        {
            if (!visited.Add((generic.ParameterType, generic.Index))) return false;
            var declaration = TargetGenericParameter(generic);
            if (declaration == null) return false;
            if ((declaration.Attributes & GenericParameterAttributes.ReferenceTypeConstraint) != 0) return true;
            if ((declaration.Attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0) return false;
            foreach (var constraint in declaration.Constraints)
            {
                var type = constraint.Constraint?.ToTypeSignature();
                if (type is GenericParameterSignature other)
                {
                    if (ProvesDelegateReferenceParameter(other, visited)) return true;
                }
                else if (type != null && InteropRestoredGenericSupport.GetIntrinsicConstraint(type) == null &&
                    type.Resolve() is { IsValueType: false, IsInterface: false })
                    return true;
            }
            return false;
        }

        private IMethodDescriptor ResolveDelegateCallback(IMethodDescriptor method,
            out InteropTypeContext declaredContext, out MethodDefinition definition, out bool managedCallback)
        {
            declaredContext = null;
            definition = null;
            managedCallback = false;
            var original = method.Resolve();
            // Static primitive callbacks belong to the native corlib wrapper, just like ordinary calls.
            var declarer = resolver.Resolve(method.DeclaringType?.ToTypeSignature(), method.Signature?.HasThis ?? true);
            var resolved = declarer?.Resolve();
            if (resolved == null || method.Name == ".ctor" || method.Name == ".cctor" ||
                !InteropDelegateContracts.HasStandardConvention(method.Signature))
                return Reject("delegate-callback-unavailable", "The callback declaration cannot be resolved.");
            declaredContext = original == null ? FindMappedSourceOwner(method.DeclaringType?.ToTypeSignature()) :
                context.TryGetAssemblyByName(original.DeclaringModule.Assembly.Name)
                    ?.TryGetTypeByName(original.DeclaringType.FullName);
            if (declaredContext?.NewType != resolved)
                return Reject("delegate-callback-unavailable", "The callback has no matching rewritten owner.");
            var mapped = original == null ? FindMappedNativeMethod(method, declaredContext) :
                declaredContext.TryGetMethodByUnityAssemblyMethod(original);
            original ??= mapped?.OriginalMethod;
            if (original == null || original.IsConstructor || original.IsAbstract && !original.IsVirtual)
                return Reject("delegate-callback-unavailable", "The callback has no exact source or mapped declaration.");
            if (mapped != null)
                definition = mapped.NewMethod;
            else if (declaredContext.TryGetRestoredManagedMethod(original) is { } managedTarget)
            {
                // A restored CLR method can belong to an existing native wrapper type.
                definition = managedTarget;
                managedCallback = true;
            }
            else
            {
                var returnType = resolver.Resolve(method.Signature.ReturnType);
                if (returnType == null) return Reject("delegate-callback-unavailable", "The callback return type is unresolved.");
                var signature = method.Signature.HasThis
                    ? MethodSignature.CreateInstance(returnType, method.Signature.GenericParameterCount)
                    : MethodSignature.CreateStatic(returnType, method.Signature.GenericParameterCount);
                foreach (var parameter in method.Signature.ParameterTypes)
                {
                    var rewritten = resolver.Resolve(parameter);
                    if (rewritten == null) return Reject("delegate-callback-unavailable", "A callback parameter is unresolved.");
                    signature.ParameterTypes.Add(rewritten);
                }
                definition = resolved.Methods.SingleOrDefault(candidate => candidate.Name == method.Name &&
                    SignatureComparer.Default.Equals(candidate.Signature, signature));
                managedCallback = declaredContext.OriginalType == null;
            }
            if (definition == null || !InteropDelegateContracts.HasStandardConvention(definition.Signature))
                return Reject("delegate-callback-unavailable", "The callback has no native wrapper or restored method declaration.");
            IMethodDefOrRef reference = new MemberReference(declarer.ToTypeDefOrRef(), definition.Name, definition.Signature);
            IMethodDescriptor result = reference;
            if (method is MethodSpecification specification)
            {
                if (specification.Signature.TypeArguments.Count != definition.Signature.GenericParameterCount)
                    return Reject("delegate-generic-arity", "The callback generic arity does not match its declaration.");
                var arguments = new TypeSignature[specification.Signature.TypeArguments.Count];
                for (var index = 0; index < arguments.Length; index++)
                {
                    var argument = specification.Signature.TypeArguments[index];
                    if (!managedCallback &&
                        InteropRestorationPlan.For(context).GetNativeIdentityRejection(argument) is string reason)
                        return Reject("delegate-generic-native-identity", reason);
                    arguments[index] = resolver.Resolve(argument);
                    if (arguments[index] == null) return Reject("delegate-generic-argument", "The callback generic argument is unresolved.");
                }
                result = reference.MakeGenericInstanceMethod(arguments);
            }
            else if (definition.Signature.GenericParameterCount != 0)
                return Reject("delegate-open-generic-method", "The callback requires an explicit method instantiation.");
            return ValidateManagedMethodUse(definition, result) ? result : null;

            IMethodDescriptor Reject(string code, string detail)
            {
                Fail(code, detail, currentInstruction);
                return null;
            }
        }

        private bool TryTranslateDelegateInstruction(CilInstruction instruction, out CilInstruction copy)
        {
            copy = null;
            if (delegateTargetBoxes.TryGetValue(instruction, out var boxedTarget))
            {
                copy = body.Add(CilOpCodes.Box, imports.Module.DefaultImporter.ImportType(boxedTarget.ToTypeDefOrRef()));
                return true;
            }
            if (delegatePointers.TryGetValue(instruction, out var pointer))
            {
                var first = pointer.ManagedCallback ? null : EmitNativeGenericIdentityChecks(pointer.SourceCallback, pointer.CallbackOwner);
                if (pointer.BoundTargetBox != null)
                {
                    // Proven class-constrained T: CLR box is a reference conversion, not native boxing.
                    var box = body.Add(CilOpCodes.Box, imports.Module.DefaultImporter.ImportType(pointer.BoundTargetBox.ToTypeDefOrRef()));
                    first ??= box;
                }
                copy = body.Add(instruction.OpCode, imports.Module.DefaultImporter.ImportMethod(pointer.Callback));
                copy = first ?? copy;
                return true;
            }
            if (delegateConstructors.TryGetValue(instruction, out var factory))
            {
                var constructor = new MemberReference(factory.ManagedType.ToTypeDefOrRef(), ".ctor",
                    MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void,
                        imports.Module.CorLibTypeFactory.Object, imports.Module.CorLibTypeFactory.IntPtr));
                copy = body.Add(CilOpCodes.Newobj, imports.Module.DefaultImporter.ImportMethod(constructor));
                if (factory.NativeType != null)
                    body.Add(CilOpCodes.Call, InteropDelegateContracts.Conversion(imports.Module, factory.NativeType));
                return true;
            }
            if (instruction.OpCode.Code is CilCode.Ldftn or CilCode.Ldvirtftn)
            {
                copy = FailInstruction("delegate-function-pointer-escape", "A CLR callback pointer may only feed a proven delegate factory.");
                return true;
            }
            return false;
        }
    }
}
