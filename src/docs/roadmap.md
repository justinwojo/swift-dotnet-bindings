# Roadmap

This doc covers work we actually intend to do, confirmed-upstream blocked items, and hard scope boundaries. Acknowledged-but-not-planned items — trigger-gated latents, deferred designs, pending owner decisions — live in [`not-planned.md`](not-planned.md); route new leftovers there, not here. Live baseline counts live in `build/baselines/validation-baseline.json`; per-library status lives with each package.

> **Every skipped test is guilty until proven innocent.** There are exactly 4 confirmed upstream .NET runtime behaviours — see `Blocked` section below + memory `feedback_mono_jit_blame.md`. If a crash doesn't match one of these, it's our bug.

---

## Strategic posture (post-0.14)

Distilled from a now-retired `research-directions.md` discussion. Standing framing for "what's next" decisions; revisit a point only if a new signal contradicts it.

- **We are input-poor, not bug-poor.** Nearly every parked item (see `not-planned.md`) is "no active repro / not reached by any current corpus library." More internal bug-chasing and re-scanning the same validation libraries has sharply diminishing returns — it rediscovers known latents that already have "no emission site." The highest-value *remaining* bug source is **new real consumer inputs**, not internal sweeps. Don't launch another broad internal audit/refactor expecting a yield; lower the barrier to real inputs instead.
- **The thin ABI corners are now exercised and graded.** The ABI coverage grid (`src/docs/Design/abi-coverage-grid.md`, `BindingTests/abi-grid-manifest.json`, `nuke binding-tests --abi-grid`) manufactured the under-tested corners the real-library corpus structurally can't reach — closures, inout, tuples, constrained generics — and grades each cell on its declared+exercised runtimes (sim = Mono JIT, device = NativeAOT). Of the 52 cells, **41 are expect-green** (release-gating — confirmed safe on sim + device), **4 are supported-low-priority** (reported, not gated), and **7 are by-design-gray** (intentionally unsupported, each citing a roadmap rationale — e.g. `@autoclosure`, the async closure-param fan-out leg). So the expect-green latents are "confirmed safe," not "unknown"; the gray cells are documented non-goals, not gaps — don't read the grid as "all corners green." The grid is a standing artifact; its cells are ordinary BindingTests gated by the everyday pass-count, so no separate baseline is needed. Widen to a new corner (actors / PATs / composition) only if a real consumer report or validation red motivates it.
- **Async-emitter consolidation: investigated, not pursued.** The "merge the diverged async emitters" idea was audited and rejected. Reality is ~10 files / ~8,980 LOC, not 3; ~40% is genuinely-different jobs that must not merge (SwiftUI async-View, AsyncStream, AsyncSequence, async-closure inversion, the CSM generic-parent 2-param error ABI), and most remaining divergence is *intentional* — a naive merge would introduce a new ABI bug against working marshalling code. The divergence bugs are real (≥7 in 12 months) but were caught by *new input shapes reaching the path*, never by structure review — reinforcing the input-poor thesis. Only survivor is optional Tier-1 exact-duplicate extraction (`BuildMethodOwnGenericParams` ×2, the `SBW_CancelTask`/`SBW_Free` P/Invoke blocks, the Swift catch-body builder); everything above that is not worth the risk. Don't re-open without a new motivating signal.

---

## Prediction-gate freeze policy (hard policy boundary)

A standing rule for anyone (contributor or agent) tempted to add a new hand-coded *prediction gate* — an emission-time predicate that pre-screens a member/shape to head off a downstream failure (the `SkipReason.*` / `MemberValidationPipeline` / `WrapperValidation` family). This is policy, not a to-do; it constrains what we *add*, and it is the settled disposition of the binding-resilience program's division-of-labor question (full rationale: `binding-resilience-design.md` §2, "The prediction/verification division of labor").

The line: **compiler success cannot prove ABI correctness.** swiftc and Roslyn are syntax / type-system / linkage oracles; neither proves the two sides agree on calling convention, register class, ownership, field offsets, or witness-table width. That splits every candidate gate cleanly:

- **Freeze growth** of gates whose only job is predicting a *compile error*. The verify-recover loop (`WrapperRecoveryController` / `InEmissionDriver`, both Swift and C# planes) is their general backstop — it renders, compiles, attributes the failure to a droppable culprit, withdraws it, and re-renders. Existing compile-error predictors stay **only as fast-path optimizations** (they save a loop round), never as the soundness boundary. Cost, frequency, or nicer diagnostics do **not** justify a new one — that is what the loop is for.
- **Keep hand-writing** gates for *soundness* conditions the compilers cannot see — ABI mismatch, indeterminate layout, register-convention violations, ownership/witness-table shape. No compiler backstop can replace these; a wrong one compiles clean and fails (or corrupts) at runtime, the one unacceptable outcome.

**Criterion (Fable): a new prediction gate is justified iff the failure it prevents would _compile_.** If the compiler would catch it, let the loop handle it. If it would compile-clean and only break at runtime, the gate is a soundness gate and is warranted. Apply this test before adding any new emission-time skip/validation predicate.

---

## Demand-driven capability backlog

Real capability gaps with an active incremental trajectory — closed shape-by-shape as consumer demand or validation signal arrives, not scheduled as sessions.

| Item | Notes |
|------|-------|
| **UnsupportedClosure remaining shapes** | ~600 skips (approximate, per the validation baseline `build/baselines/validation-baseline.json` `skip_metrics` — the #5 skip reason at ~8.2% of skipped members; re-read the baseline for the current figure. The earlier "~188" was a stale undercount.) Already reduced via setter-only closure properties and the async-closure bridge (throwing 0–3 args with primitive returns plus zero-arg `Foundation.Data` return; non-throwing 0–3 args with primitive returns only). Remaining are generic params, nested closures, and async-closure shapes outside the supported arg/return matrix (e.g., arg-bearing `Data` returns, non-throwing `Data` returns). |

---

## Blocked (Confirmed Upstream Only)

These are the **only** confirmed upstream issues. There are exactly 4 (reproduced in standalone repro at `/Users/wojo/Dev/swift-interop-repro/`). If a crash doesn't match one of these, it's our bug. See `feedback_mono_jit_blame.md` for the full investigation checklist.

| Filing | Issue | Blocked By |
|--------|-------|-----------|
| 1 | **Mono: JIT assertion `!ji->async` on CallConvSwift P/Invoke** | Fatal `jit-info.c:918` during stack unwinding through a `wrapper_managed_to_native_*` frame after a native crash in a `CallConvSwift` callee. Workaround: `@_silgen_name` Swift wrappers / avoid native crashes through `CallConvSwift` |
| 2 | **Non-blittable type rejection with CallConvSwift** | .NET runtime design limitation. Workaround: `@_cdecl` wrappers (already covers 78.5% of P/Invokes) |
| 3 | **Mono: `Cannot transition thread from STARTING with DONE_BLOCKING` on `(Bool, @out via x0)` tuple-return CallConvSwift** | Specific to `Set<T>.insert` ABI shape. `Set.contains` (no `@out`) passes. Workaround: `@_cdecl` Swift wrapper |
| 4 | **Mono: Mac Catalyst x64 instability** | Mono-JIT instability specific to the Mac Catalyst x64 runtime. See `Future/upstream-issue-04-mono-catalyst-x64-instability.md`. Workaround: `[SkipOnCatalystX64]` on affected tests |
| comment | **Mono: SafeHandle async lifetime** (tracking-issue comment, no standalone filing) | GC may collect SafeHandle during async suspension. Workaround: manual ARC retain/release or singleton pattern |

| Other | Status |
|-------|--------|
| **Non-Int32 enum raw values** | Blocked on Swift compiler: `.swiftinterface` strips integer raw values. No workaround. 1 skipped test. |

---

## Not Worth Addressing

| Skip Reason | Count | Why Not |
|-------------|------:|---------|
| @_spi / internal members | ~750 | Correct behavior — private API should not be bound |
| Synthesized Codable | ~730 | .NET consumers use own serialization (`System.Text.Json`, etc.) |
| AnyTypeFallback (`Any`, `[Any]`, `Optional<Any>`, ObjC delegate protocols, PAT subscripts) | ~614 | PAT classification + by-design Swift `Any` + ObjC protocols + cross-library — fully architecturally-deferred. In-scope single-module gaps measure 0 hits. |
| Unsupported signatures (associated types, bare generics) | ~611 | Swift patterns with no C# equivalent |
| Generic protocol constraints / PATs | ~453 | Architecturally blocked by associated type erasure |
| SwiftUI/Combine dependencies | ~181 | Framework boundary — consumers use SwiftUI bridge instead (`SwiftUIConstraint` + `SwiftUIView`) |
| Unsupported existential (opaque generics) | ~90 | Fundamental limitation of Swift's type system vs C# generics |
| UnsatisfiedGenericConstraint (remaining) | ~76 | Fundamental type system constraints, not relaxable gates |

---

## Long-term: retire `nuke validate`

`nuke validate` exists because we needed a quick "are these libraries still
working?" sanity sweep while the generator was changing rapidly. The long-term
goal has always been to make BindingTests the durable, sole gate. The 0.10.0
release cycle is the first concrete investment toward that — it lands the
`BindingTests/Sources/SurfaceArea/` corpus + Layer B `--skip-surface` ratchet
(scaffolding) and seeds skip-class snippets in-bundle as fixes ship.

**Retirement criterion**: validate is officially decorative when a full `nuke
validate` run surfaces no bug that BindingTests + SurfaceArea didn't already
catch *across multiple consecutive scheduled sweeps*. We're not close to that
yet — the audits behind the 0.10.0 plan ran against real third-party libraries
and found patterns BindingTests had no coverage for.

**Migration path**:

- **Each skip-class fix** lands a minimized Swift pattern in
  `BindingTests/Sources/SurfaceArea/`. Each shape-class fix lands Layer A
  coverage instead (Swift repro + C# assertion + generator unit test).
- **Future audit findings** route by class: skip-class drops a new
  `SurfaceArea/` snippet as the first step of triage; shape-class adds Layer
  A coverage to the appropriate domain test class. In both cases the
  regression test lands as part of triage, not after the fix.
- **Validate's role narrows progressively**:
  - Today: targeted per-bundle gate where the bug was found only in real
    libraries (Bundles 8 and 9), discovery sweep pre-release.
  - Next minor cycles: as SurfaceArea matures, drop targeted-validate gates
    bundle-by-bundle when the domain is provably covered by SurfaceArea.
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
| Broad `@dynamicMemberLookup` reification (beyond targeted Apple Supplement shims) | Affects <5 types across 53 validation libraries. `AttributedString` is covered narrowly via a hand-rolled partial layered on the Apple Supplement xcframework (Session 7 — `LanguageIdentifier` shipped; `link`/`foregroundColor`/etc. follow the same shape on demand). The general "walk every `@dynamicMemberLookup` host and reify per-key C# properties" pass is still out of scope. |
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
| **RealityKit detached-setter `willSet` trap** (RC-WILLSET) | Framework `willSet` precondition; no ABI route bypasses a Swift property observer. A best-effort preflight guard + doc note shipped; nothing more is generator-fixable. |
| **General `Measurement<T>` value-only projection** | Foundation type behavior, not a binding defect. The targeted `Measurement<T>(double, T)` ctor (WorkoutKit range alerts) is a deliberate narrow surface, not a general round-trippable `Measurement<T>`. |
| **BlinkIDUX raw `events` stream** (RC-PAT; third-party) | `BlinkIDAnalyzer.events` getter stays a produce-throw (compile-poisoned SB0006): its root proxy `EventStreamProxy` is blocked by a PAT (`associatedtype Event`), which is not forward-safe, so it correctly stays fail-closed. Supported path: the concrete `BlinkIDEventStream.Stream` `IAsyncEnumerable`, which works. |
| **Authoring brand-new C# conformers behind forward-only proxies** (RC-SB0003 instance) | After the EveryProtocol proxy rescue, RealityFoundation `IMaterial`/`ISynchronizationService` project through forward-only proxies with no reverse-dispatch impl ctor — writing a from-scratch C# conformer and packing it into the framework stays unsupported. Consuming and round-tripping framework-produced instances works. |
