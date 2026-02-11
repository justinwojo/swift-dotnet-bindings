# Completed Phases: H through WU

Archived from `roadmap.md` — February 2026.

---

## Phase H: Unit Test Gaps + Remaining Library Errors

**Status**: Done (H1 + H2)

### H1: Unit Test Coverage Gaps (Phase G fixes)

Phase G fixed 8 generator bugs but 3 fixes lacked targeted unit tests. Added 5 regression tests.

### H2: Remaining Library Errors (6 distinct bugs, 12 total errors)

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

## Phase I: Mono JIT Mitigation — Wrapper Routing (Completed Items)

**Reference**: `mono-jit-mitigation-strategies.md`, `../Future/mono-jit-future-work.md`

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

---

## Phase K: API Documentation Generation (Swift Doc Comments → C# XML Doc Comments)

**Status**: Done

Extracts Swift doc comments from `swift-symbolgraph-extract` output and emits C# XML doc comments (`/// <summary>`, `/// <param>`, `/// <returns>`, `/// <remarks>`) on all generated bindings. Join key: `node.usr` (ABI JSON) = `symbol.identifier.precise` (symbol graph JSON). Entirely opt-in via `--symbolgraph` CLI option.

**New files**: `DocComment.cs` (model), `SymbolGraphDocParser.cs` (streaming JSON parser), `XmlDocCommentEmitter.cs` (XML doc emission with backtick→`<c>` conversion, Swift label→C# param name mapping, failable factory `Returns`→`<param name="result">` projection).

**Modified**: `BaseDecl.cs` (`Documentation` property), `SwiftABIParser.cs` (`usr` field + `PopulateDocumentation` in all 9 `Create*Decl` methods), `Program.cs` (`--symbolgraph` option), 8 handler files (emission insertion points), `build-xcframework.sh` + `regenerate-bindings.sh` (pipeline integration).

**Tests**: 30 new unit tests (13 parser + 17 emitter). Verified end-to-end: TestFramework symbol graph extraction → doc comments on generated C# types (e.g., `TaskStatus`, `INamed`, `NamedItem`).

---

## Mono JIT Mitigation: Strategy D (Signature Risk Detection)

**Status**: Done

Added `MonoJitRiskDetector` — a static analysis pass that flags methods with signatures that trigger the Mono JIT crash (`jit-info.c:918`). Detects closure parameters, existential parameters, and SwiftString returns (including Optional-wrapped variants). Consumed by Strategy B for closure Cdecl wrapper decisions. 34 unit tests.

---

## Mono JIT Mitigation: Strategy B (Closure Cdecl Expansion)

**Status**: Done

Generator emits `CallConvCdecl` callbacks for non-async escaping closures with primitive args/returns. For each qualifying method, a Swift `@_silgen_name` wrapper accepts `@convention(c)` function pointer + context as separate IntPtr parameters, creating a native closure adapter on the Swift side. Mono only sees `CallConvCdecl`. 38 unit tests. See `../known-issues-workarounds.md` Workaround B for details.

---

## Tier Promotion Pass

**Status**: Done

Added `Tj` dispatch thunks for non-final class methods (library evolution), `IsFinal` detection on both `ClassDecl` and `MethodDecl`, and promoted multiple test classes from Tier 3 to Tier 2: closure tests, composition tests, string property tests. Runtime tests grew from 172 to 185 passing.

---

## Binding API WU1-WU6: Idiomatic C# Surface

**Status**: Done

Major API surface improvements (see `binding-api-review-and-improvements.md`). Remaining items in `../Future/binding-api-future-work.md`.

| Work Unit | Change |
|-----------|--------|
| WU1 | Verb prefix (`Get` for noun-only return methods), async `Async` prefix stripping |
| WU2 | Array element type conversion (`IReadOnlyList<SwiftString>` → `IReadOnlyList<string>`) |
| WU3 | Subscript type conversion (subscript element types also converted) |
| WU4 | Parameter name normalization (type-derived names, strip `_` prefixes, dedup) |
| WU5 | `unsafe` removed from public surface (moved to body-level blocks) |
| WU6 | Doc comment generation (Phase K, `--symbolgraph` option) |

Post-WU Codex review fixes: protocol proxy getter/setter type asymmetry, Optional<Array<String>> element conversion, GetSwiftWrapperType raw element types, async-void Get prefix ordering. 6 library binding bugs fixed during regeneration + 17 regression tests.

---

## Enum Existential Promotion + #nullable Bridge

**Status**: Done

ExistentialContainer→interface promotion in enum associated values + #nullable enable in SwiftUI bridge. 7 unit tests.
