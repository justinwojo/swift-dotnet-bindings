# Track G1 — Graceful Degradation / Partial-Success Bindings

| Field | Value |
|-------|--------|
| **Wave** | 7 (owner priority; threaded from W0–W3) |
| **Track** | G1 |
| **Date** | 2026-07-15 |
| **Mode** | Read-only (production code not modified) |
| **Day-1 new-library risk** | **3 / 5** (member/type skip + report story is strong; package-level hard fails and compile-but-dead still block “drop xcframework → try it”) |
| **Confidence** | **high** on admission inventory and integrity/usability split; **medium** on residual emit-then-break count without a full corpus probe |
| **Lenses** | **L3 primary**; L1 (integrity must stay hard); L2 (product-scenario test gap) |

## Product question

If a user drops a new xcframework with a few unsupported shapes, do they get:

1. A **compile-clean** binding missing those members + an honest report, or  
2. CS*/swiftc failures / hard exit that blocks *all* use and forces a GitHub issue?

**Sharpie analogue we accept:** partial compile-clean output + editable/readable skip report — not freehand ApiDefinitions edits.

---

## 1. Method

1. Methodology L3, orchestration Track G1, codebase-map §5 seed, W1–W3 L3 notes.  
2. Deep-read admission oracles: `MemberValidationPipeline`, `WrapperValidation`, `TypeSkipPrePass`, `SilentTombstoneRegistrar`, `ProtocolProxyEmissionPolicy`, `SwiftWrapperPostProcessor`, `StrippedSymbolCSharpReconciler`, `WrapperSymbolContractGate`, `WrapperSymbolIntegrityGate`.  
3. Failure exits: `Program.cs` / `BindingsGeneratorCommand`, `WrapperBuildOutcome`, SDK `SWIFTBIND*` (sample + wrapper/skip surface).  
4. Reporting: `SkipReason`, `SkipDisposition`/`SkipTriage`, `BindingReportProjection`, `SuppressedProxyReporting`, `WorkaroundRecommendations`.  
5. Prior art tag: BA-SUM / BSA EveryProtocol compile-but-dead; roadmap CSM residual undercount as L3 under-emit not hard fail.

---

## 2. Files reviewed-deep

| Path | Why |
|------|-----|
| `Emitter/StringEmitter/MemberValidationPipeline.cs` | Primary member emit-vs-skip orchestration |
| `Emitter/StringEmitter/MemberEmissionValidator.cs` | Shared property/method shape gates |
| `Emitter/StringEmitter/MemberGateEvaluator.cs` | Protocol-side / conformance agreement gates |
| `Emitter/StringEmitter/MethodValidationGates.cs` | PAT/Self constraint gate |
| `Emitter/StringEmitter/ValidationRuleSet.cs` | SSOT unsupported-module / NetUnavailable / AbsentFrameworkType |
| `Emitter/StringEmitter/TypeSkipPrePass.cs` | Pre-emit type skip → prevents CS0234 member leaks |
| `Emitter/StringEmitter/WrapperValidation.cs` | Wrapper eligibility + CallConv + SB0001 classifiers |
| `Emitter/StringEmitter/WrapperSymbolContractGate.cs` | In-band plan↔emit symbol contract |
| `Emitter/StringEmitter/WrapperSymbolIntegrityGate.cs` | Post-emit text-vs-text SWIFTBIND108 |
| `Emitter/StringEmitter/SilentTombstoneRegistrar.cs` | Opaque tombstone pre-pass + SB0002 |
| `Emitter/StringEmitter/ProtocolProxyEmissionPolicy.cs` | Proxy emit / suppress decision |
| `Configuration/SwiftWrapperPostProcessor.cs` | Wrapper strip safety net |
| `Configuration/StrippedSymbolCSharpReconciler.cs` | Surviving co-gater leg |
| `Configuration/WrapperBuildOutcome.cs` | Fatal vs SWIFTBIND050/056 |
| `Marshaler/IHandler.cs` | Type-level skips + pipeline hook |
| `Reporting/*` | Skip taxonomy, triage, projection, workarounds |
| `Program.cs` / `BindingsGeneratorCommand.cs` | Pipeline exits, mixed ObjC fail-closed, wrapper outcomes |
| `Sdk/Sdk.props` + `Sdk.targets` | SWIFTBIND* hard/soft surface, wrapper required default |
| Prior tracks A5a L3, A6 L3, M0-C §5 | Cross-wave seeds |

