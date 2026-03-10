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
**Severity**: Medium

The original ISSUES.md reported `DllNotFoundException: NukeSwiftBindings` because the old NuGet packages were built before the generator emitted `[ModuleInitializer]` resolvers. Rebuilding the packages with the current generator fixed it.

**How it works**: Every generated binding assembly includes `__SwiftFrameworkResolver_{Module}` with a `[ModuleInitializer]` that calls `NativeLibrary.SetDllImportResolver`. When .NET lazily loads the binding assembly on first type access, the initializer fires and registers a resolver that maps DllImport names to `@rpath/{name}.framework/{name}`. This is per-assembly, so it doesn't conflict with the consumer app's own resolvers. Zero consumer boilerplate required.

**File**: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ModuleHandler.cs:253` (`EmitFrameworkResolver`)

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

## Recently Fixed — Constructor @_cdecl Wrappers

### 10. LottieAnimationView(CGRect) SIGSEGV (Issue 2b)

**Status**: Done — unblocked by #14 fix, pending device revalidation
**Severity**: High — blocks all LottieAnimationView construction

The ObjC constructor return path fix (#2) resolved DataCache but NOT LottieAnimationView(CGRect). Round 4 confirmed the fix is deployed (code offset changed), but the crash persists.

**Root cause confirmed**: The constructor P/Invoke calls directly into Lottie (NOT through wrapper library):
```csharp
[UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
[LibraryImport("Lottie", EntryPoint = "$s6Lottie0A13AnimationViewC5frameACSo6CGRectV_tcfC")]
private static partial IntPtr PInvoke_init_8B933573( Swift.CGRect frame);
```
CGRect (32 bytes, 4 Doubles) is passed directly via CallConvSwift. On ARM64 NativeAOT, the struct parameter splitting across registers/memory differs from what Swift expects. The `__swift_memcpy24_8` crash (24-byte memcpy for 32-byte struct) confirms the ABI mismatch.

**Fix**: `ConstructorWrapperEmitter` routes all constructor P/Invokes through `@_cdecl` Swift wrappers using `CallingConvention.Cdecl`. See `src/docs/Completed/constructor-cdecl-wrappers-plan.md`.

**Files**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/ConstructorWrapperEmitter.cs` (new)
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PInvokeEmitter.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/DefaultParameterOverloadEmitter.cs`
- `src/Swift.Bindings/src/Marshaler/Projection/MethodMarshalPlanBuilder.cs`
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/ConstructorWrapperEmitterTests.cs` (new, 36 tests)

**Pending**: Device revalidation with rebuilt SDK. #14 fix (ConstructorHandler dispatch + ~Copyable guard hardening) is complete in source.

---

## Open Issues

### 14. Constructor @_cdecl Wrappers Not Applied to Full-Parameter Constructors

**Status**: Done — ConstructorHandler dispatch gap fixed (Bug A), ~Copyable guard hardened (Bug B)
**Severity**: Critical — blocks ALL struct constructor wrappers, negates fix #10/#12

**Discovery**: Device validation of Lottie with dev SDK (0.0.0-dev) showed LottieColor constructor still SIGSEGV. Investigation revealed:

1. The generated C# for `LottieColor.init(r:g:b:a:denominator:)` still uses `CallConvSwift` with direct import from `"Lottie"` — no `@_cdecl` wrapper was generated.
2. The generated C# for `LottieConfiguration()` (0-param default overload) correctly uses `CallConvCdecl` with import from `"LottieSwiftBindings"` — wrapper IS present.

**Root cause — two bugs**:

#### Bug A: ConstructorHandler dispatch gap (commit 4228248c)

The `ConstructorWrapperEmitter` integration was added only to the **MethodHandler general method path** (`MethodHandler.cs` line ~709 at commit time), not to `ConstructorHandler.Emit()`. But constructors are dispatched to `ConstructorHandler` via `ConstructorHandlerFactory.Handles()`, so they never reach the general method path.

- `ConstructorHandlerFactory.Handles()` returns `true` for all non-async constructors on StructDecl/ClassDecl (line 43-48)
- The Conductor tries `ConstructorHandlerFactory` before `MethodHandlerFactory` (line 59-62 in `Conductor.cs`)
- Result: full-parameter constructors go through `ConstructorHandler.Emit()` which had NO `ShouldEmitWrapper` call
- Only `DefaultParameterOverloadEmitter`-generated overloads got wrappers because that emitter has its own `ShouldEmitWrapper` call (line 91 in `DefaultParameterOverloadEmitter.cs`)

**Evidence**: In the generated Lottie bindings:
- `LottieConfiguration` full constructor (4 params) → `CallConvSwift` + `"Lottie"` (no wrapper)
- `LottieConfiguration()` default overload (0 params) → `CallConvCdecl` + `"LottieSwiftBindings"` (has wrapper)
- `LottieAnimationView` constructors → all have wrappers (but these are all default-param overloads via `@_silgen_name`)
- `LottieColor` constructor → `CallConvSwift` + `"Lottie"` (no wrapper, only one constructor, default param handled via C# default value)

**Fix A**: Add `ShouldEmitWrapper` integration to `ConstructorHandler.Emit()` mirroring the general method path. **This fix has already been applied** to the current source (line 322 in `MethodHandler.cs`) but the SDK has not been rebuilt.

#### Bug B: ~Copyable guard blocks ALL struct wrappers

After Bug A was fixed in the source, a `~Copyable` guard was added to `ShouldEmitWrapper()`:

```csharp
// Lines 46-51 in ConstructorWrapperEmitter.cs
if (env.ParentDecl is StructDecl structDecl &&
    structDecl.Conformances.Any(c => c.Protocol.ToString() == "Swift.Escapable"))
    return false;
```

The intent was to skip non-copyable types because the current wrapper uses `initializeMemory(as:repeating:count:)` which requires `Copyable` conformance. The assumption was that only `~Copyable` types explicitly list `Escapable`.

**This assumption is wrong**, and conformance-based detection is fundamentally unreliable:

**Problem 1 — `Escapable` presence blocks all structs on newer toolchains.**
Libraries built with Swift 6.2 (Xcode 26) explicitly list both `Copyable` and `Escapable` on all normal types:
```json
// LottieColor (normal copyable struct, Swift 6.2):
"conformances": [
  {"name": "Copyable"}, {"name": "Escapable"},
  {"name": "Hashable"}, {"name": "Equatable"}, ...
]
```
The guard blocks ALL struct constructor wrappers in Lottie, BlinkID, and Stripe.

**Problem 2 — Checking `!Copyable` instead is also wrong.**
Codex review found that Nuke's ABI JSON (in `swift-bindings/.libraries/`) omits BOTH marker protocols on ordinary structs like `ImageRequest`, `Progress`, `Options`, `UserInfoKey` — even though they are plain `public struct` in the swiftinterface (not `~Copyable`). So `!Copyable` would incorrectly suppress wrappers for normal Nuke structs.

**Root cause of Problem 2 — Swift toolchain version.**
The marker protocol discrepancy is caused by different Swift compiler versions:
- `swift-bindings/.libraries/Nuke/` — built with **Swift 5.10** (swiftlang-5.10.0.13, Xcode 15) — ABI JSON does NOT include `Copyable`/`Escapable` conformances
- `swift-dotnet-packages/libraries/Nuke/` — built with **Swift 6.2** (swiftlang-6.2.3.3.21, Xcode 26) — ABI JSON DOES include `Copyable`/`Escapable` conformances

Marker protocol emission was added to the ABI JSON format between Swift 5.10 and 6.2. Any detection strategy based on conformance presence/absence will produce false results for libraries built with older toolchains.

No confirmed `~Copyable` structs exist in any of the target libraries (Lottie, Nuke, BlinkID, Stripe).

#### Fix B: Harden ~Copyable guard (commit series, current)

The original guard checked `Escapable` conformance on the **parent struct** only. This was unreliable across Swift toolchain versions (see Problems 1 & 2 above). The guard was reworked in three iterations based on code review:

**Iteration 1**: Keep the parent-type `Escapable` conformance guard (for same-module structs where the full `StructDecl` is available), but the real-world toolchain issues above mean this only fires for genuine `~Copyable` types built with Swift 6.2+.

**Iteration 2**: Add `HasNonCopyableStructParameter()` to also guard non-copyable struct **parameters** (not just parent types). Uses `FindStructDecl()` recursive search in `ModuleDecl.Types`.

**Iteration 3**: Fix cross-module parameter detection. `HasNonCopyableStructParameter()` only searched same-module types — cross-module `~Copyable` struct parameters from dependency modules slipped through because `FindStructDecl` returns null. Fix:
- Added `NonCopyable = 1 << 10` to `TypeRecordFlags`
- `ModuleProcessor.CacluateFlags()` sets the flag when `StructDecl` has explicit `Swift.Escapable` conformance
- `ModuleDatabaseEmitter` serializes `nonCopyable="true"` attribute to XML
- `TypeDatabase.ReadVersion1_0()` deserializes the attribute back to the flag
- `HasNonCopyableStructParameter()` falls back to `TypeRecord.Flags.HasFlag(NonCopyable)` when `FindStructDecl` returns null (cross-module case)

**Additionally**, all 4 `initializeMemory(as:repeating:count:)` call sites were changed to `assumingMemoryBound(to:).initialize(to:)` (SE-0427, Swift 6.0+), which is universally safe for both `Copyable` and `~Copyable` types. This makes the `~Copyable` guard defense-in-depth rather than load-bearing — if a detection gap appears in the future, the wrapper will still compile correctly.

**Files changed (Bug B)**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/ConstructorWrapperEmitter.cs` — `ShouldEmitWrapper`, `HasNonCopyableStructParameter`, `FindStructDecl`
- `src/Swift.Bindings/src/TypeDatabase/TypeRecord.cs` — `NonCopyable` flag
- `src/Swift.Bindings/src/Parser/ModuleProcessor.cs` — `CacluateFlags()` sets flag
- `src/Swift.Bindings/src/Emitter/ModuleDatabaseEmitter.cs` — XML serialization
- `src/Swift.Bindings/src/TypeDatabase/TypeDatabase.cs` — XML deserialization
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/ConstructorWrapperEmitterTests.cs` (36 tests)
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/ConstructorHandlerOutputTests.cs` (20 tests)
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/ModuleDatabaseEmitterTests.cs` (2 round-trip tests)

**After both fixes**: Rebuild SDK package, delete `swift-binding.stamp` in library obj dirs, rebuild libraries, then revalidate on device.

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

**Status**: Done — unblocked by #14 fix, pending device revalidation
**Severity**: High

**Investigation findings**:
- LottieColor is a non-frozen struct with constructor `init(r:g:b:a:denominator:)` (5 params, not 4 as originally reported — `denominator` has a default value)
- Generator correctly uses `SwiftIndirectResult` for non-frozen struct return
- Crash is in NativeAOT runtime's handling of CallConvSwift with large indirect results

**Fix**: Same constructor `@_cdecl` wrapper as #10. The generated wrapper writes the result to an `IntPtr` buffer via `resultPtr.initializeMemory(as:)`, and C# uses `CallingConvention.Cdecl`.

**Pending**: Device revalidation with rebuilt SDK. #14 fix (ConstructorHandler dispatch + ~Copyable guard hardening) is complete in source.

---

### 13. XML Documentation on Generated Types (Issue 9)

**Status**: No code change needed — already implemented
**Severity**: Low — discoverability improvement

The ISSUES.md reported no XML docs, but the generator already has a complete pipeline: symbol graph extraction (`SymbolGraphExtractor`), structured parsing (`SymbolGraphDocParser`), AST attachment via USR keys, and emission (`XmlDocCommentEmitter`) of `/// <summary>`, `<param>`, `<returns>`, and `<remarks>` on all public types, methods, properties, and enum cases. The old NuGet packages were built before this existed.

**Files**: `XmlDocCommentEmitter.cs`, `SymbolGraphDocParser.cs`, `SymbolGraphExtractor.cs`, `DocComment.cs`

---

## Device Test Results

### Round 5 (Current) — Lottie constructor wrapper validation

Built Lottie test app with dev SDK (`SwiftBindings.Sdk/0.0.0-dev`, `SwiftBindings.Runtime/0.0.0-dev`) and deployed to iPhone 13 (arm64, iOS 26.3.1).

**Result**: SIGSEGV on LottieColor constructor. Crash stack confirms `PInvoke_init_5C5A3370` calling directly into Lottie with `CallConvSwift` — no `@_cdecl` wrapper was generated.

Phase 1 (smoke tests) and Phase 2 (library tests) passed. Phase 3 (constructor tests) crashed:
- `LottieAnimation.Filepath` — FAIL (`LottieSwiftBindings` DllNotFoundException — wrapper xcframework compilation failed)
- `LottieColor(1.0, 0.0, 0.0, 1.0)` — SIGSEGV in `PInvoke_init_5C5A3370`
- `LottieAnimationView(CGRect)` — not reached (crash in prior test)

Investigation led to discovery of #14 (ConstructorHandler dispatch gap + bogus ~Copyable guard).

### Round 4

#### Nuke: 8/8 passed — fully functional
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

#### Lottie: 4/4 passed, 2 blocked
| Test | Result | Blocker |
|------|--------|---------|
| LottieConfiguration.Shared | PASS | |
| DecodingStrategy enum | PASS | |
| LottieLoopMode cases | PASS | |
| LottieAnimation.Filepath | PASS | |
| LottieAnimationView playback | BLOCKED | #14 — constructor wrapper not generated |
| LottieColor construction | BLOCKED | #14 — constructor wrapper not generated |
