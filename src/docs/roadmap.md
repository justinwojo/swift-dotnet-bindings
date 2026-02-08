# Roadmap

**Created**: February 2026
**Status**: Active — single source of truth for work items

This is the active work queue. Each phase should be completed before moving to the next. After completing a phase, update its status to "Done" and note any findings.

For detailed gap descriptions and contract matrix, see `testing-gaps.md`.
For deferred/aspirational work, see `Future/`.

---

## Phase A: Testing Infrastructure Hardening

**Status**: Done
**Completed**: February 2026
**Effort**: Small (1 session)

All P1 items from `testing-gaps.md` completed (Gaps 0a, 0b, 1, 2). Unit tests: 1459 passing (was 1439). Coverage report: Active 57/57 passing, 0 degraded, 55 compiled-out, 0 missing.

- **A1**: Runtime tests in default pipeline (Gap 0a)
- **A2**: Generator strict mode (Gap 0b)
- **A3**: Conductor unit tests — 20 tests (Gap 1)
- **A4**: Coverage report clarity (Gap 2)

---

## Phase B: Enable Disabled TestFramework Features

**Status**: Not Started — start with B1
**Effort**: Medium (3-5 sessions)
**Why**: 51 must-pass features sit in `.disabled/` dirs. Enabling them closes the gap from 61 toward 90+. This also builds the regression safety net needed before the sweeping emitter changes in Phase D.

**Execution order**: B1 → B2 → B3 → B5 → B4. Rationale: Error handling and generics are the easiest wins (already work in real-world libraries). Protocols need version guard work but no new infrastructure. Compositions are additive (new Swift files, not enabling disabled ones). Async is last because it requires Swift wrapper generation in the test build pipeline and runtime tests are complicated by Mono JIT bugs.

Work one batch at a time. After each batch: run `build-and-test.sh`, check coverage report, fix any generator regressions before proceeding to the next batch.

### B1. Error Handling (testing-gaps.md Gap 8) — DO FIRST
- Enable `TestFramework/Sources/SwiftBindingsTestLib/ErrorHandling.disabled/`
- Start with `ThrowingFunctions.swift` — throwing functions already work in Nuke/Lottie/CryptoSwift
- Verify Layer 1 generation succeeds (xcframework build + binding generation)
- Check coverage report: new features should appear as `passing` or `degraded` (not `missing`)
- Fix any generator issues surfaced by the new Swift sources
- Add runtime test `TestFramework/RuntimeTestsApp/ErrorHandling/ThrowingMethodTests.cs`
- Run verification (see below), confirm no regressions

### B2. Generics (testing-gaps.md Gap 7) — SECOND
- Enable `TestFramework/Sources/SwiftBindingsTestLib/Generics.disabled/`
- Generic structs, classes, bound generic params all work in real-world libraries
- Add runtime test `TestFramework/RuntimeTestsApp/Generics/GenericTests.cs`

### B3. Protocols (testing-gaps.md Gap 4) — THIRD
- Enable protocol Swift sources (may need version guard adjustments)
- Verify protocol interfaces appear in generated bindings
- Add runtime tests for witness dispatch (blittable + String property getters/setters, method dispatch)
- File: `TestFramework/RuntimeTestsApp/Protocols/WitnessDispatchTests.cs`

### B5. Complex Compositions (testing-gaps.md Gap 5) — FOURTH
- Add `TestFramework/Sources/SwiftBindingsTestLib/Patterns/RealWorldCompositions.swift`
- Class with closure property, struct with optional array, method returning optional class, singleton static property, inheritance + protocol conformance
- Add runtime test `TestFramework/RuntimeTestsApp/Patterns/CompositionTests.cs`

### B4. Async (testing-gaps.md Gap 3) — LAST (most infrastructure needed)
- Enable `TestFramework/Sources/SwiftBindingsTestLib/Async.disabled/` starting with `AsyncMethods.swift`
- May need Swift wrapper generation step in `build-and-test.sh`
- Add runtime tests: `AsyncStringTests.cs`, `AsyncComplexTypeTests.cs`
- Blocked by: async Swift wrapper generation working for test library
- Runtime tests complicated by Mono JIT bugs — may need to defer some to Tier 3

