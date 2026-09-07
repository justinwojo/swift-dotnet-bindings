# Faster AI-driven development

Living research document, started 2026-09-06. Research baseline: `cc6f29318b143c968007371c6e8c771e6417803e`.

## Purpose and status

Make the next feature, bug fix, review, and release easier and faster while preserving binding correctness. Optimize time from an edit to trustworthy feedback, including agent exploration, build preparation, failure diagnosis, and human intervention.

This is a research register, not an approved implementation plan or a change to repository validation policy. Suggestions need evidence, a failure analysis, and a bounded experiment before promotion. No generator, runtime, build, or test changes are part of this research pass.

Evidence labels used below:

- **Observed:** checked in the current repository source or by a named static inspection.
- **Recorded:** a measurement or incident in existing project documentation, not independently reproduced here.
- **Hypothesis:** a proposed improvement whose benefit still needs measurement.
- **Rejected/deferred:** a tempting approach that does not currently justify its cost or risk.

## Current shortlist

| ID | Candidate | Initial position |
|---|---|---|
| V01 | Measure edit-to-feedback time by stage and task type | Foundation; use existing logs and result models first |
| V02 | Automatically reuse verified Swift fixtures and generated artifacts | Strong candidate; complete dependency identities before enabling reuse |
| V03 | Make focused development and full validation explicit workflows | Strong candidate; preserve full-suite completion gates |
| V04 | Reduce repeated agent context discovery and instruction drift | Strong low-cost candidate; preserve cross-cutting invariants |
| V05 | Return compact, actionable failure evidence with reproduction commands | Strong candidate; adapt existing reports and inventories |
| V06 | Validate fixed source snapshots and isolate shared resources | Prerequisite for overlapping edits, builds, and agent work |
| V07 | Fix downstream simulator/device orchestration noise | Supported by recorded incidents; implementation belongs downstream |
| V08 | Minimize consumer repros and make focused fixtures cheap | Candidate; fixture splitting depends on measured build cost |
| V09 | Simplify repeated decisions at demonstrated maintenance seams | Selective; broad emitter rewrite is not justified |
| V10 | Expose focused unit tests and generated-output sweeps through Nuke | Strong candidate; no full-suite baseline changes |
| V11 | Make ordinary AI reviews bounded and allow zero findings | Strong low-cost candidate; retain explicit deep audits |
| V12 | Reuse identical package-feed preparation across consumer gates | Measure first; fresh consumer and packaging transitions remain essential |

## Standing constraints

- Compilation does not prove ABI, ownership, callback, or runtime correctness. Preserve applicable simulator, NativeAOT, Mono full-AOT, and packaging coverage.
- A focused run supplies partial evidence; it cannot update a full-suite baseline or certify an entire change.
- A cached artifact and a previous passing test are different things. Reusing build outputs does not automatically justify reusing runtime results.
- A missing result, infrastructure failure, or unexecuted gate is unresolved coverage, not a pass.
- Speed improvements must preserve emitted API surface and skip identities, not just successful compilation or aggregate test counts.
- Existing deferred decisions remain deferred. Research can identify a new reason to reconsider one; it cannot silently turn historical ideas into implementation commitments.

## What the first research pass actually established

### Source observations, 2026-09-06

- `RunBuildXcframework` removes the entire fixture build directory before compiling the dependency and main modules. `RunRegenerateBindings` removes the generated output directory. These are explicit clean rebuilds in the ordinary path, not merely a suspicion based on elapsed time. See [Build.BindingTests.cs](../../build/Build.BindingTests.cs), especially `RunBuildXcframework` and `RunRegenerateBindings`.
- A composed simulator/device invocation dispatches the platforms sequentially. Both platform helpers prepare fixtures and regenerate iOS bindings; the device path additionally produces device wrappers. Shared preparation is therefore a concrete candidate, but equal generated output between the two fixture configurations is not yet proven. See [Build.RuntimeTests.cs](../../build/Build.RuntimeTests.cs), `RunSimulatorPlatform` and `RunDevicePlatform`.
- Existing optimizations matter: generator source/DLL stamps, Apple snapshot freshness checks, CI SwiftInterfaceParser caching, mixed-pack feed reuse across its own sim/device consumers, and runtime crash-resume support already exist. Proposals below extend these rather than assume a blank slate.
- Source inspection found instruction drift: `CLAUDE.md` describes `--class-filter` as simulator-only, while the runtime harness forwards it to other platform runners too. This establishes a navigation problem; forwarding alone does not prove every platform filter is equally well validated.
- The existing audit workflow defaults to heavy fan-out and represents an empty audit as a P2 finding. Its useful reachability and verification safeguards should remain, but the default deserves separate scrutiny. See V11.

