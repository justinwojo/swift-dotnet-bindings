# Phase 62: ArraySlice Parameter Normalization

## Summary

Added ArraySlice parameter normalization via Swift wrapper generation. Methods with `ArraySlice<T>` parameters (which have no TypeDatabase registration and resolve to `AnyType`) are recovered by emitting a Swift wrapper that accepts `Array<T>` and converts to `ArraySlice<T>` at the call site.

## Problem

`ArraySlice<T>` and `Array<T>` have different Swift runtime memory layouts. Registering ArraySlice as `SwiftArray` in the TypeDatabase would compile but crash at runtime. Without normalization, any method taking `ArraySlice<T>` is skipped with `UnsupportedSignature`.

In CryptoSwift, 34 of 42 skipped members were blocked solely by ArraySlice parameters.

## Solution

### ArraySliceNormalizationEmitter

New static emitter class that:
1. Detects ArraySlice in method parameter signatures
2. Applies scope guards (skips accessors, constructors, mutating structs, generics, inout ArraySlice, closures/tuples/optionals containing ArraySlice)
3. Creates a normalized `MethodDecl` clone with `Swift.ArraySlice` replaced by `Swift.Array`
4. Checks the normalized signature is fully marshallable (catches secondary blockers)
5. Emits a Swift wrapper function with `@_silgen_name` and `ArraySlice()` conversion
6. Delegates C# emission to the normal `WrapperEmitter` + `PInvokeEmitter` pipeline

### Swift Wrapper Patterns

**Type methods** (extension):
```swift
extension CryptoSwift.AES {
    @_silgen_name("SBW_AES_encrypt_A1B2C3D4")
    public func _sbw_encrypt_A1B2C3D4(_ block: Swift.Array<Swift.UInt8>) throws -> Swift.Array<Swift.UInt8>? {
        return try self.encrypt(block: Swift.ArraySlice(block))
    }
}
```

**Free functions** (standalone):
```swift
@_silgen_name("SBW_Global_sumArraySlice_9A8BB3E2")
public func _sbw_sumArraySlice_9A8BB3E2(_ arg0: Array<Int32>) -> Int32 {
    return SwiftBindingsTestLib.sumArraySlice(Swift.ArraySlice(arg0))
}
```

### Model Additions

- `MethodDecl.IsMutating` — parsed from `funcSelfKind` in ABI JSON; enables scope guard for mutating value type methods
- `MethodDecl.UsesWrapperLibrary` — routes P/Invoke to wrapper library instead of module library

### Symbol Naming

`SBW_{TypeName}_{MethodName}_{Hash8}` where Hash8 is FNV-1a 32-bit of the original mangled name (deterministic across processes, unlike `string.GetHashCode()`).

## Results

### CryptoSwift
- Member coverage: 61.3% -> 65.1% (427/656)
- 21 of 34 ArraySlice methods recovered
- 10 methods with `CipherModeWorker` secondary blocker remain skipped
- 2 mutating struct methods (`ChaChaEncryptor.update`, `ChaChaDecryptor.update`) correctly excluded
- 3 methods with `inout Array<UInt64>` secondary blocker correctly excluded

### TestFramework
- 4 new features: `array_slice_parameter`, `array_slice_multiple_params`, `array_slice_class_method`, `array_slice_throwing`
- Coverage: 57 -> 61 passing features, 0 degraded

### Unit Tests
- 31 ArraySlice normalization tests (detection, normalization, emission, scope guards, hashing, free functions, binding report)
- 7 parser runtime tests (funcSelfKind through full ParseModule pipeline)
- Total: 1,500 unit tests passing

## Files

| File | Change |
|------|--------|
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ArraySliceNormalizationEmitter.cs` | **New** |
| `src/Swift.Bindings/src/Model/TypeDecl/MethodDecl.cs` | Added `IsMutating`, `UsesWrapperLibrary` |
| `src/Swift.Bindings/src/Parser/SwiftABIParser.cs` | Parse `funcSelfKind` into `IsMutating` |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` | Integration hook |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PInvokeEmitter.cs` | Extended `needsWrapperLib` |
| `tests/UnitTests/EmitterTests/ArraySliceNormalizationEmitterTests.cs` | **New** — 31 tests |
| `tests/UnitTests/ParserTests/SwiftABIParserRuntimeTests.cs` | Added 3 funcSelfKind tests |
| `TestFramework/Sources/SwiftBindingsTestLib/Collections/ArraySliceOperations.swift` | **New** — test source |
| `TestFramework/generate-coverage-report.sh` | Added 4 ArraySlice features |
