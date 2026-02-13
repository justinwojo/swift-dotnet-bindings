# Mappedin Binding Review

Generated from `Mappedin.xcframework` (iOS simulator slice) using the Swift binding generator in xcframework mode.

## Generation Summary

| Metric | Value |
|--------|-------|
| Types emitted | 120 (100% coverage) |
| Members emitted | 665 of 670 (99.3%) |
| Members skipped | 5 |
| Synthesized members | 634 |
| Output size | ~49,000 lines, 2.2 MB |
| Swift wrapper | Compiled successfully (73 broken wrappers stripped) |
| `[UnsupportedSwiftType]` annotations | ~4 (78 Encoder pruned (**fixed**); 3 MPINavigatable existential, 1 MPISearchResultCommon remain) |

### Skipped Members

| Type | Member | Reason |
|------|--------|--------|
| MPIDestinationSet | `destinations` (property) | Existential in bound generic (`any MPINavigatable`) |
| MPIDestinationSet | `init` (constructor) | Same existential issue |
| MPIMapView | `delegate` (property) | Unsupported accessor signature |
| MPIMapView | `mapClickDelegate` (property) | Unsupported accessor signature |
| MPIMapView | `webView` (method) | Generic constraint unsatisfiable in C# |

## Analysis: Xamarin/.NET Developer Perspective

This review evaluates the generated binding from the perspective of a .NET mobile developer experienced with Objective Sharpie ObjC bindings but not familiar with Swift internals.

---

### What Feels Familiar and Good

#### Namespace and Naming (Grade: A)

The `Swift.Mappedin` namespace is clean and flat. All 120 types live under one namespace with no fragmentation. Property and method names are PascalCased per .NET convention (`VenueData`, `SetMap`, `BlueDotManager`), not Swift's camelCase. Factory methods like `FromVenueResponse()` are idiomatic C#.

```csharp
// Properties feel natural
public IReadOnlyList<MPIMap> Maps { get; set; }
public MPIVenue Venue { get; set; }
public string? ClientId { get; set; }

// Factory methods are idiomatic
public static MPIData? FromVenueResponse(MPIVenueResponse response)
public static MPIData? FromVenueResponse(string response)
```

#### Type Projections for Properties (Grade: A-)

String properties correctly project as `string`, arrays as `IReadOnlyList<T>`, and optionals as nullable reference types. The file starts with `#nullable enable`.

```csharp
public string Name { get; }                              // Swift String -> C# string
public string? ExternalId { get; }                       // Swift String? -> C# string?
public IReadOnlyList<MPIMap> Maps { get; set; }          // Swift [MPIMap] -> IReadOnlyList
public IReadOnlyList<MPIOpeningHours>? OperationalHours  // Swift [MPIOpeningHours]? -> nullable list
```

#### Protocol-to-Interface Mapping (Grade: B+)

Swift protocols map to clean C# interfaces. The proxy machinery (witness tables, existential containers) is hidden behind the scenes.

```csharp
public interface IMPIMapViewDelegate
{
    void OnDataLoaded(MPIData data);
    void OnFirstMapLoaded();
    void OnMapChanged(MPIMap map);
    void OnPolygonClicked(MPIPolygon polygon);
    void OnNothingClicked();
    void OnBlueDotPositionUpdate(MPIBlueDotPositionUpdate update);
    void OnBlueDotStateChange(MPIBlueDotStateChange stateChange);
    void OnStateChanged(MPIState state);
    void OnCameraChanged(MPICameraTransform cameraChange);
}

// Usage: implement the interface, wrap in a proxy, pass to Swift
var proxy = new MPIMapViewDelegateProxy(myImplementation);
```

This is directly analogous to how Objective Sharpie exposes ObjC protocols.

#### Default Parameter Expansion (Grade: B+)

Swift methods with default parameters generate multiple C# overloads, letting you call with only the parameters you need:

```csharp
ShowVenue(venueResponse)
ShowVenue(venueResponse, showVenueOptions)
ShowVenue(venueResponse, showVenueOptions, errorCallback)
```

#### Equality (Grade: B)