---

## 3. Admission-point inventory

Every major **emit vs skip / degrade** decision. “Layer” is where the consumer first feels the outcome.

| # | Site | Decision | Outcome on reject | Report / diagnostic | Layer |
|---|------|----------|-------------------|---------------------|-------|
| A1 | `MemberValidationPipeline.ValidateMethodEmission` | Gates 1–6: SPI/internal, Pattern2, parent-internal no-fallback, packs, closures/SwiftUI, generic callbacks, CSM route, PAT constraints, constrained-extension wrapper plan | **Skip member** (or `RoutedElsewhere` for CSM/closed paths) | `SkipReason.*` via handler | Emission |
| A2 | `MemberValidationPipeline.ValidatePropertyEmission` | Property dual of A1 (async property, constrained extension, etc.) | **Skip property** | `SkipReason.*` | Emission |
| A3 | `MemberValidationPipeline.ValidateMethodWrapperEligibility` | Post-marshal: should `@_cdecl` wrapper emit? | **No wrapper** → direct CallConvSwift / no-wrapper path (or later skip if unusable) | Wrapper decision, not always skip | Emission |
| A4 | `MemberEmissionValidator.ShouldSkipMethodEmission` / `CanEmitProperty` | Codable, unsupported closures, SwiftUI/Combine refs, async-tuple C6, AnyType | **Skip** | B19/B20-class reasons | Emission |
| A5 | `MethodValidationGates.HasUnsupportedProtocolConstraints` | PAT/Self-requirement constraints | **Skip** `GenericProtocolConstraint` | Skip | Emission |
| A6 | `ValidationRuleSet.ClassifyUnsupportedReference` | NetUnavailable / AbsentBridgedValueType / SwiftUI-Combine | **Skip** with distinct reasons | `NetUnavailableType`, `AbsentFrameworkType`, `SwiftUIConstraint` | Emission |
| A7 | `TypeSkipPrePass.Run` | Predict type skips (unsupported constraint, indeterminate PWT, indeterminate struct layout) + ancestor propagation | **Type skip recorded early** so members referencing it prune | Type `SkipReason` | Pre-pass |
| A8 | `SilentTombstoneRegistrar.Precompute` | Would handler emit `[OpaqueSwiftType]`? | Register tombstone → SB0002 at call sites; type still “emits” as opaque | Emission report | Pre-pass |
| A9 | `IHandler.HandleBaseDecl` type branches | Underscore SPI, Apple supplement ownership, SwiftUI View, missing handler, namespace facade | **Skip type** or alternate emit | Type skips + `// Unsupported:` | Emission |
| A10 | `FrozenStructHandler` / generic type handlers | Indeterminate buffer / sub-word optional layout | **Skip type** (mirrored in A7) | `IndeterminateStructLayout` | Emission |
| A11 | `OperatorHandler.EmitOperator` | Unsupported op symbol, generics, parent-internal | **Skip operator** | `UnsupportedType` / `ParentModuleInternalNoFallback` | Emission |
| A12 | `ProtocolProxyEmissionPolicy.Decide` | Emit proxy / suppress by conformance / skip unsupported module | **Proxy suppress** → produce-throw / consume-degrade / receiver-failfast on *members* | `EveryProtocolConformanceSkipped` + per-member `SuppressedProxyMemberDegraded` | Emission |
| A13 | `ProtocolProxyEmitter` (impl bodies) | Unfillable / static / inherited / refined return | **Throwing stub** (SB0003 Obsolete) still on type | Compile-but-dead surface | Emission |
| A14 | `WrapperValidation.CanEmitMember` + per-handler `ShouldEmitWrapper` | xcframework mode, internal, SPI, async, actor, inherited generic | **Cannot wrap** | May keep member via direct CC or drop | Wrapper plan |
| A15 | `WrapperSymbolContractGate` | P/Invoke targets unregistered SBW_/SBSW_ | **Rollback + skip** `MissingWrapperSymbol` | Review-tier | In-band |
| A16 | `SwiftWrapperPostProcessor.Process` | Internal type reach, NSInvocation, residual broken shapes | **Strip Swift block** + symbol set | StripSubCause buckets | Post-process |
| A17 | `StrippedSymbolCSharpReconciler.ProcessDirectory` | C# P/Invoke targets stripped symbol | **Suppress C# member** (co-gate) | Projects as `MissingWrapperSymbol` | Post-compile |
| A18 | `WrapperSymbolIntegrityGate.HasViolations` | Final C# EntryPoint refs ⊆ Swift defs | **Hard fail generation** SWIFTBIND108 | Integrity | Post-emit |
| A19 | CSM / constrained-extension / Route-C / closed-closure routers | Open form would shadow or crash | **RoutedElsewhere** (not skip) or skip unemittable subset | Honest open-form suppress | Emission |
| A20 | ObjC `WouldEmitMethod` / `WouldEmitProperty` + eligibility | Unresolvable type, variadic, unavailable, missing symbol | **Drop ObjC surface** | `ObjC*` SkipReasons via projection | ObjC path |
| A21 | `ObjCPipeline` mixed parse/emit | Systemic clang/AST failure | **Abort whole package** (no Swift-only) | Exit non-zero | Pipeline |
| A22 | `InputResolutionReport` + `--strict-inputs` | Silent slice/ABI/dep degradation | **Warn**, or **fail** under strict | SWIFTBIND027 | Input |
| A23 | `WrapperBuildOutcome` / `HandleWrapperCompilationOutcome` | swiftc fail / all-stripped | CLI fatal; **SDK mode → SWIFTBIND050 exit 0** then SDK validates | SWIFTBIND050/051/056 | Wrapper build |
| A24 | SDK `_ValidateSwiftWrapperCompilation` | `HasWrapper` vs `SwiftWrapperRequired` | **Error** if required (default **true**) | SWIFTBIND051 | MSBuild |
| A25 | SDK skip warnings | Non-zero skipped types/members | **Warning only** | SWIFTBIND060/061 | MSBuild |

