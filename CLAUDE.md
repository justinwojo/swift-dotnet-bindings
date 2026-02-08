# Claude Code Guide for Swift Bindings

## Project Overview

Experimental Swift/.NET interop project. Generates C# bindings from compiled Swift libraries (.dylib + ABI JSON) for .NET 10.0 on Apple platforms. Originally Microsoft, now maintained by Justin Wojciechowski. MIT License.

## Repository Structure

- `src/Swift.Bindings/src/` — Generator: Parser → TypeDatabase → Marshaler → Emitter
- `src/Swift.Runtime/src/Swift/` — Runtime: SwiftString, SwiftArray, SafeHandle, ARC
- `TestFramework/` — Comprehensive test library + runtime tests (iOS Simulator)
- `BindingTesting/` — Real-world library validation (Nuke, BlinkID, Lottie, CryptoSwift)
- `src/docs/` — Design docs, status, known issues
- `docs/` — High-level philosophy (`binding-overview.md`)

## Building & Testing

**Always use helper scripts, not raw commands.**

```bash
./build.sh                    # Build the project
./run-tests.sh                # Run all unit + integration tests

# TestFramework (after generator changes):
cd TestFramework
./build-and-test.sh           # Full: xcframework + bindings + bridge
./generate-coverage-report.sh # Coverage matrix
./run-runtime-tests.sh --tier 2 --timeout 90  # Runtime on iOS Sim

# Runtime test iteration flags:
#   --skip-regen     Skip binding regeneration (incremental build)
#   --class NAME     Run only one test class
#   --safe-only      Skip [CrashRisk] classes (no Mono JIT crash)

# Real-world library validation:
cd BindingTesting/Nuke && ./build-all.sh && ./validate-sim.sh 15
```

## Validation After Generator Changes

Run after changes to `src/Swift.Bindings/src/{Marshaler,Emitter,Parser,TypeDatabase,Model}/`:
```bash
./run-tests.sh                                                    # Unit tests first
cd TestFramework && ./build-and-test.sh && ./generate-coverage-report.sh  # Then coverage
```

Coverage report shows must-pass features as passing/degraded/missing. Verify no regressions.

## Working Guidelines

- When fixing a bug pattern, grep the entire codebase for ALL instances before finishing.
- After code gen changes, verify generated output compiles — don't assume correctness.
- Use exact file paths verified by reading the filesystem. Don't guess paths.
- Address ALL code review findings in a single pass.
- Use logical/semantic cohesion for refactoring, not arbitrary LOC limits.
- Double-check memory management operations target the correct pointer/object.
- Do NOT commit unless the user explicitly asks.

## Known Runtime Issues

- **Mono JIT assertion (jit-info.c:918)**: Kills process on closure P/Invoke + SwiftString.PInvoke_GetLength via CallConvSwift. Bridge tests (`@_cdecl`) unaffected.
- SafeHandle in async P/Invoke not preserved (workaround: singleton + IntPtr)
- See `src/docs/known-issues-workarounds.md` for full details

## Key References

- `/north-star.md` — Long-term vision and roadmap
- `src/docs/CURRENT-STATUS.md` — Current compilation status and gaps
- `src/docs/roadmap.md` — Active work queue (Phase A → E)
- `src/docs/emitter-redesign-proposal.md` — Architecture direction
- `src/docs/known-issues-workarounds.md` — Runtime workarounds
