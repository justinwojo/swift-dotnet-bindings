# Lottie Binding Gaps

This document tracks binding issues discovered while setting up Lottie as a test case for the swift-bindings project.

## Summary

Lottie binding generation revealed several significant gaps in the binding generator that need to be addressed before Lottie can be fully bound.

## Issues Fixed (in this PR)

### 1. Existential Type Detection for `any Protocol` Types

**File:** `src/Swift.Bindings/src/TypeDatabase/TypeDatabaseExtensions.cs`

**Issue:** Types like `any Swift.Encoder` were not being detected as existential types because the `IsExistentialTypeName` function only checked for string patterns like `"any "` in the type name, but TypeSpecParser parses these as `NamedTypeSpec` with `IsAny=true` flag and `Name="Swift.Encoder"`.

**Fix:** Added check for `typeSpec.IsAny` at the start of `IsExistentialTypeName()`.

**Impact:** Methods using `Encoder`, `Decoder`, and other protocol types as parameters now correctly get skipped instead of crashing.

### 2. Generic Type Parameter Detection in Closures

**File:** `src/Swift.Bindings/src/Marshaler/ClosureHandler.cs`

**Issue:** Closures with generic type parameters (like `τ_0_0`) were causing crashes when `SwiftTypeName.FromModuleQualifiedName()` tried to parse them as module-qualified names.

**Fix:** Added `IsGenericTypeParameter()` helper and check in `IsSupportedClosureParameterType()` to return `false` for generic type parameters.

**Impact:** Closures with generic parameters are now correctly skipped.

## Outstanding Issues (Not Fixed)

### 1. Generic Type Classes (Keyframe<T0>)

**Error:** `CS7042: The DllImport attribute cannot be applied to a method that is generic or contained in a generic method or type.`

**Root Cause:** The binding generator emits generic classes like `Keyframe<T0>` with P/Invoke methods inside them, which C# doesn't allow.

**Affected Types:** `Keyframe<T0>` and potentially others

**Fix Needed:** Generic types should either be excluded from emission or the P/Invoke should be factored out of the generic class.

### 2. Member Names Matching Enclosing Type

**Error:** `CS0542: 'Animation': member names cannot be the same as their enclosing type`

**Root Cause:** `DotLottieFile.Animation` nested type has a property named `Animation`.

**Fix Needed:** Property renaming logic when property name matches containing type name.

### 3. Swift Protocol Proxy Code Generation

**Issue:** The generated Swift code (`Swift.Lottie.swift`) for EveryProtocol conformances has multiple issues:

- Missing imports for `CoreGraphics`, `CoreText`, `QuartzCore`
- Invalid Swift syntax like `(any Any.Type).self`
- Empty return types: `public func value(frame: CoreGraphics.CGFloat) ->  {`
- Unresolved generic type parameters `τ_0_0` in protocol method signatures

**Affected Protocols:**
- `AnimationFontProvider`
- `AnimationTextProvider`
- `AnimationImageProvider`
- `TextContentsScaleProvider`
- `AnyValueProvider`
- `Interpolatable`
- `SpatialInterpolatable`
- `AnyInterpolatable`

**Fix Needed:** Major improvements to the EveryProtocol/protocol proxy emitter.

### 4. Async Method Return Types

**Issue:** Async wrappers require `@convention(c)` compatible return types. Complex Swift types like `DotLottieFile` aren't Objective-C representable.

**Error:** `'(DotLottieFile, Int64) -> Void' is not representable in Objective-C`

**Fix Needed:** Async wrappers need to return results via indirect pointers or use a different callback mechanism.

### 5. Operator Generation for Generic Types

**Issue:** Operators on generic types like `Keyframe<T0>` reference the non-generic type name `Keyframe`.

**Error:** `CS0305: Using the generic type 'Keyframe<T0>' requires 1 type arguments`

**Fix Needed:** Operator emission needs to handle generic types properly.

## Types That Likely Work

Based on the code generation output and Nuke test case patterns, the following types should work:

- `LottieConfiguration` - Configuration struct
- `LottieColor` - Color struct
- `LottieVector1D` - 1D vector struct
- `LottieVector3D` - 3D vector struct
- `LottieLoopMode` - Enum
- `LottieBackgroundBehavior` - Enum
- Various non-generic classes without protocol conformance issues

## Recommendations

1. **Short term:** Focus on fixing the existential/generic type parameter issues (already done) and test with Nuke which has fewer complex types.

2. **Medium term:** Address the generic class DllImport issue as this blocks many animation frameworks.

3. **Long term:** Rewrite the EveryProtocol/protocol proxy emitter with better type handling.

## Statistics

- Total classes in generated bindings: ~80
- Compilation errors: 41
- Warnings: 240
- Generated C# file size: 1.17 MB (28,330 lines)
- Generated Swift file size: 37 KB (before trimming)

## Test Status

- Binding generation: ✅ Completes without crash (after fixes)
- Swift wrapper compilation: ✅ (with stripped-down Swift.Lottie.swift)
- C# binding compilation: ❌ (41 errors in generated code)
- Test app execution: ❌ (blocked by compilation errors)
