// SPDX-License-Identifier: GPL-3.0-or-later
// Conversion generation adapted from Il2CppInterop Pass60 (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static partial class InteropConversions
    {
        private const MethodAttributes OperatorAttributes = MethodAttributes.Static | MethodAttributes.Public |
            MethodAttributes.SpecialName | MethodAttributes.HideBySig;

        internal static void Generate(InteropRewriteContext context)
        {
            var assembly = context.GetAssemblyByName("mscorlib");
            var nativeString = assembly.GetTypeByName("System.String").NewType;
            var nativeObject = assembly.GetTypeByName("System.Object").NewType;
            GenerateStringConversions(assembly, nativeString, nativeObject);
            GenerateDelegateConversions(context);
            GeneratePrimitiveConversions(assembly, nativeObject);
        }

        private static void GenerateStringConversions(InteropAssemblyContext assembly,
            TypeDefinition nativeString, TypeDefinition nativeObject)
        {
            var imports = assembly.Imports;
            var corlib = imports.Module.CorLibTypeFactory;
            var fromManaged = AddOperator(nativeString, "op_Implicit", nativeString.ToTypeSignature(), corlib.String);
            var body = fromManaged.CilMethodBody.Instructions;
            var convert = new CilInstructionLabel();
            body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Dup);
            body.Add(CilOpCodes.Brtrue_S, convert);
            body.Add(CilOpCodes.Ret);
            convert.Instruction = body.Add(CilOpCodes.Nop);
            body.Add(CilOpCodes.Call, imports.IL2CPP_ManagedStringToIl2Cpp.Value);
            body.Add(CilOpCodes.Newobj, new MemberReference(nativeString, ".ctor",
                MethodSignature.CreateInstance(corlib.Void, corlib.IntPtr)));
            body.Add(CilOpCodes.Ret);

            var toObject = AddOperator(nativeObject, "op_Implicit", nativeObject.ToTypeSignature(), corlib.String);
            body = toObject.CilMethodBody.Instructions;
            body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Call, fromManaged);
            body.Add(CilOpCodes.Ret);

            var toManaged = AddOperator(nativeString, "op_Implicit", corlib.String, nativeString.ToTypeSignature());
            body = toManaged.CilMethodBody.Instructions;
            convert = new CilInstructionLabel();
            body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Call, imports.IL2CPP_Il2CppObjectBaseToPtr.Value);
            body.Add(CilOpCodes.Dup);
            body.Add(CilOpCodes.Brtrue_S, convert);
            body.Add(CilOpCodes.Pop);
            body.Add(CilOpCodes.Ldnull);
            body.Add(CilOpCodes.Ret);
            convert.Instruction = body.Add(CilOpCodes.Nop);
            body.Add(CilOpCodes.Call, imports.IL2CPP_Il2CppStringToManaged.Value);
            body.Add(CilOpCodes.Ret);
        }

        private static void GeneratePrimitiveConversions(InteropAssemblyContext assembly, TypeDefinition nativeObject)
        {
            var corlib = assembly.Imports.Module.CorLibTypeFactory;
            TypeSignature[] primitives =
            {
                corlib.SByte, corlib.Byte, corlib.Int16, corlib.UInt16, corlib.Int32, corlib.UInt32,
                corlib.Int64, corlib.UInt64, corlib.Single, corlib.Double, corlib.Char, corlib.Boolean
            };
            foreach (var primitive in primitives)
            {
                var nativeType = assembly.GetTypeByName(primitive.FullName).NewType;
                var method = AddOperator(nativeObject, "op_Implicit", nativeObject.ToTypeSignature());
                InteropMethodDeclarations.AddParameter(method, primitive, "value", 0);
                var value = new CilLocalVariable(nativeType.ToTypeSignature());
                method.CilMethodBody.LocalVariables.Add(value);
                var body = method.CilMethodBody.Instructions;
                body.Add(CilOpCodes.Ldloca, value);
                body.Add(CilOpCodes.Initobj, nativeType);
                body.Add(CilOpCodes.Ldloca, value);
                body.Add(CilOpCodes.Ldarg_0);
                body.Add(CilOpCodes.Stfld, nativeType.Fields.Single(field => field.Name == "m_value"));
                body.Add(CilOpCodes.Ldloca_S, value);
                body.Add(CilOpCodes.Call, nativeType.Methods.Single(candidate => candidate.Name == "BoxIl2CppObject"));
                body.Add(CilOpCodes.Ret);
            }
        }

        private static MethodDefinition AddOperator(TypeDefinition owner, string name,
            TypeSignature returnType, params TypeSignature[] parameters)
        {
            var method = new MethodDefinition(name, OperatorAttributes,
                MethodSignature.CreateStatic(returnType, parameters))
            {
                CilMethodBody = new CilMethodBody()
            };
            owner.Methods.Add(method);
            return method;
        }
    }
}
