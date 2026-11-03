// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Linq;
using System.Runtime.CompilerServices;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal static class InteropManagedJobContracts
    {
        internal static bool IsExtensionMethod(MethodDefinition method, InteropTypeContext owner)
        {
            if (!method.IsStatic ||
                method.GenericParameters.Count != 1 || !method.HasMethodBody ||
                owner.TryGetMethodByUnityAssemblyMethod(method) != null) return false;
            var assemblyName = (string)method.DeclaringModule.Assembly.Name;
            (string contractName, string producerName) = assemblyName switch
            {
                "UnityEngine.CoreModule" when method.DeclaringType.FullName == "UnityEngine.Jobs.IJobParallelForTransformExtensions" &&
                    (string)method.Name is "GetReflectionData" or "Schedule" or "ScheduleByRef" or
                        "ScheduleReadOnly" or "ScheduleReadOnlyByRef" =>
                    ("UnityEngine.Jobs.IJobParallelForTransform",
                        "UnityEngine.Jobs.IJobParallelForTransformExtensions/TransformParallelForLoopStruct`1"),
                "UnityEngine.CoreModule" when (string)method.Name is "GetReflectionData" or "Run" or "RunByRef" or
                    "Schedule" or "ScheduleByRef" or "ScheduleParallel" or "ScheduleParallelByRef" =>
                    method.DeclaringType.FullName switch
                    {
                        "Unity.Jobs.IJobExtensions" => ("Unity.Jobs.IJob", "Unity.Jobs.IJobExtensions/JobStruct`1"),
                        "Unity.Jobs.IJobForExtensions" => ("Unity.Jobs.IJobFor", "Unity.Jobs.IJobForExtensions/ForJobStruct`1"),
                        "Unity.Jobs.IJobParallelForExtensions" => ("Unity.Jobs.IJobParallelFor", "Unity.Jobs.IJobParallelForExtensions/ParallelForJobStruct`1"),
                        _ => (null, null)
                    },
                "UnityEngine.ParticleSystemModule" => ParticleContract(method),
                _ => (null, null)
            };
            if (contractName == null) return false;
            var contract = owner.AssemblyContext.TryGetTypeByName(contractName);
            var producer = owner.AssemblyContext.TryGetTypeByName(producerName);
            if (contract == null || contract.OriginalType != null || !contract.NewType.IsInterface ||
                producer == null || producer.OriginalType != null || producer.NewType.GenericParameters.Count != 1) return false;
            var parameter = method.GenericParameters[0];
            return (parameter.Attributes & GenericParameterAttributes.SpecialConstraintMask) ==
                (GenericParameterAttributes.NotNullableValueTypeConstraint | GenericParameterAttributes.DefaultConstructorConstraint) &&
                parameter.Constraints.Any(constraint => constraint.Constraint?.FullName == contractName &&
                    InteropRestoredTypeSupport.GetAssemblyName(constraint.Constraint.ToTypeSignature()) == assemblyName);
        }

        private static (string Contract, string Producer) ParticleContract(MethodDefinition method)
        {
            const string prefix = "UnityEngine.ParticleSystemJobs.";
            var contract = ((string)method.DeclaringType.FullName, (string)method.Name, method.Signature.ParameterTypes.Count) switch
            {
                (prefix + "IParticleSystemJobExtensions", "Schedule", 3) or
                    (prefix + "IJobParticleSystemExtensions", "GetReflectionData", 0) => "IJobParticleSystem",
                (prefix + "IParticleSystemJobExtensions", "Schedule", 4) or
                    (prefix + "IJobParticleSystemParallelForExtensions", "GetReflectionData", 0) => "IJobParticleSystemParallelFor",
                (prefix + "IParticleSystemJobExtensions", "ScheduleBatch", 4) or
                    (prefix + "IJobParticleSystemParallelForBatchExtensions", "GetReflectionData", 0) => "IJobParticleSystemParallelForBatch",
                _ => null
            };
            return contract switch
            {
                "IJobParticleSystem" => (prefix + contract, prefix + "ParticleSystemJobStruct`1"),
                "IJobParticleSystemParallelFor" => (prefix + contract, prefix + "ParticleSystemParallelForJobStruct`1"),
                "IJobParticleSystemParallelForBatch" => (prefix + contract, prefix + "ParticleSystemParallelForBatchJobStruct`1"),
                _ => (null, null)
            };
        }

        internal static void PreserveExtensionAttribute(MethodDefinition source, MethodDefinition target)
        {
            const string name = "System.Runtime.CompilerServices.ExtensionAttribute";
            if (!source.CustomAttributes.Any(attribute => attribute.Constructor?.DeclaringType?.FullName == name)) return;
            var constructor = (ICustomAttributeType)target.DeclaringModule.DefaultImporter.ImportMethod(
                typeof(ExtensionAttribute).GetConstructor(System.Type.EmptyTypes));
            target.CustomAttributes.Add(new CustomAttribute(constructor));
            if (!target.DeclaringType.CustomAttributes.Any(attribute => attribute.Constructor?.DeclaringType?.FullName == name))
                target.DeclaringType.CustomAttributes.Add(new CustomAttribute(constructor));
        }
    }
}