**Threading note:** Proxy co-gater and wrapper-symbol contract co-gater are **retired as post-passes** (`BindingReportProjection.cs:55–58`); only wrapper-**compile strip** reconciliation remains post-hoc.

---

## 4. Failure-mode taxonomy

| Mode | What happens | Continues module? | Consumer sees | Integrity vs usability |
|------|--------------|-------------------|---------------|------------------------|
| **Member skip** | No C# member (or `// Unsupported:` comment only) | Yes | `binding-report.json` row + SWIFTBIND061 count | Usability degrade ✅ |
| **Type skip** | No type; nested `AncestorSkipped` | Yes | Type skip + member refs pruned via TypeSkipPrePass | Usability degrade ✅ |
| **RoutedElsewhere** | Open form suppressed; specialized overloads emit | Yes | Often **no** skip row (intentional) | Correct specialization |
| **Wrapper strip** | Swift function removed post-emit | Yes (if some wrapper remains) | Co-gated C# → `MissingWrapperSymbol` | Degrade if rare; integrity issue if systemic |
| **All-wrapper stripped / compile fail** | No wrapper xcframework | SDK: metadata HasWrapper=False | SWIFTBIND050 warn → **051 error** if required | **Package hard fail** (default) |
| **Contract gate rollback** | Member rolled back mid-emit | Yes | `MissingWrapperSymbol` Review | Defense-in-depth |
| **Integrity SWIFTBIND108** | Dangling EntryPoint | **No** — generator returns false → exit 1 | Hard error | Integrity must hard-fail ✅ |
| **C# compile fail** | Leaked dangling type ref / dual-path bug | Build dies | CS* | **Emit-then-break** defect |
| **swiftc fail (partial)** | Strip+retry may recover; else give-up | Managed C# may exist | 050/051 | Usability vs integrity tension |
| **Pipeline exit (CLI)** | Bad inputs, resolve fail, gen exception, mixed ObjC abort | **No** | Exit 1 | Mixed: integrity-of-package-shape |
| **SDK MSBuild Error** | Misconfig, missing framework, hook disconnect, pack lies, wrapper required | **No** | SWIFTBIND001–080 range | Mostly integrity/config ✅ |
| **Compile-but-dead** | Member/API **emits**, throws / no-op / fail-fast | Yes, clean compile | Runtime NotSupported / silent | Usability ⚠️ already-known |
| **Object / tombstone degrade** | Compiles with `object` / opaque | Yes | SWIFTBIND026 / SB0002 | Fidelity loss, not hard fail |

