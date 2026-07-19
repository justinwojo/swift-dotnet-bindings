# Phase 9a handoff — wrapper verify-recover loop (inc.1+inc.2 done; inc.3+inc.4 remain)

**Status:** 09a is *partially* landed. inc.1 (pure controller) and inc.2 (origin-anchor
attribution) are committed, gated, and reviewed on `main`. inc.3 (wire the loop into inline
single-process generation) and inc.4 (resilience fixture + corpus proof) are **not started** —
this document is the self-contained brief for the fresh continuation session that will execute
them. Do not re-derive from the transcript; everything needed is here.

Scope reminder (team-lead-endorsed split): **09a** = single-process, inline verify-recover loop
with **LeafApi + whole-AccessorGroup recovery ONLY**, fail-closed (module rung) for every coarser
scope. **09b** = separate dedicated session, runs before session 10, populates the production
`RecoveryGraph` and lands sound helper/conformance/type recovery.

---

## Honest-scope language (MANDATORY — keep in summary, report strings, docs)

- Say **"inline generation, leaf/accessor recovery"**. Do **NOT** claim "Family A is dead."
- Record explicitly: the SDK two-pass / `--compile-wrapper-only` **out-of-process** path
  (`Program.cs:1328`, `RunCompileWrapperOnly`) **cannot re-emit** under the loop — it is a compile
  fast-path with no emitter in the process. That path is wave-2 (design §Stage 6). 09a covers only
  the inline `BindingsGeneratorCommand` single-process generation path.
- Record explicitly: **RecoveryGraph population is 09b.** Wave-1 recovers only `LeafApi` /
  `AccessorGroup` (provably ABI-neutral); every coarser culprit **fails the module closed**.

## Non-negotiables (from team-lead, for 09a)

1. **Fail-closed is the invariant.** Any attribution that would require helper / conformance /
   type / closure withdrawal escalates to the **module rung** — never leaf-poison a multi-artifact
   unit. A binding that compiles but is wrong at runtime is the one unacceptable outcome; module
   failure is acceptable. (The controller already enforces this: any fresh culprit whose scope is
   not `LeafApi`/`AccessorGroup` → `RequiresGraphClosure` blocked result.)
2. **Honest scope claims everywhere** (see above).
3. **Corpus-distribution recording rides with inc.4.** While the corpus is in hand, cheaply record
   the wrapper-failure distribution (leaf-scoped vs helper/conformance-scoped) in the summary — it
   sizes 09b's payoff and feeds session-10 wave-2 planning. Rough count from fixtures/corpus
   evidence; **do not build extra tooling** for it.
4. Everything in the original session-09 brief stands: gates, paired end-of-task Codex+Grok review,
   commit discipline, and a final `.agent/phase-9-summary.md` (titled honestly as **09a**, stating
   what 09b must do, with pointers to the saved Codex/Grok session ids below).

---

## Committed so far on `main`

| Increment | SHA | What |
|---|---|---|
| inc.1 | **880e465c** | `WrapperRecoveryController` — pure verify-recover loop, 6 non-negotiable properties, unit-tested. Zero production callers yet (shipped ahead of its driver, same as this handoff anticipates for 3a). |
| inc.2 | **8880d3bb** | Priority-3 `// SBW-ORIGIN:` origin-anchor attribution: anchors on ~15 symbol-less wrapper blocks + `WrapperBlockIndex.ResolveChain` (innermost-first) + `SymbolAnchorProvenanceStep` enclosing-anchor fallback. |

### inc.2 review dispositions
- Paired Codex+Grok, foreground (headless rules). **No Highs.**
- Codex **Medium fixed + tested**: `SymbolAnchorProvenanceStep` consulted only the single smallest
  block; on a nested-`@_silgen_name` symbol-registry miss it dropped to coarse scope instead of
  falling back to the enclosing anchor. Fixed via `ResolveChain` walk; added
  `SymbolAnchorStep_InnerSymbolUnregistered_FallsBackToEnclosingAnchor`.
