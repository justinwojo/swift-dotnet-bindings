# Swift Wrapper Compilation Failures

**Status**: 51/56 passing (5 failures)
**Updated**: 2026-03-28
**Target**: 56/56 (all passing)

## Current State

After the MCB dedup and FindBlockEnd fixes, swift wrapper compilation improved from 49/56 to 51/56. Alamofire and SkeletonView now pass. The 5 remaining failures fall into 4 root cause categories.

## Fixed (This Session)

### MCB Function Name Collision — Alamofire (FIXED)

**File**: `MethodClosureBridge.cs:297`

The MCB Swift wrapper function `_sbw_mcb_{method.Name}` was not unique across overloaded methods on different parent types. Two `response()` methods on `DataRequest` and `DownloadRequest` both emitted `_sbw_mcb_response`, causing a Swift redeclaration error.

**Fix**: Changed function name to `_sbw_mcb_{closures[0].CallbackBaseName}_{method.Name}`, where `CallbackBaseName` includes a deterministic hash of the method's mangled name (`MCB_{hash}`). The `@_cdecl` attribute already used this hash, so the Swift function name now matches its uniqueness.

### FindBlockEnd Multi-Line Signature — SkeletonView (FIXED)

**File**: `SwiftWrapperPostProcessor.cs:275-289`

`FindBlockEnd()` returned early for multi-line function signatures. When `@_silgen_name` was followed by a multi-line `func` declaration (no `{` on the decorator or signature lines), the condition `depth <= 0 && j > start` fired on line 2, before reaching the opening brace. This prevented the post-processor from stripping blocks that referenced internal types.

**Fix**: Added `sawOpenBrace` tracking. The method now only returns when at least one `{` has been seen and the depth returns to 0.

**Note**: The original doc attributed SkeletonView's failure to "internal type stripping regex doesn't catch type references inside generic parameter positions." Investigation disproved this — the `\b` word boundary regex correctly matches types inside `<>`. The actual root cause was `FindBlockEnd()` terminating early on multi-line signatures.

### SwiftResult Class Extraction Bug (FOUND & FIXED)

**File**: `SwiftResult.cs` — `Success` and `Failure` getters

While adding end-to-end runtime tests for the MCB fix, discovered a pre-existing bug in `SwiftResult<TSuccess, TFailure>`. The `Success`/`Failure` getters had two issues:

1. **Stack buffer ownership**: Used `stackalloc` for payload copies passed to `NewFromPayload`. For `ISwiftObject` types, `NewFromPayload` stores the pointer in `SwiftSafeHandle` which calls `NativeMemory.Free` on dispose — crashing on the stack pointer. Fixed by heap-allocating (same pattern as `SwiftOptional.Some`).

2. **Class pointer type confusion**: For true Swift classes stored in a Result payload, the payload bytes contain the class pointer at offset 0. The old code passed the buffer pointer to `NewFromPayload`, which stored it as the class handle (should be the class pointer *value* at that address). Fixed by dereferencing + `Arc.Retain` for the +1 ownership contract. Uses `TypeMetadata.Kind == TypeMetadataKind.Class` to distinguish true classes from complex enums (both implement `ISwiftObject` without `ISwiftStruct`).

The same class-pointer extraction pattern was hardened in `SwiftOptional.cs` and `SwiftArray.cs` with the metadata Kind guard.

## Remaining Failures

### Category 1: MCB + EveryProtocol — GRDB

**Errors**: MCB redeclarations (now fixed) + 2 EveryProtocol conformance errors

The MCB fix resolved GRDB's redeclaration errors, but 2 EveryProtocol conformance failures remain. Protocols `RowAdapter` and `FTS5Tokenizer` have invisible requirements (underscore-prefixed, not in symbolgraph/ABI JSON) that EveryProtocol can't satisfy.

**Fix approach**: Detect protocols with unsatisfied hidden requirements and skip the conformance, or use swiftinterface parsing to discover the full requirement set.

### Category 2: Internal Type References — Kingfisher

**Errors**: 4252 errors referencing internal types (`ImageModifier`, `CacheSerializer`, `KingfisherParsedOptionsInfo`, etc.)

Kingfisher's internal types appear in the ABI JSON and the generator emits wrappers for them. The post-processor strips functions referencing internal types, but the volume is too large for the current stripping approach to handle — there are structural references (protocol conformances, extension blocks) that go beyond individual function bodies.

**Fix approach**: Needs deeper investigation — possibly skip entire types flagged as internal in the ABI JSON at the generator level, rather than relying on post-processor stripping.

### Category 3: Build Environment / Framework Issues — Quick, TinyConstraints, StripePaymentSheet

These failures are not generator bugs — they're caused by the libraries' build requirements.

| Library | Error | Root Cause |
|---------|-------|------------|
| Quick | `'XCTest/XCTest.h' file not found` | Depends on XCTest.framework, only available in Xcode test host |
| TinyConstraints | `unsupported Swift architecture` | xcframework built for x86_64-simulator only, no arm64 slice |
| StripePaymentSheet | MCB (fixed) + actor isolation + missing `StripePayments` module | Inter-module dependency blocks compilation regardless of other fixes |

## Fix Priority

| Priority | Category | Libraries | Impact |
|----------|----------|-----------|--------|
| **P1** | Internal type volume | Kingfisher | Fixes 1 library |
| **P2** | EveryProtocol hidden requirements | GRDB | Fixes 1 library |
| **P3** | Actor isolation | StripePaymentSheet | Partially helps (still blocked by missing module) |
| **P3** | XCTest dependency | Quick | Infrastructure fix |
| **P3** | Architecture mismatch | TinyConstraints | Library rebuild issue |

## Test Coverage

### Multi-protocol optional existential (`ExistentialContainer2+`)

The Optional existential getter fix uses the projected container type for buffer allocation (`ExistentialContainer1`, `ExistentialContainer2`, etc.) instead of hardcoding `ExistentialContainer1`. No test exercises `ExistentialContainer2+`.

**BindingTests needed**: Add a protocol composition property (`(any ProtocolA & ProtocolB)?`) to `OptionalExistentialProperties.swift` and corresponding getter/setter runtime tests.

## Completion Criteria

Each fix is complete when:
1. The generator change is implemented with unit tests
2. **BindingTests coverage exists** — Swift source + C# runtime tests in `BindingTests/` that exercise the specific pattern
3. The affected validation library passes swift wrapper compilation
4. `validate-libraries.sh` shows improvement (no regressions)

Target: **56/56** swift wrapper compilation (Quick and TinyConstraints may remain as known infrastructure limitations).