---

## 5. Integrity vs usability split

### Must stay hard-fail (do **not** “degrade”)

| Check | Why |
|-------|-----|
| `WrapperSymbolIntegrityGate` (SWIFTBIND108) | Ships lie → `EntryPointNotFoundException` |
| In-band `WrapperSymbolContractGate` skip (not soft-emit) | Same class; skip is correct recovery |
| TN2435 / pack slice / false wrapper metadata | Packaging honesty (`SWIFTBIND031+`, appstore hygiene) |
| RuntimeContract epoch fraud | Load-time safety net |
| Hook disconnection (`SWIFTBIND062–065`) | Silent no-generation |
| Explicit arch contract (`SWIFTBIND056`) | Requested fat wrapper not delivered |
| Mixed framework **metadata** claiming Mixed while ObjC dropped silently | Would bypass SWIFTBIND039 class of checks |
| Generator non-zero under BindingTests `--compile-only` / `--strict` | CI honesty |
| Corrupt / missing primary inputs (no ABI/dylib/module) | Nothing useful to emit |

### Should stay skip-and-continue (usability)

| Class | Notes |
|-------|--------|
| Unsupported signatures, closures, existentials | `SkipReason` + report |
| PAT-heavy / multi-PAT / indeterminate layout | Fail closed *on member/type*, not package |
| SwiftUI/Combine constraints | Structural product limit |
| Module-internal / Pattern2 / parent-internal no-fallback | Emission-time drop > strip |
| CSM open-form suppress when closed overloads exist | `RoutedElsewhere` |
| ObjC *member* drops with attributed reason | Folded into SkipTriage |
| Unresolved auto-deps | SWIFTBIND080 **warning** (not total death) |
| SwiftUI bridge slice fail | SWIFTBIND052 non-fatal to main binding |

### Contested / product policy (G1 opportunities)

| Class | Current | Tension |
|-------|---------|---------|
| **Wrapper compile fail** | SDK default `SwiftWrapperRequired=true` → **Error SWIFTBIND051** | Kills whole `dotnet build` even when managed surface is useful |
| **Mixed ObjC systemic parse fail** | Always abort before Swift (`ShouldAbortForFailedMixedObjC`) | Prevents silent ObjC drop; also prevents Swift-only day-1 try |
| **Produce-throw / SB0003 stubs** | Compile-clean, runtime throw | Honest-ish but not “omit” |
| **`--strict-inputs`** | Opt-in hard | Correct for CI; normal mode still graceful-to-a-fault on inputs |

---

## 6. Emit-then-break sites (candidates)

Paths that still risk **broken compile** or **claim then renege**, ordered by residual danger.

