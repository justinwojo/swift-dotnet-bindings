# Contributing to Swift Bindings

Thanks for your interest in contributing! This project generates C# bindings from compiled Swift and Objective-C frameworks for .NET 10.0 on Apple platforms. It's under active development and evolving rapidly.

## Architecture Overview

The generator transforms compiled Swift libraries into idiomatic C# bindings. Here's how the pipeline works:

```
xcframework (dylib + ABI JSON + TBD + swiftinterface)
    |
    v
  Parser ──────> TypeDatabase ──────> Marshaler ──────> Emitter
  (reads ABI      (central type       (decides C#        (generates C# source,
   JSON, TBD,      registry for        mapping per        Swift @_cdecl wrappers,
   swiftinterface) all Swift types)    Swift type)        consumer .targets)
```

**Parser** reads the Swift ABI JSON (type metadata, function signatures, conformances), TBD files (exported symbols), and swiftinterface files to populate the TypeDatabase.

**TypeDatabase** is the central type registry. Every Swift type discovered by the parser is registered here with its full metadata — kind, generic parameters, conformances, and relationships.

**Marshaler** decides how each Swift type maps to C#: which calling convention to use (CallConvCdecl for @_cdecl wrappers and native ARM64 thunks, CallConvSwift for the small number of direct calls), whether a type needs a Swift wrapper function, and how parameters/returns are marshalled across the interop boundary.

**Emitter** generates the final output: C# source files with P/Invoke declarations and managed wrappers, Swift @_cdecl wrapper functions (for non-blittable types, closures, and async), native ARM64 assembly thunks (for synchronous blittable methods), and a consumer `.targets` file for MSBuild integration.

**Runtime** (`Swift.Runtime`) provides the .NET-side infrastructure: `SwiftString` and `SwiftArray` for collection interop, `SafeHandle`-based classes for deterministic ARC bridging, and module initializers for dynamic library resolution.

**MSBuild SDK** (`SwiftBindings.Sdk`) wraps the entire generator workflow into a standard `dotnet build && dotnet pack` experience, so binding authors don't need to invoke the CLI directly.

### Key Directories

