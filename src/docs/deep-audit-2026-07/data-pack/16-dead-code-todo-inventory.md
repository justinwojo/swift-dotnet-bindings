# Data Pack — Dead Code / TODO / NotImplemented Inventory

**Date**: 2026-07-16  
**Mode**: Read-only evidence extraction (no production edits)  
**Scope**: `src/Swift.Bindings/src`, `src/Swift.Runtime/src` (+ light cross-checks of tests for NIE call-sites only)  
**Method**: Targeted greps for `NotImplementedException`, literal `TODO`/`FIXME`/`HACK`, `#if false`, dead-branch comments, dual metadata types; sample capped (not full LOC audit).

---

## Executive counts

| Signal | Production hits (approx) | Notes |
|--------|-------------------------:|-------|
| `throw new NotImplementedException` | **9** sites (generator **5** + runtime **4**) | Tests excluded; see §1 |
| Literal `// TODO` / `FIXME` / `HACK` in generator+runtime **source** | **~1** intentional emit string + **0** FIXMEs | Extremely clean; gaps use prose `"not yet supported"` instead |
| `#if false` / `#if FALSE` | **0** | No retired-path if-def blocks |
| Documented dead / unused production branches | **≥8** named items | Dual oracles, projection dead arms, obsolete overloads |
| Second metadata model | **Yes** — `SwiftTypeInfo` / `MetadataKinds` vs live `TypeMetadata` / `TypeMetadataKind` | Partial live (VWT size/EI); `FieldOffsetVector` dead |

---

## 1. `NotImplementedException` production sites

Tests excluded: `Swift.Analyzers.Tests`, `Swift.Runtime/tests`, generator unit tests that *assert* NIE.

### 1.1 Runtime (`src/Swift.Runtime/src`)

| File:line | Role | Severity / notes |
|-----------|------|------------------|
| [`SwiftMetadata.cs:75`](../../Swift.Runtime/src/Swift/Runtime/SwiftMetadata.cs) | `SwiftTypeInfo.FieldOffsetVector` `default` arm | **Dead API surface.** Only `MetadataKinds.Struct` is implemented; all other kinds throw. **No production callers** of `FieldOffsetVector` (repo grep = definition only). Part of “second metadata model.” |
| [`EveryProtocol.cs:100`](../../Swift.Runtime/src/Swift/Runtime/EveryProtocol.cs) | `ISwiftObject.GetProtocolConformanceDescriptor` | **Intentional interface stub.** Message: conformances managed by protocol proxy classes. Hit only if someone reflects through `ISwiftObject` on `EveryProtocol` itself. |
| [`SwiftOptional.cs:255`](../../Swift.Runtime/src/Swift/SwiftOptional.cs) | Same interface member | **Intentional.** Comment: Optional doesn’t support protocol witness lookup. |
| [`SwiftClosedRange.cs:178`](../../Swift.Runtime/src/Swift/SwiftClosedRange.cs) | Same interface member | **Intentional.** Consumed only as param/return via stdlib-generic cdecl bridge. |

**Doc-only (no throw):** `TypeMetadata.cs:367` XML `<exception cref="NotImplementedException">` on `TryGetTypeMetadataUncached` — **stale docs**; body does not throw NIE (uses reflection / NativeAOT fallbacks).

### 1.2 Generator (`src/Swift.Bindings/src`)

