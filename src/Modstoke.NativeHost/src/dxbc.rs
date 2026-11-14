//! DXBC shader containers: the checksum the runtime checks, and the changes the native host makes to the game's
//! shaders on DirectX 12.
//!
//! Indexed constant buffer reads. DirectX 11 returns zero for a constant buffer read past the buffer's end. On
//! DirectX 12 the engine binds constant buffers by GPU address, which has no bounds, so such a read goes wherever the
//! index points. HDRP's lit shaders look up planar reflection probes as `cb3[index * 4 + n]` and `cb3[index + 80]`
//! with `index = abs(envIndex) - 1` taken from the probe's light data. Unbuilt or stale light data gives an index far
//! past the 160-vector buffer, and the GPU stops with an MMU fault in the shader, which is what every DirectX 12 hang
//! in the driver's log was. Each read `cbN[rX.c + k]` from a dynamically indexed buffer now goes through
//! `umin rT.c, rX.c, l(4095 - k)` into a register of its own. The bound is the largest constant buffer D3D allows
//! (4096 vectors, 64 KB), not the declared size: Unity declares its instancing arrays with two entries and reads past
//! them up to the size it binds, which D3D allows. A buffer of its own takes a whole 64 KB allocation, so a read
//! within 64 KB of its start is mapped memory. Immediate constant buffer reads are bounded by the buffer's size.
//!
//! Tile light counts. HDRP's fine-pruned tile light list keeps, per 16x16 tile and light category, a light count in
//! the low 16 bits of the tile's first dword, followed by 16-bit light indices. The lit shaders read the count as
//!
//! ```text
//! ishl          rT.c, rT.c, l(k)         // tile offset * dwords per tile (1 << k)
//! ld_structured rN.c, rT.c, l(0), tS     // stride-4 structured buffer
//! and           rM.c, rN.c, l(0x0000ffff)
//! ```
//!
//! and run their punctual, area and reflection-probe loops that many times. A tile holds at most 2 * (1 << k) - 1
//! lights, so anything larger means the list was not built when the draw read it, and each pixel would loop up to
//! 65535 times over garbage lights. A `umin` after each such read keeps the count within a tile.

const OP_BREAK: u32 = 2;
const OP_BREAKC: u32 = 3;
const OP_CALL: u32 = 4;
const OP_CALLC: u32 = 5;
const OP_CASE: u32 = 6;
const OP_CONTINUE: u32 = 7;
const OP_CONTINUEC: u32 = 8;
const OP_DEFAULT: u32 = 10;
const OP_ELSE: u32 = 18;
const OP_ENDIF: u32 = 21;
const OP_ENDLOOP: u32 = 22;
const OP_ENDSWITCH: u32 = 23;
const OP_IF: u32 = 31;
const OP_LABEL: u32 = 44;
const OP_LOOP: u32 = 48;
const OP_RET: u32 = 62;
const OP_RETC: u32 = 63;
const OP_SWITCH: u32 = 76;

const OP_AND: u32 = 1;
const OP_IMUL: u32 = 38;
const OP_ISHL: u32 = 41;
const OP_CUSTOMDATA: u32 = 53;
const OP_SINCOS: u32 = 77;
const OP_UDIV: u32 = 78;
const OP_UMUL: u32 = 81;
const OP_UMIN: u32 = 84;
const OP_UADDC: u32 = 132;
const OP_USUBB: u32 = 133;
const OP_SWAPC: u32 = 142;
const OP_DCL_CONSTANT_BUFFER: u32 = 89;
const OP_DCL_TEMPS: u32 = 104;
const OP_DCL_RESOURCE_STRUCTURED: u32 = 162;
const OP_LD_STRUCTURED: u32 = 167;

const OPERAND_TEMP: u32 = 0;
const OPERAND_INPUT: u32 = 1;
const OPERAND_IMMEDIATE32: u32 = 4;
const OPERAND_IMMEDIATE64: u32 = 5;
const OPERAND_RESOURCE: u32 = 7;
const OPERAND_CONSTANT_BUFFER: u32 = 8;
const OPERAND_IMMEDIATE_CONSTANT_BUFFER: u32 = 9;

const CUSTOMDATA_IMMEDIATE_CONSTANT_BUFFER: u32 = 3;

/// D3D's largest constant buffer, in 16-byte vectors.
const CONSTANT_BUFFER_VECTORS: u32 = 4096;

