# Codebase Deep-Dive Audit — Plan & Index

**Date:** 2026-06-01
**Mode:** Read-only. No code changes. Output is written reports only.
**Window:** ~12-hour token-surplus window for large parallel-agent investigation.
**Verification policy:** Every suspected defect is adversarially verified by **compiling a small Swift probe fixture** (swiftc / SIL dump / `nuke compile`) before it lands in a report. Unverified ABI claims are noise — this follows the repo's standing rule ("verify the Swift ABI before you blame anything"; see memory `feedback_verify_swift_abi_sil.md`, `feedback_swift_frozen_first.md`).
**Report destination:** `src/docs/audits/` (this folder). Each track produces one `TrackXX_*.md`. A final synthesis produces `STATE-OF-THE-CODEBASE.md`.

> This is an **audit/reporting** effort, not an implementation effort. Findings are recorded with `file:line` evidence and a confidence rating; fixes are explicitly out of scope and are left for follow-on work.

---

## 1. How this plan was produced

Three planners independently answered the same question — *"if you were planning a large read-only audit of this Swift/.NET binding generator, what tracks would you run and how would you prioritize them?"* — with **no** sharing of each other's lists (to keep the assessments independent):

- **Claude** (lead) — initial recon + track list (A/B/C/D), see §2.
- **Codex** (`gpt-5.5`) — 16-track plan. Session `019e852b-5094-7ae2-92ba-2df5ae719c15`. Full verbatim output in **Appendix A**.
- **Grok** — H1–H6 / M7–M11 / P2 plan. sessionId `019e852b-831a-7ea3-9b6e-4e9a1e235214`. Full verbatim output in **Appendix B**.

The three lists were then cross-checked against the actual repo (file paths verified; one Grok path corrected — `ConformanceGraph.cs` is in `TypeDatabase/`, not `Marshaler/`). The result is the prioritized master plan in §4.

---

## 2. Repo recon (grounding facts, gathered read-only)

- **~197k LOC** C# generator (`src/Swift.Bindings/src`), **~18k LOC** runtime (`src/Swift.Runtime/src`), **304** unit-test files, end-to-end `BindingTests/` across ~30 Swift-feature domains (Sim/Mono-JIT + device/NativeAOT).
- **Mega-files (complexity hotspots):** `EveryProtocolEmitter.cs` (5,595), `SwiftInterfaceAccessParser.cs` (5,250), `SwiftUIBridgeEmitter.cs` (3,962), `SwiftABIParser.cs` (3,481), `Swift5Demangler.cs` (3,239), `ConcreteProtocolSpecializationEmitter.cs` (3,196).
- **Churn hotspots (last 200 commits)** — high-churn × high-complexity is where latent ABI bugs hide: `ModuleEmissionContext.cs` (21), `ConcreteProtocolSpecializationEmitter.cs` (19), `ModuleHandler.cs` (16), `WrapperEmitter.Async.cs` / `MethodClosureBridge.cs` / `ClassHandler.cs` / `EveryProtocolEmitter.cs` (15), plus `MethodMarshalPlanBuilder.cs` + `OptionalProjection.cs`.
- **ABI surface size:** 1,237 `@_cdecl` wrapper emissions, 228 `CallConvSwift` / 133 `CallConvCdecl` call sites in the generator.
- **Runtime unsafe surface (memory-safety target):** 177 `unsafe`, 83 `GCHandle`, 78 `Unmanaged`, 68 `Marshal.`, 47 `fixed`, 21 `stackalloc`, 21 `Unsafe.As` — concentrated in `SwiftMarshal.cs` (1,669), `ExistentialContainer.cs` (1,181), `TypeMetadata.cs` (1,172).
- **Self-documented latent-bug classes** in `roadmap.md`: generated-local name shadowing (hardcoded locals like `tag`/`resultPtr` collide with projected params), multi-PAT existential boxing. Plus 10 `// workaround` sites and 3 `TODO`s.
- **Invariant artifact (both external brains independently flagged):** `.claude/rules/` holds **6** rule files — `constraints.md` (40+ compile-invisible "trap" invariants), `emitter.md`, `parser-marshaler.md`, `bindingtests.md`, `csharp-files.md`, `swiftui-bridge.md`. `WasEmitted` appears across 22 files. These cross-file "must-match-exactly" rules have no static enforcement → drift is compile-invisible.

**Claude's initial track list:** **A** = ABI/marshalling correctness (incl. runtime); **B** = Swift-feature coverage + testing-gap matrix; **C** = architecture / AI-maintainability; **D** = validation sweep + docs drift. (User prioritized **A + C** as headline, **B + D** as lower-priority.)

---

## 3. Cross-check synthesis