- Grok **Mediums documented as latent/out-of-scope**: empty same-line `extension {}` + `FindBlockEnd`
  `j > start` over-eat — verified the reachable shape (empty conformance immediately followed by an
  anchor with no blank) has **zero** occurrences; anchors sit *before* the block so they don't
  activate the quirk. Left as documented latent.

### inc.2 evidence
- Additive-only wrapper diff: regenerated BindingTests wrappers = **630 `// SBW-ORIGIN:` comment
  lines added, 0 non-anchor byte changes** to the existing emitted surface (comment-only; byte
  identity of every head line preserved). Anchors are load-bearing for symbol-less header-line
  attribution (confirmed via the `WrapperBlockIndex` nesting model — a diagnostic on an
  `extension Foo {` header resolves to the anchor block, else drops to coarse MODULE scope).
- `EmissionDeterminismTests` is a double-emit self-comparison (no stored snapshot) → anchors appear
  in both emits, passes automatically. No determinism baseline to update. BlastRadius goldens are
  compiled-binary tables (Swift comments stripped at compile) → unaffected.

### Resumable review context (for the continuation session's / 09b's reviewers)
- **Codex Session: `019f77ac-a9e4-7072-9cbd-67c45bc3ebb8`**
- **Grok sessionId: `019f77ac-adc8-7bd2-ba77-973678d1b895`**

### Gate numbers at inc.2 commit (8880d3bb)
- Unit: **15,189 passed / 0 failed / 1 skipped**. `build/baselines/validation-baseline.json`
  `swift_bindings_unit_pass_floor` auto-bumped **15,181 → 15,189** (+8: 7 anchor tests + 1 fallback).
- `nuke binding-tests --compile-only`: **EXIT 0**.
- Baselines of record: `nuke test` ≥ 14,772 floor (actual 15,189); BindingTests sim leg target
  **≥ 3,242 pass / 0 fail / 0 crash** (37 skips per team-lead; spec text says ≥3238); corpus 39–120.
- Device leg **pending** (inc.2 anchors touch generated wrapper bytes as comments only; inc.3/inc.4
  wiring may warrant a `--device` leg — flag it, the loop touches what ships, not calling
  conventions/marshalling).

---

## The seam map (file:line — what exists vs what must be built)

The single biggest gap: **there is no driver, and no denylist-seeded re-render ("Gate 0").**

### `WrapperRecoveryController` (built, tested, no production caller)
`src/Swift.Bindings/src/Diagnostics/Recovery/WrapperRecoveryController.cs`
- Pure static loop driven by the seam interface `IWrapperRecoveryDriver.RenderCompileAttribute(IReadOnlySet<RecoveryUnitId> denylist) → AttributionResult?` (L96–104). **Implemented nowhere in `src/`** — only test doubles.
- `Run(driver, iterationCap=4)` (L147): each round calls the driver; `null` = converged; global-input
  or unattributed error → fail closed; any fresh culprit whose scope isn't leaf/accessor →
  `RequiresGraphClosure`; else add fresh culprits to the denylist and loop; zero fresh culprits while
  still failing → `NoProgress`.
- `IsLeafRecoverable(scope)` (L138) = `LeafApi` or `AccessorGroup` only.
- Result record `WrapperRecoveryResult` (L54): `Converged`, `Denylist`, `Rounds`, `Cause`, `Blocking`.

### The generation seam (emission runs once; failure is terminal)
`src/Swift.Bindings/src/BindingsGeneratorCommand.cs` (2179 lines)
- **`BindingsGenerator.GenerateBindings(...)` at L852** — the ONE emission call. 18 out-params, **no
  seed/denylist parameter** anywhere. There is no re-render path; emission runs exactly once.
- Wrapper compile via local `CompileForArch` closure (L979–1062) → `CompileWrapperForArchitectures`
  (L1067) → `WrapperBuildOutcome.From` (L1082).