| ID | Site | Risk | Status |
|----|------|------|--------|
| E1 | Member refs skipped type **before** TypeSkipPrePass mirror of new handler skip | CS0234 | Mitigated by TypeSkipPrePass contract; **hazard** if new type-skip not mirrored (`TypeSkipPrePass.cs:16–24`) |
| E2 | Wrapper-emit claims SBW_ then bails | Dangling P/Invoke | Mitigated: planning skips (`ConstrainedExtensionWrapper`, `GenericEnumCaseConstructor`) + contract gate + integrity gate |
| E3 | Post-processor strip without C# reconcile | DllNotFound at runtime | Mitigated: reconciler + report projection |
| E4 | Suppressed proxy **produce** still emits public API that throws | Compile OK, use broken | **already-known** compile-but-dead (A5a-005) |
| E5 | `object` degradation without `[UnsupportedSwiftType]` | Compiles; loses fidelity | Observed + SWIFTBIND026; not CS fail |
| E6 | Silent tombstone call sites | SB0002 marker; cookie invariant asserted | Integrity throw if registrar diverges (`AssertSilentTombstoneInvariant`) |
| E7 | CSM residual filter undercount | Under-emit (skip-ish), not swiftc | A6 L3 — prefer engine reject |
| E8 | Null reverse vtable slot + Swift force-unwrap | Runtime crash if hit | W2 L3 already-known |
| E9 | Residual `MissingWrapperSymbol` Review rows | Indicates planning/strip gap | Review-tier triage signal |
| E10 | Dual path: emission gate vs handler-time skip drift | CS* or empty type | TypeSkipPrePass dual-oracle hazard (L4/L5) |

**Historical emit-then-strip classes largely closed** at emission (Pattern2, parent-internal no-fallback, proxy body co-gater retired). Post-processor remains **defense-in-depth** (NSInvocation, residual Other).

---

## 7. Continue-on-error policy (per stage)

| Stage | Recoverable? | Policy today | Desired L3 default |
|-------|--------------|--------------|--------------------|
| **Input resolve** (xcframework/slice) | Partial (fallbacks) | Soft degrade + record; hard under `--strict-inputs` | Keep; document fallbacks in report |
| **Parse ABI** | No for total parse death | Exit 1 | Keep hard |
| **TypeSkip / SilentTombstone pre-pass** | N/A (predictive) | Always run before emit | Keep |
| **Type emission** | Yes per-type | Skip + continue | Keep |
| **Member emission** | Yes per-member | Skip + continue | Keep |
| **Proxy / EveryProtocol** | Partial | Suppress proxy; degrade members | Prefer more **omit** over throw (opportunity) |
| **Wrapper Swift emit** | Per-function | Emit then post-strip | Prefer admission; strip net OK |
| **Wrapper compile** | SDK: Fatal→050; then 051 if required | **Default hard package fail** | Consider soft-default or “usable partial package” mode |
| **C# co-gate after strip** | Yes | Suppress members | Keep |
| **Symbol integrity** | No | Exit 1 | Keep hard |
| **ObjC pure** | Member drops yes; parse no | Exit from ObjC result | Keep |
| **ObjC mixed** | Member drops yes; systemic no | **Abort entire gen** | Optional Swift-only + loud Mixed-degraded flag |
| **SDK generate Exec** | No IgnoreExitCode | Generator exit fails build | Keep for integrity |
| **SDK wrapper Exec** | ContinueOnError / WarnAndContinue patterns | Metadata + 051 | Align messaging with partial success |
| **Skip counts** | Always | Warnings 060/061 only | Good L3 |

---

## 8. Reporting surface — Sharpie analogue?

### What exists (strong)

| Artifact | Role |
|----------|------|
| `binding-report.json` | Emitted/skipped counts, `SkippedItems`, workarounds, object degradations, unsupported comment drops |
| `SkipTriage` | Total / ByDisposition / ByReason / `ReviewCount` / `ReviewItems` / `PublicSurfaceLost` |
| `SkipDisposition` | Recovered → ExpectedNonPublic → ExpectedStructural → KnownLimitation → **Review** |
| `WorkaroundRecommendations` | Per-`SkipReason` consumer guidance (Swift wrapper recipes) |
| `binding-emission-report.json` | Silent tombstones, degraded reverse receivers, emission-time detail |
| `binding-artifact-manifest.json` | SSOT; report projected post co-gate |
| Console | Triage headline + “see binding-report.json” |
| MSBuild | SWIFTBIND060/061 skip warnings with path to report; 050/051 wrapper; 025/026 degrade |

### Gaps vs Sharpie “edit surface”

