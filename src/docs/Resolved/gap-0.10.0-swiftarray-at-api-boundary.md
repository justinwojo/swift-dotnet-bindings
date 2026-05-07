# Gap: `Swift.SwiftArray<T>` leaks into property/return types at the API boundary

> SDK 0.10.0 generator ergonomic gap. Discovered 2026-05-05 during a
> consumer-experience audit of
> [SwiftBindings.BlinkIDUX](https://github.com/justinwojo/swift-dotnet-packages)
> 7.7.0 generated bindings.
>
> **Status: RESOLVED in Bundle 04 #8** for the AsyncStream<T> family. The
> `AsyncStreamHandler` element-type translator now substitutes
> `Swift.Array<T>` → `IReadOnlyList<T>`, `Swift.Set<T>` → `IReadOnlySet<T>`,
> and `Swift.Dictionary<K, V>` → `IReadOnlyDictionary<K, V>` at the public
> API boundary, while the internal `SwiftAsyncStream<T>` channel storage
> retains the runtime helper container (`SwiftArray<T>` etc.) so
> `SwiftMarshal.MarshalFromSwift<TElement>` in the channel's element
> callback can still deserialize the Swift payload. The covariance of
> `IAsyncEnumerable<out T>` plus the inheritance
> `SwiftArray<T> : IReadOnlyList<T>` (and matching `SwiftSet<T> :
> IReadOnlySet<T>`, `SwiftDictionary<K, V> : IReadOnlyDictionary<K, V>`)
> closes the loop at the property getter return.
>
> Coverage: unit tests in `AsyncStreamHandlerTests`
> (`GetCSharpAsyncEnumerableType_With{Array,Set,Dictionary}OfXElement_*`
> + `GetCSharpInternalChannelElementType_With*_RetainsSwift*`); BindingTests
> compile-time assertion `TestAsyncValueSourceBatchesBoundaryType` plus the
> Swift fixture `AsyncValueSource.batches: AsyncStream<[Int32]>`. Other
> AsyncStream property shapes that don't involve nested Swift collection
> containers are unaffected by the change.
>
> Out of scope: non-AsyncStream return positions (e.g., a method directly
> returning `[T]` is already projected as `IReadOnlyList<T>` via
> `ArrayProjection.PublicType`); other generic-instantiation positions
> like `Task<X>` and `Tuple<X, Y>` are not covered by this fix and would
> need an analogous boundary-substitution pass on whatever projector
> generates those signatures.

## Summary

Swift collection types (`[T]`, `Array<T>`) are projected to
`Swift.SwiftArray<T>` on parameters and returns. For most internal
positions this is fine — it's a runtime helper that's interop-friendly
and bridges back to Swift cheaply. But at the **public API boundary**
— properties, return tuples inside `IAsyncEnumerable<T>`, public
method returns — the consumer-facing type should be a standard .NET
collection abstraction (`IReadOnlyList<T>`, `IEnumerable<T>`, or `T[]`)
so callers don't need to know about `Swift.SwiftArray<T>`.

The discovery case is BlinkIDUX's event stream, which exposes a Swift
`AsyncStream<[UIEvent]>` and ends up surfaced in C# as
`IAsyncEnumerable<Swift.SwiftArray<UIEvent>>`.

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.2
- .NET SDK 10.0.107, workload 10.0.107
- Xcode 26.3 / Swift 6.2.x
- macOS 26.x, arm64
- Source library: BlinkIDUX 7.7.0

## Repro

```bash
sed -n '46,65p' libraries/BlinkIDUX/obj/Debug/net10.0-ios/swift-binding/BlinkIDUX.cs
```

```csharp
// BlinkIDUX.cs:52
public IAsyncEnumerable<Swift.SwiftArray<BlinkIDUX.UIEvent>> Stream
{
    get
    {
        unsafe
        {
            var stream = new SwiftAsyncStream<Swift.SwiftArray<BlinkIDUX.UIEvent>>();
            PInvoke_BlinkIDEventStream_stream_AsyncStream(
                (void*)_handle.DangerousGetHandle(), &stream_AsyncStream_OnElement,
                &stream_AsyncStream_OnComplete,
                stream.GetContext());
            return stream;
        }
    }
}
```

Native ground truth:

```text
swiftinterface (BlinkIDUX line 28):
  public var stream: AsyncStream<[BlinkIDUX.UIEvent]> { get }
```

So the Swift `[UIEvent]` element type passes through as
`Swift.SwiftArray<UIEvent>` rather than `IReadOnlyList<UIEvent>`.

## Hypothesis

`Swift.SwiftArray<T>` is the SDK's runtime representation for Swift's
native `Array<T>` — used by the marshalling layer to round-trip arrays
across the FFI boundary efficiently. When the type appears at an
internal boundary (parameter to a PInvoke, return from a thunk), this
is the right type. At the public API boundary, the projection step that
should map `Swift.SwiftArray<T>` → `IReadOnlyList<T>` (or similar)
appears to either not run, or not run inside generic-instantiation
positions like `IAsyncEnumerable<…>`.

The simpler "method returns `[T]`" case probably already projects
correctly to `IReadOnlyList<T>` (worth a cross-library check). If so,
the bug is specifically that the projection doesn't recurse into
generic type arguments — `IAsyncEnumerable<X>`, `Task<X>`, `Tuple<X, Y>`
all have the same problem if `X` happens to be `SwiftArray<T>`.

## Why `IReadOnlyList<T>` is the right target

Three reasons:

1. **Read semantics match.** Swift `Array<T>` returned from a property
   getter is a value copy — the consumer can read it without affecting
   the source. `IReadOnlyList<T>` is the standard .NET shape for
   "indexable, no mutation."
2. **Low marshalling cost.** `SwiftArray<T>` already has the data
   materialized; wrapping it in an `IReadOnlyList<T>` adapter is a
   constant-factor operation, no copy.
3. **Doesn't preempt advanced cases.** Consumers who specifically want
   `Swift.SwiftArray<T>` (to pass it back to Swift cheaply) can still
   downcast — the runtime type is unchanged.

Alternatives considered:

- `T[]` — forces a copy from `SwiftArray<T>` to a managed array.
  Ergonomic but expensive; rules out for hot paths like an event stream
  that might fire 30Hz.
- `IEnumerable<T>` — too weak; no `.Count`, no indexer.
- `IList<T>` — implies mutability we don't actually offer.

`IReadOnlyList<T>` is the Goldilocks choice: cheap to wrap, idiomatic
for return positions, and doesn't lie about mutability.

## Affected sites

- BlinkIDUX `BlinkIDEventStream.Stream` — discovery case, line 52.
- Any other property/return that involves a Swift `[T]` nested inside a
  generic type. Would need a cross-library scan: `grep -n
  "Swift\.SwiftArray<" libraries/*/obj/.../*.cs | grep -v
  "private\|internal\|EditorBrowsable"` to surface public-API hits.
- Likely BlinkID itself (any field-extraction surface that returns
  collections of detected items), Stripe (payment method lists), Lottie
  (keypath arrays).

## Workaround

Consumer side: `await foreach (var arr in stream.Stream) { var list =
arr.ToList(); … }` or equivalent. Loses the streaming-friendly
interface in exchange for `IReadOnlyList<UIEvent>` semantics.

Proper fix in
[swift-dotnet-bindings](https://github.com/justinwojo/swift-dotnet-bindings):
make the boundary-type projector recurse into generic type arguments
and project `Swift.SwiftArray<T>` → `IReadOnlyList<T>` (or whatever
boundary type the SDK settles on).

## Severity

**Ergonomics — Low-Medium.** Compiles and runs correctly. Consumer
just gets a Swift-runtime type they didn't expect at a position where
a .NET collection abstraction would be the natural shape. Worth fixing
in the same wave as the other projection-rule consistency work
(particularly
[bug-0.10.0-callback-arg-projection-asymmetry.md](./bug-0.10.0-callback-arg-projection-asymmetry.md),
which is the same family of "projector ran in one place, not the
other").