- **`outcome.IsFatal → context.ExitCode = …; return;` at ~L1085** (xcframework-mode) — the current
  "fail hard" gate the loop must replace with `WrapperRecoveryController.Run(driver)` + re-check the
  same `WrapperBuildOutcome`/manifest/report machinery against the converged result.
- **A second, structurally identical direct-mode branch at ~L1220–1303** (`CompileDirectForArch`,
  `directOutcome`, ~L1265). **Both** IsFatal sites must be wired.
- `StrippedSymbolCSharpReconciler.ProcessDirectory` at L1096 / L1278 (post-hoc C# reconcile of
  stripped symbols; not attribution).

### `SwiftWrapperCompiler` — NEW inc.3a-critical finding
`src/Swift.Bindings/src/Configuration/SwiftWrapperCompiler.cs` (2982 lines)
- **`InvokeSwiftCompiler` (L1691)** communicates failure by **throwing `InvalidOperationException`**
  whose message carries only a **4000-char stderr *preview*** (error/undefined-symbol lines only).
  Full stderr is *best-effort* dumped to a side-file **`<outputBinaryPath>.swiftc-stderr.txt`**
  (L1911–1912, swallowed on IO error). It does **not** return structured diagnostics.
- That throw-on-failure contract is depended on by **three callers**: `CompileAll` (L383 sim, L529
  device), `CompileSlice` (L864), `CompileBridge*` (L1146+).
- **`CompileAll` (L148)** compiles sim first (L302–419), then device *only if* `deviceResolution != null`
  (L422+). Because a failing sim slice **throws before the device slice runs**, there is **no
  cross-slice diagnostic union** today — behavior is "throw on first failing slice," not "collect all
  and union."
- Coarse result type `SwiftWrapperCompilationResult?` (L12–47): `XCFrameworkPath`, `CompiledFileCount`,
  `StrippedBlockCount`, `StrippedBlocksBySubCause`, `SliceCount`, `StrippedSymbols`. No
  `CompilerDiagnostic`/`DiagnosticGroup` anywhere.
- The parser exists and is ready: **`SwiftDiagnosticParser.Parse(stderr) → IReadOnlyList<DiagnosticGroup>`**
  (`src/Swift.Bindings/src/Diagnostics/Attribution/SwiftDiagnosticParser.cs`) — handles positioned
  diagnostics (file:line:utf8col:sev:msg), notes-attach-to-primary, tool/bare/linker (Undefined
  symbols) blocks. Nothing in `SwiftWrapperCompiler` calls it yet.
