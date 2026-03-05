# Preview 14 Bug Fixes & Validation Hardening

**Date**: March 4, 2026
**Source**: `swift-dotnet-packages/ISSUES-preview14.md` — issues discovered validating BX1-BX4 commits against 15 real-world libraries (5 library families, 15 binding projects, 5 sim test suites).
**Impact**: 12/15 libraries building clean in preview.13 dropped to 3/15 in preview.14.

---

## Session P14A: Bug Fixes ✅

**Status**: Complete. All fixes applied, 5371 unit tests passing. Note: the 53/53 library validation baseline was later discovered to be incorrect (see P14B — restore bug masked 42 real failures).

### P14-1: QuartzCore Namespace Mapping Not Applied Uniformly ✅

**Affects**: Lottie (LottieAnimationLayer.AnimationLayer property)

**Fix**: Centralized `MapSwiftModuleToNetNamespace()` and `MapQualifiedTypeToNet()` in `MarshallingHelpers.cs`. All emission paths (ForeignTypeExtensionEmitter, TypeDatabaseExtensions, ObjC base type resolution) now route through this single mapping. Covers QuartzCore→CoreAnimation, Dispatch→CoreFoundation, AVFAudio→AVFoundation, UniformTypeIdentifiers→UniformTypeIdentifiers.

**Tests**: 6 tests in `MarshallingHelpersTests.cs` (namespace mapping + qualified type mapping).

---

### P14-2: IAnyInterpolatable DIM Regression ✅

**Affects**: Lottie (LottieColor, LottieVector1D, LottieVector3D)

**Fix**: Two changes: (1) `ProtocolConformanceValidator` — removed `HasSelfRequirement` guard that was blocking conformance validation for protocols with extension defaults. (2) `ProtocolHandler` — restored `HasDirectMethodDefault`/`HasDirectPropertyDefault` (not `HasMethodDefault`) so sub-protocol defaults don't propagate upward to parent protocol interfaces. Only direct defaults become DIMs.

**Tests**: Existing `ProtocolHandlerOutputTests.Emit_MethodWithSubProtocolDefault_DoesNotEmitDIM` validates the semantic distinction.

---

### P14-3: SwiftHandle.Pointer Property Does Not Exist ✅

**Affects**: Lottie, StripeCore, all ObjC-rooted types

**Fix**: `ClassHandler.cs` — changed `handle.Pointer` to `handle.Handle` in ObjC-rooted constructor emission. Updated all test assertions in `ClassObjCRootedTests.cs`.

**Tests**: `ClassObjCRootedTests.Emit_ObjCRootedConstructor_UsesHandleDotHandle`.

---

### P14-4: SwiftOptional\<CALayer\> Type Conversion for ObjC-Rooted Types ✅

**Affects**: Lottie (LottieAnimationLayer.AnimationLayer optional property)

**Fix**: `OptionalProjection.cs` — added `ObjCRootedClassProjection` alongside `ObjCBridgedProjection` in both parameter and return plan checks. ObjC-rooted types use nullable pointer ABI (nil = IntPtr.Zero, Some = ObjC pointer) — no SwiftOptional wrapper needed.

**Tests**: 4 tests in `CompositeProjectionTests.cs` (direct return, indirect return, parameter, public type).

---

### P14-5: Simple Enum Incompatible with ISwiftObject Generic Constraint ✅

**Affects**: BlinkIDUX (BlinkIDScanningAlertType in `ScanningResult<T, U>`)

**Fix**: Two-pass approach in `ModuleProcessor`: (1) `CollectBoundGenericEnumArgs()` scans all types to find enums used as generic type arguments. (2) `DemoteSimpleEnumsUsedAsGenericArgs()` clears the `SimpleEnum` flag post-scan. `EnumHandler` checks for demotion with inverted logic (`wasDemotedFromSimple`) to handle missing TypeRecord in test contexts.

**Tests**: 2 tests in `SimpleEnumDemotionTests.cs` (demoted when used as generic arg, retained when not).

---

