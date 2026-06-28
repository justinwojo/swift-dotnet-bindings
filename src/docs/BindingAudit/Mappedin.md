# Mappedin — Binding Audit

- **Package**: SwiftBindings.Mappedin v6.2.0   **Mode**: zip   **TFM(s)**: net10.0-ios
- **Native**: MappedIn/ios 6.4.0 (xcframework zip)
- **Audited at**: main 8dcc3032, generated 2026-06-27T19:48:44Z

## Verdict

Exceptionally strong binding — 366/366 types (100%) and 2553/2932 members (87.1%) emitted. After stripping the 310 intentionally-excluded SynthesizedCodable entries, the real-gap surface is only 61 members out of a meaningful pool of ~2622, putting functional coverage closer to ~97.7%. The core indoor-mapping loop — credentials → `GetMapData` → `Navigation.Draw(directions)` → camera/markers — is fully usable from C#. The primary limitation is the event subscription model: the generic `on(event:, listener:)` pattern on `MapData`, `MapView`, and `BlueDot` is blocked by an unsupported closure shape, so typed ad-hoc event listeners cannot be wired up natively. The `Events` and `BlueDotEvents` static event factories are mostly intact and serve as a partial substitute. Tests are structurally strong (type metadata, enum round-trips, protocol conformance, WKWebView inheritance) but have zero coverage of live SDK operations; this is expected for a network-credentials SDK.

---

## 1. Coverage

### Totals

| Dimension | Emitted | Total | % |
|---|---|---|---|
| Types | 366 | 366 | 100% |
| Members | 2553 | 2932 | 87.1% |
| Synthesized members (bonus) | 1431 | — | — |

Skipped members by kind: Method 338, Property 33.

### Skip-reason breakdown

| Reason | Count | Classification |
|---|---|---|
| SynthesizedCodable | 310 | **(a) Correctly excluded** — synthesized `init(from:)`/`encode(to:)` pruned by design |
| UnsupportedSignature | 14 | **(b) Real gap** — mix of serialization helpers (low value) + BlueDot position methods (medium value) |
| AnyTypeFallback | 13 | **(b) Real gap** — MVF internal-format properties typed as `object` |
| UnsupportedExistential | 11 | **(b) Real gap** — MVF array properties with `Any` existential type arg |
| UnsupportedClosure | 8 | **(b) Real gap** — generic `on/off` event subscription + typed data-query callbacks; **highest-value gap** |
| UnsatisfiedGenericConstraint | 8 | **(b) Real gap** — 8 `Events.*` properties where generic arg fails the C# `ISwiftObject` constraint |
| ModuleInternal | 5 | **(a) Correctly excluded** — implicit+overriding constructors on system-owned types |
| SwiftUIConstraint | 2 | **(a) Correctly excluded** — `Interpolation.on`/`init` reference SwiftUI/Combine |

**Real-gap count (non-(a) skips): 54 members** (UnsupportedSignature 14 + AnyTypeFallback 13 + UnsupportedExistential 11 + UnsupportedClosure 8 + UnsatisfiedGenericConstraint 8).

---

### Real-gap detail

#### UnsupportedClosure: 8 — highest-value gap

The generator cannot marshal a generic closure parameter `(T?) -> Void` in a generic method.

| Skipped member | Why it matters |
|---|---|
| `MapData.on<T>(_ event: TypedMapDataEvent<T>, _ listener: @escaping (T?) -> Void)` | Primary typed event subscription for map data changes (floor switches, spot updates). Blocks reactive event patterns. |
| `MapData.off<T>(_ event: TypedMapDataEvent<T>, _ listener: ((T?) -> Void)?)` | Paired unsubscribe. |
| `MapData.getByType<T>(_ type: MapDataType, onResult: @escaping (Result<[T], Error>) -> Void)` | Type-safe POI/location querying by SDK type. |
| `MapData.getById<T>` | Type-safe look-up by id. |
| `MapData.getByExternalId<T>` | Type-safe look-up by external id. |
| `MapView.on<T>(_ event: TypedEvent<T>, _ listener: @escaping (T?) -> Void)` | Typed event subscription for render events. |
| `MapView.off<T>` | Paired unsubscribe. |
| `BlueDot.on<T>(_ event: BlueDotEvent<T>, _ listener: @escaping (T?) -> Void)` | Typed BlueDot event subscription (position updates, status changes, errors). |

