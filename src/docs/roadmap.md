# Roadmap

**Created**: February 2026
**Status**: Active — single source of truth for work items

For completed work (Phases A–G), see `CompletedPhases/phases-a-through-g.md`.
For detailed gap descriptions and contract matrix, see `testing-gaps.md`.
For deferred/aspirational work, see `Future/`.

---

## Current Baselines

| Metric | Value |
|--------|-------|
| Unit tests | 1753 passing |
| Integration tests | 699 passing (11 skipped, pre-existing) |
| TestFramework must-pass | 94/94 passing, 0 degraded |

| Library | Binding Errors | Simulator Tests | Notes |
|---------|---------------|-----------------|-------|
| **Lottie** | 0 | 15/15 passing | Clean |
| **BlinkID** | 0 | 13/13 passing | Modal presentation skipped (iOS async limitation) |
| **Nuke** | 0 | 15 safe + async passing | Async image load fully working end-to-end (wrapper path, BitwiseCopyable, ObjC marshalling) |
| **CryptoSwift** | 0 | 6 safe + 1 skip | Instance methods crash on CallConvSwift (see Phase I3) |
| **BridgeTest** | 0 | 35/35 passing | Clean |

---

## Phase H: Unit Test Gaps + Remaining Library Errors

**Status**: Done (H1 + H2)
**Priority**: High — eliminate remaining library errors
**Effort**: Medium (1-2 sessions)

### H1: Unit Test Coverage Gaps (Phase G fixes) — Done

Phase G fixed 8 generator bugs but 3 fixes lacked targeted unit tests. Added 5 regression tests.

### H2: Remaining Library Errors (6 distinct bugs, 12 total errors) — Done

Fixed 6 generator bugs eliminating all 12 remaining library binding errors (CryptoSwift 3→0, Nuke 1→0, Lottie 8→0). Added 12 regression tests.

| Bug | Library | Fix |
|-----|---------|-----|
| 1 | CryptoSwift | `PropertyHandler.cs` — TupleTypeSpec branch in `TranslateTypeSpecWithGenerics` |
| 2 | CryptoSwift | `EnumHandler.CaseConstruction.cs` — SimpleEnum check in `GetPInvokeArgument`/`GetPInvokeType` |
| 3 | CryptoSwift | `Receivers/Vtables/StaticInit/SwiftObject` — consistent `ProtocolSignatureHelper.GetMethodSignatureKey` dedup |
| 4 | Nuke | `WrapperEmitter.Return.cs` — `GetCSharpExistentialType()` for optional existential marshal type |
| 5 | Lottie | `WrapperEmitter.Async.cs` — exclude existentials from copy-buffer filter |
| 6 | Lottie | `WrapperEmitter.Return.cs` — `GetPublicExistentialType() == "object"` guard before proxy construction |

---

## Phase I: Mono JIT Mitigation — Wrapper Routing

**Status**: I1 done, I1a done, I1b done
**Priority**: High — unblocks core functionality for Nuke and CryptoSwift
**Effort**: Medium (2-3 sessions)
**Depends on**: Phase H
**Reference**: `Future/mono-jit-mitigation-and-nuke-loadimage-regression.md`

The Mono JIT on iOS does not fully support `CallConvSwift` for closures, non-blittable types, and certain instance method patterns. The generator already emits `@_cdecl` wrapper functions in the `SwiftBindings` framework for async methods — this phase extends that pattern to cover the remaining crash-prone signatures.

### I1. Fix Nuke test app: use wrapper-backed ImageAsync path — Done

Switched the test app from `LoadImage(request, callback)` (direct CallConvSwift — crashes) to `ImageAsync(request)` (wrapper-backed CallConvCdecl). The wrapper path correctly routes through the `SwiftBindings` framework.

### I1a. Fix BitwiseCopyable crash in async complex type returns — Done

The generated Swift wrapper used `storeBytes(of:as:)` which requires `BitwiseCopyable` in Swift 6+, crashing for class types like UIImage. Fixed by:
- **Class types**: `Unmanaged.passRetained().toOpaque()` + `storeBytes` on `UnsafeMutableRawPointer` (BitwiseCopyable)
- **Struct/enum types**: `withUnsafePointer + copyMemory` (raw bitwise copy, no BitwiseCopyable requirement, no extra retains)
- **Enum string raw values**: `withUnsafePointer + copyMemory` (same pattern for Optional<Enum> with String raw values)

Added 7 unit tests + 1 enum regression test.

### I1b. Add ObjC type marshalling in async complex type callbacks — Done

The async callback handler used `SwiftMarshal.MarshalFromSwift<T>()` which threw `NotSupportedException` for ObjC-bridged types (UIImage). Fixed by:
- **TypeDatabase**: Expanded ObjC type detection from just ObjectiveC/Foundation root classes to all Apple framework modules (UIKit, AppKit, CoreImage, AVFoundation, etc.). Types from these modules get synthetic ObjCBridged records with correct C# namespace mapping.
- **TypeDatabase XML parsing**: Fixed `ReadVersion1_0()` to parse the `kind` attribute from XML (`class`/`enum`/`struct`) — was hardcoded to `Struct`, which caused `isClassType` checks to fail for ObjC-bridged class types even when `objcBridged="true"` was set.
- **Emitter**: `EmitAsyncWrapperForComplexType` now checks `isObjCBridged` and emits `GetNSObject<T>()` instead of `MarshalFromSwift<T>()`. For ObjC-bridged types, `Arc.Release` is skipped in the finally block — `GetNSObject<T>()` takes ownership of the `passRetained` reference (releasing would cause use-after-free).
- **Runtime**: Added NSObject subclass fallback in `SwiftMarshal.MarshalFromSwift<T>()` as defense-in-depth (Apple platforms only).

