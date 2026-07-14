# Track W10 — Maintainability / Dual-Oracle / Simplification (C1 + C2 + S1)

| Field | Value |
|-------|--------|
| **Wave** | 10 |
| **Tracks** | C1 (mega-file + AI footguns), C2 (invariant / constraints parity), **S1** (simplification rollup) |
| **Date** | 2026-07-16 |
| **Mode** | Read-only consolidation (no production edits) |
| **Risk rating** | **3 / 5** maintainability — ABI cores ~2/5; residual is dual-oracle edit hazard + stale agent docs + mega-file rewrite tax |
| **Confidence** | **high** on consolidated findings from completed tracks; **medium** on Wave 6/9 areas not yet line-complete (ObjC/SwiftUI/runtime remainder) |
| **Lenses** | L4 primary (S1), L5 (C1/C2), L1 spot-check for constraints drift that can re-open fixed bugs |

## Headline

**Agents and humans are more likely to re-break reverse-dispatch or packaging by following stale constraints / dual walkers than by discovering a new CallConv P0.** Wave 2 already closed legacy async-CT and refuted roadmap F8-as-written; `constraints.md` and M0-A still advertise the CT edge as open. Highest maintainability ROI: dual-oracle inventory below + top rows in [`../synthesis/simplification-opportunities.md`](../synthesis/simplification-opportunities.md).

---

## 1. Method

1. Roll up every track’s L4 inventory + dual-oracle tables (W0 maps through T/G1/M2/A8).  
2. Spot-check `.claude/rules/constraints.md` vs Wave 2 corrections (legacy CT, F8).  
3. Build mega-file hazard map from M0-A/B/C + track file lists.  
4. Produce AI footgun list (edit patterns that fail closed late or only on device).  
5. Emit S1 catalog as synthesis doc (linked above) — no new ABI P0s invented.

---

## 2. Mega-file hazard map (C1)

### 2.1 Generator production (highest intertwine)

| LOC (approx) | Path | Why hazardous | Safe L4 shape | Do not… |
|-------------:|------|---------------|---------------|---------|
| ~7430 | `Emitter/StringEmitter/EveryProtocolEmitter.cs` | Reverse layout Swift + stubs + real-async gates + hand-enumerators | Extract Enumerate* → layout; width builder | Gate layout on projected key / skip sets |
| ~4225 | `Emitter/StringEmitter/SwiftUIBridgeEmitter.cs` (+ partials) | Async chain inference + typed closures + multi-path Create | Partial extract only after bridge unit plan asserts | Touch Param matrices without SwiftUI BindingTests |
| ~4220 | `Parser/SwiftABIParser.cs` | Visibility dual oracle + composition + bulk node kinds | `VisibilityClassifier` extract | “Simplify” PublicMemberNames without nonisolated matrix |
| ~3395 | `Demangler/Swift5Demangler.cs` | Async `Ya` / unknown `Y*` residual | Leave core; pin new markers only | Rewrite demangler for style |
| ~2990 | `Configuration/SwiftWrapperCompiler.cs` | Strip + promote + empty-wrapper outcomes | Document outcomes; small helpers | Soften strip reconciler integrity |
| ~2740 | `ProtocolProxyEmitter.Receivers.cs` | Fillability + real-async vs legacy blocking + CT | Shared fillability predicate; dead key delete | Conflate projected key with slot key |
| ~2580 | `WitnessDispatchEmitter.cs` | Separate **forward** SBW index (`EffectiveWitnessSlotKey`) | Keep axis; rename for clarity | Use reverse GetMethodKey for SBW slots |
| ~2520 | `Program.cs` | Arch decision + lipo fold try/catch/finally | Shared arch path already; freeze contract | Rethrow extra-arch fold (false HasWrapper) |
| ~2250 | `WrapperValidation.cs` | Admission Layer 2 + optional ObjC mirrors | Call shared optional predicates | Bypass two-layer closure `.All()` |
| ~2135 | `BindingsGeneratorCommand.cs` | Will-be-produced flags; two-pass packaging | Keep will-produce OR exists | Gate NativeReference on “exists now” only |
| ~2010 | `Handler/MethodHandler.cs` | EmissionContext threading; many post-processors | Keep context.GetEmissionContext() | New WrapperEmitter without emission context |
| ~2000 | `Marshaler/NameProvider.cs` | PublicMethodName shaping / collision | ForMethod context only | Hand-pass subset of shaping args |
| ~1950 | `ModuleEmissionContext.cs` | EmissionSymbol side table; collectors | Keep ref-identity symbol table | Mutate `MethodDecl.MangledName` |
| ~1880 | `BoundGenericsHandler.cs` | CSM constraints / seed-drop adjacency | Eligibility helper extract (S1-10) | Merge closed CSM with open bridge body |
| ~1730–1700 | `PropertyHandler` / `ProtocolHandler` | Pipeline residual; async CT on three sites | CT axis stays on key + emitted sig + receivers | Drop CT from one protocol site |
| ~1600+ | `WrapperEmitter*.cs` (+ Async partial) | Cdecl phases; async spine | Exact-duplicate extract only | Full async merge |
| ~1470+ | `ClosureEmitter*.cs` (aggregate larger) | Two-layer gate; return SSOT; PE dual | Document PE split; optional unify with fixtures | Optional-escaping lifetime “cleanup” |
| ~1340 | `Marshaler/IHandler.cs` | Pre-reserve override + tombstone view | Keep ClassifyOverridePrePassEmission | Rely on main-loop reservation alone |

