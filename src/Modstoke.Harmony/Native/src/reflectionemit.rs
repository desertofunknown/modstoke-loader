// Copyright (c) 2026 Alex Cooper. MIT license.
// Reflection text and IL policy. CLR identities, metadata, locals and delegates stay managed.
use crate::{boundary, disjoint, input, range, Error};
use std::ptr;

#[derive(Clone, Copy)]
#[repr(C)]
pub struct EmitterStep {
    code: u32,
    operand: u32,
}

impl EmitterStep {
    const fn new(code: u32, operand: u32) -> Self { Self { code, operand } }
}

fn member_separator(text: &[u16]) -> u32 {
    let mut separator = u32::MAX;
    for (index, &character) in text.iter().enumerate() {
        if character != b':' as u16 { continue; }
        if separator != u32::MAX { return u32::MAX; }
        // The exported input count is u32, so every valid index fits below the sentinel.
        separator = index as u32;
    }
    separator
}

/// Find the single ':' without copying UTF-16 or allocating split segments. Empty
/// halves are valid; absent/repeated separators return u32::MAX. The caller owns the
/// accessible, disjoint input/output ranges and keeps the text immutable for this call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_reflectionemit_member_separator_v1(
    text: *const u16, count: u32, separator: *mut u32,
) -> u32 {
    boundary(|| {
        disjoint(&[range(text, count)?, range(separator, 1)?])?;
        let position = member_separator(unsafe { input(text, count) });
        unsafe { separator.write(position); }
        Ok(())
    })
}

/// Immutable recipes copied once at managed initialization; no transition per emitted opcode.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_reflectionemit_tables_v1(
    indirect: *mut u16, indirect_count: u32, fields: *mut u16, field_count: u32,
) -> u32 {
    boundary(|| {
        if indirect_count != 30 || field_count != 16 { return Err(Error::Capacity); }
        disjoint(&[range(indirect, indirect_count)?, range(fields, field_count)?])?;
        // Type identity order: fallback, bool, sbyte, byte, char, short, ushort,
        // int, uint, long, ulong, float, double, IntPtr, UIntPtr. Preserve signed
        // loads for byte/ushort/uint, as required by the original emitter.
        let mut codes = [0; 30];
        for i in 0..15 {
            let (load, store) = match i {
                1..=3 => (0x46, 0x52),
                4..=6 => (0x48, 0x53),
                7..=8 => (0x4a, 0x54),
                9..=10 => (0x4c, 0x55),
                11 => (0x4e, 0x56),
                12 => (0x4f, 0x57),
                13..=14 => (0x4d, 0xdf),
                _ => (0x50, 0x51),
            };
            codes[i * 2] = load;
            codes[i * 2 + 1] = store;
        }
        let mut recipes = [0xffff; 16];
        // Index bits: static, cast. A static field must never cast the instance.
        for flags in 0..4 {
            let recipe: &[u16] = if flags & 1 != 0 {
                &[0x7f, 0x2a] // ldsflda, ret
            } else if flags & 2 != 0 {
                &[0x02, 0x74, 0x7c, 0x2a] // ldarg.0, castclass, ldflda, ret
            } else {
                &[0x02, 0x7c, 0x2a]
            };
            recipes[flags * 4..flags * 4 + recipe.len()].copy_from_slice(recipe);
        }
        unsafe {
            ptr::copy_nonoverlapping(codes.as_ptr(), indirect, codes.len());
            ptr::copy_nonoverlapping(recipes.as_ptr(), fields, recipes.len());
        }
        Ok(())
    })
}

/// Immutable dispatch recipes: prologue (5), each eligible overload (13), epilogue (5).
/// The caller keeps reflection enumeration and metadata reads interleaved with emission.
/// In particular action 8 reads IsValueType and conditionally emits unbox.any only at
/// that step; it must not be evaluated while collecting the overloads.
/// Actions: none, type, next label, declare/store local, load local, method,
/// mark label, define label, unbox value type, null message, unsupported message,
/// exception constructor. The caller owns the writable output for this call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_reflectionemit_dispatch_v2(
    output: *mut EmitterStep, count: u32,
) -> u32 {
    boundary(|| {
        let steps = [
            EmitterStep::new(0x04, 0), // ldarg.2
            EmitterStep::new(0x3a, 2), // brtrue first overload
            EmitterStep::new(0x72, 9), // ldstr null message
            EmitterStep::new(0x73, 11), // newobj Exception(string)
            EmitterStep::new(0x7a, 0), // throw
            EmitterStep::new(0, 6), // mark previous failure target
            EmitterStep::new(0, 7), // define next failure target
            EmitterStep::new(0x04, 0), // ldarg.2
            EmitterStep::new(0x75, 1), // isinst operand type
            EmitterStep::new(0x39, 2), // brfalse next
            EmitterStep::new(0x04, 0), // ldarg.2
            EmitterStep::new(0xa5, 8), // unbox.any when the live type is a value type
            EmitterStep::new(0xfe0e, 3), // stloc newly declared local
            EmitterStep::new(0x02, 0), // ldarg.0
            EmitterStep::new(0x03, 0), // ldarg.1
            EmitterStep::new(0xfe0c, 4), // ldloc
            EmitterStep::new(0x6f, 5), // callvirt selected Emit
            EmitterStep::new(0x2a, 0), // ret
            EmitterStep::new(0, 6), // mark last failure target
            EmitterStep::new(0x72, 10), // ldstr unsupported message
            EmitterStep::new(0x73, 11), // newobj Exception(string)
            EmitterStep::new(0x7a, 0), // throw
            EmitterStep::new(0x2a, 0), // ret
        ];
        if count as usize != steps.len() { return Err(Error::Capacity); }
        range(output, count)?;
        unsafe { ptr::copy_nonoverlapping(steps.as_ptr(), output, steps.len()); }
        Ok(())
    })
}