### Small static measurements

These are local file measurements, not model-token counts or performance benchmarks:

| File | Bytes | Whitespace-separated words | Implication |
|---|---:|---:|---|
| `CLAUDE.md` | 30,675 | 4,214 | Every task pays for substantial detailed gate documentation |
| `.claude/rules/bindingtests.md` | 12,638 | 1,636 | Matching tasks also read duplicated harness guidance |
| `src/docs/not-planned.md` | 363,257 | 48,451 | Search by topic and read the relevant entry; loading the whole register is costly |
| `build/Build.RuntimeTests.cs` | 202,991 | 18,862 | A whole-file fingerprint couples snapshot invalidation to unrelated launcher edits |

Sizes alone do not prove slower or worse agent work. They identify cheap experiments. Do not make this research document mandatory startup context.

### Saved test results inspected, not rerun

The existing `src/Swift.Bindings/tests/UnitTests/TestResults/unit-tests.trx` records a run from 2026-09-06 15:02:19 to 15:03:08, about **49.1 seconds**, with **17,896 passed**. The existing runtime-library TRX records about **0.62 seconds** and **868 passed**. These timings exclude earlier build preparation and are not certified measurements of the current checkout: the files do not establish the complete input identity used for the run.

In the unit TRX, slower individual cases include package restore, SDK behavior, wrapper-lock, and ObjC pipeline tests. The largest summed class duration is `SdkTargetsBehaviorTests` (135 cases, about 47.2 seconds summed). Tests overlap, so summed durations cannot be treated as wall-clock savings or used to infer the critical path.

**Inference:** “there are nearly 18,000 tests” does not establish that deleting tests or splitting the suite is the highest-value optimization. Measure build preparation and diagnosis separately; first provide a reliable focused invocation. No builds or simulator/device tests were run for this research pass.

## Candidates and their failure tests

### V01 — Measure the whole edit-to-feedback loop

**Position:** recommended foundation, kept small.

Measure three representative tasks initially: generator-only fix, runtime-only fix, and SDK/packaging fix. Separate agent navigation, fixture compilation, generation, generated-code compilation, app build, install/launch, execution, and diagnosis. Record cold/warm state, input identity, runtime, and retries. Report human active time separately from elapsed time; overlapping agent work is not additive wall time.

Use existing TRX/JSONL and [release leg durations](../../build/Models/ReleaseGatesManifest.cs) first. Add stage timing only where current logs cannot answer the question. Avoid building a dashboard before collecting a useful sample.

**Failure test:** a faster run that omits tests, uses stale artifacts, or needs more human repair is not an improvement. A single warm run is not an estimate of normal performance.

**Small experiment:** observe several ordinary tasks without changing their gates, then rank *repeated avoidable minutes* and interventions. A practical decision rule is `frequency × time saved`, considered alongside implementation and maintenance cost; no fabricated percentage return or promised speedup.

### V02 — Reuse preparation automatically, starting inside one invocation

**Position:** strongest build-side candidate; start narrower than a general cache.

First evaluate a composed `--sim --device` run. Resolve required slices once, build the shared Swift fixture once, and regenerate shared iOS bindings once **if equivalence is demonstrated**. Keep platform-specific wrappers, app builds, installations, and runtime assertions. Simply setting `--skip-regen` for the device leg would also bypass device preparation and is not the proposed solution.

Next consider persistent reuse at the Swift module/slice boundary. A generator edit should not rebuild unchanged Swift input fixtures. Cache the complete artifact set: binary, module/interface, TBD, ABI JSON, and required auxiliary outputs. The dependency module's artifact identity must be part of the main module's inputs.

