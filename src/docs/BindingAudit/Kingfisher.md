# Kingfisher — Binding Audit

- **Package**: SwiftBindings.Kingfisher v1.0.0   **Mode**: source   **TFM(s)**: net10.0-ios
- **Native**: onevcat/Kingfisher 8.10.0
- **Audited at**: main 1e8c27a8, generated 2026-06-27T13:07Z

## Verdict

Coverage is strong at the type level (129/133, 97%) with a 14.3% member-skip rate. The headline gap is **GenericTypeCallback:22** — every overload of `KingfisherWrapper.setImage` and `setBackgroundImage` is dropped, killing the traditional `imageView.kf.setImage(with: url)` pattern entirely. However, the *equivalent* `KF.Url(url).Placeholder(img).Set(imageView)` builder API **is** emitted and provides functional download-to-view. Flows 2 (cache store/retrieve) and 3 (processors) are solid. The tests are comprehensive (230+ cases) but none exercise the actual download+cache path end-to-end — they validate scaffolding, not runtime behavior.

---

## 1. Coverage

### Type Coverage

| | Count | % |
|---|---|---|
| TotalTypes | 133 | — |
| EmittedTypes | 129 | 97.0% |
| SkippedTypes | 3 | 2.3% |

The 3 skipped types are all SwiftUI `View` conformers: `KFImage`, `KFAnimatedImage`, and `KFAnimatedImageViewRepresenter`. The bridge report shows all three as `BridgeStatus: TemplatePending` or `Skipped` — bridge template not yet authored. These are correctly excluded per project policy. The 5 additional `EveryProtocolConformanceSkipped` proxy types (`DataTransformableProxy`, `ImageDataProviderProxy`, `KFImageProtocolProxy`, `KFImageHoldingViewProxy`, `OptionalProtocolProxy`) are synthesized artifacts, not native Swift types, so they don't count against TotalTypes.

### Member Coverage

| | Count | Note |
|---|---|---|
| TotalMembers | 544 | Native Swift public API |
| SkippedMembers | 78 | 14.3% of native |
| SynthesizedMembers | 349 | Generator additions (async Task wrappers, discriminated-union helpers, factory ctors, protocol stubs) |
| EmittedMembers | 574 | Reported output count (exceeds TotalMembers due to synthesis) |

**Why EmittedMembers > TotalMembers**: The generator synthesizes 349 members not present in the native API — async `Task`-returning wrappers for every Swift `async` function, `TryGet*` helpers for every discriminated-union case, factory constructors, and EveryProtocol conformance stubs. These additions dominate the 78 native skips, so the net C# surface is larger than the Swift surface. SynthesizedMembers is a separate counter (not additive to EmittedMembers in a simple way); the key native-coverage signal is that **466 of 544 native members (85.7%) are emitted in some form**.

### Skip Reason Breakdown

| Reason | Count | Classification |
|---|---|---|
| GenericTypeCallback | 22 | **(b) real gap** — critical |
| UnsupportedClosure | 12 | **(b) real gap** — moderate |
| UnsatisfiedGenericConstraint | 10 | **(b) real gap** — moderate |
| UnsupportedSignature | 9 | mixed (see below) |
| DuplicateSignature | 7 | **(b) real gap** — low-moderate |
| AnyTypeFallback | 5 | **(b) real gap** — low |
| EveryProtocolConformanceSkipped | 5 | **(a) correctly excluded** — EveryProtocol limitation |
| UnsupportedType | 5 | mixed (see below) |
| SwiftUIView | 3 | **(a) correctly excluded** — deliberate project decision |
| MissingWrapperSymbol | 2 | **(b) real gap** — low |
| StaticProtocolMember | 1 | **(a) correctly excluded** — C# interface limitation |

---

#### (b) GenericTypeCallback — 22 skips — **Critical**

All 19 overloads of `KingfisherWrapper.setImage(with:placeholder:options:progressBlock:completionHandler:)` and 2 overloads of `setBackgroundImage` are dropped. The drop reason: `Member requires [UnmanagedCallersOnly] callback inside generic type` — the CLR cannot emit a `[UnmanagedCallersOnly]` callback inside a generic type (`KingfisherWrapper<TBase>`). This is the traditional Kingfisher entrypoint:

```swift
imageView.kf.setImage(with: URL(string: "https://…"))
```

The C# equivalent `imageView.Kf.SetImage(...)` does not exist because all `setImage` overloads are gone.

**What still works**: `KF.Url(url).Placeholder(img).Set(imageView)` at `Kingfisher.cs:15607` / `17199` — Kingfisher's own fluent builder is fully emitted. It calls the same underlying cache-then-download pipeline. So the *behavior* is available, just through a different API shape.

