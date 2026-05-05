# Gap: Existential type lowering — `any Foo` ends up as `object` / `Swift.AnyType` / unimplemented protocol proxy

> SDK 0.10.0 generator feature gap (cross-cutting). Discovered across
> [SwiftBindings.Nuke](https://github.com/justinwojo/swift-dotnet-packages),
> [SwiftBindings.BlinkIDUX](https://github.com/justinwojo/swift-dotnet-packages),
> and several other libraries during a 2026-05-05 audit.

## Summary

Two related shortfalls in how Swift existential / protocol-typed values
are lowered into C#:

1. **`any Foo` (existential) container properties / parameters fall back
   to `object` or `Swift.AnyType`** when the generator can't (or chooses
   not to) emit a typed `IFoo`-projected wrapper. The wrapper is marked
   `[UnsupportedSwiftType("Existential type fallback", "any Foo")]` to
   make the gap visible, but the consumer-side surface is no longer
   strongly typed.

2. **Protocol-conforming proxies (`EveryProtocol`-style)** are emitted as
   stubs that throw `NotSupportedException` for most members. So even
   when the generator manages to emit a concrete `IFoo` interface, a
   consumer who tries to *implement* `IFoo` from C# and pass it back to
   Swift hits a runtime `throw` for any non-trivial method.

Both surfaces hit production bindings — Nuke (`ImagePipelineDelegate`,
`ImageDecoding`), BlinkIDUX (`EventStream`, `ICameraModel.SampleBuffer`),
Stripe (delegate protocols), Lottie (value providers).

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.2
- .NET SDK 10.0.107, workload 10.0.107
- Xcode 26.3 / Swift 6.2.x
- macOS 26.x, arm64

## Concrete cases

### Case 1 — `any Foo` collapses to `object`

```text
swiftinterface (BlinkIDUX line 60):
  public class BlinkIDAnalyzer {
    public var events: any EventStream<UIEvent> { get }
    …
  }
```

Generated C#:

```csharp
// BlinkIDUX.cs:483
[UnsupportedSwiftType("Existential type fallback",
    "any BlinkIDUX.EventStream<BlinkIDUX.UIEvent>")]
public object Events { get { … } }
```

Consumer ergonomic loss: the Swift type carried strong information
(`EventStream<UIEvent>`) and the C# surface threw it all away. Anyone
trying to consume the analyzer's event stream now has to either hold a
sibling reference to a typed `BlinkIDEventStream` (the concrete impl) or
opaque-cast through `object`.

### Case 2 — protocol-property collapse on `ICameraModel`

```text
swiftinterface (BlinkIDUX line 366):
  public protocol CameraModel {
    var sampleBuffer: AsyncStream<SampleBuffer> { get }
    …
  }
```

Generated C# — concrete `Camera` type:

```csharp
// BlinkIDUX.cs:7488
public IAsyncEnumerable<SampleBuffer> SampleBuffer { get { … } }
```

Generated C# — `ICameraModel` interface (the protocol):

```csharp
// BlinkIDUX.cs:8667
public Swift.AnyType SampleBuffer { get; }
```

The concrete class projects `AsyncStream<SampleBuffer>` to
`IAsyncEnumerable<SampleBuffer>` correctly. The interface that the
concrete class supposedly implements gives up and exposes `Swift.AnyType`.
A C# consumer who depends on `ICameraModel` rather than `Camera` (the
correct DI pattern) gets the worst surface.

### Case 3 — `Nuke.ImagePipeline.IDelegate` exists, but proxying is not

