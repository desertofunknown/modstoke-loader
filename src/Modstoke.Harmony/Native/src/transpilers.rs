// Copyright (c) 2026 Alex Cooper. MIT license.
//! Instruction recipes for delegate wrappers and lazy transpiler prefixes.
//! Reflection, delegate roots, and stream callbacks remain with the CLR caller.

use crate::{boundary, disjoint, range, Error};
use std::ptr;

#[derive(Clone, Copy)]
#[repr(C)]
pub struct Instruction {
    opcode: u32,
    operand: u32,
    argument: u32,
}

impl Instruction {
    const fn new(opcode: u32, operand: u32) -> Self {
        Self { opcode, operand, argument: 0 }
    }
}

// Operand tags match RustKernel.TranspilerOperand. They describe CLR operations,
// never object pointers, handles, or identities retained by the native library.
const NONE: u32 = 0;
const CACHE_FIELD: u32 = 1;
const CACHE_INDEX: u32 = 2;
const CACHE_GETTER: u32 = 3;
const TARGET_CONSTRUCTOR: u32 = 4;
const DELEGATE_METHOD: u32 = 5;
const DELEGATE_CONSTRUCTOR: u32 = 6;
const ARGUMENT: u32 = 7;
const INVOKE: u32 = 8;
const DEBUG_CHANNEL: u32 = 9;
const TEXT: u32 = 10;
const LOG_TEXT: u32 = 11;

/// One batch per generated delegate wrapper. The three-instruction prefix is
/// followed by argument loads, Invoke, and Ret. Preserve the upstream ldarg.s
/// opcode even for large argument indices; the CLR emitter owns its conversion.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_transpilers_delegate_v1(
    preserve_context: u32, has_target: u32, argument_count: u32,
    output: *mut Instruction, capacity: u32,
) -> u32 {
    boundary(|| {
        range(output, capacity)?;
        if preserve_context > 1 || has_target > 1 || argument_count > i32::MAX as u32 {
            return Err(Error::Invalid);
        }
        if preserve_context != 0 && has_target == 0 { return Err(Error::Invalid); }
        let count = argument_count.checked_add(5).ok_or(Error::Invalid)?;
        if capacity != count { return Err(Error::Capacity); }
        let prefix = if preserve_context != 0 {
            [
                Instruction::new(0x7e, CACHE_FIELD), // ldsfld
                Instruction::new(0x20, CACHE_INDEX), // ldc.i4
                Instruction::new(0x6f, CACHE_GETTER), // callvirt
            ]
        } else {
            [
                if has_target != 0 {
                    Instruction::new(0x73, TARGET_CONSTRUCTOR) // newobj
                } else {
                    Instruction::new(0x14, NONE) // ldnull
                },
                Instruction::new(0xfe06, DELEGATE_METHOD), // ldftn
                Instruction::new(0x73, DELEGATE_CONSTRUCTOR), // newobj
            ]
        };
        // All validation precedes writes; no allocation or callback can interrupt
        // publication. The caller guarantees the output range is writable.
        unsafe {
            ptr::copy_nonoverlapping(prefix.as_ptr(), output, prefix.len());
            for argument in 0..argument_count {
                output.add(argument as usize + 3).write(Instruction {
                    opcode: 0x0e, operand: ARGUMENT, argument, // ldarg.s
                });
            }
            output.add(argument_count as usize + 3).write(Instruction::new(0x6f, INVOKE));
            output.add(argument_count as usize + 4).write(Instruction::new(0x2a, NONE));
        }
        Ok(())
    })
}

/// Cached once by the managed caller. Logging operands are resolved only as each
/// instruction is yielded. Replacement opcodes are indexed by IsConstructor,
/// which the caller reads only after the original MethodBase equality succeeds.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_transpilers_templates_v1(
    debug: *mut Instruction, debug_count: u32,
    replacement: *mut u32, replacement_count: u32,
) -> u32 {
    boundary(|| {
        disjoint(&[range(debug, debug_count)?, range(replacement, replacement_count)?])?;
        if debug_count != 4 || replacement_count != 2 { return Err(Error::Capacity); }
        let debug_recipe = [
            Instruction::new(0x20, DEBUG_CHANNEL), // ldc.i4
            Instruction::new(0x72, TEXT), // ldstr
            Instruction::new(0x17, NONE), // ldc.i4.1
            Instruction::new(0x28, LOG_TEXT), // call
        ];
        let replacement_recipe = [0x28u32, 0x73u32]; // call, newobj
        unsafe {
            ptr::copy_nonoverlapping(debug_recipe.as_ptr(), debug, debug_recipe.len());
            ptr::copy_nonoverlapping(replacement_recipe.as_ptr(), replacement, replacement_recipe.len());
        }
        Ok(())
    })
}
