# Binding Resilience: Next-Impact Plan

**Status:** Recommendation after binding-resilience wave 1, ingestion hardening, and binding-resilience wave 2  
**Evidence reviewed:** the three completed session directories, the binding-resilience gameplan (since superseded by this doc; its two still-live sections are preserved as Appendices B and C below), the current `swift-bindings` implementation and tests, the live 120-package corpus output, and the current `spm-to-xcframework` implementation and receipts  
**Corpus snapshot used:** 50/120 green (42 full, 8 degraded), 70 red

## Executive recommendation

The next program should not be another broad emitter-hardening wave, and it should not begin by simply enabling the existing recovery graph. The most impactful sequence I see is:

1. Start the C#-compile-red sprint immediately.
2. In parallel, restore the real-library validation baseline and add the durable terminal failure dossier.
3. Make the already-proven 12/10/10 corpus split visible with a small reporting-script change.
4. Cut 0.18.0 after the compile-red fixes, validation restoration, and failure dossier are ready.
5. If the owner funds a post-release cross-repo program, extend coarse recovery only where production can prove the complete dependent closure, close managed sibling graphs, and teach `spm-to-xcframework` to produce import-closed output iteratively.
6. Re-sweep, preserve full versus degraded status, and route runtime coverage for moved mechanisms through BindingTests.

This ordering deliberately favors a sound, diagnosable binding over a superficially higher green count. It should still raise the count: the current corpus contains eleven realistic mobile-framework candidates that already reach C# compilation, one additional keyword-escaping mechanism exposed by swift-argument-parser, and six named-input reds blocked by sibling C# compile failures. I would not attach a numerical promise because several packages have more than one compile defect and not every mechanism is shared.

## The north star needs two contracts

“Any xcframework becomes a usable binding” and “any Swift package becomes an xcframework” are related but different goals. Keeping them separate makes failures actionable.

### Binding-generator contract

Given a valid, import-closed xcframework set, the generator should produce one of:

- a compiling full binding;
- a compiling degraded binding, with every omitted surface and reason recorded; or
- a non-publishable result with a structured explanation of the exact blocking surface, evidence, and next action.

It should never publish an unsound binding, and it should not report success when no usable module remains. Graceful skipping is a successful outcome only when the retained surface compiles and remains ABI-safe.

### SPM conversion contract

Given a Swift package, `spm-to-xcframework` should produce one of:

- a valid, import-closed xcframework set for the selected products; or
- an atomic failure receipt that identifies the unsupported source, dependency, product, platform, or toolchain constraint.

Some packages cannot be made into distributable xcframeworks under the current toolchain without changing upstream source or violating library-evolution constraints. Those should be excellent refusals, not fake successes and not permanent generic `convert_failed` entries.

### What “usable” should eventually mean

The present 50/120 headline is a compile result, not proof that every generated surface works at runtime. Compile success remains the right broad sweep metric, but every touched ABI mechanism should be reproduced as a durable Swift pattern under `BindingTests/Sources/SwiftBindingsTestLib/` and exercised through the existing simulator/device legs as appropriate. The temporary corpus harness should remain a compile and acquisition signal, not grow a second runtime-test system.

## What the current 70 reds actually say

The current top-level split is:

| Current bucket | Count | What it really contains |
|---|---:|---|
| `convert_failed` | 36 | Import closure, manifest pruning, binary artifact discovery, minimum-OS/product selection, C/C++ header problems, and genuine upstream/toolchain incompatibilities |
| `cross_module_fact_resolution` | 32 | 12 C# compile failures, 10 generator failures, and 10 named-input failures; this is not one failure class |
| mixed Objective-C | 2 | A separate ingestion path and denominator |

The `cross_module_fact_resolution` label is too broad for planning. Its classifier admits `RequiresGraphClosure`, missing metadata/accessor markers, `NoProgress`, `IterationCap`, and `Unattributable`. It therefore folds ordinary emitted-C# defects and sibling build failures into a graph-shaped label.

The 32 should be treated as these three cohorts:

| Actual terminal stage | Count | Packages |
|---|---:|---|
| C# compile failed | 12 | CombineCocoa, DGCharts, Eureka, Factory, JTAppleCalendar, Macaw, Moya, SwiftDate, SwiftMessages, SwiftUICharts, rive-ios, swift-argument-parser |
| Generator failed | 10 | Amplitude-Swift, Auth0.swift, CoreStore, Euclid, SocketIO, SwiftLocation, SwiftSoup, Time, swift-protobuf, swift-system |
| Named input unavailable | 10 | CocoaMQTT, MessageKit, RevenueCat, SwiftDraw, SwiftOTP, epoxy-ios, swift-clocks, swift-dependencies, swift-identified-collections, swift-numerics |

