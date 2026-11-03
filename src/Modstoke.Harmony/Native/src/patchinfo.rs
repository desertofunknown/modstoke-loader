// Copyright (c) 2026 Alex Cooper. MIT license.
//! Patch metadata plans over operation-scoped identities. No managed state is retained.

use crate::{boundary, disjoint, input, range, Error};

/// Reserve the possible addition indices before managed patch constructors execute.
/// The caller consumes one index per constructed patch, so null methods never leave gaps.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_patchinfo_append_indices_v1(
    current_count: u32,
    addition_count: u32,
    indices: *mut i32,
    capacity: u32,
) -> u32 {
    boundary(|| {
        range(indices, capacity)?;
        if capacity != addition_count {
            return Err(Error::Capacity);
        }
        if current_count > i32::MAX as u32 || addition_count > i32::MAX as u32 {
            return Err(Error::Invalid);
        }
        for ordinal in 0..addition_count {
            // Harmony's index arithmetic is unchecked Int32 arithmetic.
            let index = (current_count as i32).wrapping_add(ordinal as i32);
            unsafe { indices.add(ordinal as usize).write(index) };
        }
        Ok(())
    })
}

/// Stable selection shared by append, owner/method removal and debug-path collection.
/// Prefix entries are retained even when their identity equals the excluded identity;
/// this preserves null entries already present in a public PatchInfo array during append.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_patchinfo_filter_v1(
    identities: *const u32,
    count: u32,
    retained_prefix: u32,
    excluded_identity: u32,
    output: *mut u32,
    capacity: u32,
    written: *mut u32,
) -> u32 {
    boundary(|| {
        disjoint(&[
            range(identities, count)?,
            range(output, capacity)?,
            range(written, 1)?,
        ])?;
        if retained_prefix > count {
            return Err(Error::Invalid);
        }
        let identities = unsafe { input(identities, count) };
        let selected = |ordinal: usize, identity: u32| {
            ordinal < retained_prefix as usize || identity != excluded_identity
        };
        // A full-size output always fits. Only smaller caller buffers need a
        // counting pass to guarantee failure leaves every output untouched.
        if capacity < count {
            let selected_count = identities.iter().enumerate()
                .filter(|&(ordinal, &identity)| selected(ordinal, identity)).count();
            if selected_count > capacity as usize {
                return Err(Error::Capacity);
            }
        }
        let mut destination = 0usize;
        for (ordinal, &identity) in identities.iter().enumerate() {
            if selected(ordinal, identity) {
                unsafe { output.add(destination).write(ordinal as u32) };
                destination += 1;
            }
        }
        unsafe { written.write(destination as u32) };
        Ok(())
    })
}

/// Priority descending, then insertion index ascending. The immutable nine-entry
/// table avoids a native transition for each IComparable call during managed sorts.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_patchinfo_comparisons_v1(
    output: *mut i32,
    count: u32,
) -> u32 {
    boundary(|| {
        range(output, count)?;
        if count != 9 {
            return Err(Error::Capacity);
        }
        for priority_order in -1i32..=1 {
            for index_order in -1i32..=1 {
                let result = if priority_order == 0 { index_order } else { -priority_order };
                let slot = ((priority_order + 1) * 3 + index_order + 1) as usize;
                unsafe { output.add(slot).write(result) };
            }
        }
        Ok(())
    })
}