**Partial mitigation**: The `Events` static class (Mappedin.cs:65058) exposes typed `TypedEvent<T>` instances for ~30 named render/camera/floor events as properties. `BlueDotEvents` (Mappedin.cs:28966) exposes 10 `BlueDotEvent<T>` instances. A consumer can hold a `TypedEvent<T>` reference from those statics and pass it to some other registration path, but without `on/off` they cannot subscribe to it.

#### UnsatisfiedGenericConstraint: 8 — Events static class properties

All 8 are in `Events` (Mappedin.cs:65058): `outdoorStyleLoaded`, `outdoorViewLoaded`, `globalStateChange`, `postRender`, `preRender`, `resize`, `userInteractionEnd`, `userInteractionStart`. Each is `TypedEvent<T>` where `T` is a struct type (e.g., `()`, `UInt32`) that does not satisfy the C# `ISwiftObject` constraint the generator requires for generic type arguments. The remaining ~22 Events properties work (CameraChange, FloorChange, FloorChangeStart, etc.).

#### UnsupportedSignature: 14 — mixed value

- **BlueDot.update, BlueDot.follow** (2): Take a position-update placeholder type the generator could not resolve. These are how apps push live location to the SDK's blue-dot indicator. **Medium value** — means BlueDot live tracking must be wired another way (e.g., via the existing `BlueDot` property's other methods, or the app drives raw coordinate updates).
- **Icons.prefetch/prefetchByType/prefetchBySubtype/prefetchByCategory** (4): Icon preloading for performance; the generator hit an unresolvable placeholder type. **Low-medium** — SDK can lazy-load icons without these.
- **EnvMapOptions.toJson/fromJson** (3), **FindNearestOptions.LineOfSight.encode/toJsonValue/fromJsonValue** (3): JSON serialization helpers on simple enums. **Low value** — consumers rarely need to serialize SDK enums.

#### AnyTypeFallback: 13 + UnsupportedExistential: 11 — MVF class

All 24 are in `MVF` (Mappedin.cs:126339), the raw map-vector-format data container. The properties fall into two groups:
- AnyTypeFallback: `connectionJson`, `enterprise`, `floorGeojson`, `floorstackJson`, `manifestGeojson`, `mapGeojson`, `mapstackGeojson`, `mapstackJson`, `navigationFlagsJson`, `nodeGeojson`, `shapesJson`, `stylesJson` + 1 more — typed as `object` because the inner protocol isn't in TypeDatabase.
- UnsupportedExistential: `annotation`, `area`, `entrance`, `facade`, `floorImages`, `modelInstances`, `obstruction`, `shapeInstances`, `space`, `textAreas`, `window` — `SwiftArray<Any>` typed.

