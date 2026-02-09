# Roadmap

**Created**: February 2026
**Status**: Active — single source of truth for work items

For completed work (Phases A–G), see `CompletedPhases/phases-a-through-g.md`.
For detailed gap descriptions and contract matrix, see `testing-gaps.md`.
For deferred/aspirational work, see `Future/`.

---

## Current Baselines

| Metric | Value |
|--------|-------|
| Unit tests | 1677 passing |
| Integration tests | 699 passing (11 skipped, pre-existing) |
| TestFramework must-pass | 94/94 passing, 0 degraded |

| Library | Binding Errors | Test App Errors | Notes |
|---------|---------------|-----------------|-------|
| **BlinkID** | 0 | N/A | Clean |
| **Nuke** | 0 | N/A | Clean (test app has pre-existing install_name_tool iOS toolchain issue) |
| **CryptoSwift** | 0 | 0 | Clean |
| **Lottie** | 0 | N/A | Clean |

---

## Phase H: Unit Test Gaps + Remaining Library Errors

**Status**: Done (H1 + H2)
**Priority**: High — eliminate remaining library errors
**Effort**: Medium (1-2 sessions)

### H1: Unit Test Coverage Gaps (Phase G fixes) — Done

Phase G fixed 8 generator bugs but 3 fixes lacked targeted unit tests. Added 5 regression tests.

### H2: Remaining Library Errors (6 distinct bugs, 12 total errors) — Done

Fixed 6 generator bugs eliminating all 12 remaining library binding errors (CryptoSwift 3→0, Nuke 1→0, Lottie 8→0). Added 12 regression tests.

| Bug | Library | Fix |
|-----|---------|-----|
| 1 | CryptoSwift | `PropertyHandler.cs` — TupleTypeSpec branch in `TranslateTypeSpecWithGenerics` |
| 2 | CryptoSwift | `EnumHandler.CaseConstruction.cs` — SimpleEnum check in `GetPInvokeArgument`/`GetPInvokeType` |
| 3 | CryptoSwift | `Receivers/Vtables/StaticInit/SwiftObject` — consistent `ProtocolSignatureHelper.GetMethodSignatureKey` dedup |
| 4 | Nuke | `WrapperEmitter.Return.cs` — `GetCSharpExistentialType()` for optional existential marshal type |
| 5 | Lottie | `WrapperEmitter.Async.cs` — exclude existentials from copy-buffer filter |
| 6 | Lottie | `WrapperEmitter.Return.cs` — `GetPublicExistentialType() == "object"` guard before proxy construction |

---

## Phase I: Additional Library Validation

**Status**: Not Started
**Priority**: Medium
**Effort**: Medium (2-3 sessions)
**Depends on**: Phase H (validate with clean error baseline)

### I1. Select and bind a new library
Candidates (pick 1):
- **Alamofire** — networking, heavy closure/async patterns
- **Kingfisher** — image loading, different patterns from Nuke
- **SwiftProtobuf** — value types, generics, enums heavy

### I2. Process
1. Build xcframework for the library
2. Run generator, check binding report
3. Compare member coverage to existing libraries (target: 90%+)
4. Verify golden scenario compiles without interop types
5. Fix any new generator bugs found
6. Add to `BindingTesting/` with build/validate scripts

### I3. Document findings
- Update `CURRENT-STATUS.md` with new library stats
- Add any new skip reasons to `testing-gaps.md`

---

## Future Work

Once Phase I is complete:
- Must-pass features at 94+ (currently 94, up from 61 pre-Phase B)
- Runtime test coverage covers most of the contract matrix
- Generated API is idiomatic C# — no interop types in public surface
- 5-6 real-world libraries validated
- Quality scorecard metrics all at gate values
- Test pipeline catches regressions automatically

Next priorities:

- **API Documentation Generation** — Extract Swift doc comments via `swift-symbolgraph-extract` and emit as C# XML doc comments (`/// <summary>`, `/// <param>`, etc.) on generated bindings. Every `.framework`/`.xcframework` ships `.swiftdoc` files that the tool reads — no source code needed. Join key: `usr` field shared between symbol graph JSON and ABI JSON. Steps: (1) run `swift-symbolgraph-extract` in build pipeline, (2) parse `docComment.lines` from symbol graph JSON, (3) add `Documentation` property to `BaseDecl` model, (4) emit XML doc comments in emitter. Tested coverage: Nuke 87%, BlinkID 50%, StoreKit 54%, SwiftBindingsTestLib 96%.
- **`@_cdecl` wrapper generation** for all methods (bypasses Mono JIT bugs #18, #19 for runtime)
- **MSBuild SDK + project templates** — Phase 3 DX work from `north-star.md`
- **Optional string properties** — `Swift.Optional<Swift.String>` → `string?` (extend TypeConversionHandler to unwrap optional strings)
- **Cross-module protocol interface coverage** — Expand `_runtimeProtocols` for stdlib protocols used as existentials (Comparable, Sendable, CodingKey, etc.)
- **Remaining testing gaps** — P3/P4 items from `testing-gaps.md` (PInvokeEmitter tests, golden snapshots, CI)
- **Deferred work** in `Future/` (NativeAOT validation, Roslyn analyzer, existential analysis, performance benchmarks)

### Known Runtime Blockers (Upstream)
- **Mono JIT assertion (jit-info.c:918)**: Kills process on closure P/Invoke + SwiftString via CallConvSwift
- **SafeHandle in async P/Invoke**: Not preserved through async continuation
- **Non-blittable CallConvSwift**: Mono rejects non-blittable types with Swift calling convention
- See `known-issues-workarounds.md` for details