I would keep `swift-argument-parser` outside the mobile-framework headline cohort, but fix the mechanism it exposes in this sprint: the generator turns the keyword-escaped `@abstract` into `__@abstractStr`, which is invalid C#. This is a general identifier/keyword-escaping bug even if command-line products are not a primary product target.

The named-input cohort also needs reclassification. RevenueCat artifacts were produced, but the primary RevenueCat module failed generation, leaving RevenueCatUI unusable. Six of the ten named-input reds are specifically blocked on a sibling C# compile failure:

- MessageKit → InputBarAccessoryView;
- swift-identified-collections → OrderedCollections;
- SwiftOTP → Crypto;
- epoxy-ios → EpoxyBars;
- SwiftDraw → SwiftDrawDOM; and
- swift-clocks → IssueReporting.

This means compile-red mechanism fixes can cascade into the named-input cohort before any broader graph work. CocoaMQTT is different again: its run-scoped `_feed` was empty even though Starscream should have been built in-run, so I would investigate it first as a suspected cheap corpus-harness bug rather than classify it as graph-closure work. `Testing` and `_NumericsShims` appear to be genuinely absent inputs in other candidates.

## Program 0: preserve durable terminal failure evidence

The durable work here is generator-side failure evidence. It can land in parallel with Programs 1 and 2; it gates Program 3's scope selection, not the compile-red sprint.

### 0A. Make the existing artifact split visible

Do not turn `internal-binding-testing` into a product or telemetry platform. The existing `output/*/result.json` files already support the verified 12 C# compile / 10 generator / 10 named-input decomposition. Make a small change to the current summary script so it reports those terminal stages instead of presenting all 32 as `cross_module_fact_resolution`.

Keep the fixed 120-package headline, full/degraded distinction, and existing artifacts. No second denominator, provenance subsystem, classifier rebuild, or durable corpus schema is warranted.

### 0B. Always emit a terminal failure dossier

The successful degraded path is already fairly transparent. `SWIFTBIND025`, `binding-report.json`, and `binding-artifact-manifest.json` identify the skipped declaration and why it was omitted.

The nonconvergent module path is much weaker. `SWIFTBIND111` currently reports the module, round count, and a broad cause, while the recovery controller discards the terminal attribution details and the CLI resets the report collector on failure.

Add an always-written `binding-failure-report.json` (the exact name is negotiable). Its schema should be versioned and stable enough for Program 3 to consume without coupling readers to current recovery internals.

#### Minimal schema to freeze now

I would freeze these semantic fields:

- `schemaVersion`;
- module and input identity, including an input fingerprint;
- terminal outcome, stage/plane, stable reason code, and recovery round count;
- diagnostics as records with tool/plane, code, severity, normalized message, source span, and fingerprint;
- attributed units as records with a stable recovery-unit/declaration ID, display name, scope, classification/confidence, and diagnostic references;
- the recovery decision: seed IDs, proposed/actual withdrawal IDs, authorization outcome, obstruction code, and blocker unit ID;
- report/artifact paths needed to inspect the failed attempt.

Arrays and optional extension objects should permit additive evolution. Stable IDs, enums, and relationships are the contract; remediation prose, exact compiler wording, and today's internal class names should not be. Program 3 can then add captured edges, completeness witnesses, and closure details without changing the core interpretation of a diagnostic, attributed unit, or blocked recovery decision.

The console should summarize the first useful errors and point to the report. The full report should survive every nonzero generator exit.

### Exit criteria

- The reporting script exposes the already-derivable 12/10/10 split.
- Every successful skip has a declaration-level row.
- Every failed module has an always-written terminal evidence artifact.
- A Program 3 reader can consume the frozen fields without understanding current controller classes.

## Program 1: restore the correctness baseline

The real-library validation baseline predates substantial resilience work. It should be reconciled before further widening the set of surfaces the generator may withdraw.

I would run the existing validation cohort from fresh artifacts and classify each difference as:

- a true generator/runtime regression;
- an expected, explicitly reviewed degradation;
- a stale package/toolchain expectation; or
- harness noise.

Do not rebaseline a failing library simply because the corpus count improved. Existing notes about `SWIFTBIND108` and ABI-sensitive behavior deserve resolution first.

The cheap validation-only path already exists through Nuke's built-in skip support, for example:

```bash
nuke validate --filter GRDB --skip PackGate BehaviorTier
```