`MVF` is an internal format layer rarely touched by app code (the SDK's own types — `MapData.Areas`, `MapData.Coordinates`, etc. — are the idiomatic surface). **Low value** for most consumers.

---

### Prioritized generator unlocks

| Priority | Unlock | Skips fixed | Notes |
|---|---|---|---|
| 1 | **Generic closure parameter marshalling** — `(T?) -> Void` in generic `func on<T>` | 8 (UnsupportedClosure) | Highest-value: unlocks all three `on/off` event subscription sites and `getByType/getById/getByExternalId` typed queries simultaneously |
| 2 | **Loosen `ISwiftObject` constraint on generic type args** — allow `ISwiftStruct`-shaped types | 8 (UnsatisfiedGenericConstraint) | `Events.resize` / `postRender` use struct types like `()` / `UInt32`; constraint is too strict |
| 3 | **Placeholder type resolution for CDeclSignature** — `BlueDot.update/follow` | 2 (UnsupportedSignature) | Fix requires the generator to handle Swift opaque-type placeholders in method signatures; a @_cdecl bridging wrapper on the vendor side is an alternative workaround |
| 4 | **`Any`-existential array in generics** — `SwiftArray<Any>` properties on MVF | 11 (UnsupportedExistential) | Low consumer value; architectural: needs existential-type projection into untyped array |

---

## 2. C# Quality

### Naming and shape

Types are idiomatic PascalCase. The Mappedin 6.x SDK already drops the `MPI` prefix natively, so the binding surface reads naturally (`MapView`, `MapData`, `Directions`, `BlueDot`). No leaked Swift mangling. Protocols are projected as `I`-prefixed interfaces (`IAnchorable`, `IGeoJSONData`, `IQueryOrigin` — Mappedin.cs:834–850). Nested types are faithfully reproduced: `AntialiasingOptions.QualityType`, `MapView.Builders`, `Search.QueryOptions`, `Search.QueryOptions.EnterpriseCategoryOptions`, etc.

### Async / callback shape

All async Swift methods use the `Action<SwiftResult<T, ExistentialContainer1>>` callback pattern — no C# `async`/`await`. Consistent throughout. The four `GetDirections` overloads (Mappedin.cs:2052–2276) are well-structured with proper type-safe `SwiftResult<SwiftOptional<Directions>, ExistentialContainer1>` callbacks. `Navigation.Draw` (multiple overloads) cleanly surfaces the most important render operation.

### `[Obsolete(SB0001)]` on the primary load entry point

`MapView.GetMapData` (Mappedin.cs:7473) carries `[Obsolete("No @_cdecl wrapper…", SB0001)]`. This fires a compile-time warning on the one method consumers need to load a map. The SB0001 text ("P/Invoke calling convention may not match Swift ABI") is alarming to a first-time user, though the method IS callable via `CallConvSwift` directly against the Swift symbol (`$s8Mappedin7MapViewC03getB4Data…`). The callback success type is `SwiftOptional<ExistentialContainer0>` — "void" semantically (loading the map drives a side-effect, not a return value), but consumers don't know that. **Mitigation**: `MapView.MapData` (Mappedin.cs:6562) can be read after the callback fires to get the strongly-typed `MapData` reference.

### Nullability

Properly nullable throughout: `string?` for `Coordinate.FloorId`, `bool?` for `BlueDotUpdateOptions.Animate/Silent`, `double?` for `BlueDotDeviceOrientationUpdatePayload.Heading`, `AntialiasingOptions.QualityType?` for the nested enum property.

### Lifetime

`IDisposable` on all class-backed types. `SwiftObjectHelper` + `SwiftDisposeScope` + `SwiftClassHandle` all present. `BlueDotAction`, `BlueDotEvent<T>`, etc. implement IDisposable. No observable leak or ownership smells.

### `ExistentialContainer0` as "void" return

Several Navigation methods use `ExistentialContainer0` as the success result type: `Navigation.Draw`, `Navigation.Clear`, `MapView.HydrateMapData` etc. (e.g., Mappedin.cs:8833–9000 area). This is semantically `()` (Swift void) — the callback fires with a dummy container indicating success or error. Consumers can pattern-match on the error arm only and ignore the success value. Not broken, but the opaque type name is confusing.

### `MapViewController` WKWebView inheritance

`MapViewController : WKWebView` (Mappedin.cs:11465) is correctly projected. `MapViewController()` has a usable no-arg constructor (Mappedin.cs:11602). `TryCreate(NSCoder, out result)` factory is present. The view controller is how Mappedin renders its map — this path works.

### `BlueDot.update` / `BlueDot.follow` absence (UnsupportedSignature)

These two methods are how an app feeds real device-location data into the blue-dot indicator. Without them, developers must find an alternative path. Not a showstopper, but notable for location-tracking use cases.

---

## 3. Test Coverage

### Structure and count

7 test sections, approximately 150–160 named test cases total. All run on iOS Simulator (Mono JIT); no device-specific section.

| Section | Cases (approx.) | Depth |
|---|---|---|
| 1. Type Metadata | ~35 | Weak — size-only metadata check, no field access |
| 2. Integer Enums | 2 | Weak — ordinal check only |
| 3. String Enum Singletons | ~33 | Medium — loads each singleton, checks non-null |
| 4. Constructors & Properties | ~12 | **Strong** — round-trips property values through constructors |
| 5. RawValue/FromRawValue | ~22 | **Strong** — full round-trip + invalid-input null check |
| 6. Protocol Conformance | ~46 | Medium — `IsAssignableFrom` checks; reflects type system correctness, not runtime behaviour |
| 7. WKWebView Inheritance | 1 | Medium — `IsAssignableFrom` confirms MapViewController hierarchy |

**Strong tests**: `Constructor_Coordinate_Full` round-trips five fields including optional `floorId`; `Constructor_AntialiasingOptions` exercises `Optional<nested-string-enum>` through a getter; `FromRawValue` round-trips cover the failable-init path.

### Untested surface (most important)

| Surface | Why it matters | Suggested assertion |
|---|---|---|
| `MapView.GetMapData` / `HydrateMapDataFromURL` | Primary SDK entry point; the SB0001 warning makes it high-risk | Skip in CI (needs credentials); add a `HydrateMapDataFromURL` test with a bundled binary fixture (no network) |
| `MapData.GetDirections` | Core wayfinding pipeline | Skip in CI; if a bundled MVF fixture is available, construct a `Coordinate` pair and assert `Directions` is non-null with at least one `DirectionInstruction` |
| `Navigation.Draw(directions)` | Render path | Depends on `GetDirections` test above |
| `BlueDotEvents` factory properties | 10 typed events; no coverage | Assert each `BlueDotEvents.*` property returns a non-null `BlueDotEvent<T>` with a non-empty event name; zero network needed |
| `Events` static class properties | ~22 typed events emitted; none tested | Same pattern as BlueDotEvents — metadata test; confirms generic instantiation works |
| `MapData.Nearest` | `FindNearestResult` array callback — covers generic array marshalling | Would need a live `MapData`; test with a fixture if possible |
| `Search.Query` | String-based search returning `SearchResult` | Would need a live search index |
| `Coordinate.Equals` | Already tested in Section 4 ✅ | — |

**Immediately addable (no credentials):**

```csharp
// BlueDotEvents — no network needed
TestBlueDotEventsProperties(logger, results);  
// Assert BlueDotEvents.PositionUpdate != null && .EventName is non-empty (etc.)

// Events static class — confirm TypedEvent<T> generic instantiation
TestEventsStaticProperties(logger, results);
// Assert Events.CameraChange != null, Events.FloorChangeStart != null, etc.

// Directions struct construction
// DirectionInstruction[] instructions = new DirectionInstruction[0]; etc.
// (doesn't test the full callback, but exercises the struct layout)
```

---

## Action Items

| # | Dimension | Finding | Recommendation | Effort | Value |
|---|---|---|---|---|---|
| 1 | Coverage | `MapData.on/off` + `MapView.on/off` + `BlueDot.on` skipped — generic closure `(T?) -> Void` not marshallable | Generator: support generic closure parameter marshalling in typed-event methods | High | High |
| 2 | Coverage | `MapData.getByType/getById/getByExternalId` skipped — same closure limitation | Resolved by #1 above | — | High |
| 3 | Coverage | `Events.outdoorStyleLoaded` + 7 other `Events` properties skipped — `ISwiftObject` constraint rejects struct type args | Generator: allow `ISwiftStruct`-satisfying types as generic args to `TypedEvent<T>` | Medium | Medium |
| 4 | Coverage | `BlueDot.update` / `BlueDot.follow` skipped — placeholder type in signature | Vendor-side: add a @_cdecl bridging wrapper with a concrete type; or generator: improve placeholder type resolution | Medium | Medium |
| 5 | Quality | `MapView.GetMapData` carries `[Obsolete(SB0001)]` — alarming warning on the primary load entry point | Suppress or de-escalate SB0001 if CallConvSwift P/Invoke is known-safe for this symbol; or add a @_cdecl wrapper in MappedinSwiftBindings to remove the direct-Swift P/Invoke | Low | Medium |
| 6 | Quality | `GetMapData` / `Navigation.Draw` callback success type is `ExistentialContainer0` (opaque "void") | Generator: emit `ExistentialContainer0` as a known-void marker and document it in the method XML-doc; or introduce a void-result type alias | Low | Low |
| 7 | Tests | `BlueDotEvents.*` and `Events.*` static properties have zero coverage | Add a ≤20-line test block asserting each factory property is non-null with a non-empty name; zero credentials needed | Low | High |
| 8 | Tests | No test exercises live `GetDirections` → `Navigation.Draw` wayfinding pipeline | Add a bundled-MVF fixture test (if binary fixture can be shipped); otherwise accept the gap and note it as credential-gated | High | High |
