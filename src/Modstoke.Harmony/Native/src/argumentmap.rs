// Copyright (c) 2026 Alex Cooper. MIT license.
use crate::{boundary, disjoint, input, range, Error};

#[repr(C)]
#[derive(Clone, Copy)]
pub struct Request {
    stage: u32,
    attribute_present: u32,
    attribute_name: u32,
    fallback_name: u32,
    attribute_index: i32,
    names_present: u32,
}

#[repr(C)]
pub struct Resolution {
    action: u32,
    source: u32,
    source_index: i32,
    index: i32,
}

fn resolve(request: Request, names: &[u32]) -> Result<Resolution, Error> {
    if request.stage > 5 || request.attribute_present > 1 || request.names_present > 1
        || (request.names_present == 0 && !names.is_empty()) || names.len() > i32::MAX as usize {
        return Err(Error::Invalid);
    }
    let mut result = Resolution { action: 0, source: 0, source_index: -1, index: -1 };
    let mut name = 0;
    // Stages: parameter, method, declaring type, name only, dynamic, plain fallback.
    // IDs: zero is null; one is empty. Every selectable name has an ordinal
    // identity; names that cannot be selected may share an unmatched identity.
    if request.stage <= 3 && request.attribute_present != 0 {
        if request.attribute_name > 1 {
            name = request.attribute_name;
            result.source = 1;
        } else if request.attribute_index >= 0 {
            if request.stage == 0 && request.names_present == 0 {
                result.action = 3; // Parameter indexing dereferences the null array.
                return Ok(result);
            }
            if let Some(&indexed) = names.get(request.attribute_index as usize) {
                name = indexed;
                result.source = 2;
                result.source_index = request.attribute_index;
            }
        }
    }
    if request.stage == 3 { return Ok(result); }
    if name == 0 {
        match request.stage {
            0 | 1 => {
                // A null resolution alone permits reflection of the next scope.
                result.action = request.stage + 1;
                result.source = 0;
                result.source_index = -1;
                return Ok(result);
            }
            _ => {
                name = request.fallback_name;
                result.source = 3;
                result.source_index = -1;
            }
        }
    }
    // DynamicMethod searches for a null parameter name. Ordinary fallback does not.
    if name == 0 && request.stage != 4 { return Ok(result); }
    if request.names_present == 0 {
        result.action = 4; // Array.IndexOf rejects null even when the name is null.
        return Ok(result);
    }
    result.index = names.iter().position(|&candidate| candidate == name)
        .map_or(-1, |index| index as i32);
    Ok(result)
}

/// Resolve one already-selected attribute and its next lazy scope, or its final
/// parameter index. Managed enumeration stops at the first matching attribute;
/// this call never evaluates attributes from lower-precedence scopes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mh_argumentmap_resolve_v1(
    request: *const Request, names: *const u32, name_count: u32, output: *mut Resolution,
) -> u32 {
    boundary(|| {
        disjoint(&[range(request, 1)?, range(names, name_count)?, range(output, 1)?])?;
        let result = resolve(unsafe { request.read() }, unsafe { input(names, name_count) })?;
        unsafe { output.write(result); }
        Ok(())
    })
}
