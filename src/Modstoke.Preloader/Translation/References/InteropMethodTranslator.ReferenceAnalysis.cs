// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private sealed class ReferenceOrigins
        {
            public readonly HashSet<AddressOperation> Locations;
            public readonly bool Other;

            public ReferenceOrigins(IEnumerable<AddressOperation> locations, bool other = false)
            {
                Locations = new HashSet<AddressOperation>(locations);
                Other = other;
            }
        }

        private sealed class ReferenceState
        {
            public List<ReferenceOrigins> Stack;
            public ReferenceOrigins[] Locals;

            public ReferenceState Copy() => new()
            {
                Stack = new List<ReferenceOrigins>(Stack),
                Locals = (ReferenceOrigins[])Locals.Clone()
            };
        }

        private sealed class ReferenceUse
        {
            public AddressOperation Location;
            public AddressOperation CopySource;
            public AddressOperation NestedFieldRead;
            public AddressOperation NestedFieldWrite;
            public ReferenceCall Call;
        }

        private readonly Dictionary<CilInstruction, AddressOperation> referenceProducers = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<CilInstruction, ReferenceUse> referenceUses = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<CilInstruction> referencePrefixes = new(ReferenceEqualityComparer.Instance);
        private CilInstruction referenceFailure;
        private string referenceFailureDetail;

        private bool FindScopedReferenceOperations(CilMethodBody original)
        {
            var source = original.Instructions;
            var nestedFields = new Dictionary<CilInstruction, AddressOperation>(ReferenceEqualityComparer.Instance);
            var directFields = new Dictionary<CilInstruction, AddressOperation>(ReferenceEqualityComparer.Instance);
            Dictionary<CilInstruction, AddressOperation> returnedFields = null;
            var entries = GetAddressBoundaries(original);

            for (var index = 0; index < source.Count; index++)
            {
                var instruction = source[index];
                if (scopedParameters != null)
                {
                    var parameterIndex = ForwardedArgumentIndex(instruction, sourceMethod);
                    if (parameterIndex >= 0 && parameterIndex < scopedParameters.Length &&
                        scopedParameters[parameterIndex] is { } parameterLocation)
                    {
                        referenceProducers.Add(instruction, parameterLocation);
                        continue;
                    }
                }
                if ((index == 0 || !IsAddressPrefix(source[index - 1].OpCode.Code)) &&
                    TryGetRectangularReference(instruction, out var rectangular))
                {
                    referenceProducers.Add(instruction, rectangular);
                    continue;
                }
                if ((index == 0 || !IsAddressPrefix(source[index - 1].OpCode.Code)) &&
                    TryGetFieldReturn(instruction, out var returned))
                {
                    (returnedFields ??= new(ReferenceEqualityComparer.Instance)).Add(instruction, returned);
                    continue;
                }
                if (instruction.OpCode.Code == CilCode.Ldflda &&
                    (index == 0 || !IsAddressPrefix(source[index - 1].OpCode.Code)) &&
                    TryGetNestedReferenceField(instruction, out var nested))
                {
                    nestedFields.Add(instruction, nested);
                }
                if (ReferenceEquals(instruction, fixedArrayBuffer?.Producer) || addressOperations.ContainsKey(instruction) ||
                    instruction.OpCode.Code is not (CilCode.Ldflda or CilCode.Ldsflda or CilCode.Ldelema))
                    continue;
                var readOnly = index > 0 && source[index - 1].OpCode.Code == CilCode.Readonly;
                if (index > 0 && IsAddressPrefix(source[index - 1].OpCode.Code) &&
                    (!readOnly || instruction.OpCode.Code != CilCode.Ldelema || entries.Contains(instruction) ||
                     index > 1 && IsAddressPrefix(source[index - 2].OpCode.Code)))
                    continue;
                AddressOperation location;
                if (instruction.OpCode.Code == CilCode.Ldelema)
                {
                    if (!TryGetArrayAddress(instruction, out location)) continue;
                }
                else if (!TryGetFieldAddress(instruction,
                    index > 0 && !entries.Contains(instruction) ? source[index - 1] : null, out location,
                    allowGenericElement: true))
                    continue;
                location.ReadOnly |= readOnly;
                referenceProducers.Add(instruction, location);
                if (nestedFields.ContainsKey(instruction)) directFields.Add(instruction, location);
                if (readOnly) referencePrefixes.Add(source[index - 1]);
            }
            if (referenceProducers.Count == 0)
                return false;

            var positions = new Dictionary<CilInstruction, int>(ReferenceEqualityComparer.Instance);
            for (var index = 0; index < source.Count; index++) positions.Add(source[index], index);
            var localIndices = new Dictionary<CilLocalVariable, int>();
            for (var index = 0; index < original.LocalVariables.Count; index++)
                localIndices.Add(original.LocalVariables[index], index);
            var states = new Dictionary<CilInstruction, ReferenceState>(ReferenceEqualityComparer.Instance);
            var pending = new Queue<CilInstruction>();
            Merge(source[0], EntryState(0));
            foreach (var handler in original.ExceptionHandlers)
            {
                MergeLabel(handler.HandlerStart, EntryState(handler.HandlerType is
                    CilExceptionHandlerType.Exception or CilExceptionHandlerType.Filter ? 1 : 0));
                MergeLabel(handler.FilterStart, EntryState(1));
            }

            // Provenance is a finite union of producers. Locals are also checked across *all*
            // writes below, so an EH/finally assignment cannot change their storage schema.
            while (pending.Count > 0 && referenceFailure == null)
            {
                var instruction = pending.Dequeue();
                var input = states[instruction];
                var output = input.Copy();
                var code = instruction.OpCode.Code;
                if (ClearsEvaluationStack(code))
                    output.Stack.Clear();
                else if (code == CilCode.Dup && output.Stack.Count > 0)
                    output.Stack.Add(output.Stack[output.Stack.Count - 1]);
                else
                {
                    var pops = instruction.GetStackPopCount(original);
                    var pushes = instruction.GetStackPushCount();
                    if (pops < 0 || pushes < 0 || pops > output.Stack.Count)
                    {
                        Reject(instruction, "The reference flow has an unknown evaluation-stack shape.");
                        break;
                    }
                    ReferenceOrigins pushed = null;
                    if (nestedFields.TryGetValue(instruction, out var nested) && pops == 1)
                    {
                        // A source value's byref receiver can be a boxed wrapper or a native
                        // descriptor. Its provenance selects the address representation.
                        if (input.Stack[input.Stack.Count - 1] != null)
                        {
                            referenceProducers[instruction] = nested;
                            pushed = new ReferenceOrigins(new[] { nested });
                        }
                        else if (directFields.TryGetValue(instruction, out var direct))
                        {
                            referenceProducers[instruction] = direct;
                            pushed = new ReferenceOrigins(new[] { direct });
                        }
                    }
                    else if (returnedFields != null && returnedFields.TryGetValue(instruction, out var returned) && pops == 1)
                    {
                        if (input.Stack[input.Stack.Count - 1] != null)
                        {
                            referenceProducers[instruction] = returned;
                            pushed = new ReferenceOrigins(new[] { returned });
                        }
                    }
                    else if (referenceProducers.TryGetValue(instruction, out var producer))
                        pushed = new ReferenceOrigins(new[] { producer });
                    else if (code == CilCode.Ldloc && instruction.Operand is CilLocalVariable loaded)
                        pushed = input.Locals[localIndices[loaded]];
                    else if (code == CilCode.Stloc && instruction.Operand is CilLocalVariable stored && pops == 1)
                        output.Locals[localIndices[stored]] = input.Stack[input.Stack.Count - 1];
                    output.Stack.RemoveRange(output.Stack.Count - pops, pops);
                    for (var index = 0; index < pushes; index++) output.Stack.Add(pushed);
                }
                if (instruction.Operand is ICilLabel branch)
                    MergeLabel(branch, output);
                else if (instruction.Operand is IReadOnlyList<ICilLabel> branches)
                    foreach (var branchLabel in branches) MergeLabel(branchLabel, output);
                if (instruction.OpCode.FlowControl is CilFlowControl.Branch or CilFlowControl.Return or CilFlowControl.Throw ||
                    code is CilCode.Jmp or CilCode.Endfinally or CilCode.Endfilter)
                    continue;
                var next = positions[instruction] + 1;
                if (next < source.Count) Merge(source[next], output);
            }

            // Settle ancestry only after provenance converges: a later branch or loop can
            // add a readonly parent without changing an already propagated child identity.
            bool readOnlyChanged;
            do
            {
                readOnlyChanged = false;
                foreach (var pair in nestedFields)
                {
                    var child = pair.Value;
                    if (child.ReadOnly || !referenceProducers.TryGetValue(pair.Key, out var producer) ||
                        !ReferenceEquals(producer, child) ||
                        !states.TryGetValue(pair.Key, out var input) || input.Stack.Count == 0 ||
                        input.Stack[input.Stack.Count - 1] is not { } parents ||
                        !parents.Locations.Any(parent => parent.ReadOnly))
                        continue;
                    child.ReadOnly = true;
                    readOnlyChanged = true;
                }
            } while (readOnlyChanged);

            var referenceLocals = new Dictionary<CilLocalVariable, ReferenceOrigins>();
            foreach (var instruction in source)
                if (instruction.OpCode.Code == CilCode.Stloc && instruction.Operand is CilLocalVariable local &&
                    states.TryGetValue(instruction, out var state) && state.Stack.Count > 0 &&
                    state.Stack[state.Stack.Count - 1] is { } value)
                    referenceLocals[local] = referenceLocals.TryGetValue(local, out var old)
                        ? MergeReferenceOrigins(old, value) : value;

            foreach (var instruction in source)
            {
                if (referenceFailure != null) break;
                states.TryGetValue(instruction, out var input);
                if (instruction.Operand is CilLocalVariable local && referenceLocals.TryGetValue(local, out var localOrigins))
                {
                    if (local.VariableType is not ByReferenceTypeSignature localReference ||
                        !TryGetReferenceLocation(localOrigins, out var location, out _) ||
                        !SignatureComparer.Default.Equals(localReference.BaseType, location.SourceElement) ||
                        input == null || instruction.OpCode.Code is not (CilCode.Ldloc or CilCode.Stloc))
                    {
                        Reject(instruction, "A scoped reference local has mixed storage, an address-taking use or an unproven path.");
                        break;
                    }
                    var incoming = instruction.OpCode.Code == CilCode.Ldloc
                        ? input.Locals[localIndices[local]] : input.Stack.LastOrDefault();
                    if (!TryGetReferenceLocation(incoming, out var incomingLocation, out _) ||
                        !SameReferenceLocation(location, incomingLocation))
                    {
                        Reject(instruction, "Every reference-local read and assignment must have proven native-location provenance.");
                        break;
                    }
                }
                if (input == null)
                {
                    if (referenceProducers.ContainsKey(instruction))
                        Reject(instruction, "An unreachable reference producer has no proven flow.");
                    continue;
                }
                var pops = ClearsEvaluationStack(instruction.OpCode.Code)
                    ? input.Stack.Count : instruction.GetStackPopCount(original);
                if (pops < 0 || pops > input.Stack.Count)
                {
                    Reject(instruction, "A reference consumer has an unknown stack shape.");
                    break;
                }
                var consumed = input.Stack.Skip(input.Stack.Count - pops).ToArray();
                if (!consumed.Any(value => value != null))
                {
                    if (referenceProducers.TryGetValue(instruction, out var produced) && produced.IsNestedField)
                        Reject(instruction, "A nested native field requires descriptor provenance on every path.");
                    continue;
                }
                if (instruction.OpCode.Code is CilCode.Dup or CilCode.Pop || ClearsEvaluationStack(instruction.OpCode.Code) ||
                    instruction.OpCode.Code == CilCode.Stloc && instruction.Operand is CilLocalVariable stored &&
                    referenceLocals.ContainsKey(stored))
                    continue;
                if (!TryPlanReferenceUse(instruction, consumed, out var use))
                {
                    Reject(instruction, "This native location has no proven scoped consumer. Calls require exact native value slots; returned/stored references require an owner and lifetime contract and cannot escape as CLR refs or pointers.");
                    break;
                }
                var position = positions[instruction];
                if (position > 0 && IsAddressPrefix(source[position - 1].OpCode.Code))
                {
                    if (source[position - 1].OpCode.Code != CilCode.Constrained ||
                        use.Call?.ConstrainedReceiver == null || entries.Contains(instruction) ||
                        position > 1 && IsAddressPrefix(source[position - 2].OpCode.Code))
                    {
                        Reject(instruction, "Prefixed reference consumers cannot be replaced with ordinary accessor calls.");
                        break;
                    }
                    referencePrefixes.Add(source[position - 1]);
                }
                referenceUses.Add(instruction, use);
            }
            if (referenceFailure == null)
                foreach (var local in referenceLocals.Keys)
                    locals[local].VariableType = imports.Module.DefaultImporter.ImportTypeSignature(typeof(InteropReferenceLocation));
            return true;

            ReferenceState EntryState(int stackSize) => new()
            {
                Stack = Enumerable.Repeat<ReferenceOrigins>(null, stackSize).ToList(),
                Locals = new ReferenceOrigins[original.LocalVariables.Count]
            };
            void Reject(CilInstruction instruction, string detail)
            {
                referenceFailure ??= instruction;
                referenceFailureDetail ??= detail;
            }
            void MergeLabel(ICilLabel label, ReferenceState state)
            {
                if (label is CilInstructionLabel { Instruction: not null } targetLabel && positions.ContainsKey(targetLabel.Instruction))
                    Merge(targetLabel.Instruction, state);
            }
            void Merge(CilInstruction instruction, ReferenceState incoming)
            {
                if (!states.TryGetValue(instruction, out var current))
                {
                    states.Add(instruction, incoming.Copy());
                    pending.Enqueue(instruction);
                    return;
                }
                if (current.Stack.Count != incoming.Stack.Count)
                {
                    Reject(instruction, "Reference paths merge with incompatible stack heights.");
                    return;
                }
                var changed = false;
                for (var index = 0; index < current.Stack.Count; index++)
                {
                    var merged = MergeReferenceOrigins(current.Stack[index], incoming.Stack[index]);
                    changed |= !ReferenceEquals(merged, current.Stack[index]);
                    current.Stack[index] = merged;
                }
                for (var index = 0; index < current.Locals.Length; index++)
                {
                    var merged = MergeReferenceOrigins(current.Locals[index], incoming.Locals[index]);
                    changed |= !ReferenceEquals(merged, current.Locals[index]);
                    current.Locals[index] = merged;
                }
                if (changed) pending.Enqueue(instruction);
            }
        }

        private static ReferenceOrigins MergeReferenceOrigins(ReferenceOrigins left, ReferenceOrigins right)
        {
            if (ReferenceEquals(left, right)) return left;
            if (left == null) return new ReferenceOrigins(right.Locations, true);
            if (right == null) return left.Other ? left : new ReferenceOrigins(left.Locations, true);
            if ((!right.Other || left.Other) && right.Locations.IsSubsetOf(left.Locations)) return left;
            return new ReferenceOrigins(left.Locations.Concat(right.Locations), left.Other || right.Other);
        }

        private static bool SameReferenceLocation(AddressOperation left, AddressOperation right) =>
            left.IsArray == right.IsArray &&
            left.IsScopedParameter == right.IsScopedParameter &&
            left.ScopedAggregateRoot == right.ScopedAggregateRoot &&
            left.ScopedAggregateField == right.ScopedAggregateField &&
            left.RectangularRank == right.RectangularRank && left.IsNestedField == right.IsNestedField &&
            SignatureComparer.Default.Equals(left.SourceElement, right.SourceElement) &&
            SignatureComparer.Default.Equals(left.Element, right.Element) &&
            SignatureComparer.Default.Equals(left.OwnerType, right.OwnerType) &&
            SignatureComparer.Default.Equals(left.StaticOwnerType, right.StaticOwnerType) &&
            left.Getter?.FullName == right.Getter?.FullName && left.Setter?.FullName == right.Setter?.FullName &&
            left.NativeFieldPointerName == right.NativeFieldPointerName;

        private static bool TryGetReferenceLocation(ReferenceOrigins origins, out AddressOperation location, out bool readOnly)
        {
            location = null;
            readOnly = false;
            if (origins == null || origins.Other || origins.Locations.Count == 0) return false;
            foreach (var candidate in origins.Locations)
            {
                if (location != null && !SameReferenceLocation(location, candidate)) return false;
                location = candidate;
                // Equal storage shapes may merge, but every path must permit a write.
                readOnly |= candidate.ReadOnly;
            }
            return true;
        }
    }
}
