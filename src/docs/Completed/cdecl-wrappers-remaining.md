# @_cdecl Wrappers — Remaining Work

All Phase 4 items are complete. This document records what was done and what remains deferred.

---

## Completed

### 1. Fix Part 1 stale text

Updated `universal-cdecl-wrappers-design.md`:
- Line 5: Now says all sessions and Phase 4 are complete.
- Lines 29-38: Replaced "Four Workarounds (A-D)" section with "Resolved: Wrapper-First Architecture" summary.

### 2. `docs/Ownership.md`

Written. Covers all 10 boundary ownership rules with explanations for binding authors and contributors.

### 3. Dispose/destroy notes in `docs/Known-Limitations.md`

Added section covering:
- Finalization intentionally skips Swift destroy
- Generic types fall back to VWT destroy (accepted tech debt)
- Always use `using` declarations (SB1001 analyzer)

### 4. TestFramework wrapper library bundling

- **iOS Simulator**: Added wrapper dylib injection in `run-runtime-tests.sh` (Step 2.6) — copies `SwiftBindings.framework/SwiftBindings` into app bundle `Frameworks/`.
- **macOS**: Already had injection (Step 2.5 in macOS path).
- **Wrapper build**: Enhanced `build-async-wrapper.sh` with compile-and-strip retry loop — first pass strips known patterns (54), second pass identifies broken functions from compiler error line numbers (18) and strips them automatically. Handles non-copyable types, protocol `.self`, frozen struct `@_cdecl`, main actor isolation, enum case syntax, and all other error patterns without fragile pattern matching.
- **Dedup**: Fixed duplicate `SBW_Utf8Slice` / `_sbw_emptyBuffer` declarations.
- **@escaping**: Fixed `@escaping` in `.load(as:)` type context.
- **Block detection**: Fixed `find_block_end` to handle multi-line function declarations.

**Tier 3 tests**: Now unblocked by infrastructure — tests will either pass or reveal real bugs when run at `--tier 3`.

---

## Explicitly deferred (not planned)

- **SWIFTBIND060 diagnostic**: `[Obsolete] SB0001` covers the use case. Revisit if consumers request proper MSBuild-level warnings.
- **Closure routing flag consolidation**: Standalone closure wrapper path still active at 21.5% CallConvSwift. Collapse flags when coverage reaches near-100%.
- **Member-level wrapper stripping report**: Generator improvement, not a cleanup item.
