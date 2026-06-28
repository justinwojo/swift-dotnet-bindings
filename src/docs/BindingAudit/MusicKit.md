# MusicKit — Binding Audit

- **Package**: SwiftBindings.Apple.MusicKit v26.2.8   **Mode**: apple   **TFM(s)**: net10.0-ios26.2, net10.0-macos26.2, net10.0-maccatalyst26.2, net10.0-tvos26.2
- **Native**: Apple MusicKit framework (SDK 26.2)
- **Audited at**: swift-dotnet-packages main 1e8c27a, generated 2026-06-27T19:49:48Z

## Verdict

All 134 types emit (100%). Member coverage is 677/966 (70.1%) — after removing the 59 intentional SynthesizedCodable skips the real-gap surface is ~77 members. The catalog-search and player-control flows are fully usable end-to-end. The critical blocker is that `MusicLibraryResponse<T>.items` is missing (AnyTypeFallback on every T), which means a consumer can dispatch `MusicLibraryRequest<Song>.ResponseAsync()` and await the response but cannot read the results — the library-request flow is broken at the final step. Pagination (`MusicItemCollection.nextBatch`) and the fetch-by-ID (`MusicCatalogResourceRequest.response`) flow are also absent. Tests are shallow (metadata + enum values dominate); the search construct and player singleton tests are the only real-dispatch coverage.

---

## 1. Coverage

### 1a. Emitted / total

| Dimension | Emitted | Total | % |
|---|---|---|---|
| Types | 134 | 134 | 100% |
| Members | 677 | 966 | 70.1% |
| Synthesized members (generator-added) | 634 | — | — |
| Skipped members | 195 | — | — |

**Emitted by kind:** Property 581 · Method 74 · Operator 21 · Subscript 1

### 1b. Skip-reason breakdown

