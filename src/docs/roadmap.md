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

**Status**: Done
**Completed**: February 2026
**Effort**: Medium (5 sessions)
**Why**: 51 must-pass features sat in `.disabled/` dirs. Enabling them closed the gap from 61 to 93. This built the regression safety net needed before the sweeping emitter changes in Phase D.

**Execution order**: B1 → B2 → B3 → B5 → B4. Rationale: Error handling and generics are the easiest wins (already work in real-world libraries). Protocols need version guard work but no new infrastructure. Compositions are additive (new Swift files, not enabling disabled ones). Async is last because it requires Swift wrapper generation in the test build pipeline and runtime tests are complicated by Mono JIT bugs.

Work one batch at a time. After each batch: run `build-and-test.sh`, check coverage report, fix any generator regressions before proceeding to the next batch.

### B1. Error Handling (testing-gaps.md Gap 8) — DONE
- Enabled `ErrorHandling.disabled/` → `ErrorHandling/` (3 Swift source files)
- **Generator bug found**: async typed throws (`async throws(ParseError)`) emits `_payload`/`this` in static method context. Guarded `asyncParse` with `#if swift(>=99.0)` — to fix when async support is enabled (B4).
- Coverage: 64/64 passing (up from 61), 0 degraded. 3 new must-pass features: `synchronous_throws`, `static_throws`, `custom_error_type`. 3 known-unsupported: `typed_throws`, `typed_throws_on_struct`, `typed_async_throws` (compiled out).
- Unit tests: 1,603 passed, 0 regressions
- Runtime tests: 24 PASS, 0 FAIL at Tier 2 (10 Tier 3 skipped — Mono JIT crashes on SwiftString+error path, non-blittable tuple enum cases, non-frozen struct constructors, missing entry points)
- File: `TestFramework/RuntimeTestsApp/ErrorHandling/ThrowingMethodTests.cs` (class: `BasicThrowingTests`)

### B2. Generics + Protocol Conformance (testing-gaps.md Gap 7) — DONE
- Enabled `Generics/` (4 files: Types, Functions, Constraints, Existentials) and `Protocols/Conformance.swift`
- Removed directory-level exclusions from `Package.swift` and `build-xcframework.sh`; remaining `.disabled` files auto-excluded by SPM/find
- Added `BoundIntPair` (frozen) and `BoundStringPair` (non-frozen) concrete structs for bound generic coverage
- **Guard**: `GenericPair.swapped()` generates CS8500 (pointer to managed generic type with swapped type params) — guarded with `#if swift(>=99.0)`
- **Guard**: `Person` struct in Conformance.swift depends on `Nameable`/`Ageable` from disabled Composition.swift — guarded
- Coverage: 79/79 passing (up from 64), 0 degraded. 15 new must-pass features from generics, constraints, existentials, protocol conformance
- Unit tests: 1,603 passed, 0 regressions
- Runtime tests: 83 PASS (17 new BasicGenericTests), 0 new FAIL at Tier 2
  - Tier 1: BoundIntPair (frozen struct), SummableInt32 (frozen + protocol), MutableItem (non-frozen, property get/set/dispose)
  - Tier 2: BoundStringPair (string ctor + Joined method), SimpleItem (string ctor + Describe), DisplayItem (Describe + Display)
  - Tier 3 deferred: IntContainer (array param not properly marshalled through SwiftIndirectResult ctor path)
- File: `TestFramework/RuntimeTestsApp/Generics/BasicGenericTests.cs` (class: `BasicGenericTests`)

### B3. Protocols (testing-gaps.md Gap 4) — DONE
- Enabled `Composition.swift.disabled` → `Composition.swift`, `NonBlittableProtocols.swift.disabled` → `NonBlittableProtocols.swift`
- Unguarded `Person` struct in `Conformance.swift` (was guarded because Nameable/Ageable were in disabled file)
- Coverage: 81/81 passing (up from 79), 0 degraded. 2 new must-pass features: `protocol_composition`, `non_blittable_protocols`
- Generator: 20 protocols detected, 105/115 types emitted, 439 members, 0 skipped members
- Unit tests: 1,603 passed, 0 regressions
- Runtime tests: 23 PASS, 0 FAIL at Tier 2 (10 Tier 3 skipped). Tests exercise **interface projection** (concrete type → interface cast → concrete P/Invoke). Existential witness-dispatch proxy tests deferred — requires wrapper library in runtime bundle.
  - Tier 1: Conformance checks (SimpleItem, MutableItem, DisplayItem, MultiConformingValue, Person), blittable property get/set through ISwiftHasValue, Int32 methods through ISwiftAddable/Subtractable/Multipliable/Dividable, ISwiftAgeable.Age
  - Tier 2: String method dispatch through ISwiftDescribable.Describe(), ISwiftDisplayable.Display(), inherited Describe through Displayable, ISwiftStringProcessor.Process()/GetOutput(), ISwiftStatusHandler (GetCurrentStatus/TransitionStatus/HandleStatus with TaskStatus enum), TaskStatus.RawValue (Int32)
  - Tier 3 deferred: TaskPriority-based tests (String raw value enum needs SwiftBindings wrapper lib not bundled in runtime app), SwiftString property access through interfaces (Mono JIT crash risk)
