# BlinkID Binding Gaps

This document tracks binding issues discovered while setting up BlinkID as a test case for the swift-bindings project.

**Last Updated**: February 2026 (Phase 30 - Generic Operator Fix)

## Summary

BlinkID binding generation revealed several gaps in the binding generator. **After Phase 30 fixes, the library now compiles with 0 errors.**

| Metric | Before | After Phase 30 |
|--------|--------|----------------|
| Compilation errors | 66 | **0** |
| Operator errors (CS0563/CS0305) | 48 | **0** |
| DllImport warnings | 18 | 18 (skipped gracefully) |

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

## Outstanding Issues (Not Fixed)

### 1. Generic Type Classes (CS7042)

**Error Count:** 18 errors

**Error:** `CS7042: The DllImport attribute cannot be applied to a method that is generic or contained in a generic method or type.`

**Root Cause:** The binding generator emits generic classes like `VehicleClassInfo<T0>`, `DateResult<T0>`, and `DriverLicenseDetailedInfo<T0>` with P/Invoke methods inside them, which C# doesn't allow.

**Affected Types:**
- `VehicleClassInfo<T0>`
- `DateResult<T0>`
- `DriverLicenseDetailedInfo<T0>`

**Fix Needed:** Generic types should either be excluded from emission or the P/Invoke should be factored out into a non-generic helper class.

### 2. Operator Generation for Generic Types (CS0563, CS0305) ✅ FIXED

**Status:** ✅ **FIXED in Phase 30** (February 2026)

**Error Count (before fix):** 48 errors (12 + 36)

**Errors (before fix):**
- `CS0563: One of the parameters of a binary operator must be the containing type`
- `CS0305: Using the generic type 'X<T0>' requires 1 type arguments`

**Root Cause:** Operators on generic types like `DateResult<T0>` referenced the non-generic type name `DateResult` instead of `DateResult<T0>`.

**Fix Applied:** Updated `OperatorHandler.cs`, `EqualityMethodsWriter`, and `ClassEqualityMethodsWriter` to use `GenericTypeEmitter.GetTypeNameWithGenerics()` for all operator parameter types. See `src/docs/binding-gaps-consolidated.md` for details.

### 3. C++ Interop Symbols

**Issue:** BlinkID uses Swift/C++ interoperability (`Cxx` module). The demangler can't parse these symbols.

**Impact:** C++ interop symbols are skipped (gracefully after fix), but any functionality that depends on C++ bridging won't work.

### 4. AnyType in Generics

**Issue:** Many properties use generic types with `AnyType` (existential types), like `DateResult<Swift.AnyType>` or `SwiftArray<VehicleClassInfo<Swift.AnyType>>`.

**Impact:** Properties skipped with warnings:
- `effectiveDate`, `expiryDate` (DateResult<AnyType>)
- `vehicleClass`, `licenceType`, `restrictions`, `endorsements`, `conditions` (AnyType)
- `vehicleClassesInfo` (SwiftArray<VehicleClassInfo<AnyType>>)

### 5. Concurrency Types

**Issue:** Properties using `_Concurrency.UnownedSerialExecutor` type are not supported.

**Impact:** `unownedExecutor` properties skipped on actor types.

## Statistics

| Metric | Value |
|--------|-------|
| ABI JSON lines | 75,817 |
| Generated C# lines | 49,054 |
| Generated C# file size | 1.89 MB |
| Generated Swift wrapper size | 18 KB |
| Compilation errors (initial) | 2,612 |
| Compilation errors (after Phase 30) | **0** ✅ |
| Demangler warnings | ~50 (C++ interop symbols) |
| Property skip warnings | ~20 (AnyType in generics) |

## Test Status

- Binding generation: ✅ Completes without crash
- C# binding compilation: ✅ **0 errors** (after Phase 30)
- Swift wrapper compilation: 🔲 Not yet tested
- Test app execution: 🔲 Not yet tested

## Types That Likely Work

Based on the generated code and error patterns, the following types should compile after removing generic-related code:

- `RequestTimeout` - Struct with static property
- `Country` - Enum with string raw values
- `Region` - Enum with string raw values
- `ProcessingStatus` - Enum
- `BlinkIDSdk` - Main SDK class (non-generic async methods may have issues)
- Various result/configuration structs

## Recommendations

1. ~~**Short term:** Manually remove generic types~~ → **No longer needed** - bindings compile cleanly

2. **Medium term:** Fix the generic class P/Invoke issue (CS7042) - this would enable full use of generic types like `DateResult<T0>`, `VehicleClassInfo<T0>`

3. **Long term:** Implement full generic type support with proper type instantiation

## Comparison with Lottie

BlinkID exhibited the same issues as Lottie, both now fixed:
- ~~Generic type DllImport errors~~ → Still present (CS7042, properties skipped gracefully)
- ~~Operator generation for generic types~~ → ✅ **FIXED in Phase 30**

New issues discovered in BlinkID (all handled):
- C++ interop symbols (Cxx module) → Gracefully skipped
- weak-symbols in TBD files → Parser updated
- Quote escaping in enum error messages → Fixed
