# Feature Roadmap

**Date**: March 2, 2026
**Prerequisite**: Foundation Roadmap pillars complete (or the specific pillar each feature depends on)
**Goal**: Push binding quality scores from ~3.50 toward 4.0+ across all libraries

This roadmap covers features that BUILD ON the foundation pillars. Each item depends on one or more pillars being solid. Do not start feature work until its prerequisite pillar is complete.

---

## Feature Priority Matrix

| Feature | Pillar Dependency | Score Impact | Effort | Libraries Affected |
|---------|:-----------------:|:-----------:|:------:|:---------:|
| ~~nint return/property overloads~~ | None | +0.10-0.15 avg | Small | Nearly all | **DONE** |
| ~~Noise reduction~~ | None | +0.05-0.08 avg | Medium | GRDB, Alamofire, Stripe | **DONE** |
| ~~Protocol extension param gate lifts~~ | None | +0.10-0.15 avg | Medium | GRDB, Kingfisher | **DONE** |
| ~~Collection witness dispatch~~ | P2.1 (dispatch table) | +0.10-0.15 avg | 1.5 sessions | All proxy libraries | **DONE** |
| String enum raw values | None | +0.02-0.05 avg | Small | GRDB, CryptoSwift |
| Safety (Dispose, finalizer) | None | +0.00 (production) | 1 session | All |
| ~~Runtime metadata prototype~~ | P1.2 (conformance graph) | +0.00 (enabler) | 1 session | Future | **DONE** |

---

## Feature F1: nint Return/Property Overloads — COMPLETE
**Pillar dependency**: None
**Score impact**: +0.10-0.15 combined average (TypeFidelity)
**Effort**: 1 session (completed March 2, 2026)

300+ `public nint` declarations across all libraries now have idiomatic `int`/`uint` types.

**What shipped**:
- **Property narrowing**: `nint`→`int`, `nuint`→`uint`, `nint?`→`int?`, `nuint?`→`uint?` in PropertyHandler with getter/setter ABI casts
- **Protocol interface narrowing**: Interface properties use narrowed types; proxy receivers widen for ABI (getter: `(nint)result`, setter: `(int)value`)
- **InterfaceImpl dispatch casts**: `(int)MarshalFromSwift<nint>(ptr)` for blittable property dispatch
- **DIM overloads**: Protocol interface methods with nint params get default implementation overloads with int params
- **Shared helpers**: `NarrowNativeIntType`, `TryGetAbiWideningType`, `TryGetNarrowedType` on `NativeIntOverloadEmitter`

**What was NOT shipped** (by design):
- Method return type narrowing was implemented then reverted — C# overload resolution prefers `int` overloads for int literals (`Skip(3)`), causing silent 64-bit truncation. Properties are safe (no overload ambiguity).

**Key files**: `NativeIntOverloadEmitter.cs`, `PropertyHandler.cs`, `ProtocolHandler.cs`, `ProtocolProxyEmitter.Helpers.cs`, `ProtocolProxyEmitter.Receivers.cs`, `ProtocolProxyEmitter.InterfaceImpl.cs`

---

## Feature F2: Noise Reduction — COMPLETE
**Pillar dependency**: None
**Score impact**: +0.05-0.08 combined average (Noise category)
**Effort**: 1 session (completed March 2, 2026)

Three noise sources addressed:

**What shipped**:
- **`_`-prefix type suppression**: Pre-computed `underscoreSuppressedNames` set in `CollectUnderscoreSuppressedTypeNames()`, checked in `HandleBaseDecl` to skip emission. Structurally required types (superclasses, protocol conformances of non-`_` types) are preserved with `[EditorBrowsable(Never)]`. `publicTypeNames` from swiftinterface wired as `keepUnderscoreTypes` override — explicitly public `_`-prefixed types are not suppressed. New `SkipReason.UnderscorePrefixInternal` in reporting.
- **ExistentialContainer proxy hiding**: Proxy class declarations (`FooProxy`) and composition proxy classes now emit `[EditorBrowsable(Never)]`. Users interact with protocol interfaces (`IFoo`), not proxies. Optional existentials in closures (`ExistentialContainer1?`) deferred (runtime marshalling limitation).
- **Throwing closure simplification**: Methods with `Func<..., SwiftResult<T, SwiftError>>` closure params get convenience overloads accepting `Action<...>` (void throws) or `Func<..., T>` (non-void throws). Wrapper lambda catches `SwiftErrorException` → `SwiftResult.FromFailure`. Methods returning `SwiftResult<T, SwiftError>` unwrap: success returns `T`, failure throws `SwiftErrorException`. Original methods hidden via `[EditorBrowsable(Never)]` with pre-scan dedup safety. New `SwiftErrorException` runtime type in `Swift.Runtime`.

