// Copyright (c) 2026 Alex Cooper. MIT license.
//! Stateless patch planning. No CLR pointers, managed callbacks, or retained input buffers.

mod order;
mod prioritysort;
mod recipes;
mod accesstools;
mod argumentmap;
mod codematch;
mod extensions;
mod fastaccess;
mod ilmanipulator;
mod invoker;
mod manipulator;
mod matcher;
mod metadata;
mod patchclass;
mod patches;
mod patchfunctions;
mod patchinfo;
mod patchmodels;
mod patchprocessor;
mod reflectionemit;
mod transpilers;
mod typename;
mod strings;
mod exception;
mod diagnostics;
mod filelog;

use std::{mem::{align_of, size_of}, panic::{catch_unwind, AssertUnwindSafe}, ptr, slice};

#[derive(Clone, Copy)]
#[repr(C)]
pub struct Node {
    method: u32,
    before_start: u32,
    before_count: u32,
    after_start: u32,
    after_count: u32,
}

#[derive(Clone, Copy)]
#[repr(u32)]
enum Error { Invalid = 1, Allocation = 2, Panic = 3, Capacity = 4 }

fn boundary(f: impl FnOnce() -> Result<(), Error>) -> u32 {
    match catch_unwind(AssertUnwindSafe(f)) {
        Ok(Ok(())) => 0, Ok(Err(error)) => error as u32, Err(_) => Error::Panic as u32,
    }
}

fn range<T>(pointer: *const T, count: u32) -> Result<(usize, usize), Error> {
    let start = pointer as usize;
    let bytes = (count as usize).checked_mul(size_of::<T>()).ok_or(Error::Invalid)?;
    if bytes > isize::MAX as usize || (count != 0 && (start == 0 || start % align_of::<T>() != 0)) {
        return Err(Error::Invalid);
    }
    Ok((start, start.checked_add(bytes).ok_or(Error::Invalid)?))
}

fn disjoint(ranges: &[(usize, usize)]) -> Result<(), Error> {
    for (i, &(start, end)) in ranges.iter().enumerate() {
        if start == end { continue; }
        for &(other, limit) in &ranges[..i] {
            if other != limit && start < limit && other < end { return Err(Error::Invalid); }
        }
    }
    Ok(())
}

// The caller guarantees that validated pointer ranges are accessible for the duration of the call.
unsafe fn input<'a, T>(pointer: *const T, count: u32) -> &'a [T] {
    if count == 0 { &[] } else { unsafe { slice::from_raw_parts(pointer, count as usize) } }
}

#[unsafe(no_mangle)]
pub extern "C" fn mh_abi_v1() -> u32 { 4 }

#[unsafe(no_mangle)]
pub extern "C" fn mh_node_size_v1() -> u32 { size_of::<Node>() as u32 }

/// All arrays are caller owned, disjoint, and may not be mutated by another thread during the call.
/// Outputs are published only after successful planning and capacity validation.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_order_v1(
    nodes: *const Node, node_count: u32, edges: *const u32, edge_count: u32,
    output: *mut u32, output_count: u32, cycles: *mut u32, cycle_capacity: u32,
    cycle_count: *mut u32,
) -> u32 {
    boundary(|| {
        disjoint(&[
            range(nodes, node_count)?, range(edges, edge_count)?, range(output, output_count)?,
            range(cycles, cycle_capacity)?, range(cycle_count, 1)?,
        ])?;
        if output_count != node_count { return Err(Error::Capacity); }
        let (order, removed) = order::order(
            unsafe { input(nodes, node_count) }, unsafe { input(edges, edge_count) }
        )?;
        if removed.len() > cycle_capacity as usize { return Err(Error::Capacity); }
        unsafe {
            if !order.is_empty() { ptr::copy_nonoverlapping(order.as_ptr(), output, order.len()); }
            if !removed.is_empty() { ptr::copy_nonoverlapping(removed.as_ptr(), cycles, removed.len()); }
            cycle_count.write(removed.len() as u32);
        }
        Ok(())
    })
}

/// Small immutable tables eliminate native transitions during IL emission and patched gameplay.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_tables_v1(
    branches: *mut u16, branch_count: u32, variables: *mut u32, variable_count: u32,
    arguments: *mut u32, argument_count: u32,
) -> u32 {
    boundary(|| {
        if branch_count != 256 || variable_count != 36 || argument_count != 16 { return Err(Error::Capacity); }
        disjoint(&[range(branches, branch_count)?, range(variables, variable_count)?, range(arguments, argument_count)?])?;
        let (branch_values, variable_values, argument_values) = recipes::tables();
        unsafe {
            ptr::copy_nonoverlapping(branch_values.as_ptr(), branches, 256);
            ptr::copy_nonoverlapping(variable_values.as_ptr(), variables, 36);
            ptr::copy_nonoverlapping(argument_values.as_ptr(), arguments, 16);
        }
        Ok(())
    })
}
