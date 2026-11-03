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
    internal static class InteropTreeViewFactoryCache
    {
        private const string ModuleName = "UnityEngine.UIElementsModule";
        private const string ControllerName = "UnityEngine.UIElements.DefaultTreeViewController`1";

        internal static bool CanStore(InteropRewriteContext context, FieldDefinition field)
        {
            var source = field.DeclaringType;
            var outer = source.DeclaringType;
            if (field.MetadataToken.ToInt32() != 0x040004CC || field.Name != "<>9__18_0" ||
                source.MetadataToken.ToInt32() != 0x02000139 || source.Name != "<>c__18`1" ||
                source.DeclaringModule.Assembly.Name != ModuleName ||
                source.DeclaringModule.Mvid != new Guid("b6e504b1-20f0-43e3-9923-7045b32ad8bb") ||
                outer?.FullName != "UnityEngine.UIElements.TreeView" || outer.DeclaringType != null ||
                outer.GenericParameters.Count != 0 || outer.IsValueType ||
                source.IsValueType || source.IsInterface || source.IsAbstract || !source.IsSealed ||
                !InteropRestoredUnmanagedDelegateSupport.IsSystemType(source.BaseType, "System.Object") ||
                source.Interfaces.Count != 0 || source.Fields.Count != 2 || source.Methods.Count != 3 ||
                source.GenericParameters.Count != 1 || !IsUnconstrained(source.GenericParameters[0]) ||
                !IsStaticStorage(field, FieldAttributes.Public | FieldAttributes.Static) ||
                field.Signature.FieldType is not GenericInstanceTypeSignature cache || cache.IsValueType ||
                cache.GenericType.FullName != "System.Func`1" || cache.TypeArguments.Count != 1 ||
                InteropRestoredTypeSupport.GetAssemblyName(cache) is not ("netstandard" or "mscorlib") ||
                cache.TypeArguments[0] is not GenericInstanceTypeSignature controller || controller.IsValueType ||
                controller.GenericType.FullName != ControllerName || controller.TypeArguments.Count != 1 ||
                !IsParameter(controller.TypeArguments[0], GenericParameterType.Type) ||
                controller.GenericType.Resolve() is not { } sourceController ||
                sourceController.DeclaringModule != source.DeclaringModule || sourceController.IsValueType ||
                sourceController.IsInterface || sourceController.IsAbstract ||
                sourceController.GenericParameters.Count != 1 ||
                context.TryGetTypeByName(ModuleName, source.FullName)?.OriginalType != null)
                return false;

            // Use existing contexts only: the restoration plan is still being constructed here.
            var nativeController = context.TryGetTypeByName(ModuleName, ControllerName);
            var nativeFunc = context.TryGetTypeByName("mscorlib", "System.Func`1");
            var nativeOuter = context.TryGetTypeByName(ModuleName, outer.FullName);
            var sourceFunc = cache.GenericType.Resolve();
            if (nativeController?.OriginalType == null || nativeFunc?.OriginalType == null ||
                nativeOuter?.OriginalType == null || nativeOuter.OriginalType.FullName != outer.FullName ||
                nativeOuter.OriginalType.GenericParameters.Count != 0 ||
                nativeController.OriginalType.FullName != ControllerName ||
                nativeController.OriginalType.DeclaringModule.Assembly.Name != ModuleName ||
                nativeController.OriginalType.IsValueType || nativeController.OriginalType.IsInterface ||
                nativeController.OriginalType.GenericParameters.Count != 1 ||
                nativeController.NewType.FullName != ControllerName || nativeController.NewType.IsValueType ||
                nativeController.NewType.IsInterface || nativeController.NewType.IsAbstract ||
                nativeController.NewType.GenericParameters.Count != 1 || nativeController.ClassPointerFieldRef == null ||
                nativeFunc.OriginalType.FullName != "System.Func`1" ||
                nativeFunc.OriginalType.DeclaringModule.Assembly.Name != "mscorlib" ||
                nativeFunc.OriginalType.GenericParameters.Count != 1 || nativeFunc.ClassPointerFieldRef == null ||
                nativeFunc.NewType.FullName != "Il2CppSystem.Func`1" ||
                sourceFunc?.GenericParameters.Count != 1 ||
                !InteropDelegateContracts.CanStoreNativeDelegate(sourceFunc, nativeFunc.NewType))
                return false;
            var invoke = nativeFunc.NewType.Methods.Single(method => method.Name == "Invoke").Signature;
            var sourceInvoke = sourceFunc.Methods.Single(method => method.Name == "Invoke").Signature;
            if (invoke.ParameterTypes.Count != 0 || !IsParameter(invoke.ReturnType, GenericParameterType.Type) ||
                sourceInvoke.ParameterTypes.Count != 0 || !IsParameter(sourceInvoke.ReturnType, GenericParameterType.Type))
                return false;

            var singleton = source.Fields.SingleOrDefault(candidate => candidate.MetadataToken.ToInt32() == 0x040004CB);
            var callback = source.Methods.SingleOrDefault(method => method.MetadataToken.ToInt32() == 0x06000A00);
            var factory = outer.Methods.SingleOrDefault(method => method.MetadataToken.ToInt32() == 0x060009F1);
            var initializer = source.Methods.SingleOrDefault(method => method.Name == ".cctor");
            var constructor = source.Methods.SingleOrDefault(method => method.Name == ".ctor");
            var helperType = source.MakeGenericInstanceType(new GenericParameterSignature(GenericParameterType.Type, 0));
            if (singleton?.Name != "<>9" ||
                !IsStaticStorage(singleton, FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.InitOnly) ||
                !SignatureComparer.Default.Equals(singleton.Signature.FieldType, helperType) ||
                callback?.Name != "<SetRootItemsInternal>b__18_0" || !HasBody(callback, true, 0, 0) ||
                !SignatureComparer.Default.Equals(callback.Signature.ReturnType, controller) ||
                callback.CilMethodBody.Instructions.Count != 2 ||
                callback.CilMethodBody.Instructions[0].OpCode.Code != CilCode.Newobj ||
                !MatchesConstructor(callback.CilMethodBody.Instructions[0].Operand, controller, 0, out var controllerConstructor) ||
                callback.CilMethodBody.Instructions[1].OpCode.Code != CilCode.Ret ||
                nativeController.TryGetMethodByUnityAssemblyMethod(controllerConstructor)?.NewMethod is not { } nativeConstructor ||
                !nativeConstructor.IsConstructor || !nativeConstructor.IsPublic || nativeConstructor.IsStatic ||
                !IsVoidSignature(nativeConstructor.Signature, true, 0, 0) ||
                constructor == null || !HasBody(constructor, true, 0, 0) ||
                !IsVoidSignature(constructor.Signature, true, 0, 0) ||
                initializer == null || !HasBody(initializer, false, 0, 0) ||
                !IsVoidSignature(initializer.Signature, false, 0, 0) ||
                initializer.CilMethodBody.Instructions.Count != 3 ||
                initializer.CilMethodBody.Instructions[0].OpCode.Code != CilCode.Newobj ||
                !MatchesConstructor(initializer.CilMethodBody.Instructions[0].Operand, helperType, 0, out var helperConstructor) ||
                helperConstructor != constructor ||
                initializer.CilMethodBody.Instructions[1].OpCode.Code != CilCode.Stsfld ||
                !MatchesField(initializer.CilMethodBody.Instructions[1].Operand, singleton, helperType) ||
                initializer.CilMethodBody.Instructions[2].OpCode.Code != CilCode.Ret ||
                factory?.Name != "SetRootItemsInternal" || !HasBody(factory, true, 1, 1) ||
                !IsUnconstrained(factory.GenericParameters[0]) ||
                !IsVoidSignature(factory.Signature, true, 1, 1) ||
                nativeOuter.TryGetMethodByUnityAssemblyMethod(factory) != null ||
                !HasCacheSequence(factory, field, singleton, callback, cache, controller))
                return false;

            var construction = constructor.CilMethodBody.Instructions;
            if (construction.Count != 4 || construction[0].OpCode.Code != CilCode.Ldarg_0 ||
                construction[1].OpCode.Code != CilCode.Call ||
                !MatchesConstructor(construction[1].Operand, source.BaseType.ToTypeSignature(), 0, out _) ||
                construction[2].OpCode.Code != CilCode.Nop || construction[3].OpCode.Code != CilCode.Ret)
                return false;

            // No address-taking, alternate stores or other callback sites may escape this lazy cache.
            foreach (var type in source.DeclaringModule.GetAllTypes())
            foreach (var method in type.Methods)
            {
                if (method.CilMethodBody == null) continue;
                foreach (var instruction in method.CilMethodBody.Instructions)
                {
                    if (instruction.Operand is IFieldDescriptor reference &&
                        (reference.Name == field.Name || reference.Name == singleton.Name) &&
                        reference.DeclaringType?.Resolve() == source)
                    {
                        var code = factory.CilMethodBody.Instructions;
                        if (instruction != code[3] && instruction != code[7] && instruction != code[11] &&
                            instruction != initializer.CilMethodBody.Instructions[1])
                            return false;
                    }
                    if (instruction.Operand is IMethodDescriptor target && target.Name == callback.Name &&
                        target.DeclaringType?.Resolve() == source && instruction != factory.CilMethodBody.Instructions[8])
                        return false;
                }
            }
            // CLR statics root the native Func wrapper per closed helper; native materialization still checks T.
            return true;
        }

        private static bool HasCacheSequence(MethodDefinition factory, FieldDefinition field, FieldDefinition singleton,
            MethodDefinition callback, GenericInstanceTypeSignature cache, GenericInstanceTypeSignature controller)
        {
            var code = factory.CilMethodBody.Instructions;
            var methodParameter = new GenericParameterSignature(GenericParameterType.Method, 0);
            var helper = field.DeclaringType.MakeGenericInstanceType(methodParameter);
            var substitution = GenericContext.FromType(helper);
            var closedCache = cache.InstantiateGenericTypes(substitution);
            var closedController = controller.InstantiateGenericTypes(substitution);
            if (factory.Signature.ParameterTypes[0] is not GenericInstanceTypeSignature items || items.IsValueType ||
                items.GenericType.FullName != "System.Collections.Generic.IList`1" || items.TypeArguments.Count != 1 ||
                InteropRestoredTypeSupport.GetAssemblyName(items) is not ("netstandard" or "mscorlib") ||
                items.TypeArguments[0] is not GenericInstanceTypeSignature item || !item.IsValueType ||
                item.GenericType.FullName != "UnityEngine.UIElements.TreeViewItemData`1" ||
                item.GenericType.Resolve()?.DeclaringModule != factory.DeclaringModule ||
                item.TypeArguments.Count != 1 || !IsParameter(item.TypeArguments[0], GenericParameterType.Method) ||
                code.Count != 15 || code[0].OpCode.Code != CilCode.Nop ||
                code[1].OpCode.Code != CilCode.Ldarg_0 || code[2].OpCode.Code != CilCode.Ldarg_1 ||
                code[3].Offset != 0x0003 || code[3].OpCode.Code != CilCode.Ldsfld ||
                !MatchesField(code[3].Operand, field, helper) || code[4].OpCode.Code != CilCode.Dup ||
                code[5].OpCode.Code != CilCode.Brtrue_S ||
                code[5].Operand is not CilInstructionLabel branch || branch.Instruction != code[12] ||
                code[6].OpCode.Code != CilCode.Pop || code[7].OpCode.Code != CilCode.Ldsfld ||
                !MatchesField(code[7].Operand, singleton, helper) || code[8].OpCode.Code != CilCode.Ldftn ||
                code[8].Operand is not IMethodDescriptor target || target is MethodSpecification ||
                target.Resolve() != callback || !SignatureComparer.Default.Equals(target.Signature, callback.Signature) ||
                !SignatureComparer.Default.Equals(target.DeclaringType?.ToTypeSignature(), helper) ||
                code[9].OpCode.Code != CilCode.Newobj ||
                !MatchesConstructor(code[9].Operand, closedCache, 2, out var delegateConstructor) ||
                delegateConstructor.DeclaringType != cache.GenericType.Resolve() ||
                code[10].OpCode.Code != CilCode.Dup || code[11].Offset != 0x001D ||
                code[11].OpCode.Code != CilCode.Stsfld || !MatchesField(code[11].Operand, field, helper) ||
                code[12].Offset != 0x0022 || code[12].OpCode.Code != CilCode.Call ||
                code[12].Operand is not IMethodDescriptor call || call is MethodSpecification ||
                call.Name != "SetRootItems" || call.Resolve() is not { } definition ||
                definition.DeclaringModule != factory.DeclaringModule ||
                definition.DeclaringType.FullName != "UnityEngine.UIElements.TreeViewHelpers`2" ||
                definition.DeclaringType.GenericParameters.Count != 2 ||
                !SignatureComparer.Default.Equals(call.Signature, definition.Signature) ||
                !IsVoidSignature(call.Signature, false, 0, 3) ||
                call.DeclaringType?.ToTypeSignature() is not GenericInstanceTypeSignature receiver ||
                receiver.IsValueType || receiver.GenericType.Resolve() != definition.DeclaringType ||
                receiver.TypeArguments.Count != 2 || !IsParameter(receiver.TypeArguments[0], GenericParameterType.Method) ||
                !SignatureComparer.Default.Equals(receiver.TypeArguments[1], closedController) ||
                code[13].OpCode.Code != CilCode.Nop || code[14].OpCode.Code != CilCode.Ret)
                return false;

            // Match raw member !0 signatures first, then substitute the outer method's !!0 exactly once.
            var arguments = call.Signature.InstantiateGenericTypes(GenericContext.FromType(receiver)).ParameterTypes;
            return arguments[0].FullName == "UnityEngine.UIElements.BaseTreeView" &&
                arguments[0].Resolve()?.DeclaringModule == factory.DeclaringModule &&
                SignatureComparer.Default.Equals(factory.DeclaringType.BaseType?.ToTypeSignature(), arguments[0]) &&
                SignatureComparer.Default.Equals(arguments[1], factory.Signature.ParameterTypes[0]) &&
                SignatureComparer.Default.Equals(arguments[2], closedCache);
        }

        private static bool IsStaticStorage(FieldDefinition field, FieldAttributes attributes) =>
            field.Attributes == attributes && field.Constant == null && field.FieldRva == null &&
            field.MarshalDescriptor == null && field.FieldOffset == null && !InteropFieldRestoration.IsThreadStatic(field) &&
            field.Signature != null && !field.Signature.HasThis;

        private static bool IsUnconstrained(GenericParameter parameter) =>
            parameter.Number == 0 && parameter.Name == "T" &&
            parameter.Attributes == GenericParameterAttributes.NonVariant && parameter.Constraints.Count == 0;

        private static bool IsParameter(TypeSignature signature, GenericParameterType kind) =>
            signature is GenericParameterSignature parameter && parameter.ParameterType == kind && parameter.Index == 0;

        private static bool HasBody(MethodDefinition method, bool hasThis, int arity, int parameters) =>
            !method.IsAbstract && !method.IsPInvokeImpl && method.IsStatic != hasThis &&
            method.GenericParameters.Count == arity && InteropDelegateContracts.HasStandardConvention(method.Signature) &&
            method.Signature.HasThis == hasThis && method.Signature.GenericParameterCount == arity &&
            method.Signature.ParameterTypes.Count == parameters && method.CilMethodBody != null &&
            method.CilMethodBody.ExceptionHandlers.Count == 0 && method.CilMethodBody.LocalVariables.Count == 0;

        private static bool IsVoidSignature(MethodSignature signature, bool hasThis, int arity, int parameters) =>
            InteropDelegateContracts.HasStandardConvention(signature) && signature.HasThis == hasThis &&
            signature.GenericParameterCount == arity && signature.ParameterTypes.Count == parameters &&
            signature.ReturnType is CorLibTypeSignature { ElementType: ElementType.Void };

        private static bool MatchesField(object operand, FieldDefinition field, TypeSignature owner) =>
            operand is IFieldDescriptor reference && reference.Resolve() == field &&
            SignatureComparer.Default.Equals(reference.Signature, field.Signature) &&
            SignatureComparer.Default.Equals(reference.DeclaringType?.ToTypeSignature(), owner);

        private static bool MatchesConstructor(object operand, TypeSignature owner, int parameters,
            out MethodDefinition definition)
        {
            definition = null;
            if (operand is not IMethodDescriptor reference || reference is MethodSpecification || reference.Name != ".ctor" ||
                !SignatureComparer.Default.Equals(reference.DeclaringType?.ToTypeSignature(), owner) ||
                reference.Resolve() is not { IsConstructor: true, IsStatic: false } constructor ||
                constructor.DeclaringType != owner.Resolve() || constructor.GenericParameters.Count != 0 ||
                !SignatureComparer.Default.Equals(reference.Signature, constructor.Signature) ||
                !IsVoidSignature(constructor.Signature, true, 0, parameters))
                return false;
            definition = constructor;
            return true;
        }
    }
}