| Reason | Count | Classification |
|---|---|---|
| SynthesizedCodable | 59 | **(a) correctly excluded** — Encoder/Decoder are unresolvable existentials by design |
| UnsupportedType | 37 | Mix — see §1c |
| UnsupportedSignature | 29 | **(b) real gaps** — 7 `==` operators on generic types, 6 generic constructors with method-own type params, 3 variadic ctors, 5 enum `encode`, 1 async without `@_cdecl`, 2 placeholder-type methods, 5 ApplicationMusicPlayer.Queue ctors |
| GenericProtocolConstraint | 22 | **(b) real gaps** — `MusicLibraryRequest.filter(matching:)` (7 overloads) + `MusicLibrarySectionedRequest.filterItems/filterSections/sortItems/sortSections` (15 overloads) |
| AnyTypeFallback | 15 | Mix — see §1d |
| EveryProtocolConformanceSkipped | 11 | **(a) correctly excluded** — protocol proxies for MusicLibraryRequestable, FilterableMusicItem, MusicPropertyContainer, and library Filter/Sort protocols; EveryProtocol conformance not yet decided for MusicKit's protocols |
| UnsupportedExistential | 9 | **(b) real gaps** — `.types` property on SearchRequest/ChartsRequest/PersonalRecommendation/SuggestionsRequest (4 properties, 2 inits); 3 init overloads covered by the shim |
| GenericTypeCallback | 5 | **(b) real gaps** — see §1e |
| DuplicateSignature | 4 | **(b) real gaps** — 2× `MusicItemID.init(rawValue:)` collision (both map to `ctor(string)`), `MusicPropertyContainer.with` duplicate, `MusicPlayer.Transition.crossfade` enum/ctor collision |
| SwiftUIConstraint | 2 | **(a) correctly excluded** — `objectWillChange` on Queue/State references SwiftUI/Combine |
| NonBlittableCallConvSwift | 1 | **(b) real gap** — `MusicCatalogResourceRequest.init` (generic-type ctor, open dispatch) |
| UnsatisfiedGenericConstraint | 1 | **(b) real gap** — `MusicCatalogSearchSuggestionsResponse.topResults` (type arg doesn't satisfy `MusicItem` constraint) |

### 1c. UnsupportedType — detail

**36 of 37** are `PartialMusicProperty<T>` constrained-extension properties (e.g. `.artists`, `.topSongs`, `.relatedAlbums`, `.entries`). These are explicitly suppressed at the open-generic class level and **ARE available as concrete-specialization extension classes** (`ConstrainedExtensionEmitter` emits them per concrete T). Not real gaps.

**1 genuine exclusion**: `MusicItemCollection.+=` operator — no C# in-place collection-append equivalent. Correctly excluded.

### 1d. AnyTypeFallback — usability impact

| Member | Impact |
|---|---|
| `MusicLibraryResponse<T>.items` | **Severe** — library request flow broken (see §1e, Finding A1) |
| `MusicLibrarySection<S,T>.items` + subscript | High — sectioned library items inaccessible |
| `MusicLibrarySectionedResponse.sections` | High — entire sectioned response unusable |
| `MusicCatalogResourceResponse<T>.items` | High — compound with resource request gap |
| `MusicRecentlyPlayedResponse.items` | Medium — recently played list unavailable |
| `MusicPropertyContainer.with` (2) | Medium — property-scoped fetch specialization lost |
| `MusicItemCollection.indices` | Low — `StartIndex`/`EndIndex` + `Count`/indexer cover this |
| `MusicItemCollection.subscript` (2 overloads) | Low — `TMusicItemType this[int index]` (MusicKit.cs:20341) covers it |
| `MusicCatalogChart.items` | Low — chart items inaccessible but Charts requests themselves usable via ChartsResponse |
| `ApplicationMusicPlayer.Queue.Entries.indices` + subscript | Low — queue entries accessible via `MusicPlayer.Queue.Entry` current-entry property |

The `*CsmExtensions` classes for `MusicLibraryResponse` (Album, Song, Playlist, Artist, Genre, MusicVideo, Track, etc.) are all **empty** — no CSM specialization fills the `Items` property gap.

### 1e. GenericTypeCallback — the core async gap cluster

These 5 skips all share the same root: an async method on a generic type whose callback return type is parameterized by the parent class's type parameter. The generator cannot emit a safe callback bridge for this shape.

| Skipped member | Impact |
|---|---|
| `MusicItemCollection<T>.nextBatch()` (2 overloads) | No pagination — any search returning >25 results is truncated |
| `MusicLibrarySectionedRequest<S,T>.response()` | Entire sectioned library response flow unavailable |
| `MusicRecentlyPlayedRequest.response()` | Recently played items not accessible |
| `MusicCatalogResourceRequest<T>.response()` | Fetch-by-ID + relationship-property flow unavailable |

### 1f. Prioritized generator unlocks

| # | Gap | Reason | Mechanism | Value | Effort |
|---|---|---|---|---|---|
| 1 | `MusicLibraryResponse<T>.items` | AnyTypeFallback | Emit CSM concrete extension `Items` per T (same pattern as `ResponseAsync` extensions already working) | **Critical** — unblocks library read loop | Low-medium (shim-level pattern) |
| 2 | `MusicItemCollection<T>.nextBatch()` | GenericTypeCallback | Concrete CSM extension per T; emit typed `Task<MusicItemCollection<T>?>` async callback | High — pagination essential for any real search | Medium |
| 3 | `MusicCatalogResourceRequest<T>.response()` | GenericTypeCallback + AnyTypeFallback response | Depends on #1 fix for `MusicCatalogResourceResponse<T>.items`; then same CSM approach | High — fetch-by-ID core use case | Medium (blocked by #1) |
| 4 | `MusicLibraryRequest<T>.filter(matching:KeyPath:equalTo:)` | GenericProtocolConstraint | Route-C concrete extension (already used for `Sort` on same request type) | Medium — type-keyed filter like "albums with artistID == X" | Medium |
| 5 | `MusicPlayer.Queue.insert` (async) | UnsupportedSignature (no @_cdecl async wrapper) | Add `@_cdecl` wrapper in the generated Swift wrapper | Medium — queue mutation after construction needs this | Low |

---

## 2. C# Quality

### 2a. Naming and shape

Clean throughout. PascalCase, no Swift mangling visible, natural namespaces. Nested types (`MusicPlayer.Queue`, `MusicPlayer.State`, `MusicAuthorization.Status`, `MusicLibrary.Error`, `Playlist.Entry`) are correctly nested. Enums (`PlaybackStatus`, `RepeatMode`, `ShuffleMode`, `AudioVariant`, `ContentRating`, `MusicCatalogChartKind`) map cleanly with integer backing.

One nominal awkwardness: the `MusicItemCollection<T>` CSM extension classes generate names like `MusicItemCollectionMusicKit_SongCsmExtensions` — consumers unlikely to type these directly, but they appear in IntelliSense. Acceptable given the CSM pattern.

### 2b. Async

Async methods surface as `Task<T>`/`Task` with `CancellationToken` defaults. Examples:

- `MusicAuthorization.RequestAsync()` → `Task<MusicAuthorization.Status>` (MusicKit.cs:1440)
- `MusicCatalogSearchRequest.ResponseAsync()` → `Task<MusicCatalogSearchResponse>` (MusicKit.cs:48755)
- `MusicLibraryRequest<T>.ResponseAsync()` concrete extensions → `Task<MusicLibraryResponse<T>>` (e.g. MusicKit.cs:4126 for Album) ✅
- `MusicPlayer.PlayAsync()`, `SkipToNextEntryAsync()`, `SkipToPreviousEntryAsync()` (MusicKit.cs:55998, 56325, 56520) ✅
- `MusicPlayer.Queue.InsertAsync()` 9 overloads (MusicKit.cs:52637+) ✅
- `MusicLibrary.CreatePlaylistAsync()`, `EditAsync()`, `AddAsync()` (MusicKit.cs:15590+) ✅

**Broken async**: `MusicPlayer.Queue.insert(after:)` (the async overload that takes a `Queue.Entry` and position) is missing — `UnsupportedSignature` because there is no `@_cdecl` wrapper for this Swift async ABI entry (MusicKit.cs comment at line ~148 in report). The synchronous/non-async queue insertion via `InsertAsync(entry, position)` that takes typed playlist items does work.

### 2c. Nullability

Optional Swift return types consistently map to nullable C# (`?`). `Song.Artwork?`, `Song.ContentRating?`, `Song.Duration?`, `Album.Copyright?` etc. all correctly nullable. `Song.Title` and `Song.ArtistName` non-nullable (Swift has no `?`). Looks correct.

### 2d. Lifetime / IDisposable

`IDisposable` present on all struct types (`Song`, `Album`, `Artist`, `Playlist`, `MusicItemCollection<T>`, `MusicCatalogSearchRequest`, etc.). `MusicPlayer`, `ApplicationMusicPlayer`, `SystemMusicPlayer` are class-type (`ISwiftObject` without struct suffix). `MusicAuthorization` carries a `SwiftSafeHandle` and `IDisposable` (MusicKit.cs:711). Overall lifetime model is correct.

### 2e. Key usable flows (anchored)

**Search flow — ✅ fully usable:**
```csharp
// Construct (shim works around [any MusicCatalogSearchable.Type])
var req = MusicCatalogSearchRequest.Create("Taylor Swift",
    MusicCatalogSearchTypes.Song | MusicCatalogSearchTypes.Album);  // MusicKit.cs:95 (Shims)
var response = await req.ResponseAsync();  // MusicKit.cs:48755 → Task<MusicCatalogSearchResponse>
// Typed result collections directly on the response struct:
MusicItemCollection<Song> songs = response.Songs;   // MusicKit.cs:49229+
MusicItemCollection<Album> albums = response.Albums; // MusicKit.cs:13285+
foreach (var song in songs) { /* IReadOnlyList<Song> */ }  // MusicKit.cs:20341
// HasNextBatch: true/false; nextBatch() MISSING (pagination gap)
bool hasMore = songs.HasNextBatch;  // MusicKit.cs:20046 ✅ — nextBatch() ❌ GenericTypeCallback
```

**Library request flow — ⚠️ broken at result step:**
```csharp
var req = MusicLibraryRequest<Song>.FromMusicKit_Song(); // MusicKit.cs:4935 ext
req.Filter("Taylor Swift");  // text filter only, KeyPath filter missing
var response = await req.ResponseAsync(this MusicLibraryRequest<Song>); // MusicKit.cs:4936 ✅
// response.Items → MISSING (MusicKit.cs:8789 "// Unsupported: property 'MusicLibraryResponse.items'")
// Only Description/DebugDescription available on MusicLibraryResponse<Song>
```

**Player flow — ✅ fully usable:**
```csharp
var player = ApplicationMusicPlayer.Shared;  // MusicKit.cs:56613
player.Queue = ApplicationMusicPlayer.QueueType.FromSwift_SwiftArray_MusicKit_Song_MusicKit_Song(songs);
await player.PlayAsync();    // MusicKit.cs:55998
player.Pause();              // MusicKit.cs:56057
player.Stop();               // MusicKit.cs:56090
await player.SkipToNextEntryAsync();  // MusicKit.cs:56325
var status = player.State.PlaybackStatus;  // MusicKit.cs:54560 (MusicPlayer.PlaybackStatus enum)
var repeatMode = player.State.RepeatMode; // MusicKit.cs:54707 — nullable enum
```

**MusicItemCollection<T> — ✅ as IReadOnlyList, ❌ no nextBatch:**
- Implements `IReadOnlyList<TMusicItemType>` (MusicKit.cs:19971)
- `Count` (20320), `this[int index]` (20341), `GetEnumerator()` — all present
- CSM extensions: `Index(nint, nint)`, `Distance(nint, nint)`, `FormIndex(ref nint)` — present (MusicItemCollectionMusicKit_SongCsmExtensions:21034)
- `nextBatch()` → missing (MusicKit.cs:20388-20389)

### 2f. Issues

**Finding A1 (Severe) — `MusicLibraryResponse<T>.items` missing, MusicKit.cs:8789**
The property exists in Swift as `items: MusicItemCollection<MusicItemType>` but emits as AnyType. Every `*CsmExtensions` class for `MusicLibraryResponse` is empty. Consumer can await the response but cannot access results. Workaround: none without a shim.

**Finding A2 (High) — `MusicPlayer.Transition.crossfade` duplicate collision, MusicKit.cs binding-report DuplicateSignature**
The `MusicPlayer.Transition` struct has a `Crossfade` enum case plus a static factory property also named `crossfade` — the factory collides and is dropped. The `MusicPlayer.Transition.Crossfade` enum case is accessible, but the factory constructor is silently absent.

**Finding A3 (Medium) — `MusicCatalogResourceRequest<T>` non-constructable for most T**
Only `MusicCatalogResourceRequest<Genre>` has a `FromMusicKit_Genre()` factory (MusicKit.cs:45917). All other concrete type specializations (Album, Song, Artist, etc.) have empty `*CsmExtensions` classes. The `.response()` method is also missing (GenericTypeCallback). The type is effectively unusable for all catalog item types except Genre.

**Finding A4 (Medium) — `.types` property missing on request types despite shim ctors**
`MusicCatalogSearchRequest.types`, `MusicLibrarySearchRequest.types`, `MusicCatalogChartsRequest.types`, `MusicPersonalRecommendation.types`, `MusicCatalogSearchSuggestionsRequest.typesForTopResults` are all `UnsupportedExistential`. The init shims work around the ctor gap but post-construction inspection/modification of the types filter is absent. Impact: once constructed, a request's type filter is opaque and immutable from C#.

**Finding A5 (Low) — `MusicItemID` ctor collision**
Two Swift `MusicItemID` initializers (one `init(rawValue:)` and one `init(stringLiteral:)`) both project to `ctor(string)` in C#. The second is silently dropped (DuplicateSignature). Workaround: the surviving ctor is the right one for most uses; `MusicItemID.Id` (property) roundtrips correctly.

---

## 3. Test Coverage

### 3a. Count and structure

~40 distinct Pass/Fail/Skip cases across `Tests.cs`. One platform skip (`SystemMusicPlayer.Shared` on non-iOS). No `Program.MacConsole.cs` test content beyond test infrastructure.

**Depth breakdown:**

| Depth | Cases |
|---|---|
| Strong (real async dispatch, non-trivial assert) | 3 |
| Medium (enum value check + description round-trip, singleton non-null) | 13 |
| Weak (metadata handle non-zero only) | 9 |
| Shape proof (compile-time only, no runtime value) | 1 |

**Strong cases:**
- T37 (`MusicSubscription.GetCurrentAsync()`) — real async call, reads `CanPlayCatalogContent`/`CanBecomeSubscriber`/`HasCloudLibraryEnabled` (Tests.cs:581)
- T38 (`MusicCatalogSearchRequest.Create()`) — shim construction call, confirms factory + disposal round-trip (Tests.cs:609)
- T39 (unknown-bits guard) — confirms `ArgumentException` on stray bit in mask (Tests.cs:631)

### 3b. Untested surface (high priority)

| Gap | Why it matters | Recommended test |
|---|---|---|
| `MusicCatalogSearchRequest.ResponseAsync()` → iterate `response.Songs` | Core search flow never exercised at runtime | Add: construct + dispatch, assert `response.Songs` is non-null + `Count >= 0`; framework auth error = pass |
| `MusicLibraryRequest<Song>.ResponseAsync()` result access | Documents the `items` gap explicitly as a test failure | Add test that calls `ResponseAsync`, then attempts to access `.Items` — fails because the property doesn't exist, documenting the gap until fixed |
| `ApplicationMusicPlayer.PlayAsync()` / `Pause()` / `Stop()` | Player dispatch never exercised | Add: `await player.PlayAsync()` accepting framework error (no auth/queue); confirms dispatch reaches Swift |
| `MusicLibrarySearchRequest.Create() → ResponseAsync()` | Library search shim tested at ctor only | Extend T40 with a `.ResponseAsync()` call; framework error = pass |
| `Song.Title`, `Song.ArtistName`, `Song.Duration` property reads | 44-property model type zero coverage | Requires Apple Music auth + search response; add once T37 search flow test exists |
| `MusicItemCollection<T>` `Count`/indexer with live data | Ergonomics shape-proven at compile time only | Add after search response available; assert `songs.Count >= 0`, `songs[0]` non-null |
| `MusicPlayer.State.PlaybackStatus` read | Player state machine not proven | Read after `PlayAsync()`; assert returns a valid enum value |
| `MusicCatalogSearchSuggestionsRequest.ResponseAsync()` | Suggestions shim tested at ctor only (T40) | Extend with response call; assert `.Suggestions.Count >= 0` |

### 3c. Noted legitimate skips

- `SystemMusicPlayer.Shared` on non-iOS (Tests.cs:226) — correct; SystemMusicPlayer is iOS-only.

---

## Action Items

| # | Dim | Finding | Recommendation | Effort | Value |
|---|---|---|---|---|---|
| A1 | Coverage + Quality | `MusicLibraryResponse<T>.items` AnyTypeFallback — library request flow broken at result step (MusicKit.cs:8789) | Add CSM concrete-extension per T mirroring the `ResponseAsync` pattern already working; or add a hand-rolled shim | Medium | Critical |
| A2 | Coverage | `MusicItemCollection<T>.nextBatch()` GenericTypeCallback — no pagination for any collection (MusicKit.cs:20388) | Emit typed concrete CSM async extension per T (`Task<MusicItemCollection<T>?>`) | Medium | High |
| A3 | Coverage | `MusicCatalogResourceRequest<T>.response()` — fetch-by-ID flow unavailable (MusicKit.cs:45877) | Blocked on A1; then concrete CSM extension per T | Medium | High |
| A4 | Coverage | `MusicLibraryRequest<T>.filter(matching:KeyPath:equalTo:)` 7 overloads missing (GenericProtocolConstraint) | Route-C concrete extension per (T, ValueT) pair — same pattern as Sort (MusicKit.cs:8032+) | Medium | Medium |
| A5 | Coverage | `MusicPlayer.Queue.insert(after:)` async missing — no @_cdecl wrapper (binding-report UnsupportedSignature) | Add `@_cdecl` wrapper to `MusicKitSwiftBindings` and regenerate | Low | Medium |
| A6 | Quality | `MusicPlayer.Transition.crossfade` factory collision — DuplicateSignature drops factory (binding-report) | Rename factory to `CrossfadeFade` or use suffix to avoid collision | Low | Low |
| A7 | Tests | No test for `MusicCatalogSearchRequest.ResponseAsync()` full flow | Add test dispatching response; framework auth error = pass | Low | High |
| A8 | Tests | No test dispatching player `PlayAsync()`/`Pause()`/`Stop()` | Add tests accepting framework error; proves dispatch shape | Low | Medium |
| A9 | Tests | `MusicLibraryRequest.ResponseAsync()` result-access gap undocumented | Add negative test (compile-time shape) noting `.Items` missing; documents the A1 gap | Low | Medium |
