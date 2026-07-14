# Track T — Tests, Gates & Honesty (T1–T4 combined)

| Field | Value |
|-------|--------|
| **Wave** | 8 |
| **Track** | T (T1 unit mass · T2 skip honesty · T3 coverage gaps · T4 gate integrity) + **G1 product-scenario gap** |
| **Date** | 2026-07-15 |
| **Mode** | Read-only (no production edits) |
| **Risk rating** | **3 / 5** — everyday BindingTests + compile-only integrity are real; unit-test mass and unenforced baseline keys greenwash confidence; no product-level “partial package” scenario |
| **Confidence** | **high** on sampled mega-files, skip inventory, and gate code paths; **medium** on full RuntimeTestsApp method-level coverage matrix (sampled by domain structure, not every assert) |
| **Lenses** | **L2 primary**; L3 (G1 / baselines); L4 (test mass simplification) |

## Headline

**BindingTests runtime honesty is in good shape** (few skips, mostly specific, Issue-1 attribution gated) and **compile-only is genuinely fail-closed** on the legs that matter (generator exit, wrapper give-up, strip tripwire = 0, parity/API-manifest). **Unit-test mass is not honesty** — two mega-files (~10.7k / ~7.1k LOC) are overwhelmingly `Assert.Contains` string theater. **`BindingTests/baselines.json` claims four coverage budgets that no nuke path enforces** (only `wrapper_stripped_count` is live). **There is no product-scenario test** for “unsupported shapes → clean partial package.”

---

## Method

1. Methodology L2/L3 (`00-methodology.md`), M0-D landscape, `bindingtests.md` / constraints doctrine, orchestration Wave 8.
2. Spot-read largest EmitterTests: `SwiftUIBridgeEmitterTests.cs`, `ProtocolProxyEmitterTests.cs`; contrast `VtableLayoutBuilderTests.cs` (plan/semantic).
3. Full attribute inventory: `[Skip]`, `[SkipOnSimulator]`, `[SkipOnDevice]`, `[SkipOnMonoJit]`, `[SkipOnCatalystX64]`, `[MonoJitCrash]` under `BindingTests/RuntimeTestsApp/`.
4. Domain file-count delta: `Sources/SwiftBindingsTestLib/*` vs `RuntimeTestsApp/*` (+ `.disabled`).
5. Gate code: `Build.BindingTests.cs` compile-only, `Build.WrapperStrip.cs`, `baselines.json`, `coverage-report.py`, parity/API-manifest/skip-surface, `Issue1SkipAttributionTests`, `EnsureGeneratorBuilt` freshness.
6. Cross-read Track G1 product-scenario gap (DA-W7-G1-004).

**Not done:** full LOC census of all unit tests; execution of gates; regeneration of coverage-matrix.

---

## T1 — Unit test mass vs behavior

### Scale (spot)

| File | ~LOC (EOF read) | ~`[Fact]` density | Dominant assert style |
|------|----------------:|-------------------|------------------------|
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/SwiftUIBridgeEmitterTests.cs` | **~10.7k** | **≥199** Facts (grep cap; more below line ~4750) | `Assert.Contains` / `DoesNotContain` on full bridge C#/Swift blobs |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/ProtocolProxyEmitterTests.cs` | **~7.1k** | **≥199** Facts | Same — proxy body substrings |
| Contrast: `VtableLayoutBuilderTests.cs` | modest | dozens | `Assert.Equal` on `SlotIndex` / `SlotVerdict` (semantic) |

Unit tests alone exceed generator source LOC (`00-codebase-map.md` §2). Single test files exceed production mega-emitters.

### Pattern conclusions

