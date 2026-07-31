# Design Documents

Technical design documents covering Swift/C# interop internals. Docs live here only if they accurately describe the *current* implementation — each was verified against the shipped generator and runtime (most recently 2026-07). Stale fork-era design docs were deleted; recover them from git history or the upstream `dotnet/runtimelab` branch if ever needed.

These docs are useful for contributors who need to understand the internals of the generator. For user-facing documentation, see the [project wiki](https://github.com/justinwojo/swift-dotnet-bindings/wiki).

## Contents

### Binding Design
- [binding-resilience-design.md](binding-resilience-design.md) — Resilience pipeline: regenerate-from-plan, recovery ladder, prediction/verification division of labor (as-built; §8 is the wave-1/2 outcome record)
- [binding-structs.md](binding-structs.md) — Three-way struct model: frozen blittable C# struct, frozen+memory-managed class-with-buffer, non-frozen class-with-opaque-payload
- [binding-closures.md](binding-closures.md) — Closure callback patterns, the `@_cdecl` wrapper architecture, and delegate projection (`Action`/`Func` with `SwiftResult`/`Task` wrappers)
- [unsafe-mutable-raw-buffer-pointer.md](unsafe-mutable-raw-buffer-pointer.md) — `UnsafeMutableRawBufferPointer` ↔ `Span<byte>` projection
- [binding-variables.md](binding-variables.md) — Property/variable binding: accessor-method emission, wrapper strategy selection, async getters as methods; globals and `willSet`/`didSet` not emitted
- [binding-typedatabase.md](binding-typedatabase.md) — Type database: flat XML layout, registration lifecycle, resolution cascade, ownership/supplement resolvers
- [binding-value-witness-table.md](binding-value-witness-table.md) — Value witness table layout (incl. enum witnesses) and the as-built `Swift.Runtime.ValueWitnessTable` mirror
- [async-non-frozen-types.md](async-non-frozen-types.md) — Async members with non-frozen types: dual-copy buffer model, `AsyncResultPlanner` ownership algebra
- [objc-binding-consumption.md](objc-binding-consumption.md) — Pure-ObjC packages carry their native in the classic Microsoft.iOS binding sidecar (`lib/<tfm>/<Assembly>.resources[.zip]`), not `runtimes/` + a consumer `.targets`; guard-coverage boundary SWIFTBIND038 vs SWIFTBIND074

### Runtime
- [memory-management.md](memory-management.md) — Native memory ownership at the Swift–C# boundary
- [reverse-dispatch-lifetime.md](reverse-dispatch-lifetime.md) — "Design B2" lifetime/identity model for reverse dispatch (C#-implemented protocols carried across the ABI as `EveryProtocol`); proxy/impl rooting, R0 release, per-module metadata

### Internals
- [demangling.md](demangling.md) — Managed Swift 5 demangler port: TBD ingestion, node-tree reduction, raw-tree probes, SWIFTBIND058 fail-loud diagnostics
- [demangling-replacement-spike.md](demangling-replacement-spike.md) — Demangler-replacement spike findings (NO-GO)
- [retrieving-symbols-outside-abi-json.md](retrieving-symbols-outside-abi-json.md) — Symbols needed beyond ABI JSON (TBD parsing)

### Validation & Apple Frameworks
- [abi-coverage-grid.md](abi-coverage-grid.md) — ABI coverage grid (living artifact, referenced from roadmap)
- [apple-framework-portfolio.md](apple-framework-portfolio.md) — Apple framework binding portfolio
- [apple-framework-binding-strategy.md](apple-framework-binding-strategy.md) — Apple framework binding strategy
- [apple-swift-types-architecture.md](apple-swift-types-architecture.md) — Apple Swift types architecture
