# Data Pack — NetUnavailableType (780) + MissingWrapperSymbol (64)

**Date**: 2026-07-16  
**Mode**: Evidence extraction (read-only; no production edits)  
**Primary corpus**: `build/baselines/validation-baseline.json` → `skip_metrics.skip_reasons`  
**Disposition source**: `SkipDisposition.cs`  
**Related data packs**: `00-skipreason-catalog.md`, `02-emit-then-break-inventory.md` §5, `04-validation-corpus-skip-heatmap.md`

---

## Headline (validate baseline snapshot)

| Reason | Count | Share of skips | Disposition | Product character |
|--------|------:|---------------:|-------------|-------------------|
| **NetUnavailableType** | **780** | **10.6%** | **KnownLimitation** | Curated OS/Foundation type present in Swift ABI, absent from .NET assembly |
| **MissingWrapperSymbol** | **64** | **0.9%** | **Review** | Pipeline integrity residual: planned wrapper P/Invoke with no live symbol |

Same baseline also reports post-processor strip sub-causes (related but **not** the SkipReason histogram):

| Sub-cause | Count | Relation |
|-----------|------:|----------|
| InternalType | 83 | Swift wrapper blocks stripped for internal-type reach |
| NSInvocation | 1 | Residual strip shape |
| Other | 0 | — |

BindingTests contrast (live catalog snapshot in `00-skipreason-catalog.md`):

| Axis | BindingTests | Validate baseline |
|------|-------------:|------------------:|
| NetUnavailableType | **1** | **780** |
| MissingWrapperSymbol | **0** (via strip; residual Review may still be rare) | **64** |
| `wrapper_stripped_count` | **0** (tripwire) | n/a (validate uses strip sub-cause counts) |

**Insight:** NetUnavailableType is almost entirely a **real-library / Apple-framework surface** phenomenon (labels, predicates, scene-open context). MissingWrapperSymbol is a **small but Review-tier integrity** residual on hard third-party libs; BindingTests deliberately ratchets strip → 0.

---

# A) NetUnavailableType (780)

## A.1 What the reason means

From `BindingReport.cs` (`SkipReason.NetUnavailableType`):

> Member signature references a Swift type that is **auto-bridged but not yet present** in the .NET Foundation (or similar) assembly — e.g. `Foundation.LocalizedStringResource` in a container/closure position, or `Foundation.Predicate`. The **owning module IS supported**; only the individual type is unavailable in .NET.

Disposition: **KnownLimitation** (`SkipDisposition.cs`).

Consumer recommendation (`WorkaroundRecommendations`):

> Write a Swift wrapper that exposes the data through a supported type (e.g. a plain `String`).

Short description: *"Foundation type not yet available in .NET."*

## A.2 When it is recorded

### Admission oracle (single classification funnel)

`ValidationRuleSet.ClassifyUnsupportedReference` → `UnsupportedReferenceKind.NetUnavailable` → `ToSkipReason` → `SkipReason.NetUnavailableType`.

Call sites (member gates):

| Gate | File | Behavior |
|------|------|----------|
| Method params/return | `MemberEmissionValidator.cs` (~1036–1051), `MemberGateEvaluator.cs` (~408–417) | Skip with Details naming the offending module-qualified type |
| Properties | `MemberEmissionValidator.cs` (~179–197) | Same; property Details phrase differs |
| Subscripts | `SubscriptHandler.cs` (~82) | Same classification family |

Predicate:

```
AppleFrameworkRegistry.IsNetUnavailableType(moduleQualifiedName)
  ← apple-frameworks.json per-module "netUnavailableTypes"
  ← loaded as "{Module}.{TypeName}" into HashSet
```

**Single source of truth:** `apple-frameworks.json` → `AppleFrameworkRegistry.IsNetUnavailableType` → `ValidationRuleSet.IsNetUnavailableBridgedType`. Do **not** hardcode new type names in C# gates.

### Curated type inventory (current tree)

| Module | `netUnavailableTypes` entry | Module-qualified key |
|--------|----------------------------|----------------------|
| **Foundation** | `LocalizedStringResource`, `Predicate` | `Foundation.LocalizedStringResource`, `Foundation.Predicate` |
| **UIKit** | `UIOpenURLContext` | `UIKit.UIOpenURLContext` |

Only these three types can produce this SkipReason today. Count **780** is **member-frequency**, not type-frequency: the same types appear on hundreds of Apple / real-lib APIs (especially localized labels).