| Gap | Impact |
|-----|--------|
| No consumer **whitelist/blacklist** file to re-run without source edits | Can’t surgically re-include after reading report |
| Produce-throw APIs not listed as “omit candidates” in Review by default (`SuppressedProxyMemberDegraded` = KnownLimitation) | Easy to miss dead surface in triage if only watching ReviewCount=0 |
| Wrapper failure story is **package death** (default), not “here’s partial nupkg + missing wrapper” | Day-1 blocked |
| Report lives under intermediate dir — discoverable via 060/061 but not a first-class wiki “partial success” ritual | Docs/product |
| No BindingTests **product scenario**: “lib with N unsupported shapes → exit 0 + compile + ReviewItems ⊆ expected” | L2 gap (M0-D seed) |

**Verdict:** Reporting is **already a credible Sharpie analogue for diagnosis**. It is **not yet** a complete partial-success *product* because packaging defaults and compile-but-dead still force issue-filing or runtime surprise before the report can be the main escape hatch.

---

## 9. Ranked degrade-opportunity findings

### DA-W7-G1-001: Default `SwiftWrapperRequired=true` package-kills on wrapper fail

- **Severity**: P1 (day-1 experience)  
- **Status**: degrade-opportunity  
- **Confidence**: high  
- **Lenses**: L3  
- **Reachability**: integrity-gate / emission-live (any lib with wrapper swiftc pain)  
- **Claim**: SDK defaults require a successful wrapper xcframework. A single wrapper compile failure after a successful generate produces SWIFTBIND051 **Error**, blocking all use of an otherwise compile-clean managed binding. SDK mode already downgrades generator wrapper Fatal to SWIFTBIND050 exit 0 — then the SDK re-hardens.  
- **Evidence**: `Sdk.props:68–69`; `Sdk.targets:1978–1988`; `Program.cs:2257–2276` (SDK Fatal→050).  
- **Probe**: Build a binding with forced wrapper fail + `SwiftWrapperRequired=false` → package succeeds; default true → fails.  
- **Risk notes**: Soft-default risks shipping wrapper-dependent APIs that `DllNotFound` at runtime — need clear 050/051 UX, maybe `EditorBrowsable`/analyzer on wrapper-required members, or dual “managed-only” package mode. **Do not** soft-fail integrity (108, 056, pack lies).  
- **Prior art**: M0-C consumer pain list; props comment already anticipates “libraries with known internal type issues”.

### DA-W7-G1-002: Mixed ObjC systemic failure aborts Swift-only partial

- **Severity**: P1 (mixed libraries day-1)  
- **Status**: degrade-opportunity (intentional integrity today)  
- **Confidence**: high  
- **Lenses**: L3  
- **Reachability**: emission-live (FBSDK-class mixed frameworks)  
- **Claim**: On mixed detection, ObjC parse failure refuses any Swift binding (`BindingsGeneratorCommand.cs:837–851`, `ShouldAbortForFailedMixedObjC`). Prevents silent ObjC drop and metadata lies — good integrity — but a clang/header issue blocks the entire Swift surface.  
- **Evidence**: `BindingsGeneratorCommand.cs:800–851`, `:1826–1827`.  
- **Probe**: Mixed fixture with broken umbrella → exit non-zero, empty usable Swift.  
- **Risk notes**: Soft path must **clearly** mark package as Swift-only / Mixed-degraded, not claim Mixed; must not bypass SWIFTBIND039. Opt-in flag safer than default soften.  
- **Prior art**: codebase-map G1 seed “ObjC systemic fail”.

### DA-W7-G1-003: Compile-but-dead reverse-dispatch (produce-throw / consume / fail-fast)

- **Severity**: P1  
- **Status**: already-known  
- **Confidence**: high  
- **Lenses**: L3  
- **Reachability**: emission-live (BA RealityFoundation Materials, RoomPlan, …)  
- **Claim**: Suppressed EveryProtocol proxy still leaves public members that throw, silently drop C# conformers, or fail-fast reverse — compile-clean but unusable for reverse-dispatch. Reporting improved (`SuppressedProxyReporting`) but disposition is KnownLimitation, not Review.  
- **Evidence**: `SuppressedProxyReporting.cs:1–78`; `BindingReport.cs:214–234`; A5a-005; BA-SUM.  
- **Suggested direction**: Prefer **omit** produce surface or `EditorBrowsable(Never)` + analyzer SB000x; keep layout-critical receiver stubs only where slot parity demands.  
- **Prior art**: BA-SUM; BSA-05 P0 EveryProtocol — **do not re-chase as novel ABI**.