Types conforming to Swift `Equatable` get proper `IEquatable<T>`, `==`/`!=` operators, and `Equals()` overrides. `GetHashCode()` is stubbed to `return 0` with a TODO comment (honest about the limitation, but makes dictionary/set usage O(n)).

---

### What Would Feel Weird or Surprising

#### 1. Enums Are Classes, Not `enum` Types (Grade: C)

This is the single biggest conceptual shock. A Xamarin developer expects:

```csharp
public enum MPIMarkerState { Hidden, Ghost, Normal, Uncertain }
```

What they get:

```csharp
public class MPIMarkerState : ISwiftObject
{
    public static MPIMarkerState Hidden { get; }    // allocates native memory per access
    public static MPIMarkerState Ghost { get; }
    public static MPIMarkerState Normal { get; }
    public static MPIMarkerState Uncertain { get; }

    public enum CaseTag : uint { Hidden = 0, Ghost = 1, Normal = 2, Uncertain = 3 }
    public CaseTag Tag { get; }         // calls into Swift metadata
    public nint RawValue { get; }

    public static MPIMarkerState? FromRawValue(long rawValue)
}
```

**Impact:**
- Cannot use `switch(state)` -- must use `switch(state.Tag)` with the nested `CaseTag` enum
- Each static property access allocates native memory and calls Swift
- ~~`SCREAMING_CASE` convention is un-.NET~~ Now PascalCase: `Hidden`, `CameraDirection`, `EasingMode` (**fixed**)
- 21 enum-as-class types in the binding: `MPIError`, `MPIVortexType`, `MPIActionType`, `MPIBearingType`, etc.

**Why this is correct:** The generator already emits C# `enum` for Swift enums that are `@frozen`, have no associated values, and use integral raw values (see `IsSimpleEnum` in `EnumDecl.cs`). None of the Mappedin enums are `@frozen` -- the library is built with `-enable-library-evolution` but doesn't freeze its enums. This is common for third-party SDKs. Without `@frozen`, the case layout can change between versions, so mapping to fixed C# `enum` integers would be unsafe. Additionally, enums like `MPIError` and `MPIVortexType` use `String` raw values, which have no C# `enum` equivalent. The class pattern is the correct projection here -- it just doesn't feel natural to a .NET developer.

#### 2. Callback Signatures Leak Swift Types (Grade: C-)

Many methods use callbacks with `SwiftOptional<SwiftString>` instead of `string?`:

```csharp
// What you see in the binding:
public void SetMap(MPIMap map, Action<Swift.SwiftOptional<Swift.SwiftString>>? completionCallback)

// What you'd expect from an ObjC Sharpie binding:
public void SetMap(MPIMap map, Action<string?>? completionCallback)
```

This pattern is pervasive across `MPICameraManager`, `MPIFloatingLabelManager`, `MPIMapView`, and other key classes. Inside your callback you must work with `SwiftOptional<SwiftString>` rather than plain `string?`.

The `MPISearchManager.Search` method is worse -- it returns `Action<SwiftArray<ExistentialContainer1>>`, completely opaque without understanding Swift existential containers.

#### 3. No `async`/`await` -- Only Callbacks (Grade: D)

There are zero `Task<T>`-returning methods in the entire 49K-line file. Everything is callback-based:

```csharp
// What you get:
mapView.GetDirections(to, from, accessible, (directions) => { /* ... */ });

// What a modern .NET developer expects:
var directions = await mapView.GetDirectionsAsync(to, from, accessible);
```

Every callback API would need to be manually wrapped in `TaskCompletionSource<T>` for async/await usage.

#### 4. No C# Events (Grade: C)

There is no `event` keyword anywhere in the file. In Xamarin ObjC bindings, delegate protocols typically surface as both an interface and a set of C# events via `[Wrap("WeakDelegate")]`. Here, you implement the `IMPIMapViewDelegate` interface and pass a proxy -- functional but less discoverable.

#### 5. ~~The `Encode(object encoder)` / `Constructor(object decoder)` Pattern~~ (Grade: ~~D~~ N/A) **FIXED**