- File: `TestFramework/RuntimeTestsApp/Protocols/WitnessDispatchTests.cs` (class: `BasicProtocolDispatchTests`)

### B5. Complex Compositions (testing-gaps.md Gap 5) — DONE (coverage added, runtime issues deferred)
- Added `TestFramework/Sources/SwiftBindingsTestLib/Patterns/RealWorldCompositions.swift` with 5 composition types: BatchConfig (frozen struct + optional array), ValueAnimal (inheritance + protocol), Registry (singleton + optional class return), EventHandler (optional closure property), Transformer (closure return)
- Added `TestFramework/RuntimeTestsApp/Patterns/CompositionTests.cs` with 22 tests (4 Tier 1, 18 Tier 3)
- Coverage: 86/86 passing (up from 81), 0 degraded. 5 new must-pass features: `struct_with_optional_array`, `inheritance_plus_protocol`, `singleton_with_optional_return`, `class_with_closure_property`, `closure_return_composition`
- Generator: 110/120 types emitted, 466/500 members, 0 skipped members — all 5 types emit correctly
- Unit tests: 1,603 passed, 0 regressions
- Runtime tests: 4 PASS at Tier 1, 18 Tier 3 deferred. Runtime exposed deterministic composition defects (not just Mono flakiness):
  - **Interop defect**: SafeHandle arg through CallConvSwift → "Passing non-blittable types" error (Registry.Register/Clear/ProcessRegistry)
  - **Interop defect**: class inheritance + protocol conformance → EntryPointNotFoundException, symbols not exported from dylib (ValueAnimal.Value/GetValue/SetValue)
  - **Mono JIT crash**: frozen struct SwiftString return through CallConvSwift → jit-info.c:918 assertion (BatchConfig.EffectiveName/DescribeConfig)
  - **Layout mismatch**: optional array on frozen struct → "Not enough bits to represent the passed value" (BatchConfig.TagCount)
  - **Mono JIT crash**: closure P/Invoke → jit-info.c:918 assertion (EventHandler, Transformer — known)
- File: `TestFramework/RuntimeTestsApp/Patterns/CompositionTests.cs` (class: `BasicCompositionTests`)

### B4. Async (testing-gaps.md Gap 3) — DONE
- Enabled `Async.disabled/` → `Async/` (9 Swift source files: Methods, AsyncThrowing, AsyncComplexTypes, AsyncClosures, AsyncProperties, Actors, MainActor, IsolationControl, Sendable)
- Added `build-async-wrapper.sh` to build pipeline — generates Swift async wrappers, post-processes broken patterns (EveryProtocol, static `self.`, non-escaping closures in Task, mutating on let existential), compiles into SwiftBindings.xcframework
- **Guard**: `AsyncClosures.swift` produces broken C# (Task<T>→T return mismatch, `_payload`/`this` in static free function context) — guarded with `#if swift(>=99.0)`
- Coverage: 93/93 passing (up from 86), 0 degraded. 7 new must-pass features: `async_method`, `async_static_method`, `async_throwing_method`, `async_string_return`, `async_array_return`, `async_complex_return`, `sendable_type`. 11 known-unsupported: `main_actor_class`, `main_actor_method`, `sendable_closure`, `actor_type`, `actor_isolated_method`, `actor_nonisolated_method`, `async_closure_parameter`, `async_closure_with_param` (both compiled out), `async_computed_property`, `async_property_on_class`, `nonisolated_unsafe`
- Unit tests: 1,603 passed, 0 regressions
- Runtime tests: 32 tests written, all Tier 3 — two infrastructure blockers:
  1. **EntryPointNotFoundException** (26 tests): `DllImport("SwiftBindingsTestLib")` targets wrong library — async `_async` entry points are defined via `@_silgen_name` in the SwiftBindings wrapper library
  2. **InvalidProgramException** (6 tests): throwing async methods pass non-blittable function pointers through CallConvSwift (Mono limitation)
