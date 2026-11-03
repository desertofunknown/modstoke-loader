// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using BepInEx.Logging;

namespace Modstoke.Preloader
{
    internal static partial class InteropRestoredInterfaceSupport
    {
        internal static Dictionary<MethodDefinition, List<MethodDefinition>> AttachImplementations(InteropRewriteContext context)
        {
            var plan = InteropRestorationPlan.For(context);
            var implementations = new Dictionary<MethodDefinition, List<MethodDefinition>>(ReferenceEqualityComparer.Instance);
            var attached = 0;
            foreach (var assembly in context.UnityAssemblies.Assemblies)
            foreach (var source in assembly.ManifestModule.GetAllTypes())
            {
                if (source.IsInterface || source.IsValueType || source.Interfaces.Count == 0)
                    continue;
                var type = context.TryGetAssemblyByName(assembly.Name)?.TryGetTypeByName(source.FullName);
                if (type?.OriginalType == null || type.ClassPointerFieldRef == null || type.NewType.IsValueType ||
                    type.NewType.GenericParameters.Count != source.GenericParameters.Count)
                    continue;
                var resolver = new InteropUnstripTypeResolver(context, type.AssemblyContext.Imports);
                foreach (var implementation in source.Interfaces)
                {
                    var contract = implementation.Interface.ToTypeSignature();
                    if (!plan.IsManagedOnlyDefinition(contract) || contract.Resolve() is not { IsInterface: true } definition)
                        continue;
                    var rewritten = resolver.Resolve(contract);
                    if (rewritten?.Resolve() is not { IsInterface: true } targetInterface ||
                        type.NewType.Interfaces.Any(existing =>
                            SignatureComparer.Default.Equals(existing.Interface.ToTypeSignature(), rewritten)))
                        continue;
                    if (!TryBindContract(source, type, contract, definition, rewritten, targetInterface,
                        out var bridges, out var reason))
                    {
                        InteropGenerationDiagnostics.Log(LogLevel.Debug,
                            "CLR interface not attached: " + source.FullName + ": " + contract.FullName + ": " + reason);
                        continue;
                    }
                    // Attach only a complete contract. Forward through existing wrappers without creating native vtable slots.
                    foreach (var (bridge, declaration, slot, target) in bridges)
                    {
                        type.NewType.Methods.Add(bridge);
                        type.NewType.MethodImplementations.Add(new MethodImplementation(declaration, bridge));
                        if (!implementations.TryGetValue(slot, out var bodies))
                            implementations.Add(slot, bodies = new());
                        bodies.Add(target);
                    }
                    type.NewType.Interfaces.Add(new InterfaceImplementation(rewritten.ToTypeDefOrRef()));
                    attached++;
                }
            }
            InteropGenerationDiagnostics.Log(LogLevel.Debug, "Attached " + attached + " restored CLR interface contracts");
            return implementations;
        }