The Nuke 13.0.5 binding emits `ImagePipeline.IDelegate` correctly
(modulo the I-prefix bug —
[bug-0.10.0-nested-protocol-i-prefix.md](./bug-0.10.0-nested-protocol-i-prefix.md)),
and the constructor accepts `IDelegate?`. To pass a managed
implementation to Swift, the binding emits an `ImagePipeline.DelegateProxy`
class that wraps the C# delegate behind Swift's protocol-witness table.
But many of the proxy's methods are stubs that `throw new
NotSupportedException("EveryProtocol proxy stub: …")`.

Net: a consumer can't safely implement `ImagePipelineDelegate` from C#.
They can pass `null` or pass a Swift-side instance retrieved from another
API, but a managed implementation breaks at runtime on first witness
dispatch.

## Why this lands as one issue

The two halves are the same machinery in opposite directions:

- **Case 1 / 2 ("Swift→C# direction"):** when a Swift type *exposes*
  an existential to the C# consumer, the generator should project it as
  a strongly-typed `IFoo` (the same interface it emits for the protocol
  declaration). If the protocol is generic (`EventStream<UIEvent>`), the
  C# interface is `IEventStream<UIEvent>` and the existential maps to
  it.
- **Case 3 ("C#→Swift direction"):** when a C# consumer *implements*
  `IFoo` and hands an instance back to Swift, the generator emits a
  proxy class that bridges Swift's protocol witness table → C# virtual
  dispatch. Today the proxy is mostly stubs; needs the actual bridge per
  member.

Same conceptual subsystem, two emission halves.

## Hypothesis

Existential lowering ("Case 1/2") fell back to `object` /
`Swift.AnyType` whenever the generator couldn't compute a closed-form C#
type — most often when:

- the existential is generic (`any EventStream<UIEvent>`);
- the existential bounds include `any Sendable & Foo`;
- the existential appears in a property declared *on a protocol* (not on
  a concrete class).

Each of these can probably be fixed independently. The generic-existential
path probably needs the most work because closed-form lowering of
`any Foo<X>` requires having `IFoo<X>` projected.

Proxy emission ("Case 3") is structural. The `EveryProtocol`-stub
emission appears to be a placeholder that a sweep of the generator's
"emit witness for protocol member" step never replaced with actual
dispatch code. The dispatch should:

1. Take the Swift `SwiftSelf` argument as the protocol-conforming Swift
   type instance (the proxy's storage).
2. Look up the corresponding `IFoo` member (matched by Swift-side
   name/signature).
3. Marshal arguments C#-side, invoke the C# member, marshal the return
   back across.

This is the same pattern as the closure trampoline in
[bug-0.10.0-callback-trampoline-gchandle-leak.md](./bug-0.10.0-callback-trampoline-gchandle-leak.md)
but per-protocol-member rather than per-closure-arg, with
`SwiftClosureMarshaller`-equivalent runtime support.

## Impact

- **Consumer-experience.** Several flagship APIs — Nuke
  `ImagePipelineDelegate`, BlinkIDUX `BlinkIDAnalyzer.events`,
  BlinkIDUX `ICameraModel.sampleBuffer` — degrade to opaque `object` /
  `Swift.AnyType`, or to compile-time-correct interfaces that throw at
  runtime.
- **Library scope.** Existential fallback hits any Swift API surface
  that returns `any Foo` or accepts `any Foo`. Protocol-proxy stubs hit
  any API that asks the consumer to implement and pass back a Swift
  protocol. Both are common in modern Swift API design.

## Workaround

Consumer side, per case:

- Case 1/2: hold a sibling typed reference (e.g. `BlinkIDEventStream`,
  `Camera`) and use that for the strongly-typed surface; treat the
  protocol-typed surface as opaque.
- Case 3: pass `null` or pass a Swift-provided default. Don't attempt
  to implement the protocol from C#.

Proper fix in
[swift-dotnet-bindings](https://github.com/justinwojo/swift-dotnet-bindings):
both halves of the existential machinery.

## Severity

**Feature gap — High.** Doesn't crash unless the consumer attempts to
implement a protocol from C# (then runtime `throw`). Doesn't corrupt
state. But a meaningful chunk of every modern Swift framework's public
API surface lands in C# as either `object` or "throws on call,"
constraining consumers to the subset of APIs that don't touch protocol
existentials. This is the single biggest "feel" gap between using a
Swift library from Swift vs. from a SwiftBindings C# binding.

Cross-references in
[SDK-0.10.0-BLOCKERS.md](../../swift-dotnet-packages/SDK-0.10.0-BLOCKERS.md):
priority items P1 (EveryProtocol proxy completion) and P2 (existential
type lowering). Same subsystem; ship together.
