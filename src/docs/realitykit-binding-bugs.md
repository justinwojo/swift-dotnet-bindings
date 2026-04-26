# RealityKit / RealityFoundation binding bugs

Tried binding `RealityKit` and `RealityFoundation` in `swift-dotnet-packages` on 2026-04-26 with `SwiftBindings.Sdk` 0.8.0. Both fail at the wrapper-Swift compile step. Generation itself succeeds — `RealityKit` emits 27 types / 126 members, `RealityFoundation` emits 438 types / 1705 members. The failures cluster into a small number of distinct generator bugs which between them originally produced 5 wrapper errors on RealityKit and 281 on RealityFoundation. After Session 1 (RealityKit sweep) and Session 2 (Bug 10 / `@_implementationOnly`), RealityFoundation now reaches the wrapper body and reports 152 errors (76 unique), all attributable to the still-open Bugs 1, 3, and 4.

Failing csprojs (kept in tree as repros):

- `apple-frameworks/RealityKit/SwiftBindings.RealityKit.csproj` (TFM `net10.0-ios26.2`)
- `apple-frameworks/RealityFoundation/SwiftBindings.RealityFoundation.csproj` (TFM `net10.0-ios26.2`)

Reproduce: `dotnet nuke BuildAppleFramework --library RealityKit` / `--library RealityFoundation`. All Swift line numbers below are in the per-csproj `obj/Debug/net10.0-ios26.2/swift-binding/<Module>.Wrapper.swift`.

Environment: .NET SDK 10.0.103, Microsoft.iOS.Sdk 26.2.10197, Xcode 26.2 (build 17C52), iPhoneOS26.2 SDK, macOS arm64. Wrapper compiled with `xcrun --sdk iphoneos swiftc -emit-library -target arm64-apple-ios15.0 -strict-concurrency=minimal`.

The bugs, in rough order of blast radius:

## 1. Missing availability annotations on `_silgen_name` generic-trampoline wrappers

Most-impactful bug. The wrapper for any method on a generic struct or enum that's iOS 18+/26+ is emitted as a `_silgen_name` trampoline **without** an `@available` annotation, even though the body references types or generic constraints that are gated. Result: `'X' is only available in iOS 18.0 or newer` errors against a wrapper compiled at deployment target ios15.0. (`@_cdecl` wrappers in the same file *do* receive `@available` blocks correctly — see e.g. line 6020 of the RealityFoundation wrapper. The generic `@_silgen_name` path appears to be a separate emission site that skipped this step.)

Affected wrapper kinds: every `SBW_*_repeatingForever` for `ActionAnimation<ActionType>`, plus generic-method wrappers for `RealityRenderer`, `ForceEffect`, `SpatialForceFalloff`, `TimedForceFalloff`, `FromToByAction`, `EntityAction`, `UnsafeForceEffectBuffer`, every `Physics*Joint`, etc. Roughly 80 of the 281 RealityFoundation errors.

Concrete example, RealityFoundation.Wrapper.swift:36082-36088:

```swift
@_silgen_name("SBW_ActionAnimation_repeatingForever")
public func SBW_ActionAnimation_repeatingForever<ActionType>(_ self_: UnsafeMutableRawPointer, _ __actiontypeType: ActionType.Type) -> UnsafeMutableRawPointer where ActionType : RealityFoundation.EntityAction {
    var instance = self_.assumingMemoryBound(to: RealityFoundation.ActionAnimation<ActionType>.self).pointee
    let result = instance.repeatingForever()
    let buf = UnsafeMutableRawPointer.allocate(byteCount: MemoryLayout<RealityFoundation.ActionAnimation<ActionType>>.size, alignment: MemoryLayout<RealityFoundation.ActionAnimation<ActionType>>.alignment)
    buf.initializeMemory(as: RealityFoundation.ActionAnimation<ActionType>.self, repeating: result, count: 1)
    return buf
}
```

`ActionAnimation` and `EntityAction` are both `@available(iOS 18.0, *)`. Compare against the same file's `@_cdecl` site at line 6020 (`LowLevelBuffer.withUnsafeBytes`), which correctly carries the iOS 26 annotation block. Fix: the `_silgen_name` emission path needs the same availability annotation logic the `@_cdecl` path already has.

## 2. Optional-parameter name emitted with literal `?`

Whenever a method takes an `Optional<T>` parameter, the generator emits the parameter name with a trailing `?` glued on, then references that same `name?` in the call site. Both the declaration and the call are syntactically wrong Swift.

`expected ':' following argument label and parameter name` × 18, `expected ',' separator` × 18, `expected parameter name followed by ':'` × 18 in RealityFoundation alone — every one is this single bug.

Example, RealityFoundation.Wrapper.swift:36100-36105:

```swift
@_silgen_name("SBW_AnchorEntity_setParent_Optional_preservingWorldTransformBool")
@MainActor
public func SBW_AnchorEntity_setParent_Optional_preservingWorldTransformBool(_ self_: UnsafeMutableRawPointer, _ entity?: Optional<Entity>, _ preservingWorldTransform: Bool) {
    let instance = Unmanaged<RealityFoundation.AnchorEntity>.fromOpaque(self_).takeUnretainedValue()
    instance.setParent(entity?, preservingWorldTransform: preservingWorldTransform)
}
```

`_ entity?:` is invalid syntax. The `?` is bleeding from the type into the parameter name during sanitization. Should be `_ entity: Optional<Entity>` and `instance.setParent(entity, preservingWorldTransform: ...)`. Fix: strip `?` (and probably `!`, array `[]`, generic `<…>`) from parameter-name candidates before emission.

Same bug pattern repeats for `BodyTrackedEntity`, `PerspectiveCamera`, every other Entity subclass that has `setParent(_:preservingWorldTransform:)` or any other optional-parameter API.

## 3. Generic-method wrappers instantiated at the wrong element type (collection types treated as `EntityCollection`)

The generator emits `SBW_CSM_*_insert_*` / `_append_*` / `_replaceAll_*` wrappers for collection-like types — but for any type that isn't `EntityCollection`, it generates them with the **`EntityCollection` body** (calls `__self.insert(contentsOf: Data(bytesNoCopy:…))`), then passes the wrong element type to the underlying generic method. Swift then complains that the element type doesn't satisfy the constraint that's hard-coded into the wrapper.

44 errors in RealityFoundation. Pattern: `instance method 'insert(contentsOf:beforeIndex:)' requires that 'X.Element' (aka 'Y') inherit from 'Entity'`.

Example, RealityFoundation.Wrapper.swift:2935-2944:

