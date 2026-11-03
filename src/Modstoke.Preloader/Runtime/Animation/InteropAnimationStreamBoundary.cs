// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;

namespace Modstoke.Preloader
{
    // Public because generated delegate initializers call this from interop assemblies.
    public static class InteropAnimationStreamBoundary
    {
        private const string StreamName = "UnityEngine.Animations.AnimationStream";
        private const string HumanName = "UnityEngine.Animations.AnimationHumanStream";
        private const string AnimationModule = "UnityEngine.AnimationModule";
        private const string CoreModule = "UnityEngine.CoreModule";
        private const string Prefix = StreamName + "::";
        private static readonly Func<Il2CppObjectBase, Exception> ExceptionFactory = InteropExceptionSupport.WrapNativeException;

        internal static bool IsSupported(MethodDefinition original)
        {
            var signature = original.Signature;
            if (!original.IsStatic || original.GenericParameters.Count != 0 ||
                original.DeclaringType.GenericParameters.Count != 0 ||
                original.DeclaringType.FullName != StreamName ||
                original.DeclaringModule.Assembly.Name != AnimationModule ||
                !InteropDelegateContracts.HasStandardConvention(signature) ||
                signature.HasThis || signature.GenericParameterCount != 0)
                return false;

            var parameters = signature.ParameterTypes;
            if (parameters.Count == 0 || !IsValueReference(parameters[0], StreamName, AnimationModule))
                return false;

            var returnsVoid = signature.ReturnType is CorLibTypeSignature { ElementType: ElementType.Void };
            return (string)original.Name switch
            {
                "CopyAnimationStreamMotionInternal_Injected" => returnsVoid && parameters.Count == 2 &&
                    IsValueReference(parameters[1], StreamName, AnimationModule),
                "GetDeltaTime_Injected" => parameters.Count == 1 &&
                    signature.ReturnType is CorLibTypeSignature { ElementType: ElementType.R4 },
                "GetIsHumanStream_Injected" => parameters.Count == 1 &&
                    signature.ReturnType is CorLibTypeSignature { ElementType: ElementType.Boolean },
                "GetVelocity_Injected" or "SetVelocity_Injected" or
                "GetAngularVelocity_Injected" or "SetAngularVelocity_Injected" or
                "GetRootMotionPosition_Injected" => returnsVoid && parameters.Count == 2 &&
                    IsValueReference(parameters[1], "UnityEngine.Vector3", CoreModule),
                "GetRootMotionRotation_Injected" => returnsVoid && parameters.Count == 2 &&
                    IsValueReference(parameters[1], "UnityEngine.Quaternion", CoreModule),
                "GetInputStreamCount_Injected" => parameters.Count == 1 &&
                    signature.ReturnType is CorLibTypeSignature { ElementType: ElementType.I4 },
                "InternalGetInputStream_Injected" => returnsVoid && parameters.Count == 3 &&
                    parameters[1] is CorLibTypeSignature { ElementType: ElementType.I4 } &&
                    IsValueReference(parameters[2], StreamName, AnimationModule),
                "InternalGetInputWeight_Injected" => parameters.Count == 2 &&
                    parameters[1] is CorLibTypeSignature { ElementType: ElementType.I4 } &&
                    signature.ReturnType is CorLibTypeSignature { ElementType: ElementType.R4 },
                "GetHumanStream_Injected" => returnsVoid && parameters.Count == 2 &&
                    IsValueReference(parameters[1], HumanName, AnimationModule),
                "InternalReadSceneTransforms_Injected" or "InternalWriteSceneTransforms_Injected" =>
                    returnsVoid && parameters.Count == 1,
                _ => false
            };
        }

        private static bool IsValueReference(TypeSignature type, string name, string assembly) =>
            type is ByReferenceTypeSignature { BaseType: TypeDefOrRefSignature value } &&
            value.IsValueType && value.FullName == name && value.Type.Scope?.GetAssembly()?.Name == assembly;

