// SPDX-License-Identifier: GPL-3.0-or-later
// Params overloads adapted from Il2CppInterop RewriteGlobalContext and helpers (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static partial class InteropMethodDeclarations
    {
        internal static MethodDefinition CreateParamsOverload(MethodDefinition original, MethodDefinition target,
            InteropRuntimeReferences imports, Func<TypeSignature, TypeSignature> resolve)
        {
            if (target.Name == "Invoke")
                return null;

            var paramsParameters = original.Parameters.Where(parameter => IsParamsArray(parameter) &&
                resolve(((SzArrayTypeSignature)parameter.ParameterType).BaseType)
                    is not null and not GenericParameterSignature).ToArray();
            if (paramsParameters.Length == 0)
                return null;

            var signature = target.IsStatic
                ? MethodSignature.CreateStatic(target.Signature.ReturnType, target.Signature.GenericParameterCount)
                : MethodSignature.CreateInstance(target.Signature.ReturnType, target.Signature.GenericParameterCount);
            var overload = new MethodDefinition(target.Name, target.Attributes, signature);
            foreach (var parameter in original.GenericParameters)
            {
                var copy = new GenericParameter(parameter.Name.MakeValidInSource(), parameter.Attributes);
                foreach (var constraint in parameter.Constraints)
                {
                    var type = constraint.Constraint == null
                        ? null
                        : resolve(constraint.Constraint.ToTypeSignature())?.ToTypeDefOrRef();
                    copy.Constraints.Add(new GenericParameterConstraint(type));
                }
                overload.GenericParameters.Add(copy);
            }

            foreach (var parameter in original.Parameters)
            {
                var isParams = paramsParameters.Contains(parameter);
                var type = isParams
                    ? resolve(((SzArrayTypeSignature)parameter.ParameterType).BaseType)?.MakeSzArrayType()
                    : resolve(parameter.ParameterType);
                if (type == null)
                    throw new InvalidOperationException("Could not resolve parameter type " + parameter.ParameterType.FullName);

                var copy = AddParameter(overload, type, parameter.Name, parameter.Definition?.Attributes ?? default);
                if (isParams)
                {
                    var module = target.DeclaringModule;
                    var attributeType = module.DefaultImporter.ImportTypeSignature(typeof(ParamArrayAttribute)).ToTypeDefOrRef();
                    var constructor = new MemberReference(attributeType, ".ctor",
                        MethodSignature.CreateInstance(module.CorLibTypeFactory.Void));
                    copy.Definition.CustomAttributes.Add(new CustomAttribute(constructor));
                }
            }

            overload.CilMethodBody = new CilMethodBody();
            var body = overload.CilMethodBody.Instructions;
            if (!target.IsStatic)
                body.Add(CilOpCodes.Ldarg_0);

            for (var i = 0; i < overload.Parameters.Count; i++)
            {
                body.Add(CilOpCodes.Ldarg, overload.Parameters[i]);
                var parameter = original.Parameters[i];
                if (!paramsParameters.Contains(parameter))
                    continue;

                var element = ((SzArrayTypeSignature)parameter.ParameterType).BaseType;
                IMethodDescriptor constructor;
                if (element.FullName == "System.String")
                    constructor = imports.Il2CppStringArrayctor.Value;
                else
                {
                    var convertedElement = resolve(element);
                    constructor = imports.Module.DefaultImporter.ImportMethod(InteropTypeReferences.IsValueType(convertedElement)
                        ? imports.Il2CppStructArrayctor.Get(convertedElement)
                        : imports.Il2CppRefrenceArrayctor.Get(convertedElement));
                }
                body.Add(CilOpCodes.Newobj, constructor);
            }

            IMethodDescriptor callTarget = target;
            if (overload.GenericParameters.Count != 0)
            {
                // Forward the method's arguments (!!n), not its declaring type's arguments (!n).
                var arguments = new TypeSignature[overload.GenericParameters.Count];
                for (var i = 0; i < arguments.Length; i++)
                    arguments[i] = new GenericParameterSignature(GenericParameterType.Method, i);
                callTarget = target.MakeGenericInstanceMethod(arguments);
            }
            body.Add(CilOpCodes.Call, callTarget);
            body.Add(CilOpCodes.Ret);
            return overload;
        }
    }
}