### P14-6: ObjC-Rooted Type Emits .Payload References ✅

**Affects**: StripeCore (STPAPIClient)

**Fix**: `PInvokeEmitter.cs` and `WrapperEmitter.Marshalling.cs` — added `IsObjCRooted` checks to use `.Handle` instead of `.Payload.DangerousGetHandle()`. Extended in P14-11 audit (see below).

**Tests**: `ClassObjCRootedTests.IsObjCRooted_ObjCRootedFlag_ReturnsTrue`.

---

### P14-7: Cross-Module Type References Without Assembly Dependencies — Deferred to P14B

**Affects**: 9 Stripe libraries

**Status**: Infrastructure gap — requires multi-library compilation validation mode. Deferred to P14B session.

---

### P14-8: GetHashCode() Returns 0 for OptionSet Types (E10) ✅

**Affects**: Nuke (ImagePipeline.Cache.Caches)

**Fix**: `TypeHandlerHelpers.cs` and `ClassHandler.cs` — OptionSet and RawRepresentable conformances now imply Hashable when computing `_implementsHashable`. This is a practical assumption: all encountered RawRepresentable types have Hashable raw values (Int, String, etc.). ABI JSON often omits transitively-acquired conformances.

**Tests**: 2 tests in `TypeHandlerHelpersTests.cs` (OptionSet implies Hashable, RawRepresentable implies Hashable).

---

### P14-9: OpaqueSwiftType Annotations Not Being Emitted (E8) — Working as Designed

**Affects**: All libraries

**Status**: Investigated — the condition (`emittable == 0 && skipped > 0`) is simply never met in current libraries. All types either have emittable members or are entirely empty. No code change needed.

---

### P14-10: SB0001 Not Propagated to Async Wrappers (E19) ✅

**Affects**: StripeIdentity (`PresentAsync` calls `Present` which has SB0001)

**Fix**: `MethodHandler.cs` — added `GetSafetyObsoleteAttribute()` static method. Completion handler async wrappers (`TryEmitCompletionHandlerOverload`) now propagate SB0001/SB0002 from the underlying method.

**Tests**: 3 tests in `ConsumerSafetyAttributeTests.cs` (JIT risk → SB0001, missing symbol → SB0002, no risks → null).

---

### P14-11: ObjC-Rooted .Payload → .Handle Audit ✅

**Discovered during**: LLM code review audit of P14-6

**Fix**: Extended ObjC-rooted `.Handle` vs `.Payload` handling to four additional code paths:
1. `MethodClosureBridge.ClassifyParam()` — ObjC-rooted classes now return `ObjCHandle` instead of `PayloadHandle`
2. `ExtensionMarshallingHelper.ClassifyParameterType()` — ObjC-rooted → `ParamKind.ObjCClass`
3. `ExtensionMarshallingHelper.ClassifyReturnType()` — ObjC-rooted → `ReturnKind.ObjCClass`
4. `CrossModuleExtensionEmitter` — added `GetSelfExpression(bool isObjCRooted)` helper; all 3 self-expression sites now use `.Handle` for ObjC-rooted types

**Tests**: 2 tests in `MethodClosureBridgeTests.cs` (ObjC-rooted → ObjCHandle, pure Swift → PayloadHandle), 4 tests in `TypeHandlerHelpersTests.cs` (classify param/return + P/Invoke arg expressions).

---

## Session P14B: Validation Pipeline Hardening ✅

**Status**: Complete. V1 (dependency gate), V3 (7 compile-smoke tests), V5 (covered by V3).

**Goal**: Prevent this class of failure from leaking again. Every issue in P14A represents a gap in the validation pipeline.

**Key discovery**: The restore fix revealed that 42/53 validation targets have real compile errors (previously masked by silent NETSDK1004 failures when `dotnet build --no-restore` ran without `project.assets.json`). True baseline is 11/53 passing.

### V1: Multi-Library Compilation Mode ✅

**Gap addressed**: P14-7 (cross-module assembly refs)