Also dropped: `Delegate.callAsync` (ref generic in return type, line — async callback bridging gap).

**Generator fix**: Emit specialized closed-generic `@_cdecl` wrappers for known concrete `TBase` instantiations (at minimum `UIImageView`, `UIButton`) and expose them as extension methods on those types. Tractability: medium-hard (needs specialization point in ConstrainedExtensionEmitter).

---

#### (b) UnsupportedClosure — 12 skips — **Moderate**

| Skipped | Impact |
|---|---|
| `KF.Builder.set(attributedView:)` | Builder chain's terminal method that takes a closure for view setup — can't call |
| `KF.RedirectPayload.completionHandler` | Redirect handling payload is an opaque blob; redirect response customization gone |
| `RetrieveImageResult.data` | Lazy `() -> Data` accessor on the download result — can't get raw bytes from result |
| `Filter.tint`, `Filter.colorControl` | Filter sub-properties that hold `(CIImage) -> CIImage` closures — can't inspect built-in filters |
| `Filter.init(transform:)` | Can't create a custom `Filter` from a CIImage transformation closure |
| `AnyImageModifier.init(modify:)` | Can't create custom image modifier |
| `AnyRedirectHandler.init(handle:)` | Can't create custom redirect handler |
| `AnyModifier.init(modify:)` | Can't create custom modifier |
| `Delegate._delegate(block:)` × 2 | Internal delegate wiring — low direct impact |

The `RetrieveImageResult.data` gap (`Kingfisher.cs:23717`) is called out in the generated comment: `// Unsupported: property 'RetrieveImageResult.data'`. A consumer who downloads an image and wants to access the raw bytes cannot do so from the `RetrieveImageResult` return value.

**Generator fix**: The init closures (`Filter`, `AnyImageModifier`, `AnyModifier`, `AnyRedirectHandler`) all follow the same pattern — `(T) -> T` or `(T) -> T?` with a single argument of a known type. These are high-value targets for a "simple closure marshal" that wraps an `Action<T>` or `Func<T,T>`. Medium effort.

---

#### (b) UnsatisfiedGenericConstraint — 10 skips — **Moderate**

| Skipped | Impact |
|---|---|
| `ImageCache.memoryStorage` | Can't inspect or configure the in-memory storage directly |
| `ImageCache.diskStorage` | Can't inspect or configure the on-disk storage directly |
| `ImageCache.init(memoryStorage:diskStorage:)` | Can't construct `ImageCache` with custom storage backends |
| `KF.Builder.onFailureDelegate`, `.onSuccessDelegate`, `.onProgressDelegate` | Builder delegate hooks (typed `Delegate<...>`) gone |
| `KFOptionSetter.onFailureDelegate`, `.onSuccessDelegate`, `.onProgressDelegate` | Option-setter delegate hooks gone |
| `ImageProgressive.onImageUpdated` | Progressive loading callback property gone |

`ImageCache.memoryStorage` and `diskStorage` hold `MemoryStorage.Backend<UIImage>` and `DiskStorage.Backend<Data>` respectively. The constraint failure is that `Foundation.Data` does not satisfy the C# `ISwiftObject` bound required by the generator's generic type instantiation path. This prevents advanced cache introspection and custom eviction policy.

**Generator fix**: Allow binding generic types where all type arguments satisfy the *Swift* constraints but not the C# `ISwiftObject` constraint, using opaque-handle semantics for the non-ISwiftObject arguments. Medium effort.

---

#### (b) UnsupportedSignature — 9 skips — Mixed

- `PHPickerResultImageDataProvider.init` and `PhotosPickerItemImageDataProvider.init` — types `PHPickerResult` and `PhotosPickerItem` are from `PhotosUI` / SwiftUI, not in TypeDatabase. **Real gap** — can't construct photo-picker-sourced providers.
- `KF.Builder.set(...)` with `attibutedView:` — placeholder type from Swift compiler. **Real gap** (different overload from UnsupportedClosure entry above).
- `KingfisherWrapper.contains`, `.resize`, `.constrained`, `.filling`, `.constrainedRect` — constrained-extension methods on `KingfisherWrapper<UIImage>`. These are UIImage-specific helpers. **Real gap** for image manipulation on the KingfisherWrapper level, but workaroundable via `ResizingImageProcessor`.
- `GIFAnimatedImage.getFrameDuration` — placeholder type. Low impact.

---

#### (b) DuplicateSignature — 7 skips — **Low-Moderate**