My attempted five-library filter matched nothing because `--filter` is intentionally one case-insensitive substring (`ValidationManifest.cs:50`), not a regex or list. The only tooling gap is multi-library selection. If useful for this restoration pass, address it as a small comma-split of the existing `Contains` matcher, not as a separate session deliverable. I do not have a current pass/fail conclusion for GRDB, XMLCoder, RealityFoundation, Lottie, or CryptoSwift from this review.

### Exit criteria

- Each baseline library has a current result from a fresh input.
- Every difference has an owner-reviewed explanation.
- ABI or runtime failures remain release-blocking even if a package compiles.
- Each library can be rerun validation-only with `--skip PackGate BehaviorTier`; any multi-library convenience remains a tiny matcher change.

## Program 2: attack the 12 C#-compile reds

This is my highest-confidence green-count opportunity and should start immediately, without waiting for the failure dossier. These packages already crossed conversion and Swift-side generation, and their current compiler diagnostics are sufficient to begin root-cause work. Eleven are realistic mobile-framework headline candidates; swift-argument-parser adds a general naming bug worth fixing outside that headline.

### Honest sprint sizing

My best estimate is **roughly eight to ten root mechanisms across the 12 packages**, not three broad fixes and not 12 wholly independent fixes.

The Apple surface oracle is visibly implicated in three packages: SwiftDate, SwiftMessages, and DGCharts. I would guess a complete oracle fix could be sufficient for the first two, but only partial for DGCharts, which also has missing locals and proxy/reference errors. The remaining failures divide into several adjacent but probably distinct mechanisms: final reference/proxy closure, generic-constraint projection, tuple projection, collection variance, identifier/name hygiene, and local/callback emission state. Some of those categories may collapse after minimization, while DGCharts and rive-ios each demonstrate that one package can require multiple fixes. For planning, I would size this as several focused fixes over a sprint, not one oracle change that turns all 12 green.

### 2A. Apply the Apple platform surface oracle everywhere

Current failures include shapes such as:

- `Foundation.CalendarComponent`, which does not exist in the target .NET surface;
- a static `UIWindowLevel` type emitted as though it were an instance parameter type; and
- invalid CoreGraphics payload/projection usage.

`AppleTypeSurfaceIndex` is a good foundation because it reflects the installed Microsoft.iOS reference assembly. However, the current evidence suggests it protects synthesized fallback records more consistently than pre-existing database records and other projection paths.

Make the actual target framework surface authoritative at every Apple-type ingress and at final signature validation, not only during fallback synthesis. Prefer one shared correction/validation mechanism over special cases for each library. Use DGCharts, SwiftDate, and SwiftMessages as the first canaries.

This is a strong recommendation, but the evidence supports an honest expectation of two likely full beneficiaries and one partial beneficiary, not a claim that all Apple-adjacent failures share one root cause.

### 2B. Close emitted proxy and conformance references

CombineCocoa, DGCharts, Moya, and rive-ios currently show missing runtime, proxy, conformance, or generated helper references. Determine whether each reference is:

- a surface that should have been emitted;
- a dependent surface that should have been withdrawn with its provider;
- a sibling module/package reference that was not wired into C# verification; or
- an invalid projection that should never have been referenced.

The existing integrity gates should become end-to-end guarantees over the final retained syntax tree, not only checks at one creation path. Avoid substituting placeholder types when the generated member would then compile but be semantically false.

### 2C. Fix recurring signature and generic-shape defects

The remaining compile failures include:

- incompatible generic constraints;
- tuple element/type projection mismatches;
- duplicate generic parameter/member names;
- collection-variance mismatches, specifically Eureka's emitted `IEnumerable<Section>` to `IEnumerable<BaseRow>` incompatibility;
- missing local temporaries or callback fields; and
- invalid generated identifiers and keyword escaping.

Eureka, Factory, JTAppleCalendar, Macaw, SwiftUICharts, and rive-ios provide concrete canaries. swift-argument-parser should cover the separate keyword-escaping defect: prefixing or suffixing an escaped identifier must never produce an embedded `@` such as `__@abstractStr`. First reduce each to the generator mechanism responsible, then fix the shared mechanism and add a minimized fixture. Do not add a prediction gate merely because Roslyn found a new shape: compiler-driven verify/recover is already the intended oracle for compiler-catchable failures. Add an earlier gate only when it encodes a genuine semantic or ABI invariant.

### 2D. Let sound leaf recovery finish the job

Where Roslyn attributes a failure to an isolated, ABI-safe leaf, the current recovery loop should withdraw it and report it. If it does not, fix attribution or leaf identity rather than broadening the withdrawal scope prematurely.

