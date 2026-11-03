// Copyright (c) 2026 Alex Cooper. MIT license.
use crate::{boundary, disjoint, input, range, Error};

fn format_row(bytes: &[u8]) -> ([u16; 28], usize) {
    const HEX: &[u8; 16] = b"0123456789ABCDEF";
    let mut output = [0; 28];
    output[..3].copy_from_slice(&[b'#' as u16, b' ' as u16, b' ' as u16]);
    let mut position = 3;
    for (index, &byte) in bytes.iter().enumerate() {
        // The second group has an extra separator, but a final four-byte row does not.
        if index == 4 {
            output[position] = b' ' as u16;
            position += 1;
        }
        output[position] = HEX[(byte >> 4) as usize] as u16;
        output[position + 1] = HEX[(byte & 15) as usize] as u16;
        output[position + 2] = b' ' as u16;
        position += 3;
    }
    (output, position)
}

/// Caller owns accessible, disjoint buffers. Input is one already-read row of 1..=8 bytes.
/// UTF-16 output preserves the uppercase, culture-independent X2 byte format and trailing space.
/// No pointers are retained; output is published only after all validation and formatting.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_filelog_row_v1(
    bytes: *const u8, count: u32, output: *mut u16, capacity: u32, written: *mut u32,
) -> u32 {
    boundary(|| {
        if !(1..=8).contains(&count) { return Err(Error::Invalid); }
        disjoint(&[range(bytes, count)?, range(output, capacity)?, range(written, 1)?])?;
        let (formatted, length) = format_row(unsafe { input(bytes, count) });
        if length > capacity as usize { return Err(Error::Capacity); }
        unsafe {
            std::ptr::copy_nonoverlapping(formatted.as_ptr(), output, length);
            written.write(length as u32);
        }
        Ok(())
    })
}
