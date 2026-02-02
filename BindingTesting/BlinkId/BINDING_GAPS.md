# BlinkID Binding Gaps

This document tracks binding issues discovered while setting up BlinkID as a test case for the swift-bindings project.

**Last Updated**: February 2026 (Phase 33 - Generic Type Internal References Fix)

## Summary

BlinkID binding generation revealed several gaps in the binding generator. **After Phase 33 fixes, the library now compiles with 0 errors.**

| Metric | Initial | After Phase 30 | After Phase 33 |
|--------|---------|----------------|----------------|
| Compilation errors | 66 | 6 | **0** |
| Operator errors (CS0563/CS0305) | 48 | 0 | 0 |
| Generic internal refs (CS0305) | - | 6 | **0** |
| DllImport in generics (CS7042) | 18 | 0 (helper class) | 0 |

## Generator Issues Fixed During Setup

### 1. TBD Parser: weak-symbols Support

**File:** `src/Swift.Bindings/src/Demangler/TbdParser/Parsing/YamlLikeTbdFormatParser.cs`

**Issue:** BlinkID's TBD file contains `weak-symbols` entries that weren't handled by the parser, causing a crash.

**Fix:** Added `weak-symbols` case to the exports parser to consume the multi-line array and continue parsing.

### 2. TBD Parser: Top-Level Field Handling

**File:** `src/Swift.Bindings/src/Demangler/TbdParser/Parsing/YamlLikeTbdFormatParser.cs`

**Issue:** BlinkID's TBD file contains `flags`, `current-version`, and `compatibility-version` fields that caused warnings.

**Fix:** Added these as recognized optional fields (ignored but not warned about).

### 3. Demangler: Exception Resilience

**File:** `src/Swift.Bindings/src/Demangler/DemanglingResults.cs`

**Issue:** BlinkID contains C++ interop symbols (from the `Cxx` module) that the Swift demangler cannot handle, causing the entire binding generation to crash.

**Fix:** Wrapped demangler calls in try-catch to gracefully skip symbols that can't be demangled, logging warnings instead of crashing.

### 4. Enum Handler: Quote Escaping in Error Messages

**File:** `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumHandler.cs`

**Issue:** String enum raw values like `"none"` were being embedded in error messages without escaping the quotes, causing 2596 compilation errors.

**Fix:** Escape quotes in `rawValueLiteral` when generating error message strings.

### 5. Method Handler: IntPtrSelfClass Invalid Type

**File:** `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs`

**Issue:** Async instance methods on classes were using `IntPtrSelfClass` as a marker type in P/Invoke signatures, but this isn't a valid C# type.

**Fix:** Use `IntPtr` for the P/Invoke type and distinguish class vs struct at call site using different parameter names (`_selfClass` vs `_self`).

## Issues Fixed in Later Phases

### 6. Operator Generation for Generic Types (CS0563, CS0305) - Phase 30

**Status:** ✅ **FIXED**

Operators on generic types like `DateResult<T0>` now correctly use `DateResult<T0>` instead of `DateResult` in parameter types.

### 7. Generic Type DllImport (CS7042) - Phase 31

**Status:** ✅ **FIXED**

P/Invoke declarations for generic types are now emitted to non-generic helper classes (`{TypeName}_PInvoke`).

### 8. Generic Type Internal References (CS0305) - Phase 33

**Status:** ✅ **FIXED**

Internal type references (`SwiftObjectHelper<>`, `SwiftSafeHandle<>`, `_payloadSize`, `_payload`) now use `typeNameWithGenerics`. P/Invoke call sites now use helper class prefix and pass metadata parameters.

## Remaining Warnings (Expected)

### C++ Interop Symbols

**Issue:** BlinkID uses Swift/C++ interoperability (`Cxx` module). The demangler can't parse these symbols.

**Impact:** C++ interop symbols are skipped (gracefully), but any functionality that depends on C++ bridging won't work.

### AnyType in Generics

**Issue:** Many properties use generic types with `AnyType` (existential types), like `DateResult<Swift.AnyType>` or `SwiftArray<VehicleClassInfo<Swift.AnyType>>`.

**Impact:** Properties skipped with warnings:
- `effectiveDate`, `expiryDate` (DateResult<AnyType>)
- `vehicleClass`, `licenceType`, `restrictions`, `endorsements`, `conditions` (AnyType)
- `vehicleClassesInfo` (SwiftArray<VehicleClassInfo<AnyType>>)

### Concurrency Types

**Issue:** Properties using `_Concurrency.UnownedSerialExecutor` type are not supported.

**Impact:** `unownedExecutor` properties skipped on actor types.

## Statistics

| Metric | Value |
|--------|-------|
| ABI JSON lines | 75,817 |
| Generated C# lines | ~49,000 |
| Generated C# file size | ~1.9 MB |
| Generated Swift wrapper size | ~18 KB |
| Compilation errors | **0** ✅ |
| Demangler warnings | ~50 (C++ interop symbols) |
| Property skip warnings | ~20 (AnyType in generics) |

## Test Status

- Binding generation: ✅ Completes without crash
- C# binding compilation: ✅ **0 errors**
- Swift wrapper compilation: 🔲 Not yet tested
- Test app execution: 🔲 Not yet tested

## Types That Work

The following types compile and should be usable:

- `RequestTimeout` - Struct with static property
- `Country` - Enum with string raw values
- `Region` - Enum with string raw values
- `ProcessingStatus` - Enum
- `BlinkIDSdk` - Main SDK class
- `DateResult<T0>` - Generic date result struct
- `VehicleClassInfo<T0>` - Generic vehicle info struct
- `DriverLicenseDetailedInfo<T0>` - Generic license info struct
- Various result/configuration structs

## Validation Commands

```bash
# Regenerate bindings
cd BindingTesting/BlinkId
./regenerate-bindings.sh

# Build test app (includes bindings)
dotnet build BlinkIdTestApp/BlinkIdTestApp.csproj

# Count errors (should be 0)
dotnet build BlinkIdTestApp/BlinkIdTestApp.csproj 2>&1 | grep -c "error CS"
```