When the same mechanism recurs across libraries, prefer the root fix so the release is more complete, not merely more degraded.

### 2E. Capture the likely sibling cascade

After each compile-red mechanism fix, rerun the six sibling modules that currently block MessageKit, swift-identified-collections, SwiftOTP, epoxy-ios, SwiftDraw, and swift-clocks. Those failures may expose the same generator mechanisms even though their parent packages appear in the named-input cohort. This coupling raises Program 2's expected value and reduces the amount of work Program 4 may ultimately require.

Treat CocoaMQTT separately: first correct or disprove the suspected empty-`_feed` harness bug. It should not motivate production graph machinery unless Starscream is still unavailable after the in-run feed behaves correctly.

### Verification and exit criteria

For each mechanism:

- add a focused unit or BindingTests fixture;
- run generator unit tests and compile-only BindingTests;
- reproduce affected ABI/runtime patterns in `BindingTests/Sources/SwiftBindingsTestLib/` and run the existing simulator/device legs when the change affects ownership, layout, calling conventions, closures, async, or reverse dispatch;
- rerun only the affected corpus cohort from fresh artifacts;
- confirm that every new degraded green has complete skip reporting.

The program exits when the eleven headline candidates and the swift-argument-parser naming mechanism are either fixed, safely recovered, or moved to a more accurate evidence-backed class; the six blocked siblings have also been retested. It does not require forcing every package to green.

## Natural 0.18.0 release cut

Programs 1 and 2 plus the Program 0 failure dossier form a coherent 0.18.0 payload. Validation restoration directly unblocks the pre-release sweep; the compile-red sprint improves real output; and the dossier turns the remaining module failures into durable, actionable product diagnostics. The small corpus summary-script edit can accompany them but is not release substance.

Programs 3 through 6 should not hold that release. As a combined roadmap they depend on an owner decision that has not yet been made: whether to fund a cross-repo program spanning `swift-bindings`, `internal-binding-testing`, and `spm-to-xcframework`. Even Program 3's local generator work is best selected using the dossier and fresh post-release evidence. This preserves the recorded trajectory: 0.18.0 → usage feedback → maintenance posture or a deliberately funded next resilience wave.

## Program 3: make coarse recovery real, one provable scope at a time

There is now real demand for recovery above a leaf: many generator failures terminate with `RequiresGraphClosure`. But the existing graph model is infrastructure, not an enabled solution.

Important current facts:

- `RecoveryGraphBuilder`, `RecoveryCompletenessGate`, and `RecoveryAuthorizer` have no production caller.
- `InEmissionDriver` does not authorize coarse withdrawal, so a coarse culprit necessarily ends at `SWIFTBIND111`.
- Production witnessability currently admits only `LeafApi` and `AccessorGroup`, which the recovery loop already handles through the existing leaf path.
- shared helper bundles, C# type references, conformances, type representation, and type surfaces carry semantic, layout, or not-yet-materialized dependencies that cannot currently be proven complete.

Therefore, “wire the graph” is not a safe standalone task. The work is to create the missing witnesses and capture paths.

### Recommended progression

1. Use the new failure dossiers to count exact blocking scopes and select one recurring high-value scope.
2. Define every structural and render-emergent dependency that can point into that scope.
3. Capture those edges in production as artifacts are emitted.
4. Independently re-derive completeness from settled Swift/C# output; do not use the same journal as both claim and witness.
5. Materialize a Roslyn syntax tree when C# type-reference completeness is required.
6. Authorize the smallest dependent-closed withdrawal set only when Gate 0 can actuate every exact unit.
7. Record every cascaded omission in the binding report.
8. Stay fail-closed on unmodelled, semantic, conformance, initializer-ordering, or layout dependencies.

One possible lattice improvement is splitting pure symbol-callee helpers from NativeAOT registration helpers. The current `SharedHelperBundle` scope mixes witnessable helper calls with semantic conformance/initializer obligations, making the whole scope correctly non-authorizable. I regard this as a promising direction, not yet a recommendation to implement: the failure dossiers should first show that it recurs enough to justify a lattice change.

Similarly, a Roslyn witness could make `CSharpTypeReference` complete, but it does not solve conformance or layout witnessability. Whole-type withdrawal must continue through its dedicated path and must remove every dependent surface; module-wide implication remains failure rather than degradation.

### Exit criteria

- At least one previously coarse recurring scope has an independent production completeness witness.
- Production authorization is impossible when the witness or exact actuation identity is missing.
- An affected corpus compile canary demonstrates safe cascading withdrawal and a usable retained module.
- BindingTests compile, simulator, and device legs cover any affected ABI/runtime mechanism.
- The program is judged by converted failures and soundness evidence, not by number of graph classes wired.

