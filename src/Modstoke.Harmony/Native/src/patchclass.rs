// Copyright (c) 2026 Alex Cooper. MIT license.
//! Resumable class-patching policy. Each step ends before CLR work or user callbacks.

use crate::{boundary, disjoint, input, range, Error};

#[derive(Clone, Copy)]
#[repr(C)]
pub struct ClassStep {
    action: u32,
    catch_exception: u32,
}

// Actions shared with RustKernel.PatchClass.cs.
const START: u32 = 0;
const PREPARE: u32 = 1;
const TARGETS: u32 = 2;
const REVERSE: u32 = 3;
const REVERSE_SINGLE: u32 = 4;
const BULK: u32 = 5;
const ATTRIBUTES: u32 = 6;
const CLEANUP: u32 = 7;
const CLEANUP_EMPTY: u32 = 8;
const REPORT: u32 = 9;
const DONE: u32 = 10;

fn advance(
    previous: u32, unpatch: u32, outcome: u32, target_count: u32,
) -> Result<ClassStep, Error> {
    if unpatch > 1 || outcome > 3 || previous > DONE { return Err(Error::Invalid); }
    let protected = |action| unpatch == 0 && matches!(action, TARGETS | REVERSE | REVERSE_SINGLE | BULK | ATTRIBUTES);
    let patch_action = if target_count == 0 { ATTRIBUTES } else { BULK };
    let action = if outcome & 1 != 0 {
        if !protected(previous) { return Err(Error::Invalid); }
        CLEANUP
    } else {
        match previous {
            START => if unpatch == 0 { PREPARE } else { TARGETS },
            PREPARE => if outcome & 2 != 0 { TARGETS } else { CLEANUP_EMPTY },
            TARGETS => if unpatch != 0 { patch_action } else if target_count == 1 { REVERSE_SINGLE } else { REVERSE },
            REVERSE | REVERSE_SINGLE => patch_action,
            BULK | ATTRIBUTES => if unpatch != 0 { DONE } else { CLEANUP },
            CLEANUP | CLEANUP_EMPTY => REPORT,
            REPORT | DONE => DONE,
            _ => return Err(Error::Invalid),
        }
    };
    Ok(ClassStep { action, catch_exception: u32::from(protected(action)) })
}

/// Advance after one managed stage. Outcome bits are failure and prepare-success.
/// The caller retains the actual exception, methods, and replacement list.
///
/// # Safety
/// `next` must point to writable, aligned storage for one `ClassStep`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_patchclass_advance_v1(
    previous: u32, unpatch: u32, outcome: u32, target_count: u32,
    next: *mut ClassStep,
) -> u32 {
    boundary(|| {
        range(next, 1)?;
        let planned = advance(previous, unpatch, outcome, target_count)?;
        unsafe { next.write(planned); }
        Ok(())
    })
}

const INSPECT_ALL: u32 = 1;
const DECLARED: u32 = 2;
const MANY: u32 = 3;
const SINGLE: u32 = 4;
const INVALID_ELEMENTS: u32 = 5;
const TARGETS_DONE: u32 = 6;

fn target_action(previous: u32, present: u32, elements: &[u32]) -> Result<u32, Error> {
    if present > 1 || (!elements.is_empty() && (previous != MANY || present == 0)) {
        return Err(Error::Invalid);
    }
    let mut missing = false;
    for &element in elements {
        match element {
            0 => missing = true,
            1 => {},
            _ => return Err(Error::Invalid),
        }
    }
    match previous {
        START => Ok(INSPECT_ALL),
        INSPECT_ALL => Ok(if present != 0 { DECLARED } else { MANY }),
        DECLARED | SINGLE => Ok(TARGETS_DONE),
        MANY => Ok(if present == 0 { SINGLE } else if missing { INVALID_ELEMENTS } else { TARGETS_DONE }),
        _ => Err(Error::Invalid),
    }
}

/// PatchAll takes precedence, then TargetMethods (including an empty result),
/// then TargetMethod. Presence values describe only an already enumerated result;
/// no callbacks or iterators are evaluated ahead of their original position.
///
/// # Safety
/// Nonempty `elements` must be readable and immutable for the call; `next` must be
/// writable. Both ranges must be aligned, valid for their counts, and disjoint.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_patchclass_targets_v1(
    previous: u32, present: u32, elements: *const u32, element_count: u32,
    next: *mut u32,
) -> u32 {
    boundary(|| {
        disjoint(&[range(elements, element_count)?, range(next, 1)?])?;
        let elements = unsafe { input(elements, element_count) };
        let action = target_action(previous, present, elements)?;
        unsafe { next.write(action); }
        Ok(())
    })
}

#[repr(C)]
pub struct Annotation {
    // Name present, method type present, argument types present; 8 means null metadata.
    flags: u32,
    method_type: i32,
}

#[repr(C)]
pub struct AnnotationResult {
    valid_count: u32,
    // 0 = valid, 1 = name, 2 = non-normal method type, 3 = argument types, 4 = null metadata.
    error: u32,
}

fn plan_annotations(annotations: &[Annotation]) -> Result<AnnotationResult, Error> {
    let count = u32::try_from(annotations.len()).map_err(|_| Error::Invalid)?;
    let mut planned = AnnotationResult { valid_count: count, error: 0 };
    for (index, annotation) in annotations.iter().enumerate() {
        // Invalid ABI descriptors still reject the whole call, even after a
        // semantic annotation error has fixed the valid prefix.
        if annotation.flags > 8 { return Err(Error::Invalid); }
        if planned.error != 0 { continue; }
        let error = if annotation.flags == 8 { 4 }
            else if annotation.flags & 1 != 0 { 1 }
            else if annotation.flags & 2 != 0 && annotation.method_type != 0 { 2 }
            else if annotation.flags & 4 != 0 { 3 }
            else { 0 };
        if error != 0 {
            planned = AnnotationResult { valid_count: index as u32, error };
        }
    }
    Ok(planned)
}

/// Preserve patch order and the name/type/arguments error precedence within it.
/// The caller adds the valid prefix before constructing the indicated CLR error.
///
/// # Safety
/// Nonempty `annotations` must be readable and immutable for the call; `result`
/// must be writable. Both ranges must be aligned, valid for their counts, and disjoint.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_patchclass_bulk_v1(
    annotations: *const Annotation, annotation_count: u32,
    result: *mut AnnotationResult,
) -> u32 {
    boundary(|| {
        disjoint(&[range(annotations, annotation_count)?, range(result, 1)?])?;
        let annotations = unsafe { input(annotations, annotation_count) };
        let planned = plan_annotations(annotations)?;
        unsafe { result.write(planned); }
        Ok(())
    })
}