### 2.2 Runtime

| Area | Hazard | L4 note |
|------|--------|---------|
| `ExistentialContainer0..8` | Copy-paste WT counts | Source-gen (S1-16); needs fixture |
| AsyncClosureState arity bags | 0–4 × void/result | Source-gen (S1-17) |
| Marshal extract sibling APIs | Ownership-correct duals | **Docs matrix**, not merge (S1-40) |
| Mono vs NativeAOT factories | Justified dual | **Do not unify** (R-S8) |
| `SwiftMarshal` / VWT (~large) | Wire destroy/copy ownership | Wave 6 remainder; no casual extract |

### 2.3 Build / SDK / tests

| Path | Hazard | L4 note |
|------|--------|---------|
| `Sdk/Sdk.targets` (~3–4k) | Dual HasWrapper, fingerprints, 050→051 | Modularize only with PackGate ratchet (S1-23) |
| `Build.RuntimeTests.cs` (~3k+) | Multi-platform runners | Per-platform extract low urgency (S1-38) |
| `Build.Validation.cs` (~2k+) | Parallel lib generate | Prefer helper extract over rewrite |
| `Build.BindingTests*.cs` | EnsureGeneratorBuilt stale-dll | Freshness stamp (S1-24) — correctness |
| `SwiftUIBridgeEmitterTests.cs` ~10.7k | String-blob theater | Plan/semantic asserts (S1-25) |
| Large `*EmitterTests` (~7k+) | Rewrite tax on dual-oracle fixes | Theory + DTO asserts |
| `BindingTests/baselines.json` | Multi-key theater; only strip count live | Enforce or delete (S1-26) |

### 2.4 Intertwine zones (edit blast radius)

```text
1. Reverse dispatch: EveryProtocolEmitter ↔ VtableLayout ↔ Proxy.Receivers/StaticInit/Vtables ↔ WitnessDispatch
2. Admission: MemberValidationPipeline ↔ WrapperValidation ↔ TypeSkipPrePass ↔ handlers
3. Projected naming: NameProvider.ForMethod ↔ BuildProjectedMethodKey ↔ IHandler/ProtocolHandler dedup
4. Type projection: TypeProjectionFactory ↔ MarshallingHelpers optional ObjC ↔ all IProjectionVisitor families
5. Wrapper plan vs disk: SymbolContractGate ↔ StrippedSymbolCSharpReconciler ↔ ConsumerTargets will-produce
6. Mixed ObjC: parse records ↔ TypeDB ↔ companion pack ↔ IsMixedFramework
```

---

## 3. Dual-oracle inventory — must stay in sync (C1/C2)

Status legend: **Shared** (one core), **Mirrored** (copy must match), **Intentional dual** (different jobs), **Drift residual** (disagree or edit-hazard).

### 3.1 Reverse dispatch / keys