`KingfisherWrapper.cancelDownloadTask` has 3 overloads dropped (signature collision after type erasure). The one remaining overload (line 18057) is platform-gated (`iOS 14.0+`). Task cancellation from the `KingfisherWrapper` level is impaired.

`Delegate.call` and `callAsFunction` (2× each) drop due to signature collision after generic erasure in the EveryProtocol proxy. Low consumer impact (the `Delegate` type is internal plumbing).

---

#### (b) AnyTypeFallback — 5 skips — **Low**

- `CacheStoreResult.memoryCacheResult` — returns `SwiftResult<SwiftVoid, AnyType>`, so the success/failure distinction requires an unsafe cast. The disk cache result (`diskCacheResult`) is properly typed.
- `KingfisherWrapper.imageSource` — typed as `SwiftOptional<AnyType>`, opaque.
- `RetryContext.userInfo` — existential inner protocol not in TypeDatabase, falls back to `object`. Low impact.
- `KFImageProtocol.context` and `KFImageHoldingView.created` — AnyType in generic argument; not consumer-facing.

---

#### (a) Correctly Excluded

- **SwiftUI** (`KFImage`, `KFAnimatedImage`, `KFAnimatedImageViewRepresenter`): three `View` conformers. `KFImage` and `KFAnimatedImage` have `BridgeStatus: TemplatePending` — bridge templates are not yet authored. Not a coverage gap by project policy.
- **EveryProtocolConformanceSkipped** (5 proxy types): `DataTransformable`, `ImageDataProvider`, `KFImageProtocol`, `KFImageHoldingView`, `OptionalProtocol` proxies. The first has `StaticMethodRequirements` which the EveryProtocol mechanism can't yet satisfy; the others have `no decision recorded`. Legitimate limitation, not a user-facing gap for most consumers.
- **StaticProtocolMember** (`KFImageProtocol.init`): C# interfaces cannot declare constructors. Correct.

---

### Prioritized Generator Unlocks

| Priority | Fix | Benefit |
|---|---|---|
| 1 | **GenericTypeCallback** — emit closed-generic `@_cdecl` wrappers for `KingfisherWrapper<UIImageView>` and `<UIButton>` | Restores `imageView.kf.setImage(with: url)` — the most common Kingfisher pattern |
| 2 | **Simple-closure inits** (`Filter`, `AnyImageModifier`, `AnyModifier`, `AnyRedirectHandler`) — marshal `(T) -> T` closures | Enables custom processors and redirect handlers |
| 3 | **UnsatisfiedGenericConstraint on non-ISwiftObject type args** — opaque-handle path for `MemoryStorage.Backend<UIImage>`, `DiskStorage.Backend<Data>` | Restores `ImageCache.memoryStorage`/`diskStorage` for advanced cache control |
| 4 | **ConstrainedExtension on `KingfisherWrapper<UIImage>`** (`resize`, `constrained`, etc.) | Minor — image manipulation helpers on wrapper level |

---

## 2. C# Quality

### Naming & Shape

PascalCase throughout. No mangled Swift names visible to consumers. The `KF` static partial class (`Kingfisher.cs:15113`) maps cleanly to Kingfisher's `KF` namespace. `KingfisherWrapper<TBase>` is a generic class (`Kingfisher.cs:17326`). Discriminated unions emit as structs with `Tag`/`TryGet*` pattern — readable.

`KingfisherOptionsInfoItem` is correctly an item in a list (`IReadOnlyList<KingfisherOptionsInfoItem>`) since the underlying Swift `KingfisherOptionsInfo` is a typealias for an array.

### Async

Swift `async` functions surface as `Task`-returning C# methods across the board. Notable:

- `KingfisherManager.RetrieveImageAsync(IResource, ...)` → `Task<RetrieveImageResult>` (`Kingfisher.cs:25349`) ✓
- `ImageCache.StoreAsync(UIImage, byte[]?, string, ...)` → `Task` (`Kingfisher.cs:6104`) ✓
- `ImageCache.RetrieveImageAsync(string, ...)` → `Task<ImageCacheResult>` (`Kingfisher.cs:7429`) ✓
- `ImageCache.ImageCachedTypeAsync(string, ...)` → `Task<CacheType>` (`Kingfisher.cs:5044`) ✓
- `ImageCache.StoreToDiskAsync(byte[], string, ...)` → `Task` (`Kingfisher.cs:6841`) ✓

Callback-style fallbacks (`RetrieveImage(key:, completionHandler:)`) are also present for cases where the async version is unavailable.

