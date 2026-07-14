# M0-D — Test Landscape Map

**Wave**: 0 (map)  
**Agents**: M0-D  
**Mode**: Read-only inventory  
**Date**: 2026-07-15  
**Scope**:
- `src/Swift.Bindings/tests/`
- `BindingTests/Sources/`
- `BindingTests/RuntimeTestsApp/`
- `BindingTests/baselines.json`
- `.claude/rules/bindingtests.md`

**Not in scope**: Runtime unit tests under `src/Swift.Runtime/tests/` (separate map if needed later); Nuke `validate` real-library corpus (M0-C).

---

## 1. Unit test organization by domain

Root: `src/Swift.Bindings/tests/UnitTests/` (xUnit project `Swift.Bindings.Unit.Tests.csproj`, .NET 10).

Architecture-review (2026-06) measured ~271k LOC / ~10k tests; structure is **domain folders mirroring generator layers**, not milestone/session names (project rule).

| Domain folder | Approx. `*Tests.cs` count | Purpose |
|---|---:|---|
| **EmitterTests/** | ~190 | Handler/emitter output: P/Invoke, wrappers, protocols/EveryProtocol, closures, async, CSM, SwiftUI bridge, thunks, validation pipelines, vtable layout |
| **MarshalerTests/** | ~40 | Projection factory, plans, name provider, closures/optionals/tuples/generics, collision suffix, public method naming |
| **ParserTests/** | ~35 | ABI JSON ingestion, demangle markers, generics, protocols, enums, swiftinterface facts, ObjC import names, provenance |
| **ConfigurationTests/** | ~30 | XCFramework resolve/slice/merge, wrapper compile/post-process, native packaging, auto-deps, mixed-framework detection, toolchain matrix |
| **ObjCTests/** | ~25 (Parser/Emitter/Pipeline/Model) | Clang AST → bgen ApiDefinition/StructsAndEnums; mixed-framework pipeline; availability |
| **TypeDatabaseTests/** | ~10 + Resolver/ | Type records, extensions, ConformanceGraph, AppleFrameworkRegistry, TypeResolver strategies |
| **DemanglerTests/** | ~10 | Swift5 demangler, async/variadic markers, reduction corpus, punycode |
| **ReportingTests/** | ~11 | ReportCollector, skip disposition/triage, emission reports, suppressed-proxy honesty, wrapper-symbol integrity |
| **SdkTests/** | ~7 | Sdk.props/targets behavior, Apple supplement csproj, compile smoke, runtime range restore |
| **TypeSpecTests/** | ~5 | TypeSpec parse/helpers, associated-type refs, raw-value normalization |
| **AppleTypesManifestTests/** | ~4 | Apple-types manifest build/validate/emit/CLI |
| **ModelTests/** | ~3 | TypeRecord, MarkEmitted, FrameworkDependencyInfo |
| **TypeNameTests/** | ~2 | SwiftTypeName / CSharpTypeName |
| **RuntimeTests/** | ~2 | Optional span size, symbolic reference grammar (generator-adjacent) |
| **TbdParserTests/** | ~1 | TBD parsing |
| **StdlibConformancesTests/** | ~1 | Stdlib conformances regen command |
| **Root-level** | ~15 | ApiManifest baseline, ArtifactParityGate, BindingsGeneratorCommand, CatchFreeUCO, MachOReader, ProjectionCompleteness, ReleaseGates, RuntimeIdentityBaseline, Issue1 skip attribution, etc. |

**Shared fixtures**: `TestDecls.cs`, `SplitModuleSource.cs`, `EmitterTestHelpers.cs`, `ObjCTestHelpers.cs`, `ProtocolExtensionTestHelpers.cs`.

**Parallelism note**: `[Collection("ReportCollector")]` on ReportCollector / SwiftUIBridge / ExistentialBypass / SuppressedProxyReporting prevents races on static collectors.

**Layer claim (project doctrine)**: unit tests catch *logic*; they cannot prove ABI, CallConv, or marshalling — that is BindingTests.

---

## 2. BindingTests domains

### 2.1 Inputs (`BindingTests/Sources/`)

| Path | Role |
|---|---|
| **SwiftBindingsTestLib/** | Primary Swift fixture library (~339 `.swift` + ~8 `.disabled`). Domain folders mirror RuntimeTestsApp. |
| **SwiftBindingsTestLibDependency/** | Cross-module dependency types for alias/inheritance/short-name collision fixtures |
| **ObjCUmbrella/** | Minimal ObjC companion (`.h`/`.m`) for mixed ObjC+Swift packaging legs |
| **SurfaceArea/** | README-only notes (not a fixture library) |

Swift domains under `SwiftBindingsTestLib/` (non-exhaustive): `Async/`, `Closures/`, `Collections/`, `Collisions/`, `CrossModule/`, `EdgeCases/`, `Enums/`, `ErrorHandling/`, `Foundation/`, `Generics/`, `Initializers/`, `Internal/`, `KeyPath/`, `Lifetime/`, `MemoryManagement/`, `Metadata/`, `ObjCInterop/`, `Operators/`, `Optionals/`, `Parameters/`, `Patterns/`, `Properties/`, `Protocols/`, `SmokeFixtures/`, `SwiftUI/`, `Tuples/`, `Types/`, `UnsafeTypes/`, `WrapperCoverage/`, `Wrappers/`, `AppIntents/`.

### 2.2 Runtime app (`BindingTests/RuntimeTestsApp/`)

Discovery-based harness (`TestDiscoveryGenerator` → descriptors; not reflection enumeration). Platforms: iOS sim (default Mono JIT), device NativeAOT, plus sibling runners `RuntimeTestsApp.Mac`, `.MacCatalyst`, `.tvOS`.

| Domain folder | ~files | Purpose |
|---|---:|---|
| **Generics/** | 63 | Bound generics, CSM, PAT parents, constraints, existential bypass, method-level generics, variadic packs |
| **Protocols/** | 48 | Witness dispatch, EveryProtocol reverse dispatch, composition, suppressed proxies, overload collapse, fan-out |
| **Marshalling/** | 44 | Struct/class/enum/optional/string/tuple/array round-trips, Apple types, CallConv pairing, wrapper stripping |
| **Async/** | 29 | Async methods/properties/closures/streams, actors, cancellation, reverse async witnesses |
| **MemoryManagement/** | 16 | Leak probes (existential/async/carrier), dispose, library evolution, VWT destroy |
| **Closures/** | 15 | Escaping/throwing/generic/nested closures, optional throwing void, pointer args |
| **Lifetime/** | 15 | ARC, ownership, dispose scopes, proxy lifetime, reverse-dispatch invariants, GC stress |
| **Collisions/** | 13 | Overload/property-method/nested-type/async-name collisions; declaration-order Scenario E |
| **ErrorHandling/** | 10 | Throwing methods, LocalizedError, Result-of-void, cascade registry |
| **SwiftUIBridge/** | 10 | Hosted view create/session, async patterns, edge cases |
| **SmokeTests/** | 9 | Broad construction/metadata canaries |
| **ObjCInterop/** | 8 | NSObject subclass, selectors, ObjC existentials, reverse vtable lockstep |
| **Types/** | 8 | Core type surface samples |
| **EdgeCases/** | 7 | Availability, optional protocol members, buffer pointers, witness index lockstep |
| **Patterns/** | 7 | Builder/factory/hierarchy real-world compositions |
| **Collections/** | 7 | Dict/set/range/enum-array constructors |
| **Properties/** | 7 | Getters/setters/subscripts/static |
| **CrossModule/** | 5 | Alias, inheritance, short-name collision, nested rename |
| **KeyPath/** | 5 | Foundation KVO route, Route C, protocol bag, singletons |
| **AppleSupplement/** | 4 | ActivityKit readiness, AttributedString, LiveActivity smoke |
| **Operators/** | 3 | Arithmetic/comparison/unary projected operators |
| **Initializers/** | 3 | Basic/failable/throwing/gate-reduced inits |
| **Concurrency/** | 2 | Bulk stress (`[Slow]`) |
| **FoundationInterop/** | 2 | KVO observe + LocalizedStringResource |
| **Internal/** | 2 | Internal parent reach + internal conformer → public protocol |
| **Metadata/** | 2 | ABI layout tripwire, existential metadata |
| **Parameters/** | 2 | Defaults, blittable optional cdecl wrappers |
| **AppIntents/** | 2 | Mock entity / EntityProperty factory |
| **Wrappers/** | 1 | Cdecl wrapper cohesion |
| **Infrastructure/** | 6 | `TestBase`, descriptors, results, skip attrs, logger, flags |

**Outputs**: `BindingTests/output/` — generated `.cs`/wrapper `.swift`, `binding-report.json`, `binding-emission-report.json`, `binding-artifact-manifest.json`. Coverage matrix is **not** produced by normal `nuke binding-tests` (manual `coverage-report.py`).

**Baselines** (`BindingTests/baselines.json`):

| Key | Value (2026-07) | Meaning |
|---|---:|---|
| `generator_exit_code` | 0 | Generator must succeed |
| `must_pass_degraded` | 0 | Zero allowed “must-pass but degraded” cells |
| `must_pass_compiled_out` | 25 | Known `#if`/compiled-out must-pass budget |
| `known_unsupported_total` | 62 | Known-unsupported surface budget |
| `wrapper_stripped_count` | 0 | Post-processor strip tripwire (any **increase** fails) |

Related: `abi-grid-manifest.json` + `nuke binding-tests --abi-grid` grades thin ABI corners (52 cells; expect-green gated on sim+device).

---

## 3. Skip attribute taxonomy & “honest”

Defined in `RuntimeTestsApp/Infrastructure/TestResults.cs`; discovery/runner behavior in `Program.cs` + `TestDiscoveryGenerator`.

| Attribute | Scope | Behavior | Honest use |
|---|---|---|---|
| *(none)* | method/class | Runs on all paths that execute the class | Default for working tests |
| **`[Skip("reason")]`** | method/class | Always skipped | Generator bug, missing entry point, unsupported surface broken **everywhere**. Reason must be specific — not vague runtime blame |
| **`[SkipOnSimulator]`** | method/class | Skip CLI `--platform simulator` mode; run device | Mono/sim harness limitation (e.g. no native runtime dylib on sim → destroy-hook leak fallback). Prefer device validation |
| **`[SkipOnDevice]`** | method/class | Skip device/NativeAOT; run sim | Confirmed device-only limitation |
| **`[SkipOnMonoJit]`** | **method only** | Runtime-detected Mono (sim **and** Catalyst); runs CoreCLR macOS + NativeAOT device | Confirmed Issue 1-class only; reason **must name** the CallConvSwift entry-point symbol (enforced by `Issue1SkipAttributionTests`) |
| **`[SkipOnCatalystX64]`** | **method only** | Skip Catalyst x64 only | Upstream Issue 4 |
| **`[Slow]`** | method/class | Still runs; filterable marker | Stress |
| **`[MonoJitCrash]`** | — | **DEPRECATED** | Do not use; historical false-blame |

### What “honest” means here

1. **Guilty until proven innocent**: every skip/crash is *our* bug until it matches one of **exactly 4** confirmed upstream issues (`src/docs/Future/upstream-issue-0*.md`, roadmap Blocked, memory `feedback_mono_jit_blame.md`). Historical bulk `[MonoJitCrash]` labels were generator bugs.
2. **Narrowest attribute**: platform-specific → `SkipOn*`; Mono-only path that works on CoreCLR → `SkipOnMonoJit` not `SkipOnSimulator` (sim flag also suppresses macOS/Catalyst runners).
3. **Reason quality**: specific generator bug, limitation registry reference, or Issue-N symbol — never “Mono crash.”
4. **Method-only for runtime-detected skips**: class-level `SkipOnMonoJit` / `SkipOnCatalystX64` would be **silently ignored** (discovery only wires class-level for CLI-flag skips) — attribute targets make misuse a compile error.
5. **Compile ≠ product proof**: package BindingAudit repeatedly notes smoke/metadata tests are not headline functional depth; BindingTests runtime methods that only construct without asserting values are weaker than round-trips.

**Disposition taxonomy (generator reporting, not runtime attrs)** — `SkipDispositionClassifier` / `SkipDispositionClassifierTests`:
- `ExpectedNonPublic` / `ExpectedStructural` — correct skips (module-internal, SwiftUI view, synthesized Codable, …)
- `KnownLimitation` — documented consumer-visible gaps (AnyType, unsupported existential, suppressed-proxy degrade, …)
- `Review` — needs human triage (MissingHandler, MissingWrapperSymbol, EveryProtocolConformanceSkipped, …)

Completeness guard: every `SkipReason` enum value must have an explicit disposition.

---

## 4. Gates

Source of truth: `build/Build.BindingTests.cs`, `BindingTests/README.md`, `Claude.md`.

| Gate | Flag / command | What it proves | When |
|---|---|---|---|
| **Unit** | `nuke test` | Generator/parser/marshaler/emitter logic | Every generator change |
| **Compile-only** | `nuke binding-tests --compile-only` | Regen + C# compile + wrapper compile + baselines/tripwires; **no app, no runtime**. Fail-closed (generator exit, dep-gen, wrapper give-up). `--permissive` local only | CI; after generator edits |
| **Sim (default)** | `nuke binding-tests` / `--sim` | Full pipeline + iOS Simulator Mono JIT runtime | Everyday ABI/runtime gate |
| **Device** | `--device` | NativeAOT physical iPhone | CallConv, struct marshalling, P/Invoke, unskip device-only |
| **macOS / Catalyst / tvOS** | `--macos` / `--catalyst` / `--tvos` | Extra platform runners | Platform-specific work |
| **Strict** | `--strict` | Non-zero generator exit fails (implied by compile-only default) | Compose with any mode |
| **Mixed-pack** | `--mixed-pack` | ObjC+Swift **one nupkg** → single `PackageReference` → run sim and/or device (issue #40 class-dup) | Pre-release; packaging / companion / CC / marshalling changes |
| **Mixed-direct** | `--mixed-direct` | SDK-direct (app *is* the binding) sim-only; companion `<Reference>` injection + single ObjC class registration | Same cadence as mixed-pack; path **b** of three consume modes |
| **App Store hygiene** | `--appstore-hygiene` | Host-only: Runtime nupkg structure + device IPA TN2435 (signed framework, no loose dylib, no `libswift*`, no `SwiftSupport/`) | Pre-release; packaging/TN2435 changes |
| **ABI grid** | `--abi-grid` | Declared thin-corner cells green on declared runtimes | Grid maintenance; ignored under compile-only |
| **Validate** | `nuke validate` | Real-world library sweep (~5 min) | Cross-cutting / pre-release — **not** inner loop |
| **Pass-count floor** | BindingTests + unit counts ≥ baseline | Zero-regression policy for gates that *ran* | Pre-commit |

**Mutual exclusion**: mixed-*/appstore-hygiene cannot combine with each other or with `--compile-only`. Platform flags compose for sim/device; mixed-pack only composes with sim/device; mixed-direct is sim-only; appstore ignores platform flags.