/// How far back from a count read the instruction that scaled the tile offset may be.
const OFFSET_SEARCH: usize = 24;

/// What `harden` changed in a shader.
#[derive(Clone, Copy, Default)]
pub struct Hardened {
    pub constant_reads: usize,
    pub light_counts: usize,
}

/// The container with its indexed constant buffer reads kept inside their buffers and its tile light counts
/// bounded; `None` when there is nothing to change or the container is not one this understands.
pub fn harden(container: &[u8]) -> Option<(Vec<u8>, Hardened)> {
    let layout = Layout::read(container)?;
    let mut tokens: Vec<u32> = container[layout.program..layout.program + layout.program_len]
        .chunks_exact(4)
        .map(|word| u32::from_le_bytes(word.try_into().unwrap()))
        .collect();
    let program_len = *tokens.get(1)? as usize;
    if program_len < 2 || program_len > tokens.len() {
        return None;
    }
    let instructions = instructions(&tokens[..program_len])?;

    // (token index, order at that index, instruction)
    let mut inserts: Vec<(usize, u8, [u32; 7])> = Vec::new();
    let sites = count_reads(&tokens, &instructions);
    for site in &sites {
        inserts.push((site.after, 0, [
            OP_UMIN | 7 << 24,
            site.destination[0],
            site.destination[1],
            select(OPERAND_TEMP, site.component),
            site.register,
            1 | OPERAND_IMMEDIATE32 << 12,
            site.limit,
        ]));
    }
    let constant_reads = clamp_constant_reads(&mut tokens, &instructions, &mut inserts);
    if inserts.is_empty() {
        return None;
    }
    inserts.sort_by_key(|&(at, order, _)| (at, order));

    let mut program = Vec::with_capacity(program_len + inserts.len() * 7);
    let mut copied = 0;
    for (at, _, instruction) in &inserts {
        program.extend_from_slice(&tokens[copied..*at]);
        program.extend_from_slice(instruction);
        copied = *at;
    }
    program.extend_from_slice(&tokens[copied..program_len]);
    program[1] = program.len() as u32;

    let added = (program.len() - program_len) * 4;
    let mut patched = Vec::with_capacity(layout.total + added);
    patched.extend_from_slice(&container[..layout.program]);
    for token in &program {
        patched.extend_from_slice(&token.to_le_bytes());
    }
    patched.extend_from_slice(&container[layout.program + program_len * 4..layout.total]);

    let grow = |bytes: &mut Vec<u8>, at: usize| {
        let value = u32::from_le_bytes(bytes[at..at + 4].try_into().unwrap()) + added as u32;
        bytes[at..at + 4].copy_from_slice(&value.to_le_bytes());
    };
    grow(&mut patched, 24);
    grow(&mut patched, layout.program - 4);
    for (index, &offset) in layout.chunk_offsets.iter().enumerate() {
        if offset > layout.program {
            grow(&mut patched, 32 + index * 4);
        }
    }
    let sum = checksum(&patched);
    patched[4..20].copy_from_slice(&sum);
    Some((patched, Hardened { constant_reads, light_counts: sites.len() }))
}

/// A source operand token selecting one component of a register.
fn select(kind: u32, component: u32) -> u32 {
    2 | 2 << 2 | component << 4 | kind << 12 | 1 << 20
}

