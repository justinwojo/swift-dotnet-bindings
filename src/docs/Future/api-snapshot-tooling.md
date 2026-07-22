# API Snapshot Tooling

**Priority**: P3 | **Effort**: Medium | **Risk**: Low

Detect API surface drift in generated bindings. Standalone scripting — no generator changes.

## Problem

No mechanism to detect when a generator change alters the public API surface of generated bindings (method signatures, type names, parameter types). Currently detected only by manual review or downstream compile failures.

## What "Done" Looks Like

- Script that extracts public API surface from generated `.cs` files
- Baseline snapshot checked into repo
- An optional nuke/CI step compares against baseline
- Clear diff output showing added/removed/changed members

## Consideration

Potentially noisy during active development. May be better gated on releases or opt-in. Tracked as deferred tooling in `src/docs/not-planned.md`; could feed into an optional CI comparison step when picked up.

## Key Files

New scripts in `BindingTests/`

## Verification

Run against BindingTests generated bindings, verify baseline matches current output.
