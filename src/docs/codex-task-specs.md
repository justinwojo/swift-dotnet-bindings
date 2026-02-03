# Codex Task Specifications - Phase 41

Task specifications for fixing the remaining Lottie generator bugs. These are pre-existing issues surfaced after Phase 40 fixed protocol-related errors.

**Date**: February 2026
**Starting Point**: Phase 40 complete, 1028 unit tests passing
**Libraries**: Nuke (0 errors), BlinkID (0 errors), Lottie (3 generator errors)

---

## Status Summary

| Task | Description | Status | Priority |
|------|-------------|--------|----------|
| 1 | Closure callbacks returning frozen/non-frozen structs | ✅ **COMPLETED** | P0 |
| 2 | Generic enum factory T0.Payload assumption | ✅ **COMPLETED** | P0 |
| 3 | Generic type self-reference missing type argument | ✅ **COMPLETED** | P0 |

**Target**: Lottie 0 generator errors (clean compile) ✅ ACHIEVED
**Current**: 0 generator errors - Phase 41 complete

---

## Task 1: Closure Callbacks Returning Frozen/Non-Frozen Structs (CS0029)

### Status: ✅ COMPLETED (February 2026)
### Priority: P0 (Critical - 4 of 7 Lottie errors)
### Effort: Medium (4-6 hours)
### Dependencies: None

### Completion Notes

**Implemented by**: Codex
**Unit Tests**: 1028 passed (up from 1024)

**Files Modified**:
- `src/Swift.Bindings/src/Marshaler/ClosureHandler.cs` - Added `CanUseDirectCallbackReturn()` for frozen structs/primitives, updated `RequiresIndirectReturnMarshalling()` for non-frozen structs
- `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.cs` - Escaping/throwing closure callbacks now emit typed return signatures for eligible types, function pointer declarations match callback return types
- `src/Swift.Bindings/tests/UnitTests/MarshalerTests/ClosureHandlerTests.cs` - Added non-frozen struct indirect return test
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/MethodHandlerOutputTests.cs` - Added regression tests for frozen struct, scalar, and non-frozen struct closure returns

**Key fixes**:
1. Frozen structs (CGSize, CGPoint) and primitives (double) use direct callback return
2. Non-frozen structs (LottieColor) use indirect return marshalling via `void* indirectResult`
3. Function pointer signatures now match callback return types

**Results**: CS0029 errors eliminated (4 → 0)

### Problem Statement

Closure callback methods that return frozen structs are emitted with `void*` return type, but the actual delegate returns the struct value directly. This causes CS0029 "Cannot implicitly convert type 'X' to 'void*'".

**Affected lines in Swift.Lottie.cs**:
- Line 8253: `return del(...)` returns `Swift.CGSize`, callback signature returns `void*`
- Line 11728: `return del(...)` returns `Swift.Lottie.LottieColor`, callback signature returns `void*`
- Line 21731: `return del(...)` returns `double`, callback signature returns `void*`
- Line 25949: `return del(...)` returns `Swift.CGPoint`, callback signature returns `void*`

### Example

```csharp
// Generated (broken)
private static void* init_block_48B7AC29_Callback(void* arg0, SwiftSelf context)
{
    var del = SwiftClosureMarshaller.GetDelegateFromContext<Func<System.Double, Swift.CGSize>>(new IntPtr(context.Value));
    return del(SwiftMarshal.MarshalFromSwift<System.Double>(new IntPtr(arg0)));  // CS0029!
}

// Should be (for frozen struct returns)
private static Swift.CGSize init_block_48B7AC29_Callback(void* arg0, SwiftSelf context)
{
    var del = SwiftClosureMarshaller.GetDelegateFromContext<Func<System.Double, Swift.CGSize>>(new IntPtr(context.Value));
    return del(SwiftMarshal.MarshalFromSwift<System.Double>(new IntPtr(arg0)));
}
```

### Root Cause

The closure callback emitter (`ClosureEmitter.cs`) always uses `void*` for return types, which works for reference types but not for frozen structs that should be returned by value.

### Files to Investigate

1. `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.cs` - Closure callback emission
2. `src/Swift.Bindings/src/Marshaler/ClosureHandler.cs` - Closure marshalling decisions

### Implementation Approach

1. In closure callback emission, check if the return type is a frozen struct
2. If frozen struct: use the actual C# type as return type, return the value directly
3. If non-frozen/reference type: continue using `void*` with pointer marshalling

### Acceptance Criteria

- [x] CS0029 errors eliminated in Lottie (4 errors)
- [x] Closure callbacks with frozen struct returns compile correctly
- [x] Closure callbacks with non-frozen struct returns use indirect marshalling
- [x] Unit tests for closure return type handling
- [x] Nuke and BlinkID still compile clean

### Validation

**Validated by**: Claude
**Date**: February 2026

- All unit tests pass (1028)
- Lottie CS0029 errors: 4 → 0
- Remaining Lottie errors: 3 (CS1061, CS0305×2)

---

## Task 2: Generic Enum Factory T0.Payload Assumption (CS1061)

### Status: ✅ COMPLETED (February 2026)
### Priority: P0 (Critical - 1 of 7 Lottie errors)
### Effort: Low (2-3 hours)
### Dependencies: None

### Completion Notes

**Implemented by**: Codex
**Validated by**: Claude
**Unit Tests**: 1029 passed (up from 1028)

**Files Modified**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumHandler.cs` - Added generic type parameter detection and proper marshalling via `SwiftMarshal.MarshalToSwift()`
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/EnumHandlerOutputTests.cs` - Added `Emit_GenericEnum_EmitsGenericTypeAndPInvokeHelper` test

**Key fix**: When a parameter is a generic type parameter (T0), marshal via:
1. `TypeMetadata.GetTypeMetadataOrThrow<T0>()` - get metadata
2. `stackalloc byte[(int)metadata.Size]` - allocate buffer
3. `SwiftMarshal.MarshalToSwift(value0, ref value0SwiftSpan)` - marshal to buffer
4. `(IntPtr)value0SwiftBuffer` - pass buffer pointer to P/Invoke

**Results**: CS1061 error eliminated (1 → 0)

### Problem Statement

Generic enum case factory methods assume the type parameter `T0` has a `.Payload` property, but generic type parameters don't have this property.

**Affected line**: Swift.Lottie.cs:16413

### Example

```csharp
// Generated (broken)
public static ValueProviderStorage<T0> SingleValue(T0 value0)
{
    // ...
    ValueProviderStorage_PInvoke.PInvoke_SingleValue(indirectResult, value0.Payload.DangerousGetHandle(), ...);  // CS1061!
}

