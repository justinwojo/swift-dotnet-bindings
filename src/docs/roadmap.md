# Roadmap

This doc covers work we actually intend to do, confirmed-upstream blocked items, and hard scope boundaries. Acknowledged-but-not-planned items — trigger-gated latents, deferred designs, pending owner decisions — live in [`not-planned.md`](not-planned.md); route new leftovers there, not here. Live baseline counts live in `build/baselines/validation-baseline.json`; per-library status lives with each package.

> **Every skipped test is guilty until proven innocent.** There are exactly 5 confirmed upstream .NET runtime behaviours — see `Blocked` section below + memory `feedback_mono_jit_blame.md`. If a crash doesn't match one of these, it's our bug.

---

## Strategic posture (post-0.14)

Distilled from a now-retired `research-directions.md` discussion. Standing framing for "what's next" decisions; revisit a point only if a new signal contradicts it.

- **We are input-poor, not bug-poor.** Nearly every parked item (see `not-planned.md`) is "no active repro / not reached by any current corpus library." More internal bug-chasing and re-scanning the same validation libraries has sharply diminishing returns — it rediscovers known latents that already have "no emission site." The highest-value *remaining* bug source is **new real consumer inputs**, not internal sweeps. Don't launch another broad internal audit/refactor expecting a yield; lower the barrier to real inputs instead.
- **The thin ABI corners are now exercised and graded.** The ABI coverage grid (`src/docs/Design/abi-coverage-grid.md`, `BindingTests/abi-grid-manifest.json`, `nuke binding-tests --abi-grid`) manufactured the under-tested corners the real-library corpus structurally can't reach — closures, inout, tuples, constrained generics — and grades each cell on its declared+exercised runtimes (sim = Mono JIT, device = NativeAOT). Of the 57 cells, **46 are expect-green** (release-gating — confirmed safe on sim + device), **4 are supported-low-priority** (reported, not gated), and **7 are by-design-gray** (intentionally unsupported, each citing a roadmap rationale — e.g. `@autoclosure`, the async closure-param fan-out leg). So the expect-green latents are "confirmed safe," not "unknown"; the gray cells are documented non-goals, not gaps — don't read the grid as "all corners green." The grid is a standing artifact; its cells are ordinary BindingTests gated by the everyday pass-count, so no separate baseline is needed. Widen to a new corner (actors / PATs / composition) only if a real consumer report or validation red motivates it.
- **Two contracts, kept separate.** "Any xcframework becomes a usable binding" and "any Swift package becomes an xcframework" are related but distinct goals; conflating them makes failures unactionable. The **binding-generator contract**: given a valid, import-closed xcframework set, produce either a compiling full binding, a compiling degraded binding with every omitted surface and its reason recorded, or a non-publishable result with a structured explanation of the exact blocking surface — never an unsound binding, and never "success" when no usable module remains. Graceful skipping counts as success only when the retained surface compiles and stays ABI-safe. The **SPM conversion contract** (`spm-to-xcframework`): given a package, produce either a valid import-closed xcframework set for the selected products, or an atomic failure receipt naming the unsupported source / dependency / product / platform / toolchain constraint — an excellent refusal, not a fake success and not a permanent generic `convert_failed`. Corollary on measurement: a corpus compile sweep is an acquisition-and-compile signal, not proof any generated surface *runs*; every touched ABI mechanism gets a durable Swift shape under `BindingTests/Sources/SwiftBindingsTestLib/` plus the existing sim/device legs, and the corpus harness must not grow a second runtime-test system.
- **Async-emitter consolidation: investigated, not pursued.** The "merge the diverged async emitters" idea was audited and rejected. Reality is ~10 files / ~8,980 LOC, not 3; ~40% is genuinely-different jobs that must not merge (SwiftUI async-View, AsyncStream, AsyncSequence, async-closure inversion, the CSM generic-parent 2-param error ABI), and most remaining divergence is *intentional* — a naive merge would introduce a new ABI bug against working marshalling code. The divergence bugs are real (≥7 in 12 months) but were caught by *new input shapes reaching the path*, never by structure review — reinforcing the input-poor thesis. Only survivor is optional Tier-1 exact-duplicate extraction (`BuildMethodOwnGenericParams` ×2, the `SBW_CancelTask`/`SBW_Free` P/Invoke blocks, the Swift catch-body builder); everything above that is not worth the risk. Don't re-open without a new motivating signal.

