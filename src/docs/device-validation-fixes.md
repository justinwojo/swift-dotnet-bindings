# Device Validation Fixes

**Created**: March 9, 2026
**Source**: Issues discovered during physical device testing of SwiftBindings.Nuke 12.8.0 and SwiftBindings.Lottie 4.6.0 on iPhone (arm64, iOS 26.3.1). Full issue writeup: `/Users/wojo/Dev/swift-bindings-validation/ISSUES.md`.

---

## Completed Fixes

### 1. Class Constructor SwiftIndirectResult (Issues 2b, 2c, 2d)

**Status**: Done
**Severity**: High

`MarshallingHelpers.MethodRequiresIndirectResult()` forced `SwiftIndirectResult` for all non-frozen-struct constructors, including class types. Swift class inits return a pointer in a register — `SwiftIndirectResult` corrupts the calling convention.

**Fix**: Add `ClassDecl` guard so class constructors return `IntPtr` directly.

**File**: `src/Swift.Bindings/src/Marshaler/MarshallingHelpers.cs:106`

**Validated on device**: DataCache constructor now works (Round 3).

---

### 2. ObjC-Rooted Constructor Return Path (Issue 2b)

**Status**: Done
**Severity**: High

`EmitObjCRootedConstructor` unconditionally used `buf`/`SwiftIndirectResult` pattern. After fix #1, P/Invoke returns `IntPtr` directly for class types, but the emitter still read from uninitialized `*buf` instead of the return value.

**Fix**: Added `_requiresIndirectResult` conditional in `WrapperEmitter.cs` — class constructors read from P/Invoke return value, struct constructors use the `SwiftIndirectResult` buffer.

**File**: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/WrapperEmitter.cs:233-256`

**Validated on device**: DataCache constructor works. LottieAnimationView(CGRect) still crashes — different root cause (see Open Issues).

---

### 3. CGRect/CGPoint/CGSize Implicit Conversions (Issue 6)

**Status**: Done
**Severity**: Medium

`Swift.CGRect` and `CoreGraphics.CGRect` have identical layout but no conversion operators. Consumers get `CS1503` when passing standard .NET iOS types to binding APIs.

**Fix**: Add `implicit operator` conversions behind `#if` platform guards.

**Files**: `src/Swift.Runtime/src/Swift/CGRect.cs`, `CGPoint.cs`, `CGSize.cs`

---

### 4. DllImport Resolver for Late-Loaded Assemblies (Issue 4)

**Status**: No code change needed — resolved by rebuilding packages

Generated bindings already emit per-assembly `[ModuleInitializer]` + `SetDllImportResolver`. Rebuilding the packages with the current generator was sufficient.

---

### 5. Enum Projection for Comparable Enums (Issue 7)

**Status**: Done
**Severity**: Low

`CanSafelyEmitAsSimpleEnum` rejected enums with `<` operator from `Comparable` conformance. C# integral enums natively support comparison operators.

**Fix**: Allow `<`, `>`, `<=`, `>=` operators through the simple enum gate.

