// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        // Unity's managed bodies call the corlib's pure System.Math functions. The game's corlib reaches each of
        // them through a native invoke, so they bind to the CLR's functions of the same name and primitive signature.
        private bool TryTranslateClrMath(CilOpCode code, IMethodDescriptor method, out CilInstruction instruction)
        {
            instruction = null;
            if (code.Code != CilCode.Call || method is MethodSpecification || method.Signature == null ||
                method.Signature.HasThis || method.DeclaringType == null)
                return false;
            var owner = method.DeclaringType.FullName switch
            {
                "System.Math" => typeof(Math),
                "System.MathF" => typeof(MathF),
                _ => null
            };
            if (owner == null || InteropRestoredTypeSupport.GetAssemblyName(method.DeclaringType.ToTypeSignature()) is not
                ("mscorlib" or "netstandard" or "System.Runtime" or "System.Private.CoreLib"))
                return false;
            var parameters = new Type[method.Signature.ParameterTypes.Count];
            for (var index = 0; index < parameters.Length; index++)
                if ((parameters[index] = ClrPrimitive(method.Signature.ParameterTypes[index])) == null)
                    return false;
            var returnType = ClrPrimitive(method.Signature.ReturnType);
            var target = returnType == null ? null : owner.GetMethod(method.Name, BindingFlags.Public | BindingFlags.Static,
                null, parameters, null);
            if (target == null || target.ReturnType != returnType)
                return false;
            instruction = body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(target));
            return true;
        }

        private static Type ClrPrimitive(TypeSignature type) => (type as CorLibTypeSignature)?.ElementType switch
        {
            ElementType.Boolean => typeof(bool),
            ElementType.Char => typeof(char),
            ElementType.I1 => typeof(sbyte),
            ElementType.U1 => typeof(byte),
            ElementType.I2 => typeof(short),
            ElementType.U2 => typeof(ushort),
            ElementType.I4 => typeof(int),
            ElementType.U4 => typeof(uint),
            ElementType.I8 => typeof(long),
            ElementType.U8 => typeof(ulong),
            ElementType.R4 => typeof(float),
            ElementType.R8 => typeof(double),
            ElementType.I => typeof(IntPtr),
            ElementType.U => typeof(UIntPtr),
            _ => null
        };
    }
}
