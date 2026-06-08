# Contributing to Swift Bindings

Thanks for your interest! This project generates C# bindings from compiled Swift libraries (`.dylib` + ABI JSON) for .NET 10.0 on Apple platforms. It's under active development.

## AI-driven development

This repo is **designed to be worked on with AI coding tools** — primarily [Claude Code](https://claude.ai/claude-code) for implementation and [Codex](https://github.com/openai/codex) for code review, but either can be used. Two context files do most of the heavy lifting:

- **`CLAUDE.md`** — project conventions, build commands, working guidelines for Claude Code
- **`AGENTS.md`** — equivalent file for Codex and other agent tools

If you're comfortable with these tools, point one at an issue and the context files will give it the project knowledge it needs to make a meaningful contribution. The unit, runtime, and validation suites are a strong safety net for AI-generated changes. Human review is still the final gate.

## Prerequisites

- macOS (Apple Silicon recommended)
- [Xcode 26.3](https://developer.apple.com/xcode/) or later
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- `dotnet workload restore` (installs the ios/macos/maccatalyst/tvos workloads)

## Build & test

Always go through `nuke <target>` rather than raw `dotnet` / `swift` commands. The everyday targets:

| Target | Purpose |
|---|---|
| `nuke compile` | Build the project |
| `nuke test` | Unit tests (generator + runtime), ~2 min |
| `nuke binding-tests` | End-to-end: regenerate bindings, build, run on iOS Simulator |
| `nuke validate` | Compile gate across 65 real-world libraries, ~2 min |
| `nuke fetch` | Download library xcframeworks (first time only) |
| `nuke pack --version X.Y.Z --apple-version A.B.C` | Build all NuGet packages |

`nuke binding-tests` composes platform flags — `--sim` (default, Mono JIT), `--device` (physical iPhone, NativeAOT), `--macos`, `--catalyst`, `--tvos` — and supports `--compile-only`, `--skip-regen`, `--skip-build`, `--class-filter NAME`. Full flag table is in `CLAUDE.md`.

## Generator CLI

```bash
dotnet run --project src/Swift.Bindings/src -- \
  --xcframework /path/to/Library.xcframework \
  -o /tmp/output/

cd /tmp/output && dotnet build Library.Swift.iOS.csproj
```

Do **not** pass `-p:EnableDefaultCompileItems=false` on the command line — it propagates as a global property and breaks Swift.Runtime. The generated csproj already sets it locally. Run with `--help` for all options.

## Adding a validation library

1. Add an entry to `build/validation-libraries.json`. Schema varies by `mode` — `apple-framework`, `source`, `binary`, `manual`. Copy the shape of an existing entry of the same mode.
2. `nuke fetch --filter NewLib`
3. `nuke validate --filter NewLib`
4. Run full `nuke validate` to refresh `build/baselines/validation-baseline.json`.

For SPM-only third-party libraries, use [`spm-to-xcframework`](https://github.com/justinwojo/spm-to-xcframework) rather than writing custom build scripts.

## Filing issues

Issue reports are the most impactful way to contribute. For a binding error, include:

- Generator output (run with `-v 2`)
- The library name and version (or the xcframework itself, if shareable)

The [wiki](https://github.com/justinwojo/swift-dotnet-bindings/wiki) covers consumer-facing limitations and known issues.

## Pull requests

Open an issue first for anything beyond a trivial fix — generator internals change frequently and a quick discussion avoids wasted effort on both sides.

- Every change ships with tests at the right layer (generator unit / runtime unit / end-to-end BindingTests). `CLAUDE.md` describes which layer matches what kind of change.
- `nuke test` and the relevant `nuke binding-tests` / `nuke validate` runs must pass before submitting; no regressions in `build/baselines/validation-baseline.json`.
- Keep PRs focused — one logical change, no incidental refactors.

## License

By contributing, you agree your contributions will be licensed under the [MIT License](../LICENSE).