## Program 4: make multi-module output consumable as a graph

Producing several xcframeworks is not enough if their managed bindings cannot be generated, compiled, restored, and packed together.

The named-input cohort should be split into:

1. artifact genuinely absent from the conversion receipt;
2. artifact present but its own binding generation failed;
3. binding generated but C# verification/packing failed;
4. binding package produced but unavailable to a dependent in the run-scoped feed; and
5. optional sibling product failed while an independent requested product remained usable.

If the owner funds this cross-repo work, make the durable generator/packaging orchestration topological. Change the temporary corpus harness only enough to exercise and report that behavior:

- consume the converter receipt as the authoritative module set;
- generate and pack dependencies before dependents;
- expose successful sibling bindings through a run-scoped local feed or project graph;
- do not claim a dependent module green until its managed dependency closure restores and compiles;
- allow independent modules to remain publishable when an unrelated optional product fails;
- report package-level and module-level outcomes separately.

RevenueCat/RevenueCatUI and one of the six compile-blocked sibling graphs are suitable canaries. CocoaMQTT/Starscream should first be handled as the suspected empty-feed harness bug identified in Program 2, not as a production graph canary. `Testing` and `_NumericsShims` should remain in the genuinely missing-artifact cohort unless converter evidence shows they are buildable products under the selected configuration.

### Exit criteria

- Every named-input failure identifies which of the five states above occurred.
- Receipt-produced siblings are built and packed in dependency order.
- Managed restore sees all successful in-run sibling packages.
- Independent successful modules survive an optional sibling failure without misrepresenting dependent usability.

## Program 5: move `spm-to-xcframework` from closure detection to closure production

The converter now atomically writes a receipt and refuses to promote an import-open xcframework set. That is an important correctness improvement. The next highest-leverage converter change is to satisfy the closure it already detects.

### 5A. Iterative import-driven closure

The current converter can build external referenced products and some sibling products, then checks for missing public imports. It does not synthesize/build internal regular or Clang targets that are imported publicly but are not exposed as package products. Child expansion is also shallow.

Implement a bounded fixed point:

1. build the selected products;
2. inspect public module imports in the resulting interfaces/modules;
3. map each missing import to a package target or external product;
4. synthesize a temporary dynamic product for eligible internal regular/Clang targets;
5. build and add it to the staged set;
6. repeat until import-closed, cyclic, capped, or blocked by a static/toolchain-only target;
7. record every discovery, synthetic product, and blocker in the receipt;
8. promote only the complete set atomically.

FlexLayout (`FlexLayoutYogaKit`) and Pulse (`PulseObjCHelpers`) are direct canaries. This is the converter change for which I have the strongest immediate confidence.

The eligibility rules need to stay conservative. Tool/plugin/macro targets, executable-only products, resources that cannot be redistributed, binary artifacts without a usable slice, and targets that cannot support library evolution should produce an exact refusal rather than broad source rewriting.

### 5B. High-value converter mechanism pilots

After import-driven closure, choose one candidate per recurring mechanism rather than attacking all 36 conversions at once:

| Mechanism | Candidate | Current evidence / likely investigation |
|---|---|---|
| Manifest pruning consistency | Sentry | Staged products refer to targets pruned from the manifest |
| Transitive binary artifact discovery | Adyen | Required `adyen-3ds2-ios` artifact is not found in the expected artifact location |
| Product selection and minimum OS | Braintree | An unwanted UI product raises an iOS 17 requirement while requested core/payment products may be independently buildable |
| Transitive manifest dependency closure | Datadog | Pruning leaves an unresolved swift-atomics dependency |
| Vendored C/C++ header closure | PostHog | PHPLCrashReporter headers are incomplete in the build context |
| Deployment-target floor | Hue | The current floor reaches a `libarclite` failure |

For each pilot, prefer a general rule with a receipt-backed regression test. Avoid silently patching arbitrary upstream source, advertising library evolution when the artifact does not support it, or building every product when the user selected a smaller viable set.

I am less certain that the last four mechanisms belong in the same release. Import-driven internal-target closure and manifest consistency look broadly reusable; individual C++ build systems and packages that violate current Swift library-evolution constraints may have poor return on maintenance cost.

### Exit criteria

- FlexLayout and Pulse either produce import-closed sets or expose a newly precise, irreducible blocker.
- Closure traversal is recursive/fixed-point, bounded, cycle-safe, and receipt-visible.
- At least two SDK-scale converter failures are resolved through reusable mechanisms.
- Known toolchain/library-evolution walls have stable actionable reason codes and are not counted as generic converter defects.

