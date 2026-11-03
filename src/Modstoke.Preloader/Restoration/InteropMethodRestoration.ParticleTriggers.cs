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
        private const string ParticleTriggerModule = "UnityEngine.ParticleSystemModule";
        private const string ParticleTriggerOwner = "UnityEngine.ParticleSystemExtensionsImpl";
        private const string ParticleTriggerSystem = "UnityEngine.ParticleSystem";
        private const string ParticleTriggerData = "UnityEngine.ParticleSystem+ColliderData";

        // True consumes this family even when rejected: never pass CLR-only ColliderData to an ICall stub.
        private static bool TryRestoreParticleTriggers(MethodDefinition source, InteropTypeContext owner,
            InteropRuntimeReferences imports, InteropUnstripTypeResolver resolver, InteropRestorationEntry entry)
        {
            if (source.DeclaringModule.Assembly.Name != ParticleTriggerModule ||
                source.DeclaringType.FullName != ParticleTriggerOwner || source.Name != "GetTriggerParticlesWithData")
                return false;

            entry.Set("signature-rejected", "particle-trigger-contract",
                "Trigger data requires the matched ref declaration, native ParticleSystem, Particle, Component and List identities, and exact CLR array fields.");
            var sourceOwner = source.DeclaringType;
            if (source.DeclaringModule.Mvid != new Guid("52e8710d-a92a-4058-ab10-8e8ad4535ab4") ||
                source.MetadataToken.ToInt32() != 100665086 ||
                source.Attributes != (MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig) ||
                source.ImplAttributes != MethodImplAttributes.InternalCall || source.HasMethodBody ||
                source.GenericParameters.Count != 0 || !InteropDelegateContracts.HasStandardConvention(source.Signature) ||
                source.Signature.HasThis || source.Signature.GenericParameterCount != 0 ||
                source.Signature.ReturnType is not CorLibTypeSignature { ElementType: ElementType.I4 } ||
                source.Parameters.Count != 4 || source.Signature.ParameterTypes.Count != 4 ||
                source.Parameters.Any(parameter => parameter.Definition?.Attributes != (ParameterAttributes)0) ||
                sourceOwner.IsValueType || sourceOwner.IsInterface || sourceOwner.IsAbstract || sourceOwner.IsSealed ||
                sourceOwner.GenericParameters.Count != 0 || sourceOwner.DeclaringType != null ||
                sourceOwner.BaseType?.FullName != "System.Object" ||
                sourceOwner.Methods.Count(method => method.Name == source.Name) != 1 ||
                !HasParticleTriggerNativeReference(owner, ParticleTriggerModule, ParticleTriggerOwner, ParticleTriggerOwner, 0) ||
                owner.NewType.DeclaringModule != imports.Module || imports.Module.Assembly.Name != ParticleTriggerModule ||
                resolver.ResolveRaw(sourceOwner.ToTypeSignature())?.Resolve() != owner.NewType)
                return true;

            if (source.Signature.ParameterTypes[0] is not TypeDefOrRefSignature { IsValueType: false } sourceSystemRef ||
                sourceSystemRef.Resolve() is not { } sourceSystem || sourceSystem.FullName != ParticleTriggerSystem ||
                sourceSystem.DeclaringModule != source.DeclaringModule || sourceSystem.DeclaringType != null ||
                sourceSystem.IsValueType || sourceSystem.IsInterface || sourceSystem.GenericParameters.Count != 0 ||
                source.Signature.ParameterTypes[1] is not CorLibTypeSignature { ElementType: ElementType.I4 } ||
                source.Signature.ParameterTypes[2] is not GenericInstanceTypeSignature sourceList || sourceList.IsValueType ||
                sourceList.GenericType.FullName != "System.Collections.Generic.List`1" || sourceList.TypeArguments.Count != 1 ||
                InteropRestoredTypeSupport.GetAssemblyName(sourceList) is not ("netstandard" or "mscorlib") ||
                sourceList.TypeArguments[0] is not TypeDefOrRefSignature { IsValueType: true } sourceParticleRef ||
                sourceParticleRef.Resolve() is not { } sourceParticle ||
                sourceParticle.FullName != "UnityEngine.ParticleSystem+Particle" || sourceParticle.DeclaringType != sourceSystem ||
                !sourceParticle.IsValueType || sourceParticle.IsEnum || sourceParticle.GenericParameters.Count != 0 ||
                source.Signature.ParameterTypes[3] is not ByReferenceTypeSignature
                    { BaseType: TypeDefOrRefSignature { IsValueType: true } sourceDataRef } ||
                sourceDataRef.Resolve() is not { } sourceData || sourceData.DeclaringType != sourceSystem ||
                !HasParticleTriggerFields(sourceData, true))
                return true;

            var sourceFields = sourceData.Fields;
            if (sourceFields[0].Signature.FieldType is not SzArrayTypeSignature
                    { BaseType: TypeDefOrRefSignature { IsValueType: false } sourceComponentRef } ||
                sourceComponentRef.FullName != "UnityEngine.Component" ||
                InteropRestoredTypeSupport.GetAssemblyName(sourceComponentRef) != "UnityEngine.CoreModule" ||
                sourceComponentRef.Resolve() is not { } sourceComponent ||
                sourceComponent.FullName != "UnityEngine.Component" || sourceComponent.IsValueType || sourceComponent.IsInterface ||
                sourceComponent.DeclaringModule.Assembly.Name != "UnityEngine.CoreModule" ||
                sourceComponent.GenericParameters.Count != 0 || sourceComponent.DeclaringType != null ||
                sourceSystem.BaseType?.Resolve() != sourceComponent ||
                sourceFields[1].Signature.FieldType is not SzArrayTypeSignature
                    { BaseType: CorLibTypeSignature { ElementType: ElementType.I4 } } ||
                sourceFields[2].Signature.FieldType is not SzArrayTypeSignature
                    { BaseType: CorLibTypeSignature { ElementType: ElementType.I4 } })
                return true;

            var context = owner.AssemblyContext.GlobalContext;
            var system = context.TryGetTypeByName(ParticleTriggerModule, ParticleTriggerSystem);
            var particle = context.TryGetTypeByName(ParticleTriggerModule, sourceParticle.FullName);
            var component = context.TryGetTypeByName("UnityEngine.CoreModule", "UnityEngine.Component");
            var list = context.TryGetTypeByName("mscorlib", "System.Collections.Generic.List`1");
            var data = owner.AssemblyContext.TryGetTypeByName(ParticleTriggerData);
            if (!HasParticleTriggerNativeReference(system, ParticleTriggerModule, ParticleTriggerSystem, ParticleTriggerSystem, 0) ||
                system.NewType.DeclaringModule != imports.Module ||
                resolver.ResolveRaw(sourceSystemRef)?.Resolve() != system.NewType ||
                !HasParticleTriggerNativeReference(component, "UnityEngine.CoreModule", "UnityEngine.Component", "UnityEngine.Component", 0) ||
                resolver.ResolveRaw(sourceComponentRef)?.Resolve() != component.NewType ||
                system.OriginalType.BaseType?.Resolve() != component.OriginalType ||
                system.NewType.BaseType?.Resolve() != component.NewType ||
                !HasParticleTriggerNativeReference(list, "mscorlib", "System.Collections.Generic.List`1", "Il2CppSystem.Collections.Generic.List`1", 1) ||
                resolver.ResolveRaw(sourceList.GenericType.ToTypeSignature())?.Resolve() != list.NewType ||
                particle?.OriginalType == null || particle.ClassPointerFieldRef == null ||
                particle.ComputedTypeSpecifics != InteropTypeContext.TypeSpecifics.BlittableStruct ||
                particle.OriginalType.FullName != sourceParticle.FullName || !particle.OriginalType.IsValueType ||
                particle.OriginalType.IsEnum || particle.OriginalType.GenericParameters.Count != 0 ||
                particle.OriginalType.DeclaringType != system.OriginalType ||
                particle.NewType.FullName != sourceParticle.FullName || !particle.NewType.IsValueType ||
                particle.NewType.IsEnum || particle.NewType.GenericParameters.Count != 0 ||
                particle.NewType.DeclaringType != system.NewType ||
                resolver.ResolveRaw(sourceParticleRef)?.Resolve() != particle.NewType ||
                data == null || data.OriginalType != null || data.ClassPointerFieldRef != null ||
                data.NewType.DeclaringType != system.NewType || data.NewType.DeclaringModule != imports.Module ||
                !HasParticleTriggerFields(data.NewType, false) ||
                resolver.ResolveRaw(sourceDataRef)?.Resolve() != data.NewType)
                return true;

            var particleType = resolver.Resolve(sourceParticleRef);
            var componentType = resolver.Resolve(sourceComponentRef);
            var returnType = resolver.Resolve(source.Signature.ReturnType);
            var parameters = source.Signature.ParameterTypes.Select(parameter => resolver.Resolve(parameter)).ToArray();
            if (particleType == null || componentType == null || returnType == null || parameters.Any(parameter => parameter == null) ||
                parameters[2] is not GenericInstanceTypeSignature particleList || particleList.IsValueType ||
                particleList.GenericType.Resolve() != list.NewType || particleList.TypeArguments.Count != 1 ||
                !SignatureComparer.Default.Equals(particleList.TypeArguments[0], particleType))
                return true;
            var arrays = new TypeSignature[]
            {
                new GenericInstanceTypeSignature(imports.Il2CppReferenceArray.ToTypeDefOrRef(), false, componentType),
                new GenericInstanceTypeSignature(imports.Il2CppStructArray.ToTypeDefOrRef(), false, imports.Module.CorLibTypeFactory.Int32),
                new GenericInstanceTypeSignature(imports.Il2CppStructArray.ToTypeDefOrRef(), false, imports.Module.CorLibTypeFactory.Int32)
            };
            var fields = data.NewType.Fields;
            for (var index = 0; index < arrays.Length; index++)
            {
                if (!SignatureComparer.Default.Equals(resolver.Resolve(sourceFields[index].Signature.FieldType), arrays[index]) ||
                    !SignatureComparer.Default.Equals(fields[index].Signature.FieldType, arrays[index]))
                    return true;
            }

            var signature = MethodSignature.CreateStatic(returnType, parameters);
            if (owner.NewType.Methods.Any(method => method.Name == source.Name &&
                SignatureComparer.Default.Equals(method.Signature, signature)))
                return true;
            var target = new MethodDefinition(source.Name,
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                MethodSignature.CreateStatic(returnType))
            {
                ImplAttributes = MethodImplAttributes.IL | MethodImplAttributes.Managed,
                CilMethodBody = new CilMethodBody()
            };
            for (var index = 0; index < parameters.Length; index++)
                InteropMethodDeclarations.AddParameter(target, parameters[index], source.Parameters[index].Name,
                    source.Parameters[index].Definition.Attributes);

            var body = target.CilMethodBody.Instructions;
            body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Ldarg_1);
            body.Add(CilOpCodes.Ldarg_2);
            // Pass the caller's array references in place; the native helper owns ref mutation and failure semantics.
            foreach (var field in fields)
            {
                body.Add(CilOpCodes.Ldarg_3);
                body.Add(CilOpCodes.Ldflda, field);
            }
            var helper = ((IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(
                typeof(InteropParticleTriggerSupport).GetMethod(nameof(InteropParticleTriggerSupport.Get))))
                .MakeGenericInstanceMethod(particleType, componentType);
            body.Add(CilOpCodes.Call, helper);
            body.Add(CilOpCodes.Ret);
            owner.NewType.Methods.Add(target);
            owner.AddRestoredManagedMethod(source, target);
            entry.Set("native-particle-trigger-adapter", null,
                "Protected native ref array marshalling through ColliderData fields; no native struct identity or layout copy.");
            return true;
        }

        private static bool HasParticleTriggerNativeReference(InteropTypeContext type, string assembly,
            string sourceName, string targetName, int arity) =>
            type?.OriginalType != null && type.ClassPointerFieldRef != null &&
            type.OriginalType.FullName == sourceName && type.OriginalType.DeclaringModule.Assembly.Name == assembly &&
            !type.OriginalType.IsValueType && !type.OriginalType.IsInterface &&
            type.OriginalType.GenericParameters.Count == arity && type.OriginalType.DeclaringType == null &&
            type.NewType.FullName == targetName && !type.NewType.IsValueType && !type.NewType.IsInterface &&
            type.NewType.GenericParameters.Count == arity && type.NewType.DeclaringType == null &&
            type.NewType.DeclaringModule == type.AssemblyContext.NewAssembly.ManifestModule;

        private static bool HasParticleTriggerFields(TypeDefinition type, bool source)
        {
            if (type.FullName != ParticleTriggerData || !type.IsValueType || type.IsEnum || !type.IsNestedPublic ||
                !type.IsSealed || type.GenericParameters.Count != 0 || type.BaseType?.FullName != "System.ValueType" ||
                type.Fields.Count != 3 || (source ? !type.IsSequentialLayout : !type.IsAutoLayout) || type.ClassLayout != null)
                return false;
            string[] names = ["colliders", "colliderIndices", "particleStartIndices"];
            for (var index = 0; index < names.Length; index++)
            {
                var field = type.Fields[index];
                if (field.Name != names[index] || field.Attributes != (source ? FieldAttributes.Assembly : FieldAttributes.Public) ||
                    field.Signature == null || field.FieldOffset != null || field.HasFieldMarshal ||
                    field.MarshalDescriptor != null || field.FieldRva != null ||
                    source && field.MetadataToken.ToInt32() != 0x04000069 + index)
                    return false;
            }
            return true;
        }
    }
}
