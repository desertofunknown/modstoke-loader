// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;

namespace Modstoke.Preloader
{
    // Public because generated delegate initializers call this from interop assemblies.
    public static class InteropAnimationCallBoundary
    {
        private const string CreateName = "UnityEngine.Animations.AnimationScriptPlayable::CreateHandleInternal_Injected";
        private const string DataName = "UnityEngine.Playables.PlayableHandle::GetJobData_Injected";
        private const string TypeName = "UnityEngine.Playables.PlayableHandle::GetJobType_Injected";
        private static readonly Func<Il2CppObjectBase, Exception> ExceptionFactory = InteropExceptionSupport.WrapNativeException;

        internal static IMethodDescriptor ResolveInitializer(MethodDefinition original, TypeDefinition delegateType,
            InteropRuntimeReferences imports)
        {
            IMethodDefOrRef resolver;
            if (InteropAnimationStreamBoundary.IsSupported(original))
                resolver = (IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(
                    typeof(InteropAnimationStreamBoundary).GetMethod(nameof(InteropAnimationStreamBoundary.Resolve)));
            else if (InteropAnimationHandleBoundary.IsSupported(original))
                resolver = (IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(
                    typeof(InteropAnimationHandleBoundary).GetMethod(nameof(InteropAnimationHandleBoundary.Resolve)));
            else if (IsSupported(original))
                resolver = (IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(
                    typeof(InteropAnimationCallBoundary).GetMethod(nameof(Resolve)));
            else
                resolver = imports.IL2CPP_ResolveICall.Value;
            return resolver.MakeGenericInstanceMethod(delegateType.ToTypeSignature());
        }

        private static bool IsSupported(MethodDefinition method)
        {
            if (!method.IsStatic || method.GenericParameters.Count != 0) return false;
            var name = method.DeclaringType.FullName + "::" + method.Name;
            var parameters = method.Signature.ParameterTypes;
            var assembly = method.DeclaringModule.Assembly.Name;
            if (name == CreateName)
                return assembly == "UnityEngine.AnimationModule" && parameters.Count == 3 &&
                    method.Signature.ReturnType.FullName == "System.Boolean" &&
                    IsHandleReference(parameters[0], "UnityEngine.Playables.PlayableGraph") &&
                    IsHandleReference(parameters[1], "UnityEngine.Playables.PlayableHandle") &&
                    parameters[2].FullName == "System.IntPtr";
            return assembly == "UnityEngine.CoreModule" && parameters.Count == 1 &&
                IsHandleReference(parameters[0], "UnityEngine.Playables.PlayableHandle") &&
                ((name == DataName && method.Signature.ReturnType.FullName == "System.IntPtr") ||
                 (name == TypeName && method.Signature.ReturnType.FullName == "System.Type"));
        }

        private static bool IsHandleReference(TypeSignature type, string name) =>
            type is ByReferenceTypeSignature reference &&
            reference.BaseType.FullName == name &&
            reference.BaseType.Scope?.GetAssembly()?.Name == "UnityEngine.CoreModule";

        public static TDelegate Resolve<TDelegate>(string icall) where TDelegate : Delegate
        {
            var name = icall switch
            {
                CreateName => nameof(CreateHandle),
                DataName => nameof(GetJobData),
                TypeName => nameof(GetJobType),
                _ => throw new NotSupportedException("Unsupported animation internal call: " + icall)
            };
            var method = typeof(InteropAnimationCallBoundary).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
            var invoke = typeof(TDelegate).GetMethod("Invoke");
            if (invoke == null || invoke.ReturnType != method.ReturnType ||
                !invoke.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(
                    method.GetParameters().Select(parameter => parameter.ParameterType)))
                throw new NotSupportedException("The animation internal-call delegate has an unexpected signature.");
            // Binding native methods here would run too early, during generated type initialization.
            return (TDelegate)method.CreateDelegate(typeof(TDelegate));
        }

        private static bool CreateHandle(IntPtr graph, IntPtr handle, IntPtr reflection) =>
            NativeAnimationCalls.CreateHandle(graph, handle, reflection, ExceptionFactory);

        private static IntPtr GetJobData(IntPtr handle) =>
            NativeAnimationCalls.GetJobData(handle, ExceptionFactory);

        private static IntPtr GetJobType(IntPtr handle)
        {
            var result = NativeAnimationCalls.GetJobType(handle, ExceptionFactory);
            try { return result?.Pointer ?? IntPtr.Zero; }
            finally { GC.KeepAlive(result); }
        }
    }
}
