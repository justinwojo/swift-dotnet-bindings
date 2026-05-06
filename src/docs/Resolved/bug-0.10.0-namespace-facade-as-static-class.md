# Bug: Swift "namespace facade" types emit as nested static classes instead of C# namespaces

> SDK 0.10.0 generator ergonomics gap. Discovered 2026-05-05 while bumping
> [SwiftBindings.BlinkID](https://github.com/justinwojo/swift-dotnet-packages)
> from 7.6.2 → 7.7.0, where Microblink moved ~25 public types under a new
> outer `BlinkIDSDK` struct.

> **Status: RESOLVED in Bundle 04 #3.**
> A new strict predicate (`NamespaceFacadeDetector.IsNamespaceFacade`) recognises
> the canonical "uninhabited type as namespace" idiom — a top-level
> `public struct`/`public enum` with **zero** properties, methods, operators,
> subscripts, generic parameters, enum cases, and **zero non-marker** protocol
> conformances (the parser auto-attaches `Swift.Copyable` + `Swift.Escapable`
> to every value type, plus `Swift.Sendable` when applicable; those stdlib
> markers are filtered out before the count check). When the predicate matches,
> a new `NamespaceFacadeEmitter.Emit` writes a real C# `namespace {Name} { … }`
> block at the current indent (the module namespace is already open), pushes
> the facade onto the type-nesting stack so nested-type Swift wrappers see
> module-qualified Swift identifiers (`BlinkID.BlinkIDSDK.Foo`), and recurses
> into `Types` via the standard `IHandler.HandleBaseDecl` dispatch path.
> Interception lives in `IHandler.HandleBaseDecl` for both the `StructDecl`
> and `EnumDecl` branches, before the per-handler dispatch.
>
> **Coverage:** 11 unit tests for the predicate
> (`NamespaceFacadeDetectorTests` — positive struct + caseless-enum cases,
> implicit-marker conformances accepted, every disqualifier negative-tested
> including `ClassDecl`); BindingTests Swift fixture
> (`Types/NamespaceFacade.swift` exercising both struct- and enum-flavoured
> facades plus a free-function return type that forces the lifted-namespace
> reference); 5 RuntimeTestsApp tests (`NamespaceFacadeTests`) whose
> `using SwiftBindingsTestLib.LocalFacade;` / `using SwiftBindingsTestLib.LocalFacadeEnum;`
> directives at the top of the file are themselves the regression gate —
> pre-fix those usings would fail with CS0138 because `LocalFacade` was a
> type, not a namespace.

## Summary

When a Swift module uses the canonical "uninhabited type as namespace" idiom —
an outer `struct`/`enum` with no stored properties, no inits, and no instance
members, used purely to scope a family of nested types — the generator
translates it 1:1 as a C# `partial class`, with all the nested Swift types
becoming nested C# types inside that class.

The output is technically correct (round-trips the Swift type tree) but
non-idiomatic for .NET consumers: C# has first-class namespaces, and the
natural mapping for a Swift namespace facade is a real C# `namespace`, not a
container class.

The user-visible symptom is that consumers must reach for `using static` —
which most C# developers associate with member access (`using static
System.Math;`), not nested-type access — to avoid fully qualifying every type.

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.2
- .NET SDK 10.0.107, workload 10.0.107
- Xcode 26.3 / Swift 6.2.x
- macOS 26.x, arm64

## Repro

BlinkID 7.7.0's swiftinterface declares:

```swift
public struct BlinkIDSDK {            // ← outer namespace facade
  public struct StringResult: Swift.Sendable { … }
  public struct InputImageAnalysisResult: Swift.Sendable { … }
  public enum ScanningStatus: Swift.Sendable { … }
  // …25+ more nested types
  // No public init, no stored properties, no instance funcs/vars/lets.
}
```

The generator emits:

```csharp
// BlinkID.cs (~line 11149)
namespace BlinkID
{
    public partial class BlinkIDSDK            // ← container class, not a namespace
    {
        public partial class InputImageAnalysisResult : ISwiftObject, … { … }
        public enum ScanningStatus : int { … }
        public partial class StringResult : ISwiftObject, … { … }
        // …25+ nested types
    }
}
```

Consumer code in
[swift-dotnet-packages BlinkID test app](https://github.com/justinwojo/swift-dotnet-packages/blob/main/libraries/BlinkID/tests/Program.cs):

```csharp
using BlinkID;
// using BlinkID.BlinkIDSDK;  // ← would be the natural reflex
//   error CS0138: A 'using namespace' directive can only be applied to
//   namespaces; 'BlinkIDSDK' is a type not a namespace.

using static BlinkID.BlinkIDSDK;       // ← only ergonomic option
//   Works, but `using static` is conventionally for *member* access
//   (e.g. `using static System.Math;` to write `Sqrt(x)`), not for
//   reaching nested types. Most C# devs won't reach for it.

…

var status = ScanningStatus.SideScanned;             // ← what you want
var status = BlinkID.BlinkIDSDK.ScanningStatus.SideScanned;  // ← without `using static`
```

## Native-side reference (sanity check)

Three independent sources confirm the canonical fully-qualified Swift name is
`BlinkID.BlinkIDSDK.<Type>`, so `namespace BlinkID.BlinkIDSDK` is the correct
target shape:

```text
swiftinterface (type references):
  public let scanningStatus: BlinkID.BlinkIDSDK.ScanningStatus
  public let firstName: BlinkID.BlinkIDSDK.StringResult?

generator's own PInvoke entry points (already encode the path):
  SBW_Get_BlinkID_BlinkIDSDK_StringResult_value
  SBW_Get_BlinkID_BlinkIDSDK_InputImageAnalysisResult_processingStatus

Swift mangled symbol:
  $s7BlinkID0A5IDSDKV24InputImageAnalysisResultVMa
  → BlinkID.BlinkIDSDK.InputImageAnalysisResult
```

A C# `namespace BlinkID.BlinkIDSDK { … }` block preserves the Swift-side
name 1:1 and lets consumers write `using BlinkID.BlinkIDSDK;` — the direct
analog of Swift's `import BlinkID` (which implicitly puts `BlinkIDSDK.X` in
scope inside the importing module).

## Why this is the right call

In Swift, modules are the only true namespace primitive. To group ~25
types without stuffing them all at module top-level, vendors universally
fall back to one of two patterns:

```swift
public enum Foo { … }      // canonical — uninhabited, can't be instantiated
public struct Foo { … }    // also common, instance-less by convention
```

These are syntactic namespaces. They have no runtime instances, no inherent
state, and no semantics beyond name scoping. C# has the actual primitive
(`namespace`), so faithfully translating them to a class is a category error
even when it's structurally correct.

## Detection heuristic

A Swift outer type is a "namespace facade" — and should emit as a C#
namespace — when **all** of the following hold:

1. It is `public struct` or `public enum` declared at module top-level.
2. It declares no stored properties (`var`/`let`).
3. It declares no `init` (no public initializer reachable to consumers).
4. It declares no instance methods or instance computed properties — only
   nested types and (optionally) static members.
5. No `extension Foo` anywhere in the module adds instance state, inits, or
   instance methods.
6. For `enum`: it has no `case`s (uninhabited).

If any check fails, fall back to the current `partial class` emission — the
type has runtime semantics that a C# `namespace` can't host.

If it passes, emit:

```csharp
// instead of:
namespace BlinkID
{
    public partial class BlinkIDSDK { /* nested types */ }
}

// emit:
namespace BlinkID.BlinkIDSDK
{
    /* same nested types, lifted out one level */
}
```

Static members on the facade (e.g. `BlinkIDSDK.someStaticHelper()`) — if any
exist after rule #4 admits them — would need to land somewhere. Two options:

- **Pragmatic:** keep a sibling `public static class BlinkIDSDKConstants` (or
  similar) inside the new namespace for the static surface. Slightly ugly but
  zero call-site churn for consumers using the static members.
- **Principled:** lift static members to module-level free functions / static
  classes elsewhere. Larger consumer-side break.

For BlinkID specifically, rule #4 holds with no exceptions — the facade has
**zero** static members on the swiftinterface. So this consideration is
hypothetical for the discovering case.

## Comparison with Stripe

Stripe's swiftinterface declares many top-level types directly under the
`StripePayments` / `StripePaymentSheet` / etc. modules — no namespace
facade — so this bug doesn't surface there. That's why a 0.9.0 → 0.10.0
build of Stripe wouldn't have caught the issue: BlinkID is the first library
in [swift-dotnet-packages](https://github.com/justinwojo/swift-dotnet-packages)
that actually exercises the pattern, and only as of 7.7.0.

## Impact

- Affects any Swift library that uses the namespace-facade idiom for type
  grouping. Microblink (BlinkID), and likely many other vendors who maintain
  large public APIs.
- Currently shipping consumers can work around it with `using static`, but
  the friction will surface in every BlinkID adoption README and Q&A thread
  until fixed.
- BlinkIDUX (which depends on BlinkID) inherits the same shape via
  transitive type references, but its own swiftinterface doesn't add a new
  facade — so the workaround is one `using static` line per consumer file
  that touches BlinkIDSDK types.

## Workaround

Consumer side: `using static BlinkID.BlinkIDSDK;` at the top of every file
that touches a `BlinkIDSDK.*` type. Fully qualify otherwise.

Proper fix: add namespace-facade detection to
[swift-dotnet-bindings](https://github.com/justinwojo/swift-dotnet-bindings)
emission, ship in 0.10.1 / 0.11.0.

## Severity

Cosmetic / ergonomics — not a correctness or runtime bug. The current
emission compiles and runs correctly; consumers can reach every type.
Lower priority than the two structural 0.10.0 bugs filed the same day:

- [bug-0.10.0-nested-protocol-i-prefix.md](./bug-0.10.0-nested-protocol-i-prefix.md) — blocks Nuke 13
- [bug-0.10.0-mappedin-static-swift-framework.md](./bug-0.10.0-mappedin-static-swift-framework.md) — blocks Mappedin 6.2.0

But this one will be visible to *every* downstream BlinkID consumer, so
worth fixing before BlinkID adoption picks up.