**Inner-loop shortcuts**: `--skip-regen` (~17s), `--skip-build` (~5s), `--class-filter NAME`.

**Stale generator hazard**: gates invoke prebuilt `GeneratorDll`; `EnsureGeneratorBuilt` only rebuilds if **missing**. After generator source edits, `dotnet build src/Swift.Bindings/src -c Debug` (or `nuke compile`) before trusting regen.

---

## 5. Implementation vs behavior assertions

Project rule (`Claude.md`): **assert behavior, not implementation** — e.g. output contains `CallConvCdecl`, round-trip value preserved — not exact full-body string match. Use `[Theory]` for input-only variations.

### Behavior-leaning (preferred)

| Layer | Example pattern |
|---|---|
| BindingTests | Construct Swift type → call projected API → assert returned value / exception / dispose |
| Unit (semantic) | `Assert.Contains("CallConvCdecl", …)` / projected type name / disposition enum |
| Reporting | `SuppressedProxyReportingTests` — degrade site tokens land in `binding-report` as `KnownLimitation` |
| Parity gates | `ArtifactParityGate`, wrapper-strip count, ABI grid expect-green cells |
| Protocol keys | BindingTests `KeyBuilderAsyncOverloadProtocol` — both async+sync members bind correctly |

### Implementation-leaning (common in unit suite; post-1.0 debt)

