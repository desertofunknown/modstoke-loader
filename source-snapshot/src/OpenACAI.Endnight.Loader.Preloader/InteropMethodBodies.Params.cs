// SPDX-License-Identifier: GPL-3.0-or-later
// Params initialization adapted from Il2CppInterop Pass50GenerateMethods (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal sealed partial class InteropMethodBodies
    {
        private void InitializeParamsArrays()
        {
            for (var index = 0; index < original.Parameters.Count; index++)
            {
                var parameter = original.Parameters[index];
                if (!InteropMethodDeclarations.IsParamsArray(parameter))
                    continue;

                var rewritten = target.Parameters[index];
                var next = new CilInstructionLabel();
                body.Add(CilOpCodes.Ldarg, rewritten);
                body.Add(CilOpCodes.Brtrue, next);
                var element = ((SzArrayTypeSignature)parameter.ParameterType).BaseType;
                if (element is GenericParameterSignature)
                {
                    EmitEmptyGenericArray((GenericInstanceTypeSignature)rewritten.ParameterType);
                }
                else
                {
                    body.Add(CilOpCodes.Ldc_I4_0);
                    body.Add(CilOpCodes.Conv_I8);
                    IMethodDefOrRef constructor;
                    if (element.FullName == "System.String")
                        constructor = imports.Il2CppStringArrayctor_size.Value;
                    else
                    {
                        var rewrittenElement = ((GenericInstanceTypeSignature)rewritten.ParameterType).TypeArguments[0];
                        constructor = InteropTypeReferences.IsValueType(element)
                            ? imports.Il2CppStructArrayctor_size.Get(rewrittenElement)
                            : imports.Il2CppRefrenceArrayctor_size.Get(rewrittenElement);
                    }
                    body.Add(CilOpCodes.Newobj, imports.Module.DefaultImporter.ImportMethod(constructor));
                }
                body.Add(CilOpCodes.Starg, rewritten);
                next.Instruction = body.Add(CilOpCodes.Nop);
            }
        }

        private void EmitEmptyGenericArray(GenericInstanceTypeSignature arrayType)
        {
            var module = imports.Module;
            var corlib = module.CorLibTypeFactory;
            var element = arrayType.TypeArguments[0];
            var store = imports.Il2CppClassPointerStore.MakeGenericInstanceType(element).ToTypeDefOrRef();
            body.Add(CilOpCodes.Ldsfld, new MemberReference(module.DefaultImporter.ImportType(store),
                "NativeClassPtr", new FieldSignature(corlib.IntPtr)));
            body.Add(CilOpCodes.Dup);
            var allocate = new CilInstructionLabel();
            body.Add(CilOpCodes.Brtrue, allocate);
            body.Add(CilOpCodes.Pop);
            body.Add(CilOpCodes.Ldstr, "Cannot initialize a params array without a native element class.");
            var exceptionType = module.DefaultImporter.ImportTypeSignature(typeof(ArgumentException)).ToTypeDefOrRef();
            body.Add(CilOpCodes.Newobj, new MemberReference(exceptionType, ".ctor",
                MethodSignature.CreateInstance(corlib.Void, corlib.String)));
            body.Add(CilOpCodes.Throw);

            allocate.Instruction = body.Add(CilOpCodes.Ldc_I4_0);
            body.Add(CilOpCodes.Conv_U8);
            body.Add(CilOpCodes.Call, module.DefaultImporter.ImportMethod(new MemberReference(
                imports.Il2Cpp.ToTypeDefOrRef(), "il2cpp_array_new",
                MethodSignature.CreateStatic(corlib.IntPtr, corlib.IntPtr, corlib.UInt64))));
            // Array constructors reject null allocations; the generic wrapping helper otherwise accepts them.
            body.Add(CilOpCodes.Dup);
            var wrap = new CilInstructionLabel();
            body.Add(CilOpCodes.Brtrue, wrap);
            body.Add(CilOpCodes.Pop);
            var nullException = module.DefaultImporter.ImportTypeSignature(typeof(NullReferenceException)).ToTypeDefOrRef();
            body.Add(CilOpCodes.Newobj, new MemberReference(nullException, ".ctor", MethodSignature.CreateInstance(corlib.Void)));
            body.Add(CilOpCodes.Throw);
            wrap.Instruction = body.Add(CilOpCodes.Nop);

            // The return signature belongs to ArrayBase<T>, so its argument is !0, not the caller's !!n.
            var returnType = imports.Il2CppArrayBase.MakeGenericInstanceType(
                new GenericParameterSignature(GenericParameterType.Type, 0));
            body.Add(CilOpCodes.Call, module.DefaultImporter.ImportMethod(new MemberReference(
                arrayType.ToTypeDefOrRef(), "WrapNativeGenericArrayPointer",
                MethodSignature.CreateStatic(returnType, corlib.IntPtr))));
        }
    }
}
