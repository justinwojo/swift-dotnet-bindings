# Contributing

Contributions are welcome. Before opening a pull request, please open an
issue to discuss non-trivial changes so we can align on scope and
direction.

## Derivation pipeline (licensing context)

The `SwiftBindings` generator reads Apple-provided Swift ABI JSON emitted
by the Apple-supplied Swift compiler, walks `.swiftinterface` module
interfaces for declaration surface, and resolves symbols from Apple SDK
`xcframework` dylibs via `dlsym` at runtime. The published NuGet
packages contain only the managed (C#) projections this pipeline
produces — shape, layout, and ABI entry-point metadata describing Apple
Swift types — and never embed, copy, or redistribute Apple SDK headers,
source code, compiled binaries, or documentation. Contributions must
preserve this boundary: any change that would ship Apple-owned content
as part of a `SwiftBindings.*` package is out of scope and will be
rejected.

See [`NOTICE`](src/legal/NOTICE.md) and
[`RATIONALE`](src/legal/RATIONALE.md) for the full legal rationale.

## Development workflow

Build and test commands live in `CLAUDE.md` (project-level guide) and in
the wiki. In short: use `nuke <target>` rather than raw `dotnet`
commands. Running `nuke test` before submitting a PR is the minimum bar;
changes to the generator, emitter, or runtime should additionally run
`nuke validate` and `nuke binding-tests`.

## License

By contributing, you agree that your contributions will be licensed
under the repository's MIT License (see `LICENSE`).