1. **String-blob theater is the default** in mega EmitterTests: emit full class → `Assert.Contains("Receive_…")`, `Assert.Contains("MarshalToSwiftBuffer")`, multi-line Dispose/Free bodies. These catch *rephrasing* and *local renames* as hard fails; they often **miss** wrong-but-structurally-similar emission (alternate helper, reordered but correct ABI).
2. **Semantic islands exist** inside the same files: InitAnalyzer classifications (`ViewInitClassification.Simple|Unsupported|AsyncDependency`), BridgeParameterKind, report `BridgeStatus`, plan-level nint narrowing comments (F1). Ratio in sampled ranges: **~2–4× more `Contains` than `Equal`/`True` classification asserts** in the hot emit sections.
3. **One-shape-per-Fact inflation**: SwiftUI bridge walks BoundEnum × Optional × TypedClosure × async-chain combinations as separate Facts that re-read full files — high LOC, overlapping contracts. Prefer `[Theory]` + plan/classification oracles (project rule already says this).
4. **Behavior-good unit pockets elsewhere**: `VtableLayoutBuilderTests` (slot axis), `Issue1SkipAttributionTests` (path-specific symbol), `SkipDispositionClassifierTests`, `ArtifactParityGateTests`, `MemberValidationPipelineTests` — these are L2 gold standards.
5. **False-green under refactor**: post-1.0 debt already named “4K substring → plan assertions”; mega-files make mechanical consolidations (L4) and dual-oracle fixes pay a huge rewrite tax before the real regression signal moves.

### Findings

### DA-W8-T1-001: Mega EmitterTests dominated by implementation substrings

- **Severity**: P2 (P1 for refactor velocity / false green under rename)
- **Status**: confirmed
- **Confidence**: high
- **Lenses**: L2, L4
- **Reachability**: integrity-gate (test suite quality)
- **Claim**: `SwiftUIBridgeEmitterTests` and `ProtocolProxyEmitterTests` primarily lock exact emitted text, not semantic contracts; they greenwash “coverage” without proving CallConv/layout/runtime.
- **Evidence**: e.g. `ProtocolProxyEmitterTests.cs:33–70` class structure Contains; `SwiftUIBridgeEmitterTests.cs:307–433` Free/Dispose multi-Contains; EOF ~10674 still adding Contains for ResultClosure.
- **Suggested direction**: Split (a) **plan/classification** unit tests, (b) **token contract** tests (`CallConvCdecl`, EntryPoint prefix, SB000x), (c) leave full-body golden files only for rare freeze cases. Prefer BindingTests for ABI truth.
- **Prior art**: M0-D §5; roadmap test rebuild; project Claude.md “assert behavior not implementation.”

### DA-W8-T1-002: Exhaustiveness Fact explosion without shared oracles

- **Severity**: P3
- **Status**: confirmed
- **Confidence**: high
- **Lenses**: L4, L2
- **Claim**: Hundreds of near-duplicate Facts re-open bridge/proxy emission instead of one parametrized matrix over `BridgeParameterKind` / dispatchability.
- **Evidence**: Fact lists in both mega-files (grep `[Fact]` ≥199 each; SwiftUI continues into 4xxx–10k).
- **Suggested simplification**: `[Theory]` + shared emit helper + assert on analyzer/plan DTOs first.

---

## T2 — Skip honesty (RuntimeTestsApp)

### Inventory (attribute sites, production test methods)

| Attribute | Approx. method/class uses | Notes |
|-----------|--------------------------:|-------|
| `[Skip(...)]` | **~24** method/class | Dominant permanent skips |
| `[SkipOnSimulator]` | **~11** | Native runtime / destroy-hook / owner-token (device-validated) |
| `[SkipOnDevice]` | **1** | MainActor DEBUG-only guard |
| `[SkipOnMonoJit]` | **2** | Both Issue 1 + named `$s…` CallConvSwift entry |
| `[SkipOnCatalystX64]` | **0** live tests | Attribute + runner wired; no current users |
| `[MonoJitCrash]` | **0** | Deprecated; clean |
| Issue 1 / mono blame in comments | several | Mostly historical “unskipped” notes (e.g. ClosureEdgeCaseTests) |

Sources: ripgrep over `BindingTests/RuntimeTestsApp/**/*.cs` (excluding infrastructure prose where noted).

### Attribution quality

