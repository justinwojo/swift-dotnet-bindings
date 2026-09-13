# Faster development workflows

Deferred opportunities, not an implementation commitment or mandatory reading. Source status checked
2026-09-12 at `b8a965e5f`, with the documentation reorganization in the working tree. These candidates
have not demonstrated a measured speedup here.

## Choose from measured friction

Before selecting work, sample ordinary generator, runtime and packaging changes. Use existing
TRX/JSONL and [release durations](../../../build/Models/ReleaseGatesManifest.cs) to separate fixture
build, generation, compilation, install/launch, execution and diagnosis. Record cold/warm state,
retries and human intervention. Rank repeated avoidable time against implementation and maintenance
cost. A faster run that loses coverage or requires more repair is not an improvement.

## Unfinished opportunities

### Focused Nuke workflows

[UnitTests](../../../build/Build.Test.cs) still runs the unfiltered suite with its full-suite pass
floor. Add a separate supported focused invocation that rejects zero matches and cannot update that
floor. Expose the existing generated-output sweep category through Nuke too; [CI](../../../.github/workflows/ci.yml)
already runs it with `SWIFT_BINDINGS_REQUIRE_GENERATED_BINDINGTESTS_OUTPUT=true`.

The [release workflow](../../../build/Build.ReleaseGates.cs) already composes gates. Any development
or completion entry points should reuse those contracts and explain partial coverage and remaining
requirements. Start with focused commands before considering test-suite restructuring.

### Reuse fixture preparation

The simulator and device paths in [the runtime harness](../../../build/Build.RuntimeTests.cs) still
separately build fixtures and regenerate bindings. [Fixture preparation](../../../build/Build.BindingTests.cs)
cleans `.build`, and regeneration cleans output. Existing generator and Apple-snapshot stamps do not
provide the proposed shared preparation.

Measure duplication in one composed sim/device invocation first. Share Swift fixtures only for
matching inputs; prove equivalence before sharing generated bindings. Keep device wrappers and all
runtime-specific builds and execution. Later, consider persistent module/slice reuse. If snapshot
misses matter, investigate the broad `ComputeAppleSnapshotFingerprint` inputs before narrowing them.

### Query existing failure evidence

[RuntimeTestAttempts](../../../build/Models/RuntimeTestAttempts.cs), added in `002084a81`, now retains
each launch, console output and token-validated results without clearing an earlier product failure.
The proposed read-only inspection interface is still absent.

Build on those records, existing TRX/JSONL parsers and
[TestClassInventory](../../../build/Models/TestClassInventory.cs) to answer what ran, which runtime
and inputs it used, what failed, where the raw evidence is, and which supported command reproduces
it. Report unknown provenance explicitly; do not guess symbol ownership or strengthen a parser's
verdict. Judge a pilot by correct diagnostic actions and fewer repeated log reads.

### Share package-feed production

[MixedPack](../../../build/Build.BindingTests.MixedPack.cs) already shares a feed across its own
sim/device consumers. [MixedDirect](../../../build/Build.BindingTests.MixedDirect.cs) still prepares
its feed separately. Cross-gate sharing remains unimplemented.

Measure publish/pack/restore costs and compare package manifests first. Reuse only identical source,
versions, toolchain and package content, with immutable feeds and isolated consumer restore state.
Fresh consumers and deliberate framework re-copy/re-publish transitions remain part of the tests.

### Reduce remaining instruction and review overhead

Subsystem notes now replace the large deferred register, but [CLAUDE.md](../../../CLAUDE.md) still
contains detailed gate descriptions. A remaining option is one canonical harness guide, reached by
concise routing from applicable scoped rules. Verify that fresh-context tasks still find every
required gate before replacing guidance.

The older optional [audit workflow](../../../.claude/workflows/codebase-audit.js) still defaults to
heavy fan-out and requests a P2 “No defects found” entry. If that workflow is used enough to matter,
allow honest empty findings and make its depth explicit. This is separate from the bounded personal
paired-review workflow; do not introduce another review loop.

## Correctness checks for any prototype

- **Complete cache identity:** include source membership/content, dependency artifacts, SDK/compiler,
  target/architecture/minimum OS, flags and inherited build inputs. Exercise dependency-only edits,
  deleted sources and preserved timestamps. Missing/corrupt products or interrupted writes must miss;
  publish complete entries atomically. `--skip-regen` is not a complete cache key.
- **Package identity:** test a stale nupkg under the same version. Verify actual content and keep
  consumer restore state isolated; global cache deletion can disrupt another run.
- **Evidence boundaries:** reject zero-match filters and stale launch output. Focused or skip-build
  runs cannot update full-suite baselines. Valid skip-regen plus a rebuilt app and whole-suite run
  retains its existing full runtime-gate eligibility. Reused artifacts are not reused test passes.
- **Safe concurrency:** shared output directories, devices, bundle IDs and package caches need
  isolation or serialization. Fixed-snapshot validation must include uncommitted inputs; a Git SHA or
  start/end comparison alone cannot prove which bytes ran. Compiler-path attribution is not an
  immutable build snapshot. Keep the existing [source-edit concern](notes/tooling.md#a-long-running-gate-does-not-notice-source-edits-made-after-it-started)
  deferred unless new evidence or an explicit scope decision activates it.
- **Preserved behavior:** retain applicable simulator/device/packaging gates, API surface and skip
  identities. Missing first output can mean a product crash; only positively identified launcher
  failures justify infrastructure retries. Preserve failures across attempts.

## Related work and disposition

Downstream launch reliability retains [historical observations and diagnostic cautions](../Design/environmental-troubleshooting.md#historical-downstream-regression-observations).
The separate launch budget and launcher-failure handling already exist in the owning repository;
recheck its current implementation before proposing further changes. Minimal consumer repros and selective refactoring
are established practices, not outstanding projects here. Fixture splitting and architectural
changes need a measured bottleneck first. Existing [verification-cache restrictions](notes/recovery.md)
and the [real-library validation retirement criterion](validation-evolution.md) remain in force.

Select one bounded experiment when its recurring cost is demonstrated. Record the baseline, exact
change, correctness counterexamples and keep/revise/stop decision. Move accepted implementation
detail to an active plan; revise or remove this proposal as opportunities close rather than append
session history.
