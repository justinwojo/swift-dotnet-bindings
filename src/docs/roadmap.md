# Roadmap

**Updated**: February 2026
**Status**: Active
**Target**: Raise binding quality from 6.5/10 to 8.5+/10 for .NET developer experience

For completed work, see `Completed/`.
External review: `/Users/wojo/Dev/swift-dotnet-packages/binding-analysis-v2.md` (not in this repo).

---

## Current Baselines

| Metric | Value |
|--------|-------|
| Unit tests | 3,439 passing |
| Integration tests | 700 passing (11 skipped, pre-existing) |
| Runtime library tests | 207 passing |
| Runtime tests | 188 passing at Tier 2 (28 pre-existing failures, allowlist-based crash tolerance) |
| TestFramework must-pass | 94/94 passing, 0 degraded |
| Libraries validated | 31 passing (28 clean + 3 known errors) |
| External review score | 6.5/10 (up from 5.5) |

---

## Priority Key

- **P0**: Blocks adoption — APIs are unusable or methods are silently missing
- **P1**: Major DX friction — consumers hit these within first hour of use
- **P2**: Quality gaps — noticeable but workable, erodes confidence over time
- **P3**: Polish — professional quality, long-term sustainability
- **P4**: Future vision — architectural improvements, new capabilities

---

## Acceptance KPIs

Hard, measurable gates per priority tier. Grep/compile checks, not subjective scores.

| KPI | Current | After P0 | After P1 |
|-----|---------|----------|----------|
| `ExistentialContainer` in public signatures | 0 (was ~60+) | 0 | 0 |
| Stripe 11-module cross-compile (non-AnyType members) | 100% (3-module validated, SDK integration complete) | 95%+ | 98%+ |
| `SwiftDictionary` in public signatures | 0 (was 144) | 0 | 0 |
| `SwiftOptional<` in public signatures | ~20 | ~20 | 0 |
| Empty protocol interfaces (0 members) | 11 | <5 | 0 |
| Enums emitted as native C# `enum` | ~15% (was ~5%; string-raw-value enums now qualify) | ~15% | ~40% |
| Sync throw messages with actual error text | 0% | 0% | 100% |
| Runtime type leakage (SwiftArray/SwiftOptional/ExistentialContainer in public API) | ~26 (was ~170; dictionary eliminated) | ~26 | <10 |

---

## P0 — Adoption Blockers

### 1. Cross-Module Type Resolution

**Priority**: P0 | **Effort**: Large (3-4 sessions) | **Risk**: Medium

When generating bindings for StripePaymentSheet, types from StripePayments (e.g., `STPPaymentMethod`) resolve to `AnyType` and members are skipped. This blocks all real-world Stripe payment flows that span modules. The external review called this "the single biggest blocker for real-world adoption."

**Architecture today**: Generator processes ONE module at a time. TypeDatabase only contains the current module + built-in databases (Foundation, UIKit, Swift, CoreGraphics, Dispatch). Cross-module types → `GetTypeRecordOrAnyType()` → `AnyType` → member skipped.

**Architecture needed**: Load dependent module type databases before emitting the current module.

