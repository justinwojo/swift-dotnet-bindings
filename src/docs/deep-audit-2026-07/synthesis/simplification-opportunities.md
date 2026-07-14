# Simplification Opportunities (L4) — Wave 10 S1 Rollup

**Date**: 2026-07-16  
**Mode**: Read-only consolidation (no production edits)  
**Sources**: All deep-audit tracks under `tracks/`, Wave 0–4 syntheses under `waves/`, `00-codebase-map.md`, `graceful-degradation-map.md`  
**Companion**: [`../tracks/Track-W10_Maintainability-Simplification.md`](../tracks/Track-W10_Maintainability-Simplification.md) (C1 mega-files, L5 dual oracles, constraints drift, AI footguns)

---

## Headline

**Hardened ABI cores left almost no emission-live P0s; residual audit value is dual-oracle hygiene, stale-doc correction, and capability-preserving consolidations — not another CallConv hunt.** Highest-ROI L4 is **share one predicate / one width builder / one visibility classifier** where two walkers already claim to agree; lowest-ROI is **full async-emitter merge, Mono/AOT factory unification, and projection-only marshaler** (roadmap-deferred or explicitly rejected).

---

## Ranking rules

| Risk class | Meaning |
|------------|---------|
| **byte-identical** | Extract / delete / call-through; output and behavior unchanged if done carefully |
| **behavior-preserving** | Semantics unchanged for admitted shapes; may shrink unsafe dual paths |
| **needs fixture** | Safe only with unit and/or BindingTests pins before/after |

**Capability preserved?** — Yes means the consolidation must not drop supported Swift surface, weaken integrity fail-closed gates, or blur intentional multi-job paths.

---

## Ranked table

IDs are rollup keys (`S1-…`). Source column cites originating DA- / track IDs.

