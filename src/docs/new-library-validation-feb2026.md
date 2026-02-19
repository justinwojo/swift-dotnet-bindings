# New Library Validation Results — February 2026

**Date**: February 19, 2026
**Initial Git SHA**: cc77c82 (main)
**Script**: `validate-libraries-new-only.sh`

---

## Session 1 — Completed

All Session 1 fixes implemented and validated. Existing 25-library suite: 25/25 at 0 errors, no regressions. Unit tests: 3,428 passing (+9 new).

| Fix | Library | Status | Details |
|-----|---------|--------|---------|
| 1 | SwiftCollections | **Done** (pre-session) | Script path fix (`OrderedCollections.xcframework`) |
| 2 | SwiftProtobuf, Moya | **Done** | Positive check for `"dynamically linked shared library"` in `VerifyDynamicLibrary`. Clear error: "static library or object file, not a dynamic library" |
| 3 | Starscream | **Partial** | `Foundation.Stream → NSStream`, `Foundation.Stream.Event → NSStreamEvent` added to TypeDatabaseExtensions. 4 residual errors: `SwiftDictionary<SwiftString, SwiftString>` — pre-existing limitation (SwiftString keys not projected) |
| 4 | SnapKit (Issue B) | **Done** | `UIKit.UIUserInterfaceLayoutDirection` added to `AppleFrameworkValueTypes`. Down from 6 → 1 error |
| 5 | Kingfisher | **Done** | `IsBoundGeneric` guard before `TranslateBoundGenericTypeToCSharp` in `EnumHandler.CaseConstruction.cs`. 46,825 lines generated without crash. Wrapper compile failure is pre-existing (internal Kingfisher types) |
| 6 | GRDB | **Done** | `_processingInProgress` HashSet cycle detection in `ModuleProcessor.ProcessTypeRecursively`. 77,795 lines generated without stack overflow. Wrapper compile failure is pre-existing |

### Files Changed (Session 1)