/// Points every indexed read of a dynamically indexed constant buffer, and of the immediate constant buffer, at a
/// clamped copy of its index in a register added for it, and queues the `umin` that makes the copy. Returns how
/// many reads were changed; shaders with no single temp declaration (hull shader phases) are left alone.
fn clamp_constant_reads(tokens: &mut [u32], instructions: &[(usize, usize)], inserts: &mut Vec<(usize, u8, [u32; 7])>) -> usize {
    // Dynamically indexed constant buffer slots, each bounded by D3D's largest buffer rather than its declared size.
    let mut sizes: Vec<(u32, u32)> = Vec::new();
    let mut immediate_size = None;
    let mut temps = Vec::new();
    for &(at, length) in instructions {
        match tokens[at] & 0x7ff {
            OP_DCL_CONSTANT_BUFFER if tokens[at] >> 11 & 1 == 1 => {
                if let Some(buffer) = Operand::read(&tokens[..at + length], at + 1) {
                    if buffer.kind == OPERAND_CONSTANT_BUFFER && buffer.len == 3 && tokens[at + 1] >> 20 & 3 == 2 {
                        sizes.push((tokens[at + 2], CONSTANT_BUFFER_VECTORS));
                    }
                }
            }
            OP_CUSTOMDATA if tokens[at] >> 11 == CUSTOMDATA_IMMEDIATE_CONSTANT_BUFFER => {
                immediate_size = Some(((length - 2) / 4) as u32);
            }
            OP_DCL_TEMPS if length == 2 => temps.push(at),
            _ => {}
        }
    }
    let [temps_at] = temps[..] else { return 0 };
    if sizes.is_empty() && immediate_size.is_none() {
        return 0;
    }
    let added = tokens[temps_at + 1];

    let mut changed = 0;
    for &(at, length) in instructions {
        let opcode = tokens[at] & 0x7ff;
        if opcode == OP_CUSTOMDATA || (88..=106).contains(&opcode) || (143..=162).contains(&opcode) {
            continue;
        }
        // Up to four distinct indices per instruction, one component of the added register each.
        let mut lanes: Vec<(u32, u32, u32, u32)> = Vec::new();
        for operand in operands(tokens, at, length) {
            let Some(relative) = operand.relative else { continue };
            let size = match operand.kind {
                OPERAND_CONSTANT_BUFFER => operand.slot.and_then(|slot| sizes.iter().find(|&&(s, _)| s == slot)).map(|&(_, size)| size),
                OPERAND_IMMEDIATE_CONSTANT_BUFFER => immediate_size,
                _ => None,
            };
            let index = relative.operand;
            let (Some(size), Some(register), Components::Select(component)) = (size, index.register, index.components) else { continue };
            if !matches!(index.kind, OPERAND_TEMP | OPERAND_INPUT) || index.len != 2 || relative.offset >= size {
                continue;
            }
            let limit = size - 1 - relative.offset;
            let key = (index.kind, register, component, limit);
            let lane = match lanes.iter().position(|&known| known == key) {
                Some(lane) => lane,
                None if lanes.len() < 4 => {
                    lanes.push(key);
                    lanes.len() - 1
                }
                None => continue,
            } as u32;
            tokens[relative.at] = select(OPERAND_TEMP, lane);
            tokens[relative.at + 1] = added;
            changed += 1;
        }
        for (lane, &(kind, register, component, limit)) in lanes.iter().enumerate() {
            inserts.push((at, 1, [
                OP_UMIN | 7 << 24,
                2 | (1 << lane) << 4 | OPERAND_TEMP << 12 | 1 << 20,
                added,
                select(kind, component),
                register,
                1 | OPERAND_IMMEDIATE32 << 12,
                limit,
            ]));
        }
    }
    if changed > 0 {
        tokens[temps_at + 1] = added + 1;
    }
    changed
}

struct Layout {
    total: usize,
    chunk_offsets: Vec<usize>,
    /// Start of the SHEX/SHDR chunk's data and its size in bytes.
    program: usize,
    program_len: usize,
}

impl Layout {
    fn read(container: &[u8]) -> Option<Layout> {
        let word = |at: usize| container.get(at..at + 4).map(|bytes| u32::from_le_bytes(bytes.try_into().unwrap()));
        if container.get(..4)? != b"DXBC" || word(20)? != 1 {
            return None;
        }
        let total = word(24)? as usize;
        let count = word(28)? as usize;
        if total > container.len() || 32 + count * 4 > total {
            return None;
        }
        let mut chunk_offsets = Vec::with_capacity(count);
        let mut program = None;
        for index in 0..count {
            let offset = word(32 + index * 4)? as usize;
            let size = word(offset + 4)? as usize;
            if offset + 8 + size > total {
                return None;
            }
            let tag = &container[offset..offset + 4];
            if tag == b"SHEX" || tag == b"SHDR" {
                if program.is_some() || size % 4 != 0 {
                    return None;
                }
                program = Some((offset + 8, size));
            }
            chunk_offsets.push(offset);
        }
        let (program, program_len) = program?;
        Some(Layout { total, chunk_offsets, program, program_len })
    }
}

/// Start and length of every instruction after the version and length tokens.
fn instructions(program: &[u32]) -> Option<Vec<(usize, usize)>> {
    let mut found = Vec::new();
    let mut at = 2;
    while at < program.len() {
        let opcode = program[at] & 0x7ff;
        let length = if opcode == OP_CUSTOMDATA { *program.get(at + 1)? as usize } else { (program[at] >> 24 & 0x7f) as usize };
        if length == 0 || at + length > program.len() {
            return None;
        }
        found.push((at, length));
        at += length;
    }
    Some(found)
}