| Pattern | Where |
|---|---|
| Exact emitted helper strings (`" where DonationInfo == …"`) | `WrapperEmitterHelpersTests` |
| Hardcoded local/plan fragments (`resultPtr`, setup statement substrings) | Many `*Emitter*Tests`, `TypeProjectionFactoryTests` |
| Private helper boolean predicates | `GenericDispatchEmitterTests.HasGenericOuterAncestor` |
| Full substring of generated method bodies | Widespread in EmitterTests |

Post-1.0 architecture roadmap explicitly lists “4K substring assertion migration to plan assertions” under deferred test rebuild. Wave 8 (M4 / test honesty) should inventory high-churn substring tests that would not catch wrong-but-rephrased emission.

---

## 6. Coverage gaps obvious from structure (Wave 8 seeds)

These are **structural** observations — not proven product bugs.

1. **Compile-only without runtime twin** — large fixture surface in `Sources/` may only be compile-gated if no matching RuntimeTestsApp assertion exists (M4 hunt: “compile-only coverage not backed by runtime”).
2. **Domain imbalance** — Generics/Protocols/Marshalling dominate file count; **Operators**, **Parameters**, **Metadata**, **AppIntents**, **Wrappers** are thin.
3. **`.disabled` Swift fixtures** — PropertyWrappers, some Foundation (Data/URL/Extensions), KeyPaths/Metatypes/PointerGenerics variants, EdgeCases/Keywords+Visibility copies — permanent non-goals or forgotten work?
4. **Platform matrix gaps** — default inner loop is iOS sim; device is opt-in for CC-sensitive work; tvOS device runner **missing** (roadmap low priority); Catalyst x64 largely skip-attributed.
5. **Mixed consume path c** — ProjectReference companion surfacing is unit-tested (`ConsumerTargetsEmitterTests`) **without** dedicated iOS runtime leg (documented: shares path b build path).
6. **Package product depth** — BindingAudit / binding-surface-audit: package tests are construction-heavy, not headline flows (StoreKit purchase, CryptoKit KAT, etc.) — outside BindingTests tree but Wave 8/M4 adjacent.
7. **Skip debt** — many `[Skip]` strings cite generator limitations (weak/unowned, optional closures, cross-module alias emit, nested-of-parent VWT). Need re-triage: still open vs fixed-but-skipped (M4).
8. **Stress under-exercised on device** — `[Slow]` / bulk concurrency primarily sim unless device run is deliberate.
9. **Leak probes often sim-skipped** — destroy-hook / native runtime framework absent on sim (`IncludeSwiftBindingsRuntimeNative=false`) → lifetime correctness **device-biased**.
10. **ABI grid gray cells** — 7 by-design-gray + 4 supported-low-priority are documented non-goals; do not re-file as gaps without consumer demand.
11. **Implementation-assert concentration** — EmitterTests size + substring style → high false-green risk under refactors (C1 / Wave 8 honesty).
12. **No automated coverage-matrix in CI** — `coverage-report.py` is manual; degraded feature statuses (passing/degraded/missing/compiled_out) not continuously published.