| File:line | Role | Severity / notes |
|-----------|------|------------------|
| [`SwiftABIParser.cs:3680`](../../Swift.Bindings/src/Parser/SwiftABIParser.cs) | `CreateTypeSpec` unknown `node.Kind` | **Live degradation path.** Caught at `SwiftABIParser.cs:1172` → log warning + drop node (`_nodesDroppedWithError`). Not a process kill. |
| [`IHandler.cs:683`](../../Swift.Bindings/src/Marshaler/IHandler.cs) | Unknown `BaseDecl` type in handler dispatch | **Hard fail** if a new decl kind is added without a handler. Expected invariant, not a feature stub. |
| [`ITypeDatabase.cs:129`](../../Swift.Bindings/src/TypeDatabase/ITypeDatabase.cs) | Default `ApplyEmissionResult` | **Fail-closed default** for interface. Real `TypeDatabase` overrides; silent no-op would miscompile (documented). Not “unfinished work.” |
| [`ProtocolListTypeSpec.cs:48`](../../Swift.Bindings/src/Model/TypeSpec/ProtocolListTypeSpec.cs) | `SpecComparer.GetHashCode` | **Likely dead.** `SortedList` uses `IComparer` only; hash never consulted. Latent footgun if comparer is reused as equality. |
| [`Provenance.cs:119`](../../Swift.Bindings/src/Model/TypeSpec/Provenance.cs) | `ToString()` unknown provenance shape | **Invariant guard** (Instance / Extension / TopLevel exhaust the model). |

### 1.3 Catch / rethrow (not throw sites)

| File | Behavior |
|------|----------|
| `SwiftABIParser.cs:1172` | Catches NIE from type-spec / node handling → warning, drop |
| `TbdParser.cs:79` | Catches NIE from format-specific parser → `ParsingException` (“not yet implemented”) |

### 1.4 Classification summary

| Class | Sites | Action posture |
|-------|------:|----------------|
| Intentional `ISwiftObject` stubs | 3 (EveryProtocol, Optional, ClosedRange) | Keep; or split interface later (`IGeneratedSwiftObject` post-1.0) |
| Incomplete second-metadata API | 1 (`FieldOffsetVector`) | Delete with model or implement enum/class offsets |
| Fail-closed / invariant | 3 (IHandler, ITypeDatabase default, Provenance) | Keep |
| Parser node gap (recoverable) | 1 + catch sites | Expand arms as digester adds kinds |
| Dead comparer hash | 1 | Delete or implement if needed |

---

## 2. TODO / FIXME / HACK inventory

### 2.1 Literal markers (generator + runtime production)

