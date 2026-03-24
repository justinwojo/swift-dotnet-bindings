# Library Parity Roadmap: Nuke & Lottie

**Created**: March 22, 2026
**Goal**: Bring generated bindings for Nuke and Lottie to ~100% native-equivalent functionality for .NET iOS / .NET MAUI consumers
**Context**: Pre-0.3.0 release audit — both libraries compile and pass runtime tests, but gaps remain vs. native Swift API surface

---

## Current State (March 22, 2026)

### Nuke (v12.8.0)

| Metric | Value |
|--------|-------|
| Runtime tests | 49 passed, 0 failed, 8 skipped |
| Estimated native parity | ~75% |
| Generated C# binding | 21,409 lines |
| Swift wrapper | ~21 KB |
| Blocking gaps | 2 (custom headers, processors on requests) |

### Lottie (v4.6.0)

| Metric | Value |
|--------|-------|
| Runtime tests | 64 passed, 0 failed, 4 skipped |
| Estimated native parity | ~90% |
| Generated C# binding | 31,451 lines |
| Swift wrapper | ~6,024 lines |
| Blocking gaps | 1 (SetValueProvider interface gap) |

---

## Nuke: Remaining Work

### N1. ImageRequest URLRequest constructor — custom HTTP headers ✅ `8216f793`

**Priority**: P1 — **Blocks real-world adoption**
**Effort**: Medium (generator + runtime)
**Generator issue**: Yes — requires generating `ImageRequest(urlRequest:)` initializer
**Status**: Fixed — added SetValue/AddValue/Value HTTP header methods to URLRequest runtime type

Native Swift:
```swift
var urlRequest = URLRequest(url: imageURL)
urlRequest.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
let request = ImageRequest(urlRequest: urlRequest)
let image = try await pipeline.image(for: request)
```

Current C# (only constructor):
```csharp
var request = new ImageRequest("https://example.com/image.jpg");
// No way to add headers
```

**What's needed**:
- `URLRequest` type must be marshalable (it wraps `NSMutableURLRequest` under the hood)
- Generate `ImageRequest(URLRequest)` constructor
- Alternatively: generate a factory or builder that accepts URL + headers

**Why it matters**: Any app loading images from authenticated CDNs, APIs with keys, or corporate proxies needs custom headers. This is the single biggest gap for Nuke real-world usage.

### N2. ImageRequest.Processors property ✅ `ed3dfd5a`

**Priority**: P2
**Effort**: Medium (array-of-protocol marshalling)
**Generator issue**: Yes — `[any ImageProcessing]` array property not generated
**Status**: API surface preserved — proxy co-gater emits `NotSupportedException` throw body instead of stripping members. Full processor marshalling (`[any ImageProcessing]` array) requires EveryProtocol conformance emission, which is a separate generator capability.

Native Swift:
```swift
let request = ImageRequest(
    url: imageURL,
    processors: [.resize(width: 300), .roundedCorners(radius: 16)]
)
```

Current state: `ImageProcessors.Resize`, `.Circle`, `.RoundedCorners`, `.GaussianBlur`, `.CoreImageFilter`, and `.Anonymous` are all generated as types — but `ImageRequest` has no `Processors` property to attach them.

**What's needed**:
- Generate `Processors` get/set property on `ImageRequest`
- Requires marshalling `[any ImageProcessing]` (array of existential protocol type)
- May also need `ImageRequest` constructor overloads that accept processors

**Why it matters**: Image processing is a core Nuke feature. Without it, the 6 generated processor types are dead code.

### N3. NSUrl non-blittable workaround (4 affected APIs) ✅ (already resolved)

**Priority**: P2
**Effort**: Medium (generator/runtime)
**Generator issue**: No — URL-parameter methods already generated via NativeRemappedProjection

Affected methods:
- `ImagePipeline.ImageTask(URL)` — use `ImageTask(ImageRequest)` instead
- `ImagePipeline.ImageAsync(URL)` — use `ImageAsync(ImageRequest)` instead
- `ImagePipeline.DataAsync(URL)` — use `DataAsync(ImageRequest)` instead
- `DataCache.Path` property — no workaround

**Current workaround**: Construct `ImageRequest` from string URL, then pass to non-URL overloads. Works for all methods except `DataCache.Path`.

**What's needed**: Either make `Foundation.URL` blittable in the runtime, or generate `@_cdecl` wrapper overloads that accept string URLs and convert internally.

