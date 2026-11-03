// Copyright (c) 2026 Alex Cooper. MIT license.
//! Fresh owner aggregation over ordinal UTF-16 values; no metadata is retained.

use std::{collections::HashSet, ptr};
use crate::{boundary, disjoint, input, range, Error};

#[repr(C)]
pub struct OwnerText {
    start: u32,
    length: u32,
    is_null: u32,
}

fn owner_ordinals(owners: &[OwnerText], characters: &[u16]) -> Result<Vec<u32>, Error> {
    // Validate the entire input before allocating or selecting any owners.
    for owner in owners {
        let end = owner.start.checked_add(owner.length).ok_or(Error::Invalid)?;
        if end as usize > characters.len() || owner.is_null > 1
            || (owner.is_null == 1 && owner.length != 0)
        {
            return Err(Error::Invalid);
        }
    }

    let mut seen: HashSet<Option<&[u16]>> = HashSet::new();
    let mut selected = Vec::new();
    for (ordinal, owner) in owners.iter().enumerate() {
        let value = if owner.is_null == 1 { None } else {
            let start = owner.start as usize;
            Some(&characters[start..start + owner.length as usize])
        };
        if seen.contains(&value) { continue; }

        // Many patches can share an owner. Grow only for distinct owners, with
        // fallible reservations before either collection can allocate implicitly.
        seen.try_reserve(1).map_err(|_| Error::Allocation)?;
        selected.try_reserve(1).map_err(|_| Error::Allocation)?;
        seen.insert(value);
        selected.push(ordinal as u32);
    }
    Ok(selected)
}

/// Return first-occurrence ordinals in group traversal order. UTF-16 equality
/// preserves CLR ordinal string equality, including unpaired surrogates.
///
/// # Safety
/// The caller supplies accessible, disjoint buffers for the duration of the call.
/// Input buffers must not be mutated during the call. Outputs are written only
/// after successful validation, selection and capacity checking.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_patches_owners_v1(
    owners: *const OwnerText, owner_count: u32,
    characters: *const u16, character_count: u32,
    output: *mut u32, capacity: u32, written: *mut u32,
) -> u32 {
    boundary(|| {
        disjoint(&[
            range(owners, owner_count)?, range(characters, character_count)?,
            range(output, capacity)?, range(written, 1)?,
        ])?;
        let selected = owner_ordinals(
            unsafe { input(owners, owner_count) },
            unsafe { input(characters, character_count) },
        )?;
        if selected.len() > capacity as usize { return Err(Error::Capacity); }
        unsafe {
            if !selected.is_empty() {
                ptr::copy_nonoverlapping(selected.as_ptr(), output, selected.len());
            }
            written.write(selected.len() as u32);
        }
        Ok(())
    })
}
