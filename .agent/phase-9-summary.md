# Session 09 — wrapper verify-recover loop — final summary

**Outcome:** the wave-1 in-emission verify-recover loop is landed and wired end-to-end on the default
(simulator) wrapper path, in one coherent commit. The loop renders the wrapper under a denylist,
compiles all slices, attributes each swiftc error to a recovery unit, withdraws leaf/accessor-scoped
culprits, and re-renders until the wrapper compiles clean or it cannot attribute / makes no progress —
in which case it **fails closed** (today's behavior: the module's wrapper compile fails). Coarser-than-leaf
scopes escalate/fail-closed; they never recover.

## Session-09 SHAs (all on `main`)
- `36810bb4` Contain an emitter fault to the declaration that caused it (Phase-7 carryover / Gate-0 seam)
- `22dac441` Attribute a failed wrapper compile to its root recovery units (inc.2 — parser + attributor)
- `35c75b28` Skip two async-cancel sim tests for an upstream Mono unwinder fault
- `880e465c` Add the wave-1 wrapper verify-recover controller (inc.1)
- `8880d3bb` Anchor symbol-less wrapper blocks to their owning declaration
- `8e76a420` Seed the wrapper verify-recover denylist into an emission re-render (inc.3b — Gate-0 seed)
- `2a2b4d81` / `ba985a80` — 09a handoff + interim summary docs
- **`<this commit>`** inc.3a + inc.3c (production loop) + inc.4 (hermetic integration test) — one commit

## Loop mechanics as built
- **Controller** (`WrapperRecoveryController.Run(IWrapperRecoveryDriver, cap=4)`): monotonic denylist, fresh
  culprits required for progress, `D'==D` ⇒ `NoProgress`, unattributable / global-input / coarse scope ⇒
  fail closed, cap ⇒ `IterationCapExhausted`. Recovers ONLY `LeafApi` + whole `AccessorGroup` (ABI-neutral).
- **Production driver** `InEmissionDriver` (inc.3c): before EVERY render restores the pristine baseline —
  `_declBaseline.Restore()` → `_contextBaseline.Restore()` → `_rebuildCollaborators()` →
  `_outerJournal.RestoreInto(_typeDatabase)`, plus a `_preRender` that deletes the prior render's
  `{ns}.Wrapper.swift` / `{ns}.*.s` so each render's on-disk artifact set is a pure function of the denylist.
  Then `WrapperDenylistSeed.Build(denylist)` → Gate-0 poison → `ContainedModuleEmission.Run(seed, retainInto)`
  → `_compileWrapper`. Clean ⇒ return null (converged); else attribute via `DiagnosticAttributor` over the
  cross-slice diagnostic union.
- **Cross-slice diagnostic surface** (inc.3a): `WrapperSliceCollector` / `WrapperCompileDiagnostics` — every
  promised slice is compiled (no first-failure abort), diagnostics unioned so a unit failing on any slice is
  withdrawn on every slice; a failed compile drops the staging tree (`Result` null), so only an all-slices-clean
  compile ever promotes.
- **Attribution** (from inc.2): `SwiftDiagnosticParser.Parse` → `DiagnosticGroup[]`; provenance steps in
  priority order (interval-map P1 → symbol/anchor P2 → origin-anchor P3 → linker P4), each wrapped in
  `DroppableGate` so a non-droppable hit becomes unattributed ⇒ fail closed.
- **Wiring** (`Program.cs` / `BindingsGeneratorCommand.cs`): `GenerateBindings` takes an optional
  `compileWrapper` delegate; supplied ONLY for `shouldCompileWrapper && resolution != null && wrapperArch ==
  "simulator"`. Logs `SWIFTBIND111` (failed closed) / `SWIFTBIND112` (withdrew leaves).

## Deliberate deviations (honest scope)
1. **Loop settles the on-disk wrapper source; the post-loop compile is UNCHANGED.** The command still
   recompiles the primary wrapper from the settled on-disk source after the loop. The loop's own compile
   `Result` (`LastConvergedOutcome`) is intentionally a throwaway; the post-loop recompile of settled source
   is the authoritative ship gate and is fully fail-closed. The design's "no-recompile consume converged
   outcome" is **09b**.
2. **inc.4 delivered as a hermetic real-swiftc-in-the-loop integration test** (`WrapperRecoveryLoopIntegrationTests`,
   4 tests): recorded genuine swiftc stderr flows through the REAL parser + attributor + controller.
   The BindingTests `--compile-only` resilience fixture + a sim runtime leg are **deferred to 09b** — the
   emitter tombstones structurally-unbindable members up front (8 empirical probes across documented-fragile
   emitter areas each emitted a wrapper that compiles clean), so a natural "emitted-but-broken" wrapper
   fixture would enshrine a live generator bug, which policy forbids.
3. **Loop is wired for the simulator wrapper-arch path only.** SDK two-pass (`--skip-wrapper-compilation`),
   `--compile-wrapper-only`, and `device|all` do not enter the loop; they still fail closed at the post-loop
   compile. Wave-1 scope. **Family A is reduced, not dead.**