**File**: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumHandler.SimpleEnum.cs:1522`

**Result**: `Nuke.ImageRequest.Priority` now projects as `enum Priority : int`.

---

### 6. Tuple Label Loss in Swift Wrapper Emission

**Status**: Done
**Severity**: High — blocked all wrapper xcframework compilation for libraries using labeled tuples

`SwiftTypeNameHelper.GetSwiftTypeName()` and `ExistentialBypassEmitter.RenderSwiftTypeSpec()` dropped `TypeLabel` when rendering tuple elements.

**Fix**: Include `TypeLabel` prefix when rendering each tuple element in both methods.

**Files**: `src/Swift.Bindings/src/Emitter/StringEmitter/SwiftTypeNameHelper.cs:66-76`, `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ExistentialBypassEmitter.cs:1121-1139`

---

### 7. SwiftOptional.Some Use-After-Free (Issue 10)

**Status**: Done
**Severity**: High — LottieAnimation property getters returned garbage data on device

`SwiftOptional<T>.Some` used `stackalloc` for payload copy, passed stack pointer to `MarshalFromSwift` → `NewFromPayload` → `SwiftSafeHandle(ownsHandle: true)`. Later `ReleaseHandle` called `NativeMemory.Free` on stack memory.

**Fix**: Changed to `NativeMemory.Alloc` (heap copy). Added conditional free: ISwiftObject types transfer ownership to `NewFromPayload`; non-ISwiftObject types (primitives, tuples) are freed after read.

**File**: `src/Swift.Runtime/src/Swift/SwiftOptional.cs:228-268`

**Validated on device**: LottieAnimation properties now return correct data (4.0s, 60fps, 0-238 frames).

---

### 8. Generate Per-Type `@_cdecl` Destroy Wrappers (Issues 1, 5)

**Status**: Done
**Severity**: High — Dispose() crashes on device

`SwiftSafeHandle<T>.ReleaseHandle()` calls `ValueWitnessTable->Destroy()` via indirect `CallConvSwift` function pointer — crashes on NativeAOT.

**Fix**: New `DestroyWrapperEmitter` emits `SBW_Destroy_{Module}_{Type}` per type in each wrapper framework. `SwiftSafeHandle<T>.RegisterDestroyAction()` allows generated code to register type-specific destroy actions via static field initializer. Falls back to VWT when no wrapper library (generic types also fall back due to CS7042).

Generic skip logic refined after Codex review: only skips when the **containing type itself** is generic (e.g., `SpikeBox<T>`), not when a non-generic derived type has a generic root SafeHandle type (e.g., `IntContainer` with `SwiftSafeHandle<Container<int>>`). Uses `StartsWith(csharpTypeName, StringComparison.Ordinal)` check.

**Files**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/DestroyWrapperEmitter.cs` (new)
- `src/Swift.Runtime/src/Swift/Runtime/SwiftHandle.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/ModuleEmissionContext.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ClassHandler.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/NonFrozenStructHandler.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/FrozenStructHandler.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumHandler.cs`
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/DestroyWrapperEmitterTests.cs` (new, 16 tests)

---

### 9. Update SB1001 Analyzer (Issue 5)

**Status**: Done — resolved by #8
**Severity**: Medium

No code changes needed. Issue 8's destroy wrappers make Dispose() safe for all non-generic types with a wrapper library (xcframework mode). The SB1001 analyzer recommendation ("use `using` or call `Dispose()`") is now correct.

**Edge case**: Generic types (e.g., `SpikeBox<T>`) fall back to VWT→Destroy due to CS7042 (DllImport not allowed in generic types). This is a narrow edge case — most consumer-facing types are non-generic.

---

## Open Issues — NativeAOT CallConvSwift Limitations

### 10. LottieAnimationView(CGRect) SIGSEGV (Issue 2b)

**Status**: NativeAOT limitation — needs constructor @_cdecl wrappers
**Severity**: High — blocks all LottieAnimationView construction

The ObjC constructor return path fix (#2) resolved DataCache but NOT LottieAnimationView(CGRect). Round 4 confirmed the fix is deployed (code offset changed), but the crash persists.

**Root cause confirmed**: The constructor P/Invoke calls directly into Lottie (NOT through wrapper library):
```csharp
[UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
[LibraryImport("Lottie", EntryPoint = "$s6Lottie0A13AnimationViewC5frameACSo6CGRectV_tcfC")]
private static partial IntPtr PInvoke_init_8B933573( Swift.CGRect frame);
```
CGRect (32 bytes, 4 Doubles) is passed directly via CallConvSwift. On ARM64 NativeAOT, the struct parameter splitting across registers/memory differs from what Swift expects. The `__swift_memcpy24_8` crash (24-byte memcpy for 32-byte struct) confirms the ABI mismatch.

**Fix approach**: Generate constructor `@_cdecl` wrappers in the Swift wrapper library, similar to destroy wrappers. The wrapper would take parameters via C calling convention and call the Swift init internally. This is a significant feature requiring:
- New `ConstructorWrapperEmitter` (per-constructor wrapper generation)
- Integration with `WrapperEmitter` to redirect P/Invoke calls through wrapper
- Handling of ObjC-rooted vs non-ObjC, class vs struct return conventions
- Default parameter overloads, failable inits, generic parameters

**Scope**: Dedicated session — touches the core constructor emission pipeline.

---

### 11. LottieAnimationView() Returns Null (Issue 2c)

**Status**: Investigated — not a generator bug, likely @MainActor isolation issue on device
**Severity**: Medium

**Investigation findings**:
- LottieAnimationView has NO parameterless `init()` in the ABI JSON
- The closest is `init(configuration:logger:)` (designated, both params have defaults)
- The generator correctly creates a default-parameter overload via Swift wrapper:
  ```swift
  @_silgen_name("DBW_LottieAnimationView_init_4E165D2F_2")
  public static func _dbw_init_4E165D2F_2() -> Lottie.LottieAnimationView {
      return Lottie.LottieAnimationView()
  }
  ```
- The symbol IS exported in `LottieSwiftBindings.xcframework` (verified: `_DBW_LottieAnimationView_init_4E165D2F_2` at address `0xc880`)
- LottieAnimationView is `@MainActor` annotated — init may fail when called off main thread
- Non-failable init should never return nil in normal Swift execution

**Possible causes on device**:
1. `@MainActor` isolation: init called from non-main thread on NativeAOT → undefined behavior
2. `LottieConfiguration()` or `LottieLogger()` default values require runtime setup not available on NativeAOT
3. CallConvSwift return value marshalling issue (though simple pointer return should be safe)

**Recommendation**: Test with explicit `configuration:logger:` parameters on main thread. If that works, the issue is default parameter evaluation context, not the generator.

---

### 12. LottieColor Struct Constructor SIGSEGV (Issue 2a)

**Status**: NativeAOT limitation — needs constructor @_cdecl wrappers (same feature as #10)
**Severity**: High

**Investigation findings**:
- LottieColor is a non-frozen struct with constructor `init(r:g:b:a:denominator:)` (5 params, not 4 as originally reported — `denominator` has a default value)
- Generator correctly uses `SwiftIndirectResult` for non-frozen struct return
- Crash is in NativeAOT runtime's handling of CallConvSwift with large indirect results

**Fix**: Same constructor `@_cdecl` wrapper approach as Issue #10. The wrapper would:
```swift
@_cdecl("SBW_Lottie_LottieColor_init")
public func SBW_Lottie_LottieColor_init(
    _ resultPtr: UnsafeMutableRawPointer,
    _ r: Double, _ g: Double, _ b: Double, _ a: Double, _ denominator: Double
) {
    let result = Lottie.LottieColor(r: r, g: g, b: b, a: a, denominator: denominator)
    resultPtr.initializeMemory(as: Lottie.LottieColor.self, repeating: result, count: 1)
}
```
C# side would use `CallingConvention.Cdecl`, allocate a buffer, pass it as `IntPtr`, and read the result.

---

## Future

### 13. XML Documentation on Generated Types

**Severity**: Low — discoverability improvement

Generate XML doc comments from Swift documentation in `.swiftinterface` files.

---

## Device Test Results (Round 4 — Current)

### Nuke: 8/8 passed — fully functional
| Test | Result |
|------|--------|
| ImagePipeline.Shared | PASS |
| ImageRequest construction | PASS |
| ImagePipeline.Configuration | PASS |
| Priority enum (5 cases) | PASS |
| Priority enum cast | PASS |
| ImageRequest.Options | PASS |
| DataCache construction | PASS |
| Image load (network) | PASS |

### Lottie: 4/4 passed, 2 skipped
| Test | Result | Blocker |
|------|--------|---------|
| LottieConfiguration.Shared | PASS | |
| DecodingStrategy enum | PASS | |
| LottieLoopMode cases | PASS | |
| LottieAnimation.Filepath | PASS | |
| LottieAnimationView playback | SKIP | Issue #10 — CGRect struct param SIGSEGV |
| LottieColor construction | SKIP | Issue #12 — NativeAOT limitation |