### Scalar carve-out (LocalizedStringResource only)

Bare top-level **non-generic** `Foundation.LocalizedStringResource` on the **simple concrete** wire path is **not** classified NetUnavailable when `allowProjectableScalar: true`:

| Condition | Result |
|-----------|--------|
| Scalar LSR + `AllowsProjectableScalarCarveOut` (not async, not method-generic, not generic parent) | **Emit** — projects to C# `string` via `StringProjection` |
| Optional / Array / Dictionary / nested generic of LSR | **NetUnavailableType** (recursion never passes carve-out) |
| LSR on async / method-generic / generic-parent member | **NetUnavailableType** (carve-out false) |
| `Foundation.Predicate` (any position, any flag) | **Always NetUnavailableType** |
| `UIKit.UIOpenURLContext` | **Always NetUnavailableType** |

Carve-out helpers: `MarshallingHelpers.IsLocalizedStringResource`, `AllowsProjectableScalarCarveOut(MethodDecl|PropertyDecl)`.

Projection note (`TypeProjectionFactory.cs`): LSR maps to `StringProjection` and **does not** require an Apple-supplement assembly reference — emitted C# only names `string`. The `@_cdecl` wrapper rebuilds with `LocalizedStringResource(stringLiteral:)` / resolves with `String(localized:)`.

## A.3 Distinct from neighboring reasons

| Reason | How it differs |
|--------|----------------|
| **SwiftUIConstraint** | Historical mislabel for LSR/Predicate when they hit the unsupported-module arm. **Fixed classification path** now returns NetUnavailable first. Residual SwiftUIConstraint (124 on validate) should be **real** SwiftUI/Combine refs. Older BindingAudit docs still say "SwiftUIConstraint for LSR" — treat those as **pre-reclass** artifacts unless revalidated. |
| **AbsentFrameworkType** | Framework value type (USR ends in V/O) resolved only by synthesizing a bridged ObjC **class** record — no real .NET type exists. Not a curated `netUnavailableTypes` entry. |
| **OwnedByAppleSupplement** | Type is **owned** by `SwiftBindings.Apple` and suppressed from re-generation so consumers take the hand-rolled projection. Different lifecycle. |
| **UnsupportedType** | Type needs exporting / not in public ABI path — not "auto-bridged but missing from .NET". |

## A.4 Relationship to Foundation / Apple supplement

### What the Apple supplement **does** ship (`Swift.Bindings.Apple/Sources/Foundation/`)

| Type | Role |
|------|------|
| `Data`, `URL`, `URLRequest` | Hand-rolled Foundation projections |
| `AnyError` | Error existential carrier |
| `AttributedString` | Hand-rolled |
| `Measurement` | Hand-rolled |

Plus other modules (ActivityKit LiveActivity, ManagedSettings Token, SwiftUI Text shims). **None of** `LocalizedStringResource`, `Predicate`, or `UIOpenURLContext` are in the supplement today.

### Ownership model

| Layer | Responsibility for net-unavailable types |
|-------|------------------------------------------|
| **Generator gate** | Fail-closed skip → no CS0234 to a missing `Foundation.LocalizedStringResource` C# type |
| **apple-frameworks.json** | Curated allowlist of "auto-bridged in Swift, absent in .NET" names |
| **Scalar LSR carve-out** | Product exception: project bare LSR as `string` without a real type binding |
| **SwiftBindings.Apple** | Future home **if** a real typed binding (not string collapse) is desired for LSR / Predicate / scene-open context |
| **OwnedByAppleSupplement** | Only after a hand-rolled type exists and the generator is told to suppress re-emit |

### Why 780 is large

Cross-library label surface: Apple frameworks and SDKs pass `LocalizedStringResource` on titles, dialogs, parameters, entity properties, alert configuration, etc. Even with scalar carve-out, **generic parents**, **optional/container positions**, and **async** paths still drop — and those dominate AppIntents-class APIs (`IntentParameter`, `EntityProperty`, `DisplayRepresentation`, …).

Historical AppIntents audit (pre/around reclass) attributed **~764** skips to LSR alone under the old SwiftUIConstraint label. Today's **780 NetUnavailableType** is the truthful successor bucket across the **whole** validation corpus (not AppIntents-only).

## A.5 BindingTests sample