- **Assessment (why 3a can't be designed in isolation):** the *shape* of 3a's diagnostic-capture
  surface — out-collector vs a result object vs reading the side-file — must be chosen by how the
  **3c driver** consumes it (per-slice stderr, continue-past-failing-slice, union across slices, feed
  `DiagnosticAttributor`). Building 3a before 3c risks the wrong surface. Hence this whole handoff:
  do 3a→3c as one coherent full-context session.

### PostProcessor pre-strip (runs, but acts invisibly — must become recorded iteration 0)
`src/Swift.Bindings/src/Configuration/SwiftWrapperPostProcessor.cs`
- `Process(...)` (L120) invoked unconditionally before every wrapper compile at **two sites**:
  `CompileAll` L216–217 and `CompileSlice` L706–707.
- `PostProcessingResult` carries `StrippedBlockCount`, `StrippedBlocksBySubCause` (`StripSubCause`
  enum: InternalType/NSInvocation/Other — **not** `RecoveryUnitId`), `StrippedSymbols`,
  `CleanedLineSources`. Counts reach the manifest, but **no strip is recorded as a `RecoveryUnitId`
  or a `SkippedItem` report row**. Spec 09 requires: pre-strip may run as iteration 0, but *every*
  strip must flow through the recovery-unit machinery (visible in report, counted in `D`). Currently
  it acts invisibly relative to `RecoveryStage`/`RootCauseId`/`CascadeFrom`.

### `AbiContractChecker` (always throws — must become a loop input)
`src/Swift.Bindings/src/Emitter/AbiContractChecker.cs`
- `Validate(...)` (L212) invoked once at emission time from `ModuleEmitter.cs:154`, **before** any
  file is written. On a violation it **throws `AbiContractViolationException`** (SWIFTBIND095) →
  fails the whole run. No degrade/denylist path; no warn-only branch found (no catch of that
  exception anywhere in `src/`).
- Violations are already structured (`AbiCheckViolation`: `DiagnosticCode`, `RuleId`, `MethodName`,
  `EntryPoint`, `Explanation`, `AffectedElements`) → clean inputs to turn into `RecoveryUnitId` loop
  inputs, replacing session-03's interim fail-publication. Classes left warn-only in session 03 stay
  warn-only.

### Report schema (fields exist, unpopulated)
`src/Swift.Bindings/src/Reporting/{SkipAttribution.cs,BindingReport.cs,SkipAttributionLinker.cs}`
- `RecoveryStage` enum (SkipAttribution.cs:42) already has **`SwiftCompile`** — but **no `SkipReason`
  maps to it** in `SkipCauseClassifier.BuildTable()` (used stages today: Plan/Emit/Parse/SymbolValidation).
- `SkippedItem` (BindingReport.cs:296) already carries settable-post-hoc `RootCauseId` (L348),
  `CascadeFrom` (L354), `CauseOwner` (L365), `RecoveryStage` (L368), `Confidence` (L374). Rows append
  to `BindingReport.SkippedItems`; `SkipAttributionLinker` fills attribution later.
- Missing: a path that takes the controller's final `Denylist`/`Blocking`/`Cause`, walks each
  withdrawn `RecoveryUnitId.Decl` → owning declaration, and appends/updates `SkippedItem` rows with
  `RecoveryStage = SwiftCompile` + owner/root/cascade.

### Recovery model + attribution (built + tested, never wired in production)
- `RecoveryScope` enum (Model/Recovery/RecoveryScope.cs) is **9 values**, least-severe-first:
  `LeafApi, AccessorGroup, ForwardProtocolView, ManagedProtocolConformance, ConformanceEdge,
  SharedHelperBundle, TypeRepresentation, TypeSurface, Module`. (No `Input` scope — input/toolchain
  live in `CauseOwner`/`WrapperRecoveryFailureCause`.)
- `RecoveryUnitId` (Decl + Scope), factories `ForAccessorGroup`/`ForSharedHelper`/`ForConformanceEdge`.
- `RecoveryGraph` / `RecoveryGraphBuilder` / `DependentClosure(seeds)` / `RecoveryPolicy.Escalate` —
  **fully built and tested, NEVER populated/called by any emitter.** ← **this population is 09b.**
- `DiagnosticAttributor(IEnumerable<IProvenanceStep>)` (Diagnostics/Attribution/DiagnosticAttributor.cs) —
  `.Attribute(IReadOnlyList<DiagnosticGroup>) → AttributionResult` (`Diagnostics`, `Culprits`
  distinct-by-unit errors-only, `Fingerprint`, derived `HasUnattributedError`). Steps:
  `IntervalMapProvenanceStep(ModuleFragmentSet)`, `SymbolAnchorProvenanceStep(WrapperBlockIndex,
  Func<string,ArtifactId?> symbolLookup, Func<ArtifactId,RecoveryUnitId?> unitLookup)`,
  `LinkerSymbolProvenanceStep(symbolLookup, unitLookup)`. In production the `symbolLookup`/`unitLookup`
  delegates need a populated wrapper-symbol registry and — for coarse scopes — the 09b graph; for 09a
  they only need to resolve **leaf/accessor** units.

---

## Remaining decomposition (for the fresh session)

- **inc.3a — cross-slice structured-diagnostic surface in `SwiftWrapperCompiler`.** Capture per-slice
  stderr, parse via `SwiftDiagnosticParser`, **continue past a failing slice**, and **union**
  DiagnosticGroups across sim+device (target-slice consistency: a device-only failure must not be
  lost when sim compiled clean). Must wrap — not silently break — the existing throw contract that
  `CompileAll`/`CompileSlice`/`CompileBridge*` callers depend on. **Design its surface together with
  3c's consumption pattern** (that's why it wasn't bolted on solo).
- **inc.3b — Gate 0: denylist-seeded emission re-render.** Thread `IReadOnlySet<RecoveryUnitId>` into
  emission so a denied `LeafApi`/`AccessorGroup` unit's member drops on re-render.
  **HYPOTHESIS (UNVERIFIED):** Gate 0 can hook the *existing per-member skip machinery* (the
  validation-driven `_skippedMethodKeys`/emit-decision gates already in the handlers) rather than
  thread a brand-new parameter through every emitter — map a `RecoveryUnitId.Decl` into that skip set.
  **Verify before committing to a design.** This is the foundational piece; keep it scoped to
  leaf/accessor for 09a.
- **inc.3c — the production `IWrapperRecoveryDriver` + wiring.** Bind (re-render with denylist) +
  (`SwiftWrapperCompiler` 3a union) + (`DiagnosticAttributor` with real leaf/accessor symbol→unit
  lookups) behind `RenderCompileAttribute`; replace **both** `outcome.IsFatal` sites (~L1085, ~L1265)
  with `WrapperRecoveryController.Run(driver)`; record PostProcessor strips as recorded iteration 0;
  feed `AbiContractChecker` violations in as loop inputs (replacing the throw); populate
  `SkippedItem` rows with `RecoveryStage=SwiftCompile` + root/cascade for each withdrawn unit; keep
  H2 supersession (wrapper failure no longer fails publication when the loop converges to a sound
  degraded binding; still fails at the module rung).
- **inc.4 — durable gate + corpus proof.** A resilience fixture Swift lib (its own small target next
  to `SwiftBindingsTestLib`, 2–3 structurally-unbindable + healthy-interleaved constructs,
  genericized) wired into `nuke binding-tests --compile-only` (not opt-in). Gate asserts: generation
  succeeds; emitted C# compiles; hostile members tombstoned with correct root/cascade rows; healthy
  siblings survive with identical names/suffixes; `WrapperSymbolIntegrityGate` clean. Then daemonized
  single-process leaf-subset corpus before→after proof (per-library: hard-fail → green/degraded-green
  N tombstones/still-failing+why); re-run session-03 checker-blocked libs. **Record the leaf vs
  helper/conformance wrapper-failure distribution here** (non-negotiable #3; rough count, no tooling).

## Final gates for 09a completion (whole 3a→inc.4 diff)
`nuke test` ≥ 14,772 (+new; current floor 15,189) · `nuke binding-tests --compile-only` (incl. the
resilience fixture) EXIT 0 · default sim leg **≥ 3,242 / 0 / 0** · paired Codex+Grok review of the
full 09a diff (fix Highs + real Mediums; one verifying re-review per fixed High) · then the honest
**`.agent/phase-9-summary.md`** (force-add, `.agent/` is gitignored) recording loop-convergence
stats, fixture shapes + why, checker-integration behavior, any session-05 policy amendment under
real fire, and the corpus distribution. Flag a `--device` leg.

## Working-discipline reminders (still in force)
Root-cause only (no skip/assertion-weakening). `dotnet build -c Debug` (or `nuke compile`) after
generator edits **before** regen — stale `bin/Debug/` masks changes. Never `git stash`. No doc-file
references in code comments (inline the rationale). Copyright header `// Copyright (c) 2026 Justin
Wojciechowski.\n// Licensed under the MIT License.` on new files. Headless worker: run Codex/Grok
reviews **foreground** (`&` + `wait`, timeout 600000ms), never `run_in_background`, never end the
turn while a review runs. Grep the whole codebase for all instances of a bug pattern before finishing.
