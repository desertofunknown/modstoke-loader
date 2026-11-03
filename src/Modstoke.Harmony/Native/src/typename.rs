// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See ../../Managed/License.txt.
// Formatting adapted from Ben.Demystifier TypeNameHelper.
use crate::{boundary, disjoint, input, range, Error};

#[repr(C)]
pub struct Part { kind: u32, start: u32, length: u32, value: u32 }

const ALIASES: [&[u8]; 19] = [
    b"void", b"bool", b"byte", b"char", b"decimal", b"double", b"float", b"int",
    b"long", b"object", b"sbyte", b"short", b"string", b"uint", b"ulong", b"ushort",
    b"void", b"Option", b"Async",
];

struct Sink { output: *mut u16, length: usize }
impl Sink {
    fn repeat(&mut self, value: u16, count: usize) -> Result<(), Error> {
        let end = self.length.checked_add(count).ok_or(Error::Capacity)?;
        if end > i32::MAX as usize { return Err(Error::Capacity); }
        if !self.output.is_null() {
            for i in self.length..end { unsafe { self.output.add(i).write(value); } }
        }
        self.length = end;
        Ok(())
    }
    fn text(&mut self, text: &[u16]) -> Result<(), Error> {
        let end = self.length.checked_add(text.len()).ok_or(Error::Capacity)?;
        if end > i32::MAX as usize { return Err(Error::Capacity); }
        if !self.output.is_null() && !text.is_empty() {
            unsafe { std::ptr::copy_nonoverlapping(text.as_ptr(), self.output.add(self.length), text.len()); }
        }
        self.length = end;
        Ok(())
    }
}

fn render(parts: &[Part], text: &[u16], sink: &mut Sink) -> Result<(), Error> {
    for part in parts {
        let start = part.start as usize;
        let end = start.checked_add(part.length as usize).ok_or(Error::Invalid)?;
        let name = text.get(start..end).ok_or(Error::Invalid)?;
        match part.kind {
            0 => sink.text(name)?,
            1 => {
                let alias = ALIASES.get(part.value as usize).ok_or(Error::Invalid)?;
                for &c in *alias { sink.repeat(c as u16, 1)?; }
            }
            2 => {
                sink.text(name.get(..part.value as usize).ok_or(Error::Invalid)?)?;
                sink.repeat(b'<' as u16, 1)?;
            }
            3 => { sink.text(name)?; sink.repeat(b'.' as u16, 1)?; }
            4 => sink.repeat(b'?' as u16, 1)?,
            5 => {
                if part.value == 0 { return Err(Error::Invalid); }
                sink.repeat(b'[' as u16, 1)?;
                sink.repeat(b',' as u16, (part.value - 1) as usize)?;
                sink.repeat(b']' as u16, 1)?;
            }
            6 => sink.repeat(b'+' as u16, 1)?,
            7 => { sink.repeat(b',' as u16, 1)?; if part.value == 0 { sink.repeat(b' ' as u16, 1)?; } }
            8 => sink.repeat(b'>' as u16, 1)?,
            9 => sink.repeat(b'<' as u16, 1)?,
            _ => return Err(Error::Invalid),
        }
    }
    Ok(())
}

/// Caller-owned UTF-16 buffers, including unpaired surrogate code units, remain unchanged.
/// Null output with zero capacity queries the size; validation completes before publishing output.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_typename_render_v1(
    parts: *const Part, count: u32, text: *const u16, text_count: u32,
    output: *mut u16, capacity: u32, length: *mut u32,
) -> u32 {
    boundary(|| {
        disjoint(&[range(parts, count)?, range(text, text_count)?, range(output, capacity)?, range(length, 1)?])?;
        let parts = unsafe { input(parts, count) };
        let text = unsafe { input(text, text_count) };
        let mut measure = Sink { output: std::ptr::null_mut(), length: 0 };
        render(parts, text, &mut measure)?;
        if !output.is_null() {
            if measure.length > capacity as usize { return Err(Error::Capacity); }
            render(parts, text, &mut Sink { output, length: 0 })?;
        }
        unsafe { length.write(measure.length as u32); }
        Ok(())
    })
}