```swift
public func SBW_CSM_RealityFoundation_EntityCollection_Swift_Array_Swift_UInt8_insert_29D8E8D1(
    _ _sequence: UnsafeRawPointer,
    _ _sequenceLen: Int,
    _ _index: Int,
    _ self_: UnsafeMutableRawPointer
) {
    var __self = self_.assumingMemoryBound(to: RealityFoundation.RealityRenderer.EntityCollection.self).pointee
    __self.insert(contentsOf: Data(bytesNoCopy: UnsafeMutableRawPointer(mutating: _sequence), count: _sequenceLen, deallocator: .none), beforeIndex: _index)
    self_.assumingMemoryBound(to: RealityFoundation.RealityRenderer.EntityCollection.self).pointee = __self
}
```

The wrapper calls `__self.insert(contentsOf: Data(...), beforeIndex: ...)` on `EntityCollection`, but `EntityCollection.insert(contentsOf:)` requires `S.Element == Entity`, while `Data.Element == UInt8`. Same wrong template gets stamped into wrappers for `BlendShapeWeights` (UInt8/Float), `JointTransforms` (Transform), `MeshSkeletonCollection`, `MeshPartCollection`, `MeshModelCollection`, `MeshInstanceCollection`, `BlendShapeWeightsSet`, `EntityGeometricPins`, `SkeletalPoseSet`, `any PhysicsJoints` etc. — none of these have an Entity-constrained insert.

Each of these collection types has its **own** `insert(contentsOf:)` constrained on its own `Element`. Fix: the per-collection wrapper needs to call the per-collection method, not the EntityCollection-flavored one. Looks like the template is shared across collection types but the body reference wasn't re-resolved per type.

`'no exact matches in call to instance method 'replaceAll''` × 22 and `'append'` × 22 are the same root cause.

## 4. Closure-callback wrappers pass buffer-pointer to a raw-pointer-typed callback

For methods like `withUnsafeBytes(_:)` / `withUnsafeMutableBytes(_:)` whose closure parameter is a buffer pointer, the generator emits an adapter that takes `UnsafeMutableRawBufferPointer` (or `UnsafeRawBufferPointer`) but invokes a `@convention(c)` callback typed `UnsafeMutableRawPointer`. ~18 errors (`cannot convert value of type 'UnsafeMutableRawBufferPointer' to expected argument type 'UnsafeMutableRawPointer'`) plus 18 paired `'converting non-escaping value to '(UnsafeMutableRawBufferPointer) -> Void' may allow it to escape'` errors.

Example, RealityFoundation.Wrapper.swift:6020-6035:

```swift
@_cdecl("SBW_RealityFoundation_LowLevelBuffer_withUnsafeBytes_4CC031B8")
public func _sbw_method_0E5126F9(_ callbackFuncPtr: UnsafeMutableRawPointer?, _ callbackContext: UnsafeMutableRawPointer?, _ self_: UnsafeMutableRawPointer) {
    let cdecl_callback = unsafeBitCast(callbackFuncPtr!, to: (@convention(c) (UnsafeMutableRawPointer, UnsafeMutableRawPointer?) -> Void).self)
    let _adapted_callback = { (_ p0: Swift.UnsafeRawBufferPointer) in
        cdecl_callback(p0, callbackContext!)   // ← p0 is a buffer pointer, callback wants a raw pointer
    }
    let obj = Unmanaged<RealityFoundation.LowLevelBuffer>.fromOpaque(self_).takeUnretainedValue()
    obj.withUnsafeBytes(_adapted_callback)     // ← also: callback is non-escaping, can't be stored
}
```

The fix probably needs to (a) decompose the buffer pointer to `(baseAddress, count)` and pass both to the cdecl callback, and (b) use `withoutActuallyEscaping` or otherwise reconcile the escape attribute.

## 5. `EveryProtocol` extension declarations don't satisfy non-`Self`-mentioning protocol requirements

The generator emits an `EveryProtocol` placeholder type and tries to extend it to conform to bound protocols — but for protocols whose requirements include `static var` properties (no `Self` mention), the bridge stubs in the extension don't actually satisfy the requirement. Swift then rejects the conformance.

Examples, RealityFoundation.Wrapper.swift:191 (`RealityCoordinateSpace`) and 443 (`MaterialFunction`):

```swift
extension EveryProtocol: RealityFoundation.RealityCoordinateSpace {
    public static var scene: SceneRealityCoordinateSpace { fatalError("EveryProtocol does not support static protocol requirements") }
    public static var camera: CameraRealityCoordinateSpace { fatalError("EveryProtocol does not support static protocol requirements") }
}
```

