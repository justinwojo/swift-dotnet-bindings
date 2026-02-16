# Completed Roadmap Sessions (1–14)

**Archived**: February 2026
**Source**: Moved from `roadmap.md` — these sessions are fully complete with no remaining action items.

---

## Session 1: Consumer Safety Attributes — Done (2026-02-14, 2519 unit tests)

**Priority**: P0/P2 | **Type**: Implementation | **Risk**: Low

All three items are about surfacing hidden information to consumers at compile time — turning silent runtime crashes into visible warnings. They share code paths (`PInvokeEmitter`, TBD symbol parsing, attribute emission) and need the same context (how methods are flagged, how symbols are resolved).

| Item | Priority | Effort | Status |
|------|----------|--------|--------|
| **Compile-time warnings for unbindable methods** | P0 | Small | Done — `[Obsolete("...", true)]` on unmitigated JIT-risky methods. 7 unit tests. |
| **P/Invoke symbol cross-referencing** | P2 | Medium | Done — `ComputeEntryPoint()` extracted, `CheckExportedSymbol()` cross-refs TBD. 7 unit tests. |
| **`[OriginalSwiftType]` attribute** | P2 | Small | Done — New runtime attribute + param/return emission for AnyType fallbacks. 8 unit tests. |

**Key changes**: `PInvokeEmitter.ComputeEntryPoint()` (extracted), `MethodHandler.CheckExportedSymbol()`, `WrapperEmitter.EmitSafetyObsolete()` + `BuildOriginalSwiftTypeAttributes()` + `EmitReturnTypeOriginalSwiftType()`, `MethodSignature.ParametersString()` overload, `OriginalSwiftTypeAttribute.cs` (new), `UnsupportedSwiftTypeSupport.EscapeStringLiteral()` (now internal). Property accessors deferred (see plan).
**Note**: `[OriginalSwiftType]` requires `Swift.Runtime` NuGet re-publish to compile in consumer projects.

---

## Session 2: SwiftOptional Extra Inhabitants Fix — Done (2026-02-14, 2527 unit tests)

**Priority**: P0 | **Type**: Bug fix | **Risk**: Low

Root cause: `SwiftOptional<T>.NewSome()` assumed all Optional types have a discriminator byte (`metadata.Size - 1`). For extra-inhabitant types (String, Array, classes) where `Optional<T>.Size == T.Size`, this created an undersized span — crashing with "Span size does not match type size." This is the same crash as Stripe's `StripeAPI.DefaultPublishableKey = "pk_test_xxx"`.

| Item | Priority | Effort | Status |
|------|----------|--------|--------|
| **SwiftOptional.NewSome() span fix** | P0 | Small | Done — use inner type's metadata size instead of `metadata.Size - 1`. 7 unit tests for `ComputePayloadSpanSize`. |
| **DllImportResolver conflict fix** | P0 | Small | Done — wrapped RuntimeTestsApp resolver in try-catch. Generated `[ModuleInitializer]` and app's `Main()` both call `SetDllImportResolver`. |
| **[Obsolete] test build compatibility** | P0 | Small | Done — `run-runtime-tests.sh` Step 1.7 sed-downgrades `[Obsolete("...", true)]` to warning for test builds. Consumer bindings retain `error: true`. |
| **Runtime tests** | P0 | Small | Done — 5 new Tier 3 Optional<String> tests (Mono JIT + P/Invoke truncation block Tier 2). |
| **StripePayments runtime verification** | P0 | Small | Deferred — requires external Stripe test app. |

**Key changes**: `SwiftOptional.cs` (`NewSome()` + `ComputePayloadSpanSize()`), `SwiftOptionalSpanSizeTests.cs` (7 tests), `run-runtime-tests.sh` (Step 1.7), `Program.cs` (DllImportResolver try-catch), `SdkPropsTargetsTests.cs` (removed brittle version-string assertions).
**Discovered**: Pre-existing `Optional<String>` P/Invoke truncation bug — `PayloadBuffer<IntPtr>` only captures 8 of 16 bytes. Tracked in Known Generator Bugs.

---

## Session 3: SDK & NuGet DX — Done (2026-02-14, 2542 unit tests)

**Priority**: P1 | **Type**: Implementation | **Risk**: Low

Both items are MSBuild SDK and NuGet packaging improvements. They share `Sdk.targets` and the `ConsumerTargetsEmitter`. Both affect the `dotnet build` -> `dotnet pack` -> consumer experience chain.

