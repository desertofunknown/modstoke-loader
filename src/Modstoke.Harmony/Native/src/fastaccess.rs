// Copyright (c) 2026 Alex Cooper. MIT license.
use crate::{boundary, range, Error};
use std::ptr;

// Five recipes, each with a count and up to four instructions. An instruction's
// low word is a CLR opcode; the high word selects its managed member operand:
// none, constructor, method, or field. No member identity crosses the ABI.
fn recipes() -> [u32; 25] {
    let mut output = [0; 25];
    for (operation, recipe) in output.chunks_exact_mut(5).enumerate() {
        let (opcode, operand) = match operation {
            0 => (0x73, 1), // newobj
            1 | 3 => (0x28, 2), // call, deliberately not callvirt
            2 => (0x7b, 3), // ldfld, including the existing static-field limitation
            _ => (0x7d, 3), // stfld
        };
        let mut count = 0;
        if operation != 0 {
            count += 1;
            recipe[count] = 0x02; // ldarg.0, even for static accessors
        }
        if operation >= 3 {
            count += 1;
            recipe[count] = 0x03; // ldarg.1
        }
        count += 1;
        recipe[count] = opcode | (operand << 16);
        count += 1;
        recipe[count] = 0x2a; // ret
        recipe[0] = count as u32;
    }
    output
}

/// The caller supplies an accessible output buffer for the duration of the call.
/// Recipes are loaded once and replayed in managed IL generation; generated
/// delegates never call the native backend.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_fastaccess_recipes_v1(output: *mut u32, count: u32) -> u32 {
    boundary(|| {
        if count != 25 { return Err(Error::Capacity); }
        range(output, count)?;
        let values = recipes();
        unsafe { ptr::copy_nonoverlapping(values.as_ptr(), output, values.len()); }
        Ok(())
    })
}