| Oracle A | Oracle B | Must agree? | Status | Notes |
|----------|----------|-------------|--------|-------|
| `VtableLayoutBuilder.Classify*` | `ProtocolVtableMembers.Includes*` | Yes | **Shared** (delegate) | Held |
| `IncludedSlots` Swift walk | C# SwiftVTable + LocalVTable walks | Yes | **Shared** model | Held; ArtifactParity Gate 3 names |
| Layout membership | `_skippedMethodKeys` / projected keys | **No** — fillability only | Intentional | F8-as-written **refuted**; null fill = product residual |
| `MethodEmitsVtableField` | `IncludesMethod` | Yes | **Drift residual** | Nested @objc existential (A5a-001) |
| `EmitMethodVtableField` arity | `GetWidth` | Yes | **Drift residual** | Debug/empty-tuple (A5a-002) |
| `EnumerateProtocolMethodsForDispatch` | `MethodSlotIndexByKey` | Yes | **Mirrored** (hand) | Match today; L4 fold (S1-04) |
| `EnumerateIndexedSubscripts` | subscript `SlotIndex` | Yes | **Mirrored** (hand) | Same |
| Projected C# method key | Reverse `GetMethodKey` / slot key | **No** | Intentional dual axes | Never collapse; rename APIs (S1-33) |
| Projected key | Protocol `BuildEmittedSignature` | Yes on async-CT | **Mirrored** | AF05 sites 1+2 |
| Projected key / CT | Real-async + **legacy blocking** receivers | Yes on CT | **Shared intent** | Code fixed; constraints stale (below) |
| Same-module fill filters | Cross-module parent cctor | Different by design | Intentional | Empty skip sets cross-module |
| `WitnessDispatch` string-first | Blittable/struct class branches | Yes order | Held | Constraints string FIRST |
| `EffectiveWitnessSlotKey` | Reverse slot key | **No** | Intentional | Forward SBW axis |
| Dead `Helpers.GetMethodKey` | Live `EveryProtocolEmitter.GetMethodKey` | N/A | Dead | Delete (S1-13) |

### 3.2 P/Invoke / cdecl / layout

| Oracle A | Oracle B | Must agree? | Status | Notes |
|----------|----------|-------------|--------|-------|
| `CdeclParamMapper` | `PInvokeEmitter.HandleArguments` | Multi-word names | **Intentional dual** | Share name helpers only (S1-09) |
| `CdeclSignatureContract` phases | GSF hand-rolled phases | Yes ideally | **Drift residual** | S1-08 |
| Enum case factory resultPtr-last | Default ResultPtr-first contract | Second contract | **Intentional / under-documented** | S1-20 |
| Layer1 closure support | Layer2 `IsCdeclCompatibleType` | Emit vs wrapper | Intentional two-layer | `.All()` both |
| ClosureEmitter cdecl compat | PE / Foreign private copies | If same adapters | **Drift residual** | S1-11 |
| Callback `BuildCallbackReturnStatement` | Returned-closure matrices | Related shapes | **Dual residual** | Fail-closed CS* if drift |
| `EmitBoundGenericArguments` | `EmitTypeConversions` `{name}Buffer` | No double buffer | Held hazard | constraints dual-path |
| CGFloat tag domains | Optional spare-bit domains | Same spelling set | **Drift residual** | S1-07 |
| HasFloatFields / HasBoolFields | Optional unwrap | Should unwrap | Residual L1/L4 | A2 |

### 3.3 TypeDB / projection / ObjC

| Oracle A | Oracle B | Must agree? | Status | Notes |
|----------|----------|-------------|--------|-------|
| `IsOptionalObjCBridged` | TypeProjectionFactory optional paths | Yes | **Shared** core | M3 held |
| Path 3 concrete-class optional | `TryProjectObjCElement` Branch 2 | Yes | **Mirrored** dup | Extract S1-06 |
| `IsObjCModuleType` | Prefix bridge candidate | Different purpose | Intentional | Broader vs narrow |
| `GetWhereClause` ISwiftObject seed-drop | `PInvokeHelperEmitter` isResolvable | Yes | **Mirrored** | constraints holds |
| `AppleFrameworkRegistry` | XML / TypeDBExtensions | Registry SSOT | Held | NSUnderlineStyle excludeFromXml |
| Projection Visit arms | All `IProjectionVisitor`s | Exhaustive | Held | Missing arm = compile error |
| IsMixedFramework | FilterAndEmit companion | Yes | Mirrored | CS0234 if skew |
| SwiftUI Path A TypeDB | Path B MemberEmissionValidator | Yes | Mirrored | Two-path suppression |

