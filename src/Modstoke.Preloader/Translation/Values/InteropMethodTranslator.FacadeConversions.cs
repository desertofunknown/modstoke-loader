// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private sealed class NativeFacadeCallConversions
        {
            internal readonly Dictionary<int, TypeSignature> Arguments = new();
            internal CilLocalVariable[] Spills;
            internal TypeSignature Result;
        }

        private bool PlanMappedNativeCall(CilOpCode code, IMethodDescriptor source, IMethodDescriptor rewritten,
            out NativeFacadeCallConversions plan)
        {
            plan = null;
            NativeFacadeCallConversions conversions = null;
            var sourceContext = GenericContext.FromMethod(source);
            var rewrittenContext = GenericContext.FromMethod(rewritten);
            var sourceResult = source.Signature.ReturnType.InstantiateGenericTypes(sourceContext);
            var expectedResult = resolver.Resolve(sourceResult);
            var actualResult = rewritten.Signature.ReturnType.InstantiateGenericTypes(rewrittenContext);
            if (expectedResult == null) return ResultFailure();
            if (!SignatureComparer.Default.Equals(expectedResult, actualResult) &&
                !IsPlannedNativeSpanResult(source, rewritten))
            {
                if (!IsNativeVectorCarrier(sourceResult, expectedResult, actualResult)) return ResultFailure();
                conversions = new NativeFacadeCallConversions { Result = expectedResult };
            }

            for (var index = 0; index < source.Signature.ParameterTypes.Count; index++)
            {
                var parameter = source.Signature.ParameterTypes[index].InstantiateGenericTypes(sourceContext);
                var expected = resolver.Resolve(parameter);
                var actual = rewritten.Signature.ParameterTypes[index].InstantiateGenericTypes(rewrittenContext);
                if (expected == null || !SignatureComparer.Default.Equals(expected, actual) &&
                    !IsNativeVectorCarrier(parameter, expected, actual) ||
                    !ArgumentCompatible(parameter, actual, source.Signature.ParameterTypes.Count - index - 1, false))
                    return Fail("native-facade-argument-conversion", "The mapped native argument needs an explicit representation conversion.", currentInstruction);
            }
            if (code.Code != CilCode.Newobj && source.Signature.HasThis &&
                !ArgumentCompatible(source.DeclaringType.ToTypeSignature(), rewritten.DeclaringType.ToTypeSignature(),
                    source.Signature.ParameterTypes.Count, true))
                return Fail("native-facade-receiver-conversion", "The mapped native receiver needs an explicit representation conversion.", currentInstruction);

            if (conversions?.Arguments.Count > 0)
            {
                var depth = 0;
                foreach (var index in conversions.Arguments.Keys)
                    if (index > depth) depth = index;
                conversions.Spills = new CilLocalVariable[depth + 1];
                for (var index = 0; index <= depth; index++)
                {
                    if (!TryGetStackValue(currentInstruction, index, out var value) ||
                        value.Type is ByReferenceTypeSignature or PointerTypeSignature or FunctionPointerTypeSignature)
                        return Fail("native-facade-conversion-stack", "A reference conversion cannot spill an unknown value or native alias.", currentInstruction);
                    if (value.IsNull) continue;
                    var type = resolver.Resolve(value.Type);
                    if (type == null || !InteropDelegateContracts.CanBeArgument(type, false))
                        return Fail("native-facade-conversion-stack", "The reference conversion stack has no spillable representation.", currentInstruction);
                    conversions.Spills[index] = AddArrayLocal(type);
                }
            }
            plan = conversions;
            return true;

            bool ResultFailure() => Fail("native-facade-result-conversion",
                "The mapped native result needs an explicit representation conversion.", currentInstruction);

            bool ArgumentCompatible(TypeSignature sourceParameter, TypeSignature rewrittenParameter, int depth, bool receiver)
            {
                // The validated constrained prefix has already replaced the source byref.
                // Only its exact emitted receiver type can satisfy this mapped call.
                if (receiver && convertedConstrainedReceivers.TryGetValue(currentInstruction, out var converted))
                    return SignatureComparer.Default.Equals(converted, rewrittenParameter);
                if (!TryGetStackValue(currentInstruction, depth, out var value)) return false;
                if (value.IsNull) return IsDelegateReferenceType(rewrittenParameter, true);
                var sourceValue = value.Type;
                if (receiver && FindNativeSpanBorrow(source) is { Root: not null } borrow &&
                    sourceValue is ByReferenceTypeSignature spanReference &&
                    SignatureComparer.Default.Equals(spanReference.BaseType, borrow.SourceType) &&
                    SignatureComparer.Default.Equals(sourceParameter, borrow.SourceType) &&
                    SignatureComparer.Default.Equals(rewrittenParameter, borrow.Wrapper)) return true;
                // An unresolved facade can still have a proven native value layout. Blittable
                // receivers retain the CLR byref; boxed receivers use the value-call lowering.
                if (receiver && sourceValue is ByReferenceTypeSignature address &&
                    (sourceParameter.IsValueType || rewrittenParameter.IsValueType &&
                        FindMappedSourceOwner(sourceParameter) is
                        {
                            OriginalType.IsValueType: true,
                            ComputedTypeSpecifics: InteropTypeContext.TypeSpecifics.BlittableStruct
                        } mappedReceiver && mappedReceiver.NewType == rewrittenParameter.Resolve()))
                    sourceValue = address.BaseType;
                var rewrittenValue = resolver.Resolve(sourceValue);
                if (rewrittenValue == null) return false;
                if (DelegateTypeCompatible(sourceValue, sourceParameter, rewrittenValue, rewrittenParameter)) return true;
                // void* consumes an unmanaged address, including a typed pointer or an
                // explicit conv.i/conv.u result. CLR byrefs are not interchangeable with it.
                if (!receiver && IsStorageVoidPointer(sourceParameter) && IsStorageVoidPointer(rewrittenParameter) &&
                    rewrittenValue is PointerTypeSignature or CorLibTypeSignature { ElementType: ElementType.I or ElementType.U })
                    return true;
                var objectTarget = SignatureComparer.Default.Equals(rewrittenParameter,
                    context.CorLib.GetTypeByName("System.Object").NewType.ToTypeSignature());
                var stringObject = objectTarget && sourceValue.ElementType == ElementType.String &&
                    rewrittenValue.ElementType == ElementType.String;
                var interfaceObject = objectTarget &&
                    FindMappedSourceOwner(sourceValue) is { OriginalType.IsInterface: true } sourceInterface &&
                    sourceInterface.NewType == rewrittenValue.Resolve() && IsNativeArrayReference(rewrittenValue);
                var arrayBase = sourceValue is ArrayBaseTypeSignature &&
                    (objectTarget || SignatureComparer.Default.Equals(rewrittenParameter,
                        context.CorLib.GetTypeByName("System.Array").NewType.ToTypeSignature())) &&
                    TryGetAddressElement(sourceValue, out var array) && IsNativeArrayReference(array);
                if (stringObject || interfaceObject || arrayBase || CanWrapBoxedBase(value, sourceParameter, rewrittenParameter) ||
                    CanWrapNativeInterfaceArgument(currentInstruction, depth, sourceParameter, rewrittenParameter))
                {
                    // The checked cast marshals CLR strings and preserves native object identity,
                    // including closed interface contracts that CLR wrapper inheritance cannot express.
                    conversions ??= new NativeFacadeCallConversions();
                    conversions.Arguments.Add(depth, rewrittenParameter);
                    return true;
                }
                // Facade enum definitions may be absent; the exact mapped parameter retains
                // its underlying numeric storage and is the type the emitted call consumes.
                var numericValue = NumericStackValue(rewrittenValue);
                return !receiver && numericValue.Type != null &&
                    SameStackValue(numericValue, NumericStackValue(rewrittenParameter));
            }
        }

        private bool CanWrapBoxedBase(StackValue value, TypeSignature sourceParameter, TypeSignature rewrittenParameter)
        {
            if (value.BoxedType == null || value.Type?.ElementType != ElementType.Object ||
                InteropRestorationPlan.For(context).GetNativeIdentityRejection(value.BoxedType) != null ||
                FindMappedSourceOwner(value.BoxedType)?.OriginalType is not { IsValueType: true } nativeType)
                return false;
            var intrinsic = InteropRestoredGenericSupport.GetIntrinsicConstraint(sourceParameter);
            var name = intrinsic == typeof(System.ValueType) ? "System.ValueType" :
                intrinsic == typeof(System.Enum) && nativeType.IsEnum ? "System.Enum" : null;
            return name != null && SignatureComparer.Default.Equals(rewrittenParameter,
                context.CorLib.GetTypeByName(name).NewType.ToTypeSignature());
        }

        private bool IsNativeVectorCarrier(TypeSignature source, TypeSignature vector, TypeSignature carrier)
        {
            if (source is not SzArrayTypeSignature array || carrier is not GenericInstanceTypeSignature generic ||
                !SignatureComparer.Default.Equals(generic.GenericType, imports.Il2CppArrayBase.ToTypeDefOrRef()) ||
                generic.TypeArguments.Count != 1 ||
                !SignatureComparer.Default.Equals(resolver.Resolve(array.BaseType), generic.TypeArguments[0])) return false;
            return ProvesManagedBase(vector, carrier, new HashSet<string>(), 0);
        }

        private CilInstruction EmitFacadeCallArguments(NativeFacadeCallConversions plan)
        {
            if (plan?.Spills == null) return null;
            CilInstruction first = null;
            for (var index = 0; index < plan.Spills.Length; index++)
            {
                var spill = plan.Spills[index] == null ? body.Add(CilOpCodes.Pop) : body.Add(CilOpCodes.Stloc, plan.Spills[index]);
                first ??= spill;
            }
            for (var index = plan.Spills.Length - 1; index >= 0; index--)
            {
                if (plan.Spills[index] == null) body.Add(CilOpCodes.Ldnull);
                else body.Add(CilOpCodes.Ldloc, plan.Spills[index]);
                if (plan.Arguments.TryGetValue(index, out var contract))
                    body.Add(CilOpCodes.Call, ((IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(
                        typeof(InteropGenericValueSupport).GetMethod(nameof(InteropGenericValueSupport.CastReference))))
                        .MakeGenericInstanceMethod(contract));
            }
            return first;
        }

        private void EmitFacadeCallResult(NativeFacadeCallConversions plan)
        {
            if (plan == null) return;
            // Native generic array wrappers already select the concrete vector type for T.
            // A CLR cast preserves null, array identity and aliases without a second wrapper.
            if (plan.Result != null) body.Add(CilOpCodes.Castclass, plan.Result.ToTypeDefOrRef());
            foreach (var depth in plan.Arguments.Keys)
                EmitAddressKeepAlive(plan.Spills[depth]);
        }
    }
}