Keys need source membership and content, compiler/SDK identity, target/minimum OS, defines, dependency artifacts, and relevant harness options. Publish only completed entries atomically; a missing or corrupt required output invalidates the entry. Start with local reuse; remote cache infrastructure is not required.

**Existing foundation:** `EnsureGeneratorBuilt`, `ComputeSourceFingerprint`, and `IsAppleFrameworkSnapshotFresh`. **Insufficient shortcut:** `AssertBindingsNotStale` checks the main binding file, smoke flags, platform, and main Swift source timestamps; that is not a complete dependency identity. See [runtime harness](../../build/Build.RuntimeTests.cs) and [validation fingerprints](../../build/Build.Validation.cs).

**Failure tests:** dependency-only edit; source deletion; preserved timestamps; switched SDK; changed flags; interrupted write; missing wrapper; corrupt binary; wrong architecture. A hit must not silently change emitted surface or skip identities.

**Small experiment:** measure duplicate preparation, compare simulator-only and device-inclusive generation, then add a proposed key in observation-only mode while clean builds still execute. Compare results using manifests and semantic gates; account explicitly for harmless binary/signing nondeterminism. Advance to reuse only after invalidation tests pass.

### V03 — Encode development and completion workflows around existing gates

**Position:** recommended, with conservative coverage claims.

Expose a small, supported set of Nuke entry points for focused iteration, completion validation, and release validation. Names are undecided; these are proposed workflows, not commands available today. Keep advanced targets for diagnosis. The workflow should print what will execute and why, what is partial, and what remains required. Release validation must include the status of applicable downstream-repository checks; it does not imply publishing packages.

This should compose the existing targets and result semantics. It should not add a second test system or copy the flag matrix into another handwritten policy file. Follow the current [CLAUDE validation requirements](../../CLAUDE.md), including compile-only checks and runtime gates where applicable.

**Failure test:** a changed-file selector says “only test this class,” but a shared projection helper affects closures, constructors, and properties. Path matching can recommend or broaden gates; it cannot prove semantic independence. Unknown/cross-cutting scope keeps the broad existing requirements. A focused green never updates full-suite baselines.

**Small experiment:** use a handful of historical changes to see whether the proposed workflow chooses every required gate and explains the selection without manual flag archaeology. Preserve device NativeAOT and Mono full-AOT distinctions; platform labels alone are insufficient.

### V04 — Reduce mandatory context and make facts easy to retrieve

**Position:** recommended low-cost pilot, evaluated for correctness as well as size.

Keep authorization rules, ABI invariants, no-shortcut requirements, and a concise task-to-validation routing table in mandatory context. Move detailed descriptions of opt-in packaging/release gates to one canonical harness guide. Scoped rules for both `build/**` and `BindingTests/**` should route to the relevant section on demand, not automatically load the entire detailed guide again. Link to authoritative test identities and supported runtime definitions instead of copying mutable counts.

The current scoped rules are a useful foundation. Do not scatter a global invariant across narrow filename scopes: callback ownership or calling-convention constraints can matter outside an obvious emitter file. Preserve discoverability for agents starting with an error message rather than a known source path.

Prefer a small task handoff containing the exact failing assertion, relevant symbols/artifacts, rejected hypotheses with evidence, and outstanding validation. Use existing active-session conventions; do not create a new permanent diary or automatically append every conversation to mandatory instructions.

**Failure tests:** the shorter guide saves reads but hides a required device gate; an index links to a deleted helper; a handoff cites output from another checkout; an agent treats a historical deferred idea as scheduled work.

**Small experiment:** compare fresh-context navigation on six fixed questions before/after a draft routing guide: where to add a closure repro, how to run its class, which device runtime is required, where its generated wrapper is, how to investigate a missing symbol, and which baseline can legitimately change. Record wrong answers and missing safeguards, not just bytes saved. See the mixed external evidence below.

### V05 — Query existing evidence instead of repeatedly reading giant logs

**Position:** recommended; presentation over existing parsers.

There is already substantial structured evidence: `TestClasses.g.txt`, runtime JSONL, unit TRX, generation reports/artifact manifests, and the [release manifest](../../build/Models/ReleaseGatesManifest.cs). Add a small read-only inspection surface that answers: what ran, for which inputs/runtime, what failed, where the raw evidence is, and which supported command can isolate the failure.

