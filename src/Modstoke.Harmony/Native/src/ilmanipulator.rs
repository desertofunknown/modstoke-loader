// Copyright (c) 2026 Alex Cooper. MIT license.
//! Cached instruction recipes and switch-label fixup plans. Managed execution reads live
//! operands and runs CLR operations at their original sequence points; planning never
//! enumerates transpiler output.

use crate::{boundary, disjoint, input, range, Error};

#[derive(Clone, Copy)]
#[repr(C)]
pub struct OperandKinds {
    branch: i32,
    short_branch: i32,
    field: i32,
    method: i32,
    type_: i32,
    token: i32,
    variable: i32,
    short_variable: i32,
    argument: i32,
    short_argument: i32,
    switch: i32,
    none: i32,
    signature: i32,
}

#[derive(Clone, Copy)]
#[repr(C)]
pub struct Opcode {
    value: i32,
    read_operand: i32,
    operand: i32,
}

#[derive(Clone, Copy)]
#[repr(C)]
pub struct Recipe {
    read: u32,
    resolve: u32,
    prepare: u32,
    emit: u32,
    handler_end: u32,
}

#[derive(Clone, Copy)]
#[repr(C)]
pub struct HandlerRecipe {
    target: u32,
    block: u32,
    catch_type: u32,
}

// These action IDs are private to RustKernel.ILManipulator.cs, not CLR enum values.
const READ_RAW: u32 = 0;
const READ_MEMBER: u32 = 1;
const READ_VARIABLE: u32 = 2;
const READ_ARGUMENT: u32 = 3;
const READ_SHORT_ARGUMENT: u32 = 4;
const READ_BRANCH: u32 = 5;
const READ_SWITCH: u32 = 6;
const RESOLVE_RAW: u32 = 0;
const RESOLVE_BRANCH: u32 = 1;
const RESOLVE_SWITCH: u32 = 2;
const PREPARE_RAW: u32 = 0;
const PREPARE_VARIABLE: u32 = 1;
const PREPARE_BRANCH: u32 = 2;
const PREPARE_SWITCH: u32 = 3;
const EMIT_OPERAND: u32 = 0;
const EMIT_NONE: u32 = 1;
const EMIT_SIGNATURE: u32 = 2;

fn recipe(opcode: Opcode, cecil: OperandKinds, reflection: OperandKinds) -> Recipe {
    let read_operand = opcode.read_operand;
    let read = if [cecil.field, cecil.method, cecil.type_, cecil.token].contains(&read_operand) {
        READ_MEMBER
    } else if [cecil.variable, cecil.short_variable].contains(&read_operand) {
        READ_VARIABLE
    } else if read_operand == cecil.argument {
        READ_ARGUMENT
    } else if read_operand == cecil.short_argument {
        READ_SHORT_ARGUMENT
    } else if [cecil.branch, cecil.short_branch].contains(&read_operand) {
        READ_BRANCH
    } else if read_operand == cecil.switch {
        READ_SWITCH
    } else {
        READ_RAW
    };

    let operand = opcode.operand;
    let (resolve, prepare) = if [reflection.branch, reflection.short_branch].contains(&operand) {
        (RESOLVE_BRANCH, PREPARE_BRANCH)
    } else if operand == reflection.switch {
        (RESOLVE_SWITCH, PREPARE_SWITCH)
    } else if [reflection.variable, reflection.short_variable].contains(&operand) {
        (RESOLVE_RAW, PREPARE_VARIABLE)
    } else {
        (RESOLVE_RAW, PREPARE_RAW)
    };

    // ECMA-335 endfilter and endfinally: Cecil emits these automatically when closing
    // a marked handler. The executor must inspect block count after MarkBlockBefore.
    let handler_end = u32::from(matches!(opcode.value as u16, 0xfe11 | 0xdc));
    let emit = if operand == reflection.none {
        EMIT_NONE
    } else if operand == reflection.signature {
        EMIT_SIGNATURE
    } else {
        EMIT_OPERAND
    };
    Recipe { read, resolve, prepare, emit, handler_end }
}

/// Build all opcode and exception attachment recipes together. Inputs describe CLR metadata,
/// not instruction instances. Nothing is retained, and no callbacks cross this boundary.
/// The caller supplies accessible, disjoint buffers for the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_ilmanipulator_plan_v1(
    kinds: *const OperandKinds, kind_count: u32,
    opcodes: *const Opcode, opcode_count: u32,
    recipes: *mut Recipe, recipe_count: u32,
    handlers: *mut HandlerRecipe, handler_count: u32,
) -> u32 {
    boundary(|| {
        disjoint(&[
            range(kinds, kind_count)?, range(opcodes, opcode_count)?,
            range(recipes, recipe_count)?, range(handlers, handler_count)?,
        ])?;
        if kind_count != 2 || recipe_count != opcode_count || handler_count != 4 {
            return Err(Error::Capacity);
        }
        let kinds = unsafe { input(kinds, kind_count) };
        let opcodes = unsafe { input(opcodes, opcode_count) };
        if opcodes.iter().any(|opcode| i16::try_from(opcode.value).is_err()) {
            return Err(Error::Invalid);
        }
        // Validation precedes publication; recipe construction is allocation-free.
        for (index, &opcode) in opcodes.iter().enumerate() {
            unsafe { recipes.add(index).write(recipe(opcode, kinds[0], kinds[1])); }
        }
        // Catch, filter, finally, fault. Block IDs match Harmony's public ExceptionBlockType.
        // Target 0 is HandlerStart; target 1 is FilterStart, resolved only when executing it.
        let attachments = [
            HandlerRecipe { target: 0, block: 1, catch_type: 1 },
            HandlerRecipe { target: 1, block: 2, catch_type: 0 },
            HandlerRecipe { target: 0, block: 4, catch_type: 0 },
            HandlerRecipe { target: 0, block: 3, catch_type: 0 },
        ];
        unsafe { std::ptr::copy_nonoverlapping(attachments.as_ptr(), handlers, attachments.len()); }
        Ok(())
    })
}

