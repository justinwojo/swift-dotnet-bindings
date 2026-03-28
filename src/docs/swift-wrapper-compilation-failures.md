# Swift Wrapper Compilation Failures

**Status**: 49/56 passing (7 failures)
**Updated**: 2026-03-27
**Target**: 56/56 (all passing)

## Current State

After the session 7 review fixes, swift wrapper compilation improved from 40/56 to 49/56. The 7 remaining failures fall into 4 root cause categories.

## Failure Categories

### Category 1: MCB (Method Callback) Redeclarations — Alamofire, GRDB, Kingfisher

**Error**: `invalid redeclaration of '_sbw_mcb_*'`

Multiple `@_cdecl` wrapper functions end up with the same symbol name. This happens when overloaded Swift methods map to the same MCB callback function name after the generator's naming/hashing logic runs.

| Library | MCB Redeclarations | Other Errors |
|---------|-------------------|--------------|
| Alamofire | 8 | 0 |
| GRDB | 26 | 2 (EveryProtocol conformance) |
| Kingfisher | 14 | 0 |

**Examples** (Alamofire):
- `_sbw_mcb_response` — multiple `response()` overloads on DataRequest/DownloadRequest
- `_sbw_mcb_responseData`, `_sbw_mcb_responseString`, `_sbw_mcb_responseJSON`

**Root cause**: The MCB function naming in `MethodCallbackEmitter` doesn't disambiguate overloaded methods across different parent types or with different closure signatures. The hash/name collides.

**Fix approach**: Improve MCB function name deduplication — include parent type or a disambiguating hash of the full method signature.

**BindingTests needed**: Add overloaded methods with closure callbacks on different classes that would produce the same MCB name. Verify the generated Swift wrapper compiles.

### Category 2: Incomplete EveryProtocol Conformance — GRDB

**Error**: `type 'EveryProtocol' does not conform to protocol 'X'`

Two GRDB protocols have methods that the generator emits into the EveryProtocol conformance, but the emitted signatures don't match what the protocol actually requires.

| Protocol | Issue |
|----------|-------|
| `RowAdapter` | Only `addingScopes(_:)` emitted, but protocol has additional requirements (e.g., `layoutAdapter(from:)`) that the generator doesn't see or skips |
| `FTS5Tokenizer` | Closure stub emitted for `tokenize(context:...)`, but the protocol likely has additional requirements beyond what was emitted |

**Root cause**: The generator's ABI JSON parsing may not capture all protocol requirements, or some requirements are being silently skipped without generating stubs.

**Fix approach**: Audit the EveryProtocol conformance emission to ensure ALL protocol requirements are emitted (either as vtable dispatch, closure stubs, or generic stubs). Any requirement the generator can't handle should get a `fatalError()` stub rather than being silently omitted.

**BindingTests needed**: Add a protocol with a mix of emittable and non-emittable requirements (e.g., associated type methods, complex generic constraints). Verify the conformance compiles even when some methods are stubbed.

### Category 3: Internal Type References — SkeletonView

**Error**: `module 'SkeletonView' has no member named 'SkeletonCollectionDataSource'`

The generated wrapper references types that are internal to the library (not public). The post-processor strips functions referencing internal types, but some slip through — particularly in generic contexts where the type name appears in a type parameter position.

| Detail | Value |
|--------|-------|
| Internal types stripped | 54 |
| Remaining errors | ~4 (SkeletonCollectionDataSource references in generic contexts) |

**Root cause**: The internal type stripping regex doesn't catch type references inside generic parameter positions (e.g., `Foo<SkeletonCollectionDataSource>`).

**Fix approach**: Improve the `SwiftWrapperPostProcessor.ReferencesInternalType()` method to detect internal type names inside generic parameters, not just as standalone type references.

**BindingTests needed**: Add a type that references an internal type inside a generic parameter (e.g., `func process<T: InternalProtocol>(...)`). Verify the post-processor strips it.

### Category 4: Build Environment / Framework Issues — Quick, TinyConstraints, StripePaymentSheet

These failures are not generator bugs — they're caused by the libraries' build requirements.

#### Quick
**Error**: `'XCTest/XCTest.h' file not found`

