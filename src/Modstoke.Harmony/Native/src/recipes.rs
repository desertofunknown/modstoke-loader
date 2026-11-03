// Copyright (c) 2026 Alex Cooper. MIT license.
// Values follow HarmonyX CodeInstruction and HarmonyManipulator semantics.

pub fn tables() -> ([u16; 256], [u32; 36], [u32; 16]) {
    let mut branches = [0; 256];
    for (i, code) in branches.iter_mut().enumerate() {
        *code = match i { 0x2b..=0x37 => (i + 0x0d) as u16, 0xde => 0xdd, _ => i as u16 };
    }
    // local load, local address, local store, argument load, argument address, argument store.
    let forms: [(u16, u16, Option<u16>); 6] = [
        (0x11, 0xfe0c, Some(0x06)), (0x12, 0xfe0d, None),
        (0x13, 0xfe0e, Some(0x0a)), (0x0e, 0xfe09, Some(0x02)),
        (0x0f, 0xfe0a, None), (0x10, 0xfe0b, None),
    ];
    let mut variables = [0; 36];
    for (operation, (short, long, implicit)) in forms.into_iter().enumerate() {
        for category in 0..6 {
            variables[operation * 6 + category] = if category < 4 && implicit.is_some() {
                u32::from(implicit.unwrap() + category as u16)
            } else if category < 5 { u32::from(short) | (1 << 16) }
            else { u32::from(long) | (2 << 16) };
        }
    }
    // Actions: direct, box, box temporary, address, indirect, indirect box, indirect box temporary.
    let mut arguments = [0; 16];
    for (bits, action) in arguments.iter_mut().enumerate() {
        let original_normal = bits & 1 != 0;
        let patch_normal = bits & 2 != 0;
        let boxing = bits & 4 != 0 && bits & 8 == 0;
        *action = match (original_normal, patch_normal, boxing) {
            (true, true, false) | (false, false, false) => 0,
            (true, true, true) => 1, (true, false, true) => 2,
            (true, false, false) => 3, (false, true, false) => 4,
            (false, true, true) => 5, (false, false, true) => 6,
        };
    }
    (branches, variables, arguments)
}