// Fixed: Use generic marshalling pattern
public static ValueProviderStorage<T0> SingleValue(T0 value0)
{
    var value0Metadata = TypeMetadata.GetTypeMetadataOrThrow<T0>();
    byte* value0SwiftBuffer = stackalloc byte[(int)value0Metadata.Size];
    var value0SwiftSpan = new Span<byte>(value0SwiftBuffer, (int)value0Metadata.Size);
    SwiftMarshal.MarshalToSwift(value0, ref value0SwiftSpan);
    ValueProviderStorage_PInvoke.PInvoke_SingleValue(indirectResult, (IntPtr)value0SwiftBuffer, ...);
}
```

### Acceptance Criteria

- [x] CS1061 error eliminated in Lottie
- [x] Generic enum case factories compile correctly with proper marshalling
- [x] Unit tests for generic parameter detection in enum factories

---

## Task 3: Generic Type Self-Reference Missing Type Argument (CS0305)

### Status: ✅ COMPLETED (February 2026)
### Priority: P0 (Critical - 2 of 7 Lottie errors)
### Effort: Low (2-3 hours)
### Dependencies: None

### Completion Notes

**Implemented by**: Codex
**Validated by**: Claude
**Unit Tests**: 1029 passed

**Files Modified**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ClassHandler.cs` - Fixed `NewFromPayload` to use `_typeNameWithGenerics` for constructor call
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/TypeHandlerHelpers.cs` - Fixed conformance dictionary to use `typeName` (which includes generics)
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumHandler.cs` - Fixed `NewFromPayload` for generic enums
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/TypeHandlersOutputTests.cs` - Added `Emit_ClassHandler_GenericClass_UsesTypeArgumentsInSelfReferences` test

**Key fix**: Pass `typeNameWithGenerics` (e.g., `Keyframe<T0>`) instead of just type name to:
1. `NewFromPayload` constructor: `new Keyframe<T0>(handle)`
2. Protocol conformance dictionary: `typeof(IEquatable<Keyframe<T0>>)`

**Results**: CS0305 errors eliminated (2 → 0)

### Problem Statement

Generic types reference themselves without the type argument in certain contexts.

**Affected lines**: Swift.Lottie.cs:26277, 26315

### Example

```csharp
// Generated (broken)
public class Keyframe<T0> : ISwiftObject, IEquatable<Keyframe<T0>>
{
    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
    {
        return new Keyframe(handle);  // CS0305! Should be Keyframe<T0>
    }

    static Keyframe()
    {
        _protocolConformanceSymbols = new Dictionary<Type, string>
        {
            {typeof(IEquatable<Keyframe>), "..."}  // CS0305! Should be IEquatable<Keyframe<T0>>
        };
    }
}

// Fixed
static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
{
    return new Keyframe<T0>(handle);  // ✓ Correct
}
{typeof(IEquatable<Keyframe<T0>>), "..."}  // ✓ Correct
```

### Acceptance Criteria

- [x] CS0305 errors eliminated in Lottie (2 errors)
- [x] Generic types self-reference correctly with type parameters
- [x] Unit tests for generic type self-reference

---

## Testing Commands Reference

```bash
# Run all unit tests
./run-tests.sh

# Regenerate and test Lottie
cd BindingTesting/Lottie
./regenerate-bindings.sh
dotnet build LottieTestApp/LottieTestApp.csproj

# Count errors by type
dotnet build LottieTestApp/LottieTestApp.csproj 2>&1 | grep "error CS" | sed 's/.*error \(CS[0-9]*\).*/\1/' | sort | uniq -c

# Regenerate and test Nuke (with runtime validation)
cd BindingTesting/Nuke
./build-all.sh && ./validate-sim.sh 15
```

---

## Phase 41 Summary

**Completed**: February 2026
**Result**: Lottie 0 generator errors ✅

| Metric | Before | After |
|--------|--------|-------|
| Unit Tests | 1028 | 1029 |
| Lottie Generator Errors | 7 | 0 |
| Nuke Errors | 0 | 0 |
| BlinkID Errors | 0 | 0 |

**Note**: Lottie test app has 1 error in `Program.cs` (CS7036 - missing constructor argument) due to Lottie API changes. This is a test app issue, not a generator issue.

## Notes

- All three tasks were completed successfully
- Generator now properly handles generic type parameters in enum factories
- Generic types now correctly include type arguments in self-references
- All three test libraries compile with 0 generator errors
