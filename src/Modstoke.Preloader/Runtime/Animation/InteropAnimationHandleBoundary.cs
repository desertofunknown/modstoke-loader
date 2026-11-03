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
using MethodImplAttributes = AsmResolver.PE.DotNet.Metadata.Tables.MethodImplAttributes;

namespace Modstoke.Preloader
{
    // Public because generated delegate initializers call this from interop assemblies.
    public static class InteropAnimationHandleBoundary
    {
        private const string StreamName = "UnityEngine.Animations.AnimationStream";
        private const string AnimationModule = "UnityEngine.AnimationModule";
        private const string ScenePrefix = "UnityEngine.Animations.AnimationSceneHandleUtility::";
        private const string StreamPrefix = "UnityEngine.Animations.AnimationStreamHandleUtility::";
        private static readonly Func<Il2CppObjectBase, Exception> ExceptionFactory = InteropExceptionSupport.WrapNativeException;

        internal static bool IsSupported(MethodDefinition original)
        {
            var signature = original.Signature;
            if (!original.IsStatic || original.GenericParameters.Count != 0 ||
                original.DeclaringType.GenericParameters.Count != 0 ||
                original.DeclaringModule.Assembly.Name != AnimationModule ||
                original.ImplAttributes != MethodImplAttributes.InternalCall || original.HasMethodBody ||
                !InteropDelegateContracts.HasStandardConvention(signature) ||
                signature.HasThis || signature.GenericParameterCount != 0 ||
                signature.ReturnType is not CorLibTypeSignature { ElementType: ElementType.Void })
                return false;

            var parameters = signature.ParameterTypes;
            if (parameters.Count is not (4 or 5) || !IsStreamReference(parameters[0]) ||
                !IsVoidPointer(parameters[1]) || !IsVoidPointer(parameters[2]) ||
                parameters[3] is not CorLibTypeSignature { ElementType: ElementType.I4 })
                return false;

            return (original.DeclaringType.FullName + "::" + (string)original.Name) switch
            {
                ScenePrefix + "ReadSceneIntsInternal" or ScenePrefix + "ReadSceneFloatsInternal" or
                StreamPrefix + "ReadStreamIntsInternal" or StreamPrefix + "ReadStreamFloatsInternal" =>
                    parameters.Count == 4,
                StreamPrefix + "WriteStreamIntsInternal" or StreamPrefix + "WriteStreamFloatsInternal" =>
                    parameters.Count == 5 &&
                    parameters[4] is CorLibTypeSignature { ElementType: ElementType.Boolean },
                _ => false
            };
        }

        private static bool IsStreamReference(TypeSignature type) =>
            type is ByReferenceTypeSignature { BaseType: TypeDefOrRefSignature value } &&
            value.IsValueType && value.FullName == StreamName &&
            value.Type.Scope?.GetAssembly()?.Name == AnimationModule;

        private static bool IsVoidPointer(TypeSignature type) =>
            type is PointerTypeSignature { BaseType: CorLibTypeSignature { ElementType: ElementType.Void } };

        public static TDelegate Resolve<TDelegate>(string icall) where TDelegate : Delegate
        {
            var name = icall switch
            {
                ScenePrefix + "ReadSceneIntsInternal" => nameof(ReadSceneInts),
                ScenePrefix + "ReadSceneFloatsInternal" => nameof(ReadSceneFloats),
                StreamPrefix + "ReadStreamIntsInternal" => nameof(ReadStreamInts),
                StreamPrefix + "ReadStreamFloatsInternal" => nameof(ReadStreamFloats),
                StreamPrefix + "WriteStreamIntsInternal" => nameof(WriteStreamInts),
                StreamPrefix + "WriteStreamFloatsInternal" => nameof(WriteStreamFloats),
                _ => throw new NotSupportedException("Unsupported animation handle internal call: " + icall)
            };
            var method = typeof(InteropAnimationHandleBoundary).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
            var invoke = typeof(TDelegate).GetMethod("Invoke");
            if (invoke == null || invoke.IsStatic || invoke.IsGenericMethod ||
                (invoke.CallingConvention & CallingConventions.Any) != CallingConventions.Standard ||
                (invoke.CallingConvention & CallingConventions.ExplicitThis) != 0 ||
                invoke.ReturnType != method.ReturnType ||
                !invoke.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(
                    method.GetParameters().Select(parameter => parameter.ParameterType)))
                throw new NotSupportedException("The animation handle internal-call delegate has an unexpected signature.");
            // Binding native methods here would run too early, during generated type initialization.
            return (TDelegate)method.CreateDelegate(typeof(TDelegate));
        }

        private static void ReadSceneInts(IntPtr stream, IntPtr handles, IntPtr buffer, int count) =>
            NativeAnimationCalls.ReadSceneInts(stream, handles, buffer, count, ExceptionFactory);

        private static void ReadSceneFloats(IntPtr stream, IntPtr handles, IntPtr buffer, int count) =>
            NativeAnimationCalls.ReadSceneFloats(stream, handles, buffer, count, ExceptionFactory);

        private static void ReadStreamInts(IntPtr stream, IntPtr handles, IntPtr buffer, int count) =>
            NativeAnimationCalls.ReadStreamInts(stream, handles, buffer, count, ExceptionFactory);

        private static void ReadStreamFloats(IntPtr stream, IntPtr handles, IntPtr buffer, int count) =>
            NativeAnimationCalls.ReadStreamFloats(stream, handles, buffer, count, ExceptionFactory);

        private static void WriteStreamInts(IntPtr stream, IntPtr handles, IntPtr buffer, int count, bool useMask) =>
            NativeAnimationCalls.WriteStreamInts(stream, handles, buffer, count, useMask, ExceptionFactory);

        private static void WriteStreamFloats(IntPtr stream, IntPtr handles, IntPtr buffer, int count, bool useMask) =>
            NativeAnimationCalls.WriteStreamFloats(stream, handles, buffer, count, useMask, ExceptionFactory);
    }
}