---

## 7. Tests vs graceful degradation

Graceful degradation (lens L3 / track G1): **skip unbindable surface cleanly** rather than emit broken C#/Swift or abort the whole binding.

| Mechanism | Tested? | How |
|---|---|---|
| **SkipReason exhaustiveness + disposition** | Yes | `SkipDispositionClassifierTests.EverySkipReason_HasExplicitDisposition` + theory rows |
| **Skip triage / reporting rows** | Yes | ReportingTests, `SuppressedProxyReportingTests` (produce-throw / consume-degraded / receiver-failfast site tokens) |
| **Unsupported surface attributes** | Partial | `UnsupportedSwiftTypeSupportTests`; generated `[UnsupportedSwiftType]` inspection |
| **Member validation fail-closed** | Yes | `MemberValidationPipelineTests`, `WrapperValidationTests`, ObjC existential fail-closed tests |
| **Compile-only fail-closed** | Yes (gate) | Non-zero generator, wrapper give-up hard-fail unless `--permissive` |
| **Wrapper strip tripwire** | Yes | `wrapper_stripped_count == 0` baseline; increase fails |
| **Artifact parity (Swift/C# slot names)** | Yes | `ArtifactParityGate` (+ unit `ArtifactParityGateTests`) |
| **Known-unsupported budgets** | Yes | `known_unsupported_total`, `must_pass_degraded`, `must_pass_compiled_out` in baselines |
| **Protocol conformance drop (naming divergence)** | Yes | `ProtocolConformanceValidatorTests` — drop conformance vs CS0535 |
| **Suppressed proxy degrade honesty** | Yes unit; partial runtime | Unit reporting pins; runtime `SuppressedProxyChannelTests` (some FailFast paths still `[Skip]` needing subprocess) |
| **Skip-clean compile of full BindingTests fixture** | **Yes — primary** | `--compile-only` + CompileCheck project: entire generated surface must compile after intentional skips |
| **Skip-clean on real libraries** | Validate / package gates | `nuke validate` + BindingAudit static; not every skip disposition re-asserted per library in-repo |
| **Runtime “skipped member is absent / throws documented”** | Sparse | Some tests assert `NotSupportedException` / missing entry; many skips are test-method skips, not API-surface degrade probes |

### Gaps for G1 / Wave 7–8

- Prefer **honest absence or classified degrade** over public members that always throw (binding-surface-audit P0: RealityFoundation `Materials` getter).
- Few tests assert “binding-report disposition matches runtime behavior” end-to-end for a degraded member.
- FailFast-by-design paths cannot be fully proven in-process (harness limitation) — subprocess gate not present.

---

## Quick reference: layers

```text
nuke test                    → UnitTests (logic)
nuke binding-tests --compile-only → generate + compile + baselines (skip-clean compile)
nuke binding-tests           → + iOS sim runtime (ABI truth)
nuke binding-tests --device  → + NativeAOT
nuke validate                → real libraries (opt-in)
mixed-*/appstore-hygiene     → packaging consumption (opt-in, heavy)
```

## Handoff

- **Wave 1+ deep tracks**: use BindingTests domains as the ABI oracle for A1–A7; unit EmitterTests for structural claims only with verification.
- **Wave 8 / M4**: skip inventory, runtime-vs-compile coverage matrix, skip honesty re-triage.
- **Wave 7 / G1**: degrade reporting contracts + “compile but dead” public API shapes.
- **Do not** re-blame Mono without checking the 4 upstream filings and generated CallConv/symbol pairing.
