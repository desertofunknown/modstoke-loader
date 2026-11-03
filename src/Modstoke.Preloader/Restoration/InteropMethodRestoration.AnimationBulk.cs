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
        private static string AnimationBulkHelper(MethodDefinition source)
        {
            if (source.DeclaringModule.Assembly.Name != InteropNativeArrayLowering.AnimationModule) return null;
            return (source.DeclaringType.FullName, (string)source.Name) switch
            {
                ("UnityEngine.Animations.AnimationSceneHandleUtility", "ReadInts") => nameof(InteropAnimationBulkSupport.ReadSceneInts),
                ("UnityEngine.Animations.AnimationSceneHandleUtility", "ReadFloats") => nameof(InteropAnimationBulkSupport.ReadSceneFloats),
                ("UnityEngine.Animations.AnimationStreamHandleUtility", "ReadInts") => nameof(InteropAnimationBulkSupport.ReadStreamInts),
                ("UnityEngine.Animations.AnimationStreamHandleUtility", "ReadFloats") => nameof(InteropAnimationBulkSupport.ReadStreamFloats),
                ("UnityEngine.Animations.AnimationStreamHandleUtility", "WriteInts") => nameof(InteropAnimationBulkSupport.WriteStreamInts),
                ("UnityEngine.Animations.AnimationStreamHandleUtility", "WriteFloats") => nameof(InteropAnimationBulkSupport.WriteStreamFloats),
                _ => null
            };
        }

        private static bool TryRestoreAnimationBulk(MethodDefinition source, InteropTypeContext owner,
            InteropRuntimeReferences imports, InteropUnstripTypeResolver resolver, InteropRestorationEntry entry,
            string helperName)
        {
            var context = owner.AssemblyContext.GlobalContext;
            var signature = source.Signature;
            var write = source.Name == "WriteInts" || source.Name == "WriteFloats";
            var scene = helperName.StartsWith("ReadScene", System.StringComparison.Ordinal);
            var integer = source.Name == "ReadInts" || source.Name == "WriteInts";
            var parameters = signature?.ParameterTypes;
            entry.Set("signature-rejected", "animation-bulk-array-contract",
                "Bulk animation requires the exact source signature and closed CLR handle storage with native numeric buffers.");
            if (!source.IsPublic || !source.IsStatic || source.IsAbstract || !source.HasMethodBody ||
                source.ImplAttributes != MethodImplAttributes.IL || source.GenericParameters.Count != 0 ||
                source.DeclaringType.GenericParameters.Count != 0 || !InteropDelegateContracts.HasStandardConvention(signature) ||
                signature.HasThis || signature.GenericParameterCount != 0 ||
                signature.ReturnType is not CorLibTypeSignature { ElementType: ElementType.Void } ||
                parameters.Count != (write ? 4 : 3) || source.Parameters.Count != parameters.Count ||
                parameters[0] is not TypeDefOrRefSignature { IsValueType: true } stream ||
                stream.FullName != "UnityEngine.Animations.AnimationStream" ||
                InteropRestoredTypeSupport.GetAssemblyName(stream) != InteropNativeArrayLowering.AnimationModule ||
                !InteropNativeArrayLowering.TryGetElement(context, parameters[1], out var handle, out var readOnly) || readOnly ||
                handle.FullName != (scene ? InteropNativeArrayLowering.SceneHandle : InteropNativeArrayLowering.StreamHandle) ||
                parameters[2] is not GenericInstanceTypeSignature buffer || !buffer.IsValueType ||
                buffer.GenericType.FullName != InteropNativeArrayLowering.ArrayName || buffer.TypeArguments.Count != 1 ||
                InteropRestoredTypeSupport.GetAssemblyName(buffer.GenericType.ToTypeSignature()) != InteropNativeArrayLowering.CoreModule ||
                buffer.TypeArguments[0] is not CorLibTypeSignature numeric ||
                numeric.ElementType != (integer ? ElementType.I4 : ElementType.R4) ||
                write && parameters[3] is not CorLibTypeSignature { ElementType: ElementType.Boolean }) return false;

            var resolved = parameters.Select(parameter => resolver.Resolve(parameter)).ToArray();
            var element = resolver.Resolve(handle);
            if (resolved.Any(parameter => parameter == null) || element == null || !resolved[0].IsValueType ||
                resolved[1] is not GenericInstanceTypeSignature storage || !storage.IsValueType ||
                resolved[2] is not GenericInstanceTypeSignature nativeBuffer || nativeBuffer.IsValueType ||
                context.TryGetTypeByName(InteropNativeArrayLowering.CoreModule, InteropNativeArrayLowering.ArrayName)?.OriginalType == null)
                return false;
            var target = new MethodDefinition(source.Name,
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                MethodSignature.CreateStatic(imports.Module.CorLibTypeFactory.Void))
            {
                ImplAttributes = MethodImplAttributes.IL | MethodImplAttributes.Managed,
                CilMethodBody = new CilMethodBody()
            };
            for (var index = 0; index < resolved.Length; index++)
            {
                var parameter = source.Parameters[index];
                var copy = InteropMethodDeclarations.AddParameter(target, resolved[index], parameter.Name,
                    parameter.Definition?.Attributes ?? 0);
                copy.Definition.Constant = parameter.Definition?.Constant;
                target.CilMethodBody.Instructions.Add(CilOpCodes.Ldarg, copy);
            }
            var helper = ((IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(
                typeof(InteropAnimationBulkSupport).GetMethod(helperName)))
                .MakeGenericInstanceMethod(resolved[0], element, resolved[2]);
            target.CilMethodBody.Instructions.Add(CilOpCodes.Call, helper);
            target.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
            owner.NewType.Methods.Add(target);
            owner.AddRestoredManagedMethod(source, target);
            entry.Set("managed-native-array-adapter", null,
                "Closed CLR handle storage and native numeric buffers through the protected bulk boundary; no native handle-array identity.");
            return true;
        }
    }
}
