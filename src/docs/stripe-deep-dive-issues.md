# Stripe Deep Dive: Issues Found

**Date**: April 2, 2026
**Context**: Expanded Stripe sim tests from 8 tests to 88 test points across all 12 modules. Results: 73 passed, 0 failed, 15 skipped.

---

## Issue 1: Wrapper Compilation — Unqualified Type Names in CdeclParamMapper

**Modules affected**: StripePayments (and likely any library with cross-type references in @_cdecl wrappers)

**Severity**: High — blocks all StripePayments constructor/method tests (~17 tests skipped)

**Root cause**: `CdeclParamMapper.cs` (lines ~326, ~353) uses `ExistentialBypassEmitter.RenderSwiftTypeSpec()` instead of `RenderModuleQualifiedSwiftTypeSpec()` when generating `Unmanaged<T>` casts for non-ObjC-bridged Swift class parameters.

**Generated (broken)**:
```swift
let buttonCustomizationVal = Unmanaged<STPThreeDSButtonCustomization>.fromOpaque(buttonCustomization).takeUnretainedValue()
```

**Expected (correct)**:
```swift
let buttonCustomizationVal = Unmanaged<StripePayments.STPThreeDSButtonCustomization>.fromOpaque(buttonCustomization).takeUnretainedValue()
```

**Impact**: 100+ unqualified type references in the StripePayments wrapper alone. Swift compiler can't resolve the bare type name without module qualification.

**Fix**: In `src/Swift.Bindings/src/Emitter/StringEmitter/CdeclParamMapper.cs`, change `RenderSwiftTypeSpec` to `RenderModuleQualifiedSwiftTypeSpec` at the Unmanaged cast sites (~lines 326, 353). Similar to how it's already done correctly for protocol existentials (lines 83-94) and non-copyable structs (line 316).

**Verification**: After fix, `nuke validate --filter Stripe` should show StripePayments swift_compile passing. Then re-run Stripe sim tests — the Phase 4 StripePayments skip should convert to ~17 passes.

---

## Issue 2: SwiftUI Bridge — Bare `Binding` Without Generic Parameter

**Modules affected**: StripePaymentSheet (SwiftUI bridge only — main bindings unaffected)

**Severity**: Medium — blocks PaymentSheet appearance/config tests (~16 tests skipped), but main wrapper also fails (see Issue 3)

**Root cause**: The SwiftUI bridge generator doesn't resolve the concrete generic type for `SwiftUI.Binding<T>`. It emits bare `Binding` instead of `Binding<AddressElement.AddressDetails?>`.

**Generated (broken)** in `StripePaymentSheet.SwiftUIBridge.swift`:
```swift
@Published var address: Binding           // Line 17 — invalid: Binding requires type parameter
init(address: Binding, ...)               // Line 29
let addressConverted = addressPtr.assumingMemoryBound(to: Binding.self).pointee  // Line 65
```

**Expected**:
```swift
@Published var address: Binding<AddressElement.AddressDetails?>
```

**Note**: This is a SwiftUI bridge issue (SWIFTBIND052 warning), not the main wrapper. The main wrapper for StripePaymentSheet also fails — see Issue 3.

---

## Issue 3: Wrapper Compilation — Missing Framework Dependencies

**Modules affected**: StripePaymentSheet, StripePaymentsUI, StripeApplePay, StripeConnect, StripeIssuing, StripeFinancialConnections (6 modules total)

**Severity**: High — blocks tests across 6 modules

**Symptom**: Build log shows `SWIFTBIND051: Swift wrapper compilation failed for '<module>SwiftBindings'`. The generated wrapper Swift source exists and appears syntactically valid, but fails to compile.

**Root cause**: The wrapper compilation command passes `--framework-dependency` for direct dependencies but may be missing transitive dependencies or the dependency xcframeworks aren't available at wrapper compile time. For example:

- **StripePaymentSheet** depends on StripeCore, StripePayments, StripeUICore — but the StripePayments wrapper hasn't compiled (Issue 1), so the dependency chain is broken.
- **StripePaymentsUI** depends on StripeCore, StripePayments, StripeUICore — same cascading failure.
- **StripeIssuing** depends on StripeCore, StripePayments — same.
- **StripeApplePay** depends on StripeCore — but its wrapper still fails, possibly due to Issue 1 patterns in its own wrapper.
- **StripeConnect** depends on StripeCore, StripeFinancialConnections, StripeUICore.
- **StripeFinancialConnections** depends on StripeCore, StripeUICore.

**Hypothesis**: Fixing Issue 1 (unqualified type names) may unblock several of these modules since StripePayments is a transitive dependency for many. The remaining failures may be independent issues in each module's wrapper.

**Investigation needed**: After fixing Issue 1, rebuild all Stripe modules and check which wrappers still fail. The cascade effect means the true independent failures may be fewer than 6.

---

## Issue 4: String Marshalling Corruption — STPAPIClient.AppInfo

**Status**: Fixed (2026-04-02).

**Root cause**: The accessor return type for `Optional<Class/ObjCRooted>` properties was `SwiftOptional<T>` instead of `IntPtr`. This forced the getter through `MarshalFromSwift<T>` + `SwiftOptional.NewSome()` — two VWT operations that performed `InitializeWithCopy` / `swift_retain` on the ObjC object, corrupting tagged pointer NSString ivars by +2 per call at byte offset 4.

**Fix**: Extended the `IsOptionalObjCBridged`-only accessor checks to use `IsOptionalWithReferenceInner` (covers ObjC-bridged, ObjC-rooted, and pure Swift classes). The accessor now returns `IntPtr` directly, and the property getter converts via `GetNSObject<T>` (ObjC-rooted) or `MarshalFromSwift<T>` (Swift class) — zero VWT operations for ObjC types.

**Files changed**:
- `MethodSignature.cs`: Accessor return type → IntPtr for all reference optionals
- `WrapperEmitter.Return.cs`: Accessor body → passthrough `return result;`
- `AccessorConversionVisitors.cs`: Added IntPtr→T? conversions for ClassProjection and ObjCRootedClassProjection

---

## Issue 5: StripePayments Was Incorrectly Excluded

**Status**: Fixed in this session.

The .csproj had `StripePayments` commented out with the note "generator produces invalid enum-as-NSObject bindings". Investigation confirmed this is no longer true — all 31 enums in the binding use correct primitive integer backing (`: long` or `: ulong`). The wrapper xcframework was also generated successfully.

**Change made**: Uncommented the `ProjectReference` for StripePayments and added `using StripePayments;` to the test file. All StripePayments enum tests (Phase 5) pass.

---

## Test Coverage Summary (after all fixes)

**74 passed, 0 failed, 14 skipped**

Remaining skips are from modules whose wrapper xcframeworks aren't built for the test project (StripePaymentSheet, StripeConnect, StripeIssuing, StripeFinancialConnections, StripePaymentsUI, StripeCardScan). All validation targets (15/15) pass.

## Remaining Work

- **Issue 2** (SwiftUI bridge Binding<T>) — lower priority, SwiftUI bridge is secondary