### 3.4 Packaging / SDK / gates

| Oracle A | Oracle B | Must agree? | Status | Notes |
|----------|----------|-------------|--------|-------|
| Full generate arch decision | `--compile-wrapper-only` arch | Yes | **Shared** path required | constraints |
| HasWrapper metadata | Consumer NativeReference will-produce | Coordinated | **Dual signals intentional** | Exists() guard |
| Fingerprint echo XCFramework mode | Apple-framework mode | Both must list archs | Mirrored | Stale arm64-only |
| Generator Debug dll on disk | Generator source | Freshness | **Hazard** | EnsureGeneratorBuilt missing-only |
| PackGate Runtime layout | Actual Runtime.csproj pack | Yes | Gate honesty | M0-C |
| Appstore hygiene structural | Runtime.targets embed | Yes | TN2435 | |
| Strip baseline | `SwiftWrapperPostProcessor` | Tripwire | Live | |
| `baselines.json` coverage keys | Nuke enforcement | Claimed budgets | **Theater** | Only strip count |

### 3.5 Parser / admission / async

| Oracle A | Oracle B | Must agree? | Status | Notes |
|----------|----------|-------------|--------|-------|
| ABI DeclAttributes visibility | `PublicMemberNames` negative space | Yes ideally | **Dual residual** | A8 VisibilityClassifier |
| SwiftSyntax NonisolatedMembers | BroadPublic allowed-after | Yes | Residual gap | nonisolated undercount |
| TypeSkipPrePass | Handler type skips | Yes | **Mirrored hazard** | S1-01 / G1-005 |
| Override pre-pass keys | Main-loop projected keys | Yes + tombstone view | Held | ClassifyOverridePrePassEmission |
| AsyncHarness error helper FQ | AMGBE bare helper name | Should match | **Drift residual** | S1-12 |
| Main 6-param error ABI | CSM parent 2-param error | Different jobs | Intentional | Do not merge emitters |
| Sync MethodGenericBridge eligibility | Async twin | Eligibility only | Extract S1-10 | Not emission bodies |

### 3.6 Already one-core (do not re-split)

| Domain | Core |
|--------|------|
| Projected C# overload key | `ProtocolSignatureHelper.BuildProjectedMethodKey` + shims |
| Reverse layout membership/index | `VtableLayoutBuilder` / `IncludedSlots` |
| Apple framework heuristics | `AppleFrameworkRegistry` + `apple-frameworks.json` |
| Optional ObjC prefix bridge core | Shared MarshallingHelpers / factory sites (M3) |
| Wrapper multi-arch compile | `TryDecideWrapperArchitectures` → `CompileWrapperForArchitectures` |
| Emission promoted symbol | `MethodEnvironment.EmissionSymbol` side table (AF13) — not `MangledName` |
| Public method name shaping | `PublicMethodNameContext.ForMethod` |

---

## 4. constraints.md drift list (C2 spot-check)

Spot-checked against Wave 2 (A5b/A5c) and track evidence. **Stale** = agent-facing claim wrong relative to production code as of audit.

