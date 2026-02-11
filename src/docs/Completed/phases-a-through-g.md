# Completed Phases A–G

**Completed**: February 2026
**Final Baselines**: 1660 unit tests, 699 integration tests, 94/94 TestFramework must-pass features

This document archives all completed roadmap phases. For active work, see `roadmap.md`.

---

## Phase A: Testing Infrastructure Hardening

**Effort**: Small (1 session)

All P1 items from `testing-gaps.md` completed (Gaps 0a, 0b, 1, 2). Unit tests: 1459 passing (was 1439). Coverage report: Active 57/57 passing, 0 degraded, 55 compiled-out, 0 missing.

- **A1**: Runtime tests in default pipeline (Gap 0a)
- **A2**: Generator strict mode (Gap 0b)
- **A3**: Conductor unit tests — 20 tests (Gap 1)
- **A4**: Coverage report clarity (Gap 2)

---

## Phase B: Enable Disabled TestFramework Features

**Effort**: Medium (5 sessions)

51 must-pass features sat in `.disabled/` dirs. Enabling them closed the gap from 61 to 93 must-pass features. This built the regression safety net needed before the sweeping emitter changes in Phase D.

### B1. Error Handling
- Enabled `ErrorHandling.disabled/` → `ErrorHandling/` (3 Swift source files)
- Generator bug found: async typed throws in static context — guarded with `#if swift(>=99.0)`
- Coverage: 64/64 passing. 3 new must-pass: `synchronous_throws`, `static_throws`, `custom_error_type`
- Runtime tests: 24 PASS at Tier 2

### B2. Generics + Protocol Conformance
- Enabled `Generics/` (4 files) and `Protocols/Conformance.swift`
- Added `BoundIntPair` (frozen) and `BoundStringPair` (non-frozen) concrete structs
- Guard: `GenericPair.swapped()` CS8500 (pointer to managed generic type)
- Coverage: 79/79 passing. 15 new must-pass features
- Runtime tests: 83 PASS (17 new BasicGenericTests)

### B3. Protocols
- Enabled `Composition.swift`, `NonBlittableProtocols.swift`
- Coverage: 81/81 passing. 2 new must-pass: `protocol_composition`, `non_blittable_protocols`
- Runtime tests: 23 PASS at Tier 2 (witness dispatch through protocol interfaces)

### B5. Complex Compositions
- Added `Patterns/RealWorldCompositions.swift` with 5 composition types
- Coverage: 86/86 passing. 5 new must-pass features
- Runtime tests: 4 PASS at Tier 1, 18 Tier 3 deferred (SafeHandle, inheritance, JIT, layout bugs)

### B4. Async
- Enabled `Async/` (9 Swift source files)
- Added `build-async-wrapper.sh` to pipeline with post-processing
- Guard: `AsyncClosures.swift` produces broken C# — compiled out
- Coverage: 93/93 passing. 7 new must-pass + 11 known-unsupported
- Runtime tests: 32 written, all Tier 3 (EntryPointNotFoundException + InvalidProgramException)

---

## Phase C: CryptoSwift Validation

**Effort**: Large (6 sessions — ArraySlice normalization + 9 fix steps)

- Built xcframework, ran generator: 61.3% member coverage initially
- **Key finding**: 34 of 42 skipped members blocked by `ArraySlice<T>` (no TypeDatabase registration)
- **Fix**: ArraySlice parameter normalization — Swift wrapper accepts `Array<T>`, converts at call site
- **9 fix steps** addressed 24 generator bugs: P/Invoke enum handling, constructor projection, operator ABI, tuple marshalling, EveryProtocol vtable/index/throws/dedup, protocol proxy alignment, wrapper extension filtering, protocol composition return types, swiftinterface internal detection
- **Result**: 88.0% member coverage (441/501), 103/103 types emitted, Swift typechecks 0 errors
- Remaining skips: 20 compound assignment operators, 14 unsupported closure signatures, 4 AnyType fallbacks, 4 static protocol members, 18 internal methods
- See `cryptoswift-codegen-bugs.md` and `cryptoswift-fix-order.md`

---

## Phase D: Binding API Overhaul

**Effort**: Large (10-15 sessions across 4 waves)

The generator produced correct interop code, but the public API exposed too many interop implementation details. This was a must-do before opening the project to external developers.

### Wave 1: Type Foundation (P0)
- Constructors: async → `static CreateAsync()` factories, failable → `TryCreate()` pattern
- String unification: properties declare `string` with conversion bridges
- IDisposable: `ISwiftObject` extends `IDisposable`, all implementers have `Dispose()`

### Wave 2: Type Safety (P1)
- `#nullable enable` in generated files, `SwiftOptional<T>` → `T?`
- Swift `Int` → `nint`, Swift `UInt` → `nuint`
- Equals/GetHashCode: removed throwing overrides, contract-safe fallbacks

### Wave 3: API Shape (P2)
- Parameter names from `.swiftinterface` extraction
- Frozen enums → C# `enum` value types with switch-based Swift wrappers
- `ExistentialContainer` removed from public API → protocol interfaces
- Default parameter overloads (up to 4 per method)

### Wave 4: Polish (P3)
- Interface naming: `I{Name}` instead of `ISwift{Name}`
- Async naming: `Async` suffix per .NET convention
- `[UnsupportedSwiftType]` attribute on AnyType fallback members
- `SwiftArray<T>` implements `IList<T>`
- Nested type collision resolution (property wins, type gets `Info` suffix)

