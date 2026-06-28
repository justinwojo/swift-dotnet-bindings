# RealityFoundation — Binding Audit

- **Package**: SwiftBindings.Apple.RealityFoundation v26.2.8   **Mode**: apple   **TFM(s)**: net10.0-ios26.2
- **Native**: Apple SDK framework (RealityFoundation / RealityKit re-export)
- **Audited at**: swift-dotnet-packages 1e8c27a, generated 2026-06-27

## Verdict

Strong coverage at 92.6% members (2375/2564) across a 570-type, ~220k-line surface. The ECS core — `Entity`, `ModelEntity`, `Transform`, `ComponentSet`, `Scene`, `MeshResource` — is fully usable. All `SIMD3<Float>/simd_quatf/simd_float4x4` fields marshal cleanly to `System.Numerics.{Vector3,Quaternion,Matrix4x4}` with verified all-lane round-trips. The headline real gap is **`ModelComponent.Materials` getter always throws `NotSupportedException`** (the `Material` protocol proxy wasn't emitted), which prevents any inspection of a model entity's material list — write-only materials is a real consumer friction point. A second structural gap is **no typed `Get<T>` on `ComponentSet`**: you can `Set<T>` a component but cannot retrieve it typed, only via an untyped `IComponent` subscript. The 316 skips are otherwise dominated by intentional Codable pruning (64) and peripheral animation/Metal/physics-authoring APIs, none of which block basic ECS use.

---

## 1. Coverage

### Numbers

| Metric | Value |
|---|---|
| TotalTypes | 570 |
| EmittedTypes | 569 (99.8%) |
| SkippedTypes | 1 (`Entity.ChildCollection.IndexingIterator` — `IndeterminatePwtShape`) |
| TotalMembers | 2564 |
| EmittedMembers | 2375 (92.6%) |
| SkippedMembers | 316 |
| SynthesizedMembers | 2381 |

**Emitted by kind**: Property 1352, Method 888, Operator 96, Subscript 39.

**Skipped by kind**: Method 202, Property 79, Subscript 19, Type 16.

---

### Skip-reason breakdown

| Reason | Count | Classification |
|---|---|---|
| UnsupportedSignature | 66 | Mix — see below |
| SynthesizedCodable | 64 | **(a) Correctly excluded** — intentional by design |
| AnyTypeFallback | 52 | Mostly **(b) real gaps** — mostly peripheral |
| UnsupportedType | 41 | Mix — see below |
| DuplicateSignature | 41 | Mix — mostly peripheral |
| EveryProtocolConformanceSkipped | 16 | **(b) Real gaps** — includes `Material` proxy |
| UnsupportedClosure | 8 | **(b) Real gap** — blocks event subscription |
| SwiftUIConstraint | 8 | **(a) Correctly excluded** — SwiftUI type in signature |
| NonBlittableCallConvSwift | 6 | **(a) Correctly excluded** — generics + Array params crash |
| UnsatisfiedGenericConstraint | 5 | **(a) Correctly excluded** — collection init; not emittable |
| IndeterminatePwtShape | 1 | **(a) Correctly excluded** — opaque protocol not projected |
| GenericProtocolConstraint | 1 | **(a) Correctly excluded** — PAT constraint |
| StaticProtocolMember | 1 | **(a) Correctly excluded** — `System.init` protocol req |

---

### (a) Correctly excluded

- **SynthesizedCodable (64)**: All `encode`/`init(from:)` members pruned. `Encoder`/`Decoder` are unresolvable existentials. Intentional; correct.
- **SwiftUIConstraint (8)**: `AccessibilityComponent.label/value/customActions` typed as `SwiftUI.LocalizedStringKey` or `SwiftUI.AnyView`. No SwiftUI bridge needed here; correct.
- **NonBlittableCallConvSwift (6)**: `FromToByAnimation.init`, `SampledAnimation.init`, `ActionAnimation.init`, `MeshBuffer.init`, `MeshResource.Skeleton.init` — all have `[T]` or `Dictionary` container generic params that crash in `CallConvSwift` dispatch. Correct skip; @_cdecl wrapper not possible.
- **UnsatisfiedGenericConstraint (5)**: `AudioLibraryComponent.init([String:AudioResource])`, `AnimationLibraryComponent.AnimationCollection.init`, `SkeletalPose.init`, `BlendShapeWeightsData.init` — Swift `Dictionary<String, AudioResource>` / similar bound-generic arg can't satisfy `ISwiftObject`. Correct.

---

### (b) Real gaps

**UnsupportedSignature (66)** — mixed bag:

| API | Details | ECS-core? | Fix worth it? |
|---|---|---|---|
| `PhysicsJoints.init` | Variadic `T…` — can't wrap | No | Medium — physics constraint authoring blocked |
| `AudioPlaybackController.seek` | Placeholder type in CMTime param | No | Low — audio seek peripheral |
| `TextureResource.DrawableQueue.Descriptor.init` | Placeholder type in MTLPixelFormat | No | Low — Metal render pipeline |
| `IKRig.JointCollection.subscript` | Incompatible wrapper call syntax | No | Low — IK-only |
| `PhotogrammetrySample.init` | Placeholder in AVDepthData/CMSampleBuffer | No | Low — photogrammetry |

→ None block ECS core. All blocked by missing Metal/CoreMedia/AVFoundation type bridging.

**AnyTypeFallback (52)** — the two category leaders:

| API | Details | Impact |
|---|---|---|
| `BindableValuesReference.subscript` | Return type is `any BindableData` existential, falls to `object` | Medium — animation binding authoring awkward |
| `ParameterSet.subscript` | Return type is `any ParameterValue`, falls to `object` | Medium — shader parameter access awkward |
| `EmphasizeAction/BillboardAction/PlayAnimationAction.animatedValueType` | Inner PAT protocol not in TypeDB | Low — animation metadata |
| `PhotogrammetrySample.depthDataMap/gravity/objectMask` | Typed as `AVDepthData?`/`CMAcceleration?` — not in TypeDB | Low — photogrammetry only |
| `ActionEvent.action/parameter` | Open generic existential `any EntityAction` | Low — action event reflection |

→ None are ECS core. Blocked by existential PAT protocols not in the type database.

**UnsupportedType (41)**:

| Affected Types | Missing Members | Impact |
|---|---|---|
| `FromToByAnimation<T>` | `fromValue`, `toValue`, `byValue`, `isScaleAnimated`, `isRotationAnimated`, `isTranslationAnimated`, `jointNames`, `weightNames` | Medium — can't inspect/author keyframe ranges; animation authoring limited |
| `SampledAnimation<T>` | `frames`, skeletal flags | Medium — same as above |
| `PhysicsBodyComponent` | `isTranslationLocked`, `isRotationLocked` (OptionSet/SIMD3<Bool>) | Medium — locking physics axes requires workaround |
| `LowLevelTexture.Descriptor` / `TextureResource.DrawableQueue.Descriptor` | `textureType`, `textureUsage`, `swizzle`, `timeout` | Low — Metal texture setup |
| `Scene.timebase` | `CMTimebase` not bridged | Low — timing control only |
| `LowLevelMesh.Attribute/Descriptor` | `format` (MTLVertexFormat), `indexType` | Low — low-level mesh only |

Root cause for most: Metal `OptionSet`-backed enums (`MTLTextureType`, `MTLTextureUsage`, `MTLVertexFormat`) not bridged; or constrained-extension on open generic type (`FromToByAnimation<T>`) not projected.

**DuplicateSignature (41)**:

| API | Cause | Fix? |
|---|---|---|
| `TextureResource.init` (3 overloads) | After type-erasure, `CGImage/URL/Data` ctors produce identical C# ctor signatures | Medium — needs mangled-name disambiguation |
| `FromToByAction.init` (5 overloads) | AnyType-erased generic ctors collapse to same signature | Low — animation-only |
| `PhysicsBodyComponent.init` | `IEnumerable<ShapeResource>` + float variants collapse | Low — physics-only |

**EveryProtocolConformanceSkipped (16)** — ECS-core impact:

| Proxy | Reason | Impact |
|---|---|---|
| `Material.MaterialProxy` | `UnsatisfiedHigherKindConstraint` | **HIGH** — blocks `ModelComponent.Materials` getter (throws NSE) |
| `EntityAction.EntityActionProxy` | No decision record | Medium — can't box custom actions for `EntityActionComponent` |
| `PhysicsJoint.PhysicsJointProxy` | No decision record | Medium — physics joint programming |
| `SynchronizationService.SynchronizationServiceProxy` | `UnsatisfiedHigherKindConstraint` | Low — collaboration only |
| `TransientComponent.TransientComponentProxy` | `UnsatisfiedPrecondition` | Low — transient flag |

**UnsupportedClosure (8)**:

| API | Impact |
|---|---|
| `Scene.subscribe` (2 overloads) | **HIGH** — primary RealityKit event subscription pattern blocked; no way to receive `SceneEvents.Update`, collision events, etc. |
| `RealityRenderer.subscribe` | Medium — renderer-level event hook |
| `IKRig.JointCollection.forEach` | Low — IK iteration |
| `CustomMaterial.withMutableUniforms` (2) | Low — custom shader uniform mutation |
| `MaterialParameters.Texture.Sampler.modify/access` | Low — texture sampler accessor |

Root cause: generic-over-protocol closure (`(Event) -> Void where Event : EventType`) can't be marshalled.

---

### Prioritized generator unlocks

1. **`Material` protocol proxy** (`EveryProtocolConformanceSkipped`, UnsatisfiedHigherKindConstraint): Unblocks `ModelComponent.Materials` getter (currently throws). HIGH value — every ModelEntity user will hit this. Tractability: medium (higher-kinded protocol constraint is the blocker in the EveryProtocol emitter).

2. **`ComponentSet.Get<T>`** — typed component getter: The Swift `ComponentSet.subscript<T: Component>(T.Type) -> T?` metatype-subscript pattern has no C# projection. `Set<T>` was synthesized but `Get<T>` was not. Without it, consumers can't read typed components back from an entity. HIGH value — core ECS read path. Tractability: medium (metatype subscript → generic method synthesis, analogous to `Set<T>`).

3. **`Scene.subscribe` closure** (UnsupportedClosure): Blocks the event-driven ECS update loop pattern that virtually every RealityKit app uses. Medium tractability (generic-over-protocol event closures).

4. **`FromToByAnimation<T>` constrained-extension properties** (UnsupportedType): `fromValue/toValue/byValue/isScaleAnimated` etc. — needed to author keyframe animations in code. Medium value, medium tractability (open-generic + constrained-extension path is a known generator gap).

5. **`AddChild/RemoveChild/RemoveFromParent` default value for `preservingWorldTransform`**: Swift default is `false`; C# binding requires it. Small ergonomics issue, trivial fix (emit overloads or default parameter).

---

## 2. C# Quality

**Naming / shape** — Clean. PascalCase throughout, no leaked Swift mangling, sensible namespace (`RealityFoundation.*`). Protocol interfaces prefix with `I` (`IComponent`, `IHasModel`, `IHasTransform`). Nested types reasonable (`Entity.ComponentSet`, `Entity.ChildCollection`, `MeshBuffer<T>`). Generic type projections are correct (`MeshBuffer<Vector3>`, `FromToByAnimation<Transform>`).

**SIMD marshalling** — Strong. All four critical `Transform` fields:
- `Scale`: `SIMD3<Float>` → `System.Numerics.Vector3` (RealityFoundation.cs:199261)
- `Rotation`: `simd_quatf` → `System.Numerics.Quaternion` (RealityFoundation.cs:199341)
- `Translation`: `SIMD3<Float>` → `System.Numerics.Vector3` (RealityFoundation.cs:199422)
- `Matrix`: `simd_float4x4` → `System.Numerics.Matrix4x4` (RealityFoundation.cs:199428)

All use the indirect/pointer @_cdecl path (`stackalloc` buffer → `MarshalToSwift` → `IntPtr value` param), preventing the AArch64 NEON vs HFA register-class mismatch. Verified by round-trip tests. `Vector3` is also used correctly throughout (e.g., `OrbitEntityAction.OrbitalAxis`, `OrbitAnimation` `axis` param, `PhysicsBody` force vectors).

**`ModelComponent.Materials` getter broken** (RealityFoundation.cs:79286):
```csharp
get => throw new NotSupportedException("Protocol proxy not available: EveryProtocol conformance was not emitted.");
```
The setter works; the getter always throws. Any code that reads `modelComponent.Materials` crashes at runtime with no compile-time warning. The Swift type is `[any Material]` — the getter needs `Material.MaterialProxy` which wasn't emitted (see Coverage §1). This is a **silent runtime failure** — the member is publicly visible and signature-valid, but throws.

**`ComponentSet.Has(object)` and `ComponentSet.Remove(object)` are Obsolete** (RealityFoundation.cs:~113700):
Both carry `[Obsolete("No @_cdecl wrapper or native thunk available...", SB0001)]`. These work via direct `CallConvSwift` PInvoke — usable but at caller's risk. The `Set<T>` is available and clean.

**`ComponentSet` no typed `Get<T>`**: The entire component-read pattern requires the `IComponent`-typed subscript (`this[ComponentSet.Index]`) which returns an untyped `IComponent`. Consumers must cast manually with no type safety. The Swift `entity.components[ModelComponent.self]` pattern has no C# equivalent.

**`AddChild(entity, preservingWorldTransform:)` — required parameter** (RealityFoundation.cs:120297): Swift default is `false`; C# requires the argument. Callers always write `AddChild(child, false)` or `AddChild(child, preservingWorldTransform: false)`. Minor ergonomics gap — no overload provided.

**Async / MainActor**: Well-handled. `@MainActor` properties and methods carry `[SwiftMainActor]` and the `MainActorGuard.AssertMainThread()` runtime check. Async methods (e.g., `TextureResource.CreateAsync`, `MeshResource.GenerateAsync`) surface correctly as `Task<T>`-returning C# methods with `CancellationToken`. No blocking-only fallbacks observed for the `async` APIs.

**Nullability**: Consistent. Optional class returns use `T?` (`Scene?`, `Entity?`), optional value types use `SwiftOptional<T>`. No missing nullability annotations observed on the ECS core surface.

**Lifetime / `IDisposable`**: Value-type structs (`Transform`, `ModelComponent`, `ComponentSet`) implement `IDisposable` with `using`-block semantics; class types (`Entity`, `Scene`, `MeshResource`) are reference-counted via `_handle`. Pattern is consistent. No obvious ownership leaks on the emitted surface.

---

## 3. Test Coverage

**Files**: `tests/Tests.cs` (454 lines), `tests/Program.UIKit.cs` (38 lines — UIKit harness bootstrap)
**Test case count**: 59 distinct Pass/Fail/Skip calls

### Depth assessment: **Strong for ECS core, weak for materials and component I/O**

| Test area | Depth | Verdict |
|---|---|---|
| Type metadata smokes (Entity, AnchorEntity, Scene, Transform, MeshResource, ModelComponent, etc.) | Weak (metadata only) | Necessary baseline; not ABI proof |
| `MeshBuffer<Vector3>`, `MeshBuffers.Semantic<T>` generic metadata | Weak | Pins SIMD generic metadata regression from T2.1 |
| `MeshBuffers.Positions/Normals/Tangents` static read | Medium | Proves symbol resolution |
| `Transform.Identity` / `Translation/Scale/Rotation/Matrix` round-trips | **Strong** | All-lane verification; NEON/HFA regression gate |
| `Transform(Matrix4x4)` ctor / `Transform(scale,rotation,translation)` ctor | **Strong** | Multi-SIMD ctor paths verified |
| `Entity()` ctor, `Name` round-trip, `IsEnabled` round-trip, `Id` read | **Strong** | Core entity lifecycle |
| Hierarchy: `AddChild/RemoveChild/RemoveFromParent`, `ChildCollection` count, `FindEntity` | **Strong** | Real ECS hierarchy proven |
| `Entity.ObservableValue.Transform` read + write-with-preflight | **Strong** | willSet-trap guardrail documented and tested |
| `Entity.Components.Count` | Weak | Read-only count; no typed component set/get exercised |
| `Entity.Clone(recursive: false)` | Medium | Name preserved; deep recursion not checked |
| `Entity.Scene` null when detached | Medium | Nil-optional marshal |
| `AnchorEntity()` ctor, `IHasAnchoring` boxing | **Strong** | Existential conformance descriptor recovery tested |
| `MeshResource.GenerateBox/Plane/Sphere` | Medium (skip if no renderer) | Binding shape proven; renderer not tested |

### Significant untested surface

1. **`ComponentSet.Set<T>` + typed retrieval** — No test writes a concrete component (e.g., `ModelComponent`) to `entity.Components.Set<ModelComponent>(mc)` and verifies it was stored. This is the primary ECS composition path.

2. **`ModelComponent.Materials` getter throws** — No test exercises this property. A test asserting `NotSupportedException` would pin the known bug and catch a regression when it's fixed.

3. **`ModelEntity` creation and `ModelComponent` assignment** — `ModelEntity` has `IHasModel` but no test creates one, assigns a `ModelComponent`, or round-trips `mesh` + materials. The `new ModelEntity()` ctor path is untested.

4. **`SimpleMaterial` / `PhysicallyBasedMaterial` / `UnlitMaterial` creation** — All three are emitted and implement `IMaterial` but are entirely untested. Setting and (when fixed) reading a material via `ModelComponent` would catch the Materials-getter bug.

5. **Physics components** — `CollisionComponent`, `PhysicsBodyComponent`, `PhysicsMotionComponent` are all emitted but not exercised. A test constructing `PhysicsBodyComponent` from a shape would cover physics initialization.

6. **`AnimationResource`** — `AnimationResource` (async load + play) is emitted but not tested. A synchronous `AnimationLibraryComponent` smoke would be a start.

### Recommended tests to add

| Test | Layer | Value |
|---|---|---|
| `entity.Components.Set<ModelComponent>(new ModelComponent(...)); _ = entity.Components.Count` | BindingTests | Covers `ComponentSet.Set<T>`, component lifecycle |
| Assert `modelComponent.Materials` throws `NotSupportedException` | BindingTests | Pins known gap; turns into a green regression check when fixed |
| `var me = new ModelEntity(); me.AddChild(new Entity(), false);` basic lifecycle | BindingTests | `ModelEntity` ctor + downcast |
| `var m = new SimpleMaterial(); var mc = new ModelComponent(mesh, [m]); mc.Materials` | BindingTests | Materials write-path smoke (getter will throw until proxy fixed) |
| `var pc = new PhysicsBodyComponent(ShapeResource.GenerateSphere(radius: 0.1f), 1.0f, PhysicsBodyMode.Dynamic)` | BindingTests | Physics ctor |

---

## Action Items

| # | Dimension | Finding | Recommendation | Effort | Value |
|---|---|---|---|---|---|
| 1 | Coverage | `ModelComponent.Materials` getter throws `NotSupportedException` — root cause is `Material.MaterialProxy` (EveryProtocol higher-kind constraint not emitted) | Fix the EveryProtocol higher-kind constraint path so `Material.MaterialProxy` emits; unblocks the getter | High | High |
| 2 | Coverage | `ComponentSet` has `Set<T>` but no `Get<T>` — the `subscript<T: Component>(T.Type) -> T?` metatype-subscript pattern not projected | Synthesize a `Get<T>()` method on `ComponentSet` mirroring the `Set<T>` synthesis path | Medium | High |
| 3 | Coverage | `Scene.subscribe` (2 overloads) blocked by UnsupportedClosure — generic-over-protocol event closure | Support generic-over-protocol closure marshalling for event subscription | High | High |
| 4 | Quality | `ModelComponent.Materials` getter is publicly visible but always throws — no compile-time warning | Add `[Obsolete(..., IsError = false)]` annotation to the getter until proxy is fixed; or suppress emission and emit a comment | Low | Medium |
| 5 | Coverage | `AddChild/RemoveChild/RemoveFromParent` require `preservingWorldTransform` with no default | Emit a `= false` default or a convenience overload | Low | Low |
| 6 | Tests | No test covers `ComponentSet.Set<T>`, `ModelEntity`, material assignment, physics construction | Add the 5 tests listed in §3 | Low | High |
| 7 | Tests | `ModelComponent.Materials` getter bug has no test to pin it | Add a test asserting `NotSupportedException` (becomes green when #1 fixed) | Low | Medium |
