# Lottie Binding Gaps

This document tracks binding issues discovered while setting up Lottie as a test case for the swift-bindings project.

**Last Updated**: February 2026 (Phase 33 - Generic Type Internal References Fix)

## Summary

Lottie binding generation revealed several significant gaps in the binding generator. After Phase 33 fixes, **21 errors remain** - these are different categories of issues from what was fixed for BlinkID.

| Metric | Initial | After Phase 33 |
|--------|---------|----------------|
| Compilation errors | 41 | **21** |
| Generic DllImport (CS7042) | Many | 0 ✅ |
| Generic internal refs (CS0305) | Many | Partial ✅ |
| SwiftUI-related errors | - | 8 |
| Other issues | - | 13 |

## Issues Fixed (in previous phases)

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

### 3. Generic Type DllImport (CS7042) - Phase 31

**Status:** ✅ **FIXED**

P/Invoke declarations for generic types are now emitted to non-generic helper classes (`{TypeName}_PInvoke`).

### 4. Generic Type Internal References - Phase 33

**Status:** ✅ **FIXED**

Internal type references (`SwiftObjectHelper<>`, `SwiftSafeHandle<>`, `_payloadSize`, `_payload`) now use `typeNameWithGenerics`. P/Invoke call sites now use helper class prefix and pass metadata parameters.

## Outstanding Issues (21 Errors)

### Category 1: SwiftUI Types (8 errors)

**Errors:** CS0246, CS0314

**Problem:** `LottieView<T0>` has a constraint `where T0 : ISwiftView`, but `ISwiftView` (representing SwiftUI's `View` protocol) is not defined.

**Affected Code:**
```csharp
public unsafe class LottieView<T0> : ISwiftObject where T0 : ISwiftView  // CS0246: ISwiftView not found
```

**Root Cause:** SwiftUI protocols are not yet supported in the binding generator.

**Fix Needed:** Either skip types with SwiftUI constraints or add SwiftUI protocol stubs.

### Category 2: Duplicate Enum Cases (1 error)

**Error:** CS0102

**Problem:** `LottiePlaybackMode.Paused` is defined twice in the enum.

**Affected Code:**
```csharp
public enum LottiePlaybackMode
{
    Paused,
    // ... other cases ...
    Paused,  // CS0102: Duplicate!
}
```

**Root Cause:** Enum case deduplication not implemented in EnumHandler.

**Fix Needed:** Add duplicate case detection in EnumHandler.

### Category 3: Non-Generic Type Used as Generic (10 errors)

**Error:** CS0308

**Problem:** `ValueProviderStorage` is emitted as a non-generic class but is used with type arguments.

**Affected Code:**
```csharp
public class ValueProviderStorage { ... }  // Non-generic

// Usage tries to use it as generic:
new ValueProviderStorage<LottieColor>()  // CS0308!
```

**Root Cause:** The generator didn't detect that `ValueProviderStorage` should be generic.

**Fix Needed:** Improve generic type detection for bound generic usages.

### Category 4: Missing Paired Operator (1 error)

**Error:** CS0216

**Problem:** `Keyframe<T0>.operator !=` is synthesized, but `operator ==` was skipped because its signature was unsupported.

**Affected Code:**
```csharp
// This exists:
public static bool operator !=(Keyframe<T0> left, Keyframe<T0> right)

// But this was skipped (unsupported signature):
// public static bool operator ==(Keyframe<T0> left, Keyframe<T0> right)
```

**Root Cause:** Paired operator synthesis doesn't check if the source operator was actually emitted.

**Fix Needed:** Only synthesize paired operators if the source operator was successfully emitted.

### Category 5: Type Parameter Constraint Mismatch (1 error)

**Error:** CS0315

**Problem:** `ExistentialContainer0` is used as a type argument where `ISwiftObject` is required.

**Affected Code:**
```csharp
Keyframe<ExistentialContainer0>  // CS0315: No boxing conversion from ExistentialContainer0 to ISwiftObject
```

**Root Cause:** Existential containers don't implement `ISwiftObject`.

**Fix Needed:** Either make existential containers implement `ISwiftObject` or skip usages with existential type arguments in generic constraints.

## Statistics

| Metric | Value |
|--------|-------|
| Generated C# lines | ~28,000 |
| Generated C# file size | ~1.17 MB |
| Generated Swift file size | ~37 KB |
| Compilation errors | **21** |
| SwiftUI-related errors | 8 |
| Duplicate enum errors | 1 |
| Generic type detection errors | 10 |
| Operator pairing errors | 1 |
| Type constraint errors | 1 |

## Test Status

- Binding generation: ✅ Completes without crash
- Swift wrapper compilation: ✅ (with stripped-down Swift.Lottie.swift)
- C# binding compilation: ❌ (21 errors in generated code)
- Test app execution: ❌ (blocked by compilation errors)

## Types That Likely Work

Based on the code generation output and error patterns, the following types should work if not dependent on broken types:

- `LottieConfiguration` - Configuration struct
- `LottieColor` - Color struct
- `LottieVector1D` - 1D vector struct
- `LottieVector3D` - 3D vector struct
- `LottieLoopMode` - Enum
- `LottieBackgroundBehavior` - Enum
- Various non-generic classes without SwiftUI dependencies

## Recommendations

1. **Short term:** Skip types with SwiftUI constraints (`ISwiftView`)
2. **Medium term:** Add enum case deduplication; improve generic type detection
3. **Long term:** Add SwiftUI protocol stubs; fix existential container constraints

## Validation Commands

```bash
# Regenerate bindings
cd BindingTesting/Lottie
./regenerate-bindings.sh

# Build test app (includes bindings)
dotnet build LottieTestApp/LottieTestApp.csproj

# Count errors
dotnet build LottieTestApp/LottieTestApp.csproj 2>&1 | grep -c "error CS"
```

## Comparison with BlinkID

BlinkID compiles with 0 errors after Phase 33 fixes. Lottie has additional issues because:

1. **SwiftUI dependency** - Lottie uses SwiftUI's `View` protocol which isn't supported
2. **More complex generics** - Lottie has generic types used with existential type arguments
3. **Enum edge cases** - Duplicate enum case names
4. **Generic type detection** - Some types should be generic but aren't detected as such