### N4. DataAsync(ImageRequest) crash — NSUrlResponse marshalling ✅ `8216f793`

**Priority**: P2
**Effort**: Small–Medium (runtime bug)
**Generator issue**: No — runtime marshalling bug
**Status**: Fixed — ObjC-bridged Optional tuple returns now emit nullable reference type instead of SwiftOptional&lt;T&gt;

`pipeline.DataAsync(request)` crashes when the async callback tries to marshal `NSUrlResponse`. `ImageAsync(request)` works fine because it returns `UIImage` directly.

**What's needed**: Debug the `NSUrlResponse` marshalling in the async completion callback. Likely a type mismatch or missing null check in the generated wrapper.

### N5. Prefetcher/ImageTask Priority property — @_cdecl wrapper needed ✅ (already working)

**Priority**: P3
**Effort**: Small (generator)
**Generator issue**: No — already had @_cdecl wrappers via `IsParamTypeCdeclRequired` code path

`ImagePrefetcher.Priority` and `ImageTask.Priority` (set) crash on Mono JIT because they use `CallConvSwift` without an `@_cdecl` wrapper.

**Current workaround**: Set priority on `ImageRequest` before creating the task/prefetcher.

**What's needed**: Generate `@_cdecl` wrappers for these property accessors, similar to how other properties are wrapped.

### N6. ImagePipeline.Cache query methods ✅ (verified + tested)

**Priority**: P3
**Effort**: Small–Medium
**Status**: Verified — all cache CRUD methods (CachedItem, StoreItem, RemoveItem, ContainsItem, MakeKey) are generated with @_cdecl wrappers and pass runtime tests. GetItemCount and RemoveAll use CallConvSwift (no @_cdecl wrapper — works on NativeAOT, skipped on Mono simulator).

Native Swift `ImagePipeline.Cache` supports:
```swift
pipeline.cache.cachedImage(for: request)
pipeline.cache.storeCachedImage(container, for: request)
pipeline.cache.removeCachedImage(for: request)
pipeline.cache.containsCachedImage(for: request)
pipeline.cache[request] = container
```

Synthetic pattern tests model this via `DataPipeline.Cache` in SwiftBindingsTestLib (CachePatternTests.cs, 13 tests).

### N7. ImageDecoders.Empty constructor ✅ (already working)

**Priority**: P4
**Effort**: Trivial (generator)

Constructor already emitted with @_cdecl wrapper. No change needed.

### N8. NukeUI module (SwiftUI: LazyImage, FetchImage)

**Priority**: Deferred — depends on SwiftUI bridge infrastructure
**Effort**: Large

`LazyImage` is Nuke's SwiftUI entry point. Not generated because it's a separate module (`NukeUI`) and depends on SwiftUI view composition. Tracked separately in `swiftui-roadmap.md`.

### N9. NukeExtensions module (UIImageView convenience) — Deferred

**Priority**: P4 → Deferred
**Effort**: Small–Medium
**Status**: Deferred — requires multi-module generation support. NukeExtensions is a separate Swift module that extends UIImageView. The generator currently only processes one module at a time. Generating from the separate module would require `--xcframework` to point at NukeExtensions.xcframework with Nuke.xcframework as a `--framework-dependency`. The manual equivalent is trivial:

```csharp
// Manual equivalent of NukeExtensions
var image = await ImagePipeline.Shared.ImageAsync(new ImageRequest(url));
imageView.Image = image;
```

---

## Lottie: Remaining Work

### L1. SetValueProvider compile-time interface gap ✅ `0a5e814e`

**Priority**: P1 — **Core Lottie feature partially broken**
**Effort**: Medium (generator investigation)
**Generator issue**: Yes — protocol conformance not surfaced in C# type hierarchy
**Status**: Fixed — phantom defaults detection identifies invisible PAT extension members and emits them as DIMs

Native Swift:
```swift
let colorProvider = ColorValueProvider(UIColor.red.lottieColorValue)
animationView.setValueProvider(colorProvider, keypath: AnimationKeypath(keypath: "**.Fill 1.Color"))
```

Current C#: `SetValueProvider(IAnyValueProvider provider, AnimationKeypath keypath)` is generated, but `FloatValueProvider`, `SizeValueProvider`, `PointValueProvider`, and `ColorValueProvider` don't implement `IAnyValueProvider` at compile time — even though they conform to `AnyValueProvider` in Swift.

