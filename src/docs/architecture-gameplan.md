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

### Milestone 1 — Trust the output *(3–4 sessions)*

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


### Milestone 2 — Catch real release bugs *(2–3 sessions)*

**Goal**: every confirmed release target has a regression baseline and at least one library that is verified working end-to-end from a fresh consumer.

**Scope**:
- End-to-end consumer test: `dotnet new swift-binding && dotnet build && dotnet run` actually invoking a Swift method. Today `Build.PackGate.cs` does library-build only — never runs Swift.
- Behavior tier in `nuke validate` for 1–2 representative libs: instantiate one type, call one Swift function from a fresh consumer project. Today validation only proves bindings *compile*, not that they *run*. Library selection is an open question (Foundation + one Theme B candidate is the working assumption).
- Populate runtime regression baselines for macOS, Mac Catalyst, and tvOS simulator. `Build.RuntimeTests.cs:2121` returns null for those today, so they aren't load-bearing as gates. All three are confirmed release targets per roadmap Theme E. (iOS simulator + iOS device baselines exist already; tvOS device is explicitly deferred per roadmap — no provisioning + physical Apple TV.)

**Why (litmus)**: exposes binding failures earlier (real consumer surface, real platforms). Three of the supported runtime axes currently can't catch regressions — that will produce real release bugs.

**Gate**: end-to-end consumer test passes; behavior-tier libs pass; all five runtime axes (iOS simulator/Mono JIT, iOS device/NativeAOT, macOS, Mac Catalyst, tvOS simulator) have populated baselines.

### Milestone 3 — Improve emitted API surface *(3–5 sessions)*

**Goal**: more bindings work. Specifically: fewer post-emission text rewrites stripping wrappers, fewer false-positive type suppressions, fewer high-volume `AnyTypeFallback` causes.

**Scope**:
- **CoGater inventory pass.** Classify each handler in `SwiftWrapperPostProcessor`, `CSharpWrapperCoGater` (Steps D–G), `ProcessSuppressedProxyReferencesInDirectory`, and `SimulatorOnlyMemberDetector` as either "we shouldn't have emitted this" (fixable at emission time) or "Swift compiler output normalization" (essential, keep). The inventory itself is the deliverable for the first session in M3.
- **Fix the top stripped-wrapper causes at emission time**, not via post-process text rewrite. Target the highest-volume "shouldn't have emitted" classes from the inventory — these directly increase the count of bindings that work. **Not in scope**: strangling the entire post-emission subsystem (post-1.0 — see Future/ roadmap).
- **SwiftUICore / SwiftUI suppression parity.** `SwiftUIViewDetector` recognizes both modules as View modules, but `ValidationRuleSet:22` lists only `SwiftUI` + `Combine` without `SwiftUICore`. Suppression gates can therefore differ depending on which module a declaration references. Audit all SwiftUI suppression sites for parity; add focused tests.
- **Highest-frequency `AnyTypeFallback` / type-resolution skip causes** that are *not* fundamentally cross-library scope. Roadmap explicitly defers cross-library dependency-graph resolution as different product scope. In-module supplement-resolution misses, alias resolution gaps, and similar single-module fixes are in scope.

**Why (litmus)**: every fix here directly increases emitted API surface — each one is a binding that works for consumers that didn't before.

**Gate**: skip count down on validation libraries (`AnyTypeFallback` is ~303 today per roadmap; M3 should put a meaningful dent in this); CoGater handlers reduced in count; SwiftUICore parity tests pass; full `nuke binding-tests --sim --device` + `nuke validate` at or above baseline (emission-time changes can affect generated calling conventions).

### Milestone 4 — Reduce bug-factory areas *(3–5 sessions)*

**Goal**: areas where the codebase silently produces wrong bindings under drift get a single source of truth.

**Scope**:
- **`TypeResolver` central seam.** Replace the 9-stage `TryGetTypeRecord` + 4 duplicated extension overloads (`TryGetTypeRecord`, `GetTypeRecordOrAnyType`, `GetTypeRecordOrThrow`, `TryGetAnyTypeFallbackInfo`) with one `TypeResolver.Resolve(TypeSpec, ResolutionContext) → TypeResolutionResult` returning `{record, syntheticFallback?, skipReason?, supplementReference?, confidence, provenance}`. Apple supplement, ObjC bridging, `Swift.Error`, dynamic self, generic params, existentials, pointers, metatypes, SIMD aliases, primitive aliases all become `IResolutionStrategy` plug-ins behind the resolver. **Comments in `TypeDatabase.cs` explicitly warn the contract will break if call paths merge** — that's the bug factory we're closing.
- **`SwiftInterfaceFacts` aggregator.** One immutable facts object replaces the 17 nullable side-channel maps threaded individually through `Program.GenerateBindings` into `SwiftABIParser`'s 27-arg constructor. Existing regex parser populates it. The producer swap (SwiftSyntax) defers post-1.0; the aggregator boundary itself doesn't. Internal members, actor isolation, typed throws, availability, default args, subscript labels — all currently fragile, all silently feed real decisions.
- **Source provenance plumbing.** Best-effort Swift `file:line:column` in diagnostics, using what the regex parser can give us. Imperfect now is better than nothing — full positions tighten when SwiftSyntax lands post-1.0. This is what makes "ALL runtime crashes are OUR BUGS" investigable instead of guesswork.

**Why (litmus)**: prevents a known class of bad generated binding. Type resolution drift produces wrong bindings now. Swiftinterface side-channel drift produces wrong decisions now. Both are silent. Both compound with every new feature added.

**Gate**: type resolution tests prove single-path policy (no special-case duplication); facts tests cover all 17 fact types; diagnostics surface source positions where available; full `nuke binding-tests --sim --device` + `nuke validate` at or above baseline.

### Total: ~11–17 sessions

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

1. End of M1 — diagnostic surface trustworthy; CI fail-closed.
2. End of M2 — every release target gates against regressions; consumer surface verified.
3. End of M3 — emitted API surface measurably larger.
4. End of M4 — bug-factory areas closed. **1.0 candidate.**

### Phase 0 setup

Before M1 starts:
1. Snapshot baselines: `validation-baseline.json`, BindingTests sim pass count, BindingTests device pass count, unit test pass count.
2. Capture a `binding-report.json` pre/post co-gating to evidence the staleness bug M1 fixes.
3. Verify dependent local repos are clean: `swift-dotnet-packages`, `swift-interop-repro`, wiki repo, `spm-to-xcframework`.
4. Architecture work proceeds on a `1.0-milestones` branch (or per-milestone branches), not `main`. Main remains shippable for hotfixes.

### Agent usage

- **Phase-start exploration**: spawn Explore subagents to map touch points before editing. Use Sonnet model per memory feedback.
- **Verification runs**: agents run gates and report pass/fail to keep test output out of main context.
- **Mechanical work**: e.g., applying `[EditorBrowsable(Never)]` across a list of types. Bounded scope, clear spec.

Architectural reasoning, bug-hunting, and judgment calls stay in main session.

### Standing rules

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