## Program 6: resweep and prove the moved mechanisms

Do not run the full corpus after every small patch. Use concentric gates:

1. minimized unit/BindingTests fixture;
2. affected real-library compile canary from fresh conversion output;
3. the relevant failure cohort;
4. the fixed correctness baseline;
5. the full 120-package sweep at the end of a program wave.

Runtime coverage is not a corpus layer. A mechanism with runtime or ABI implications must have its Swift shape reproduced in BindingTests and use the existing simulator/device legs.

For every full sweep, report:

- package full green, degraded green, and red;
- failures by exact terminal stage and reason code;
- number of skipped declarations by reason and scope;
- newly green, newly degraded, regressed, and reclassified candidates;
- BindingTests simulator/device status for touched ABI mechanisms.

The ratchet should reject:

- a former full green becoming degraded without explicit review;
- a former green becoming red;
- a reduced failure count caused only by dropping requested products or modules;
- a new skip without a report row;
- a success whose dependency closure cannot restore and compile; or
- a compile green that regresses the applicable BindingTests runtime coverage.

## Proposed session breakdown

I would organize the next work into bounded sessions rather than one new mega-wave.

> **Executable session docs:** the pre-release payload (Sessions A–D below) is elaborated into runnable per-session docs at `src/docs/sessions/2026-07-next-impact/` (Session D is split there into two implementation sessions plus a closeout, sized for one fresh context window each). Those docs are the execution contract; this section remains the strategic summary.

### Session A: terminal failure dossier

- make the 12/10/10 split visible with a small summary-script edit;
- preserve terminal attribution and compiler diagnostics;
- freeze the minimal stable schema;
- always write generator failure evidence.

### Session B: validation restoration

- use `--skip PackGate BehaviorTier` for validation-only runs;
- optionally comma-split the existing substring matcher for multi-library convenience;
- rerun the baseline from fresh inputs;
- fix or explicitly classify every drift.

### Session C: Apple projection closure

- make the real Microsoft.iOS surface authoritative across all type-record paths;
- canaries: DGCharts, SwiftDate, SwiftMessages;
- reproduce affected runtime/ABI patterns in BindingTests where projection changes representation or calling shape.

### Session D: final-syntax reference and signature closure

- proxy/conformance/reference integrity over retained output;
- recurring generic, tuple, variance, naming, and local-emission defects;
- canaries drawn from the remaining compile reds and six blocked siblings.

**Release cut:** Sessions A through D are the natural 0.18.0 payload. The following sessions require a post-release owner decision to fund the cross-repo program.

### Session E: production coarse-recovery pilot

- select the scope using failure-report frequency;
- build independent completeness evidence and exact actuation;
- enable one scope only, with cascading skip reports and BindingTests device coverage where needed.

### Session F: managed sibling binding closure

- classify the ten named-input candidates;
- topological generate/pack/restore;
- canaries: RevenueCat and a compile-blocked sibling graph; fix CocoaMQTT's suspected empty feed separately.

### Session G: converter import closure

- iterative internal-target synthesis;
- canaries: FlexLayout and Pulse;
- atomic receipt and promotion invariants.

### Session H: SDK converter pilots and final soak

- choose two or three reusable converter mechanisms from current evidence;
- run the fixed baseline and full corpus;
- add BindingTests runtime coverage for mechanisms that changed.

Sessions A through D can proceed in parallel where ownership allows; Program 2 does not wait for Session A. Session A's dossier must be complete before Session E selects a coarse scope. If the post-release program is funded, keep Sessions E and F sequential enough that a failure fixed by ordinary generation or managed dependency wiring is not mistakenly used to justify broader recovery.

## Priority and expected impact

| Priority | Program | Confidence | Expected kind of impact |
|---:|---|---|---|
| 0 | Twelve C# compile reds + blocked-sibling retest | High | Best near-term path to additional green/degraded-green packages, with six possible cascades |
| 0 | Terminal failure dossier | High | Durable user diagnostics and the evidence needed to select Program 3 |
| 0 | Correctness-baseline restoration | High need, unknown current result | Protects runtime/ABI quality before widening recovery |
| Small support task | Corpus summary-script split | High | Exposes existing evidence without investing in temporary scaffolding |
| Post-release 1 | Iterative converter import closure | High for named canaries | Converts a correctness gate into artifact production |
| Post-release 2 | Managed sibling binding closure | Medium-high | Recovers residual modules after compile-red cascades are removed |
| Post-release 3 | One production coarse-recovery scope | Medium | Potentially unlocks generator reds, but only after new witnesses exist |
| Post-release 4 | SDK converter mechanism pilots | Mixed | Some broad wins; some package-specific maintenance traps |

