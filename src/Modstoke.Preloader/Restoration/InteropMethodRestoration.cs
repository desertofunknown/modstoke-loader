// SPDX-License-Identifier: GPL-3.0-or-later
// Unity method restoration adapted from Il2CppInterop Pass80/81 (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;
using BepInEx.Logging;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodRestoration
    {
        private readonly List<(MethodDefinition Original, MethodDefinition Target,
            InteropTypeContext Type, InteropRuntimeReferences Imports, InteropRestorationEntry Entry)> pendingBodies = new();
        private readonly InteropRestorationReport report;
        private readonly List<(FieldDefinition Field, TypeDefinition Delegate, MethodDefinition Original,
            InteropRuntimeReferences Imports)> pendingInitializers = new();
        private readonly List<(MethodDefinition Target, InteropRestorationEntry Entry)> completedBodies = new();
        private Dictionary<MethodDefinition, List<MethodDefinition>> interfaceImplementations;

        internal InteropMethodRestoration(InteropRestorationReport report) => this.report = report;

        internal void Restore(InteropRewriteContext context)
        {
            var restored = 0;
            var ignored = 0;
            var restorationPlan = InteropRestorationPlan.For(context);
            report.Started = true;
            foreach (var unityAssembly in context.UnityAssemblies.Assemblies)
            {
                var assembly = context.TryGetAssemblyByName(unityAssembly.Name);
                var imports = assembly?.Imports;
                var resolver = assembly == null ? null : new InteropUnstripTypeResolver(context, imports);
                foreach (var unityType in unityAssembly.ManifestModule.GetAllTypes())
                {
                    var type = assembly?.TryGetTypeByName(unityType.FullName);

                    foreach (var original in unityType.Methods)
                    {
                        var entry = report.Add(original);
                        entry.Representation = restorationPlan.GetRepresentation(unityType);
                        if (type == null)
                        {
                            entry.Set("unavailable-type", assembly == null ? "assembly-not-mapped" : "declaring-type-not-mapped",
                                unityType.FullName + ": " + restorationPlan.GetRejectionReason(unityType));
                            continue;
                        }
                        var existing = type.TryGetMethodByUnityAssemblyMethod(original);
                        if (TryRestoreAudioProvider(original, existing?.NewMethod, type, imports, resolver, entry))
                        {
                            if (entry.Outcome == "native-audio-state-adapter") restored++;
                            else ignored++;
                            continue;
                        }
                        if (InteropMethodTranslator.IsAnimationScriptPlayableMethod(original))
                        {
                            if (TryRestoreAnimationScriptPlayable(original, existing?.NewMethod, type, imports, resolver, entry))
                                restored++;
                            else
                                ignored++;
                            continue;
                        }
                        // Other native wrappers take precedence, including constructors and abstract declarations.
                        if (existing != null)
                        {
                            entry.Set("existing-wrapper");
                            continue;
                        }
                        if (TryQueueTransitionAnimation(original, type, imports, resolver, entry))
                            continue;
                        if (TryRestoreMuscleHandles(original, type, imports, entry))
                        {
                            restored++;
                            continue;
                        }
                        if (TryRestoreNavMeshTriangulation(original, type, imports, resolver, entry))
                        {
                            if (entry.Outcome == "native-triangulation-adapter") restored++;
                            else ignored++;
                            continue;
                        }
                        if (TryRestoreScreenDisplay(original, type, imports, resolver, entry))
                        {
                            if (entry.Outcome == "native-screen-display-adapter") restored++;
                            else ignored++;
                            continue;
                        }
                        if (TryRestoreScreenLayout(original, type, imports, resolver, entry))
                        {
                            if (entry.Outcome == "native-screen-layout-adapter") restored++;
                            else ignored++;
                            continue;
                        }
                        if (TryRestoreShaderWarmup(original, type, imports, resolver, entry))
                        {
                            if (entry.Outcome == "native-shader-warmup-adapter") restored++;
                            else ignored++;
                            continue;
                        }
                        if (TryRestoreParticleTriggers(original, type, imports, resolver, entry))
                        {
                            if (entry.Outcome == "native-particle-trigger-adapter") restored++;
                            else ignored++;
                            continue;
                        }
                        if (TryRestoreUQueryWhere(original, type, imports, resolver, entry))
                        {
                            if (entry.Outcome == "restored-native-uquery-predicate") restored++;
                            else ignored++;
                            continue;
                        }
                        if (TryRestoreUQueryProjection(original, type, imports, resolver, entry))
                        {
                            if (entry.Outcome == "restored-uquery-projection") restored++;
                            else ignored++;
                            continue;
                        }
                        if (TryRestoreListViewMultiEdit(original, type, imports, resolver, entry))
                        {
                            if (entry.Outcome == "native-list-view-multi-edit-adapter") restored++;
                            else ignored++;
                            continue;
                        }
                        if (TryRestoreEventDebuggerProperty(original, type, imports, resolver, entry))
                        {
                            if (entry.Outcome == "native-event-debugger-property-adapter") restored++;
                            else ignored++;
                            continue;
                        }
                        if (TryRestoreOverlayAccessor(original, type, imports, resolver, entry))
                        {
                            if (entry.Outcome == "native-overlay-event-adapter") restored++;
                            else ignored++;
                            continue;
                        }
                        if (AnimationBulkHelper(original) is { } bulkHelper)
                        {
                            if (TryRestoreAnimationBulk(original, type, imports, resolver, entry, bulkHelper)) restored++;
                            else ignored++;
                            continue;
                        }
                        if (InteropRestoredUnmanagedDelegateSupport.IsRestored(type) ||
                            InteropRestoredManagedDelegateSupport.IsRestored(type))
                        {
                            if (original.Name == ".ctor" || original.Name == "Invoke")
                                entry.Set("clr-runtime-delegate-member");
                            else
                                entry.Set("skipped", "clr-delegate-async-unavailable",
                                    "Legacy asynchronous delegate invocation is not supported by this CLR delegate representation.");
                            continue;
                        }
                        if (type.OriginalType == null && type.NewType.IsInterface)
                        {
                            entry.Set("clr-interface-member");
                            continue;
                        }
                        var isAtomicBridge = InteropAtomicBridge.TryBind(original, imports.Module,
                            element => resolver.Resolve(element), out var atomicBridge);
                        var isInternalCall = (original.ImplAttributes & MethodImplAttributes.InternalCall) != 0;
                        var isManagedValueConstructor = original.IsConstructor && !original.IsStatic &&
                            original.HasMethodBody && original.DeclaringType.IsValueType && !original.DeclaringType.IsEnum;
                        // Existing blittable structs already own their exact instance storage.
                        // Restoring their managed constructor does not add native fields or a wrapper object.
                        var isBlittableConstructor = isManagedValueConstructor &&
                            type.OriginalType != null && type.NewType.IsValueType && !type.NewType.IsEnum &&
                            type.ComputedTypeSpecifics == InteropTypeContext.TypeSpecifics.BlittableStruct;
                        var isBoxedValueConstructor = isManagedValueConstructor &&
                            original.Signature.GenericParameterCount == 0 && type.OriginalType != null &&
                            !type.NewType.IsValueType && type.ClassPointerFieldRef != null &&
                            type.ComputedTypeSpecifics == InteropTypeContext.TypeSpecifics.NonBlittableStruct;
                        var isReferenceConstructor = original.IsConstructor && !original.IsStatic &&
                            original.HasMethodBody && !original.DeclaringType.IsValueType && type.OriginalType != null;
                        if ((original.IsConstructor && type.OriginalType != null && !isBlittableConstructor &&
                            !isBoxedValueConstructor && !isReferenceConstructor) ||
                            original.IsAbstract ||
                            (!original.HasMethodBody && !isInternalCall && !isAtomicBridge))
                        {
                            entry.Set("skipped", original.IsConstructor ? "constructor" :
                                original.IsAbstract ? "abstract" : "no-managed-body");
                            continue;
                        }
                        var isRestoredValueVirtual = type.OriginalType == null && original.IsVirtual &&
                            InteropRestoredValueContracts.CanRestore(original, type.NewType);
                        if (type.OriginalType == null && original.IsVirtual && !isRestoredValueVirtual)
                        {
                            entry.Set("signature-rejected", "managed-helper-virtual-contract",
                                "A restored container does not implement the source's native interface or virtual dispatch identity.");
                            ignored++;
                            continue;
                        }
                        if (isInternalCall && !isAtomicBridge && ((!original.IsStatic && type.OriginalType == null)
                            || restorationPlan.ContainsManagedOnlyStorage(original.Signature.ReturnType)
                            || original.Signature.ParameterTypes.Any(restorationPlan.ContainsManagedOnlyStorage)))
                        {
                            entry.Set("signature-rejected", "managed-storage-native-call",
                                "The native call cannot receive a restored CLR-only container without an explicit native marshalling contract.");
                            ignored++;
                            continue;
                        }
                        if (isInternalCall && !isAtomicBridge && (original.GenericParameters.Count != 0 || unityType.GenericParameters.Count != 0))
                        {
                            entry.Set("signature-rejected", "generic-internal-call",
                                "The native delegate bridge does not bind open generic parameters.");
                            ignored++;
                            continue;
                        }
                        if (isInternalCall && !original.IsStatic && type.NewType.IsValueType)
                        {
                            entry.Set("signature-rejected", "native-value-receiver-unavailable",
                                "The native delegate bridge requires an object receiver; it cannot reinterpret a CLR struct address as an IL2CPP wrapper.");
                            ignored++;
                            continue;
                        }

                        var returnType = resolver.Resolve(original.Signature.ReturnType);
                        if (returnType == null)
                        {
                            LogUnsupported(original, "return", original.Signature.ReturnType);
                            entry.Set("signature-rejected", "return-type-unresolved",
                                InteropRestorationReport.Describe(() => original.Signature.ReturnType.FullName) + ": " +
                                resolver.GetRejectionReason(original.Signature.ReturnType));
                            ignored++;
                            continue;
                        }

                        var attributes = InteropNativeMovieTextureType.IsPrepared(type.OriginalType) ||
                            InteropNativeWwwType.IsPrepared(type.OriginalType)
                            ? original.Attributes
                            : (original.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Public;
                        if (isRestoredValueVirtual)
                            attributes = InteropRestoredValueContracts.RewriteAttributes(original, attributes);
                        var signature = original.IsStatic
                            ? MethodSignature.CreateStatic(returnType, original.Signature.GenericParameterCount)
                            : MethodSignature.CreateInstance(returnType, original.Signature.GenericParameterCount);
                        var target = new MethodDefinition(original.Name, attributes, signature)
                        {
                            CilMethodBody = new CilMethodBody()
                        };

                        var unsupportedParameter = false;
                        foreach (var parameter in original.Parameters)
                        {
                            var parameterType = resolver.Resolve(parameter.ParameterType);
                            if (parameterType == null)
                            {
                                LogUnsupported(original, "parameter", parameter);
                                entry.Set("signature-rejected", "parameter-type-unresolved",
                                    InteropRestorationReport.Describe(() => parameter.ToString()) + ": " +
                                    resolver.GetRejectionReason(parameter.ParameterType));
                                unsupportedParameter = true;
                                break;
                            }
                            InteropMethodDeclarations.AddParameter(target, parameterType, parameter.Name,
                                parameter.Definition?.Attributes ?? default);
                            if (InteropNativeWwwType.IsPrepared(type.OriginalType))
                                target.Parameters[^1].Definition.Constant = parameter.Definition?.Constant;
                        }
                        if (unsupportedParameter)
                        {
                            ignored++;
                            continue;
                        }

                        var managedJobMethod = InteropManagedJobContracts.IsExtensionMethod(original, type);
                        var preserveManagedConstraints = type.OriginalType == null || managedJobMethod;
                        if (preserveManagedConstraints &&
                            !InteropRestoredGenericSupport.TryCopyMethodParameters(original, target, resolver, out var genericReason))
                        {
                            entry.Set("signature-rejected", "managed-generic-contract", genericReason);
                            ignored++;
                            continue;
                        }
                        foreach (var parameter in preserveManagedConstraints
                            ? Enumerable.Empty<GenericParameter>() : original.GenericParameters)
                        {
                            var copy = new GenericParameter(parameter.Name.MakeValidInSource(), parameter.Attributes.StripValueTypeConstraint());
                            foreach (var constraint in parameter.Constraints)
                            {
                                if (constraint.Constraint?.FullName == "System.ValueType" ||
                                    constraint.Constraint?.Resolve()?.IsInterface == true)
                                    continue;

                                var constraintType = constraint.Constraint?.FullName == "System.Enum"
                                    ? imports.Module.DefaultImporter.ImportTypeSignature(typeof(Enum))
                                    : resolver.Resolve(constraint.Constraint?.ToTypeSignature());
                                if (constraintType != null)
                                    copy.Constraints.Add(new GenericParameterConstraint(constraintType.ToTypeDefOrRef()));
                            }
                            target.GenericParameters.Add(copy);
                        }

                        if (!InteropMethodTranslator.ValidateGenericSignature(original, target, type, imports, out var signatureReason))
                        {
                            entry.Set("signature-rejected", "managed-generic-contract", signatureReason);
                            ignored++;
                            continue;
                        }
                        if (isReferenceConstructor)
                        {
                            if (!TryRestoreReferenceConstructor(original, target, type, imports, resolver, entry))
                            {
                                ignored++;
                                continue;
                            }
                        }
                        else if (isBoxedValueConstructor)
                        {
                            if (!TryRestoreValueConstructor(original, ref target, type, imports, entry))
                            {
                                ignored++;
                                continue;
                            }
                        }
                        else if (isAtomicBridge)
                        {
                            type.NewType.Methods.Add(target);
                            InteropAtomicBridge.FillBody(target, atomicBridge);
                            entry.Set("clr-atomic-bridge");
                        }
                        else if (InteropSharedStaticSupport.TryFillBody(original, target, type, imports))
                        {
                            type.NewType.Methods.Add(target);
                            entry.Set("native-shared-static-bridge");
                        }
                        else if (isInternalCall)
                        {
                            var delegateType = InteropNativeCallStubs.CreateDelegateType(original, target, imports);
                            type.NewType.NestedTypes.Add(delegateType);
                            type.NewType.Methods.Add(target);
                            var delegateField = type.OriginalType == null
                                ? InteropNativeCallStubs.CreateDelegateField(type.NewType, delegateType)
                                : InteropNativeCallStubs.AddInitializer(type.NewType, delegateType, original, imports);
                            if (type.OriginalType == null)
                                pendingInitializers.Add((delegateField, delegateType, original, imports));
                            var inPlaceValueReferences = InteropNativeCallStubs.FillBody(original, target,
                                delegateField, delegateType, type, imports);
                            type.AddRestoredInternalCall(original, target, delegateField, delegateType, inPlaceValueReferences);
                            entry.Set("internal-call-bridge");
                        }
                        else
                        {
                            pendingBodies.Add((original, target, type, imports, entry));
                            type.AddRestoredBody(original, target);
                            entry.Set("body-pending");
                            type.NewType.Methods.Add(target);
                            if (managedJobMethod)
                            {
                                type.AddRestoredManagedMethod(original, target);
                                InteropManagedJobContracts.PreserveExtensionAttribute(original, target);
                            }
                        }

                        if (isRestoredValueVirtual)
                            InteropRestoredValueContracts.AttachInterface(original, target, imports.Module);

                        if (original.IsGetMethod)
                            GetOrCreateProperty(original, target).GetMethod = target;
                        else if (original.IsSetMethod)
                            GetOrCreateProperty(original, target).SetMethod = target;

                        var paramsMethod = InteropMethodDeclarations.CreateParamsOverload(original, target,
                            imports, resolver.Resolve);
                        if (paramsMethod != null)
                            type.NewType.Methods.Add(paramsMethod);
                        restored++;
                    }
                    RestoreOverlayEvents(unityType, type, imports);
                }
            }

            PrepareTransitionAnimations(ref restored, ref ignored);
            interfaceImplementations = InteropRestoredInterfaceSupport.AttachImplementations(context);
            PrepareEventReplayBindings(context);
            PrepareStyleEnumBindings(context);
            PrepareMovieTextureBindings(context);
            PrepareWwwBindings(context);
            InteropGenerationDiagnostics.Log(LogLevel.Info, $"Restored {restored} methods");
            InteropGenerationDiagnostics.Log(LogLevel.Info, $"Failed to restore {ignored} methods");
        }

        private bool TryRestoreAnimationScriptPlayable(MethodDefinition original, MethodDefinition target,
            InteropTypeContext type, InteropRuntimeReferences imports, InteropUnstripTypeResolver resolver,
            InteropRestorationEntry entry)
        {
            if (!InteropMethodTranslator.ValidateAnimationScriptPlayableMethod(original, target, type, resolver,
                out _, out var reason))
            {
                entry.Set("signature-rejected", "animation-job-contract", reason);
                return false;
            }

            var declaration = new MethodDefinition(target.Name, target.Attributes, target.Signature)
            {
                CilMethodBody = new CilMethodBody()
            };
            if (!InteropRestoredGenericSupport.TryCopyMethodParameters(original, declaration, resolver, out reason) ||
                !InteropMethodTranslator.ValidateGenericSignature(original, declaration, type, imports, out reason))
            {
                entry.Set("signature-rejected", "managed-generic-contract", reason);
                return false;
            }

            // Keep the generated definition, signature and native mapping identities. Managed calls
            // must bypass native generic identity checks now that T is a restored CLR job contract.
            var parameter = target.GenericParameters[0];
            var restoredParameter = declaration.GenericParameters[0];
            parameter.Attributes = restoredParameter.Attributes;
            parameter.Constraints.Clear();
            foreach (var constraint in restoredParameter.Constraints)
                parameter.Constraints.Add(new GenericParameterConstraint(constraint.Constraint));
            type.AddRestoredManagedMethod(original, target);
            pendingBodies.Add((original, target, type, imports, entry));
            type.AddRestoredBody(original, target);
            entry.Set("body-pending");
            return true;
        }

        internal void FillBodies()
        {
            var succeeded = 0;
            var failed = 0;
            try
            {
                foreach (var method in pendingBodies)
                {
                    try
                    {
                        var success = IsStyleEnumStart(method.Original, method.Type)
                            ? FillStyleEnumStart(method.Original, method.Target, method.Type, method.Imports, out var failure)
                            : InteropMethodTranslator.TranslateMethod(method.Original, method.Target, method.Type, method.Imports,
                                out failure);
                        if (success)
                        {
                            if (method.Type.TryGetRestoredReferenceInitializer(method.Original) == method.Target)
                                RetainEventTraceArguments(method.Target, method.Imports);
                            succeeded++;
                            method.Entry.Set("managed-body-translated");
                        }
                        else
                        {
                            failed++;
                            method.Entry.Set("body-rejected", failure?.Reason, failure?.Detail);
                            method.Entry.Failure = failure;
                            InteropMethodTranslator.ReplaceBodyWithException(method.Target, method.Imports);
                            LogUnsupportedBody(method.Original, failure);
                        }
                    }
                    catch (Exception error)
                    {
                        method.Entry.Set("generation-error", error.GetType().FullName,
                            InteropGenerationDiagnostics.Message(error));
                        throw;
                    }
                }
                foreach (var initializer in pendingInitializers)
                    InteropNativeCallStubs.PrependInitializer(initializer.Field, initializer.Delegate,
                        initializer.Original, initializer.Imports);
                MarkUnavailableReplayContracts();
                MarkUnavailableStyleEnumContracts();
                MarkUnavailableMovieTextureContracts();
                MarkUnavailableWwwContracts();
                ReportUnavailableDependencies();
                BindEventReplayBodies();
                BindStyleEnumBodies();
                BindMovieTextureBodies();
                BindWwwBodies();
                report.Completed = true;
            }
            finally
            {
                // An interrupted translation must not retain metadata from this run.
                pendingBodies.Clear();
                completedBodies.Clear();
                pendingInitializers.Clear();
                valueConstructorBodies.Clear();
                referenceConstructorBodies.Clear();
                interfaceImplementations?.Clear();
                replayBindings.Clear();
                replayDependencies.Clear();
                styleEnumBindings.Clear();
                styleEnumDependencies.Clear();
                movieTextureBindings.Clear();
                movieTextureDependencies.Clear();
                wwwBindings.Clear();
                wwwDependencies.Clear();
                pendingTransitionAnimations.Clear();
            }
            InteropGenerationDiagnostics.Log(LogLevel.Info,
                $"IL unstrip statistics: {succeeded} translated bodies, {failed} rejected bodies; dependency and native-call availability are separate.");
        }

        private void ReportUnavailableDependencies()
        {
            // Translation alone does not establish a usable call chain. Keep translated
            // bodies intact so a valid branch still works, but do not label callers of
            // rejected restored methods as fully translated coverage.
            var entries = new Dictionary<MethodDefinition, InteropRestorationEntry>(ReferenceEqualityComparer.Instance);
            var callers = new Dictionary<MethodDefinition, List<MethodDefinition>>(ReferenceEqualityComparer.Instance);
            var pending = new Queue<MethodDefinition>();
            var referenceAdapters = pendingBodies.Select(method => method.Type).Distinct()
                .Where(type => type.ScopedReferenceMethods != null)
                .SelectMany(type => type.ScopedReferenceMethods.Values).Where(method => method != null).ToHashSet();
            var adapterDependencies = new Dictionary<MethodDefinition, MethodDefinition[]>(ReferenceEqualityComparer.Instance);
            var methods = pendingBodies.Select(method => (method.Target, method.Entry)).Concat(completedBodies).ToArray();
            var translated = new HashSet<MethodDefinition>(ReferenceEqualityComparer.Instance);
            var unresolvedBodies = new HashSet<MethodDefinition>(ReferenceEqualityComparer.Instance);
            var unresolved = 0;
            foreach (var method in methods)
            {
                entries.Add(method.Target, method.Entry);
                if (method.Entry.Outcome is "managed-body-translated" or "restored-uquery-projection" or
                    "native-list-view-multi-edit-adapter" or "native-overlay-event-adapter" or
                    "native-reference-copy-constructor-adapter" or "restored-transition-animation-forwarder" or
                    "native-vfx-spawner-constructor-adapter" or "native-web-request-constructor-adapter")
                    translated.Add(method.Target);
                else
                    pending.Enqueue(method.Target);
            }

            foreach (var method in methods)
            {
                if (!translated.Contains(method.Target))
                    continue;

                var uniqueDependencies = new HashSet<MethodDefinition>(ReferenceEqualityComparer.Instance);
                var inspectedAdapters = new HashSet<MethodDefinition>(ReferenceEqualityComparer.Instance);
                var bodies = new Queue<MethodDefinition>();
                bodies.Enqueue(method.Target);
                if (replayDependencies.TryGetValue(method.Target, out var replayMethods))
                    foreach (var implementation in replayMethods) AddDependency(implementation);
                if (styleEnumDependencies.TryGetValue(method.Target, out var styleMethods))
                    foreach (var implementation in styleMethods) AddDependency(implementation);
                if (movieTextureDependencies.TryGetValue(method.Target, out var movieMethods))
                    foreach (var implementation in movieMethods) AddDependency(implementation);
                if (wwwDependencies.TryGetValue(method.Target, out var wwwMethods))
                    foreach (var implementation in wwwMethods) AddDependency(implementation);
                while (bodies.TryDequeue(out var inspected))
                {
                    if (adapterDependencies.TryGetValue(inspected, out var cached))
                    {
                        foreach (var dependency in cached)
                            AddDependency(dependency);
                        continue;
                    }

                    var resolved = referenceAdapters.Contains(inspected) ? new List<MethodDefinition>() : null;
                    var complete = true;
                    foreach (var instruction in inspected.CilMethodBody.Instructions)
                    {
                        if (instruction.Operand is not IMethodDescriptor descriptor)
                            continue;

                        MethodDefinition dependency;
                        try
                        {
                            dependency = descriptor.Resolve();
                        }
                        catch (Exception)
                        {
                            complete = false;
                            unresolved++;
                            unresolvedBodies.Add(method.Target);
                            continue;
                        }
                        if (dependency == null)
                        {
                            complete = false;
                            unresolved++;
                            unresolvedBodies.Add(method.Target);
                            continue;
                        }

                        resolved?.Add(dependency);
                        AddDependency(dependency);
                    }
                    // Failed resolutions may succeed after another dependency loads. Only
                    // complete shared-body inspections can be reused by subsequent roots.
                    if (resolved != null && complete)
                        adapterDependencies.Add(inspected, resolved.ToArray());
                }

                void AddDependency(MethodDefinition dependency)
                {
                    // An interface call can reach any attached implementation, including a rejected restored body.
                    if (interfaceImplementations != null && interfaceImplementations.TryGetValue(dependency, out var implementations))
                    {
                        foreach (var implementation in implementations) AddDependency(implementation);
                        return;
                    }
                    // Scoped adapters are translated outside pendingBodies, but their
                    // restored callees still contribute to the root's coverage status.
                    if (referenceAdapters.Contains(dependency))
                    {
                        if (inspectedAdapters.Add(dependency)) bodies.Enqueue(dependency);
                        return;
                    }

                    // Constructor translation outcomes belong to their shared initializers.
                    if (valueConstructorBodies.TryGetValue(dependency, out var initializer) ||
                        referenceConstructorBodies.TryGetValue(dependency, out initializer))
                        dependency = initializer;
                    if (!entries.ContainsKey(dependency) || !uniqueDependencies.Add(dependency))
                        return;

                    if (!callers.TryGetValue(dependency, out var dependencyCallers))
                    {
                        dependencyCallers = new();
                        callers.Add(dependency, dependencyCallers);
                    }
                    dependencyCallers.Add(method.Target);
                }
            }

            var partial = MarkUnresolvedWwwDependencies(unresolvedBodies, callers, entries, translated, pending);
            while (pending.Count != 0)
            {
                var unavailable = pending.Dequeue();
                if (!callers.TryGetValue(unavailable, out var dependencyCallers))
                    continue;

                foreach (var caller in dependencyCallers)
                {
                    var entry = entries[caller];
                    if (!translated.Remove(caller))
                        continue;
                    entry.Set("managed-body-partial", "restored-dependency-unavailable", unavailable.FullName);
                    partial++;
                    pending.Enqueue(caller);
                }
            }
            if (partial != 0)
                InteropGenerationDiagnostics.Log(LogLevel.Info,
                    $"Restored method dependency coverage: {partial} translated bodies depend on unavailable restored methods; working branches were preserved.");
            if (unresolved != 0)
                InteropGenerationDiagnostics.Log(LogLevel.Warning,
                    $"Restored method dependency inspection could not resolve {unresolved} call references; this report is not complete runtime callability evidence.");
        }

        private static PropertyDefinition GetOrCreateProperty(MethodDefinition original, MethodDefinition target)
        {
            var originalProperty = original.DeclaringType.Properties.Single(property =>
                property.SetMethod == original || property.GetMethod == original);
            var propertyType = original.IsGetMethod
                ? target.Signature.ReturnType
                : target.Signature.ParameterTypes.Last();
            var parameters = (original.IsGetMethod
                ? target.Signature.ParameterTypes
                : target.Signature.ParameterTypes.Take(target.Signature.ParameterTypes.Count - 1)).ToArray();
            var property = target.DeclaringType?.Properties.SingleOrDefault(candidate =>
                candidate.Name == originalProperty.Name &&
                candidate.Signature.HasThis == originalProperty.Signature.HasThis &&
                SignatureComparer.Default.Equals(candidate.Signature.ReturnType, propertyType) &&
                candidate.Signature.ParameterTypes.SequenceEqual(parameters,
                    SignatureComparer.Default));
            if (property != null)
                return property;

            var signature = originalProperty.Signature.HasThis
                ? PropertySignature.CreateInstance(propertyType, parameters)
                : PropertySignature.CreateStatic(propertyType, parameters);
            property = new PropertyDefinition(originalProperty.Name, originalProperty.Attributes, signature);
            target.DeclaringType.Properties.Add(property);
            return property;
        }

        private static void LogUnsupportedBody(MethodDefinition method, InteropTranslationFailure failure)
        {
            try
            {
                InteropGenerationDiagnostics.Log(LogLevel.Debug,
                    "Unsupported method body: " + method.FullName + " [" + failure?.Reason + "] " +
                    failure?.Detail + (failure?.Offset is int offset ? " at IL_" + offset.ToString("X4") + " " + failure.OpCode : ""));
            }
            catch
            {
                // Formatting unsupported metadata is diagnostic only.
            }
        }

        private static void LogUnsupported(MethodDefinition method, string position, object unsupportedType)
        {
            try
            {
                InteropGenerationDiagnostics.Log(LogLevel.Debug,
                    $"Method {method} has unsupported {position} type {unsupportedType}");
            }
            catch
            {
                // Formatting unsupported metadata is diagnostic only.
            }
        }
    }
}
