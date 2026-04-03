# Stripe Deep Dive: Issues Found

**Date**: April 2, 2026
**Context**: Expanded Stripe sim tests from 8 tests to 88 test points across all 12 modules. Results: 73 passed, 0 failed, 15 skipped.

---

## Issue 1: Wrapper Compilation — Unqualified Type Names in CdeclParamMapper

**Status**: Fixed (2026-04-02, commit 16a6adfa).

**Root cause**: `CdeclParamMapper.cs` used `RenderSwiftTypeSpec()` instead of `RenderModuleQualifiedSwiftTypeSpec()` for `Unmanaged<T>` casts. Fixed by switching to module-qualified rendering at the Unmanaged cast sites.

---

## Issue 2: SwiftUI Bridge — Bare `Binding` Without Generic Parameter

**Status**: Fixed (2026-04-02).

**Root cause**: When `MapBindingType` returned null for unsupported inner types (e.g., `Binding<Optional<AddressDetails>>`), the code fell through to `MapDatabaseType` which resolved `SwiftUI.Binding` as a bare struct type, stripping the generic parameter. This produced broken Swift code like `@Published var address: Binding` instead of the inner type.

**Fix** (two parts):
1. **Prevented fallthrough**: `MapNamedType` now returns null directly for unsupported Binding inner types instead of falling through to `MapDatabaseType`. Views with unsupported Binding params correctly fall back to templates.
2. **Extended Binding support**: `MapBindingType` now accepts `OptionalWrapped` inner types, enabling `Binding<T?>` where T is Primitive, String, or BoundEnum. The State stores the inner value; `$state.x` creates the Binding projection automatically.

**Files changed**:
- `SwiftUIBridgeEmitter.InitAnalyzer.cs`: Fallthrough fix + OptionalWrapped in Binding filter
- `SwiftUIBridgeEmitterTests.cs`: Updated regression test, added 3 new Binding<Optional<T>> tests

---

## Issue 3: Wrapper Compilation — Cascading Framework Dependencies

**Status**: Resolved by Issue 1 fix. All 15 Stripe validation targets pass (cs_compile, dep_compile, swift_compile).

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

All 5 issues resolved. No remaining Stripe-specific work items.