| Skip | Verdict |
|------|---------|
| `OptionalMarshallingTests` / `OptionalThrowingVoidClosureTests` `[SkipOnMonoJit]` | **Honest** — Issue 1, symbol in reason, gate `Issue1SkipAttributionTests` |
| Lifetime `SkipOnSimulator` (destroy hook / N-3 / async owner-token) | **Honest** — harness `IncludeSwiftBindingsRuntimeNative=false`; device is truth |
| FailFast subprocess skips (async closure, KVO, suppressed-proxy Category A) | **Honest harness limit** — not mono-blame theater |
| `weak/unowned` ×4 | **Honest product gap** — short reason, clear |
| Cross-module alias class-level Skip | **Honest generator gap** — specific emitter skip |
| Nested-of-parent destroy ×2 | **Honest open bug** — doc-linked hypothesis |
| ClosureFanOut async reverse | **Honest by-design-gray** ABI grid |
| Variadic constructor ×3 | **Honest** emission suppression |
| Non-@objc enum raw values | **Honest** ABI/source-of-truth limit |
| `MethodLevelGenericTests` DONE_BLOCKING | **Mostly honest as OUR bug** — Skip always; reason correctly prioritizes missing `@_cdecl` + SB0001; Mono DONE_BLOCKING is symptom (Issue 3-adjacent), not used as permanent “upstream only” excuse |
| `ClosureTests` setter-only SIGSEGV on Mono ×2 | **Borderline** — uses always-`[Skip]` citing Mono SIGSEGV; if device/NativeAOT would pass under cdecl path, should be `SkipOnMonoJit` or fixed; reason also admits **our** missing `@_cdecl` for existential-param setters |
| `GenericClosureBridgeTests` MCB dylib ×2 | **Honest gap** — short; could cite ticket/handler |
| `ProtocolClosureSkipTests` UTF-8 thunk | **Honest incomplete feature** — short |
| `InoutStructDispatchTests` Unsafe.Read | **Honest known reverse-dispatch bug** — specific |

### Doctrine check (4 upstream only)

Confirmed upstream catalog (`src/docs/Future/upstream-issues-README.md`): Issues **1–4** only.

| Upstream | Live skip usage |
|----------|-----------------|
| Issue 1 (`!ji->async`) | 2× `SkipOnMonoJit` + enforcer unit test |
| Issue 2 (non-blittable CallConvSwift) | Not a skip attr — product/wrapper policy; variadic Skip cites non-blittable guard |
| Issue 3 (Set.insert DONE_BLOCKING) | **No dedicated SkipOnMonoJit**; MethodLevelGeneric mentions DONE_BLOCKING under always-Skip (our missing cdecl primary) |
| Issue 4 (Catalyst x64) | **Attribute exists, zero test uses** |

**No bulk Mono blame theater remains.** Historical `[MonoJitCrash]` purge held.

### Findings

### DA-W8-T2-001: Skip inventory is mostly honest; residual always-Skip mono-symptom wording

- **Severity**: P3
- **Status**: confirmed
- **Confidence**: high
- **Lenses**: L2
- **Claim**: ~35 skip sites; none are vague “Mono crash.” Two Issue-1 skips are correctly narrow. Two setter-only closures use always-Skip with Mono SIGSEGV language while root cause is generator missing `@_cdecl` — re-triage device/NativeAOT and narrow attribute if applicable.
- **Evidence**: inventory above; `ClosureTests.cs:580–591`; `Issue1SkipAttributionTests.cs:15–29`.
- **Prior art**: M0-D §3; bindingtests.md “guilty until proven innocent.”

### DA-W8-T2-002: Issue1SkipAttribution is a model L2 gate

- **Severity**: P3 (positive finding)
- **Status**: confirmed
- **Confidence**: high
- **Lenses**: L2
- **Claim**: Path-specific `$s…` + CallConvSwift emission check prevents gaming Issue-1 skips — rare example of meta-honesty test.
- **Evidence**: `Issue1SkipAttributionTests.cs:46–144`.

### DA-W8-T2-003: FailFast / process-abort paths untested at runtime

