// Copyright (c) 2026 Alex Cooper. MIT license.
//! IL-error location matching and diagnostic offset ordering over CLR-owned data.
use crate::{boundary, disjoint, input, range, Error};

const DIGIT_MAP_LENGTH: usize = 0x10000 / 8;
const PREFIX: &[u8] = b"Invalid IL code in";
const OFFSET_PREFIX: &[u8] = b": IL_";

fn starts_with_ascii(text: &[u16], prefix: &[u8]) -> bool {
    text.len() >= prefix.len()
        && text.iter().zip(prefix).all(|(&actual, &expected)| actual == u16::from(expected))
}

fn offset_capture(text: &[u16], digits: &[u8]) -> Option<usize> {
    // TrimEnd runs in the CLR before this call. Since a trailing LF cannot remain,
    // the regex's final $ requires the match to end at the end of this last line.
    // Without Singleline, . excludes only LF (CR and Unicode separators do match).
    let line_start = text.iter().rposition(|&c| c == u16::from(b'\n')).map_or(0, |i| i + 1);
    let line = &text[line_start..];
    let prefix_start = line.windows(PREFIX.len()).position(|part| starts_with_ascii(part, PREFIX))?;

    // The optional "Reason: " changes only the unused whole-match start. The
    // earliest required prefix suffices: any later successful prefix also has a
    // suffix reachable from this one. The first .+ needs at least one code unit.
    let first_suffix = prefix_start + PREFIX.len() + 1;
    // ": IL_" + four digits + ": " + at least one error-text code unit.
    let last_suffix = line.len().checked_sub(12)?;
    if first_suffix > last_suffix { return None; }

    // Greedy .+ backtracks to the rightmost valid suffix, even if it appears
    // inside another error description. Every candidate takes constant work.
    for start in (first_suffix..=last_suffix).rev() {
        let suffix = &line[start..];
        if !starts_with_ascii(suffix, OFFSET_PREFIX)
            || suffix[9] != u16::from(b':') || suffix[10] != u16::from(b' ') {
            continue;
        }
        if suffix[5..9].iter().all(|&c| digits[usize::from(c) >> 3] & (1u8 << (c & 7)) != 0) {
            return Some(line_start + start + OFFSET_PREFIX.len());
        }
    }
    None
}

/// Matches the offset capture in an already CLR-TrimEnd'ed error message.
/// The caller supplies accessible, immutable UTF-16 and a CLR Nd bitmap, disjoint
/// from the writable result. No pointers survive the call. -1 means no match.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_exception_offset_v1(
    text: *const u16, text_count: u32, digits: *const u8, digit_count: u32, offset: *mut i32,
) -> u32 {
    boundary(|| {
        if text_count > i32::MAX as u32 || digit_count as usize != DIGIT_MAP_LENGTH {
            return Err(Error::Invalid);
        }
        disjoint(&[range(text, text_count)?, range(digits, digit_count)?, range(offset, 1)?])?;
        let text = unsafe { input(text, text_count) };
        let digits = unsafe { input(digits, digit_count) };
        let capture = offset_capture(text, digits).map_or(-1, |index| index as i32);
        unsafe { offset.write(capture); }
        Ok(())
    })
}

/// Returns original ordinals in ascending signed offset order. CLR instruction
/// objects remain with the caller; equal offsets retain their original order.
/// Buffers must be accessible and disjoint. Output is written only after sorting.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_exception_order_v1(
    offsets: *const i32, count: u32, output: *mut u32, capacity: u32,
) -> u32 {
    boundary(|| {
        if count > i32::MAX as u32 { return Err(Error::Invalid); }
        if capacity < count { return Err(Error::Capacity); }
        disjoint(&[range(offsets, count)?, range(output, capacity)?])?;
        let offsets = unsafe { input(offsets, count) };
        let mut order = Vec::new();
        order.try_reserve_exact(count as usize).map_err(|_| Error::Allocation)?;
        order.extend(0..count);
        order.sort_unstable_by_key(|&ordinal| (offsets[ordinal as usize], ordinal));
        if !order.is_empty() {
            unsafe { std::ptr::copy_nonoverlapping(order.as_ptr(), output, order.len()); }
        }
        Ok(())
    })
}