**Key files**: `ThrowingClosureSimplificationEmitter.cs`, `Program.cs` (`CollectUnderscoreSuppressedTypeNames`), `ModuleEmissionContext.cs`, `ProtocolProxyEmitter.cs`, `ModuleHandler.cs`, `SwiftErrorException.cs`, `IHandler.cs` (HandleBaseDecl skip), `WrapperEmitter.Signature.cs` (EditorBrowsable emission)

---

## Feature F3: Protocol Extension Parameter Gate Lifts — COMPLETE
**Pillar dependency**: None (pillar dependency removed — gates lifted independently)
**Score impact**: +0.10-0.15 combined average
**Effort**: 1 session (completed March 2, 2026)

37 of 77 KFOptionSetter methods were blocked by parameter gates. Five sub-tasks lifted gates incrementally:

**What shipped**:
- **Primitive return type fix**: Return gate now accepts primitives (Int, Bool, Float, Double) — `IsPrimitiveReturn` helper
- **Throwing methods**: Untyped `throws` methods get `throws`/`try` in Swift wrapper, `Throws=true` on synthetic MethodDecl. `rethrows` passes through as non-throwing. Typed `throws(ErrorType)` stays gated (requires `ThrownErrorType` resolution)
- **Existential params**: `IsSupportedExistentialParam` validates protocol existentials (PAT/Self-requirement/ObjC-mixed blocked, generic protocol existentials blocked). Renders `any Protocol` by value in wrapper, no Unmanaged conversion
- **Foundation.Data params**: Accepted as frozen blittable struct. Wrapper declares `Foundation.Data` directly (by value), downstream `NativeRemappedFrozen` pipeline handles marshaling
- **Array params**: `Swift.Array<T>` accepted via `IsSwiftArray` check. Wrapper uses `UnsafeMutableRawPointer` + `unsafeBitCast` to element array type. `Optional<Array<T>>` explicitly blocked (wrapper rendering doesn't handle optional bound generics)

**Shared helpers extracted during simplification**:
- `IsSupportedExistentialCore` + `HasBlockingProtocolFlags` — shared validation between return and param existential checks
- `RenderSwiftParam` + `RenderCallArg` — 5-way param type dispatch (existential→Data→array→class→primitive) shared between `EmitSwiftWrapper` and `EmitClosureSwiftWrapper`

**Key files**: `ProtocolExtensionEmitter.cs` (`IsCdeclCompatibleType`, `TryInjectMethod`, `IsSupportedExistentialParam`, `RenderSwiftParam`, `RenderCallArg`)

---

## Feature F4: Collection Returns + Optional Existential Returns — COMPLETE
**Pillar dependency**: P2.1 (dispatch table)
**Score impact**: +0.10-0.15 combined average (largest single feature)
**Effort**: 1 session (completed March 3, 2026)

Protocol proxy methods returning collections or optional existentials now dispatch through Swift witness tables instead of throwing `NotSupportedException`.

**What shipped**:
- **BoundGenericReturn dispatch kind**: `Array<T>`, `Dictionary<K,V>`, `Set<T>` returns classified and dispatched. Swift accessors use heap-allocated pointer pattern (allocate → initialize → return + typed free function). C# uses `TypeProjectionFactory` projections for marshalling (`MarshalFromSwift<SwiftArray<T>>().AsProjected(...)`, `.Select().ToHashSet()`, etc.)
- **Optional existential return**: `Optional<any Protocol>` dispatch via `if let` Swift pattern, `IntPtr.Zero` → `null` in C#. Full safety gates from `IsSupportedExistentialReturn` applied (PAT, Self requirement, InheritedRequirementsOnly all block dispatch)
- **Throwing + optional gate**: Throwing methods with optional existential return stay `NotDispatchable` — `IntPtr.Zero` sentinel conflict between error and `.none`
- **Classification helpers**: `IsCollectionType`, `IsPropertyCollectionReturn`, `IsBoundGenericReturnDispatchable`, `IsElementTypeResolvable`, `GetSwiftCollectionTypeString` (renders Swift sugar syntax: `[String]`, `[K: V]`, `Set<T>`)
- **Secondary C# validation**: BoundGenericReturn shares AnyType/object rejection and param validation with ClassReturn/StructReturn in a merged conditional

**Key files**: `WitnessDispatchEmitter.cs`, `ProtocolProxyEmitter.InterfaceImpl.cs`, `ProtocolProxyEmitter.SwiftObject.cs`, `ProtocolExtensionEmitter.cs` (`HasBlockingProtocolFlagsForReturn`)

---

## Feature F5: String Enum Raw Values — BLOCKED (no data source)
**Pillar dependency**: None
**Score impact**: +0.02-0.05 (GRDB ResultCode, CryptoSwift error codes)
**Effort**: 0.5 session
**Status**: Blocked — no viable data source in compiled xcframeworks

ABI JSON doesn't include string/integer raw values for enums. The original assumption was that swiftinterface files would contain them, but **investigation (March 2026) confirmed they do not**:

- Compiled `.swiftinterface` files preserve type signatures (`RawRepresentable`, `rawValue: Swift.Int`) but strip actual per-case values
- Integer enums: `case cfb8` appears without `= 0` assignment
- String enums: no `case foo = "bar"` syntax present
- GRDB `ResultCode` is a `struct` with `static let` members in the swiftinterface — initializer values are compiled away

**Possible future approaches** (none currently viable):
- **Runtime introspection** (F7 dependency): call `init(rawValue:)` at generation time for candidate values — requires a running Swift runtime during generation
- **Source-level swiftinterface**: pre-compilation `.swiftinterface` files may retain values, but published xcframeworks don't ship these
- **Hardcoded mappings**: per-library override files with known raw values — doesn't scale

**Key files**: `SwiftInterfaceAccessParser.cs`, `EnumHandler.cs`

---

## Feature F6: Safety & Production Hardening
**Pillar dependency**: None
**Score impact**: +0.00 (not measured in scores, critical for production)
**Effort**: 1 session

- **Proxy Dispose() cleanup**: GCHandle/EveryProtocol leak prevention
- **Finalizer safety**: Prevent double-free and leaked handles
- **SB0003 message improvement**: Include specific skip reason (not just "non-dispatchable")
- **Roslyn analyzer prototype**: Warn when `ISwiftObject` lacks `using`/`Dispose`

**Key files**: Protocol proxy emitters, `SwiftSafeHandle.cs`

---

## Feature F7: Runtime Metadata Prototype — COMPLETE
**Pillar dependency**: P1.2 (conformance graph)
**Score impact**: +0.00 (future enabler)
**Effort**: 1 session (completed March 2, 2026)

Proved `swift_conformsToProtocol` callable from C# at runtime via Cdecl P/Invoke. Dynamic conformance checks agree with static `swift_getWitnessTable` path.

**What shipped**:
- **`ProtocolDescriptor`**: Readonly struct wrapping `$s...Mp` protocol descriptor symbols. `LoadFromSymbol` + `IEquatable` + `Zero`/`IsValid`, following `ProtocolConformanceDescriptor` pattern
- **`SwiftConformance`**: Static class with dual API — `ConformsToProtocol` (throws on invalid inputs) and `TryGetWitnessTable` (returns false on invalid inputs). P/Invokes `swift_conformsToProtocol` via `CallingConvention.Cdecl`
- **26 tests**: Protocol descriptor loading (5 well-known protocols + error/equality), conformance checks (5 positive + 2 negative + 2 validation), witness table retrieval (4 tests), cross-validation against static path (3 tests)

**What was NOT shipped** (deferred):
- `swift_getAssociatedTypeWitness` — requires SwiftCC calling convention + `ProtocolRequirement` pointer layout complexity

**Key files**: `ProtocolDescriptor.cs`, `SwiftConformance.cs`, `SwiftConformanceTests.cs`
**Research**: [`swift-runtime-metadata-feasibility.md`](research/swift-runtime-metadata-feasibility.md)

---

## Suggested Sequencing

Features can be interleaved with foundation work. Independence from pillars noted.

```
IMMEDIATE (no pillar dependency):
  F1: nint overloads           ── DONE (March 2, 2026)
  F2: Noise reduction          ── DONE (March 2, 2026)
  F3: Protocol ext param lifts ── DONE (March 2, 2026)
  F5: String enum raw values   ── BLOCKED (no data source in compiled xcframeworks)
  F6: Safety                   ── 1 session

AFTER P2.1 (dispatch table):
  F4: Collection witness dispatch ── DONE (March 3, 2026)

AFTER P1.2 (conformance graph):
  F7: Runtime metadata         ── DONE (March 2, 2026) — may unblock F5 via runtime introspection
```

---

## Score Projections

Starting from current combined average of ~3.50. Three scenarios account for estimation uncertainty — each feature's impact depends on how many methods actually compile and how scoring categories weight them.

| Work | Base | Likely | Stretch | Notes |
|------|:----:|:------:|:-------:|-------|
| Foundation pillars (all) | 3.58 | 3.65 | 3.72 | P1.1/P4.1 unlock methods; P2/P1.3 are refactors |
| + F1 (nint) | 3.68 | 3.75 | 3.82 | Broad TypeFidelity lift, 300+ declarations |
| + F2 (noise) | 3.73 | 3.80 | 3.88 | Noise category, depends on ExistentialContainer hiding |
| ~~+ F3 (param gate lifts)~~ | 3.80 | 3.88 | 3.96 | **DONE** — Per-library variance: GRDB/Kingfisher benefit most |
| + F4 (collection dispatch) | 3.88 | 3.98 | 4.10 | Largest single feature, high per-library variance |
| + F6 (safety) | 3.88 | 3.98 | 4.10 | Production hardening, no score change |
| ~~+ F5 (string enums)~~ | — | — | — | BLOCKED: no data source in compiled xcframeworks |

**Scenario definitions**:
- **Base** (~25th percentile): Some feature impacts lower than estimated — e.g., nint overloads don't improve all libraries equally, noise reduction harder to scope than expected
- **Likely** (~50th percentile): Features deliver near estimated impact, no major surprises
- **Stretch** (~75th percentile): Features compound — e.g., struct conformers + param gate lifts together unlock more than the sum of parts

**Per-library confidence** (likely scenario):
- **High confidence ≥4.0**: SmartCardIO (4.56→4.8+), Nuke (3.55→4.0+), Kingfisher (3.40→4.0+), Alamofire (3.45→3.9-4.1)
- **Medium confidence 3.5-4.0**: GRDB (3.45→3.7-4.0), Stripe (3.30→3.6-3.9), CryptoSwift (3.60→3.9-4.1)
- **Structural ceiling <4.0**: RxSwift (~3.5, associated types + class-level generics), ObjectMapper (~3.5, heavy generic constraints)

**Getting 90% of libraries above 4.0** requires: all foundation pillars + F1-F4 (likely scenario)
**Getting 90% above 4.0 in base scenario** requires: all of the above + F5 + some library-specific patches

---

## Research Files Index

All supporting research in `src/docs/research/`:

| File | Topic |
|------|-------|
| `swiftinterface-conformance-audit.md` | ABI JSON TypeWitness data coverage |
| `swift-runtime-metadata-feasibility.md` | Runtime introspection API feasibility |
| `composition-helper-plan.md` | 4 helpers, 21 call sites, ~217 lines |
| `method-handler-dispatch-design.md` | 19-step pipeline, 5 ordering invariants |
| `self-return-investigation.md` | DynamicSelf ≠ τ_0_0, accidental correctness |
| `dispatch-table-dedup-investigation.md` | EmittedProjectedSignatures safe to refactor |
| `implementation-roadmap.md` | Quick win analysis (original, partially superseded) |

| `gap-root-cause-analysis.md` | Root cause breakdown per library to 4.0 |
| `anytype-integration-trace.md` | 5 concrete AnyType code path traces |
| `typwitness-coverage-validation.md` | TypeWitness coverage: 30-40% of AnyType gaps |
| `self-return-protocol-extension-trace.md` | Self-return already works for classes |
| `closure-gap-classification.md` | 14 categories, 184 methods classified |
| `iswiftobject-constraint-investigation.md` | 45 of 65 are incomplete `SatisfiesConstraint` |