**Implementation**:
- Added `"dependencies"` arrays to 11 of 14 Stripe products in `validation-libraries.json`
- Added Phase 3.5 "Dependency Gate" to `validate-libraries.sh`: checks if dependency DLLs exist, creates csproj with assembly references, compiles with `dotnet restore` + `dotnet build`
- Fixed restore gap: fallback `Test.csproj` now gets `dotnet restore` before compile
- Added infrastructure failure detection (NETSDK1004, MSB errors not counted as "0 CS errors")
- Changed solution build to targeted project builds (generator + runtime only)
- Dependency gate reports: passed/failed/skipped per target, with "all skipped" display when dependencies haven't compiled

**Scope**: Stripe family (11 dependent targets). Infrastructure generalizes to any library with declared dependencies.

---

### V2: Cross-Feature Interaction Test Matrix

**Gap addressed**: P14-1, P14-4, P14-6 (feature interaction bugs)

**Problem**: Each BX feature was tested in isolation. The bugs are all at boundaries between features — optional + ObjC-rooted, enum + generic constraints, namespace mapping + property emission.

**Implementation**:
Add a dedicated test class `FeatureInteractionTests.cs` with cases for:
- Optional\<ObjCRootedType\> (BX1 x BX4)
- Optional\<SimpleEnum\> (BX1 x BX2)
- SimpleEnum as generic type arg (BX2 x module-level constraints)
- ObjC-rooted type with protocol conformance DIMs (BX4 x protocol extensions)
- ObjC-rooted type in cross-module reference (BX4 x BX1)
- ObjC-rooted type namespace in all emission paths (BX4 namespace mapping uniformity)

Each test constructs a synthetic `ModuleDecl` combining features from multiple BX sessions and verifies the complete generated output.

---

### V3: Compile-Smoke Tests for Generated Output ✅

**Gap addressed**: P14-3, P14-5, P14-6 (string-pattern tests miss type system errors)

**Implementation**: 7 compile-smoke tests in `CompileSmokeTests.cs` that generate representative C# code and `dotnet build` it against real `Swift.Runtime.dll`:
1. `SwiftClass_HandleBoilerplate_Compiles` — P14-3 regression guard (handle.Handle not .Pointer)
2. `ObjCRootedClass_HandleProperty_Compiles` — P14-6/P14-11 regression guard (.Handle not .Payload)
3. `SimpleEnum_WithExtensions_Compiles` — P14-5 regression guard (enum + extensions)
4. `OptionalObjCRooted_NullablePattern_Compiles` — P14-4 regression guard (nullable ObjC type)
5. `CrossModuleReference_WithAssemblyRef_Compiles` — P14-7 regression guard (cross-module types)
6. `ProtocolProxy_Compiles` — Protocol proxy pattern
7. `OptionSet_GetHashCode_Compiles` — P14-8 regression guard (GetHashCode on OptionSet)

Uses `MakeSwiftClassBody()` helper for full `ISwiftObject` boilerplate. Each test writes temp csproj + code, runs `dotnet restore` + `dotnet build`, asserts exit code 0.

---

### V4: Regression Guard Tests

**Gap addressed**: P14-2 (regression of previously-fixed behavior)

**Problem**: The DIM emission fix from `84a9f3f` had no dedicated regression test. When BX3 modified adjacent code, the fix silently broke.

**Implementation**:
- Policy: every bug fix and every feature must have a test that would fail if the fix/feature were reverted
- Audit existing fixes that lack dedicated tests (DIM emission, protocol extension defaults, conformance graph)
- Add targeted regression tests for each
- Mark these tests with `[Trait("Category", "Regression")]` so they're visible as a group

---

### V5: Runtime API Compatibility Check ✅

**Gap addressed**: P14-3 (generated code references non-existent runtime API)

**Status**: Covered by V3. The compile-smoke tests build against the real `Swift.Runtime.dll`, so any reference to a non-existent runtime API (like `SwiftHandle.Pointer`) fails the build.

---

## E-Series Disposition

Items discovered during developer review of the 3 clean-building libraries. Categorized by destination.

