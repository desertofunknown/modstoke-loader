// Copyright (c) 2026 Alex Cooper. MIT license.
//! Resumable matcher traversal. Each yield is a managed callback/capture boundary;
//! the caller supplies its live cursor and list length on resumption.

use crate::{boundary, disjoint, input, range, Error};

const SEARCH: u32 = 0;
const MATCH: u32 = 1;
const SEQUENCE: u32 = 2;

const BEGIN: u32 = 0;
const SEARCH_PREDICATE: u32 = 1;
const MATCH_PREDICATE: u32 = 2;
const NULL_MATCHES: u32 = 3;
const DONE: u32 = 4;

#[repr(C)]
#[derive(Clone, Copy)]
pub struct MatcherState {
    mode: u32,
    action: u32,
    direction: i32,
    use_end: u32,
    match_count: i32,
    match_index: i32,
    instruction_index: i32,
    position: i32,
    reset_captures: u32,
    matched: u32,
}

fn valid(position: i32, length: i32) -> bool {
    position >= 0 && position < length
}

impl MatcherState {
    fn finish(&mut self, matched: bool) {
        self.action = DONE;
        self.matched = u32::from(matched);
    }

    fn next_search(&mut self, length: i32) {
        if valid(self.position, length) {
            self.action = SEARCH_PREDICATE;
        } else {
            self.finish(false);
        }
    }

    fn next_match(&mut self, length: i32, mut new_candidate: bool) {
        loop {
            if new_candidate {
                if !valid(self.position, length) {
                    self.finish(false);
                    return;
                }
                self.instruction_index = self.position;
                self.match_index = 0;
                self.reset_captures = 1;
            }

            // Null arrays must throw in managed code, after the capture reset,
            // but only when traversal actually enters a sequence.
            if self.match_count < 0 {
                self.action = NULL_MATCHES;
                return;
            }
            if self.match_index == self.match_count {
                if self.mode == MATCH && self.use_end != 0 {
                    self.position = self.position.wrapping_add(self.match_count.wrapping_sub(1));
                }
                self.finish(true);
                return;
            }
            if self.instruction_index < length {
                self.action = MATCH_PREDICATE;
                return;
            }
            if self.mode == SEQUENCE {
                self.finish(false);
                return;
            }
            // A candidate can run out of instructions after partial captures.
            // Advance the live cursor, not the sequence's local instruction index.
            self.position = self.position.wrapping_add(self.direction);
            new_candidate = true;
        }
    }

    fn validate(&self, length: i32, response: u32) -> Result<(), Error> {
        if length < 0 || self.mode > SEQUENCE || self.use_end > 1 || self.match_count < -1
            || self.match_index < 0 || (self.match_count >= 0 && self.match_index > self.match_count)
            || response > 2
            || ((self.mode == SEARCH || self.mode == MATCH) && self.direction != 1 && self.direction != -1)
        {
            return Err(Error::Invalid);
        }
        Ok(())
    }

    fn step(&mut self, position: i32, length: i32, response: u32) -> Result<(), Error> {
        self.validate(length, response)?;
        self.position = position;
        self.reset_captures = 0;
        self.matched = 0;
        match self.action {
            BEGIN => match self.mode {
                SEARCH => {
                    self.position = self.position.max(0);
                    self.next_search(length);
                }
                MATCH => {
                    self.position = self.position.max(0);
                    self.next_match(length, true);
                }
                SEQUENCE => {
                    if self.instruction_index < 0 {
                        self.finish(false);
                    } else {
                        self.reset_captures = 1;
                        self.next_match(length, false);
                    }
                }
                _ => return Err(Error::Invalid),
            },
            SEARCH_PREDICATE if self.mode == SEARCH && response < 2 => {
                if response != 0 {
                    self.finish(true);
                } else {
                    self.position = self.position.wrapping_add(self.direction);
                    self.next_search(length);
                }
            }
            MATCH_PREDICATE if self.mode == MATCH || self.mode == SEQUENCE => {
                if response == 2 {
                    // A callback-free first-opcode scan may seek directly to a
                    // candidate, including the forward/backward exhaustion sentinel.
                    if self.mode != MATCH || self.match_index != 0 {
                        return Err(Error::Invalid);
                    }
                    self.next_match(length, true);
                } else if response != 0 {
                    self.match_index = self.match_index.wrapping_add(1);
                    self.instruction_index = self.instruction_index.wrapping_add(1);
                    self.next_match(length, false);
                } else if self.mode == SEQUENCE {
                    self.finish(false);
                } else {
                    self.position = self.position.wrapping_add(self.direction);
                    self.next_match(length, true);
                }
            }
            _ => return Err(Error::Invalid),
        }
        Ok(())
    }

    fn exact_opcodes(&mut self, length: i32, offset: u32, codes: &[i32], pattern: &[i16]) -> Result<(), Error> {
        self.validate(length, 0)?;
        if self.mode == SEARCH || self.action != MATCH_PREDICATE || pattern.is_empty()
            || pattern.len() != self.match_count as usize || self.match_index >= self.match_count
            || offset as usize > length as usize || codes.len() > length as usize - offset as usize
            || codes.iter().any(|&code| code != i32::MIN && i16::try_from(code).is_err())
        {
            return Err(Error::Invalid);
        }

        // No names or callbacks occur in this batch, so only the final capture
        // reset is observable. Keep it across transitions and buffer boundaries.
        let mut reset_captures = self.reset_captures;
        while self.action == MATCH_PREDICATE {
            let Some(index) = self.instruction_index.checked_sub_unsigned(offset) else { break; };
            let Some(&code) = usize::try_from(index).ok().and_then(|index| codes.get(index)) else { break; };
            // A null instruction must fail through the original managed access,
            // with the cursor still on the candidate that reached it.
            if code == i32::MIN { break; }
            let matched = code == i32::from(pattern[self.match_index as usize]);
            self.step(self.position, length, u32::from(matched))?;
            reset_captures |= self.reset_captures;
        }
        self.reset_captures = reset_captures;
        Ok(())
    }
}

/// State is caller-owned POD. No references survive this call, and no managed
/// callbacks execute inside a native frame. Publish only a successful transition.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_matcher_step_v1(
    state: *mut MatcherState, position: i32, length: i32, response: u32,
) -> u32 {
    boundary(|| {
        range(state, 1)?;
        let mut next = unsafe { state.read() };
        next.step(position, length, response)?;
        unsafe { state.write(next) };
        Ok(())
    })
}

/// Resume a callback-free, unnamed exact-opcode pattern inside a caller-owned
/// instruction window. i32::MIN marks a null instruction; all other entries are
/// signed opcode values. Stop at a null, window boundary, or completed traversal.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_matcher_exact_opcodes_v1(
    state: *mut MatcherState, length: i32, offset: u32,
    codes: *const i32, code_count: u32, pattern: *const i16, pattern_count: u32,
) -> u32 {
    boundary(|| {
        disjoint(&[range(state, 1)?, range(codes, code_count)?, range(pattern, pattern_count)?])?;
        let mut next = unsafe { state.read() };
        next.exact_opcodes(length, offset, unsafe { input(codes, code_count) },
            unsafe { input(pattern, pattern_count) })?;
        unsafe { state.write(next) };
        Ok(())
    })
}
