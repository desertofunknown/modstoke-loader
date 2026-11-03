// Copyright (c) 2026 Alex Cooper. MIT license.
// Delegate IL planning. CLR metadata and references stay managed.
use crate::{boundary, disjoint, range, Error};
use std::{mem::MaybeUninit, slice};

#[derive(Clone, Copy)]
#[repr(C)]
pub struct Instruction {
    opcode: u16,
    operand: u16,
    index: u32,
    target: u32,
}

const NONE: u16 = 0;
const INTEGER: u16 = 1;
const PARAMETER_TYPE: u16 = 2;
const METHOD: u16 = 3;
const CONSTRUCTOR: u16 = 4;
const UNLESS_DECLARING_VALUE: u16 = 5;
const UNLESS_ARGUMENT_EXISTS: u16 = 6;
const IF_ARGUMENT_BYREF: u16 = 7;
const UNLESS_PARAMETER_VALUE: u16 = 8;
const IF_ARGUMENT_VALUE: u16 = 9;
const JUMP: u16 = 10;

fn instruction(opcode: u16, operand: u16, index: u32) -> Instruction {
    Instruction { opcode, operand, index, target: 0 }
}

fn branch(operand: u16, index: u32, target: u32) -> Instruction {
    Instruction { opcode: 0, operand, index, target }
}

// The export validates the mode, count, and exact output length before calling this.
// Recipe generation is then infallible and needs no intermediate native allocation.
fn write_delegate_plan(mode: u32, parameter_count: u32, output: &mut [MaybeUninit<Instruction>]) {
    if mode == 1 {
        let plan = [
            instruction(0x02, NONE, 0),          // ldarg.0
            instruction(0xfe06, METHOD, 0),     // ldftn
            instruction(0x73, CONSTRUCTOR, 0),  // newobj
            instruction(0x2a, NONE, 0),         // ret
        ];
        for (slot, value) in output.iter_mut().zip(plan) {
            slot.write(value);
        }
        return;
    }
    // Branches are interpreted during emission, so CLR metadata queries remain lazy and
    // interleaved with ILGenerator calls. The generated delegate contains only ordinary IL.
    let receiver = [
        branch(UNLESS_DECLARING_VALUE, 0, 5),
        branch(UNLESS_ARGUMENT_EXISTS, 0, 5),
        branch(IF_ARGUMENT_BYREF, 0, 5),
        instruction(0x0f, INTEGER, 0), // ldarga.s, using the original int overload
        branch(JUMP, 0, 6),
        instruction(0x02, NONE, 0),   // ldarg.0
    ];
    let parameters = (1..parameter_count).flat_map(|index| {
        // Six receiver entries precede five entries per parameter. The checked
        // total of 5 * parameter_count + 3 also bounds every branch target.
        let next = index * 5 + 6;
        [
            instruction(0xfe09, INTEGER, index), // ldarg
            branch(UNLESS_PARAMETER_VALUE, index, next),
            branch(UNLESS_ARGUMENT_EXISTS, index, next),
            branch(IF_ARGUMENT_VALUE, index, next),
            instruction(0xa5, PARAMETER_TYPE, index), // unbox.any
        ]
    });
    let tail = [
        instruction(0x28, METHOD, 0), // call, deliberately nonvirtual
        instruction(0x2a, NONE, 0),
    ];
    let plan = receiver.into_iter().chain(parameters).chain(tail);
    for (slot, value) in output.iter_mut().zip(plan) {
        slot.write(value);
    }
}

/// Plans a complete dynamic delegate body. No metadata, handles, or byrefs cross this boundary.
/// Caller-owned output ranges must be accessible, disjoint, and exclusively writable during
/// the call. All validation completes before any output is written.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_accesstools_delegate_plan_v1(
    mode: u32, parameter_count: u32,
    output: *mut Instruction, capacity: u32, written: *mut u32,
) -> u32 {
    boundary(|| {
        disjoint(&[range(output, capacity)?, range(written, 1)?])?;
        let required = match (mode, parameter_count) {
            (1, 0) => 4,
            (0, count) if count != 0 => count.checked_mul(5).and_then(|n| n.checked_add(3)).ok_or(Error::Capacity)?,
            _ => return Err(Error::Invalid),
        };
        if capacity < required { return Err(Error::Capacity); }
        // MaybeUninit allows callers to supply storage that has not been initialized.
        // The validated recipe always writes exactly `required` entries.
        let destination = unsafe {
            slice::from_raw_parts_mut(output.cast::<MaybeUninit<Instruction>>(), required as usize)
        };
        write_delegate_plan(mode, parameter_count, destination);
        unsafe {
            written.write(required);
        }
        Ok(())
    })
}
