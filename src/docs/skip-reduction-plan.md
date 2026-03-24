# Skip Reduction Plan

**Status**: 920 passing, 78 skipped (as of 2026-03-24)
**Previous**: 897 passing, 101 skipped
**This session**: +23 real tests unskipped by removing stale attributes after thunk migration

This document catalogs all 78 remaining skipped runtime tests, categorized by fix path. Use this to plan future sessions.

---

## Tier 1: Clear path, proven fix (30 tests)

These have been prototyped or partially validated. Each needs a focused session.

### URL/URLRequest P/Invoke Refactoring (16 tests)

**Tests**: All 16 in `URLRequestTests.cs`

**What we proved this session**: All 16 pass on iOS 26 simulator when:
1. P/Invoke params use `SwiftString.Buffer` (blittable) instead of `SwiftString`
2. P/Invoke returns use `SwiftString.Buffer` in registers (not `SwiftIndirectResult`) for String/Optional<String>
3. Instance method P/Invokes use `SwiftSelf` + `DangerousGetHandle()` instead of passing `URL`/`SafeHandle` directly
4. Entry points updated for swift-foundation ABI (borrowing `SSh`, back-reference `B0`, new `init(url:cachePolicy:timeoutInterval:)`)

**Why we reverted**: Codex review identified three P1 issues:
- Entry points hardcoded to iOS 26 swift-foundation — no fallback for older runtimes
- String return ABI assumes arm64 (16B = 2 registers) — untested on x64 simulator/macOS
- `Swift.Runtime.csproj` advertises iOS 15+ / macOS 12+ support

**What the fix session needs**:
1. Availability probing: check if new symbols exist at runtime, fall back to old symbols
2. Architecture guard: verify String returns work the same on x64 (or use `SwiftIndirectResult` there)
3. Test on both arm64 simulator and x64 macOS
4. Entry point mapping:
   - `URL.init?(string:)`: old `$s10Foundation3URLV6stringACSgSS_tcfC` → new `$s10Foundation3URLV6stringACSgSSh_tcfC`
   - `URL.isFileURL`: old `$s10Foundation3URLV9isFileURLSbvg` → new `$s10Foundation3URLV06isFileB0Sbvg`
   - `URL.init(filePath:)`: old `init(filePath:isDirectory:)` removed → new `init(filePath:directoryHint:relativeTo:)` with enum param
   - `URLRequest.init(url:)`: old `init(url:)` removed → new `init(url:cachePolicy:timeoutInterval:)` with 2 extra params (0, 60.0 for defaults)
   - All URLRequest method symbols unchanged

### Parameter Test Stubs (10 tests)

**Tests**: All 10 in `ParameterTests.cs` (4 inout, 3 default, 3 variadic)

**What we proved this session**: The generator already emits working P/Invokes for all 10. When unskipped with empty bodies, the P/Invoke layer doesn't crash — these genuinely work at the calling convention level.

**What the fix session needs**: Write real test bodies. The generated bindings are:
- `TestLibFunctions.IncrementValue(ref int value)` — CallConvSwift with `ref int` (blittable)
- `TestLibFunctions.SwapValues(ref int a, ref int b)` — two `ref` params
- `TestLibFunctions.IncrementPoint(ref ValuePoint point)` — `ref` on struct (check if struct is blittable)
- `TestLibFunctions.DoubleInPlace(ref int value)` — `ref` with return value
- Default param tests: check if generator emits overloads or single method with all params
- Variadic tests: check if generator emits `IEnumerable<T>` or suppresses

### Crashes Needing Repro Investigation (4 tests)

These crash on Mono simulator but have @_cdecl wrappers — could be our marshalling bug, not upstream.

**KeywordTest SIGSEGV** (1 test: `EdgeCaseTests.TestKeywordTestCreation`)
- Has cdecl wrapper `SBW_SwiftBindingsTestLib_KeywordTest_init_AF4C9CD0`
- Takes 4 `SwiftString.Buffer` params — all blittable
- SIGSEGV on call, not `EntryPointNotFoundException`
- **Repro approach**: Create minimal KeywordTest in `/Users/wojo/Dev/swift-interop-repro/` with a Swift struct taking 4 String params via @_cdecl, call from C#. If it crashes, it's a Mono issue with 4+ string params. If it works, our wrapper has a bug.

**Existential Boxing Crash** (2 tests: `ConstructorParamTests.TestProtocolExistentialParam*`)
- Has cdecl wrapper with `ExistentialContainerFactory.GetOrCreate<IDescribable>()`
- Crashes with Mono JIT `!ji->async` assertion
- **Repro approach**: Create minimal protocol + conforming class, pass as existential container to @_cdecl wrapper. Isolate whether it's the container boxing or the P/Invoke call itself.

**Tuple+String Return Crash** (1 test: `ReturnPathTests.TestPairMakerTupleReturn`)
- Has cdecl wrapper `SBW_SwiftBindingsTestLib_Free_makePair_4F67B55A` returning `(Int32, String)` tuple
- Uses indirect result buffer, marshals elements
- SIGSEGV when reading SwiftString element from tuple buffer
- **Repro approach**: Create @_cdecl returning `(Int32, String)` tuple, unmarshal in C#. Check if the tuple buffer layout matches what the C# marshalling expects (especially the String offset within the tuple).

---

## Tier 2: Generator features needed (21 tests)

These require generator/emitter changes, not just test or runtime fixes.

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

## Tier 3: Known limitations (27 tests)

These are blocked by external factors or fundamental constraints. No near-term fix path.

### Upstream Mono JIT Async Assertion — Issue 1 (9 tests)

**Tests**: All 9 in `AsyncFactoryMethodTests.cs`

**Status**: Confirmed upstream Mono bug. P/Invoke calls inside async continuations crash with `!ji->async` assertion. All 9 use CallConvCdecl (thunks work), but the async continuation triggers the Mono bug. These pass on NativeAOT device.

### Nullable<struct> Return Marshalling (2 tests)

**Tests**: `HierarchyInspectionTests.TestConvertPointInvalidKeypath`, `TestConvertRectInvalidKeypath`

**Status**: Mono returns `HasValue=true` for `.none` optional struct returns. The P/Invoke itself works (CallConvCdecl), but the Nullable<CGPoint>/Nullable<CGRect> unmarshalling is wrong on Mono. Needs investigation — could be our marshalling or Mono's.

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

1. **Parameter test bodies** (10 tests, ~30 min) — Quick win, no risk, just write test implementations
2. **Repro investigation** (4 tests, ~1-2 hrs) — KeywordTest SIGSEGV, existential crash, tuple crash in `/Users/wojo/Dev/swift-interop-repro/`
3. **URL/URLRequest** (16 tests, ~2 hrs) — Proven fix, needs availability probes + x64 validation
4. **Closure SB0001** (6 tests, ~2-3 hrs) — Generator work in ClosureEmitter
5. **~Copyable wrappers** (11 tests, ~3-4 hrs) — Generator work for consuming/borrowing semantics