- **Severity**: P2
- **Status**: confirmed (harness limit)
- **Confidence**: high
- **Lenses**: L2, L3
- **Claim**: FailFast-by-design channels rely on structural unit tests only; no subprocess runner → product cannot prove abort wording / non-corrupt path on device.
- **Evidence**: `SuppressedProxyChannelTests.cs:477–483`; `AsyncClosureTests.cs:99`; `FoundationKvoTests.cs:115`.
- **Suggested fixture**: Host-side subprocess harness (macOS CoreCLR first) asserting exit/failfast for 1–2 canonical cases.

---

## T3 — Coverage gaps (sources vs runtime asserts)

### Domain imbalance (file counts)

| Domain | Swift sources (approx) | Runtime test files | Gap note |
|--------|----------------------:|-------------------:|----------|
| Generics | ~55 (+ disabled) | 63 | Strong twinning |
| Protocols | ~49 | 48 | Strong |
| Marshalling / Types / Enums | spread | 44 + 8 Types | Strong core |
| Async | ~26 | 29 | Strong |
| Closures | ~21 | 15 | Good; some Skip debt |
| **Operators** | **4** | **3** | Thin — bitwise/overflow shapes may be compile-only |
| **Parameters** | **6** | **2** | Inout / Variadic / UnderscoreLabels / RealityKit repros under-asserted at runtime |
| **Tuples** | **5** | folded into Marshalling | No dedicated folder; effects/named may be compile-only |
| **UnsafeTypes** | **7** | EdgeCases buffer ×2 | Span / OpaquePointer / PointerGenerics thin |
| **Foundation** | several + **3 `.disabled`** | 2 Interop | Data/URL/Extensions disabled permanent? |
| **PropertyWrappers** | **entire folder disabled** | 0 | Non-goal or forgotten |
| **WrapperCoverage** | 7 | Wrappers/1 + Internal | Compile-heavy |
| Metadata | 1 | 2 | OK tripwire |
| AppIntents | 1 | 2 | Product non-goal depth |
| Mixed path **c** ProjectReference | — | unit only | Documented; no iOS runtime leg |

### Platform matrix

| Platform | Default? | Gap |
|----------|----------|-----|
| iOS sim Mono | **Yes** inner loop | Leak/destroy hooks SkipOnSimulator → **lifetime device-biased** |
| Device NativeAOT | Opt-in | Required for CC/marshalling; not every PR |
| macOS / Catalyst / tvOS | Opt-in | Catalyst x64 attribute unused; tvOS device missing (roadmap low) |
| Mixed-pack/direct / appstore | Heavy opt-in | Correct cadence; not honesty holes if pre-release runs |

### Compile-only vs runtime twin

M0-D seed holds: many Swift fixtures exist primarily to keep **compile-only** green after intentional skips. Coverage-matrix `runtime_tested` / `passing_untested` would quantify this — **but that script is manual and not in CI** (see T4).

### Findings

### DA-W8-T3-001: Thin domains (Operators, Parameters, UnsafeTypes, Tuples, Foundation disabled)

- **Severity**: P2
- **Status**: confirmed (structural)
- **Confidence**: high
- **Lenses**: L2
- **Claim**: Source:test file ratios show several ABI-sensitive domains lean on compile-only; regression risk higher than Generics/Protocols mass suggests.
- **Evidence**: directory listings under `BindingTests/Sources/…` vs `RuntimeTestsApp/…`.
- **Suggested fixtures**: (1) operator round-trip matrix including overflow ops already parser-gated; (2) inout + variadic runtime; (3) Span/OpaquePointer peek/poke; (4) decide `.disabled` Foundation — resurrect or delete.

### DA-W8-T3-002: No automated feature×runtime matrix in CI

- **Severity**: P2
- **Status**: confirmed
- **Confidence**: high
- **Lenses**: L2
- **Claim**: `coverage-report.py` can flag `passing_untested` must-pass features; not invoked by `nuke binding-tests`; budgets in `baselines.json` unused.
- **Evidence**: M0-D §2.2; `coverage-report.py:1253–1268`; no nuke target wiring found in `Build.BindingTests.cs` ReportBindingTestResults (logs file counts only, `:1168–1188`).

