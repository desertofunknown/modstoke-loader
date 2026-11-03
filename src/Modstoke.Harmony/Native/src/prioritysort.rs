// Copyright (c) 2026 Alex Cooper. MIT license.
// Introsort adapted from the non-IComparable<T> ArraySortHelper<T> implementation in
// dotnet/runtime v11.0.0-rc.2.26504.105. Its swaps preserve List.Sort's unstable ties.
// https://github.com/dotnet/runtime/blob/v11.0.0-rc.2.26504.105/src/libraries/System.Private.CoreLib/src/System/Collections/Generic/ArraySortHelper.cs
//
// Copyright (c) .NET Foundation and Contributors
// All rights reserved.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

use std::cmp::Ordering;
use crate::{boundary, disjoint, input, range, Error};

#[repr(C)]
pub struct PriorityKey {
    index: i32,
    priority: i32,
}

fn compare(keys: &[PriorityKey], left: u32, right: u32) -> Ordering {
    let left = &keys[left as usize];
    let right = &keys[right as usize];
    right.priority.cmp(&left.priority).then_with(|| left.index.cmp(&right.index))
}

fn swap_if_greater(order: &mut [u32], keys: &[PriorityKey], left: usize, right: usize) {
    if compare(keys, order[left], order[right]).is_gt() { order.swap(left, right); }
}

fn insertion_sort(order: &mut [u32], keys: &[PriorityKey]) {
    for i in 1..order.len() {
        let value = order[i];
        let mut j = i;
        while j > 0 && compare(keys, value, order[j - 1]).is_lt() {
            order[j] = order[j - 1];
            j -= 1;
        }
        order[j] = value;
    }
}

fn down_heap(order: &mut [u32], keys: &[PriorityKey], mut i: usize, count: usize) {
    let value = order[i - 1];
    while i <= count / 2 {
        let mut child = i * 2;
        if child < count && compare(keys, order[child - 1], order[child]).is_lt() {
            child += 1;
        }
        if !compare(keys, value, order[child - 1]).is_lt() { break; }
        order[i - 1] = order[child - 1];
        i = child;
    }
    order[i - 1] = value;
}

fn heap_sort(order: &mut [u32], keys: &[PriorityKey]) {
    let count = order.len();
    for i in (1..=count / 2).rev() { down_heap(order, keys, i, count); }
    for i in (2..=count).rev() {
        order.swap(0, i - 1);
        down_heap(order, keys, 1, i - 1);
    }
}

fn partition(order: &mut [u32], keys: &[PriorityKey]) -> usize {
    let hi = order.len() - 1;
    let middle = hi / 2;
    swap_if_greater(order, keys, 0, middle);
    swap_if_greater(order, keys, 0, hi);
    swap_if_greater(order, keys, middle, hi);
    let pivot = order[middle];
    order.swap(middle, hi - 1);
    let mut left = 0;
    let mut right = hi - 1;
    // Median-of-three places sentinels at both ends; the integer comparer is total.
    while left < right {
        loop {
            left += 1;
            if !compare(keys, order[left], pivot).is_lt() { break; }
        }
        loop {
            right -= 1;
            if !compare(keys, pivot, order[right]).is_lt() { break; }
        }
        if left >= right { break; }
        order.swap(left, right);
    }
    if left != hi - 1 { order.swap(left, hi - 1); }
    left
}

fn intro_sort(mut order: &mut [u32], keys: &[PriorityKey], mut depth: u32) {
    while order.len() > 1 {
        // This threshold and the small-partition swap order match the pinned runtime.
        if order.len() <= 16 {
            match order.len() {
                2 => swap_if_greater(order, keys, 0, 1),
                3 => {
                    swap_if_greater(order, keys, 0, 1);
                    swap_if_greater(order, keys, 0, 2);
                    swap_if_greater(order, keys, 1, 2);
                }
                _ => insertion_sort(order, keys),
            }
            return;
        }
        if depth == 0 {
            heap_sort(order, keys);
            return;
        }
        depth -= 1;
        let pivot = partition(order, keys);
        let (left, pivot_and_right) = order.split_at_mut(pivot);
        intro_sort(&mut pivot_and_right[1..], keys, depth);
        order = left;
    }
}

/// Sorts immutable base-Patch keys by descending priority, then ascending index.
/// Equal keys retain the pinned CLR sort's swaps, not an ordinal tie-breaker.
/// Caller-owned buffers must be accessible and disjoint; inputs cannot change during the call.
/// Keys are borrowed; only ordinal workspace is allocated. Output follows successful sorting.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_prioritysort_v1(
    keys: *const PriorityKey, count: u32, output: *mut u32, output_capacity: u32,
) -> u32 {
    boundary(|| {
        disjoint(&[range(keys, count)?, range(output, output_capacity)?])?;
        if output_capacity < count { return Err(Error::Capacity); }
        let keys = unsafe { input(keys, count) };
        let mut order = Vec::new();
        order.try_reserve_exact(count as usize).map_err(|_| Error::Allocation)?;
        order.extend(0..count);
        if count > 1 {
            // A u32 count bounds recursive right partitions to at most 64 levels.
            intro_sort(&mut order, keys, 2 * (count.ilog2() + 1));
        }
        if !order.is_empty() {
            unsafe { std::ptr::copy_nonoverlapping(order.as_ptr(), output, order.len()); }
        }
        Ok(())
    })
}
