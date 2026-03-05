# Preview 14 Bug Fixes & Validation Hardening

**Date**: March 4, 2026
**Source**: `swift-dotnet-packages/ISSUES-preview14.md` — issues discovered validating BX1-BX4 commits against 15 real-world libraries (5 library families, 15 binding projects, 5 sim test suites).
**Impact**: 12/15 libraries building clean in preview.13 dropped to 3/15 in preview.14.

---

## Session P14A: Bug Fixes ✅

**Status**: Complete. All fixes applied, 53/53 library validation passing, 5371 unit tests passing.

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

## Session P14B: Validation Pipeline Hardening

**Goal**: Prevent this class of failure from leaking again. Every issue in P14A represents a gap in the validation pipeline.

### V1: Multi-Library Compilation Mode

**Gap addressed**: P14-7 (cross-module assembly refs)

**Problem**: `validate-libraries.sh` compiles each library target in isolation with only `Swift.Runtime` as a reference. It cannot detect cross-assembly reference failures.

**Implementation**:
- Add a `"dependencies"` field to `validation-libraries.json` entries (e.g., `StripeApplePay` depends on `StripeCore`)
- After individual compile gate, run a second pass that compiles dependency groups together
- For Stripe: compile StripeCore first, then compile each dependent library with a reference to StripeCore's output DLL
- Report results as a separate "dependency gate" section in the output

**Scope**: Start with Stripe (the only multi-module family in the current manifest). The infrastructure should generalize to any library with declared dependencies.

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

### V3: Compile-Smoke Tests for Generated Output

**Gap addressed**: P14-3, P14-5, P14-6 (string-pattern tests miss type system errors)

**Problem**: Unit tests use `Assert.Contains("string pattern", output)` — they verify the generated code *looks right* but never compile it. `handle.Pointer` passed the test because the test expected `handle.Pointer`.

**Implementation**:
- Add a small set of "compile-smoke" integration tests that take representative generated C# and actually `dotnet build` it
- Requires a test project with references to `Swift.Runtime`, MAUI iOS bindings (`Microsoft.iOS`), and a mock xcframework
- Cases: ObjC-rooted class, simple enum with extensions, cross-module type reference, optional ObjC type
- These are slower than unit tests (seconds vs milliseconds) — run as part of `run-tests.sh` integration suite, not on every unit test invocation
- Alternative: expand TestFramework golden files to cover these scenarios (generates from a real xcframework, so covers the full pipeline)

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

### V5: Runtime API Compatibility Check

**Gap addressed**: P14-3 (generated code references non-existent runtime API)

**Problem**: The generator emits code that calls `SwiftHandle.Pointer`, but `SwiftHandle` only has `.Handle`. No validation step checks that generated code is compatible with the actual runtime API surface.

**Implementation**:
- Add a test or build step that extracts the public API surface of `Swift.Runtime.dll` and verifies all generated runtime references resolve
- Simpler alternative: the compile-smoke tests (V3) inherently catch this — if the generated code doesn't compile against the real runtime DLL, the test fails
- V3 is likely sufficient; this is only needed as a separate check if V3 doesn't cover enough cases

---

## E-Series Disposition

Items discovered during developer review of the 3 clean-building libraries. Categorized by destination.

### Already in P14A (promoted to bug fixes)

| Item | Description | As |
|------|-------------|-----|
| E8 | OpaqueSwiftType not emitting | P14-9 |
| E10 | GetHashCode returns 0 for OptionSet | P14-8 |
| E19 | SB0001 not propagated to async | P14-10 |

### In P14C (quality polish session)

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

Three sessions. Each scoped to complete in a single Claude session.

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

### Session 2: P14B — Validation Pipeline Hardening

Infrastructure changes to `validate-libraries.sh` and the test suite so this category of failure can't leak again.

1. **V1** — Multi-library compilation mode in `validate-libraries.sh`
   - Add `"dependencies"` to `validation-libraries.json`
   - Compile Stripe family together (StripeCore first, dependents reference its DLL)
   - New "dependency gate" section in output
2. **V3** — Compile-smoke tests for generated output
   - Integration tests that `dotnet build` representative generated C# against real `Swift.Runtime.dll` + MAUI refs
   - Cases: ObjC-rooted class, simple enum in generic context, cross-module refs, optional ObjC type
3. **V5** — Runtime API compatibility (if V3 doesn't fully cover)

**Gate**: Full downstream revalidation — all 15 libraries build clean in `swift-dotnet-packages`.

### Session 3: P14C — Quality Polish

Developer experience improvements from the E-series findings. Mix of quick wins and two high-impact medium-effort items.

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
