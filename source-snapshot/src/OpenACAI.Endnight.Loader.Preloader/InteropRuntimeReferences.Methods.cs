// SPDX-License-Identifier: GPL-3.0-or-later
// Runtime references adapted from Il2CppInterop RuntimeAssemblyReferences (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using Il2CppInterop.Common.Attributes;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal sealed partial class InteropRuntimeReferences
    {
        internal GenericMethodReference Il2CppRefrenceArrayctor { get; private set; }
        internal GenericMethodReference Il2CppStructArrayctor { get; private set; }
        internal GenericMethodReference Il2CppRefrenceArrayctor_size { get; private set; }
        internal GenericMethodReference Il2CppStructArrayctor_size { get; private set; }
        internal GenericMethodReference Il2CppArrayBase_get_Item { get; private set; }
        internal GenericMethodReference Il2CppArrayBase_set_Item { get; private set; }
        internal Lazy<IMethodDefOrRef> Il2CppStringArrayctor { get; private set; }
        internal Lazy<IMethodDefOrRef> Il2CppStringArrayctor_size { get; private set; }
        internal Lazy<IMethodDefOrRef> Il2CppArrayBase_get_Length { get; private set; }
        internal Lazy<IMethodDefOrRef> IL2CPP_Il2CppObjectBaseToPtr { get; private set; }
        internal Lazy<IMethodDefOrRef> IL2CPP_Il2CppObjectBaseToPtrNotNull { get; private set; }
        internal Lazy<IMethodDefOrRef> IL2CPP_Il2CppStringToManaged { get; private set; }
        internal Lazy<IMethodDefOrRef> IL2CPP_ManagedStringToIl2Cpp { get; private set; }
        internal Lazy<IMethodDefOrRef> Il2CppObjectBase_Cast { get; private set; }
        internal Lazy<IMethodDefOrRef> Il2CppObjectBase_TryCast { get; private set; }
        internal Lazy<IMethodDefOrRef> Il2CppObjectPool_Get { get; private set; }
        internal Lazy<IMethodDefOrRef> IL2CPP_ResolveICall { get; private set; }
        internal Lazy<IMethodDefOrRef> IL2CPP_il2cpp_gc_wbarrier_set_field { get; private set; }
        internal Lazy<IMethodDefOrRef> IL2CPP_FieldWriteWbarrierStub { get; private set; }
        internal Lazy<IMethodDefOrRef> IL2CPP_il2cpp_field_get_offset { get; private set; }
        internal Lazy<IMethodDefOrRef> IL2CPP_il2cpp_field_static_get_value { get; private set; }
        internal Lazy<IMethodDefOrRef> IL2CPP_il2cpp_field_static_set_value { get; private set; }
        internal Lazy<IMethodDefOrRef> IL2CPP_il2cpp_runtime_invoke { get; private set; }
        internal Lazy<IMethodDefOrRef> IL2CPP_il2cpp_runtime_class_init { get; private set; }
        internal Lazy<IMethodDefOrRef> IL2CPP_il2cpp_object_unbox { get; private set; }
        internal Lazy<IMethodDefOrRef> IL2CPP_il2cpp_value_box { get; private set; }
        internal Lazy<IMethodDefOrRef> IL2CPP_il2cpp_class_value_size { get; private set; }
        internal Lazy<IMethodDefOrRef> IL2CPP_il2cpp_object_get_class { get; private set; }
        internal Lazy<IMethodDefOrRef> IL2CPP_il2cpp_class_is_valuetype { get; private set; }
        internal Lazy<IMethodDefOrRef> Il2CppException_RaiseExceptionIfNecessary { get; private set; }
        internal Lazy<IMethodDefOrRef> IL2CPP_il2cpp_object_get_virtual_method { get; private set; }
        internal Lazy<IMethodDefOrRef> IL2CPP_GetIl2CppField { get; private set; }
        internal Lazy<IMethodDefOrRef> IL2CPP_GetIl2CppNestedType { get; private set; }
        internal Lazy<IMethodDefOrRef> IL2CPP_GetIl2CppClass { get; private set; }
        internal Lazy<IMethodDefOrRef> IL2CPP_GetIl2CppMethod { get; private set; }
        internal Lazy<IMethodDefOrRef> IL2CPP_GetIl2CppMethodByToken { get; private set; }
        internal Lazy<IMethodDefOrRef> IL2CPP_il2cpp_class_get_type { get; private set; }
        internal Lazy<IMethodDefOrRef> IL2CPP_il2cpp_class_from_type { get; private set; }
        internal Lazy<IMethodDefOrRef> IL2CPP_il2cpp_object_new { get; private set; }
        internal Lazy<IMethodDefOrRef> IL2CPP_il2cpp_method_get_from_reflection { get; private set; }
        internal Lazy<IMethodDefOrRef> IL2CPP_il2cpp_method_get_object { get; private set; }
        internal Lazy<IMethodDefOrRef> IL2CPP_PointerToValueGeneric { get; private set; }
        internal Lazy<IMethodDefOrRef> IL2CPP_RenderTypeName { get; private set; }
        internal Lazy<IMethodDefOrRef> OriginalNameAttributector { get; private set; }
        internal Lazy<IMethodDefOrRef> ObfuscatedNameAttributector { get; private set; }
        internal Lazy<IMethodDefOrRef> CallerCountAttributector { get; private set; }
        internal Lazy<IMethodDefOrRef> CachedScanResultsAttributector { get; private set; }
        internal Lazy<IMethodDefOrRef> Il2CppSystemDelegateCombine { get; private set; }
        internal Lazy<IMethodDefOrRef> Il2CppSystemDelegateRemove { get; private set; }
        internal Lazy<IMethodDefOrRef> Il2CppSystemRuntimeTypeHandleGetRuntimeTypeHandle { get; private set; }

        private unsafe void InitializeMethods()
        {
            var importer = Module.DefaultImporter;
            var voidType = importer.ImportTypeSignature(typeof(void));
            var stringType = importer.ImportTypeSignature(typeof(string));
            var stringArray = importer.ImportTypeSignature(typeof(string[]));
            var pointer = importer.ImportTypeSignature(typeof(IntPtr));
            var intType = importer.ImportTypeSignature(typeof(int));
            var uintType = importer.ImportTypeSignature(typeof(uint));
            var longType = importer.ImportTypeSignature(typeof(long));
            var boolType = importer.ImportTypeSignature(typeof(bool));
            var voidPointer = importer.ImportTypeSignature(typeof(void*));
            var voidPointers = importer.ImportTypeSignature(typeof(void**));
            var pointerReference = importer.ImportTypeSignature(typeof(IntPtr).MakeByRefType());
            var uintReference = importer.ImportTypeSignature(typeof(uint).MakeByRefType());
            var elementArray = new GenericParameterSignature(GenericParameterType.Type, 0).MakeSzArrayType();
            var methodType = new GenericParameterSignature(GenericParameterType.Method, 0);

            Il2CppRefrenceArrayctor = ArrayConstructor(Il2CppReferenceArray, voidType, elementArray);
            Il2CppStructArrayctor = ArrayConstructor(Il2CppStructArray, voidType, elementArray);
            Il2CppRefrenceArrayctor_size = ArrayConstructor(Il2CppReferenceArray, voidType, longType);
            Il2CppStructArrayctor_size = ArrayConstructor(Il2CppStructArray, voidType, longType);
            Il2CppStringArrayctor = Instance(Il2CppStringArray, ".ctor", voidType, stringArray);
            Il2CppStringArrayctor_size = Instance(Il2CppStringArray, ".ctor", voidType, longType);
            Il2CppArrayBase_get_Length = Instance(RuntimeType("InteropTypes.Arrays", "Il2CppArrayBase"), "get_Length", intType);
            Il2CppArrayBase_get_Item = new(element => new MemberReference(
                new GenericInstanceTypeSignature(Il2CppArrayBase.ToTypeDefOrRef(), false, element).ToTypeDefOrRef(),
                "get_Item", MethodSignature.CreateInstance(new GenericParameterSignature(Module, GenericParameterType.Type, 0), intType)));
            Il2CppArrayBase_set_Item = new(element => new MemberReference(
                new GenericInstanceTypeSignature(Il2CppArrayBase.ToTypeDefOrRef(), false, element).ToTypeDefOrRef(),
                "set_Item", MethodSignature.CreateInstance(Module.CorLibTypeFactory.Void, intType,
                    new GenericParameterSignature(Module, GenericParameterType.Type, 0))));

            IL2CPP_Il2CppObjectBaseToPtr = Native("Il2CppObjectBaseToPtr", pointer, Il2CppObjectBase);
            IL2CPP_Il2CppObjectBaseToPtrNotNull = Native("Il2CppObjectBaseToPtrNotNull", pointer, Il2CppObjectBase);
            IL2CPP_Il2CppStringToManaged = Native("Il2CppStringToManaged", stringType, pointer);
            IL2CPP_ManagedStringToIl2Cpp = Native("ManagedStringToIl2Cpp", pointer, stringType);
            Il2CppObjectBase_Cast = Generic(Il2CppObjectBase, "Cast", false, methodType);
            Il2CppObjectBase_TryCast = Generic(Il2CppObjectBase, "TryCast", false, methodType);
            Il2CppObjectPool_Get = Generic(Il2CppObjectPool, "Get", true, methodType, pointer);
            IL2CPP_ResolveICall = Generic(Il2Cpp, "ResolveICall", true, methodType, stringType);
            IL2CPP_il2cpp_gc_wbarrier_set_field = Native("il2cpp_gc_wbarrier_set_field", voidType, pointer, pointer, pointer);
            IL2CPP_FieldWriteWbarrierStub = Native("FieldWriteWbarrierStub", voidType, pointer, pointer, pointer);
            IL2CPP_il2cpp_field_get_offset = Native("il2cpp_field_get_offset", uintType, pointer);
            IL2CPP_il2cpp_field_static_get_value = Native("il2cpp_field_static_get_value", voidType, pointer, voidPointer);
            IL2CPP_il2cpp_field_static_set_value = Native("il2cpp_field_static_set_value", voidType, pointer, voidPointer);
            IL2CPP_il2cpp_runtime_invoke = Native("il2cpp_runtime_invoke", pointer, pointer, pointer, voidPointers, pointerReference);
            IL2CPP_il2cpp_runtime_class_init = Native("il2cpp_runtime_class_init", voidType, pointer);
            IL2CPP_il2cpp_object_unbox = Native("il2cpp_object_unbox", pointer, pointer);
            IL2CPP_il2cpp_value_box = Native("il2cpp_value_box", pointer, pointer, pointer);
            IL2CPP_il2cpp_class_value_size = Native("il2cpp_class_value_size", intType, pointer, uintReference);
            IL2CPP_il2cpp_object_get_class = Native("il2cpp_object_get_class", pointer, pointer);
            IL2CPP_il2cpp_class_is_valuetype = Native("il2cpp_class_is_valuetype", boolType, pointer);
            Il2CppException_RaiseExceptionIfNecessary = Static(Il2CppException, "RaiseExceptionIfNecessary", voidType, pointer);
            IL2CPP_il2cpp_object_get_virtual_method = Native("il2cpp_object_get_virtual_method", pointer, pointer, pointer);
            IL2CPP_GetIl2CppField = Native("GetIl2CppField", pointer, pointer, stringType);
            IL2CPP_GetIl2CppNestedType = Native("GetIl2CppNestedType", pointer, pointer, stringType);
            IL2CPP_GetIl2CppClass = Native("GetIl2CppClass", pointer, stringType, stringType, stringType);
            IL2CPP_GetIl2CppMethod = Native("GetIl2CppMethod", pointer, pointer, boolType, stringType, stringType, stringArray);
            IL2CPP_GetIl2CppMethodByToken = Native("GetIl2CppMethodByToken", pointer, pointer, intType);
            IL2CPP_il2cpp_class_get_type = Native("il2cpp_class_get_type", pointer, pointer);
            IL2CPP_il2cpp_class_from_type = Native("il2cpp_class_from_type", pointer, pointer);
            IL2CPP_il2cpp_object_new = Native("il2cpp_object_new", pointer, pointer);
            IL2CPP_il2cpp_method_get_from_reflection = Native("il2cpp_method_get_from_reflection", pointer, pointer);
            IL2CPP_il2cpp_method_get_object = Native("il2cpp_method_get_object", pointer, pointer, pointer);
            IL2CPP_PointerToValueGeneric = Generic(Il2Cpp, "PointerToValueGeneric", true, methodType, pointer, boolType, boolType);
            IL2CPP_RenderTypeName = Generic(Il2Cpp, "RenderTypeName", true, stringType, boolType);

            OriginalNameAttributector = AttributeConstructor(typeof(OriginalNameAttribute),
                Module.CorLibTypeFactory.String, Module.CorLibTypeFactory.String, Module.CorLibTypeFactory.String);
            ObfuscatedNameAttributector = AttributeConstructor(typeof(ObfuscatedNameAttribute), Module.CorLibTypeFactory.String);
            CallerCountAttributector = AttributeConstructor(typeof(CallerCountAttribute), Module.CorLibTypeFactory.Int32);
            CachedScanResultsAttributector = AttributeConstructor(typeof(CachedScanResultsAttribute));
            Il2CppSystemDelegateCombine = new(() => importer.ImportMethod(
                NativeCorlibType("Il2CppSystem.Delegate").Methods.Single(method => method.Name == "Combine" && method.Parameters.Count == 2)));
            Il2CppSystemDelegateRemove = new(() => importer.ImportMethod(
                NativeCorlibType("Il2CppSystem.Delegate").Methods.Single(method => method.Name == "Remove")));
            Il2CppSystemRuntimeTypeHandleGetRuntimeTypeHandle = new(() => importer.ImportMethod(
                new MemberReference(RuntimeReflectionHelper.ToTypeDefOrRef(), "GetRuntimeTypeHandle",
                    MethodSignature.CreateStatic(importer.ImportType(NativeCorlibType("Il2CppSystem.RuntimeTypeHandle"))
                        .ToTypeSignature(), 1))));
        }

        private Lazy<IMethodDefOrRef> AttributeConstructor(Type type, params TypeSignature[] parameters) =>
            new(() => new MemberReference(Module.DefaultImporter.ImportType(type), ".ctor",
                MethodSignature.CreateInstance(Module.CorLibTypeFactory.Void, parameters)));

        private TypeDefinition NativeCorlibType(string name) =>
            context.GetAssemblyByName("mscorlib").NewAssembly.ManifestModule.TopLevelTypes.First(type => type.FullName == name);
    }
}