| File | Fixes |
|------|-------|
| `src/Swift.Bindings/src/Configuration/XCFrameworkResolver.cs` | 2 |
| `src/Swift.Bindings/src/TypeDatabase/TypeDatabaseExtensions.cs` | 3, 4 |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumHandler.CaseConstruction.cs` | 5 |
| `src/Swift.Bindings/src/Parser/ModuleProcessor.cs` | 6 |
| `tests/.../ConfigurationTests/XCFrameworkResolverTests.cs` | Theory with 3 static binary patterns |
| `tests/.../TypeDatabaseTests/TypeDatabaseExtensionsTests.cs` | InlineData for Stream, StreamEvent, UIUserInterfaceLayoutDirection |
| `tests/.../EmitterTests/EnumCaseAssociatedValueTests.cs` (new) | 3 tests for bound generic guard |
| `tests/.../ParserTests/ModuleProcessorCycleTests.cs` (new) | 2 tests for cycle detection |

---

## Session 2 — Remaining Work

### Fix 7: RxSwift — Protocol name collision with `System.IDisposable` (132 errors)

**Problem**: RxSwift defines `Disposable` protocol with `dispose()` and `disposed(by:)`. Generator projects as `IDisposable` which collides with `System.IDisposable`.

**Error patterns**:
- CS0535 (~100): Missing interface members
- CS0111 (~20): Duplicate `Dispose` method
- CS0528 (~10): Duplicate `IDisposable` in interface list
- CS0234 (2): `Foundation.OperationQueuePriority` not mapped

**Fix approach options**:
1. **(Recommended)** Add `Disposable` to `_runtimeProtocols` in `NameProvider.GetInterfaceName` → becomes `ISwiftDisposable`. Simple, ~20 lines. Impact: protocol emission, conformance lists, proxy classes.
2. General collision detection against `System` namespace types (more work, broader coverage).
3. Module-prefix for all non-stdlib protocols (over-broad).

**Secondary**: Add `Foundation.OperationQueuePriority` to `AppleFrameworkValueTypes` (same pattern as Fix 4).

**Effort**: 1.5-2 hours.

### Fix 8: SnapKit Issue A — Missing proxy class for protocol return types (1 error)

**Problem**: `LayoutConstraintItem` protocol has no proxy class (no implementable members), but a property getter returns this type and emits `new LayoutConstraintItemProxy(result)`. Proxy class was never generated.

**Fix approach options**:
1. Emit a minimal proxy class even for protocols with no implementable members (contains just handle + constructor).
2. Skip the wrapping — use `IntPtr` or the interface type directly.
3. Check for proxy class existence at return-value emission and fall back.

**Scope**: Check how many protocols across all 34 validated libraries hit this pattern. Touches return-value marshalling in `MethodHandler` / `PropertyHandler`.

**Effort**: 1-1.5 hours.

### Starscream residual: `SwiftDictionary<SwiftString, SwiftString>` (4 errors)

**Problem**: Dictionary projection emits `SwiftDictionary<SwiftString, SwiftString>` but `SwiftDictionary` requires projected key types (`string`, not `SwiftString`). The `FromDictionary`/`ToDictionary` methods don't exist on `SwiftDictionary<SwiftString, SwiftString>`.

**Fix approach**: When dictionary key type is `SwiftString`, project to `string` in the dictionary type parameter (consistent with how array element types are projected). Likely in `TypeConversionHandler.IsSwiftDictionary()` or the dictionary branch of `WrapperEmitter.Marshalling.cs`.

**Effort**: 1 hour.

### Kingfisher & GRDB residual: Wrapper compilation failures

These generate correct C# but the Swift wrapper can't compile because it references types internal to the library. Same pattern as Alamofire/SkeletonView/Mixpanel in the existing 25-library suite. Not a generator bug — would need Swift wrapper to conditionally skip internal-type wrappers.

**Effort**: Deferred (not targeted for Session 2).

### Session 2 validation plan

1. Implement fixes 7 + 8 + Starscream residual
2. Run `./run-tests.sh | tail -20` — baseline 3,428+
3. Run `./validate-libraries.sh --compile-only | tail -30` — baseline 25/25
4. Spot-check all 9 new libraries
5. Target: 5/9 clean (KeychainAccess + SwiftCollections + SnapKit + RxSwift + Starscream), 2 static (SwiftProtobuf, Moya — correct behavior), 2 wrapper-only failures (Kingfisher, GRDB — correct C#)

---

## Summary

9 new open-source Swift libraries tested against the binding generator.

| Library | Stars | Initial Result | Post-Session-1 | Remaining |
|---------|-------|---------------|-----------------|-----------|
| **KeychainAccess** | 8k+ | **PASS** (0 errors) | **PASS** | — |
| **SwiftCollections** | 4k+ | Script bug | **PASS** | — |
| **SwiftProtobuf** | 5k+ | Generator fail | **Static lib detected** | Expected behavior |
| **Moya** | 15k+ | Generator fail | **Static lib detected** | Expected behavior |
| **SnapKit** | 20k+ | 6 errors | **1 error** | Issue A: missing proxy class (Session 2) |
| **Starscream** | 8k+ | 4 errors | **4 errors** (different) | SwiftDictionary<SwiftString,SwiftString> (Session 2) |
| **Kingfisher** | 23k+ | Generator crash | **Wrapper fail** | Wrapper compile (deferred) |
| **GRDB** | 7k+ | Generator crash | **Wrapper fail** | Wrapper compile (deferred) |
| **RxSwift** | 24k+ | 132 errors | **132 errors** | Protocol name collision (Session 2) |

---

## Detailed Failure Analysis

### 1. Kingfisher — Generator Crash (`NotSupportedException`) — FIXED

**Category**: Generator bug
**Exception**: `System.NotSupportedException: Attempted to translate to C# name for a non-bound generic property _temp`
**Location**: `BoundGenericsHandler.TranslateBoundGenericTypeToCSharp` → `EnumHandler.GetCSharpTypeNameForEnumCase`

**Root cause**: An enum case has associated values containing a type with `ContainsGenericParameters=true` but that isn't a bound generic (e.g., `UnsafePointer<T>`). The `BoundGenericsHandler` doesn't handle pointer types and threw.

**Fix**: Added `IsBoundGeneric` check at the call site in `EnumHandler.CaseConstruction.cs`. Non-bound generics fall back to `GetTypeRecordOrAnyType`. Generator now produces 46,825 lines without crash.

---

