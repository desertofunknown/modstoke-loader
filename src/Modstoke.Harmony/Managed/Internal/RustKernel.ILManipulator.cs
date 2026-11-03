// Copyright (c) 2026 Alex Cooper. MIT license.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using HarmonyLib.Internal.Util;
using Mono.Cecil.Cil;
using SRE = System.Reflection.Emit;

namespace HarmonyLib;

internal static unsafe partial class RustKernel
{
    internal enum ILReadAction : uint { Raw, Member, Variable, Argument, ShortArgument, Branch, Switch }
    internal enum ILResolveAction : uint { Raw, Branch, Switch }
    internal enum ILPrepareAction : uint { Raw, Variable, Branch, Switch }
    internal enum ILEmitAction : uint { Operand, None, Signature }

    [StructLayout(LayoutKind.Sequential)]
    private struct ILOperandKinds
    {
        internal int Branch, ShortBranch, Field, Method, Type, Token, Variable, ShortVariable;
        internal int Argument, ShortArgument, Switch, None, Signature;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ILOpcode
    {
        internal int Value, ReadOperand, Operand;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ILRecipe
    {
        internal ILReadAction Read;
        internal ILResolveAction Resolve;
        internal ILPrepareAction Prepare;
        internal ILEmitAction Emit;
        internal uint HandlerEnd;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ILHandlerRecipe
    {
        internal uint Target;
        internal ExceptionBlockType Block;
        internal uint CatchType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ILSwitchSpan
    {
        internal uint Start, Count;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ILLabelFixupGroup
    {
        internal uint Label, Placeholder;
        internal ILSwitchSpan Switches;
    }

    internal sealed class ILManipulatorPlan
    {
        // Dispatch operand operations by the live OperandType, not opcode value: a
        // default OpCode can share a value with Nop while carrying different flags.
        internal readonly Dictionary<OperandType, ILReadAction> ReadOperands = new();
        internal readonly Dictionary<SRE.OperandType, ILRecipe> Operands = new();
        internal readonly HashSet<short> HandlerEnds = new();
        internal readonly Dictionary<ExceptionHandlerType, ILHandlerRecipe> Handlers = new();

        internal ILReadAction Read(OperandType type) => ReadOperands.TryGetValue(type, out var action) ? action : ILReadAction.Raw;
        internal ILRecipe Operand(SRE.OperandType type) => Operands.TryGetValue(type, out var recipe) ? recipe : default;
    }

    private static class ILManipulatorExports
    {
        internal static readonly delegate* unmanaged[Cdecl]<ILOperandKinds*, uint, ILOpcode*, uint,
            ILRecipe*, uint, ILHandlerRecipe*, uint, uint> Plan =
            (delegate* unmanaged[Cdecl]<ILOperandKinds*, uint, ILOpcode*, uint,
                ILRecipe*, uint, ILHandlerRecipe*, uint, uint>)Export("mh_ilmanipulator_plan_v1");

        internal static readonly delegate* unmanaged[Cdecl]<ILLabelFixupGroup*, uint, uint,
            ILSwitchSpan*, uint, uint*, uint, uint*, uint, uint, int*, uint, uint> LabelFixups =
            (delegate* unmanaged[Cdecl]<ILLabelFixupGroup*, uint, uint,
                ILSwitchSpan*, uint, uint*, uint, uint*, uint, uint, int*, uint, uint>)
                Export("mh_ilmanipulator_label_fixups_v2");
    }

    internal static int[] PlanILLabelFixups(List<ILEmitter.Label> pendingLabels, Instruction replacement)
    {
        // These are concrete fields and Instruction operands, with no user callbacks.
        // Keep identities by reference: aliased switch arrays must share simulated state.
        // The emitter calls this at its first switch, so ordinary branch-only label
        // binding does not allocate or cross the native boundary.
        var identities = new Dictionary<Instruction, uint>(ReferenceEqualityComparer.Instance);
        var labels = new Dictionary<ILEmitter.Label, uint>(ReferenceEqualityComparer.Instance);
        var arrays = new Dictionary<Instruction[], uint>(ReferenceEqualityComparer.Instance);
        var groups = new List<ILLabelFixupGroup>();
        var spans = new List<ILSwitchSpan>();
        var references = new List<uint>();
        var switchState = new List<uint>();

        uint Identity(Instruction instruction)
        {
            if (instruction == null) return 0;
            if (!identities.TryGetValue(instruction, out var identity))
            {
                identity = checked((uint)identities.Count + 1);
                identities.Add(instruction, identity);
            }
            return identity;
        }

        foreach (var label in pendingLabels)
        {
            // Leave malformed-list/target exceptions to the original traversal, after
            // its preceding operand writes and label commits have taken place.
            if (label?.targets == null) break;
            var first = references.Count;
            var interrupted = false;
            foreach (var target in label.targets)
            {
                if (target == null)
                {
                    interrupted = true;
                    break;
                }
                if (target.Operand is not Instruction[] elements) continue;
                if (!arrays.TryGetValue(elements, out var array))
                {
                    array = (uint)spans.Count;
                    arrays.Add(elements, array);
                    spans.Add(new ILSwitchSpan { Start = (uint)switchState.Count, Count = (uint)elements.Length });
                    switchState.EnsureCapacity(checked(switchState.Count + elements.Length));
                    foreach (var element in elements) switchState.Add(Identity(element));
                }
                references.Add(array);
            }
            if (references.Count != first)
            {
                if (!labels.TryGetValue(label, out var identity))
                {
                    identity = (uint)labels.Count;
                    labels.Add(label, identity);
                }
                groups.Add(new ILLabelFixupGroup
                {
                    Label = identity,
                    Placeholder = Identity(label.instruction),
                    Switches = new ILSwitchSpan { Start = (uint)first, Count = (uint)(references.Count - first) },
                });
            }
            if (interrupted) break;
        }
        if (references.Count == 0) return Array.Empty<int>();

        var groupData = CollectionsMarshal.AsSpan(groups);
        var spanData = CollectionsMarshal.AsSpan(spans);
        var referenceData = CollectionsMarshal.AsSpan(references);
        // Native planning consumes this disposable POD workspace in place. The live
        // instruction arrays stay untouched until the emitter replays the result.
        var stateData = CollectionsMarshal.AsSpan(switchState);
        var result = new int[referenceData.Length];
        var replacementIdentity = Identity(replacement);
        fixed (ILLabelFixupGroup* nativeGroups = groupData)
        fixed (ILSwitchSpan* nativeSpans = spanData)
        fixed (uint* nativeReferences = referenceData)
        fixed (uint* nativeState = stateData)
        fixed (int* nativeResult = result)
            Check(ILManipulatorExports.LabelFixups(nativeGroups, (uint)groupData.Length, (uint)labels.Count,
                nativeSpans, (uint)spanData.Length, nativeReferences, (uint)referenceData.Length,
                nativeState, (uint)stateData.Length, replacementIdentity, nativeResult, (uint)result.Length),
                "IL switch label fixups");
        for (var i = 0; i < result.Length; i++)
            if (result[i] < -1 || (result[i] >= 0 && (uint)result[i] >= spanData[(int)referenceData[i]].Count))
                throw new RustBackendException("Modstoke Harmony received an invalid switch label fixup.");
        return result;
    }

    // One plan for the opcode metadata, not a snapshot of mutable CodeInstructions. This
    // keeps native transitions out of the per-instruction and per-transpiler paths.
    internal static ILManipulatorPlan CreateILManipulatorPlan(
        Dictionary<short, SRE.OpCode> reflection, Dictionary<short, OpCode> cecil)
    {
        var kinds = new[]
        {
            new ILOperandKinds
            {
                Branch = (int)OperandType.InlineBrTarget, ShortBranch = (int)OperandType.ShortInlineBrTarget,
                Field = (int)OperandType.InlineField, Method = (int)OperandType.InlineMethod,
                Type = (int)OperandType.InlineType, Token = (int)OperandType.InlineTok,
                Variable = (int)OperandType.InlineVar, ShortVariable = (int)OperandType.ShortInlineVar,
                Argument = (int)OperandType.InlineArg, ShortArgument = (int)OperandType.ShortInlineArg,
                Switch = (int)OperandType.InlineSwitch, None = (int)OperandType.InlineNone,
                Signature = (int)OperandType.InlineSig,
            },
            new ILOperandKinds
            {
                Branch = (int)SRE.OperandType.InlineBrTarget, ShortBranch = (int)SRE.OperandType.ShortInlineBrTarget,
                Field = (int)SRE.OperandType.InlineField, Method = (int)SRE.OperandType.InlineMethod,
                Type = (int)SRE.OperandType.InlineType, Token = (int)SRE.OperandType.InlineTok,
                Variable = (int)SRE.OperandType.InlineVar, ShortVariable = (int)SRE.OperandType.ShortInlineVar,
                Argument = -1, ShortArgument = -1,
                Switch = (int)SRE.OperandType.InlineSwitch, None = (int)SRE.OperandType.InlineNone,
                Signature = (int)SRE.OperandType.InlineSig,
            },
        };
        var opcodes = new ILOpcode[reflection.Count];
        var recipes = new ILRecipe[opcodes.Length];
        var handlers = new ILHandlerRecipe[4];
        var index = 0;
        foreach (var pair in reflection)
            opcodes[index++] = new ILOpcode
            {
                Value = pair.Key,
                ReadOperand = cecil.TryGetValue(pair.Key, out var opcode) ? (int)opcode.OperandType : -1,
                Operand = (int)pair.Value.OperandType,
            };
        fixed (ILOperandKinds* nativeKinds = kinds)
        fixed (ILOpcode* nativeOpcodes = opcodes)
        fixed (ILRecipe* nativeRecipes = recipes)
        fixed (ILHandlerRecipe* nativeHandlers = handlers)
            Check(ILManipulatorExports.Plan(nativeKinds, (uint)kinds.Length,
                nativeOpcodes, (uint)opcodes.Length, nativeRecipes, (uint)recipes.Length,
                nativeHandlers, (uint)handlers.Length), "IL manipulation plan");

        var plan = new ILManipulatorPlan();
        for (var i = 0; i < opcodes.Length; i++)
        {
            var recipe = recipes[i];
            if (recipe.Read > ILReadAction.Switch || recipe.Resolve > ILResolveAction.Switch
                || recipe.Prepare > ILPrepareAction.Switch || recipe.Emit > ILEmitAction.Signature || recipe.HandlerEnd > 1)
                throw new RustBackendException("Modstoke Harmony received an invalid IL instruction recipe.");
            if (opcodes[i].ReadOperand >= 0)
                plan.ReadOperands[(OperandType)opcodes[i].ReadOperand] = recipe.Read;
            plan.Operands[(SRE.OperandType)opcodes[i].Operand] = recipe;
            if (recipe.HandlerEnd != 0) plan.HandlerEnds.Add((short)opcodes[i].Value);
        }
        var handlerTypes = new[] { ExceptionHandlerType.Catch, ExceptionHandlerType.Filter,
            ExceptionHandlerType.Finally, ExceptionHandlerType.Fault };
        for (var i = 0; i < handlers.Length; i++)
        {
            var recipe = handlers[i];
            if (recipe.Target > 1 || recipe.CatchType > 1
                || recipe.Block < ExceptionBlockType.BeginCatchBlock || recipe.Block > ExceptionBlockType.BeginFinallyBlock)
                throw new RustBackendException("Modstoke Harmony received an invalid exception attachment recipe.");
            plan.Handlers.Add(handlerTypes[i], recipe);
        }
        return plan;
    }
}
