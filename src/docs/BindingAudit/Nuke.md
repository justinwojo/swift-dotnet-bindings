# Nuke — Binding Audit

- **Package**: SwiftBindings.Nuke v13.0.6   **Mode**: source   **TFM(s)**: net10.0-ios, net10.0-tvos, net10.0-macos
- **Native**: kean/Nuke 13.0.6
- **Audited at**: swift-dotnet-packages 1e8c27a, generated 2026-06-27T19:47Z

## Verdict

The Nuke binding is **production-ready for the core flow**. All 63 types bind (100%), member coverage is 88.6% (319/360), and the primary consumer pattern — `ImagePipeline.Shared.ImageAsync(request)` → `UIImage`, cache read/write, DataAsync tuple return — is fully bound and deeply tested. The 19 skipped members are dominated by two patterns: an existential-dictionary type (`[K: any Sendable]`) that blocks both `userInfo` properties, and two closure-typed properties that require a wiring fix (`makeImageDecoder`, `ImageProcessors.Anonymous.init`). Neither blocks the core load flow. The most material gap for advanced users is the absence of `userInfo` (request metadata tagging) and the `ImagePipeline.IDelegate` being exercised by zero tests.

---

## 1. Coverage

| Metric | Count | % |
|---|---|---|
| Types emitted | 63 / 63 | 100% |
| Members emitted | 319 / 360 | 88.6% |
| Members synthesized | 283 | — |
| Members skipped | 19 | 5.3% |

### Skip-reason breakdown

#### DuplicateSignature — 5 items

| Swift API | C# collision | Judgment |
|---|---|---|
| `DataCache.filename(_for:SwiftString)` | Collides with `filename(for:URL)` projected as same C# sig | **(b) minor real gap** — second overload dropped; the `cachedData(for:)` route still exists |
| `AssetType.init(rawValue:String)` | Collides with `init(_ string:String)` | **(a) correctly excluded** — both ctors project to `ctor(string)`; the remaining one works |
| `ImageContainer.UserInfoKey.init` | Same as above | **(a)** — surviving ctor is usable |
| `ImageRequest.UserInfoKey.init` | Same as above | **(a)** — surviving ctor is usable |
| `ImageProcessors.Resize.init(size:,unit:,contentMode:,…)` | Collides with a shorter overload | **(a)** — shorter overload survives; Nuke.cs:18807 tests it |

#### UnsupportedExistential — 4 items (all `[K: any Sendable]`)

| Swift API | Impact |
|---|---|
| `ImageContainer.userInfo: [UserInfoKey: any Sendable]` | **(b) real gap** — extension point for decoder metadata, animated-image flags. `UserInfoKey` constants ARE bound but the dictionary is inaccessible. |
| `ImageContainer.init(image:…userInfo:[K:any Sendable])` | **(b) real gap** — no direct construction with metadata; workaround: construct without userInfo then let the pipeline populate it. |
| `ImageRequest.userInfo: [UserInfoKey: any Sendable]` | **(b) real gap** — request tagging (custom cache keys, analytics) and progressive-preview signalling are blocked. |
| `ImageRequest.init(url:…userInfo:[K:any Sendable])` | **(b) real gap** — the `init(url:processors:priority:options:userInfo:)` factory is the idiomatic full init; without it consumers can only use the partial URL-string constructor. |

**Root cause**: `[K: any Sendable]` — an `any Sendable` existential in the value position of a `Dictionary`. Fix: project as `Dictionary<K, object>` (erase `any Sendable` → `object`). Medium generator effort; would unlock this pattern across all libraries.

#### AnyTypeFallback — 3 items

| Swift API | Swift type | Judgment |
|---|---|---|
| `ImageTask.progress` | `AsyncCompactMapSequence<AsyncStream<Event>, Progress>` | **(b) convenience gap** — opaque generic async sequence can't be projected. Workaround: `ImageTask.Events` (IAsyncEnumerable<Event>, fully bound at Nuke.cs:9417) + filter for `.progress` case. |
| `ImageTask.previews` | `AsyncCompactMapSequence<AsyncStream<Event>, ImageResponse>` | **(b) convenience gap** — same pattern. Workaround: `Events` + filter for `.preview` case. |
| `DataLoader.delegate` | `(any URLSessionDelegate)?` | **(b) minor** — falls back to `object?`; `[Nuke.cs:10795]` comment-dropped. Affects consumers who want to inspect the underlying `NSURLSession` delegate. Low priority. |