### Already in P14A (promoted to bug fixes)

| Item | Description | As |
|------|-------------|-----|
| E8 | OpaqueSwiftType not emitting | P14-9 |
| E10 | GetHashCode returns 0 for OptionSet | P14-8 |
| E19 | SB0001 not propagated to async | P14-10 |

### In P14D (quality polish session, moved from P14C)

| Item | Description |
|------|-------------|
| E2 | ExistentialContainer visible on proxy types → explicit interface impl |
| E5 | `stp_` internal properties exposed → `[EditorBrowsable(Never)]` |
| E9 | Duplicate Utf8Slice in every extension class → shared type |
| E11 | `RawRepresentable<Int>` enums → BX2 simple path |
| E12 | Namespace-like enums (zero cases) → `static class` |
| E13 | Hash suffix in factory method names → strip |
| E15 | `@_spi` types surfaced as public → skip or `internal` |
| E16 | `@available` → `[SupportedOSPlatform]` |

### Deferred to usability roadmap

| Item | Description | Reason |
|------|-------------|--------|
| E1 | `SwiftOptional<T>` in closure params | Already tracked as "Optional<Primitive/Enum> in closures" |
| E3 | `AnyError` → Exception wrapping | Runtime design decision, needs `SwiftException : Exception` |
| E14 | `ConfigurationValue` property name collision | Alternative disambiguation strategy needed |
| E17 | `Array<ObjCClass>` properties not bound | Extends collection projection for ObjC types |
| E18 | Cross-module protocol conformances dropped | Threading conformances across modules |

### Not actionable (design tradeoffs)

| Item | Reason |
|------|--------|
| E4 | Payload enum IDisposable is correct — associated values need heap allocation. |
| E6 | `[UnsupportedSwiftType]` annotations are informational diagnostic. |
| E7 | `SwiftInheritanceChain` constructor is `protected` — acceptable scope. |

---

## Session Plan

Five sessions. Each scoped to complete in a single Claude session. Sessions 1-3 and 5 are complete. Session 4 is planned work.

### Session 1: P14A — Bug Fixes + Regression Guards

All 10 bug fixes, each with a dedicated regression test (V4) and cross-feature interaction tests (V2) where the fix involves feature boundaries.

**Order** (dependencies flow downward):
1. **P14-3** — `handle.Pointer` → `handle.Handle` (trivial, unblocks ObjC-rooted compile)
2. **P14-1** — Centralize namespace mapping (unblocks P14-4)
3. **P14-6** — `.Payload` → `.Handle` audit for ObjC-rooted types
4. **P14-2** — DIM regression (identify BX3 breakage, restore)
5. **P14-4** — Optional x ObjC-rooted marshalling (depends on P14-1 namespace fix)
6. **P14-5** — Simple enum generic constraint gate
7. **P14-8** — GetHashCode returning 0 for OptionSet
8. **P14-9** — OpaqueSwiftType annotation investigation
9. **P14-10** — SB0001 propagation to async wrappers
10. **P14-7** — Cross-module assembly references (largest item, do last)

**Tests written alongside fixes:**
- V4 regression guards: one per fix (10 tests minimum)
- V2 interaction tests: Optional x ObjC-rooted, enum x generic constraint, namespace x all emission paths

**Gate**: `run-tests.sh` + `validate-libraries.sh` must pass.

### Session 2: P14B — Validation Pipeline Hardening ✅

**Status**: Complete. V1 + V3 + V5 delivered. 7 compile-smoke tests passing. Dependency gate infrastructure in place.

**Note**: The restore fix revealed 42/53 targets have pre-existing compile errors (previously masked). True baseline updated to 11/53. The 42 failures are pre-existing generator bugs, not P14B regressions.

### Session 3: P14C — Compile Error Fixes (11/53 → 38/53) ✅

**Status**: Complete. 11/53 → 38/53 compile gate passing (+27 libraries). Dependency gate 6/6 all pass. 5420 unit tests (0 failures). 23 new tests for P14C fixes.

