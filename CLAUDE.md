# Claude Code Guide for Swift Bindings

## Project Overview

Experimental Swift/.NET interop project. Generates C# bindings from compiled Swift libraries (.dylib + ABI JSON) for .NET 10.0 on Apple platforms. Originally Microsoft, now maintained by Justin Wojciechowski. MIT License.

## Repository Structure

- `build/` — Nuke Build targets (C#): compile, test, validate, pack, runtime tests
- `src/Swift.Bindings/src/` — Generator: Parser → TypeDatabase → Marshaler → Emitter
- `src/Swift.Bindings.Sdk/` — MSBuild SDK package (`SwiftBindings.Sdk`): `Sdk.props`, `Sdk.targets`
- `src/Swift.Bindings.Templates/` — `dotnet new swift-binding` project template
- `src/Swift.Runtime/src/Swift/` — Runtime: SwiftString, SwiftArray, SafeHandle, ARC (NuGet: `SwiftBindings.Runtime`)
- `BindingTests/` — Comprehensive test library + runtime tests (Simulator + Device/NativeAOT)
- `build/validation-libraries.json` — Library validation manifest (90 targets across 46 libraries)
- `build/scripts/` — `coverage-report.py` (coverage matrix), `ci/` (CI orchestrator scripts)
- `src/docs/` — Internal design docs, status, known issues
- Public-facing documentation lives in the [GitHub wiki](https://github.com/justinwojo/swift-dotnet-bindings/wiki) (separate repo)

## Building & Testing

**Always use `nuke <target>`, not raw commands.**

**IMPORTANT: Slow commands (`nuke test` ~2 min, `nuke binding-tests` ~5 min, `nuke runtime-tests-simulator` ~3 min, `nuke runtime-tests-device` ~5 min, `nuke validate` ~1 min) — ALWAYS pipe to a temp file with `2>&1 | tee /tmp/<name>-results.txt`. Then use the Read tool on the temp file to inspect results. This avoids re-running slow commands just to see different slices of output. NEVER run a slow command twice.**

```bash
nuke compile                          # Build the project
nuke test                             # Run all unit + integration tests

# BindingTests (after generator changes):
nuke binding-tests                    # Full: xcframework + bindings + bridge
nuke binding-tests --strict           # Strict mode (fail on non-zero generator exit)
nuke runtime-tests-simulator          # Runtime on iOS Sim (Mono JIT)
nuke runtime-tests-device             # Runtime on iOS device (NativeAOT)

# Runtime test iteration flags:
#   --skip-regen     Skip binding regeneration (incremental build, ~17s)
#   --skip-build     Skip all builds, just install + run (~5s, use after --skip-regen)
#   --class-filter NAME   Run only one test class

# Real-world library validation:
nuke fetch                            # Fetch xcframeworks (first time)
nuke validate                         # Compile gate (all tiers, 90 targets)
nuke validate --tier 1                # Tier 1 only (34 targets)
nuke validate --tier 2                # Tier 2 only (54 targets)
nuke validate --filter Nuke           # Validate one library

# NuGet packaging:
nuke pack --version 0.1.0             # Build all 3 NuGet packages
```

## Generator CLI Usage

```bash
# Recommended: --xcframework mode (auto-resolves ABI JSON, dylib, TBD, swiftinterface)
dotnet run --project src/Swift.Bindings/src -- \
  --xcframework /path/to/Library.xcframework \
  -o /path/to/output/

# Verify generated bindings compile (need -p:EnableDefaultCompileItems=false for CLI-generated .csproj)
cd /path/to/output && dotnet build {Module}.Swift.iOS.csproj -p:EnableDefaultCompileItems=false
```

All CLI options (including manual mode, ObjC framework detection): `dotnet run --project src/Swift.Bindings/src -- --help`

## Validating Third-Party Libraries

All validation libraries are declared in `build/validation-libraries.json`. For SPM-only libraries, use [`spm-to-xcframework`](https://github.com/justinwojo/spm-to-xcframework) to build xcframeworks — do NOT write custom build scripts.

## MSBuild SDK & NuGet Packages

- SDK source: `src/Swift.Bindings.Sdk/Sdk/` (`Sdk.props`, `Sdk.targets`). Automates generate → compile → pack into `dotnet build`.
- **NuGet package prefix is `SwiftBindings.*`** (not `Swift.*` — that's reserved by Microsoft). Assembly/namespace remains `Swift.Runtime`.
- `nuke pack --version 0.1.1` builds all 3 packages. Output: `/tmp/swift-nuget/`.
- SDK inter-framework deps use `<SwiftFrameworkDependency>` (requires `PackageId` + `PackageVersion` metadata).

## Working Guidelines

### Testing layers (all three matter)

1. **Unit tests** (`nuke test`, ~2 min) — fast iteration on internal logic. Use per sub-task during development.
2. **BindingTests** — **the true end-to-end validation.** Takes real Swift source, generates C# bindings, compiles the Swift wrapper, and runs the result on a real runtime. This catches ABI mismatches, calling convention bugs, and marshalling crashes that unit tests CANNOT detect. Unit tests alone are not sufficient.
   - `nuke runtime-tests-simulator` — **default**: runs on iOS Simulator via Mono JIT. Use for most changes.
   - `nuke runtime-tests-device` — runs on physical iOS device via NativeAOT. Use when changes touch calling conventions, struct marshalling, P/Invoke signatures, or anything where Mono and NativeAOT may behave differently (they have different bugs — see Known Issues). Also run after fixing any NativeAOT-skipped test.
   - `nuke binding-tests` — full pipeline: rebuilds xcframework + regenerates bindings + runs simulator tests. Use when generator/emitter output changed.
3. **Library validation** (`nuke validate`, ~1 min) — compile gate across ~90 real-world library targets. Catches C# and Swift wrapper compilation regressions across diverse API surfaces.

**BindingTests are REQUIRED for generator, emitter, or runtime changes.** If your change affects how bindings are generated or how the runtime marshals data, you MUST add or verify BindingTests coverage. Don't rely on unit tests alone — a unit test can pass while the generated code crashes on a real device. Add Swift source to `BindingTests/Sources/SwiftBindingsTestLib/` and C# runtime tests to `BindingTests/RuntimeTestsApp/`, in the appropriate domain file. When fixing a bug from `nuke validate`, always reproduce the underlying Swift pattern in BindingTests so it's permanently covered.

- When fixing a bug pattern, grep the entire codebase for ALL instances before finishing.
- After code gen changes, verify generated output compiles — don't assume correctness.
- Do NOT commit unless the user explicitly asks.
- **Mid-session iteration**: Use `nuke test` per sub-task for fast feedback. Save `nuke validate` and `nuke binding-tests` for end-of-session gates — running 5+ minute commands repeatedly destroys productivity.
- NEVER use `git stash` — linter hooks detect reverted files and stash pop discards changes silently.
- Test files are organized by domain, not by milestone/session/SDK version. Place tests in their respective domain test files (e.g., closure tests go in closure test files, not in a "phase-15" file).
- **Test quality**: Assert behavior, not implementation details. Prefer assertions on semantic correctness (e.g., "output contains CallConvCdecl", "method compiles", "round-trip marshalling preserves value") over exact string matching of generated code. This prevents tests from breaking when emitter internals change (e.g., extracting helper methods) while the behavior remains correct. Use `[Theory]`/`[InlineData]` when multiple tests differ only in input values.
- **Bug-first testing**: When writing tests for untested code, read and understand the code BEFORE writing tests. Don't assume existing behavior is correct — look for bugs first. Flag suspected bugs explicitly so they can be triaged.

### Final Validation Gates (only when code changes warrant it)

These gates are for sessions that make **code changes to the generator, runtime, emitter, or test infrastructure**. Skip them entirely for research-only sessions, documentation updates, investigation tasks, or work on external projects (e.g., repro projects).

**When to run each gate:**

| What changed | `nuke test` | `nuke validate` | `nuke binding-tests` / `nuke runtime-tests-simulator` |
|---|---|---|---|
| Generator/emitter/parser | Yes | Yes | Yes (`nuke binding-tests`). Also `runtime-tests-device` if calling conventions or marshalling changed. |
| Runtime (`Swift.Runtime`) | Yes | No (unless marshalling changed) | Yes (`nuke runtime-tests-simulator --skip-regen`). Also `runtime-tests-device` if marshalling changed. |
| Test infrastructure only | No | No | Yes (the specific target) |
| Documentation / research | No | No | No |
| Repro project / external | No | No | No |

If a gate fails, fix the regressions before signing off. Do not run gates that aren't relevant to the changes made.

**Zero-regression policy**: Validation baselines must never regress. The `.validation-baseline.json` pass counts (both `cs_compile` and `swift_compile`), BindingTests runtime pass count, and unit test pass count must all be equal to or better than the baseline BEFORE committing. If a change causes any of these numbers to drop, the regression must be fixed before the commit goes in — no exceptions, no "will fix later." This applies to all commits, not just end-of-session gates.

## Known Issues

### Runtime
- **ALL runtime crashes are OUR BUGS until proven otherwise.** 102/102 tests previously labeled `[MonoJitCrash]` were proven to be generator/runtime bugs. Before labeling any crash "upstream", verify the generated C# P/Invoke matches the Swift @_cdecl wrapper: calling convention (`CallConvCdecl` vs `CallConvSwift`), parameter count, parameter types, library name, entry point symbol. See memory file `feedback_mono_jit_blame.md` for the exhaustive list of 6 confirmed upstream .NET bugs — anything not on that list is our bug.
- SafeHandle in async P/Invoke not preserved — upstream Mono issue (workaround: singleton + IntPtr)
- DllImportResolver conflict: `[ModuleInitializer]` + consuming app both call `SetDllImportResolver` → `InvalidOperationException`. RuntimeTestsApp wraps in try-catch.
- See [wiki Known Limitations](https://github.com/justinwojo/swift-dotnet-bindings/wiki/Known-Limitations) for full consumer-facing details

### Generator (open bugs)
- ExistentialContainer0 in tuple element (blocked by `HasClosureUnsafeTupleElements` gate)
- Optional<any Protocol> in closures: deferred (`MarshalFromSwift` limitation)

## Key References

- `src/docs/roadmap.md` — Single consolidated roadmap (remaining work to ship + post-ship improvements)