`ImageTask.progress` and `.previews` are ergonomic; the `Events` stream is functionally equivalent and IS bound. Not blocking.

#### UnsupportedType — 2 items

- `ImageRequest.Priority.<` — comparison operator on a Swift `enum`; C# enums are already comparable by cast. **(a) correctly excluded** — no real gap.
- `ImagePipelineActor.unownedExecutor` — actor runtime property. **(a) correctly excluded** — not user-facing by design.

#### UnsupportedClosure — 2 items

| Swift API | Impact |
|---|---|
| `ImagePipeline.Configuration.makeImageDecoder: @Sendable (ImageDecodingContext) -> (any ImageDecoding)?` | **(b) real gap** — custom decoder factory injection is a core extensibility hook. Workaround: implement `ImagePipeline.IDelegate.ImageDecoder(context:pipeline:)` (fully bound at Nuke.cs:14779). Medium value. |
| `ImageProcessors.Anonymous.init(id:closure:)` | **(b) real gap** — this type exists solely to wrap an ad-hoc processor closure; with the init dropped the type has no usable constructor. Low value (niche API), but the type appears dead in the binding. |

#### SwiftUIConstraint — 2 items

Both are `ImagePipeline.imagePublisher(with:)` overloads returning `AnyPublisher<ImageResponse, Error>` from Combine. **(a) correctly excluded** by policy. `ImageAsync` covers the same use case idiomatically.

#### EveryProtocolConformanceSkipped — 1 item

`ImagePipeline.Delegate.DelegateProxy` — a synthesized Swift conformance helper. **(a) correctly excluded** — doesn't affect C# consumers; `ImagePipeline.IDelegate` (the C# interface) is properly bound and passable to the two `ImagePipeline` constructors.

### Prioritized generator unlocks

| # | Gap | Fix | Effort | Value |
|---|---|---|---|---|
| 1 | `[K: any Sendable]` dict — drops 4 members across `ImageRequest` + `ImageContainer` | Project `any Sendable` → `object` in dictionary value position | Medium | High — cross-library pattern |
| 2 | `makeImageDecoder` closure property | Support `@Sendable (T) -> U?` closure property binding | Medium | Medium — `IDelegate` workaround exists |
| 3 | `ImageTask.progress`/`.previews` opaque async sequences | Project as `IAsyncEnumerable<T>` wrapping the `Events` stream | Low-Medium | Low — `Events` already serves |
| 4 | `ImageProcessors.Anonymous.init` closure ctor | Same closure-shape fix as `makeImageDecoder` | Medium | Low — niche API |

---

## 2. C# Quality

### Naming and shape
PascalCase throughout. Namespacing is clean: caseless-enum namespaces (`ImageProcessors`, `ImageDecoders`, `ImageProcessingOptions`) project as C# child namespaces, which requires explicit `using ImageProcessors = Nuke.ImageProcessors;` aliases in consumer code — the test file documents this at UIKit.cs:14–18. Slightly awkward, but correct. No mangled symbols visible.

Nested types (`ImagePipeline.ConfigurationType`, `ImageRequest.PriorityType`, `ImageTask.StateType`, `ImageTask.Progress`, `ImageTask.Event`, `ImagePipeline.Error`) follow the `<Type>Type` / `<Type>` pattern consistently. The `*Type` suffix on what is really an inner enum (e.g. `ImageRequest.PriorityType` for Swift `ImageRequest.Priority`) is generator convention, not a mistake, but will read oddly to a Nuke user who knows the Swift names.

### Async
The core async surface is excellent:
- `ImagePipeline.ImageAsync(NSUrl, CancellationToken) → Task<UIImage>` — Nuke.cs:15917
- `ImagePipeline.ImageAsync(ImageRequest, CancellationToken) → Task<UIImage>` — Nuke.cs:16096
- `ImagePipeline.DataAsync(ImageRequest, CancellationToken) → Task<(byte[], NSUrlResponse?)>` — Nuke.cs:16278 — tuple return properly projected.
- `ImageTask.GetImageAsync(CancellationToken) → Task<UIImage>` — Nuke.cs:9151
- `ImageTask.GetResponseAsync(CancellationToken) → Task<ImageResponse>` — Nuke.cs:9325
- `ImageTask.Events: IAsyncEnumerable<ImageTask.Event>` — Nuke.cs:9417 — full event stream, including progress and preview events.

No async API is surfaced as blocking-only. `CancellationToken` is a defaulted optional on every async method.