### Quality Scorecard (All Gates Met)

| Metric | Gate |
|--------|------|
| Public `IntPtr` for non-pointer semantics | 0 |
| Public `SwiftOptional<T>` | 0 |
| Public `SwiftString` properties | 0 |
| Public `ExistentialContainer*` | 0 |
| `Init()` instance methods | 0 |
| `arg0`/`arg1` parameter names | 0 |
| Types missing `IDisposable` | 0 |
| `Equals`/`GetHashCode` that throw | 0 |
| Public `Payload` property | 0 |
| Golden scenarios compile without interop types | 3/3 |

---

## Phase D.5: Post-Validation Fixes

**Commit**: `3ee7d5f`

Fixed 5 categories of build-breaking issues found during Phase D real-world library validation:

1. **Cross-reference rename propagation** — Centralized nested type rename in NameProvider with pre-pass. Recursive descendant updates in TypeDatabase (e.g., `Cache.Entry` → `CacheInfo.Entry`)
2. **Closure @escaping/@Sendable** — Added missing attributes across ExistentialBypassEmitter, DefaultParameterOverloadEmitter, WrapperEmitter.Async
3. **Non-generic SwiftArray** — Generic-aware typeTranslator for PropertyHandler property declarations
4. **Async struct default overloads** — Fixed invalid overload emission for async throwing constructors in struct context
5. **Protocol composition interface timing** — Scoped composition collector before free method emission

---

## Phase E: Library Bug Fixes (RC1–RC6)

**Commit**: `10c672d`

Fixed 6 generator bugs across CryptoSwift, Lottie, Nuke:

- **RC1**: `ProtocolListTypeSpec.LLToString()` returns "Any" for empty protocol list
- **RC2**: Proxy property types fall through to idiomatic conversion (fixes CS0738)
- **RC3**: EveryProtocol skips Self-typed members via `τ_0_0` detection
- **RC4**: ExistentialHandler validates protocol TypeRecords before generating `IProtocol` names
- **RC5**: Skip default param overloads on generic parent types; render empty ProtocolListTypeSpec as "Any"
- **RC6**: Update Nuke test app stale interface names and return types

---

## Phase F: Nuke Bug Fixes

**Commit**: `1307fb6`

Fixed 5 generator bugs + Nuke test app updates:

- Async throws constructors emitting invalid default parameter overloads in struct context
- Optional existential property casts (`SwiftOptional<ExistentialContainer>` → `AnyType?`)
- Optional parameter nullability for value types (pattern matching instead of `FromNullable`)
- Closure/existential parameter bridging: optional closure guard, optional existential return proxy wrapping, AnyType placeholder for "object" existentials
- Protocol composition interface emission scoping
- Native type remapping for non-existential properties
- Optional existential indirect return proxy wrapping

---

## Phase G: 8 Generator Bugs + CryptoSwift Test App

**Status**: Implemented, not yet committed

Fixed 8 generator bugs reducing library errors from 48 to 12, plus CryptoSwift test app:

| Bug | File | Fix |
|-----|------|-----|
| **G1** | PropertyHandler.cs | Generic type params in property types — added `GenericContext` to `TranslateTypeSpecWithGenerics()` |
| **G2** | PropertyHandler.cs | Optional existential property getter/setter — pass-through to accessor methods |
| **G3** | ModuleHandler.cs | Protocol composition proxy classes — full wrap-only proxy emission with member stubs |
| **G4** | OperatorHandler.cs | Generic operator skip — detect undeclared marshalling vars in P/Invoke signature |
| **G5** | WrapperEmitter.Return.cs | Zero-protocol existential guard — `Any` returns container directly |
| **G6** | ProtocolProxyEmitter.InterfaceImpl.cs + ProtocolHandler.cs | Proxy dedup key unification — use `ProtocolSignatureHelper` keys in InterfaceImpl |
| **G7** | BoundGenericsHandler.cs | `GetBufferType()` fallback → `IntPtr` (was AnyType → CS8500 warnings) |
| **G8** | BoundGenericsHandler.cs | Existential type args → `AnyType` (was ExistentialContainer{N} → ISwiftObject constraint failure) |

**CryptoSwift Test App**: Replaced `Uninit<T>().Init()` with direct constructors, fixed `SHA2.VariantInfo` enum access.

**11 unit test updates**: 8 BoundGenericsHandler (ExistentialContainer → AnyType), 1 MethodHandler (method skipped), 1 ProtocolProxy (Box type), 1 Operator (skip instead of emit).

### Final Library Status

| Library | Binding Errors | Test App Errors |
|---------|---------------|-----------------|
| **BlinkID** | 0 | N/A |
| **Nuke** | 1 | 0 |
| **CryptoSwift** | 3 | 0 |
| **Lottie** | 8 | N/A |

### Key Fix Patterns Discovered

- **Optional parameter value types**: `SwiftOptional<T>.FromNullable(T?)` fails for unconstrained T with value types. Use `is {} val` pattern matching instead.
- **Composition interface collection timing**: `SetActiveCompositionCollector()` must be called BEFORE emitting free methods, not just before types.
- **TranslateTypeSpecForConversion existential "object" fallback**: Return AnyType placeholder to trigger `ContainsPlaceholder` skip.
- **Composition proxy stubs**: Use `ResolveCSharpTypeName()` with ClosureTypeSpec→Action/Func and TupleTypeSpec→ValueTuple to match ProtocolHandler interface emission.