| # | Claim location | Stale claim | Reality | Severity | Action |
|---|----------------|-------------|---------|----------|--------|
| **D1** | `constraints.md` overload/dedup bullet — “KNOWN INCOMPLETE EDGE” legacy blocking receiver CT + unfixtured | Legacy blocking async receiver still bare `impl.{Name}({args}).GetAwaiter().GetResult()` without `default(CancellationToken)` | **Fixed** — `Receivers.cs` ~1343–1360 `implCallArgs` with CT; fixtures `KeyBuilderAsyncBlockingOverloadProtocol*`; unit pins ~2042–2098 | **P3 docs / P1 AI rework risk** | Rewrite trap: mark site 3 **closed** for CT; residual is **blocking/deadlock** class (void/arity/non-blittable), not CT mis-bind |
| **D2** | `waves/W0-map/M0-A-generator-pipeline.md` ~403 “known incomplete CT edge” | Same as D1 | Fixed | P3 | Update map residual list |
| **D3** | Roadmap / prior-art **F8** as written: Vtables consult only `_closureSkippedMethodKeys` → struct-size divergence | Layout walks still skip-set gated | **Refuted** — Vtables walk `IncludedSlots` only; skip sets are fillability-only | P3 | Rewrite F8 → “fillability-null + solo force-unwrap” product residual |
| **D4** | Stale SSOT comments on `MethodEmitsVtableField` | Comments claim membership SSOT with layout | Dual residual nested-@objc (A5a-001/004) | P3 | Fix comments or collapse function (S1-02) |
| **D5** | `InterfaceImpl.cs` comment: ProtocolHandler skip sets via `GetMethodSignatureKey` | Wrong key name | Uses `ProtocolMethodDisambiguator.EffectiveRawKey` — behavior OK, comment wrong (A5c-009) | P3 | Comment fix |
| **D6** | Roadmap rows: CSM bare Self, MethodGenericBridge free, class-conformer carrier, multi-constraint labels, primary composition | Listed open / latent | **Fixed in code** + tests (A6 re-tag ≥5) | P3 | Roadmap hygiene |
| **D7** | Roadmap / KeyPath comments: frozen `inout` writeback missing | Cdecl frozen blittable writeback runtime-green (`TestIncrementPoint`); KeyPath e2e still gap | Partially stale | P3 | Split “cdecl done / KeyPath e2e open” |
| **D8** | `constraints.md` hand-allocators Enumerate* “byte-identical today” | Still true | True residual L4, not false claim | — | Keep; track S1-04 |
| **D9** | `constraints.md` VtableLayout SSOT + fillability model | Accurate post Finding-8 | **Holds** | — | Do not “update” toward skip-set layout |
| **D10** | `constraints.md` projected-key one-core + CT on protocol sites 1–2 + real-async receiver | Accurate | **Holds** for real-async; only legacy CT paragraph stale (D1) | — | Surgical edit D1 only |
| **D11** | M0-A dual-oracle list still lists incomplete CT | Stale | Fixed | P3 | Same as D2 |
| **D12** | Closure return-marshalling parity trap | Documents callback SSOT only | True but incomplete vs returned-closure dual matrices (A4-002) | P3 optional | Mention returned-closure matrices as separate dual |

### Top 5 doc drifts (owner shortlist)

1. **D1** — constraints.md legacy async CT “KNOWN INCOMPLETE EDGE”  
2. **D3** — Roadmap F8 layout/skip-set claim  
3. **D2/D11** — M0-A “incomplete CT edge”  
4. **D6** — Roadmap CSM “open” rows already fixed  
5. **D4** — MethodEmitsVtableField SSOT comments  

---

## 5. AI footgun list (C1)

Patterns that agents (and humans following agents) repeatedly get wrong. Ordered by damage if mishandled.