Fix the generator bugs revealed by the P14B restore fix. The 42 failing targets break down into 12 root causes (16 error patterns). Ordered by ROI (errors eliminated x libraries affected).

**Error pattern analysis**: 42 targets, ~1,425 unique CS errors, 16 patterns, 12 fix groups.

#### Tier 1 — High ROI, low-to-medium complexity ✅

1. **CX-1: Namespace/type name collision** ✅ — ~285 errors across 8 libraries (Valet, SwiftyBeaver, FSPagerView, Mixpanel, NVActivityIndicatorView, AnimatedCollectionViewLayout, Reachability, KeychainSwift)
   - CS0426: `Valet.SecureEnclaveValet` resolves to nested type, not namespace member
   - **Fix**: `NameProvider.GetModuleScopedTypeName()` detects when module name == top-level type name and emits `global::Namespace.Type` qualified references. `ModuleEmitter` collects colliding type names and passes them through emission context.
   - Also fixed Pattern 10 (CS0563/CS0216 operator type mismatch — cascading from this)

2. **CX-2: Identifier sanitization** ✅ — ~218 errors in 1 library (Kingfisher)
   - CS1002/CS1003/CS1525: parameter names contain `>` from existential annotations (`retryStrategy>`)
   - **Fix**: `NameProvider.SanitizeIdentifier()` strips `<`, `>`, and other illegal C# chars from parameter names.

3. **CX-3: ObjC class initializer emitter** ✅ — ~68 errors across 9 libraries (SnapKit, Starscream, BonMot, XMLCoder, SwipeCellKit, Alamofire, StripeCore, AMPopTip, Mappedin)
   - CS0103: `swiftIndirectResult` used without declaration; `{param}Handle` used without marshaling
   - CS0121: ambiguous constructor (SwiftHandle vs NativeHandle) on ObjC-rooted types
   - CS0841: `handlerHandle` used before declared in closure params
   - **Fix**: Repaired ObjC-rooted constructor P/Invoke emission in `PInvokeEmitter`, `WrapperEmitter`, `MethodMarshalPlanBuilder`, and `MethodClosureBridge`. Added explicit handle cast, parameter marshaling, and indirect result construction. Closure bridge handles now use `{param}Handle` variable scoping correctly.

4. **CX-4: External type handle accessor** ✅ — ~52 errors across 5 libraries (Alamofire, StripeCore, BonMot, AMPopTip, SwipeCellKit)
   - CS1061: `.Payload` emitted for Apple framework types / dependency types that use `.Handle`
   - **Fix**: Two changes: (1) `Payload` property changed from `internal` to `public` on all 4 type handlers (NonFrozenStructHandler, ClassHandler, EnumHandler, FrozenStructHandler) for cross-assembly access. (2) ObjC-rooted container accessor conversions (Dict, Set, Subscript) now include `ObjCRootedClassProjection` in pattern matches to skip `.Handle` element conversion (Codex review finding).

5. **CX-5: Variable name scoping in throw paths** ✅ — ~24 errors across 2 libraries (Alamofire, SwipeCellKit)
   - CS0841/CS0136: `error` variable used for existential container clashes with `out var error` from P/Invoke
   - **Fix**: Renamed existential container variables in throw paths to avoid scoping conflicts with P/Invoke `out var error`.

6. **CX-6: Reserved C# name collision** ✅ — ~4 errors across 2 libraries (GRDB, CryptoSwift)
   - CS0111: Swift `Finalize` method clashes with C# destructor
   - **Fix**: `IHandler.cs` detects `Finalize` method name and renames to `SwiftFinalize` to avoid C# destructor collision.

7. **CX-7: Unresolved base class IDisposable** ✅ — ~10 errors across 4 libraries (Quick, PhoneNumberKit, DifferenceKit, Lottie)
   - CS0535: class inherits from unresolved Apple type but lists `IDisposable` without providing `Dispose()`
   - **Fix**: `ClassHandler` generates default `Dispose()` stub for classes with unresolved ObjC base types that don't inherit a Dispose implementation.