| ID | Theme | Files / symbols | Risk class | Capability preserved? | Do-not-do-if… | Source |
|----|-------|-----------------|------------|----------------------|---------------|--------|
| **S1-01** | **TypeSkipPrePass ↔ handler skip predicates share one core** | `TypeSkipPrePass.cs`; `GenericTypeEmitter` / PWT-shape skips; handler type-level skip mirrors | behavior-preserving | Yes — same skip set, fewer CS0234 leaks | New skip added only on one side “for now”; softens pre-pass without unit pin | G1-005; M0-A dual-oracle; W4-G1 synthesis |
| **S1-02** | **Collapse `MethodEmitsVtableField` → layout `IncludesMethod`** | `EveryProtocolEmitter` fan-out; `VtableLayoutBuilder.ClassifyMethod` / `ProtocolVtableMembers` | behavior-preserving | Yes — nested-@objc existential fan-out shrinks to match layout (safer) | Fan-out still needs a field layout omits (would re-open dual membership) | A5a-001 |
| **S1-03** | **Single reverse-dispatch width / arity builder** | `EmitMethodVtableField` (Swift); `GetWidth` / `CountVtableSlots` (C# model) | behavior-preserving (byte-identical for ordinary methods) | Yes | Debug/empty-tuple protocols untested; rewrite width without ArtifactParity + width unit | A5a-002; W2 synthesis |
| **S1-04** | **Route hand-enumerators through `VtableLayout`** | `EnumerateProtocolMethodsForDispatch`; `EnumerateIndexedSubscripts` | behavior-preserving + parity fixture | Yes | Edit one enumerator’s pre-skip without the other; use projected key for index | A5a-003; constraints residual; M0-A |
| **S1-05** | **VisibilityClassifier SSOT (parser dual oracles)** | `SwiftABIParser` PublicMemberNames / InternalMemberKeys / DeclAttributes; protocol-req exceptions; SwiftSyntax nonisolated facts | behavior-preserving → then fix undercount | Yes after fixtures; more public surface may appear (good) | Merge without StoreKit dual-set + protocol-req + nonisolated matrix | A8-009; A8-001/002 |
| **S1-06** | **Optional concrete-class Path 3 extract** | `TypeProjectionFactory` Optional Path 3 + `TryProjectObjCElement` Branch 2 | **byte-identical** | Yes | RealityFoundation Optional/Array tests diverge; change heuristic while extracting | M3-005 |
| **S1-07** | **CGFloat / Optional spare-bit domain → one registry** | `SwiftValueLayout`; optional tag oracles; CoreGraphics spelling consumers; `AppleFrameworkRegistry.IsCGFloat` | behavior-preserving | Yes | CoreGraphics spelling reaches wrong consumer without pin; partial adopt of registry | A2-003; W1 synthesis |
| **S1-08** | **GSF cdecl phase assembly → `CdeclSignatureContract` loop** | Generic static-dispatch wrapper phases; `CdeclSignatureContract`; property GSF twin | behavior-preserving + fixtures | Yes | Self mutability / throws / meta order differ without BindingTests | A1-002; W1 L4 |
| **S1-09** | **Shared multi-word cdecl name helpers** | `CdeclParamMapper` (Swift); `PInvokeEmitter.HandleArguments` (C#); String/Data/RawBuffer word names | behavior-preserving (naming only) | Yes | Forcing full `MarshalledType` ↔ `CdeclLowering` merge (code remarks reject) | A1-003 |
| **S1-10** | **CSM / MethodGenericBridge eligibility helpers** | Sync + async bridge eligibility predicates (A6 dual helpers) | behavior-preserving | Yes | Emission *bodies* start diverging on purpose; fold open-existential into closed CSM | A6-003; A6 L4 S-A6-1 |
| **S1-11** | **PE / Foreign private `IsCdeclCompatibleType` vs ClosureEmitter** | `ClosureEmitter.IsCdeclCompatibleType`; ProtocolExtension / ForeignTypeExtension private copies | needs fixture | Yes only if adapters match | Blind unify while PE/foreign lack ClosureEmitter adapter arms | A4-001; W4-G1 |
| **S1-12** | **AMGBE error helper FQ name** | `AsyncMethodGenericBridgeEmitter.EmitErrorCallbackBody` → `ErrorRegistryHelperEmitter.GetFullyQualifiedHelperReference` | behavior-preserving | Yes | NamespacePattern remaps untested on AMGBE path | A7 dual-oracle residual |
| **S1-13** | **Delete dead `Helpers.GetMethodKey`** | `ProtocolProxyEmitter.Helpers.cs` | **byte-identical** | Yes (dead code) | Re-home into a live walk without fixture; confuse with reverse `GetMethodKey` | A5b dead key |
| **S1-14** | **Thread one `VtableLayout` per protocol emission** | Repeated `VtableLayoutBuilder.Build` in proxy / StaticInit / EveryProtocol walks | behavior-preserving | Yes | Pass mutable layout; gate membership on skip sets (re-opens F8-class) | A5c-008 |
| **S1-15** | **Shared fillability “should fill method slot?” predicate** | Receivers + StaticInit local/swift assignment loops | byte-identical / behavior-preserving | Yes | Drive *layout* membership from `_skippedMethodKeys` | A5c L4 notes |
| **S1-16** | **ExistentialContainer0–8 source-gen** | Runtime `ExistentialContainerN` structs | needs fixture | Yes if layout sizes frozen | Hand-edit WT counts; change padding without ABI test | A3-S2; M0-B R-S1 |
| **S1-17** | **AsyncClosureState arity bags source-gen** | Runtime async reverse-closure state 0–4 × void/result | behavior-preserving if API frozen | Yes for InternalsVisibleTo-only surface | Public API consumers; change cookie layout | A3-S3; M0-B R-S2 |
| **S1-18** | **`TypeKeyedRegistry<T>` for five dispatchers** | Runtime ConcurrentDictionary Register/TryGet quintet | **byte-identical** careful | Yes | Concurrent register-once semantics diverge | A3-S4; M0-B R-S6 |
| **S1-19** | **Exact-duplicate async extract only** | `BuildMethodOwnGenericParams`; near-identical UCO fault-catch blocks; cancel-key helpers | byte-identical / behavior-preserving | Yes | **Full merge** of WrapperEmitter.Async / AsyncHarness / AMGBE | A7 S01–S05; roadmap reject |
| **S1-20** | **Document enum-case `resultPtr`-last as named second contract** | Enum case factory cdecl vs `CdeclSignatureContract` | docs / or needs fixture if migrate | Yes if documented; migration changes ABI | “Just ResultPtr-first” without dual-side migrate + BindingTests | A1-001 |
| **S1-21** | **Split `GetSwiftTagByteOffset` size vs tag oracles** | `SwiftValueLayout` / tag helpers | behavior-preserving | Yes | Callers use “offset” API for size only (rename hazard) | A2-013 |
| **S1-22** | **PackThrowawayFeed + gate shared helpers** | `Build.Pack*.cs`, PackGate, Mixed*, Hygiene, X64* | behavior-preserving | Yes | Assertion order / VersionScope diverge | M0-C B-S1/B-S2 |
| **S1-23** | **Sdk.targets modularization by concern** | `src/Swift.Bindings.Sdk/Sdk/Sdk.targets` (~3–4k) | needs fixture (PackGate + compile-only + mixed-pack) | Yes only if dual HasWrapper / fingerprints frozen | Reorder MSBuild without dual-signal table | M2-008; M0-C B-S4 |
| **S1-24** | **`EnsureGeneratorBuilt` freshness (stamp/hash)** | `Build.BindingTests.cs` EnsureGeneratorBuilt | correctness > L4 | Yes — prevents silent stale certify | Treat as pure simplification and skip timestamp check | M0-C B-S6; constraints; M2-003 cousin |
| **S1-25** | **Mega unit-test string theater → plan/semantic asserts** | `SwiftUIBridgeEmitterTests.cs` (~10.7k); large ProtocolProxy/EveryProtocol EmitterTests (~7k+) | needs fixture / gradual | Yes if semantics held | Bulk rewrite without dual-oracle pins; treat green mega-file as ABI proof | T1-001; M0-D; codebase-map |
| **S1-26** | **baselines.json dead coverage keys → enforce or delete** | `BindingTests/baselines.json` vs nuke compile-only | behavior-preserving (honesty) | Yes | “Enforce” fake budgets without measuring the right artifact | T4-001 |
| **S1-27** | **Returned-closure matrices ↔ `BuildCallbackReturnStatement`** | Closure return emit matrices vs callback SSOT | needs fixture; high risk | Yes only under one IR | Merge directions blindly (param callback vs returned closure) | A4-002 |
| **S1-28** | **Forward vs reverse conversion shared expression builders** | `AccessorConversionVisitors` vs `ReceiverConversionVisitors` + Receivers helpers | needs fixture | Yes where byte-identical | Merge +1 retain vs borrow paths | A5b L4 |
| **S1-29** | **Projection-only marshaler (post-1.0)** | Factory + handlers + string visitors hybrid | large behavior-preserving program | Goal yes; deferred | Promote as 0.x fire drill | M3-009; W3 synthesis |
| **S1-30** | **Static collectors → PipelineContext (post-1.0)** | `ReportCollector`; `SwiftUIBridgeCollector` | behavior-preserving | Yes | Parallel xUnit without collection fixture | codebase-map seed |
| **S1-31** | **SameType sugar normalize on direct arm** | CSM constraint parse / normalize | behavior-preserving / more admits | May admit more (good) | Sugar maps distinct types together | A6 S-A6-2 |
| **S1-32** | **Composition Target split on `&` in method-where** | `ParseMethodLevelConstraints` | behavior-preserving / more admits | May admit more | Digester never emits composite Target (dead work) | A6 S-A6-3 |
| **S1-33** | **Rename key APIs (`GetReverseSlotKey` / `GetForwardWitnessKey`)** | `GetMethodKey` domains; `EffectiveWitnessSlotKey`; projected key | non-behavior rename | Yes | “Cleanup” that uses projected key for layout lookup | A5c L4 rename note |
| **S1-34** | **Optional dual-oracle docs table + thin wrappers** | `IsOptionalObjCBridged` / Handle vs nullable class paths | docs / behavior-preserving | Yes | Rename without updating WrapperEmitter sites | M3 L4 notes |
| **S1-35** | **Native metadata shim codegen** | Runtime `SBW_*_GetMetadata` trivial casts | low risk | Yes | Non-trivial shims mixed into generator | M0-B R-S7 |
| **S1-36** | **Closure bridge family shared ownership preamble** | MCB / NCB / GCB + `ClosureContextHelperEmitter` | behavior-preserving possible | Yes | Half-wire `ClosureProjection` ownership | A4 L4 inventory |
| **S1-37** | **Obsolete `ComputeEntryPoint(MethodDecl)` pre-AF13** | Entry-point helpers | low / byte-identical if unused | Yes | External tooling still calls overload | A1-C01 |
| **S1-38** | **Build.RuntimeTests per-platform runner extract** | `build/Build.RuntimeTests.cs` | high touch / low urgency | Yes | Change install/run protocol mid-extract | M0-C B-S3 |
| **S1-39** | **Collection COW/SafeHandle shared base** | `SwiftArray` / `Dictionary` / `Set` | medium | Yes if P/Invoke surfaces stay | Unify Mono/AOT factory branches blindly | M0-B R-S3; R-S8 **do not** |
| **S1-40** | **Document marshal extract ownership matrix** | `Moved` / `Borrowed` / `Extracted` / `CallbackArg` / `OwnedClass` | docs-only | Yes | Merging APIs loses ownership distinctions | A3-S1; M0-B R-S5 |

---

## Top 10 (owner shortlist)

| Rank | ID | Why first |
|------|-----|-----------|
| 1 | **S1-01** TypeSkip shared predicates | Highest L3/L5 compound risk: one new skip condition → CS0234 on day-1 libs |
| 2 | **S1-02** MethodEmitsVtableField collapse | Live dual membership vs layout; safer to shrink fan-out |
| 3 | **S1-03** Width/arity single builder | Low reachability today; cheap pin prevents silent reverse-dispatch arity drift |
| 4 | **S1-04** Hand-enumerators → VtableLayout | Already byte-identical; edit hazard called out in constraints |
| 5 | **S1-05** VisibilityClassifier | A8 residual risk 3/5; public-surface undercount is product-visible |
| 6 | **S1-06** Path-3 optional extract | Pure dual-oracle byte-identical extract |
| 7 | **S1-07** CGFloat domain unify | Completes AppleFrameworkRegistry SSOT adoption |
| 8 | **S1-08** GSF phase loop | Highest-volume cdecl dual-oracle after intentional mapper split |
| 9 | **S1-13** Delete dead GetMethodKey | Free win; removes AI footgun |
| 10 | **S1-24** + **S1-25** Stale generator + test theater | Silent wrong certify + rewrite tax on every dual-oracle fix |

---

## Explicitly rejected / deferred (do not re-propose as audit L4 wins)

| Item | Why |
|------|-----|
| Full async-emitter merge (Wrapper.Async + Harness + AMGBE) | Roadmap strategic reject; A7 L4 = exact-duplicate only |
| Drive layout membership from `_skippedMethodKeys` / projected keys | Re-opens Finding-8 / Bug-21 slot corruption |
| Full `MarshalledType` ↔ `CdeclLowering` unification | Intentional dual classifiers (A1-003) |
| Mono / NativeAOT factory unification | Justified by Mono issues (M0-B R-S8) |
| Finalizer vs Dispose / PayloadConstructionSemantics / ClosureHandle escaping split | Ownership-correct duals (A3 / M0-B) |
| Projection-only marshaler / Type IR under TypeResolver | Post-1.0; not 0.x fire drill (M3) |
| TypeSpec five call-site policy merge | Partial centralization studied; policies intentionally differ (codebase-map) |
| Softening SWIFTBIND051 / wrapper-required integrity | G1/M2: packaging honesty stays hard |

---

## Counts

| Bucket | Count |
|--------|------:|
| Ranked rollup rows | **40** |
| byte-identical primary | ~8 |
| behavior-preserving primary | ~20 |
| needs fixture / deferred | ~12 |
| Explicit do-not-merge | **8** themes |

---

## Traceability (wave → theme)

| Wave | L4 residue carried into this catalog |
|------|--------------------------------------|
| W0 | Mega Build/Sdk; runtime Existential N; dual-oracle seed list; collectors |
| W1 | Cdecl phase/name duals; CGFloat domains; tag API split |
| W2 | Vtable width/membership/hand-enums; dead key; layout thread; fillability predicate |
| W3 | Path-3 extract; seed-drop already mirrored; projection-only deferred; CSM eligibility |
| W4 | Closure PE/Foreign cdecl; returned-closure matrices; async exact extract; AMGBE FQ |
| G1/M2/T | TypeSkip share; Sdk.targets split; EnsureGeneratorBuilt; string theater; baselines honesty |
| A8 | VisibilityClassifier |

**Next (Wave 11):** fold promoted rows into `work-items-backlog.md` with severity × reachability × fixture cost; keep integrity fail-closed items out of “simplify” lanes.