#[derive(Clone, Copy)]
enum Components {
    None,
    One,
    Mask(u32),
    Swizzle([u32; 4]),
    Select(u32),
}

#[derive(Clone, Copy)]
struct Operand {
    kind: u32,
    components: Components,
    /// The register number, when the operand has one immediate index and no modifier.
    register: Option<u32>,
    /// The first index when it is immediate: a constant buffer's slot.
    slot: Option<u32>,
    /// The last index that holds a register, with the immediate offset added to it.
    relative: Option<Relative>,
    immediate: [u32; 4],
    len: usize,
}

#[derive(Clone, Copy)]
struct Relative {
    /// Token index of the index register's operand.
    at: usize,
    operand: Leaf,
    offset: u32,
}

/// An index register operand: only what a clamp needs, so `Operand` does not contain itself.
#[derive(Clone, Copy)]
struct Leaf {
    kind: u32,
    components: Components,
    register: Option<u32>,
    len: usize,
}

impl Operand {
    fn read(tokens: &[u32], at: usize) -> Option<Operand> {
        let token = *tokens.get(at)?;
        let mut len = 1;
        let modified = token >> 31 != 0;
        if modified {
            while *tokens.get(at + len)? >> 31 != 0 {
                len += 1;
            }
            len += 1;
        }
        let count = token & 3;
        let components = match count {
            0 => Components::None,
            1 => Components::One,
            2 => match token >> 2 & 3 {
                0 => Components::Mask(token >> 4 & 0xf),
                1 => Components::Swizzle([token >> 4 & 3, token >> 6 & 3, token >> 8 & 3, token >> 10 & 3]),
                2 => Components::Select(token >> 4 & 3),
                _ => return None,
            },
            _ => return None,
        };
        let kind = token >> 12 & 0xff;
        let mut immediate = [0; 4];
        let values = match count {
            1 => 1,
            2 => 4,
            _ => 0,
        };
        if kind == OPERAND_IMMEDIATE32 {
            for (index, value) in immediate.iter_mut().take(values).enumerate() {
                *value = *tokens.get(at + len + index)?;
            }
            len += values;
        } else if kind == OPERAND_IMMEDIATE64 {
            len += values * 2;
        }
        let dimensions = token >> 20 & 3;
        let (mut register, mut slot, mut relative) = (None, None, None);
        for dimension in 0..dimensions {
            let mut index = |skip: usize, offset: u32| -> Option<usize> {
                let operand = Operand::read(tokens, at + len + skip)?;
                let leaf = Leaf { kind: operand.kind, components: operand.components, register: operand.register, len: operand.len };
                relative = Some(Relative { at: at + len + skip, operand: leaf, offset });
                Some(skip + operand.len)
            };
            let used = match token >> (22 + dimension * 3) & 7 {
                0 => {
                    let value = *tokens.get(at + len)?;
                    if dimension == 0 {
                        slot = Some(value);
                    }
                    if dimensions == 1 && !modified {
                        register = Some(value);
                    }
                    1
                }
                1 => 2,
                2 => index(0, 0)?,
                3 => index(1, *tokens.get(at + len)?)?,
                4 => 2 + Operand::read(tokens, at + len + 2)?.len,
                _ => return None,
            };
            len += used;
        }
        if at + len > tokens.len() {
            return None;
        }
        Some(Operand { kind, components, register, slot, relative, immediate, len })
    }

    /// The one component a temp destination writes.
    fn written(&self) -> Option<u32> {
        match self.components {
            Components::Mask(mask) if mask.count_ones() == 1 => Some(mask.trailing_zeros()),
            _ => None,
        }
    }

    /// The component a source supplies to destination component `lane`.
    fn read_for(&self, lane: u32) -> Option<u32> {
        match self.components {
            Components::Select(component) => Some(component),
            Components::Swizzle(swizzle) => Some(swizzle[lane as usize]),
            _ => None,
        }
    }

    fn scalar(&self, lane: u32) -> Option<u32> {
        match (self.kind, self.components) {
            (OPERAND_IMMEDIATE32, Components::One) => Some(self.immediate[0]),
            (OPERAND_IMMEDIATE32, Components::Swizzle(_) | Components::Mask(_) | Components::Select(_)) => {
                Some(self.immediate[lane as usize])
            }
            _ => None,
        }
    }
}

