# Bug: Direct `CallConvSwift` PInvoke emitted for an overload missing its `@_cdecl` wrapper — ABI-unsafe

> SDK 0.10.0 generator correctness bug. Discovered 2026-05-05 during a
> consumer-experience audit of
> [SwiftBindings.Apple.StoreKit2](https://github.com/justinwojo/swift-dotnet-packages)
> (Apple StoreKit framework, 26.2.2).

## Summary

For `Product.purchase(confirmIn scene: some UIScene, options: Set<...> =
[]) async throws → PurchaseResult`, the generator emits a public C#
`PurchaseAsync<T0>(...)` method whose underlying P/Invoke targets the
**mangled Swift async symbol directly** with `[UnmanagedCallConv(CallConvs
= new[] { typeof(CallConvSwift) })]`, instead of routing through a
`@_cdecl`-annotated Swift wrapper that bridges the async callback.

This presents a working-looking API to consumers but the call site is
ABI-unsafe across the Swift async runtime boundary. The sibling overload
that takes `confirmIn vc: UIViewController` IS cdecl-wrapped correctly.

The `binding-emission-report.json` describes this as "native/direct
escapes" rather than full wrapper coverage.

Distinct from `bug-0.10.0-missingwrappersymbol-after-wrapper-emit.md`
(Round 3 / I-5): I-5 reports `MissingWrapperSymbol` AND emits no API; this
bug emits a working-looking API but with the wrong call shape.

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.2
- .NET SDK 10.0.107, workload 10.0.107
- Xcode 26.3 / Swift 6.2.x
- macOS 26.x, arm64
- Source framework: system StoreKit (iOS 26.2.2)

## Repro

```bash
sed -n '24276,24345p' apple-frameworks/StoreKit2/obj/Debug/net10.0-ios26.2/swift-binding/StoreKit2.cs
jq '.strategy' apple-frameworks/StoreKit2/obj/Debug/net10.0-ios26.2/swift-binding/binding-emission-report.json
```

Generated C# (StoreKit2.cs:24282):

```csharp
public Task<StoreKit2.Product.PurchaseResult> PurchaseAsync<T0>(
    T0 viewController, IEnumerable<StoreKit2.Product.PurchaseOption> options,
    CancellationToken cancellationToken = default)
    where T0 : ISwiftObject
{
    var t = new TaskCompletionSource<...>();
    PInvoke_purchase_…(/* options, scene metadata, ... */);   // [BAD: direct]
    return t.Task;
}

[UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
[DllImport("StoreKit", EntryPoint = "$s8StoreKit7ProductV8purchase9confirmIn7options...async")]
private static extern unsafe void PInvoke_purchase_…(/* mangled Swift async signature */);
```

Compare the cdecl-wrapped sibling (StoreKit2.cs:24465):

```csharp
public Task<StoreKit2.Product.PurchaseResult> PurchaseAsync(
    UIKit.UIViewController viewController, IEnumerable<...> options,
    CancellationToken cancellationToken = default)
{
    var t = new TaskCompletionSource<...>();
    var __callback = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)
        &purchase_callback_…;
    PInvoke_purchase_…(/* uses cdecl wrapper symbol */, GCHandle.ToIntPtr(__h),
        __callback);
    return t.Task;
}

[DllImport("StoreKitSwiftBindings", EntryPoint = "SBW_StoreKit_Product_purchase_...")]
private static extern void PInvoke_purchase_…(IntPtr ctx, IntPtr handle,
    delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> callback);
```

The first form has direct CallConvSwift to the Swift async ABI; the
second form goes through the SDK's emitted Swift wrapper that bridges
the Swift async to a cdecl callback.

`binding-emission-report.json:7`:

```json
{
  "strategy": {
    "purchase(confirmIn:options:)/UIScene": "native-direct",
    "purchase(confirmIn:options:)/UIViewController": "wrapper-cdecl"
  }
}
```

## Native ground truth

```text
swiftinterface (StoreKit framework, line ~1652-1657):
  @MainActor public func purchase(
    confirmIn scene: some UIScene,
    options: Set<Product.PurchaseOption> = []
  ) async throws -> Product.PurchaseResult

  public func purchase(
    confirmIn viewController: UIKit.UIViewController,
    options: Set<Product.PurchaseOption> = []
  ) async throws -> Product.PurchaseResult
```

Both are async-throwing in Swift; both should emit through cdecl
wrappers.

## Hypothesis

The wrapper-emission pipeline appears to skip the `@_cdecl` wrapper for
`some Protocol` parameter shapes (likely because the wrapper would need to
emit a generic Swift function that captures the metatype, which the
emitter doesn't yet support). When the wrapper is skipped, the C# emitter
falls back to the direct-CallConvSwift path that exists for sync APIs but
that hasn't been validated for Swift async.

Likely fix shape: emit a generic Swift wrapper that takes the metatype as
an explicit parameter and forwards to the Swift async overload, then
bridge the result through the existing cdecl-callback infrastructure.

Alternative: explicitly skip the overload entirely (`UnsupportedSignature`)
rather than emit it as ABI-unsafe.

## Impact

- **Calling the overload may crash or return wrong values.** Swift async
  ABI is not stable for direct PInvoke; continuation tracking, executor
  hopping, and error propagation are all under-specified at this
  boundary.
- **Consumers see a working-looking API.** No `[Obsolete]`, no
  `[UnsupportedSwiftType]` marker. The overload appears safe.
- Combined with M-3 (over-broad generic constraint), the same overload
  has *two* defects: any `ISwiftObject` is accepted, and the call shape
  is ABI-unsafe.

## Workaround

Consumer side: prefer the cdecl-wrapped `purchase(confirmIn:
UIViewController, options)` overload over the `some UIScene` one. The
UIViewController shape covers ~all consumer needs since you can ask any
UIScene for its key window's root view controller.

## Severity

**Correctness — High.** API looks safe; isn't. Adjacent to but distinct
from
[`bug-0.10.0-missingwrappersymbol-after-wrapper-emit.md`](./bug-0.10.0-missingwrappersymbol-after-wrapper-emit.md):
that one drops the API entirely; this one emits it with an unsafe call
shape.

Cross-reference in
[SDK-0.10.0-BLOCKERS.md](../../swift-dotnet-packages/SDK-0.10.0-BLOCKERS.md)
under Round 4 / M-6.

## Status — DEFERRED to Bundle 7

Bundle 02 wired up `WrapperValidation.IsSkippedWrapperDirectPInvoke` and the
matching skip site in `MethodHandler` as a cross-bundle scaffold, but the
naive trigger ("async with no wrapper flags in xcframework mode") over-fires:
it matches both the genuine ABI-unsafe shape (this bug — `confirmIn: some
UIScene` existential param) AND simple-signature async on generic class
parents that empirically work with the legacy `CallConvSwift` direct path
(e.g. `BindingTests` `AsyncGenericContainer<T>.processAsync`,
`fetchOrThrow`). Both reach the predicate with identical method-flag state,
so flag-only inspection cannot tell them apart.

Bundle 7 owns the refined detector — a signature-level discriminator that
recognises the genuine ABI-unsafe shapes (existential params, complex
non-blittable returns) without catching the working simple-signature async
path. Bundle 7 also folds the SB0001 / `WorkaroundRecommendations`
integration in `gap-0.10.0-misleading-unsupported-attribute-on-working-members`.

Until Bundle 7 lands, the predicate returns `false` and the legacy
`@_silgen_name` + `CallConvSwift` direct-PInvoke path stays in place — same
behaviour as `main`. The pinned scaffold tests live in
`AbiSafetyTests.IsSkippedWrapperDirectPInvoke_*` and
`SilgenNameTrampolineTests.Async_WithClosureParam_NoConversion_LegacyFallbackEmitted_Bundle7Deferred`.
