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
| `nuke validate` | ~2 min | Compile gate across real-world libs. Flags: `--tier N`, `--filter X` |
| `nuke fetch` | — | Download xcframeworks (first time only) |
| `nuke binding-tests` | ~2 min | Full pipeline: rebuild xcframework + regenerate bindings + Simulator (Mono JIT). `--strict` to fail on non-zero generator exit |
| `nuke runtime-tests-simulator` | ~2 min | Simulator only (Mono JIT). `--skip-regen` (~17s), `--skip-build` (~5s), `--class-filter NAME` |
| `nuke runtime-tests-device` | ~2 min | Physical iOS device (NativeAOT) |
| `nuke pack --version X.Y.Z` | fast | Build all 3 NuGet packages → `/tmp/swift-nuget/` |

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

## Working Guidelines

- **No shortcuts.** Prefer the correct long-term solution over a patch that papers over the real issue — root-cause fixes, not symptom suppression, not "skip the failing test", not weakening an assertion to make it green. If you're unsure whether a fix addresses the root cause or whether a short-term workaround is acceptable, ask the user before proceeding.
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

`runtime-tests-simulator` is the default. Also run `runtime-tests-device` when changes touch calling conventions, struct marshalling, or P/Invoke signatures (Mono and NativeAOT have different bugs), and after fixing any NativeAOT-skipped test.

### Final validation gates (run only what the change warrants)

| What changed | `nuke test` | `nuke validate` | `binding-tests` / `runtime-tests-simulator` |
|---|---|---|---|
| Generator / emitter / parser | Yes | Yes | Yes (`binding-tests`). Also device if calling conventions or marshalling changed. |
| Runtime (`Swift.Runtime`) | Yes | Only if marshalling changed | Yes (`runtime-tests-simulator --skip-regen`). Device if marshalling changed. |
| Test infrastructure only | No | No | Just the target touched |
| Docs / research / external repos | No | No | No |

**Zero-regression policy**: `.validation-baseline.json` (`cs_compile` + `swift_compile`), BindingTests pass count, and unit test pass count must all be ≥ baseline before committing. No "will fix later" — applies to every commit.

## Known Issues

- **ALL runtime crashes are OUR BUGS until proven otherwise.** 102/102 tests once labeled `[MonoJitCrash]` turned out to be generator/runtime bugs in our code. The authoritative list of confirmed upstream .NET bugs lives in memory at `feedback_mono_jit_blame.md` — anything not on that list is ours. Before blaming the runtime, verify the generated C# P/Invoke matches the Swift `@_cdecl` wrapper: calling convention (`CallConvCdecl` vs `CallConvSwift`), parameter count, parameter types, library name, entry point symbol.
- `DllImportResolver` conflict: `[ModuleInitializer]` + consuming app both call `SetDllImportResolver` → `InvalidOperationException`. `Swift.Runtime/.../SwiftFrameworkResolver.cs` wraps in try-catch.
- Generator open bugs and blocked items: see `src/docs/roadmap.md`.
- Consumer-facing limitations: [wiki Known Limitations](https://github.com/justinwojo/swift-dotnet-bindings/wiki/Known-Limitations).

## Key References

- `src/docs/roadmap.md` — remaining work to ship + post-ship improvements (single source of truth)
