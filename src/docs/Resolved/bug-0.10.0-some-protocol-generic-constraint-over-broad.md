# Bug: Swift `some Protocol` parameter lowered to generic with `where T : ISwiftObject` instead of the bound protocol

> SDK 0.10.0 generator correctness bug. Discovered 2026-05-05 during a
> consumer-experience audit of
> [SwiftBindings.Apple.StoreKit2](https://github.com/justinwojo/swift-dotnet-packages)
> (Apple StoreKit framework, 26.2.2).

## Summary

Swift's "opaque-result-type" parameter syntax (`some UIScene`) lowers to a
generic in C# — but the generator emits the constraint as `where T :
ISwiftObject` instead of the bound protocol type (`UIKit.UIScene`).
Compiler accepts any `ISwiftObject` (e.g. another `Product`); runtime
crashes when Swift tries to project the wrong metadata against the
expected protocol witness table.

The parameter is also misnamed `viewController` despite Swift's `confirmIn
scene:` argument label — confusing on its own, and conflicts with the
sibling `confirmIn viewController: UIViewController` overload's parameter
name.

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.2
- .NET SDK 10.0.107, workload 10.0.107
- Xcode 26.3 / Swift 6.2.x
- macOS 26.x, arm64
- Source framework: system StoreKit (iOS 26.2.2)

## Repro

```bash
sed -n '24276,24327p' apple-frameworks/StoreKit2/obj/Debug/net10.0-ios26.2/swift-binding/StoreKit2.cs
```

```csharp
[SupportedOSPlatform("ios17.0")] [SupportedOSPlatform("tvos17.0")] [SupportedOSPlatform("maccatalyst17.0")]
[SupportedOSPlatform("ios18.2")] [SupportedOSPlatform("tvos18.2")] [SupportedOSPlatform("maccatalyst18.2")]
public Task<StoreKit2.Product.PurchaseResult> PurchaseAsync<T0>(
    T0 viewController,
    IEnumerable<StoreKit2.Product.PurchaseOption> options,
    CancellationToken cancellationToken = default)
    where T0 : ISwiftObject       // [BAD] should be: where T0 : UIKit.UIScene
{
    ...
    TypeMetadata T0Metadata = TypeMetadata.GetTypeMetadataOrThrow<T0>();
    ...
}
```

## Native ground truth

```text
swiftinterface (StoreKit framework, line ~1653):
  @MainActor public func purchase(
    confirmIn scene: some UIScene,
    options: Set<Product.PurchaseOption> = []
  ) async throws -> Product.PurchaseResult
```

`some UIScene` is Swift opaque-result-type sugar for an unnamed generic
parameter constrained to `UIKit.UIScene`. The Swift compiler resolves it
to a fresh generic with the protocol constraint visible at the call site.

## Hypothesis

The emitter sees `some Protocol` and falls back to the generic-parameter
path, but the constraint synthesizer drops the protocol type and emits
`where T : ISwiftObject` (the universal Swift-class lower bound). Likely
fix: when the source `some <Type>` constraint is a Swift protocol, look
up the corresponding C# protocol type (interface or type-erased class —
in this case `UIKit.UIScene`) in the typedb and emit
`where T : UIKit.UIScene`.

The fallback to `ISwiftObject` is the same general "couldn't lower the
constraint" pattern as
`gap-0.10.0-everyprotocol-and-existentials.md`, but distinct:
existentials there land on `object` / `Swift.AnyType` at the call site;
M-3 here has a *typed* generic parameter with the *wrong* constraint.

## Impact

- **Compile-time type safety hole.** Any `ISwiftObject` is accepted.
  `await product.PurchaseAsync(otherProduct, options)` compiles cleanly.
- **Runtime crash.** Swift projects the metadata against the
  `UIScene` protocol witness table; mismatched metadata segfaults.
- Limited blast radius today (one StoreKit overload), but the emitter is
  shared across every binding that surfaces a `some Protocol` parameter.
  Future Apple-framework bindings (RealityKit, SwiftUI) will hit this
  more.
- The misnamed parameter (`viewController` instead of `scene`) compounds
  consumer confusion.

## Workaround

Consumer side: use `await product.PurchaseAsync(uiScene, options)` and
manually verify at the call site that `uiScene is UIKit.UIScene`. Static
analysis won't help.

Repo side: a Roslyn analyzer that flags `PurchaseAsync<T>(...)` calls
where `T` does not derive from `UIKit.UIScene` is feasible but excessive.

## Severity

**Correctness — Medium.** Compile-accept, runtime-crash. Distinct from
existential lowering (`gap-0.10.0-everyprotocol-and-existentials.md`)
because the parameter IS typed-generic; the constraint is just wrong.

Cross-reference in
[SDK-0.10.0-BLOCKERS.md](../../swift-dotnet-packages/SDK-0.10.0-BLOCKERS.md)
under Round 4 / M-3.

## Resolution

Fixed by widening `MethodValidationGates.TryGetClassConstraintTarget` to
recognize ObjC-bridged class constraint targets through two paths:

1. **Registered records.** When the conformance target is in the
   `TypeDatabase` with `Kind == Class` and the `ObjCBridged` flag, the
   helper returns the C# class name (e.g. `Foundation.NSCoding` →
   `Foundation.INSCoding`). DB-registered records always win — the helper
   trusts the recorded `Kind`/`Flags` even when the type happens to live
   in a known autoBridge module.
2. **autoBridge synthesis fallback.** When the conformance target is
   *absent* from the database but matches
   `TypeDatabaseExtensions.IsObjCClassSwiftType` (e.g. `UIKit.UIScene`
   under `IsAutoBridgeModule`), the helper synthesizes the same record
   on demand via `CreateObjCBridgedTypeRecord`. autoBridge framework
   types are intentionally *not* pre-loaded into the DB to keep generator
   memory bounded; the rest of the emitter (`GetTypeRecordOrThrow`,
   `GetTypeRecordOrAnyType`) follows the same "absent-but-ObjC" pattern.

Both `WrapperEmitter.Signature.BuildWhereClause` (for method-level
generic constraints) and `GenericTypeEmitter.GetWhereClause` (for
type-level constraints) now consult `TryGetClassConstraintTarget` before
the historical permissive interface-name path. When a class constraint
matches, the emitter:

- Drops the `ISwiftObject` seed — ObjC-bridged classes do not implement
  `ISwiftObject`; combining the two would yield an unsatisfiable
  constraint.
- Emits the class name first (`where T : UIKit.UIScene`), so additional
  interface constraints follow C#'s "class first, interfaces after"
  ordering.
- Skips the witness-table extraction and PWT P/Invoke parameter, which
  the body builders already filter via `IsProtocolAvailableForConstraint`.

`StoreKit2.Product.PurchaseAsync<T0>` (the original repro) now emits:

```csharp
public Task<StoreKit2.Product.PurchaseResult> PurchaseAsync<T0>(
    T0 viewController,
    IEnumerable<StoreKit2.Product.PurchaseOption> options,
    CancellationToken cancellationToken = default)
    where T0 : UIKit.UIScene
{
    ...
    TypeMetadata T0Metadata = TypeMetadata.GetTypeMetadataOrThrow<T0>();
    ...
}
```

Coverage:

- `TryGetClassConstraintTarget_*` (5 tests in
  `ConditionalExtensionConstraintTests.cs`): registered ObjCBridged
  Class promotes, Protocol record does not, plain Swift Class without
  ObjCBridged does not, unknown type does not, autoBridge framework
  type absent from the DB synthesizes correctly.
- `GetWhereClause_ObjCBridgedClass*` (3 tests in
  `GenericTypeEmitterTests.cs`): type-level constraint promotion +
  ordering vs. interfaces + non-ObjCBridged cross-module classes still
  take the interface path.

The misnamed parameter side-note (`viewController` instead of `scene`)
is a separate parameter-naming concern in the disambiguation/overload
pass, not a type-safety bug; tracked separately if it surfaces in
consumer feedback.