8. **CX-8: Nested protocol type qualification** ✅ — ~12 errors in 1 library (PhoneNumberKit)
   - CS0246: proxy classes reference nested protocol interfaces without namespace qualification
   - **Fix**: `ProtocolProxyEmitter.Helpers.cs` — `GetInterfaceNameWithGenerics()` now walks the parent type chain and fully qualifies nested protocol interface names for module-level proxy classes.

#### Tier 2 — Medium-to-high complexity or infrastructure

9. **CX-9: Cross-module dependency resolution** ✅ — fully addressed
   - Dependency gate: 6/6 tested, all pass (was 4/6 with 2 failures)
   - **Fix (proxy visibility)**: Three changes: (1) Cross-module proxy class references now use `Module.SwiftInterop.ProxyName` qualification via new `GetQualifiedProxyClassName()` in `ExistentialHandler.cs`. (2) Proxy ExistentialContainer constructor changed from `internal` to `public` for cross-assembly access (`ProtocolProxyEmitter.Receivers.cs`). (3) `CurrentModuleName` threaded through all `ProjectionContext` creation sites: `MethodMarshalPlanBuilder.cs`, `WrapperEmitter.Return.cs` (5 sites), `WrapperEmitter.Marshalling.cs` (2 sites).
   - **Fix (interface qualification)**: Cross-module existential interface types qualified with module namespace via `ProjectionContext.CurrentModuleName` threading through `MethodSignature.cs` (3 sites) and `TypeProjectionFactory.cs` (2 ExistentialHandler creations).
   - Compile gate: StripeApplePay, StripeConnect, StripeUICore still fail standalone (references `StripeCore` types without assembly ref) but pass dep gate with StripeCore DLL
   - Deep dep chains (Stripe, StripeCryptoOnramp, StripeIssuing, StripePayments, StripePaymentSheet, StripePaymentsUI, StripeFinancialConnections) still fail compile gate — need multiple `--framework-dependency` flags for generation

10. **CX-10: Apple framework type mapping** ✅ — resolved
    - **Done**: `NetStaticClassTypes` gate (UITextContentType), `AppleFrameworkValueTypes` (UITextLayoutDirection, AVCaptureDevice.FocusMode), `AppleFrameworkSimpleEnumRemappings` (AVCaptureDevice.FocusMode → AVCaptureFocusMode), `SwiftToNetTypeRemappings` (Foundation.Formatter → NSFormatter)
    - SVGView now passes (prior session fixes resolved SwiftUI.Color/Font issues)
    - Parchment, Alamofire now pass (`.Payload` errors on ObjC-rooted types resolved by CX-4 + prior session fixes)

11. **CX-11: Generic constraint tracking** ✅ — resolved in P15
    - 9 errors across 2 libraries (Lottie 8, GRDB 1)
    - CS0311: `LottieColor`/`LottieVector1D`/`LottieVector3D` don't satisfy `IAnyInterpolatable` constraint on `ValueProviderStorage<T>`
    - CS0314: `TRowDecoder` doesn't satisfy `IFetchableRecord` constraint on `RecordCursor<TRecord>`
    - **Fix**: Three changes: (1) `GenericTypeEmitter` — when a generic constraint references a protocol, the emitted C# `where T : IProtocol` now includes both the protocol interface and any ancestor protocols from the conformance graph. (2) `ProtocolExtensionClosureBridge` — closure-based protocol extension methods now correctly propagate generic constraints from the parent type through to the emitted wrapper. (3) `ModuleProcessor.InferMissingConformances()` — infers conformances from generic type arguments at the module level (e.g., `ValueProviderStorage<T: Interpolatable>` + `LottieColor` used as `T` → infer `LottieColor: Interpolatable`).
    - DifferenceKit previously listed here — now passing (resolved by other fixes)

12. **CX-12: Async closure callback** ✅ — resolved
    - Alamofire now passes (prior session fixes resolved async closure issues)

#### Remaining failing libraries — all resolved

