# Data Pack — Gates, Baselines, CI: Enforced vs Theater

**Date**: 2026-07-16  
**Mode**: Evidence extraction (no production edits)  
**Question**: What is actually gated in CI / nuke, vs baseline files that are theater?

### Headline counts

| Corpus | Enforced keys/files | Theater / advisory | Notes |
|--------|--------------------:|-------------------:|-------|
| `BindingTests/baselines.json` numeric keys | **1** | **4** | Only `wrapper_stripped_count` is read by Build code |
| `build/baselines/*` files (5) | **4** live somewhere | **1** CI-dead | Skip-surface opt-in; validate not in CI |
| `validation-baseline.json` top-level sections | **3** (when their host runs) | **1** (`skip_metrics`) | `compile_gate` host = `nuke validate` only |

**BindingTests/baselines.json: 1 enforced / 4 theater.**

---

## 1. `BindingTests/baselines.json` — keys vs readers

File (committed budgets + tripwire):

| Key | Value (sample) | Read by Build? | Enforced? |
|-----|---------------:|:--------------:|:----------|
| `_comment` | doc string | no | n/a |
| `generator_exit_code` | `0` | **no** | **Theater** — real enforcement is regen exit under `Strict \|\| !Permissive` (`Build.BindingTests.cs`), not this key |
| `must_pass_degraded` | `0` | **no** | **Theater** — never opened by any `build/*.cs` |
| `must_pass_compiled_out` | `25` | **no** | **Theater** |
| `known_unsupported_total` | `62` | **no** | **Theater** |
| `wrapper_stripped_count` | `0` | **yes** | **Enforced** — `Build.WrapperStrip.cs` → `LoadWrapperStripBaseline` / `EnforceWrapperStripTripwire` |

**Grep proof**: the only `baselines.json` property access under `build/` is `wrapper_stripped_count` (`Build.WrapperStrip.cs`).  
`coverage-report.py` *computes* `must_pass_*` / `known_unsupported_*` / reads `output/generator-exit-code`, but **never opens** `BindingTests/baselines.json`.

| Reader | Path | What it does |
|--------|------|--------------|
| `Build.WrapperStrip.cs` | `BindingTestsDir / "baselines.json"` | Fail-closed on strip count **>** baseline (`Strict \|\| !Permissive`) |
| `nuke seed-wrapper-strip-baseline` | same file | Reseeds `wrapper_stripped_count` only |

---

## 2. `build/baselines/*` — each file, what enforces it

| File | Enforcer | Host command | Fail mode | CI? |
|------|----------|--------------|-----------|:---:|
| `validation-baseline.json` | `Build.Validation.cs` (`Compare` + `Assert.Fail`) | `nuke validate` | C# compile / dep / **baseline regressions** (incl. `swift:ok→fail`) | **No** |
| ↳ `unit_tests.swift_bindings_unit_pass_floor` | `Build.Test.cs` `EnforceUnitTestPassFloor` | `nuke test` → UnitTests | Pass count drop | **Yes** (PR + release) |
| ↳ `runtime_tests.<platform>` scalar pass | `Build.RuntimeTests.cs` baseline compare | `nuke binding-tests --sim/--device/...` (full suite) | Pass floor drop | **Yes** (CI sim via `ci_ios_test.py`) |
| ↳ `skip_metrics` | `Build.Validation.cs` log only | `nuke validate` | **Warn only** — never in `validationFailed` | n/a (advisory) |
| `parity-baseline.json` | `Build.Parity.cs` + `ArtifactParityGate` | `binding-tests --compile-only` | **New** symbol / arity / vtable divergences | **Yes** |
| `api-manifest-baseline.json` | `Build.ApiManifestGate.cs` | `binding-tests --compile-only` | **Retarget** only (`(module,sig)→symbol` change). Add/remove reported, not fail | **Yes** |
| `skip-surface-baseline.json` | `Build.SkipSurface.cs` | `binding-tests --compile-only --skip-surface` | Upward skip-marker counts / new keys | **No** (opt-in) |
| `runtime-identity-baseline.json` | `Build.RuntimeTests.cs` | full runtime run (no class-filter / skip-build) | Per-test skip identity regression | **Yes** on CI sim (when platform seeded) |