### 2. GRDB — Generator Crash (`StackOverflowException`) — FIXED

**Category**: Generator bug
**Exception**: `Stack overflow.`
**Location**: `ModuleProcessor.ProcessTypeRecursively` → `ProcessStructProperties` → `ProcessTypeRecursively` (infinite recursion)

**Root cause**: Struct A has a property of type B, and struct B has a property of type A. Types are only registered in `_moduleDatabase` after `ProcessStructProperties` returns, so the `IsTypeProcessed` guard fails to detect the cycle.

**Fix**: Added `_processingInProgress` HashSet field to `ModuleProcessor`. `ProcessTypeRecursively` adds the type's `ModuleQualifiedName` before processing and removes it in a `finally` block. Generator now produces 77,795 lines without stack overflow.

---

### 3. SwiftProtobuf & Moya — Static Library Detection — FIXED

**Category**: Input limitation (not a generator bug)

**Root cause**: Both xcframeworks contain static libraries. `VerifyDynamicLibrary` checked for `"current ar archive"` and `"object file"` but SwiftProtobuf's binary outputs `"Mach-O 64-bit object arm64"` — the word "object" appears without "file" following it.

**Fix**: Replaced negative pattern checks with a positive check: if `file` output does not contain `"dynamically linked shared library"`, reject it. Error message now reads: "static library or object file, not a dynamic library."

---

### 4. SwiftCollections — Script Path Mismatch — FIXED (pre-session)

**Category**: Script bug (not a generator issue)

The library directory contains `OrderedCollections.xcframework`, not `SwiftCollections.xcframework`. Script path updated.

---

### 5. RxSwift — 132 Compile Errors (Session 2)

**Category**: Generator bug — protocol name collision with .NET BCL
**Generated**: 11,822 lines

RxSwift defines `Disposable` protocol → generator projects as `IDisposable` → collides with `System.IDisposable`.

**Secondary**: `Foundation.OperationQueuePriority` needs ObjC mapping.

---

### 6. SnapKit — 1 Compile Error (Session 2)

**Category**: Generator bug — missing proxy class for protocol return types
**Generated**: 9,444 lines

Issue B (ObjC enum) fixed in Session 1. Remaining Issue A: `LayoutConstraintItem` protocol has no proxy class but return-value code emits `new LayoutConstraintItemProxy(result)`.

---

### 7. Starscream — 4 Compile Errors (Session 2)

**Category**: Pre-existing limitation — SwiftDictionary key type projection
**Generated**: 15,018 lines

Foundation.Stream/StreamEvent mapping fixed in Session 1. Remaining: `SwiftDictionary<SwiftString, SwiftString>` — dictionary projection doesn't handle `SwiftString` keys (needs `string` projection).

---

## Bug Pattern Summary

| Pattern | Libraries Affected | Status |
|---------|-------------------|--------|
| Static library xcframework not supported | SwiftProtobuf, Moya | **Fixed** — clear error message |
| Unhandled bound generic in enum associated values | Kingfisher | **Fixed** — IsBoundGeneric guard |
| Recursive struct type processing (stack overflow) | GRDB | **Fixed** — cycle detection |
| ObjC C-enum treated as NSObject | SnapKit | **Fixed** — registry addition |
| ObjC-bridged type name not mapped to .NET name | Starscream | **Fixed** — NSStream/NSStreamEvent |
| Protocol name collision with .NET BCL types | RxSwift | Session 2 |
| Missing proxy class for protocol return types | SnapKit | Session 2 |
| SwiftDictionary key type projection | Starscream | Session 2 |

---

## Comparison with Existing Validation

The existing 25-library validation suite (`validate-libraries.sh`) passes at 0 compile errors post-Session-1, no regressions. These 9 new libraries exercise patterns not well-represented in the existing suite:

- **Protocol-heavy designs** (RxSwift) — name collision with .NET BCL
- **Deeply recursive type hierarchies** (GRDB) — stack overflow in processing
- **Complex generic enums** (Kingfisher) — unresolved bound generics in tuple positions
- **Static library distribution** (SwiftProtobuf, Moya) — not dynamic-library xcframeworks
- **Heavy ObjC interop** (SnapKit, Starscream) — Foundation/UIKit bridged type edge cases
