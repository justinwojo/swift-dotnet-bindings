# Bug: Result-style enum cases with payloads emit `Tag` but no factory or `TryGet*` extractor

> SDK 0.10.0 generator correctness bug. Discovered 2026-05-05 during a
> consumer-experience audit of
> [SwiftBindings.Stripe](https://github.com/justinwojo/swift-dotnet-packages)
> (StripeFinancialConnections 26.2.1).

## Summary

For a Swift enum case with an associated value (`case completed(session:
FinancialConnectionsSession)`), the generator normally emits three things
on the C# wrapper:

1. A `CaseTag` arm (e.g. `CaseTag.Completed`).
2. A factory method (`NewCompleted(FinancialConnectionsSession session)`)
   to construct an instance.
3. An extractor (`bool TryGetCompleted(out FinancialConnectionsSession session)`)
   to unpack the payload from a Swift-produced instance.

In StripeFinancialConnections, the success cases of `Result` and
`TokenResult` get only #1 — the tag accessor — without #2 or #3. The
`failed` case in the same enum gets all three, so the omission is
per-case rather than per-enum.

A C# consumer who receives `Result.completed(session: ...)` from a
successful Financial Connections flow can detect that it succeeded
(`Tag == Completed`) but has no way to extract the `session` payload.
The flow's primary success-path output is unreachable.

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.2
- .NET SDK 10.0.107, workload 10.0.107
- Xcode 26.3 / Swift 6.2.x
- macOS 26.x, arm64
- Source library: stripe-ios 26.2.1 (StripeFinancialConnections)

## Repro

```bash
sed -n '880,910p' libraries/Stripe/StripeFinancialConnections/obj/Debug/net10.0-ios/swift-binding/StripeFinancialConnections.cs
sed -n '1170,1200p' libraries/Stripe/StripeFinancialConnections/obj/Debug/net10.0-ios/swift-binding/StripeFinancialConnections.cs
```

```csharp
// StripeFinancialConnections.cs:894 — Result enum
public partial class Result : ISwiftObject, …
{
    public enum CaseTag { Completed, Canceled, Failed }
    public CaseTag Tag => …;

    public static Result NewCanceled() => …;
    public static Result NewFailed(Swift.Foundation.AnyError error) => …;
    public bool TryGetFailed(out Swift.Foundation.AnyError error) { … }
    // ← no NewCompleted, no TryGetCompleted
}

// StripeFinancialConnections.cs:1183 — TokenResult enum (same shape)
public partial class TokenResult : ISwiftObject, …
{
    public enum CaseTag { Completed, Canceled, Failed }
    public CaseTag Tag => …;

    public static TokenResult NewCanceled() => …;
    public static TokenResult NewFailed(Swift.Foundation.AnyError error) => …;
    public bool TryGetFailed(out Swift.Foundation.AnyError error) { … }
    // ← no NewCompleted, no TryGetCompleted
}
```

Native:

```text
swiftinterface (StripeFinancialConnections line 195-205):
  public enum Result {
    case completed(session: FinancialConnections.FinancialConnectionsSession)
    case canceled
    case failed(error: any Swift.Error)
  }
  public enum TokenResult {
    case completed(result: (token: STPToken,
                            session: FinancialConnections.FinancialConnectionsSession))
    case canceled
    case failed(error: any Swift.Error)
  }
```

The `failed` case payload is `any Swift.Error` (which lowers to
`Swift.Foundation.AnyError`) — and that case got both factory and
extractor. The `completed` case payload is a structured Swift type
(`FinancialConnectionsSession` or a 2-tuple); that case got neither
factory nor extractor.

## Hypothesis

The factory-and-extractor emitter likely has a guard that bails out when
the case payload is "complex" — possibly:

- A nominal Swift type from another product (`FinancialConnectionsSession`
  is owned by the same module so this *probably* isn't it, but
  `STPToken` in the `TokenResult` 2-tuple lives in `StripePayments`).
- A tuple-typed payload (the `TokenResult.completed` case is a
  named-element tuple).
- Something that requires PInvoke shape the emitter doesn't handle for
  associated-value enum cases.

The pattern is that simple/erased payload types (`AnyError`) get the
emission; structured payloads don't. The fix is the
"payload-shape switch" that selects the marshalling strategy needs to
have a fallback for structured types, even if the fallback is a
`SwiftMarshal.MarshalFromSwift<T>(IntPtr)` round-trip.

Adjacent observation: the `canceled` case (no payload) gets a `NewCanceled`
factory but no extractor — which is correct; an empty case has nothing to
extract. So the case-classification logic does run; it just has a hole
for structured-payload cases.

## Native ground truth — what the consumer needs

```swift
// Consumer-side Swift idiom:
sheet.present { result in
    switch result {
    case .completed(let session):
        await api.persist(session.accounts)
    case .canceled:
        showCanceled()
    case .failed(let error):
        log(error)
    }
}
```

The `.completed(let session)` extraction is the primary success path. In
C# today the equivalent code can detect `result.Tag == CaseTag.Completed`
but cannot get the `session` value out. The whole vertical's success
case is unreachable from .NET.

## Impact

- **Vertical unusable on success path.** `Result.completed(session:)`
  carries the `FinancialConnectionsSession`; without extraction, the
  consumer cannot retrieve the connected accounts. Same for
  `TokenResult.completed(result:)` — the `STPToken` is unreachable.
- **Asymmetric failure shape.** Code can handle errors and
  cancellations but not successes. This is the inverse of what the
  user wants and surfaces as "the SDK only ever reports failures."
- **Library scope.** Any enum case with a structured (non-erased)
  payload. Worth auditing across the codebase for other instances —
  every result-style enum that distinguishes success / failure /
  cancellation is suspect.

## Workaround

None purely consumer-side. The Swift wrapper exposes no other accessor.
Possible escape hatches:

- Reach into the underlying `Payload` `IntPtr` and call
  `SwiftMarshal.MarshalFromSwift<FinancialConnectionsSession>(payload)`
  manually — fragile and depends on the layout the generator would emit.
- Bypass `Result` / `TokenResult` entirely and call lower-level
  Stripe APIs that return the session directly — if any exist.

The proper fix is in
[swift-dotnet-bindings](https://github.com/justinwojo/swift-dotnet-bindings):
the case-emission code path needs to handle structured payloads.

## Severity

**Correctness — High.** Combined with the closure-skip on
`EmbeddedComponentManager`, FinancialConnections is essentially
unusable from .NET on its happy path: the consumer can present the
sheet, observe that the user finished, and then has no way to obtain
what the user produced.

Cross-reference in
[SDK-0.10.0-BLOCKERS.md](../../swift-dotnet-packages/SDK-0.10.0-BLOCKERS.md)
under Round 3 / I-4.
