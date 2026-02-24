# Roadmap — Completed Items (Archived February 2026)

Items moved from `roadmap.md` when they were fully complete. See git history for implementation details.

---

## P0: Test Pipeline Hardening

**Status**: Complete
**Spec**: `testframework-review.md`

All items implemented (TH-1 through TH-7). TH-8 (semantic verification depth) deferred as ongoing practice.

- **TH-1. Compile gate** — `CompileCheck.csproj` in `build-and-test.sh` Step 2.5
- **TH-2/3/4. Baseline budgets** — `baselines.json` + `check-baselines.sh` (exit code, degraded, compiled-out, unsupported, crash-risk, strip count)
- **TH-5. Allowlist-based crash tolerance** — `run-tests.sh` extracts last test class, allowlist: `EnumMarshallingTests|OwnershipGCStressTests`
- **TH-6. Test profiles** — PR Gate + Nightly documented in `TestFramework/README.md`
- **TH-7. Reduce simulator flake** — default timeout 90s, deterministic device preference

---

## P1: Testing Depth — Completed Portions

### Gap 4: Protocol Witness Dispatch Runtime Tests — DONE (Interface Projection)

`BasicProtocolDispatchTests` with 33 tests (14 Tier 1, 9 Tier 2, 10 Tier 3). Covers
protocol conformance, blittable property/method dispatch through interfaces, string
method dispatch, and enum method/property dispatch. Proxy-based witness dispatch
(existential container path) deferred — requires wrapper library in RuntimeTestsApp.

### Gap 5: Complex Type Composition Tests — DONE

`BasicCompositionTests` with 23 tests (4 Tier 1, 2 Tier 2, 17 Tier 3). Covers class+closure, struct+optional-array, singleton+async, inheritance+protocol patterns.

---

## P3: Testing Infrastructure — Completed Portions

- **PInvokeEmitter unit tests** (Gap 6) — `PInvokeEmitterTests.cs` with 48 tests
- **Generic runtime tests** (Gap 7) — 30 tests total (20 existing + 10 new for unbound generics + generic free functions), Tier 3 pending confirmation
- **Error handling tests** (Gap 8) — `BasicThrowingTests` with 34 tests (24 passing Tier 1-2, 10 Tier 3)

---

## Completed Work Summary

All completed phases are archived in `Completed/`. Key milestones:

| Phase | What |
|-------|------|
| A-G | Core infrastructure through CryptoSwift validation (~1,700 unit + 185 runtime tests) |
| H1-H2 | Unit test gaps + 6 library binding bugs -> all 4 libraries 0 errors |
| I1/I1a/I1b | Mono JIT mitigation: Nuke wrapper path, BitwiseCopyable, ObjC async callbacks |
| K | Swift doc comments -> C# XML doc comments |
| Strategy D+B | MonoJitRiskDetector + Closure Cdecl expansion |
| Tier Promo | Tj dispatch thunks + IsFinal + tier promotions (172->185 runtime) |
| WU1-WU6 | Idiomatic C# binding API |
| DX Steps 1-5 | `--xcframework` mode, auto wrapper compilation, Swift.Runtime NuGet, .csproj/.targets emission, MSBuild SDK + templates |
| Validation 1-4 | 4 passes fixing 440+ binding errors across 25 libraries -> 0 generator errors |
| DX Improvements | C# type aliases, Codable pruning, enum PascalCase |
| Framework Deps | `--framework-dependency` CLI + `<SwiftFrameworkDependency>` MSBuild item |
| Gaps 6-8 | PInvokeEmitter tests (48) + generic runtime tests (10 new) + error handling tests (34) |
| Stripe Binding Fixes | ObjC enum types, URL return marshalling, exit codes |
| ObjC Framework Deps | SwiftModuleNotFoundException, ResolveObjCFramework(), ObjC fallback |
| DllImport Library Name | Replaced 9 hardcoded "SwiftBindings" strings with dynamic library name |

SwiftUI Bridge v2 (Phases 1-3) and TestFramework Phases A-D ran in parallel, adding comprehensive parameter type support, ABI-driven async inference, bridge hints, and ~184 runtime tests.

---

## Roadmap Items Completed (Binding Quality Phase)

### 1. Cross-Module Type Resolution (was P0 #1)

Steps 1a-1d all complete. Generator emits `{Module}Database.xml` after processing; `--module-database` CLI option loads dependency databases; MSBuild SDK collects databases from NuGet packages via `_CollectSwiftModuleDatabases` target; cross-module protocol conformance expanded (whitelist removed). Validated: StripePaymentSheet resolves 46 StripeCore + 87 StripePayments types (0 AnyType). Key files: `ModuleDatabaseEmitter.cs`, `TypeDatabase.cs`, `Sdk.targets`, `ConsumerTargetsEmitter.cs`.