### DA-W8-T3-003: Lifetime correctness is device-biased by construction

- **Severity**: P2
- **Status**: confirmed
- **Confidence**: high
- **Lenses**: L2, L1-adjacent
- **Claim**: Multiple ownership/leak probes SkipOnSimulator due to missing native runtime framework on sim — default inner loop cannot certify destroy hooks.
- **Evidence**: `OwnershipGCStressTests.cs:376+`; `LifetimeTrackingTests.cs:156`; Escaping/Async lifetime tests.
- **Mitigation (docs only)**: Require `--device` for lifetime PRs; consider sim-inject of runtime native for leak probes (product decision).

---

## T4 — Gate integrity

### What is fail-closed (good)

| Gate | Mechanism | Fail-open escape |
|------|-----------|------------------|
| Compile-only default | `failClosed = !Permissive` — generator regen strict, wrapper compile throw, parity + API-manifest | `--permissive` local only |
| Wrapper strip tripwire | `wrapper_stripped_count` **0**; increase throws `WrapperStripTripwireException` | `--permissive` |
| Getter parity harness vs generator-own | EnforceWrapperGetterParity | permissive warn |
| Mutual exclusion mixed*/compile-only | throw on combine | n/a |
| Issue-1 skip attribution | unit SkippableFact | needs generated output present |
| API-manifest retarget | RunApiManifestGate | permissive |
| Artifact parity | RunParityGate | permissive |
| **EnsureGeneratorBuilt freshness** | SHA fingerprint + `.bindingtests-generator-stamp` — **rebuilds on source change** | n/a |

Evidence: `Build.BindingTests.cs:25–34`, `:931–968`, `:609–627`; `Build.WrapperStrip.cs:159–196`; `baselines.json`.

### What is fail-open / theater

| Claim | Reality |
|-------|---------|
| `baselines.json` `must_pass_degraded: 0` | **Not read by nuke** — only documented + coverage-report *computes* degraded counts; **does not compare to baselines.json** |
| `must_pass_compiled_out: 25` | Same — dead budget key |
| `known_unsupported_total: 62` | Same — dead budget key |
| `generator_exit_code: 0` | Not enforced via this file; real enforcement is `Strict \|\| failClosed` on regen exit |
| Coverage matrix in CI | **Manual** `coverage-report.py`; exits 1 only on missing *test file*, not on degraded/untested budgets vs baseline |
| Skip-surface trend gate | **Opt-in** `--skip-surface` only (`Build.BindingTests.cs:976–977`) |
| `ReportBindingTestResults` | **Logs only** — no pass-count floor vs baseline in this method |
| Stale generator (historical) | **Fixed** via fingerprint (constraints.md / M0-D text may still say “missing only” — docs drift) |

### Strip count 0 honesty

`wrapper_stripped_count: 0` is a **real integrity tripwire**: emission admission (parent-internal → CallConvSwift fallback) closed the last intentional strip; any new uncompilable wrapper emission fails the gate. This is L2/L3 gold — co-gater residual still tracked in G1.

### Findings

### DA-W8-T4-001: baselines.json multi-key theater — only strip count is enforced

- **Severity**: P1
- **Status**: confirmed
- **Confidence**: high
- **Lenses**: L2, L3
- **Reachability**: integrity-gate
- **Claim**: Four of five numeric keys in `BindingTests/baselines.json` provide **false confidence**. A rise in known-unsupported or must-pass-degraded would not fail `nuke binding-tests --compile-only`.
- **Evidence**:
  - `BindingTests/baselines.json:3–7` lists all keys.
  - Only `wrapper_stripped_count` loaded in `Build.WrapperStrip.cs:144–157`.
  - Repo grep: `known_unsupported` / `must_pass_degraded` appear in M0-D + `coverage-report.py` summary math, **not** in any `build/*.cs` enforcer.
  - `coverage-report.py` never opens `baselines.json`.
