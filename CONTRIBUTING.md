# Contributing to Swift Bindings

Thanks for your interest in contributing! This project is under active development and evolving rapidly.

## How to Contribute

### Issues First

The most impactful way to contribute right now is through **issue reports**:

- **Binding errors** — the generator produces C# that doesn't compile for your library
- **Runtime failures** — generated bindings crash or behave incorrectly at runtime
- **Feature requests** — Swift patterns or workflows the generator doesn't handle
- **Documentation gaps** — missing or unclear information in the [wiki](https://github.com/justinwojo/swift-dotnet-bindings/wiki)

When filing a binding error, please include:

1. **Generator logs** — run with `-v 2` for verbose output
2. **The binding report** — `binding-report.json` from the output directory
3. **The xcframework** (if possible) — or at minimum the library name and version so we can reproduce

### Pull Requests

PRs are welcome, but **please open an issue first** to discuss the change — especially for anything beyond a trivial fix. The generator internals are changing frequently, and coordinating upfront avoids wasted effort on both sides.

## Development Setup

### Prerequisites

- macOS (Apple Silicon recommended)
- [Xcode 26](https://developer.apple.com/xcode/) or later
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) with the iOS workload (`dotnet workload install ios`)

### Building and Testing

```bash
# Build everything
./build.sh

# Run unit tests
./run-tests.sh

# Run library validation (requires fetching libraries first)
scripts/fetch-libraries.sh          # First time only (~30-60 min)
./validate-libraries.sh             # Full validation
./validate-libraries.sh --filter Nuke  # Single library
```

### Project Structure

| Directory | Description |
|-----------|-------------|
| `src/Swift.Bindings/src/` | Generator: Parser, TypeDatabase, Marshaler, Emitter |
| `src/Swift.Runtime/src/Swift/` | Runtime library (SwiftString, SwiftArray, SafeHandle, ARC) |
| `src/Swift.Bindings.Sdk/` | MSBuild SDK package |
| `src/Swift.Bindings.Templates/` | `dotnet new swift-binding` project template |
| `BindingTests/` | Integration test library + iOS Simulator runtime tests |
| `src/Swift.Bindings/tests/` | Unit tests |

### Generator CLI

```bash
# Generate bindings from an xcframework
dotnet run --project src/Swift.Bindings/src -- \
  --xcframework /path/to/Library.xcframework \
  -o /tmp/output/

# Verify generated output compiles
cd /tmp/output && dotnet build Library.Swift.iOS.csproj -p:EnableDefaultCompileItems=false
```

## PR Guidelines

- **All changes must have tests.** Unit tests at minimum; integration tests for new Swift patterns.
- **Run `./run-tests.sh` before submitting.** All 7,000+ unit tests must pass.
- **Run `./validate-libraries.sh`** if your change affects code generation. No regressions in the validation baseline.
- **Run `cd BindingTests && ./build-and-test.sh`** if your change affects code generation or the runtime. This rebuilds bindings from a comprehensive Swift test library and runs 700+ runtime tests on iOS Simulator — the primary end-to-end regression gate.
- **Keep PRs focused.** One logical change per PR. Don't bundle unrelated fixes.
- **Don't refactor surrounding code.** Fix/add what's needed, nothing more.

## AI Driven Development

This project is developed primarily with AI tooling — [Claude Code](https://claude.ai/claude-code) (Claude Opus) for implementation and [Codex](https://openai.com/codex) for code review. The repository is configured to support this workflow:

- **`CLAUDE.md`** — Project context, build commands, architecture constraints, and working guidelines for Claude Code sessions
- **`AGENTS.md`** — Equivalent context file for OpenAI Codex

The typical workflow for this repo is: plan and implement with Claude Code (Opus), then review plans and code with Codex. If you're comfortable working with AI coding tools, the repo is set up to be productive out of the box — point Claude Code or Codex at an issue and the context files provide the project knowledge needed to make meaningful contributions. The 7,000+ unit tests, 700+ runtime tests on iOS Simulator, and 89-target validation suite serve as a strong safety net for AI-generated changes.

Human review remains the final gate on all changes.

## Architecture Overview

The generator pipeline flows: **ABI JSON** → Parser → TypeDatabase → Marshaler → Emitter → **C# source + Swift wrapper**

For architectural details, see the [Architecture](https://github.com/justinwojo/swift-dotnet-bindings/wiki/Architecture) wiki page.

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).