| Pattern | Hits in `Swift.Bindings/src` + `Swift.Runtime/src` |
|---------|---------------------------------------------------|
| `// TODO` | **1 source emission** (`StructsAndEnumsEmitter` writes TODO **into generated C#**) |
| `// FIXME` | **0** |
| `// HACK` | **0** |
| `// XXX` | **0** |
| Stale *reference* to “ProtocolHandler.cs TODO” | **1** comment only — **no matching TODO in ProtocolHandler.cs** |

**Production source emission TODO (intentional consumer-facing gap):**

```text
// TODO: {constant.Name} ({fieldType}) — [Field] not supported for this type
```

- Path: `ObjC/Emitter/StructsAndEnumsEmitter.cs:625`
- Unit tests assert presence/absence of the emitted string (`StructsAndEnumsEmitterTests`).

**Generated-template TODO (not a generator-source marker):**

- `SwiftUIBridgeEmitter.cs:2263` emits `// Status: Template — complete the TODO sections` into **bridge template** output for incomplete SwiftUI views.

**Stale cross-reference (docs/AI hazard):**

- `ProtocolProxyEmitter.InterfaceImpl.cs:192` — “see ProtocolHandler.cs TODO” — **no `// TODO` in ProtocolHandler**; describes latent static-requirement / SetVtable co-gate issue instead.

### 2.2 Prose “not yet …” sample (top ~40 by path)

Literal TODO is rare; capability gaps are phrased as comments / skip messages / `NotSupportedException`. Sample **by path order** (capped; full list is larger):

| # | Path | Snippet theme |
|---|------|---------------|
| 1 | `Marshaler/ClosureHandler.cs` | Complex closure return marshalling not yet implemented |
| 2 | `Marshaler/Projection/ResultProjection.cs` | `Result<T,E>` parameter direction not supported |
| 3 | `Marshaler/TupleHandler.cs` | Call-argument path incomplete vs P/Invoke element type |
| 4 | `Parser/SwiftABIParser.cs` | Non-frozen foreign struct receivers not yet supported |
| 5 | `Configuration/XCFrameworkResolver.cs` | Multi-module xcframeworks not yet supported |
| 6 | `Model/TypeDecl/EnumDecl.cs` | Unsigned-64 raw values not end-to-end |
| 7 | `Demangler/TbdParser/TbdParser.cs` | TBD format detected but parsing not implemented |
| 8 | `Emitter/.../ConstrainedExistentialBridge.cs` | Utf8Slice body emission not yet |
| 9 | `Emitter/.../KeyPathSingletonEmitter.cs` | Per-conformer KeyPath params not yet |
| 10 | `Emitter/.../ConcreteProtocolSpecializationEmitter.cs` | Optional return / method-own generics async not yet |
| 11 | `Emitter/.../ConcreteProtocolSpecializationEmitter.Async.cs` | Typed throws not yet |
| 12 | `Emitter/.../ExistentialBypassEmitter.cs` | Bypass path incomplete |
| 13 | `Emitter/.../KeyPathBoundValueSpecializationEmitter.cs` | Non-void / throwing RouteC not yet |
| 14 | `Emitter/.../MethodClosureBridge.cs` | Multi optional-existential args not yet |
| 15 | `Emitter/.../AsyncMethodGenericBridgeEmitter.cs` | Patterns not yet exercised by fixtures |
| 16 | `Emitter/StringEmitter/ProtocolProxyEmitter.cs` | PAT proxy → UnmanagedCallersOnly in generic not yet |
| 17 | `Emitter/StringEmitter/ProtocolProxyEmitter.InterfaceImpl.cs` | Subscript dispatch SB0003 “not yet supported” (emitted Obsolete) |
| 18 | `Emitter/StringEmitter/PropertyWrapperEmitter.cs` | Dynamic PWT / buffer-mode ABI not yet |
| 19 | `Emitter/StringEmitter/ConstructorWrapperEmitter.cs` | Default-param overloads on generic constructors |
| 20 | `Emitter/StringEmitter/WrapperValidation.cs` | Cross-reference “not yet implemented” |
| 21 | `Emitter/StringEmitter/BridgeHints.cs` | `resultMonitor` accepted but not yet supported |
| 22 | `Emitter/StringEmitter/Handler/SubscriptHandler.cs` | Per-member `@MainActor` on subscript not surfaced |
| 23 | `Emitter/StringEmitter/MemberEmissionValidator.cs` | Subscripts on concrete types “not yet emitted” (partially historical — verify vs live) |
| 24 | `Reporting/BindingReport.cs` | Conditional-conformance wrapper extensions; auto-bridged missing .NET type |
| 25 | `Reporting/WorkaroundRecommendations.cs` | Consumer workarounds for “not yet” families |
| 26 | `Reporting/SkipDisposition.cs` | KnownLimitation “not yet supported” triage |
| 27 | `Emitter/StringEmitter/ValidationRuleSet.cs` | Modules / Foundation types not yet in .NET |
| 28 | `Marshaler/MarshallingHelpers.cs` | Auto-bridged type absent from Foundation assembly |
| 29 | `ObjC/Emitter/StructsAndEnumsEmitter.cs` | Field constant TODO emission |
| 30 | `AppleTypesManifest/AppleTypesCsEmitter.cs` | Protocol conformance not implemented (runtime throw string) |
| 31 | `CliOptions.cs` / `Program.cs` | Multi-module / legacy option comments (capability bounds) |
| 32 | `Configuration/NativePackagingPolicy.cs` | “will be produced” / wrapper not yet on disk (not a feature gap) |
| 33 | `Runtime/SwiftResult.cs` | Remarks: full case discrimination “future work” (**partially superseded** — type has success/failure APIs; remarks stale) |
| 34 | `Runtime/AnyType.cs` | Unsupported type placeholder |
| 35 | `Runtime/SwiftOptional.cs` / `SwiftClosedRange.cs` | Conformance NIE (see §1) |
| 36 | `Runtime/SwiftMetadata.cs` | Non-struct FieldOffsetVector NIE |
| 37 | `Emitter/.../Handler/MethodClosureBridge.cs` | Count/shape rejections |
| 38 | `Marshaler/Projection/ClosureProjection.cs` | Dead lambda-builder notes (see §4) |
| 39 | `Marshaler/Projection/AsyncProjection.cs` | Dead `GetSwiftWrapperCode` / `CallbackDeclarations` (see §4) |
| 40 | `Marshaler/NameProvider.cs` | `[Obsolete]` ambiguous `ParserNameToSwift(string)` |

**Runtime production:** essentially **zero** `TODO`/`FIXME`/`HACK` markers; remaining gaps are NIEs + prose.

---

## 3. `#if false` / retired-path comments

### 3.1 `#if false`

**None** under `src/**/*.{cs,swift}`.

Platform / feature gates that *are* present (live, not retired):

- Runtime: `#if IOS || TVOS || MACCATALYST || MACOS` (CG geometry interop), `#if DEBUG` in `TypeMetadata`
- Generator *emits* `#if canImport(UIKit)`, `#if __IOS__ || …`, `#if targetEnvironment(simulator)` into wrappers/bridges

### 3.2 Retired / removed paths (comment archaeology — sample)

| Location | What was retired |
|----------|------------------|
| `SwiftWrapperPostProcessor.cs:79–84, 376+` | Safety-net strip patterns **(b)–(f)** removed; emission-time gates replace them; residual pattern (a) remains |
| `BindingReport.cs` | SkipReason **UnsupportedThrowingAsyncStream**, **SuppressedProxyMethodBody** marked **Retired** (enum retained for coverage) |
| `ExistentialProjection.cs` / `ClosureHandler.cs` / `ExistentialHandler.cs` | CoGater proxy-reference / wrap-fallback **post-pass** → emit-time checkpoints |
| `MethodMarshalPlan.cs:6–13` | Former aggregate plan records **deleted** (zero refs); live type is `SyncMethodPlan` |
| `AsyncHarnessEmitter.cs` | Former `BuildSwift*` duplicate deleted |
| `ModuleProcessor.cs:1380` | `InheritedRequirementsOnly` **no longer set** |
| `Program.cs` | Legacy `depModuleNamesForCollision` single-list JSON still hydrated for back-compat |
| `CliOptions.cs` | Explicit note: legacy regex interface-facts producer **removed** |
| Roadmap / Track G1 | Generate-then-strip proxy + wrapper-contract co-gates largely **retired** as post-passes |

These are **documentation of completed cleanup**, not `#if false` zombies.

---

## 4. Dead / unused production code (named inventory)

| ID | Item | Evidence | Risk if “cleaned wrong” |
|----|------|----------|-------------------------|
| **D01** | `ProtocolProxyEmitter.Helpers.GetMethodKey` | `Helpers.cs:177–180`; **zero** call sites; label- and async-blind (wrong vs `EveryProtocolEmitter.GetMethodKey`) | Re-homing into live walks re-opens slot collapse (A5b-002 / S1-13) |
| **D02** | `ITypeProjection.CallbackDeclarations` production readers | **0** refs under `Swift.Bindings/src`; only unit tests | Wiring live without ClosureEmitter parity |
| **D03** | `ClosureProjection` escaping-param lambda / keepAlive builder | Self-commented dead for live emission (`:111–115`); divert to `ClosureEmitter` | Half-live keepAlive vs EC2+ |
| **D04** | `AsyncProjection.GetSwiftWrapperCode` + its `CallbackDeclarations` | Self-doc: **not on production async path**; unit tests pin shape; cancel-key race if revived as-is | Registry key reuse |
| **D05** | `SwiftTypeInfo.FieldOffsetVector` | NIE for non-struct; **no callers** | Assuming field offsets for class/enum |
| **D06** | `PInvokeEmitter.ComputeEntryPoint(MethodDecl)` | Still used by **CrossModuleExtensionEmitter** + many tests; env overload is AF13 path for main emit | Treating as fully dead (S1-37 partially wrong — production still hits decl overload) |
| **D07** | `NameProvider.ParserNameToSwift(string)` | `[Obsolete]`; prefer decl overload | Still callable; silence Obsolete in builds |
| **D08** | `ProtocolListTypeSpec.SpecComparer.GetHashCode` | NIE; SortedList doesn’t hash | Reuse as `IEqualityComparer` |
| **D09** | Emitting unused `_dbw_` silgen shims for async default trims | Documented in upstream-issue-04 / roadmap | Stripping without symbol audit |
| **D10** | Deleted MethodMarshalPlan aggregate types | Already gone; comment only | Reintroducing shadow names (`PInvokeDeclarationInfo`) |

**Not dead (do not delete without deeper proof):**

- `SwiftTypeInfo` **as a TypeRecord field** — still constructed in `ModuleProcessor`, read in `SwiftValueLayout` for size / extra inhabitants when `MetadataPtr != 0`
- `GetWitnessTableSymbol` — used by `ProtocolProxyEmitter.SwiftObject.cs`
- Retired SkipReason enum members — retained for report coverage / total enum

---

## 5. Second metadata model / parallel type graphs

### 5.1 Live primary model (runtime)

| Type | File | Role |
|------|------|------|
| `TypeMetadata` / `TypeMetadataKind` / `TypeMetadataFlags` | `Runtime/TypeMetadata.cs` | **SSOT** for runtime kind, size, VWT, registration, collections, Optional, Hashable, etc. |
| `ValueWitnessTable` | `Runtime/ValueWitnessTable.cs` | Live VWT ops |
| `TypeMetadataCache` / helpers | various | Cache + `ISwiftObject` metadata factories |

### 5.2 Second / incomplete model

| Type | File | Role | Dead vs live |
|------|------|------|--------------|
| `MetadataKinds` | `SwiftMetadata.cs:30` | Parallel kind enum (subset of `TypeMetadataKind`; missing e.g. ForeignReferenceType, ExtendedExistential, …) | **Mostly unused** outside `SwiftTypeInfo` |
| `SwiftMetadata` ref struct | `SwiftMetadata.cs:86` | Common metadata header + TypeDescriptor | Only via `SwiftTypeInfo.Metadata` |
| `StructDescriptor` / `NominalTypeDescriptor` / `FieldRecord` / … | same file | Descriptor / field-record parsing (symbolic refs) | Field-record demangle helpers may still support tooling paths; **FieldOffsetVector** path is incomplete |
| `SwiftTypeInfo` | `SwiftMetadata.cs:52` | `{ MetadataPtr, Metadata*, ValueWitnessTable*, FieldOffsetVector }` | **Partial live:** generator stores `MetadataPtr` on `TypeRecord`; `SwiftValueLayout` uses VWT size / EI when pointer non-zero. **`FieldOffsetVector` dead + NIE.** Cross-compile often has `MetadataPtr == 0` (tests document this). |

**Post-1.0 roadmap claim** (`docs/Future/post-1.0-architecture-roadmap.md:65`):

> Dead second metadata model deletion (`SwiftTypeInfo` / `MetadataKinds`). Throws `NotImplementedException` on every kind except struct.

**Audit nuance:** Deletion is **not** free “byte-identical delete-all.” Must:

1. Keep or re-home `TypeRecord.SwiftTypeInfo` / `MetadataPtr` + VWT readers in `SwiftValueLayout`, **or** migrate those readers to the primary `TypeMetadata` shape at generate time.
2. Delete only the incomplete **FieldOffsetVector** / unused descriptor graph once no dylib-host path depends on it.
3. Avoid conflating with generator **TypeDatabase** metadata (XML / TypeRecord) — different layer.

### 5.3 Other “dead types” / deferred consolidations (not second-metadata)

| Item | Status |
|------|--------|
| `ExistentialContainer0..8` | **Live** copy-paste family; post-1.0 source-gen (S1-16), not dead |
| Former `MethodMarshalPlan` aggregate records | **Already deleted** |
| `IGeneratedSwiftObject` split | Planned post-1.0; not present as dead type |
| SB0007 diagnostic ID | **Not emitted** (reserved/retired) — see data-pack 01 / 12 |

---

## 6. Obsolete / always-throw production surfaces (related, not NIE)

Not `NotImplementedException`, but “compile-but-dead” or fail-loud:

| Surface | Mechanism |
|---------|-----------|
| Emitted `[Obsolete(SB0001–SB0006)]` members | Consumer-facing degrade (data-pack 12) |
| Proxy `NotSupportedException` stubs | Static / non-dispatchable / inherited members (`ProtocolProxyEmitter.InterfaceImpl`) |
| `ResultProjection` param plan | `NotSupportedException` |
| `ISwiftObject` default / AnyType / AnyHashable | `InvalidOperationException` / `NotSupportedException` |

---

## 7. Cross-links (deep-audit)

| Finding class | Track / pack |
|---------------|--------------|
| Dead `Helpers.GetMethodKey` | Track A5b DA-W2-A5b-002; synthesis S1-13 |
| Second metadata model | post-1.0 roadmap; methodology L4 |
| ClosureProjection / CallbackDeclarations dead | roadmap §2.1; Track A4; Design reverse-dispatch-lifetime |
| ComputeEntryPoint dual overloads | Track A1; S1-37 (**revise**: decl overload still production-used in CrossModuleExtensionEmitter) |
| Retired co-gates / skip reasons | Track G1; data-pack 00-skipreason-catalog; BindingReport |
| SB000x obsolete family | data-pack 01, 12 |

---

## 8. Suggested follow-ups (inventory only — no execution)

**Safe / low-risk delete candidates (byte-identical if greps stay clean):**

1. `ProtocolProxyEmitter.Helpers.GetMethodKey` (D01)
2. `SpecComparer.GetHashCode` NIE → implement or remove from interface surface (D08)
3. Stale `TypeMetadata` XML `NotImplementedException` doc (D-doc)
4. Stale “ProtocolHandler.cs TODO” comment (InterfaceImpl)
5. Stale `SwiftResult` “future work” remarks if APIs already cover cases

**Needs design / fixture (not blind delete):**

1. `SwiftTypeInfo` / `MetadataKinds` consolidation into `TypeMetadata` (D05 + §5)
2. Projection `CallbackDeclarations` / `GetSwiftWrapperCode` (delete vs revive under one IR)
3. Migrate `ComputeEntryPoint(MethodDecl)` callers to env overload, then obsolete decl overload
4. Async `_dbw_` unused silgen shims (emission audit)

**Do not treat as unfinished TODO:**

- Fail-closed `ITypeDatabase.ApplyEmissionResult` default
- `ISwiftObject` conformance stubs on Optional / ClosedRange / EveryProtocol
- Parser NIE catch → soft drop (working degradation)

---

## 9. Method limits / honesty

- Greps capped; “not yet” sample is **illustrative top 40**, not exhaustive capability catalog (use SkipReason / BindingReport for that).
- No full unused-member analyzer (Roslyn IDE0005 / whole-solution dead-code analysis) was run.
- No binary/IL unused export scan.
- `FieldOffsetVector` / `GetMethodKey` “zero callers” is **source grep**, not reflection-proof.

**Bottom line:** The codebase is unusually free of literal `TODO`/`FIXME` and has **zero `#if false` graveyards**. Remaining deadness is **named dual paths, obsolete overloads, one wrong GetMethodKey, and a partial second metadata model** — not a large unmarked dump.
