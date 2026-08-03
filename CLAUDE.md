# Claude Code Guide for Swift Bindings

Swift/.NET interop: generates C# bindings from compiled Swift libraries (`.dylib` + ABI JSON) for .NET 10.0 on Apple platforms. MIT licensed, maintained by Justin Wojciechowski.

## Repository Structure

- `build/` — Nuke Build targets (C#) + `validation-libraries.json` + `scripts/` (e.g. `coverage-report.py`)
- `src/Swift.Bindings/src/` — Generator: Parser → TypeDatabase → Marshaler → Emitter
- `src/Swift.Bindings.Sdk/` — MSBuild SDK (`SwiftBindings.Sdk`): `Sdk.props`, `Sdk.targets`
- `src/Swift.Bindings.Templates/` — `dotnet new swift-binding` template
- `src/Swift.Runtime/src/Swift/` — Runtime library (NuGet: `SwiftBindings.Runtime`)
- `BindingTests/` — End-to-end test library + runtime tests (Simulator + Device/NativeAOT)
- `src/docs/` — Internal design docs, roadmap, known issues
- Public docs: [GitHub wiki](https://github.com/justinwojo/swift-dotnet-bindings/wiki) (separate repo)

## Building & Testing

**Always use `nuke <target>`, not raw commands.** For slow targets, pipe to a temp file (`2>&1 | tee /tmp/<name>.txt`) and Read it — never re-run a slow command just to see a different slice of output.

| Target | Time | Purpose |
|---|---|---|
| `nuke compile` | fast | Build the project |
| `nuke test` | ~2 min | Unit + integration tests |
| `nuke validate` | ~5 min | Compile gate across real-world libs. Flags: `--tier N`, `--filter X` |
| `nuke fetch` | — | Download xcframeworks (first time only) |
| `nuke binding-tests [flags]` | varies | End-to-end BindingTests gate — see flag table below |
| `nuke pack --version X.Y.Z --apple-version A.B.C` | fast | Build all 4 NuGet packages (Runtime, Sdk, Templates, Apple) → `/tmp/swift-nuget/`. `--apple-version` is **required** (pack hard-fails without it so the Apple supplement can't silently ride an unrelated version); pass `--skip-apple` to ship the 3 SDK-lane packages only. |

### `nuke binding-tests` flags

One target covers the compile gate and every runtime gate. Platform flags compose — `--sim --device` runs both pipelines back to back.

| Flag | What it does |
|---|---|
| *(no platform flag)* | Default: compile + run iOS Simulator (Mono JIT) — the common inner loop |
| `--compile-only` | Compile gate only: regenerate + compile-check. No app build, no tests. Used by CI. **Fail-closed by default**: generator non-zero exit, dependency-gen exit, and wrapper compilation give-up all hard-fail. |
| `--sim` | Explicit iOS Simulator (Mono JIT) |
| `--device` | Physical iOS device (NativeAOT) |
| `--macos` | macOS |
| `--catalyst` | Mac Catalyst |
| `--tvos` | tvOS Simulator |
| `--strict` | Fail on non-zero generator exit (implied by `--compile-only`'s default; every regeneration path — all runtime lanes plus the standalone `regenerate-bindings` / `compile-check-bindings` / `build-async-wrapper` targets — is fail-closed on that exit by default too) |
| `--permissive` | Opt out of the fail-closed gates — `--compile-only`'s, and every regeneration path's non-zero-generator-exit gate (runtime lanes *and* the standalone regen targets). Local-exploration only. |
| `--skip-regen` (~17s) | Skip binding regeneration; assumes bindings are current |
| `--skip-build` (~5s) | Skip app build; just install + run |
| `--class-filter NAME` | Run only one test class (Simulator path) |
| `--skip-surface` | Layer B skip-surface **trend** gate. Final post-step in the `--compile-only` path (after the parity, API-manifest, resilience-kitchen and ingestion-kitchen gates): parses skip markers straight out of the generated `.cs` under `BindingTests/output/` and diffs against `build/baselines/skip-surface-baseline.json`, ratcheting skip-class counts downward over time. Run by CI in the release gate. Scans the generated files rather than a metrics sidecar on purpose — the markers ratcheted are exactly what a consumer reading the binding sees. |
| `--mixed-pack` | **Opt-in, heavyweight.** Packs a mixed (ObjC+Swift) binding into ONE nupkg and consumes it via a single `PackageReference` on iOS sim/device. Composes with `--sim`/`--device` (defaults to sim). Never in the default run or `--compile-only`. See note below. |
| `--mixed-direct` | **Opt-in, heavyweight, sim-only.** Builds a mixed (ObjC+Swift) binding in **SDK-direct mode** — the app's OWN csproj imports `SwiftBindings.Sdk` + `<SwiftFramework>`, so the app IS the binding project (no PackageReference) — then runs it on the iOS Simulator. The runtime gate for consumption *path b*. Mutually exclusive with `--mixed-pack`/`--appstore-hygiene`/`--partial-success-kitchen`; never in the default run or `--compile-only`. See note below. |
| `--appstore-hygiene` | **Opt-in, heavyweight, host-only.** TN2435 App Store hygiene gate (issue #42). Packs `SwiftBindings.Runtime` to a local feed and first asserts the runtime nupkg's native packaging *structurally* (cheap, no signing): the `SwiftBindingsRuntime.xcframework` device + simulator framework slices are present, `buildTransitive/SwiftBindings.Runtime.targets` ships, there is **no** loose `libSwiftBindingsRuntime.dylib` and **no** `add-swiftsupport-folder.sh`, and `lipo -archs` on the extracted slices matches (device `arm64`, sim `arm64`+`x86_64`). Then, from one single-`PackageReference` consumer app, it runs an **IPA leg** (`ios-arm64`, `BuildIpa=true`) and asserts the finished `.ipa` is TN2435-compliant: the runtime embeds as a signed `Frameworks/SwiftBindingsRuntime.framework/SwiftBindingsRuntime` (install_name `@rpath/SwiftBindingsRuntime.framework/SwiftBindingsRuntime`, `codesign --verify --strict` passes), there is **no** loose `Frameworks/libSwiftBindingsRuntime.dylib`, **zero** `libswift*.dylib` embedded anywhere (a stable-ABI min-iOS-15 app links the OS-resident `/usr/lib/swift`), **no** top-level `SwiftSupport/` folder, no `.DS_Store`/`__MACOSX`, and the app signature verifies. Builds + inspects on the build host (needs a signing identity; **no** device/sim — platform flags are ignored). Mutually exclusive with `--mixed-pack`/`--mixed-direct`/`--partial-success-kitchen`; never in the default run or `--compile-only`. |
| `--partial-success-kitchen` | **Opt-in, host-only, fast (~30s).** The *partial-success product gate*. Builds the deliberately-hostile `BindingTests/Sources/PartialSuccessKitchen/` fixture — a tiny pure-Swift module mixing two must-emit positive controls (`KitchenOk`, `KitchenOkClass`) with a dozen unsupported shapes (SwiftUI View, PATs/existentials, closure-bearing members, parameter packs, internal-parent members, synthesized Codable) — into a one-slice simulator xcframework, generates, and asserts five things fail-closed: the generator exits **0**, no `SWIFTBIND108` (dangling wrapper `EntryPoint`) in the log, the emitted C# **actually compiles** (a real `dotnet build`, not a string assert), both positive controls are declared, and the skip report clears its design floors (`ReviewCount == 0`) **and matches `build/baselines/partial-success-kitchen-baseline.json` exactly**. No app build, no sim/device run. Mutually exclusive with `--mixed-pack`/`--mixed-direct`/`--appstore-hygiene`; never in the default run or `--compile-only`. See note below. |

The compile gate (`--compile-only`) and the runtime gates are complementary: the first asks "does it compile?", the second asks "does it pass?". Generator/emitter changes want both — run `nuke binding-tests --compile-only` then `nuke binding-tests --skip-regen`. For runtime-only C# changes, `nuke binding-tests --skip-regen` alone is enough.

**Gates inside `--compile-only` that have no flag.** Several gates run on *every* `--compile-only` invocation and are deliberately not opt-in — there is no CLI switch to skip them, and `--skip-surface` is the only compile-gate step that is flag-gated. In run order: the wrapper-strip tripwire and the wrapper getter-parity oracle (inside the async-wrapper build), the ObjC-umbrella xcframework build + regen (iOS only) and the CSM recovery-annotation assertion (inside build/regen), then the **artifact-parity gate**, the **API-manifest gate**, the **resilience-kitchen gate**, the **ingestion-kitchen gate**, and the **overload-name gate** (reads the resolver's own disambiguation records from the generated `binding-report.json`s and fails if any overload name is a bare numeric suffix — policy, not baseline, so it has no reseed target and no `--permissive` arm). The API-manifest gate is the two-sided ABI-contract check: it diffs the emitted public surface against `build/baselines/api-manifest-baseline.json` and is additive-tolerant (added members pass, removed members fail), reseeded by `nuke seed-api-manifest-baseline`. **When a fixture change legitimately moves both baselines, reseed the manifest baseline first** — the manifest gate runs before the skip-surface gate and throws first, so reseeding in the other order just re-reds. `--permissive` demotes only the gates explicitly wired to it (parity, API-manifest, wrapper give-up, generator/dep-gen exit); the resilience-kitchen, ingestion-kitchen and CSM-recovery assertions are fail-closed regardless.

**When/why to run `--mixed-pack`.** It closes the one gap the macOS-host PackGate cannot: a mixed (ObjC+Swift) binding, packed into a single NuGet package and consumed via one `PackageReference`, actually *linked and run* on the iOS runtimes where duplicate-ObjC-class registration bites — iOS Simulator (Mono JIT) and physical device (NativeAOT). This is the exact shape of the issue #40 "Class X is implemented in both …" report. PackGate proves the *nupkg structure* (static source dropped, wrapper sole-carrier, companion embedded in `lib/`) and runs the consumer on the macOS host; `--mixed-pack` adds the iOS loader + Mono-JIT/NativeAOT runtime coverage. It is **deliberately not part of the inner loop**: it packs the Runtime/SDK/Apple feed, builds a 2-slice iOS mixed xcframework, packs the fixture, then builds (sim) or NativeAOT-publishes (device) a fresh consumer — minutes, not seconds, and it needs a booted simulator (`--sim`) and/or a provisioned device (`--device`). Run it **before a release** and **after changes to native packaging policy, the ObjC companion pack path, calling conventions, or struct/P-Invoke marshalling** — not on every iteration. Examples: `nuke binding-tests --mixed-pack` (sim only), `nuke binding-tests --mixed-pack --device` (device only), `nuke binding-tests --mixed-pack --sim --device` (both).

**When/why to run `--mixed-direct`.** A mixed binding supports three consumption modes: (a) a single packed `PackageReference` — covered by `--mixed-pack`; (b) **SDK-direct**, where the consuming app's own csproj imports `SwiftBindings.Sdk` and declares `<SwiftFramework>` so the app *is* the binding; and (c) a local `ProjectReference` to a generated binding csproj. Paths b and c surface the ObjC companion's managed assembly to a *different* assembly's compile (path b via the SDK's `_ReferenceMixedObjCCompanion` target, path c via the emitted `{PackageId}.ProjectReference.targets`) rather than through a package's `lib/`. `--mixed-direct` is path b's runtime gate: it builds the SDK-direct app, runs it on the Simulator, and asserts the ObjC type round-trips **and** the class registers exactly once (no "Class X is implemented in both …") — both at runtime *and* structurally (the generated companion csproj dropped its source `NativeReference`). **Coverage by path: a → `--mixed-pack` (sim+device runtime); b → `--mixed-direct` (sim runtime + structural); c → unit tests only** (`ConsumerTargetsEmitterTests` assert the emitted `.ProjectReference.targets` injects the companion `<Reference>` and the SWIFTBIND042 fail-closed guard) — path c has no dedicated iOS runtime leg, since it shares path b's `_BuildMixedObjCCompanion` build path and the same plain-`<Reference>` surfacing mechanism. It is **sim-only by design** — the native single-registration story is keyed on linkage, not consumer path, and is already device-proven by `--mixed-pack`; the new surface here (injecting the companion `<Reference>` into the app's compile + copy-local) is fully observed on the Mono-JIT simulator. Same trigger cadence as `--mixed-pack`: before a release and after changes to the ObjC companion build/reference path or native packaging policy. Example: `nuke binding-tests --mixed-direct`.

**When/why to run `--partial-success-kitchen`.** Every other gate asks "does the *supported* surface work?" This one asks "does the *unsupported* surface fail honestly?" — the day-1 promise to a third-party consumer that a library containing shapes we can't bind still yields a clean partial binding rather than a hard failure or a silently-empty shell. **Don't confuse it with `--skip-surface`**: that one is an aggregate *trend* ratchet over the whole BindingTests corpus and runs in CI; this one is an *exact* frozen-baseline assertion over one small hostile fixture and runs nowhere in CI. It is cheap enough (~30s, host-only, no app build) to run on any generator/emitter change and belongs in the pre-release sweep. **Reseeding is a deliberate act, not a reflex**: the report compare is exact, so an intentional change to which members bind fails the gate exactly like a regression does. When it goes red, read the drift lines — if the change is intended, reseed `build/baselines/partial-success-kitchen-baseline.json` *in the same commit as the generator change* so the baseline's `git_sha` stays meaningful; if it isn't, you just caught a skip-surface regression that nothing else would have. Example: `nuke binding-tests --partial-success-kitchen`.

## Generator CLI

```bash
dotnet run --project src/Swift.Bindings/src -- --xcframework /path/to/Library.xcframework -o /path/to/output/
cd /path/to/output && dotnet build {Module}.Swift.iOS.csproj
```

Do **NOT** pass `-p:EnableDefaultCompileItems=false` on the command line — it propagates as a global property and breaks `Swift.Runtime` (which relies on default Compile items). The generated csproj already sets it locally.

All options: `dotnet run --project src/Swift.Bindings/src -- --help`. Validation libraries declared in `build/validation-libraries.json`; for SPM-only libs use [`spm-to-xcframework`](https://github.com/justinwojo/spm-to-xcframework) — don't write custom build scripts.

## NuGet & SDK

- **Package prefix is `SwiftBindings.*`** (not `Swift.*` — reserved by Microsoft). Assembly/namespace stays `Swift.Runtime`.
- SDK source: `src/Swift.Bindings.Sdk/Sdk/`. Automates generate → compile → pack into `dotnet build`.
- SDK inter-framework deps use `<SwiftFrameworkDependency>` with `PackageId` + `PackageVersion`.

### Releasing

Releases are cut by GitHub Actions (`.github/workflows/release.yml`), triggered by pushing a `release/**` branch whose name encodes the lane + version(s):

| Branch | Lane | Publishes |
|---|---|---|
| `release/sdk-X.Y.Z+apple-A.B.C` | combined | All 4 packages — Runtime/Sdk/Templates at `X.Y.Z`, Apple at `A.B.C` |
| `release/sdk-X.Y.Z` | SDK only | Runtime/Sdk/Templates at `X.Y.Z` (Apple stays at its latest `apple-v*`) |
| `release/apple-A.B.C` | Apple only | SwiftBindings.Apple at `A.B.C` (SDK lane stays at its latest `sdk-v*`) |

- Append `-dryrun.N` (e.g. `release/sdk-0.15.0-dryrun.1`) to validate + pack **without** publishing, tagging, or releasing.
- A prerelease version (`-preview`/`-alpha`/`-beta`/`-rc`) marks the GitHub Release as a prerelease.
- Publish is gated on `nuke test`, `nuke binding-tests --strict --compile-only` + a tier-2 sim run, `nuke validate-blast-radius`, and a NuGet preflight. On success the pipeline `dotnet nuget push`es each nupkg, pushes the lane tag(s) (`sdk-v*` / `apple-v*`), and creates the GitHub Release with nupkgs attached (notes from a branch `RELEASE-NOTES.md` if present, else auto-generated).
- The NuGet key is the repo's `NUGET_API_KEY` **GitHub Actions secret**, consumed directly by `dotnet nuget push` — there is no `nuke publish` target.

**Runtime-contract floor — decide it on every minor bump.** The load-time `RuntimeContract` gate accepts a binding whose epoch falls in `[MinimumSupportedGeneratedVersion, Version]` (`src/Swift.Runtime/src/Swift/Runtime/RuntimeContract.cs`). `Version` and the emitted epoch derive automatically from the package version; the **floor is the one hand-set value**, and the gate **fails open** — if a release breaks the module-init↔runtime dispatch contract and the floor is *not* raised, old bindings keep loading silently and the safety net becomes false confidence. So at every release, ask: did this version break the dispatch contract? Breaking changes happen only at a **minor** bump (a patch is additive-only). If yes → raise `MinimumSupportedGeneratedVersion` to the new minor (guarded by a floor↔minor unit test). **0.x → 1.0 is the high-stakes case**: epoch jumps from `<1000` to `1000`, so 0.x bindings would still pass a floor left at 16 — decide deliberately whether 1.0 is a compatibility reset (floor → 1000, 0.x bindings rejected) or a pure stability marker (floor stays low). That value is an owner decision; surface it, don't autopilot.

## Working Guidelines

- **No shortcuts.** Prefer the correct long-term solution over a patch that papers over the real issue — root-cause fixes, not symptom suppression, not "skip the failing test", not weakening an assertion to make it green. If you're unsure whether a fix addresses the root cause or whether a short-term workaround is acceptable, ask the user before proceeding.
- **Prediction-gate freeze policy.** Before adding a new hand-coded emission-time prediction gate (a `SkipReason.*` / `MemberValidationPipeline` / `WrapperValidation` predicate), apply the criterion: a new prediction gate is justified **iff the failure it prevents would _compile_**. A compile-error-catchable shape goes to the verify-recover loop, not a new predictor; only soundness conditions the compilers can't see (ABI mismatch, indeterminate layout, register-convention violations) warrant a new gate. Full policy: `src/docs/roadmap.md` § "Prediction-gate freeze policy".
- Do NOT commit unless the user explicitly asks. Commit messages: subject + 1–3 sentences on the *why*. No numbered sub-changes, no "Session N handoff", no "Gates passing" footers. Don't reference session/phase numbers from docs.
- **Never `git stash`** — linter hooks detect reverted files; `stash pop` discards changes silently.
- When fixing a bug pattern, grep the whole codebase for ALL instances before finishing.
- After generator changes, verify generated output compiles — don't assume.
- Test files are organized by domain (closures, generics, …), not by milestone/session/SDK version.
- **Assert behavior, not implementation.** Prefer semantic checks (`output contains CallConvCdecl`, round-trip value preserved) over exact string matches of generated code. Use `[Theory]`/`[InlineData]` for input-only variations.
- **Bug-first testing**: when writing tests for untested code, read it first and look for bugs — don't assume existing behavior is correct. Flag suspected bugs explicitly.
- **New work ships with tests.** Every new feature, bug fix, and behavioral change needs coverage at the right layer — unit tests for generator/emitter/parser logic, runtime tests for marshalling and P/Invoke behavior, BindingTests for end-to-end ABI validation. Match the layer to what actually exercises the change (see *BindingTests are the real end-to-end gate* below). "It's covered by an existing test" is only true if you can point at the assertion.
- **Keep the main context clean**: offload exploration that needs >3 searches or spans multiple files to the `Explore` subagent.

### BindingTests are the real end-to-end gate

Unit tests catch logic bugs. **BindingTests** catch ABI mismatches, calling-convention bugs, and marshalling crashes that unit tests CANNOT. Required for generator, emitter, or runtime changes. Add Swift source to `BindingTests/Sources/SwiftBindingsTestLib/` and C# tests to the matching domain file in `BindingTests/RuntimeTestsApp/`. When fixing a `nuke validate` bug, reproduce the Swift pattern in BindingTests so it's permanently covered.

`nuke binding-tests` (default sim) is the everyday runtime gate. Also run `nuke binding-tests --device` when changes touch calling conventions, struct marshalling, or P/Invoke signatures (Mono and NativeAOT have different bugs), and after fixing any NativeAOT-skipped test.

### Final validation gates (run only what the change warrants)

`nuke test` (unit tests) and `nuke binding-tests` (BindingTests) are the everyday signals — fast, targeted, and the layers where new coverage should land. **`nuke validate` is opt-in**, not part of the routine inner loop: it takes ~5 minutes and re-runs the full real-world library sweep. Only run it for (a) larger refactors / cross-cutting generator or emitter changes where category-wide regression is plausible, (b) pre-release regression sweeps, or (c) when you genuinely want the insight from a validation-libraries run. Don't burn ~5 minutes per change "just in case" — that is what's hurting velocity.

| What changed | `nuke test` | `nuke binding-tests` | `nuke validate` |
|---|---|---|---|
| Generator / emitter / parser | Yes | Yes (default sim run). Add `--device` if calling conventions or marshalling changed. | Optional — only for cross-cutting changes or pre-release sweeps |
| Runtime (`Swift.Runtime`) | Yes | Yes (`--skip-regen`). Add `--device` if marshalling changed. | Optional — only if marshalling changed *and* the change plausibly affects multiple libs |
| Test infrastructure only | No | Just the target touched | No |
| Docs / research / external repos | No | No | No |

**Zero-regression policy**: BindingTests pass count and unit test pass count must be ≥ baseline before committing — these are the per-commit gates. `build/baselines/validation-baseline.json` (`cs_compile` + `swift_compile`) only needs to be ≥ baseline *when you actually run `nuke validate`*; if you didn't run validate this change, you don't need to defend against it. No "will fix later" for the gates that ran.

## Known Issues

- **ALL runtime crashes are OUR BUGS until proven otherwise.** 102/102 tests once labeled `[MonoJitCrash]` turned out to be generator/runtime bugs in our code. The authoritative list of confirmed upstream .NET bugs lives in memory at `feedback_mono_jit_blame.md` — anything not on that list is ours. Before blaming the runtime, verify the generated C# P/Invoke matches the Swift `@_cdecl` wrapper: calling convention (`CallConvCdecl` vs `CallConvSwift`), parameter count, parameter types, library name, entry point symbol.
- `DllImportResolver` conflict: `[ModuleInitializer]` + consuming app both call `SetDllImportResolver` → `InvalidOperationException`. `Swift.Runtime/.../SwiftFrameworkResolver.cs` wraps in try-catch.
- **Runtime native ships as a framework, never a loose dylib (Apple TN2435).** `SwiftBindings.Runtime` packages its native code as `SwiftBindingsRuntime.xcframework` (device + simulator framework slices) so it embeds into an `.ipa` as a signed `Frameworks/SwiftBindingsRuntime.framework`. A loose `Frameworks/lib*.dylib` (anything other than Apple's own `/usr/lib/swift` `libswift*`) fails App Store submission. Do **not** "fix" packaging by emitting a loose dylib, and do **not** re-introduce a top-level `SwiftSupport/`-folder injector (no `add-swiftsupport-folder.sh`) — a stable-ABI min-iOS-15 app links the OS-resident Swift and needs neither; both were tried and removed. The `--appstore-hygiene` gate enforces all of this structurally and on a built `.ipa`.
- Generator open bugs and blocked items: see `src/docs/roadmap.md`. Acknowledged-but-not-planned latents, deferred designs, and pending owner decisions: `src/docs/not-planned.md` (each entry has a reopen trigger — don't pick these up as next work).
- Consumer-facing limitations: [wiki Known Limitations](https://github.com/justinwojo/swift-dotnet-bindings/wiki/Known-Limitations).

## Key References

- `src/docs/roadmap.md` — statement of intent (work we expect to do) plus hard policy boundaries (confirmed-upstream blocks, out-of-scope/by-design). **Not** a complete index of active work: we often work from dedicated docs (top-level `src/docs/*.md`, subsystem folders) that are not listed in roadmap at all. When picking next work or proposing direction, check both roadmap *and* recent top-level docs; ask the user when ambiguous rather than assuming roadmap is exhaustive.
- `src/docs/not-planned.md` — acknowledged-but-not-planned register: trigger-gated latents, deferred designs, declined refactors, pending owner decisions, organized by area. Nothing there is queued; an entry reopens only when its trigger fires. When closing out work, route leftovers here — not into roadmap.
