// Copyright (c) 2026 Alex Cooper. MIT license.
//! Attribute recognition and stable grouping over call-scoped patch descriptors.

use crate::{boundary, disjoint, input, range, Error};

#[repr(C)]
pub struct Text {
    start: u32,
    count: u32,
}

#[repr(C)]
pub struct Job {
    prefixes: i32,
    postfixes: i32,
    transpilers: i32,
    finalizers: i32,
    ilmanipulators: i32,
}

fn text_at<'a>(text: &Text, characters: &'a [u16]) -> Result<&'a [u16], Error> {
    let end = text.start.checked_add(text.count).ok_or(Error::Invalid)?;
    characters.get(text.start as usize..end as usize).ok_or(Error::Invalid)
}

fn matches_ascii(text: &[u16], value: &str) -> bool {
    text.len() == value.len() && text.iter().copied().eq(value.bytes().map(u16::from))
}

/// Text 0 is the method name; all remaining texts are attribute full names.
/// Recognition precedence intentionally follows patch kind, not attribute order.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_patchmodels_classify_v1(
    texts: *const Text, text_count: u32,
    characters: *const u16, character_count: u32,
    patch_type: *mut u32,
) -> u32 {
    boundary(|| {
        disjoint(&[
            range(texts, text_count)?, range(characters, character_count)?, range(patch_type, 1)?,
        ])?;
        let texts = unsafe { input(texts, text_count) };
        let characters = unsafe { input(characters, character_count) };
        if texts.is_empty() { return Err(Error::Invalid); }
        for text in texts { text_at(text, characters)?; }
        let method = text_at(&texts[0], characters)?;
        let names = [
            ("Prefix", "HarmonyLib.HarmonyPrefix"),
            ("Postfix", "HarmonyLib.HarmonyPostfix"),
            ("Transpiler", "HarmonyLib.HarmonyTranspiler"),
            ("Finalizer", "HarmonyLib.HarmonyFinalizer"),
            ("ReversePatch", "HarmonyLib.HarmonyReversePatch"),
            ("ILManipulator", "HarmonyLib.HarmonyILManipulator"),
        ];
        let mut selected = 0;
        for (index, (method_name, attribute_name)) in names.iter().enumerate() {
            if matches_ascii(method, method_name) || texts[1..].iter().any(|text| {
                // Every span was validated above, before publishing any result.
                matches_ascii(&characters[text.start as usize..(text.start + text.count) as usize], attribute_name)
            }) {
                selected = index as u32 + 1;
                break;
            }
        }
        unsafe { patch_type.write(selected); }
        Ok(())
    })
}

fn group_indices(completeness: &[u8], output: &mut [u32]) -> Result<usize, Error> {
    if output.len() < completeness.len() { return Err(Error::Capacity); }
    let mut incomplete_count = 0usize;
    for &complete in completeness {
        match complete {
            0 => incomplete_count += 1,
            1 => (),
            _ => return Err(Error::Invalid),
        }
    }
    // All validation precedes writes. Each input occupies one slot in the two
    // stable, disjoint partitions; duplicate occurrences remain distinct indices.
    let mut incomplete_cursor = 0usize;
    let mut complete_cursor = incomplete_count;
    for (index, &complete) in completeness.iter().enumerate() {
        let cursor = if complete == 0 { &mut incomplete_cursor } else { &mut complete_cursor };
        output[*cursor] = index as u32;
        *cursor += 1;
    }
    Ok(incomplete_count)
}

/// Returns incomplete indices followed by complete indices in source order.
/// Completeness flags are 0 or 1; counts are [incomplete, complete]. The caller
/// merges incomplete metadata before evaluating live CLR target equality.
///
/// # Safety
/// Buffers must be accessible for their declared lengths and exclusively owned
/// for the call. All nonempty buffers must be disjoint.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_patchmodels_group_v2(
    completeness: *const u8, target_count: u32,
    output: *mut u32, output_capacity: u32,
    counts: *mut u32,
) -> u32 {
    boundary(|| {
        disjoint(&[
            range(completeness, target_count)?, range(output, output_capacity)?, range(counts, 2)?,
        ])?;
        let completeness = unsafe { input(completeness, target_count) };
        let output = if output_capacity == 0 { &mut [] } else {
            unsafe { std::slice::from_raw_parts_mut(output, output_capacity as usize) }
        };
        let incomplete_count = group_indices(completeness, output)? as u32;
        unsafe {
            counts.write(incomplete_count);
            counts.add(1).write(target_count - incomplete_count);
        }
        Ok(())
    })
}

/// Selects jobs with a positive total patch count in their existing enumeration order.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_patchmodels_jobs_v1(
    jobs: *const Job, job_count: u32,
    output: *mut u32, output_capacity: u32,
    selected_count: *mut u32,
) -> u32 {
    boundary(|| {
        disjoint(&[range(jobs, job_count)?, range(output, output_capacity)?, range(selected_count, 1)?])?;
        if output_capacity < job_count { return Err(Error::Capacity); }
        let jobs = unsafe { input(jobs, job_count) };
        let mut selected = 0usize;
        for (index, job) in jobs.iter().enumerate() {
            // Preserve the managed unchecked signed sum, including overflow behavior.
            let count = job.prefixes.wrapping_add(job.postfixes).wrapping_add(job.transpilers)
                .wrapping_add(job.finalizers).wrapping_add(job.ilmanipulators);
            if count > 0 {
                unsafe { output.add(selected).write(index as u32); }
                selected += 1;
            }
        }
        unsafe { selected_count.write(selected as u32); }
        Ok(())
    })
}
