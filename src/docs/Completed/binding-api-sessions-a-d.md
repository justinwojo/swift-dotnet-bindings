# Binding API — Completed Sessions A–D

**Completed**: February 2026
**Source**: `binding-api-future-work.md` (Sessions A–D)

---

## Session A: ExistentialContainer in Public API — DONE

**Origin**: R6 (Partial) | **Priority**: P2 | **Difficulty**: Hard

Created `Swift.AnyError` — a blittable struct wrapping `ExistentialContainer1` that implements `IExistentialContainer`. The generator maps `any Swift.Error` → `AnyError` via well-known protocol lookup in `ExistentialHandler.TryGetWellKnownProtocolType()`, running before the general proxy path.

**Changes:**
- **Runtime**: `AnyError.cs` (new), `TypeMetadata.GetProtocolCountFromExistentialType` (wrapper type support), `SwiftMarshal.MarshalFromSwift` (IExistentialContainer handling)
- **Generator**: TypeDatabase registers `Swift.Error` as built-in, `ClosureHandler.TranslateTypeSpecToCSharp` maps to `AnyError`/`AnyError?`, `ClosureEmitter` wraps/unwraps in callback and invoker lambdas (all 6 return paths: non-throwing callback/invoker, throwing callback/invoker, frozen struct invoker, non-frozen struct invoker), `EnumHandler.Marshalling` emits `new AnyError(...)` instead of proxy, `ProtocolProxyEmitter.Helpers` handles well-known protocols in closure type translation
- **API compat**: Protocol proxy constructors changed to `internal`

**Validation**: All 25 libraries at 0 generator errors. Nuke/Lottie/BlinkID compile with AnyError replacing ExistentialContainer1 in `any Swift.Error` positions. 2916 unit + 699 integration tests pass.

**Key files modified:**
| File | Change |
|------|--------|
| `src/Swift.Runtime/src/Swift/AnyError.cs` | New: AnyError struct implementing IExistentialContainer |
| `src/Swift.Runtime/src/Swift/Runtime/TypeMetadata.cs` | Fix GetProtocolCountFromExistentialType for wrapper types |
| `src/Swift.Runtime/src/Swift/Runtime/InteropServices/SwiftMarshal.cs` | Add IExistentialContainer to MarshalFromSwift |
| `src/Swift.Bindings/src/TypeDatabase/TypeDatabase.cs` | Register Swift.Error built-in TypeRecord |
| `src/Swift.Bindings/src/TypeDatabase/TypeDatabaseExtensions.cs` | SwiftErrorType + IsWellKnownRuntimeProtocol |
| `src/Swift.Bindings/src/Marshaler/ExistentialHandler.cs` | TryGetWellKnownProtocolType + GetPublicExistentialType update |
| `src/Swift.Bindings/src/Marshaler/ClosureHandler.cs` | Well-known protocol in TranslateTypeSpecToCSharp + Optional path |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.cs` | Callback/invoker return wrapping |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.Throwing.cs` | Throwing callback/invoker return wrapping |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.StructParams.cs` | Struct-params invoker return wrapping |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumHandler.Marshalling.cs` | new AnyError() instead of new ErrorProxy() |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.Helpers.cs` | Well-known protocol + Optional<existential> in proxy closures |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.Receivers.cs` | Constructor → internal |
| `src/Swift.Bindings/src/Emitter/StringEmitter/GenericTypeEmitter.cs` | Skip well-known protocols from generic constraints |

---

## Session B: Exception Mapping for Swift `throws` — DONE

`SwiftException<TError>` implemented in runtime (`SwiftException.cs`) and generator (`WrapperEmitter.cs`). Typed error extraction works for async methods; sync path has message but null error value (can't extract from Swift existential box). Fallback to untyped `SwiftException` for unresolvable error types. Test coverage in `ThrowingMethodTests.cs`.

---

## Session C: CancellationToken on Async Methods — DONE

`CancellationTaskEmitter` emits Swift-side task dictionary + cancel function per module. All async methods receive `CancellationToken cancellationToken = default` parameter with pre-cancellation check, registration callback, and `SBW_CancelTask` P/Invoke. `CancellationRegistrationHolder` in runtime. Protocol interface/proxy methods included. 35+ unit tests in `CancellationTokenEmitterTests.cs`.

---

## Session D: Async Callback → Task Wrappers — DONE

`CompletionHandlerDetector` identifies 4 callback shapes (VoidResult, SingleResult, ResultWithError, ErrorOnly). `MethodHandler.TryEmitCompletionHandlerOverload()` emits `TaskCompletionSource`-based `Async`-suffixed overloads with cancellation support. Dedup with native async methods prevents CS0111. 23+ tests in `CompletionHandlerDetectorTests.cs`.
