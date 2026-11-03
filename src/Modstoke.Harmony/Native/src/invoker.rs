// Copyright (c) 2026 Alex Cooper. MIT license.
//! Cached invocation IL recipes. CLR metadata and emission remain managed.

use crate::{boundary, disjoint, input, range, Error};

#[repr(C)]
#[derive(Clone, Copy)]
pub struct Descriptor {
    kind: u32,
    value: i32,
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct Instruction {
    opcode: u32,
    operand: u32,
}

const NONE: u32 = 0;
const TYPE: u32 = 1;
const OBJECT_TYPE: u32 = 2;
const METHOD: u32 = 3;
const INDEX: u32 = 4;
const SIGNED_BYTE: u32 = 5;
const INTEGER: u32 = 6;
const BOX_LOCAL: u32 = 7;

fn emit(plan: &mut impl FnMut(Instruction), opcode: u32, operand: u32) {
    plan(Instruction { opcode, operand });
}

fn argument_slot(plan: &mut impl FnMut(Instruction)) {
    emit(plan, 0x03, NONE); // ldarg.1
    emit(plan, 0, INDEX); // Current parameter index, using the cached integer recipe.
}

fn parameter(plan: &mut impl FnMut(Instruction), flags: i32) {
    let by_ref = flags & 1 != 0;
    let value_type = flags & 2 != 0;
    let direct = flags & 4 != 0;
    if by_ref && value_type && !direct {
        argument_slot(plan); // Keep array and index below the value for stelem.ref.
    }
    argument_slot(plan);
    if by_ref && !value_type {
        // Reference byrefs point at the array slot itself; never defer writeback.
        emit(plan, 0x8f, OBJECT_TYPE); // ldelema object
    } else {
        emit(plan, 0x9a, NONE); // ldelem.ref
        if value_type {
            if !by_ref || !direct {
                emit(plan, 0xa5, TYPE); // unbox.any
                if by_ref {
                    // A fresh box replaces the slot before later arguments or the call.
                    // Aliases outside the argument array still refer to the original box.
                    emit(plan, 0x8c, TYPE); // box
                    emit(plan, 0x25, NONE); // dup
                    emit(plan, 0, BOX_LOCAL); // Declare the shared object local once.
                    emit(plan, 0x0a, NONE); // stloc.0
                    emit(plan, 0xa2, NONE); // stelem.ref
                    emit(plan, 0x06, NONE); // ldloc.0
                    emit(plan, 0x79, TYPE); // unbox
                }
            } else {
                emit(plan, 0x79, TYPE); // unbox: preserve direct box aliasing.
            }
        }
    }
}

fn recipe(plan: &mut impl FnMut(Instruction), descriptor: Descriptor) -> Result<(), Error> {
    match (descriptor.kind, descriptor.value) {
        (0, 0) => emit(plan, 0x02, NONE), // ldarg.0
        (1, 0) => emit(plan, 0xa5, TYPE), // unbox.any, including a value-type target
        (2, flags @ 0..=7) => parameter(plan, flags),
        (3, 0) => emit(plan, 0x6f, METHOD), // callvirt for every instance method
        (3, 1) => emit(plan, 0x28, METHOD), // call for static methods
        (4, 0) => emit(plan, 0x14, NONE), // ldnull for void
        (4, 1) => emit(plan, 0x8c, TYPE), // box result
        (4, 2) => emit(plan, 0x2a, NONE), // ret
        (5, value @ -1..=8) => emit(plan, (0x16 + value) as u32, NONE),
        (5, -128..=127) => emit(plan, 0x1f, SIGNED_BYTE), // ldc.i4.s
        (5, _) => emit(plan, 0x20, INTEGER), // ldc.i4
        _ => return Err(Error::Invalid),
    }
    Ok(())
}

/// Batch descriptor kinds: target load, target unbox, parameter, call, return, integer.
/// Parameter flags: byref (1), value type (2), direct box access (4).
/// Offsets delimit each recipe; all buffers are caller-owned and disjoint.
/// Input descriptors must remain unchanged for the duration of the call.
/// Nothing is published until every descriptor and the output capacity are valid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_invoker_plan_v1(
    descriptors: *const Descriptor, count: u32,
    output: *mut Instruction, capacity: u32,
    offsets: *mut u32, offset_count: u32, written: *mut u32,
) -> u32 {
    boundary(|| {
        disjoint(&[
            range(descriptors, count)?, range(output, capacity)?,
            range(offsets, offset_count)?, range(written, 1)?,
        ])?;
        if count.checked_add(1) != Some(offset_count) { return Err(Error::Capacity); }
        let descriptors = unsafe { input(descriptors, count) };
        // Count the actual recipes without allocating worst-case scratch buffers.
        // At most thirteen instructions per u32 descriptor count fit in u64.
        let mut length = 0u64;
        for &descriptor in descriptors {
            recipe(&mut |_| length += 1, descriptor)?;
        }
        if length > u32::MAX as u64 { return Err(Error::Allocation); }
        if length > capacity as u64 { return Err(Error::Capacity); }

        // Descriptors and capacity are validated before publishing anything.
        // Replaying the same immutable recipes cannot fail or allocate.
        let mut position = 0usize;
        for (index, &descriptor) in descriptors.iter().enumerate() {
            unsafe { offsets.add(index).write(position as u32); }
            recipe(&mut |instruction| {
                unsafe { output.add(position).write(instruction); }
                position += 1;
            }, descriptor)?;
        }
        unsafe {
            offsets.add(descriptors.len()).write(position as u32);
            written.write(position as u32);
        }
        Ok(())
    })
}
