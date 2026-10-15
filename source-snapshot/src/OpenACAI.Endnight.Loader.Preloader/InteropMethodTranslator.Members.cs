// SPDX-License-Identifier: GPL-3.0-or-later
// Restored IL translation adapted from Il2CppInterop UnstripTranslator (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private CilInstruction TranslateField(CilOpCode code, IFieldDescriptor field)
        {
            var declarer = resolver.ResolveRaw(field.DeclaringType.ToTypeSignature(), field.Signature?.HasThis ?? true);
            var definition = declarer?.Resolve();
            if (definition == null)
                return null;

            var type = context.GetContextForNewType(definition);
            var propertyName = type.Fields.SingleOrDefault(candidate => candidate.OriginalField.Name == field.Name)?.UnmangledName;
            var rewrittenField = definition.Fields.SingleOrDefault(candidate => candidate.Name == field.Name)
                ?? definition.Fields.SingleOrDefault(candidate => candidate.Name == propertyName);
            if (rewrittenField != null)
                return body.Add(code, imports.Module.DefaultImporter.ImportField(rewrittenField));
            if (propertyName == null)
                return null;

            MethodDefinition accessor;
            if (code.Code is CilCode.Ldfld or CilCode.Ldsfld)
                accessor = definition.Properties.SingleOrDefault(property => property.Name == propertyName)?.GetMethod;
            else if (code.Code is CilCode.Stfld or CilCode.Stsfld)
                accessor = definition.Properties.SingleOrDefault(property => property.Name == propertyName)?.SetMethod;
            else
                return null;

            return accessor == null ? null : body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(accessor));
        }

        private CilInstruction TranslateMethod(CilOpCode code, IMethodDescriptor method)
        {
            var declarer = resolver.Resolve(method.DeclaringType?.ToTypeSignature(), method.Signature?.HasThis ?? true);
            if (declarer == null)
                return null;
            var returnType = resolver.Resolve(method.Signature?.ReturnType);
            if (returnType == null)
                return null;

            var signature = method.Signature.HasThis
                ? MethodSignature.CreateInstance(returnType, method.Signature.GenericParameterCount)
                : MethodSignature.CreateStatic(returnType, method.Signature.GenericParameterCount);
            foreach (var parameter in method.Signature.ParameterTypes)
            {
                var parameterType = resolver.Resolve(parameter);
                if (parameterType == null)
                    return null;
                signature.ParameterTypes.Add(parameterType);
            }

            var reference = new MemberReference(declarer.ToTypeDefOrRef(), method.Name, signature);
            IMethodDescriptor rewrittenMethod = reference;
            if (method is MethodSpecification genericMethod)
            {
                if (genericMethod.Signature == null)
                    return null;
                var arguments = new TypeSignature[genericMethod.Signature.TypeArguments.Count];
                for (var index = 0; index < arguments.Length; index++)
                {
                    arguments[index] = resolver.Resolve(genericMethod.Signature.TypeArguments[index]);
                    if (arguments[index] == null)
                        return null;
                }
                rewrittenMethod = reference.MakeGenericInstanceMethod(arguments);
            }
            return body.Add(code, imports.Module.DefaultImporter.ImportMethod(rewrittenMethod));
        }

        private CilInstruction TranslateType(CilOpCode code, ITypeDefOrRef type)
        {
            var rewrittenType = resolver.Resolve(type.ToTypeSignature());
            if (rewrittenType == null)
                return null;

            if ((code.Code == CilCode.Castclass && !rewrittenType.IsValueType) ||
                (code.Code == CilCode.Unbox_Any && rewrittenType is GenericParameterSignature))
                return body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(
                    imports.Il2CppObjectBase_Cast.Value.MakeGenericInstanceMethod(rewrittenType)));
            if (code.Code == CilCode.Isinst && !rewrittenType.IsValueType)
                return body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(
                    imports.Il2CppObjectBase_TryCast.Value.MakeGenericInstanceMethod(rewrittenType)));

            switch (code.Code)
            {
                case CilCode.Box when rewrittenType is CorLibTypeSignature && rewrittenType.IsValueType:
                {
                    // Restored signatures use Il2CppSystem.Object, not a boxed CLR primitive.
                    var objectType = context.GetAssemblyByName("mscorlib").GetTypeByName("System.Object").NewType;
                    var conversion = objectType.Methods.SingleOrDefault(method =>
                        method.Name == "op_Implicit" && method.IsStatic &&
                        method.Signature.ParameterTypes.Count == 1 &&
                        SignatureComparer.Default.Equals(method.Signature.ParameterTypes[0], rewrittenType) &&
                        SignatureComparer.Default.Equals(method.Signature.ReturnType, objectType.ToTypeSignature()));
                    return conversion == null ? null : body.Add(CilOpCodes.Call,
                        imports.Module.DefaultImporter.ImportMethod(conversion));
                }
                case CilCode.Newarr:
                {
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
                    return null;
                case CilCode.Ldelem:
                    return CallArrayAccessor(false, rewrittenType);
                case CilCode.Stelem:
                    return CallArrayAccessor(true, rewrittenType);
                default:
                    return body.Add(code, rewrittenType.ToTypeDefOrRef());
            }
        }

        private CilInstruction TranslateToken(object operand)
        {
            if (operand is not ITypeDefOrRef type)
                return null;
            var rewrittenType = resolver.Resolve(type.ToTypeSignature());
            if (rewrittenType == null)
                return null;
            return body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(
                imports.Il2CppSystemRuntimeTypeHandleGetRuntimeTypeHandle.Value.MakeGenericInstanceMethod(rewrittenType)));
        }
    }
}