| Artifact | Path | What it proves |
|----------|------|----------------|
| Fixture | `BindingTests/Sources/SwiftBindingsTestLib/Foundation/LocalizedStringResource.swift` | Scalar param/return/property/ctor **in**; optional LSR member **must stay out** |
| Runtime tests | `BindingTests/RuntimeTestsApp/FoundationInterop/LocalizedStringResourceTests.cs` | String ↔ LSR identity round-trip on iOS 16+ |
| Unit tests | `ValidationRuleSetClassificationTests.cs` | Strict vs carve-out; Optional/Array stay NetUnavailable; Predicate never carved; `ToSkipReason` mapping |
| Catalog count | BindingTests skip histogram | **1** NetUnavailableType row (the optional-LSR fixture member) |

**Not covered in BindingTests (as first-class fixtures):** `Foundation.Predicate`, `UIKit.UIOpenURLContext` — only unit-level classification for Predicate.

## A.6 Fix vs document vs capacity

| Action | Scope | Notes |
|--------|-------|-------|
| **Document** | Primary for product | KnownLimitation by design. Wiki / consumer docs: "these Foundation/UIKit types have no .NET type; scalar LSR → string; write a Swift wrapper for nested/predicate cases." |
| **Capacity — expand scalar LSR** | Medium product value | More call sites already emit via carve-out; nested LSR remains majority of the 780. |
| **Capacity — nested LSR** | High cross-lib surface, hard ABI | Would need Optional/Array/Dictionary marshalling of a non-exist type **or** a real supplement type + wire format. Not a one-line gate flip. |
| **Capacity — Predicate** | Medium/hard | True `Foundation.Predicate<T>` binding is a different product (expression trees / #Predicate macros). Wrapper-returning filtered arrays is the practical consumer workaround. |
| **Capacity — UIOpenURLContext** | Low–medium | Scene open-URL context; only useful with UIScene lifecycle from C#. |
| **Fix (already landed)** | Classification honesty | NetUnavailable vs false SwiftUIConstraint; scalar LSR projection; unit + BindingTests gates. |
| **Do not** | Treat 780 as generator crash bug | Members are correctly dropped to avoid CS0234. Growth should track **new** `netUnavailableTypes` entries or new Apple surface using existing entries — not silent integrity failure. |

**Recommended posture:** **Document + capacity roadmap**, not Review-tier panic. Optional: product decision whether LSR belongs in `SwiftBindings.Apple` as a first-class type vs permanent string collapse.

---

# B) MissingWrapperSymbol (64)

## B.1 What the reason means

From `BindingReport.cs` / `WorkaroundRecommendations`:

A C# P/Invoke was planned against a Swift `@_cdecl` / Swift-CC wrapper symbol that **does not exist** in the compiled wrapper (or was never registered by wrapper-emit). The member is **suppressed** to avoid `DllNotFoundException` / `EntryPointNotFoundException` at first call.

Disposition: **Review** — "the tool cannot fully explain this as a decided product gap; treat growth as integrity tripwire."

Two **honest** causes (recommendation text):

1. **Strip path** — symbol stripped during wrapper compilation / post-process; C# co-gated afterward.  
2. **Contract path** — wrapper-emit bailed after a symbol was claimed (or never registered); in-band contract gate rolls the member back.

## B.2 When it is recorded (two live legs)

### Leg 1 — In-band contract gate (emit-time, no post-pass)

`WrapperSymbolContractGate`:

| API | Role |
|-----|------|
| `FindUnregisteredWrapperSymbol(MethodEnvironment)` | Wrapper-targeting entry point (`SBW_*` / `SBSW_*`) **not** in `ModuleEmissionContext` registry |
| `HandleSkip(...)` | `// Unsupported:` + `ReportCollector.RecordMemberSkipped(..., MissingWrapperSymbol, details)` + log |

Mechanisms per site:

| Pattern | When used | Behavior |
|---------|-----------|----------|
| **Predict-then-skip** | Constructors (symbol registered before C# body) | Skip before writing orphan body |
| **Transactional rollback** | Methods/bridges (symbol registered mid-`EmitMethod`) | Checkpoint C# writer → emit → on `WrapperSymbolContractException` roll back → `HandleSkip` |

Details string shape: `wrapper symbol '{name}' not registered by wrapper-emit`.

**Retired:** post-pass "contract co-gating" section on the artifact manifest (`BindingReportProjection.cs` comment: proxy + contract co-gates no longer projected from manifest).

### Leg 2 — Strip → C# reconciler (sole surviving **post-hoc** co-gate)

Pipeline:

```
Swift wrapper source
  → SwiftWrapperPostProcessor.Process  (strip blocks; collect StrippedSymbols + StripSubCause)
  → swiftc (may fail/retry; residual broken shapes)
  → StrippedSymbolCSharpReconciler.ProcessDirectory(strippedSymbols)
       removes LibraryImport/DllImport EntryPoints targeting stripped symbols
       + 3-level transitive callers (P/Invoke → caller → property forwarder)
  → BindingArtifactManifest.Wrapper.CSharpCoGatedMembers
  → BindingReportProjection.ApplyCoGated(..., SkipReason.MissingWrapperSymbol, details)
```

Details string shape:  
`P/Invoke removed: wrapper symbol '{mangled}' was stripped from compiled wrapper.`

Wiring: `Program.cs` / `BindingsGeneratorCommand.cs` after strip set is known.

**Documented liability** (`StrippedSymbolCSharpReconciler` header): delete with the Swift strip leg; must not masquerade as primary architecture — emission admission should prevent the strip.

### Related but **not** MissingWrapperSymbol

| Mechanism | Outcome |
|-----------|---------|
| `WrapperSymbolIntegrityGate` (SWIFTBIND108) | **Hard-fail generation** if final C# still has dangling EntryPoints ⊆ defs — independent integrity hard-fail |
| `ConstrainedExtensionWrapper` | Planning-time KnownLimitation — **replaces** former mislabeled MissingWrapperSymbol on constrained-extension shapes |
| `GenericEnumCaseConstructor` | Planning-time KnownLimitation — **replaces** former mislabel on open-generic enum case ctors |
| `ParentModuleInternalNoFallback` | Emission drop for async/closure/operator on internal parent — **replaces** emit-then-strip |
| SDK all-wrapper-stripped (SWIFTBIND050/051) | Package-level wrapper absence, not per-member MissingWrapperSymbol |

## B.3 Relation to strip

| Layer | Signal | Healthy product |
|-------|--------|-----------------|
| BindingTests `wrapper_stripped_count` | Block count stripped by **generator's** `SwiftWrapperPostProcessor` | **0** (tripwire in `BindingTests/baselines.json`) |
| Validate `post_processor_sub_causes.InternalType` | 83 | Residual strip on real libs (internal-type reach) — related integrity heat |
| Validate `MissingWrapperSymbol` | 64 | Co-gated / contract residuals after admission+strip |
| Track G1 goal (G1-005) | Drive MissingWrapperSymbol **and** InternalType strip → 0 on corpus | Growth = Review-tier regression |

**Admission vs residual (G1 / emit-then-break inventory):** each strip/co-gate hit means **emission admission missed**. Co-gate is correct recovery; residual count is the honesty metric.

Historical reclass (no longer should land as MissingWrapperSymbol):

- Constrained extension wrapper collisions / narrower-than-parent constraints → `ConstrainedExtensionWrapper`  
- Generic enum payload case constructors → `GenericEnumCaseConstructor`  
- Nested `Result<Self, Error>` / optional-Error closures (PaymentSheet-class) → fixed MCB emission (BindingTests)

## B.4 Example surfaces (prior audits + residual class)

| Library / shape | Notes | Status class |
|-----------------|-------|--------------|
| **ObjectMapper** `Mapper.map*` / `mapArray` / `mapDictionary` | Method-generic / constrained `where N: ImmutableMappable` cluster; binding-surface-audit called 12× Review | **Defect / capacity** — planning-time ConstrainedExtensionWrapper covers some arms; residual generic wrappers still integrity-sensitive |
| **Kingfisher** `Delegate.call*` | Old dangling story; post-run verification says real `SBW_Kingfisher_Delegate_call*` wrappers **now emit** | **Mostly fixed**; Kingfisher audit still listed 2 residual MissingWrapperSymbol |
| **Generic enum Event cases** (Rx-class) | Case constructors claimed wrapper symbols that strip | Prefer `GenericEnumCaseConstructor` today |
| **Nested Result/Error closures** | Pre-fix MissingWrapperSymbol | **Fixed** + BindingTests regression |

Exact per-library split for the current **64** is **not** in `validation-baseline.json` (aggregate only). Re-run validate with per-lib `binding-report.json` to re-bucket if ownership work starts.

## B.5 BindingTests sample

| Artifact | Path | What it proves |
|----------|------|----------------|
| Fixture | `BindingTests/Sources/SwiftBindingsTestLib/ErrorHandling/NestedResultClosureFixture.swift` | Nested class + `Result<Self, Error>` / `(Error?)` closures |
| Runtime | `BindingTests/RuntimeTestsApp/ErrorHandling/NestedResultClosureTests.cs` | Symbols reachable (no `EntryPointNotFoundException`); success/failure round-trip |
| Unit | `WrapperSymbolContractTests.cs` | `HandleSkip` records MissingWrapperSymbol + marker; overload fan-out |
| Unit | `MethodWrapperEmitterTests` constrained-extension | Planning-time skip **≠** late MissingWrapperSymbol |
| Unit | `BindingArtifactManifestTests` | Co-gated members project to Review triage |
| Baseline | `BindingTests/baselines.json` → `wrapper_stripped_count: 0` | Strip tripwire |

BindingTests skip catalog shows **MissingWrapperSymbol is not a bulk BindingTests reason** (strip driven to 0; NestedResultClosure is green-path coverage for a **former** MissingWrapperSymbol class).

## B.6 Co-gate path (end-to-end diagram)

```
                    ┌──────────────────────────────┐
                    │  MemberValidationPipeline /  │
                    │  MethodWrapperEmitter plan   │
                    │  (prefer honest SkipReason)  │
                    └──────────────┬───────────────┘
                                   │ claim SBW_/SBSW_
                                   ▼
                    ┌──────────────────────────────┐
                    │ ModuleEmissionContext        │
                    │ IsWrapperSymbolRegistered?   │
                    └──────────────┬───────────────┘
               no (emit-time)      │ yes
                  │                ▼
                  │     Swift wrapper source emitted
                  │                │
                  │                ▼
                  │     SwiftWrapperPostProcessor.Process
                  │         strips InternalType / NSInvocation / …
                  │                │
                  │                ▼
                  │     swiftc wrapper compile
                  │                │
     ┌────────────┴──────┐        │ stripped set non-empty
     │ Contract gate     │        ▼
     │ HandleSkip        │  StrippedSymbolCSharpReconciler
     │ MissingWrapper…   │  → CSharpCoGatedMembers
     └───────────────────┘        │
                                  ▼
                         BindingReportProjection
                         ApplyCoGated → MissingWrapperSymbol
                                  │
                                  ▼
                         SkipTriage Review bucket
```

Parallel hard-fail: `WrapperSymbolIntegrityGate` if any dangling EntryPoint survives co-gate (SWIFTBIND108 → generator exit non-zero).

## B.7 Fix vs document vs capacity

| Action | Scope | Notes |
|--------|-------|-------|
| **Fix (primary)** | Drive residual → **0** on validation corpus | Each of 64 is an admission miss or residual strip. Track G1-005. Prefer planning-time KnownLimitation reasons over Review. |
| **Fix — extend planning-time gates** | ConstrainedExtensionWrapper / method-generic / closure cdecl-compat | Stop claiming SBW_ for shapes that cannot emit; already done for several arms. |
| **Fix — reduce InternalType strip (83)** | Shared TypeSkip / parent-internal predicates | Post-processor InternalType heat feeds co-gate; BindingTests proves 0 is achievable for the kitchen-sink lib. |
| **Document** | Consumer-facing Review meaning | "This is not a product shape gap; regenerate/report indicates generator integrity. Prefer newer packages; file bug if core API." |
| **Capacity** | Method-generic closed wrappers (ObjectMapper map*, Kingfisher-class) | Real feature work — closed specialization / better MCB — not a docs-only fix. |
| **Do not** | Relabel residual MissingWrapperSymbol as KnownLimitation without root-cause | Would hide integrity heat. Only re-bucket when a **specific** honest SkipReason exists (pattern of ConstrainedExtensionWrapper / GenericEnumCaseConstructor). |
| **Keep hard-fail** | SWIFTBIND108 integrity gate | Separate from soft MissingWrapperSymbol Review rows. |

**Recommended posture:** **Fix / ratchet**, not capacity-as-excuse. Document the Review semantics for consumers; treat count growth as CI tripwire-class (binding-surface-audit already recommends fail-closed if it grows).

---

# C) Side-by-side decision matrix

| Dimension | NetUnavailableType (780) | MissingWrapperSymbol (64) |
|-----------|--------------------------|---------------------------|
| Disposition | KnownLimitation | Review |
| Root cause class | .NET platform / product type inventory | Generator admission / strip integrity |
| Data source | `apple-frameworks.json` curated list (3 types) | Wrapper registry + strip set |
| Apple supplement | Future home for real types; **not** present today | N/A |
| BindingTests | 1 residual (optional LSR) + scalar green path | Strip 0; NestedResultClosure regression |
| Healthy trend | May grow with Apple APIs; should not become Review | Must shrink toward **0** |
| Default owner action | **Document + capacity** (typed binding / nested LSR) | **Fix admission** (G1-005) |
| Consumer workaround | Swift wrapper → `String` / filtered array | Upgrade generator; alternate API surface; file integrity bug |

---

# D) Evidence index (absolute paths)

| Topic | Path |
|-------|------|
| Skip enum + docs | `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Reporting/BindingReport.cs` |
| Disposition map | `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Reporting/SkipDisposition.cs` |
| Workarounds | `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Reporting/WorkaroundRecommendations.cs` |
| Classification | `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/ValidationRuleSet.cs` |
| Member gates | `MemberEmissionValidator.cs`, `MemberGateEvaluator.cs` |
| Registry load | `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/TypeDatabase/AppleFrameworkRegistry.cs` |
| Curated list | `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Data/apple-frameworks.json` |
| LSR projection | `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Marshaler/Projection/TypeProjectionFactory.cs` |
| Scalar helpers | `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Marshaler/MarshallingHelpers.cs` |
| Contract gate | `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/WrapperSymbolContractGate.cs` |
| Strip post-processor | `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Configuration/SwiftWrapperPostProcessor.cs` |
| C# co-gate | `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Configuration/StrippedSymbolCSharpReconciler.cs` |
| Report projection | `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Reporting/BindingReportProjection.cs` |
| Validate counts | `/Users/wojo/Dev/swift-bindings/build/baselines/validation-baseline.json` |
| BindingTests LSR | `BindingTests/Sources/.../Foundation/LocalizedStringResource.swift`, `RuntimeTestsApp/FoundationInterop/LocalizedStringResourceTests.cs` |
| BindingTests MissingWrapper regression | `.../ErrorHandling/NestedResultClosureFixture.swift`, `NestedResultClosureTests.cs` |
| Strip baseline | `/Users/wojo/Dev/swift-bindings/BindingTests/baselines.json` |
| G1 track | `/Users/wojo/Dev/swift-bindings/src/docs/deep-audit-2026-07/tracks/Track-G1_Graceful-Degradation.md` |
| Apple Foundation sources | `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings.Apple/Sources/Foundation/` |

---

# E) Open questions (for later owner / validate re-run)

1. **Per-library breakdown of 780 / 64** — not in the aggregate baseline; re-run `nuke validate` and aggregate `binding-report.json` Details / library folders if prioritization needs "who owns which residual."  
2. **How many of 780 are LSR vs Predicate vs UIOpenURLContext** — Details strings name the offending type; histogram not stored in baseline.  
3. **How many of 64 are contract-leg vs strip-leg** — Details prefix differs (`not registered by wrapper-emit` vs `was stripped from compiled wrapper`); not pre-aggregated.  
4. **Whether BindingAudit AppIntents "764 SwiftUIConstraint = LSR" is fully reclassified** under current HEAD — code path is NetUnavailable; re-audit AppIntents once before citing old numbers.  
5. **Product decision**: promote LSR/Predicate into `SwiftBindings.Apple` vs permanent string/wrapper story.

---

**Bottom line**

- **NetUnavailableType (780)** = honest **KnownLimitation** for a **tiny curated set** of auto-bridged OS types missing from .NET; dominated by LSR label surface; scalar LSR already projects to `string`; **document + capacity**, not integrity panic. Unrelated to Apple-supplement ownership until types are hand-bound.  
- **MissingWrapperSymbol (64)** = **Review** integrity residual on the wrapper plan ↔ emit ↔ strip co-gate path; BindingTests strip is already 0; several former mislabels rehomed to KnownLimitation reasons; **drive to zero** (G1-005), keep SWIFTBIND108 hard-fail for unreconciled dangling EntryPoints.