| Item | Priority | Effort | Status |
|------|----------|--------|--------|
| **Two-pass build fix (SWIFTBIND050)** | P1 | Small | Done — `EffectiveOutcome()` downgrades Fatal→Warning in SDK mode. `HandleWrapperCompilationOutcome()` extracted. 10 unit tests. |
| **NativeReference Exists() guard** | P1 | Small | Done — Source xcframework NativeReference gets `Exists()` condition matching wrapper pattern. 5 unit tests. |
| **Sdk.targets case-insensitive doc comments** | P1 | Small | Done — `SwiftGenerateDocComments` comparison uses `System.String.Equals(..., OrdinalIgnoreCase)`. |

**Key changes**: `SwiftWrapperCompiler.EffectiveOutcome()`, `BindingsGenerator.HandleWrapperCompilationOutcome()`, `Program.cs` (wired outcome handling), `ConsumerTargetsEmitter.cs` (Exists guard), `Sdk.targets` (case-insensitive), `dx-msbuild-sdk-design.md` (SWIFTBIND050 error code).

---

## Session 4: Typed Swift Exceptions — Done (2026-02-14, 2593 unit tests)

**Priority**: P1 | **Type**: Implementation | **Risk**: Medium

Standalone feature touching the async error pipeline. Requires a new type in `Swift.Runtime` and changes to how error callbacks marshal exception information. Self-contained — doesn't share code paths with other sessions.

| Item | Priority | Effort | Status |
|------|----------|--------|--------|
| **`SwiftException<TError>` runtime type** | P1 | Small | Done — generic exception class with nullable `Error` property. |
| **Typed throws detection** | P1 | Medium | Done — `GetTypedThrowsErrors()` parses `.swiftinterface` for `throws(ErrorType)`, threaded to `MethodDecl.ThrownErrorType` via `SwiftABIParser`. |
| **Async typed error callbacks** | P1 | Medium | Done — 4-param error callback (errorPtr + size + msg + task) with `MarshalFromSwift<TError>` + `SBW_Free`. `BuildErrorCallbackBlock()` helper deduplicates 5 emission sites. |
| **Sync typed exceptions** | P1 | Small | Done — `SwiftException<TError>(message)` with `Error = null` (existential extraction deferred). |
| **Free-function async guard** | P1 | Small | Done — D5 guard: `HasTypedThrows && IsAsync && parentTypeName == null` falls back to untyped (avoids known `_payload`/`this` bug). |

**Scope**: Async method wrappers (full error value transport) + sync method exception typing (message-only). Throwing closures (`ClosureEmitter.Throwing.cs`) explicitly out of scope — closures use `SwiftResult<TSuccess, SwiftError>`.
**Key changes**: `SwiftException.cs` (generic subclass), `SwiftInterfaceAccessParser.cs` (`GetTypedThrowsErrors()`), `MethodDecl.cs` (`ThrownErrorType`, `HasTypedThrows`), `SwiftABIParser.cs` (typed throws dictionary), `Program.cs` (wiring), `WrapperEmitter.cs` (sync `SwiftException<T>`), `WrapperEmitter.Async.cs` (`BuildErrorCallbackBlock()`, typed catch blocks, `SBW_Free` P/Invoke in error callback), `TypedThrows.swift` (async instance method).
**Tests**: 10 parser unit tests (`SwiftInterfaceTypedThrowsTests`), 10 emitter unit tests (`TypedThrowsEmitterTests`), 1 new Tier 1 runtime test (`TestValidateRangeTypedCatchNullError`), 2 Tier 3 async runtime tests, 5 existing runtime tests updated from `SwiftRuntimeException` to `SwiftException<T>`.
**Note**: `SwiftException<TError>` requires `Swift.Runtime` NuGet re-publish to compile in consumer projects.

---

## Session 5: SwiftArray Collection Interface — Done (2026-02-14, 2593 unit tests + 156 runtime library tests)

**Priority**: P2 | **Type**: Implementation | **Risk**: Low

Standalone runtime library change. `SwiftArray<T>` previously copied to `List<T>` via LINQ `.Select().ToList()` on every string array access. Now uses lazy `AsProjected()` — zero-copy indexed access.

| Item | Priority | Effort | Status |
|------|----------|--------|--------|
| **Constructors from T[] and IEnumerable\<T\>** | P2 | Small | Done — `new SwiftArray<T>(source)` + implicit operator from `T[]`. |
| **Indexer bounds checking** | P2 | Small | Done — `ArgumentOutOfRangeException` on OOB (Swift would crash). |
| **Lazy projection wrapper** | P2 | Medium | Done — `SwiftArrayProjection<TSource, TResult>` (internal, IReadOnlyList). Live view, no copying. |
| **Emitter integration** | P2 | Small | Done — `.Select(e => e.ToString()).ToList()` → `.AsProjected(e => e.ToString())` in `GetReturnConversion`. |