**One rough edge**: `RetrieveImageInDiskCache` via the callback overload (`Kingfisher.cs:4373`) types the result as `Action<SwiftResult<SwiftOptional<IntPtr>, KingfisherError>>` — the image is an `IntPtr`, not typed `UIImage?`. The `Task`-based async version (`Kingfisher.cs:7616`) corrects this to `Task<UIKit.UIImage?>`. Consumers should prefer the `Async` overload.

### Nullability

Optionals → `?` throughout. `ImageCache.Default` returns non-null. `KingfisherManager.Shared` returns non-null (class). Processor properties with optional fields (`RoundCornerImageProcessor.TargetSize`, `.BackgroundColor`) surface as `Swift.CGSize?` and `UIColor?` correctly.

### Lifetime / IDisposable

- Reference types (`KingfisherManager`, `ImageCache`, `ImageDownloader`, `AnimatedImageView`) implement `IDisposable`. ✓
- Value-type structs (`RetrieveImageResult`, `StorageExpiration`, `Radius`, `ImageProcessItem`, `KF.ImageResource`, etc.) implement `IDisposable` via `SwiftSafeHandle`. ✓
- Singleton accessors (`KingfisherManager.Shared`, `ImageCache.Default`, `ImageDownloader.Default`) return reference types — consuming code should not `Dispose` these.

No obvious lifetime smells in the generated surface.

### Ergonomic Issues

1. **No `.Kf` extension property on `UIImageView` in C#** — the Swift `.kf` accessor that returns `KingfisherWrapper<UIImageView>` is not emitted as a C# extension. Consumers must use the `KF.Url(...).Set(imageView)` builder pattern instead. This is a direct consequence of GenericTypeCallback removing all `setImage` overloads (no point in surfacing the accessor when it has no callable methods), but it is surprising to developers familiar with Kingfisher.

2. **`KF.Builder.Set(UIImageView)` is the correct entry point** — `Kingfisher.cs:15607`. The builder chain `KF.Url(url).Placeholder(img).Set(imageView)` works end-to-end through the Swift side, which calls the native `kf.setImage` path internally.

3. **`RetrieveImageResult.data` is gone** (`Kingfisher.cs:23717` comment). There is no way to get raw bytes from a download result. Workaround: use `ImageDownloader.DownloadImageAsync(url)` → `ImageLoadingResult.OriginalData` if the raw data is needed.

4. **`CacheStoreResult.memoryCacheResult`** returns `SwiftResult<SwiftVoid, AnyType>` because the error type inside the result is opaque. The cast at runtime to a specific error type would need reflection. Low probability of consumer hitting this — typically only inspected for error recovery.

5. **`RetrieveImageInDiskCache` IntPtr return in callback overload** — see Async section above. Typo-prone; async overload is the right path.

---

## 3. Test Coverage

### Summary

**39 sections, ~230 distinct test cases** in a single `Program.cs` (3,622 lines). Tests run on iOS Simulator (Mono JIT).

| Surface Area | Test Depth | Verdict |
|---|---|---|
| Type metadata (all major types) | Weak (metadata size only) | Proves type registration, not ABI |
| Singletons (KingfisherManager.Shared, ImageCache.Default, ImageDownloader.Default) | Medium (access + non-null assert) | Proves singleton access |
| Core class constructors (ImageCache, ImageDownloader, KingfisherManager) | Strong (construct + dispose) | Proves ctor ABI |
| Discriminated unions (StorageExpiration, Radius, ImageTransition, ExpirationExtending, CallbackQueue, RepeatCountType, DelayRetryStrategy.Interval) | Strong (tag values, TryGet positive + negative, value round-trip) | Best-tested surface |
| Image processors (Blur, Resize, Round, Overlay, Tint, B&W, Crop, Downsampling, Default) | Strong (ctor, identifier, value props, Append) | Well-covered |
| ImageCache methods (IsCached, Clear, CleanExpired, CalculateDiskStorageSize w/ callback) | Medium (calls succeed, no data round-trip) | Proves method dispatch |
| Property setter round-trips (timeout, pipelining, AnimatedImageView props) | Strong (set + read-back verify) | Good |
| ImagePrefetcher (ctor, MaxConcurrentDownloads round-trip, Stop) | Medium | Fine for compile gate |
| KF.ImageResource (ctor, CacheKey, DownloadURL, custom cacheKey) | Strong | Good |
| ImageProcessItem (Image/Data factories, tag values, loop) | Strong | Good |

### Untested Surface (most important gaps)

1. **`KingfisherManager.RetrieveImageAsync(url)`** — the primary download+cache path is **never called**. Sections 2/6 access the manager but only read `.Cache` and `.Downloader`. No test exercises a real or mock download. High value: a test that downloads a small bundled image (or uses a local test server) and asserts `RetrieveImageResult.Image != null` would prove the async download ABI end-to-end.

