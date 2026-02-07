# Generated Binding API Review

**Reviewer**: Senior .NET/C# Developer (Xamarin Native/Forms, .NET MAUI, iOS bindings background)
**Date**: February 2026
**Libraries Reviewed**: Nuke (image loading), BlinkID (document scanning), Lottie (animation)
**Perspective**: Consuming these bindings in a .NET iOS application, comparing against native Swift documentation and prior Objective-C binding experience

---

## Executive Summary

These bindings represent genuinely impressive technical work. Getting Swift interop to work at all is a significant achievement, and the breadth of coverage across three very different real-world libraries is notable. That said, the developer experience when consuming these bindings has serious rough edges. A .NET developer picking these up for the first time would face a steep learning curve, not because Swift concepts are hard, but because the C# API surface exposes too many interop implementation details and deviates from .NET conventions in ways that create constant friction.

**Overall Grade: C+**
- Technical achievement: A
- API discoverability: D
- .NET idiom compliance: D+
- Type safety: B-
- Usability for a "just make it work" developer: C-

---

## Issue #1: The `Init()` Method Pattern (Critical)

**Impact: High — Breaks fundamental C# expectations**

Swift initializers (`init`) are mapped as instance methods that return a new instance, rather than as C# constructors. This is deeply confusing:

```csharp
// What you'd expect (C#):
var cache = new ImageCache(costLimit: 1024, countLimit: 100);

// What you get:
var cache = new ImageCache(); // creates... what? A zero/empty object?
cache = cache.Init(costLimit, countLimit); // THEN you initialize it?
```

This pattern appears everywhere:

```csharp
// Nuke
public unsafe Swift.Nuke.ImageCache Init(System.IntPtr costLimit, System.IntPtr countLimit)
public unsafe Swift.Nuke.ImagePrefetcher Init(pipeline, destination, maxConcurrentRequestCount)
public unsafe Swift.Nuke.DataCache Init(string name, Func<...> filenameGenerator)

// Lottie
public unsafe Swift.Lottie.AnimatedButton Init(LottieAnimation? animation, LottieConfiguration configuration)
public unsafe Swift.Lottie.ColorValueProvider Init(Func<System.Double, LottieColor> block)
public unsafe Swift.Lottie.FloatValueProvider Init(System.Double arg0)
```

As a .NET developer, I have no idea what the default constructor creates, whether the object returned by `Init()` is the same object or a new one, or whether I need to dispose the original. This is an instance method on an uninitialized object that returns a *different* object. Nothing about this maps to any C# pattern.

**The rule is simple**:

| Swift | C# |
|-------|-----|
| `init(costLimit: Int, countLimit: Int)` | `public ImageCache(nint costLimit, nint countLimit)` — a real constructor |
| `init?(rawValue: String)` (failable) | `public static bool TryCreate(string rawValue, out Priority result)` — follows `TryParse` convention (`out T`, not `out T?`; set `default` on failure) |
| Static factory in Swift (e.g., `Animal.createAnimal(...)`) | `public static Animal CreateAnimal(...)` — mirrors the Swift API shape |

Only use static `Create()` / `TryCreate()` when Swift itself uses a factory or failable pattern. Don't invent factories for what Swift exposes as plain initializers. The generator is currently transliterating Swift's `init` keyword literally instead of mapping the *concept* — Swift `init` **is** a constructor.

**Verdict**: This single issue would make me hesitate to adopt the bindings. Every Swift type I look at, I have to wonder "how do I actually construct this?"

---

## Issue #2: `SwiftString` vs `string` Inconsistency (Critical)

**Impact: High — Constant type juggling**

Properties return `Swift.SwiftString` while methods return `string`. This is documented as intentional (properties use accessor types, methods get type conversion), but as a consumer it's maddening:

```csharp
// Properties return SwiftString:
public Swift.SwiftString RawValue { get; }       // BlinkID enums
public Swift.SwiftString Name { get; }            // Lottie ImageAsset
public Swift.SwiftString Directory { get; }       // Lottie ImageAsset
public Swift.SwiftString Description { get; }     // Nuke ImageRequest
public Swift.SwiftString Identifier { get; }      // Nuke ISwiftImageProcessing

// Methods return string:
public unsafe string GetSessionId()               // BlinkID
```

So to use a property value in normal C# code, I always need `.ToString()`:

```csharp
var name = asset.Name.ToString();  // Why can't this just be string?
var desc = request.Description.ToString(); // Every. Single. Time.
```

For someone coming from Objective-C bindings where `NSString` was seamlessly bridged to `string`, this is a regression. The old Xamarin bindings got this right — `NSString` properties just gave you `string`. Here I'm constantly aware that I'm crossing a boundary.

**Verdict**: Properties should return `string` for string-typed Swift properties, just like methods do. The consumer shouldn't care about the marshalling strategy.

---

## Issue #3: `Swift.SwiftOptional<T>` Instead of Nullable `T?` (Major)

**Impact: Medium-High — Breaks standard null patterns**

