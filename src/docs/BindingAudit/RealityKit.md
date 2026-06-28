# RealityKit — Binding Audit

- **Package**: SwiftBindings.Apple.RealityKit v26.2.8   **Mode**: apple   **TFM(s)**: net10.0-ios26.2
- **Native**: Apple RealityKit (Xcode 26.3 / iOS 26.2 SDK)
- **Audited at**: main 1e8c27a, generated 2026-06-27

## Verdict

Healthy binding for a thin re-export surface. 100% type coverage (32/32) and 83.4% native member
coverage (136/163). The 12 skipped members split cleanly into two buckets: five ARView spatial-query
methods blocked by unbound ARKit types, and five `AccessibilityEvents.RotorNavigation` members blocked
by cross-module and UIKit type resolution. Code quality is consistent throughout. The test suite is
substantive — 20 distinct cases including strong cross-module and existential-boxing coverage — with the
primary gap being zero coverage of `MaterialColorParameter`'s discriminated-union factory pattern.

## 1. Coverage

### Totals

| Dimension | Count | % native |
|---|---|---|
| Types emitted | 32 / 32 | 100% |
| Members emitted | 136 / 163 | 83.4% |
| Members skipped | 12 | 7.4% |
| Members synthesized | 111 | n/a (generator-added) |

`EmittedMembersByKind`: Property 72, Method 60, Operator 4  
`SkippedMembersByKind`: Property 5, Method 7

### Skip-reason breakdown

| Reason | Count | Members | Classification |
|---|---|---|---|
| `SwiftUIConstraint` | 2 | `CustomAction.key`, `CustomAction.init(key:name:handler:)` | **(a) Correctly excluded** — both reference `Foundation.LocalizedStringResource`, which drags in SwiftUI/Combine |
| `UnsupportedSignature` | 5 | `ARView.snapshot`, `ARView.hitTest`, `ARView.makeRaycastQuery`, `ARView.trackedRaycast`, `ARView.raycast` | **(b) Real gap** — see B1 below |
| `UnsupportedType` | 2 | `RotorNavigation.rotorType`, `RotorNavigation.searchDirection` | **(b) Real gap** — see B2 below |
| `AnyTypeFallback` | 1 | `RotorNavigation.currentItem` | **(b) Real gap** — see B2 below |
| `UnsupportedClosure` | 1 | `RotorNavigation.resultHandler` | **(b) Real gap** — see B2 below |

### (b) Real gaps

**B1 — ARView spatial-query methods (5 Methods)**

All five skipped with `UnsupportedSignature / unsupported placeholder type`. Root cause: their signatures
reference ARKit ObjC-bridged types (`ARKit.ARHitTestResult`, `ARKit.ARRaycastQuery`,
`ARKit.ARRaycastResult`, `ARKit.ARTrackedRaycast`) that are listed in `ObjCPrefixBridges` but absent
from the TypeDatabase.

- `ARView.hitTest` (swiftinterface line 468) — the *ARKit overload* taking `ARHitTestResultType` options;
  deprecated iOS 14+. The RealityKit-native `hitTest(CGPoint, CollisionCastQueryType, CollisionGroup)`
  (lines 8226, 8264) **is** bound and is the correct modern replacement — so this skip has negligible
  consumer impact.
- `ARView.makeRaycastQuery` (line 472), `ARView.trackedRaycast` (line 477), `ARView.raycast` (line 481)
  — current world-plane detection API; the gap is real for any app doing plane-aligned AR placement.
- `ARView.snapshot` (line 790) — captures the AR scene to `UIImage`; completion-closure with a UIKit
  result type. Requires UIKit closure marshalling support.

**Worth a generator fix?** Yes for the raycast trio (makeRaycastQuery / raycast / trackedRaycast),
medium for snapshot. All three raycast methods are blocked by ARKit type bindings, not a generator logic
gap — they will resolve once `SwiftBindings.Apple.ARKit` ships the relevant types.

**B2 — AccessibilityEvents.RotorNavigation (4 Properties + 1 Method)**

