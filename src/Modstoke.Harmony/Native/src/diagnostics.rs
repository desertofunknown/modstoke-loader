// Copyright (c) 2026 Alex Cooper. MIT license.
use crate::{boundary, disjoint, input, range, Error};

#[derive(Clone, Copy)]
#[repr(C)]
pub struct DiagnosticFormat {
    argument: u32,
    labels: u32,
    blocks: u32,
    arguments: u32,
    instruction: u32,
}

#[repr(C)]
pub struct DiagnosticFragment {
    start: u32,
    length: u32,
}

struct Composition<'a> {
    output: Option<&'a mut [u16]>,
    position: usize,
}

impl Composition<'_> {
    fn end(&self, length: usize) -> Result<usize, Error> {
        self.position.checked_add(length).filter(|&end| end <= i32::MAX as usize).ok_or(Error::Capacity)
    }

    fn text(&mut self, text: &[u16]) -> Result<(), Error> {
        let end = self.end(text.len())?;
        if let Some(output) = &mut self.output { output[self.position..end].copy_from_slice(text); }
        self.position = end;
        Ok(())
    }

    fn ascii(&mut self, text: &str) -> Result<(), Error> {
        let end = self.end(text.len())?;
        if let Some(output) = &mut self.output {
            for (unit, byte) in output[self.position..end].iter_mut().zip(text.bytes()) { *unit = u16::from(byte); }
        }
        self.position = end;
        Ok(())
    }

    fn block_name(&mut self, text: &[u16]) -> Result<(), Error> {
        const BLOCK: &[u16] = &[b'B' as u16, b'l' as u16, b'o' as u16, b'c' as u16, b'k' as u16];
        let mut start = 0;
        let mut position = 0;
        // Match String.Replace's ordinal, non-overlapping removal of "Block".
        while position < text.len() {
            if text[position..].starts_with(BLOCK) {
                self.text(&text[start..position])?;
                position += BLOCK.len();
                start = position;
            } else {
                position += 1;
            }
        }
        self.text(&text[start..])
    }
}

struct InstructionFormat<'a> {
    argument: u32,
    labels: &'a [DiagnosticFragment],
    blocks: &'a [DiagnosticFragment],
    arguments: &'a [DiagnosticFragment],
    opcode: Option<&'a DiagnosticFragment>,
    characters: &'a [u16],
}

impl<'a> InstructionFormat<'a> {
    fn new(format: DiagnosticFormat, fragments: &'a [DiagnosticFragment], characters: &'a [u16]) -> Result<Self, Error> {
        let count = u64::from(format.labels) + u64::from(format.blocks)
            + u64::from(format.arguments) + u64::from(format.instruction);
        if format.instruction > 1 || count != fragments.len() as u64
            || (format.instruction == 0 && (format.labels != 0 || format.blocks != 0))
        {
            return Err(Error::Invalid);
        }
        let valid_arguments = match format.argument {
            0 => format.arguments == 0,
            1 => (1..=2).contains(&format.arguments),
            2 => format.arguments == 3,
            3 | 6 => format.arguments == 1,
            4 => true,
            5 => format.arguments == 2,
            _ => false,
        };
        if !valid_arguments { return Err(Error::Invalid); }
        for fragment in fragments {
            let end = (fragment.start as usize).checked_add(fragment.length as usize).ok_or(Error::Invalid)?;
            if end > characters.len() { return Err(Error::Invalid); }
        }
        let (labels, rest) = fragments.split_at(format.labels as usize);
        let (blocks, rest) = rest.split_at(format.blocks as usize);
        let (arguments, opcode) = rest.split_at(format.arguments as usize);
        Ok(Self { argument: format.argument, labels, blocks, arguments, opcode: opcode.first(), characters })
    }