Swift optionals should map to C# nullable types. Instead, we get a custom wrapper:

```csharp
// What you'd expect:
public UIImage? Image { get; }
public double? Ttl { get; }
public NSUrlResponse? UrlResponse { get; }

// What you get:
public Swift.SwiftOptional<Swift.Data> Data { get; }
public Swift.SwiftOptional<System.Double> Ttl { get; }
public Swift.SwiftOptional<Foundation.NSUrlResponse> UrlResponse { get; }
public Swift.SwiftOptional<Swift.Nuke.ImageResponse.CacheType> CacheTypeValue { get; }
public Swift.SwiftOptional<Swift.SwiftString> ImageId { get; }
```

Now I need to learn the `SwiftOptional<T>` API to do basic null checks. Can I do `if (ttl != null)`? Do I need `.HasValue`? `.Value`? Is it like `Nullable<T>`? The native Swift documentation says "this property is optional" which in C# means nullable — but I can't use standard C# null patterns.

This affects BlinkID heavily with date fields:

```csharp
public Swift.SwiftOptional<System.IntPtr> Day { get; }   // DateResult
public Swift.SwiftOptional<System.IntPtr> Month { get; }
public Swift.SwiftOptional<System.IntPtr> Year { get; }
```

**Verdict**: Optional reference types should be `T?`. Optional value types should be `T?` (Nullable<T>). The wrapping should be invisible to the consumer.

---

## Issue #4: `System.IntPtr` for Integer Types (Major)

**Impact: Medium-High — Type safety regression**

Swift's `Int` (which is platform-sized, like `nint`) is mapped to `System.IntPtr` in many places where it should be `int`, `long`, or `nint`:

```csharp
// BlinkID:
public System.IntPtr RawValue { get; }      // On multiple enum types
public System.IntPtr HashValue { get; }      // On hashable types
public System.IntPtr GetSessionNumber()      // Returns a session number

// Nuke:
public System.IntPtr CostLimit { get; }       // ImageCache — a byte count
public System.IntPtr CountLimit { get; }      // ImageCache — an item count
public System.IntPtr TotalCount { get; }      // DataCache
public System.IntPtr TotalSize { get; }       // DataCache
public unsafe ImagePrefetcher Init(pipeline, destination, System.IntPtr maxConcurrentRequestCount)
```

`IntPtr` in .NET means "a pointer or a platform-sized integer used in interop". When I see `IntPtr CostLimit`, my first thought is "is this a pointer to something?" It's not — it's a count of bytes. This should be `long` or `nint`.

Constructing an `ImageCache` requires me to write:

```csharp
var cache = new ImageCache();
cache = cache.Init((IntPtr)1024 * 1024, (IntPtr)100); // Cast int to IntPtr? Really?
```

Compare to what the Swift API looks like: `ImageCache(costLimit: 1024 * 1024, countLimit: 100)` — clean integer parameters.

**Verdict**: Swift `Int` should map to `nint` or `long`, not `IntPtr`. `IntPtr` should be reserved for actual pointers.

---

## Issue #5: Enums Are Classes, Not Enums (Major)

**Impact: Medium — Usable but non-idiomatic**

Swift enums are mapped as C# classes with a nested `CaseTag` enum. This means you can't use them in switch statements naturally:

```csharp
// Swift (native):
switch scanningStatus {
case .scanningSideInProgress: ...
case .sideScanned: ...
case .documentScanned: ...
}

// C# (what you'd expect with a real enum):
switch (scanningStatus)
{
    case ScanningStatus.ScanningSideInProgress: ...
    case ScanningStatus.SideScanned: ...
    case ScanningStatus.DocumentScanned: ...
}

// C# (what you actually get):
switch (scanningStatus.Tag) // Need to access .Tag first
{
    case ScanningStatus.CaseTag.ScanningSideInProgress: ... // Nested CaseTag enum
    case ScanningStatus.CaseTag.SideScanned: ...
    case ScanningStatus.CaseTag.DocumentScanned: ...
}
```

I understand the technical reason — Swift enums can have associated values, so they can't always be C# enums. But for simple enums with no associated values (like `ScanningMode`, `ScanningStatus`, `DocumentSide`), these should generate as actual C# `enum` types.

The current approach also means every enum case allocation goes through native memory:

```csharp
public static ScanningMode Single
{
    get
    {
        var result = new ScanningMode();
        var metadata = SwiftObjectHelper<ScanningMode>.GetTypeMetadata();
        IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
        metadata.ValueWitnessTable->DestructiveInjectEnumTag((void*)buffer, (uint)0, metadata);
        result._payload = new SwiftSafeHandle<ScanningMode>(buffer);
        return result;
    }
}
```

That's a heap allocation for what should be a constant integer value. In a tight loop or frequent comparison, this adds up.

**Verdict**: Simple enums (no associated values) should be real C# enums. Complex enums (with associated values) using the class + CaseTag + TryGet pattern are acceptable — that pattern actually maps reasonably well to discriminated unions.

---

## Issue #6: `ExistentialContainer` in the Public API (Major)