### DA-W7-G1-004: No product-scenario gate for partial-success

- **Severity**: P2  
- **Status**: degrade-opportunity  
- **Confidence**: high  
- **Lenses**: L2, L3  
- **Reachability**: fixture-reachable  
- **Claim**: Skip disposition unit tests exist; no BindingTests/nuke scenario asserts “unsupported shapes → generator exit 0 + C# compile + wrapper policy + SkipTriage.ReviewCount within budget”. Regression can reintroduce emit-then-CS without a product-level red.  
- **Evidence**: methodology success metrics; M0-D / codebase-map §7 gap.  
- **Suggested fixture**: Tiny Swift lib with intentional PAT method + SwiftUI constraint + internal-parent async; expect skips only + clean compile.  
- **Prior art**: none as automated gate.

### DA-W7-G1-005: TypeSkipPrePass dual-oracle drift → CS0234

- **Severity**: P2 (P1 if drifts)  
- **Status**: candidate / hazard  
- **Confidence**: medium  
- **Lenses**: L3, L5  
- **Reachability**: latent until new type-skip condition  
- **Claim**: TypeSkipPrePass must mirror handler skips exactly; comment documents CS0234 leak class. New FrozenStruct/generic skip without pre-pass mirror reopens emit-then-break.  
- **Evidence**: `TypeSkipPrePass.cs:5–24`, conditions 1–4.  
- **Probe**: Unit tests exist (`TypeSkipPrePassTests`); extend whenever handler gains skip.  
- **Suggested simplification**: Shared predicate functions (byte-identical or call-through), not copy-paste.  
- **Prior art**: same pattern as SilentTombstoneRegistrar mirror.

### DA-W7-G1-006: Residual strip / `MissingWrapperSymbol` as Review signal

- **Severity**: P2  
- **Status**: degrade-opportunity  
- **Confidence**: medium  
- **Lenses**: L3  
- **Reachability**: integrity-gate / emission-live on hard libs  
- **Claim**: Strip reconciler is correct recovery, but each hit means emission admission missed. Healthy product goal: `MissingWrapperSymbol` → 0 on validation corpus; growth is tripwire-class.  
- **Evidence**: `BindingReportProjection.cs:59–67`; `SkipDisposition` Review for `MissingWrapperSymbol`; post-processor safety-net warnings.  
- **Prior art**: wrapper-strip count tripwire in BindingTests baselines.

### DA-W7-G1-007: Object degradations / silent tombstones

- **Severity**: P2  
- **Status**: candidate (observability good; product still rough)  
- **Confidence**: high on existence  
- **Lenses**: L3  
- **Reachability**: emission-live  
- **Claim**: Members can compile with bare `object` (SWIFTBIND026) or opaque tombstones (SB0002). Prefer skip or marked unsupported attribute over silent fidelity loss when member is effectively unusable.  
- **Evidence**: `EmissionReportEmitter.cs:232–263`; `SilentTombstoneRegistrar.cs:1–25`.  
- **Prior art**: Finding 53 diagnostics.

### DA-W7-G1-008: CSM residual undercount as preferred degrade form

- **Severity**: P3  
- **Status**: already-known / degrade-opportunity (positive)  
- **Confidence**: high  
- **Lenses**: L3  
- **Claim**: Engine-side reject beats wrapper swiftc fail. A6 residual SameType sugar / multi-PAT is under-emit, not package death — correct L3 posture; tighten filters only with fixtures.  
- **Evidence**: Wave 3 synthesis L3 takeaway; Track A6.  
- **Prior art**: roadmap medium CSM rows.

---

## 10. What already works well

