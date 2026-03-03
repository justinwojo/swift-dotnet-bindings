# How Bindings Map

Side-by-side examples of Swift declarations and the C# they generate. These are real patterns from the test suite and validated libraries.

## Classes

```swift
// Swift
public class ImagePipeline {
    public static var shared: ImagePipeline { get }
    public var configuration: ImagePipeline.Configuration { get }

    public init(configuration: ImagePipeline.Configuration)
    public func loadImage(_ request: ImageRequest) async -> UIImage
}
```

```csharp
// Generated C#
public partial class ImagePipeline : ISwiftObject, IDisposable
{
    public static ImagePipeline Shared { get; }
    public ImagePipeline.Configuration Configuration { get; }

    public ImagePipeline(ImagePipeline.Configuration configuration) { ... }
    public Task<UIImage> LoadImageAsync(ImageRequest request, CancellationToken cancellationToken = default) { ... }
}
```

Classes use `SafeHandle`-based ARC. `retain` on creation, `release` on `Dispose()` or GC finalization.

---

## Structs

### Frozen structs → C# structs

```swift
// Swift
@frozen public struct CGPoint {
    public var x: Double
    public var y: Double
}
```

```csharp
// Generated C#
public partial struct CGPoint : ISwiftObject
{
    public double X { get; set; }
    public double Y { get; set; }
}
```

Frozen structs with only blittable fields become C# `struct` with matching memory layout.

### Non-frozen structs → C# classes

```swift
// Swift
public struct Configuration {
    public var maxConcurrentTasks: Int
    public var isProgressiveDecodingEnabled: Bool
}
```

```csharp
// Generated C#
public partial class Configuration : ISwiftObject, IDisposable
{
    public nint MaxConcurrentTasks { get; set; }
    public bool IsProgressiveDecodingEnabled { get; set; }
}
```

Non-frozen structs become C# `class` with a `SafeHandle` payload — their size isn't known at compile time.

---

## Enums

### Simple enums

```swift
// Swift
@frozen public enum Direction {
    case north, south, east, west

    public func opposite() -> Direction
}
```

```csharp
// Generated C#
public enum Direction : int
{
    North = 0, South = 1, East = 2, West = 3,
}

public static partial class DirectionExtensions
{
    public static Direction Opposite(this Direction self) { ... }
}
```

### Enums with associated values

```swift
// Swift
public enum Result<T> {
    case success(T)
    case failure(Error)
}
```

```csharp
// Generated C#
public partial class Result<T> : ISwiftObject, IDisposable
{
    public static Result<T> Success(T value) { ... }
    public static Result<T> Failure(SwiftError error) { ... }

    public bool IsSuccess { get; }
    public T SuccessValue { get; }
}
```

### Raw representable enums

```swift
// Swift
public enum LogLevel: String {
    case debug, info, warning, error
}
```

```csharp
// Generated C#
public partial class LogLevel : ISwiftObject, IDisposable
{
    public static LogLevel Debug { get; }
    public static LogLevel Info { get; }
    public static LogLevel Warning { get; }
    public static LogLevel Error { get; }

    public SwiftString RawValue { get; }
    public static LogLevel? FromRawValue(string rawValue) { ... }
}
```

---

## Protocols

```swift
// Swift
public protocol ImageProcessing {
    var identifier: String { get }
    func process(_ image: UIImage) -> UIImage?
}
```

```csharp
// Generated C# — interface for type-safe usage
public interface IImageProcessing
{
    string Identifier { get; }
    UIImage? Process(UIImage image);
}

// Generated C# — proxy for implementing the protocol from C#
public partial class ImageProcessingProxy : IImageProcessing, ISwiftObject, IDisposable
{
    public ImageProcessingProxy(IImageProcessing implementation) { ... }
}
```

Implement `IImageProcessing` in C#, wrap it in `ImageProcessingProxy`, and pass it to any Swift API that expects `ImageProcessing`.

---

## Type Conversions

Both method signatures and properties use idiomatic C# types.

| Swift | C# |
|-------|----|
| `String` | `string` |
| `Array<T>` | `IReadOnlyList<T>` (return) / `IEnumerable<T>` (param) |
| `Optional<T>` | `T?` |
| `Int` | `nint` |
| `Bool` | `bool` |
| `URL` | `NSUrl` |

```swift
// Swift
public func search(_ query: String, limit: Int) -> [Result]
public var title: String { get }
```

```csharp
// Generated C#
public IReadOnlyList<Result> Search(string query, nint limit) { ... }
public string Title { get; }
```

---

## Closures

```swift
// Swift
public func onComplete(_ handler: @escaping (Int, Bool) -> Void)
public func transform(_ fn: @escaping (String) -> String) -> String
```

```csharp
// Generated C#
public void OnComplete(Action<nint, bool> handler) { ... }
public string Transform(Func<string, string> fn) { ... }
```

Closures marshal through `GCHandle` pinning. The generated code handles the delegate lifecycle automatically.

### Throwing closures (simplified)

```swift
// Swift
public func attempt(_ action: @escaping () throws -> String)
```

```csharp
// Generated C# — simplified overload (user-facing)
public void Attempt(Func<string> action) { ... }

// Generated C# — raw overload (hidden via EditorBrowsable)
public void Attempt(Func<SwiftResult<SwiftString, SwiftError>> action) { ... }
```

The simplified overload wraps your `Func` in a try/catch and converts exceptions to `SwiftResult` automatically.

---

## Async Methods

```swift
// Swift
public func loadImage(_ url: URL) async -> UIImage
public func validate() async throws
```

```csharp
// Generated C#
public Task<UIImage> LoadImageAsync(NSUrl url, CancellationToken cancellationToken = default) { ... }
public Task ValidateAsync(CancellationToken cancellationToken = default) { ... }
```

Async bridging uses a generated Swift wrapper with `@_cdecl` callback + `TaskCompletionSource<T>` on the C# side.

---

## Failable Initializers

```swift
// Swift
public init?(name: String)
```

```csharp
// Generated C#
public static bool TryCreate(string name, out MyType result) { ... }
```

Failable `init?` becomes a `TryCreate` factory with `out` parameter. Returns `false` when Swift returns `.none`.

For raw representable enums specifically, the pattern is `FromRawValue`:

```csharp
public static LogLevel? FromRawValue(string rawValue) { ... }
```

---

## Subscripts

```swift
// Swift
public subscript(index: Int) -> Element { get }
public subscript(key: String) -> Value? { get set }
```

```csharp
// Generated C#
public Element this[nint index] { get; }
public Value? this[string key] { get; set; }
```

---

## Operators

```swift
// Swift
public static func == (lhs: MyType, rhs: MyType) -> Bool
public static func + (lhs: MyType, rhs: MyType) -> MyType
```

```csharp
// Generated C#
public static bool operator ==(MyType lhs, MyType rhs) { ... }
public static bool operator !=(MyType lhs, MyType rhs) { ... }  // auto-synthesized
public static MyType operator +(MyType lhs, MyType rhs) { ... }
```

Comparison operators auto-synthesize their complement (`==`/`!=`, `<`/`>`, `<=`/`>=`).

---

## Next Steps

- **[Supported Features](Supported-Features)** — Full feature reference
- **[Known Limitations](Known-Limitations)** — What's not supported yet
- **[Getting Started](Getting-Started)** — Set up your first binding
