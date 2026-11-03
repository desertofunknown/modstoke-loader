// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using System.Runtime.InteropServices;
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
        private const string AudioProviderType = "UnityEngine.Experimental.Audio.AudioSampleProvider";
        private const string AudioFramesHandlerType = AudioProviderType + "/SampleFramesEventNativeFunction";

        private static bool TryRestoreAudioProvider(MethodDefinition original, MethodDefinition target,
            InteropTypeContext owner, InteropRuntimeReferences imports, InteropUnstripTypeResolver resolver,
            InteropRestorationEntry entry)
        {
            if (original.DeclaringModule.Assembly.Name != "UnityEngine.AudioModule" ||
                original.DeclaringType.FullName != AudioProviderType)
                return false;

            var name = (string)original.Name;
            var helperName = name switch
            {
                "Create" => nameof(NativeAudioSampleProvider.Create),
                "Lookup" => nameof(NativeAudioSampleProvider.Lookup),
                "get_id" => nameof(NativeAudioSampleProvider.GetId),
                "get_trackIndex" => nameof(NativeAudioSampleProvider.GetTrackIndex),
                "get_channelCount" => nameof(NativeAudioSampleProvider.GetChannelCount),
                "get_sampleRate" => nameof(NativeAudioSampleProvider.GetSampleRate),
                "get_owner" => nameof(NativeAudioSampleProvider.GetOwner),
                "Dispose" => nameof(NativeAudioSampleProvider.Dispose),
                "ConsumeSampleFrames" => nameof(NativeAudioSampleProvider.ConsumeSampleFrames),
                "SetSampleFramesAvailableNativeHandler" => nameof(NativeAudioSampleProvider.SetSampleFramesAvailableNativeHandler),
                "SetSampleFramesOverflowNativeHandler" => nameof(NativeAudioSampleProvider.SetSampleFramesOverflowNativeHandler),
                "ClearSampleFramesAvailableNativeHandler" => nameof(NativeAudioSampleProvider.ClearSampleFramesAvailableNativeHandler),
                "ClearSampleFramesOverflowNativeHandler" => nameof(NativeAudioSampleProvider.ClearSampleFramesOverflowNativeHandler),
                _ => null
            };
            var lifecycleMember = original.IsPrivate && name is
                ".ctor" or "set_id" or "set_trackIndex" or "set_channelCount" or "set_sampleRate" or "set_owner";
            if (helperName == null && name != "Finalize" && !lifecycleMember) return false;

            entry.Set("signature-rejected", "audio-provider-state-contract",
                "Audio provider state requires the matched source family and retained native wrapper identity.");
            var source = original.DeclaringType;
            if (original.DeclaringModule.Mvid != new Guid("7eea753e-2c77-4819-ad69-c9242cbac7a4") ||
                source.IsValueType || source.IsInterface || source.IsAbstract || source.IsSealed ||
                source.GenericParameters.Count != 0 || source.DeclaringType != null ||
                !original.HasMethodBody || original.GenericParameters.Count != 0 ||
                original.Signature.GenericParameterCount != 0 ||
                owner.OriginalType == null || owner.ClassPointerFieldRef == null ||
                owner.OriginalType.FullName != AudioProviderType || owner.OriginalType.IsValueType ||
                owner.OriginalType.IsInterface || owner.OriginalType.GenericParameters.Count != 0 ||
                owner.OriginalType.DeclaringModule.Assembly.Name != "UnityEngine.AudioModule" ||
                owner.NewType.FullName != AudioProviderType || owner.NewType.IsValueType ||
                owner.NewType.IsInterface || owner.NewType.GenericParameters.Count != 0 ||
                owner.NewType.DeclaringModule != imports.Module ||
                resolver.ResolveRaw(source.ToTypeSignature())?.Resolve() != owner.NewType)
                return true;

            var factory = name is "Create" or "Lookup";
            var consumesFrames = name == "ConsumeSampleFrames";
            var setsHandler = name is "SetSampleFramesAvailableNativeHandler" or "SetSampleFramesOverflowNativeHandler";
            var parameterNames = name switch
            {
                "Create" => new[] { "System.UInt16", "System.UInt32" },
                "Lookup" or ".ctor" => new[] { "System.UInt32", "UnityEngine.Object", "System.UInt16" },
                "set_id" or "set_sampleRate" => new[] { "System.UInt32" },
                "set_trackIndex" or "set_channelCount" => new[] { "System.UInt16" },
                "set_owner" => new[] { "UnityEngine.Object" },
                "SetSampleFramesAvailableNativeHandler" or "SetSampleFramesOverflowNativeHandler" =>
                    new[] { AudioFramesHandlerType, "System.IntPtr" },
                _ => Array.Empty<string>()
            };
            var returnName = name switch
            {
                "Create" or "Lookup" => AudioProviderType,
                "get_id" or "get_sampleRate" or "ConsumeSampleFrames" => "System.UInt32",
                "get_trackIndex" or "get_channelCount" => "System.UInt16",
                "get_owner" => "UnityEngine.Object",
                _ => "System.Void"
            };
            if (original.IsStatic != factory || original.Signature.HasThis == factory ||
                original.Signature.ReturnType.FullName != returnName ||
                (!consumesFrames && !original.Signature.ParameterTypes.Select(parameter => parameter.FullName).SequenceEqual(parameterNames)))
                return true;

            if (consumesFrames && (original.Signature.ParameterTypes.Count != 1 ||
                original.Signature.ParameterTypes[0] is not GenericInstanceTypeSignature { IsValueType: true } array ||
                array.GenericType.FullName != InteropNativeArrayLowering.ArrayName || array.TypeArguments.Count != 1 ||
                array.TypeArguments[0] is not CorLibTypeSignature { ElementType: ElementType.R4 } ||
                InteropRestoredTypeSupport.GetAssemblyName(array.GenericType.ToTypeSignature()) != InteropNativeArrayLowering.CoreModule))
                return true;

            if (setsHandler)
            {
                var handler = original.Signature.ParameterTypes[0].Resolve();
                if (handler?.DeclaringType != source || !HasAudioHandlerSignature(handler)) return true;
            }

            if (lifecycleMember)
            {
                if (target != null) return true;
                entry.Set("native-audio-lifecycle-member", null,
                    "The private initialization/mutation path is owned by native factories and disposal; no private reflection entrypoint is restored.");
                return true;
            }

            if (name == "Finalize")
            {
                if (target != null) return true;
                // A CLR alias only releases its own native handle. Shared disposal belongs to
                // the native object's finalizer, after all aliases and native roots are gone.
                entry.Set("native-audio-finalizer", null,
                    "Provider teardown is owned by the shared native object; no CLR wrapper finalizer is restored.");
                return true;
            }

            var returnType = resolver.Resolve(original.Signature.ReturnType);
            var parameters = original.Signature.ParameterTypes.Select(parameter => resolver.Resolve(parameter)).ToArray();
            if (returnType == null || parameters.Any(parameter => parameter == null)) return true;
            if (setsHandler)
            {
                var handler = parameters[0].Resolve();
                if (!HasAudioHandlerSignature(handler) ||
                    !InteropRestoredUnmanagedDelegateSupport.IsRestored(owner.AssemblyContext.GlobalContext.GetContextForNewType(handler)))
                    return true;
            }
            if (consumesFrames)
            {
                var arrayContext = owner.AssemblyContext.GlobalContext.TryGetTypeByName(
                    InteropNativeArrayLowering.CoreModule, InteropNativeArrayLowering.ArrayName);
                if (arrayContext?.OriginalType == null || arrayContext.ClassPointerFieldRef == null ||
                    parameters[0] is not GenericInstanceTypeSignature { IsValueType: false } nativeArray ||
                    nativeArray.TypeArguments.Count != 1 ||
                    nativeArray.TypeArguments[0] is not CorLibTypeSignature { ElementType: ElementType.R4 } ||
                    nativeArray.GenericType.Resolve() != arrayContext.NewType)
                    return true;
            }
            var signature = factory
                ? MethodSignature.CreateStatic(returnType, parameters)
                : MethodSignature.CreateInstance(returnType, parameters);
            var existing = target != null;
            if (existing)
            {
                if (target.DeclaringType != owner.NewType || target.GenericParameters.Count != 0 ||
                    !SignatureComparer.Default.Equals(target.Signature, signature))
                    return true;
            }
            else
            {
                if (owner.NewType.Methods.Any(method => method.Name == original.Name &&
                    SignatureComparer.Default.Equals(method.Signature, signature)))
                    return true;
                target = new MethodDefinition(original.Name,
                    (original.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Public,
                    factory ? MethodSignature.CreateStatic(returnType) : MethodSignature.CreateInstance(returnType));
                for (var index = 0; index < parameters.Length; index++)
                    InteropMethodDeclarations.AddParameter(target, parameters[index], original.Parameters[index].Name,
                        original.Parameters[index].Definition?.Attributes ?? default);
            }

            var body = new CilMethodBody { InitializeLocals = true };
            var code = body.Instructions;
            if (!factory) code.Add(CilOpCodes.Ldarg_0);
            foreach (var parameter in target.Parameters) code.Add(CilOpCodes.Ldarg, parameter);
            // Reflection resolves return types through the interop cache that is still being generated.
            var helperType = imports.Module.DefaultImporter.ImportType(typeof(NativeAudioSampleProvider)).Resolve();
            IMethodDescriptor helper = imports.Module.DefaultImporter.ImportMethod(helperType.Methods.Single(method =>
                method.Name == helperName && method.IsPublic && method.IsStatic));
            if (consumesFrames || setsHandler) helper = ((IMethodDefOrRef)helper).MakeGenericInstanceMethod(parameters[0]);
            code.Add(CilOpCodes.Call, helper);
            if (factory || name == "get_owner")
            {
                var nativeOwner = new CilLocalVariable(helper.Signature.ReturnType);
                body.LocalVariables.Add(nativeOwner);
                code.Add(CilOpCodes.Dup);
                code.Add(CilOpCodes.Stloc, nativeOwner);
                var cast = new CilInstruction(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(
                    imports.Il2CppObjectBase_Cast.Value.MakeGenericInstanceMethod(returnType)));
                code.Add(CilOpCodes.Dup);
                code.Add(CilOpCodes.Brtrue, cast.CreateLabel());
                code.Add(CilOpCodes.Pop);
                code.Add(CilOpCodes.Ldnull);
                code.Add(CilOpCodes.Ret);
                code.Add(cast);
                // Cast creates another wrapper; retain the source handle until that alias owns its handle.
                code.Add(CilOpCodes.Ldloc, nativeOwner);
                code.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(typeof(GC).GetMethod(nameof(GC.KeepAlive))));
            }
            code.Add(CilOpCodes.Ret);
            target.ImplAttributes = MethodImplAttributes.IL | MethodImplAttributes.Managed;
            target.CilMethodBody = body;
            if (!existing) owner.NewType.Methods.Add(target);
            owner.AddRestoredManagedMethod(original, target);
            if (original.IsGetMethod) GetOrCreateProperty(original, target).GetMethod = target;
            entry.Set("native-audio-state-adapter", null,
                "Routes audio provider operations through the matched native runtime.");
            return true;
        }

        private static bool HasAudioHandlerSignature(TypeDefinition type)
        {
            if (type?.FullName != AudioFramesHandlerType ||
                !InteropRestoredUnmanagedDelegateSupport.TryDescribe(type, out var invoke, out var convention, out _) ||
                convention != (int)CallingConvention.Cdecl)
                return false;
            var signature = invoke.Signature;
            return signature.ReturnType is CorLibTypeSignature { ElementType: ElementType.Void } &&
                signature.ParameterTypes.Count == 3 &&
                signature.ParameterTypes[0] is CorLibTypeSignature { ElementType: ElementType.I } &&
                signature.ParameterTypes[1] is CorLibTypeSignature { ElementType: ElementType.U4 } &&
                signature.ParameterTypes[2] is CorLibTypeSignature { ElementType: ElementType.U4 };
        }
    }
}