| # | Footgun | Wrong move | Correct move | Failure mode |
|---|---------|------------|--------------|--------------|
| F1 | **Projected key vs reverse slot key** | One “GetMethodKey” cleanup | Keep axes; rename if needed | Device SIGSEGV slot shift |
| F2 | **Layout from skip sets / projected membership** | “Fillability should shrink the struct” | IncludedSlots independent; null unfilled | F8-class corruption |
| F3 | **Stale constraints CT incomplete** | Re-implement legacy CT or re-file P1 | Read Receivers + fixtures first | Wasted work / wrong patch |
| F4 | **Stale generator Debug dll** | Edit generator → `nuke binding-tests` without rebuild | `dotnet build` generator or `nuke compile` | “Patch didn’t take” |
| F5 | **NativeReference “exists now”** | Gate consumer targets on disk xcframework at generate | will-produce OR exists | PackageReference `DllNotFound` |
| F6 | **Extra-arch lipo fold rethrow** | Fail whole compile on x86_64 fold | Restore primary; return non-null; warn | HasWrapper=False for all archs |
| F7 | **Mutate `MethodDecl.MangledName`** | Promote symbol on decl | `env.PromoteSymbol` / side table | Cross-method symbol leak |
| F8 | **Drop CT from one protocol site** | “Dedup already has CT” | Key + emitted sig + receiver all | Silent drop or CS0029 |
| F9 | **Override pre-pass without tombstone view** | Key on bare field | `ClassifyOverridePrePassEmission` | CS0111 order-dependent |
| F10 | **String not first in WitnessDispatch** | Treat String as frozen+RefFields indirect | String branch first | Wrong dispatch / crash |
| F11 | **Closure Layer2 `.Any()`** | Emit if any param cdecl-ok | `.All()` | Emit without wrapper |
| F12 | **Optional closure not always escaping** | Lifetime from `IsEscaping` only | Also `IsOptionalClosure()` | GCHandle premature free |
| F13 | **ISwiftObject seed-drop vs method-Self-only** | Drop seed for all non-PAT | Mirror isResolvable (PAT/Self only) | CS0314 |
| F14 | **Merge async emitters** | One mega async file | Exact-duplicate extract only | Ownership dual-path bugs |
| F15 | **Mega unit Assert.Contains as ABI proof** | Green EmitterTests → ship | BindingTests for ABI | False-green wrong CallConv |
| F16 | **baselines.json multi-key confidence** | Assume coverage budgets enforced | Only strip count is live | Silent coverage erosion |
| F17 | **Soft wrapper-required for “partial success”** | Default SwiftWrapperRequired=false globally | Product opt-out; keep integrity hard | TN2435 / missing carrier lies |
| F18 | **Dead Helpers.GetMethodKey reuse** | Wire into new walk | Delete; use EveryProtocol/Witness APIs | Wrong key domain |
| F19 | **PublicMethodName without ForMethod** | Hand-pass parameterCount only | `PublicMethodNameContext.ForMethod` | P1-21 collision rename miss |
| F20 | **ModuleEmissionContext not threaded** | `new WrapperEmitter` without context | `context.GetEmissionContext()` | Dedup / symbol side-table miss |
| F21 | **Double `{name}Buffer`** | Fast path in both EmitBoundGenericArguments and EmitTypeConversions | One path owns buffer | CS redefinition / wrong free |
| F22 | **Visibility “cleanup” without nonisolated** | Trust PublicMemberNames alone | Dual oracle + protocol-req fixtures | Drop public surface |
| F23 | **Mono/AOT “simplify” factories** | One code path | Keep dual where Mono-justified | Runtime-only regressions |
| F24 | **Enum XML kind=enum for value remaps** | kind=enum for CG/UI value types | kind=struct unless real ObjC enum | Ghost enum members |
| F25 | **NSUnderlineStyle into UIKit XML** | Add for completeness | excludeFromXml | Tuple P/Invoke raw mismatch |

---

## 6. S1 deliverable pointer

Full ranked table (40 rows), top 10, and rejected merges:

→ [`../synthesis/simplification-opportunities.md`](../synthesis/simplification-opportunities.md)

---

## 7. Counts & residual open (maintainability only)

| Metric | Value |
|--------|------:|
| Dual-oracle rows inventoried | **~55** across §3 |
| constraints / doc drifts listed | **12** (5 top) |
| AI footguns | **25** |
| Mega-file rows mapped | **~30** |
| New emission-live ABI P0 invented this wave | **0** |
| Highest residual maintainability themes | TypeSkip mirror; vtable width/membership duals; visibility dual oracle; stale CT docs; test theater |

---

## 8. Recommended owner follow-ups (docs-first, then L4)

1. **Docs PR (no product behavior):** D1 constraints CT paragraph; D3 roadmap F8; D2 M0-A; D6 roadmap CSM fixed tags; D4 MethodEmitsVtableField comments.  
2. **L4 PR pack A (byte-identical / low risk):** S1-13 dead key; S1-06 Path-3 extract; S1-14 layout thread; S1-12 AMGBE FQ.  
3. **L4 PR pack B (behavior-preserving + fixture):** S1-01 TypeSkip share; S1-02/03 membership+width; S1-04 Enumerate*.  
4. **Correctness hygiene:** S1-24 EnsureGeneratorBuilt freshness; S1-26 baselines honesty.  
5. **Do not open:** async-emitter merge; layout-from-skips; Mono/AOT unify.

---

## 9. File coverage (this track)

| Artifact | Role |
|----------|------|
| All `tracks/Track-*.md` completed through T/M2/G1/A8 | L4/L5 source |
| `waves/W0-map/M0-{A,B,C,D}.md`, `W1`–`W4` syntheses | Dual-oracle seeds |
| `synthesis/graceful-degradation-map.md`, mid-audit exec summary | Priority context |
| `.claude/rules/constraints.md` | Drift spot-check (read; not edited) |
| `synthesis/simplification-opportunities.md` | S1 output (written this wave) |

Wave 6 (runtime line-complete) and Wave 9 (ObjC/SwiftUI) may add dual-oracle rows; this rollup should be re-diffed then, not treated as closed forever.