**Impact: Medium-High — Completely opaque to consumers**

Several properties and method parameters expose `Swift.Runtime.ExistentialContainer1` directly:

```csharp
// Lottie LottieAnimationView/LottieAnimationLayer:
public Swift.Runtime.ExistentialContainer1 ImageProvider { get; set; }
public Swift.Runtime.ExistentialContainer1 TextProvider { get; set; }
public Swift.Runtime.ExistentialContainer1 FontProvider { get; set; }

// Nuke ImagePipeline:
public unsafe ImagePipeline Init(Configuration configuration, ExistentialContainer1? _delegate)

// Nuke ImagePipeline.Error:
public static Error DataLoadingFailed(Swift.Runtime.ExistentialContainer1 error)
```

As a .NET developer, I have no idea what an `ExistentialContainer1` is. I can't construct one. I can't figure out what type goes in it. I can't even Google it because it's an internal Swift ABI concept.

For LottieAnimationView, I want to set an image provider. The Swift API says `imageProvider: AnimationImageProvider`. In C#, I'd expect the property to be typed as `ISwiftAnimationImageProvider` — the interface already exists in the bindings! Instead I get an opaque container type with no indication of what protocol it wraps.

**Verdict**: These should be typed as their corresponding protocol interface (e.g., `ISwiftAnimationImageProvider`) or, if that's not possible, they should be hidden and replaced with helper methods that accept the interface type.

---

## Issue #7: `AnyType` Fallback (Moderate)

**Impact: Medium — Type information lost**

When the type system can't resolve a Swift type, it falls back to `Swift.AnyType`:

```csharp
// Nuke ISwiftImageProcessing:
Swift.AnyType HashableIdentifier { get; }

// Nuke ISwiftImagePipelineDelegate:
public Swift.AnyType? ImageDecoder(ImageDecodingContext _for, ImagePipeline pipeline)
public Swift.AnyType? ImageCache(ImageRequest _for, ImagePipeline pipeline)
public Swift.AnyType? DataCache(ImageRequest _for, ImagePipeline pipeline)

// Lottie ISwiftAnimationFontProvider:
Swift.AnyType? FontFor(string family, System.Double size);

// Lottie ISwiftAnimationImageProvider:
Swift.AnyType ContentsGravity(ImageAsset _for);
```

`AnyType` tells me nothing. The Swift docs for `AnimationImageProvider.contentsGravity` says it returns `CALayerContentsGravity` — a well-known type. The `AnimationFontProvider.fontFor` returns `CTFont?`. These are concrete types that the binding system couldn't resolve, but the consumer has no way to use the return value meaningfully.

**Verdict**: When a type can't be resolved, the binding should at minimum include a comment or attribute saying what the original Swift type was, so the developer can cast or work around it. Something like `[OriginalSwiftType("CoreText.CTFont")]`.

---

## Issue #8: Parameter Naming (Moderate)

**Impact: Medium — Reduces API readability**

Many parameters use placeholder names like `arg0`, `arg1` or carry Swift's parameter label conventions awkwardly:

```csharp
// Unnamed parameters:
UIKit.UIImage? Process(UIKit.UIImage arg0);                     // What is arg0?
public unsafe void StoreData(Foundation.NSData arg0, string _for)  // What is arg0?
public unsafe UserInfoKey(string arg0)                           // What is arg0?

// Underscore-prefixed labels from Swift:
public unsafe Swift.Data? CachedData(string _for)    // "_for" what?
public unsafe void RemoveData(string _for)
public unsafe void Flush(string _for)
public unsafe Swift.URL? Url(string _for)

// Confusing operator parameter names:
public static Boolean operator ==(ImageTask arg0, ImageTask arg1)  // Should be left, right
```

In Swift, `_for` is an external parameter label: `cachedData(for key: String)`. The binding is exposing the external label as the C# parameter name, which looks wrong. It should be `key` (the internal name) or something meaningful like `cacheKey`.

The `arg0`/`arg1` naming means IntelliSense gives you zero guidance on what to pass. Compare to how Objective-C bindings handle this: parameters always have meaningful names derived from the Obj-C selector.

**Verdict**: Parameters should use their internal Swift names where available. `arg0`/`arg1` should never appear in a public API. The Swift ABI JSON contains both external and internal parameter labels — use the internal ones.

---

## Issue #9: `Payload` Property Leaking Everywhere (Moderate)

**Impact: Medium — Implementation detail exposure**

Every single type exposes a `Payload` property of type `SwiftSafeHandle<T>`:

```csharp
public SwiftSafeHandle<ImageCache> Payload => _payload;
public SwiftSafeHandle<LottieAnimation> Payload => _payload;
public SwiftSafeHandle<DocumentType> Payload => _payload;
```

And the test app shows disposal via `Payload`:

```csharp
request.Payload.Dispose();
priority.Payload.Dispose();
```

This is an interop implementation detail. The consumer shouldn't need to know about payloads and safe handles. The types should implement `IDisposable` directly:

```csharp
// What you'd expect:
using var request = ImageRequestFactory.FromUrlString("https://example.com/image.jpg");
// or
request.Dispose();

// What you get:
request.Payload.Dispose(); // Why Payload? Why not the object itself?
```

**Verdict**: Types should implement `IDisposable` and delegate to the payload internally. `Payload` should be `internal` or at least not the primary disposal mechanism.

---

## Issue #10: Throwing `InvalidOperationException` from `Equals` / `GetHashCode` (Moderate)

**Impact: Medium — Runtime bombs**

Types that don't implement Swift's `Equatable` throw from `Equals()` and `GetHashCode()`:

```csharp
public override bool Equals(object? obj)
{
    throw new InvalidOperationException(
        "Type ImageAsset does not implement Swift's Equatable protocol, " +
        "so equality comparison is not supported.");
}

public override int GetHashCode()
{
    throw new InvalidOperationException(
        "Type ImageAsset does not implement Swift's Equatable protocol, " +
        "so GetHashCode() is not supported.");
}
```

This is a landmine. Putting objects in a `HashSet`, `Dictionary`, or any LINQ `.Distinct()` call will throw at runtime. Even a debugger's variable display might call `GetHashCode()`. The `==` and `!=` operators also throw, so `if (a == b)` explodes.

Reference equality (`object.ReferenceEquals`) is always valid. These overrides should either use reference equality as a fallback or not override at all (the base `object` implementations are fine).

**Verdict**: Don't override `Equals`/`GetHashCode`/operators to throw. Either implement them (reference equality for classes, value equality for structs) or leave the base implementation alone.

---

## Issue #11: Property Name Suffixes (Minor)

**Impact: Low — Clutters autocomplete**

Several properties have unnecessary suffixes that differ from their Swift names:

```csharp
// What Swift calls it → What the binding calls it
configuration → ConfigurationValue
cache → CacheValue
priority → PriorityValue
state → StateValue
cacheType → CacheTypeValue
cancelBehavior → CancelBehaviorValue
```

I assume these suffixes exist to avoid name collisions with the nested type of the same name (e.g., `ImagePipeline.Configuration` the type vs `ConfigurationValue` the property). But in C# the convention for this is to use the type as-is — the compiler can disambiguate between `pipeline.Configuration` (property access) and `ImagePipeline.Configuration` (type reference).

**Verdict**: Use the natural property name without the `Value` suffix where possible. If there's genuinely a collision, prefer the property name (it's what developers use most) and give the type a different name, not the other way around.

---

## Issue #12: `ISwift*` Interface Naming (Minor)

**Impact: Low — But worth noting**

Protocol interfaces use an `ISwift` prefix:

```csharp
ISwiftImageProcessing
ISwiftImageEncoding
ISwiftDataLoading
ISwiftCancellable
ISwiftAnimationFontProvider
ISwiftAnimationImageProvider
```

The `Swift` in the name is redundant — everything in these bindings is from Swift. In Objective-C bindings, we didn't prefix interfaces with `IObjC`. The standard .NET convention is just `I` + name:

```csharp
IImageProcessing
IImageEncoding
IDataLoading
ICancellable
IAnimationFontProvider
```

This is cosmetic, but it adds noise to every protocol-typed parameter and property.

**Verdict**: Drop the `Swift` from interface names. Just use `I` + the protocol name.

---

## What Works Well

It's not all negative. Several things are done well:

### 1. Protocol Proxy Pattern
The proxy classes for implementing Swift protocols from C# are well-designed. Being able to write `class MyProcessor : ISwiftImageProcessing` and pass it to Swift code via `new ImageProcessingProxy(myProcessor)` is clean and functional.

### 2. Enum Associated Values (TryGet Pattern)
For enums with associated values, the `TryGet*` methods follow .NET conventions nicely:

```csharp
if (error.TryGetDataLoadingFailed(out var loadError))
{
    // Handle the specific case
}
```

This mirrors `Dictionary.TryGetValue` and other standard .NET patterns. It's the right approach.

### 3. Async/Await Mapping
Swift async methods mapping to C# `Task<T>` and `IAsyncEnumerable<T>` is exactly right:

```csharp
var image = await pipeline.Image(request);

await foreach (var progress in task.ProgressValue)
{
    Console.WriteLine($"Downloaded: {progress.Fraction:P0}");
}
```

This is idiomatic C# and matches how developers expect async to work.

### 4. Nested Type Organization
Types like `ImagePipeline.Error`, `ImageRequest.Priority`, `ImageRequest.Options`, and `ImageTask.Progress` mirror Swift's nested type organization. This is natural in both languages and works well.

### 5. Operator Overloading
Where implemented, `==`, `!=`, and `IEquatable<T>` work as expected. The `ImageTask`, `ImageCacheKey`, and `AnimationKeypath` equality implementations are correct and idiomatic.

### 6. OptionSet Mapping
`ImageRequest.Options` with static properties like `DisableMemoryCacheReads` and `DisableDiskCache` follows the same pattern as .NET's `BindingFlags` or `RegexOptions`. The `RawValue` constructor for combining flags works.