- Files: `TestFramework/RuntimeTestsApp/Async/AsyncMethodTests.cs` (13 tests), `AsyncStringTests.cs` (11 tests), `AsyncComplexTypeTests.cs` (8 tests)

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

**Status**: DONE (All 4 waves complete)
**Effort**: Large (10-15 sessions across 4 waves)
**Why**: The generator produces correct interop code, but the public API surface exposes too many interop implementation details. A .NET developer consuming these bindings faces constant friction from `SwiftString`, `IntPtr`, `Init()` methods, `SwiftOptional<T>`, and `Payload.Dispose()`. This work is a **must-do before opening the project to external developers**.
**Depends on**: Phase B (need broad test coverage as regression safety net before sweeping emitter changes)
**Design doc**: `binding-review.md` — full API review with 12 issues, priority recommendations, DX criteria, quality scorecard, and implementation waves

The binding review identified the generated API as grade C+ — technically impressive but with serious DX rough edges. The fix is structured as 4 sequential waves, each building on the previous.

### Wave 1: Type Foundation (P0) — DONE
**Completed**: February 2026
**Goal**: Fix the most fundamental type-mapping issues. Every subsequent wave builds on these.

1. **Constructors** — Async constructors → `static CreateAsync()` factories (no more nonsensical `Init()` instance methods requiring an uninitialized object). Failable struct factories → `bool TryCreate(params, out T result)` per TryParse convention. Failable class constructors now emit `TryCreate` instead of being silently skipped.
2. **String unification** — Properties declare `string` type with conversion bridges (`get => Name_Get().ToString();` / `set => Name_Set(new SwiftString(value));`). P/Invoke layer keeps `SwiftString` for correct marshalling — the `IsAccessor` gates are preserved in the emitter pipeline; only the property declaration and bridge are changed.
3. **IDisposable** — `ISwiftObject` extends `IDisposable`. All implementers across the repo have `Dispose()` (runtime types, generated types, test mocks, CryptoKit integration types). Generated `Payload` is `internal`. Protocol proxy types emit no-op `Dispose()`.

**DoD verified**: Zero `Init()` instance methods, zero `SwiftString` properties, zero public `Payload` (all 92 are `internal`), 127 types have `Dispose()`. Unit tests: 1603 pass. Integration tests: 699 pass. Runtime tests: 116 pass. Coverage: 93/93 must-pass, 0 degraded.

### Wave 2: Type Safety (P1) — DONE
**Completed**: February 2026
**Goal**: Eliminate remaining non-idiomatic types from the public API.

1. **Nullable mapping** — `#nullable enable` in generated files. `SwiftOptional<T>` → `T?` conversion already handled by TypeConversionHandler (zero public `SwiftOptional` references).
2. **Integer types** — Swift `Int` → `nint`, Swift `UInt` → `nuint` via SwiftDatabase.xml + `CSharpTypeName.FromKeyword()`. `IntPtr` reserved for actual pointer types (OpaquePointer, UnsafePointer, etc.).
3. **Equals/GetHashCode** — Non-Equatable types: removed all throwing overrides (inherit reference equality from `object`). Equatable types: `GetHashCode()` returns `0` (contract-safe fallback) instead of throwing. No throwing `==`/`!=` operators for non-Equatable types.

**DoD verified**: Zero public `SwiftOptional<T>`, zero non-pointer `IntPtr` (`HashValue` → `nint`), zero Equals/GetHashCode throws, `#nullable enable` present. Unit tests: 1603 pass. Coverage: 93/93 must-pass, 0 degraded.

### Wave 3: API Shape (P2) — DONE
**Completed**: February 2026
**Goal**: Clean up naming, parameter conventions, and interop type leakage.

1. **Parameter names** — `SwiftInterfaceAccessParser.GetParameterNames()` extracts internal Swift parameter names from `.swiftinterface` files. `NameProvider.GetCSharpParameterName()` centralizes C# name resolution. 26 remaining `argN` names are legitimate (operators, `_` labels).
2. **Simple enums** — Frozen enums without associated values with integral (or no) raw values emit as C# `enum` value types with extension methods. String-raw-value enums stay as classes. Switch-based Swift wrappers for ABI-safe tag conversion.
3. **ExistentialContainer removal** — Public API uses protocol interfaces (`ISwiftDescribable`) instead of `ExistentialContainer1`. `ISwiftExistentialConvertible<T>` enables proxy→container extraction for P/Invoke calls. Array-of-existential uses `.Select()` element-level conversion. Optional existentials extract container before `SwiftOptional.NewSome()`.
4. **Default parameter overloads** — Swift wrapper functions omit trailing defaulted parameters (Swift fills defaults). Up to 4 C# overloads per method. Collision detection prevents CS0111 duplicate declarations.