`ImagePipeline.LoadImage` (callback-based API, soft-deprecated in Nuke 12.9) is marked `[Obsolete(SB0001)]` at Nuke.cs:16497 — the diagnostic correctly signals that it uses `CallConvSwift` with no `@_cdecl` wrapper and may crash on Mono JIT (Issue 1). Consumers will see the warning; the `SB0001` URL points to the troubleshooting wiki.

### Nullability
Optional returns are consistently nullable (`?`):
- `ImagePipeline.Shared` → `Nuke.ImagePipeline` (non-nullable — correct, it's a guaranteed singleton)
- `DataCache.CachedData(for:)` → `byte[]?`
- `ImageCache.this[ImageCacheKey]` → `ImageContainer?` — Nuke.cs:1846
- `ImagePipeline.Cache.CachedImage(request:)` → `ImageContainer?`
- `DataLoader.delegate` → `object?` (degraded from `(any URLSessionDelegate)?` but nullable is correct)

`ImageTask.CurrentProgress` at Nuke.cs:8994 returns `ImageTask.Progress` (non-nullable struct) — correct, it always has a value (both bytes are 0 before download starts, which is valid).

### Lifetime / IDisposable
`IDisposable` present on all Swift reference types (DataCache, ImageCache, ImagePipeline, ImagePrefetcher, ImageTask, DataLoader, ImageDecoderRegistry, TaskQueue) and all value-type structs that own native memory (ImageRequest, ImageContainer, ImageCacheKey, ImageResponse, AssetType, ImageProcessors.*, etc.). No obvious lifetime gaps.

The test uses `using var` for struct processors (Nuke.cs tests: N9a–N9c), which is the correct pattern.

### Ergonomic gaps
1. **`userInfo` silently absent** — Nuke.cs:5654 and 6275 have comment-drop annotations. A consumer reading the Swift docs who expects to set `request.userInfo[.imageID] = "my-key"` will find neither property; the only hint is the inline `// Unsupported:` comment in the generated source, not in any public diagnostic. *No observable degradation notice surfaces to the consumer.* (The `UserInfoKey` structs with their static properties ARE emitted, which may confuse consumers who see keys but no dictionary to use them with.)

2. **`ImageTask.progress` / `.previews` not sugar-wrapped** — Consumers will need to use `Events` + LINQ/pattern-match to get the same data. Not broken; just less ergonomic.

3. **`ImageDecoderRegistry.Register` throws `NotSupportedException`** at runtime — Nuke.cs:3047 stub is `[Obsolete(SB0005)]`. The method is visible in public API but useless; a consumer who calls it gets a runtime throw, not a compile error.

4. **`ImageProcessors.Anonymous` has no usable constructor** — the type appears in the public surface but cannot be instantiated.

---

## 3. Test Coverage

| Platform | File | Test cases |
|---|---|---|
| iOS Simulator + Device (UIKit) | `tests/Program.UIKit.cs` | 78 distinct named cases |
| macOS | `tests/Program.MacConsole.cs` | 6 metadata-only checks |

### Phase-by-phase depth

| Phase | Cases | Depth |
|---|---|---|
| 1 — Smoke | 3 | Weak (type metadata size > 0) |
| 2 — Library | 12 | Strong — construction, property get/set, enum values, singleton access |
| 3 — Async | 10 | **Strong** — real image load from bundled file, `DataAsync` tuple, cancellation, 5 concurrent loads, cache-hit timing |
| 4 — Cache | 15 | **Strong** — `DataCache` store/contains/remove/sweep roundtrip, `ImageCache` cost/count limit set/get, TTL, `RemoveAll` |
| 5 — Pipeline Config | 7 | Strong — `ImagePipeline(Configuration)`, `Invalidate`, `Cache` access, `DataLoader` statics |
| 6 — Prefetcher & Task | 12 | Strong — `ImagePrefetcher` construction, `Priority` set/get, `StopPrefetching`, `ImageTask` from NSUrl + ImageRequest, `Cancel`, `CurrentProgress`, `Priority` set |
| 7 — Decoder | 2 | Medium — construction only, no `Decode()` call |
| 8 — Parity | 9 | **Strong** — N1 `UrlRequest` ObjC bridge, N2 `Processors` existential-array round-trip, N6 `Cache.ContainsCachedImage` + `CachedImage` + `StoreCachedImage` + `RemoveCachedImage` |
| 9 — Coverage Gaps | 10 | Medium-Strong — processor construction, `Options`/`Priority`/`ThumbnailOptions` structs, memory pressure (100 creates) |

The test suite is notably well-engineered: it routes all network loads through a bundled `test_image.png` via a `file://` URL (see `BuildTestImageBaseUrl()`) to eliminate external-host flakiness, and uses URL fragment seeds (`#async-load`, `#concurrent0`, …) to maintain distinct cache keys without needing separate files.

### Untested surface

| Surface | Type | Priority |
|---|---|---|
| `ImageTask.Events` (IAsyncEnumerable) | Strong gap | **High** — this is the primary observability path for progress + previews; not a single test touches it |
| `ImagePipeline.IDelegate` | Real gap | **High** — the interface is carefully bound with async `WillLoadDataAsync` and 10 other methods; zero tests prove any method is callable |
| `ImageProcessors.*` round-trip via pipeline | Moderate gap | Medium — `Resize`, `Circle`, `RoundedCorners` are constructed but never passed to a request or run through the pipeline |
| `ImageRequest.Processors` setter | Minor gap | Medium — getter is proven empty; the setter path (existential-array serialization) is untested |
| `ImageContainer` direct construction | Minor gap | Low — only accessed via pipeline response; `userInfo` constructor is skipped anyway |
| `TaskQueue` | Zero tests | Low — internal scheduling, limited consumer value |
| `ImagePipeline.Cache.RemoveAll()` | Minor gap | Low — `RemoveCachedImage` is tested; `RemoveAll()` on the cache accessor is not |
| `DataLoader` custom init | Zero | Low — only static properties tested |

### Recommended tests to add

1. **`ImageTask.Events` progress stream** — create a task, iterate `task.Events` via `await foreach`, assert at least one `.progress` event before `.completed`. Tests `IAsyncEnumerable<Event>` + `Event.TryGetProgress`. Add to Phase 3 async.
2. **Processor round-trip** — build `new ImageRequest(url, processors: [new ImageProcessors.Resize(width: 64)])`, load via pipeline, assert returned image size ≤ 64px wide. Validates existential-array processor injection end-to-end.
3. **`ImagePipeline.IDelegate` smoke** — implement a minimal `IDelegate` (return `null` from optional methods, identity from required ones), pass it to `new ImagePipeline(config, delegate: myDelegate)`, load an image, assert `ImageTaskCreated` was called. This proves the C# interface shim works at the Swift boundary.
4. **`ImageRequest.Processors` setter round-trip** — create a request, assign `[new ImageProcessors.Circle()]` to `Processors`, read it back, assert `Count == 1`. Validates existential-array serialization.

---

## Action Items

| # | Dimension | Finding | Recommendation | Effort | Value |
|---|---|---|---|---|---|
| 1 | Coverage | `[K: any Sendable]` blocks `ImageRequest.userInfo`, `ImageContainer.userInfo`, + their full constructors | Generator fix: project `any Sendable` → `object` in dictionary value position | Medium | High |
| 2 | Coverage | `makeImageDecoder` closure property unbound; `ImageProcessors.Anonymous.init` dead | Generator fix: support `@Sendable (T) -> U?` closure property/parameter | Medium | Medium |
| 3 | Quality | `ImageDecoderRegistry.Register` is visible public API that always throws | Mark `[EditorBrowsable(Never)]` in addition to `[Obsolete]`, or exclude entirely | Low | Low |
| 4 | Quality | `ImageProcessors.Anonymous` has no usable ctor — appears in public surface | Exclude type when its only constructor is dropped, or add XML doc comment noting the gap | Low | Low |
| 5 | Quality | `userInfo` absent with no consumer-visible signal (only `// Unsupported:` in source) | Emit `[Obsolete(SB00xx, …)]` stubs for skipped-existential properties so consumers see a compile-time diagnostic | Medium | Medium |
| 6 | Tests | `ImageTask.Events` (IAsyncEnumerable) has zero coverage | Add Phase 3 test: iterate `task.Events`, assert ≥1 `.progress` event | Low | High |
| 7 | Tests | `ImagePipeline.IDelegate` has zero coverage | Add minimal delegate implementation; assert `ImageTaskCreated` fires | Low | High |
| 8 | Tests | Processor injection untested end-to-end | Add `Resize(width:64)` round-trip: load image, assert width ≤ 64 | Low | Medium |
| 9 | Tests | `ImageRequest.Processors` setter untested | Set `[Circle()]`, read back, assert `Count == 1` | Low | Medium |
