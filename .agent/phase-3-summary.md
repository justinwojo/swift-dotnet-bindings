# Wave-2 Session 03 — C# verify-recover loop

STATUS: COMPLETE — the joint (Swift-wrapper + emitted-C#) fixed-point loop is landed, gated, and corpus-proven. All three correctness gates green; the review round surfaced no High/Critical.

## What shipped

The wave-1 Swift-wrapper verify-recover loop now extends to the emitted C#. After the wrapper
compiles clean each round, an optional C# verifier runs; its compile errors attribute through a
C#-plane interval map to the exact recovery unit, feed the SAME monotonic denylist, and re-render
pristine — so convergence now requires BOTH planes clean in one round (a joint fixed-point). A C#
withdrawal removes the member's Swift wrapper too, so the next round re-verifies Swift first.

Five non-negotiables, each tested:
- **One controller** — reuses `WrapperRecoveryController` with a C#-verifier driver (`InEmissionDriver._verifyCsharp`).
- **Tree-based attribution** — Roslyn diagnostic span → C#-plane interval map (`CSharpIntervalMapProvenanceStep`, UTF-16 columns) → member fragment → recovery unit; coarse/unmapped hits stay `RequiresGraphClosure` fail-closed (session 04).
- **One channel** — every C#-loop withdrawal flows through Gate 0 with origin `CSharpRecoveryWithdrawal`, distinct wording `"Withdrawn by C# verify-recover: withdrawn to recover the C# compile"`, stage `CSharpCompile`.
- **Cross-verifier convergence** — Swift is re-verified after C# withdrawals; joint fixed-point pinned end-to-end.
- **Withdrawal ≠ fix** — predictable Swift-wrapper families get plan-stage skips (session 01); the loop is the backstop.

**Reconciler retirement (begun):** on the loop path (`verifyRecoverCompile != null`), `StrippedSymbolCSharpReconciler` is not invoked; a post-loop stripped symbol fails closed (SWIFTBIND115). Legacy legs (SDK two-pass, `--compile-wrapper-only`) keep the reconciler until session 06. Corpus strip count is 0, so SWIFTBIND115 did not fire on any library.

Diagnostic codes: SWIFTBIND111 (did not converge), 112 (withdrew N units), 113 (post-loop publication gate — unchanged), 114 (C# verify inconclusive — round-0 pass-through, post-withdrawal fail-closed), 115 (post-loop stripped symbol on loop path).

## Correctness gates (all green, generator rebuilt this turn)

- `nuke test`: **15,274 pass / 0 fail / 1 skip** (unit floor auto-ratcheted 15246 → 15274; `git_sha` validate-section untouched). +2 over prior verified 15,272 = the two new M3 tests.
- `nuke binding-tests --compile-only`: **exit 0**, no fail-closed gate tripped.
- `nuke binding-tests --skip-regen` (iOS Simulator, Mono JIT): **3242 pass / 0 fail / 0 crash / 37 skip** — baseline matches. The 37 skips include the two documented upstream `[SkipOnMonoJit]` async-cancel tests (proven-upstream Mono arm64 unwinder crash, pure-managed `TaskCompletionSource` repro).

## Corpus joint-convergence stats (isolated `--skip-convert` runs, rebuilt generator)

| Lib | Result | Detail |
|---|---|---|
| **Hero** | converts `compile_failed` → **green** | SWIFTBIND112, **3 CSharpCompile-stage** withdrawals over 2 rounds: `HeroConditionalContext.matchedAncestorView`, `HeroTargetState.overlay`, `HeroTargetState.subscript` (all accessor-groups). Joint fixed-point. |
| **CSV.swift** | **stays green** | SWIFTBIND112, 1 withdrawal (`CSVWriter.write`) over 2 rounds. Joint fixed-point. |
| **PromiseKit** | **stays green** | Clean joint convergence. |
| **ReSwift** | **stays green** | Clean joint convergence. |
| **Eureka** | honest fail-closed | SWIFTBIND111 **RequiresGraphClosure** after 3 rounds — CS8895 `UnmanagedCallersOnly` in generic `HeaderFooterView<TViewType>` is inherently coarse (session-04 coarse-scope authorization). Loop made progress, then refused to mis-attribute. Correct, not a wrong result. |
| **CocoaMQTT** | honest fail-closed | SWIFTBIND114 → 111 **InputConfiguration** — the C# verify build hit NU1101 (missing dep packages CocoaMQTTWebSocket/Starscream in this environment) AFTER 1 withdrawal, so inconclusive-after-withdrawal failed closed rather than ship an unproven reduction. Environmental, not a wrong result. |

No library produced a wrong result (unsound retention / over-withdrawal cascade).

**Harness caveat (not a defect):** running all 6 libraries' full generate+verify+compile pipelines
concurrently produced a spurious MSB4018 ("task failed unexpectedly") in the C# verification build
for CSV.swift — parallel dotnet-build contention, not a regression. Isolated, CSV.swift converges
green. This actually confirmed the SWIFTBIND114 policy: a genuine inconclusive-after-withdrawal
(here, transient MSB4018) correctly fails closed rather than shipping an unproven binding; with the
contention gone the verifier returns Clean and the loop converges. Corpus libs must be run isolated,
not 6-way parallel under one repo root (an internal-binding-testing harness property).

## Attribution-mapping cases

- **Real diagnostics** (corpus): Hero — 3 real Roslyn/SARIF CS diagnostics mapped through the C#-plane interval map to exact accessor-groups; CSV.swift — 1. Each landed on a droppable leaf/accessor and produced a CSharpCompile-stage disclosure row.
- **Fixtures**: `CSharpVerifyRecoverDriverTests` (round-0 joint clean; C# error → withdraw → re-verify Swift → converge; inconclusive round-0 pass-through vs post-withdrawal fail-closed; **new:** verifier-throw → Inconclusive round-0 pass-through vs post-withdrawal fail-closed). `CSharpIntervalMapProvenanceStepTests` (UTF-16 column resolution, Swift-plane rejection, positionless / unmapped-file / past-EOF / null guards).

## Family-B (and siblings) diagnostic families + dispositions

- **Eureka** — `RequiresGraphClosure` (coarse CS8895 in a generic type). Disposition: honest fail-closed; deferred to session-04 coarse-scope authorization. The production `RecoveryGraph` is not yet wired (see not-planned.md).
- **CocoaMQTT** — `InputConfiguration` (NU1101 missing dependency packages). Disposition: environmental; fail-closed is correct on an unproven compile after a withdrawal.
- Hero / CSV.swift / PromiseKit / ReSwift — recovered or already-green; the withdrawn units are genuine leaf/accessor culprits with disclosure rows.

## Review round (Codex + Grok, one round; no High/Critical → no re-review needed)

- **Codex M3 (verifier exceptions bypass SWIFTBIND114)** — CONFIRMED + FIXED. `MsbuildSarifCSharpVerifier.Verify`'s outer `try` has only a cleanup `finally`, no catch, so a command-runner timeout / project-emission IO fault escapes the delegate. Root-cause fix: wrap the single `_verifyCsharp()` call site in `InEmissionDriver` in try/catch → `Inconclusive`, preserving the round-0 pass-through / post-withdrawal fail-closed policy. Locked by 2 new tests (`ThrowsInfrastructureFailure` behavior).
- **Codex M1 / Grok M1 (SWIFTBIND115 wording)** — wording corrected in the log message, the case comment, and the `ClassifyStrippedSymbols` doc. The overclaim "the post-loop recompile stripped a symbol the loop's compile did not" is removed: convergence is `AllSlicesClean`, NOT a zero-strip predicate, so a converged loop can hand back a non-empty strip set; on the loop path the retired reconciler no longer claws it back, so a residual strip fails closed. **Keying stays per-mandate** (`verifyRecoverCompile != null`).
- **Codex M2 (SwiftUI bridge omitted from in-loop verify)** — FALSE POSITIVE. `BindingProjectEmitter` emits the `.SwiftUIBridge.cs` `<Compile>` item unconditionally (Exists-gated), NOT `HasBridgeSwift`-gated; `HasBridgeSwift` gates only native refs. The in-loop verification csproj compiles the same C# that ships.
- **Grok L5 (Column doc)** — FIXED: `CompilerDiagnostic.Column` doc now states its plane-dependent encoding (UTF-8 byte for swiftc, UTF-16 char for Roslyn/SARIF).
- **Grok L7 (SWIFTBIND111 grammar)** — FIXED: "whose {planes} did not reach a clean compile" (agrees for both "wrapper" and "wrapper and C#").
- **Grok M2 (C#-plane has no symbol/anchor fallback → multi-site cascade) / M3 (iteration cap 4)** — sound-but-conservative by design (session-04 coarse scope, session-07 bisection); routed to not-planned.md with triggers. Neither is an unsound-ship risk (post-loop SWIFTBIND113 gate still blocks non-compiling C#).
- **Grok L4 / L6** — Low, conservative-not-false-enable / reporting-label-only. No change.

## not-planned.md routing

- New test-infra latent: `ModuleEmissionContext.Default` static singleton shared across parallel tests via `TypeHandlerContext.Empty` → flaky `Collection was modified` in `HasOpenGenericAncestor()` (a distinct site of the existing `PayloadSemantics` flake class). Pre-existing, orthogonal to this work; observed once, green on re-run.
- New wave-2 verify-recover row: C#-plane attribution fallback + shared iteration cap (Grok M2/M3) with triggers.

## Out of scope (unchanged)

Coarse-scope authorization (session 04), typed contracts (05), shipped-artifact consumption + legacy-leg policy (06), bisection for unattributable C# errors (07). No device legs (sim-gated). `nuke validate` not run.
