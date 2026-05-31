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
| `nuke binding-tests [flags]` | varies | End-to-end BindingTests gate — see flag table below |
| `nuke pack --version X.Y.Z` | fast | Build all 3 NuGet packages → `/tmp/swift-nuget/` |

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
| `--strict` | Fail on non-zero generator exit (implied by `--compile-only`'s default) |
| `--permissive` | Opt out of `--compile-only` fail-closed gates. Local-exploration only. |
| `--skip-regen` (~17s) | Skip binding regeneration; assumes bindings are current |
| `--skip-build` (~5s) | Skip app build; just install + run |
| `--class-filter NAME` | Run only one test class (Simulator path) |

The compile gate (`--compile-only`) and the runtime gates are complementary: the first asks "does it compile?", the second asks "does it pass?". Generator/emitter changes want both — run `nuke binding-tests --compile-only` then `nuke binding-tests --skip-regen`. For runtime-only C# changes, `nuke binding-tests --skip-regen` alone is enough.

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

`nuke binding-tests` (default sim) is the everyday runtime gate. Also run `nuke binding-tests --device` when changes touch calling conventions, struct marshalling, or P/Invoke signatures (Mono and NativeAOT have different bugs), and after fixing any NativeAOT-skipped test.

### Final validation gates (run only what the change warrants)

`nuke test` (unit tests) and `nuke binding-tests` (BindingTests) are the everyday signals — fast, targeted, and the layers where new coverage should land. **`nuke validate` is opt-in**, not part of the routine inner loop: it takes ~5 minutes and re-runs the full real-world library sweep. Only run it for (a) larger refactors / cross-cutting generator or emitter changes where category-wide regression is plausible, (b) pre-release regression sweeps, or (c) when you genuinely want the insight from a validation-libraries run. Don't burn ~5 minutes per change "just in case" — that is what's hurting velocity.

| What changed | `nuke test` | `nuke binding-tests` | `nuke validate` |
|---|---|---|---|
| Generator / emitter / parser | Yes | Yes (default sim run). Add `--device` if calling conventions or marshalling changed. | Optional — only for cross-cutting changes or pre-release sweeps |
| Runtime (`Swift.Runtime`) | Yes | Yes (`--skip-regen`). Add `--device` if marshalling changed. | Optional — only if marshalling changed *and* the change plausibly affects multiple libs |
| Test infrastructure only | No | Just the target touched | No |
| Docs / research / external repos | No | No | No |

**Zero-regression policy**: BindingTests pass count and unit test pass count must be ≥ baseline before committing — these are the per-commit gates. `.validation-baseline.json` (`cs_compile` + `swift_compile`) only needs to be ≥ baseline *when you actually run `nuke validate`*; if you didn't run validate this change, you don't need to defend against it. No "will fix later" for the gates that ran.

## Known Issues

- **ALL runtime crashes are OUR BUGS until proven otherwise.** 102/102 tests once labeled `[MonoJitCrash]` turned out to be generator/runtime bugs in our code. The authoritative list of confirmed upstream .NET bugs lives in memory at `feedback_mono_jit_blame.md` — anything not on that list is ours. Before blaming the runtime, verify the generated C# P/Invoke matches the Swift `@_cdecl` wrapper: calling convention (`CallConvCdecl` vs `CallConvSwift`), parameter count, parameter types, library name, entry point symbol.
- `DllImportResolver` conflict: `[ModuleInitializer]` + consuming app both call `SetDllImportResolver` → `InvalidOperationException`. `Swift.Runtime/.../SwiftFrameworkResolver.cs` wraps in try-catch.
- Generator open bugs and blocked items: see `src/docs/roadmap.md`.
- Consumer-facing limitations: [wiki Known Limitations](https://github.com/justinwojo/swift-dotnet-bindings/wiki/Known-Limitations).

## Key References

- `src/docs/roadmap.md` — higher-level prioritized themes, blocked items, and lower-priority ideas. **Not** a complete index of active work: we often work from dedicated docs (top-level `src/docs/*.md`, subsystem folders) that are not listed in roadmap at all. When picking next work or proposing direction, check both roadmap *and* recent top-level docs; ask the user when ambiguous rather than assuming roadmap is exhaustive.