Do not start the post-release rows by inertia. Together they are a cross-repo funding decision, and 0.18.0 should not ride on them.

## Things I would explicitly not do next

- Do not make 0.18.0 wait for Programs 3 through 6 once Programs 1 and 2 plus the failure dossier are ready.
- Do not treat all remaining reds as dependency-graph failures.
- Do not enable `RecoveryAuthorizer` globally with the current production evidence.
- Do not withdraw conformance, layout, type-representation, or initializer-ordering surfaces because the binding happens to compile afterward.
- Do not add one prediction gate per Roslyn error shape.
- Do not count a produced xcframework as success until its public import closure is present.
- Do not count a generated module as usable until managed dependencies restore and compile.
- Do not silently drop requested products to improve the corpus score.
- Do not hide whole-module failure behind exit code zero.
- Do not broadly rewrite upstream packages inside the converter without a narrow, reviewable compatibility policy.
- Do not rebaseline real-library failures without understanding them.

## Open judgments for Claude/owner review

These are the areas where I do not have a strong enough basis to prescribe one answer:

1. **Scope of the corpus.** Should command-line/server-oriented packages such as swift-argument-parser remain first-class binding targets, or stay in the historical denominator but outside the high-value mobile-framework cohort?
2. **First coarse scope.** I would select it from new failure-dossier frequency. I do not currently see evidence strong enough to choose a recovery-lattice split in advance.
3. **Roslyn as a completeness witness.** Materializing final C# syntax could unlock safe C# type-reference closure, but its value depends on how many current blockers actually live at that scope.
4. **Independent sibling publication.** I favor keeping an independent successful module when an optional sibling fails, but product-selection semantics and user expectations should decide the exact package-level status.
5. **Converter compatibility policy.** Manifest/product normalization looks appropriate; arbitrary source patching does not. The boundary for known mechanical upstream fixes should be explicitly owned.
6. **Cross-repo funding.** After 0.18.0 and usage feedback, should the project fund Programs 3 through 6 as another resilience wave, or enter the recorded maintenance posture and take only evidence-backed fixes?
7. **Numerical target.** Even with the 12/10/10 split exposed, packages contain multiple mechanisms. I would not set an `N/120` promise before the compile-red minimization work.

## Appendix B — out-of-scope corpus classes (pending owner ratification)

Preserved from the superseded gameplan (§6a). The failing pattern in these ~28–30 reds does not generalize to the tool's real use case (an Apple-platform framework consumed from C#). Most sit in `convert_failed` precisely *because* they are not shaped like Apple-platform frameworks — that is the tool behaving correctly, not a defect to fix. Recommendation: formally declare this class out of scope so it stops depressing the headline number; the effective green rate on the remaining ~90 realistic targets is ~55% (vs 42% raw).