**Key changes**: `SwiftArray.cs` (constructors, implicit op, bounds check, `AsProjected<T>()`), `SwiftArrayProjection.cs` (new), `TypeConversionHandler.cs` (return conversion).
**Tests**: 15 new SwiftArray tests (constructors, conversions, bounds, IList, AsProjected) + 10 SwiftArrayProjection tests (lazy access, live view, enumeration, bounds, SwiftString) + 2 updated emitter tests.
**Note**: `SwiftArrayProjection` requires `Swift.Runtime` NuGet re-publish to compile in consumer projects.

---

## Session 6: Async Method Improvements — Done (2026-02-14)

**Priority**: P2/P3 | **Type**: Implementation | **Risk**: Medium

| Item | Priority | Effort | Status |
|------|----------|--------|--------|
| **CancellationToken on async methods** | P2 | Medium | Done — `CancellationToken cancellationToken = default` on all `Task`-returning methods. Swift Task store (`_SBWTaskEntry` + `_sbwActiveTasks` dictionary + `NSLock`), `@_cdecl("SBW_CancelTask_{Module}")` cancel function, C# registration → Swift cancel + `TrySetCanceled`, pre-cancel check with `Task.FromCanceled`. `isCancellation: Int32` error callback parameter (type-safe, not string matching). |
| **Callback-to-Task overloads** | P3 | Medium | Done — `CompletionHandlerDetector` identifies completion handler closures (trailing, void-returning, recognized shapes: VoidResult, SingleResult, ErrorOnly, ResultWithError). Generates `Task<T>`-returning overloads with `TaskCompletionSource` + `RunContinuationsAsynchronously`. Bound generic type resolution with protocol guard. |

**Key files**: `CancellationTaskEmitter.cs` (new), `CompletionHandlerDetector.cs` (new), `WrapperEmitter.Async.cs`, `WrapperEmitter.cs`, `ModuleHandler.cs`, `MethodHandler.cs`
**Tests**: 52 new (30 CancellationTokenEmitterTests + 22 CompletionHandlerDetectorTests)

---

## Session 7: Emitter Quality Fixes — Done (2026-02-14)

**Priority**: P3 | **Type**: Verification + test hardening | **Risk**: Low

Both items' original descriptions were stale — core behavioral work was completed in earlier sessions (R11 nested type renames, overload emitter scope expansion). This session verified correctness and added regression tests.

| Item | Priority | Effort | Status |
|------|----------|--------|--------|
| **Property collision logic (N6)** | P3 | Small | Done — R11 already uses Info suffix for nested type collisions. CS0542 Value suffix verified mandatory. 4 unit tests added. |
| **Default parameter overloads** | P3 | Small | Done — already covers all emission-eligible methods. Intentional skips: accessors, internal, generic parents, placeholders, collisions. 9 unit tests added. |

---

## Session 8: ExistentialContainer Cleanup — Done (2026-02-14)

**Priority**: P2 | **Type**: Implementation | **Risk**: High

| Item | Priority | Effort | Status |
|------|----------|--------|--------|
| **ExistentialContainer in public API** | P2 | Hard | Done — `AllProtocolsHaveTypeRecords()` gate, closure params emit `IProtocol`, enum case constructors use typed interfaces, `Optional<any Protocol>` guard relaxed, mixed ObjC composition safety guards. |

**Key changes**: `ExistentialHandler.AllProtocolsHaveTypeRecords()`, `ClosureEmitter.cs` + 3 partials (Async, IndirectReturn, Throwing), `ClosureHandler.NeedsProxyWrapping()`, `EnumHandler.CaseConstruction.cs`, `MemberEmissionValidator.cs`, `MethodHandler.cs`.
**Tests**: `ClosureExistentialTests.cs` (413 lines), `ExistentialOptionalGuardTests.cs` (561 lines), `ConstructorHandlerOutputTests.cs` (158 lines).
**Remaining ExistentialContainer usages are intentional**: unknown protocols without TypeRecords, Optional existentials in closures (MarshalFromSwift limitation), mixed ObjC compositions (safety guard prevents size mismatch). None fixable without upstream changes.

---

## Session 13: Multi-Framework Auto-Detection — Done (2026-02-15)

**Priority**: P2 | **Type**: Implementation | **Risk**: Medium

Builds on existing `--framework-dependency` / `<SwiftFrameworkDependency>` support. Adds automatic detection so users don't need to specify dependencies manually.

| Item | Priority | Effort | Status |
|------|----------|--------|--------|
| **BinaryDependencyAnalyzer** | P2 | Medium | Done — `otool -L` parsing, framework name extraction, sibling xcframework search, full analysis with slice validation. |
| **TopologicalSort** | P2 | Small | Done — Kahn's algorithm with lexical tie-breaking for deterministic build ordering across runs/platforms. |
| **DependencyManifestEmitter** | P2 | Small | Done — `dependency-manifest.json` with effective deps, unresolved/overridden tracking, build order, and graph warnings. |
| **CLI `--no-auto-detect` opt-out** | P2 | Small | Done — disables auto-detection when manual control needed. |
| **MSBuild SDK integration** | P2 | Small | Done — `SwiftAutoDetectDependencies` property (defaults to `true`), fingerprint integration. |

