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

## Session 2: SwiftUI Gate Lift + Optional Projection

**Effort:** 1 session
**Completed:** March 5, 2026
**Status:** CQ-6 and CQ-7 completed
**Validation:** 53/53 passing (no regressions, BonMot improved from 3 errors to ok)
**Unit tests:** 5524 passing (+8 from Session 3 baseline)

### CQ-6: Lift SwiftUI Gate for Registered Types -- COMPLETED

**Affects:** BlinkIDUX (53 members suppressed), StripePaymentsUI, StripePaymentSheet, StripeUICore
**Severity:** Low (stubs work around it) — but a real friction point for consumers.

Non-generic registered SwiftUI types (Color, Font, AnyView, EdgeInsets, Animation, Image, Text) are now fully supported surface area — proper ISwiftObject stubs in `Swift.Runtime`, both suppression paths lifted, members emitted in generated bindings. Generic `Binding` is bridge-only (borrowed-handle stub).

**Design**: These are opaque handle types for pass-through usage — consumers receive values from Swift APIs (getters, return values) and pass them to other Swift APIs (setters, parameters). The `internal` constructor + `ISwiftObject.NewFromPayload` is the standard marshalling-only construction path (same as all generated non-primitive types). Public value construction (e.g., `new SwiftUI.Color(red, green, blue)` analogous to `SwiftColor`) is a post-ship enhancement, not a correctness requirement for the gate lift.

