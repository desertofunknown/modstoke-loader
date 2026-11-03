// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

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
        private static bool TryRestoreMuscleHandles(MethodDefinition source, InteropTypeContext owner,
            InteropRuntimeReferences imports, InteropRestorationEntry entry)
        {
            if (source.DeclaringModule.Assembly.Name != "UnityEngine.AnimationModule" ||
                source.DeclaringType.FullName != "UnityEngine.Animations.MuscleHandle" ||
                source.Name != "GetMuscleHandles" || source.MetadataToken.ToInt32() != 0x0600062B ||
                source.Attributes != (MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig) ||
                source.ImplAttributes != MethodImplAttributes.InternalCall || source.HasMethodBody ||
                !InteropDelegateContracts.HasStandardConvention(source.Signature) || source.Signature.HasThis ||
                source.Signature.GenericParameterCount != 0 || source.GenericParameters.Count != 0 ||
                source.Signature.ReturnType is not CorLibTypeSignature { ElementType: ElementType.Void } ||
                source.Signature.ParameterTypes.Count != 1 || source.Parameters.Count != 1 ||
                source.Signature.ParameterTypes[0] is not SzArrayTypeSignature array ||
                array.BaseType is not TypeDefOrRefSignature element || !element.IsValueType ||
                element.Resolve() != source.DeclaringType ||
                source.Parameters[0].Definition is not { } parameter || parameter.Attributes != ParameterAttributes.Out ||
                !parameter.CustomAttributes.Any(attribute =>
                    attribute.Constructor?.DeclaringType?.FullName == "UnityEngine.Bindings.NotNullAttribute") ||
                owner.OriginalType != null || owner.ClassPointerFieldRef != null ||
                owner.NewType.DeclaringModule != imports.Module ||
                owner.NewType.DeclaringModule.Assembly.Name != source.DeclaringModule.Assembly.Name ||
                owner.NewType.FullName != source.DeclaringType.FullName ||
                !HasMuscleHandleArrayLayout(source.DeclaringType) || !HasMuscleHandleArrayLayout(owner.NewType))
                return false;

            // This is a CLR array contract, not an IL2CPP array or an internal-call bridge.
            var targetElement = owner.NewType.ToTypeSignature();
            var target = new MethodDefinition(source.Name,
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                MethodSignature.CreateStatic(imports.Module.CorLibTypeFactory.Void))
            {
                ImplAttributes = MethodImplAttributes.IL | MethodImplAttributes.Managed,
                CilMethodBody = new CilMethodBody()
            };
            InteropMethodDeclarations.AddParameter(target, new SzArrayTypeSignature(targetElement),
                source.Parameters[0].Name, parameter.Attributes);
            var helper = ((IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(
                typeof(InteropMuscleHandleSupport).GetMethod(nameof(InteropMuscleHandleSupport.GetMuscleHandles))))
                .MakeGenericInstanceMethod(targetElement);
            target.CilMethodBody.Instructions.Add(CilOpCodes.Ldarg_0);
            target.CilMethodBody.Instructions.Add(CilOpCodes.Call, helper);
            target.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
            owner.NewType.Methods.Add(target);
            owner.AddRestoredManagedMethod(source, target);
            entry.Set("managed-array-adapter", null,
                "CLR MuscleHandle[] enumeration with managed bounds preflight; no native array identity or internal call.");
            return true;
        }

        private static bool HasMuscleHandleArrayLayout(TypeDefinition type)
        {
            if (!type.IsValueType || type.IsEnum || !type.IsSequentialLayout || type.GenericParameters.Count != 0 ||
                type.ClassLayout is { } layout &&
                (layout.ClassSize is not (0 or 8) || layout.PackingSize is not (0 or 4 or 8)))
                return false;
            var fields = type.Fields.Where(field => !field.IsStatic).ToArray();
            if (fields.Length != 2 || fields[0].Name != "<humanPartDof>k__BackingField" ||
                fields[1].Name != "<dof>k__BackingField" ||
                fields[0].FieldOffset is not (null or 0) || fields[1].FieldOffset is not (null or 4) ||
                fields.Any(field => field.HasFieldMarshal || field.FieldRva != null) ||
                fields[1].Signature?.FieldType is not CorLibTypeSignature { ElementType: ElementType.I4 } ||
                fields[0].Signature?.FieldType is not TypeDefOrRefSignature part ||
                !part.IsValueType || part.Resolve() is not { } partType || !partType.IsEnum ||
                partType.FullName != "UnityEngine.HumanPartDof" || partType.DeclaringModule != type.DeclaringModule ||
                partType.GenericParameters.Count != 0)
                return false;
            // Sequential int32 fields establish offsets 0 and 4 and an eight-byte stride.
            var underlying = partType.Fields.Where(field => !field.IsStatic).ToArray();
            return underlying.Length == 1 && underlying[0].Name == "value__" &&
                underlying[0].Signature?.FieldType is CorLibTypeSignature { ElementType: ElementType.I4 };
        }
    }
}
