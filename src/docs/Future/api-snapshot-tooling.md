# API Snapshot Tooling

**Priority**: P3 | **Effort**: Medium | **Risk**: Low

Detect API surface drift in generated bindings. Standalone scripting — no generator changes.

## Problem

No mechanism to detect when a generator change alters the public API surface of generated bindings (method signatures, type names, parameter types). Currently detected only by manual review or downstream compile failures.

## What "Done" Looks Like

- Script that extracts public API surface from generated `.cs` files
- Baseline snapshot checked into repo
- `build-and-test.sh` optionally compares against baseline
- Clear diff output showing added/removed/changed members

## Consideration

Potentially noisy during active development. May be better gated on releases or opt-in. Could feed into CI integration (roadmap task 2) as an optional comparison step.

## Key Files

New scripts in `TestFramework/`

## Verification

Run against TestFramework generated bindings, verify baseline matches current output.