### Unanimous top-3 (all three rated High) — locks
1. **ABI / calling-convention fidelity** — everyone's #1 (Claude A · Codex #1 · Grok H1)
2. **ARC / ownership / lifetime / memory safety** (Claude A-runtime · Codex #3 · Grok H2)
3. **AI-maintainability / hotspot hazard-map** (Claude C · Codex #13 · Grok H6)

### Two-of-three, rated High by both external brains — Claude had under-weighted these
4. **Concrete Specialization (CSM) / generics / PAT** (Codex #6 · Grok H4) — high latent-bug density; roadmap full of "trigger to revisit" CS0xxx items
5. **Parser / ABI-ingestion / demangler fidelity** (Codex #7 · Grok H5) — source of truth; downstream inherits its errors
6. **Existentials / protocol proxies / witness dispatch** (Codex #5 · Grok M8)
7. **Closures / optional-closure / reabstraction** (Codex #4) — matches the memory's worst confirmed SIGSEGV trap (`feedback_optional_closure_reabstraction.md`), so it earns its own agent
8. **Async / throws / error-carrier paths** (Codex #8)

### Grok's standout unique High (neither Codex nor Claude elevated it)
9. **Invariant-drift / dedup / key-consistency / name-collision** (Grok H3) — anchored on the 6 `.claude/rules/*.md` files, 22 `WasEmitted` sites, and the roadmap's generated-local shadowing item. Compile-passing invariant violations are exactly the failure mode that bites AI agents editing a god-class. **Promoted to Tier-1**; pairs with the maintainability track.

### Independent confirmation of the B/D-is-lower call
Both Codex and Grok *independently* ranked the **coverage matrix = Medium**, **docs-drift = Low**, and a **full `nuke validate` re-run = "only if a High track triggers it."** This triangulates with the user's priorities and the "compile-probes, not full gates" choice.

### Divergences adjudicated
- Codex rated maintainability *Medium*; Claude + Grok rated it *High*. **Resolved High** — non-coding owner makes it a first-class concern.
- Codex split closures/async into their own High tracks; Grok folded them into lifetime. **Resolved: keep separate** — the memory's worst confirmed bug is closure-specific.

---

## 4. Prioritized master plan

The cross-check revealed that the headline **"A"** is a *cluster of 8 sub-tracks* and **"C"** is a *cluster of 2*. That decomposition is the value of the three-way pass.

### TIER 1 — Headline A: ABI / Marshalling cluster (deepest)

| # | Track | Target subsystems/files | Hunt for |
|---|---|---|---|
| A1 | P/Invoke ABI contract + x64 thunks | `PInvokeEmitter`, `CdeclParamMapper`, `WrapperEmitter.*`, `MethodMarshalPlanBuilder`, `ThunkEmitter/*` (Arm64/SysV) | CallConvSwift↔Cdecl mismatch, SwiftSelf/sret/indirect-result shape, x64 thunk register drift |
| A2 | Struct layout / register / VWT | `FrozenStructHandler`, `NonFrozenStructHandler`, `ValueWitnessTable`, `SwiftOptional` | @frozen-vs-resilient misclassification, optional tag/extra-inhabitant, tuple direct/indirect |
| A3 | ARC / ownership / lifetime | `Arc`, `SwiftHandle`, `ProxyLifetimeTracker`, `ExistentialContainer`, `SwiftMarshal` (177 `unsafe`/83 `GCHandle`) | leaked passRetained, double-free, missing retain on borrowed returns, async SafeHandle loss |
| A4 | Closures / optional-closure / reabstraction | `ClosureHandler`, `ClosureEmitter.*`, `MethodClosureBridge`, `NestedClosureBridge` | optional-closure reabstraction SIGSEGV trap, GCHandle lifetime, throwing-closure error leak |
| A5 | Existentials / protocol proxies / witness dispatch | `EveryProtocolEmitter`, `ProtocolProxyEmitter.*`, `WitnessDispatchEmitter`, `ExistentialHandler` | class-bound layout, composition size/order, missing PWTs, Optional<Any> fallback |
| A6 | Concrete Specialization / generics / PAT | `ConcreteSpecializationEngine`, `ConcreteProtocolSpecializationEmitter`, `BoundGenericsHandler`, `ConformanceGraph` (TypeDatabase/) | CS0xxx false-rejects, Self substitution, multi-PAT boxing, result-ptr alloc/free |
| A7 | Async / throws / error-carrier | `WrapperEmitter.Async`, `AsyncHarnessEmitter`, `SwiftResult`, `AsyncClosureHelper` | callback GCHandle leak, error-pointer ownership, cancellation/error asymmetry |
| A8 | Parser / ABI-ingestion / demangler | `SwiftABIParser`, `SwiftInterfaceAccessParser`, `Swift5Demangler` | public-req misclassified internal, mangling divergence, ABI-JSON shape drift |

### TIER 1 — Headline C: Architecture / Maintainability cluster

| # | Track | Target | Hunt for |
|---|---|---|---|
| C1 | AI-maintainability hazard-map | 5 mega-files + top-10 churn hotspots | duplicated decision logic, undocumented invariants, null-returning fallbacks, "future-agent will make a locally-plausible-but-globally-wrong change" hotspots |
| C2 | Invariant-drift / dedup / key-consistency | `.claude/rules/*.md` (6 files), 22 `WasEmitted` sites, overload/subscript key sites, `NameProvider` | doc↔code drift, generated-local shadowing, WasEmitted drift on new paths, dedup-key collapse |

### TIER 2 — Medium (run if tokens remain)
- SwiftUI bridge support matrix (`SwiftUIBridgeEmitter.*`, `SwiftUIViewDetector`)
- Wrapper / SDK / packaging / arch decisions (`CSharpWrapperCoGater`, `SwiftWrapperCompiler`, `Sdk.targets`, `ConsumerTargetsEmitter`)
- TypeDatabase / projection parity / AppleFrameworkRegistry
- **BindingTests skip-taxonomy & coverage matrix** (= user's Track B): inventory `[Skip]`/NativeAOT-skips, map validate-discovered bugs to BindingTests coverage

### TIER 3 — Low (sample or skip)
- Docs/roadmap drift (= user's Track D)
- ObjC interop pipeline
- Performance / API-drift readiness
- Full `nuke validate` re-run — **off** unless a Tier-1 track surfaces a specific validate concern

---

## 5. Execution model

- Each track = parallel finder agent(s) → **adversarial compile-probe verification** of each finding → one `TrackXX_*.md` report in this folder.
- Waves (each = one Workflow invocation):
  - **Wave 1** = Tier-1 A-cluster (A1–A8)
  - **Wave 2** = Tier-1 C-cluster (C1–C2)
  - **Wave 3** = Tier-2 Medium (if tokens remain)
  - **Wave 5** = synthesis → `STATE-OF-THE-CODEBASE.md` (risk heatmap + prioritized backlog + "top-20 files to touch with care")

### Locked decisions
- Headline priority: **A + C** (Tier-1). **B + D** are Tier-2/3.
- Verification: **static + compile probes** (no full gates by default).
- Output: `src/docs/audits/`.

### Open decisions (to confirm before launch)
- **Sequencing** of the waves over the 12h window.
- **Fan-out aggressiveness** per track.

---

## 6. Report index

_(populated as reports land — see `RESUME.md` for the live status table + how to continue)_

- [x] `Track-A1_PInvoke-ABI-Contract.md` — **done** (13 confirmed, unioned across 3 runs; raw runs in `Track-A1_run-reports/`)
- [x] `Track-A2_Struct-Layout-VWT.md` — **done** (4 confirmed: 1 P0 eightbyte mis-count → silent garbage return; 3 P1 = sub-8-byte Optional over-read, `Optional<T>` Buffer mis-sizing, `SwiftOptional<value-type>` None→zero collapse; 24 deferred)
- [x] `Track-A3_ARC-Ownership-Lifetime.md` — **done** (7 confirmed; 2 P0/P1 ARC-corruption SIGSEGVs)
- [x] `Track-A4_Closures-Reabstraction.md` — **done** (**5/5 Critical**; 7 confirmed: 5 P0 = throwing-closure null-deref before error check, throwing/struct-param closure-return re-emitting the Mono `!ji->async` crash lambda, three unguarded `[UnmanagedCallersOnly]` callbacks (escaping/throwing/indirect-return) that SIGABRT on a managed throw + uninitialized indirect buffer; 1 P1 = per-invocation box leak in NestedClosureBridge; 23 deferred, GenericClosureBridge least-verified)
- [x] `Track-A5_Existentials-Witness-Dispatch.md` — **done** (**5/5 Critical**; 9 confirmed: 2 P0 = opaque `any P` owned-return double-release → SIGSEGV (heap-free Destroy + proxy Dispose Destroy, no retain-on-read); 7 P1 = optional-existential receiver always returns `nil` (silent drop), EveryProtocol value-type getter/subscript/method-return buffer leak, `ExistentialContainer0` int→long round-trip drift, bare-`Any` param can't box non-primitives (throws), owned collection-element existential leak, class-bound `[any P]` array wrong stride (40B vs 16B) → ABI corruption; 10 deferred incl. dropped `inout` on C# side + composition existential dead-receiver)
- [x] `Track-A6_Concrete-Specialization-PAT.md` — **done** (5 confirmed in 2 P0 families: (a) CSM class-conformer `returnsGenericParam` reads carrier *address* as the object pointer → handle aliases freed buffer, **shipping/reachable from bare ABI conformance**; (b) `MethodGenericBridgeEmitter` fixed-256-byte result buffer → heap overflow >256B + double-free/allocator-mismatch + +1 ARC leak, latent (CSM emitter shadows it). 1 inconclusive nested-bound-generic CS0305; 6 well-probed refutations incl. protocol-composition `&` constraint (refuted ×4 — Swift canonicalizes before serializing); 32 deferred)
- [ ] `Track-A7_Async-Throws-Error-Carrier.md`
- [ ] `Track-A8_Parser-Demangler-Fidelity.md`
- [x] `Track-C1_Maintainability-Hazard-Map.md` — **done** (7 confirmed: 1 P0 SwiftUI async dup-param, 6 P1; dominant pattern = unguarded identifier emission across 6+ emitter families; ~47 same-shape deferred)
- [ ] `Track-C2_Invariant-Drift-Dedup.md`
- [ ] (Tier-2/3 reports as scheduled)
- [ ] `STATE-OF-THE-CODEBASE.md` (synthesis)

---

## Appendix A — Codex full plan (verbatim)

> Codex `gpt-5.5`, session `019e852b-5094-7ae2-92ba-2df5ae719c15`.

```
I detected runtime first: Darwin. I did not run validation or make changes.

For a 12-hour read-only audit, I would prioritize by "can this silently produce ABI-wrong code, leaks, crashes, or unmaintainable AI churn?" The highest-value split is not by folder, but by failure mode.

High Priority
1. P/Invoke ABI Contract Audit
   Targets: PInvokeEmitter.cs, WrapperEmitter.Marshalling.cs, WrapperEmitter.Return.cs, MethodMarshalPlanBuilder.cs, CdeclParamMapper.cs, generated BindingTests/output/*.cs/*.swift.
   Look for: CallConvSwift vs CallConvCdecl mismatches, missing/extra params, wrong SwiftSelf, wrong indirect result/error-register modeling, bool marshalling, generic metadata/PWT ordering, x86_64 thunk shape drift.
   Method: sample 30 generated wrappers across sync/async/throwing/mutating/generic/property/subscript cases and compare Swift @_cdecl signatures to C# P/Invoke declarations mechanically. Grep all CallConvSwift fallback paths and classify whether a wrapper should exist.
   Deliverable: ABI contract matrix with P0/P1 suspected mismatches and a list of direct CallConvSwift fallbacks that deserve BindingTests scrutiny.

2. Struct Layout, Register Passing, and Value Witness Audit
   Targets: FrozenStructHandler.cs, NonFrozenStructHandler.cs, TypeLoweringTests.cs, SwiftMarshal.cs, ValueWitnessTable.cs, SwiftOptional.cs.
   Look for: @frozen vs resilient misclassification, frozen-but-non-POD copy errors, optional tag/extra-inhabitant mistakes, mixed direct/indirect tuple returns, inout writeback gaps, class-with-buffer vs opaque-payload inconsistency.
   Method: build a shape taxonomy from code/tests, then verify every branch has a runtime BindingTests representative. Compare code against constraints.md traps and src/docs/Design/binding-structs.md.
   Deliverable: shape coverage grid: "supported / skipped / untested / suspect" for structs, tuples, optionals, and generic value carriers.

3. ARC, Ownership, and Lifetime Audit
   Targets: Arc.cs, SwiftHandle.cs, ProxyLifetimeTracker.cs, ExistentialContainer.cs, ClosureEmitter.cs, EveryProtocolEmitter.cs, BindingTests/RuntimeTestsApp/Lifetime.
   Look for: leaked passRetained, double release after takeRetainedValue, missing Arc.Retain on borrowed returns, async SafeHandle lifetime loss, finalizer-only correctness assumptions, simulator/device divergence.
   Method: trace ownership for returns, params, closures, existentials, protocol proxies, async callbacks. Require each passRetained/Retain to have an explicit owner and release path.
   Deliverable: ownership ledger with unmatched retain/release sites and tests that prove or fail to prove each contract.

4. Closure, Optional Closure, and Reabstraction Audit
   Targets: ClosureHandler.cs, ClosureProjection.cs, ClosureEmitter.cs, ClosureEmitter.SwiftWrapper.cs, MethodClosureBridge.cs, NestedClosureBridge.cs, GenericClosureBridgeEmitter.cs.
   Look for: optional closures not treated as escaping, wrong GCHandle lifetime, unsupported closure shape emitted instead of skipped, return marshalling drift, reabstraction thunk traps, throwing closure error leaks.
   Method: enumerate all closure gates and compare Layer 1 "method emits" vs Layer 2 "cdecl wrapper emits". Cross-check BindingTests skips in closures/protocols/async.
   Deliverable: closure shape table with supported, skipped, and dangerous-partial-support categories.

5. Existentials, Protocol Proxies, and Witness Dispatch Audit
   Targets: EveryProtocolEmitter.cs, ProtocolProxyEmitter.InterfaceImpl.cs, ProtocolProxyEmitter.Receivers.cs, WitnessDispatchEmitter.cs, ExistentialHandler.cs, ExistentialBypassEmitter.cs.
   Look for: class-bound existential layout mistakes, mixed protocol composition size/order mismatch, missing PWTs, wrong witness default selection, dead receiver implementations returning invalid carriers, Any/Optional<Any> unsafe fallbacks.
   Method: classify existential layouts: opaque any P, class-bound, Any, compositions, optional existentials, returns vs params. Compare emitted helpers to runtime container layout.
   Deliverable: existential ABI report with concrete suspicious proxy/witness paths and untested shape list.

6. Concrete Specialization, PAT, and Generic Constraint Audit
   Targets: ConcreteSpecializationEngine.cs, ConcreteProtocolSpecializationEmitter.cs, BoundGenericsHandler.cs, GenericTypeEmitter.cs, PInvokeHelperEmitter.cs.
   Look for: wrong generic arity, Self not substituted, SameType sugar mismatch, protocol-composition constraints treated as opaque, associated-type constraints skipped, value conformer passed to ISwiftObject APIs, PWT metadata ordering.
   Method: use roadmap open items as seed hypotheses, then inspect current code paths and unit tests for each. Sample generated CSM output and tombstones.
   Deliverable: CSM/PAT risk register separating known documented limitations from likely bugs.

7. Parser, Demangler, and Access-Control Truth Audit
   Targets: SwiftABIParser.cs, SwiftInterfaceAccessParser.cs, Swift5Demangler.cs, GenericSignatureParserTests.cs, SwiftInterfaceAccessParserTests.cs.
   Look for: public protocol requirements misclassified internal, nested ObjC enum names, overflow operators parsed as methods, async detection fallback errors, dependent-member parse loss, ABI JSON nodes with no children.
   Method: audit parser decisions that feed skip/suppress behavior. Compare parser output assumptions with .swiftinterface snippets and ABI JSON edge cases in tests.
   Deliverable: parser truth table with "false internal", "false public", "wrong type spec", and "demangle fallback" risks.

8. Async, Throws, and Error-Carrier Audit
   Targets: WrapperEmitter.Async.cs, AsyncMethodGenericBridgeEmitter.cs, AsyncHarnessEmitter.cs, SwiftResult.cs, AsyncClosureHelper.cs.
   Look for: callback GCHandle leaks, Swift error pointer ownership errors, async opaque existential owned-return mismatch, cancellation/error path asymmetry, three async emitter paths diverging.
   Method: trace success, failure, cancellation, and thrown-error paths separately. Compare sync and async ownership contracts.
   Deliverable: async/throws path matrix with per-path ownership and coverage status.

Medium Priority
9. TypeDatabase and Projection Consistency Audit
   Targets: TypeDatabase.cs, TypeDatabaseExtensions.cs, TypeProjectionFactory.cs, XML databases under src/Swift.Bindings/src/Data.
   Look for: Apple framework heuristic drift, optional ObjC bridged parity gaps, XML kind mistakes, SwiftUI two-path suppression divergence, AnyTypeFallback where a known type exists.
   Method: compare registry, XML, projection factory, and skip reports. Use binding-report.json skip reasons as leads.
   Deliverable: projection parity report and list of likely TypeDatabase false negatives.

10. Wrapper Compilation, SDK, Packaging, and Architecture Audit
    Targets: Program.cs, BindingsGeneratorCommand.cs, SwiftWrapperCompiler.cs, Sdk.targets, ConsumerTargetsEmitter.cs.
    Look for: stale wrapper fingerprints, missing NativeReference, arch option ignored in one path, Apple-framework vs third-party flow divergence, Windows path/package regressions.
    Method: static trace CLI modes: generate, compile-wrapper-only, SDK two-pass, Apple framework direct mode, package consumption.
    Deliverable: build-flow state machine with places metadata can lie.

11. SwiftUI Bridge Audit
    Targets: SwiftUIBridgeEmitter.cs, SwiftUIBridgeEmitter.AsyncPattern.cs, SwiftUIBridgeEmitter.InitAnalyzer.cs, SwiftUIViewDetector.cs, BindingTests/RuntimeTestsApp/SwiftUIBridge.
    Look for: platform gating mistakes, async inference wrong constructor, retained controller/session lifetime bugs, optional bound type/enum conversion errors, unsupported params silently bridged.
    Method: compare detector, collector, emitter, and tests for all documented parameter kinds and platform targets.
    Deliverable: SwiftUI bridge support matrix and lifetime/platform concerns.

12. BindingTests and Skip-Taxonomy Audit
    Targets: BindingTests/README.md, binding-report.json, BindingTests/RuntimeTestsApp/**, BindingTests/Sources/SwiftBindingsTestLib/**.
    Look for: skipped tests hiding project bugs, stale upstream classifications, compile-only coverage not backed by runtime assertions, missing SurfaceArea coverage for validate-discovered bugs.
    Method: inventory [Skip], [SkipOnSimulator], [SkipOnDevice]; map to confirmed upstream list vs project limitation vs suspicious gap. Current report shows 1025 types, 3661 members, 35 skipped types, 135 skipped members; highest skip reasons include SwiftUIView, EveryProtocolConformanceSkipped, UnsupportedSignature, UnsupportedType, AnyTypeFallback.
    Deliverable: skip burn-down planning report: "legitimate limitation / needs audit / likely bug / needs test-only repro".

13. AI-Maintainability and Hotspot Audit
    Targets: mega-files: EveryProtocolEmitter.cs, SwiftInterfaceAccessParser.cs, SwiftUIBridgeEmitter.cs, SwiftABIParser.cs, Swift5Demangler.cs.
    Look for: duplicated decision logic, undocumented invariants, switch fallbacks returning null, generated-local collisions, hidden ordering constraints, tests asserting implementation rather than behavior.
    Method: produce a "future AI hazard map": where an agent is likely to make a locally plausible but globally wrong change.
    Deliverable: maintainability risk report with invariant list and suggested future audit/test topics only, no code changes.

Low Priority
14. ObjC Interop Pipeline Audit
    Targets: src/Swift.Bindings/src/ObjC/**, ObjC unit tests, Apple supplement paths.
    Look for: ObjC import blind spots, availability projection drift, name collisions, Swift overlay/Foundation routing mistakes.
    Deliverable: ObjC pipeline gap memo.

15. Performance and API Drift Readiness Audit
    Targets: regression-matrix-performance.md, interop-performance-validation-plan.md, api-snapshot-tooling.md.
    Look for: missing benchmark hooks, no API surface ratchet, expensive wrappers hidden behind idiomatic APIs.
    Deliverable: non-blocking observability/tooling recommendations.

16. Documentation and Roadmap Consistency Audit
    Targets: CLAUDE.md, roadmap.md, src/docs/Design/**, .claude/rules/**.
    Look for: stale "known issue" statements, conflicts between rules and docs, retired campaign references, validation guidance drift.
    Deliverable: docs consistency report with stale/ambiguous guidance called out.

Recommended agent allocation: put two agents each on tracks 1, 2, 3, 5, and 6; one agent each on tracks 4, 7, 8, 9, 10, 11, 12, and 13; only run Low tracks if capacity remains. The highest probability of serious findings is ABI contract, ownership, existentials/protocols, and CSM/generics because those are both historically sensitive and heavily changed in recent commits.
```

---

## Appendix B — Grok full plan (verbatim)

> Grok, sessionId `019e852b-831a-7ea3-9b6e-4e9a1e235214`.

```
Audit Planning: Swift/.NET Bindings Generator (READ-ONLY, ~12h parallel-agent execution window)

Context and inspection performed (all read-only):
This is a mature, AI-agent-maintained Swift -> C# binding generator (~197k LOC in src/Swift.Bindings/src, pipeline Parser -> TypeDatabase -> Marshaler -> Emitter; ~18k LOC Swift.Runtime P/Invoke/ARC layer; 304+ unit test files under src/Swift.Bindings/tests/UnitTests/ + E2E BindingTests/ covering ~30 domains on Simulator Mono JIT + device NativeAOT). Largest/complex files exactly as noted: EveryProtocolEmitter.cs (5595 LOC), SwiftInterfaceAccessParser.cs (5250), SwiftUIBridgeEmitter.cs (3962), SwiftABIParser.cs (3481), Swift5Demangler.cs (3239), plus ConcreteProtocolSpecializationEmitter.cs (3196, CSM), WrapperEmitter.Async.cs, CSharpWrapperCoGater.cs, ProtocolExtensionEmitter.cs, WitnessDispatchEmitter.cs, ClosureHandler.cs, etc.

Inspected: full src/docs/ (roadmap.md + Design/*.md + Future/* upstream issues), .claude/rules/constraints.md (40+ "trap" invariants), AGENTS.md/Claude.md + scoped rules, .validation-baseline.json, BindingTests/ structure + sample binding-report.json + output/, artifacts/ (behavior-tier, pack-gate, x64-*-gate reports with skip stats), generator source tree (hotspots + cross-cuts via grep), runtime marshalling core, git history (recent ~154 commits on 4 largest files since late 2025; heavy focus on Apple frameworks, existentials, x64/Intel, ARC/+1, CSM, KeyPaths; "AI-style" commit messages), unit test layout (strong EmitterTests/, MarshalerTests/, ParserTests/ (31 files), DemanglerTests/, ConcreteSpecializationEngineTests, EveryProtocolEmitterTests, etc.), and Nuke build/validation orchestration.

Core principles from the repo (non-negotiable for any audit):
- "Every skipped test / runtime crash is guilty until proven innocent" - only 4 confirmed upstream .NET issues (Mono JIT async assert on CallConvSwift, non-blittable CallConvSwift rejection, specific Set.insert DONE_BLOCKING, Catalyst-x64 instability; see src/docs/Future/upstream-issues-README.md and memory feedback_mono_jit_blame.md). All else is generator/runtime bug.
- BindingTests (sim + device) + SurfaceArea/ corpus is the durable long-term gate; nuke validate is transitional discovery (roadmap retirement plan).
- Zero-regression on BindingTests pass count + unit tests for any change.
- Architectural "traps" (constraints.md) are compile-time invisible in many cases (key consistency across ~26 sites, WasEmitted at 13 emission points, projection parity, ISwiftObject seed-drop mirroring isResolvable, pipeline timing, wrapper arch decision sharing, etc.).
- High recent churn on largest files + external knowledge concentration (constraints.md, MEMORY.md refs, Claude sessions) makes long-term AI-maintainability a first-class risk for a non-coding owner.

Prioritization criteria (used to rank):
1. Runtime correctness impact (crash, silent wrong result, leak, ABI mismatch on real devices/loads).
2. Breadth of ABI surface affected (direct CallConvSwift paths, existentials, generics/PATs/CSM, ARC across async/throwing/closures).
3. AI-maintainability / invariant drift risk (LOC hotspots + cross-file "must match exactly" rules without static enforcement + churn + external docs).
4. Real-world vs test corpus gap (validate skips "UnsupportedSignature / GenericProtocolConstraint / UnsatisfiedGenericConstraint / UnsupportedClosure / DuplicateSignature" vs BindingTests coverage; roadmap "trigger to revisit" items).
5. Newer/less-exercised code (x64 thunks, SwiftUI hosting bridge, recent CSM/AppEntity/KeyPath, library-evolution paths).

HIGH (P0 - run first 4-6h with 4+ parallel agents)
H1: ABI & Calling-Convention Fidelity (Direct CallConvSwift vs Cdecl Wrapper Paths + x64 Thunks)
  Targets: BindingsGeneratorCommand.cs, Marshaler/Projection/MethodMarshalPlanBuilder.cs, Emitter/StringEmitter/Handler/MethodClosureBridge.cs, PInvokeHelperEmitter.cs, WrapperEmitter*.cs (esp. Async), ProtocolProxyEmitter.Receivers.cs + .Vtables.cs, ThunkEmitter/* (Arm64ThunkTarget, SysVThunkTarget, ThunkAssemblyEmitter, TypeLowering), CSharpWrapperCoGater.cs, SwiftWrapperCompiler.cs, all sites setting method.UsesWrapperLibrary / IsCdeclCompatibleType / deciding non-blittable, ConsumerTargetsEmitter.cs, runtime SwiftMarshal.cs + Arc.cs. Cross-check against generated output + .swift + nm -g on frameworks.
  Defect/risk classes: Wrong convention; missing/extra SwiftSelf / sret / indirect-result shapes; x64 thunk register/return mismatches; SafeHandle + async suspension lifetime; silent wrapper stripping; CallConvSwift fallback warnings vs actual emission.
  Deliverable: H1_ABI_CC_Fidelity_Report.md - executive risk rating, as-built decision flowchart (file:line), mismatch matrix by shape + platform, evidence excerpts, "top 12 must-verify-on-change" sites, gaps vs BindingTests, recommended minimal new fixtures (no fixes).

H2: ARC / Ownership / Lifetime / Memory Safety (VWT, +1, async/throwing/existential paths)
  Targets: Runtime (Arc.cs, ValueWitnessTable.cs, SwiftMarshal.cs, ExistentialContainer.cs, ProxyLifetimeTracker.cs, SwiftClassHandle.cs), Emitter (Utf8SliceEmitter.cs, StringReturnEmitter.cs, OptionalMarshalStrategy.cs, ClosureEmitter.InvokeThunk.cs + .Throwing.cs, WrapperEmitter.Async.cs, EveryProtocolEmitter.cs, ConcreteProtocolSpecializationEmitter.cs, SelfReconstructionEmitter.cs), fixtures in MemoryManagement/, Lifetime/, Async/, ErrorHandling/, Protocols/Existential*.
  Defect/risk classes: Leaks, double-free, premature release, missing retain on non-mutating ref returns, VWT misuse on resilient vs @frozen, Optional<Any>/composition existential boxing, RetainCycle gaps, library-evolution paths.
  Deliverable: H2_Ownership_Lifetime_Report.md - ownership model diagram, high-risk emission paths with site counts, evidence, coverage gaps, instrumentation ideas (no code).

H3: Deduplication / Overload / Name Collision / Key Consistency Invariants
  Targets: DefaultParameterOverloadEmitter.cs, ProtocolSignatureHelper.cs, SwiftSourceStripper, EmittedProjectedSignatures, ModuleEmissionContext.cs, all 13 WasEmitted setters, IHandler.GetProjectedCSharpMethodKey, NameProvider.cs, EveryProtocolEmitter.cs, ProtocolExtensionEmitter.cs, ConcreteProtocolSpecializationEmitter.cs, WrapperDedupTests.cs, ModuleEmissionContextCollisionTests.cs, Collisions/ fixtures.
  Defect/risk classes: Overload/subscript drops, generated local shadowing by projected params, WasEmitted drift on new paths, EveryProtocol cross-extension dedup collapse, dedup key mismatch, threading races on emission context.
  Deliverable: H3_Dedup_Invariants_Drift_Report.md - full site inventory with risk per invariant, uncovered shapes, "audit these on every PR" checklist, churn analysis.

H4: Concrete Specialization Engine (CSM) + Generic/PAT/Protocol-Extension Handling
  Targets: Marshaler/ConcreteSpecializationEngine.cs + partials, ConcreteProtocolSpecializationEmitter.cs, BoundGenericsHandler.cs, ProtocolExtensionEmitter.cs, ConformanceGraph.cs, RouteCSortShapeEligibility.cs, Generic*Bridge* emitters, ConformanceGraphResolutionTests.cs, ConcreteSpecializationEngineTests.cs, ProtocolExtension*Tests.cs, Generics/ + KeyPath/ + Protocols/ fixtures, AppEntity keypath emitters.
  Defect/risk classes: CS0xxx false-rejects or over-emits; availability floor propagation gaps; value-type conformer ISwiftObject constraint mismatches; multi-PAT boxing; CSM result-pointer alloc/free antipatterns; ConformanceGraph depth/DependentMember resolution bugs.
  Deliverable: H4_CSM_Risk_Register.md - prioritized latent-bug list by trigger condition + evidence, test coverage matrix, complexity assessment, invariant-test opportunities.

H5: Parser / ABI Ingestion / Internal Detection / Demangler Fidelity (Source of Truth)
  Targets: Parser/SwiftABIParser.cs, SwiftInterfaceAccessParser.cs (esp. CollectPublicMember + IsModuleInternal), Demangler/Swift5Demangler.cs + reducer, GenericSignatureParser.cs, SymbolGraphDocParser.cs, UnderscoreProtocolSynthesizer.cs, ModuleProcessor.cs, ParserTests/ (all 31), DemanglerTests/, artifacts fixture .abi.json + .swiftinterface files.
  Defect/risk classes: Missed @usableFromInline internal protocol requirements; mangling divergence; ABI JSON shape drift (Swift 6+); incorrect IsMutating/funcSelfKind/availability; ObjC nested enum naming; ProtocolComposition printedName fallback; cross-module conformance loss.
  Deliverable: H5_Parser_Fidelity_Gap_Analysis.md - parse success on corpus subsets, heuristic risks, demangler coverage, downstream assumption gaps.

H6: AI-Agent Maintainability, Invariant Enforcement & Knowledge Concentration
  Targets: .claude/rules/constraints.md (full), all 5 largest files + top 10 hotspots, AGENTS.md + Claude.md + scoped rules, git log on hotspots + constraints.md, unit test patterns, reporting/ + emission reports, external doc refs in code/comments.
  Defect/risk classes: Compile-passing invariant violations; drift between constraints doc and code; tribal knowledge concentration; long methods/files that defeat holistic agent reasoning; weak discoverability of "guilty until proven" + 4-upstream list inside the codebase; over-reliance on "run the gate" as only signal.
  Deliverable: H6_Maintainability_Audit_Report.md - quantitative + qualitative findings, risk to owner, "make checkable" opportunities, files needing owner docstrings or property tests, prioritized "hardest for future agents" modules.

MEDIUM (P1)
- M7: SwiftUI Bridge / Async Views / Hosting / KeyPath Integration (SwiftUIBridgeEmitter.cs + .InitAnalyzer/.AsyncPattern, two-path suppression, SwiftUIViewDetector, hosting controller + AppEntity, SwiftUI/ fixtures).
- M8: Protocol Proxy / Witness Dispatch / EveryProtocol Core (ProtocolProxyEmitter.*, WitnessDispatchEmitter.cs, EveryProtocolEmitter.cs, vtable/receivers/static-init, cross-module).
- M9: Wrapper/Bridge/Thunk Compilation Pipeline + Arch Decisions (CSharpWrapperCoGater, SwiftWrapperCompiler/PostProcessor, ThunkEmitter, ConsumerTargetsEmitter, CompileWrapperForArchitectures try/catch/finally + primary-restore rules).
- M10: BindingTests Coverage Sufficiency vs Validate Retirement (SurfaceArea/ corpus, domain gaps, how well past validate reds would have been caught, skip-class vs shape-class migration).
- M11: Projection Parity + AppleFrameworkRegistry + TypeDatabase Consistency (TypeProjectionFactory + all ITypeProjection impls, AppleFrameworkRegistry, XML DBs, IsOptionalObjCBridged parity, fallbacks, ConformanceGraph).

LOW (P2)
- Performance/ILLink/trimming descriptors.
- Public docs vs internal src/docs/ drift.
- SDK/Template/consumer DX edge cases.
- ObjC interop legacy.
- Full nuke validate re-run + baseline comparison (only if High tracks surface specific validate concerns).

Execution recommendations:
- Agent allocation: 2 agents on H1, 1-2 each on H2/H3/H4, 1 each on H5/H6. Overlap M7-M11 lightly. One synthesis agent at 8-10h mark.
- Tooling discipline: Read-only only. No nuke/build/xcodebuild/simctl unless explicitly escalated for inspection only. Prefer artifacts/ + existing output/ for generated code.
- Cross-cutting synthesis deliverable: Master risk heatmap, "top 20 files that must be touched with extreme care," recommended BindingTests/SurfaceArea seed corpus additions, invariant-check automation opportunities, 1-page executive for the non-coding owner.
- Success criteria: All 40+ constraints.md items spot-checked; all 5 largest files + top 5 hotspots deep-reviewed; decision trees extracted for H1/H2/H3; 3+ real validation libs sampled end-to-end; explicit mapping of roadmap "trigger" items to code locations.
```