**DoD verified**: Zero `ExistentialContainer*` in public API (excluding proxy internals), simple enums as C# enums (Direction, Color, TaskStatus, AlertStyle), 11 default parameter overloads generated. Unit tests: 1636 pass. Integration tests: 699 pass. Coverage: 94/94 must-pass (default_parameter_value promoted from known-unsupported), 0 degraded.

### Wave 4: Polish (P3) — DONE
**Completed**: February 2026
**Goal**: Cosmetic and convention alignment.

1. **Interface naming** — Generated protocol interfaces use `I{Name}` instead of `ISwift{Name}` (e.g., `IImageProcessing` instead of `ISwiftImageProcessing`). Runtime infrastructure interfaces (`ISwiftObject`, `ISwiftHashable`, etc.) are unchanged.
2. **Async naming** — Async methods in public API end with `Async` suffix per .NET convention. Centralized via `NameProvider.GetPublicMethodName()`.
3. **AnyType fallback attribute** — `[UnsupportedSwiftType]` attribute (with original Swift type name) now emitted on protocol interface properties, methods, parameters, and subscripts that fall back to AnyType. This fulfills the `[OriginalSwiftType]` requirement — `[UnsupportedSwiftType]` provides both reason and original type.
4. **Collection interfaces** — `SwiftArray<T>` implements `IList<T>` via explicit interface implementations (in addition to existing `IReadOnlyList<T>`).
5. **Property name collisions** — Nested types that collide with property names are renamed with `Info` suffix (e.g., nested type `Configuration` → `ConfigurationInfo`), keeping the property name clean. TypeDatabase is updated to reflect the renamed C# type. CS0542 (property = containing type) still uses `Value` suffix on the property.

**DoD verified**: Zero `ISwift{Name}` generated interfaces (all use `I{Name}`), async methods end with `Async`, AnyType fallbacks carry `[UnsupportedSwiftType]` with SwiftType, `SwiftArray<T>` implements `IList<T>`, nested type collisions resolved by renaming types. Unit tests: 1636 pass. Integration tests: 699 pass. Runtime tests: 116 pass.

### Cross-Cutting (All Waves)
- **Exception mapping** — Typed `SwiftException<TError>` for Swift `throws`. Improve incrementally.
- **CancellationToken** — Deferred to post-Phase D. Adding CancellationToken requires new parameter plumbing, wrapper generation changes, and runtime testing beyond cosmetic naming changes.
- **Ownership/lifetime docs** — Deferred to post-Phase D. No XML doc comment infrastructure exists yet; better suited as combined work with API Documentation Generation.
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

Once D, E are complete:
- Must-pass features should be 93+ passing (currently 93, up from 61 pre-Phase B)
- Runtime test coverage covers most of the contract matrix
- Generated API is idiomatic C# — no interop types in public surface
- 5-6 real-world libraries validated with clean API
- Quality scorecard metrics all at gate values
- Test pipeline catches regressions automatically

Next priorities would be:
- **API Documentation Generation** — Extract Swift doc comments via `swift-symbolgraph-extract` and emit as C# XML doc comments (`/// <summary>`, `/// <param>`, etc.) on generated bindings. Every `.framework`/`.xcframework` ships `.swiftdoc` files that the tool reads — no source code needed. Join key: `usr` field shared between symbol graph JSON and ABI JSON. Steps: (1) run `swift-symbolgraph-extract` in build pipeline, (2) parse `docComment.lines` from symbol graph JSON, (3) add `Documentation` property to `BaseDecl` model, (4) emit XML doc comments in emitter. Tested coverage: Nuke 87%, BlinkID 50%, StoreKit 54%, SwiftBindingsTestLib 96%.
- Phase 3 DX work (MSBuild SDK, project templates) from `north-star.md`
- `@_cdecl` wrapper generation for all methods (bypasses Mono JIT bugs #18, #19 for runtime)
- Remaining P3/P4 items from `testing-gaps.md` (PInvokeEmitter tests, golden snapshots, CI)
- Deferred work in `Future/` (NativeAOT validation, Roslyn analyzer, existential analysis)
