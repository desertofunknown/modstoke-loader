// Copyright (c) 2026 Alex Cooper. MIT license.
use crate::{boundary, disjoint, input, range, Error};

fn join_length(lengths: &[u32], text_count: usize, delimiter_count: usize) -> Result<usize, Error> {
    let mut consumed = 0usize;
    let mut required = 0usize;
    for &length in lengths {
        consumed = consumed.checked_add(length as usize).ok_or(Error::Invalid)?;
        // Harmony inserts a separator only after the accumulated string becomes
        // nonempty, but then also inserts one before every null or empty value.
        if required != 0 {
            required = required.checked_add(delimiter_count).ok_or(Error::Capacity)?;
        }
        required = required.checked_add(length as usize).ok_or(Error::Capacity)?;
    }
    if consumed != text_count { return Err(Error::Invalid); }
    if required > i32::MAX as usize { return Err(Error::Capacity); }
    Ok(required)
}

fn write_join(lengths: &[u32], text: &[u16], delimiter: &[u16], output: &mut [u16]) {
    let mut source = 0;
    let mut destination = 0;
    for &length in lengths {
        if destination != 0 {
            output[destination..destination + delimiter.len()].copy_from_slice(delimiter);
            destination += delimiter.len();
        }
        let length = length as usize;
        output[destination..destination + length].copy_from_slice(&text[source..source + length]);
        source += length;
        destination += length;
    }
}

/// Lengths describe consecutive UTF-16 strings in text; zero includes null
/// converter results. Inputs stay readable and immutable during the call and
/// may alias each other. Writable output and length are disjoint from all
/// inputs and each other. Null output with zero capacity queries the size.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_string_join_v1(
    lengths: *const u32, count: u32, text: *const u16, text_count: u32,
    delimiter: *const u16, delimiter_count: u32,
    output: *mut u16, capacity: u32, length: *mut u32,
) -> u32 {
    boundary(|| {
        let destination = range(output, capacity)?;
        let length_range = range(length, 1)?;
        disjoint(&[range(lengths, count)?, destination, length_range])?;
        disjoint(&[range(text, text_count)?, destination, length_range])?;
        disjoint(&[range(delimiter, delimiter_count)?, destination, length_range])?;
        let lengths = unsafe { input(lengths, count) };
        let text = unsafe { input(text, text_count) };
        let delimiter = unsafe { input(delimiter, delimiter_count) };
        let required = join_length(lengths, text.len(), delimiter.len())?;
        if !output.is_null() {
            if required > capacity as usize { return Err(Error::Capacity); }
            if required != 0 {
                write_join(lengths, text, delimiter,
                    unsafe { std::slice::from_raw_parts_mut(output, required) });
            }
        }
        unsafe { length.write(required as u32); }
        Ok(())
    })
}

// Escape code units rather than Unicode scalar values: isolated surrogates and
// the two halves of a surrogate pair each retain their own four-digit escape.
fn escaped(unit: u16) -> ([u16; 6], usize) {
    let short = match unit {
        0x27 => b'\'', 0x22 => b'"', 0x5c => b'\\',
        0 => b'0', 7 => b'a', 8 => b'b', 12 => b'f',
        10 => b'n', 13 => b'r', 9 => b't', 11 => b'v',
        _ => 0,
    };
    if short != 0 { return ([b'\\' as u16, short as u16, 0, 0, 0, 0], 2); }
    if (0x20..=0x7e).contains(&unit) { return ([unit, 0, 0, 0, 0, 0], 1); }
    const HEX: &[u8; 16] = b"0123456789abcdef";
    ([b'\\' as u16, b'u' as u16,
        HEX[(unit >> 12) as usize] as u16,
        HEX[((unit >> 8) & 15) as usize] as u16,
        HEX[((unit >> 4) & 15) as usize] as u16,
        HEX[(unit & 15) as usize] as u16], 6)
}

fn literal_length(text: &[u16], quote: &[u16]) -> Result<usize, Error> {
    let mut length = quote.len().checked_mul(2).ok_or(Error::Capacity)?;
    for &unit in text {
        length = length.checked_add(escaped(unit).1).ok_or(Error::Capacity)?;
    }
    if length > i32::MAX as usize { return Err(Error::Capacity); }
    Ok(length)
}

fn write_literal(text: &[u16], quote: &[u16], output: &mut [u16]) {
    output[..quote.len()].copy_from_slice(quote);
    let mut position = quote.len();
    for &unit in text {
        let (characters, count) = escaped(unit);
        output[position..position + count].copy_from_slice(&characters[..count]);
        position += count;
    }
    output[position..position + quote.len()].copy_from_slice(quote);
}

/// Input UTF-16 ranges remain readable and immutable for the call and may alias
/// each other. Output and length must be writable and disjoint from all inputs
/// and each other. Null output with zero capacity queries the required size.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_string_literal_v1(
    text: *const u16, text_count: u32, quote: *const u16, quote_count: u32,
    output: *mut u16, capacity: u32, length: *mut u32,
) -> u32 {
    boundary(|| {
        let destination = range(output, capacity)?;
        let length_range = range(length, 1)?;
        disjoint(&[range(text, text_count)?, destination, length_range])?;
        disjoint(&[range(quote, quote_count)?, destination, length_range])?;
        let text = unsafe { input(text, text_count) };
        let quote = unsafe { input(quote, quote_count) };
        let required = literal_length(text, quote)?;
        if !output.is_null() {
            if required > capacity as usize { return Err(Error::Capacity); }
            if required != 0 {
                let destination = unsafe { std::slice::from_raw_parts_mut(output, required) };
                write_literal(text, quote, destination);
            }
        }
        unsafe { length.write(required as u32); }
        Ok(())
    })
}
