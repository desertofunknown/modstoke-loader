// Copyright (c) 2026 Alex Cooper. MIT license.
// Instruction-family membership and implicit operands for Harmony extension methods.
use crate::{boundary, range, Error};

const CALLING: u32 = 1;
const LOCAL_ADDRESS: u32 = 2;
const LOCAL_LOAD: u32 = 4;
const LOCAL_STORE: u32 = 8;
const ARGUMENT_ADDRESS: u32 = 16;
const ARGUMENT_LOAD: u32 = 32;
const ARGUMENT_STORE: u32 = 64;
const BRANCH: u32 = 128;
const CONSTANT: u32 = 256;
const INTEGER: u32 = 512;
const FLOATING_POINT: u32 = 1024;
const STRING: u32 = 2048;
const IMPLICIT: u32 = 4096;
const FIELD_LOAD: u32 = 8192;
const FIELD_ADDRESS: u32 = 16384;
const FIELD_STORE: u32 = 32768;
const STATIC_FIELD_LOAD: u32 = 65536;
const STATIC_FIELD_ADDRESS: u32 = 131072;
const STATIC_FIELD_STORE: u32 = 262144;

fn descriptor(opcode: usize) -> u32 {
    // The high byte stores implicit value + 1 (including ldc.i4.m1).
    match opcode {
        0x02..=0x05 => ARGUMENT_LOAD | IMPLICIT | (((opcode - 0x02 + 1) as u32) << 24),
        0x06..=0x09 => LOCAL_LOAD | IMPLICIT | (((opcode - 0x06 + 1) as u32) << 24),
        0x0a..=0x0d => LOCAL_STORE | IMPLICIT | (((opcode - 0x0a + 1) as u32) << 24),
        0x0e | 0xfe09 => ARGUMENT_LOAD,
        0x0f | 0xfe0a => ARGUMENT_ADDRESS,
        0x10 | 0xfe0b => ARGUMENT_STORE,
        0x11 | 0xfe0c => LOCAL_LOAD,
        0x12 | 0xfe0d => LOCAL_ADDRESS,
        0x13 | 0xfe0e => LOCAL_STORE,
        0x15..=0x1e => CONSTANT | INTEGER | IMPLICIT | (((opcode - 0x15) as u32) << 24),
        0x1f..=0x21 => CONSTANT | INTEGER,
        0x22 | 0x23 => CONSTANT | FLOATING_POINT,
        0x28 | 0x6f => CALLING,
        // Harmony Branches excludes switch, leave and leave.s.
        0x2b..=0x44 => BRANCH,
        0x72 => CONSTANT | STRING,
        0x7b => FIELD_LOAD,
        0x7c => FIELD_ADDRESS,
        0x7d => FIELD_STORE,
        0x7e => STATIC_FIELD_LOAD,
        0x7f => STATIC_FIELD_ADDRESS,
        0x80 => STATIC_FIELD_STORE,
        _ => 0,
    }
}

/// Caller owns an accessible output buffer for 512 u32 entries. No pointers are retained.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_extensions_tables_v1(output: *mut u32, count: u32) -> u32 {
    boundary(|| {
        range(output, count)?;
        if count != 512 { return Err(Error::Capacity); }
        let mut table = [0; 512];
        for (index, entry) in table.iter_mut().enumerate() {
            *entry = descriptor(if index < 256 { index } else { 0xfe00 + index - 256 });
        }
        unsafe { std::ptr::copy_nonoverlapping(table.as_ptr(), output, table.len()); }
        Ok(())
    })
}