All 53/53 targets pass (40 compile gate + 13 dependency gate). See Session 5 (P15) for the final fixes.

**Resolved by P14E (validation infrastructure)**:
- BlinkIDUX: added `"dependencies": ["BlinkID"]` to manifest → passes dep gate
- Stripe deep dep chains (11 libraries): cascading dependency gate resolves transitive deps in rounds. Round 1 compiles direct deps (StripePayments→StripeCore, StripeUICore→StripeCore, etc.), round 2 compiles deeper deps (Stripe→StripeApplePay+StripePayments, StripePaymentsUI→StripePayments+StripeUICore, etc.)
- StripeIdentity: added `"dependencies": ["StripeCameraCore", "StripeCore", "StripeUICore"]`
- Stripe: updated deps to `["StripeApplePay", "StripePayments"]` (was missing StripeApplePay)
- StripeCryptoOnramp: updated deps to `["StripeApplePay", "StripeCore", "StripePaymentSheet", "StripePayments"]`

**Tests**: 23 new tests — MemberEmissionValidatorTests (5), Tier2LibraryFixTests (5), ProtocolProxyEmitterTests (1+2 updated), PropertyHandlerTests (5), ExistentialHandlerTests (7: cross-module proxy qualification).

**Gate**: Unit tests 5427 passing (0 failures). Validation 38/53 compile gate, 6/6 dependency gate. Remaining 4 failures (GRDB, Lottie, StripePaymentSheet, StripeCryptoOnramp) resolved in P15.

### Session 5: P15 — 53/53 Validation + Codex Review Fixes ✅

**Status**: Complete. All 53 validation targets pass. 5439 unit tests (0 failures). Codex review P1 findings addressed.

**Goal**: Fix the remaining 4 failing libraries (GRDB, Lottie, StripePaymentSheet, StripeCryptoOnramp) and address Codex review findings.

#### Generator Fixes

1. **CX-11 resolution: Generic constraint tracking** ✅
   - GRDB (1 error): `RecordCursor<TRecord>` constraint on `IFetchableRecord`
   - Lottie (8 errors): `ValueProviderStorage<T>` constraint on `IAnyInterpolatable`
   - **Fix**: Three changes:
     - `GenericTypeEmitter.cs` — emits ancestor protocol constraints from conformance graph
     - `ProtocolExtensionClosureBridge.cs` — propagates generic constraints through closure wrappers
     - `ModuleProcessor.InferMissingConformances()` — infers conformances from generic type argument usage patterns

2. **StripePaymentSheet SwiftUI references** ✅
   - 2 errors: References to `SwiftUI.View` types that don't exist in the type database
   - **Fix**: `TypeDatabaseExtensions.GetTypeRecordOrAnyType()` — added `IsUnsupportedAppleModule` check for SwiftUI/Combine/Observation modules, returns `AnyType` instead of throwing

3. **StripeCryptoOnramp cascading failure** ✅
   - Blocked by StripePaymentSheet — automatically resolved when StripePaymentSheet fixed
   - Added `"dependencies": ["StripeApplePay", "StripeCore", "StripePaymentSheet", "StripePayments", "StripeUICore"]` to manifest

4. **IsOptionalObjCBridged parity fix** ✅
   - `MarshallingHelpers.IsOptionalObjCBridged()` — Apple framework ObjC classes not in module database (e.g., `QuartzCore.CALayer`) now correctly identified via `TypeProjectionFactory.IsKnownAppleModule + HasObjCClassPrefix` fallback
   - Matches TypeProjectionFactory's Optional inner type projection exactly
   - ObjCRooted types correctly excluded (they use `SwiftOptional<T>`, not `IntPtr`)

#### Codex Review P1 Fixes

5. **P1: Validation compile-gate contamination** ✅
   - `validate-libraries.sh` dependency gate was overwriting compile-gate results with `set_result "$dep_fw" compile "ok"`, making baseline unreliable
   - **Fix**: Dep-gate results now stored as `dep_compile` (separate field). Compile-gate results are never mutated by dep-gate. Baseline stores both independently. Regression detection considers both fields.
   - Summary now shows: "Overall" (combined 53/53), "Compile" (standalone 40/53), "Dependencies" (dep-gate 13/13)