## Corpus before→after proof
20 single-framework libs (single-process leaf subset: Swinject, CryptoSwift, SnapKit, SwiftyBeaver,
KeychainSwift, DeviceKit, ObjectMapper, DifferenceKit, TinyConstraints, BonMot, AlertToast, SkeletonView,
NVActivityIndicatorView, AMPopTip, SwiftyGif, Starscream, XMLCoder, WhatsNewKit, RichTextKit, PhoneNumberKit),
generated + wrapper-compiled with the baseline vs loop-active generator.
- **Loop fully inert on today's corpus: 0 `SWIFTBIND112` withdrawals, 0 `SWIFTBIND111` fail-closed.**
- Before→after equivalent for all 20 (XMLCoder exits 1 identically in both — a pre-existing generation
  failure, not loop-related). Wrapper compilation confirmed exercised ("Compiling wrapper into
  …SwiftBindings.xcframework").
- **Leaf-vs-helper/conformance wrapper-failure distribution: empty** (0 wrapper failures on this subset).
  The loop is a dormant safety net on the current corpus — it activates only where a healthy sibling would
  otherwise be lost to a leaf/accessor failure.
- Byte-identity: the BindingTests full regen ran with **0 loop activity**, so generated `.cs`/`.swift` are
  byte-identical to baseline (proven inert, corroborated on Swinject where only GeneratedAt/UpdatedAt differ).

## Gates
- `nuke test`: **15,202 passed / 0 failed / 1 skipped** (Swift.Bindings.Unit.Tests, +4 from the new
  integration tests) + Analyzers 35/35 + Runtime 719/720. Floor `swift_bindings_unit_pass_floor` ratcheted
  15,194 → 15,202 (target auto-writes it to the real deterministic count).
- `nuke binding-tests --compile-only`: **EXIT 0** (BindingTests Succeeded; wrapper compiled clean).
- `nuke binding-tests` (full default sim): **3242 pass / 0 fail** after a confirming re-run.
  The first run flaked with 5 crashes in `PatParentAsyncVoidMethodsTests` (SIGSEGV in
  `mono_sigsegv_signal_handler_debug`). Root-caused as the documented upstream, Swift-independent,
  **layout-sensitive Mono arm64 JIT exception-unwinder flake** — the immediate `PatParentAsyncMethods`
  siblings already carry a committed `[SkipOnMonoJit]` with a pure-managed `TaskCompletionSource<T>` repro
  (zero Swift/binding frames on the faulting stack) that "reproduces only under full-suite load; adding
  unrelated sibling methods relocates the JIT code and hides it." The BindingTests regen ran with **0
  `SWIFTBIND111/112`** (loop inert ⇒ generated + managed test assembly byte-identical to baseline), so the
  crash is provably not this change. Phase-7's summary independently recorded the same test family flaking
  byte-identically at HEAD. **Not our bug** (per the confirmed-upstream Mono-JIT list discipline).
- `--device` leg: **flagged, not run.** The loop touches emitted wrapper bytes on the recovery path (inert
  on today's corpus). A device (NativeAOT) leg is warranted before any release that depends on the loop
  actually firing; not required for this inert-on-corpus landing.

## Review (paired Codex + Grok, full uncommitted diff)
- Codex session `019f786c-1644-79a3-bb62-709827a0d517`; Grok session `019f786c-1c9c-78c2-be94-8150cd1a8e96`.
- **Both: NO High findings.** Core fail-closed guarantee confirmed — Gate-0 poisons C# and Swift together,
  so no withdrawal leaves a dangling P/Invoke; mis-attribution over-drops consistently; non-droppable /
  coarse hits fail closed; controller termination sound.
- **Shared M1 (empty-collector "false convergence"):** verified against code and dispositioned as
  non-correctness. The two null/empty-result paths are "no wrapper surface at all" (correct convergence)
  and "post-processor stripped everything" (pre-existing tombstone behavior, unchanged). The loop's compile
  result is a throwaway; the authoritative gate is the unchanged post-loop recompile of settled source, so
  even a falsely-declared convergence cannot ship wrong ABI — worst case the loop stops early and the
  post-loop compile fails closed (fail-closed-degraded). Precision matters only once the loop `Result`
  becomes authoritative → **09b**.
- **Codex M2 / Grok M4 (production `InEmissionDriver` untested):** known inc.4 deferral → 09b.
- **Grok M2/M3 (SDK/device path asymmetry; loop-arch vs post-loop-arch):** documented wave-1 scope; Grok
  itself labels M3 correctness-safe.
- **Lows** (demangle-tally reset, dead `LastConvergedOutcome`, last-failed-render on disk, untested
  Snapshot/Restore): cosmetic / hygiene / deferred-wiring; none affect ABI.
- No High and no reachable correctness-Medium ⇒ no verifying re-review round warranted (the round gate
  triggers only on a fixed High).

## Precise 09b backlog
1. **Populate the production `RecoveryGraph`** so symbol/anchor hits resolve a culprit via dependency lookup
   rather than the artifact's own classification (wave-1 derives the unit from the artifact directly).
2. **ABI-as-loop-input / PostProcessor-as-iteration-0:** feed the `AbiContractChecker` result and the
   post-processor's up-front strip into the denylist loop as recovery units, instead of failing the module
   closed inside emission.
3. **Consume the converged outcome without re-compiling** — make `LastConvergedOutcome` authoritative and
   retire the post-loop double-compile; this is where the convergence predicate (shared M1) must become
   precise (require ≥1 recorded clean slice or an explicit no-surface signal).
4. **Production-driver purity test:** drive one real module through two renders and assert render N is
   independent of render N-1 (journal + baseline + denylist), plus the BindingTests resilience fixture +
   sim runtime leg deferred from inc.4.
5. **Extend the loop to the SDK / `--compile-wrapper-only` / `device|all` paths** (or ratify permanent
   path asymmetry).
