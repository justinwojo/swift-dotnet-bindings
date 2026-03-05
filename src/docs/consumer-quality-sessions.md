# Consumer Quality Sessions

**Created**: March 5, 2026
**Source**: Issues discovered validating [swift-dotnet-packages](https://github.com/justinwojo/swift-dotnet-packages) (16 libraries, 5 sim test suites) against SDK preview.15.

These sessions address remaining issues and quality observations from real-world consumer validation. Ordered by ROI — quick wins first, then deeper infrastructure work.

---

## Session 1: Generator Quick Wins

**Effort:** 1 session
**Completed:** March 5, 2026
**Status:** 3/5 completed (CQ-1, CQ-4, CQ-5), 2 deferred (CQ-2, CQ-3)
**Validation:** 53/53 passing (no regressions)
**Unit tests:** 5467 passing (+22 from baseline)

### CQ-1: QuartzCore String in Obsolete Attribute (Q10) -- COMPLETED

**Affects:** Lottie
**Severity:** Trivial

One remaining `QuartzCore.CALayerContentsGravity` reference in an `[Obsolete]` attribute string. Namespace mapping isn't applied to attribute string content.

**Fix:** Added `MarshallingHelpers.MapModulesInString()` and applied it to all diagnostic reason strings in `WitnessDispatchEmitter.ClassifyMethodDispatchWithReason` via `MapForDiagnostic()` helper. Covers all 7 `NotDispatchable` parameter/return branches (including `ExistentialReturn` and `BoundGenericReturn` paths caught in review).
**Files:** `MarshallingHelpers.cs`, `WitnessDispatchEmitter.cs`
**Tests:** `MarshallingHelpersTests.MapModulesInString_*` (4 tests)

### CQ-2: Property Name Collision Uses Value Suffix (Q4) -- DEFERRED (current behavior correct)

**Affects:** Nuke (`pipeline.ConfigurationValue`, `pipeline.CacheValue`, `request.PriorityValue`)
**Severity:** Medium — affects discoverability of core APIs.

**Investigation:** The `Value` suffix prevents C# CS0102 (member name collides with nested type name in same class). This is NOT the "Color Color" pattern (which applies when the type is defined externally). Nested type definitions in the same class MUST have unique names from properties. Attempted removing the suffix — caused 32/53 validation failures with CS0102 across Nuke, Alamofire, and many others. No better alternative found. Current behavior is correct.
**Future improvement:** Surface the original Swift name in XML doc comments on renamed members so consumers can map `cache` -> `CacheValue` without confusion.

### CQ-3: SPI-Internal Types Still Public (Q6) -- DEFERRED (needs member-level filtering)

**Affects:** StripeIdentity (`Image`, `StripeIdentityBundleLocator`)
**Severity:** Low — clutters IntelliSense and increases binary size.

**Investigation:** Type-level suppression via `IsModuleInternal` in `HandleBaseDecl` removes type definitions but leaves dangling references from members of OTHER public types that reference the suppressed types. This caused 21/53 failures across Alamofire (WebSocketRequest), CryptoSwift (Words), GRDB (RowDecodingContext), DifferenceKit (AnyDifferentiableBox), XMLCoder (protocols), and many others.
**Recommended approach:** Reuse the internal-type-name collection in `Program.CollectInternalTypeNames()` and add a recursive `ReferencesSuppressedType(TypeSpec)` helper in `MemberEmissionValidator` (alongside existing `ReferencesUnsupportedModule()`). Apply uniformly in `CanEmitProperty`, `CanEmitMethod`, `CanEmitSubscript`, and `HasUnsupportedPropertyType` so both emission and collision logic stay aligned. This matches the existing validator architecture and avoids dangling signatures without broader parser/model changes.

### CQ-4: Simple Enum Demotion is Conservative (Issue 4) -- COMPLETED

**Affects:** BlinkID (23 enums demoted), BlinkIDUX
**Severity:** Low — correctness over ergonomics, but significant enum count regression.

**Fix:** Narrowed `ModuleProcessor.DemoteSimpleEnumsUsedAsGenericArgs` to skip constraint-free stdlib containers (Array, Optional, Dictionary, Set) and check `GenericConformances` on the generic parameter at the usage site. Enums used only in unconstrained positions (e.g., `Array<MyEnum>`) are no longer demoted.
**Files:** `ModuleProcessor.cs`
**Tests:** `SimpleEnumDemotionTests` — updated existing + 2 new tests

### CQ-5: Array\<ObjCClass\> Properties Not Bound (Q9) -- COMPLETED

**Affects:** StripeIdentity (`[UIImage]` properties)
**Severity:** Low — silently dropped APIs.

**Fix:** Added `TryProjectObjCElement` fallback in `TypeProjectionFactory` for Array, Set, and Dictionary element projections. Uses `IsKnownAppleModuleForElements` (UIKit/Foundation only — broader Apple modules excluded to avoid static class issues like `PassKit.PKPaymentNetwork`). Rejects nested types (e.g., `NSAttributedString.Key`) to avoid projecting structs as ObjC classes.
**Files:** `TypeProjectionFactory.cs`
**Tests:** `OptionalAppleFallbackTests` — updated `IsKnownAppleModule` tests + 9 new tests (Array/Set/Dictionary positive and negative cases: ObjC class elements, nested type rejection, non-UIKit Apple module rejection)

---

## Session 2: SwiftUI Suppression + Optional Projection

**Effort:** 1 session
**Planning:** Plan Mode Recommended — CQ-6 has design decisions (suppress vs stub, configurable vs always-on, diagnostic emission). Worth investigating code paths and deciding on approach before implementing.
**Theme:** Improve handling of external module types in generated code.

### CQ-6: Missing SwiftUI Namespace Types (Issue 1)

**Affects:** BlinkIDUX (Color, Font), StripePaymentsUI (Binding), StripePaymentSheet (Binding, AnyView), StripeUICore (Binding)
**Severity:** Low (stubs work around it) — but a real friction point for consumers.

Generated code references `SwiftUI.Color`, `SwiftUI.Font`, `SwiftUI.Binding`, `SwiftUI.AnyView` which have no .NET binding. Consumers must create `SwiftUI.Stubs.cs` files manually.

**Fix:** Two-pronged approach:
1. **Short-term:** Suppress emission of members that reference types from modules without available bindings (configurable, off by default).
2. **Long-term:** Generate minimal stub types for referenced-but-unbound SwiftUI types so consumers don't need manual stubs.

**Acceptance:** Libraries referencing SwiftUI types either compile without manual stubs, or cleanly omit the affected members with a diagnostic.

### CQ-7: SwiftOptional\<T\> Leaking into Public API (Q1)

**Affects:** Multiple libraries — callback parameters with optional ObjC types.
**Severity:** Medium — developers need `Swift` namespace import and `SwiftOptional<T>.Value`/`.HasValue`.

`SwiftOptional<T>` appears in public method signatures where `T?` would be more natural.

**Fix:** Project `SwiftOptional<T>` to `T?` in public API signatures where `T` is a reference type (classes, ObjC-bridged types). Keep `SwiftOptional<T>` only where `T` is a value type that can't be nullable in C#.
**Acceptance:** Public API surfaces use `T?` instead of `SwiftOptional<T>` for reference types.

---

## Session 3: @available -> [SupportedOSPlatform]

**Effort:** 1 session
**Planning:** Plan Mode Required — entirely new infrastructure. Design questions: .swiftinterface parsing strategy, declaration correlation with ABI JSON entries (mangled name vs signature), handling inherited/class-level availability, edge cases (deprecated, unavailable, platform variants).
**Dependency note:** Session 4 (default parameter values) also parses .swiftinterface files and correlates declarations with ABI JSON. The plan for this session should design the .swiftinterface reading and declaration-correlation infrastructure as reusable components (not availability-specific), so Session 4 can extend rather than refactor.
**Theme:** Extract availability annotations from .swiftinterface files.

### CQ-8: Missing Platform Availability Annotations (Q5)

**Affects:** All libraries with iOS version-constrained APIs.
**Severity:** High — silent runtime crash on older iOS instead of compile-time warning.

Swift `@available(iOS 16.0, *)` annotations aren't mapped to `[SupportedOSPlatform("ios16.0")]`. The .swiftinterface files in xcframeworks contain these annotations in a predictable format.

**Fix:**
1. Parse `@available` annotations from .swiftinterface files (pattern matching, not full parser).
2. Correlate declarations in .swiftinterface with ABI JSON entries by mangled name or signature.
3. Emit `[SupportedOSPlatform("iosX.Y")]` on the corresponding C# declarations.
4. Handle class-level, method-level, and property-level availability.

**Acceptance:** Generated bindings include `[SupportedOSPlatform]` attributes. Consumer code targeting iOS 14 gets warnings when calling iOS 16+ APIs.

---

## Session 4: Default Parameter Values

**Effort:** 1-2 sessions
**Planning:** Plan Mode Required — new parsing infrastructure (shares foundation with Session 3). Design questions: which default expressions can be reliably mapped to C#, how to resolve enum case defaults (needs type context), optional params vs convenience overloads, handling `default` keyword for non-trivial types.
**Theme:** Extract default parameter values from .swiftinterface files.

### CQ-9: Swift Default Parameter Values Lost (Q2)

**Affects:** All libraries with settings/configuration types (BlinkID `ScanningSettings` — 18+ required params).
**Severity:** High — major usability cliff for settings-heavy APIs.

ABI JSON doesn't contain default parameter values, but .swiftinterface files do:
```swift
public init(timeout: Swift.Double = 10.0, showOverlay: Swift.Bool = true, ...)
```

**Fix:**
1. Extract default value expressions from .swiftinterface init/method signatures.
2. Map Swift literal defaults to C# equivalents:
   - Numeric literals (`10.0` -> `10.0`)
   - Bool literals (`true`/`false`)
   - `nil` -> `null` / `default`
   - Enum cases (`.someCase` -> `EnumType.SomeCase`)
   - `default` keyword -> C# `default`
3. Emit as C# optional parameters or generate convenience overloads.
4. Gracefully skip computed/complex defaults that can't be mapped.

**Acceptance:** `ScanningSettings` constructor has reasonable defaults. Common literal defaults (numbers, bools, nil, enum cases) are preserved in generated C# signatures.

**Shares infrastructure with Session 3** — both read .swiftinterface files from xcframeworks.

---

## Session 5: SDK Auto-ProjectReferences

**Effort:** 1 session
**Planning:** Plan Mode Recommended — need to decide how the generator surfaces cross-module dependency metadata, how the SDK targets consume it, and whether to emit `<ProjectReference>` vs `<PackageReference>` (or both depending on context). Less complex than Sessions 3-4 but has MSBuild design choices.
**Theme:** MSBuild SDK automation for cross-module dependencies.

### CQ-10: Cross-Module Dependencies Require Manual ProjectReferences (Issue 3)

**Affects:** All Stripe libraries except StripeCore and StripeIdentity.
**Severity:** Medium — manual configuration required per dependency.

When generated code references types from another module, the consuming project needs `<ProjectReference>` entries. This is correct behavior but adds friction.

**Fix:** After generation, scan emitted C# for cross-module type references and auto-add `<ProjectReference>` or `<PackageReference>` entries in the SDK targets. The generator already knows which modules it references — surface this as metadata the SDK can consume.
**Acceptance:** `dotnet build` on a Stripe library project auto-resolves sibling dependencies without manual `<ProjectReference>` entries (or at minimum emits an actionable diagnostic with the needed references).

---

## Not Actionable

These items were evaluated and determined to not be worth pursuing:

| Item | Reason |
|------|--------|
| **Issue 2** (Non-blittable SIGSEGV) | .NET Mono JIT bug in dotnet/runtime. Generated code is correct. NativeAOT unaffected. Upstream bug report drafted. |
| **Q3** (Small value types as classes) | Non-frozen structs MUST be class-based — their memory layout isn't ABI-stable. Projecting as C# value types would produce incorrect code. |
| **Q7** (Payload enums as classes) | Complex enums have non-trivial memory management. Class-based is the only correct projection. |
| **Q8** (AnyError -> Exception) | Tracked separately in roadmap as post-ship improvement. Methods are bound and work; error info is available but in awkward form. |

---

## Progress Tracking

| Session | Status | Date | Notes |
|---------|--------|------|-------|
| 1 — Quick Wins | **3/5 done** | Mar 5, 2026 | CQ-1, CQ-4, CQ-5 done; CQ-2, CQ-3 deferred |
| 2 — SwiftUI + Optional | Not started | | CQ-6, CQ-7 |
| 3 — @available | Not started | | CQ-8 |
| 4 — Default Params | Not started | | CQ-9 |
| 5 — Auto-ProjectRefs | Not started | | CQ-10 |