### Verification after each batch
```bash
# 1. Unit tests still pass
./run-tests.sh

# 2. TestFramework generation + coverage
cd TestFramework
./build-and-test.sh && ./generate-coverage-report.sh
# Check: passing count increased, degraded didn't increase

# 3. Runtime tests (if runtime tests were added)
./run-runtime-tests.sh --tier 2 --timeout 90
```

---

## Phase C: CryptoSwift Validation

**Status**: Done
**Completed**: February 2026
**Effort**: Large (6 sessions — ArraySlice normalization + 9 fix steps)

### C1. CryptoSwift
- Built xcframework, ran generator: 61.3% member coverage initially
- **Finding**: 34 of 42 skipped members blocked by `ArraySlice<T>` (no TypeDatabase registration)
- **Fix**: ArraySlice parameter normalization (Phase 62) — Swift wrapper accepts `Array<T>`, converts to `ArraySlice<T>` at call site
- **9 fix steps** addressed 24 generator bugs: P/Invoke enum handling, constructor projection, operator ABI, tuple marshalling, EveryProtocol vtable/index/throws/dedup, protocol proxy alignment, wrapper extension filtering, protocol composition return types, swiftinterface internal detection
- **Result**: 88.0% member coverage (441/501), 103/103 types emitted, generated Swift typechecks with 0 errors
- Remaining 60 skipped members: 20 compound assignment operators (no C# equivalent), 14 unsupported closure signatures, 4 AnyType fallbacks, 4 static protocol members, 18 internal methods (correctly excluded)
- Runtime: 4/10 tests pass (static methods, enum construction). Remaining blocked by Mono JIT bugs (#18, #19) — same upstream issues as TestFramework Tier 3
- See `CompletedPhases/cryptoswift-codegen-bugs.md` and `CompletedPhases/cryptoswift-fix-order.md` for full details

---

## Phase D: Binding API Overhaul

**Status**: Not Started
**Effort**: Large (10-15 sessions across 4 waves)
**Why**: The generator produces correct interop code, but the public API surface exposes too many interop implementation details. A .NET developer consuming these bindings faces constant friction from `SwiftString`, `IntPtr`, `Init()` methods, `SwiftOptional<T>`, and `Payload.Dispose()`. This work is a **must-do before opening the project to external developers**.
**Depends on**: Phase B (need broad test coverage as regression safety net before sweeping emitter changes)
**Design doc**: `binding-review.md` — full API review with 12 issues, priority recommendations, DX criteria, quality scorecard, and implementation waves

The binding review identified the generated API as grade C+ — technically impressive but with serious DX rough edges. The fix is structured as 4 sequential waves, each building on the previous.

### Wave 1: Type Foundation (P0)
**Goal**: Fix the most fundamental type-mapping issues. Every subsequent wave builds on these.

1. **Constructors** — Swift `init(...)` → real C# constructor. `init?(...)` → `static bool TryCreate(..., out T result)`. Static factories only when Swift uses factory pattern.
2. **String unification** — Properties emit `string`, not `SwiftString`. Marshalling internal only.
3. **IDisposable** — All `ISwiftObject` types implement `IDisposable`. `Payload` becomes `internal`.

**DoD**: Zero `Init()` instance methods, zero `SwiftString` properties, zero public `Payload`, all types have `IDisposable`. TestFramework: 0 regressions.

### Wave 2: Type Safety (P1)
**Goal**: Eliminate remaining non-idiomatic types from the public API.

1. **Nullable mapping** — `SwiftOptional<T>` → `T?` in public signatures.
2. **Integer types** — Swift `Int` → `nint` or `long`, not `IntPtr`. `IntPtr` reserved for actual pointers.
3. **Equals/GetHashCode** — Don't throw. Use reference equality (classes) or don't override (structs).

**DoD**: Zero `SwiftOptional<T>`, zero non-pointer `IntPtr`, zero throwing `Equals`/`GetHashCode`. `#nullable enable` in all generated files.

### Wave 3: API Shape (P2)
**Goal**: Clean up naming, parameter conventions, and interop type leakage.

1. **Simple enums** — Enums without associated values → real C# `enum` types.
2. **Parameter names** — Use internal Swift names. Zero `arg0`/`arg1`. Remove `_for`/`_with` prefixes.
3. **ExistentialContainer removal** — Replace with typed protocol interfaces in public API.
4. **Default parameters / overloads** — Swift methods with defaults produce C# overloads.

**DoD**: Zero `arg0`/`arg1`, zero `ExistentialContainer*`, simple enums are C# enums.

### Wave 4: Polish (P3)
**Goal**: Cosmetic and convention alignment.

1. **Property name suffixes** — Remove `Value` suffix (`ConfigurationValue` → `Configuration`).
2. **Interface naming** — `ISwiftImageProcessing` → `IImageProcessing`.
3. **AnyType fallback** — Add `[OriginalSwiftType("CoreText.CTFont")]` attribute.
4. **Collection interfaces** — `SwiftArray<T>` implements `IReadOnlyList<T>` and `IList<T>`.
5. **Async naming** — Async methods in public API end with `Async`.

**DoD**: All scorecard metrics at gate values. Golden scenarios (Nuke, Lottie, BlinkID) compile without interop types.

### Cross-Cutting (All Waves)
- **Exception mapping** — Typed `SwiftException<TError>` for Swift `throws`. Improve incrementally.
- **CancellationToken** — Add to async methods as they're modified.
- **Ownership/lifetime docs** — Update XML doc comments as types are modified.
- **Versioning strategy** — Establish before shipping to external consumers.

### Quality Scorecard (Target: All Zero)

| Metric | Gate |
|--------|------|
| Public `IntPtr` for non-pointer semantics | 0 |
| Public `SwiftOptional<T>` | 0 |
| Public `SwiftString` properties | 0 |
| Public `ExistentialContainer*` | 0 |
| `Init()` instance methods (should be ctors) | 0 |
| `arg0`/`arg1` parameter names | 0 |
| Types missing `IDisposable` | 0 |
| `Equals`/`GetHashCode` that throw | 0 |
| Public `Payload` property | 0 |
| Golden scenarios compile without interop types | 3/3 |

---

## Phase E: Additional Library Validation

**Status**: Not Started
**Effort**: Medium (2-3 sessions)
**Why**: Validates that the post-overhaul generator produces clean, idiomatic bindings for new libraries. Should be done after Phase D so the new library gets the polished API from day one.
**Depends on**: Phase D (validate with new API shape, not old)

### E1. Select and bind a library
Candidates (pick 1):
- **Alamofire** — networking, heavy closure/async patterns
- **Kingfisher** — image loading, different patterns from Nuke
- **SwiftProtobuf** — value types, generics, enums heavy

### E2. Process
1. Build xcframework for the library
2. Run generator, check binding report
3. Compare member coverage to existing libraries (target: 90%+)
4. Verify golden scenario compiles without interop types (Phase D quality gate)
5. Fix any new generator bugs found
6. Add to `BindingTesting/` with build/validate scripts

### E3. Document findings
- Update `CURRENT-STATUS.md` with new library stats
- Add any new skip reasons to `testing-gaps.md`

---

## After All Phases

Once B, D, E are complete:
- Must-pass features should be 80+ passing (up from 61)
- Runtime test coverage covers most of the contract matrix
- Generated API is idiomatic C# — no interop types in public surface
- 5-6 real-world libraries validated with clean API
- Quality scorecard metrics all at gate values
- Test pipeline catches regressions automatically

Next priorities would be:
- Phase 3 DX work (MSBuild SDK, project templates) from `north-star.md`
- `@_cdecl` wrapper generation for all methods (bypasses Mono JIT bugs #18, #19 for runtime)
- Remaining P3/P4 items from `testing-gaps.md` (PInvokeEmitter tests, golden snapshots, CI)
- Deferred work in `Future/` (NativeAOT validation, Roslyn analyzer, existential analysis)
