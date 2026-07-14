# Deep-Audit 2026-07 — Independent Verification ("audit of the audit")

**Date**: 2026-07-16
**Verifier**: Claude (Fable), orchestrating six parallel Opus verification agents — a different model family than the one that produced the audit.
**Method**: adversarial, read-only. Each agent read its assigned track/data-pack docs, then checked every claim against the actual code (Read/Grep + generated-artifact ground truth; no builds, no nuke targets). Every verdict below is backed by a `file:line` that an agent actually opened. Verification depth was concentrated on the items the backlog would promote (Tiers 0–2 plus the headline refuted-claims); see §10 for coverage limits.
**Repo state verified against**: `33541d45` (main).

> **Purpose (updated 2026-07-16, after owner review):** this doc is the **single source of truth for the follow-up fix program**. Owner directive: *no timeline, no scope constraints — fix every real issue found.* §3 is the authoritative worklist; a fresh orchestration session should execute it directly. The audit's own ranked queue (`synthesis/work-items-backlog.md`) is **superseded** by §3 — two of its top-six items are wrong (§4) and its tiering encoded release-window assumptions that no longer apply. §§4–6 hold the per-item evidence; §9 governs how to consume audit content not verified here.

---

## 1. Overall verdict

**The audit is high quality and its central thesis survives verification — but two of its top-six backlog items are wrong, and its headline severities are systematically inflated one notch relative to its own finding records.**

What holds up:

- **Zero hallucinated symbols.** Across ~40 verified claims spanning all six clusters, every cited file, symbol, and line region exists. Line citations repeatedly landed "on the nose" (verifiers' words). This is far better than typical AI-audit output.
- **The "cores are mature, ~2/5, no new live P0s" conclusion is real**, not asserted. The "≥20 P/Invoke pairs MATCH" table was re-derived from generated artifacts at symbol level and is genuine mechanical pairing. This **validates the roadmap's "input-poor, not bug-poor" posture** and the decision-record's "never another audit round" stop rule.
- The refuted-claims ledger is trustworthy: every "FIXED" tag spot-checked (Self substitution, AllocHGlobal antipattern, T:P&Q composition, inout cdecl writeback, `emittedRawKeys`, `default(CancellationToken)`) genuinely exists in code, most with pinning tests.
- The docs-drift findings are all confirmed — code is ahead of `constraints.md` and `roadmap.md` in exactly the places claimed (and two more, see §6).

What does not:

- **Tier-0 item #3 (DA-W5-A8-002, `public nonisolated` visibility drop) is REFUTED** — the audit's single named P1-candidate bug does not exist as a live defect (§4.1).
- **Tier-1 item #6 (G1-003, "compile-but-dead produce-throw APIs") is substantially already implemented** — produce-throw members are compile-poisoned with `[Obsolete(error: true)]` SB0006, which is *stronger* than the audit's own recommendation. The track doc contradicts the audit's own data-pack here (§4.2).
- Two "cheap delete" simplification rows are unsafe as written (S1-37; A1-C01's "low-risk delete" — both have live production callers).
- One test-honesty claim is backwards: `coverage-report.py` **is** a CI step in both workflows; the real defect is its weak fail criterion (§4.4).

Net: the audit's *strategic* reading (day-1 degradation policy, gate honesty, dual-oracle hygiene as the residual value) is sound. Its *ranked queue* needs the corrections below before anything is promoted.

---

## 2. Scoreboard

~40 claims verified across six clusters:

| Cluster (agent scope) | Confirmed | Partial / re-scoped | Refuted / wrong-as-written |
|---|---|---|---|
| A8 parser visibility | 1 (A8-003 subscripts) | 2 (A8-001, S1-05) | **1 (A8-002 — the Tier-0 headline)** |
| A1/A2 P/Invoke + layout | 12 (incl. all spot-checked refuted-claims) | 2 (A2-001 consequence; A1-C01 under-stated) | 1 factual error (Wrapper.swift retention) |
| G1/M2 degradation + packaging | 2 (051 default; mixed abort) | 3 (G1-003; G1-006; admission-stack order) | G1-003's "compile-but-dead" headline |
| T tests/gates | 4 (dead keys; tripwire; compile-only; skip culture) | 1 ("theater" label) | **1 (coverage-report.py "not in CI")** |
| W10/S1 simplification + drift | 7 (incl. S1-13, S1-06, all docs drift) | 1 (S1-01 re-scoped) | **1 (S1-37 — has 3 live callers)** |
| A6/A7/A3 generics + async + ARC | 14 (every checked finding) | 1 (A7-003 — verdict right, probe evidence wrong) | 0 |

Pattern: **finding records are reliable; track-level headlines inflate.** The audit's own methodology ("prefer under-claim") was followed at the finding level and violated at the synthesis level.

---

## 3. THE WORKLIST — every real issue (single source of truth)

> **Owner directive (2026-07-16):** no timeline, no scope constraints. The goal is the best possible tool — fix every real issue. This section is the complete, corrected list of everything the audit + verification established as real. **Headline count: 10 confirmed code defects (D1–D10), 7 gate/infrastructure defects (G1–G7), 2 intentional leaks to close out (L1–L2), 1 docs batch (DOC), 4 probe-first items (P1–P4), 1 completeness sweep (P5), 5 authorized refactors (R1–R5).** §3.6 (owner decisions) remains **NOT authorized** — discussion pending; do not start those. §3.7's refactors were owner-approved 2026-07-16; its "declined" subsection stays out of scope.
>
> **Execution rules for the orchestrating session** (binding, from CLAUDE.md + standing feedback):
> - Every fix ships with a test at the right layer; regression fixes go **red-fixture-first** (fixture, verify red, fix, green). BindingTests + unit pass counts ≥ baseline per commit. No assertion weakening, no "fix later".
> - Probe-first items must not be "fixed" until the probe settles what's true.
> - Generator/emitter changes: verify generated output compiles; run `nuke binding-tests --compile-only` + `--skip-regen` sim run; add `--device` where calling conventions/marshalling change (D2, D9, L2, P1 at minimum).
> - When fixing a bug pattern, grep for ALL instances before finishing (several items below are instances of the same dual-oracle pattern — check for siblings).
> - Item IDs below are stable — use them in commit messages, task tracking, and adversarial-review prompts.

### 3.1 Code defects — confirmed (10)

| ID | Defect | Evidence | Fix + required test |
|---|---|---|---|
| **D1** | **Subscript visibility never classified** (A8-003). `SubscriptDecl` has no `IsModuleInternal`/`IsSpiProtected`; `CreateSubscriptDecl` never classifies; `MemberValidationPipeline.cs:687–690` gates only on type-reach. An `@usableFromInline internal subscript` with all-public signature emits a C# indexer → loud wrapper-compile error. Corpus shapes exist (CryptoSwift, DifferenceKit) | `SubscriptDecl.cs`; `MemberValidationPipeline.cs:687–690` | Classify subscripts like methods/properties. Fixture: all-public-signature `@usableFromInline internal subscript(i: Int) -> Int` on a public type (existing `InternalTypeReach.swift:335` is caught only incidentally via its internal index type) |
| **D2** | **Optional-blind struct-flag computation** (A2-001). `CacluateFlags` doesn't unwrap Optional; `ClassifyFieldType` in the *same file* does. Corpus-swept: zero live victims, `Bool?` covered by EI-decline; **`Float?` on a small frozen struct is the genuine gap** — HasFloatFields fails open → potential lowering misclass (silent-corruption class) | `ModuleProcessor.cs:313–337` vs `:588–612` | **Derive flags from `ClassifyFieldType`** (kill the dual oracle) — do not hand-roll a second unwrapper. Fixture: primitive-signature *instance method* on a small frozen struct with a `Float?` field (the audit's `-> Self`/mutating fixture never reaches the bug); pair with the disasm probe if needed to demonstrate wrongness |
| **D3** | **SameType direct arm skips normalization** (A6-001). Ordinal compare false-rejects sugared `Data?` vs `Optional<Data>` → legal members silently skipped (undercount) | `ConcreteSpecializationEngine.cs:1424–1429` | Apply the existing `NormalizeTypeForComparison` on the direct arm (more-admitting; fail-closed direction preserved). Fix the misleading comment at `:1425–1427` in the same change. Unit case: sugared same-type constraint admitted |
| **D4** | **CSM parent-cancel surfaces as fault, not cancellation** (A7-001). Swift `CancellationError` on the CSM parent-only path → `TrySetException(SwiftException)`; ordinary path does it right | `AsyncGenericParent.cs:703–707` vs `AsyncHarnessEmitter.cs:1606–1615` | Local `error is CancellationError` pre-check inside the existing 2-param ABI (no wire change). BindingTests async-cancel case on the CSM parent-only path asserting canceled Task / OCE |
| **D5** | **Vtable fan-out predicate diverged from layout** (S1-02 / A5a-001 residual). `MethodEmitsVtableField` lacks the ctor/static/@objc-optional pre-skips and `HasUnsupportedObjCProtocolExistentialPosition` check that `VtableLayoutBuilder.ClassifyMethod` applies. **Live divergence**: nested @objc-protocol existential param → fan-out emits a branch reading a vtable field the struct never emits → Swift wrapper compile failure (fails closed, but kills the package) | `EveryProtocolEmitter.cs:5307` (predicate), `:1322–1323` (fan-out gate) vs `VtableLayout.cs:233–256` | Collapse to layout truth (`IncludedSlots`/`IncludesMethod`); delete the one-caller predicate and its false SSOT comment (`:5301–5305`, `:1318–1320`). Fixture: protocol method with nested @objc-protocol existential param |
| **D6** | **Consume-degraded proxy arm is silent** (G1-003 residual). A C#-authored conformer set into a setter/parameter silently never fires; only trace is a `KnownLimitation` row, invisible at `ReviewCount=0`. (Produce-throw needs nothing — SB0006 `error: true` already ships, §4.2) | `PInvokeEmitter.cs:637` + siblings; `SkipDisposition.cs:124` | Compile-time marking of the consume-degraded arm (mirror the SB0006 pattern) + make the disposition visible at `ReviewCount=0`. Unit assert on the emitted marker; fixture proving a degraded consume site is flagged |
| **D7** | **Cross-module extension P/Invokes can get the unpromoted symbol** (A1-C01, promoted from the audit's understatement). Decl-only `ComputeEntryPoint` overload returns the pre-promotion mangled name post-AF13; env overload uses `env.EmissionSymbol`. Three live callers | `PInvokeEmitter.cs:1140` vs `:1166`; callers `CrossModuleExtensionEmitter.cs:633/694/734` | **Probe-first**: build a cross-module extension member that hits `NeedsWrapperLib`; check whether the contract gate catches it at build time. Fix regardless: migrate the 3 call sites to the env overload, then remove/redirect the decl overload (this also correctly retires S1-37) |
| **D8** | **Named tuple + String → CS0029** (A2-006). Known generator bug where the *fixture was deleted instead of the bug fixed* (`SwiftString.Buffer` cannot convert to `SwiftString` in named-tuple context) | `BindingTests/Sources/SwiftBindingsTestLib/Tuples/Named.swift:23–24` (removal note) | Restore `makeNamedMixed()` red-first, then fix the conversion in named-tuple emission |
| **D9** | **Mixed-indirect generic tuple returns — wrong-ABI latent** (A2-004). `AllElementsAreBareGenericTypeParameter`'s own doc-comment concedes mixed bare-`T` + bound-generic tuples "produce a mixed indirect/direct ABI that this branch does not model". Emit-then-wrong-ABI — **the only wrong-ABI-class item in the entire audit** (L3 "Bad"; already-known/roadmap, no live emission site found) | `MarshallingHelpers.cs:529–542`; `TupleHandler.cs:103–105` | Max-case fixture first (mixed `(T, Array<T>)`-class returns), then either model the mixed ABI correctly or fail-closed skip the shape. `--device` leg required |
| **D10** | **AMGBE helper-name resolver bypass** (A7-002). Builds the helper-class reference from the bare module name; every other site goes through the shared resolver precisely so NamespacePattern remaps can't diverge | `AsyncMethodGenericBridgeEmitter.cs:1017` vs `AsyncHarnessEmitter.cs:1694–1695` | One-line fix (route through `GetFullyQualifiedHelperReference`) + remapped-namespace unit case |

### 3.2 Gate & test-infrastructure defects (7)

| ID | Defect | Fix |
|---|---|---|
| **G1** | **4 dead keys in `BindingTests/baselines.json`** (`generator_exit_code`, `must_pass_degraded`, `must_pass_compiled_out`, `known_unsupported_total`) — nothing reads them; only `wrapper_stripped_count` is enforced (`Build.WrapperStrip.cs:151–197`) | Delete (recommended, Q4) or re-derive from a fresh run + wire. Do not trust the stored 25/62 |
| **G2** | **`coverage-report.py` fail criterion too weak.** The CI step *exists* (`ci.yml:110–115`, `release.yml:205–210`) — the defect is that its only hard-fail is a must-pass feature with no test file; degraded/untested counts print as warnings and never compare against any baseline (data-pack `03:106`) | Add baseline comparison; promote the warnings to failures |
| **G3** | **Skip-surface gate never runs in CI** (T4-004). `RunSkipSurfaceGate()` sits behind `if (SkipSurface)` (`Build.BindingTests.cs:976–977`); `--skip-surface` appears nowhere in `.github/workflows/` — skip growth against `skip-surface-baseline.json` is ungated | Wire `--skip-surface` into CI |
| **G4** | **Mixed-ObjC abort predicate duplicated; tests test the copy production never calls** (§6.1). Production inlines at `BindingsGeneratorCommand.cs:844`; `ShouldAbortForFailedMixedObjC` (`:1826`) is exercised only by the 4 unit tests, and the two differ (`Module == null`) | Call the helper from `:844`; extend it to cover `Module == null`; tests then pin the live path |
| **G5** | **`EnsureGeneratorBuilt` staleness** (S1-24). A stale Debug generator binary can silently certify old output (this has bitten before). Current state **unverified** — check first | Verify, then add a freshness check (generator source newer than binary → rebuild or fail) |
| **G6** | **PartialSuccessKitchen fixture/gate missing** (G1-004). No gate proves "intentional unsupported shapes → exit 0 + clean compile + accurate report" | Build it as a binding-tests flag per standing doctrine; design ready in `data-pack/08` |
| **G7** | **MissingWrapperSymbol slack is warn-only.** Validation corpus sits at **64**, gated by a warn-only `+5` margin (`skip-metrics.py:174–182`) — growth never fails | Harden to a ratchet (fail on growth above baseline). Driving 64→0 is capability triage → §3.6 |

### 3.3 Intentional leaks to close out (2)

| ID | Leak | Status → action |
|---|---|---|
| **L1** | **Nested escaping inner-closure box** (A3-010). Generated Swift: `Unmanaged.passRetained(...)` box for escaping inner closures is intentionally never released — "no safe release point on this synchronous path" (`NestedClosureBridge.cs:733–745`, only non-escaping inners join `innerBoxesToRelease`). Not LifetimeTracker-gated, not documented as a limitation | Design a release protocol if one exists; otherwise document as a Known Limitation **and** add a leak-bound gate so the boundedness is asserted, not assumed |
| **L2** | **Move-arm `SwiftString` 16-byte payload buffer per callback** (C# runtime, distinct from L1). `MarshalCallbackArg<T>` Adopt/Move arm suppresses the wrapper's own buffer free along with the borrowed source (`SwiftMarshal.cs:1215–1240`). Previously deferred as design-gated; the `ConsumePayloadBuffer` DIM design exists | Execute the design. Runtime unit + BindingTests leak-bound coverage; `--device` leg (marshalling change) |

### 3.4 Docs & comment drift (one batch — DOC)

**(i) Repo docs/comments** (~9 spots): rewrite `constraints.md:16` (legacy-async CT paragraph — code fixed at `Receivers.cs:1356–1403` and fixtured; cited lines 1152/1169 stale; "unfixtured" claim now false) · rewrite roadmap **F8** (`roadmap.md:99`) to the fillability-null residual (layout = `IncludedSlots`, `Vtables.cs:45–46/:87–88`) · **verify F7** on the same line (likely stale via the same AF05 overhaul) · `EveryProtocolEmitter.cs:5301–5305` + `:1318–1320` false SSOT comment (dies with D5) · `ConcreteSpecializationEngine.cs:1425–1427` misleading comment (dies with D3) · `TypeSkipPrePass.cs:16–24` doc lists 2 of 4 conditions · stale inout-writeback text (roadmap + `KeyPathFoundation.swift`) · roadmap's "~188 UnsupportedClosure" vs validate's ~600 · add a `MemberCollectionWalker.swift` comment recording *why* `public nonisolated` is unreachable (swiftc canonicalizes modifier order in `.swiftinterface`) so no future audit rediscovers A8-002.

**(ii) Audit-doc corrections** (so future consumers aren't misled): mark DA-W5-A8-002 refuted; correct the G1-003 headline (SB0006 already ships; only consume-degraded arm survives); correct the S1-37 row (3 live callers); correct Track-T's "coverage not in CI" row; drop A1 §2's stale-Wrapper.swift caveat. Cross-link this doc from the audit README.

**(iii) Wiki**: document the `SwiftWrapperRequired=false` **exploration/triage ritual** (never a shipping configuration). Mechanically complete today: soft mode self-heals the native carrier (`NativePackagingPolicy.cs:87–91`); only `SBW_`/`SBSW_` P/Invokes hit `DllNotFoundException`.

### 3.5 Probe-first (4) + completeness sweep (1)

| ID | Probe | Then |
|---|---|---|
| **P1** | **Closure-error retain balance** (A7-011/A3-011). Unverifiable statically — run `TestReturnedThrowingClosureErrorLeakBounded` live and read the count | Fix if unbalanced; otherwise pin the balance with the live test |
| **P2** | **Unguarded composite-Target throw** (§6.4). `SwiftTypeName.FromModuleQualifiedName` at `ConcreteSpecializationEngine.cs:1384` has no try/catch; a `&`-containing unqualified Target = generator crash. Reachability unproven either way | Guard, or prove unreachable and comment why. D3's normalization change is the natural vehicle |
| **P3** | **`ClosureTests.cs:580/591` skip re-triage** (T2-001). The skip reason itself admits our generator gap ("no @_cdecl wrapper for existential-param closure setter, Tj dispatch SIGSEGV on Mono") — per house rule, crashes are ours until proven otherwise | Root-cause the existential-param closure-setter gap; device/NativeAOT re-triage; either fix or convert to an honest, runtime-scoped skip with the gap tracked as a capability item |
| **P4** | **Fourth async marshaller** (§6.3). `CrossModuleExtensionEmitter.Class.cs` is a self-contained async path: own ObjC/NSError error wire (`:1136–1160`), bare-TCS `GCHandle.Alloc` (`:1045`), **no CancellationToken support at all**. Re-run the GCHandle probe across *all* emitters (A7-003's probe missed this one) | Then decide: add cancellation support to this path (consistency with the other three) or document the asymmetry deliberately. Prerequisite for any S-A7-S05 action |
| **P5** | **Unverified-remainder sweep.** This verification deep-checked Tiers 0–2 + refuted-claims + G1/M2/T + 9/40 S1 rows + A6/A7/A3; tracks A5a/b/c, M3, W6, W9, M2 residuals and 31 S1 rows were only sampled. To honor "fix every real issue": one adversarial verification pass over the remainder, applying §9's discount rules; promote survivors into this worklist. Known candidates: W9 CreateAsync-parity CS0030, SB1002 property false-negative, M3 ObjCRooted reverse-setter passthrough, M2 stamp-before-success, A1-C03 SysV decline gates, W2 null-reverse-slot force-unwrap residual, cross-module empty skip sets | Survivors become new D/G items here |

### 3.6 Owner decisions — discuss before any action

- **Q1 wrapper-fail default**: keep the fail-closed default. The sound half of option B is *compile-time marking of wrapper-dependent members* without any default flip (moves failure from runtime to compile time — the house pattern). Decide whether to build it.
- **Q3 mixed-ObjC continue (G1-002)**: the abort is real and correctly cited (`BindingsGeneratorCommand.cs:844–852`), but the safe version is **"rebind as an honestly-labelled Swift-only artifact"** — metadata must not claim Mixed or it bypasses SWIFTBIND039. A real feature with real cost; build on demand.
- **MissingWrapperSymbol 64→0**: G7 hardens the gate; actually driving the corpus count down is capability triage (which members, why suppressed) — scope and decide separately.

### 3.7 Refactors — AUTHORIZED (R1–R5, owner-approved 2026-07-16)

Owner framing: no token/time budget; small-but-real benefit qualifies. Same execution rules as everything else; each item carries an acceptance gate. These are all instances of (or defenses against) the dual-oracle pattern that produced D2, D5, and G4.

| ID | Refactor | Why it's worth doing | Preconditions / acceptance gate |
|---|---|---|---|
| **R1** | **Delete dead `ProtocolProxyEmitter.Helpers.GetMethodKey`** (S1-13). Zero references (confirmed by fresh grep), and its key format is *weaker* than the live one — an active trap for any future caller who "reuses" it | Removes a collision-bug landmine | Re-grep for references immediately before deleting; unit suite green |
| **R2** | **Extract the byte-identical Optional Path-3/Branch-2 concrete-class fallback** (S1-06). `TypeProjectionFactory.cs:227–241` vs `:684–697` — guard chains literally identical; extraction precedented by `TryProjectObjCPrefixBridged` | Two copies of fallback logic = the drift shape that made D2 | Acceptance: zero diff in generated output across the BindingTests corpus |
| **R3** | **Extract the skip-condition *list*** (S1-01 re-scoped). The predicates are already shared functions; what's mirrored — and drifts — is the condition list across `TypeSkipPrePass` and its consumers | Kills the drift surface behind the TypeSkipPrePass doc rot (DOC batch) | **Precondition**: settle the third mirror first — `SilentTombstoneRegistrar.cs:88/:95` replicates conditions 1+2 but not 3+4, possibly deliberately (different job). If deliberate: document why and exclude it. Acceptance: zero output diff |
| **R4** | **Route `EmitGenericStaticDispatchMethod` through the shared `CdeclSignatureContract` phase loop** (A1-002). `MethodWrapperEmitter.cs:934+` hand-rebuilds `[ResultPtr][Arguments][Metadata][Self][ErrorOut]` with comments *citing* the contract it doesn't call; the normal path runs the contract's phase loop at `:381` | The highest-stakes drift surface in the R batch — a silent phase-order divergence here is an ABI bug | **Precondition**: pin the current GSF phase order with unit tests first (sampled pairs 14–16 currently match — capture that as assertions), then migrate. Acceptance: byte-identical generated output |
| **R5** | **Static collectors → PipelineContext** (S1-30, *conditional*). `ReportCollector` / `SwiftUIBridgeCollector` static mutable state → threaded context; also unblocks parallel xUnit without collection fixtures | Static mutable collectors are a real hazard class (cross-run bleed, ordering coupling), and parallel tests are a standing velocity win | **Precondition**: this row was NOT verified (outside the 9/40 verified S1 rows) — verify the shape during the P5 sweep first; proceed only if it holds as described. Behavior-preserving; acceptance: zero output diff + unit suite green (then enable parallelization separately) |

#### Declined (revisit only if the stated reason dies)

- **S1-05** VisibilityClassifier SSOT — zero remaining defect class behind it (A8-002 refuted, D1 fixed directly); the three oracles compose monotonically and *cannot disagree*, only over-suppress (`SwiftABIParser.cs:3131–3141`). Consolidating fail-closed suppression logic is pure regression risk with no behavior gain. Revisit only if a fourth visibility oracle is ever added.
- **S1-23** Sdk.targets split — no defect class behind it; churn in the most consumer-visible surface (the MSBuild import graph).
- **S1-25** mega-test migration — a bulk Contains→plan migration would destroy real regression pins (e.g. the per-module-metadata `DoesNotContain` at `ProtocolProxyEmitterTests.cs:97–98`). The safe version is already house policy (CLAUDE.md: assert behavior, not implementation) — new tests comply; migrate old ones opportunistically when touched anyway. No project needed.
- **S1-29** projection-only marshaler rearchitecture — "large behavior-preserving program" with no defect behind it; the audit itself defers it. Risk ≫ gain.

### 3.8 Do not do (refuted / superseded)

- **DA-W5-A8-002** (`public nonisolated`) — refuted (§4.1). The DOC batch adds the walker comment recording why.
- **S1-37** (`ComputeEntryPoint(MethodDecl)` "low-risk delete") — 3 live production callers; superseded by **D7**'s migrate-then-retire.
- **S1-05 as a prerequisite** for anything — see §3.7.
- **A8-008 and any remaining walker-shape finding** — do not act without first grepping the *generated-interface* corpus for the rejected shape (the A8-002 lesson; 0 corpus hits in either modifier order for A8-008).
- Everything on the audit's own rejected list (async-emitter merge, layout-from-skip-sets, Mono/AOT factory merge, softening 108/TN2435) — re-confirmed as correctly rejected.

---

## 4. The four wrong-as-written items (detail)

### 4.1 DA-W5-A8-002 — `public nonisolated` visibility drop: REFUTED

The mechanism is real (the `MemberCollectionWalker.swift:405–434` BroadPublic shape gates genuinely lack `nonisolated` in their allow-lists) — but it is **unreachable**. The gate is order-sensitive and `advanceToAccess` (`:346–353`) tolerates any modifiers *before* the access keyword, so `nonisolated public var` passes; only `public nonisolated var` fails. **swiftc canonicalizes modifier order in generated `.swiftinterface`, always emitting `nonisolated` before `public`.** Corpus sweep: 0 hits for the failing order across the entire iOS 26.2 SDK and all 1,675 repo `.swiftinterface` files, vs 2,850+ hits for the passing order.

The audit's own reachability evidence (`CustomGlobalActor.swift:38`, `public nonisolated var unownedExecutor`) is `.swift` **source** — the generated interface for that exact declaration reads `nonisolated public var`, is in `PublicMemberNames`, and is not dropped. The walker only ever consumes `.swiftinterface` (`SwiftSyntaxInterfaceFactsProducer.cs:76–82`). The audit validated a walker gate against source syntax it never sees.

The proposed allow-list widening is a no-op on real input and mildly erodes the negative-space gate's precision. Same caution applies to A8-008 (`public indirect enum`): 0 corpus hits in *either* modifier order — check the generated-interface corpus before spending anything on remaining walker-shape findings.

### 4.2 G1-003 — "produce-throw compile-but-dead": substantially already implemented

Produce-throw members are compile-poisoned: `EmitSuppressedProxyReadPoison` (`WrapperEmitter.cs:870–876`) emits `[Obsolete(…, true, DiagnosticId = "SB0006", UrlFormat = …)]`, injected for public methods at `:744–749`, with the accessor side-table (`:734–741` + `PropertyHandler.cs:1053–1058`) putting the marker on the public getter while leaving the private accessor unpoisoned so generated code still compiles. Same pattern in `EnumHandler.CaseInspection.cs`. The `NotSupportedException` body is a reflection backstop, not the primary signal. **Calling a produce-throw member is a compile error today** — strictly stronger than the audit's recommended `EditorBrowsable(Never)`.

Notably, the audit's own `data-pack/02-emit-then-break-inventory.md:38–58` documents SB0006 correctly and in detail; the G1 track narrative never mentions it. Track-vs-data-pack drift, not an evidence failure.

Two things in G1-003 survive: the **consume-degraded arm is genuinely silent** at compile time, and `SkipDisposition.cs:124` maps it to `KnownLimitation`, so `ReviewCount=0` hides it. That narrower finding is the actionable one.

### 4.3 S1-37 — "obsolete `ComputeEntryPoint(MethodDecl)`, byte-identical delete if unused": REFUTED

Three live production callers (`CrossModuleExtensionEmitter.cs:633, 694, 734`). Worse, the overloads genuinely diverge post-AF13 (§6.2). The row's do-not-do guard names the wrong risk entirely ("external tooling").

### 4.4 Track-T — "coverage matrix manual / not in CI": REFUTED

`coverage-report.py` runs as a named step in both `ci.yml:110–115` and `release.yml:205–210`, no `continue-on-error`, and its `sys.exit(1)` fails CI. The accurate finding — which data-pack 03 states correctly (`03:106`) — is that its *only* hard-fail condition is a must-pass feature with no test file at all; degraded/untested/known-unsupported counts print as warnings and never compare against `baselines.json`. The remediation is a baseline comparison + promoted failures, **not** wiring a CI step that already exists. (T4-004 — skip-surface gate never run in CI — is confirmed as filed.)

---

## 5. Headline severities, recalibrated

| Audit framing | Fair framing |
|---|---|
| "Mega unit tests are string theater / greenwash" (T1-001) | Metrics are **exact** (10,715 / 7,332 lines, Contains-counts within ±2), but sampling shows two populations: trivial format-string restatements *and* load-bearing regression pins (e.g. the per-module-EveryProtocol-metadata `DoesNotContain`, a real past-defect pin with no plan-level equivalent). "Brittle regression pins with poor cost/benefit and rewrite tax" — P2-for-velocity, not an honesty P1. Data-pack 15's own §2 wording was already right |
| G1-001 "UX contradiction: generator soft-fails, SDK re-hardens" (P1) | Factually exact (`Sdk.props:68`, `Program.cs:2257–2276`, `Sdk.targets:1979–1988`) — but this reads as the fail-closed policy *working as designed* (generator reports, SDK gates), not an accident. P1 defensible only as a day-1 product observation |
| G1-006 "MissingWrapperSymbol growth is tripwire-class" | The tripwire-at-0 exists only in BindingTests (and measures strip blocks). The validation corpus sits at **64**, gated by a warn-only `+5` slack margin (`skip-metrics.py:174–182`). The audit's aspiration is fine; the current-state description would mislead. The warn-only gap is itself the stronger finding |
| A8 track "risk 3/5, visibility dual-oracle" | With 002 dead and 001 mitigated (`EveryProtocolEmitter.cs:2314/:2319` deliberately check SPI only, unit-pinned), residual visibility risk is the P2 subscript gap. ~2/5 like everything else |

---

## 6. New defects found *during* verification (not in the audit)

These came out of the adversarial pass itself; none are urgent, all are cheap:

1. **Mixed-ObjC abort predicate is duplicated and its tests test the wrong copy.** The production abort at `BindingsGeneratorCommand.cs:844` *inlines* the predicate; `ShouldAbortForFailedMixedObjC` (`:1826–1827`) is called only by the four unit tests (`BindingsGeneratorCommandTests.cs:591–616`). The two differ (inline form also aborts on `Module == null` with exit 0; helper doesn't). Behavior is currently correct — the inline form is stricter — but it's exactly the dual-oracle class the audit flags elsewhere. One-line fix: call the helper from `:844`, extend it to cover `Module == null`.
2. **A1-C01 should be promoted, not deleted.** The decl-only `ComputeEntryPoint` overload (`PInvokeEmitter.cs:1140`) returns the **unpromoted** mangled name, while the env overload (`:1166`) uses `env.EmissionSymbol`. Since AF13 stopped mutating `decl.MangledName` during emission, a cross-module extension member hitting `NeedsWrapperLib` through the three `CrossModuleExtensionEmitter` call sites would get the pre-promotion symbol. Whether that shape is reachable is unproven (the contract gate plausibly catches it at build time, which would explain green fixtures) — worth a targeted probe, then migrating the 3 call sites to the env overload.
3. **A7's path map is missing an entire async marshaller.** `CrossModuleExtensionEmitter.Class.cs` is a self-contained fourth async path (own ObjC/NSError error wire at `:1136–1160`, own bare-TCS `GCHandle.Alloc` at `:1045`, **no CancellationToken support at all**). A7-003's probe ("only holder / CSM object[]") missed it. Its verdict survives for an unstated reason (this path can't feed the harness's `directTcs` arm), but for a track whose thesis is "these are distinct jobs, don't merge them," an unmapped job is the blind spot the thesis guards against. Re-run the GCHandle probe across *all* emitters before acting on S-A7-S05.
4. **Unguarded throw arm in composite-Target parsing.** If the digester ever emits a method-where Target containing `&` unqualified, `SwiftTypeName.FromModuleQualifiedName` throws with **no try/catch** at the `ConcreteSpecializationEngine.cs:1384` call site — a generator crash, worse than the false-reject A6-002 implies. Reachability unproven either way; noted so the S1-32 row is scoped correctly.
5. **A third skip-condition mirror the S1-01 row doesn't mention.** `SilentTombstoneRegistrar.cs:88/:95` replicates TypeSkipPrePass conditions 1+2 but not 3+4. Possibly deliberate (different job); must be checked before any S1-01 extraction. Also: S1-01's framing is off — the *predicates* are already shared functions; what's mirrored (and drifts) is the **condition list**.
6. **A1 §2 factual error**: `BindingTests/output/SwiftBindingsTestLib.Wrapper.swift` **is** retained on the iOS run (4.3 MB, present). The audit's "stale macOS evidence" caveat on its own pairing table can be dropped — its confidence was, if anything, conservative.

---

## 7. Strategic notes

- The audit **confirms** the signed decision record's premises rather than challenging them: a full multi-wave, ~13k-line audit of the entire surface found zero new emission-live P0s. The "input-poor, not bug-poor" thesis and D5's "never another audit round" stop rule are now empirically backed. **This should be the last broad internal audit**; the wall-assessment doc says the same, and it's right. (§3's P5 sweep is the *verification* of already-written audit material, not a new audit.)
- **Scope framing (owner directive, 2026-07-16)**: the fix program is quality-driven, not release-sliced. §3 is executed in full regardless of any release window. Severity context still matters for *ordering*: none of D1–D10 is a known live crash or data corruption — D9 is the only wrong-ABI-class item and has no live emission site; most items are undercounts, fail-open gates, or fail-closed-but-package-killing shapes. A sensible order: DOC → G1–G5 → R1/R2 (trivial, de-risk later items) → D1–D8/D10 (red-fixture-first each) → R3/R4 (after their preconditions; R4's pins before any D-item touches `MethodWrapperEmitter`) → D9 after its max-case fixture → L1/L2 → probes → P5 sweep → R5 (only if P5 confirms its shape).
- Release sequencing (contract-and-ship 06/07, device legs, MapLibre/FB) remains the owner's call and is neither gated on this worklist nor a gate for it.
- The strategic policy items (packaging default, mixed-ObjC continue) stay owner-decided (§3.6). One of the audit's three strategic asks turned out to be already solved (SB0006) — a caution against deciding the other two on audit evidence alone.

## 8. The audit's open questions — recommendations

| Q | Recommendation |
|---|---|
| **Q1** wrapper-fail default | **C now** (document the flag; §3 item 5). Defer the B-vs-A decision to post-0.18 feedback; if B is ever revisited, do compile-time marking of wrapper-dependent members *without* the default flip |
| **Q2** produce-throw surface | **Already answered by the codebase** — SB0006 `error: true` ships today and beats both proposed options. Remaining question is only the consume-degraded arm; recommend compile-time marking there (P2, not release-gating) |
| **Q3** mixed-ObjC continue | **B, opt-in, but scoped honestly**: it's "rebind as an honestly-labelled Swift-only artifact" (metadata must not claim Mixed), not a continue flag. Build on demand, not speculatively |
| **Q4** baselines.json | **B (delete)** — cheapest honest state. A only if the budgets are re-derived from a fresh run first. Either way, correct the Track-T claim: the CI step exists; the fail criterion is what's weak |
| **Q5** simplification push | **A now** (S1-13, S1-06, docs) — with the correction that S1-37 is *not* in the A bucket. B per §3.7's authorized R batch (S1-01 re-scoped to the condition list + third mirror; S1-02 is a real defect and moved to the worklist as **D5**). C: declined except S1-30, conditional as **R5** |
| **Q6** execute vs archive | **Execute §3 of this doc in full; archive the audit's own ranked queue as superseded.** Don't open the audit's four proposed streams — they presuppose the pre-verification priorities, two of which are gone. (Owner directive 2026-07-16: no scope constraints; fix every real issue) |

---

## 9. How to consume the *unverified* remainder of this audit

Systemic error patterns the verification exposed — apply these as discount rules to any row not verified here:

1. **Reachability was sometimes validated against Swift *source*, not swiftc-canonicalized `.swiftinterface`.** This killed the A8 headline. Before acting on any remaining walker/parser-shape finding, grep the actual generated-interface corpus for the *rejected* shape.
2. **Track narratives drift from their own data-packs** (G1-003 vs data-pack 02; Track-T vs data-pack 03). Where they disagree, **trust the data-pack** — every data-pack number checked (line counts, Contains-counts, key inventories) reproduced exactly.
3. **Headline severity runs one notch hot; finding records run honest.** Read the `Severity:`+`Status:`+`Reachability:` lines, not the executive prose.
4. **"Dead code / low-risk delete" rows need a fresh grep before acting** (S1-37 and the A1-C01 shape both had live callers). The confirmed-dead S1-13 shows the audit *can* get this right; it just isn't uniform.

## 10. Verification coverage and limits

- **Deep-verified**: everything the backlog would promote (Tiers 0–2), all Tier-3 latents, the refuted-claims ledger (sampled across all five sections), the G1/M2 factual spine, Track-T, 9 of 40 S1 rows (including all XS/S ranked ones), and A6/A7/A3's open findings.
- **Sampled only**: A5a/b/c, M3, W6, W9 tracks (verified indirectly via the refuted-claims and S1 rows that cite them — all consistent); data-pack taxonomies (09/10/13/14) and the file-coverage ledger were not re-counted.
- **Unverifiable statically** (flagged, not resolved): whether the A2-001 `SwiftSelf<Buffer>` shape actually corrupts (needs the disasm probe the audit itself proposes); A7-011/A3-011 closure-error retain balance (needs the live leak-bound test); A1-C03 SysV decline gates; A1-C01 reachability.
- No production code was modified; no builds or nuke targets were run by the verification.

**Raw agent reports** (full evidence trails) are preserved at `/Users/wojo/Dev/SB-Backup-Docs/2026-07-deep-audit-verification/report-{a8,a1a2,g1m2,tests,simpl,a6a7}.md` (out-of-repo backup, per the standing docs policy).
