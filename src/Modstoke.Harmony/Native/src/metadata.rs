// Copyright (c) 2026 Alex Cooper. MIT license.
//! Metadata precedence policy. CLR values and reflection remain with the caller.

use crate::{boundary, range, Error};

const MASTER_PRESENT: u32 = 1;
const DETAIL_PRESENT: u32 = 2;
const PRIORITY: u32 = 4;
const MASTER_UNSET: u32 = 8;
const DETAIL_UNSET: u32 = 16;
const DETAIL_HIGHER: u32 = 32;
const STATE_COUNT: u32 = 64;

fn select(mode: u32, facts: u32) -> u32 {
    let master = facts & MASTER_PRESENT != 0;
    let detail = facts & DETAIL_PRESENT != 0;
    let priority = facts & PRIORITY != 0;
    match mode {
        0 => if detail { 2 } else { 0 },
        // Only -1 is unset: other negative priorities are ordinary supplied values.
        1 => if detail && (!priority || facts & DETAIL_UNSET == 0) { 2 } else { 0 },
        _ if priority => {
            if facts & MASTER_UNSET != 0 { 2 }
            else if facts & DETAIL_UNSET != 0 { 1 }
            else if facts & DETAIL_HIGHER != 0 { 2 }
            else { 1 }
        },
        _ => if detail { 2 } else if master { 1 } else { 0 },
    }
}

/// Modes: 0 copies every supplied detail, 1 overlays an annotation, 2 merges a pair.
/// Selections: 0 leaves the destination unchanged, 1 selects master, 2 selects detail.
/// Each mode has 64 entries indexed by the six fact bits above. The caller derives
/// these facts only after the required CLR field reads and priority unboxing.
/// The caller supplies an accessible output buffer for the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_metadata_policy_v1(
    selections: *mut u32, selection_count: u32,
) -> u32 {
    boundary(|| {
        range(selections, selection_count)?;
        if selection_count != 3 * STATE_COUNT { return Err(Error::Capacity); }
        for mode in 0..3 {
            for facts in 0..STATE_COUNT {
                unsafe { selections.add((mode * STATE_COUNT + facts) as usize).write(select(mode, facts)); }
            }
        }
        Ok(())
    })
}
