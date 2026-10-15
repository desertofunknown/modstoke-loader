// SPDX-License-Identifier: GPL-3.0-or-later
// Restored IL translation adapted from Il2CppInterop UnstripTranslator (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using AsmResolver;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Collections;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private readonly MethodDefinition target;
        private readonly InteropRewriteContext context;
        private readonly InteropRuntimeReferences imports;
        private readonly InteropUnstripTypeResolver resolver;
        private readonly CilInstructionCollection body;
        private readonly Dictionary<CilLocalVariable, CilLocalVariable> locals = new();
        private readonly Dictionary<CilInstruction, CilInstruction> instructions = new();
        private readonly List<(CilInstructionLabel Source, CilInstructionLabel Target)> labels = new();

        private InteropMethodTranslator(MethodDefinition target, InteropTypeContext type,
            InteropRuntimeReferences imports)
        {
            this.target = target;
            context = type.AssemblyContext.GlobalContext;
            this.imports = imports;
            resolver = new InteropUnstripTypeResolver(context, imports);
            body = target.CilMethodBody.Instructions;
        }

        internal static bool TranslateMethod(MethodDefinition original, MethodDefinition target,
            InteropTypeContext type, InteropRuntimeReferences imports)
        {
            if (original.CilMethodBody == null)
                return true;
            target.CilMethodBody = new CilMethodBody
            {
                InitializeLocals = original.CilMethodBody.InitializeLocals
            };
            return new InteropMethodTranslator(target, type, imports).Translate(original.CilMethodBody);
        }

        private bool Translate(CilMethodBody original)
        {
            foreach (var variable in original.LocalVariables)
            {
                var type = resolver.Resolve(variable.VariableType);
                if (type == null)
                    return false;
                var copy = new CilLocalVariable(type);
                target.CilMethodBody.LocalVariables.Add(copy);
                locals.Add(variable, copy);
            }

            // A source instruction can expand into several instructions, so short branches must expand too.
            original.Instructions.ExpandMacros();
            foreach (var instruction in original.Instructions)
            {
                var copy = TranslateInstruction(instruction);
                if (copy == null)
                    return false;
                instructions.Add(instruction, copy);
            }
            foreach (var pair in labels)
                pair.Target.Instruction = instructions[pair.Source.Instruction];
            return CopyExceptionHandlers(original);
        }

        private CilInstruction TranslateInstruction(CilInstruction instruction)
        {
            var operand = instruction.Operand;
            if (operand == null)
                return TranslateOperandless(instruction.OpCode);

            switch (instruction.OpCode.OperandType)
            {
                case CilOperandType.InlineField:
                    return TranslateField(instruction.OpCode, (IFieldDescriptor)operand);
                case CilOperandType.InlineMethod:
                    return TranslateMethod(instruction.OpCode, (IMethodDescriptor)operand);
                case CilOperandType.InlineType:
                    return TranslateType(instruction.OpCode, (ITypeDefOrRef)operand);
                case CilOperandType.InlineSig:
                    return null;
                case CilOperandType.InlineTok:
                    return TranslateToken(operand);
                case CilOperandType.InlineSwitch when operand is IReadOnlyList<ICilLabel> targets:
                {
                    var copies = new List<ICilLabel>(targets.Count);
                    foreach (var label in targets)
                    {
                        if (label is not CilInstructionLabel instructionLabel)
                            return null;
                        copies.Add(CopyBranchLabel(instructionLabel));
                    }
                    return body.Add(instruction.OpCode, copies);
                }
            }

            if (operand is string or Utf8String || operand.GetType().IsPrimitive)
            {
                var copy = new CilInstruction(instruction.OpCode, operand);
                body.Add(copy);
                return copy;
            }
            if (operand is Parameter parameter)
                return body.Add(instruction.OpCode, target.Parameters.GetBySignatureIndex(parameter.MethodSignatureIndex));
            if (operand is CilLocalVariable variable)
                return body.Add(instruction.OpCode, locals[variable]);
            if (operand is CilInstructionLabel branch)
                return body.Add(instruction.OpCode, CopyBranchLabel(branch));
            return null;
        }

        private CilInstructionLabel CopyBranchLabel(CilInstructionLabel source)
        {
            var copy = new CilInstructionLabel();
            labels.Add((source, copy));
            return copy;
        }

        private CilInstruction TranslateOperandless(CilOpCode code)
        {
            switch (code.Code)
            {
                case CilCode.Ldlen:
                    return body.Add(CilOpCodes.Callvirt,
                        imports.Module.DefaultImporter.ImportMethod(imports.Il2CppArrayBase_get_Length.Value));
                case CilCode.Ldelem_R4:
                    return CallArrayAccessor(false, imports.Module.CorLibTypeFactory.Single);
                case CilCode.Ldelem_R8:
                    return CallArrayAccessor(false, imports.Module.CorLibTypeFactory.Double);
                case CilCode.Stelem_R4:
                    return CallArrayAccessor(true, imports.Module.CorLibTypeFactory.Single);
                case CilCode.Stelem_R8:
                    return CallArrayAccessor(true, imports.Module.CorLibTypeFactory.Double);
                case CilCode.Ldelem_Ref:
                case CilCode.Stelem_Ref:
                case CilCode.Ldelem_I1:
                case CilCode.Ldelem_U1:
                case CilCode.Ldelem_I2:
                case CilCode.Ldelem_U2:
                case CilCode.Ldelem_I4:
                case CilCode.Ldelem_U4:
                case CilCode.Ldelem_I8:
                case CilCode.Ldelem_I:
                case >= CilCode.Stelem_I and <= CilCode.Stelem_I8:
                    // These opcodes do not identify the actual array element type.
                    return null;
                default:
                    return body.Add(code);
            }
        }

        private CilInstruction CallArrayAccessor(bool setter, TypeSignature element)
        {
            var method = setter ? imports.Il2CppArrayBase_set_Item.Get(element) : imports.Il2CppArrayBase_get_Item.Get(element);
            return body.Add(CilOpCodes.Callvirt, imports.Module.DefaultImporter.ImportMethod(method));
        }

        internal static void ReplaceBodyWithException(MethodDefinition method, InteropRuntimeReferences imports)
        {
            method.CilMethodBody = new CilMethodBody();
            var body = method.CilMethodBody.Instructions;
            var exception = imports.Module.DefaultImporter.ImportTypeSignature(typeof(NotSupportedException)).ToTypeDefOrRef();
            var constructor = new MemberReference(exception, ".ctor",
                MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void, imports.Module.CorLibTypeFactory.String));
            body.Add(CilOpCodes.Ldstr, "Method unstripping failed");
            body.Add(CilOpCodes.Newobj, constructor);
            body.Add(CilOpCodes.Throw);
            body.Add(CilOpCodes.Ret);
        }
    }
}