1. **MemberValidationPipeline** as single emission admission funnel (SPI → Pattern2 → parent-internal → closures → generics → CSM route → PAT).  
2. **Honest SkipReason taxonomy** + exhaustive disposition map (unmapped → Review by default).  
3. **TypeSkipPrePass** + ancestor propagation (CS0234 class closed when mirrors hold).  
4. **Emission-time replacement of generate-then-strip co-gaters** for proxy and contract; strip reconciler only for compile-time strip.  
5. **Planning-time** `ConstrainedExtensionWrapper` / `GenericEnumCaseConstructor` instead of mislabeled MissingWrapperSymbol.  
6. **ParentModuleInternalNoFallback** emission drop (async/closure/operator) instead of emit-then-strip.  
7. **WrapperSymbolIntegrityGate** fail-closed post-emit.  
8. **SDK skip warnings** (060/061) pointing at report path.  
9. **SuppressedProxyReporting** per-member classified degradation (was silent).  
10. **ObjC skips folded** into same SkipTriage gate.  
11. **CSM RoutedElsewhere** avoids shipping wrong open-generic async ABI.  
12. **Bridge / x86_64 fold** best-effort degrade without killing primary (with explicit-arch hard).  
13. **WorkaroundRecommendations** give consumers a “write a Swift wrapper” path analogous to Sharpie hand-edit *direction*.

---

## 11. Day-1 experience matrix (scenario)

| Scenario | Likely outcome today | Risk |
|----------|----------------------|------|
| Pure Swift third-party, unsupported *members* only, wrapper compiles | Compile-clean partial + report + 060/061 | **Low** ✅ |
| Pure Swift, wrapper fails (deps/internal residue) | **dotnet build Error 051** (default) | **High** |
| Mixed ObjC+Swift, ObjC parse OK, some ObjC drops | Continues; ObjC in SkipTriage | **Low–med** |
| Mixed, ObjC parse/systemic fail | **Total abort**, no Swift package | **High** |
| Protocol-heavy reverse-dispatch (Materials-class) | Compiles; reverse dead / throws | **Med** (usability) |
| Generator dangling wrapper symbol | Exit 1 SWIFTBIND108 | Correct integrity |
| Input silent fallback (device→sim) without strict | Exit 0, shrunken surface | Med (strict for CI) |

**Overall day-1 risk: 3/5** — not “always broken,” not “drop and go.” Unsupported **shapes** usually skip cleanly; **package-level** gates and **compile-but-dead** still force friction.

---

## 12. Top 5 opportunities (implementation later, owner-gated)

1. **Partial package mode** when wrapper fails: usable managed binding + loud SWIFTBIND050/051 story; keep integrity hard elsewhere (G1-001).  
2. **Honest omit** for suppressed-proxy produce surface (or browsable hide) instead of public throw (G1-003).  
3. **Opt-in Swift-only continue** on mixed ObjC systemic fail with Mixed-degraded metadata (G1-002).  
4. **Product scenario test + Review budget** for intentional unsupported shapes (G1-004).  
5. **Drive MissingWrapperSymbol / InternalType strip → 0** on validation corpus; shared predicates for TypeSkip mirrors (G1-005/006).

---

## 13. Cross-wave L3 thread (do not re-audit as novel)

| Source | Note |
|--------|------|
| W1 A2 | Layout skip fail-closed = correct type degrade |
| W2 A5 | Null reverse → crash if hit; suppress honestly preferred |
| W3 A6 | Engine reject > swiftc fail for CSM |
| M0-C | SDK mostly integrity-hard; consumer wrapper pain listed |

---

## 14. Ledger status (G1)

Files in §2 → `reviewed-deep` for L3 gracefulness. Full ledger batch update deferred to program ledger pass.

---

## 15. Headline

**Admission-at-emission is mature; packaging defaults and compile-but-dead still decide day-1.**  
Skip taxonomy + report + TypeSkipPrePass + contract/integrity gates deliver a real partial-success *generator*. Default `SwiftWrapperRequired`, mixed-ObjC abort, and reverse-dispatch throwing stubs still convert “a few unsupported shapes” into package death or runtime dead API — so the Sharpie analogue is **half-built**: excellent report, incomplete usable-package story.
