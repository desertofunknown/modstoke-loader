// Copyright (c) 2026 Alex Cooper. MIT license.
// Dependency traversal adapted from HarmonyX; see ../../LICENSE and ../../LICENSE.Harmony.

use crate::{Error, Node};

struct Dependencies<'a> {
    before: &'a [u32],
    before_removed_start: usize,
    before_remaining: usize,
    after: &'a [u32],
}

impl Dependencies<'_> {
    fn first_unhandled(&mut self, nodes: &[Node], handled: &[bool]) -> Option<u32> {
        while let Some((&dependency, remaining)) = self.after.split_first() {
            if !handled[nodes[dependency as usize].method as usize] { return Some(dependency); }
            // Handled method identities never leave the set. These entries cannot block a
            // later pass, but their reciprocal before entries remain until an actual cull.
            self.after = remaining;
        }
        None
    }

    fn remove_before(&mut self, method: u32, nodes: &[Node], removed: &mut [bool]) {
        let flags = &mut removed[self.before_removed_start..self.before_removed_start + self.before.len()];
        for (&dependency, flag) in self.before.iter().zip(flags) {
            if !*flag && nodes[dependency as usize].method == method {
                *flag = true;
                self.before_remaining -= 1;
                break;
            }
        }
    }
}

fn row(edges: &[u32], start: u32, length: u32, count: usize) -> Result<&[u32], Error> {
    let start = start as usize;
    let end = start.checked_add(length as usize).ok_or(Error::Invalid)?;
    let row = edges.get(start..end).ok_or(Error::Invalid)?;
    if row.iter().any(|&edge| edge as usize >= count) { return Err(Error::Invalid); }
    Ok(row)
}

pub fn order(nodes: &[Node], edges: &[u32]) -> Result<(Vec<u32>, Vec<u32>), Error> {
    let count = nodes.len();
    let mut dependencies = Vec::new();
    dependencies.try_reserve_exact(count).map_err(|_| Error::Allocation)?;
    let mut before_count = 0usize;
    for node in nodes {
        if node.method as usize >= count { return Err(Error::Invalid); }
        let before = row(edges, node.before_start, node.before_count, count)?;
        let after = row(edges, node.after_start, node.after_count, count)?;
        let before_removed_start = before_count;
        before_count = before_count.checked_add(before.len()).ok_or(Error::Invalid)?;
        dependencies.push(Dependencies {
            before, before_removed_start, before_remaining: before.len(), after,
        });
    }
    // Each row owns its removal flags even if input descriptors share edge ranges.
    // Keep the caller's HashSet enumeration order and never copy its edge payload.
    let mut before_removed = Vec::new();
    before_removed.try_reserve_exact(before_count).map_err(|_| Error::Allocation)?;
    before_removed.resize(before_count, false);

    let mut queue = Vec::new();
    queue.try_reserve_exact(count).map_err(|_| Error::Allocation)?;
    queue.extend(0..count);
    let mut handled = Vec::new();
    handled.try_reserve_exact(count).map_err(|_| Error::Allocation)?;
    handled.resize(count, false);
    let mut waiting: Vec<usize> = Vec::new();
    let mut result = Vec::new();
    let mut culled = Vec::new();
    waiting.try_reserve_exact(count).map_err(|_| Error::Allocation)?;
    result.try_reserve_exact(count).map_err(|_| Error::Allocation)?;

    while !queue.is_empty() {
        for &node in &queue {
            if dependencies[node].first_unhandled(nodes, &handled).is_some() {
                waiting.push(node);
                continue;
            }
            result.push(node as u32);
            handled[nodes[node].method as usize] = true;
            if dependencies[node].before_remaining == 0 { continue; }

            let mut i = 0;
            while i < waiting.len() {
                let candidate = waiting[i];
                if dependencies[candidate].first_unhandled(nodes, &handled).is_some() { i += 1; continue; }
                // Harmony's List.Remove uses method equality rather than occurrence identity.
                let first_equal = waiting.iter().position(|&other| {
                    nodes[other].method == nodes[candidate].method
                }).ok_or(Error::Invalid)?;
                waiting.remove(first_equal);
                result.push(candidate as u32);
                handled[nodes[candidate].method as usize] = true;
                i = 0;
            }
        }

        let mut edge = None;
        for &node in waiting.iter().rev() {
            if let Some(dependency) = dependencies[node].first_unhandled(nodes, &handled) {
                edge = Some((node, dependency as usize));
                break;
            }
        }
        if let Some((node, dependency)) = edge {
            dependencies[node].after = &dependencies[node].after[1..];
            dependencies[dependency].remove_before(nodes[node].method, nodes, &mut before_removed);
            culled.try_reserve(2).map_err(|_| Error::Allocation)?;
            culled.extend_from_slice(&[dependency as u32, node as u32]);
        }
        queue.clear();
        std::mem::swap(&mut queue, &mut waiting);
    }
    if result.len() != count { return Err(Error::Invalid); }
    Ok((result, culled))
}