/// The operands of the instruction at `at` (length `length`), after its opcode and any extended opcode tokens.
fn operands(tokens: &[u32], at: usize, length: usize) -> Vec<Operand> {
    let end = at + length;
    let mut position = at + 1;
    if tokens[at] >> 31 != 0 {
        while position < end && tokens[position] >> 31 != 0 {
            position += 1;
        }
        position += 1;
    }
    let mut found = Vec::new();
    while position < end {
        match Operand::read(&tokens[..end], position) {
            Some(operand) => {
                position += operand.len;
                found.push(operand);
            }
            None => break,
        }
    }
    found
}

struct Site {
    /// Token index just past the `and`, where the `umin` goes.
    after: usize,
    destination: [u32; 2],
    register: u32,
    component: u32,
    limit: u32,
}

fn count_reads(tokens: &[u32], instructions: &[(usize, usize)]) -> Vec<Site> {
    let mut light_lists = Vec::new();
    for &(at, length) in instructions {
        if tokens[at] & 0x7ff == OP_DCL_RESOURCE_STRUCTURED && length == 4 {
            if let Some(resource) = Operand::read(&tokens[..at + length], at + 1) {
                if resource.kind == OPERAND_RESOURCE && tokens[at + 3] == 4 {
                    light_lists.extend(resource.register);
                }
            }
        }
    }
    if light_lists.is_empty() {
        return Vec::new();
    }

    let mut sites = Vec::new();
    for (index, pair) in instructions.windows(2).enumerate() {
        let ((load_at, load_len), (and_at, and_len)) = (pair[0], pair[1]);
        if tokens[load_at] & 0x7ff != OP_LD_STRUCTURED || tokens[and_at] & 0x7ff != OP_AND {
            continue;
        }
        let load = operands(tokens, load_at, load_len);
        let mask = operands(tokens, and_at, and_len);
        let [loaded, address, offset, resource] = load[..] else { continue };
        let [masked, source, bits] = mask[..] else { continue };
        let read = (|| {
            if loaded.kind != OPERAND_TEMP || address.kind != OPERAND_TEMP || resource.kind != OPERAND_RESOURCE {
                return None;
            }
            if !light_lists.contains(&resource.register?) || offset.scalar(0)? != 0 {
                return None;
            }
            let loaded_lane = loaded.written()?;
            let (address_register, address_lane) = (address.register?, address.read_for(0)?);
            if masked.kind != OPERAND_TEMP || source.kind != OPERAND_TEMP || source.register? != loaded.register? {
                return None;
            }
            let lane = masked.written()?;
            if source.read_for(lane)? != loaded_lane || bits.scalar(lane)? != 0xffff {
                return None;
            }
            let shift = tile_scale(tokens, &instructions[index.saturating_sub(OFFSET_SEARCH)..index], address_register, address_lane)?;
            let destination_at = and_at + (and_len - masked.len - source.len - bits.len);
            Some(Site {
                after: and_at + and_len,
                destination: [tokens[destination_at], tokens[destination_at + 1]],
                register: masked.register?,
                component: lane,
                limit: (2 << shift) - 1,
            })
        })();
        if let Some(site) = read {
            if masked.len == 2 {
                sites.push(site);
            }
        }
    }
    sites
}

/// The shift `k` of the `ishl` that last wrote `register.lane` before the read, searching back within its block.
fn tile_scale(tokens: &[u32], before: &[(usize, usize)], register: u32, lane: u32) -> Option<u32> {
    for &(at, length) in before.iter().rev() {
        let opcode = tokens[at] & 0x7ff;
        if matches!(
            opcode,
            OP_BREAK | OP_BREAKC | OP_CALL | OP_CALLC | OP_CASE | OP_CONTINUE | OP_CONTINUEC | OP_DEFAULT | OP_ELSE
                | OP_ENDIF | OP_ENDLOOP | OP_ENDSWITCH | OP_IF | OP_LABEL | OP_LOOP | OP_RET | OP_RETC | OP_SWITCH
        ) {
            return None;
        }
        let operands = operands(tokens, at, length);
        let destinations = if matches!(opcode, OP_IMUL | OP_SINCOS | OP_UDIV | OP_UMUL | OP_SWAPC | OP_UADDC | OP_USUBB) { 2 } else { 1 };
        let writes = operands.iter().take(destinations).any(|operand| {
            operand.kind == OPERAND_TEMP
                && operand.register == Some(register)
                && matches!(operand.components, Components::Mask(mask) if mask >> lane & 1 != 0)
        });
        if !writes {
            continue;
        }
        if opcode != OP_ISHL || operands.len() != 3 {
            return None;
        }
        let shift = operands[2].scalar(lane)?;
        return (3..=8).contains(&shift).then_some(shift);
    }
    None
}

