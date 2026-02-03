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
| 2 | Generic enum factory T0.Payload assumption | 🔲 Pending | P0 |
| 3 | Generic type self-reference missing type argument | 🔲 Pending | P0 |

**Target**: Lottie 0 generator errors (clean compile)
**Current**: 3 errors (2× CS0305, 1× CS1061) - CS0029 ✅ eliminated

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

### Status: 🔲 Pending
### Priority: P0 (Critical - 1 of 7 Lottie errors)
### Effort: Low (2-3 hours)
### Dependencies: None

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

// Problem: T0 is a generic type parameter, it doesn't have .Payload
```

### Root Cause

The enum case factory emitter treats `T0` as if it were a concrete type with a `Payload` property. Generic type parameters need different marshalling - likely using `SwiftObjectHelper<T0>` or a constraint-based approach.

### Files to Investigate

1. `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumHandler.cs` - Enum case factory emission
2. `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` - Generic parameter handling

### Implementation Approach

1. Detect when a parameter is a generic type parameter (not a concrete type)
2. Use appropriate marshalling for generic parameters (e.g., `SwiftMarshal.MarshalToSwift<T0>(value0)`)
3. Or skip emission of such methods if generic marshalling isn't supported

### Acceptance Criteria

- [ ] CS1061 error eliminated in Lottie
- [ ] Generic enum case factories either compile correctly or are skipped with reason
- [ ] Unit tests for generic parameter detection in enum factories

---

## Task 3: Generic Type Self-Reference Missing Type Argument (CS0305)

### Status: 🔲 Pending
### Priority: P0 (Critical - 2 of 7 Lottie errors)
### Effort: Low (2-3 hours)
### Dependencies: None

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
```

### Root Cause

When emitting self-references within a generic type, the emitter uses the type name without including the generic parameter.

### Files to Investigate

1. `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ClassHandler.cs` - NewFromPayload emission
2. `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/TypeHandlerHelpers.cs` - Protocol conformance dictionary emission

### Implementation Approach

1. When emitting self-references within a generic type, include the type parameters
2. For `NewFromPayload`: `new Keyframe<T0>(handle)` instead of `new Keyframe(handle)`
3. For conformance dict: `typeof(IEquatable<Keyframe<T0>>)` instead of `typeof(IEquatable<Keyframe>)`

### Acceptance Criteria

- [ ] CS0305 errors eliminated in Lottie (2 errors)
- [ ] Generic types self-reference correctly with type parameters
- [ ] Unit tests for generic type self-reference

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

## Notes

- These are all P0 tasks since they block Lottie from compiling
- Tasks are independent and can be worked in parallel
- After Phase 41, Lottie should have 0 generator errors (only 1 test app error remains in Program.cs)
