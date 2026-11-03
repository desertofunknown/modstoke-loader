// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Il2CppInterop.Runtime.Injection;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodRestoration
    {
        private static bool IsStyleEnumStart(MethodDefinition source, InteropTypeContext owner) =>
            source.DeclaringModule.Assembly.Name == InteropNativeStyleEnumType.AssemblyName &&
            source.DeclaringType.FullName == InteropNativeStyleEnumType.OwnerName && source.Name == "StartAnimationEnum" &&
            InteropNativeStyleEnumType.IsPrepared(owner.AssemblyContext.TryGetTypeByName(InteropNativeStyleEnumType.TypeName)?.OriginalType);

        private static bool FillStyleEnumStart(MethodDefinition source, MethodDefinition target, InteropTypeContext owner,
            InteropRuntimeReferences imports, out InteropTranslationFailure failure)
        {
            failure = new InteropTranslationFailure("style-enum-storage-contract",
                "Enum animation requires the exact lazy field access, native enum constructor and native transition method.",
                null, null, null);
            if (source.DeclaringModule.Mvid != new Guid("b6e504b1-20f0-43e3-9923-7045b32ad8bb") ||
                source.MetadataToken.ToInt32() != 100670186 || source.IsStatic || source.GenericParameters.Count != 0 ||
                source.Signature.ParameterTypes.Count != 7 || source.Signature.ReturnType.ElementType != ElementType.Boolean ||
                source.CilMethodBody is not { } sourceBody || sourceBody.ExceptionHandlers.Count != 0 ||
                sourceBody.LocalVariables.Count != 1 || sourceBody.LocalVariables[0].VariableType.ElementType != ElementType.Boolean ||
                owner.OriginalType == null || owner.ClassPointerFieldRef == null ||
                owner.OriginalType.FullName != InteropNativeStyleEnumType.OwnerName ||
                owner.OriginalType.Fields.Any(field => field.Name == "m_Enums")) return false;

            var code = sourceBody.Instructions.Where(instruction => instruction.OpCode.Code != CilCode.Nop).ToArray();
            if (code.Length != 17 || Enumerable.Range(0, 8).Any(index => ReferenceConstructorArgumentIndex(code[index]) != index) ||
                ReferenceConstructorArgumentIndex(code[8]) != 0 || ReferenceConstructorArgumentIndex(code[9]) != 0 ||
                code[10].OpCode.Code != CilCode.Ldflda || code[10].Operand is not IFieldDescriptor fieldReference ||
                fieldReference.Resolve() is not { } field || field.DeclaringType != source.DeclaringType ||
                field.MetadataToken.ToInt32() != 67111722 || field.Name != "m_Enums" || field.Attributes != FieldAttributes.Private ||
                field.Signature.FieldType.FullName != InteropNativeStyleEnumType.TypeName ||
                code[11].OpCode.Code != CilCode.Call || code[11].Operand is not MethodSpecification getOrCreate ||
                getOrCreate.Resolve()?.MetadataToken.ToInt32() != 100670180 ||
                getOrCreate.DeclaringType?.Resolve() != source.DeclaringType || getOrCreate.Name != "GetOrCreate" ||
                getOrCreate.Signature.TypeArguments.Count != 1 ||
                !SignatureComparer.Default.Equals(getOrCreate.Signature.TypeArguments[0], field.Signature.FieldType) ||
                code[12].OpCode.Code != CilCode.Call || code[12].Operand is not MethodSpecification transition ||
                transition.Resolve() is not { } transitionSource || transitionSource.MetadataToken.ToInt32() != 100670181 ||
                transitionSource.DeclaringType != source.DeclaringType || transition.Name != "StartTransition" ||
                transition.Signature.TypeArguments.Count != 1 || transition.Signature.TypeArguments[0].ElementType != ElementType.I4 ||
                code[13].OpCode.Code != CilCode.Stloc_0 || code[14].OpCode.Code != CilCode.Br_S ||
                code[14].Operand is not ICilLabel finish || finish.Offset != code[15].Offset ||
                code[15].OpCode.Code != CilCode.Ldloc_0 || code[16].OpCode.Code != CilCode.Ret) return false;

            var context = owner.AssemblyContext.GlobalContext;
            var resolver = new InteropUnstripTypeResolver(context, imports);
            var enumOwner = owner.AssemblyContext.TryGetTypeByName(InteropNativeStyleEnumType.TypeName);
            var enumSource = field.Signature.FieldType.Resolve();
            var constructorSource = enumSource?.Methods.SingleOrDefault(method => method.MetadataToken.ToInt32() == 100670307);
            var constructor = constructorSource == null ? null : enumOwner?.TryGetRestoredReferenceConstructor(constructorSource);
            var mappedTransition = owner.TryGetMethodByUnityAssemblyMethod(transitionSource);
            var nativeTransition = mappedTransition?.NewMethod;
            if (enumOwner == null || !InteropNativeStyleEnumType.IsPrepared(enumOwner.OriginalType) ||
                resolver.Resolve(field.Signature.FieldType)?.Resolve() != enumOwner.NewType ||
                constructor == null || constructor.DeclaringType != enumOwner.NewType || !constructor.IsPublic ||
                constructor.Signature.ParameterTypes.Count != 0 || constructor.IsStatic ||
                nativeTransition == null || nativeTransition.DeclaringType != owner.NewType || nativeTransition.IsStatic ||
                nativeTransition.IsAbstract || nativeTransition.GenericParameters.Count != 1 ||
                mappedTransition.NonGenericMethodInfoPointerField == null ||
                !InteropDelegateContracts.HasStandardConvention(nativeTransition.Signature)) return false;

            var intType = imports.Module.CorLibTypeFactory.Int32;
            var closedTransition = new MemberReference(owner.NewType, nativeTransition.Name, nativeTransition.Signature)
                .MakeGenericInstanceMethod(intType);
            var transitionSignature = nativeTransition.Signature.InstantiateGenericTypes(GenericContext.FromMethod(closedTransition));
            var expectedArguments = source.Signature.ParameterTypes.Select(parameter => resolver.Resolve(parameter)).ToArray();
            var values = resolver.Resolve(transitionSource.Signature.ParameterTypes[^1]
                .InstantiateGenericTypes(GenericContext.FromMethod(transition)));
            var transitionArguments = expectedArguments.Append(values).ToArray();
            if (expectedArguments.Any(argument => argument == null) || values == null ||
                !transitionSignature.HasThis || transitionSignature.ReturnType.ElementType != ElementType.Boolean ||
                transitionSignature.ParameterTypes.Count != transitionArguments.Length ||
                transitionSignature.ParameterTypes.Where((parameter, index) =>
                    !SignatureComparer.Default.Equals(parameter, transitionArguments[index])).Any() ||
                !SignatureComparer.Default.Equals(target.Signature,
                    MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Boolean, expectedArguments))) return false;

            var importer = imports.Module.DefaultImporter;
            var get = importer.ImportMethod(typeof(NativeStyleEnumState).GetMethod(nameof(NativeStyleEnumState.Get)));
            var set = importer.ImportMethod(typeof(NativeStyleEnumState).GetMethod(nameof(NativeStyleEnumState.Set)));
            var keepAlive = importer.ImportMethod(typeof(GC).GetMethod(nameof(GC.KeepAlive), new[] { typeof(object) }));
            target.CilMethodBody = new CilMethodBody { InitializeLocals = true };
            var body = target.CilMethodBody.Instructions;
            var cached = new CilLocalVariable(enumOwner.NewType.ToTypeSignature());
            var result = new CilLocalVariable(imports.Module.CorLibTypeFactory.Boolean);
            target.CilMethodBody.LocalVariables.Add(cached);
            target.CilMethodBody.LocalVariables.Add(result);
            var start = body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Call, get);
            InteropEventDebuggerFields.EmitOwnedCast(target.CilMethodBody, imports, get.Signature.ReturnType, cached.VariableType);
            body.Add(CilOpCodes.Stloc, cached);
            body.Add(CilOpCodes.Ldloc, cached);
            var callStart = new CilInstruction(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Brtrue, callStart.CreateLabel());
            // The field is assigned only after construction succeeds, exactly like GetOrCreate(ref m_Enums).
            body.Add(CilOpCodes.Newobj, importer.ImportMethod(constructor));
            body.Add(CilOpCodes.Stloc, cached);
            body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Ldloc, cached);
            body.Add(CilOpCodes.Call, set);
            body.Add(callStart);
            foreach (var parameter in target.Parameters) body.Add(CilOpCodes.Ldarg, parameter);
            body.Add(CilOpCodes.Ldloc, cached);
            body.Add(CilOpCodes.Call, importer.ImportMethod(closedTransition));
            body.Add(CilOpCodes.Stloc, result);
            var returnValue = new CilInstruction(CilOpCodes.Ldloc, result);
            body.Add(CilOpCodes.Leave, returnValue.CreateLabel());
            var cleanup = body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Call, keepAlive);
            foreach (var parameter in target.Parameters.Where(parameter => !parameter.ParameterType.IsValueType))
            {
                body.Add(CilOpCodes.Ldarg, parameter);
                body.Add(CilOpCodes.Call, keepAlive);
            }
            body.Add(CilOpCodes.Ldloc, cached);
            body.Add(CilOpCodes.Call, keepAlive);
            body.Add(CilOpCodes.Endfinally);
            body.Add(returnValue);
            body.Add(CilOpCodes.Ret);
            target.CilMethodBody.ExceptionHandlers.Add(new CilExceptionHandler
            {
                HandlerType = CilExceptionHandlerType.Finally,
                TryStart = start.CreateLabel(), TryEnd = cleanup.CreateLabel(),
                HandlerStart = cleanup.CreateLabel(), HandlerEnd = returnValue.CreateLabel()
            });
            failure = null;
            return true;
        }
    }
}