---

## Prediction-gate freeze policy (hard policy boundary)

A standing rule for anyone (contributor or agent) tempted to add a new hand-coded *prediction gate* — an emission-time predicate that pre-screens a member/shape to head off a downstream failure (the `SkipReason.*` / `MemberValidationPipeline` / `WrapperValidation` family). This is policy, not a to-do; it constrains what we *add*, and it is the settled disposition of the binding-resilience program's division-of-labor question (full rationale: `Design/binding-resilience-design.md` §2, "The prediction/verification division of labor").

The line: **compiler success cannot prove ABI correctness.** swiftc and Roslyn are syntax / type-system / linkage oracles; neither proves the two sides agree on calling convention, register class, ownership, field offsets, or witness-table width. That splits every candidate gate cleanly:

- **Freeze growth** of gates whose only job is predicting a *compile error*. The verify-recover loop (`WrapperRecoveryController` / `InEmissionDriver`, both Swift and C# planes) is their general backstop — it renders, compiles, attributes the failure to a droppable culprit, withdraws it, and re-renders. Existing compile-error predictors stay **only as fast-path optimizations** (they save a loop round), never as the soundness boundary. Cost, frequency, or nicer diagnostics do **not** justify a new one — that is what the loop is for.
- **Keep hand-writing** gates for *soundness* conditions the compilers cannot see — ABI mismatch, indeterminate layout, register-convention violations, ownership/witness-table shape. No compiler backstop can replace these; a wrong one compiles clean and fails (or corrupts) at runtime, the one unacceptable outcome.

**Criterion (Fable): a new prediction gate is justified iff the failure it prevents would _compile_.** If the compiler would catch it, let the loop handle it. If it would compile-clean and only break at runtime, the gate is a soundness gate and is warranted. Apply this test before adding any new emission-time skip/validation predicate.

---

## Surface loss is a distinct failure mode (hard policy boundary)

Standing framing, extracted from the 0.18.0 regression (16 red corpus cells behind 169 commits of green gates, including one published release). Two rules, both about what our gates structurally cannot see.

**The defect class: a fail-closed absence rule whose authority is incomplete for the binding's real reference closure.** "Authority X has no record of this type ⇒ withdraw it" is sound only when X sees every type the emitted binding can name. Every defect in that wave was one instance of the same asymmetry — *a plane that names types without consulting the plane that deletes them* (withdrawal vs the SwiftUI bridge; plan vs emit bookkeeping; a stale module database vs the emitted reference set). So when adding or widening an absence rule, the question is not "is the rule right?" but "which planes name types this authority never sees?" — and the answer belongs in the same commit as the rule.

**The gates are directional: they ask whether what we emit compiles, never whether we stopped emitting.** `nuke validate` structurally cannot catch surface shrink (a cell with fewer members still compiles); `--skip-surface` logs a vanished skip marker as an improvement. The one gate that *is* bidirectional is narrow: the API-manifest ratchet is additive-tolerant for additions but **fails** on a removed member (`build/Build.ApiManifestGate.cs`), and it sees only the BindingTests corpus it baselines — it says nothing about the surface of a third-party binding. Corollary: **a program that trades surface for compilability needs a surface meter, and the meter has to exist before the program starts.** Related, from the same wave's pure fixture-coverage misses: a fixture accompanying a change to *which shapes reach an emission path* must enumerate parent kinds (class/struct/enum × member/ctor), member asyncness/wrapper mode, and module locality (local/cross-module) — one shape proves the path, not the reach.

**A green gate matrix is necessary, not sufficient.** Green in-repo gates say the shapes we chose to model still work. They say nothing about third-party inputs, and they cannot report surface that quietly stopped being emitted.

---

## Pending agreed work

Small leftovers we already committed to, not a new program.

| Item | Notes |
|------|-------|
| **Document the `net10.0-*` floor (issue #45)** | The requester went looking for the .NET-version constraint in MSBuild and could not find it. State `net10.0-*` only as policy on the repo README requirements line (and FAQ / Platform Compatibility). The floor lives in the generator's hardcoded TFMs (`PlatformInfoFactory.cs`), not in the SDK targets. The net9 lane itself stays declined: no ABI blocker, but a supported lane doubles the Mono/NativeAOT matrix for a runtime that leaves support 2026-11-10. |
| **SWIFTBIND010 condition vs message (issue #45)** | `Sdk.targets` error *message* names the four `net10.0-*` TFMs, but the *condition* only checks `_SwiftBindingPlatformUnsupported` (platform-substring match — `net9.0-ios` passes). A net9 project sails past 010 and fails later on the generator's hardcoded `net10.0-ios` output TFM. Make the condition also gate on the .NET version, or reword the message to match the condition — either way the first error a declined user hits must name the net10 floor. |

---

## Demand-driven capability backlog

Real capability gaps with an active incremental trajectory — closed shape-by-shape as consumer demand or validation signal arrives, not scheduled as sessions.

| Item | Notes |
|------|-------|
| **UnsupportedClosure remaining shapes** | ~609 skips (per the validation baseline `build/baselines/validation-baseline.json` `skip_metrics`; re-read the baseline for the current figure. The earlier "~188" was a stale undercount.) Already reduced via setter-only closure properties and the async-closure bridge (throwing 0–4 args — `ClosureHandler.MaxAsyncThrowingClosureArity` — with primitive or `Swift.String` returns, plus a zero-arg `Foundation.Data` return; non-throwing 0–4 args with primitive returns only). Remaining are generic params, nested closures, and async-closure shapes outside the supported arg/return matrix (e.g., arg-bearing `Data` returns, non-throwing `Data` returns). One carve-out inside "nested closures": a root, non-failable, non-generic, non-ObjC-rooted, non-isolated class initializer with no defaulted parameters and *sync* nested closures now binds as a real C# constructor via `NestedClosureBridge`. Failable, throwing, async, struct and enum nested-closure constructors, and every `MethodClosureBridge` constructor, are still refused — see `not-planned.md` § Emitter — closures & async. |
| **Cross-module dependency-graph closure (deep-stack ecosystems)** | The 2026-07 fresh-conversion corpus soak (50/120 honest-green) located the remaining corpus green-count leverage in **dependency-graph closure, not further emitter hardening**: convert-stage failures + cross-module fact resolution + named-missing-input dependency closures together are ~65% of reds, concentrated in deep-stack multi-module ecosystems (TCA / `swift-*` packages) where a primary needs many in-run sibling bindings resolved before it compiles. Incremental trajectory (not scheduled; closed shape-by-shape as a consumer names a specific ecosystem): strengthen the converter's import-closure production (internal-target product synthesis — see `not-planned.md` § Ingestion) and the generator's cross-module fact resolution so a multi-module package closes its sibling graph. Consistent with the input-poor posture: this is a real demand-gated trajectory, not a broad internal sweep. |

---

## Blocked (Confirmed Upstream Only)

These are the **only** confirmed upstream issues. There are exactly 5 — issues 1–4 are reproduced in the standalone `swift-interop-repro` sibling repo; issue 5 is proven upstream via a pure-managed A/B substitution inside BindingTests (standalone reduction still pending, owner files it). If a crash doesn't match one of these, it's our bug. See `feedback_mono_jit_blame.md` for the full investigation checklist.

| Filing | Issue | Blocked By |
|--------|-------|-----------|
| 1 | **Mono: JIT assertion `!ji->async` on CallConvSwift P/Invoke** | Fatal `jit-info.c:918` during stack unwinding through a `wrapper_managed_to_native_*` frame after a native crash in a `CallConvSwift` callee. Workaround: `@_silgen_name` Swift wrappers / avoid native crashes through `CallConvSwift` |
| 2 | **Non-blittable type rejection with CallConvSwift** | .NET runtime design limitation. Workaround: `@_cdecl` wrappers (~67% of P/Invokes require them — see `Future/upstream-issue-02-non-blittable-callconvswift.md`) |
| 3 | **Mono: `Cannot transition thread from STARTING with DONE_BLOCKING` on `(Bool, @out via x0)` tuple-return CallConvSwift** | Specific to `Set<T>.insert` ABI shape. `Set.contains` (no `@out`) passes. Workaround: `@_cdecl` Swift wrapper |
| 4 | **Mono: Mac Catalyst x64 instability** | Mono-JIT instability specific to the Mac Catalyst x64 runtime. See `Future/upstream-issue-04-mono-catalyst-x64-instability.md`. Workaround: Mono interpreter is the default for `--catalyst-x64` (`[SkipOnCatalystX64]` retained as escape hatch, no call sites) |
| 5 | **Mono (ios-sim arm64): exception-unwinder PAC fault on canceled shared-ref-generic `Task<T>`** | `EXC_BAD_ACCESS` in `mono_arch_unwind_frame` throwing `OperationCanceledException` out of a canceled `Task<T>` (reference-type `T`) on a UIKit sync-context continuation; zero Swift/P-Invoke frames on the faulting stack. See `Future/upstream-issue-05-mono-unwinder-oce-pac.md`. Workaround: `[SkipOnMonoJit]` on the two affected cancellation tests |
| comment | **Mono: SafeHandle async lifetime** (tracking-issue comment, no standalone filing) | GC may collect SafeHandle during async suspension. Workaround: manual ARC retain/release or singleton pattern |

| Other | Status |
|-------|--------|
| **Non-Int32 enum raw values** | Blocked on Swift compiler: `.swiftinterface` strips integer raw values. No workaround. 1 skipped test. |

---

## Not Worth Addressing

Counts are from `build/baselines/validation-baseline.json` `skip_metrics.skip_reasons` (`git_sha` `9772e256`) — re-read the baseline for current figures rather than trusting this table.

| Skip Reason | Count | Why Not |
|-------------|------:|---------|
| @_spi / internal members (`ModuleInternal`) | ~1095 | Correct behavior — private API should not be bound |
| Synthesized Codable | ~971 | .NET consumers use own serialization (`System.Text.Json`, etc.) |
| AnyTypeFallback (`Any`, `[Any]`, `Optional<Any>`, ObjC delegate protocols, PAT subscripts) | ~418 | PAT classification + by-design Swift `Any` + ObjC protocols + cross-library — fully architecturally-deferred. In-scope single-module gaps measure 0 hits. |
| Unsupported signatures (associated types, bare generics) | ~1418 | Swift patterns with no C# equivalent |
| Generic protocol constraints / PATs | ~394 | Architecturally blocked by associated type erasure |
| SwiftUI/Combine dependencies | ~178 | Framework boundary — consumers use SwiftUI bridge instead (`SwiftUIConstraint` + `SwiftUIView`) |
| Unsupported existential (opaque generics) | ~101 | Fundamental limitation of Swift's type system vs C# generics |
| UnsatisfiedGenericConstraint (remaining) | ~263 | Fundamental type system constraints, not relaxable gates |

---

## Long-term: retire `nuke validate`

`nuke validate` exists because we needed a quick "are these libraries still
working?" sanity sweep while the generator was changing rapidly. The long-term
goal has always been to make BindingTests the durable, sole gate. The 0.10.0
release cycle landed the first concrete investment toward that — the Layer B
`--skip-surface` ratchet over the BindingTests corpus, tightened in-bundle as
fixes ship.

**Retirement criterion**: validate is officially decorative when a full `nuke
validate` run surfaces no bug that BindingTests didn't already catch *across
multiple consecutive scheduled sweeps*. We're not close to that yet — the
audits behind the 0.10.0 plan ran against real third-party libraries and found
patterns BindingTests had no coverage for.

**Migration path**:

- **Each skip-class fix** lands a minimized Swift pattern in the BindingTests
  corpus (`BindingTests/Sources/SwiftBindingsTestLib/`), whose generated output
  is what the `--skip-surface` ratchet scans. Each shape-class fix lands Layer A
  coverage instead (Swift repro + C# assertion + generator unit test).
- **Future audit findings** route by class: skip-class drops a new minimized
  Swift pattern into the corpus as the first step of triage; shape-class adds
  Layer A coverage to the appropriate domain test class. In both cases the
  regression test lands as part of triage, not after the fix.
- **Validate's role narrows progressively**:
  - Today: targeted per-bundle gate where the bug was found only in real
    libraries (Bundles 8 and 9), discovery sweep pre-release.
  - Next minor cycles: as the corpus matures, drop targeted-validate gates
    bundle-by-bundle when the domain is provably covered by BindingTests.
  - Eventually: validate runs as scheduled discovery only, no merge gates.
- **Retirement happens** when validate stops surfacing surprises that
  BindingTests didn't catch across, say, three consecutive scheduled sweeps.
  Until then, validate stays in scope as a discovery sweep, not a blanket
  per-bundle blocker.

---

## Explicitly Out of Scope

| Item | Reason |
|------|--------|
| Full Swift type graph infrastructure | Over-engineered for current needs |
| Deep generic signature / associated type constraint emission | C# generics can't express Swift's full type system |
| Result builder (`@resultBuilder`) projection | Compile-time Swift feature, no ABI JSON representation |
| Broad `@dynamicMemberLookup` reification (beyond targeted Apple Supplement shims) | Affects <5 types across 66 validation libraries. `AttributedString` is covered narrowly via a hand-rolled partial layered on the Apple Supplement xcframework (Session 7 — `LanguageIdentifier` shipped; `link`/`foregroundColor`/etc. follow the same shape on demand). The general "walk every `@dynamicMemberLookup` host and reify per-key C# properties" pass is still out of scope. |
| Composing SwiftUI view trees from C# | Result builders are a compiler feature |
| Structs projected as C# value types | Only safe for frozen+blittable subset; marginal benefit |

### Apple-framework by-design limits (won't fix)

Framework-specific consequences of the result-builder / source-gen / PAT boundaries above — closing any of these is a different product or contradicts the framework's own design. The Apple-framework gap-fix campaign closed in 0.12.0 and the residual Tier 2 surface shipped in 0.14.0 (RC-AOT typed mesh buffers `68e984ae`, CryptoKit/HPKE construction `7cfa4950`, witness-getter callback path Option A, sibling emission-marker re-keying `ad6c8d27`); these are the consciously-parked limits that outlived it.

| Item | Reason / workaround |
|------|------|
| **AppIntents `perform()` / authoring, ActivityKit Live Activities** (RC-STRUCTURAL) | Need a C#→Swift source-gen + macro-expansion subsystem (a different product). Both on the `swift-dotnet-packages` do-not-ship list. |
| **WeatherKit statistics/summaries + `weather(for:including:)`** | 6-way method-own-generic `async` tuple return exceeds the CSM cartesian cap. Full-bundle `WeatherAsync` is the workaround. |
| **TipKit result-builder DSL** (RC-AEIC) | Entrypoints are shimmable but the authoring experience is not restorable from C# — the same `@resultBuilder` wall as the rows above. |
| **RC-SB0003 reverse witness dispatch** | Case-by-case; many are by-design Swift limits. The forward (C#-implements) path works and is the supported mechanism. |
| **`@autoclosure`** (RC-CLOSURE) | No shipping-framework consumer; revisit only if one needs it. |
| **App-defined PAT conformers** (RC-PAT; e.g. ProximityReader `requestDocument`) | CSM only works for Apple-finite conformer sets; app-defined conformers are source-gen territory. |
| **RealityKit detached-setter `willSet` trap** (RC-WILLSET) | Framework `willSet` precondition; no ABI route bypasses a Swift property observer, so nothing is generator-fixable here — the setter must be called on an attached entity per the framework's own contract. |
| **General `Measurement<T>` value-only projection** | Foundation type behavior, not a binding defect. The targeted `Measurement<T>(double, T)` ctor (WorkoutKit range alerts) is a deliberate narrow surface, not a general round-trippable `Measurement<T>`. |
| **BlinkIDUX raw `events` stream** (RC-PAT; third-party) | `BlinkIDAnalyzer.events` getter stays a produce-throw (compile-poisoned SB0006): its root proxy `EventStreamProxy` is blocked by a PAT (`associatedtype Event`), which is not forward-safe, so it correctly stays fail-closed. Supported path: the concrete `BlinkIDEventStream.Stream` `IAsyncEnumerable`, which works. |
| **Authoring brand-new C# conformers behind forward-only proxies** (RC-SB0003 instance) | After the EveryProtocol proxy rescue, RealityFoundation `IMaterial`/`ISynchronizationService` project through forward-only proxies with no reverse-dispatch impl ctor — writing a from-scratch C# conformer and packing it into the framework stays unsupported. Consuming and round-tripping framework-produced instances works. |