    fn text(&self, fragment: &DiagnosticFragment) -> &'a [u16] {
        &self.characters[fragment.start as usize..fragment.start as usize + fragment.length as usize]
    }

    fn argument(&self, output: &mut Composition<'_>) -> Result<(), Error> {
        match self.argument {
            0 => output.ascii("NULL")?,
            1 => {
                output.text(self.text(&self.arguments[0]))?;
                if self.arguments.len() == 2 {
                    output.ascii(" ")?;
                    output.text(self.text(&self.arguments[1]))?;
                }
            }
            2 => {
                output.text(self.text(&self.arguments[0]))?;
                output.ascii(" ")?;
                output.text(self.text(&self.arguments[1]))?;
                output.ascii("::")?;
                output.text(self.text(&self.arguments[2]))?;
            }
            3 => {
                output.ascii("Label")?;
                output.text(self.text(&self.arguments[0]))?;
            }
            4 => {
                output.ascii("Labels")?;
                for (index, argument) in self.arguments.iter().enumerate() {
                    if index != 0 { output.ascii(",")?; }
                    output.text(self.text(argument))?;
                }
            }
            5 => {
                output.text(self.text(&self.arguments[0]))?;
                output.ascii(" (")?;
                output.text(self.text(&self.arguments[1]))?;
                output.ascii(")")?;
            }
            6 => output.text(self.text(&self.arguments[0]))?,
            _ => return Err(Error::Invalid),
        }
        Ok(())
    }

    fn compose(&self, argument_length: usize, output: &mut Composition<'_>) -> Result<(), Error> {
        let Some(opcode) = self.opcode else { return self.argument(output); };
        output.text(self.text(opcode))?;
        if argument_length != 0 { output.ascii(" ")?; }
        self.argument(output)?;
        if !self.labels.is_empty() || !self.blocks.is_empty() {
            output.ascii(" [")?;
            for (index, label) in self.labels.iter().enumerate() {
                if index != 0 { output.ascii(", ")?; }
                output.ascii("Label")?;
                output.text(self.text(label))?;
            }
            for (index, block) in self.blocks.iter().enumerate() {
                if index != 0 || !self.labels.is_empty() { output.ascii(", ")?; }
                output.ascii("EX_")?;
                output.block_name(self.text(block))?;
            }
            output.ascii("]")?;
        }
        Ok(())
    }
}

/// All ranges are caller owned and disjoint. Format, fragments and UTF-16 text
/// stay immutable for the call. Fragment ranges within text may overlap. No
/// pointers are retained. Null output queries size; a fill validates every
/// fragment and measures the whole composition before writing any output.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_diagnostic_format_v1(
    format: *const DiagnosticFormat, fragments: *const DiagnosticFragment, fragment_count: u32,
    characters: *const u16, character_count: u32, output: *mut u16, capacity: u32, length: *mut u32,
) -> u32 {
    boundary(|| {
        disjoint(&[range(format, 1)?, range(fragments, fragment_count)?, range(characters, character_count)?,
            range(output, capacity)?, range(length, 1)?])?;
        let plan = InstructionFormat::new(unsafe { format.read() },
            unsafe { input(fragments, fragment_count) }, unsafe { input(characters, character_count) })?;
        let mut argument = Composition { output: None, position: 0 };
        plan.argument(&mut argument)?;
        let mut measure = Composition { output: None, position: 0 };
        plan.compose(argument.position, &mut measure)?;
        if !output.is_null() {
            if measure.position > capacity as usize { return Err(Error::Capacity); }
            if measure.position != 0 {
                let mut writer = Composition {
                    output: Some(unsafe { std::slice::from_raw_parts_mut(output, measure.position) }), position: 0,
                };
                plan.compose(argument.position, &mut writer)?;
            }
        }
        unsafe { length.write(measure.position as u32); }
        Ok(())
    })
}

#[derive(Clone, Copy)]
#[repr(C)]
pub struct DiagnosticState {
    indent: i32,
    depth: u32,
}

#[repr(C)]
pub struct DiagnosticResult {
    length: u32,
    required_depth: u32,
    error: u32,
    indent: i32,
}

#[derive(Clone, Copy)]
struct Line<'a> {
    indent: i32,
    prefix: &'static str,
    text: &'a [u16],
    suffix: &'static str,
}

struct Layout<'a> {
    lines: [Line<'a>; 2],
    count: usize,
    next: DiagnosticState,
    push: Option<u32>,
}