- `rotorType` — `RealityFoundation.AccessibilityComponent.RotorType` not in TypeDatabase (cross-module
  opaque type from RealityFoundation's accessibility subsystem).
- `searchDirection` — `UIKit.UIAccessibilityCustomRotor.Direction` not in TypeDatabase.
- `currentItem` — optional existential inner protocol not in TypeDatabase (`AnyTypeFallback`).
- `resultHandler` — getter-only closure with parameters not invocable from C# (unsupported closure shape).
- `init` — constructor references the same placeholder types above.

Effect: `RotorNavigation` is receive-only. Consumers receive it via the `IEvent` system but cannot
inspect the rotor type, search direction, current item, or result handler. This is a functional
accessibility gap for apps that implement `AccessibilityComponent` with custom rotors.

**Worth a generator fix?** Medium priority. `searchDirection` is unblocked by adding
`UIKit.UIAccessibilityCustomRotor.Direction` to the UIKit bindings. `rotorType` requires surfacing
the cross-module `RealityFoundation.AccessibilityComponent.RotorType` (likely via the RealityFoundation
binding). The closure shape (`resultHandler`) needs general closure-with-parameters support.

### Prioritized generator unlocks

| Priority | Unlock | What it unblocks |
|---|---|---|
| High | Ship ARKit type bindings (`ARRaycastQuery`, `ARRaycastResult`, `ARTrackedRaycast`) | `ARView.makeRaycastQuery`, `.raycast`, `.trackedRaycast` — core AR spatial query |
| Medium | Surface `UIKit.UIAccessibilityCustomRotor.Direction` in UIKit bindings | `RotorNavigation.searchDirection` |
| Medium | Surface `RealityFoundation.AccessibilityComponent.RotorType` | `RotorNavigation.rotorType` |
| Low | Getter-only closure with parameters support | `RotorNavigation.resultHandler`; snapshot's UIImage callback |

## 2. C# Quality

**Naming / shape.** PascalCase throughout, no leaked Swift mangling, namespacing is correct
(`RealityKit.*`, `RealityKit.AccessibilityEvents.*`). The `Type` suffix on several nested value types
(`DebugOptionsType`, `RenderOptionsType`, `RenderCallbacksType`, `EnvironmentType`) is slightly
unconventional but internally consistent. Enum values are clean (`CameraModeType.Ar`, `.NonAR`).

**Async.** No Swift `async` methods appear in this module (RealityKit's async surface lives in
RealityFoundation). `System.Threading.Tasks` is imported but unused at the visible surface — harmless.

**Nullability.** `#nullable enable` at file top (RealityKit.cs:1). Optional class returns correctly
annotated (`RealityFoundation.IHasCollision?` at lines 101, 883, 1217; `RealityFoundation.Entity?`
at lines 2810, 3072; `UIKit.UIEvent?` at lines 8431, 8462, 8493, 8524). No missing `?` observed on
reference-type optionals.

**SwiftOptional leakage** (minor ergonomic gap).  
`IEntityGestureRecognizer.Location()` (RealityKit.cs:1477),
`EntityTranslationGestureRecognizer.Translation()` (line 434), and `.Location()` (line 548) return
`Swift.SwiftOptional<Vector3>` rather than `Vector3?`. Consumers must call `.HasValue` / `.Value`
instead of using C# nullable patterns. This is a known limitation of value-type optional marshalling
and is not actionable without boxing or a new projection helper.

**`ARView.LayerClass` typed as `object`** (RealityKit.cs:2968).  
Maps to Swift's `AnyClass` metatype. There is no suitable C# type short of raw `IntPtr`; `object`
is the best achievable representation. No action needed, but worth a `<remarks>` noting the raw ObjC
class handle.

**Empty extension stubs** (minor API noise).  
`MeshResourceRealityKitExtensions`, `ShapeResourceRealityKitExtensions`, `EntityRealityKitExtensions`,
`TextureResourceRealityKitExtensions` (RealityKit.cs:9500–9528) are emitted as empty bodies. These
correspond to Swift extension methods on RealityFoundation types whose signatures couldn't be resolved.
They appear as public types in IDE completions with no members — harmless but noisy. If no members are
expected to land here, they could be suppressed or marked `[EditorBrowsable(Never)]`.

**`MultipeerConnectivityService` missing `IEquatable<>`** (RealityKit.cs:8677).  
The class overrides `Equals(object?)` and `GetHashCode()` (lines 8741–8750) but does not declare
`IEquatable<MultipeerConnectivityService>`, unlike the gesture recognizer classes which do. This means
`EqualityComparer<T>.Default` will use the `object` overload (causing boxing in generic collections).
Compare with `EntityTranslationGestureRecognizer` (line 34) which declares the interface correctly.

**`AccessibilityEvents.CustomAction` has no public constructor** (RealityKit.cs:2370).  
Both `key` and `init(key:name:handler:)` are correctly skipped (`SwiftUIConstraint`). The remaining
emitted constructor is the synthesized parameterless one for receive-side deserialization only — apps
cannot create a `CustomAction` to publish a custom accessibility action from C#. A Swift helper method
is the recommended workaround until `LocalizedStringResource` is bound.

**Lifetime.** All Swift struct types implement `IDisposable` with correct `Dispose()` bodies
(SwiftSafeHandle release + `GC.SuppressFinalize`). Class types (`ARView`, gesture recognizers,
`MultipeerConnectivityService`) follow the ObjC/ARC pattern with finalizer-backed cleanup and
optional deterministic `Dispose()`. No lifetime smells observed.

## 3. Test Coverage

**Case count**: 20 distinct named test cases in `tests/Tests.cs`.

**Depth breakdown:**

| Depth | Cases | Notes |
|---|---|---|
| Weak (metadata smoke only) | 5 | `ARView`, `EnvironmentType`, `DebugOptionsType`, `RenderOptionsType`, `EntityTranslationGestureRecognizer` |
| Medium (constructor / property read / enum values) | 9 | constructors ×2, property reads ×6, enum case values ×1 |
| Strong (round-trip / cross-module / existential boxing) | 6 | Scene traversal, AddAnchor/RemoveAnchor existential, InstallGestures boxing, Entity proxy read, carrier identity A→B→null, CameraTransform SIMD values |

The strong cases are well-chosen: they exercise the cross-module class projection edge
(RealityFoundation types reached through RealityKit @_cdecl wrappers), the `IHasAnchoring`
existential round-trip (historically a conformance-descriptor regression site), and the
`EveryEntityProtocol` carrier identity at both forward and backward paths.

**Coverage gaps by type:**

| Type | Coverage | Note |
|---|---|---|
| `MaterialColorParameter` | None | Discriminated-union factory (`Color/Texture`) + `TryGet` pattern — unique shape, zero tests |
| `ARKitAnchorComponent` | None | iOS 26+ / visionOS 26+; metadata smoke would still be cheap |
| `AccessibilityEvents.Activate` | None | Metadata smoke only needed |
| `AccessibilityEvents.Increment` | None | Metadata smoke only needed |
| `AccessibilityEvents.Decrement` | None | Metadata smoke only needed |
| `AccessibilityEvents.CustomAction` | None | Metadata smoke; round-trip requires receiving a live event |
| `EntityScaleGestureRecognizer` | None | Only `EntityTranslation` has a metadata smoke; Scale/Rotation lack one |
| `EntityRotationGestureRecognizer` | None | See above |
| `MultipeerConnectivityService` | None | Requires real MCSession / networking; acceptable skip |
| `ARView.Unproject()` overloads | None | 3 overloads untested (viewport-relative, plane-transform, relativeToCamera) |
| `ARView.Ray()` | None | Untested |

**Recommended additions (highest value first):**

1. **`MaterialColorParameter` discriminated-union round-trip** — call `Color(UIColor.Red)`, assert
   `Tag == CaseTag.Color`, call `TryGetColor(out var c)` and assert `c` non-null; repeat for `Texture`.
   Tests the `static unsafe` factory + unsafe TryGet marshal path that no other type exercises.

2. **`EntityScaleGestureRecognizer` + `EntityRotationGestureRecognizer` metadata smokes** — two-line
   `MetadataTest<>` calls; cheap parity with the Translation smoke already present.

3. **`AccessibilityEvents.{Activate,Increment,Decrement,CustomAction}` metadata smokes** — four
   `MetadataTest<>` calls; confirms struct layout registration for all event types, not just the
   ones whose properties were inspected.

4. **`ARView.Unproject(CGPoint, CGRect)` call** — can be exercised on the existing NonAR `arView`;
   the method is @_cdecl-wrapped and a null return is valid on a NonAR sim. Tests the overload
   resolution between the three `Unproject` signatures.

## Action Items

| # | Dimension | Finding | Recommendation | Effort | Value |
|---|---|---|---|---|---|
| 1 | Coverage | ARView.makeRaycastQuery / raycast / trackedRaycast skipped (ARKit types not in TypeDatabase) | Blocked on ARKit bindings shipping; no generator fix needed. Track as dependency. | n/a | High |
| 2 | Coverage | RotorNavigation.searchDirection blocked by UIKit.UIAccessibilityCustomRotor.Direction | Add to UIKit bindings | Small | Medium |
| 3 | Coverage | RotorNavigation.rotorType blocked by RealityFoundation.AccessibilityComponent.RotorType | Surface in RealityFoundation binding | Small | Medium |
| 4 | Quality | `MultipeerConnectivityService` missing `IEquatable<MultipeerConnectivityService>` | Add `IEquatable<MultipeerConnectivityService>` to class declaration and regenerate | Trivial | Low |
| 5 | Quality | Empty extension stubs (`MeshResourceRealityKitExtensions` etc.) contribute noise | Suppress or `[EditorBrowsable(Never)]` if no members are expected | Trivial | Low |
| 6 | Quality | `ARView.LayerClass` returns `object` with no doc callout | Add `<remarks>` noting the raw ObjC class handle | Trivial | Low |
| 7 | Tests | `MaterialColorParameter` has zero coverage | Add factory + TryGet round-trip test | Small | High |
| 8 | Tests | `EntityScaleGestureRecognizer` / `EntityRotationGestureRecognizer` missing metadata smokes | Add two `MetadataTest<>` calls | Trivial | Medium |
| 9 | Tests | `AccessibilityEvents.*` (4 types) missing metadata smokes | Add four `MetadataTest<>` calls | Trivial | Low |
| 10 | Tests | `ARView.Unproject()` overloads untested | Add call on existing NonAR `arView`; assert no exception | Trivial | Low |