For runtime failures, return the exact test identity, launch outcome, exception/crash evidence, and relevant generated-symbol locations when traceable. Distinguish a missing entry point, a compile error, an assertion failure, and an unclassified crash. If symbol ownership cannot be established, say so instead of guessing a likely emitter.

For test discovery, use the [existing inventory](../../build/Models/TestClassInventory.cs) rather than a handwritten feature-to-test map. Inventory absence/staleness must be distinguishable from zero matching tests. Source locations are optional unless their correspondence is proven. Runtime JSONL already uses per-launch tokens; reuse that protection. A launch token establishes which launch produced a result, not which source tree produced its binary.

**Failure tests:** truncated logs hide an earlier root error; an empty inventory is reported as “no tests”; a previous run's JSONL is selected; a cached artifact is mistaken for a current runtime pass; a suggested reproduction command omits smoke flags or runtime flavor.

**Small experiment:** assemble compact summaries from saved full, filtered, failed-test, failed-launch, and missing-artifact runs. Ask a fresh agent to identify the next diagnostic action. Judge answer correctness and number of raw log reads. Preserve source parser verdicts and links; the summary must never invent stronger assurance.

### V06 — Isolate mutable work before increasing parallelism

**Position:** required design consideration for concurrent validation; not a blanket new implementation commitment.

Current platform lanes share `BindingTests/.build` and `BindingTests/output`; regeneration deletes these directories. Apps reference shared output paths. Package gates also publish into shared SDK tools paths and touch global NuGet caches. `VersionScope` already avoids changing tracked version files, but its staging directory is keyed by version within a checkout. Temporary outputs such as `/tmp/runtime-tests-run-{attempt}.jsonl` also need run-specific names before simultaneous invocations. See [VersionScope](../../build/Helpers/VersionScope.cs), [mixed-pack](../../build/Build.BindingTests.MixedPack.cs), and [runtime harness](../../build/Build.RuntimeTests.cs).

Independent read-only investigation can run concurrently now. Concurrent builds/packaging need isolated artifact roots and controlled access to shared device, simulator, bundle-ID, and package-cache state. Worktrees isolate repository files, not the entire host. Start by serializing conflicting resources; add more parallelism only when measured host capacity supports it.

If the user/agent keeps editing while validation runs, a result must identify the fixed source snapshot it tested, including uncommitted inputs. A Git SHA alone is insufficient. Merely comparing files at start/end also misses an edit-and-revert during a run; immutable inputs provide stronger evidence.

**Policy boundary:** [not-planned.md](not-planned.md), final mid-run-source-edit entry, currently defers a detection feature pending an observed false green. This research does not assert that trigger occurred or authorize implementation. Any future overlap feature must explicitly resolve its source-identity contract; that is a dependency of the proposed workflow.

**Failure tests:** simultaneous iOS/macOS generation; two same-version pack jobs; the same bundle installed twice on one simulator; edited/deleted untracked input; a shared NuGet package removed during restore; interrupted lease owner.

**Small experiment:** inventory mutable paths and host resources first. Later, validate two isolated host-only preparations with deliberately different inputs before attempting parallel runtime launches. Avoid introducing a persistent build service until simple directories and bounded scheduling prove insufficient.

### V07 — Eliminate repeated downstream launcher diagnosis

**Position:** supported by recorded incidents; implementation primarily belongs in `swift-dotnet-packages`.

The existing [reliability investigation](regression-harness-reliability.md) records a 30-second first-marker timeout against observed startup times around 29.9–32.4 seconds, plus device launcher aborts. These are recorded downstream observations, not fresh reproductions in this research pass.

Separate installation, launch confirmation, first application output, and test execution. Use measured startup distributions and host load to set budgets. Reuse the conservative [LaunchDiagnostics](../../build/Models/LaunchDiagnostics.cs) distinction already implemented here. Preserve attempts and rerun only positively identified infrastructure failures, preferably against the same verified bundle.

**Failure test:** no first marker can also mean an early product crash or deadlock. A green on another runtime or unchanged public API surface does not prove this runtime is healthy. Classification needs positive launcher evidence; unknown stays unknown and blocks claimed coverage. “Retry until green” can conceal genuine intermittent product defects.

