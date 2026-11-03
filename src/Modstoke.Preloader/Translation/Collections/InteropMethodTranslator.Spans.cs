// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Collections;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using MethodImplAttributes = AsmResolver.PE.DotNet.Metadata.Tables.MethodImplAttributes;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private sealed class NativeSpanBorrow
        {
            internal Parameter Parameter;
            internal TypeSignature SourceType, SourceElement, Wrapper;
            internal CilInstruction Instruction;
            internal InteropMethodContext Getter;
            internal CilLocalVariable Pin, Pointer, Root;
            internal bool ReadOnly;
        }

        private NativeSpanBorrow[] nativeSpanBorrows;

        private bool PlanNativeSpanBorrows(CilMethodBody original)
        {
            var resources = sourceMethod.DeclaringType.FullName == "UnityEngine.Resources" &&
                sourceMethod.Name == "InstanceIDsToValidArray";
            var strip = sourceMethod.Name == "DrawLineStrip";
            var gizmos = sourceMethod.DeclaringType.FullName == "UnityEngine.Gizmos" &&
                (strip || sourceMethod.Name == "DrawLineList");
            var parameters = sourceMethod.Signature.ParameterTypes;
            if ((!resources && !gizmos) || parameters.Count == 0 ||
                parameters[0] is not GenericInstanceTypeSignature { GenericType.FullName: "System.ReadOnlySpan`1" })
                return true;
            if (sourceMethod.DeclaringModule.Assembly.Name != "UnityEngine.CoreModule" ||
                !sourceMethod.IsStatic || sourceMethod.GenericParameters.Count != 0 ||
                !InteropDelegateContracts.HasStandardConvention(sourceMethod.Signature) ||
                sourceMethod.Signature.ReturnType.ElementType != ElementType.Void || original.ExceptionHandlers.Count != 0 ||
                parameters.Count != (resources || strip ? 2 : 1) ||
                strip && parameters[1].ElementType != ElementType.Boolean)
                return Reject();

            var borrows = new NativeSpanBorrow[resources ? 2 : 1];
            for (var index = 0; index < borrows.Length; index++)
            {
                var readOnly = index == 0;
                if (parameters[index] is not GenericInstanceTypeSignature span || !span.IsValueType ||
                    span.GenericType.FullName != (readOnly ? "System.ReadOnlySpan`1" : "System.Span`1") ||
                    !InteropArraySegmentLowering.HasSourceContract(span.GenericType) || span.TypeArguments.Count != 1 ||
                    !TryGetNativeValueWrapper(span, out var wrapper) ||
                    !SignatureComparer.Default.Equals(TargetParameter(sourceMethod.Parameters[index]).ParameterType, wrapper) ||
                    !HasNativeSpanElement(span.TypeArguments[0], resources, readOnly)) return Reject();
                borrows[index] = new NativeSpanBorrow
                {
                    Parameter = sourceMethod.Parameters[index], SourceType = span,
                    SourceElement = span.TypeArguments[0], Wrapper = wrapper, ReadOnly = readOnly
                };
            }

            var source = original.Instructions.Where(instruction => instruction.OpCode.Code != CilCode.Nop).ToArray();
            var start = Array.FindIndex(source, instruction => instruction.OpCode.Code == CilCode.Call &&
                instruction.Operand is IMethodDescriptor { Name: var name } && name == "GetPinnableReference") - 1;
            if (start < 0 || !inputStacks.TryGetValue(source[start], out var stack) || stack?.Count != 0)
                return Reject();
            var cursor = start;
            var slots = new HashSet<CilLocalVariable>();
            foreach (var borrow in borrows)
            {
                if (!Take(CilCode.Ldarga, borrow.Parameter) || cursor >= source.Length ||
                    source[cursor].OpCode.Code != CilCode.Call || source[cursor].Operand is not IMethodDescriptor getter ||
                    !BindNativeSpanGetter(getter, borrow)) return Reject();
                borrow.Instruction = source[cursor++];
                if (cursor >= source.Length || source[cursor].Operand is not CilLocalVariable pin ||
                    pin.VariableType is not PinnedTypeSignature { BaseType: ByReferenceTypeSignature reference } ||
                    !SignatureComparer.Default.Equals(reference.BaseType, borrow.SourceElement) || !slots.Add(pin) ||
                    !Take(CilCode.Stloc, pin) || !Take(CilCode.Ldloc, pin) || !Take(CilCode.Conv_U) ||
                    cursor >= source.Length || source[cursor].Operand is not CilLocalVariable pointer ||
                    pointer.VariableType is not PointerTypeSignature pointerType ||
                    !SignatureComparer.Default.Equals(pointerType.BaseType, borrow.SourceElement) || !slots.Add(pointer) ||
                    !Take(CilCode.Stloc, pointer)) return Reject();
                borrow.Pin = pin;
                borrow.Pointer = pointer;
            }
            foreach (var borrow in borrows)
            {
                if (!Take(CilCode.Ldloc, borrow.Pointer)) return Reject();
                if (resources && (cursor >= source.Length ||
                    source[cursor].Operand is not IMethodDescriptor conversion || !IsSpanPointerConversion(conversion) ||
                    !Take(CilCode.Call))) return Reject();
                if (!TakeLength(borrow)) return Reject();
            }
            if (strip && !Take(CilCode.Ldarg, sourceMethod.Parameters[1])) return Reject();
            if (cursor >= source.Length || source[cursor].Operand is not IMethodDescriptor consumer ||
                !IsNativeSpanConsumer(consumer, borrows, resources, strip) || !Take(CilCode.Call)) return Reject();
            for (var index = borrows.Length - 1; index >= 0; index--)
                if (!Take(CilCode.Ldc_I4, 0) || !Take(CilCode.Conv_U) || !Take(CilCode.Stloc, borrows[index].Pin))
                    return Reject();
            if (!Take(CilCode.Ret) || cursor != source.Length) return Reject();

            // Only guards precede the fixed region. No branch may enter its interior,
            // repeat acquisition, or bypass a clear while retaining an alias.
            for (var index = 0; index < start; index++)
            {
                var instruction = source[index];
                switch (instruction.OpCode.Code)
                {
                    case CilCode.Ldarga:
                        var borrow = borrows.FirstOrDefault(item => ReferenceEquals(item.Parameter, instruction.Operand));
                        if (borrow == null || index + 1 >= start || source[index + 1].OpCode.Code != CilCode.Call ||
                            source[++index].Operand is not IMethodDescriptor length || !IsNativeSpanLength(length, borrow))
                            return Reject();
                        break;
                    case CilCode.Ldloc:
                    case CilCode.Stloc:
                        if (instruction.Operand is not CilLocalVariable local ||
                            local.VariableType.ElementType != ElementType.Boolean) return Reject();
                        break;
                    case CilCode.Br:
                    case CilCode.Brfalse:
                        if (instruction.Operand is not CilInstructionLabel { Instruction: not null } label) return Reject();
                        var destination = original.Instructions.IndexOf(label.Instruction);
                        if (destination < 0) return Reject();
                        while (destination < original.Instructions.Count &&
                            original.Instructions[destination].OpCode.Code == CilCode.Nop) destination++;
                        if (destination >= original.Instructions.Count ||
                            destination <= original.Instructions.IndexOf(instruction) ||
                            destination > original.Instructions.IndexOf(source[start]) &&
                            !ReferenceEquals(original.Instructions[destination], source[source.Length - 1])) return Reject();
                        break;
                    case CilCode.Newobj:
                        if (instruction.Operand is not IMethodDescriptor constructor ||
                            !IsSpanGuardException(constructor)) return Reject();
                        break;
                    case CilCode.Ret:
                        if (!inputStacks.TryGetValue(instruction, out var returnStack) || returnStack?.Count != 0)
                            return Reject();
                        break;
                    case CilCode.Ldc_I4:
                    case CilCode.And:
                    case CilCode.Ceq:
                    case CilCode.Cgt_Un:
                    case CilCode.Ldstr:
                    case CilCode.Throw:
                        break;
                    default:
                        return Reject();
                }
            }
            foreach (var borrow in borrows) borrow.Root = AddArrayLocal(borrow.Wrapper);
            target.CilMethodBody.InitializeLocals = true;
            nativeSpanBorrows = borrows;
            return true;

            bool Take(CilCode code, object operand = null)
            {
                if (cursor >= source.Length || source[cursor].OpCode.Code != code ||
                    operand != null && !Equals(source[cursor].Operand, operand)) return false;
                cursor++;
                return true;
            }
            bool TakeLength(NativeSpanBorrow borrow) => Take(CilCode.Ldarga, borrow.Parameter) &&
                cursor < source.Length && source[cursor].Operand is IMethodDescriptor length &&
                IsNativeSpanLength(length, borrow) && Take(CilCode.Call);
            bool Reject() => Fail("native-span-borrow-contract",
                "The native span requires an exact fixed-reference flow into its synchronous Unity pointer consumer.");
        }

        private bool HasNativeSpanElement(TypeSignature source, bool resources, bool readOnly)
        {
            var rewritten = resolver.Resolve(source);
            if (rewritten == null || !IsBlittableAddressValue(source, rewritten) ||
                InteropRestorationPlan.For(context).GetNativeIdentityRejection(source) != null) return false;
            if (resources)
                return source is CorLibTypeSignature && rewritten is CorLibTypeSignature &&
                    source.ElementType == (readOnly ? ElementType.I4 : ElementType.Boolean) &&
                    rewritten.ElementType == source.ElementType;
            var mapped = FindMappedSourceOwner(source);
            if (source is not TypeDefOrRefSignature || source.FullName != "UnityEngine.Vector3" ||
                InteropRestoredTypeSupport.GetAssemblyName(source) != "UnityEngine.CoreModule" ||
                mapped?.OriginalType?.FullName != "UnityEngine.Vector3" || mapped.NewType != rewritten.Resolve() ||
                !mapped.NewType.IsExplicitLayout ||
                mapped.NewType.ClassLayout is { } layout &&
                    (layout.ClassSize is not (0 or 12) || layout.PackingSize is not (0 or 4 or 8)) ||
                mapped.OriginalType.ClassLayout is { } nativeLayout &&
                    (nativeLayout.ClassSize is not (0 or 12) || nativeLayout.PackingSize is not (0 or 4 or 8))) return false;
            var fields = mapped.NewType.Fields.Where(field => !field.IsStatic).ToArray();
            var nativeFields = mapped.OriginalType.Fields.Where(field => !field.IsStatic).ToArray();
            var names = new[] { "x", "y", "z" };
            // Three R4 fields at 0/4/8 with ordinary packing have a 12-byte stride.
            // An explicit larger size or marshaled field must not change that contract.
            return fields.Length == 3 && nativeFields.Length == 3 &&
                fields.Concat(nativeFields).All(field => !field.HasFieldMarshal && field.MarshalDescriptor == null && field.FieldRva == null) &&
                names.Select((name, index) => fields.Any(field =>
                field.Name == name && field.FieldOffset == index * 4 &&
                field.Signature.FieldType is CorLibTypeSignature { ElementType: ElementType.R4 })).All(matches => matches);
        }

        private bool BindNativeSpanGetter(IMethodDescriptor source, NativeSpanBorrow borrow)
        {
            // Resolved facade definitions use a different member-resolution path.
            if (source is MethodSpecification || source.Name != "GetPinnableReference" ||
                source.Resolve() != null ||
                !SignatureComparer.Default.Equals(source.DeclaringType?.ToTypeSignature(), borrow.SourceType) ||
                !IsSpanInstanceSignature(source.Signature)) return false;
            var result = source.Signature.ReturnType;
            if (borrow.ReadOnly)
            {
                if (result is not CustomModifierTypeSignature { IsRequired: true } modifier ||
                    modifier.ModifierType.FullName != "System.Runtime.InteropServices.InAttribute" ||
                    !InteropArraySegmentLowering.HasSourceContract(modifier.ModifierType)) return false;
                result = modifier.BaseType;
            }
            if (!IsSpanElementReference(result)) return false;
            var owner = FindMappedSourceOwner(borrow.SourceType);
            if (owner?.OriginalType == null || owner != context.CorLib.TryGetTypeByName(
                    borrow.ReadOnly ? "System.ReadOnlySpan`1" : "System.Span`1") ||
                owner.NewType != borrow.Wrapper.Resolve()) return false;
            foreach (var candidate in owner.Methods)
            {
                var original = candidate.OriginalMethod;
                if (original.Name != source.Name || !original.IsPublic || !candidate.NewMethod.IsPublic ||
                    !IsSpanInstanceSignature(original.Signature) || !IsSpanElementReference(original.Signature.ReturnType) ||
                    !IsSpanInstanceSignature(candidate.NewMethod.Signature) ||
                    !IsSpanElementReference(candidate.NewMethod.Signature.ReturnType)) continue;
                if (borrow.Getter != null) return false;
                borrow.Getter = candidate;
            }
            return borrow.Getter != null;
        }

        private static bool IsSpanInstanceSignature(MethodSignature signature) =>
            signature is { HasThis: true, GenericParameterCount: 0 } && signature.ParameterTypes.Count == 0 &&
            InteropDelegateContracts.HasStandardConvention(signature);

        private static bool IsSpanElementReference(TypeSignature type) => type is ByReferenceTypeSignature
        { BaseType: GenericParameterSignature { ParameterType: GenericParameterType.Type, Index: 0 } };

        private static bool IsNativeSpanLength(IMethodDescriptor method, NativeSpanBorrow borrow) =>
            method is not MethodSpecification && method.Name == "get_Length" &&
            SignatureComparer.Default.Equals(method.DeclaringType?.ToTypeSignature(), borrow.SourceType) &&
            IsSpanInstanceSignature(method.Signature) && method.Signature.ReturnType.ElementType == ElementType.I4;

        private static bool IsSpanPointerConversion(IMethodDescriptor method) =>
            method is not MethodSpecification && method.Name == "op_Explicit" &&
            method.DeclaringType?.FullName == "System.IntPtr" &&
            InteropArraySegmentLowering.HasSourceContract(method.DeclaringType.ToTypeSignature().ToTypeDefOrRef()) &&
            method.Signature is { HasThis: false, GenericParameterCount: 0 } signature &&
            InteropDelegateContracts.HasStandardConvention(signature) && signature.ReturnType.ElementType == ElementType.I &&
            signature.ParameterTypes.Count == 1 && signature.ParameterTypes[0] is
                PointerTypeSignature { BaseType.ElementType: ElementType.Void };

        private static bool IsSpanGuardException(IMethodDescriptor method) =>
            method is not MethodSpecification && method.Name == ".ctor" &&
            method.Signature is { HasThis: true, GenericParameterCount: 0 } signature &&
            InteropDelegateContracts.HasStandardConvention(signature) && signature.ReturnType.ElementType == ElementType.Void &&
            signature.ParameterTypes.Count == 1 && signature.ParameterTypes[0].ElementType == ElementType.String &&
            (method.DeclaringType?.FullName == "System.ArgumentException" &&
             InteropArraySegmentLowering.HasSourceContract(method.DeclaringType.ToTypeSignature().ToTypeDefOrRef()) ||
             method.DeclaringType?.FullName == "UnityEngine.UnityException" &&
             InteropRestoredTypeSupport.GetAssemblyName(method.DeclaringType.ToTypeSignature()) == "UnityEngine.CoreModule");

        private bool IsNativeSpanConsumer(IMethodDescriptor method, NativeSpanBorrow[] borrows, bool resources, bool strip)
        {
            if (method is MethodSpecification || method.Signature is not { HasThis: false, GenericParameterCount: 0 } signature ||
                !InteropDelegateContracts.HasStandardConvention(signature) || signature.ReturnType.ElementType != ElementType.Void ||
                method.Name != (resources ? "InstanceIDsToValidArray_Internal" : (string)sourceMethod.Name) ||
                method.Resolve() is not { IsStatic: true } definition || definition.DeclaringType != sourceMethod.DeclaringType ||
                (definition.ImplAttributes & MethodImplAttributes.InternalCall) == 0 ||
                !SignatureComparer.Default.Equals(method.Signature, definition.Signature) ||
                signature.ParameterTypes.Count != (resources ? 4 : strip ? 3 : 2)) return false;
            for (var index = 0; index < borrows.Length; index++)
            {
                var pointer = signature.ParameterTypes[index * 2];
                if (resources ? pointer.ElementType != ElementType.I :
                    !SignatureComparer.Default.Equals(pointer, borrows[index].SourceElement.MakePointerType())) return false;
                if (signature.ParameterTypes[index * 2 + 1].ElementType != ElementType.I4) return false;
            }
            return !strip || signature.ParameterTypes[2].ElementType == ElementType.Boolean;
        }

        private NativeSpanBorrow FindNativeSpanBorrow(IMethodDescriptor source) => nativeSpanBorrows?.FirstOrDefault(borrow =>
            ReferenceEquals(borrow.Instruction, currentInstruction) && ReferenceEquals(borrow.Instruction.Operand, source));

        private bool IsPlannedNativeSpanResult(IMethodDescriptor source, IMethodDescriptor rewritten) =>
            FindNativeSpanBorrow(source) is { ReadOnly: true, Root: not null } borrow &&
            SignatureComparer.Default.Equals(rewritten.DeclaringType?.ToTypeSignature(), borrow.Wrapper) &&
            rewritten.Name == borrow.Getter.NewMethod.Name &&
            SignatureComparer.Default.Equals(rewritten.Signature, borrow.Getter.NewMethod.Signature);

        private bool TryTranslateNativeSpanBorrow(CilInstruction instruction, out CilInstruction translated)
        {
            translated = null;
            if (instruction.Operand is not IMethodDescriptor method || FindNativeSpanBorrow(method) is not { } borrow)
                return false;
            if (!TryGetStackValue(instruction, 0, out var value) || value.Type is not ByReferenceTypeSignature reference ||
                !SignatureComparer.Default.Equals(reference.BaseType, borrow.SourceType))
            {
                translated = FailInstruction("native-span-receiver-contract", "The borrowed span requires its exact parameter location.");
                return true;
            }
            // Receiver normalizes a null descriptor to the existing native zero/default value.
            var first = EmitValueStorageCall(nameof(InteropNativeValueStorage.Receiver), borrow.Wrapper);
            body.Add(CilOpCodes.Stloc, borrow.Root);
            body.Add(CilOpCodes.Ldloc, borrow.Root);
            if (TranslateMethod(instruction.OpCode, method) == null) return true;
            translated = first;
            return true;
        }

        private void CompleteNativeSpanBorrows()
        {
            if (nativeSpanBorrows == null) return;
            var start = new CilInstructionLabel(body[0]);
            var exit = new CilInstructionLabel();
            foreach (var instruction in body.Where(item => item.OpCode.Code == CilCode.Ret).ToArray())
            {
                instruction.OpCode = CilOpCodes.Leave;
                instruction.Operand = exit;
            }
            var cleanup = new CilInstructionLabel(body.Add(CilOpCodes.Nop));
            // These synchronous calls borrow backing storage; callers keep it valid and immobile.
            // Rooting the descriptor through exceptions does not pin or own the backing buffer.
            foreach (var borrow in nativeSpanBorrows) EmitAddressKeepAlive(borrow.Root);
            body.Add(CilOpCodes.Endfinally);
            exit.Instruction = body.Add(CilOpCodes.Ret);
            target.CilMethodBody.ExceptionHandlers.Add(new CilExceptionHandler
            {
                HandlerType = CilExceptionHandlerType.Finally, TryStart = start, TryEnd = cleanup,
                HandlerStart = cleanup, HandlerEnd = exit
            });
        }

        private bool TryTranslateSpanMethod(CilOpCode code, IMethodDescriptor method,
            out CilInstruction translated)
        {
            translated = null;
            var sourceOwner = method.DeclaringType?.ToTypeSignature();
            if (!InteropSpanLowering.TryGetSpan(context, sourceOwner, out _, out var runtimeType)) return false;
            translated = TranslatePublicClrContractMethod(code, method, runtimeType,
                IsStandardSpanMember(runtimeType, method), "managed-span-method-unavailable");
            return true;
        }

        private CilInstruction TranslatePublicClrContractMethod(CilOpCode code, IMethodDescriptor method,
            Type runtimeType, bool supportedMember, string failureCode, TypeSignature targetOwner = null)
        {
            var sourceOwner = method.DeclaringType.ToTypeSignature();
            var signature = method.Signature;
            var constructor = method.Name == ".ctor";
            var supportedCode = runtimeType.IsInterface
                ? code.Code == CilCode.Callvirt && signature?.HasThis == true
                : code.Code is CilCode.Call or CilCode.Newobj ||
                    code.Code == CilCode.Callvirt && !runtimeType.IsValueType && signature?.HasThis == true && !constructor;
            if (!supportedCode || method is MethodSpecification ||
                !InteropDelegateContracts.HasStandardConvention(signature) || signature.GenericParameterCount != 0 ||
                code.Code == CilCode.Newobj && !constructor || constructor && !signature.HasThis ||
                !supportedMember) return Reject();

            var owner = targetOwner ?? resolver.Resolve(sourceOwner);
            if (owner == null) return Reject();
            var substitution = GenericContext.FromMethod(method);
            var result = resolver.Resolve(signature.ReturnType.InstantiateGenericTypes(substitution));
            var parameters = signature.ParameterTypes.Select(parameter =>
                resolver.Resolve(parameter.InstantiateGenericTypes(substitution))).ToArray();
            if (result == null || parameters.Any(parameter => parameter == null)) return Reject();

            // The source facade need not contain an implementation. Match its public contract
            // against CLR metadata, including readonly-return modifiers, not private fields.
            var candidates = constructor
                ? runtimeType.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Cast<MethodBase>()
                : runtimeType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Where(candidate => candidate.Name == (string)method.Name).Cast<MethodBase>();
            IMethodDescriptor selected = null;
            foreach (var candidate in candidates)
            {
                if (candidate.IsStatic == signature.HasThis || candidate is MethodInfo { IsGenericMethod: true }) continue;
                var imported = imports.Module.DefaultImporter.ImportMethod(candidate);
                IMethodDescriptor reference = new MemberReference(owner.ToTypeDefOrRef(), imported.Name, imported.Signature);
                if (!MatchesSubstitutedCallSignature(reference, result, parameters)) continue;
                if (selected != null) return Reject();
                selected = reference;
            }
            if (selected == null) return Reject();
            return body.Add(code, imports.Module.DefaultImporter.ImportMethod(selected));

            CilInstruction Reject() => FailInstruction(failureCode,
                "The operation has no matching public CLR contract. Native or private-layout fallback is unavailable.");
        }

        private static bool MatchesSubstitutedCallSignature(IMethodDescriptor method, TypeSignature returnType,
            IReadOnlyList<TypeSignature> parameters)
        {
            var signature = method.Signature;
            var substitution = GenericContext.FromMethod(method);
            if (!SignatureComparer.Default.Equals(returnType, signature.ReturnType.InstantiateGenericTypes(substitution)) ||
                signature.ParameterTypes.Count != parameters.Count)
                return false;
            for (var index = 0; index < parameters.Count; index++)
                if (!SignatureComparer.Default.Equals(parameters[index],
                    signature.ParameterTypes[index].InstantiateGenericTypes(substitution)))
                    return false;
            return true;
        }

        private static bool IsStandardSpanMember(Type runtimeType, IMethodDescriptor method)
        {
            var signature = method.Signature;
            if (signature == null) return false;
            var count = signature.ParameterTypes.Count;
            var name = (string)method.Name;
            if (runtimeType == typeof(Span<>.Enumerator) || runtimeType == typeof(ReadOnlySpan<>.Enumerator))
                return signature.HasThis && count == 0 && (name is "MoveNext" or "get_Current");
            return name switch
            {
                // .NET Standard 2.1 has array and pointer constructors, not the later ref-T overloads.
                ".ctor" => signature.HasThis && ((count is 1 or 3) && signature.ParameterTypes[0] is SzArrayTypeSignature ||
                    count == 2 && IsStorageVoidPointer(signature.ParameterTypes[0])),
                "get_Empty" => !signature.HasThis && count == 0,
                "get_Length" or "get_IsEmpty" or "GetPinnableReference" or "GetEnumerator" or "ToArray" or
                    "ToString" or "GetHashCode" => signature.HasThis && count == 0,
                "get_Item" or "CopyTo" or "TryCopyTo" or "Equals" => signature.HasThis && count == 1,
                "Slice" => signature.HasThis && count is 1 or 2,
                "Clear" => runtimeType == typeof(Span<>) && signature.HasThis && count == 0,
                "Fill" => runtimeType == typeof(Span<>) && signature.HasThis && count == 1,
                "op_Implicit" => !signature.HasThis && count == 1,
                "op_Equality" or "op_Inequality" => !signature.HasThis && count == 2,
                _ => false
            };
        }
    }
}