        private static bool TryBindContract(TypeDefinition source, InteropTypeContext type, TypeSignature contract,
            TypeDefinition definition, TypeSignature rewritten, TypeDefinition targetInterface,
            out List<(MethodDefinition Bridge, MemberReference Declaration, MethodDefinition Slot, MethodDefinition Target)> bridges,
            out string reason)
        {
            bridges = new();
            reason = "The interface requires a constrained generic implementation contract.";
            if (definition.GenericParameters.Any(parameter => parameter.Constraints.Count != 0 ||
                (parameter.Attributes & GenericParameterAttributes.SpecialConstraintMask) != 0) ||
                targetInterface.GenericParameters.Count != definition.GenericParameters.Count ||
                targetInterface.Methods.Count != definition.Methods.Count)
                return false;
            var sourceContext = GenericContext.FromType(contract);
            var targetContext = GenericContext.FromType(rewritten);
            var importer = type.AssemblyContext.Imports.Module.DefaultImporter;
            for (var index = 0; index < definition.Methods.Count; index++)
            {
                var slot = definition.Methods[index];
                var targetSlot = targetInterface.Methods[index];
                var signature = slot.Signature.InstantiateGenericTypes(sourceContext);
                var original = FindImplementation(source, contract, slot.Name, signature);
                var wrapper = original == null ? null : type.TryGetMethodByUnityAssemblyMethod(original)?.NewMethod;
                var target = wrapper ?? type.TryGetRestoredBody(original);
                var targetSignature = targetSlot.Signature.InstantiateGenericTypes(targetContext);
                if (target == null || target.DeclaringType != type.NewType || target.IsStatic || target.IsAbstract ||
                    !target.HasMethodBody || target.GenericParameters.Count != 0 ||
                    !SignatureComparer.Default.Equals(target.Signature, targetSignature) || targetSlot.Name != slot.Name)
                {
                    reason = "No exact callable implementation for " + slot.FullName;
                    return false;
                }

                var name = rewritten.FullName + "." + targetSlot.Name;
                if (type.NewType.Methods.Any(method => method.Name == name &&
                    SignatureComparer.Default.Equals(method.Signature, targetSignature)) ||
                    bridges.Any(entry => entry.Bridge.Name == name &&
                        SignatureComparer.Default.Equals(entry.Bridge.Signature, targetSignature)))
                {
                    reason = "The explicit implementation already exists: " + name;
                    return false;
                }
                var bridge = new MethodDefinition(name, MethodAttributes.Private | MethodAttributes.HideBySig |
                    MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot,
                    MethodSignature.CreateInstance(importer.ImportTypeSignature(targetSignature.ReturnType)));
                for (var parameterIndex = 0; parameterIndex < targetSignature.ParameterTypes.Count; parameterIndex++)
                {
                    var parameter = targetSlot.Parameters[parameterIndex];
                    InteropMethodDeclarations.AddParameter(bridge, importer.ImportTypeSignature(targetSignature.ParameterTypes[parameterIndex]),
                        parameter.Name, (parameter.Definition?.Attributes ?? 0) & (ParameterAttributes.In | ParameterAttributes.Out));
                }
                bridge.CilMethodBody = new CilMethodBody();
                var code = bridge.CilMethodBody.Instructions;
                code.Add(CilOpCodes.Ldarg_0);
                foreach (var parameter in bridge.Parameters)
                    code.Add(CilOpCodes.Ldarg, parameter);
                code.Add(CilOpCodes.Callvirt, new MemberReference(type.SelfSubstitutedRef, target.Name, target.Signature));
                code.Add(CilOpCodes.Ret);
                bridges.Add((bridge, (MemberReference)importer.ImportMethod(new MemberReference(
                    rewritten.ToTypeDefOrRef(), targetSlot.Name, targetSlot.Signature)),
                    targetSlot, target));
            }
            reason = null;
            return true;
        }

        private static MethodDefinition FindImplementation(TypeDefinition source, TypeSignature contract,
            string name, MethodSignature signature)
        {
            var explicitSlots = source.MethodImplementations.Where(implementation =>
                implementation.Declaration.Name == name &&
                SignatureComparer.Default.Equals(implementation.Declaration.DeclaringType.ToTypeSignature(), contract) &&
                SignatureComparer.Default.Equals(implementation.Declaration.Signature.InstantiateGenericTypes(
                    GenericContext.FromMethod(implementation.Declaration)), signature)).ToArray();
            if (explicitSlots.Length != 0)
            {
                var body = explicitSlots.Length == 1 ? explicitSlots[0].Body?.Resolve() : null;
                return body?.DeclaringType == source && Matches(body) ? body : null;
            }
            var candidates = source.Methods.Where(method => method.IsPublic && method.Name == name && Matches(method)).ToArray();
            return candidates.Length == 1 ? candidates[0] : null;

            bool Matches(MethodDefinition method) => !method.IsStatic && !method.IsAbstract && method.IsVirtual &&
                method.GenericParameters.Count == 0 && SignatureComparer.Default.Equals(method.Signature, signature);
        }
    }
}