### 2. ExistentialContainer Elimination from Public API (was P0 #2)

`ExistentialContainer{N}` removed from all public closure/delegate signatures, tuple element types, and generic type arguments. Known protocols → interface type, well-known → runtime type (e.g. `AnyError`), unknown → `object`. P/Invoke layer still uses containers internally. 0 matches across all 32 libraries. Key files: `ClosureHandler.cs`, `TupleHandler.cs`, `ClosureEmitter.cs`.

### 3. Native C# Enums for Simple Swift Enums (was P1 #3)

Steps 3a-3c complete. String-raw-value enums with instance methods now emit as C# enums. Non-frozen simple enums supported (removed `IsFrozen` gate, added `CanSafelyEmitAsSimpleEnum` safety gate). `Lazy<T>`-backed singleton caching for class-based enum cases. ~15% of enums now native C# (up from ~5%). Key files: `EnumDecl.cs`, `EnumHandler.SimpleEnum.cs`, `ModuleProcessor.cs`.

### 4. Optional<T> P/Invoke Truncation Fix (was P1 #4)

`_optbuf` wrapper covers all paths: standalone methods, constructors, properties, mutating methods, wrapper-owned methods, async, and sync Optional returns. No more silent data truncation for `T.Size > 8`. Key files: `OptionalPointerWrapperEmitter.cs`, `PInvokeEmitter.cs`, `WrapperEmitter.Return.cs`.

### 5. SwiftDictionary Projection (was P1 #5)

Runtime `SwiftDictionary<K,V>` implements `IReadOnlyDictionary<K,V>`. Generator projects returns → `IReadOnlyDictionary<K,V>`, params → `IDictionary<K,V>`. 144 `SwiftDictionary` occurrences in public signatures → 0. Key files: `SwiftDictionary.cs`, `SwiftDictionaryProjection.cs`, `WrapperEmitter.Return.cs`.

### Architecture Overhaul (Sessions 1-9)

9-session architecture rework replacing fragmented type conversion with unified `TypeProjectionFactory` + `ITypeProjection` + `MarshalPlan`. Eliminated ~1,000 lines of overlapping conversion code. `TypeHandlerContext` replaced mutable Conductor state. All 32/32 libraries passing at 0 compile errors post-rework.

### 6. Class Inheritance (Phase 1, Sessions I1-I6)

6-session implementation of Swift class inheritance in generated C# bindings. Full plan: `class-inheritance-implementation.md`.

- **I1-I2 (Parse & Resolve)**: `superclassUsr`, `superclassNames`, `inheritsConvenienceInitializers`, `hasMissingDesignatedInitializers` parsed from ABI JSON. `ModuleProcessor.ResolveClassHierarchy()` resolves same-module superclasses with cycle detection. `TypeRecord.SuperclassTypeName` persisted for cross-module support. Generic superclass names guarded (stored as null).
- **I3 (Core Emission)**: Topological sort (Kahn's algorithm in `BaseHandler.TopologicallySortTypes`). `class Derived : Base` syntax. Protected `_payload` on root, shared by derived. `SwiftInheritanceChain` sentinel for constructor chaining. ISwiftObject re-implementation on derived (own type metadata). Disposal `<remarks>` on all classes. Validation: 32/32 (up from 22/32 mid-session).
- **I4 (Virtual/Override Dispatch)**: `overriding` field and `Override`/`Final` declAttributes parsed. `virtual`/`override`/`sealed override` emitted on class instance methods and properties. `HasMethodInResolvedAncestors`/`HasPropertyInResolvedAncestors` walks resolved superclass chain matching by name + param count + param types + `WasEmitted`. CS0108/CS0109 warning suppressions removed.
- **I5 (Protocol Conformance Inheritance)**: `GetEmittableAncestors` walks `ResolvedSuperclass` chain for protocol conformance validation. Inherited conformance dictionary entries. Empty conformance symbols resolved from ancestors. `IsEffectivelyDerived` canonical predicate.
- **I6 (Validation)**: All edge cases verified as handled: generic base classes (fallback to flat), actor inheritance (non-issue), cross-module inheritance (flat fallback), multi-level ObjC chains (stops at boundary), TestFramework Animal/Dog hierarchy working.

**Results**: 60 derived classes across 12 libraries now have proper C# inheritance. 4,089 unit tests, 700 integration, 221 runtime, 94/94 must-pass, 32/32 validation. Post-inheritance score assessment: Alamofire 2.90→~3.15, SnapKit 3.20→~3.50, Lottie 3.85→~4.05.