6. **P1: ThrowingClosureSimplificationEmitter type mismatch** ✅
   - `BuildWrapperLambda` used raw `closureHandler.TranslateTypeSpecToCSharp` for `SwiftResult<T, SwiftError>` success type, while delegate declarations used projected types (e.g., `string` vs `SwiftString`)
   - **Fix**: `BuildWrapperLambda` now accepts `MethodEnvironment` and uses `NativeIntOverloadEmitter.ResolveType()` for projected type consistency
   - Latent bug — no current library triggered it, but delegate/lambda types now always agree

#### Validation Infrastructure

7. **Cascading dependency gate** ✅
   - Dependency gate now resolves transitive deps in rounds (round 1: direct deps, round 2: deeper deps)
   - 13 dependency targets all pass: BlinkIDUX, StripeApplePay, StripeCardScan, StripeConnect, StripeFinancialConnections, StripePayments, StripeUICore, Stripe, StripeIdentity, StripeIssuing, StripePaymentSheet, StripePaymentsUI, StripeCryptoOnramp

**Tests**: 12 new tests — BoundGenericsHandlerTests (2: HasMethodSelfTypeParams constraint skipping), MarshallingHelpersTests (3: IsOptionalObjCBridged correctness), TypeDatabaseExtensionsTests (1: unsupported Apple module fallback), ThrowingClosureSimplificationTests (1: projected return type in wrapper lambda), plus updates to existing ConditionalExtensionConstraintTests.

**Gate**: Unit tests 5439 passing (0 failures). Validation 53/53 overall (40 compile, 13 dep gate). No regressions.

---

### Session 4: P14D — Quality Polish

Developer experience improvements from the E-series findings. Moved from original P14C to prioritize compile error fixes.

**Quick wins** (isolated, low-risk):
1. **E9** — Deduplicate `Utf8Slice` across BX2 extension classes
   - Route through existing `Utf8SliceEmitter.EmitIfNeeded` with `ModuleEmissionContext`, or extract to shared namespace-level internal type
2. **E2** — Hide `ExistentialContainer` on proxy types
   - Change `ISwiftExistentialConvertible<T>` to explicit interface implementation in `ProtocolHandler.EmitProtocolProxy()`
3. **E5** — Suppress `stp_` internal properties
   - Detect `stp_`/`_spi` prefix in `MemberEmissionValidator`, mark `[EditorBrowsable(Never)]`
   - Must be conservative — some `_`-prefixed members are intentional public API
4. **E12** — Namespace-like enums → `static class`
   - Detect enums with zero cases and only nested types; emit `static class` instead of `ISwiftObject, IDisposable` class
5. **E13** — Strip hash suffix from factory method names
   - `Create_529DA596` → `Create` (or meaningful disambiguation like `CreateFromUrlRequest`)
6. **E15** — Suppress `@_spi` types
   - Skip SPI-annotated declarations entirely, or emit as `internal`
   - 41% of StripeIdentity is SPI — significant code reduction

**High-impact items**:
7. **E16** — `@available` → `[SupportedOSPlatform]`
   - Parse availability attributes from ABI JSON
   - Map `@available(iOS X.Y, *)` to `[SupportedOSPlatform("iosX.Y")]`
   - High impact: without this, calling an iOS 14.3+ API on iOS 13 silently crashes at runtime
8. **E11** — `RawRepresentable<Int>` enums → BX2 simple enum path
   - Extend `CanSafelyEmitAsSimpleEnum` to recognize `RawRepresentable<Int>` enums with no associated values
   - High impact: common types like `ImageRequest.Priority` (5 cases) currently require 200 lines + IDisposable

**Gate**: `run-tests.sh` + `validate-libraries.sh` must pass. Downstream re-spot-check on Nuke, BlinkID, StripeIdentity.