        public static TDelegate Resolve<TDelegate>(string icall) where TDelegate : Delegate
        {
            var name = icall switch
            {
                Prefix + "CopyAnimationStreamMotionInternal_Injected" => nameof(CopyMotion),
                Prefix + "GetDeltaTime_Injected" => nameof(GetDeltaTime),
                Prefix + "GetIsHumanStream_Injected" => nameof(GetIsHuman),
                Prefix + "GetVelocity_Injected" => nameof(GetVelocity),
                Prefix + "SetVelocity_Injected" => nameof(SetVelocity),
                Prefix + "GetAngularVelocity_Injected" => nameof(GetAngularVelocity),
                Prefix + "SetAngularVelocity_Injected" => nameof(SetAngularVelocity),
                Prefix + "GetRootMotionPosition_Injected" => nameof(GetRootMotionPosition),
                Prefix + "GetRootMotionRotation_Injected" => nameof(GetRootMotionRotation),
                Prefix + "GetInputStreamCount_Injected" => nameof(GetInputCount),
                Prefix + "InternalGetInputStream_Injected" => nameof(GetInput),
                Prefix + "InternalGetInputWeight_Injected" => nameof(GetInputWeight),
                Prefix + "GetHumanStream_Injected" => nameof(GetHuman),
                Prefix + "InternalReadSceneTransforms_Injected" => nameof(ReadSceneTransforms),
                Prefix + "InternalWriteSceneTransforms_Injected" => nameof(WriteSceneTransforms),
                _ => throw new NotSupportedException("Unsupported animation stream internal call: " + icall)
            };
            var method = typeof(InteropAnimationStreamBoundary).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
            var invoke = typeof(TDelegate).GetMethod("Invoke");
            if (invoke == null || invoke.IsStatic || invoke.IsGenericMethod ||
                (invoke.CallingConvention & CallingConventions.Any) != CallingConventions.Standard ||
                (invoke.CallingConvention & CallingConventions.ExplicitThis) != 0 ||
                invoke.ReturnType != method.ReturnType ||
                !invoke.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(
                    method.GetParameters().Select(parameter => parameter.ParameterType)))
                throw new NotSupportedException("The animation stream internal-call delegate has an unexpected signature.");
            // Binding native methods here would run too early, during generated type initialization.
            return (TDelegate)method.CreateDelegate(typeof(TDelegate));
        }

        private static void CopyMotion(IntPtr stream, IntPtr other) =>
            NativeAnimationCalls.CopyStreamMotion(stream, other, ExceptionFactory);

        private static float GetDeltaTime(IntPtr stream) =>
            NativeAnimationCalls.GetStreamDeltaTime(stream, ExceptionFactory);

        private static bool GetIsHuman(IntPtr stream) =>
            NativeAnimationCalls.GetStreamIsHuman(stream, ExceptionFactory);

        private static void GetVelocity(IntPtr stream, IntPtr output) =>
            NativeAnimationCalls.GetStreamVelocity(stream, output, ExceptionFactory);

        private static void SetVelocity(IntPtr stream, IntPtr value) =>
            NativeAnimationCalls.SetStreamVelocity(stream, value, ExceptionFactory);

        private static void GetAngularVelocity(IntPtr stream, IntPtr output) =>
            NativeAnimationCalls.GetStreamAngularVelocity(stream, output, ExceptionFactory);

        private static void SetAngularVelocity(IntPtr stream, IntPtr value) =>
            NativeAnimationCalls.SetStreamAngularVelocity(stream, value, ExceptionFactory);

        private static void GetRootMotionPosition(IntPtr stream, IntPtr output) =>
            NativeAnimationCalls.GetStreamRootMotionPosition(stream, output, ExceptionFactory);

        private static void GetRootMotionRotation(IntPtr stream, IntPtr output) =>
            NativeAnimationCalls.GetStreamRootMotionRotation(stream, output, ExceptionFactory);

        private static int GetInputCount(IntPtr stream) =>
            NativeAnimationCalls.GetStreamInputCount(stream, ExceptionFactory);

        private static void GetInput(IntPtr stream, int index, IntPtr output) =>
            NativeAnimationCalls.GetStreamInput(stream, index, output, ExceptionFactory);

        private static float GetInputWeight(IntPtr stream, int index) =>
            NativeAnimationCalls.GetStreamInputWeight(stream, index, ExceptionFactory);

        private static void GetHuman(IntPtr stream, IntPtr output) =>
            NativeAnimationCalls.GetStreamHuman(stream, output, ExceptionFactory);

        private static void ReadSceneTransforms(IntPtr stream) =>
            NativeAnimationCalls.ReadStreamSceneTransforms(stream, ExceptionFactory);

        private static void WriteSceneTransforms(IntPtr stream) =>
            NativeAnimationCalls.WriteStreamSceneTransforms(stream, ExceptionFactory);
    }
}