| Directory | What's There |
|-----------|-------------|
| `src/Swift.Bindings/src/` | Generator: Parser, TypeDatabase, Marshaler, Emitter |
| `src/Swift.Bindings/tests/` | Generator unit tests (~9,000 tests) |
| `src/Swift.Runtime/src/Swift/` | Runtime library (SwiftString, SwiftArray, SafeHandle, ARC) |
| `src/Swift.Bindings.Sdk/` | MSBuild SDK package (`SwiftBindings.Sdk`) |
| `src/Swift.Bindings.Templates/` | `dotnet new swift-binding` project template |
| `BindingTests/` | Integration test library + iOS Simulator runtime tests (~850 tests) |
| `validation-libraries.json` | Library validation manifest (90 targets across 46 libraries) |
| `build/` | Nuke Build targets (C#): compile, test, validate, pack |
| `scripts/` | Coverage report, CI orchestrator scripts |
| `src/docs/` | Internal design docs, status, known issues |

## Getting Started

### Prerequisites

- macOS (Apple Silicon recommended)
- [Xcode 26](https://developer.apple.com/xcode/) or later
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) with the iOS workload:
  ```bash
  dotnet workload install ios
  ```

### Building

```bash
nuke compile
```

### Running Tests

```bash
# Unit tests (~9,000 tests, ~2 min)
nuke test

# Library validation (requires fetching libraries first)
nuke fetch                     # First time only (~30-60 min)
nuke validate                  # Full validation (90 targets)
nuke validate --filter Nuke    # Single library

# End-to-end integration tests (~5 min)
nuke binding-tests
```

### Generator CLI

```bash
# Generate bindings from an xcframework
dotnet run --project src/Swift.Bindings/src -- \
  --xcframework /path/to/Library.xcframework \
  -o /tmp/output/

# Verify generated output compiles
cd /tmp/output && dotnet build Library.Swift.iOS.csproj -p:EnableDefaultCompileItems=false
```

Run `dotnet run --project src/Swift.Bindings/src -- --help` for all CLI options.

## Development Workflow

### Testing Generator Changes

Follow this progression from fast to thorough:

1. **Unit tests** (`nuke test`) — fast feedback on parser/marshaler/emitter logic
2. **Library validation** (`nuke validate`) — compile gate across 90 real-world library targets
3. **BindingTests** (`nuke binding-tests`) — end-to-end: Swift source to generated binding to runtime execution on iOS Simulator

Unit tests alone can't catch ABI mismatches, calling convention bugs, or marshalling crashes that only surface when running real bindings. Always run the full progression for generator/emitter changes.

### Adding a New Validation Library

1. Add an entry to `validation-libraries.json` (repo URL, version, mode, tier)
2. Fetch: `nuke fetch --filter NewLib`
3. Validate: `nuke validate --filter NewLib`
4. Run full validation (`nuke validate`) to update `.validation-baseline.json`

### Where Tests Go

Tests are organized **by domain** (closures, enums, structs, protocols, etc.), not by milestone, session, or SDK version. Place new tests in the appropriate domain file. For example, closure-related tests go in closure test files.

### Test Quality

- **Assert behavior, not implementation.** Prefer `"output contains CallConvCdecl"` over exact string matching of generated code. This prevents tests from breaking when emitter internals change while behavior stays correct.
- **Bug-first testing.** When writing tests for untested code, read and understand the code first. Don't assume existing behavior is correct — if something looks wrong, write a test that exposes the correct behavior, not one that enshrines the bug.
- Use `[Theory]`/`[InlineData]` when multiple tests differ only in input values.

## Filing Issues

The most impactful way to contribute is through **issue reports**:

- **Binding errors** — the generator produces C# that doesn't compile for your library
- **Runtime failures** — generated bindings crash or behave incorrectly at runtime
- **Feature requests** — Swift patterns or workflows the generator doesn't handle
- **Documentation gaps** — missing or unclear information in the [wiki](https://github.com/justinwojo/swift-dotnet-bindings/wiki)

When filing a binding error, please include:

1. **Generator logs** — run with `-v 2` for verbose output
2. **The binding report** — `binding-report.json` from the output directory
3. **The xcframework** (if possible) — or at minimum the library name and version so we can reproduce

## Pull Requests

PRs are welcome, but **please open an issue first** to discuss the change — especially for anything beyond a trivial fix. The generator internals change frequently, and coordinating upfront avoids wasted effort on both sides. PRs should target the `main` branch.

### PR Expectations

- **All changes must have tests.** Unit tests at minimum; integration tests for new Swift patterns.
- **Run `nuke test` before submitting.** All unit tests must pass.
- **Run `nuke validate`** if your change affects code generation. No regressions in the validation baseline.
- **Run `nuke binding-tests`** if your change affects code generation or the runtime.
- **Keep PRs focused.** One logical change per PR. Don't bundle unrelated fixes.
- **Don't refactor surrounding code.** Fix/add what's needed, nothing more.

## Code Conventions

For detailed development guidelines, see [`CLAUDE.md`](CLAUDE.md) — it's the authoritative reference for project conventions and is kept current. Key points:

- **Never use `git stash`** — hooks detect reverted files and stash pop can discard changes silently.
- **Test files by domain** — never create test files named after milestones, sessions, or versions.
- **Bug-first testing** — don't assume existing behavior is correct when writing tests for untested code.
- **Verify generated output compiles** — after code gen changes, always build the output to confirm correctness.

## AI-Driven Development

This project is developed primarily with AI tooling — [Claude Code](https://claude.ai/claude-code) (Claude Opus) for implementation and [Codex](https://openai.com/codex) for code review. The repository is configured to support this workflow:

- **`CLAUDE.md`** — Project context, build commands, architecture constraints, and working guidelines for Claude Code sessions
- **`AGENTS.md`** — Equivalent context file for OpenAI Codex

If you're comfortable working with AI coding tools, the repo is set up to be productive out of the box — point Claude Code or Codex at an issue and the context files provide the project knowledge needed to make meaningful contributions. The unit tests, runtime tests, and validation suite serve as a strong safety net for AI-generated changes.

Human review remains the final gate on all changes.

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).