### 7. Real-World Coverage
The fact that Nuke (49 types), BlinkID (96+ types), and Lottie (56+ types) all compile and work at runtime is itself an achievement. These aren't toy libraries.

---

## Comparison: ObjC Bindings vs Swift Bindings

| Aspect | ObjC Bindings (Xamarin) | Swift Bindings (This Project) |
|--------|------------------------|-------------------------------|
| String types | Transparent (`string` everywhere) | Mixed (`SwiftString` in properties, `string` in methods) |
| Optionals | `T?` | `SwiftOptional<T>` |
| Integers | `nint`, `int`, `long` | `IntPtr` in many places |
| Enums | C# `enum` | Classes with `CaseTag` |
| Constructors | Real C# constructors | `Init()` methods |
| Disposal | `NSObject.Dispose()` | `obj.Payload.Dispose()` |
| Parameter names | Derived from selector | `arg0`, `arg1`, `_for` |
| Type discoverability | IntelliSense-friendly | Requires documentation |

The ObjC bindings had decades of refinement and language-level support from Xamarin/Microsoft. These Swift bindings are starting from scratch with a harder problem (Swift's ABI is more complex than ObjC's runtime). But the bar has been set by the ObjC experience, and these bindings don't meet it yet for developer experience.

---

## Priority Recommendations

If I were prioritizing fixes to make these bindings production-ready for external developers:

1. **P0 — Constructors**: Replace `Init()` methods with real C# constructors. Use `TryCreate()` only for failable initializers (`init?`). Use static factories only when Swift itself uses a factory pattern.
2. **P0 — String unification**: Properties should return `string`, not `SwiftString`
3. **P1 — Nullable mapping**: `SwiftOptional<T>` should be invisible — use `T?`
4. **P1 — Integer types**: Map Swift `Int` to `nint` or `long`, not `IntPtr`
5. **P1 — IDisposable**: Types should implement `IDisposable`; hide `Payload`
6. **P2 — Simple enums**: Generate real C# `enum` types for enums without associated values
7. **P2 — Parameter names**: Use internal Swift parameter names; eliminate `arg0`/`arg1`
8. **P2 — ExistentialContainer**: Replace with typed protocol interfaces in public API
9. **P2 — Equals/GetHashCode**: Don't throw — use reference equality or don't override
10. **P3 — Property name suffixes**: Remove `Value` suffix from properties
11. **P3 — Interface naming**: Drop `Swift` from `ISwift*` prefix
12. **P3 — AnyType fallback**: Add original Swift type info as attribute or comment

---

## Additional DX Criteria

The following criteria extend the core issues above with structural guidance, measurable quality gates, and acceptance criteria. These define what "production-ready for external developers" looks like.

### 1. Bake Idiomatic C# Directly Into Generation

**Preferred approach**: Since this project is pre-release with no existing consumers, the generator itself should emit the idiomatic C# API as its primary output. There is no need for a separate facade layer — push the interop complexity down into `internal` implementation details at generation time.

Concretely:

- **Generated public API**: Constructors, `string`, `T?`, `nint`/`int`/`long`, `IDisposable`, clean parameter names. This is what consumers see.
- **Generated internals**: `SwiftSafeHandle`, `ExistentialContainer`, `SwiftString`, P/Invoke marshalling, witness tables. All `internal` — invisible to the consumer.

```
Consumer Code
    ↓ uses
Generated Public API (idiomatic C#, constructors, string, T?, IDisposable)
    ↓ internally delegates to
Generated Interop Internals (SwiftSafeHandle, P/Invoke, ExistentialContainer)
    ↓ calls
Swift Framework (.dylib)
```

This is a single generated layer with an internal architecture boundary, not two shipped layers. The generator emits both the public surface and the plumbing in one pass, but only the idiomatic types are `public`.

**Fallback option**: Where generator gaps still exist and a specific type can't yet emit clean public API, use a temporary hand-written shim in an `*.Extensions.cs` file (like the existing `ImageRequestFactory`). These shims are explicitly temporary — each one is a tracking issue to be deleted when the generator catches up.

> **Note**: A separate facade layer (generated or hand-authored) was considered as an alternative. It would decouple "make interop work" from "make API nice" and allow incremental progress. However, since there are no consumers to break, the right technical decision is to get the generated output right from the start rather than institutionalizing a wrapper layer that becomes permanent technical debt.

### 2. Exception Mapping for Swift `throws`

Swift throwing methods should map to structured .NET exceptions:

| Swift | C# |
|-------|-----|
| `throws` (untyped) | `SwiftException` with `Message` |
| `throws SomeError` (typed) | `SwiftException<SomeError>` or a mapped exception type |
| Error domain/code/userInfo (NSError-backed) | Preserved as properties on the exception |

Current behavior wraps everything in `SwiftRuntimeException("Call to Swift method X failed.")` — this loses all diagnostic information. The error enum's case and associated values should be accessible from the catch block:

```csharp
// Goal:
try { session.Reset(); }
catch (SwiftException<SessionError> ex)
{
    Console.WriteLine($"Error: {ex.SwiftError.Tag}"); // Specific case
    Console.WriteLine($"Details: {ex.Message}");       // Human-readable
}
```

### 3. Cancellation Token Support

All async methods should accept an optional `CancellationToken`:

```csharp
// Current:
var image = await pipeline.Image(request);

// Goal:
var image = await pipeline.Image(request, cancellationToken);
```

Swift's structured concurrency supports task cancellation. The binding should wire `CancellationToken.Register()` to Swift's `Task.cancel()` or the `ImageTask.cancel()` equivalent. Without this, .NET developers lose a core async pattern they rely on for timeouts, user-initiated cancellation, and resource cleanup.

### 4. Default Parameters and Overload Shaping

Swift methods with default parameter values should produce C# overloads or optional parameters:

```csharp
// Swift:
// func loadImage(url: URL, priority: Priority = .normal, processors: [ImageProcessing] = [])

// Goal (overloads):
public ImageTask LoadImage(URL url);
public ImageTask LoadImage(URL url, Priority priority);
public ImageTask LoadImage(URL url, Priority priority, IReadOnlyList<IImageProcessing> processors);

// Or (optional parameters):
public ImageTask LoadImage(URL url, Priority? priority = null, IReadOnlyList<IImageProcessing>? processors = null);
```

Currently, the consumer must provide every parameter even when Swift would use defaults. This is a major discoverability hit — the simplest way to call a method is hidden behind the most complex overload.

### 5. Collection Type Mapping

Swift collection types should map to standard .NET collection interfaces:

| Swift | C# (public API) | Notes |
|-------|-----------------|-------|
| `Array<T>` (read) | `IReadOnlyList<T>` | Indexable, countable |
| `Array<T>` (read/write) | `IList<T>` or custom `SwiftArray<T> : IList<T>` | Must support mutation |
| `Dictionary<K,V>` | `IReadOnlyDictionary<K,V>` | Standard lookup |
| `Set<T>` | `IReadOnlySet<T>` | .NET 7+ |
| Return arrays | `IReadOnlyList<T>` | Consumers shouldn't need `SwiftArray<T>` |

Currently, `Swift.SwiftArray<T>` appears in many public signatures. Consumers can't use LINQ, can't pass `List<T>`, and can't use standard collection patterns without conversion. At minimum, `SwiftArray<T>` should implement `IList<T>` and `IReadOnlyList<T>`.

### 6. Ownership and Lifetime Rules

Define and document explicit rules:

- **Who owns the native object?** The C# wrapper owns it after construction. Disposal releases the Swift reference.
- **When is disposal required?** All types wrapping Swift objects require disposal. Failure to dispose leaks native memory.
- **Are objects thread-safe?** Follow Swift's rules: value types (structs) are copyable, reference types (classes) are reference-counted but not inherently thread-safe, actors are thread-safe.
- **UIKit types**: Must be accessed from the main thread only, same as in Xamarin/ObjC bindings.
- **Double-dispose safety**: Calling `Dispose()` twice must not crash (standard .NET convention).

These rules should appear in XML doc comments on the base `ISwiftObject` interface and in a "Getting Started" guide.

### 7. No Interop Types in Public API (Hard Rule)

The following types must never appear in consumer-facing signatures:

| Forbidden in Public API | Replace With |
|------------------------|-------------|
| `ExistentialContainer0`, `ExistentialContainer1` | Typed protocol interface (`IImageProcessing`, etc.) |
| `SwiftSafeHandle<T>` | `IDisposable` on the type itself |
| `TypeMetadata` | Nothing — internal only |
| `ValueWitnessTable` | Nothing — internal only |
| `SwiftIndirectResult` | Nothing — internal only |
| `EveryProtocol` | Nothing — internal only |
| `SwiftSelf` | Nothing — internal only |
| `SwiftError` | Mapped to .NET exception |

If a type can't be mapped, the member should be marked `[EditorBrowsable(EditorBrowsableState.Never)]` at minimum, with a doc comment explaining the limitation and the original Swift type.

### 8. Nullable Reference Annotations

All generated code should use C# nullable reference type annotations (`#nullable enable`) and match Swift's optionality:

```csharp
// Swift non-optional → C# non-null
public string GetSessionId()          // Guaranteed non-null

// Swift optional → C# nullable
public string? GetOptionalName()      // May be null

// Swift non-optional parameter → C# non-null parameter
public void Process(UIImage image)    // ArgumentNullException if null

// Swift optional parameter → C# nullable parameter
public void Load(URL? url)            // null is valid
```

This enables the compiler to catch null misuse at build time. The binding report should flag any member where optionality couldn't be determined.

### 9. Naming and Shape Consistency Checklist

Apply these rules as a lint pass over generated output. Any violation is a bug:

- [ ] No `arg0`, `arg1`, etc. in public parameter names
- [ ] No `_for`, `_with`, etc. — use the internal Swift parameter name
- [ ] No `Value` suffix on properties unless genuinely needed for disambiguation
- [ ] Async methods end with `Async` (e.g., `LoadImageAsync`)
- [ ] Pascal case on all public members (methods, properties, parameters)
- [ ] No `System.` prefix in parameter types in doc comments
- [ ] Boolean properties use `Is`/`Has`/`Can` prefix where Swift does
- [ ] Events use .NET event pattern (`event EventHandler<T>`) for Swift closures that are notification-style callbacks

### 10. Versioning and Breaking Change Strategy

Since generated bindings evolve as the generator improves:

- **Internal interop changes**: May break between generator versions. Documented in release notes.
- **Public API changes**: Follow semantic versioning. Breaking changes require a major version bump.
- **Generated file headers**: Include generator version so consumers know what produced the output.
- **Upgrade guide**: Each generator release includes a migration guide if public API shapes changed.
- **Stability tiers**: Mark types/members as `[Experimental]`, `[Preview]`, or stable. Experimental APIs can change freely; stable APIs follow semver.

### 11. Golden Scenario Samples (Acceptance Criteria)

Each bound library must have a minimal end-to-end sample that compiles and runs. These are the acceptance test for API usability:

**Nuke — Load and display an image:**
```csharp
// This should "just work" with no interop knowledge
var pipeline = ImagePipeline.Shared;
var request = new ImageRequest("https://example.com/photo.jpg");
var image = await pipeline.LoadImageAsync(request);
imageView.Image = image;
request.Dispose();
```

**Lottie — Play an animation:**
```csharp
var animation = LottieAnimation.FromBundle("loading.json");
var animationView = new LottieAnimationView(animation);
animationView.LoopMode = LottieLoopMode.Loop;
animationView.Play();
view.AddSubview(animationView);
```

**BlinkID — Scan a document:**
```csharp
var settings = new BlinkIDSessionSettings(
    inputImageSource: InputImageSource.Camera,
    scanningMode: ScanningMode.Automatic);
var session = new BlinkIDSession(settings);
var result = session.Process(inputImage);
if (result.Status == ScanningStatus.DocumentScanned)
{
    var scanResult = session.GetResult();
    Console.WriteLine($"Name: {scanResult.FullName}");
}
```

If any of these samples require `SwiftString`, `SwiftOptional`, `ExistentialContainer`, `Payload.Dispose()`, or `IntPtr` casts, the generated public API is incomplete.

### 12. Measurable Quality Scorecard

Define quantitative gates that must pass before a binding is considered "release-ready":

| Metric | Gate | Current (Estimated) |
|--------|------|---------------------|
| Public `IntPtr` for non-pointer semantics | 0 | ~30+ across all libraries |
| Public `SwiftOptional<T>` | 0 | ~20+ |
| Public `SwiftString` properties | 0 | ~40+ |
| Public `ExistentialContainer*` | 0 | ~15+ |
| `Init()` instance methods (should be ctors) | 0 | ~15+ |
| `arg0`/`arg1` parameter names | 0 | ~20+ |
| Types missing `IDisposable` | 0 | All types |
| `Equals`/`GetHashCode` that throw | 0 | ~80% of types |
| Public `Payload` property | 0 (should be internal) | All types |
| Missing nullable annotations | 0 | All files |
| Golden scenarios compile without interop types | 3/3 | 0/3 |

Track these metrics per generator release. A dashboard or CI check that computes these counts from generated output would enforce quality over time.

---

## Implementation Waves

The priority recommendations (P0-P3) define *what* to fix. This section defines *when* and *in what order*, with explicit dependencies and a definition of done (DoD) for each wave. Later waves depend on earlier ones — don't start a wave until the previous one's DoD is met.

### Wave 1: Type Foundation (P0)

**Goal**: The most fundamental type-mapping issues. Every subsequent wave builds on these being correct.

**Items**:
1. **Constructors** — Swift `init(...)` becomes a real C# constructor. Swift `init?(...)` (failable) becomes `static bool TryCreate(..., out T result)` (mirrors `TryParse` — `out T`, not `out T?`; set `default` on failure). Static factories only when Swift itself uses a factory pattern — don't invent them.
2. **String unification** — Properties emit `string`, not `SwiftString`. Marshalling to/from `SwiftString` is internal only.
3. **IDisposable** — All types implementing `ISwiftObject` also implement `IDisposable`. `Payload` becomes `internal`.

**Dependencies**: None — these are leaf changes in the emitter.

**DoD**:
- [ ] Zero `Init()` instance methods in generated public API
- [ ] Zero `SwiftString` in public property return types
- [ ] Zero types missing `IDisposable`
- [ ] Zero public `Payload` properties
- [ ] All three golden scenarios (Nuke, Lottie, BlinkID) use constructors and `Dispose()` directly
- [ ] All existing unit tests pass
- [ ] TestFramework coverage: no regressions (0 degraded)

### Wave 2: Type Safety (P1)

**Goal**: Eliminate the remaining non-idiomatic types from the public API.