Quick depends on `XCTest.framework`, which is only available in the Xcode test host environment. The generator compiles against the simulator SDK which doesn't include XCTest headers.

**Fix approach**: Pass `-framework XCTest` and the appropriate `-F` search path pointing to the Xcode developer platforms directory. Or skip Quick from wrapper compilation (it's a test framework, not a runtime dependency).

**BindingTests needed**: None — this is an infrastructure issue, not a generator bug.

#### TinyConstraints
**Error**: `unsupported Swift architecture`

The TinyConstraints xcframework was built for `x86_64-simulator` only (no `arm64-simulator` slice). The generator targets `arm64-apple-ios*-simulator`, so the ObjC bridging header fails.

**Fix approach**: Rebuild the TinyConstraints xcframework with arm64-simulator support, or handle architecture mismatches gracefully in the generator.

**BindingTests needed**: None — this is a library build issue.

#### StripePaymentSheet
**Errors**:
- `invalid redeclaration of '_sbw_mcb_create'` (1 MCB redeclaration — same as Category 1)
- `call to main actor-isolated method in a synchronous nonisolated context` (3 actor isolation errors)

The actor isolation errors occur because the generator emits `@_cdecl` wrapper functions that call `@MainActor`-isolated methods without being on the main actor.

**Fix approach**: The MCB redeclaration is the same fix as Category 1. The actor isolation errors require emitting `@MainActor` on the wrapper function or dispatching to the main actor inside the wrapper.

**BindingTests needed**: Add a `@MainActor` class with methods that get `@_cdecl` wrappers. Verify the wrapper compiles with proper actor isolation.

## Fix Priority

| Priority | Category | Libraries | Impact | BindingTests |
|----------|----------|-----------|--------|--------------|
| **P1** | MCB redeclarations | Alamofire, GRDB, Kingfisher (+StripePaymentSheet) | Fixes 3-4 libraries | Overloaded MCB methods across types |
| **P2** | EveryProtocol incomplete conformance | GRDB | Fixes remaining GRDB errors | Protocol with mixed emittable/non-emittable requirements |
| **P2** | Internal type in generics | SkeletonView | Fixes 1 library | Internal type inside generic parameter |
| **P3** | Actor isolation | StripePaymentSheet | Fixes 1 library | @MainActor class with @_cdecl wrappers |
| **P3** | XCTest dependency | Quick | Fixes 1 library | Infrastructure fix (no BindingTests) |
| **P3** | Architecture mismatch | TinyConstraints | Fixes 1 library | Library rebuild (no BindingTests) |

## Test Coverage Gaps

### Multi-protocol optional existential (`ExistentialContainer2+`)

The Optional existential getter fix (session 7) uses the projected container type for buffer allocation (`ExistentialContainer1`, `ExistentialContainer2`, etc.) instead of hardcoding `ExistentialContainer1`. The code path is correct but **no test exercises `ExistentialContainer2+`** — all current tests use single-protocol optionals (`(any Renderable)?`).

**Risk**: If a library has a property like `var combined: (any P & Q)?`, the allocation would use `ExistentialContainer2` (48 bytes) instead of `ExistentialContainer1` (40 bytes). The code handles this correctly via `(innerProjection as ExistentialProjection)?.PInvokeType`, but a bug in the projection factory or a fallback to the default `"ExistentialContainer1"` would silently corrupt memory.

**BindingTests needed**: Add a protocol composition property (`(any ProtocolA & ProtocolB)?`) to `OptionalExistentialProperties.swift` and corresponding getter/setter runtime tests. This would exercise `ExistentialContainer2` allocation and verify round-trip marshalling.

## Completion Criteria

Each fix is complete when:
1. The generator change is implemented with unit tests
2. **BindingTests coverage exists** — Swift source + C# runtime tests in `BindingTests/` that exercise the specific pattern, serving as a permanent regression gate
3. The affected validation library passes swift wrapper compilation
4. `validate-libraries.sh` shows improvement (no regressions)

Target: **56/56** swift wrapper compilation (or as close as possible — Quick and TinyConstraints may remain as known infrastructure limitations).
