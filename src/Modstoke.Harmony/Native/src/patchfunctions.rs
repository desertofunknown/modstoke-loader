// Copyright (c) 2026 Alex Cooper. MIT license.
//! Reverse-patch setup and conditional-unpatch traversal. CLR callbacks remain managed.

use crate::{boundary, disjoint, input, range, Error};

#[derive(Clone, Copy)]
#[repr(C)]
pub struct UnpatchRange {
    category: u32,
    count: u32,
}

/// Actions: fetch snapshot, append sorted snapshot transpilers, append sorted
/// snapshot manipulators, append post-transpiler, append post-manipulator.
/// Unknown reverse-patch enum values intentionally behave like Original.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_patchfunctions_reverse_plan_v1(
    reverse_type: i32,
    post_callbacks: u32,
    output: *mut u32,
    output_capacity: u32,
    written: *mut u32,
) -> u32 {
    boundary(|| {
        disjoint(&[range(output, output_capacity)?, range(written, 1)?])?;
        if post_callbacks & !3 != 0 {
            return Err(Error::Invalid);
        }

        let mut actions = [0u32; 5];
        let mut count = 0;
        if reverse_type == 1 {
            actions[..3].copy_from_slice(&[0, 1, 2]);
            count = 3;
        }
        if post_callbacks & 1 != 0 {
            actions[count] = 3;
            count += 1;
        }
        if post_callbacks & 2 != 0 {
            actions[count] = 4;
            count += 1;
        }
        if count > output_capacity as usize {
            return Err(Error::Capacity);
        }
        unsafe {
            if count != 0 {
                std::ptr::copy_nonoverlapping(actions.as_ptr(), output, count);
            }
            written.write(count as u32);
        }
        Ok(())
    })
}

/// Counts are prefix, postfix, transpiler, finalizer, IL manipulator. Each output
/// describes a contiguous category traversal, avoiding a plan allocation for
/// every occurrence. Consumers visit every index in each range: duplicates must
/// each run their predicate and, if matched, their own Unpatch call.
/// No predicates or patch metadata are evaluated here, so callbacks can mutate
/// later patches. The caller provides five accessible, disjoint input/output
/// records for the duration of the call. Errors leave the output untouched.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_patchfunctions_unpatch_ranges_v1(
    counts: *const u32,
    count_count: u32,
    output: *mut UnpatchRange,
    output_count: u32,
) -> u32 {
    boundary(|| {
        disjoint(&[range(counts, count_count)?, range(output, output_count)?])?;
        if count_count != 5 {
            return Err(Error::Invalid);
        }
        if output_count != 5 {
            return Err(Error::Capacity);
        }
        let counts = unsafe { input(counts, count_count) };

        for (cursor, category) in [1usize, 0, 4, 2, 3].into_iter().enumerate() {
            unsafe {
                output.add(cursor).write(UnpatchRange {
                    category: category as u32,
                    count: counts[category],
                });
            }
        }
        Ok(())
    })
}
