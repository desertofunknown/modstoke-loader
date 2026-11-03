// Copyright (c) 2026 Alex Cooper. MIT license.
//! Patch-time emission plans. CLR reflection, IL objects and execution stay managed.

use crate::{boundary, disjoint, input, range, Error};
use std::ptr;

#[repr(C)]
#[derive(Clone, Copy)]
pub struct Method {
    // Private patch-context facts: args array, catch wrapper. CLR signatures remain live.
    flags: u32,
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct Step {
    action: u32,
    method: u32,
    target: u32,
}

const BEGIN: u32 = 0;
const CALL: u32 = 1;
const ASSIGN_ARGS: u32 = 2;
const RESTORE_INSTANCE: u32 = 3;
const INVOKE_RESULT_REF: u32 = 4;
const RESTORE_RESULT: u32 = 5;
const RESTORE_ARGUMENTS: u32 = 6;
const INVALID_PREFIX: u32 = 7;
const MERGE_RUN_ORIGINAL: u32 = 8;
const WRAP: u32 = 9;
const LOAD_RESULT: u32 = 10;
const STORE_RESULT: u32 = 11;
const VALIDATE_PASSTHROUGH: u32 = 12;
const STORE_EXCEPTION: u32 = 13;
const IF_VOID: u32 = 14;
const IF_NONVOID: u32 = 15;
const IF_BOOL: u32 = 16;
const DISABLE_RETHROW: u32 = 17;

fn step(steps: &mut Vec<Step>, action: u32, method: u32) {
    steps.push(Step { action, method, target: 0 });
}

fn end_branch(steps: &mut [Step], branch: usize) {
    steps[branch].target = steps.len() as u32;
}

fn call(steps: &mut Vec<Step>, method: u32, flags: u32) {
    step(steps, CALL, method);
    if flags & 1 != 0 { step(steps, ASSIGN_ARGS, method); }
    // These writebacks deliberately precede return validation and exception wrapping.
    for action in [RESTORE_INSTANCE, INVOKE_RESULT_REF, RESTORE_RESULT, RESTORE_ARGUMENTS] {
        step(steps, action, method);
    }
}

/// Stage: 0 prefix, 1 postfix, 2 finalizer. Context: result local (1), suppress exceptions (2),
/// prefix control-flow eligibility (4). Return-type branches are evaluated at execution time
/// by the CLR adapter; custom MethodInfo callbacks must not be eagerly snapshotted.
/// Summary initializes prefix eligibility (1) and finalizer rethrow state (2).
/// All buffers are caller-owned and disjoint; no plan or metadata survives this call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_manipulator_stage_v1(
    stage: u32, methods: *const Method, method_count: u32, context: u32,
    output: *mut Step, capacity: u32, written: *mut u32, summary: *mut u32,
) -> u32 {
    boundary(|| {
        disjoint(&[
            range(methods, method_count)?, range(output, capacity)?,
            range(written, 1)?, range(summary, 1)?,
        ])?;
        if stage > 2 || context & !7 != 0 { return Err(Error::Invalid); }
        let methods = unsafe { input(methods, method_count) };
        if methods.iter().any(|m| m.flags & !3 != 0) {
            return Err(Error::Invalid);
        }
        let limit = methods.len().checked_mul(32).and_then(|n| n.checked_add(1))
            .ok_or(Error::Allocation)?;
        if limit > u32::MAX as usize { return Err(Error::Capacity); }
        let mut steps = Vec::new();
        steps.try_reserve_exact(limit).map_err(|_| Error::Allocation)?;
        let mut result = 0;
        match stage {
            0 => {
                if context & 4 != 0 { result |= 1; }
                for (i, method) in methods.iter().enumerate() {
                    let i = i as u32;
                    step(&mut steps, BEGIN, i);
                    call(&mut steps, i, method.flags);
                    let void_branch = steps.len();
                    step(&mut steps, IF_VOID, i);
                    let bool_branch = steps.len();
                    step(&mut steps, IF_BOOL, i);
                    step(&mut steps, INVALID_PREFIX, i);
                    end_branch(&mut steps, bool_branch);
                    if context & 4 != 0 { step(&mut steps, MERGE_RUN_ORIGINAL, i); }
                    end_branch(&mut steps, void_branch);
                    if method.flags & 2 != 0 { step(&mut steps, WRAP, i); }
                }
            }
            1 => {
                // Stable partition: all void postfixes, result load, then chained postfixes.
                for (i, method) in methods.iter().enumerate() {
                    let i = i as u32;
                    let branch = steps.len();
                    step(&mut steps, IF_NONVOID, i);
                    step(&mut steps, BEGIN, i);
                    call(&mut steps, i, method.flags);
                    if method.flags & 2 != 0 { step(&mut steps, WRAP, i); }
                    end_branch(&mut steps, branch);
                }
                if context & 1 != 0 { step(&mut steps, LOAD_RESULT, u32::MAX); }
                for (i, method) in methods.iter().enumerate() {
                    let i = i as u32;
                    let branch = steps.len();
                    step(&mut steps, IF_VOID, i);
                    let wrap = method.flags & 2 != 0;
                    if wrap { step(&mut steps, STORE_RESULT, i); }
                    step(&mut steps, BEGIN, i);
                    if wrap { step(&mut steps, LOAD_RESULT, i); }
                    call(&mut steps, i, method.flags);
                    step(&mut steps, VALIDATE_PASSTHROUGH, i);
                    if wrap {
                        step(&mut steps, STORE_RESULT, i);
                        step(&mut steps, WRAP, i);
                        step(&mut steps, LOAD_RESULT, i);
                    }
                    end_branch(&mut steps, branch);
                }
            }
            _ => {
                result |= 2;
                for (i, method) in methods.iter().enumerate() {
                    let i = i as u32;
                    step(&mut steps, BEGIN, i);
                    call(&mut steps, i, method.flags);
                    let branch = steps.len();
                    step(&mut steps, IF_VOID, i);
                    step(&mut steps, STORE_EXCEPTION, i);
                    step(&mut steps, DISABLE_RETHROW, i);
                    end_branch(&mut steps, branch);
                    if context & 2 != 0 || method.flags & 2 != 0 { step(&mut steps, WRAP, i); }
                }
            }
        }
        if steps.len() > capacity as usize { return Err(Error::Capacity); }
        unsafe {
            if !steps.is_empty() { ptr::copy_nonoverlapping(steps.as_ptr(), output, steps.len()); }
            written.write(steps.len() as u32);
            summary.write(result);
        }
        Ok(())
    })
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct Name {
    start: u32,
    // MAX denotes null. Its failure is deferred to the original managed emission point.
    length: u32,
}

fn equals(name: &[u16], ascii: &[u8]) -> bool {
    name.len() == ascii.len() && name.iter().zip(ascii).all(|(&a, &b)| a == b as u16)
}

fn classify(name: &[u16]) -> u32 {
    // Ordering matches the emitter: field names take precedence over indexed arguments.
    if equals(name, b"__originalMethod") { return 1; }
    if equals(name, b"__instance") { return 2; }
    if equals(name, b"__args") { return 3; }
    if name.starts_with(&[95, 95, 95]) { return 4; }
    if equals(name, b"__state") { return 5; }
    if equals(name, b"__result") { return 6; }
    if equals(name, b"__resultRef") { return 7; }
    if name.starts_with(&[95, 95]) { return 8; }
    0
}

/// Classify all resolved parameter names in one patch-time call. Numeric parsing and local
/// lookup remain CLR operations so culture, overflow and local-before-index behavior survive.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_manipulator_arguments_v1(
    names: *const Name, name_count: u32, text: *const u16, text_count: u32,
    output: *mut u32, capacity: u32,
) -> u32 {
    boundary(|| {
        disjoint(&[range(names, name_count)?, range(text, text_count)?, range(output, capacity)?])?;
        if capacity != name_count { return Err(Error::Capacity); }
        let names = unsafe { input(names, name_count) };
        let text = unsafe { input(text, text_count) };
        for name in names {
            if name.length != u32::MAX && (name.start as usize).checked_add(name.length as usize)
                .is_none_or(|end| end > text.len()) { return Err(Error::Invalid); }
        }
        for (i, name) in names.iter().enumerate() {
            let kind = if name.length == u32::MAX { 9 } else {
                classify(&text[name.start as usize..name.start as usize + name.length as usize])
            };
            unsafe { output.add(i).write(kind); }
        }
        Ok(())
    })
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct TransferStep {
    action: u32,
    target: u32,
}

// Predicates are evaluated by the CLR adapter when reached, never from a signature snapshot.
const IF_NOT_OUT: u32 = 0;
const PREPARE_OUT_TYPE: u32 = 1;
const NEW_ARRAY: u32 = 2;
const READ_TYPE: u32 = 3;
const OPEN_TYPE: u32 = 4;
const DUPLICATE: u32 = 5;
const ELEMENT_INDEX: u32 = 6;
const LOAD_ARGUMENT: u32 = 7;
const IF_NOT_BY_REF: u32 = 8;
const IF_NOT_STRUCT: u32 = 9;
const LOAD_OBJECT: u32 = 10;
const LOAD_INDIRECT: u32 = 11;
const JUMP: u32 = 12;
const IF_NOT_VALUE: u32 = 13;
const BOX: u32 = 14;
const STORE_ELEMENT: u32 = 15;
const LOAD_ARRAY: u32 = 16;
const LOAD_ELEMENT: u32 = 17;
const UNBOX: u32 = 18;
const CAST: u32 = 19;
const STORE_OBJECT: u32 = 20;
const STORE_INDIRECT: u32 = 21;
const STORE_REF: u32 = 22;
const STORE_ARGUMENT: u32 = 23;
const LOAD_BOX: u32 = 24;
const IF_NOT_PRIMITIVE: u32 = 25;
const INITIALIZE_VALUE: u32 = 26;
const INITIALIZE_OBJECT: u32 = 27;
const LOAD_NULL: u32 = 28;
const BEGIN_ARGUMENTS: u32 = 29;
const NEXT_ARGUMENT: u32 = 30;
const IF_NOT_BOX_BY_REF: u32 = 31;
const IF_INSTANCE_NOT_STATIC: u32 = 32;
const READ_INSTANCE_TYPE: u32 = 33;
const IF_INSTANCE_STRUCT: u32 = 34;
const IF_NOT_PARAMETER_BY_REF: u32 = 35;
const IF_TYPED_INSTANCE: u32 = 36;
const LOAD_INSTANCE: u32 = 37;
const INSTANCE_ADDRESS: u32 = 38;
const DECLARE_INSTANCE_BOX: u32 = 39;
const STORE_INSTANCE_BOX: u32 = 40;
const LOAD_INSTANCE_BOX_ADDRESS: u32 = 41;

// Each recipe has a fixed instruction bound, independent of the original parameter count.
struct TransferRecipe {
    steps: [TransferStep; 64],
    length: usize,
}

impl TransferRecipe {
    fn new() -> Self {
        Self { steps: [TransferStep::default(); 64], length: 0 }
    }

    fn push(&mut self, action: u32) -> usize {
        let position = self.length;
        self.steps[position] = TransferStep { action, target: 0 };
        self.length += 1;
        position
    }

    fn emit(&mut self, actions: &[u32]) {
        for &action in actions { self.push(action); }
    }

    fn end(&mut self, branch: usize) {
        self.steps[branch].target = self.length as u32;
    }

    fn end_loop(&mut self, begin: usize) {
        let next = self.push(NEXT_ARGUMENT);
        self.steps[next].target = (begin + 1) as u32;
        self.end(begin);
    }
}

fn initialize_arguments(recipe: &mut TransferRecipe) {
    // Complete the out/retval pass before allocating or filling the array.
    let initialize = recipe.push(BEGIN_ARGUMENTS);
    let skip = recipe.push(IF_NOT_OUT);
    recipe.emit(&[PREPARE_OUT_TYPE, LOAD_ARGUMENT]);
    let primitive = recipe.push(IF_NOT_STRUCT);
    recipe.push(INITIALIZE_OBJECT);
    let initialized = recipe.push(JUMP);
    recipe.end(primitive);
    let reference = recipe.push(IF_NOT_PRIMITIVE);
    recipe.push(INITIALIZE_VALUE);
    let initialized_value = recipe.push(JUMP);
    recipe.end(reference);
    recipe.emit(&[LOAD_NULL, STORE_REF]);
    recipe.end(initialized_value);
    recipe.end(initialized);
    recipe.end(skip);
    recipe.end_loop(initialize);

    recipe.push(NEW_ARRAY);
    let fill = recipe.push(BEGIN_ARGUMENTS);
    recipe.emit(&[READ_TYPE, OPEN_TYPE, DUPLICATE, ELEMENT_INDEX, LOAD_ARGUMENT]);
    let by_ref = recipe.push(IF_NOT_BY_REF);
    let value = recipe.push(IF_NOT_STRUCT);
    recipe.push(LOAD_OBJECT);
    let loaded = recipe.push(JUMP);
    recipe.end(value);
    recipe.push(LOAD_INDIRECT);
    recipe.end(loaded);
    recipe.end(by_ref);
    let boxed = recipe.push(IF_NOT_VALUE);
    recipe.push(BOX);
    recipe.end(boxed);
    recipe.push(STORE_ELEMENT);
    recipe.end_loop(fill);
}

fn assign_arguments(recipe: &mut TransferRecipe) {
    let arguments = recipe.push(BEGIN_ARGUMENTS);
    recipe.push(READ_TYPE);
    let ordinary = recipe.push(IF_NOT_BY_REF);
    recipe.emit(&[OPEN_TYPE, LOAD_ARGUMENT, LOAD_ARRAY, ELEMENT_INDEX, LOAD_ELEMENT]);
    let reference = recipe.push(IF_NOT_VALUE);
    recipe.push(UNBOX);
    let primitive = recipe.push(IF_NOT_STRUCT);
    recipe.push(STORE_OBJECT);
    let stored_value = recipe.push(JUMP);
    recipe.end(primitive);
    recipe.push(STORE_INDIRECT);
    recipe.end(stored_value);
    let stored = recipe.push(JUMP);
    recipe.end(reference);
    recipe.emit(&[CAST, STORE_REF]);
    recipe.end(stored);
    let next = recipe.push(JUMP);
    recipe.end(ordinary);
    recipe.emit(&[LOAD_ARRAY, ELEMENT_INDEX, LOAD_ELEMENT]);
    let reference = recipe.push(IF_NOT_VALUE);
    recipe.push(UNBOX);
    let converted = recipe.push(JUMP);
    recipe.end(reference);
    recipe.push(CAST);
    recipe.end(converted);
    recipe.push(STORE_ARGUMENT);
    recipe.end(next);
    recipe.end_loop(arguments);
}

fn restore_boxes(recipe: &mut TransferRecipe) {
    let arguments = recipe.push(BEGIN_ARGUMENTS);
    let load = recipe.push(IF_NOT_BOX_BY_REF);
    recipe.push(LOAD_ARGUMENT);
    recipe.end(load);
    recipe.emit(&[LOAD_BOX, UNBOX]);
    let ordinary = recipe.push(IF_NOT_BOX_BY_REF);
    recipe.push(STORE_OBJECT);
    let next = recipe.push(JUMP);
    recipe.end(ordinary);
    recipe.push(STORE_ARGUMENT);
    recipe.end(next);
    recipe.end_loop(arguments);
}

fn load_instance(recipe: &mut TransferRecipe) {
    let instance = recipe.push(IF_INSTANCE_NOT_STATIC);
    recipe.push(LOAD_NULL);
    let static_done = recipe.push(JUMP);
    recipe.end(instance);
    recipe.push(READ_INSTANCE_TYPE);
    let structure = recipe.push(IF_INSTANCE_STRUCT);
    let value = recipe.push(IF_NOT_PARAMETER_BY_REF);
    recipe.push(INSTANCE_ADDRESS);
    let address_done = recipe.push(JUMP);
    recipe.end(value);
    recipe.push(LOAD_INSTANCE);
    let value_done = recipe.push(JUMP);
    recipe.end(structure);
    let typed = recipe.push(IF_TYPED_INSTANCE);
    recipe.emit(&[LOAD_INSTANCE, LOAD_OBJECT, BOX]);
    let box_done = recipe.push(IF_NOT_PARAMETER_BY_REF);
    recipe.emit(&[DECLARE_INSTANCE_BOX, STORE_INSTANCE_BOX, LOAD_INSTANCE_BOX_ADDRESS]);
    let boxed_done = recipe.push(JUMP);
    recipe.end(typed);
    recipe.push(LOAD_INSTANCE);
    let load_value = recipe.push(IF_NOT_PARAMETER_BY_REF);
    let typed_done = recipe.push(JUMP);
    recipe.end(load_value);
    recipe.push(LOAD_OBJECT);
    for branch in [static_done, address_done, value_done, box_done, boxed_done, typed_done] {
        recipe.end(branch);
    }
}

/// Return a bounded, cacheable patch-time recipe: 0 __args initialization, 1 per-patch
/// __args copyback, 2 boxed argument restoration, 3 __instance loading. Loop bounds and
/// reflection predicates remain live in the CLR adapter. No allocations, retained pointers,
/// or callbacks; the caller supplies disjoint output storage and caches the returned steps.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_manipulator_transfer_v2(
    kind: u32, output: *mut TransferStep, capacity: u32, written: *mut u32,
) -> u32 {
    boundary(|| {
        disjoint(&[range(output, capacity)?, range(written, 1)?])?;
        let mut recipe = TransferRecipe::new();
        match kind {
            0 => initialize_arguments(&mut recipe),
            1 => assign_arguments(&mut recipe),
            2 => restore_boxes(&mut recipe),
            3 => load_instance(&mut recipe),
            _ => return Err(Error::Invalid),
        }
        if recipe.length > capacity as usize { return Err(Error::Capacity); }
        unsafe {
            ptr::copy_nonoverlapping(recipe.steps.as_ptr(), output, recipe.length);
            written.write(recipe.length as u32);
        }
        Ok(())
    })
}