- **Probe**: Change `must_pass_degraded` to 999 in baselines.json; run compile-only — expect no failure from that key.
- **Suggested fix (owner-gated)**: Either wire coverage-report + baseline compare into compile-only, or delete dead keys and document single-purpose strip baseline.

### DA-W8-T4-002: Compile-only integrity core is solid

- **Severity**: P3 (positive)
- **Status**: confirmed
- **Confidence**: high
- **Lenses**: L2, L3
- **Claim**: Generator exit, wrapper give-up, strip=0, parity, API-manifest form a real fail-closed CI spine when not `--permissive`.
- **Evidence**: `Build.BindingTests.cs:938–968`.

### DA-W8-T4-003: EnsureGeneratorBuilt staleness hazard largely closed; docs lag

- **Severity**: P3
- **Status**: confirmed (code fixed; doc hazard residual)
- **Confidence**: high
- **Lenses**: L2, L5
- **Claim**: Code uses source fingerprint stamp (`:604–621`); constraints.md still describes “rebuild only if missing” — AI/agents may over-debug “patch didn’t take.”
- **Evidence**: `Build.BindingTests.cs:604–621` vs constraints.md “Stale generator binary” entry.

### DA-W8-T4-004: Skip-surface / coverage budgets opt-in → regressions can accumulate silently

- **Severity**: P2
- **Status**: confirmed
- **Confidence**: high
- **Lenses**: L2
- **Claim**: Layer B skip-surface and coverage-matrix are not default compile-only; silent growth of Review/skip surface possible between integration runs.
- **Evidence**: `Build.BindingTests.cs:970–977`; manual coverage-report.

---

## G1 product gap — unsupported shapes → clean partial package

Cross-track with DA-W7-G1-004.

### What exists

- Exhaustive `SkipReason` dispositions (unit).
- MemberValidationPipeline / TypeSkipPrePass admission (unit + BindingTests compile-only of the **kitchen-sink** fixture).
- `binding-report.json` triage + SWIFTBIND060/061.
- BindingTests **whole library** compile-clean after intentional skips — **not** the same as a **minimal product scenario** with expected skip budget.

### What is missing

| Missing product test | Why it matters |
|----------------------|----------------|
| Tiny fixture xcframework: PAT method + SwiftUI-constrained type + internal-parent async + unsupported closure | Proves generator exit 0 + compile without kitchen-sink noise |
| Assert `SkipTriage.ReviewCount ≤ N` and `ByDisposition` expected set | Prevents emit-then-CS regressions from looking “green” on full suite |
| Optional pack leg: managed nupkg usable when wrapper fails under soft policy | Blocks day-1 “drop xcframework” story (G1-001) |
| Report row ↔ runtime absence / NotSupported for one degraded public member | Closes “compile-but-dead” honesty gap |

### Recommended fixtures (concrete)

1. **`PartialSuccessKitchen`** (Swift module, 1 file):
   - `public protocol HasAssoc { associatedtype T; func f() -> T }` + free `func use(_: any HasAssoc)` → expect **type/member skip**, not CS.
   - `struct V: View { var body: some View { EmptyView() } }` → SwiftUI skip/bridge path; main binding still packs.
   - `public func bad(_ c: (String) async -> Void)` or known UnsupportedClosure shape → skip + report KnownLimitation.
   - One **must-emit** blittable `public struct Ok { public var x: Int; public init(x: Int) }` round-trip later.

2. **Nuke/SDK assertion** (compile-only sibling):
   - Generator exit 0.
   - `dotnet build` generated csproj succeeds.
   - Parse `binding-report.json`: zero unexpected Review reasons; `Ok` emitted; unsupported rows present with dispositions.
   - Wrapper: either compiles or documents SWIFTBIND050 path under explicit soft flag — do **not** weaken integrity 108.

3. **Optional runtime**: construct `Ok`, assert value; reflect that skipped types are absent (or Obsolete-only with documented id).

### Finding

### DA-W8-T-G1-001: No product-scenario gate for partial-success (reaffirm G1-004)