~~Nearly every Codable type has these two members.~~ These are now pruned by `MemberEmissionValidator.IsSynthesizedCodableMember()`. The 78 `Encode` methods and matching `init(from: Decoder)` constructors are detected and skipped with `SkipReason.SynthesizedCodable`. They no longer appear in the generated binding.

#### 6. `_object` Parameter Name (Grade: C)

In `MPISearchManager.AddQuery`, a parameter is named `_object`:

```csharp
public void AddQuery(string query, MPICategory _object, float? weight, Action callback)
```

The underscore prefix is a Swift artifact (the external argument label was `_`, meaning "no label"). In C# this looks like a private field name.

#### 7. Nested Type Structure: `MPIOptions.Init` (Grade: C+)

The Mappedin SDK uses `MPIOptions.Init` as its initialization config:

```csharp
var initOptions = new MPIOptions.Init(
    clientId: "...", clientSecret: "...",
    venue: "...", perspective: "...",
    baseUrl: null, noAuth: false,
    headers: null, useBundle: false,
    emitAnalyticsEvents: false, useDraftData: false,
    language: null, things: ...
);
```

`.Init` as a class name reads like a method, not a type. A .NET developer would expect something like `MappedinConfiguration` or `MPIInitializationOptions`. This comes directly from the Swift source's `MPIOptions.Init` struct name.

#### 8. ~~`System.Single` Instead of `float`~~ (Grade: ~~B-~~ A) **FIXED**

~~Properties use the full CLR type name.~~ Now uses C# keyword aliases:

```csharp
public float Score { get; set; }
public bool Accessible { get; set; }
```

Previously emitted `System.Single` / `System.Boolean`. Now normalized to keyword aliases in `CSharpTypeName.FromNamespaceAndName()`.

#### 9. String Array Properties Reallocate on Every Access (Grade: C)

Optional string array properties use LINQ projection on every getter call:

```csharp
public IReadOnlyList<string>? Parents =>
    (Parents_Get().Case == SwiftOptionalCases.None
        ? (IReadOnlyList<string>?)null
        : Parents_Get().Some.Select(e => e.ToString()).ToList());
```

`.Select().ToList()` runs on each access with no caching. For large lists accessed in loops, this is a performance concern.

#### 10. Class Hierarchies Are Flattened (Grade: C+)

All 120 classes inherit only from `ISwiftObject` -- no class-to-class inheritance is visible:

```csharp
public class MPIMarkerManager : ISwiftObject
public class MPIVenue : ISwiftObject
public class MPILocation : ISwiftObject
```

Swift class hierarchies are completely flattened. Protocol conformance is tracked internally via `_protocolConformanceSymbols` dictionaries but isn't visible to consumers as base class relationships.

---

### Documentation (Grade: F initially, A- after fix)

The initial generation produced **zero doc comments from the native SDK**. Only generated infrastructure had XML comments. A developer browsing IntelliSense would see method signatures with no context.

**Root cause:** The generator was not passed `--symbolgraph`, and Mappedin doesn't ship pre-built symbol graph JSON in the xcframework (most libraries don't). However, the compiled `.swiftmodule` **does** contain the documentation -- `swift-symbolgraph-extract` can extract it:

```bash
xcrun swift-symbolgraph-extract \
  -module-name Mappedin \
  -target arm64-apple-ios15.0-simulator \
  -sdk "$(xcrun --sdk iphonesimulator --show-sdk-path)" \
  -F Mappedin.xcframework/ios-arm64_x86_64-simulator \
  -output-dir /tmp/symbolgraph \
  -minimum-access-level public
```

This produced **892 documented symbols out of 1070** -- the Mappedin SDK has extensive documentation. Re-running the generator with `--symbolgraph` produced **917 XML doc comments** including type summaries, parameter descriptions, remarks, and return value documentation:

```csharp
/// <summary>
/// <c>MPIData</c> represents the data received when loading a specific venue.
/// </summary>
/// <remarks>
/// Note: All core aspects of the venue, such as MPIMaps, MPILocation, MPIPolygon,
/// MPINode, MPIVortex, MPIMapGroup, can be accessed through MPIData.
/// </remarks>
public class MPIData : ISwiftObject

/// <summary>
/// Loads the map based on the options passed in <c>MPIMapView</c>
/// </summary>
/// <param name="options">The options to load the MPIMapView with</param>
/// <param name="showVenueOptions">The options to display the venue with</param>
/// <param name="useBundle">(experimental) Speed up subsequent map loading by caching locally</param>
/// <param name="errorCallback">Callback when loadVenue fails, contains an MPIError</param>
public void LoadVenue(...)
```

**Resolved:** The generator now automatically runs `swift-symbolgraph-extract` in xcframework mode when no `--symbolgraph` is provided, the same way it auto-generates TBD files from the dylib when none exist. The swiftmodule is always present in the xcframework. Enabled by default with `--no-docs` to opt out. The MSBuild SDK exposes `SwiftGenerateDocComments` (default: `true`).

---

## Summary Scorecard

| Aspect | Grade | Notes |
|--------|-------|-------|
| Naming conventions | A | PascalCase, clear method names |
| Type projections (properties) | A- | string, IReadOnlyList, nullable -- all correct |
| Type projections (callbacks) | C- | Leaks SwiftOptional/SwiftString into Action params |
| Enum usability | C+ | Classes instead of enums (correct for non-frozen); PascalCase now (**fixed**) |
| Async patterns | D | No Task/async -- callbacks only |
| Documentation | A- | 917 doc comments auto-extracted from swiftmodule (now automatic) |
| Protocol/delegate pattern | B+ | Clean interfaces, proxy pattern works |
| Memory management | B | SafeHandle correct, must manually Dispose |
| Constructor discoverability | B- | Codable `init(from: Decoder)` pruned (**fixed**); no parameterless ctors |
| Overall API discoverability | B | Clean surface with docs; Codable noise removed |

## Potential Improvements

These are not bugs -- they're opportunities to improve developer experience in future generator versions:

1. **Callback type projection**: Convert `Action<SwiftOptional<SwiftString>>` to `Action<string?>` by adding a trampoline layer that unwraps Swift types before invoking the user's delegate.

2. **Non-frozen simple enum projection**: The generator already emits C# `enum` for `@frozen` integral enums. Consider an opt-in mode that also projects non-frozen enums without associated values as C# `enum` (with a runtime fallback for unknown cases), accepting the risk that new cases could appear in library updates.

3. **Async wrappers**: For methods that take a single callback as their last parameter, optionally generate a `Task<T>`-returning async overload.

4. ~~**C# type aliases**: Use `float` instead of `System.Single`, `bool` instead of `System.Boolean`, etc.~~ **Done.**

5. **String array caching**: Cache the `.Select(e => e.ToString()).ToList()` result or use a lazy wrapper.

6. **Event generation**: For delegate protocol properties (like `delegate` and `mapClickDelegate`), generate C# events in addition to the interface approach.

7. **Parameter naming**: When Swift uses `_` as the external label, generate a more meaningful C# parameter name from the internal label rather than `_object`.

---

## Implemented Improvements

The following items from the "Potential Improvements" list above have been implemented:

- **Item 4 (C# type aliases):** `CSharpTypeName.FromNamespaceAndName()` now normalizes all `System.*` primitives to C# keyword aliases. Properties emit `float`, `bool`, `int`, etc. instead of `System.Single`, `System.Boolean`, `System.Int32`.

- **Item 5 from review ("Encode/Decoder pruning"):** Synthesized Codable members (`encode(to: Encoder)` and `init(from: Decoder)`) are now detected and pruned by `MemberEmissionValidator`. This eliminates the 78 useless `[UnsupportedSwiftType]` `Encode` methods and the unusable `object decoder` constructors. A new `SynthesizedCodable` skip reason is reported.

- **Enum PascalCase (from Item 1 "Enum usability"):** `NameProvider.ToPascalCase()` now detects `SCREAMING_CASE` and converts it to PascalCase. Enum cases like `HIDDEN` → `Hidden`, `CAMERA_DIRECTION` → `CameraDirection`. All inline `char.ToUpper` sites in the enum handler were also updated to route through `ToPascalCase`.