**Items**:
1. **Nullable mapping** — `SwiftOptional<T>` becomes `T?` in public signatures. Marshalling is internal.
2. **Integer types** — Swift `Int` maps to `nint` (or `long` where platform-sized semantics don't apply). `IntPtr` reserved for actual pointers.
3. **Equals/GetHashCode** — Types without Swift `Equatable` use reference equality (classes) or don't override (structs). Remove the throwing overrides.

**Dependencies**: Wave 1 (IDisposable must be in place before nullable optionals work cleanly with `using` patterns).

**DoD**:
- [ ] Zero `SwiftOptional<T>` in public API
- [ ] Zero `IntPtr` for non-pointer semantics in public API
- [ ] Zero `Equals`/`GetHashCode` implementations that throw
- [ ] `#nullable enable` in all generated files
- [ ] Nullable annotations match Swift optionality
- [ ] All existing unit tests pass
- [ ] TestFramework coverage: no regressions

### Wave 3: API Shape (P2)

**Goal**: Clean up naming, parameter conventions, and interop type leakage.

**Items**:
1. **Simple enums** — Enums without associated values generate as C# `enum` types (not classes with `CaseTag`)
2. **Parameter names** — Use internal Swift parameter names. Zero `arg0`/`arg1`. Remove `_for`/`_with` prefixes.
3. **ExistentialContainer removal** — Replace with typed protocol interfaces in all public signatures
4. **Default parameters / overloads** — Swift methods with defaults produce C# overloads (simplest overload first)

**Dependencies**: Wave 2 (nullable mapping must be done before overload shaping, since optional parameters depend on `T?`).

**DoD**:
- [ ] Zero `arg0`/`arg1` in public parameter names
- [ ] Zero `ExistentialContainer*` in public API
- [ ] Simple enums (no associated values) are C# `enum` types
- [ ] High-frequency methods have overloads matching Swift's defaulted parameters
- [ ] Golden scenarios compile without any interop-specific types
- [ ] All existing unit tests pass
- [ ] TestFramework coverage: no regressions

### Wave 4: Polish (P3)

**Goal**: Cosmetic and convention alignment. These don't block usability but raise the quality bar.

**Items**:
1. **Property name suffixes** — Remove `Value` suffix (`ConfigurationValue` → `Configuration`)
2. **Interface naming** — `ISwiftImageProcessing` → `IImageProcessing`
3. **AnyType fallback** — Add `[OriginalSwiftType("CoreText.CTFont")]` attribute when type resolution falls back to `AnyType`
4. **Collection interfaces** — `SwiftArray<T>` implements `IReadOnlyList<T>` and `IList<T>`
5. **Async naming** — Async methods in public API end with `Async`

**Dependencies**: Wave 3 (naming changes should happen after structural changes to avoid churn).

**DoD**:
- [ ] Zero `Value`-suffixed property names (unless genuinely disambiguating)
- [ ] Zero `ISwift*` interface names — all use `I` + protocol name
- [ ] All `AnyType` returns have `[OriginalSwiftType]` attribute
- [ ] `SwiftArray<T>` implements `IReadOnlyList<T>`
- [ ] All scorecard metrics at gate values
- [ ] Golden scenarios are clean, idiomatic, and documentation-ready

### Cross-Cutting (All Waves)

These apply throughout, not in a specific wave:

- **Exception mapping** — Improve incrementally as throwing methods are touched. Typed `SwiftException<TError>` is the goal.
- **CancellationToken** — Add to async methods as they're modified in any wave. Not a blocker for any specific wave.
- **Ownership/lifetime docs** — Update XML doc comments as types are modified. Complete by end of Wave 2.
- **Versioning strategy** — Establish before Wave 1 ships to any external consumer. Pre-release, breaking changes are free.

### Wave Sequencing Diagram

```
Wave 1 (Type Foundation)
  Constructors, string, IDisposable
    │
    ▼
Wave 2 (Type Safety)
  Nullable, integers, Equals/GetHashCode
    │
    ▼
Wave 3 (API Shape)
  Enums, parameters, ExistentialContainer, overloads
    │
    ▼
Wave 4 (Polish)
  Naming, AnyType, collections, async naming
```

Each wave is a self-contained improvement. After each wave, regenerate all three library bindings (Nuke, Lottie, BlinkID) and verify the golden scenarios improve. The scorecard metrics should monotonically improve wave over wave.

---

## Bottom Line

These bindings are a technical triumph and a UX challenge. The underlying interop machinery is solid — ARC integration, protocol witness tables, async bridging, existential containers — all of this works. But the API surface that developers actually touch needs significant polish before it could be handed to a .NET developer who just wants to "use Nuke to load images" or "add Lottie animations to my app."

The gap isn't in capability — it's in abstraction. The interop layer is visible in too many places. A great binding makes you forget you're crossing a language boundary. These bindings remind you on every line.

That said, this is an experimental project solving one of the hardest problems in .NET mobile development. The foundation is here. With attention to the API surface issues documented above, these bindings could evolve into something that .NET iOS developers would genuinely want to use.