### `validation-baseline.json` section detail

| Section | Enforced? | How |
|---------|:---------:|-----|
| `git_sha` | advisory | `--quick` warns on mismatch |
| `compile_gate.libraries` (`compile`, `errors`, `lines`, `dep_compile`, `swift_compile`) | **yes under `nuke validate`** | `Compare()` → regressions hard-fail; green full run auto-saves |
| `skip_metrics` (totals, reasons, `post_processor_sub_causes`) | **no** | `Log.Warning` on skip count / sub-cause bumps; **not** in `validationFailed` predicate |
| `runtime_tests.{simulator,device,macos,macos_x64,maccatalyst,maccatalyst_x64,tvos_simulator}` | **yes under runtime BindingTests** | Pass floor; auto-raise on clean improvement |
| `unit_tests.swift_bindings_unit_pass_floor` | **yes under `nuke test`** | Floor for `Swift.Bindings.Unit.Tests` only (analyzers excluded by design) |

**Fail-open edge (validate)**: `validationFailed = compileFailed || compileNoOutput || depFailed || regressionCount`. Current-run `swiftFailed` alone is **not** in that expression — a pure Swift-wrapper failure fails only if the baseline records `swift:ok → swift:fail` (or C#/dep also fails). First-time / unbaselined pure-swift fails can slip until baselined.

### Parity gate (`ArtifactParityGate`) — three checks

1. **Symbol existence** — called P/Invoke `EntryPoint` ∈ dylib `nm -gU`; reverse orphans among generator-authored wrapper exports  
2. **Struct-mirror arity** — C# `Buffer` stems vs ABI stored instance properties  
3. **Vtable parity** — ordered field names of `{P}SwiftVTable` vs Swift `{P}_vtable`

Baseline absorbs **pre-existing** divergences; gate fails on **new** ones. Reseed: `nuke SeedParityBaseline`.

### API-manifest gate

- **Fails**: retarget (same public C# signature, different native symbol)  
- **Does not fail**: added members, removed members (logged)  
- Reseed: `nuke SeedApiManifestBaseline`

### Skip-surface gate

- Scans generated `.cs` for `// Unsupported:`, `// Skipped:`, `[UnsupportedSwiftType]`, `[Obsolete(..., SB0001)]`  
- Ratchets **downward**; upward = throw  
- **Only** when `--skip-surface` — **not** in PR/release workflows  
- Reseed: `nuke SeedSkipSurfaceBaseline`

### Runtime-identity gate

- Closes the scalar-pass blind spot (pass↔skip swap nets out)  
- Compares non-pass identities per platform  
- Inert until platform seeded; CI sim has a seeded `simulator` block  
- Reseed: `nuke binding-tests --skip-regen --seed-runtime-identity-baseline`

---

## 3. GitHub workflows — which nuke targets run

### PR CI — `.github/workflows/ci.yml`

| Job | Command(s) | Role |
|-----|------------|------|
| `build-and-test` | `dotnet nuke test` | Unit + analyzer + runtime-lib tests; **unit pass floor** |
| `bindingtests` | `dotnet nuke binding-tests --strict --compile-only` | Fail-closed compile spine (regen, C#, wrapper, strip, parity, api-manifest) |
| | `dotnet test … Issue1SkipAttributionTests…` | Skip-attribution integrity (needs generated output) |
| | `python3 build/scripts/coverage-report.py …` | Coverage matrix; **exit 1 only if must-pass feature has no test file** — no baselines.json compare; degraded/untested = warn |
| | `python3 build/scripts/ci/ci_ios_test.py … --tier 2 --skip-regen` | Full sim runtime via `nuke binding-tests --sim --skip-regen` (**note**: `--tier` is logged only — **not** forwarded to nuke; vestigial) |
| | `dotnet nuke validate-blast-radius` | Apple supplement linkage golden diffs |
| `package-smoke` | `dotnet nuke pack --version 0.0.0-smoke --apple-version 26.2.0-smoke` | Pack all 4 nupkgs (structure smoke, not PackGate assertions) |

### Release — `.github/workflows/release.yml`

| Job | Command(s) | Role |
|-----|------------|------|
| `validate-branch` | branch-name parse | Lane + semver + dryrun shape |
| `build-and-test` | `dotnet nuke test` | Same as CI |
| `bindingtests` | compile-only + Issue1 + coverage-report + `ci_ios_test.py` | Same as CI **minus** blast-radius (split out) |
| `blast-radius` | `dotnet nuke validate-blast-radius` | Parallel job |
| `packgate` | `dotnet nuke pack-gate` | **Release-only** nupkg packaging regression |
| `release-pack` | `dotnet nuke pack --version … --apple-version …` | Real versioned packages |
| `publish-release` | nuget push + tags + GH release | Needs **all** of the above |

### Explicitly **not** in PR or release workflows

| Target / flag | Why it matters |
|---------------|----------------|
| `nuke validate` | Real-world library compile gate + `compile_gate` baseline |
| `binding-tests --skip-surface` | Skip-surface trend ratchet |
| `nuke release-gates` / `ReleaseGatesAttest` | RC orchestrator + disposition manifest (explicitly "not wired into CI") |
| `--device`, `--macos`, `--catalyst`, `--tvos` | Cross-platform runtime floors |
| `--mixed-pack`, `--mixed-direct`, `--appstore-hygiene` | Mixed ObjC+Swift + TN2435 IPA legs |
| `nuke x64-thunk-gate` / `x64-sim-gate` / `x64-pack-gate` | Hosted-x64 specialty gates |

---

## 4. Named gates — one line each

| Gate | Proves |
|------|--------|
| **ReleaseGates** (`Build.ReleaseGates.cs`) | Composes host-safe release legs (unit tests, strict compile-only, PackGate, appstore-hygiene **structural**) into a JSON catalog where skip ≠ pass; **not in CI** — RC checklist with optional `--require-complete` + attest path for device/mixed/IPA legs |
| **PackGate** (`Build.PackGate.cs`) | Packs Runtime/Sdk/Apple at a throwaway version, consumes them (TipKit + HelloPack), asserts nupkg/wrapper-slice structure (Info.plist per slice, mixed fixture, AOT-injection descriptors) on **macOS host only** — catches packaging regressions the intermediate `nuke validate` wrapper check cannot |
| **ApiManifest** (`Build.ApiManifestGate.cs`) | Fail-closed retarget detector: stable public C# signature must keep the same native entry symbol across regenerations |
| **SkipSurface** (`Build.SkipSurface.cs`) | Opt-in downward ratchet on mechanically visible skip markers in generated C# (Layer B trend); dead in default CI |
| **Parity** (`Build.Parity.cs` + `ArtifactParityGate`) | Fail-closed cross-artifact agreement: symbols, frozen-struct Buffer arity, reverse-dispatch vtable field lists — latent runtime faults made compile-time |

### Related CI-adjacent gates (for completeness)

| Gate | Proves |
|------|--------|
| **Wrapper-strip tripwire** | Generator post-processor strip count ≤ `wrapper_stripped_count` (currently 0) |
| **Wrapper getter parity** | Harness wrapper exports the same EveryProtocol witness getters as generator-own wrapper |
| **ValidateBlastRadius** | Adding SwiftBindings.Apple adds zero new `-framework` / Swift ABI surface vs Runtime-only goldens |
| **Unit pass floor** | `Swift.Bindings.Unit.Tests` pass count ≥ `validation-baseline.json` floor |
| **Runtime pass + identity** | Per-platform pass floor + per-skip identity ratchet on full suite runs |
| **Issue1 skip attribution** | Every `[SkipOnMonoJit]` Issue-1 skip names a real CallConvSwift entry point on its path |

---

## 5. Fail-closed vs fail-open inventory

### Fail-closed (hard fail unless noted)

| Surface | Default | Escape hatch |
|---------|---------|--------------|
| `--compile-only` generator exit / dep-gen exit | fatal | `--permissive` |
| `--compile-only` + `--strict-inputs` (implied) | fatal on degraded inputs | `--permissive` drops strict |
| Wrapper compile give-up (single-shot) | fatal in compile-only | `--permissive` |
| Wrapper-strip tripwire (strip > baseline) | `Strict \|\| !Permissive` | `--permissive` |
| Parity gate (new divergences) | failClosed = `!Permissive` | `--permissive` (setup + violations → warn) |
| API-manifest (retargets + empty baseline + schema mismatch) | failClosed = `!Permissive` | `--permissive` |
| Runtime test fail/crash/timeout | always throw | none (smoke/class-filter skip **baseline** only) |
| Runtime pass floor drop | throw | none on full green-path runs |
| Runtime identity regression | throw when baselined | reseed flag |
| Unit test failure (xUnit) | throw | none |
| Unit pass floor drop | throw | trx missing → **warn skip** (see fail-open) |
| `nuke validate` compile/dep/regression | `Assert.Fail` | filtered/tier runs skip baseline save |
| PackGate / pack / blast-radius / appstore structural | throw | n/a |
| Mutually exclusive BindingTests flags | throw | n/a |
| ReleaseGates catalog integrity / failed executed leg | `Assert.Fail` | skips exit 0 unless `--require-complete` |

### Fail-open / advisory / theater

| Surface | Behavior | Risk |
|---------|----------|------|
| `baselines.json` `must_pass_*` / `known_unsupported_total` / `generator_exit_code` | **Unread** by nuke | False confidence that coverage budgets are gated |
| `validation-baseline.json` `skip_metrics` | Warn only | Skip-rate / post-processor residue can rise silently |
| `coverage-report.py` vs baselines.json | Never compares | Degraded / known-unsupported budgets are theater |
| `coverage-report.py` degraded / passing_untested | Warn; exit 0 | CI step stays green |
| `coverage-report.py` must_pass **missing** test file | exit 1 | **Actually fails CI** — only real coverage hard gate |
| Skip-surface without `--skip-surface` | not run | Skip-class growth invisible in CI |
| `nuke validate` not in CI | never runs on PR/release | Real-world lib compile regressions not CI-blocked |
| Pure `swift_compile` fail without baseline regression | not in `validationFailed` | New/unbaselined wrapper fails can soft-land |
| Unit trx missing / unparseable | skip floor | Floor inert that run |
| Runtime baseline absent for platform | skip comparison | Identity/floor inert until seeded |
| Partial runtime (`--class-filter`, `--skip-build`, smoke flags) | skip baseline compare | Intentional |
| API-manifest add/remove | log only | Surface shrink/grow without retarget is silent |
| Parity pre-baselined known missing | absorbed | Gate only catches *new* drift |
| `ReportBindingTestResults` | log file counts only | No floor |
| `ci_ios_test.py --tier N` | logged, not passed to nuke | Dead CLI surface |
| ReleaseGates undispositioned skips | exit 0 by default | Ship-ready requires manifest + optional `--require-complete` |
| PackGate source-xcframework legs without `.libraries/Nuke` | log + skip | Hosted CI still green without those legs |
| Coverage step after compile-only | can fail job only on missing features | Does **not** re-run binding-tests on matrix content |

### `--compile-only` contract (CI spine)

Default = **fail-closed** (`failClosed = !Permissive`):

1. Build xcframework  
2. Regenerate (strict inputs + non-zero exit fatal)  
3. C# compile-check  
4. Wrapper post-process + strip tripwire + single-shot wrapper compile  
5. Bridge  
6. Parity gate  
7. API-manifest gate  
8. Skip-surface **iff** `--skip-surface`

`--permissive` is documented as **local exploration only**; CI never passes it.

---

## 6. Enforced vs theater — tally

### A. `BindingTests/baselines.json` keys (primary theater finding)

| Class | Count | Keys |
|-------|------:|------|
| **Enforced** | **1** | `wrapper_stripped_count` |
| **Theater** | **4** | `generator_exit_code`, `must_pass_degraded`, `must_pass_compiled_out`, `known_unsupported_total` |
| **Total numeric** | **5** | |

### B. `build/baselines/*` files

| Class | Count | Files |
|-------|------:|-------|
| **Enforced on CI path** | **3** | `parity-baseline.json`, `api-manifest-baseline.json`, `runtime-identity-baseline.json` (sim) + `validation-baseline` **unit_tests** / **runtime_tests.simulator** sections |
| **Enforced only out-of-CI** | **1** (partial) | `validation-baseline.json` `compile_gate` via `nuke validate` |
| **Opt-in only (CI-dead)** | **1** | `skip-surface-baseline.json` |
| **Advisory section inside enforced file** | **1 section** | `skip_metrics` inside `validation-baseline.json` |

### C. CI job spine honesty (what actually blocks merge/release)

| Blocker on green CI | Source |
|---------------------|--------|
| Unit/analyzer/runtime-lib test failures + unit pass floor | `nuke test` |
| Generator/wrapper/C# integrity + strip=0 + parity + api retarget | `binding-tests --strict --compile-only` |
| Issue-1 skip mis-attribution | filtered unit test |
| Must-pass feature with **no** test file | `coverage-report.py` exit 1 |
| Sim runtime fail/crash + pass floor + identity | `ci_ios_test` → `binding-tests --sim` |
| Apple blast-radius golden drift | `validate-blast-radius` |
| Pack produces nupkgs | `pack` smoke / release pack |
| **Release only**: nupkg packaging contract | `pack-gate` |

| Looks gated but is not (on PR CI) | Why |
|-----------------------------------|-----|
| must_pass_degraded = 0 budget | unread key |
| known_unsupported_total = 62 budget | unread key |
| must_pass_compiled_out = 25 budget | unread key |
| generator_exit_code in baselines.json | unread key |
| skip-surface trend | flag off |
| validate real-world libs | not invoked |
| coverage degraded/untested budgets | warn-only |
| device / mixed / IPA / ReleaseGates ship_ready | not in CI |

---

## 7. Evidence index (absolute paths)

| Artifact / enforcer | Path |
|---------------------|------|
| BindingTests budgets | `/Users/wojo/Dev/swift-bindings/BindingTests/baselines.json` |
| Wrapper-strip reader | `/Users/wojo/Dev/swift-bindings/build/Build.WrapperStrip.cs` |
| Compile-only orchestration | `/Users/wojo/Dev/swift-bindings/build/Build.BindingTests.cs` |
| Validation baseline model | `/Users/wojo/Dev/swift-bindings/build/Models/ValidationBaseline.cs` |
| Validate gate | `/Users/wojo/Dev/swift-bindings/build/Build.Validation.cs` |
| Unit floor | `/Users/wojo/Dev/swift-bindings/build/Build.Test.cs` |
| Runtime floor + identity | `/Users/wojo/Dev/swift-bindings/build/Build.RuntimeTests.cs` |
| Parity | `/Users/wojo/Dev/swift-bindings/build/Build.Parity.cs`, `…/Helpers/ArtifactParityGate.cs` |
| ApiManifest | `/Users/wojo/Dev/swift-bindings/build/Build.ApiManifestGate.cs` |
| SkipSurface | `/Users/wojo/Dev/swift-bindings/build/Build.SkipSurface.cs` |
| PackGate | `/Users/wojo/Dev/swift-bindings/build/Build.PackGate.cs` |
| ReleaseGates | `/Users/wojo/Dev/swift-bindings/build/Build.ReleaseGates.cs` |
| Coverage report | `/Users/wojo/Dev/swift-bindings/build/scripts/coverage-report.py` |
| PR CI | `/Users/wojo/Dev/swift-bindings/.github/workflows/ci.yml` |
| Release CI | `/Users/wojo/Dev/swift-bindings/.github/workflows/release.yml` |
| Baseline files | `/Users/wojo/Dev/swift-bindings/build/baselines/*.json` |

---

## 8. Audit recommendation (pointer only)

Matches open question **Q4 / DA-W8-T4-001**: either **wire** the four dead `baselines.json` keys through compile-only (coverage-report compare → fail) or **delete** them so the file is single-purpose (`wrapper_stripped_count`). Leaving them continues multi-key confidence theater.
