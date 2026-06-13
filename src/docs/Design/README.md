# Design Documents

Technical design documents covering Swift/C# interop internals. Docs live here only if they accurately describe the *current* implementation — each was verified against the shipped generator and runtime (2026-06). Stale fork-era design docs were deleted; recover them from git history or the upstream `dotnet/runtimelab` branch if ever needed.

These docs are useful for contributors who need to understand the internals of the generator. For user-facing documentation, see the [project wiki](https://github.com/justinwojo/swift-dotnet-bindings/wiki).

## Contents

### Binding Design
- [binding-structs.md](binding-structs.md) — Frozen vs non-frozen struct handling
- [binding-closures.md](binding-closures.md) — Closure callback patterns and the `@_cdecl` wrapper architecture
- [unsafe-mutable-raw-buffer-pointer.md](unsafe-mutable-raw-buffer-pointer.md) — `UnsafeMutableRawBufferPointer` ↔ `Span<byte>` projection
- [binding-variables.md](binding-variables.md) — Module-level globals and observer semantics
- [binding-typedatabase.md](binding-typedatabase.md) — Type database design
- [binding-value-witness-table.md](binding-value-witness-table.md) — Value witness table layout and access
- [async-non-frozen-types.md](async-non-frozen-types.md) — Async with non-frozen parameters

### Runtime
- [memory-management.md](memory-management.md) — Native memory ownership at the Swift–C# boundary

### Internals
- [demangling.md](demangling.md) — Swift symbol name demangling
- [demangling-replacement-spike.md](demangling-replacement-spike.md) — Demangler-replacement spike findings (NO-GO)
- [retrieving-symbols-outside-abi-json.md](retrieving-symbols-outside-abi-json.md) — Symbols needed beyond ABI JSON (TBD parsing)

### Validation & Apple Frameworks
- [abi-coverage-grid.md](abi-coverage-grid.md) — ABI coverage grid (living artifact, referenced from roadmap)
- [apple-framework-portfolio.md](apple-framework-portfolio.md) — Apple framework binding portfolio
- [apple-framework-binding-strategy.md](apple-framework-binding-strategy.md) — Apple framework binding strategy
- [apple-swift-types-architecture.md](apple-swift-types-architecture.md) — Apple Swift types architecture
