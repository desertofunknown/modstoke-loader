// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private MethodDefinition sourceMethod;

        private bool ValidateNativePointerConversion()
        {
            if (!TryGetStackValue(currentInstruction, 0, out var value)) return true;
            var source = value.Type;
            while (source is PinnedTypeSignature pinned) source = pinned.BaseType;
            if (source is not ByReferenceTypeSignature reference) return true;
            var element = resolver.Resolve(reference.BaseType);
            // Preserve the source storage and pinning; layout proof does not extend the pointer lifetime.
            if (element is PointerTypeSignature ||
                element != null && HasStackAddressLayout(reference.BaseType, element, new HashSet<TypeDefinition>()))
                return true;
            // Pinning a wrapper slot does not pin or expose its native value payload.
            return Fail("unsupported-native-pointer-layout",
                "A CLR reference can become a native pointer only when its element retains its native value layout.",
                currentInstruction);
        }

        private bool TryTranslateNativeTypeOperation(CilOpCode code, TypeSignature source, TypeSignature rewritten,
            out CilInstruction translated)
        {
            translated = null;
            if (code.Code is not (CilCode.Box or CilCode.Unbox_Any or CilCode.Castclass or CilCode.Isinst))
                return false;
            if (source is ArrayBaseTypeSignature) return false; // Rank/vector lowering owns array casts.
            var plan = InteropRestorationPlan.For(context);
            if (code.Code is CilCode.Castclass or CilCode.Isinst && plan.IsManagedReference(source))
            {
                if (!TryGetStackValue(currentInstruction, 0, out var value) ||
                    !value.IsNull && !plan.IsManagedReference(value.Type))
                    translated = FailInstruction("managed-reference-cast",
                        "A managed helper cast requires a proven CLR reference, not a native wrapper.");
                else
                    translated = body.Add(code, rewritten.ToTypeDefOrRef());
                return true;
            }
            var reason = plan.GetNativeIdentityRejection(source);
            if (reason != null)
            {
                translated = FailInstruction("native-type-identity-unavailable", reason);
                return true;
            }
            var nativeObject = context.CorLib.GetTypeByName("System.Object").NewType.ToTypeSignature();
            string helper;
            TypeSignature[] arguments;
            switch (code.Code)
            {
                case CilCode.Box:
                    helper = nameof(InteropGenericValueSupport.Box);
                    arguments = new[] { rewritten, nativeObject };
                    break;
                case CilCode.Unbox_Any:
                    helper = nameof(InteropGenericValueSupport.UnboxAny);
                    arguments = new[] { rewritten };
                    break;
                case CilCode.Castclass:
                    var boxedCast = source.IsValueType || source is GenericParameterSignature parameter && !IsReferenceGeneric(parameter);
                    helper = boxedCast ? nameof(InteropGenericValueSupport.CastBoxed) : nameof(InteropGenericValueSupport.CastReference);
                    arguments = boxedCast ? new[] { rewritten, nativeObject } : new[] { rewritten };
                    break;
                default:
                    var boxed = source.IsValueType || source is GenericParameterSignature generic && !IsReferenceGeneric(generic);
                    helper = boxed ? nameof(InteropGenericValueSupport.IsInstanceBoxed) : nameof(InteropGenericValueSupport.TryCastReference);
                    arguments = boxed ? new[] { rewritten, nativeObject } : new[] { rewritten };
                    break;
            }
            var method = (IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(typeof(InteropGenericValueSupport).GetMethod(helper));
            translated = body.Add(CilOpCodes.Call, method.MakeGenericInstanceMethod(arguments));
            return true;
        }

        private bool IsReferenceGeneric(GenericParameterSignature parameter) =>
            IsReferenceGeneric(parameter, new System.Collections.Generic.HashSet<(GenericParameterType, int)>());

        private bool IsReferenceGeneric(GenericParameterSignature parameter,
            System.Collections.Generic.HashSet<(GenericParameterType, int)> visited)
        {
            if (!visited.Add((parameter.ParameterType, parameter.Index))) return false;
            var parameters = parameter.ParameterType == GenericParameterType.Method
                ? sourceMethod.GenericParameters : sourceMethod.DeclaringType.GenericParameters;
            if (parameter.Index < 0 || parameter.Index >= parameters.Count) return false;
            var declaration = parameters[parameter.Index];
            if ((declaration.Attributes & GenericParameterAttributes.ReferenceTypeConstraint) != 0) return true;
            foreach (var constraint in declaration.Constraints)
            {
                var type = constraint.Constraint?.ToTypeSignature();
                if (type is GenericParameterSignature other)
                {
                    if (IsReferenceGeneric(other, visited)) return true;
                }
                else if (type != null && type.FullName is not ("System.Object" or "System.ValueType" or "System.Enum") &&
                    type.Resolve() is { IsValueType: false, IsInterface: false })
                    return true;
            }
            return false;
        }

        private CilInstruction EmitNativeGenericIdentityChecks(IMethodDescriptor method, InteropTypeContext declarer)
        {
            if (declarer?.OriginalType == null) return null;
            CilInstruction first = null;
            if (method.DeclaringType.ToTypeSignature() is GenericInstanceTypeSignature owner)
                foreach (var argument in owner.TypeArguments) Check(argument);
            if (method is MethodSpecification specification)
                foreach (var argument in specification.Signature.TypeArguments) Check(argument);
            return first;

            void Check(TypeSignature argument)
            {
                var rewritten = resolver.Resolve(argument);
                var requirement = (IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(
                    typeof(InteropGenericValueSupport).GetMethod(nameof(InteropGenericValueSupport.RequireNativeType)));
                var check = body.Add(CilOpCodes.Call, requirement.MakeGenericInstanceMethod(rewritten));
                first ??= check;
            }
        }
    }
}