**Fix:** Three-part approach:
1. **Runtime stubs** (`src/Swift.Runtime/src/Swift/SwiftUI/`): 7 ISwiftObject stubs following CIContext pattern (SwiftSafeHandle payload, cached metadata, P/Invoke metadata accessor, IDisposable). 1 `Binding` borrowed-handle stub (ownsHandle=false, no ISwiftObject — generic type can't call GetTypeMetadata).
2. **Path A — Type Resolution** (`TypeDatabaseExtensions.cs`): Modified 3 resolution methods (`GetTypeRecordOrAnyType`, `TryGetTypeRecordOrAnyType`, `GetTypeRecordOrThrow`) to check DB registration before collapsing to AnyType. Generic guard (`ContainsGenericParameters`) prevents generic usages from bypassing the gate.
3. **Path B — Member Emission** (`MemberEmissionValidator.cs`): Modified `ReferencesUnsupportedModule` to allow registered non-generic types through. Null DB, generic usages, and unregistered types still rejected.

**Files:** `KnownLibraries.cs`, `SwiftUI/*.cs` (8 files), `TypeDatabaseExtensions.cs`, `MemberEmissionValidator.cs`
**Tests:** MemberGateEvaluatorTests (7 flipped + 2 new), TypeDatabaseExtensionsTests (1 split + 1 new)

### CQ-7: SwiftOptional\<T\> → T? for Optional ObjC Types -- COMPLETED

**Affects:** Multiple libraries — `SwiftOptional<UIKit.UIFont>` in public API signatures.
**Severity:** Medium — developers need `Swift` namespace import and `SwiftOptional<T>.Value`/`.HasValue`.

**Fix:** Added UIKit and Foundation to `KnownAppleModules` in TypeProjectionFactory so `Optional<UIKit.UIFont>` projects as `UIKit.UIFont?` (ObjCBridged) instead of `SwiftOptional<UIKit.UIFont>`. Triple safety guard prevents misprojection of non-class types: `IsNestedType` (nested structs like NSAttributedString.Key), `IsKnownAppleValueType` (ObjC enums like NSTextAlignment, NSLineBreakMode from AppleFrameworkValueTypes set), and `IsRemappedAppleValueType`/`IsRemappedAppleEnum` (remapped types). MarshallingHelpers `IsOptionalObjCBridged` updated with identical guards for parity.

**Files:** `TypeProjectionFactory.cs`, `MarshallingHelpers.cs`, `TypeDatabaseExtensions.cs` (new `IsKnownAppleValueType` helper)
**Tests:** ProjectionCompletenessTests (3 new), MarshallingHelpersTests (2 new), PropertyHandlerTests (1 new)

---

## Session 3: @available -> [SupportedOSPlatform]

**Effort:** 1 session
**Completed:** March 5, 2026
**Status:** CQ-8 completed
**Validation:** 53/53 passing (no regressions)
**Unit tests:** 5516 passing (+49 from Session 1 baseline)

### CQ-8: Missing Platform Availability Annotations (Q5) -- COMPLETED

**Affects:** All libraries with iOS version-constrained APIs.
**Severity:** High — silent runtime crash on older iOS instead of compile-time warning.

Swift `@available(iOS 16.0, *)` annotations weren't mapped to `[SupportedOSPlatform("ios16.0")]`. The .swiftinterface files in xcframeworks contain these annotations in a predictable format.

**Fix:** End-to-end pipeline: parse → model → correlate → emit.

1. **SwiftInterfaceContextTracker** (`Parser/SwiftInterfaceContextTracker.cs`) — NEW reusable infrastructure for .swiftinterface parsing. Extracts boilerplate duplicated across 12+ methods: type stack with brace depth, extension scope tracking, pending annotation accumulation, multi-line continuation, qualified type path building. Designed for Session 4 reuse.

2. **AvailabilityAnnotation model** (`Model/AvailabilityAnnotation.cs`) — NEW record type capturing platform, introduced/deprecated/obsoleted versions, unconditional deprecation/unavailability, message, and renamed fields.

3. **GetAvailabilityAnnotations** (`Parser/SwiftInterfaceAccessParser.cs`) — First consumer of the tracker. Extracts `@available(...)` clauses using balanced-paren matching (handles nested parens in messages like `"Use init(config:) instead"`). Parses into annotations keyed by qualified type/member path. Handles: multi-platform, per-platform lifecycle, stacked annotations, extension-scope inheritance, multi-line continuations.

4. **ABI correlation** (`Parser/SwiftABIParser.cs`) — New `ApplyAvailability`/`ApplyMemberAvailability` wired into all decl creation: types (struct/class/enum/protocol), methods, properties, subscripts, operators. `IsUnavailableFromSwiftInterface` suppresses `@available(*, unavailable)` members/types.

5. **AvailabilityAttributeEmitter** (`Emitter/StringEmitter/AvailabilityAttributeEmitter.cs`) — NEW static utility. Emits `[SupportedOSPlatform]`, `[ObsoletedOSPlatform]`, `[Obsolete]`. Two-path deprecation: types/properties emit `[Obsolete]` directly; methods merge into `EmitSafetyObsolete` via `GetDeprecationMessage()` to avoid duplicate attributes. Parent-relative dedup skips redundant platform+version. Platform mapping: iOS→ios, macOS→macos, tvOS→tvos, watchOS→watchos; visionOS skipped.

6. **Emission integration** — 16 emission points across all surfaces: 7 type-level (class, struct×2, enum×3, protocol), 5 method-level (WrapperEmitter×3, ProtocolHandler, OperatorHandler×2), 3 property-level (PropertyHandler, ProtocolHandler), 1 subscript-level (SubscriptHandler, ProtocolHandler).

**Design decisions:**
- `[Obsolete]` conflict: Safety DiagnosticId wins when both safety + deprecation apply.
- Unavailable: Suppressed via `IsModuleInternal = true` (same pattern as SPI types).
- Platforms: Emit ALL platforms (ios, macos, tvos, watchos), not just iOS.
- Nested-type extensions: Strip module prefix only (first dot component), preserve nested path. `extension Module.Outer.Inner` → `Outer.Inner`.

**Files modified:** 20 files (3 new, 17 modified). See plan for full listing.
**Tests:** 49 new tests — SwiftInterfaceContextTrackerTests (14), SwiftInterfaceAccessParserTests availability region (23), AvailabilityAttributeEmitterTests (12)

---

## Session 4: Default Parameter Values

**Effort:** 1-2 sessions
**Planning:** Plan Mode Required — parsing infrastructure is ready from Session 3. Remaining design questions: which default expressions can be reliably mapped to C#, how to resolve enum case defaults (needs type context), optional params vs convenience overloads, handling `default` keyword for non-trivial types.
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

**Session 3 infrastructure ready for reuse:**
- **`SwiftInterfaceContextTracker`** — Instantiate same tracker, iterate lines, on each `MemberLine` extract default values from the declaration line. Use `tracker.CompletedMultiLine` for multi-line signatures. Use `tracker.QualifiedTypePath` for context and `tracker.BuildMemberKey()` for ABI correlation. No refactoring needed — just a new consumer.
- **`ExtractMemberPrintedName()`** — Already handles func, var, init, subscript. Returns the printed name needed for ABI key correlation.
- **Balanced-paren parsing** — `ExtractAvailableClauses` pattern reusable for extracting default value expressions where closures contain parens.
- **ABI correlation pattern** — Same `Dictionary<string, T>` keyed by qualified path, wired through `SwiftABIParser` constructor. Follow `ApplyMemberAvailability` pattern for `ApplyDefaultValues`.

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
| 3 — @available | **Done** | Mar 5, 2026 | CQ-8 done; 49 new tests, 53/53 validation |
| 4 — Default Params | Not started | | CQ-9; Session 3 infrastructure ready for reuse |
| 5 — Auto-ProjectRefs | Not started | | CQ-10 |
