# Skip Reduction Plan

**Status**: 928 passing, 70 skipped (as of 2026-03-24)
**Previous**: 920 passing, 78 skipped
**This session**: +8 tests unskipped (7 parameter tests + 1 tuple return fix)

This document catalogs all 70 remaining skipped runtime tests, categorized by fix path. Use this to plan future sessions.

---

## Completed This Session

### Parameter Test Stubs → 7 unskipped, 3 remain (variadic)

- **4 inout tests**: All pass via CallConvSwift. Public API passes by value (mutations not visible to caller), but P/Invoke path works without crash.
- **3 default param tests**: All pass via CallConvCdecl @_cdecl wrappers. Verified with/without explicit args.
- **3 variadic tests**: Remain skipped — generator does not emit variadic bindings (ABI JSON represents `T...` as `Array<T>`, neither @_cdecl nor CallConvSwift can dispatch correctly).

### Tuple+String Return Crash → FIXED (1 test unskipped)

**Root cause**: `WrapperEmitter.Return.cs` `EmitCdeclTupleReturn` read String elements as 8-byte `IntPtr` (dereference) instead of computing the 16-byte address. The Swift wrapper writes tuples inline via `initializeMemory(as:)`, not heap-allocated.

**Fix**: In Phase 1 of `EmitCdeclTupleReturn`, String/Data elements now compute `(IntPtr)((byte*)resultPtr + offset)` (address-of) instead of `*(IntPtr*)((byte*)resultPtr + offset)` (dereference). Phase 2 `MarshalFromSwift<SwiftString>` correctly reads all 16 bytes from the address.

### Repro Investigation → 3 patterns classified

Repro project: `/Users/wojo/Dev/swift-interop-repro/` (3 new test classes added)

| Pattern | Verdict | Detail |
|---------|---------|--------|
| **KeywordTest SIGSEGV** | OUR BUG | 4 `SwiftString.Buffer` structs (16 bytes each) exceed 8 GPR slots on arm64. AAPCS64 puts 4th struct on stack, but @_cdecl expects `x7` + stack split. Fix: decompose Buffer into `nint` pairs in P/Invoke. |
| **Existential boxing** | OUR BUG | Repro proved @_cdecl pathway works when container is correctly built. Our `ExistentialContainerFactory.GetOrCreate` has a layout or boxing bug. |
| **Tuple+String return** | OUR BUG (FIXED) | See above. |

---

## Tier 1: Clear path, proven fix (19 tests remaining)

### URL/URLRequest — ObjC Bridge Projection (16 tests)

**Tests**: All 16 in `URLRequestTests.cs`

**Root cause**: The hand-written `Swift.URL` and `Swift.URLRequest` runtime types have two independent problems:
1. Non-blittable types (`SwiftString`, `URL` SafeHandle) in `CallConvSwift` P/Invokes — Mono rejects these
2. Four Foundation entry points changed on iOS 26 (swift-foundation rewrite) — symbols don't exist

**New approach**: Eliminate the hand-written runtime types entirely. The generated bindings already expose `Foundation.NSUrl` / `Foundation.NSUrlRequest` to consumers — `Swift.URL` is just an internal marshalling intermediary. Instead, have the @_cdecl wrappers accept ObjC class pointers (`AnyObject` = IntPtr, always blittable) and let Swift do the bridging (`nsUrl as URL`). No Foundation entry points needed, no iOS version sensitivity.

**Design doc**: `src/docs/objc-bridge-projection-design.md` — full design, implementation plan, generator entry points to modify, and verification steps.

### KeywordTest ABI Fix (1 test)

**Test**: `EdgeCaseTests.TestKeywordTestCreation`

**Root cause** (confirmed via repro): C# P/Invoke passes `SwiftString.Buffer` as 16-byte structs. With 4 strings + resultPtr = 9 GPR values, the 4th struct (needing 2 GPR but only x7 available) goes entirely on stack per AAPCS64. But Swift @_cdecl expects individual `Int` params: x7 gets first word, stack gets second word.

**Fix**: Decompose `SwiftString.Buffer` P/Invoke params into individual `nint` pairs when emitting for @_cdecl wrappers that use the `_sW0_`/`_sW1_` pattern. This matches the C# ABI to the Swift @_cdecl signature exactly.

### Existential Container Fix (2 tests)

**Tests**: `ConstructorParamTests.TestProtocolExistentialParam*`

**Root cause** (confirmed via repro): The @_cdecl pathway (`repro_describeExistential`) works correctly when the existential container is manually constructed with correct object ptr + metadata + witness table. The crash is in our `ExistentialContainerFactory.GetOrCreate<IDescribable>()` — either the container layout doesn't match Swift's expectation, or the boxing code triggers a Mono JIT issue.

**Fix**: Investigate `ExistentialContainerFactory` container layout. Swift existential = 3 words value buffer + type metadata + protocol witness table. Verify our C# container matches this layout.

---

## Tier 2: Generator features needed (21 tests)

### ~Copyable / UniqueResource (11 tests)

**Tests**: 5 in `OwnershipTests.cs`, 2 in `OwnershipGCStressTests.cs`, 1 in `DisposeScopeTests.cs`, 1 in `NegativePathTests.cs`, 2 in `ClassMarshallingTests.cs`

**Root cause**: `createUniqueResource` free function has @_cdecl wrapper emitted, but wrapper body copies the `UniqueResource` value — which fails Swift compilation for `~Copyable` types. The wrapper is silently stripped.