The body acknowledges the case via `fatalError`, but the conformance still fails to type-check at compile time. Either the conformance needs to be skipped entirely for these protocols (and the corresponding C# binding marked unsupported), or the generator needs a different shape for "static-only existential" cases.

## 6. Noncopyable parameter ownership not declared

Methods that take `~Copyable` parameters need explicit `borrowing` / `consuming` ownership annotations in Swift 6. The generator omits them.

Example, RealityFoundation.Wrapper.swift:584:

```swift
public func postProcess(context: RealityFoundation.PostProcessEffectContext<any Metal.MTLCommandBuffer>) {
    var selfProto: RealityFoundation.PostProcessEffect = self
    var contextCopy = context
    ...
}
```

→ `parameter of noncopyable type 'PostProcessEffectContext<any MTLCommandBuffer>' must specify ownership`. Should be `context: borrowing …` (or `consuming` depending on the original Swift signature; usually `borrowing` is the right default).

## 7. Implicit `BindTarget` cast through `.self` returns the nested struct type

Wrappers for `BindTarget.ScenePath.self` and `BindTarget.EntityPath.self` getters try to return `BindTarget` but materialize the nested struct directly:

```swift
let obj = self_.assumingMemoryBound(to: RealityFoundation.BindTarget.ScenePath.self).pointee
let result = obj.self
resultPtr.initializeMemory(as: RealityFoundation.BindTarget.self, repeating: result, count: 1)
```

→ `cannot convert value of type 'BindTarget.ScenePath' to expected argument type 'BindTarget'`. The `.self` accessor here is the type-of-the-instance accessor that returns the nested struct, but the binding metadata expected an upcast. Fix: emit an explicit upcast to `BindTarget`, or treat `.self` accessors as no-ops at the binding layer.

## 8. Missing `import MultipeerConnectivity` (RealityKit only)

For `RealityKit.MultipeerConnectivityService.init(session:)`, the wrapper references `MultipeerConnectivity.MCSession` but never imports `MultipeerConnectivity`. Imports emitted are `RealityKit, Foundation, ARKit, CoreFoundation, CoreGraphics, Metal, simd, UIKit`.

RealityKit.Wrapper.swift:2226:

```swift
let sessionVal = Unmanaged<AnyObject>.fromOpaque(session).takeUnretainedValue() as! MultipeerConnectivity.MCSession
```

→ `cannot find type 'MultipeerConnectivity' in scope`. Likely cause: there's no `MultipeerConnectivityDatabase.xml` under the SDK's `Swift/` database directory, so the auto-import detection has nothing to match against. Either ship a database for MultipeerConnectivity or fall back to scanning the generated wrapper for `Module.Type` references and emitting `import Module` for each one seen.

## 9. `let obj` materialized from pointer used with mutating getter (RealityKit only)

For struct properties whose getter is mutating, the generator binds the receiver as a `let` and then accesses the property — Swift rejects the mutating-getter call.

RealityKit.Wrapper.swift:1246-1250:

```swift
public func _sbw_get_sceneUnderstanding_075C6F0B(_ resultPtr: UnsafeMutableRawPointer, _ self_: UnsafeRawPointer) {
    let obj = self_.assumingMemoryBound(to: RealityKit.ARView.Environment.self).pointee
    let result = obj.sceneUnderstanding   // ← mutating getter on let
    ...
}
```

→ `cannot use mutating getter on immutable value: 'obj' is a 'let' constant`. Either bind `obj` as `var`, or — since this is a getter — don't materialize the struct at all and access through the pointer with `assumingMemoryBound(...).pointee.sceneUnderstanding` directly (the property is read-only from the C# side regardless).

## 10. Module marked as RealityKit implementation detail (RealityFoundation only) — **FIXED (Session 2)**

```
RealityFoundation.Wrapper.swift:1:8: error: module 'RealityFoundation' is an implementation detail of 'RealityKit'; import 'RealityKit' instead
```

Apple has flagged `RealityFoundation` as `@_implementationOnly` from RealityKit — direct `import RealityFoundation` is an error.

**Fix applied (Option D — registry-driven import remapping):** added a `compileImportModule` field to `apple-frameworks.json` (and its schema), populated `RealityFoundation → RealityKit`, and wired `AppleFrameworkRegistry.MapModuleToCompileImport` into `ModuleHandler.EmitSwiftImports` so the wrapper Swift emits `import RealityKit` while .NET namespace and `RealityFoundation.X` Swift type qualifications stay unchanged. After rebuild, the "implementation detail" errors are gone and the wrapper file reaches body compilation, exposing the rest of the bugs (1, 3, 4) on RF.

Post-Session-2 RealityFoundation wrapper-error histogram (152 raw / 76 unique, down from 281):
- Bug 1 (availability gates): 16 unique — `'==' is only available` ×7, `'FromToByAction'` ×2, `'SpatialForceFalloff'` ×2, `'TimedForceFalloff'` ×2, `'ForceEffect'`, `'UnsafeForceEffectBuffer'`, `'subscript(_:)'`.
- Bug 3 (collection-template element-type mismatch): 33 unique — `no exact matches in call to instance method 'replaceAll'` ×11, `'append'` ×11, `no exact matches in call to initializer` ×2, plus 18 `inherit from`/`requires the types`/`conform to protocol` constraint failures (e.g. `'EveryProtocol' does not conform to 'RealityCoordinateSpace'`, `Data.Element (UInt8) inherit from Entity`).
- Bug 4 (closure / unsafe-buffer): 9 unique — `cannot convert UnsafeMutableRawBufferPointer to UnsafeMutableRawPointer` ×6, `UnsafeRawBufferPointer to UnsafeMutableRawPointer` ×3.

The categorization matches the original bug list — no new bug families surfaced, scope unchanged. Sanity check on Bug 14 still confirms broken (`AnchorEntity`/`BodyTrackedEntity`/`ModelEntity` emit as flat `: ISwiftObject` with no `: Entity` base in the generated `RealityFoundation.cs`, exactly as predicted).

This bug was also flagged as the reason RealityFoundation can't ship as a standalone NuGet package — that question is now reframed: the wrapper compiles via `import RealityKit`, but RF still produces its own assembly/namespace `SwiftBindings.RealityFoundation`. Whether to fold RF into `SwiftBindings.RealityKit` as a packaging decision is now independent of the wrapper-compile blocker and can be revisited separately.

---

## Bugs found in follow-up investigation (multi-TFM + C# inspection, 2026-04-26)

After cataloguing bugs 1–10 from the iOS-only build, three more axes were investigated: (a) building at the macOS / Mac Catalyst / tvOS TFMs, (b) reading the *generated C#* (assuming the wrapper compile bugs get fixed), (c) cross-checking the 330 `binding-report.json` skipped members against the wrapper bugs. Five additional bugs surfaced. The TFM matrix also reframed the existing bug priorities.

### Multi-TFM headline finding

The Swift wrapper compile is **clean (0 errors)** for both libraries on **macOS** and **Mac Catalyst**. Every wrapper-compile bug listed above is iOS- or tvOS-specific — bugs 1–9 are mostly availability-version-driven (the same generator code path emits an `iOS 18.0+` annotation on iOS but not on tvOS, where the equivalent gate is `tvOS 26.0+`, etc.). On macOS and Mac Catalyst, the generator's availability emission lines up with the deployment target and the wrapper compiles. tvOS is the worst offender — RealityFoundation hits 898 wrapper errors there vs 281 on iOS, ~1.6× the volume from the same root causes.

This means RealityKit and RealityFoundation are within reach of shipping for macOS and Mac Catalyst *today* (modulo the C# bugs below), without waiting for any wrapper fix. iOS and tvOS need bugs 1–10 closed.

| TFM | RealityFoundation wrapper / C# errors | RealityKit wrapper / C# errors |
|-----|---------------------------------------|--------------------------------|
| net10.0-ios26.2 | 281 / 0 | 5 / 0 |
| net10.0-macos26.2 | 0 / 26 | 0 / 52 |
| net10.0-maccatalyst26.2 | 0 / 26 | 0 / 66 |
| net10.0-tvos26.2 | 898 / 0 | 0 / 54 |

(C# error count is zero on iOS/tvOS because the wrapper-compile failure aborts the build before the C# compile runs — the C# bugs are all there, just hidden.)

## 11. Swift-style named-tuple syntax in generated C#

The generator emits Swift's named-tuple punctuation (`(label: Type, label: Type)`) instead of C#'s element-name syntax (`(Type label, Type label)`). Surfaces only when the C# compile actually runs (macOS / maccatalyst), but exists in iOS/tvOS output too — verified by grepping the iOS `RealityFoundation.cs`.

`apple-frameworks/RealityFoundation/obj/Debug/net10.0-ios26.2/swift-binding/RealityFoundation.cs:69214`:

```csharp
public (key: Swift.String, value: RealityKit.AnimationResource) this[RealityFoundation.AnimationLibraryComponent.AnimationCollection.Index index0]
{
    get => Subscript_Get(index0);
}
```

Should be `public (Swift.String key, RealityKit.AnimationResource value) this[…]`. Triggers CS8124 / CS1026 / CS1519. Affects subscript wrappers on dictionary-shaped collections — generator likely echoes Swift's tuple grammar into the C# emit verbatim. Single-site fix in the C# tuple emitter.

## 12. Missing `Element` type on `[Swift.Array]` in tvOS-only existential getters

`apple-frameworks/RealityFoundation/obj/Debug/net10.0-tvos26.2/swift-binding/RealityFoundation.Wrapper.swift:347`:

```swift
public func SBW_HasModel_get_blendWeights_0(_ containerPtr: UnsafeRawPointer) -> UnsafeMutableRawPointer {
    var existential = containerPtr.load(as: (any RealityFoundation.HasModel).self)
    let result = existential.blendWeights
    let ptr = UnsafeMutablePointer<[Swift.Array]>.allocate(capacity: 1)   // ← `[Swift.Array]` has no Element
    ptr.initialize(to: result)
    return UnsafeMutableRawPointer(ptr)
}
```

→ `error: generic parameter 'Element' could not be inferred`. Should be `UnsafeMutablePointer<[Float]>` (or `[Swift.Array<Float>]`). Same pattern at line 371 for `blendWeightNames` (should be `<String>`). Four occurrences total, all in the **tvOS-only existential-getter codegen path** for `(any RealityFoundation.HasModel)` — iOS/macOS skip emitting these wrappers entirely, so the bug is invisible there. The generator drops the element-type when emitting a boxed pointer for an Array-typed existential property.

## 13. Maccatalyst-only: missing `using ARKit;` for ARKit type references

`apple-frameworks/RealityKit/obj/Debug/net10.0-maccatalyst26.2/swift-binding/RealityKit.cs:2181` references `ARKit.ARSession` in a method signature, but the file's `using` directives don't include the maccatalyst-resolved ARKit namespace. iOS and macOS resolve the same reference fine — likely a workload-namespace gap rather than a generator bug per se, but the generator emits the same C# unconditionally so it manifests as a generator-side issue. Easiest fix: emit `using ARKit;` (or fully-qualify) in every C# file that references ARKit types, regardless of TFM.

## 14. Same-module class inheritance not emitted in C#

The generator emits cross-framework inheritance correctly (`ARView : UIKit.UIView`, `EntityTranslationGestureRecognizer : UIKit.UIGestureRecognizer` — verified in `RealityKit.cs`) but flattens **same-module Swift class hierarchies** into sibling C# classes. In Swift, `ModelEntity`, `AnchorEntity`, `BodyTrackedEntity`, `PerspectiveCamera` all extend `Entity`. In the generated C#, they're parallel:

```csharp
public partial class Entity            : ISwiftObject, IDisposable, IRealityCoordinateSpace, IEquatable<Entity>, …
public partial class ModelEntity       : ISwiftObject, IDisposable, IRealityCoordinateSpace, IEquatable<ModelEntity>, …
public partial class AnchorEntity      : ISwiftObject, IDisposable, IRealityCoordinateSpace, IEquatable<AnchorEntity>, …
public partial class BodyTrackedEntity : ISwiftObject, IDisposable, IRealityCoordinateSpace, IEquatable<BodyTrackedEntity>, …
public partial class PerspectiveCamera : ISwiftObject, IDisposable, IRealityCoordinateSpace, IEquatable<PerspectiveCamera>, …
```

Zero `: RealityFoundation.Entity` declarations exist in `RealityFoundation.cs`. Consequence: every method on `Entity` (`addChild`, `removeFromParent`, `findEntity`, `name`, `position`, …) has to be re-emitted on every subclass, and a C# caller cannot pass a `ModelEntity` to a method expecting `Entity` without an explicit cast. This is a structural usability problem — a C# dev approaching RealityKit will hit it on the first line.

This is *the* highest-leverage usability fix and likely the largest single piece of new generator work in this list. Compare to RoomPlan's `RoomCaptureView : UIKit.UIView` — the cross-namespace path works, the intra-namespace path is missing.

## 15. `SwiftOptional<IntPtr>` returned for nullable reference types

286 occurrences in `RealityFoundation.cs`. Properties and method returns that should be `Scene?` / `Entity?` are typed as `Swift.SwiftOptional<IntPtr>`, exposing a raw pointer instead of a managed reference:

```csharp
// Entity.Scene — should be `Scene?`
public virtual Swift.SwiftOptional<IntPtr> Scene { get => Scene_Get(); }

// Entity.FindEntity — should return `Entity?`
public virtual Swift.SwiftOptional<IntPtr> FindEntity(string name) { … }
```

Caller has to manually round-trip the IntPtr through `SwiftMarshal.MarshalFromSwift<T>` to recover the typed object. The generator already knows the underlying type (it emitted the corresponding Swift wrapper with the right type) — this is an emit-side gap where the C# type-resolution path falls back to `IntPtr` instead of looking up the bound managed type. RoomPlan and CryptoKit don't show this pattern at the same density (RoomPlan returns `RoomPlan.CapturedRoom.Surface?` etc.), so it's likely triggered by something specific to RealityFoundation's type graph — possibly tied to bug 10 (`@_implementationOnly` confuses the type-name resolver).

## Generator-bug ↔ silent-skip cross-link

Bug 5 (`EveryProtocol` static-only conformances, compile error) and the `EveryProtocolConformanceSkipped` skip reason in `binding-report.json` (34 silently dropped types in RealityFoundation, including `Material.MaterialProxy`, `Component.ComponentProxy`, `MaterialFunction`, `RealityCoordinateSpace`, `SynchronizationService`) are **two manifestations of the same generator weakness** — the witness-table emit path can't handle protocols whose requirements are all static. Fixing bug 5 should also unblock those 34 skipped types; right now they're missing from the C# entirely. This is one of the higher-leverage fixes in the bottom list because it kills a compile error *and* recovers ~34 types.

The other ~140 silently-skipped members fall into categories with no compile-bug correspondence (`AnyTypeFallback × 91`, `UnsupportedSignature × 76`, `UnsatisfiedGenericConstraint × 22`, `UnsupportedClosure × 9`, etc.) and are pre-existing generator limitations, not new bugs introduced by RealityKit/RealityFoundation. RealityFoundation's overall skip rate (15%) is **lower than** RoomPlan (21%) and CryptoKit (19%) — the framework is not pathological, just exposed via a code path that has more bugs.

---

## Implementation plan (informed by 2026-04-26 prep research)

Three parallel Sonnet research agents mapped each bug to its source location, audited cross-framework blast radius, and answered the bug-10↔bug-15 coupling question. The plan was then stress-tested by a Codex review which corrected several findings — corrections are inlined below and called out where they change session scope.

Key findings that reshape the plan:

- **Bug 14 is *RealityFoundation-specific*, but not expected to cascade from Bug 10.** LiveCommunicationKit (9), MusicKit (4), ProximityReader (1) emit same-module class inheritance correctly today. RF's flat hierarchy was initially hypothesized to cascade from Bug 10 via the `RealityKit.Entity` references in the wrapper — **Codex review rejected this**: `ResolveClassHierarchy` (`Parser/ModuleProcessor.cs:892–975`) builds `classesByName` from `_typeDecls` of the *current* processed module and only resolves `DirectSuperclassName` against that dictionary. Changing the wrapper's `import` line in #10 doesn't change `DirectSuperclassName` resolution or merge RF types into the RealityKit class lookup. **Plan Session 6 (#14) as full work**; rebuild after Session 2 to confirm rather than betting on the cascade.
- **Bug 15 is one general bug, not "registry gap + general path".** Initial research suggested adding RF to `apple-frameworks.json` as a quick fix for RF's 286 occurrences. **Codex review rejected this**: (a) the JSON property is `module`, not `name`, per the deserializer at `TypeDatabase/AppleFrameworkRegistry.cs:41` and `Data/apple-frameworks.schema.json:14`; (b) more importantly, the `optionalFallback` path at `Marshaler/Projection/TypeProjectionFactory.cs:191–203` also requires `AppleFrameworkRegistry.HasObjCClassPrefix(...)` — RF types like `Entity`, `Scene` have no ObjC prefix, so a registry entry alone changes nothing. (c) The non-accessor failure (e.g. `FindEntity`) goes through the *bound-generic fallback* at `Emitter/StringEmitter/Handler/MethodSignature.cs:556`, not the accessor-only `IntPtr` branch at `:539`. **Treat #15 as one cross-framework session covering both fallback paths**; do not split into "RF JSON quick win + general fix later".
- **Bug 10 ↔ Bug 15 are independent pipeline stages.** Wrapper compile time vs C# generator analysis time. Confirmed by code-path trace.
- **Most bugs are RF-only manifestations.** Bugs 1, 2, 3, 4, 5, 6, 7, 10, 11, 12 hit only RealityFoundation; fixes can land surgically with low cross-framework regression risk. Bugs 8, 9, 13 are RealityKit-only confirmed. Bugs 14, 15 have wider scope.
- **Bug 9 is latent across all frameworks** — the `let obj = self_.assumingMemoryBound(...).pointee` pattern appears in every framework's wrappers (RF: 930, MusicKit: 434, StoreKit2: 211, …). It only manifests when a struct has a mutating-getter property in the bound surface, which today is just `ARView.Environment.sceneUnderstanding`. The fix (`let`→`var` for struct getters) is universal and defensive.
- **Bug 1's blast radius is RF-only in practice.** The earlier "every wrapper file has a `@_silgen_name` without `@available`" reading was a false positive driven by the universal `SBW_Free_<Module>` helper (intentionally unconditional). With that subtracted and a 5-line annotation lookback applied, RF has 73 real cases; every other framework has 0.

### Per-bug source map

Each bug's exact emission site, derived from the wrapper-Swift and C# emit research agents.

| Bug | File (under `src/Swift.Bindings/src/`) | Method | Line | Diff size |
|-----|-----------------------------------------|--------|------|-----------|
| 1 | `Emitter/StringEmitter/Handler/ProtocolExtensionEmitter.cs` | `EmitSwiftWrapper` (two overloads) | 1312–1318, 1537–1544 | small (<30) |
| 2 | `Emitter/StringEmitter/Handler/ProtocolExtensionEmitter.cs` + `Emitter/StringEmitter/SwiftBuilder.cs` | protocol-extension `_silgen_name` wrapper emit (~1255, ~2147); `SanitizeIdentifier` / `IsTypeSyntaxChar` (194). **Codex correction**: the bad `_ entity?:` output is in a `_silgen_name` protocol-extension wrapper (e.g. `RealityFoundation.Wrapper.swift:36102`), not a closure cdecl wrapper — earlier mapping to `ClosureEmitter.SwiftWrapper.cs:820–830` was wrong. Adding `?`/`!` to `IsTypeSyntaxChar` is still likely part of the fix because `GetParamNameFromType` calls it, but the fix must be tested against the protocol-extension path. | — | small (<15) |
| 3 | `Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.cs` | `ClassifyConformerForSwiftParam` + `TryEmitConcreteSwiftWrapper` body emit | 1380–1386, 527 | medium (30–60) |
| 4 | `Emitter/StringEmitter/SwiftBuilder.cs` + `ClosureEmitter.SwiftWrapper.cs` | `GetSwiftCdeclParamType(NamedTypeSpec)` 76–81; `BuildAdapterClosureBody` ~385 | — | medium (30–80) |
| 5 | `Emitter/StringEmitter/EveryProtocolEmitter.cs` | `EmitConformanceForProtocol` else branch | 933–953 | small (<20) |
| 6 | `Emitter/StringEmitter/EveryProtocolEmitter.cs` + `Handler/ProtocolExtensionEmitter.cs` | inline body emission ~1050–1627; `RenderSwiftParam` 1139 | — | small–medium (20–40) |
| 7 | `Emitter/StringEmitter/PropertyWrapperEmitter.cs` | `EmitGetter` | ~355–440 | small (<20) |
| 8 | `Emitter/StringEmitter/Handler/ModuleHandler.cs` | `AppleFrameworks` static set | 374–400 | trivial (1–5) |
| 9 | `Emitter/StringEmitter/PropertyWrapperEmitter.cs` | `EmitSelfReconstruction` (hardcoded `isMutating: false`) | 669–672 | small (<15) |
| 10 | `Emitter/StringEmitter/Handler/ModuleHandler.cs` | `EmitSwiftImports` (no `@_implementationOnly` detection) | 407–408 | depends on approach: small (<20) for CLI flag; large (>100) for auto-detect via `.swiftinterface` scan |
| 11 | `Emitter/StringEmitter/Handler/SubscriptHandler.cs` | `ResolveSubscriptTypeName` falls through to `TypeSpec.ToString()` for tuples | 819 | small (~20) |
| 12 | `Emitter/StringEmitter/WitnessDispatchEmitter.cs` | `GetCSharpTypeName` / `GetSwiftPrimitiveType` → `EmitHeapAllocatedPropertyGetter` | 1158, 1177, 1217 | small (<20) |
| 13 | `Emitter/StringEmitter/Handler/ModuleHandler.cs` | `Emit()` hardcoded C# `using` list (no dynamic scan) | 91–108 | small (5–40) depending on approach |
| 14 | `Parser/ModuleProcessor.cs` + `Emitter/StringEmitter/Handler/ClassHandler.cs` | `ResolveClassHierarchy` (892–975) only sees same-module types; `IsEffectivelyDerived` (391) returns false when `ResolvedSuperclass` is null | — | residual after #10: TBD. Without #10: large (>150). |
| 15 | `Emitter/StringEmitter/Handler/MethodSignature.cs` (`HandleReturnType` accessor branch 539–542; **bound-generic fallback at 556 — primary fix site for non-accessor failures like `FindEntity`**) + `Marshaler/Projection/TypeProjectionFactory.cs` (191–203) + `Marshaler/BoundGenericsHandler.cs` (Optional<T> path) | — | medium (40–80). **Codex correction**: a registry entry alone is *not* sufficient — `optionalFallback` requires `AppleFrameworkRegistry.HasObjCClassPrefix` which RF types fail. Property name in JSON is `module`, not `name` (per `AppleFrameworkRegistry.cs:41`). Treat as one cross-framework fix covering both accessor and bound-generic fallback paths. |

**Existing logic to reuse** (derived from research, useful when implementing):
- Bug 1: `WrapperEmitterHelpers.MergeAvailability` + `EmitSwiftAvailability` (used at `MethodGenericBridgeEmitter.cs:438–441`, `MethodWrapperEmitter.cs:998–1001`, `WrapperEmitter.Marshalling.cs:93–98`). Mechanical port.
- Bug 2: `SwiftBuilder.SanitizeIdentifier` already strips `<>[]()`; needs `?` and `!` added. Other callers (`MethodWrapperEmitter.cs:719`, `ConstructorWrapperEmitter.cs:887`) already sanitize labels — the closure path forgot to.
- Bug 4: `CdeclParamMapper.Map` (`CdeclParamMapper.cs:53–71`) already correctly decomposes `UnsafeRawBufferPointer` into `(ptr, len)` for direct `@_cdecl` free functions. Same logic needed in the `@convention(c)` callback path.
- Bug 9: `SelfReconstructionEmitter.Emit` already accepts an `isMutating` parameter and emits `var obj` correctly when `true`. Caller `EmitSelfReconstruction` hardcodes `false`. One-line wiring fix.
- Bug 11: `TupleHandler.GetCSharpTupleType` (`Marshaler/TupleHandler.cs:264–281`) renders `{type} {label}` correctly and is used by `MethodHandler` for method returns. Subscripts skip it; just route through it.
- Bug 12: `ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec` (`Handler/ExistentialBypassEmitter.cs:~1229`) already renders `Swift.Array<Swift.Float>` as `[Float]`. Replace the broken `GetCSharpTypeName` → `GetSwiftPrimitiveType` chain with a direct call to it.
- Bug 13: `EmitSwiftImports` (lines 374–451 same file as #13's emission site) already does the dynamic Apple-framework scan for the Swift wrapper. Replicate the same scan for C# `using` directives.
- Bug 14: ObjC-superclass path at `ClassHandler.cs:173–188` (driven by `HasExternalSuperclass && IsObjCBridgedSuperclass` via `AppleFrameworkRegistry`) already emits `: UIKit.UIView` etc. correctly. Same shape needed for cross-module Swift superclasses.
- Bug 15 (RF subset): adding `{ "name": "RealityFoundation", "autoBridge": true, "optionalFallback": true }` to `apple-frameworks.json`. The general bug requires a different fix at `MethodSignature.HandleReturnType` / `IsLargeOptionalParam` — needs in-session investigation.

### Cross-framework blast radius

Counts measured against built `obj/Debug/<TFM>/swift-binding/` outputs in `swift-dotnet-packages/apple-frameworks/` on 2026-04-26.

**Counting query**: Bug 15 numbers below were originally reported by the research agent and **do not exactly reproduce** under direct grep. A Codex re-count with `Swift.SwiftOptional<IntPtr>` shows RF: 240, StoreKit2: 41, MusicKit: 15, RK: 19; with unqualified `SwiftOptional<IntPtr>` shows RF: 327, StoreKit2: 41, MusicKit: 27, TipKit: 24. Treat the per-framework densities below as *directional* — the "11 of 13 frameworks affected" conclusion holds, but workers should not use these numbers as exact regression baselines. Before starting Session 7, define a single counting query (recommended: `grep -c 'Swift\.SwiftOptional<IntPtr>'` on the iOS C# output for each framework) and lock that as the regression baseline.

| Bug | RF / RK | Other apple-frameworks | Fix scope |
|-----|---------|-----------------------|-----------|
| 1 | RF: 73 | 0 (after subtracting `SBW_Free` helper + already-annotated cases) | RF-only |
| 2 | RF: 9 | 0 | RF-only (fix should still strip `?`/`!` universally for safety) |
| 3 | RF: 3 generator sites (manifest as ~44 compile errors) | 0 (CryptoKit's 148 `Data(bytesNoCopy:)` calls are correct uses) | RF-only |
| 4 | RF: 9 | 0 | RF-only |
| 5 | RF: 2 | 0 | RF-only (also unlocks 34 silently-skipped types) |
| 6 | RF: 1 (`PostProcessEffect.postProcess`) | 0 (no other framework binds noncopyable param types today) | RF-only |
| 7 | RF: 2 (`BindTarget.ScenePath`, `EntityPath`) | 0 | RF-only |
| 8 | RK: 1 (`MultipeerConnectivity.MCSession`) | 0 confirmed | RK-only confirmed; latent for any unimported framework reference |
| 9 | RK: 1 (`ARView.Environment.sceneUnderstanding`) | latent everywhere — pattern appears in every framework | Apply universally; defensive `let`→`var` |
| 10 | RF: 1 (load-bearing) | 0 (Apple-imposed boundary unique to RK/RF) | RF-only |
| 11 | RF: 1 | 0 | RF-only |
| 12 | RF: 4 (tvOS) | 0 across all built tvOS outputs | RF-only |
| 13 | RK: 66 errors (maccatalyst) | 0 (no other maccatalyst C# references ARKit) | RK-only |
| 14 | RF: confirmed flat (re-verified post-Session-2 — `AnchorEntity`/`BodyTrackedEntity`/`ModelEntity` still emit without `: Entity`) | LCK / MusicKit / ProximityReader: working today | RF-only — cascade rejected; needs full Session 6 fix |
| 15 | RF: 286, RK: 19 | StoreKit2: 37, MusicKit: 23, TipKit: 21, Translation: 15, CryptoKit: 12, WorkoutKit: 12, LCK: 9, RoomPlan: 6, ProximityReader: 3 | **General — affects 11 of 13 built frameworks.** Two-layer fix: registry add for RF + general path fix for the rest. |

### Coupling decisions

- **Bug 10 ↔ Bug 15: independent.** Different pipeline stages (Swift wrapper compile vs C# generator analysis). Confirmed by the C# emit agent reading both code paths end-to-end. Fixing one does *not* fix the other.
- **Bug 10 → Bug 14: cascade hypothesis rejected (Codex review).** `Parser/ModuleProcessor.cs:892–975` resolves superclasses by exact `DirectSuperclassName` match within the current module's `_typeDecls`. Changing the wrapper's `import RealityFoundation` to `import RealityKit` does *not* alter the parser's view of `_typeDecls` membership or the `DirectSuperclassName` strings — those come from the ABI JSON. So #10's wrapper-import fix won't make `Entity` resolve as a same-module superclass for `ModelEntity`/etc. **Plan Session 6 (#14) as full work.** Verify after Session 2 only as a sanity check; do not depend on the cascade.
- **Bug 15 is a single C# emit path bug, not "registry gap + general fix".** Codex traced two fallback sites (`MethodSignature.cs:539` accessor branch, `:556` bound-generic branch) that both fall back to `IntPtr` for nullable class returns when the type isn't ObjC-bridged. Adding RF to `apple-frameworks.json` doesn't help because RF types lack the ObjC class prefix that `optionalFallback` requires. Land as one cross-framework session.

### Test fixture pattern (per session)

Each session must add at least one BindingTests fixture per bug being fixed:
- Swift source under `BindingTests/Sources/SwiftBindingsTestLib/<Domain>/<BugName>.swift` reproducing the pattern
- C# test under `BindingTests/RuntimeTestsApp/<Domain>/<BugName>Tests.cs` asserting the fixed shape compiles and round-trips
- Per CLAUDE.md "BindingTests are the real end-to-end gate" — required for every generator change

Per-bug fixture sketches (use these as starting points; expand as needed):
- **#1**: generic struct with `iOS 18+` availability, method returning `Self<Generic>`. Verify wrapper compiles at `-target arm64-apple-ios15.0`.
- **#2**: method on a struct taking `Optional<SomeType>` parameter named after a class. Assert wrapper has `_ name: Optional<…>` not `_ name?:`.
- **#3**: custom collection type with non-`UInt8`/non-`Data` element conforming to `RangeReplaceableCollection`. Assert `insert(contentsOf:beforeIndex:)` wrapper uses the right body.
- **#4**: type with `withUnsafeBytes(_ body: (UnsafeRawBufferPointer) -> Void)` method.
- **#5**: protocol with only `static var` requirements (no `Self` mention). Assert binding compiles or skip path is taken cleanly.
- **#6**: method taking a `~Copyable` parameter.
- **#7**: nested struct with `.self` property of parent type.
- **#8**: type referencing a class from a framework not in current `AppleFrameworks` set.
- **#9**: struct with mutating-getter computed property.
- **#10**: a wrapper-only test fixture isn't possible without an `@_implementationOnly` chain. Use the actual `RealityFoundation` build as the regression gate.
- **#11**: type with `subscript(...) -> (label1: T1, label2: T2)`.
- **#12**: `(any Protocol)` existential getter returning `[Float]`. Compile target tvOS.
- **#13**: maccatalyst-targeted type referencing `ARKit.X`. Verify `using ARKit;` emits.
- **#14**: same-module class hierarchy `Sub: Base` where `Base` is in a *different* module than the one being processed (the actual scenario RF hits — its `Entity` reference resolves into a foreign-module `ClassDecl` lookup, not the local `_typeDecls` dictionary). Existing LCK/MusicKit/ProximityReader cases work because their hierarchies are entirely within one processed module; do not use them as the regression bar — they don't exercise the broken path.
- **#15**: nullable class-typed property/return on a registered framework. Assert the C# type is `T?` not `SwiftOptional<IntPtr>`.

### Architecture decisions (resolve before relevant session starts)

**Bug 10 — RealityFoundation packaging**

Four viable approaches; pick before Session 2 starts:

| Approach | Effort | Implications |
|----------|--------|--------------|
| **A. CLI flag**: `--umbrella-module RealityKit` for RF builds. Generator emits `import RealityKit` instead of `import RealityFoundation` when flag is set. C# binding still namespaces types as `RealityFoundation.X`. | Small (<20 lines) | RF can ship as a separate package, but consumer-side discoverability is awkward (consumers add a `SwiftBindings.RealityFoundation` PackageReference but never write `import RealityFoundation` in any context). Slightly leaky abstraction. Per-build CLI state is not source-controlled. |
| **B. Auto-detect via `.swiftinterface` scan**: read parent module's `.swiftinterface` for `@_implementationOnly import <X>` directives. Auto-substitute the umbrella import. | Large (>100 lines) | Most robust; future implementation-only modules from Apple work automatically. But: still leaves the question of whether RF should be its own NuGet package or fold into `SwiftBindings.RealityKit`. |
| **C. Fold RF into `SwiftBindings.RealityKit` package**: stop building RF as a separate package; emit RF's bound types under `SwiftBindings.RealityKit` with `RealityFoundation.X` namespace prefix preserved. | Small generator change + packaging restructure | Cleanest consumer story (one PackageReference covers the whole RealityKit surface). Requires SDK packaging change to support multi-module emit into one package, or a build-time merge step. |
| **D. Registry-driven import remapping (Codex suggestion)**: add a `compileImportModule` (or `umbrellaModule`) field to `apple-frameworks.json`. RF stays the logical module/namespace; `ModuleHandler.EmitSwiftImports` consults the field and emits `import RealityKit` instead of `moduleDecl.Name` at line 408. | Small (~30 lines + JSON) | Less leaky than A (decision is in source-controlled metadata, not per-build CLI state). Smaller than B (no `.swiftinterface` parser). Doesn't solve package unification long-term. Reusable for any future Apple `@_implementationOnly` module without code changes. |

**Recommended**: **D as the stop-gap**, then C if/when packaging supports it. D supersedes the original A recommendation — it's the same effort, more reproducible, and reusable. C remains the long-term answer for Apple framework families with implementation-only modules; D buys time without preempting C.

**Bug 14 — plan as full work.** Codex review rejected the "Bug 10 cascade" hypothesis (see Coupling decisions above). The fix is the cross-module `ClassDecl` resolver work outlined in the source map. Verify after Session 2 only as a sanity check; do not let the timeline depend on a cascade. Likely ~150 lines across `ModuleProcessor.cs`, `ClassDecl.cs`, `ClassHandler.cs`.

**Bug 15 — single general-fix session.** Treat as one cross-framework session, not "RF JSON quick win + general fix later" (Codex correction). The fix targets both fallback sites in `MethodSignature.cs`: the accessor branch at `:539` *and* the bound-generic fallback at `:556`, plus the `Optional<T>` path in `BoundGenericsHandler.cs`. The `optionalFallback` registry mechanism is irrelevant for RF/etc. since they lack ObjC class prefixes. Specifics need in-session investigation; flag if the fix requires type-database schema changes.

### Session plan

| # | Session | Bugs | Files touched (approx.) | Validation gates |
|---|---------|------|-------------------------|------------------|
| 1 | **Big sim-only emit sweep** | 1, 2, 6, 7, 8, 9, 11, 12, 13 | `ProtocolExtensionEmitter.cs`, `SwiftBuilder.cs`, `EveryProtocolEmitter.cs`, `PropertyWrapperEmitter.cs`, `ModuleHandler.cs`, `WitnessDispatchEmitter.cs`, `SubscriptHandler.cs` | `nuke test` + `nuke validate` + `nuke binding-tests` (sim) |
| 2 | **#10 wrapper-import handling** | 10 | `ModuleHandler.cs::EmitSwiftImports` (option D adds field to `apple-frameworks.json` + reader). Also rebuild RF afterward and confirm #14 status (expected: still broken, scope unchanged). | sim |
| 3 | **#3 collection-template re-resolution** | 3 | `ConcreteProtocolSpecializationEmitter.cs` | sim |
| 4 | **#4 closure-buffer adapter** | 4 | `SwiftBuilder.cs`, `ClosureEmitter.SwiftWrapper.cs` | sim + device (calling convention change) |
| 5 | **#5 EveryProtocol witness skip** | 5 | `EveryProtocolEmitter.cs` | sim + device (witness-table machinery) |
| 6 | **#14 same-module class inheritance** | 14 | `Parser/ModuleProcessor.cs`, `Model/TypeDecl/ClassDecl.cs`, `Emitter/StringEmitter/Handler/ClassHandler.cs` | sim + device |
| 7 | **#15 general nullable-class fix** | 15 (covers RF + 9 other frameworks) | `Emitter/StringEmitter/Handler/MethodSignature.cs` (both fallback sites: 539 accessor, 556 bound-generic), `Marshaler/Projection/TypeProjectionFactory.cs`, `Marshaler/BoundGenericsHandler.cs` | sim + device |

**7 implementation sessions** (no longer 5–7 contingent — Codex review eliminated both cascade hypotheses). Sessions are largely independent after Session 1; 3/4/5/7 can run in parallel via worktrees if you want wall-clock compression.

**Why this ordering, briefly:**
- Session 1 is the dense sweep — 9 bugs, all sim-only, mostly small fixes in distinct files. File collisions (#7+#9 in `PropertyWrapperEmitter.cs`, #8+#13 in `ModuleHandler.cs`) are in different methods, mechanical to merge. If review burden gets bad, split by file boundary into 1a (wrapper-Swift sweep: 1, 2, 6, 7, 8, 9, 12) and 1b (C# emit sweep: 11, 13).
- Session 2 is the wrapper-import-handling session for `@_implementationOnly`. Architecture decision on Bug 10 (above — recommended option D) needs sign-off before this session starts.
- Sessions 3, 4, 5 are the three independent architectural fixes. Each could split if the worker hits unexpected complexity.
- Session 6 (#14) is full work — see Coupling decisions for why the cascade hypothesis was rejected. Plan ~150 lines across three files.
- Session 7 (#15) closes out the cross-framework `SwiftOptional<IntPtr>` regression. Can be moved earlier (between Sessions 2 and 3) since it's general-fix territory and unlocks multiple frameworks at once, not just RF. Lock the regression-counting query (see Cross-framework blast radius note) before starting.

**Shipping milestones along the path:**
- After Session 1: most iOS wrapper compile errors resolved (RF down from 281 to ~20–40, RK to ~0). macOS / Mac Catalyst still blocked on Sessions 2+ for C# compile errors.
- After Session 2: RF wrapper compiles (`@_implementationOnly` import resolved). macOS / Mac Catalyst still blocked on #11, #13 (closed by Session 1) and #15 — so milestone is conditional on Session 1 also having shipped.
- After Sessions 3+4+5: iOS RealityKit ships (RealityFoundation still needs #14 + #15).
- After Session 6: RealityFoundation's class inheritance is correct in C# — usability blocker resolved.
- After Session 7: cross-framework `SwiftOptional<IntPtr>` regression closed; nullable-ref typing improves for 11 frameworks. Both libraries ship for all four TFMs.
- tvOS shipping: covered by Session 1 (#12) + verifying #1's availability emission also covers tvOS. No dedicated session needed unless verification surfaces a gap.

### Risks worth surfacing

- **Session 1 bisect cost**: 9 bugs in one commit. Each bug has its own test fixture; if a baseline regresses, narrow by file. If the worker hits any nontrivial regression mid-session, split rather than push through.
- **Bug 10 architecture decision is load-bearing**: A/B/C/D affects packaging and reusability of the mechanism. Decide before Session 2 starts. Recommendation: D.
- **Bug 14 is real work, not a cascade.** Plan ~150 lines across `ModuleProcessor.cs`, `ClassDecl.cs`, `ClassHandler.cs`. Session 6 is the second-largest session in the plan after #15.
- **Bug 15 general path is uncharted**: research agent's RF-specific analysis was partially wrong (Codex correction). The actual fix targets two fallback sites in `MethodSignature.cs` plus the `Optional<T>` path in `BoundGenericsHandler.cs`. Worker should message back if the fix requires type-database schema changes.
- **Zero-regression gate is tight on #15**: 11 frameworks change shape. The fix needs cross-framework BindingTests confirmation, not just RF. Lock the counting query first.
- **JSON field name for any registry edit is `module`, not `name`** (Codex correction). Per `TypeDatabase/AppleFrameworkRegistry.cs:41` and `Data/apple-frameworks.schema.json:14`. Applies to options A/D for Bug 10 if either lands as a JSON edit.

### Background (preserved from earlier passes)

The 140-ish silently-skipped members in the `AnyTypeFallback` / `UnsupportedSignature` / `UnsatisfiedGenericConstraint` categories are out of scope of this doc — pre-existing generator limitations tracked in their own backlog. RealityFoundation's overall skip rate (15%) is *lower* than RoomPlan (21%) and CryptoKit (19%); the framework is not pathological, just exposed via a code path that has more bugs.