- **Server-side Swift** (not Apple-platform frameworks at all): swift-nio, swift-nio-ssl, grpc-swift, fluent-kit, jwt-kit, MongoKitten
- **CLI / tooling**: swift-argument-parser *(stays in the historical denominator; its keyword-escaping mechanism is fixed in Program 2 regardless)*
- **Low-level primitives with direct .NET/BCL equivalents** (no reason to bind): swift-atomics, swift-collections, swift-algorithms, swift-numerics, swift-crypto, swift-certificates, swift-log, swift-system, swift-async-algorithms, swift-protobuf, combine-schedulers, swift-clocks
- **Swift-only architecture patterns / sugar** (nonsensical to call from C# — they are *how you structure a Swift app*): ComposableArchitecture, swift-navigation, swift-dependencies, swift-case-paths, swift-identified-collections, SwifterSwift, Factory, CombineCocoa, SwiftUIX, swift-parsing

Note the overlap with Program 2: Factory and CombineCocoa are in the 12 compile reds, and swift-numerics/swift-clocks/swift-identified-collections are in the named-input cohort. Their *mechanisms* are still worth fixing (they recur in in-scope libraries); only their *headline-cohort membership* is at stake here. This bucketing is data-backed (corpus `category`/`stars_approx` tags) but is a judgment call awaiting owner ratification — see open judgment 1.

## Appendix C — validate-restoration bug table (Program 1 / Session B input)

Preserved from the superseded gameplan (§7). The frozen `validation-baseline.json` (`git_sha 52ac336a`, ~2026-06-28) still records all four libraries as `compile: ok` — stale-green, not current behavior.

| Library | Diagnostic | Nature | Root-cause hypothesis |
|---|---|---|---|
| **GRDB** | SWIFTBIND108 | Dangling wrapper symbol: a C# P/Invoke references an `SBW_…` entry point no emitted Swift wrapper defines (would throw `EntryPointNotFoundException`) | Synthesized `operator==`/`Equatable` wrapper planned C#-side, Swift wrapper symbol not emitted. Gate: `WrapperSymbolIntegrityGate.cs:102-107` |
| **XMLCoder** | SWIFTBIND108 | Same as GRDB | Same dangling-`operator==` mechanism (grouped with GRDB in source) |
| **RealityFoundation** | SWIFTBIND092 / 095 | `Tj` dispatch-thunk bound against the wrong library (cross-module class-thunk attribution); compiles but wrong at the call boundary | Class vtable thunk declared in one submodule, bound as if exported by another. `AbiContractChecker.cs:668-682`. (Distinct from the earlier, already-fixed RealityFoundation SWIFTBIND051.) |
| **Lottie** | *(packaging; no SWIFTBIND code)* | Standalone `.Wrapper.swift` fails `swiftc`: passes `Bool` where `() -> Bool` closure expected | Closure-vs-value shape mismatch in wrapper emission (`not-planned.md` entry of 2026-07-17) |

**Completeness caveat:** the source (the ingestion-hardening `00-program-results.md`, archived at `/Users/wojo/Dev/SB-Backup-Docs/2026-07-sessions-cleanup/2026-07-ingestion-hardening/`) calls this "a structured subset," **not** an exhaustive list. An earlier snapshot (`not-planned.md`, 2026-07-17) attributes GRDB *differently* (CSM argument-label-drop CS0305, `FastDatabaseValueCursor` conformance CS0311) and names **CryptoSwift** + XMLCoder/CryptoSwift skips not present in the newer list. The two are most likely the same freeze event described at two investigation depths, but they have not been reconciled. Therefore Session B's first task is not "fix the four" — it is: run targeted `nuke validate --filter <lib> --skip PackGate BehaviorTier` against these libraries (plus CryptoSwift) to capture current ground truth, reconcile the two snapshots, then fix. No blind re-baseline; no fixing against stale attribution.

## Evidence anchors for follow-up review

The strongest factual claims above can be checked quickly in:

- `/Users/wojo/Dev/SB-Backup-Docs/2026-07-sessions-cleanup/` (archived wave1 / wave2 / ingestion-hardening session docs — removed from `src/docs/sessions/` in the 2026-07 doc cleanup)
- `/Users/wojo/Dev/internal-binding-testing/corpus-sweep/logs/soak-s09-vs-s1.json`
- `/Users/wojo/Dev/internal-binding-testing/corpus-sweep/scripts/soak_s09_report.py`
- `/Users/wojo/Dev/internal-binding-testing/corpus-sweep/output/*/result.json`
- `src/Swift.Bindings/src/Program.cs` around the `SWIFTBIND111` exit path
- `src/Swift.Bindings/src/Diagnostics/Recovery/WrapperRecoveryController.cs`
- `src/Swift.Bindings/src/Diagnostics/Recovery/InEmissionDriver.cs`
- `src/Swift.Bindings/src/Model/Recovery/RecoveryEdgeKind.cs`
- `src/Swift.Bindings/src/Model/Recovery/RecoveryCompletenessGate.cs`
- `/Users/wojo/Dev/spm-to-xcframework/src/spm_to_xcframework/graph_closure.py`
- `/Users/wojo/Dev/spm-to-xcframework/src/spm_to_xcframework/receipt.py`

The corpus output is generated evidence rather than a stable checked-in API. Package-specific diagnoses should be reconfirmed against a fresh run before implementation; the plan intentionally treats them as canaries for mechanisms, not permanent truths about those packages.

## Final recommendation

The most important change in strategy is to stop viewing the remaining distance as one resilience mechanism. The project now has a strong leaf-recovery and reporting foundation, but the next gains live in four distinct systems:

- ordinary generator correctness at final C# compilation;
- provably complete recovery above a leaf;
- managed dependency closure across generated sibling modules; and
- source-package conversion into an import-closed artifact set.

For 0.18.0, start the compile-red sprint now while the failure dossier and validation restoration proceed in parallel; put permanent runtime coverage in BindingTests. Then cut the release. After usage feedback, make an explicit owner decision about funding the cross-repo recovery, sibling-closure, and converter programs. That path is more likely to produce the best next release without making it ride on an open-ended resilience wave: more packages compile, degraded bindings explain themselves, failures teach the user what to do, and every published binding remains defensibly usable.