**Small experiment:** reclassify saved failing launches and compare with app/crash evidence; then observe representative cold/warm launches under bounded concurrency. Verify that known assertion failures and early product crashes are never auto-cleared by the infrastructure retry path.

### V08 — Make minimal repros and focused fixtures a cheap habit

**Position:** recommended practice; fixture splitting remains conditional.

For a consumer-discovered defect, preserve the smallest Swift shape that still fails and the precise C# runtime assertion. Prefer an existing domain fixture and source-generator discovery. A helper that finds a close example or scaffolds the two files may be useful; it should not infer ownership, skip attributes, or expected values from failing output.

Try artifact reuse before restructuring the monolithic Swift fixture. If compilation remains dominant, pilot one independently buildable domain. Retain explicit cross-module and broad-corpus checks; many tiny projects can multiply restore and graph overhead.

**Failure test:** minimization removes a generic constraint, non-frozen layout, concurrency condition, or library-evolution setting that caused the original defect. The tiny test goes green while the consumer still fails. Require the original failure to reproduce before the fix, and retain a relevant consumer check when equivalence is uncertain.

**Small experiment:** take one already-understood consumer regression, document which properties of the input must survive minimization, and compare setup/diagnosis time. Follow the [existing gradual narrowing of real-library validation](roadmap.md#long-term-retire-nuke-validate); its retirement criterion has not been established as satisfied.

### V09 — Refactor a demonstrated source of repeated mistakes

**Position:** selective investment, not a comprehensive architecture program.

Pick a seam where recent fixes required the same semantic decision in several places. A shared computation can improve AI development by shrinking the set of files an agent must change consistently. The recent commit `58bc62ce` (closure public delegate type and trampoline cast from one computation) is an example of this direction, not a new proposal to redo that fix.

Prefer one authority for a real invariant and semantic tests around it. Avoid unifying superficially similar emitters whose differences encode ABI rules. The [roadmap's async consolidation decision](roadmap.md#strategic-posture-post-014) and [architecture inventory](Future/post-1.0-architecture-roadmap.md) are prior art, not permission to reopen every deferred design.

**Failure test:** an abstraction requires callers to pass many booleans and still duplicate policy; a generic “universal marshaler” erases ownership distinctions; a refactor changes emitted names despite matching compilation results.

**Small experiment:** follow one recurring defect family through a few historical fixes. Count independent policy decisions and files an agent must inspect/change. Prototype only the repeated decision, preserving generated surface and runtime behavior. File count and LOC reduction alone are not success criteria.

### V10 — Expose focused unit tests and current-output sweeps through Nuke

**Position:** recommended small workflow improvement; suite restructuring is optional.

[Build.Test.cs](../../build/Build.Test.cs) runs the unit project with a full-suite pass floor and no test filter in that target. Provide a separate supported focused invocation that never evaluates or updates that full-suite floor. Unknown filters and zero selected tests should be explicit failures for an intended test run. Do not instruct agents to bypass the project's Nuke workflow with ad hoc commands.

Also expose the generated-output sweep category as a named target. [CI already runs it](../../.github/workflows/ci.yml) after generation with `SWIFT_BINDINGS_REQUIRE_GENERATED_BINDINGTESTS_OUTPUT=true`. Bare-checkout unit tests intentionally skip those assertions; local completion should make it obvious which fresh generated artifacts the sweep consumed. Reuse that category and environment contract.

The saved TRX suggests an optional development lane separating pure logic tests from expensive SDK/restore/compile tests could help. First measure it. Some tests already carry `Category=CompileSmoke`; reuse existing traits where suitable. Starting a process is a weak cost proxy: a cheap probe and a full build are different. Classification must reflect measured work and dependencies, not merely “slow” names; full `nuke test` retains all current tests and baseline checks.

**Failure tests:** focused test writes a lower baseline; bare-checkout skips are represented as actual assertion execution; a generated-output sweep consumes yesterday's bindings; categorization causes an SDK-affecting change to miss its only meaningful assertion.

**Small experiment:** add no new test logic initially. Draft the exact target contract and check it against filtered, zero-match, bare-checkout, and fresh-generation scenarios. Benchmark categorization only if focused invocation leaves a significant measured delay.

### V11 — Bound routine AI reviews and make “no finding” valid

**Position:** strong low-cost candidate; change defaults, not evidence standards.

[codebase-audit.js](../../.claude/workflows/codebase-audit.js) defaults to `heavy`: three finders per round, two base rounds (up to four), and two verifiers for each of up to twelve selected findings. That means six finder invocations before optional rounds and up to twenty-four verifier invocations in that verification stage. This is a static configuration count, not evidence that ordinary tasks currently invoke this workflow or that every invocation reaches the cap.

The finder prompt says an audit should rarely return zero findings and asks for a P2 “No defects found” entry otherwise. Permit an empty findings array with a separate inspection summary. Keep explicit deep-audit mode, but make routine reviews narrow and driven by a diff, reproduction, or concrete uncertainty. Preserve the workflow's existing guards against unreachable claims, severity inflation, already-fixed reports, and unsupported dead-code claims.

**Failure test:** a cheap review misses a cross-cutting ABI defect. Use independent adversarial review where consequence and uncertainty warrant it, and retain required runtime proof. Conversely, two models agreeing is not independent empirical evidence; they may share the same mistaken premise.

**Small experiment:** replay a small set of known-fix and no-defect review tasks at fixed source snapshots with the same model/settings, bounded scope, and hidden reference findings. Compare valid defects found, false positives, reviewer repair time, and total tool/model cost. Fresh contexts and held-out tasks reduce answer leakage; do not optimize merely for fewer findings.

### V12 — Share package preparation without caching away packaging tests

**Position:** measure-first candidate, later than focused commands and fixture reuse.

[MixedPack](../../build/Build.BindingTests.MixedPack.cs) and [MixedDirect](../../build/Build.BindingTests.MixedDirect.cs) both publish the generator, pack a local feed, and clear package caches. MixedPack already shares its prepared feed across its own sim/device consumers. Further sharing is safe only for identical source, versions, toolchain, parser/native products, and package content.

Consider an immutable feed producer with exact package hashes and isolated consumer restore directories. Preserve fresh consumers for each supported consumption mode. Preserve deliberate framework re-copy/re-publish transitions in App Store hygiene; those are the test, not redundant setup.

**Failure tests:** a fixed version restores an older nupkg; changed version changes the embedded runtime contract; global cache deletion disrupts another task; reused consumer `obj/` conceals a packaging error; the supposedly shared feeds have different dependency versions.

**Small experiment:** time publish/pack/restore and compare package content manifests across an existing release-oriented sweep. Only pilot producer reuse where input/version equivalence holds. Test an intentionally stale same-version package in an isolated cache.

## Additional hypothesis worth retaining

**Narrow Apple snapshot invalidation after measuring misses.** `ComputeAppleSnapshotFingerprint` hashes the whole `Build.RuntimeTests.cs` alongside a broad generator/runtime/supplement fingerprint. A launcher-only edit can therefore invalidate enabled framework snapshots. Extracting a small snapshot producer/configuration may reduce unnecessary misses. It must still include inline templates, SDK files, generation flags, and actual dependencies; hashing only the generator DLL is not sufficient. Start by logging reasons for misses, not by weakening the existing stamp. This is subordinate to V01/V02, not a separate immediate project.

## Adversarial review: what changed after challenging the ideas

The first pass included independent read-only investigation of artifact behavior and agent workflows, followed by an adversarial review. This is source-level pressure-testing, not an implemented performance experiment. No candidate is labeled benchmark-proven.

| Initial intuition | Counterexample checked | Revised conclusion |
|---|---|---|
| Prepare sim/device once | Simulator uses compile-check and async-wrapper preparation; device uses device wrappers; regeneration deletes shared files | Start with shared immutable Swift fixture preparation. Share generated artifacts only after equivalence and lifecycle checks |
| Add partial/full run protection | The runtime baseline already excludes class-filter/skip-build runs; skip-regen alone remains a full runtime lane | Preserve that distinction and expose it. Add equivalent semantics only where a new unit-filter path needs them |
| Add an agent-friendly test report | Existing JSONL tokens already reject results from the wrong launch | Adapt existing parsers; add source/artifact provenance separately instead of replacing token protection |
| Move the big guide into scoped rules | Broadly matching rules could reload the same huge text for every task | Keep concise routing in scoped rules and load detailed sections on demand |
| Split slow unit tests immediately | Saved full unit execution is about 49 seconds; existing compile-smoke traits already exist | Provide focused invocation first. Measure critical-path cost before changing suite structure |
| Treat no marker as launcher failure | Product initialization can fail before a marker; unchanged surface does not prove runtime equivalence | Only positively identified launcher failures get infrastructure retries; unknown coverage remains unresolved |
| More reviewers provide more confidence | The audit prompt manufactures a P2 no-finding entry; multiple verifiers may share a mistaken premise | Allow honest empty results, use empirical probes, and escalate depth only for a concrete reason |

### Cache and evidence acceptance cases for a future prototype

This matrix is the proposed minimum adversarial exercise, **not a test suite executed in this pass**:

| Change or failure | Required behavior |
|---|---|
| Edit only generator C# | Reuse unchanged Swift fixture; regenerate affected bindings |
| Edit only a C# runtime test | Reuse fixture/bindings if their complete inputs still match; rebuild the app |
| Edit dependency Swift, delete a source, or restore old timestamps | Invalidate every affected fixture stage using membership/content identity |
| Change toolchain, architecture, minimum OS, smoke flags, embedded resources, or inherited build properties | Invalidate the stages whose actual inputs changed; no blind reuse of an incomplete existing fingerprint |
| Delete/corrupt one required cached product or interrupt a producer | Miss/rebuild; never publish or consume a partial successful entry |
| Seed a stale nupkg under the same version | Isolated restore must resolve verified package content or reject reuse |
| Run a nonexistent test filter | Explicit zero-match failure; no passing completion evidence |
| Run a class filter or reuse an app with skip-build | Partial evidence; no full-suite baseline mutation |
| Rebuild app and run whole suite with valid skip-regen inputs | Preserve the existing full runtime-gate eligibility |
| Find old JSONL or unknown source identity | Reject mismatched launch output; clearly mark source provenance unavailable |
| Change inputs while a supposed fixed-snapshot validation runs | Prevent mutation of the snapshot or refuse certification of the current edit |
| App crashes before its first test marker | Product/unknown investigation; absence of output alone cannot authorize retry-to-pass |

## Ideas rejected or deferred by this pass

| Tempting approach | Decision and reason |
|---|---|
| Run every platform in parallel immediately | Reject as an initial change: shared output directories, SDK staging, NuGet caches, and installed bundles create interference; V06 first |
| Trust `--skip-regen` as a complete cache | Reject: its checks do not identify every producer input/output; V02 requires stronger identities |
| Enable the existing verification cache globally | Defer: [not-planned.md](not-planned.md), verification-cache row, records missing inherited MSBuild/resolved-package inputs and final-verification concerns |
| Replace the build system with Bazel | No demonstrated need: apply action/input principles locally before paying for migration and another platform/toolchain integration |
| Merge all async emitters or invent a universal marshaler | Reject without new evidence: prior investigation found intentional ABI differences |
| Rewrite rendering to avoid every recovery pass | Defer: the recorded profile in `not-planned.md` reports about 248 ms rendering/restoration against larger compiler costs, with extra rounds in only 6/120 libraries; old measurements need reconfirmation, but do not justify a rewrite |
| Remove real runtime tests because parity/compilation passes | Reject: compilers cannot prove ownership, calling convention, callback lifetime, or runtime behavior |
| Drop real-library validation now | Defer: the existing retirement criterion requires repeated evidence that durable fixtures catch its discoveries |
| Reduce supported platforms or introduce public support tiers | Not proposed: this changes the product promise and conflicts with prior owner decisions in [1.0-decision-record.md](1.0-decision-record.md) |
| Add more mandatory instructions for every past mistake | Reject as a default: prefer enforced invariants, canonical references, and measured navigation improvements |
| Give every edit several AI reviewers | Reject as a default: review overhead and correlated mistakes can increase cost; reserve depth for concrete uncertainty and consequence |
| Keep retrying every failed test until it passes | Reject: a later green does not explain away an intermittent product failure |

## External research and how it changes the proposals

Primary sources consulted 2026-09-06. These support design principles or experimental caution; none demonstrates a speedup for this repository.

- **Build input identity:** Bazel documents actions with declared inputs, outputs, commands, and environment, and warns about source mutation during builds and untracked host compilers. Apply the principle to V02/V06; this is not a recommendation to adopt Bazel. [Remote caching](https://bazel.build/remote/caching).
- **Incrementality has edge cases:** MSBuild documents `Inputs`/`Outputs`, output inference, and that changed input-list membership does not automatically invalidate a target. Explicitly test source deletion and output availability in V02. [Incremental builds](https://learn.microsoft.com/en-us/visualstudio/msbuild/incremental-builds?view=visualstudio).
- **Agent context and tools:** Anthropic advocates selective context retrieval and tools with clear responsibilities and concise outputs. V04/V05 apply those ideas using existing repository artifacts rather than creating a broad new tool catalog. This is vendor engineering guidance, not a controlled evaluation here. [Context engineering](https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents), [writing tools](https://www.anthropic.com/engineering/writing-tools-for-agents).
- **Context-file evidence is mixed:** Gloaguen et al., revised June 2026, report that context files did not generally improve success and increased inference cost in their evaluations. Lulla et al., revised March 2026, report lower median runtime and output tokens in a different 10-repository/124-PR study. Different designs/settings limit direct comparison. The practical conclusion is to evaluate useful repository-specific constraints and retrieval locally, not delete all instructions or assume more documentation is better. [Gloaguen et al.](https://arxiv.org/abs/2602.11988v2), [Lulla et al.](https://arxiv.org/abs/2601.20404v2).
- **Measure human and agent time honestly:** METR's February 2026 update explains why selection effects and concurrent agent use complicated its later productivity experiment. Do not apply its earlier slowdown number to this project or current models. V01 measures elapsed time, human involvement, and quality separately. [METR experiment-design update](https://metr.org/blog/2026-02-24-uplift-update/).

## Proposed experiment order

This order is a recommendation for a later implementation decision, not authorization to run all experiments now.

1. **Observe existing work (V01):** collect representative stage times and human interventions. Keep a few examples, not an always-growing telemetry project.
2. **Pilot low-cost AI workflow changes (V04/V10/V11):** concise routing, supported focused tests/current-output sweeps, and bounded review defaults. Evaluate correctness on fixed tasks before replacing guidance.
3. **Remove duplicate preparation (V02):** prove same-invocation sim/device sharing first; then test a fixture cache in observation-only mode.
4. **Make results easier to consume (V05):** adapt existing evidence, including explicit partial/full and freshness status. This supports V03 without duplicating verdict logic.
5. **Address measured external friction (V07/V12):** launcher reliability may move earlier if human diagnosis dominates; package preparation depends on timing and isolation evidence.
6. **Consider concurrency and architectural changes (V06/V08/V09):** only at demonstrated bottlenecks, with fixed inputs and semantic equivalence checks.

Keep at most two or three experiments active. Each should record the baseline, exact proposed change, evidence of benefit, correctness counterexamples, and a keep/revise/stop decision. A failed idea is a useful result if it prevents a larger investment.

## Continuation questions

- Which part of a typical generator fix currently dominates elapsed time: Swift fixture build, wrapper compile, managed app build, runtime execution, or diagnosis?
- How often is the heavy audit workflow actually invoked, and how much follow-up does it create? Configuration alone does not answer this.
- Can simulator-only and device-inclusive input fixtures produce equivalent shared generated artifacts across smoke flags and strict input modes?
- How much package preparation is identical across gates after accounting for version constants and runtime-contract stamping?
- Which recurring edits require duplicated semantic decisions that a narrow shared computation could eliminate?
- Can a fresh agent choose the correct gate and diagnose a saved failure from a compact summary without missing an invariant?

Future updates should revise the ranked shortlist and tested conclusions rather than append repetitive session transcripts. Keep this document an on-demand research register. When a candidate is accepted, move its implementation detail to the appropriate active plan; when work closes, follow the [docs conventions](README.md).
