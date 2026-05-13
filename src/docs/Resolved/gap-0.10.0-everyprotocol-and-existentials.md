# Gap: Existential type lowering — `any Foo` ends up as `object` / `Swift.AnyType` / unimplemented protocol proxy

> SDK 0.10.0 generator feature gap (cross-cutting). Discovered across
> [SwiftBindings.Nuke](https://github.com/justinwojo/swift-dotnet-packages),
> [SwiftBindings.BlinkIDUX](https://github.com/justinwojo/swift-dotnet-packages),
> and several other libraries during a 2026-05-05 audit.

## Status — RESOLVED

All three cases ship in 0.10.0.

- **Case 1** (`any Foo` property/parameter collapse to `object`/`Swift.AnyType`): RESOLVED.
  The generator projects single-protocol existentials to the generated `IFoo` interface.
- **Case 2** (typed existential receives concrete-class instance): RESOLVED by Case 1's
  projection — the same `IFoo` surface is consumed for both shapes.
- **Case 3** (consumer-implemented `IFoo` proxy → `EveryProtocol`-style throwing stubs):
  RESOLVED across Sessions 4a (`dfc7fc89` — `() -> Void` closure-param dispatch through
  C#-implemented proxies, vtable slot expansion to `(fnPtr, ctx)` pairs, per-shape `@_cdecl`
  invoke thunks, `SBW_SwiftReleaseRaw` lazy-`dlsym` runtime helper), 4b (`a52de59b` —
  multi-arg closures, return-typed closures, `Optional<Closure>` with the inout-bytes
  reabstraction trap fix at `EveryProtocolEmitter.cs:2113-2150`), and 4c (`5b4068bc` —
  throwing closures with `_errorOut` plumbing surfaced as `SwiftResult<T, SwiftError>`,
  async closures, closure properties via `HasClosureInPropertyType` lift, and closure-returning
  methods). Non-closure protocol methods already dispatched correctly through
  `EmitMethodReceiver` before Session 4 — the audit-confirmed gap was closure-bearing members
  only. Synthetic BindingTests fixtures cover every shipped shape end-to-end on sim + device.

  **Documented residual carve-out:** closure-bearing protocol methods whose closure args/returns
  fall outside `ClosureEmitter.CanUseInvokeThunk` still emit `NotSupportedException` stubs.
  `CanUseInvokeThunk` accepts cdecl primitives, simple enums, and complex enums for args, and
  cdecl primitives, simple enums, and class types for returns; it rejects String args, struct
  value types, generics, and protocol existentials. The flagship documented example is
  String-arg `DataLoadingDelegate.onDataLoaded`. The Session 4c regression sentinel
  (`ProtocolClosureSkipTests`) asserts the residual `"EveryProtocol: closure method"` stub
  count is fully attributable to this gate, not to unintended shape gaps. Widening the gate is
  future work, not a 0.10.0 blocker — the gate's residuals are a strict subset of the
  pre-Session-4 universe where *every* closure-bearing method was a stub.

  **Per-error leak carve-out (throwing path):** `ClosureEmitter.InvokeThunk.cs:360-368`
  emits `SwiftResult.FromFailure(new SwiftError((void*)_err))` and keeps the +1 retain
  from `Unmanaged.passRetained` in the `@_cdecl` thunk for the `SwiftError` lifetime to
  match the existing `SwiftErrorException.Error` / `ClosureEmitter.Throwing.cs` convention
  ("managed code never releases bare `SwiftError` pointers"). Tracked in `src/docs/roadmap.md`
  as a future Disposable failure carrier; per-error leak that only materializes when a
  Swift→C# throwing closure throws frequently.

  **Out-of-tree consumer validation:** Nuke `ImagePipeline.IDelegate` and BlinkIDUX
  `ICameraModel` consumer-side C# implementations are not exercised in this repo; they
  land via `swift-dotnet-packages` / `internal-binding-testing` if/when needed. The
  in-tree synthetic matrix covers the shape grid.

BindingTests coverage: `BindingTests/Sources/SwiftBindingsTestLib/Protocols/ProtocolClosureSkipping.swift` (Swift protocols: `EventDelegate`, `DataLoadingDelegate`, `NumericDataDelegate`, `CompletionDelegate`, `IntFactoryDelegate`, `ThrowingIntDelegate`, `HasCallbackDelegate`, `HandlerFactoryDelegate`, `AsyncIntDelegate`, `MultiShapeDelegate`, with their `*Router` / `*Loader` consumers) + `BindingTests/RuntimeTestsApp/Protocols/ProtocolClosureSkipTests.cs` (test class `ProtocolClosureSkipTests`).

### Typed-PAT runtime conformance lookup — design constraint

The closed-constrained existential projection (Case 1, e.g. `any LabelledContainer<String>`
→ `ILabelledContainer<SwiftString>`) requires a runtime fallback in the generated
`GetProtocolConformanceDescriptor<TProtocol>()` body, because a single-PAT conformer's
`_protocolConformanceSymbols` dictionary is keyed on `typeof(object)` (the lowered C# type
for an unparameterised `object` parameter) rather than on the closed generic interface
type the typed boxing site uses. The fallback is:

```csharp
if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
{
    if (!(typeof(TProtocol).IsGenericType &&
          _protocolConformanceSymbols.TryGetValue(typeof(object), out symbolName)))
    {
        throw new SwiftRuntimeException(...);
    }
}
```

**Constraint analysis** — this fallback is robust by construction in three ways:

1. **`IsGenericType` gate** — only generic-protocol lookups (typed PATs like
   `IFoo<X>`) hit the typeof(object) fallback. Non-PAT typed lookups (`IBar`)
   take the exact-typed-key path and never reach the fallback. The two key
   spaces don't overlap, so a type that conforms to both `any P<X>` (PAT) and
   `any Q` (non-PAT, possibly closed existential) cannot collide on lookup.

2. **Multi-PAT upstream guard** — `CountPatConformances == 1`
   (`TypeHandlerHelpers.cs` `GetImplementedInterfaces` + `GenerateProtocolConformanceDictionaryEntries` +
   `GetConformanceProtocolNames`) suppresses the typeof(object) dict entry,
   the `IExistentialBoxable` interface, AND the conformance-factory registration
   when a type has 2+ PAT conformances. So a multi-PAT conformer surfaces as a
   clear `InvalidCastException` at the boxing call site, not silent
   wrong-witness-table dispatch.

3. **`object` lookup is never typed-issued** — production C# call sites for
   typed boxing always use the closed generic interface (e.g.
   `BoxAsExistential1<ILabelledContainer<SwiftString>>()`); the unparameterised
   `BoxAsExistential1<object>()` shape is reserved for internal lowering
   paths that already know they want the PAT entry.

These three together mean a Swift type with both a PAT conformance and any
number of orthogonal non-PAT conformances resolves correctly: each lookup
takes its respective branch (typed key for non-PAT, IsGenericType-gated
fallback for PAT) without crosstalk. No fixture is required to assert this
because the property holds at the C#-type-system level (`IsGenericType` /
typed-key uniqueness), not at runtime witness-table content.

If a future protocol shape violates this — e.g. a non-PAT generic protocol
that surfaces as `IBar<X>` with `IsGenericType=true` AND coexists with a PAT
conformer — the multi-PAT guard at `TypeHandlerHelpers.cs:1014–1023` would
need to widen to also count generic non-PAT conformances. Flag in this
document if observed in validation.

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
[Resolved/bug-0.10.0-callback-trampoline-gchandle-leak.md](./Resolved/bug-0.10.0-callback-trampoline-gchandle-leak.md)
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