**What's needed**:
- Investigate why protocol conformance for `AnyValueProvider` isn't being projected onto the concrete C# types
- Either: (a) make generated types implement the interface, (b) generate implicit conversion operators, or (c) provide a bridge wrapper
- This is the highest-impact gap for Lottie — dynamic property modification is the library's killer feature

### L2. LottieColor / ColorValueProvider — CallConvSwift only ✅ `cded13fa`

**Priority**: P2
**Effort**: Small–Medium (generator — @_cdecl wrappers)
**Generator issue**: Yes — missing `@_cdecl` wrappers for `LottieColor` struct operations
**Status**: Fixed — expanded `RequiresCdeclForAbiSafety()` to all struct constructors (not just frozen)

`LottieColor(r:g:b:a:)` constructor and `.r`, `.g`, `.b`, `.a` property accessors use `CallConvSwift` without `@_cdecl` wrappers. Works on NativeAOT but crashes on Mono JIT (simulator).

**What's needed**: Generate `@_cdecl` wrapper functions for `LottieColor` init and property accessors. This unblocks `ColorValueProvider` on simulator, which is the most common value provider use case (animating colors).

### L3. GradientValueProvider ✅ `0a5e814e`

**Priority**: P3
**Effort**: Small–Medium

**Status**: Runtime tests added as part of ValueProviderPattern test suite (16 tests covering gradient/color/float/size providers, SetValueProvider dispatch, existential free functions).

### L4. `.repeat(Float)` / `.repeatBackwards(Float)` loop modes ✅ (already working)

**Priority**: P3
**Effort**: N/A
**Generator issue**: No — already generated as `LottieLoopMode.Repeat(float)` and `LottieLoopMode.RepeatBackwards(float)` factory methods with `TryGetRepeat`/`TryGetRepeatBackwards` extractors

Native Swift:
```swift
animationView.loopMode = .repeat(3)      // Play exactly 3 times
animationView.loopMode = .repeatBackwards(2)  // Play + reverse 2 times
```

Current C#: Only `.PlayOnce`, `.Loop`, `.AutoReverse` are available (parameterless cases). The `repeat(Float)` and `repeatBackwards(Float)` cases have associated values which the generator doesn't yet project.

**What's needed**: Generator support for enum cases with associated values. This is a broader generator capability, not Lottie-specific. Tracked in the main roadmap.

### L5. DotLottie format testing ✅ (pattern verified + tested)

**Priority**: P3
**Effort**: Small (test-only)
**Status**: Verified — async file/data loading factory methods and cache patterns are tested via synthetic `AnimationBundle`/`AnimationAsset`/`AnimationCacheStore` types in SwiftBindingsTestLib. The actual DotLottieFile generated bindings have matching API shapes (LoadedFromAsync overloads, DotLottieCache). AsyncFactoryMethodTests.cs covers 13 tests including file/data/URL loading and cache store/retrieve/clear.

### L6. AnimatedButton / AnimatedSwitch UIKit controls ✅ (pattern verified + tested)

**Priority**: P4
**Effort**: Small (test + possible fixes)
**Status**: Verified — class hierarchy (AnimatedControlBase → ToggleSwitch/TapButton), IsOn bool property, SetIsOn with animated parameter, play range configuration, and inherited Play/Stop all work. SetIsOn and PerformTap use CallConvSwift (works on NativeAOT, skipped on simulator). ControlHierarchyTests.cs covers 17 tests.

### L7. URL-based animation loading ✅ (pattern verified + tested)

**Priority**: P3
**Effort**: Small (test + verify)
**Status**: Verified — async URL-based loading factory method tested via synthetic `AnimationAsset.LoadFromUrlAsync`. Tests cover valid URLs, empty URLs, and non-HTTP URL rejection. Included in AsyncFactoryMethodTests.cs.

### L8. Animation hierarchy inspection ✅ (pattern verified + tested)

**Priority**: P4
**Effort**: Small (test-only)
**Status**: Verified — hierarchy inspection methods tested via synthetic `LayerContainer` with `LayerNode` types. ConvertPoint, ConvertRect (optional CGPoint/CGRect returns), SetNodeEnabled, IsNodeEnabled, GetValueAtFrame, and LogKeypaths all use @_cdecl wrappers and work on simulator. GetAllKeypaths and GetNodeCount use CallConvSwift (array/Int32 returns without wrappers — skipped on simulator). Note: getValue/getOriginalValue not present in Lottie v4.6.0 bindings. HierarchyInspectionTests.cs covers 12 tests.

### L9. SwiftUI LottieView ✅ (already bridged)

