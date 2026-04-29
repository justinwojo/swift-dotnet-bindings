# Architecture Gameplan — Pre-1.0

**Status**: Plan of record for the architecture track to 1.0.
**Companion doc**: `Future/post-1.0-architecture-roadmap.md` (deferred work — full inventory of items that didn't pass the litmus test).

**Decision**: 1.0 ships after the four milestones below. Architecture cleanup that doesn't fix bindings, prevent regressions, or expose failures earlier is **post-1.0** work.

This is the **architecture track**, run alongside `roadmap.md` (coverage / skip / library themes). The two are independent; both must close before 1.0.

---

## The 1.0 Litmus Test

Every proposed task answers one question:

> Will this either expose a real binding failure earlier, prevent a known class of bad generated binding, or increase valid emitted API surface?

If **no**, defer to post-1.0. The pre-1.0 audits surfaced ~150K LOC of architecture debt; most of it is real but doesn't move 1.0 quality. The four milestones below are everything that passes the litmus test (~12–17 sessions). The rest is in `Future/post-1.0-architecture-roadmap.md`.

---

## 1.0 Plan: Four Milestones

### Milestone 1 — Trust the output *(3–4 sessions)* — **DONE**

**Goal**: the diagnostic surface reflects what the consumer actually receives, and CI catches degraded generation.

**Scope**:
- Snapshot Phase 0 baselines: current `.validation-baseline.json`, BindingTests sim/device pass counts, unit test pass count. Capture `binding-report.json` pre/post co-gating to demonstrate the staleness bug.
- Introduce one typed `WrapperBuildOutcome` / `BridgeBuildOutcome` consumed identically across `RunCompileWrapperOnly`, the main command path, and `RunCompileBridgeOnly` (today they drift in severity).
- Introduce `BindingArtifactManifest` written *after* wrapper compilation, `CSharpWrapperCoGater`, and bridge compilation. `binding-report.json` derives from the manifest, not mid-pipeline state.
- Add overload-stable diagnostic identity: Swift module + decl path + member kind + base name + parameter labels/types + accessor kind + mangled symbol when available.
- Fix `RecordMemberSkipped` so it no longer refuses to record a skip when the same `Kind:ContainingType:Name` key was already emitted (today it collapses overloads).
- Make `--compile-only` BindingTests fail-closed by default in CI: generator non-zero exit, dependency-gen failure, missing wrapper symbols (per the typed outcome), and report regressions all hard-fail. Add `--permissive` for local exploration.
- Minimal 1.0 surface reduction: make `TypeOwnerRegistry` and `RuntimeLimitations.Limitation` `internal` (move ownership policy into `Swift.Bindings`; both have zero runtime-side callers). Apply `[EditorBrowsable(Never)]` consistently across `ExistentialContainer*`, `SwiftHandle`, `TypeMetadata`, `SwiftMetadata`, `NominalTypeDescriptor`, `FieldDescriptor`, `RelativePointer`, `ProtocolWitnessTable`, `ValueWitnessTable`. Honest framing: `EditorBrowsable(Never)` hides from IntelliSense but the type remains public API — full lock would require the `IGeneratedSwiftObject` split (deferred). The 1.0 message is "documented as generated-code infrastructure," not "removed from the contract." **Not in scope**: `IGeneratedSwiftObject` migration, dead metadata model deletion, `ExistentialContainer` consolidation — those defer.

**Why (litmus)**: exposes binding failures earlier (truthful reports + identity), prevents silent release regressions (fail-closed gates), reduces consumer-facing surface where it can be done cheaply.

**Gate**: full `nuke binding-tests --sim --device` + `nuke validate` at or above baseline; manifest-derived report shows correct counts vs. captured pre-gating evidence. Device included because fail-closed gating changes apply to NativeAOT packaging paths.


### Milestone 2 — Catch real release bugs *(2–3 sessions)* — **IN PROGRESS**

**Goal**: every confirmed release target has a regression baseline and at least one library that is verified working end-to-end from a fresh consumer.

**Scope**:
- **DONE**: End-to-end consumer test: `dotnet new swift-binding && dotnet build && dotnet run` actually invoking a Swift method. Today `Build.PackGate.cs` does library-build only — never runs Swift.
- Behavior tier in `nuke validate` for 1–2 representative libs: instantiate one type, call one Swift function from a fresh consumer project. Today validation only proves bindings *compile*, not that they *run*. Library selection is an open question (Foundation + one Theme B candidate is the working assumption).
- **DONE**: Populate runtime regression baselines for macOS, Mac Catalyst, and tvOS simulator. `Swift.Foundation.*` reference gap closed by wiring `Swift.Bindings.Apple` (multi-targeted across all four Apple TFMs) plus the `AppleIdentity.ConsumerA/B` probes into the Mac / Catalyst / tvOS RuntimeTestsApp variants. AbiSafety frozen-struct ABI regression fixed (Session 2). Optional<generic-param> @in double-release on Mac-family Mono Interpreter fixed (Session 2). Baselines: macOS=1448, MacCatalyst=1448, TvOSSimulator=1532 (all 0 fail / 0 crash). iOS simulator (1760) and iOS device (1773) baselines were already in place; tvOS device is explicitly deferred per roadmap (no physical Apple TV).

**Sessions** (2 of 3 done, 1 remaining):

1. **Session 1 — Pack-gate consumer run + end-to-end consumer test** *(DONE — `c496573a`)*. Closes M2's first scope bullet.

2. **Session 2 — macOS ABI fix + populate macOS / Catalyst / tvOS sim baselines** *(DONE)*
   - **DONE**: Root-caused and fixed the `AbiSafetyRuntimeTests` frozen-struct field-read regression. The thunk's `mov x20, x{ParameterCount}` only matches swiftcc when self is passed by indirect pointer — i.e. mutating funcs, property/subscript setters, classes, non-frozen structs. Non-mutating instance methods on **frozen** value types use the expanded-direct ABI (HFA in `d0-d3`, ≤32B integer aggregates in `x0-x3`, etc.) and leave x20 untouched at entry. The thunk was forwarding a pointer into a register the callee never reads, so `LottieColorLike.R` returned stack residue, `LargeConfig.Width` came back as `1875139712`, etc. `IsSelfTypeLowerable` (`NativeThunkEmitter.cs`) now rejects non-mutating frozen-struct instance methods so `PropertyHandler` / `MethodHandler` route those through the @_cdecl wrapper instead, where the Swift compiler emits the correct expanded-direct ABI. Mutating methods + setters continue to thunk safely. Verified 37/37 AbiSafetyRuntimeTests pass on macOS.
   - **DONE**: Root-caused and fixed the Mac-family finalizer-thread SIGSEGV. Swift inits taking `Optional<T>` of a generic parameter (e.g. `OptionalGenericHolder(stored:)`, `OptionalWrapper(value:)`) lower the parameter to `@in` (callee-destroyed) under raw CallConvSwift — confirmed via SIL dump showing `[%1: ..., destroy v**]`. The C# side allocated a SwiftOptional buffer, passed `DangerousGetHandle()`, and ran `using var` Dispose afterward. After Swift's @in init consumed the value via `copy_addr [take]`, the buffer's bits remained pointing at the moved-out class instance, so SwiftOptional's normal Dispose called VWT Destroy on the deinitialized buffer → second `swift_release` on the class field → eventual finalizer-thread SIGSEGV when GC reached the same SafeHandle. Fix: new `SwiftOptional<T>.DisposeAfterConsumption()` runtime helper that frees the .NET buffer but bypasses VWT Destroy (`SetHandleAsInvalid` on the SafeHandle so `ReleaseHandle` is suppressed). Generator wiring: `WrapperEmitter` records `_inConventionOptionalNames` for parameters that hit the `needsGenericOptOverride` path with raw CallConvSwift (skipped for @_cdecl wrappers, which use `.load(as:)` copy semantics), and `EmitInConventionOptionalCleanup` emits `{name}Swift.DisposeAfterConsumption();` immediately after each `EmitPInvokeCall`. iOS Simulator (Mono JIT) masked the bug because of different finalizer scheduling; only Mono Interpreter on Mac/Catalyst/tvOS-sim surfaced it.
   - **DONE**: `RuntimeTestsBaseline` (`build/Models/ValidationBaseline.cs`) extended with `MacOS`, `MacCatalyst`, `TvOSSimulator` records; `Build.RuntimeTests.cs:2121` dispatch switch + auto-update logic updated for all 5 platforms. `.validation-baseline.json` seeded with macOS=1448, MacCatalyst=1448, TvOSSimulator=1532 (all 0 fail, 0 crash).
   - **Gate (MET)**: all three platform runs green; baselines committed; M2's third scope bullet **DONE**.

3. **Session 3 — Behavior-tier `nuke validate` + close M2**
   - Resolve Open Question #2: commit to Foundation + one Theme B candidate (Alamofire is the strong default — doubles as Theme B's deep-dive target).
   - Add behavior tier in `nuke validate`: fresh consumer project, instantiate one type, call one Swift function, assert result. Today validation only proves bindings *compile*, not that they *run*.
   - Wire the behavior tier into the validate gate.
   - **Gate**: M2 checkpoint sweep (`nuke binding-tests --sim --device --macos --catalyst --tvos` + `nuke validate`) clean; M2 marked DONE; checkpoint #2 reached.

**Why (litmus)**: exposes binding failures earlier (real consumer surface, real platforms). Three of the supported runtime axes currently can't catch regressions — that will produce real release bugs.

**Gate**: end-to-end consumer test passes; behavior-tier libs pass; all five runtime axes (iOS simulator/Mono JIT, iOS device/NativeAOT, macOS, Mac Catalyst, tvOS simulator) have populated baselines.

### Milestone 3 — Improve emitted API surface *(3–5 sessions)*

**Goal**: more bindings work. Specifically: fewer post-emission text rewrites stripping wrappers, fewer false-positive type suppressions, fewer high-volume `AnyTypeFallback` causes.

**Scope**:
- **CoGater inventory pass.** Classify each handler in `SwiftWrapperPostProcessor`, `CSharpWrapperCoGater` (Steps D–G), `ProcessSuppressedProxyReferencesInDirectory`, and `SimulatorOnlyMemberDetector` as either "we shouldn't have emitted this" (fixable at emission time) or "Swift compiler output normalization" (essential, keep). The inventory itself is the deliverable for the first session in M3.
- **Fix the top stripped-wrapper causes at emission time**, not via post-process text rewrite. Target the highest-volume "shouldn't have emitted" classes from the inventory — these directly increase the count of bindings that work. **Not in scope**: strangling the entire post-emission subsystem (post-1.0 — see Future/ roadmap).
- **SwiftUICore / SwiftUI suppression parity.** `SwiftUIViewDetector` recognizes both modules as View modules, but `ValidationRuleSet:22` lists only `SwiftUI` + `Combine` without `SwiftUICore`. Suppression gates can therefore differ depending on which module a declaration references. Audit all SwiftUI suppression sites for parity; add focused tests.
- **Highest-frequency `AnyTypeFallback` / type-resolution skip causes** that are *not* fundamentally cross-library scope. Roadmap explicitly defers cross-library dependency-graph resolution as different product scope. In-module supplement-resolution misses, alias resolution gaps, and similar single-module fixes are in scope.

**Sessions** (4):

1. **Session 1 — CoGater inventory pass**
   - Catalog every handler in `SwiftWrapperPostProcessor`, `CSharpWrapperCoGater` (Steps D–G), `ProcessSuppressedProxyReferencesInDirectory`, `SimulatorOnlyMemberDetector`.
   - For each handler, classify as either "shouldn't have emitted" (with proposed emission-time fix location: Marshaler / `PropertyHandler` / etc.) or "essential Swift compiler output normalization" (keep).
   - Histogram each handler's hit count across validation libs to size the fix-or-keep decisions.
   - Deliverable: `src/docs/scratch/m3-cogater-inventory.md` (deleted in M3 close per the standing rule on milestone scaffolding).
   - **Gate**: top-N "shouldn't have emitted" classes identified with concrete emission-time fix proposals.

2. **Session 2 — Stripped-wrapper emission fix (round 1)**
   - From the inventory, fix the top 1–2 "shouldn't have emitted" classes by volume at emission time, not via post-process text rewrite.
   - Disable the corresponding CoGater handlers; add unit tests proving the emission no longer needs stripping.
   - **Gate**: skip count for those causes down ≥80% on validation libs; CoGater handlers removed; `nuke binding-tests --sim --device` + `nuke validate` at-or-above baseline.

3. **Session 3 — Stripped-wrapper emission fix (round 2) + SwiftUICore parity**
   - 1–2 more emission-time fixes from the inventory (continue picking by volume per Open Question #3 — top 3 by volume, or any class whose fix cost is < 1 session).
   - Add `SwiftUICore` to `ValidationRuleSet:22` alongside `SwiftUI` + `Combine`. Audit every SwiftUI suppression site (`SwiftUIViewDetector` vs `ValidationRuleSet`) for parity. Add focused tests proving `SwiftUICore` declarations are suppressed identically.
   - **Gate**: SwiftUICore parity tests pass; another CoGater handler removed; baselines at-or-above.

4. **Session 4 — `AnyTypeFallback` reduction + close M3**
   - Generate skip-cause histogram from `coverage-matrix.json` across all validation libs.
   - Pick 3–5 highest-frequency causes that are *not* cross-library scope (in-module supplement-resolution misses, alias resolution gaps, similar single-module fixes). Fix each at the resolution layer (`TypeDatabase` / type XML / supplement). Per CLAUDE.md zero-regression policy, ship each with tests at the right layer.
   - Roadmap reconciliation: update Theme A rows in `roadmap.md`.
   - Delete `src/docs/scratch/m3-cogater-inventory.md`.
   - **Gate**: M3 checkpoint sweep clean (`nuke binding-tests --sim --device --macos --catalyst --tvos` + `nuke validate` at-or-above; AnyTypeFallback count down meaningfully — no pre-committed number per Open Question #4); M3 marked DONE; checkpoint #3 reached.

**Why (litmus)**: every fix here directly increases emitted API surface — each one is a binding that works for consumers that didn't before.

**Gate**: skip count down on validation libraries (`AnyTypeFallback` is ~303 today per roadmap; M3 should put a meaningful dent in this); CoGater handlers reduced in count; SwiftUICore parity tests pass; full `nuke binding-tests --sim --device` + `nuke validate` at or above baseline (emission-time changes can affect generated calling conventions).

### Milestone 4 — Reduce bug-factory areas *(3–5 sessions)*

**Goal**: areas where the codebase silently produces wrong bindings under drift get a single source of truth.

**Scope**:
- **`TypeResolver` central seam.** Replace the 9-stage `TryGetTypeRecord` + 4 duplicated extension overloads (`TryGetTypeRecord`, `GetTypeRecordOrAnyType`, `GetTypeRecordOrThrow`, `TryGetAnyTypeFallbackInfo`) with one `TypeResolver.Resolve(TypeSpec, ResolutionContext) → TypeResolutionResult` returning `{record, syntheticFallback?, skipReason?, supplementReference?, confidence, provenance}`. Apple supplement, ObjC bridging, `Swift.Error`, dynamic self, generic params, existentials, pointers, metatypes, SIMD aliases, primitive aliases all become `IResolutionStrategy` plug-ins behind the resolver. **Comments in `TypeDatabase.cs` explicitly warn the contract will break if call paths merge** — that's the bug factory we're closing.
- **`SwiftInterfaceFacts` aggregator.** One immutable facts object replaces the 17 nullable side-channel maps threaded individually through `Program.GenerateBindings` into `SwiftABIParser`'s 27-arg constructor. Existing regex parser populates it. The producer swap (SwiftSyntax) defers post-1.0; the aggregator boundary itself doesn't. Internal members, actor isolation, typed throws, availability, default args, subscript labels — all currently fragile, all silently feed real decisions.
- **Source provenance plumbing.** Best-effort Swift `file:line:column` in diagnostics, using what the regex parser can give us. Imperfect now is better than nothing — full positions tighten when SwiftSyntax lands post-1.0. This is what makes "ALL runtime crashes are OUR BUGS" investigable instead of guesswork.

**Sessions** (4):

1. **Session 1 — `TypeResolver` scaffold + first strategies**
   - Define `TypeResolver.Resolve(TypeSpec, ResolutionContext) → TypeResolutionResult { record, syntheticFallback?, skipReason?, supplementReference?, confidence, provenance }`.
   - Define `IResolutionStrategy` plug-in interface.
   - Migrate the 3 simplest strategies first as proof-of-shape: primitive aliases, generic params, dynamic self.
   - Old `TryGetTypeRecord` paths kept in parallel for the rest; parity tests prove the resolver matches existing behavior on the migrated strategies.
   - Resolver core unit tests.
   - **Gate**: 3 strategies fully on `TypeResolver`; `nuke test` green; `nuke validate` at-or-above baseline.

2. **Session 2 — Complete `TypeResolver` migration + delete duplicates**
   - Migrate remaining strategies: Apple supplement, ObjC bridging, `Swift.Error`, existentials, pointers, metatypes, SIMD aliases.
   - Delete the 4 duplicated extension overloads (`TryGetTypeRecord`, `GetTypeRecordOrAnyType`, `GetTypeRecordOrThrow`, `TryGetAnyTypeFallbackInfo`) and the 9-stage `TryGetTypeRecord`.
   - Update or remove the contract-warning comments in `TypeDatabase.cs` that flagged this as the bug factory.
   - Tests proving single-path policy (no special-case duplication).
   - **Gate**: zero duplicate type-resolution paths; `nuke binding-tests --sim --device` + `nuke validate` at-or-above.

3. **Session 3 — `SwiftInterfaceFacts` aggregator**
   - Define an immutable `SwiftInterfaceFacts` record covering all 17 fact types currently threaded as nullable side-channel maps (internal members, actor isolation, typed throws, availability, default args, subscript labels, etc.).
   - Existing regex parser populates it (the producer swap to SwiftSyntax stays deferred post-1.0).
   - Replace the 17 maps in `Program.GenerateBindings`; reduce `SwiftABIParser` ctor from 27 args to `(config, facts)`.
   - Tests covering all 17 fact types.
   - **Gate**: ctor sig reduced; all fact-type tests pass; baselines at-or-above.

4. **Session 4 — Source provenance plumbing + close M4 (1.0 candidate)**
   - Best-effort Swift `file:line:column` extraction from the regex parser's existing match offsets.
   - Plumb provenance through `Diagnostic`, skip messages, and `binding-report.json`.
   - Tests proving positions appear where the parser can supply them (and gracefully degrade where it cannot).
   - Final M4 checkpoint sweep: full `nuke binding-tests --sim --device --macos --catalyst --tvos` + `nuke validate`. Confirm baselines.
   - **Gate**: 1.0 candidate sweep clean; M4 marked DONE; checkpoint #4 reached. Per Open Question #6, optional ~1-release-cycle soak in `swift-dotnet-packages` before shipping.

**Why (litmus)**: prevents a known class of bad generated binding. Type resolution drift produces wrong bindings now. Swiftinterface side-channel drift produces wrong decisions now. Both are silent. Both compound with every new feature added.

**Gate**: type resolution tests prove single-path policy (no special-case duplication); facts tests cover all 17 fact types; diagnostics surface source positions where available; full `nuke binding-tests --sim --device` + `nuke validate` at or above baseline.

### Total: ~14 sessions (9 remaining)

Allocation: M1 (3–4, DONE) + M2 (3, 2 done / 1 remaining) + M3 (4) + M4 (4). Each subsequent `/next-session` corresponds to exactly one of the numbered sessions enumerated under its milestone above — the unit of work is the session, not "the next visible incremental step."

Elapsed time is **validation-bound**, not session-stacked. Each milestone ends with a full sim + device + validate sweep (~30+ minutes of run time even when everything passes), and most milestones will surface at least one fix-and-rerun cycle. Don't sell this as compressible by stacking sessions per day — that pressures rushing the very gates this rescope is meant to protect. Realistic framing is a focused working week of execution, not a sprint.

---

## Execution Strategy

### Validation tiers

Tier each gate by what it uniquely catches.

| Gate | Time | Uniquely catches | Cadence |
|---|---|---|---|
| `nuke compile` | fast | Compile errors in generator/runtime | Every chunk |
| `nuke test` (unit) | ~2 min | Generator/emitter logic bugs | Every chunk |
| `nuke binding-tests --compile-only --strict --skip-regen` | ~5 min | Generated C# that doesn't compile | Every generator/emitter chunk |
| `nuke validate` | ~2 min | Cross-library compile-shape regressions | End of each milestone |
| `nuke binding-tests --sim` | ~10 min | Mono JIT runtime regressions | End of each milestone |
| `nuke binding-tests --device` | ~15 min | NativeAOT calling-conv / marshalling bugs | End of each milestone + when CC/marshalling touched |

Unit-tests-only loops miss generated C# that compiles unit tests but emits broken P/Invokes. `--compile-only --strict --skip-regen` is the highest-leverage gate for that bug class.

### Checkpoints

Four checkpoints — one per milestone. Each runs the full sim + device + validate sweep and updates baselines.

1. **DONE** — End of M1: diagnostic surface trustworthy; CI fail-closed.
2. End of M2 — every release target gates against regressions; consumer surface verified.
3. End of M3 — emitted API surface measurably larger.
4. End of M4 — bug-factory areas closed. **1.0 candidate.**

### Phase 0 setup — **DONE**

Before M1 starts:
1. Snapshot baselines: `validation-baseline.json`, BindingTests sim pass count, BindingTests device pass count, unit test pass count.
2. Capture a `binding-report.json` pre/post co-gating to evidence the staleness bug M1 fixes.
3. Verify dependent local repos are clean: `swift-dotnet-packages`, `swift-interop-repro`, wiki repo, `spm-to-xcframework`.

### Agent usage

- **Phase-start exploration**: spawn Explore subagents to map touch points before editing. Use Sonnet model per memory feedback.
- **Verification runs**: agents run gates and report pass/fail to keep test output out of main context.
- **Mechanical work**: e.g., applying `[EditorBrowsable(Never)]` across a list of types. Bounded scope, clear spec.

Architectural reasoning, bug-hunting, and judgment calls stay in main session.

### Standing rules

- **Trunk-based by default.** Each session ships to `main` once gates pass. Downstream consumers only see what's published to NuGet, so "main stays shippable for hotfixes" isn't a constraint that buys anything here. Spin a short-lived feature branch only when a single semantic change genuinely cannot be coherent in one commit, and merge as a unit when done. Long-running per-milestone branches are explicitly not the default.
- **Milestone scaffolding under `src/docs/scratch/`.** Phase-0 evidence, inventories, and other docs that exist only to inform a specific milestone live under `scratch/` and are deleted in that milestone's completion commit. They are not part of the durable docs surface.
- **Zero-regression policy active throughout.** Per CLAUDE.md.
- **Commit discipline**: subject + 1–3 sentences on the *why*. No "Milestone N handoff" footers. No phase-number references in code comments.
- **Memory updates** as discoveries land — non-obvious decisions get a memory file.
- **Roadmap reconciliation**: when M2 picks behavior-tier libs and M3 fixes skip causes, update `src/docs/roadmap.md` accordingly.

---

## Strengths to Preserve

Don't lose these — they're patterns the team got right. Most of them stay untouched by the four milestones.

- **`ModuleEmissionContext`** — the right shape for per-module instance state.
- **`IProjectionVisitor<T>` + `ITypeProjection`** — compile-time exhaustive dispatch.
- **`MethodHandler.Emit` `IMethodBridgeEmitter` strategy table** — cleanest abstraction in the emitter.
- **`TypeSpec` discriminated union + `TypeSpecTokenizer`/`TypeSpecParser`** lexer-parser separation.
- **`TbdParser`** — clean isolation, two-format strategy.
- **`MarshalledType` / `MarshalPlan` records** — typed sum types.
- **`apple-frameworks.json` declarative registry** — data-driven model.
- **`PlatformInfo` / `SliceVariant` / `PlatformInfoFactory`** — table-driven 4-platform factory.
- **`SwiftClassHandle<T>` / `SwiftSafeHandle<T>` finalizer split** — 1.0 quality already.
- **`SwiftFrameworkResolver`** — 1.0 quality already.
- **`ICommandRunner` with timeout-honest cancellation** — right base for future `SwiftToolchain`.
- **SDK diagnostic-code discipline** (~25 numbered `SWIFTBIND0xx`) — the discipline survives even when codes get unified.
- **BindingTests domain-first folder layout**.
- **Compile-time test discovery via source generator** (`TestRegistry.Classes`).
- **Skip-attribute scheme** (`[Skip]`, `[SkipOnSimulator]`, `[SkipOnDevice]`, `[Slow]`).
- **`AppleIdentity.ConsumerA/B` cross-assembly identity probe** — replicate the style for other architectural invariants.
- **`AbiContractChecker`** — closest existing model for unified Diagnostic.
- **`Tools/` and `Models/`** in build directory.

---

## Explicitly Rejected for 1.0

- **Wholesale Roslyn `SyntaxFactory` rewrite of the emitter.** Wrong tradeoff at any milestone.
- **Verify.NET 4K-snapshot replacement of `Assert.Contains` tests.** Same brittleness at larger scale.
- **Shell-out to `swift demangle` per symbol.** Per-symbol fork/exec on Foundation-sized TBDs is intolerable.
- **Big-bang Type IR rewrite.** Even post-1.0, do `TypeResolver` seam first (M4 above).
- **Plan-vs-Emit phase separation as a 1.0 deliverable.** Real architectural improvement; doesn't move 1.0 quality. Post-1.0.
- **Roslyn analyzer pack for diagnostics.** Out of scope.

---

## Open Questions

1. **Source position strategy (M4).** Use what the existing regex parser can surface (cheap, lossy) and tighten with SwiftSyntax post-1.0, or wait for SwiftSyntax to land before plumbing positions? Recommendation: best-effort now. Don't gate M4 on a parser swap.

2. **Behavior-tier library selection (M2).** Foundation is a near-certain pick for universal coverage. Second slot: CryptoKit (already partial validation coverage), or one of the Theme B candidates (Alamofire / Kingfisher / GRDB) once committed? Recommendation: choose at start of M2; favor whichever doubles as Theme B's deep-dive target.

3. **CoGater inventory threshold (M3).** Inventory will identify 5–10+ classes of "shouldn't have emitted." How many do we fix at emission time before declaring M3 done? Recommendation: fix the top 3 by volume, or any class whose fix cost is < 1 session — whichever yields more.

4. **`AnyTypeFallback` reduction target (M3).** ~303 skips today; how aggressive a target makes M3 done? Recommendation: don't pre-commit a number. Pick the 3–5 highest-frequency causes from a skip-cause histogram and fix those; let the count drop where it drops.

5. **Roadmap reconciliation cadence (standing rule).** When M3 closes a skip cause, the corresponding row in `roadmap.md` Theme A should update. Mid-milestone or end-of-milestone? Recommendation: end-of-milestone — avoid mid-flight doc churn.

6. **Soak before 1.0.** After M4, ship 1.0 immediately, or run a soak window with the consumer test app + behavior-tier validation against current bindings? Recommendation: soak for ~1 release cycle of `swift-dotnet-packages` after M4; if no consumer-reported issues, ship.

---

## Out of Scope / Essential Complexity

These look like problems but aren't.

- **The Swift `@_cdecl` wrapper itself.** Swift's `swiftcc` (error register, indirect-result registers, generic metadata params) is not expressible in C#'s `[LibraryImport]`. The wrapper is essential; the post-processor is accidental.
- **Multi-emitter protocol bridging.** PATs, witness tables, existential containers, `EveryProtocol`-style C#-to-Swift conformance genuinely need multi-emitter machinery. The 8-way protocol emitter split reflects three distinct bridging directions, not three implementations of the same thing.
- **Mono vs. NativeAOT runtime support.** Branching is essential; the duplication is the cleanup target (post-1.0).
- **xcframework slice handling, codesign, NativeAOT vs. Mono platform matrix.** Build infrastructure complexity is partly real.
