// Copyright (c) 2026 Alex Cooper. MIT license.
//! Ordered patch mutations and stable owner reduction. CLR metadata stays managed.

use crate::{boundary, disjoint, input, range, Error};
use std::{collections::HashMap, ptr};

#[repr(C)]
#[derive(Clone, Copy)]
pub struct Owner {
    start: u32,
    count: u32,
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct OwnerEntry {
    first: u32,
    last: u32,
}

/// Actions 0..4 append categories; 5..9 remove owners; 10 removes a method;
/// 11 rebuilds the wrapper and 12 records its replacement. The caller executes
/// them sequentially without snapshotting mutable patch metadata or rolling back.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_patchprocessor_plan_v1(
    operation: u32, patch_type: i32, output: *mut u32, capacity: u32, count: *mut u32,
) -> u32 {
    boundary(|| {
        disjoint(&[range(output, capacity)?, range(count, 1)?])?;
        let mut actions = [0u32; 7];
        let mut length = 0usize;
        match operation {
            0 => {
                // Even an absent patch performs its category's Add call.
                // Skipping it would change null-array failures and array identity.
                for action in 0..5 {
                    actions[length] = action;
                    length += 1;
                }
            }
            1 => {
                // HarmonyPatchType is not a flags enum. ReversePatch (5) and
                // unknown values still rebuild, but remove no ordinary patches.
                for (category, action) in [(1, 5), (2, 6), (3, 7), (4, 8), (6, 9)] {
                    if patch_type == 0 || patch_type == category {
                        actions[length] = action;
                        length += 1;
                    }
                }
            }
            2 => {
                actions[length] = 10;
                length += 1;
            }
            _ => return Err(Error::Invalid),
        }
        actions[length] = 11;
        actions[length + 1] = 12;
        length += 2;
        if length > capacity as usize { return Err(Error::Capacity); }
        unsafe {
            ptr::copy_nonoverlapping(actions.as_ptr(), output, length);
            count.write(length as u32);
        }
        Ok(())
    })
}

/// Reduce repeated Harmony owners in ordinal UTF-16 equality order. Each owner
/// keeps its first key object/insertion position and its last observed assembly.
/// Output indexes refer only to this call's managed observation list.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_patchprocessor_owners_v1(
    owners: *const Owner, owner_count: u32, text: *const u16, text_count: u32,
    output: *mut OwnerEntry, capacity: u32, count: *mut u32,
) -> u32 {
    boundary(|| {
        disjoint(&[
            range(owners, owner_count)?, range(text, text_count)?,
            range(output, capacity)?, range(count, 1)?,
        ])?;
        let owners = unsafe { input(owners, owner_count) };
        let text = unsafe { input(text, text_count) };
        let mut slots: HashMap<&[u16], usize> = HashMap::new();
        let mut entries: Vec<OwnerEntry> = Vec::new();
        for (index, owner) in owners.iter().enumerate() {
            let end = owner.start.checked_add(owner.count).ok_or(Error::Invalid)?;
            let key = text.get(owner.start as usize..end as usize).ok_or(Error::Invalid)?;
            if let Some(&slot) = slots.get(key) {
                entries[slot].last = index as u32;
            } else {
                // A single owner can appear on many methods. Grow scratch storage
                // only for distinct owners, rather than for every observation.
                slots.try_reserve(1).map_err(|_| Error::Allocation)?;
                entries.try_reserve(1).map_err(|_| Error::Allocation)?;
                slots.insert(key, entries.len());
                entries.push(OwnerEntry { first: index as u32, last: index as u32 });
            }
        }
        if entries.len() > capacity as usize { return Err(Error::Capacity); }
        unsafe {
            if !entries.is_empty() {
                ptr::copy_nonoverlapping(entries.as_ptr(), output, entries.len());
            }
            count.write(entries.len() as u32);
        }
        Ok(())
    })
}