**Priority**: Complete
**Effort**: N/A

All 3 Lottie SwiftUI views (LottieView, LottieButton, LottieSwitch) are bridged via UIHostingController wrappers with 6 view modifiers on LottieView. 15/15 runtime tests passing. See `swiftui-roadmap.md`.

---

## Priority Summary

### Must-have for native parity (P1)

| Item | Library | Effort | Type |
|------|---------|--------|------|
| N1. URLRequest constructor (custom headers) | Nuke | Medium | Generator |
| L1. SetValueProvider interface gap | Lottie | Medium | Generator |

### Should-have (P2)

| Item | Library | Effort | Type |
|------|---------|--------|------|
| N2. ImageRequest.Processors property | Nuke | Medium | Generator |
| N3. NSUrl non-blittable | Nuke | Medium | Generator/Runtime |
| N4. DataAsync crash | Nuke | Small–Medium | Runtime bug |
| L2. LottieColor @_cdecl wrappers | Lottie | Small–Medium | Generator |

### Nice-to-have (P3)

| Item | Library | Effort | Type |
|------|---------|--------|------|
| N5. Prefetcher/Task Priority wrappers | Nuke | Small | Generator |
| N6. Pipeline.Cache methods verification | Nuke | Small–Medium | Test |
| L3. GradientValueProvider testing | Lottie | Small–Medium | Test |
| L4. `.repeat(N)` loop modes | Lottie | Medium | Generator |
| L5. DotLottie format testing | Lottie | Small | Test |
| L7. URL-based animation loading | Lottie | Small | Test |

### Low priority (P4)

| Item | Library | Effort | Type |
|------|---------|--------|------|
| N7. ImageDecoders.Empty constructor | Nuke | Trivial | Generator |
| N9. NukeExtensions module | Nuke | Small–Medium | Generator |
| L6. AnimatedButton/AnimatedSwitch | Lottie | Small | Test |
| L8. Hierarchy inspection methods | Lottie | Small | Test |

### Deferred (SwiftUI dependency)

| Item | Library | Effort |
|------|---------|--------|
| N8. NukeUI (LazyImage, FetchImage) | Nuke | Large |
| L9. SwiftUI LottieView | Lottie | Large |

---

## What "Native Equivalent" Means

A .NET iOS developer consuming `SwiftBindings.Nuke` or `SwiftBindings.Lottie` should be able to:

1. **Follow the library's official documentation** and translate Swift examples to C# with only naming convention changes (camelCase → PascalCase)
2. **Access all major features** listed on the library's README / getting started guide
3. **Not hit dead ends** where a documented capability simply doesn't exist in the binding
4. **Build production apps** that use these libraries comparably to native Swift apps

### Nuke: what "done" looks like
- Load images from any URL, with custom headers and auth tokens (N1)
- Apply image processors (resize, rounded corners, blur) to requests (N2)
- Manage memory and disk caches with full control
- Prefetch images for smooth scrolling
- Cancel in-flight requests
- Track progress on active loads

### Lottie: what "done" looks like
- Load animations from files, bundles, assets, data, and URLs
- Full playback control (play, pause, stop, loop, speed, scrub)
- Dynamically modify animation properties at runtime via value providers (L1, L2)
- Cache animations
- Use all loop modes including repeat counts (L4)
- Load `.lottie` bundle format (L5)

---

## Cross-Cutting Generator Issues

Several items above trace back to the same underlying generator capabilities:

| Generator capability | Blocks |
|---------------------|--------|
| `@_cdecl` wrapper generation for struct properties | L2, N5 |
| Protocol conformance projection onto concrete types | L1 |
| Array-of-protocol marshalling (`[any Protocol]`) | N2 |
| `Foundation.URL` / `NSUrl` blittability | N3 |
| Enum cases with associated values | L4 |
| `URLRequest` struct marshalling | N1 |

Fixing these at the generator level will improve not just Nuke and Lottie but all future library bindings.

---

## Out of Scope

| Item | Reason |
|------|--------|
| NukeVideo module | Video playback is niche; no demand signal |
| Lottie ObjC compatibility layer | `Compatible*` wrappers are for ObjC consumers, not relevant to .NET |
| Combine publishers on ImagePipeline | .NET has no Combine equivalent; async/await covers the same use cases |
| ImagePipeline.Delegate (all 14 methods) | Advanced customization; most apps use defaults |
| Lottie `LottieLogger` customization | Logging infrastructure is internal tooling, not consumer-facing |