2. **`ImageCache.StoreAsync` → `RetrieveImageAsync` round-trip** — `CalculateDiskStorageSize` (Section 5) fires a callback, but no test stores a `UIImage` and then retrieves it. A store-then-retrieve test would prove async `Task` completion and the `ImageCacheResult.CacheType` enum at runtime.

3. **`KF.Builder.Set(UIImageView)`** — the only viable download-to-view path is completely untested. Even a "URL → KF.Builder → Set → check task non-null" assertion would validate the builder chain.

4. **`IImageProcessor.Process()`** — all processor tests confirm construction and property values, but none calls `Append`'s result to process an actual `ImageProcessItem` and assert the output image is non-null.

5. **`ImageDownloader.DownloadImageAsync(url)`** (`Kingfisher.cs:41237`) — callable with a local/mock URL. The `ImageLoadingResult.OriginalData` path also works around the `RetrieveImageResult.data` gap.

6. **`KingfisherOptionsInfoItem` array construction** — `IReadOnlyList<KingfisherOptionsInfoItem>` as options parameter is never constructed and passed in any test; defaults-only paths are tested. A test passing `[KingfisherOptionsInfoItem.TargetCache(cache)]` would exercise option marshalling.

### Recommended High-Value Tests to Add

```csharp
// 1. ImageCache round-trip
var cache = new ImageCache("AuditRoundTrip");
var image = new UIImage(); // minimal 1×1 UIImage
await cache.StoreAsync(image, null, "test-key");
var result = await cache.RetrieveImageAsync("test-key");
Assert(result.CacheType == CacheType.Memory, "round-trip cache type");
// Also: Assert(result.Image != null)

// 2. KF.Builder → Set (compile-gate only — no network needed)
var imageView = new UIImageView();
var url = new NSUrl("https://example.com/image.png");
var task = KF.Url(url).Set(imageView);
// task == null when offline is acceptable — proves dispatch path

// 3. ImageDownloader.DownloadImageAsync — requires a reachable URL
// (Use a bundled test fixture or local HTTP server)

// 4. Processor.Process()
using var proc = new BlurImageProcessor(5.0);
using var item = ImageProcessItem.Image(new UIImage());
// var output = await proc.ProcessAsync(item, [...]) -- check non-null
```

---

## Action Items

| # | Dimension | Finding | Recommendation | Effort | Value |
|---|---|---|---|---|---|
| 1 | Coverage | GenericTypeCallback:22 — `KingfisherWrapper.setImage` and `setBackgroundImage` all skipped; `.kf.setImage()` pattern unavailable | Emit closed-generic `@_cdecl` wrappers for `KingfisherWrapper<UIImageView>` + `<UIButton>` in ConstrainedExtensionEmitter | Medium-Hard | High |
| 2 | Coverage | UnsatisfiedGenericConstraint — `ImageCache.memoryStorage` / `.diskStorage` / custom `init` lost; `Foundation.Data` fails ISwiftObject check | Allow opaque-handle binding path for generic type args that satisfy Swift constraints but not ISwiftObject | Medium | Medium |
| 3 | Coverage | UnsupportedClosure — `Filter.init`, `AnyImageModifier.init`, `AnyModifier.init`, `AnyRedirectHandler.init` take `(T)->T` closures | Generalize closure marshal for single-arg transform shape `(T) -> T` | Medium | Medium |
| 4 | Quality | `RetrieveImageInDiskCache` callback overload (`Kingfisher.cs:4373`) exposes `IntPtr` instead of `UIImage?` | Emit typed `UIKit.UIImage?` in callback; or add doc comment directing to `RetrieveImageInDiskCacheAsync` | Low | Low |
| 5 | Quality | No C# `.Kf` extension property on `UIImageView`/`UIButton` (natural consequence of #1 but confusing to Kingfisher users) | After #1 lands, emit a `Kf` extension method on `UIImageView` returning `KingfisherWrapper<UIImageView>` | Low | High (UX) |
| 6 | Testing | `KingfisherManager.RetrieveImageAsync` never called in tests — primary download path unproven | Add cache round-trip test (Section 5 extension) and KF.Builder.Set test (new section) | Low | High |
| 7 | Testing | `ImageCache.StoreAsync` → `RetrieveImageAsync` round-trip never executed | Add Section 5 extension: store a UIImage, retrieve it, assert CacheType + non-null Image | Low | High |
| 8 | Testing | `KF.Builder.Set(UIImageView)` entirely untested | New section: call `KF.Url(url).Set(imageView)`, assert task non-null (offline → null is fine) | Low | Medium |
