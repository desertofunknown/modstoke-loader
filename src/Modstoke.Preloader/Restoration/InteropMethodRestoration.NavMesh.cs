// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodRestoration
    {
        private const string NavMeshModule = "UnityEngine.AIModule";
        private const string NavMeshType = "UnityEngine.AI.NavMesh";
        private const string NavMeshTriangulationType = "UnityEngine.AI.NavMeshTriangulation";

        // True consumes this family even when its contract is rejected: never fall through to an ICall stub.
        private static bool TryRestoreNavMeshTriangulation(MethodDefinition original, InteropTypeContext owner,
            InteropRuntimeReferences imports, InteropUnstripTypeResolver resolver, InteropRestorationEntry entry)
        {
            if (original.DeclaringModule.Assembly.Name != NavMeshModule ||
                original.DeclaringType.FullName != NavMeshType ||
                (string)original.Name is not ("CalculateTriangulation" or "Triangulate" or "CalculateTriangulation_Injected"))
                return false;

            entry.Set("signature-rejected", "navmesh-triangulation-contract",
                "Triangulation requires the matched source family, native NavMesh and Vector3 identities, and exact CLR-only array fields.");
            var sourceOwner = original.DeclaringType;
            if (original.DeclaringModule.Mvid != new Guid("67185cb2-d65c-4e42-8ab9-781bfee20f59") ||
                sourceOwner.IsValueType || sourceOwner.IsInterface || !sourceOwner.IsAbstract || !sourceOwner.IsSealed ||
                sourceOwner.GenericParameters.Count != 0 || sourceOwner.DeclaringType != null ||
                owner.OriginalType == null || owner.ClassPointerFieldRef == null ||
                owner.OriginalType.FullName != NavMeshType || owner.OriginalType.IsValueType ||
                owner.OriginalType.IsInterface || owner.OriginalType.GenericParameters.Count != 0 ||
                owner.OriginalType.DeclaringModule.Assembly.Name != NavMeshModule ||
                owner.NewType.FullName != NavMeshType || owner.NewType.IsValueType || owner.NewType.IsInterface ||
                owner.NewType.GenericParameters.Count != 0 || owner.NewType.DeclaringModule != imports.Module ||
                imports.Module.Assembly.Name != NavMeshModule ||
                resolver.ResolveRaw(sourceOwner.ToTypeSignature())?.Resolve() != owner.NewType)
                return true;

            var sourceTypes = original.DeclaringModule.TopLevelTypes.Where(type =>
                type.FullName == NavMeshTriangulationType).ToArray();
            if (sourceTypes.Length != 1 || !HasNavMeshTriangulationFields(sourceTypes[0], true))
                return true;
            var sourceTriangulation = sourceTypes[0];
            var sourceFields = sourceTriangulation.Fields;
            if (sourceFields[0].Signature.FieldType is not SzArrayTypeSignature
                    { BaseType: TypeDefOrRefSignature { IsValueType: true } sourceVector } ||
                sourceVector.FullName != "UnityEngine.Vector3" ||
                InteropRestoredTypeSupport.GetAssemblyName(sourceVector) != "UnityEngine.CoreModule" ||
                sourceVector.Resolve() is not { IsValueType: true, IsEnum: false } sourceVectorType ||
                sourceVectorType.FullName != "UnityEngine.Vector3" ||
                sourceVectorType.DeclaringModule.Assembly.Name != "UnityEngine.CoreModule" ||
                sourceVectorType.GenericParameters.Count != 0 ||
                sourceFields[1].Signature.FieldType is not SzArrayTypeSignature
                    { BaseType: CorLibTypeSignature { ElementType: ElementType.I4 } } ||
                sourceFields[2].Signature.FieldType is not SzArrayTypeSignature
                    { BaseType: CorLibTypeSignature { ElementType: ElementType.I4 } })
                return true;

            // Validate all three source declarations before publishing any member of the family.
            if (!HasNavMeshTriangulationMethod(sourceOwner, "CalculateTriangulation", 0x06000137,
                    sourceTriangulation.ToTypeSignature()) ||
                !HasNavMeshTriangulationMethod(sourceOwner, "Triangulate", 0x06000138,
                    original.DeclaringModule.CorLibTypeFactory.Void,
                    sourceFields[0].Signature.FieldType.MakeByReferenceType(),
                    sourceFields[1].Signature.FieldType.MakeByReferenceType()) ||
                !HasNavMeshTriangulationMethod(sourceOwner, "CalculateTriangulation_Injected", 0x06000163,
                    original.DeclaringModule.CorLibTypeFactory.Void,
                    sourceTriangulation.ToTypeSignature().MakeByReferenceType()))
                return true;

            var context = owner.AssemblyContext.GlobalContext;
            var triangulation = owner.AssemblyContext.TryGetTypeByName(NavMeshTriangulationType);
            var vector = context.TryGetTypeByName("UnityEngine.CoreModule", "UnityEngine.Vector3");
            if (triangulation == null || triangulation.OriginalType != null || triangulation.ClassPointerFieldRef != null ||
                triangulation.NewType.DeclaringModule != imports.Module ||
                !HasNavMeshTriangulationFields(triangulation.NewType, false) ||
                resolver.ResolveRaw(sourceTriangulation.ToTypeSignature())?.Resolve() != triangulation.NewType ||
                vector?.OriginalType == null || vector.ClassPointerFieldRef == null ||
                vector.ComputedTypeSpecifics != InteropTypeContext.TypeSpecifics.BlittableStruct ||
                vector.OriginalType.FullName != "UnityEngine.Vector3" || !vector.OriginalType.IsValueType ||
                vector.OriginalType.IsEnum || vector.OriginalType.GenericParameters.Count != 0 ||
                vector.OriginalType.DeclaringModule.Assembly.Name != "UnityEngine.CoreModule" ||
                vector.NewType.FullName != "UnityEngine.Vector3" || !vector.NewType.IsValueType || vector.NewType.IsEnum ||
                vector.NewType.GenericParameters.Count != 0 ||
                vector.NewType.DeclaringModule.Assembly.Name != "UnityEngine.CoreModule" ||
                resolver.ResolveRaw(sourceVector)?.Resolve() != vector.NewType)
                return true;

            var vectorType = resolver.Resolve(sourceVector);
            var structType = resolver.Resolve(sourceTriangulation.ToTypeSignature());
            var arrays = new TypeSignature[]
            {
                new GenericInstanceTypeSignature(imports.Il2CppStructArray.ToTypeDefOrRef(), false, vectorType),
                new GenericInstanceTypeSignature(imports.Il2CppStructArray.ToTypeDefOrRef(), false, imports.Module.CorLibTypeFactory.Int32),
                new GenericInstanceTypeSignature(imports.Il2CppStructArray.ToTypeDefOrRef(), false, imports.Module.CorLibTypeFactory.Int32)
            };
            var fields = triangulation.NewType.Fields;
            for (var index = 0; index < arrays.Length; index++)
            {
                if (!SignatureComparer.Default.Equals(resolver.Resolve(sourceFields[index].Signature.FieldType), arrays[index]) ||
                    !SignatureComparer.Default.Equals(fields[index].Signature.FieldType, arrays[index]))
                    return true;
            }

            var returnType = resolver.Resolve(original.Signature.ReturnType);
            var parameters = original.Signature.ParameterTypes.Select(parameter => resolver.Resolve(parameter)).ToArray();
            if (returnType == null || parameters.Any(parameter => parameter == null))
                return true;
            var signature = MethodSignature.CreateStatic(returnType, parameters);
            if (owner.NewType.Methods.Any(method => method.Name == original.Name &&
                SignatureComparer.Default.Equals(method.Signature, signature)))
                return true;

            var target = new MethodDefinition(original.Name,
                (original.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Public,
                MethodSignature.CreateStatic(returnType))
            {
                ImplAttributes = MethodImplAttributes.IL | MethodImplAttributes.Managed,
                CilMethodBody = new CilMethodBody { InitializeLocals = true }
            };
            for (var index = 0; index < parameters.Length; index++)
                InteropMethodDeclarations.AddParameter(target, parameters[index], original.Parameters[index].Name,
                    original.Parameters[index].Definition.Attributes);

            var locals = arrays.Select(array => new CilLocalVariable(array)).ToArray();
            var body = target.CilMethodBody.Instructions;
            foreach (var local in locals)
            {
                target.CilMethodBody.LocalVariables.Add(local);
                body.Add(CilOpCodes.Ldloca, local);
            }
            var helper = ((IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(
                typeof(InteropNavigationSupport).GetMethod(nameof(InteropNavigationSupport.CalculateTriangulation))))
                .MakeGenericInstanceMethod(vectorType);
            body.Add(CilOpCodes.Call, helper);

            // Only publish caller-visible outputs after the protected helper has returned all three arrays.
            if (original.Name == "Triangulate")
            {
                for (var index = 0; index < parameters.Length; index++)
                {
                    body.Add(CilOpCodes.Ldarg, target.Parameters[index]);
                    body.Add(CilOpCodes.Ldloc, locals[index]);
                    body.Add(CilOpCodes.Stind_Ref);
                }
            }
            else
            {
                var result = new CilLocalVariable(structType);
                target.CilMethodBody.LocalVariables.Add(result);
                for (var index = 0; index < fields.Count; index++)
                {
                    body.Add(CilOpCodes.Ldloca, result);
                    body.Add(CilOpCodes.Ldloc, locals[index]);
                    body.Add(CilOpCodes.Stfld, fields[index]);
                }
                if (original.Name == "CalculateTriangulation_Injected")
                    body.Add(CilOpCodes.Ldarg_0);
                body.Add(CilOpCodes.Ldloc, result);
                if (original.Name == "CalculateTriangulation_Injected")
                    body.Add(CilOpCodes.Stobj, structType.ToTypeDefOrRef());
            }
            body.Add(CilOpCodes.Ret);
            owner.NewType.Methods.Add(target);
            owner.AddRestoredManagedMethod(original, target);
            entry.Set("native-triangulation-adapter", null,
                "Protected native array outputs populate CLR-only NavMeshTriangulation fields; no native struct identity or layout copy.");
            return true;
        }

        private static bool HasNavMeshTriangulationFields(TypeDefinition type, bool source)
        {
            if (type.FullName != NavMeshTriangulationType || !type.IsValueType || type.IsEnum || !type.IsPublic ||
                !type.IsSealed || type.GenericParameters.Count != 0 || type.DeclaringType != null ||
                type.BaseType?.FullName != "System.ValueType" || type.Fields.Count != 3 ||
                (source ? !type.IsSequentialLayout : !type.IsAutoLayout) || type.ClassLayout != null)
                return false;
            string[] names = ["vertices", "indices", "areas"];
            for (var index = 0; index < names.Length; index++)
            {
                var field = type.Fields[index];
                if (field.Name != names[index] || field.Attributes != FieldAttributes.Public ||
                    field.Signature == null || field.FieldOffset != null || field.HasFieldMarshal ||
                    field.MarshalDescriptor != null || field.FieldRva != null ||
                    source && field.MetadataToken.ToInt32() != 0x0400002D + index)
                    return false;
            }
            return true;
        }

        private static bool HasNavMeshTriangulationMethod(TypeDefinition owner, string name, int token,
            TypeSignature returnType, params TypeSignature[] parameters)
        {
            var methods = owner.Methods.Where(method => method.Name == name).ToArray();
            if (methods.Length != 1) return false;
            var method = methods[0];
            var injected = name == "CalculateTriangulation_Injected";
            if (method.MetadataToken.ToInt32() != token ||
                method.Attributes != ((injected ? MethodAttributes.Private : MethodAttributes.Public) |
                    MethodAttributes.Static | MethodAttributes.HideBySig) ||
                method.ImplAttributes != (injected ? MethodImplAttributes.InternalCall : MethodImplAttributes.IL) ||
                method.HasMethodBody == injected || method.GenericParameters.Count != 0 ||
                !InteropDelegateContracts.HasStandardConvention(method.Signature) || method.Signature.HasThis ||
                method.Signature.GenericParameterCount != 0 ||
                !SignatureComparer.Default.Equals(method.Signature, MethodSignature.CreateStatic(returnType, parameters)) ||
                method.Parameters.Count != parameters.Length)
                return false;
            return method.Parameters.All(parameter => parameter.Definition?.Attributes == ParameterAttributes.Out);
        }
    }
}