// Domain failures are replayed through CLR operations by the bridge. In
// particular, an empty handler stack fails before a negative indentation does.
fn layout<'a>(operation: u32, block: i32, state: DiagnosticState, handlers: &[u32], text: &'a [u16])
    -> Result<Layout<'a>, (u32, i32)>
{
    let line = Line { indent: state.indent, prefix: "", text: &[], suffix: "" };
    let mut result = Layout { lines: [line; 2], count: 1, next: state, push: None };
    match operation {
        0 => {
            result.lines[0].prefix = ".locals init (";
            result.next.indent = state.indent.wrapping_add(4);
        }
        1 => result.lines[0].text = text,
        2 => {
            result.next.indent = state.indent.wrapping_sub(4);
            result.lines[0].indent = result.next.indent;
            result.lines[0].prefix = ")";
        }
        3 => match block {
            0..=4 => {
                result.lines[0].prefix = match block {
                    0 => ".try", 1 => "catch ", 2 => "filter", 3 => "fault", _ => "finally",
                };
                if block == 1 { result.lines[0].text = text; }
                result.lines[1].prefix = "{";
                result.count = 2;
                result.next.indent = state.indent.wrapping_add(2);
                result.next.depth = state.depth + 1;
                result.push = Some(block as u32);
            }
            5 => {
                result.next.indent = state.indent.wrapping_sub(2);
                let handler = handlers.last().ok_or((2, result.next.indent))?;
                result.lines[0].indent = result.next.indent;
                result.lines[0].prefix = "} // end ";
                result.lines[0].suffix = match handler {
                    0 => ".try", 1 => "handler (catch)", 2 => "handler (filter)",
                    3 => "handler (fault)", 4 => "handler (finally)",
                    _ => return Err((4, result.next.indent)),
                };
                result.next.depth -= 1;
            }
            _ => return Err((1, state.indent)),
        },
        _ => return Err((4, state.indent)),
    }
    for line in &result.lines[..result.count] {
        if line.indent < 0 { return Err((3, line.indent)); }
    }
    Ok(result)
}

fn length(plan: &Layout<'_>, newline: &[u16]) -> Result<usize, Error> {
    let mut result = 0usize;
    for line in &plan.lines[..plan.count] {
        for count in [line.indent as usize, line.prefix.len(), line.text.len(), line.suffix.len(), newline.len()] {
            result = result.checked_add(count).ok_or(Error::Capacity)?;
        }
    }
    if result > i32::MAX as usize { return Err(Error::Capacity); }
    Ok(result)
}

fn write(plan: &Layout<'_>, newline: &[u16], output: &mut [u16]) {
    let mut position = 0;
    for line in &plan.lines[..plan.count] {
        let end = position + line.indent as usize;
        output[position..end].fill(b' ' as u16);
        position = end;
        // All prefixes and suffixes are ASCII constants; dynamic text stays UTF-16.
        for unit in line.prefix.bytes().map(u16::from).chain(line.text.iter().copied())
            .chain(line.suffix.bytes().map(u16::from)).chain(newline.iter().copied())
        {
            output[position] = unit;
            position += 1;
        }
    }
}

/// State, handler storage, output and result are writable, mutually disjoint
/// caller-owned ranges. Text and newline are immutable UTF-16 ranges, may alias
/// each other, and must not overlap writable ranges. No pointers are retained.
/// Null output queries size and required stack capacity without changing state.
/// A fill publishes state only after validating and writing the complete layout.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_diagnostic_layout_v1(
    operation: u32, block: i32, text: *const u16, text_count: u32,
    newline: *const u16, newline_count: u32,
    state: *mut DiagnosticState, handlers: *mut u32, handler_capacity: u32,
    output: *mut u16, capacity: u32, result: *mut DiagnosticResult,
) -> u32 {
    boundary(|| {
        let writable = [range(state, 1)?, range(handlers, handler_capacity)?, range(output, capacity)?, range(result, 1)?];
        disjoint(&writable)?;
        for source in [range(text, text_count)?, range(newline, newline_count)?] {
            for destination in writable { disjoint(&[source, destination])?; }
        }
        let current = unsafe { state.read() };
        if current.depth > handler_capacity || current.depth >= i32::MAX as u32 { return Err(Error::Invalid); }
        let plan = match layout(operation, block, current,
            unsafe { input(handlers, current.depth) }, unsafe { input(text, text_count) })
        {
            Ok(plan) => plan,
            Err((4, _)) => return Err(Error::Invalid),
            Err((error, indent)) => {
                unsafe { result.write(DiagnosticResult { length: 0, required_depth: current.depth, error, indent }); }
                return Ok(());
            }
        };
        let newline = unsafe { input(newline, newline_count) };
        let required = length(&plan, newline)?;
        let required_depth = current.depth.max(plan.next.depth);
        if !output.is_null() {
            if required > capacity as usize || required_depth > handler_capacity { return Err(Error::Capacity); }
            if required != 0 {
                write(&plan, newline, unsafe { std::slice::from_raw_parts_mut(output, required) });
            }
            unsafe {
                if let Some(handler) = plan.push { handlers.add(current.depth as usize).write(handler); }
                state.write(plan.next);
            }
        }
        unsafe { result.write(DiagnosticResult {
            length: required as u32, required_depth, error: 0, indent: plan.next.indent,
        }); }
        Ok(())
    })
}
