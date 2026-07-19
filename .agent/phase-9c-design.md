# Phase 9c design record — in-emission wrapper verify-recover loop (inc.3+inc.4)

**Status:** design settled, implementation NOT started. This is the self-contained brief for the
fresh full-context session that will execute inc.3a→inc.3c + inc.4. It supersedes the inc.3c *sketch*
in `phase-9a-handoff.md` (the two-IsFatal-sites post-compile driver), which a paired Codex+Grok design
consult proved **unsound**. Everything the handoff says about inc.1/inc.2 (committed: 880e465c,
8880d3bb), the recovery model, the report schema, and the honest-scope language still stands and is
NOT repeated here — read `phase-9a-handoff.md` first, then this.

Written by session-09c after the design consult; team-lead approved the split-if-needed rule and the
in-emission architecture. inc.3a/inc.3b were NOT committed by 09c — the whole coherent 3a→3c+inc.4
implementation is handed to the fresh session because 3a's surface is tightly coupled to 3c's
consumer and starting the core-pipeline surgery without budget for the full gate battery + review
would risk a half-built core-pipeline change (the one outcome worse than a split).

## Design-consult provenance (resumable)
- **Codex Session: `019f77d2-e92f-72b3-9fd5-03747f90e4d7`**
- **Grok sessionId: `019f77d2-ecd4-77a1-af5a-60443a5183f5`**
- Both brains **independently converged** on the same disqualifying holes in the post-compile-driver
  sketch. High-confidence signal — do not re-litigate; build the in-emission design below.

---

## Why the handoff's inc.3c sketch is dead (the four soundness holes)

The sketch put the loop at the two `outcome.IsFatal` sites in `BindingsGeneratorCommand.cs` (~L1085
xcframework-mode, ~L1265 Apple-direct-mode), re-rendering via a closure over emitter internals AFTER
`GenerateBindings` returned. Both reviewers found multiple independent paths from that placement to a
**compile-clean-but-runtime-wrong binding** — the ONE unacceptable outcome. These are now hard
requirements the in-emission design MUST close:

1. **Dirty-state re-render (Codex#1, Grok#1).** `ContainedModuleEmission.Run` captures its
   decl/context snapshots at *entry* and restores only on attempts 2+ *within one Run*
   (`ContainedModuleEmission.cs:81-90`). The first production `Run` (`Program.cs:597`) commits its
   journal and returns; `GenerateBindings` then FURTHER mutates `decl`/`typeDatabase`
   (`ProtocolHandler.FixupProtocolInheritedRequirements`, `ClassHandler.PopulateEmittedClassMethods`
   at `Program.cs:713`) and writes artifacts. A later `Run(seed)` would snapshot ALREADY-MUTATED
   state → mass DuplicateSignature skips / wrong collision suffixes / partial surfaces that compile
   but mis-bind. **Requirement:** capture the pristine PRE-first-emission baseline and restore it
   before EVERY recovery render (including the first seeded one), OR run the whole loop before the
   journal commit.

2. **Stale post-emission artifacts (Codex#4, Grok#2).** After the first render, `GenerateBindings`
   writes the binding-artifact/generation report, the module-database XML (feeds downstream modules'
   `WasEmitted`/method sets, `Program.cs:731`), the trimmer descriptor / API manifest, and runs
   **`WrapperSymbolIntegrityGate`** (`Program.cs:~746-757`). A loop that only re-runs `EmitModule`
   leaves all of these describing the pre-withdrawal surface. The integrity gate NOT re-running on
   the settled render is a direct runtime-`EntryPointNotFoundException` channel (a withdrawn member's
   P/Invoke could survive in a stale artifact). **Requirement:** convergence must re-run the FULL
   post-emission finalization (report, module DB, integrity gate, manifest, descriptors) from the
   settled render — post-hoc `SkippedItem` row insertion is NOT sufficient.

3. **Wrong-bytes attribution (Codex#5, Grok#3).** Emission publishes the pre-strip on-disk wrapper
   `ModuleFragmentSet` (`ModuleEmitter.cs:187-205`). swiftc instead compiles the POST-processed copy
   under `.wrapper-build/`: `SwiftWrapperPostProcessor.Process` strips blocks (line shifts,
   `SwiftWrapperCompiler.cs:213-235`) AND `SimulatorOnlyMemberDetector.ApplySimulatorGuards` inserts
   `#if targetEnvironment(simulator)` lines (`SwiftWrapperCompiler.cs:260-274`). Attributing a
   diagnostic's file:line against the pre-strip map resolves the wrong fragment → withdraws a HEALTHY
   sibling, or misses the culprit → `Unattributable` fail-closed. `WrapperStripRemap` +
   `PostProcessingResult.CleanedLineSources` already exist for exact remapping
   (`WrapperStripRemap.cs:9-30`) and are NOT wired into the compile paths; guard-rewritten files must
   be treated as UNMAPPED (rely on the `SymbolAnchorProvenanceStep` block/symbol index for those).
   **Requirement:** the `IntervalMapProvenanceStep` must be built over a map remapped to the exact
   compiled bytes; wiring `WrapperStripRemap` into the compile path is part of 3c's definition of
   done (team-lead confirmed).

4. **ABI-violation channel + PostProcessor dual-channel (Codex#2/#3, Grok#4/#9).** `AbiContractChecker.Validate`
   throws `AbiContractViolationException` at `ModuleEmitter.cs:154` BEFORE any file is written;
   `GenerateBindings` catches → returns false (`Program.cs:~767`); `NonRecoverableFault` explicitly
   classifies it non-recoverable (`NonRecoverableFault.cs:40-42`); and `AbiCheckViolation` carries
   only `MethodName`/`EntryPoint`, no `DeclId`/`RecoveryUnitId`. It CANNOT be a post-compile loop
   input. Likewise the PostProcessor pre-strip removes blocks with no `RecoveryUnitId`; folding it in
   as "iteration 0" without a single owner risks double-suppression with the existing post-success
   `StrippedSymbolCSharpReconciler`. **DECISION (team-lead approved): DEFER both to 09b.** Both are
   fail-closed today, so deferral loses nothing; folding them in risks the prime invariant. Leave the
   ABI throw and the PostProcessor strip channel exactly as they are; record honestly in the summary
   as 09b scope. Do NOT attempt either in wave-1.

Additional required corrections from the consult (lower severity but in-scope for wave-1):
- **Droppable ≠ scope alone (Codex#7).** `RecoveryUnitClassifier` represents an UNCLASSIFIED artifact
  as `LeafApi` while marking it not-droppable-alone (`RecoveryUnitClassifier.cs:138,156`). The
  production artifact→unit lookup MUST reject any classification whose droppable-alone flag is false
  rather than hand a nominal leaf to the controller (which would withdraw it as ABI-neutral when it
  is not). Check the flag; a non-droppable classification becomes coarse/unattributable → fail closed.
- **Non-exception denial record (Codex#8, Grok#8/#12).** Keying a synthetic `EmitterFaultRecord` on
  `unit.Decl.Canonical` is gate-sound (the poison gate reads only `Canonical`), and `AccessorGroup`
  normalization already maps to the property (`RecoveryUnitId.ForAccessorGroup`, matches
  `MemberGateEvaluator` property gates). BUT `EmitterFaultRecord.Details` hard-codes "Emitter threw
  …" (`EmitterFaultRecord.cs:60`) — a fabricated one produces a misleading tombstone/report. Add a
  denial ORIGIN (emitter-fault vs wrapper-compile-withdrawal) so the seeded record reads honestly,
  and ensure SINGLE-OWNER reporting: relabel the settled render's skip row in place
  (`RecoveryStage` is mutable, `BindingReport.cs:367`) rather than emitting a second row.
- **Driver result too narrow (Grok#5).** `IWrapperRecoveryDriver.RenderCompileAttribute` returns only
  `AttributionResult?`; the converged path still needs the `SwiftWrapperCompilationResult` /
  `WrapperBuildOutcome` (stripped symbols, xcframework path) for `StrippedSymbolCSharpReconciler` +
  manifest. The driver must expose the last successful compile outcome via a side channel (e.g. a
  `LastConvergedOutcome` property the caller reads after `Run` converges) — the controller signature
  is committed (inc.1) and must not change.
- **`--compile-only` can't prove ABI soundness (Codex#9, Grok#14).** inc.4's fixture must ALSO get a
  BindingTests sim RUNTIME leg that invokes every RETAINED healthy sibling and round-trips it — a
  compile-only gate cannot catch a shifted vtable / wrong entry point / healthy-sibling-that-faults.

---

## The settled architecture: an in-emission recovery session

The loop lives INSIDE `GenerateBindings` (or a session object it owns), not after it. The compile is
INJECTED (the compile machinery — `CompileForArch` / direct — lives in `BindingsGeneratorCommand` and
closes over resolution/arch/paths, so `GenerateBindings` receives a compile delegate).

```
BindingsGeneratorCommand:
  build a compile delegate `compileWrapper(outputDir) -> WrapperCompileDiagnostics`
      (wraps CompileForArch/direct in 3a collecting-mode)
  call GenerateBindings(..., compileWrapper)            // NEW optional param; null under
                                                        // --compile-only / non-wrapper paths (no loop)

GenerateBindings (Program.cs):
  capture PRISTINE pre-emission baseline (decl + emissionContext + typeDatabase journal)
  loop = WrapperRecoveryController.Run(new InEmissionDriver(baseline, compileWrapper, ...))
      driver.RenderCompileAttribute(denylist):
          restore pristine baseline                     // hole #1
          seed = WrapperDenylistSeed.Build(denylist)    // inc.3b
          poison = ContainedModuleEmission.Run(decl, ctx, db, ..., seed)   // Gate 0 re-render
          fragmentSet = ctx.FragmentSet                 // this render's map
          remapped = WrapperStripRemap over post-strip/guarded compiled bytes   // hole #3
          diags = compileWrapper(outputDir)             // inc.3a union across slices
          if diags.AllSlicesClean: stash converged outcome; return null
          attributor = new DiagnosticAttributor([ IntervalMapProvenanceStep(remapped),
                                                  SymbolAnchorProvenanceStep(...leaf/accessor lookup,
                                                     DroppableAlone-gated...), LinkerSymbolProvenanceStep(...) ])
          return attributor.Attribute(diags.Diagnostics)
  on Converged:  re-run FULL finalization from the settled render                // hole #2
                 (report, module DB, integrity gate, manifest, descriptors),
                 relabel withdrawn units' skip rows RecoveryStage=SwiftCompile,
                 reconcile stripped symbols, write WrapperSection from converged outcome
  on Blocked/NoProgress/InputConfiguration/Unattributable/IterationCapExhausted:
                 fail closed exactly as today (context.ExitCode, manifest records failure)
```

The healthy path is preserved: a module whose first render+compile is clean returns `null` on round
1 (empty denylist) → `Run` converges in 1 round with an empty denylist → behaviorally identical to
today's single compile, with the full finalization running once as it does now. The loop only does
extra work when the first compile is fatal.

### Open implementation choice for the fresh session
Two ways to satisfy holes #1+#2 — pick by reading the code, don't assume:
- **(A) Loop-before-commit:** move the `ContainedModuleEmission.Run` + compile + attribute loop to
  run BEFORE `GenerateBindings`'s post-emission finalization block, so finalization naturally runs
  once on the settled render. Cleanest if the compile delegate can be threaded in and the
  finalization block is cohesive. RISK: the wrapper compile currently happens in
  `BindingsGeneratorCommand` AFTER `GenerateBindings` returns; folding it in inverts the two-phase
  flow (the outer L1085/L1265 sites and their manifest/stripped-symbol/lipo handling must move or be
  refactored to consume the converged outcome).
- **(B) Explicit pristine-baseline session object** that owns the snapshots and a "commit artifacts
  once" step, re-runnable. More surface but keeps `GenerateBindings`'s shape closer to today.
Prototype both against `WrapperSymbolIntegrityGate` re-running correctly; that gate re-running on the
settled render is the acid test for whichever you pick.

---

## inc.3a — cross-slice diagnostic union (surface, now that 3c's consumer is known)

`SwiftWrapperCompiler.InvokeSwiftCompiler` (`SwiftWrapperCompiler.cs:1691`, `internal static void`)
throws `InvalidOperationException` on `exitCode != 0` (`:1960`) after dumping FULL stderr to
`<outputBinaryPath>.swiftc-stderr.txt` (`:1911`, best-effort, swallowed on IO error). `CompileAll`
compiles the sim slice (`:383`) then the device slice (`:529`) only if sim didn't throw — so a failing
sim slice throws before device runs → NO union today. Four entry points depend on the throw: `Compile`,
`CompileSlice`, `CompileAll`, `CompileBridge*`. `SwiftDiagnosticParser.Parse(stderr)` (pure,
never-throws) is ready.

**Surface (opt-in; throw contract for existing callers UNCHANGED):**
```csharp
public sealed record WrapperSliceDiagnostics(string SliceId, bool Succeeded,
    IReadOnlyList<DiagnosticGroup> Diagnostics);
public sealed record WrapperCompileDiagnostics(bool AllSlicesClean,
    IReadOnlyList<DiagnosticGroup> Diagnostics,          // union across ALL failing promised slices
    IReadOnlyList<WrapperSliceDiagnostics> Slices,
    SwiftWrapperCompilationResult? Result);              // non-null only when AllSlicesClean
```
A recovery-mode compile (new method or an opt-in collector param on `CompileAll`/`CompileSlice`/
`Compile`) that, per PROMISED slice: wraps the compile in try/catch; on failure reads the per-slice
side-file `<binaryPath>.swiftc-stderr.txt` (fallback to the exception message), parses to
`DiagnosticGroup`s, records a failed `WrapperSliceDiagnostics`, and CONTINUES to the next slice.
Requirements the consult verified/flagged:
- **Do NOT promote partial staging** — promotion (`PromoteStagedXcframework`, `:588`) stays
  success-path only; the `finally` drops the unpromoted staging tree. Confirmed sound: sim/device
  binary paths differ, so `<binaryPath>.swiftc-stderr.txt` is NOT cross-slice-overwritten
  (`:309` vs `:431`), and shadow dirs are per-triple.
- **Cover the WHOLE slice op, not just `InvokeSwiftCompiler`** — thunk compile, SDK resolution,
  shadow-module precompile, `ValidateCompiledSliceBinary`, and clang linking can also throw; the
  per-slice try/catch must capture those as a classified infrastructure failure (fail-closed, not a
  leaf culprit) while still collecting diagnostics from the OTHER promised slices.
- **"Promised slices"** = sim + optional device for `CompileAll`; the single slice for
  `Compile`/`CompileSlice`; multi-arch fat-fold EXTRAS are best-effort today (contractual-unmet stays
  outside recovery — correct). Don't let a fold-degrade swallow a leaf failure silently; `log()` any
  dropped coverage.
- Prefer refactoring the existing method with an opt-in collector over duplicating its ~300-line
  body; keep every existing caller on the exact throw path (collector == null → today's behavior).
- Unit-test with the injected `ICommandRunner` (already a param) producing failing stderr for sim
  and asserting the union collects BOTH slices' diagnostics without throwing and without promotion.

---

## inc.3b — Gate-0 denylist seed (standalone, unit-testable)

Confirmed mechanism: `ContainedModuleEmission.Run(..., seed: EmitterPoisonList?)` →
`EmissionAttempt.Begin(poison)` → every handler consults `EmitterFaultGate.IsDenied(declId)` /
`EmissionSeam.TryDenyUpFront(decl)` / `EmissionSeam.Guard(...)` which read `poison.IsPoisoned(declId)`
keyed on `DeclId.Canonical`. A poisoned member is denied UP FRONT with a tombstone, a report row, and
a consumed-but-empty vtable slot (layout-preserving) — exactly what a leaf withdrawal needs.

Build:
- `WrapperDenylistSeed.Build(IReadOnlySet<RecoveryUnitId>) -> EmitterPoisonList` — one record per
  unit keyed on `unit.Decl` (AccessorGroup already normalized to the property), Scope=`unit.Scope`,
  Escalation=null (leaf/accessor only). Use a DEDICATED denial origin so the tombstone/report reads
  "withdrawn by wrapper verify-recover" not "Emitter threw" (add an origin discriminator to
  `EmitterFaultRecord` or a sibling record the poison gate accepts).
- The production artifact→unit lookup for `SymbolAnchorProvenanceStep`/`LinkerSymbolProvenanceStep`:
  resolve leaf/accessor units only, and REJECT any classification whose droppable-alone flag is false
  (`RecoveryUnitClassifier` — check the flag, don't fabricate a nominal leaf).
- Tests: seed a poison list from a denylist of a method + a property, re-render a fixture module,
  assert both members are tombstoned up-front with the honest origin string and healthy siblings keep
  identical names/suffixes; assert a non-droppable classification is refused.

---

## inc.4 — durable gate + corpus proof (unchanged from handoff, PLUS a runtime leg)

Resilience fixture Swift lib (2-3 structurally-unbindable members interleaved with healthy siblings,
genericized) next to `SwiftBindingsTestLib`, wired into `nuke binding-tests --compile-only` (not
opt-in). Assert: generation succeeds; emitted C# compiles; hostile members tombstoned with correct
root/cascade rows (`RecoveryStage=SwiftCompile`); healthy siblings survive with identical
names/suffixes; `WrapperSymbolIntegrityGate` clean. **NEW (Codex#9):** add a BindingTests sim RUNTIME
leg that calls every retained sibling and round-trips a value — compile-only cannot prove the retained
surface is ABI-sound. Then the daemonized single-process leaf-subset corpus before→after proof and the
leaf-vs-helper/conformance wrapper-failure distribution (non-negotiable #3; rough count, no tooling).

## Final gates for 09 completion (whole 3a→inc.4 diff)
`nuke test` ≥ 15,189 (+new) · `nuke binding-tests --compile-only` (incl. the resilience fixture)
EXIT 0 · default sim leg ≥ 3,242 / 0 / 0 (37 skips) · a BindingTests sim runtime leg for the fixture ·
paired Codex+Grok review of the full diff (fix Highs + real Mediums; one verifying re-review per fixed
High) · then the honest `.agent/phase-9-summary.md` (force-add). Flag a `--device` leg (the loop
touches emitted wrapper bytes; if inc.3a/3c change how P/Invokes are surfaced, device warrants a run).

## Working-discipline reminders (unchanged)
Root-cause only. `dotnet build -c Debug` after generator edits BEFORE regen (stale `bin/Debug/` masks
changes). Never `git stash`. No doc-file refs in code comments. Copyright header on new files. Headless
worker: Codex/Grok reviews FOREGROUND with the Bash `timeout` param set to 600000 (a foreground `&`+`wait`
WITHOUT the timeout param is killed at the 2-min default — set it explicitly). `setsid` is absent on
macOS. Grep the whole codebase for a bug pattern before finishing.

---

## SEQUENCING DECISION (settled 2026-07-18 via paired consult — inc.3a defers WITH inc.3c)

**inc.3b landed alone this session; inc.3a is deferred to land coherently with inc.3c.**

Both Codex (`019f77e6-959a-7cd0-ad54-6561e85dc03c`) and Grok (`019f77e6-99a2-77e1-8c7a-6b796502f30a`)
independently converged: inc.3a's cross-slice collecting mode must NOT land as un-wired dead code. Its
surface — promote policy, return shape, per-slice dedup, handling of non-swiftc failures — is
consumer-defined, and the recovery driver (inc.3c) is the only correct acceptance oracle. Grok rated
"do not land collecting as dead code" High. Landing 3a first would bake in a surface 3c then has to
bend around. So 3a rides with 3c in the fresh session. Hard requirements for that session, captured so
they are not re-derived: catch the WHOLE per-slice compile body (not just `InvokeSwiftCompiler`);
NEVER promote a partial staging tree; snapshot full stderr in-catch (the `<binaryPath>.swiftc-stderr.txt`
side-file is overwritten per slice, so a later slice clobbers an earlier slice's dump).

**What shipped this session (inc.3b, commit 8e76a420):**
- `EmitterFaultOrigin` discriminator + `EmitterFaultRecord.ForRecoveryWithdrawal(...)` — honest
  withdrawal wording, exception path unchanged by default.
- `WrapperDenylistSeed.Build(IReadOnlySet<RecoveryUnitId>) -> EmitterPoisonList` — the Gate-0 seed.
- 5 unit tests (`WrapperDenylistSeedTests`), incl. an end-to-end render proving each denied unit is
  tombstoned under its own `Describe()` identity and that a `Count` sibling emits identically to a
  clean render. Floor 15,189 -> 15,194.
- Paired review (Codex `019f77f2-1e38-76a2-8de3-8b3da8c836c5`, Grok `019f77f2-2260-7a70-9ced-e746cf395f83`):
  NO Highs, production path sound both. Acted on the shared Medium (E2E under-assertion) by
  strengthening the test to prove BOTH units independently + a sibling-parity baseline, and the Low
  doc-drift on `EmitterFaultRecord`. The `Record`-bool-collapse note (Codex Low / Grok Medium) is
  unreachable on the wave-1 leaf/accessor path AND safe when reached (both units map to the same
  declaration gate, so the member is still denied) — left as-is by design.

**Deferred to the fresh session, in order:** inc.3a (cross-slice diagnostic surface) WITH inc.3c
(production in-emission verify-recover loop inside `GenerateBindings`), then inc.4 (resilience fixture
+ `--compile-only` wiring + BindingTests sim runtime leg + corpus before->after proof). Everything the
3c surgery needs is in the sections above; the ABI-as-loop-input and PostProcessor-as-iteration-0
channels remain 09b scope.