- **Severity**: P2
- **Status**: confirmed / degrade-opportunity
- **Confidence**: high
- **Lenses**: L2, L3
- **Reachability**: fixture-reachable
- **Claim**: Same as G1-004 — without a dedicated fixture, “drop xcframework with a few hard shapes” is uncertified beyond admission unit tests + kitchen-sink compile.
- **Prior art**: Track-G1 §8–9 DA-W7-G1-004.

---

## Ranked issues (top)

| Rank | ID | Severity | One-liner |
|-----:|----|----------|-----------|
| 1 | DA-W8-T4-001 | **P1** | `baselines.json` coverage keys are unenforced theater |
| 2 | DA-W8-T1-001 | **P2** | Mega unit tests = string theater; high false-green / rewrite tax |
| 3 | DA-W8-T3-002 / T4-004 | **P2** | Coverage matrix + skip-surface not default gates |
| 4 | DA-W8-T-G1-001 | **P2** | No partial-success product scenario fixture |
| 5 | DA-W8-T3-001 / T3-003 | **P2** | Thin domains + sim-skipped lifetime truth |
| 6 | DA-W8-T2-003 | **P2** | FailFast paths structural-only |
| 7 | DA-W8-T2-001 | **P3** | Re-triage always-Skip mono-symptom wording |
| 8 | DA-W8-T4-003 | **P3** | Stale-generator docs lag fixed code |

**Positive anchors:** Issue1 attribution; strip=0 tripwire; compile-only fail-closed core; skip inventory largely clean; BindingTests runtime mass on Generics/Protocols/Marshalling.

---

## Risk summary

| Area | Risk /5 | Why |
|------|--------:|-----|
| Runtime BindingTests honesty | **2** | Skips mostly specific; ABI truth layer real |
| Unit-test meaning | **4** | Mass ≫ meaning in mega EmitterTests |
| Baseline / CI coverage budgets | **4** | Dead keys + manual matrix |
| Partial package product story | **3** | Admission strong; product scenario absent |
| **Overall Track T** | **3** | Solid spine with dishonest side-panels |

---

## Suggested backlog (owner-gated; no implementation this wave)

1. **Wire or delete** dead `baselines.json` keys; if wire: compile-only runs coverage-report and fails on degraded/untested/known_unsupported vs budget.
2. **Product fixture** PartialSuccessKitchen + report assertions (G1 + T).
3. **Substring→plan migration** pilot on ProtocolProxy (highest churn ABI surface) — 1 receiver family first.
4. **Re-triage** ClosureTests setter-only + MethodLevelGeneric (device leg / cdecl plan).
5. **Subprocess FailFast** host gate for 1 suppressed-proxy + 1 closure case.
6. **Doc fix**: EnsureGeneratorBuilt fingerprint in constraints.md / M0-D.
7. **Domain runtime twins** for Parameters/Inout/Variadic, Operators, UnsafeTypes.

---

## Files / artifacts consulted

- `src/docs/deep-audit-2026-07/00-methodology.md` (L2/L3)
- `src/docs/deep-audit-2026-07/waves/W0-map/M0-D-test-landscape.md`
- `src/docs/deep-audit-2026-07/tracks/Track-G1_Graceful-Degradation.md`
- `BindingTests/baselines.json`
- `build/Build.BindingTests.cs`, `Build.WrapperStrip.cs`, `build/scripts/coverage-report.py`
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/{SwiftUIBridgeEmitterTests,ProtocolProxyEmitterTests,VtableLayoutBuilderTests}.cs`
- `src/Swift.Bindings/tests/UnitTests/Issue1SkipAttributionTests.cs`
- `BindingTests/RuntimeTestsApp/**` skip attributes (inventory)
- `src/docs/Future/upstream-issues-README.md`
- `.claude/rules/bindingtests.md` / constraints (via project context)

---

## Handoff

- **Synthesis**: Pair T4-001 with M0-C fail-open notes; G1 product fixture with T-G1-001.
- **Do not** re-open Mono bulk blame; Issue1 gate is healthy.
- **Do not** treat unit mega-file green as ABI proof — BindingTests remains oracle for L1.
)
