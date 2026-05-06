# bug-0.10.0: Auto-emitted protocol-proxy class type doesn't inherit the protocol's `@available`

## Status

Identified during Bundle 09 (Family-F `@available` emission, 2026-05-06).
Filed as a follow-on; not in Bundle 09 scope. Natural fix-site is the
proxy-dispatch / conformance-lowering emitter (Bundle 6 territory).

## Symptom

When the generator emits a C# proxy class for a Swift protocol that
carries `@available(iOS 15.0, *)` (or any platform/version gate), the
proxy class declaration and its constructors do **not** inherit the
protocol's `[SupportedOSPlatform("ios15.0")]`. The protocol interface
itself does carry the attribute (Bundle 09's Family-F F-2 fix landed
that), but the proxy class type is bare.

This produces two distinct user-facing failures:

1. **Compile-time DX (CA1416)**: under an iOS-15 baseline TFM, every
   proxy-class internal call site trips CA1416 because the call site
   cannot statically prove the platform gate is satisfied. Bundle 09
   suppresses CA1416 in `BindingTests/CompileCheck.csproj` and
   `RuntimeTestsApp.csproj` to ship the parser fix; the suppression is
   an explicit acknowledgement that this gap exists.
2. **Runtime crash (residual F-2 surface)**: a consumer running on an
   OS version below the protocol's gate (e.g. iOS 14) and calling
   through the proxy class type would still crash, because the proxy
   class itself isn't gated. Bundle 09's Family-F F-2 fix closes the
   parser-side drop on the protocol declaration, but the proxy-class
   inheritance is a sibling generator-side issue.

## Repro pattern

A Swift protocol declared with `@available` and at least one bare
protocol requirement (no explicit access modifier):

```swift
@available(iOS 15.0, *)
public protocol PaymentProcessor {
    func charge(amount: Decimal)
}
```

Generated C# (post-Bundle-09) — the interface IS gated, the proxy is NOT:

```csharp
[SupportedOSPlatform("ios15.0")]
public interface IPaymentProcessor { ... }

// Missing [SupportedOSPlatform("ios15.0")]
internal class PaymentProcessorProxy : IPaymentProcessor {
    public PaymentProcessorProxy(IntPtr csVTHandle) { ... }
    public void Charge(decimal amount) { ... }
}
```

## Fix site

Conformance / proxy-dispatch emitter. Whatever code path emits the
proxy class declaration should inherit `[SupportedOSPlatform]` /
`[UnsupportedOSPlatform]` / `[Obsolete]` attributes from the source
protocol declaration onto the proxy class type and its constructors,
mirroring the parser's per-overload disambiguation key model from
Bundle 09's `MemberSignatureNormalizer`.

## Coverage

Layer A. Add a Swift fixture in
`BindingTests/Sources/SwiftBindingsTestLib/EdgeCases/` (or extend
`AvailabilityFamilyF.swift`) that declares an `@available`-gated
protocol with at least one default-modifier requirement, and a C#
attribute-reflection assertion that the auto-emitted proxy class type
carries the protocol's `[SupportedOSPlatform]`. Once the fix lands,
remove the CA1416 suppression in `CompileCheck.csproj` and
`RuntimeTestsApp.csproj` (Bundle 09 added these explicitly so the
suppression is removable when the fix lands).

## Cross-bundle context

Bundle 09 documents the same gap inline in
`BindingTests/Sources/SwiftBindingsTestLib/EdgeCases/AvailabilityFamilyF.swift`
under "MARK: F-2 — covered by unit test only". This bug doc is the
durable tracking record so Bundle 6 (or a Phase 5 cleanup pass) can
sweep it.
