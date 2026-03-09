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

## Open Issues — Needs Investigation

### 8. LottieAnimationView(CGRect) SIGSEGV (Issue 2b)

**Status**: Not fixed — root cause differs from DataCache
**Severity**: High — blocks all LottieAnimationView construction (Issue 8)

The ObjC constructor return path fix (#2) resolved DataCache but NOT LottieAnimationView(CGRect). Round 4 confirmed the fix is deployed (code offset changed), but the crash persists.

**Crash stack**: `__swift_memcpy24_8` — a 24-byte memcpy for a 32-byte struct (CGRect = 4 Doubles). The size mismatch suggests `CallConvSwift` is passing CGRect incorrectly on ARM64 — possibly splitting it across registers/memory differently than Swift expects.

**Key difference from DataCache**: DataCache's constructor takes a `SwiftString` (passed via `@_cdecl` wrapper). LottieAnimationView's constructor takes a `CGRect` struct passed directly via `CallConvSwift`. The struct parameter passing is the likely issue.

**Possible fixes**:
- Generate `@_cdecl` wrapper for constructors taking struct parameters (route through C calling convention)
- Investigate ARM64 struct passing rules in CallConvSwift vs actual Swift ABI

---

### 9. LottieAnimationView() Returns Null (Issue 2c)

**Status**: Not investigated
**Severity**: Medium

Parameterless `init()` returns null. May not exist on this type (only `init(frame:)` may be a valid designator). Needs ABI JSON inspection.

---

### 10. LottieColor Struct Constructor SIGSEGV (Issue 2a)

**Status**: NativeAOT limitation — no generator fix possible
**Severity**: High

Non-frozen struct with 4 Doubles + enum. Generator correctly uses `SwiftIndirectResult`. Crash is in NativeAOT runtime's handling of CallConvSwift with large indirect results.

**Possible workaround**: `@_cdecl` wrapper for struct constructors (same pattern as Destroy wrappers).

---

## Planned — Session 2

### 11. Generate Per-Type `@_cdecl` Destroy Wrappers (Issues 1, 5)

**Status**: Not started
**Severity**: High — Dispose() crashes on device

`SwiftSafeHandle<T>.ReleaseHandle()` calls `ValueWitnessTable->Destroy()` via indirect `CallConvSwift` function pointer — crashes on NativeAOT.

**Fix**: New `DestroyWrapperEmitter` that generates `SBW_{TypeName}_Destroy` per type in each wrapper framework. Wire `ReleaseHandle()` to call the cdecl wrapper instead of the VWT.

**Consumer workaround until fixed**: Don't call `Dispose()` or use `using` on ISwiftObject types.

### 12. Update SB1001 Analyzer (Issue 5)

**Status**: Not started — depends on #11

**File**: `src/Swift.Analyzers/SwiftObjectDisposeAnalyzer.cs:35-43`

---

## Future

### 13. XML Documentation on Generated Types (Issue 9)

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
| LottieAnimationView playback | SKIP | Issue 2b — CGRect struct param SIGSEGV |
| LottieColor construction | SKIP | Issue 2a — NativeAOT limitation |
