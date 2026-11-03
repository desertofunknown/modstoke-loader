// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private bool TryTranslateManagedDictionaryMethod(CilOpCode code, IMethodDescriptor method,
            out CilInstruction translated)
        {
            translated = null;
            var sourceOwner = method.DeclaringType?.ToTypeSignature();
            if (!InteropManagedDictionaryLowering.TryGetStorage(context, sourceOwner, out var key, out var value, out var runtimeType))
                return false;
            if (method.Name == ".ctor" && runtimeType == typeof(InteropNativeKeyDictionary<,>))
            {
                translated = TranslateManagedDictionaryConstructor(code, method, key);
                return true;
            }
            if (method.Name == ".ctor" && runtimeType == typeof(KeyValuePair<,>))
            {
                translated = TranslateManagedPairConstructor(code, method, key, value);
                return true;
            }
            if (method.Signature is not { HasThis: true } signature ||
                !TryGetStackValue(currentInstruction, signature.ParameterTypes.Count, out var receiver) ||
                (runtimeType.IsValueType
                    ? receiver.Type is not ByReferenceTypeSignature reference || !SignatureComparer.Default.Equals(reference.BaseType, sourceOwner)
                    : !receiver.IsNull && !SignatureComparer.Default.Equals(receiver.Type, sourceOwner)))
            {
                translated = FailInstruction("managed-dictionary-receiver", "Dictionary dispatch requires its exact CLR storage contract.");
                return true;
            }

            var count = signature.ParameterTypes.Count;
            if ((method.Name == "ContainsValue" || runtimeType == typeof(InteropNativeKeyDictionary<,>.ValueCollection) && method.Name == "Contains") &&
                (InteropManagedDictionaryLowering.NeedsNativeEquality(context, value) ||
                    value is TypeDefOrRefSignature { IsValueType: true } &&
                    InteropRestorationPlan.For(context).IsManagedOnlyDefinition(value)))
            {
                translated = FailInstruction("managed-dictionary-native-value-equality",
                    "The stored value needs a proven equality contract; CLR default equality is not a substitute.");
                return true;
            }
            bool supported;
            if (runtimeType == typeof(InteropNativeKeyDictionary<,>))
                supported = (string)method.Name switch
                {
                    "get_Count" or "Clear" or "get_Keys" or "get_Values" or "GetEnumerator" => count == 0,
                    "get_Item" or "ContainsKey" or "ContainsValue" or "EnsureCapacity" => count == 1,
                    "set_Item" or "Add" or "TryAdd" or "TryGetValue" => count == 2,
                    "Remove" => count is 1 or 2,
                    "TrimExcess" => count is 0 or 1,
                    _ => false
                };
            else if (runtimeType == typeof(KeyValuePair<,>))
                supported = count == 0 && (string)method.Name is "get_Key" or "get_Value" || method.Name == "Deconstruct" && count == 2;
            else if (InteropManagedDictionaryLowering.IsEnumerator(runtimeType))
                supported = count == 0 && (string)method.Name is "get_Current" or "MoveNext" or "Dispose";
            else
                supported = count == 0 && (string)method.Name is "get_Count" or "GetEnumerator" ||
                    method.Name == "Contains" && count == 1;
            translated = TranslatePublicClrContractMethod(code, method, runtimeType,
                supported, "managed-dictionary-method-unavailable");
            return true;
        }

        private CilInstruction TranslateManagedPairConstructor(CilOpCode code, IMethodDescriptor method,
            TypeSignature key, TypeSignature value)
        {
            var signature = method.Signature;
            if (!InteropDelegateContracts.HasStandardConvention(signature))
                return FailInstruction("managed-dictionary-pair-constructor", "The pair constructor signature is unavailable.");
            var substitution = GenericContext.FromMethod(method);
            if (code.Code is not (CilCode.Newobj or CilCode.Call) || method is MethodSpecification ||
                signature is not { HasThis: true, GenericParameterCount: 0 } ||
                signature.ReturnType.ElementType != ElementType.Void || signature.ParameterTypes.Count != 2 ||
                !SignatureComparer.Default.Equals(signature.ParameterTypes[0].InstantiateGenericTypes(substitution), key) ||
                !SignatureComparer.Default.Equals(signature.ParameterTypes[1].InstantiateGenericTypes(substitution), value) ||
                code.Code == CilCode.Call && (!TryGetStackValue(currentInstruction, 2, out var receiver) ||
                    receiver.Type is not ByReferenceTypeSignature reference ||
                    !SignatureComparer.Default.Equals(reference.BaseType, method.DeclaringType.ToTypeSignature())))
                return FailInstruction("managed-dictionary-pair-constructor", "A pair requires its exact key/value construction contract.");
            var keyType = resolver.Resolve(key);
            var valueType = resolver.Resolve(value);
            if (keyType == null || valueType == null) return FailInstruction("managed-dictionary-pair-storage", "The pair storage is unavailable.");
            if (InteropManagedDictionaryLowering.IsStringKey(key))
                return TranslatePublicClrContractMethod(code, method, typeof(KeyValuePair<,>), true,
                    "managed-dictionary-pair-constructor");

            var temporary = AddArrayLocal(valueType);
            var first = body.Add(CilOpCodes.Stloc, temporary);
            var copy = (IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(typeof(InteropNativeReferenceIdentity)
                .GetMethod(nameof(InteropNativeReferenceIdentity.CopyOwned)));
            body.Add(CilOpCodes.Call, copy.MakeGenericInstanceMethod(keyType));
            body.Add(CilOpCodes.Ldloc, temporary);
            if (TranslatePublicClrContractMethod(code, method, typeof(KeyValuePair<,>), true,
                "managed-dictionary-pair-constructor") == null) return null;
            return first;
        }

        private CilInstruction TranslateManagedDictionaryConstructor(CilOpCode code, IMethodDescriptor method, TypeSignature key)
        {
            var signature = method.Signature;
            if (code.Code != CilCode.Newobj || method is MethodSpecification ||
                !InteropDelegateContracts.HasStandardConvention(signature) ||
                signature is not { HasThis: true, GenericParameterCount: 0 } ||
                signature.ReturnType.ElementType != ElementType.Void || signature.ParameterTypes.Count > 1 ||
                signature.ParameterTypes.Count == 1 && signature.ParameterTypes[0].ElementType != ElementType.I4)
                return FailInstruction("managed-dictionary-constructor", "Only default-comparer empty and capacity constructors are supported.");

            var owner = resolver.Resolve(method.DeclaringType.ToTypeSignature());
            var keyType = resolver.Resolve(key);
            if (InteropManagedDictionaryLowering.IsStringKey(key))
            {
                if (owner == null || keyType?.ElementType != ElementType.String)
                    return FailInstruction("managed-dictionary-string-storage", "The dictionary requires CLR string storage.");
                var stringConstructor = imports.Module.DefaultImporter.ImportMethod(typeof(InteropNativeKeyDictionary<,>)
                    .GetConstructor(new[] { typeof(int) }));
                var stringTarget = new MemberReference(owner.ToTypeDefOrRef(), stringConstructor.Name, stringConstructor.Signature);
                var firstString = signature.ParameterTypes.Count == 0 ? body.Add(CilOpCodes.Ldc_I4_0) : null;
                var createString = body.Add(CilOpCodes.Newobj, imports.Module.DefaultImporter.ImportMethod(stringTarget));
                return firstString ?? createString;
            }
            var nativeComparer = context.TryGetAssemblyByName("mscorlib")?.TryGetTypeByName("System.Collections.Generic.EqualityComparer`1");
            if (owner == null || keyType == null || !IsNativeArrayReference(keyType) ||
                nativeComparer?.OriginalType is not { IsValueType: false } original || original.GenericParameters.Count != 1)
                return RejectComparer();

            var comparerType = new GenericInstanceTypeSignature(nativeComparer.NewType, false, keyType);
            var corlib = imports.Module.CorLibTypeFactory;
            var getter = BindComparerMethod("get_Default", false, comparerType);
            var equals = BindComparerMethod("Equals", true, corlib.Boolean, keyType, keyType);
            var hash = BindComparerMethod("GetHashCode", true, corlib.Int32, keyType);
            if (getter == null || equals == null || hash == null) return RejectComparer();

            var equalsDelegate = new GenericInstanceTypeSignature(imports.Module.DefaultImporter.ImportType(typeof(Func<,,>)),
                false, keyType, keyType, corlib.Boolean);
            var hashDelegate = new GenericInstanceTypeSignature(imports.Module.DefaultImporter.ImportType(typeof(Func<,>)),
                false, keyType, corlib.Int32);
            var constructor = imports.Module.DefaultImporter.ImportMethod(typeof(InteropNativeKeyDictionary<,>)
                .GetConstructors().Single(candidate => candidate.GetParameters().Length == 4));
            var target = new MemberReference(owner.ToTypeDefOrRef(), constructor.Name, constructor.Signature);
            var comparer = AddArrayLocal(comparerType);

            // Bind the generated wrappers, whose bodies perform native virtual dispatch and exception translation.
            CilInstruction first;
            if (signature.ParameterTypes.Count == 0) first = body.Add(CilOpCodes.Ldc_I4_0);
            else
            {
                // Reject a negative capacity before initializing the native comparer.
                var validCapacity = new CilInstructionLabel();
                first = body.Add(CilOpCodes.Dup);
                body.Add(CilOpCodes.Ldc_I4_0);
                body.Add(CilOpCodes.Bge, validCapacity);
                body.Add(CilOpCodes.Pop);
                body.Add(CilOpCodes.Ldstr, "capacity");
                body.Add(CilOpCodes.Newobj, imports.Module.DefaultImporter.ImportMethod(
                    typeof(ArgumentOutOfRangeException).GetConstructor(new[] { typeof(string) })));
                body.Add(CilOpCodes.Throw);
                validCapacity.Instruction = body.Add(CilOpCodes.Nop);
            }
            body.Add(CilOpCodes.Call, getter);
            body.Add(CilOpCodes.Stloc, comparer);
            body.Add(CilOpCodes.Ldloc, comparer);
            EmitDelegate(equalsDelegate, equals);
            EmitDelegate(hashDelegate, hash);
            body.Add(CilOpCodes.Newobj, imports.Module.DefaultImporter.ImportMethod(target));
            return first;

            IMethodDescriptor BindComparerMethod(string name, bool instance, TypeSignature result, params TypeSignature[] parameters)
            {
                IMethodDescriptor selected = null;
                foreach (var candidate in nativeComparer.Methods)
                {
                    var source = candidate.OriginalMethod;
                    if (source.Name != name || source.Signature?.HasThis != instance || source.GenericParameters.Count != 0 ||
                        !InteropDelegateContracts.HasStandardConvention(source.Signature) ||
                        instance && !source.IsVirtual || candidate.NonGenericMethodInfoPointerField == null ||
                        candidate.NewMethod.CilMethodBody?.Instructions.Count is not > 0) continue;
                    var reference = new MemberReference(comparerType.ToTypeDefOrRef(), candidate.NewMethod.Name,
                        candidate.NewMethod.Signature);
                    if (!MatchesSubstitutedCallSignature(reference, result, parameters)) continue;
                    if (selected != null) return null;
                    selected = imports.Module.DefaultImporter.ImportMethod(reference);
                }
                return selected;
            }

            void EmitDelegate(TypeSignature delegateType, IMethodDescriptor targetMethod)
            {
                body.Add(CilOpCodes.Ldloc, comparer);
                body.Add(CilOpCodes.Ldftn, targetMethod);
                body.Add(CilOpCodes.Newobj, new MemberReference(delegateType.ToTypeDefOrRef(), ".ctor",
                    MethodSignature.CreateInstance(corlib.Void, corlib.Object, corlib.IntPtr)));
            }

            CilInstruction RejectComparer() => FailInstruction("managed-dictionary-native-comparer",
                "The exact native default-comparer wrappers are unavailable. CLR wrapper or pointer equality is not a substitute.");
        }
    }
}