#[derive(Clone, Copy)]
#[repr(C)]
pub struct SwitchSpan {
    start: u32,
    count: u32,
}

#[derive(Clone, Copy)]
#[repr(C)]
pub struct LabelFixupGroup {
    label: u32,
    placeholder: u32,
    switches: SwitchSpan,
}

fn span_bounds(span: SwitchSpan, length: usize) -> Result<std::ops::Range<usize>, Error> {
    let start = span.start as usize;
    let end = start.checked_add(span.count as usize).ok_or(Error::Invalid)?;
    if end > length { return Err(Error::Invalid); }
    Ok(start..end)
}

// Each switch array appears once in `arrays`; references retain every occurrence in
// traversal order. Simulating the shared storage preserves first-match semantics when
// multiple instructions reference the same array or a target is listed more than once.
fn label_fixups(
    groups: &[LabelFixupGroup], label_count: u32, arrays: &[SwitchSpan],
    references: &[u32], state: &mut [u32], replacement: u32,
) -> Result<Vec<i32>, Error> {
    if label_count as usize > groups.len() { return Err(Error::Invalid); }
    let mut end = 0;
    for group in groups {
        let span = span_bounds(group.switches, references.len())?;
        if group.label >= label_count || span.start != end || span.is_empty() {
            return Err(Error::Invalid);
        }
        end = span.end;
    }
    if end != references.len() { return Err(Error::Invalid); }
    end = 0;
    for array in arrays {
        let span = span_bounds(*array, state.len())?;
        if span.start != end || array.count > i32::MAX as u32 { return Err(Error::Invalid); }
        end = span.end;
    }
    if end != state.len() || references.iter().any(|&index| index as usize >= arrays.len()) {
        return Err(Error::Invalid);
    }

    // The caller's disposable identity workspace already contains the simulated array
    // elements. Validate and reserve everything before changing it; results remain
    // private until planning succeeds. The workspace contains IDs, never CLR pointers.
    let mut bound = Vec::new();
    bound.try_reserve_exact(label_count as usize).map_err(|_| Error::Allocation)?;
    bound.resize(label_count as usize, false);
    let mut result = Vec::new();
    result.try_reserve_exact(references.len()).map_err(|_| Error::Allocation)?;

    for group in groups {
        // A repeated pending label has already had its placeholder replaced by the
        // managed loop. It must search for the new instruction on later occurrences.
        let placeholder = if bound[group.label as usize] { replacement } else { group.placeholder };
        // All ranges and the exact result count were validated above. No allocation
        // or other fallible operation remains while the workspace is being changed.
        let first = group.switches.start as usize;
        let limit = first + group.switches.count as usize;
        for &array in &references[first..limit] {
            let span = arrays[array as usize];
            let start = span.start as usize;
            let elements = &mut state[start..start + span.count as usize];
            let position = elements.iter().position(|&target| target == placeholder);
            if let Some(index) = position { elements[index] = replacement; }
            result.push(position.map_or(-1, |index| index as i32));
        }
        bound[group.label as usize] = true;
    }
    Ok(result)
}

/// Plan switch-label fixups over opaque reference identities. Managed code replays
/// the selected indices at its original mutation points; no CLR objects are retained.
///
/// # Safety
/// All buffers must be accessible and disjoint for the call. Counts describe their
/// actual lengths. Topology inputs remain immutable. `state` is writable, disposable
/// workspace containing flattened instruction identities; its contents must not be
/// reused after the call. Only `output` is a result, published after successful planning.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_ilmanipulator_label_fixups_v2(
    groups: *const LabelFixupGroup, group_count: u32, label_count: u32,
    arrays: *const SwitchSpan, array_count: u32,
    references: *const u32, reference_count: u32,
    state: *mut u32, state_count: u32, replacement: u32,
    output: *mut i32, output_count: u32,
) -> u32 {
    boundary(|| {
        disjoint(&[
            range(groups, group_count)?, range(arrays, array_count)?,
            range(references, reference_count)?, range(state, state_count)?,
            range(output, output_count)?,
        ])?;
        if output_count != reference_count { return Err(Error::Capacity); }
        let state = if state_count == 0 {
            &mut []
        } else {
            // Range/disjoint validation precedes creation of the exclusive borrow.
            unsafe { std::slice::from_raw_parts_mut(state, state_count as usize) }
        };
        let result = label_fixups(
            unsafe { input(groups, group_count) }, label_count,
            unsafe { input(arrays, array_count) }, unsafe { input(references, reference_count) },
            state, replacement,
        )?;
        if !result.is_empty() {
            unsafe { std::ptr::copy_nonoverlapping(result.as_ptr(), output, result.len()); }
        }
        Ok(())
    })
}
