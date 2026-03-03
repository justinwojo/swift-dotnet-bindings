# Swift Runtime Metadata Feasibility — `swift_conformsToProtocol`

## Summary

Feature F7 proves that `swift_conformsToProtocol` is callable from C# at runtime via P/Invoke, enabling dynamic conformance checking without compile-time knowledge of every (type, protocol) pair.

## ABI Verification

**Source**: Swift runtime [`RuntimeFunctions.def`](https://github.com/swiftlang/swift/blob/main/include/swift/Runtime/RuntimeFunctions.def)

```
FUNCTION(ConformsToProtocol,
         Swift, swift_conformsToProtocol, C_CC, AlwaysAvailable,
         RETURNS(WitnessTablePtrTy),
         ARGS(TypeMetadataPtrTy, ProtocolDescriptorPtrTy),
         ATTRS(NoUnwind))
```

Key properties:
- **Calling convention**: `C_CC` (standard C calling convention) — safe for `CallingConvention.Cdecl` P/Invoke on both Mono and NativeAOT
- **Return type**: `WitnessTablePtr` — pointer-sized value, null for non-conformance
- **Arguments**: `(TypeMetadataPtr, ProtocolDescriptorPtr)` — both are single-pointer blittable structs already in our runtime
- **Attributes**: `NoUnwind` — will not throw through the call frame
- **Availability**: `AlwaysAvailable` — present in all Swift runtimes (ABI-stable since Swift 5)

## Symbol Resolution

Protocol descriptor symbols (`$s...Mp`) use the Swift 5 ABI-stable mangled name scheme. Verified on macOS 15.4, Swift 6.1:

| Symbol | Protocol | Verified |
|--------|----------|----------|
| `$sSHMp` | Hashable | Yes |
| `$sSQMp` | Equatable | Yes |
| `$sSLMp` | Comparable | Yes |
| `$sSTMp` | Sequence | Yes |
| `$sSlMp` | Collection | Yes |

On modern macOS, `libswiftCore.dylib` is in the shared dylib cache (not a standalone file on disk), but resolves correctly via `NativeLibrary.TryLoad` — the same mechanism used throughout the existing runtime (`KnownLibraries.SwiftCore`, `ProtocolConformanceDescriptor.LoadFromSymbol`, `TypeMetadata.KnownMetadata`).

## Design Rationale

### Two new types

1. **`ProtocolDescriptor`** — wraps `IntPtr` to a `$s...Mp` symbol. Distinct from `ProtocolConformanceDescriptor` (`$s...Mc`) which describes a specific type→protocol conformance. Follows the exact pattern of `ProtocolConformanceDescriptor.cs`.

2. **`SwiftConformance`** — static class providing:
   - `ConformsToProtocol(TypeMetadata, ProtocolDescriptor) → bool` — throws on invalid inputs (fail-fast)
   - `TryGetWitnessTable(TypeMetadata, ProtocolDescriptor, out ProtocolWitnessTable?) → bool` — returns false on invalid inputs (try-pattern)

### Why no caching

The Swift runtime internally caches conformance lookups in a concurrent hash table. Adding a managed cache would duplicate this without benefit.

### Why not SwiftCC

`swift_conformsToProtocol` uses C calling convention (`C_CC`), unlike some Swift runtime functions that use Swift calling convention. This makes it directly callable via standard `CallingConvention.Cdecl` P/Invoke without the `CallConvSwift` complexity that causes Mono JIT issues.

## Future Applications

- **F5 (string enum raw values)**: Could potentially call `swift_EnumCaseName` or witness table methods to get raw values at runtime, bypassing the ABI JSON limitation
- **Dynamic dispatch**: Runtime conformance checks enable scenarios like "cast to protocol if conforming" without static code generation
- **Type introspection**: Combined with `TypeMetadata` enumeration, enables runtime discovery of a type's protocol conformances

## Test Coverage

26 tests covering:
- Protocol descriptor loading (5 protocols + error cases + equality semantics)
- `ConformsToProtocol` (5 positive, 2 negative, 2 validation)
- `TryGetWitnessTable` (1 success, 1 non-conformance, 2 invalid input)
- Cross-validation against static `swift_getWitnessTable` path (3 tests)