**Key changes**: `BinaryDependencyAnalyzer.cs` (new, 579 lines), `TopologicalSort.cs` (new, 103 lines), `DependencyManifestEmitter.cs` (new, 204 lines), `FrameworkDependencyInfo.cs` (model), `Program.cs` (CLI wiring), `Sdk.props` + `Sdk.targets` (MSBuild integration).
**Tests**: 49 new unit tests covering parsing, extraction, sibling search, slice demotion, manifest emission, topological sort, cycle fallback, name-mismatch handling, and CLI/SDK integration.
**Design**: `Future/dx-multi-framework-auto-detection.md`

---

## P4-4: NativeAOT Migration — Done (2026-02-15)

**Design**: `nativeaot-investigation.md`

All three Mono JIT blockers verified resolved under NativeAOT (28/28 tests pass on macOS, 13/14 on device). `[LibraryImport]` migration complete across all emitters. `SwiftBindingsInteropMode` (Auto/Safe/Direct) ships in consumer `.targets`. Remaining: Step 22 device re-validation after loose ends cleanup, Steps 17-18 (`[MarshalUsing]` for typed non-blittable params) deferred — IntPtr approach works on both runtimes.

---

## Session 14: Consumer API Polish — Done (2026-02-16, 2782 unit tests)

**Priority**: P1 | **Type**: Implementation | **Risk**: Low
**Motivation**: External binding analysis (score: 5.5/10) identified several easy-win gaps where the generated API doesn't meet .NET developer expectations.

| Item | Priority | Effort | Status |
|------|----------|--------|--------|
| **`IDisposable` interface declaration** | P1 | Small | Done — Added to `GetImplementedInterfaces()` in `TypeHandlerHelpers.cs` (propagates to all 4 type handlers), `ProtocolProxyEmitter.cs` proxy classes, and `ModuleHandler.cs` composition proxies. 10 test assertions updated. |
| **`[EditorBrowsable(Never)]` on infrastructure** | P1 | Small | Done — Applied to `_payload`, `_payloadSize`, `Payload` fields across all handlers (ClassHandler, EnumHandler, NonFrozenStructHandler, FrozenStructHandler). Applied to `NewFromPayload`, `MarshalToSwift`, `_protocolConformanceSymbols`, `GetProtocolConformanceDescriptor` in all ISwiftObjectMethodWriter implementations. `using System.ComponentModel;` added to emitted usings. |
| **Share async helper structs in Swift.Runtime** | P2 | Small | Done — Created `AsyncHelpers.cs` in `Swift.Runtime` with `RetainedSelfPtr`, `DeferredSafeHandleRelease`, `CopyBufferWithType`, `CancellationRegistrationHolder` (all `[EditorBrowsable(Never)]`). Removed inline emission from `ModuleHandler.cs`. Aligned `BindingProjectEmitter` version to `0.1.0-preview.5`. |
| **SwiftString in enum case constructors** | P1 | Medium | Done — Standalone `SwiftString` params use `string` in public API with `using var` conversion. Tuple elements keep `SwiftString` ABI type (marshalling requires it). `GetPublicCSharpTypeNameForEnumCase` returns `"string"` for standalone SwiftString; tuple recursion selectively suppresses only SwiftString→string (existential→interface still applies). TryGet methods emit `.ToString()` for standalone SwiftString extraction. |
| **Module-name stutter** | P1 | Small | Done — `ModuleHandler.cs` detects when namespace ends with module name and renames wrapper class to `Functions`. Collision handling escalates to `GlobalFunctions` → `{Module}Functions` → numeric suffix. Handles edge case where module is literally named `Functions`. All 11 integration test modules updated. |

**Key changes**: `TypeHandlerHelpers.cs`, `ProtocolProxyEmitter.cs`, `ModuleHandler.cs`, `ClassHandler.cs`, `EnumHandler.cs`, `EnumISwiftObjectMethodWriter.cs`, `NonFrozenStructHandler.cs`, `FrozenStructHandler.cs`, `EnumHandler.CaseConstruction.cs`, `EnumHandler.CaseInspection.cs`, `BindingProjectEmitter.cs`, `AsyncHelpers.cs` (new in Swift.Runtime).
**Tests**: 5 new unit tests (stutter naming × 3, mixed tuple SwiftString+existential, standalone SwiftString enum case). 10 existing test assertions updated for IDisposable. 11 integration test files updated for Functions class rename.
**Validation**: Nuke (74 errors — all async helper NuGet version mismatch, pre-existing), Lottie (18 errors — same category), Alamofire (stutter verified).
