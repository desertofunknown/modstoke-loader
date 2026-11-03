// Copyright (c) 2026 Alex Cooper. MIT license.
//! Opcode candidate batches and scalar-label jump topology. CLR equality stays managed.

use crate::{boundary, disjoint, input, range, Error};

#[repr(C)]
pub struct OpcodeCandidate {
    value: i32,
    present: u32,
}

/// Entries are in search order. A null instruction is a barrier, not a rejection:
/// managed matching must decide whether accessing it throws.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_codematch_candidates_v1(
    candidates: *const OpcodeCandidate, candidate_count: u32,
    allowed: *const i16, allowed_count: u32, required: i32, found: *mut u32,
) -> u32 {
    boundary(|| {
        disjoint(&[
            range(candidates, candidate_count)?, range(allowed, allowed_count)?, range(found, 1)?,
        ])?;
        if required != i32::MIN && i16::try_from(required).is_err() {
            return Err(Error::Invalid);
        }
        let candidates = unsafe { input(candidates, candidate_count) };
        let allowed = unsafe { input(allowed, allowed_count) };
        if candidates.iter().any(|c| c.present > 1 || i16::try_from(c.value).is_err()) {
            return Err(Error::Invalid);
        }
        let result = candidates.iter().position(|c| {
            c.present == 0 ||
                ((allowed.is_empty() || allowed.contains(&(c.value as i16))) &&
                 (required == i32::MIN || required == c.value))
        }).map_or(candidate_count, |index| index as u32);
        unsafe { found.write(result); }
        Ok(())
    })
}

#[repr(C)]
pub struct JumpSource {
    operand_label: i32,
    // 0: valid, 2: null codes, 3: invalid index, 4: null instruction.
    state: u32,
}

#[repr(C)]
pub struct JumpNode {
    label_start: u32,
    label_count: u32,
    // 1: null instruction, 2: null label list. Neither is an eager ABI error.
    state: u32,
}

#[repr(C)]
pub struct JumpQuery {
    label_start: u32,
    label_count: u32,
    operand_label: i32,
    codes_present: u32,
    to_present: u32,
}

#[repr(C)]
pub struct JumpResult {
    // 0: rejected, 1: matched, 2..8: deferred managed access failure.
    outcome: u32,
    ordinal: u32,
}

fn labels_at(labels: &[u32], start: u32, count: u32) -> Result<&[u32], Error> {
    let end = start.checked_add(count).ok_or(Error::Invalid)?;
    labels.get(start as usize..end as usize).ok_or(Error::Invalid)
}

fn jumps(
    query: &JumpQuery, nodes: &[JumpNode], labels: &[u32], from: &[JumpSource], to: &[i32],
) -> Result<JumpResult, Error> {
    let outcome = |outcome, ordinal| JumpResult { outcome, ordinal };
    if query.codes_present > 1 || query.to_present > 1 || query.operand_label < -1 {
        return Err(Error::Invalid);
    }
    let current_labels = labels_at(labels, query.label_start, query.label_count)?;
    if from.iter().any(|source| !matches!(source.state, 0 | 2 | 3 | 4) || source.operand_label < -1) {
        return Err(Error::Invalid);
    }
    for node in nodes {
        if node.state > 2 { return Err(Error::Invalid); }
        labels_at(labels, node.label_start, node.label_count)?;
    }

    if !from.is_empty() {
        let mut found = false;
        // Intersect builds the current-label set first, then reads jump sources only
        // through the first hit. A later invalid source must remain unobserved.
        for (ordinal, source) in from.iter().enumerate() {
            if source.state != 0 { return Ok(outcome(source.state, ordinal as u32)); }
            if source.operand_label >= 0 && current_labels.contains(&(source.operand_label as u32)) {
                found = true;
                break;
            }
        }
        if !found { return Ok(outcome(0, 0)); }
    }

    if query.to_present == 0 { return Ok(outcome(5, 0)); }
    if !to.is_empty() {
        if query.operand_label < 0 { return Ok(outcome(0, 0)); }
        if query.codes_present == 0 { return Ok(outcome(6, 0)); }
        // The original Intersect materializes ALL matching target indices before
        // reading jumpsTo, so faults after an early target are still observable.
        for (index, node) in nodes.iter().enumerate() {
            if node.state == 1 { return Ok(outcome(7, index as u32)); }
            if node.state == 2 { return Ok(outcome(8, index as u32)); }
        }
        // Label IDs have no callbacks. Once all target faults are ruled out,
        // inspect only requested indices instead of searching `to` for each node.
        // Out-of-range indices simply cannot intersect the instruction indices.
        for &index in to {
            if index < 0 { continue; }
            if let Some(node) = nodes.get(index as usize) {
                if labels_at(labels, node.label_start, node.label_count)?
                    .contains(&(query.operand_label as u32)) {
                    return Ok(outcome(1, 0));
                }
            }
        }
        return Ok(outcome(0, 0));
    }
    Ok(outcome(1, 0))
}

/// All buffers are caller owned and disjoint. Label IDs are scoped to this call;
/// no managed handles, callbacks, or retained state cross the native boundary.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_codematch_jumps_v1(
    query: *const JumpQuery, nodes: *const JumpNode, node_count: u32,
    labels: *const u32, label_count: u32, from: *const JumpSource, from_count: u32,
    to: *const i32, to_count: u32, result: *mut JumpResult,
) -> u32 {
    boundary(|| {
        disjoint(&[
            range(query, 1)?, range(nodes, node_count)?, range(labels, label_count)?,
            range(from, from_count)?, range(to, to_count)?, range(result, 1)?,
        ])?;
        if node_count > i32::MAX as u32 { return Err(Error::Invalid); }
        let answer = jumps(
            unsafe { &*query }, unsafe { input(nodes, node_count) },
            unsafe { input(labels, label_count) }, unsafe { input(from, from_count) },
            unsafe { input(to, to_count) },
        )?;
        unsafe { result.write(answer); }
        Ok(())
    })
}