/// The checksum D3D stores at bytes 4..20: MD5 over everything after it, with its own final block.
pub fn checksum(container: &[u8]) -> [u8; 16] {
    let data = &container[20..];
    let mut state = [0x6745_2301u32, 0xefcd_ab89, 0x98ba_dcfe, 0x1032_5476];
    let mut blocks = data.chunks_exact(64);
    for block in &mut blocks {
        md5_block(&mut state, block);
    }
    let tail = blocks.remainder();
    let bits = (data.len() as u32).wrapping_mul(8);
    let mut last = [0u8; 64];
    if tail.len() >= 56 {
        last[..tail.len()].copy_from_slice(tail);
        last[tail.len()] = 0x80;
        md5_block(&mut state, &last);
        last = [0; 64];
    } else {
        last[4..4 + tail.len()].copy_from_slice(tail);
        last[4 + tail.len()] = 0x80;
    }
    last[..4].copy_from_slice(&bits.to_le_bytes());
    last[60..].copy_from_slice(&(bits >> 2 | 1).to_le_bytes());
    md5_block(&mut state, &last);
    let mut sum = [0u8; 16];
    for (bytes, word) in sum.chunks_exact_mut(4).zip(state) {
        bytes.copy_from_slice(&word.to_le_bytes());
    }
    sum
}

fn md5_block(state: &mut [u32; 4], block: &[u8]) {
    const SHIFTS: [u32; 16] = [7, 12, 17, 22, 5, 9, 14, 20, 4, 11, 16, 23, 6, 10, 15, 21];
    const SINES: [u32; 64] = [
        0xd76aa478, 0xe8c7b756, 0x242070db, 0xc1bdceee, 0xf57c0faf, 0x4787c62a, 0xa8304613, 0xfd469501,
        0x698098d8, 0x8b44f7af, 0xffff5bb1, 0x895cd7be, 0x6b901122, 0xfd987193, 0xa679438e, 0x49b40821,
        0xf61e2562, 0xc040b340, 0x265e5a51, 0xe9b6c7aa, 0xd62f105d, 0x02441453, 0xd8a1e681, 0xe7d3fbc8,
        0x21e1cde6, 0xc33707d6, 0xf4d50d87, 0x455a14ed, 0xa9e3e905, 0xfcefa3f8, 0x676f02d9, 0x8d2a4c8a,
        0xfffa3942, 0x8771f681, 0x6d9d6122, 0xfde5380c, 0xa4beea44, 0x4bdecfa9, 0xf6bb4b60, 0xbebfbc70,
        0x289b7ec6, 0xeaa127fa, 0xd4ef3085, 0x04881d05, 0xd9d4d039, 0xe6db99e5, 0x1fa27cf8, 0xc4ac5665,
        0xf4292244, 0x432aff97, 0xab9423a7, 0xfc93a039, 0x655b59c3, 0x8f0ccc92, 0xffeff47d, 0x85845dd1,
        0x6fa87e4f, 0xfe2ce6e0, 0xa3014314, 0x4e0811a1, 0xf7537e82, 0xbd3af235, 0x2ad7d2bb, 0xeb86d391,
    ];
    let mut words = [0u32; 16];
    for (word, bytes) in words.iter_mut().zip(block.chunks_exact(4)) {
        *word = u32::from_le_bytes(bytes.try_into().unwrap());
    }
    let [mut a, mut b, mut c, mut d] = *state;
    for round in 0..64 {
        let (mix, word) = match round / 16 {
            0 => (b & c | !b & d, round),
            1 => (d & b | !d & c, (5 * round + 1) % 16),
            2 => (b ^ c ^ d, (3 * round + 5) % 16),
            _ => (c ^ (b | !d), 7 * round % 16),
        };
        let sum = mix.wrapping_add(a).wrapping_add(SINES[round]).wrapping_add(words[word]);
        a = d;
        d = c;
        c = b;
        b = b.wrapping_add(sum.rotate_left(SHIFTS[round / 16 * 4 + round % 4]));
    }
    for (value, add) in state.iter_mut().zip([a, b, c, d]) {
        *value = value.wrapping_add(add);
    }
}