**Fix approach**: Emit wrappers that use `consuming` or `borrowing` parameter passing for ~Copyable types. The wrapper needs to transfer ownership without copying: `consuming` takes ownership, `borrowing` borrows without copy.

### Closure SB0001 Fallbacks (6 tests)

**Tests**: 4 in `ClosureTests.cs` (Optional<Bool/Enum> params + Optional<String>/[String] returns), 2 extra inhabitant encoding tests

**Root cause**: Generator doesn't emit @_cdecl wrappers for closures with:
- `Optional<T>` closure parameters (Bool, Enum) — wrapper needs to marshal optional through heap allocation
- Non-primitive closure returns (String, [String]) — callback wrapper needs to return via heap pointer

**Fix approach**: Extend `ClosureEmitter` to emit @_cdecl callback wrappers for these patterns. The frozen struct closure param path already works (tested in this session) — extend the same pattern to optional and string types.

### Generic Multi-Param Specialization (1 test)

**Test**: `BasicGenericTests.TestGetPairSameType`

**Root cause**: `pair<T, U>()` generic free function with 2 type params — generator emits CallConvSwift fallback. No concrete @_cdecl specialization generated.

**Fix approach**: Extend generic specialization to handle 2+ type parameters. Currently works for single type param generics.

### Cross-Module Types (3 tests)

**Tests**: 3 in `CrossModuleTests.cs` (DependencyPoint, DependencyConfig, DependencyService)

**Root cause**: Types from `SwiftBindingsTestLibDependency` module aren't included in the main module's generated bindings. By design — would need multi-module binding coordination.

**Fix approach**: Generate bindings for the dependency module separately, then reference them. Or support `--framework-dependency` for test library bindings.

---

## Tier 3: Known limitations (30 tests)

These are blocked by external factors or fundamental constraints. No near-term fix path.

### Upstream Mono JIT Async Assertion — Issue 1 (9 tests)

**Tests**: All 9 in `AsyncFactoryMethodTests.cs`

**Status**: Confirmed upstream Mono bug. P/Invoke calls inside async continuations crash with `!ji->async` assertion. All 9 use CallConvCdecl (thunks work), but the async continuation triggers the Mono bug. These pass on NativeAOT device.

### Nullable<struct> Return Marshalling (2 tests)

**Tests**: `HierarchyInspectionTests.TestConvertPointInvalidKeypath`, `TestConvertRectInvalidKeypath`

**Status**: Mono returns `HasValue=true` for `.none` optional struct returns. The P/Invoke itself works (CallConvCdecl), but the Nullable<CGPoint>/Nullable<CGRect> unmarshalling is wrong on Mono. Needs investigation — could be our marshalling or Mono's.

### Variadic Parameters (3 tests)

**Tests**: 3 in `ParameterTests.cs` (sumAll, joinStrings, VariadicConsumer)

**Status**: Generator does not emit variadic bindings. ABI JSON represents `T...` as `Array<T>`, but neither @_cdecl nor CallConvSwift can dispatch variadic calls correctly.

### Test Infrastructure Gaps (11 tests)

These have empty test bodies or disabled Swift source. Not blocked by bugs — just need implementation.

| Test | Issue |
|------|-------|
| `LeakDetectionTests` (4 weak/unowned) | Swift types generated but test bodies empty. Need real weak ref cycle tests. |
| `ObjCInteropTests` (3 Selector) | Selector type not projected. Test bodies empty. |
| `EdgeCaseTests` (2 Unicode) | Swift source file disabled (`.disabled` extension). Need to enable and verify emitter handles unicode identifiers. |
| `LifetimeTrackingTests` (2 async) | Swift source functions commented out (`// public func asyncCreateObject`). Source disabled due to `_payload/this in static context` generator bug. |

### Other (5 tests)

| Test | Issue |
|------|-------|
| `EdgeCaseTests.TestDeprecationTestNormalMethod` | `DeprecationTest` type not emitted — investigate if `@available(*, deprecated)` suppresses generation |
| `WrapperStrippingTests.TestMixedEmittabilityOpaqueReturn` | `EntryPointNotFoundException` — CallConvSwift fallback for opaque `some CustomStringConvertible` return. Symbol not in dylib. |
| `WrapperStrippingTests.TestVariadicHolderSum` | `IEnumerable<int>` values lost in non-frozen struct — variadic init passes array but struct doesn't retain data (Sum returns 0) |
| `NonStandardEnumTests.TestPermissionCaseValues` | ABI JSON lacks non-Int32 enum raw values — generator emits sequential ordinals (0,1,2,3) instead of Swift values (0,1,2,4) |
| `LifetimeTrackingTests.TestOwnableProtocolConformance` | `some Ownable` opaque existential param — generator doesn't support this yet. Test body empty. |

---

## Priority Order for Future Sessions

1. **URL/URLRequest** (16 tests, ~2 hrs) — Proven fix, needs availability probes + x64 validation
2. **KeywordTest ABI fix** (1 test, ~1 hr) — Decompose Buffer into nint pairs in P/Invoke emitter
3. **Existential container fix** (2 tests, ~1-2 hrs) — Debug ExistentialContainerFactory layout
4. **Closure SB0001** (6 tests, ~2-3 hrs) — Generator work in ClosureEmitter
5. **~Copyable wrappers** (11 tests, ~3-4 hrs) — Generator work for consuming/borrowing semantics