Added 20 unit tests for Apple framework type detection + 3 emitter tests for async ObjC callback generation + 3 XML Kind parsing regression tests.

### I2. Generator: auto-route closure+CallConvSwift to wrapper library

Extend the `UsesWrapperLibrary` routing so that methods with closure parameters are automatically emitted through `@_cdecl` wrapper functions instead of direct `CallConvSwift` P/Invoke. This eliminates the "non-blittable types" error for closure-taking APIs across all libraries, not just Nuke.

### I3. Generator: route instance methods through `@_cdecl` wrappers

Instance methods on Swift classes/structs currently use `CallConvSwift` for the `self` parameter, which triggers the `jit-info.c:918` assertion. Generate `@_cdecl` wrapper functions that take `self` as a regular `UnsafeMutableRawPointer` parameter and forward to the instance method. This would unblock CryptoSwift's instance API (SHA2.Calculate, HMAC.Authenticate, ChaCha20.Encrypt/Decrypt, RSA, etc.).

---

## Phase K: API Documentation Generation (Swift Doc Comments → C# XML Doc Comments)

**Status**: Done
**Priority**: Medium
**Effort**: 1 session

Extracts Swift doc comments from `swift-symbolgraph-extract` output and emits C# XML doc comments (`/// <summary>`, `/// <param>`, `/// <returns>`, `/// <remarks>`) on all generated bindings. Join key: `node.usr` (ABI JSON) = `symbol.identifier.precise` (symbol graph JSON). Entirely opt-in via `--symbolgraph` CLI option.

**New files**: `DocComment.cs` (model), `SymbolGraphDocParser.cs` (streaming JSON parser), `XmlDocCommentEmitter.cs` (XML doc emission with backtick→`<c>` conversion, Swift label→C# param name mapping, failable factory `Returns`→`<param name="result">` projection).

**Modified**: `BaseDecl.cs` (`Documentation` property), `SwiftABIParser.cs` (`usr` field + `PopulateDocumentation` in all 9 `Create*Decl` methods), `Program.cs` (`--symbolgraph` option), 8 handler files (emission insertion points), `build-xcframework.sh` + `regenerate-bindings.sh` (pipeline integration).

**Tests**: 30 new unit tests (13 parser + 17 emitter). Verified end-to-end: TestFramework symbol graph extraction → doc comments on generated C# types (e.g., `TaskStatus`, `INamed`, `NamedItem`).

---

## Phase J: Additional Library Validation

**Status**: Not Started
**Priority**: Medium
**Effort**: Medium (2-3 sessions)
**Depends on**: Phase I (wrapper routing makes more APIs functional)

### J1. Select and bind a new library
Candidates (pick 1):
- **Alamofire** — networking, heavy closure/async patterns
- **Kingfisher** — image loading, different patterns from Nuke
- **SwiftProtobuf** — value types, generics, enums heavy

### J2. Process
1. Build xcframework for the library
2. Run generator, check binding report
3. Compare member coverage to existing libraries (target: 90%+)
4. Verify golden scenario compiles without interop types
5. Fix any new generator bugs found
6. Add to `BindingTesting/` with build/validate scripts

### J3. Document findings
- Update `CURRENT-STATUS.md` with new library stats
- Add any new skip reasons to `testing-gaps.md`

---

## Future Work

Once Phase J is complete:
- Must-pass features at 94+ (currently 94, up from 61 pre-Phase B)
- Runtime test coverage covers most of the contract matrix
- Generated API is idiomatic C# — no interop types in public surface
- 5-6 real-world libraries validated
- Quality scorecard metrics all at gate values
- Test pipeline catches regressions automatically

Next priorities:

- **MSBuild SDK + project templates** — Phase 3 DX work from `north-star.md`
- **Optional string properties** — `Swift.Optional<Swift.String>` → `string?` (extend TypeConversionHandler to unwrap optional strings)
- **Cross-module protocol interface coverage** — Expand `_runtimeProtocols` for stdlib protocols used as existentials (Comparable, Sendable, CodingKey, etc.)
- **Remaining testing gaps** — P3/P4 items from `testing-gaps.md` (PInvokeEmitter tests, golden snapshots, CI)
- **Deferred work** in `Future/` (NativeAOT validation, Roslyn analyzer, existential analysis, performance benchmarks)

### Known Runtime Blockers (Upstream)
- **Mono JIT assertion (jit-info.c:918)**: Kills process on closure P/Invoke + SwiftString via CallConvSwift
- **SafeHandle in async P/Invoke**: Not preserved through async continuation
- **Non-blittable CallConvSwift**: Mono rejects non-blittable types with Swift calling convention
- See `known-issues-workarounds.md` for details
