# Design Documents

Technical design documents covering Swift/C# interop internals. These were originally written as part of Microsoft's `dotnet/runtimelab` experimental branch and describe the foundational binding architecture.

These docs are useful for contributors who need to understand the internals of the generator. For user-facing documentation, see the [project wiki](https://github.com/justinwojo/swift-dotnet-bindings/wiki).

## Contents

### Binding Design
- [binding-overview.md](binding-overview.md) — Language parity, ABI, and idiomatic differences
- [binding-classes.md](binding-classes.md) — Class binding strategy (ARC, SafeHandle)
- [binding-structs.md](binding-structs.md) — Frozen vs non-frozen struct handling
- [binding-enums.md](binding-enums.md) — Enum categories and C# projection
- [binding-protocols.md](binding-protocols.md) — Protocol interfaces, proxies, witness tables
- [binding-generics.md](binding-generics.md) — Generic type projection
- [binding-pats.md](binding-pats.md) — Protocols with Associated Types
- [binding-closures.md](binding-closures.md) — Closure callback patterns
- [binding-tuples.md](binding-tuples.md) — Tuple marshalling
- [binding-functions.md](binding-functions.md) — Function classification
- [unsafe-mutable-raw-buffer-pointer.md](unsafe-mutable-raw-buffer-pointer.md) — `UnsafeMutableRawBufferPointer` ↔ `Span<byte>` projection
- [binding-properties.md](binding-properties.md) — Property getter/setter patterns
- [binding-variables.md](binding-variables.md) — Module-level globals
- [binding-typedatabase.md](binding-typedatabase.md) — Type database design
- [binding-value-witness-table.md](binding-value-witness-table.md) — Value witness table operations

### Runtime
- [memory-management.md](memory-management.md) — Native memory for projected value types
- [runtime-features-overview.md](runtime-features-overview.md) — Runtime feature summary
- [runtime-metadata.md](runtime-metadata.md) — Swift type metadata structure
- [runtime-nominal-type-descriptor.md](runtime-nominal-type-descriptor.md) — Nominal type descriptors
- [runtime-existential-containers.md](runtime-existential-containers.md) — Existential container layout

### Internals
- [swift-code-generation.md](swift-code-generation.md) — When and why Swift wrappers are generated
- [demangling.md](demangling.md) — Swift symbol name demangling
- [retrieving-symbols-outside-abi-json.md](retrieving-symbols-outside-abi-json.md) — Symbols needed beyond ABI JSON
- [vtable-alternative.md](vtable-alternative.md) — Simulated vtable design
- [async-non-frozen-types.md](async-non-frozen-types.md) — Async with non-frozen parameters
- [process-binding.md](process-binding.md) — Handler/factory pattern for code generation