| Step | Description | Effort | Status |
|------|-------------|--------|--------|
| **1a. Generate module database XML** | After processing a module, emit a `{Module}Database.xml` alongside the binding. Contains all TypeRecord entries needed by dependents (type name, C# name, kind, flags, metadata accessor). | Medium | **COMPLETE** |
| **1b. Accept `--module-database` CLI option** | Repeatable option that loads dependent module XML files into TypeDatabase before emission. Validation: SWIFTBIND070 (missing file), SWIFTBIND071 (self-reference), SWIFTBIND072 (invalid XML). | Low | **COMPLETE** |
| **1c. SDK integration** | `_CollectSwiftModuleDatabases` target gathers databases from NuGet packages (`SwiftModuleDatabase` items from consumer `.targets`) and local `ModuleDatabasePath` metadata. Deduplicates, warns on missing paths (SWIFTBIND073), passes `--module-database` args to generator. `ConsumerTargetsEmitter` emits `SwiftModuleDatabase` item in consumer `.targets`. Pack layout bundles `{Module}Database.xml` in `buildTransitive/net10.0-ios/`. Fingerprint includes database content hashes. Build order NOT enforced by SDK (inherent MSBuild limitation — NuGet packages are pre-built; local builds use explicit `ProjectReference`). | Medium | **COMPLETE** |
| **1d. Expand cross-module protocol conformance** | Remove the `CrossModuleSupportedProtocols` whitelist (currently only `Equatable`/`Hashable`). With full type databases loaded, cross-module conformances are emitted for empty/marker interfaces (`EmittedMemberCount == 0`). Non-empty interfaces are gated to prevent CS0535. `ProtocolHandler.FixupProtocolInheritedRequirements` post-pass propagates inherited requirements (transitive, nested) to a fixed point. | Low | **COMPLETE** |

**Key insight**: We don't need to re-parse dependent ABI JSON. We just need the TypeRecord metadata (C# type name, mangled name, kind, flags). A small XML file per module (~10KB for StripePayments) is sufficient.

**Steps 1a+1b validated**: StripePaymentSheet with both StripeCore + StripePayments dependency databases resolves 46 StripeCore types + 87 StripePayments types (0 AnyType). `STPPaymentMethod` appears as proper type. New files: `ModuleDatabaseEmitter.cs`, `ProgramModuleDatabaseTests.cs`, `ModuleDatabaseEmitterTests.cs`. Extended: `TypeDatabase.cs` (protocol/existential kind, new flags), `ModuleDatabase.cs` (GetAllTypeRecords), `Program.cs` (CLI option + loading).

**Step 1c validated**: SDK integration adds `_CollectSwiftModuleDatabases` target to `Sdk.targets`, `SwiftModuleDatabase` item emission in `ConsumerTargetsEmitter.cs`, pack layout bundling, and fingerprint hashing. 4 behavioral MSBuild execution tests (stub generator approach) + 5 emitter content tests + 8 Sdk.targets content tests. NuGet flow: consumer `.targets` registers `SwiftModuleDatabase` → `_CollectSwiftModuleDatabases` collects → `_GenerateSwiftBindings` passes `--module-database` → cross-module types resolve.

**Acceptance gate**: Generate all 11 Stripe modules with dependency chain. `STPPaymentMethod` appears as proper type in StripePaymentSheet bindings (not `AnyType`). AnyType fallback count in Stripe drops >80%.

---

### 2. Eliminate ExistentialContainer from Public API — COMPLETE

**Priority**: P0 | **Effort**: Medium (2 sessions) | **Risk**: Low-Medium | **Status**: Complete

`ExistentialContainer{N}` no longer appears in any public closure/delegate signature, tuple element type, or generic type argument across all 25 validated libraries. Known protocols project to their interface type (e.g., `IImageProcessing`), well-known protocols to their runtime type (e.g., `AnyError`), and unknown protocols to `object`. The P/Invoke layer still uses `ExistentialContainer` internally — only the public C# API changed.

| Step | Description | Status |
|------|-------------|--------|
| **2a. Project unknown protocols to `object`** | Unknown existentials in closure params/returns → `object`. P/Invoke stays `ExistentialContainer`. Callback: `(object)arg` boxing. Invoker: `(ExistentialContainer1)_arg` unboxing. Added `IsExistentialParam()` and `GetPInvokeExistentialType()` helpers. | **COMPLETE** |
| **2b. Project closure return existentials** | Removed `isReturnType` guard in `TranslateTypeSpecToCSharp`. Known-protocol returns → interface with `ISwiftExistentialConvertible` extraction in callbacks and proxy wrapping in invokers. Unknown returns → `object` with boxing/unboxing. Applied to all emitter paths: `ClosureEmitter.cs`, `ClosureEmitter.Throwing.cs`, `ClosureEmitter.StructParams.cs`. | **COMPLETE** |
| **2c. Project SwiftResult error type** | Already handled — `SwiftResult<T, SwiftError>` uses `SwiftError` (well-known protocol mapping), not `ExistentialContainer1`. No work needed. | **COMPLETE** (pre-existing) |
| **2d. Array/collection of existentials** | `TranslateBoundGenericToCSharp` in both `ClosureHandler` and `TupleHandler` now uses `GetPublicExistentialType` (interface/object) instead of `GetCSharpExistentialType` (container). `TupleHandler.TranslateElementTypeToCSharp` also updated. `HasClosureUnsafeTupleElements` expanded to detect existential type mismatches (blocks closures with tuple-existential elements in both params and returns). | **COMPLETE** |

**Acceptance gate**: Passed. `grep 'Func<.*ExistentialContainer\|Action<.*ExistentialContainer'` across all 25 library outputs returns 0 matches. All libraries compile with 0 errors. `SwiftOptional<ExistentialContainer>` also resolved to 0 occurrences (deferred item had lower impact than estimated).

**Residual risk — runtime cast for unknown protocols**: When a closure parameter or return uses `object` (unknown protocol path), the callback boxes `ExistentialContainer` → `object` and the invoker unboxes `object` → `ExistentialContainer`. This round-trip works correctly when the object originated from Swift (the box contains the original container). However, if a .NET consumer provides an arbitrary `object` that is *not* a boxed `ExistentialContainer`, the unbox cast will throw `InvalidCastException` at runtime. This is expected fail-fast behavior — `object` is strictly better UX than `ExistentialContainer` (discoverable, documentable), and the cast failure is immediate and descriptive. A future improvement could add a runtime adapter that constructs an `ExistentialContainer` from user-provided objects, but this requires protocol witness table synthesis which is out of scope.

**Key files changed**: `ClosureHandler.cs`, `TupleHandler.cs`, `ClosureEmitter.cs`, `ClosureEmitter.Throwing.cs`, `ClosureEmitter.StructParams.cs`, `ClosureExistentialTests.cs`.

---

## P1 — Major DX Friction

### 3. Native C# Enums for Simple Swift Enums

**Priority**: P1 | **Effort**: Medium (2-3 sessions) | **Risk**: Low

Swift enums are modeled as heap-allocated classes with `CaseTag`, `Dispose()`, and native memory allocation per access. This is jarring for .NET developers — enums should be value types with zero allocation.

The generator already emits native `enum` for frozen, non-generic enums with integral raw values (`IsSimpleEnum` path in `EnumHandler.SimpleEnum.cs`). The gap is in expanding coverage.

| Step | Description | Effort | Status |
|------|-------------|--------|--------|
| **3a. String-raw-value enums with instance methods** | Relaxed `IsStringRawValueSimpleEnum` to allow instance methods (emitted as extension methods). Static methods, properties, and operators still block the simple path. Added safety gate: only takes simple path when all instance methods have simple-emitter-compatible signatures (primitives, string, bool, void, same-enum). Set `TypeRecordFlags.SimpleEnum` for string-raw-value simple enums in `ModuleProcessor`. | Low | **COMPLETE** |
| **3b. Non-frozen simple enums** | Removed `IsFrozen` gate from `IsSimpleEnum` (kept on `IsStringRawValueSimpleEnum` to preserve correct raw value behavior). Added `CanSafelyEmitAsSimpleEnum` safety gate preventing silent member loss — checks nested types, properties, static methods, non-equality operators, and instance method signature compatibility. Fixed `MarshallingHelpers.MethodRequiresIndirectResult` and `WrapperEmitter.Async.cs` nonFrozenParams filter to bypass SimpleEnum types. `ModuleProcessor` also calls `CanSafelyEmitAsSimpleEnum` for consistent `TypeRecordFlags.SimpleEnum` classification. Async simple-enum returns are a pre-existing limitation (from 3a, latent — 0 real-world impact). | Medium | **COMPLETE** |
| **3c. Enum case caching for class-based enums** | Added `Lazy<T>`-backed singleton caching for no-payload case properties on immutable class-based enums. Eligibility gate: only caches when enum has no mutating methods and no writable instance properties (prevents mutation/disposal poisoning). Added `_isCachedSingleton` field with disposal guard (`Dispose()` and finalizer become no-ops for cached instances). Thread-safe via `Lazy<T>`. Applies to both `EmitRawRepresentableSupport` and `EmitSimpleCaseFromTag` paths. Nuke output: 27 `Lazy<>` instances generated, 0 compile errors. | Medium | **COMPLETE** |

**Step 3a key files**: `EnumDecl.cs`, `ModuleProcessor.cs`, `EnumHandler.cs` (emission gate), `EnumHandler.SimpleEnum.cs` (`AreAllInstanceMethodsSimpleEmitterCompatible`).

**Step 3b key files**: `EnumDecl.cs` (`IsSimpleEnum`), `EnumHandler.SimpleEnum.cs` (`CanSafelyEmitAsSimpleEnum`), `EnumHandler.cs` (emission gate), `MarshallingHelpers.cs` (indirect result bypass), `WrapperEmitter.Async.cs` (async param filter bypass), `ModuleProcessor.cs` (flag classification).

**Step 3c key files**: `EnumHandler.cs` (class template, eligibility gate, `EmitSimpleCaseFromTag`), `EnumHandler.RawRepresentable.cs` (case property emission).

**Acceptance gate**: All 25 libraries compile with 0 errors. 9 new unit tests covering non-frozen simple enum emission, safety gate behavior (nested types, properties, static methods, operators), and class-path fallback. Non-frozen no-payload/integral-raw-value enums without incompatible members now emit as C# enums. Enums with nested types, properties, static methods, non-equality operators, or incompatible instance methods correctly fall to class-based emission.

---

### 4. Optional<T> P/Invoke Truncation (Correctness) — COMPLETE

**Priority**: P1 | **Effort**: Medium (2 sessions) | **Risk**: Medium | **Status**: Complete

`Optional<T>` for `T.Size > 8` no longer truncates through P/Invoke. The `_optbuf` wrapper now covers all paths: standalone methods, frozen struct constructors, property setters/getters, mutating methods, wrapper-owned methods (ArraySlice, DefaultParam, ClosureCdecl, opaque return), async methods, and sync Optional return values.

| Step | Description | Status |
|------|-------------|--------|
| **4a. Audit remaining truncation paths** | Enumerated every `PayloadBuffer<IntPtr>` emission for Optional types where inner size > 8B. Categorized by fix strategy: params (wrapper-owned, async) and returns (sync). | **COMPLETE** |
| **4b. Fix parameter truncation for wrapper-owned + async methods** | Broadened C# `DangerousGetHandle()` gate to fire when `IsLargeOptionalParam` AND any Swift wrapper exists (`HasOptionalPointerWrapper \|\| UsesWrapperLibrary \|\| IsAsync \|\| opaqueReturn`). Added shared helpers to `OptionalPointerWrapperEmitter` (`ShouldWidenParam`, `GetDerefCode`, `GetReturnBufferCode`). Modified 5 Swift wrapper emitters (ArraySlice, DefaultParam, ClosureCdecl, opaque return, async) to accept `UnsafeRawPointer` for large Optional params with `.assumingMemoryBound(to:).pointee` dereference. | **COMPLETE** |
| **4c. Fix sync Optional return value truncation** | P/Invoke returns void + `_optRetPtr` out-buffer instead of `IntPtr` register return. Swift wrappers write result to `_resultBuf` via `copyMemory`. C# allocates `stackalloc` buffer, passes as `_optRetPtr`, reads back via `MarshalFromSwift`. Applied to all wrapper paths (standalone, ArraySlice, DefaultParam, ClosureCdecl). Getter returns also handled. Guard: only activates when `HasOptionalPointerWrapper \|\| UsesWrapperLibrary` ensures Swift wrapper exists. | **COMPLETE** |

**Step 4b key files**: `OptionalPointerWrapperEmitter.cs` (shared helpers), `WrapperEmitter.Marshalling.cs` (broadened gate + opaque return), `ArraySliceNormalizationEmitter.cs`, `DefaultParameterOverloadEmitter.cs`, `ClosureEmitter.SwiftWrapper.cs`, `WrapperEmitter.Async.cs`.

**Step 4c key files**: `BoundGenericsHandler.cs` (`IsLargeOptionalReturn`), `MethodHandler.cs` (extended gate), `OptionalPointerWrapperEmitter.cs` (return buffer + getter support), `PInvokeEmitter.cs` (void return + `_optRetPtr` param), `WrapperEmitter.cs` (`EmitOptionalReturnBuffer`, return prefix), `WrapperEmitter.Return.cs` (`EmitOptionalReturnBufferRead`).

**Acceptance gate**: Passed. All 25 libraries compile with 0 errors. 3,419 unit tests passing (0 failures). Unit tests cover each path: standalone param/return, wrapper-owned param/return, getter return, Closure Cdecl + large Optional return interaction.

---

### 5. SwiftDictionary Projection — COMPLETE

**Priority**: P1 | **Effort**: Medium (2 sessions) | **Risk**: Low | **Status**: Complete

`SwiftDictionary<TKey, TValue>` now implements `IReadOnlyDictionary<TKey, TValue>` and the generator projects all dictionary types to idiomatic .NET interfaces. 144 occurrences of `SwiftDictionary` in public signatures across 5 libraries reduced to 0.

| Step | Description | Status |
|------|-------------|--------|
| **5a. Runtime: IReadOnlyDictionary interface** | `SwiftDictionary<TKey, TValue>` implements `IReadOnlyDictionary<TKey, TValue>`. Added `TryGetValue`, `ContainsKey`, indexer with `KeyNotFoundException`, `Keys`, `Values`, `GetEnumerator` (Swift stdlib iterator P/Invokes: `makeIterator` + `Iterator.next()`), `FromDictionary()` static factory, `RemoveValue()`, `RemoveAll()`. `AsProjected()` lazy projection (value-only and key+value variants). `SwiftDictionaryProjection` types with proper reverse-key disposal. | **COMPLETE** |
| **5b. Generator: Dictionary type conversion** | `TypeConversionHandler`: `IsSwiftDictionary()`, `GetRawDictionary{Key,Value}Type()`, `IsDictionary{Key,Value}TypeConverted()`. Returns use `IReadOnlyDictionary<K,V>`, parameters use `IDictionary<K,V>`. Key/value types converted independently (SwiftString→string, SwiftArray→IReadOnlyList). `WrapperEmitter.Return.cs`: dictionary return emission. `WrapperEmitter.Marshalling.cs`: dictionary parameter emission with `ToList()` + `try/finally` disposal for converted elements, `Optional<Dictionary>` with intermediate dictionary and optional wrapper disposal. | **COMPLETE** |

**Runtime correctness details**:
- **Arc.Retain for iterator lifetime**: `Arc.Retain` before `makeIterator()` P/Invoke balances the iterator's VWT Destroy, preventing over-release of dictionary storage when the iterator is cleaned up.
- **Exception-safe iteration**: `resultConsumed` boolean flag tracks whether each `Iterator.next()` result has been moved via `MarshalFromSwift`. VWT Destroy called for unconsumed results (`.none` at loop end, or `.some` on exception) in `finally` block.
- **NativeMemory leak fixes**: Getter and `RemoveValue` result buffers wrapped in `try/finally { NativeMemory.Free(...) }`.
- **Optional detection**: Uses `VWT.GetEnumTag` (not byte-zero inspection) to correctly handle zero-valued entries.

**Generator disposal correctness**:
- Converted `Optional<Dictionary>` branch uses inner variable pattern (`{csName}SwiftInner`) for conditional assignment, then `using var {csName}Swift = {csName}SwiftInner` for scope-based disposal of the optional wrapper.
- Intermediate `FromDictionary()` result disposed via `try/finally` after `NewSome()` copies payload.
- Non-converted `Optional<Dictionary>` branch uses `using var` inside `if` block for intermediate dictionary disposal.
- Converted element temporaries (SwiftString keys/values) materialized via `ToList()` and disposed in `finally` block.

**Key files**: `SwiftDictionary.cs`, `SwiftDictionaryProjection.cs`, `TypeConversionHandler.cs`, `WrapperEmitter.Return.cs`, `WrapperEmitter.Marshalling.cs`, `SwiftDictionaryTests.cs` (26 tests).

**Acceptance gate**: Passed. All 25 libraries compile with 0 errors. 3,419 unit tests + 700 integration tests + 207 runtime library tests passing. `SwiftDictionary` no longer appears in any public method/property signatures across all validated libraries.

---

### 6. Public API Projection Completeness

**Priority**: P1 | **Effort**: Medium (1-2 sessions) | **Risk**: Low

Unified item covering all remaining runtime type leakage in public signatures. Individual items (dictionary, optional) handle the biggest categories; this covers the long tail.

| Step | Description | Effort | Files |
|------|-------------|--------|-------|
| **6a. Consistent SwiftOptional projection** | Apply `GetIdiomaticCSharpType()` to async return types (`WrapperEmitter.Async.cs:133-175`) and tuple elements (`WrapperEmitter.Return.cs:593-595`). Currently only properties and regular methods project `SwiftOptional<T>` to `T?`. | Low | `WrapperEmitter.Async.cs`, `WrapperEmitter.Return.cs`, `MethodSignature.cs` |
| **6b. AsyncStream inner type projection** | `IAsyncEnumerable<SwiftArray<UIEvent>>` should be `IAsyncEnumerable<IReadOnlyList<UIEvent>>`. Apply array projection inside async stream generic parameter. | Low | `TypeConversionHandler.cs`, async emitter |
| **6c. Remaining AnyType cleanup** | After cross-module resolution (Task 1), audit remaining `AnyType` occurrences. For types still unresolvable, emit `[UnsupportedSwiftType]` with the original Swift type name instead of silently using `object`. | Medium | `TypeDatabaseExtensions.cs`, `MemberEmissionValidator.cs` |
| **6d. SwiftArray in non-projected contexts** | `SwiftArray<T>` appears unprojected in ~24 locations (async stream elements, enum factory params). Apply projection consistently. | Low | `TypeConversionHandler.cs` |
| **6e. Runtime existential marshalling for Optional projection** | Extend `SwiftMarshal.MarshalFromSwift<T>()` to support interfaces and `object` as `T`. When `T` is an interface, read the `ExistentialContainer` from the Swift payload and construct the corresponding proxy class. When `T` is `object`, box the container. This unblocks `SwiftOptional<IProtocol>` and `SwiftOptional<object>` — currently deferred because `.Some` throws `NotSupportedException` for non-concrete types. The reverse path (`MarshalToSwift` constructing `ExistentialContainer` from .NET-created objects) requires protocol witness table synthesis and remains deferred. Currently 0 occurrences across 25 libraries, but blocks full existential cleanup and will grow as libraries add Optional protocol APIs. | Medium | `SwiftMarshal.cs`, `SwiftOptional.cs` |
| **6f. QuartzCore auto-bridging** | Add `QuartzCore` to `AppleObjCFrameworkModules` so its class types (CALayer, CAAnimation, etc.) resolve instead of falling through to AnyType. QuartzCore is the only confirmed Apple framework where the Swift module name doesn't match the C# namespace (`QuartzCore` → `CoreAnimation` in `Microsoft.iOS.dll`). Add a `ModuleToCSharpNamespaceOverrides` dictionary and `ResolveObjCBridgedNamespace()` method (consolidating the existing `ObjectiveC`→`Foundation` special case). Add QuartzCore non-class types to `AppleFrameworkValueTypes` (CATransform3D, CACornerMask, etc.). Types not in `Microsoft.iOS.dll` (like `CALayerContentsGravity`) must also be excluded. Fixes Lottie's last AnyType (1→0). | Low | `TypeDatabaseExtensions.cs` |

**Acceptance gate**: Combined grep for `SwiftOptional<|SwiftArray<|SwiftDictionary<|ExistentialContainer` in public signatures across all 25 libraries < 10 total occurrences (down from ~230). After 6e, `SwiftOptional<ExistentialContainer` drops to 0 (generator can emit `SwiftOptional<IProtocol>` once runtime supports it). After 6f, Lottie AnyType drops to 0.

---

### 7. Empty Protocol Interface Completeness

**Priority**: P1 | **Effort**: Medium (1-2 sessions) | **Risk**: Low

11 protocol interfaces across StripeConnect (7 delegate protocols) and StripeIssuing (3 key provider protocols) are generated with zero members, making them unimplementable. Some empties may resolve after cross-module resolution (Task 1) — members could be skipped due to AnyType fallback on cross-module parameter types.

| Step | Description | Effort | Files |
|------|-------------|--------|-------|
| **7a. Post-cross-module audit** | After Task 1, regenerate Stripe modules and count remaining empty interfaces. Identify root cause per interface (AnyType fallback vs genuinely memberless protocol). | Low | Generated output analysis |
| **7b. Emit diagnostic on empty interfaces** | For protocols that genuinely have members in ABI JSON but all were skipped, emit `[Obsolete("...", DiagnosticId = "SB0004")]` with the skip reasons. For protocols with zero ABI members, consider suppression. | Low | `ProtocolHandler.cs`, `ProtocolProxyEmitter.cs` |
| **7c. Reduce member skip rate** | For members skipped due to non-blittable existential params or unsupported signatures, evaluate whether projection improvements (Task 2, 6) resolve the skip. Includes closure-bearing methods: Session E broadened the skip gate to skip ALL closure params from protocol interfaces/receivers because `GetCSharpTypeName(forAbiMarshalling: true)` can't resolve closures (even supported ones like `Optional<() -> Void>`) — falls through to AnyType. Recovering these requires implementing closure marshalling in protocol proxy receivers (`ProtocolProxyEmitter.Helpers.cs`). | Medium | `MemberEmissionValidator.cs`, `ProtocolHandler.cs` |

**Acceptance gate**: Empty protocol interfaces with skipped-member root cause drop to 0. Genuinely empty protocols (no ABI members) get explicit diagnostic.

---

### 8. Remove `Info` Suffix on Nested Types

**Priority**: P1 | **Effort**: Medium (1-2 sessions) | **Risk**: Medium

50+ types across libraries have `Info` appended: `PaymentSheet.ConfigurationInfo` instead of `PaymentSheet.Configuration`. The suffix avoids CS0542 (property/type name collision) by renaming the nested type.

**Proposed change**: Rename the colliding *property* instead, keeping the type name clean. Types appear in `new`, `typeof`, variable declarations, generic constraints, and documentation. Properties appear only at call sites.

| Step | Description | Effort | Files |
|------|-------------|--------|-------|
| **8a. Reverse the rename priority** | In `NameProvider.ComputeNestedTypeRenames()`, instead of renaming the type, rename the property. Property gets a suffix (`Value`, `Instance`, or context-derived). | Medium | `NameProvider.cs:552-649` |
| **8b. Verify descendant propagation** | Property renames need TypeDatabase propagation like type renames currently do. | Medium | `NameProvider.cs`, `PropertyHandler.cs` |

**Acceptance gate**: Grep for `Info` suffix on nested types in generated output — only types that genuinely end in "Info" in Swift should have the suffix. Count should drop from ~50+ to <5.

---

### 9. Synchronous Error Detail Extraction

**Priority**: P1 | **Effort**: Medium (1-2 sessions) | **Risk**: Low

Synchronous throwing methods throw `SwiftRuntimeException("Call to Swift method {name} failed.")` — losing all error detail. Async methods already extract the actual error message and typed error value via callback parameters.

| Step | Description | Effort | Files |
|------|-------------|--------|-------|
| **9a. Extract error message in sync path** | After checking `error.Value != null`, marshal the error existential to extract `localizedDescription` via a Swift wrapper function (`SBW_Error_GetDescription`). | Medium | `WrapperEmitter.cs:546-589`, new Swift wrapper |
| **9b. Extract typed error in sync path** | For typed throws, use `SwiftMarshal.MarshalFromSwift<TError>()` on the error existential (same pattern as async at `WrapperEmitter.Async.cs:1490`). | Medium | `WrapperEmitter.cs:550-589` |

**Acceptance gate**: All sync `throw new SwiftRuntimeException(...)` include actual Swift error message. Update expectations in `ThrowingMethodTests.cs`.

---

## P2 — Quality Gaps

### 10. Mark NotSupportedException Proxy Members

**Priority**: P2 | **Effort**: Low (1 session) | **Risk**: Low

~320 protocol proxy members throw `NotSupportedException` when called on Swift-backed existential containers, with no compile-time indication.

| Step | Description | Effort | Files |
|------|-------------|--------|-------|
| **10a. Add `[Obsolete]` with SB0003 diagnostic** | Emit on proxy members that throw. Message explains the limitation and reason (non-blittable type, async, throwing, subscript). | Low | `ProtocolProxyEmitter.InterfaceImpl.cs` |

**Acceptance gate**: `SB0003` count matches `NotSupportedException` count in all generated proxy classes.

---

### 11. Suppress Internal/Telemetry Types

**Priority**: P2 | **Effort**: Low-Medium (1 session) | **Risk**: Low

Types like `CameraHardwareInfoPinglet` appear in public API when they should be internal.

| Step | Description | Effort | Files |
|------|-------------|--------|-------|
| **11a. swiftinterface-based access filtering** | Types not present in `.swiftinterface` are internal (even if in ABI JSON). Mark with `[EditorBrowsable(Never)]` or suppress entirely. This is the source of truth. | Medium | `SwiftInterfaceAccessParser.cs`, `MemberEmissionValidator.cs` |
| **11b. Heuristic fallback** | Only when swiftinterface is unavailable: types matching patterns (`*Pinglet*`, `*Telemetry*`, `_*`) get `[EditorBrowsable(Never)]`. | Low | `ClassHandler.cs` or new filter |

**Acceptance gate**: Public type count in generated output decreases for BlinkID, StripePayments. Swiftinterface-based filtering covers >95% of cases.

---

### 12. Normalize Async Method Names

**Priority**: P2 | **Effort**: Low (1 session) | **Risk**: Low

Swift async methods get `Get` prefix from the Swift wrapper: `GetPresentAsync`, `GetConfirmAsync`. .NET convention: `PresentAsync`, `ConfirmAsync`.

| Step | Description | Effort | Files |
|------|-------------|--------|-------|
| **12a. Strip `Get` prefix from async wrappers** | When an async method name starts with `Get` and the original Swift method doesn't, strip it. Expand the partial implementation from WU phase. | Low | `WrapperEmitter.Async.cs`, `MethodHandler.cs` |

**Acceptance gate**: `GetPresentAsync`, `GetConfirmAsync` no longer appear in generated output.

---

### 13. Improve Parameter Naming

**Priority**: P2 | **Effort**: Low-Medium (1 session) | **Risk**: Low

Swift unnamed parameters become `_` and `_2`. Factory method parameters become `value0`. Generic type parameters become `T0`.

| Step | Description | Effort | Files |
|------|-------------|--------|-------|
| **13a. Use Swift argument labels** | ABI JSON contains both parameter names and argument labels. Use argument label when parameter name is `_`. | Low | `MethodSignature.cs`, parser |
| **13b. Rename generic type params** | `T0` → `T` (single param), `T0`/`T1` → `TKey`/`TValue` or `TInput`/`TOutput` based on constraint names or position heuristics. | Low | Emitter generic handling |

**Acceptance gate**: `_2`, `value0`, `T0` counts in generated output drop >80%.

---

### 14. Lightweight Regression Gate

**Priority**: P2 | **Effort**: Low (1 session) | **Risk**: Low

Protect upcoming refactors with a minimal smoke test before full CI. Not a replacement for CI — a fast local pre-push check.

| Step | Description | Effort | Files |
|------|-------------|--------|-------|
| **14a. `check-regressions.sh` script** | Runs: unit tests, regenerates Stripe multi-module bindings, compiles all 25 library outputs, diffs public API surface against baseline. Fails on new AnyType fallbacks or compile errors. Target: <5 min. | Medium | New `check-regressions.sh` |
| **14b. API surface baseline** | Generate `api-baseline.txt` (public type/member signatures) from current output. Diff against baseline on each run to catch unintended API changes. | Low | New `generate-api-baseline.sh` |

**Acceptance gate**: Script runs in <5 min and catches regressions introduced by P0/P1 work.

---

### 15. Actor-Aware Wrapper Emission

**Priority**: P2 | **Effort**: Medium | **Risk**: Low

(Carried forward from previous roadmap.)

Swift 6 enforces actor isolation as hard type-system errors. Parse `@MainActor` annotations from `.swiftinterface` files and emit matching actor isolation on generated wrapper functions.

| Step | Description | Effort | Files |
|------|-------------|--------|-------|
| **Parse @MainActor from swiftinterface** | `SwiftInterfaceAccessParser` already extracts other annotations. Add `@MainActor` / `@_Concurrency.MainActor`. | Low | `SwiftInterfaceAccessParser.cs` |
| **Emit actor isolation on wrappers** | When a protocol/class is `@MainActor`, emit `@MainActor` on generated wrapper functions. | Medium | `EveryProtocolEmitter.cs`, `WitnessDispatchEmitter.cs` |
| **Handle custom actors** | Types like `BlinkIDEventStream` need actor execution context. | Medium | Same |
| **Remove -strict-concurrency=minimal** | Once actor-aware emission covers known cases. | Low | `SwiftWrapperCompiler.cs` |

**Acceptance gate**: BlinkIDUX wrapper compiles with 0 actor isolation errors.

---

## P3 — Polish & Infrastructure

### 16. CI Integration

**Priority**: P3 | **Effort**: Large | **Risk**: Medium

GitHub Actions workflow. macOS runner. Tier 1 on every PR, Tier 2 before merge, Tier 3 nightly. Real-world library validation on merge. Builds on the lightweight regression gate (Task 14).

**Key files**: New `.github/workflows/`, existing test scripts

---

### 17. Library Validation Expansion

**Priority**: P3 | **Effort**: Medium per library | **Risk**: Low

Runtime test apps for additional libraries. Stripe end-to-end (multi-module with dependency chain) is the key target after cross-module resolution (Task 1) ships.

**Verification**: Per-library `build-all.sh` + `validate-sim.sh`

---

### 18. Performance Benchmarks

**Priority**: P3 | **Effort**: Medium | **Risk**: Low

BenchmarkDotNet harness measuring interop overhead. 5 CI perf smoke scenarios.

**Design**: `Future/interop-performance-validation-plan.md`

---

### 19. SwiftUI Bridge Corpus

**Priority**: P3 | **Effort**: Medium | **Risk**: Low

Track bridge coverage across 10+ libraries with 3-tier metrics (generated / typechecked / runtime-validated).

**Design**: `Future/swiftui-bridge-v2-plan.md` (Phase 4)

---

## Multi-Session Efforts (P4)

Too large for single sessions. Each needs a **planning session first** to scope implementation.

### Class Inheritance Hierarchy

**Effort**: Very Large (5+ sessions)
**Prerequisite**: ObjC binding integration (informs NSObject bridging)

Emit C# class hierarchies mirroring Swift type graph. Requires: cross-module inheritance chain resolution from ABI JSON `inheritsFrom`, diamond inheritance handling with protocols, `base()` constructor chains, bridging into `Foundation.NSObject`.

### ObjC Binding Integration

**Effort**: Large (3-5 sessions)
**Design**: `Future/objc-binding-integration.md`

Replace Objective Sharpie. Uses `clang -ast-dump=json`. Same CLI/SDK for Swift and ObjC. ~1,500-2,000 lines new code.

### Emitter Architecture Redesign

**Effort**: Very Large (5+ sessions)
**Design**: `Future/emitter-redesign-proposal.md`

Three-phase architecture: type pre-processing, type processing, emission from representations.

### Multi-Platform Support

**Effort**: Large (3+ sessions)
**Design**: `Future/dx-multi-framework-auto-detection.md` (Platform Coverage)

Extend beyond iOS to Mac Catalyst, macOS, tvOS.

---

## Blocked on Upstream (.NET Runtime)

Workarounds are in place. Draft bug reports ready in `Future/upstream-bug-reports-draft.md`.

| Issue | Root Cause | Current Mitigation | Unblocked When |
|-------|-----------|-------------------|----------------|
| **SafeHandle finalizer crashes on Mono** | `VWT->Destroy()` via indirect CallConvSwift → JIT assertion | MutableProps tests at Tier 3; consumers must call `Dispose()` | Mono JIT CallConvSwift fix |
| **Non-blittable types with CallConvSwift** | .NET requires all CallConvSwift P/Invoke params be blittable | Wrapper methods for known patterns; `MonoJitRiskDetector` flags | dotnet/runtime adds managed type marshalling for CallConvSwift |
| **Async runtime tests (32 tests, all Tier 3)** | Mono JIT assertion on CallConvSwift in async P/Invoke | Tests written and ready; tagged Tier 3 | Same as above |
| **Non-primitive closure Cdecl** | Strategy B only covers primitive-arg closures | Non-primitive closures fall back to CallConvSwift | Mono JIT fix OR Swift-side marshal adapters |
| **SafeHandle in async P/Invoke** | .NET runtime doesn't preserve SafeHandle through async continuation | Singleton pattern detection + IntPtr conversion | dotnet/runtime adds SwiftSelf register support with async Task capture |
| **VWT InitializeWithCopy** | Indirect CallConvSwift function pointer in `MarshalToSwift` | No known test failures yet | Same as VWT Destroy |

**Tracking issues**: [#93631](https://github.com/dotnet/runtime/issues/93631) (.NET 9), [#108662](https://github.com/dotnet/runtime/issues/108662) (.NET 10), [#64215](https://github.com/dotnet/runtime/issues/64215) (CallConvSwift), [#80905](https://github.com/dotnet/runtime/issues/80905) (NativeAOT iOS).

---

## Known Generator Bugs (Tracked, not prioritized)

Workarounds exist for all. Not blocking any library validation.

| Bug | Impact | Workaround |
|-----|--------|------------|
| String enum raw values use case names | ABI JSON lacks individual case raw values | Case names used; cosmetic only |
| `UnsafePointer<T>` → AnyType | No concrete projection for immutable pointers | Use `UnsafeMutablePointer<T>` |
| Throwing closure thunks | `SwiftString` return emitted as `void*` | Exclude throwing closures |
| `async throws(ErrorType)` free functions | Emit `_payload`/`this` in static context | Guarded — no runtime impact |
| ExistentialContainer0 in tuple element | Lottie edge case | Blocked by `HasClosureUnsafeTupleElements` safety gate (params and returns) |
| Bare `Any` in generic positions → AnyType | CS0311 when `object` used as generic arg with `ISwiftObject` constraint; `TypeMetadata.GetTypeMetadataOrThrow<object>()` throws at runtime | AnyType fallback is correct behavior — `SwiftAny` wrapper or runtime metadata registration needed to unblock. See `Completed/binding-api-work.md` for full analysis. |
