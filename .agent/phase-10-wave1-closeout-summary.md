# Wave-1 closeout + wave-2 planning — summary

Final session of the binding-resilience wave-1 program: full-corpus re-sweep with honest
accounting, floor ratchets + leftover routing, and the wave-2 session-doc set. No generator
behavior changes except one measurement-honesty fix (below), TDD'd and gated.

## Corpus re-sweep accounting (120 libraries, vs program-start 39/120)

Green: **39 → 42**. Buckets: same 102 / improved 18 / worsened 0 / missing 0.

| Category | Count | Libraries / detail |
|---|---|---|
| Recovered (red → green via the wrapper verify-recover loop) | 3 | CSV.swift, PromiseKit, ReSwift — loop fired, withdrew broken leaves, settled render compiles |
| Honest advancement (loop converged Swift-side; now red at the *next* stage, C# consumer compile) | 3 | CocoaMQTT (1 unit withdrawn), Eureka (5), Hero (10 — accessor-group HeroExtension properties) |
| Honest red (loop fail-closed, correct refusal) | 19 | SWIFTBIND111; cause tally across 23 occurrences: 12 InputConfiguration / 7 RequiresGraphClosure / 1 IterationCapExhausted / 1 NoProgress / 2 Unattributable |
| Degraded-green regressions (previously-green lib now shipping with silent withdrawals) | **0** | verified by same-bucket SWIFTBIND112 query |
| Regressions (green → red attributable to wave-1 code) | **0** | zero tolerance met |

Environmental drift excluded from the accounting (verified, not wave-1 code): 3 NU1101
dependency-feed flips (Macaw, MessageKit, YPImagePicker) + 8 convert-cache flips. XMLCoder
exit-1 pre-existing.

Loop-activation spot-check surfaced one report-honesty defect — withdrawal skip rows carried
`RecoveryStage: Emit` (where the row was *recorded*) instead of `SwiftCompile` (where the
failure *occurred*). Fixed red-first: `EmitterFaultRecord.WithdrawalDetailsPrefix` is the
origin signal, `SkipCauseClassifier.Classify(reason, details)` refines the stage, the linker
passes details. 2 new unit tests; unit 15,214/0; `--compile-only` exit 0; sim 3,242/0/0.

## Floors (ratcheted to verified actuals)

| Gate | Floor |
|---|---|
| Unit tests (`swift_bindings_unit_pass_floor`) | **15,214** (was 15,212; +2 from the fix above) |
| BindingTests sim (Mono JIT) | **3,242 / 0 / 0**, 37 named skips |
| BindingTests device (NativeAOT) | **3,255 / 0 / 0** (ratcheted previously at `acbfec67`) |
| Corpus baseline for wave 2 | **42/120** |

## Routing

- Umbrella `src/docs/binding-resilience-design.md` §8 → wave-1 **done and verified**, with
  the movement decomposed and 8 changed-decision bullets recorded.
- `src/docs/not-planned.md`: first natural loop firings recorded; the BindingTests
  resilience-fixture row's trigger **FIRED** (6 real emitted-but-broken libs) → routed to
  wave-2 session 01, no longer blocked.
- Nothing added to roadmap (policy only).

## Wave-2 session docs (`src/docs/sessions/2026-07-binding-resilience-wave2/`)

| Doc | One-line scope |
|---|---|
| `00-overview.md` | Program goal (Stages 4–7), wave-1 digest, run rules, gates, OD markers, out-of-run list |
| `01-withdrawal-triage-and-resilience-fixture.md` | Triage the 6 live withdrawal libs to root cause; land the BindingTests resilience fixture |
| `02-roslyn-probe.md` | In-process C# verification probe; parity-vs-MSBuild+SARIF decided by experiment |
| `03-csharp-verify-loop.md` | C# verify-recover loop on the wave-1 controller; reconciler retirement begins |
| `04-abi-callplan-dependency-capture.md` | Emitted-dependency capture + unit-identity actuators + `AbiCallPlan` foundation (the unblocking dependency) |
| `05-abi-callplan-validation.md` | Typed plan-vs-descriptor validation; text checkers demoted to defense-in-depth; violations become loop inputs |
| `06-settled-publication.md` | Staging + atomic promote gated on the 13 proof obligations; converged-outcome consumption; predicate precision |
| `07-fallback-and-optimization.md` | Bounded dependency-aware bisection; verification caching; gate-freeze policy |
| `08-final-soak-and-closeout.md` | Full soak vs 42/120, localized-construct ratchet, program close + release-resumption pointer |

## OWNER DECISIONS raised (surfaced in `00-overview.md`, not decided)

- **OD-W2-1** — protocol capability split (forward-view vs implementability) if it surfaces
  in generated public API shape (may fire in session 04).
- **OD-W2-2** — ship policy for strip-everything / no-wrapper-surface modules once the
  convergence predicate can distinguish them (session 06 implements up to the fork).
- **OD-W2-3** — disposition of the residual honest-red population at program close
  (session 08 assembles the evidence package).

## Review

Paired Codex + Grok round-1 review of the code change and the doc set: no High/Critical in
either scope; both verified the stage-attribution fix correct on the production path. Nine
Medium doc-hygiene findings (fixture-placement contradiction, gate-wording self-contradiction,
stale wave-map line, mis-cited/loosened prediction-gate freeze criterion, cause-tally 22→23
arithmetic, completeness-witness scoping for `Safe` verdicts, fixture-vs-predictive-skip
tension, one-directional disagreement invariant, ratchet keyed on originating not terminal
cause) — all fixed in the docs before this commit. Scope-A Lows (Details-prefix string
coupling; refined-stage cascade coverage) accepted by design: the prefix is the documented
sole origin signal and cascade inheritance shares the single attribution path already under
test.
