# StoreKit2 — Binding Audit

- **Package**: SwiftBindings.Apple.StoreKit2 v26.2.8   **Mode**: apple   **TFM(s)**: net10.0-ios26.2
- **Native**: Apple StoreKit framework (module name `StoreKit`) — Xcode 26.2 SDK
- **Audited at**: swift-dotnet-packages main `1e8c27a`, generated 2026-06-27T12:49:54Z

## Verdict

Strong coverage (78/78 types, 81.3% members). All five core StoreKit 2 async flows are usable in C#: `Product.ProductsAsync`, `Product.PurchaseAsync` (three overloads), `Transaction.CurrentEntitlements`/`Updates`/`All`/`Unfinished` (all as `IAsyncEnumerable<VerificationResult<Transaction>>`), `Transaction.FinishAsync`, and `AppStore.SyncAsync`. Swift `AsyncSequence` bridges cleanly to .NET `IAsyncEnumerable<T>` for all five emitted sequence types. Real gaps are narrow: one `purchase` overload missing a Swift wrapper, `VerificationResult.payloadValue` typed as `Swift.AnyType` (workaround: `TryGetVerified(out T)`), `Status.all` stream with array element type unsupported, and some name stutter on raw-value types ending in "Type". Test coverage is wide but uniformly shallow — no async flow is actually awaited.

---

## 1. Coverage

| Dimension | Emitted | Total | % |
|---|---|---|---|
| Types | 78 | 78 | **100 %** |
| Members (Swift API) | 421 | 518 | **81.3 %** |
| Synthesized members (generator-added) | 369 | — | — |
| Skipped members | 22 | — | — |

### Skip-reason breakdown

| Reason | Count | Classification |
|---|---|---|
| `UnsupportedType` | 12 | Mixed — see detail |
| `SynthesizedCodable` | 2 | (a) Correctly excluded |
| `AnyTypeFallback` | 2 | (b) Real gap |
| `UnsupportedSignature` | 4 | (b) 3 minor / 1 real gap |
| `UnsupportedAsyncStream` | 1 | (b) Real gap |
| `DuplicateSignature` | 1 | (a) Correctly excluded |

**`UnsupportedType` (12)** — split result:
- 9× `VerificationResult.*` constrained-extension properties (`jwsRepresentation`, `headerData`, `payloadData`, `signatureData`, `signature`, `signedData`, `signedDate`, `deviceVerification`, `deviceVerificationNonce`) are suppressed at the *open-generic* class level but **are emitted as closed-generic C# extension methods** for every meaningful specialization (`VerificationResult<Transaction>`, `VerificationResult<AppTransaction>`, `VerificationResult<RenewalInfo>`). These are **(a) correctly handled** — the report counts them as "skipped" on the generic class, but the surface is fully covered via `GetJwsRepresentation()`, `GetHeaderData()`, etc.
- `Product.priceFormatStyle` — `Foundation.Decimal.FormatStyle.Currency` cross-module type not bound. `Product.DisplayPrice` (`string`) and `Product.Price` (`Foundation.NSDecimalNumber`) are emitted; this is **(a) minor / foundation gap**.
- `Product.subscriptionPeriodFormatStyle` — `Foundation.Date.ComponentsFormatStyle` not bound. **(a) minor / foundation gap**.
- `Product.SubscriptionPeriod.Unit.<` — operator on enum type, unsupported class of operator. **(a) correctly excluded**.

**`AnyTypeFallback` (2)** — real gaps:
- `VerificationResult.payloadValue`, `VerificationResult.unsafePayloadValue` (lines 4174–4175, 4945–4946, 5097–5098, 5249–5250): both properties resolve to `Swift.AnyType` at the open-generic level AND remain unsupported at each closed specialization. There is no synchronous "give me the payload regardless of verification status" path in C#. **(b) Ergonomic gap** — workaround: `TryGetVerified(out T)` covers the verified path; `TryGetUnverified(out T, out VerificationError)` covers the unverified path. Tractable: the constrained-extension pattern already works for JWS/header data; adding `GetPayloadValue()`/`GetUnsafePayloadValue()` extensions for each specialization would close the ergonomic gap.

**`UnsupportedAsyncStream` (1)** — real gap:
- `Product.SubscriptionInfo.Status.all` (line 23260): `AsyncStream<[Product.SubscriptionInfo.Status]>` — the *element* type is a Swift array, which the generator doesn't support as an `AsyncStream` payload. `Status.Updates` (same type, live-update semantics) IS emitted and covers the common subscription-monitoring use case. `all` adds historical/all-subscriptions enumeration. **(b) Secondary subscription gap** — `Updates` is the idiomatic listener; `all` matters for apps that want a point-in-time snapshot of every subscription group status. Effort: generator needs to handle array-element `AsyncStream` as `IAsyncEnumerable<IReadOnlyList<T>>`.

**`UnsupportedSignature` (4)**:
- `Product.SubscriptionPeriod.Unit.formatted`, `Product.SubscriptionPeriod.formatted`, `Product.SubscriptionPeriod.dateRange` — all have `FormatStyle`-protocol–typed placeholder arguments; generator can't emit. **(a) Minor** — `DisplayPrice` string covers formatted price display.
- `Product.purchase` — ONE overload dropped with "Async method without @_cdecl wrapper — direct CallConvSwift on Swift async ABI is unsafe." **(b) Minor gap** — three other `PurchaseAsync` overloads ARE emitted: `PurchaseAsync()`, `PurchaseAsync(IReadOnlySet<PurchaseOption>)`, `PurchaseAsync(UIViewController, IReadOnlySet<PurchaseOption>)`. The missing one is likely the `UIWindowScene`-typed variant; the `UIViewController` overload covers the equivalent use case.

**`DuplicateSignature` (1)**:
- `Product.SubscriptionPeriod.formatted(arg0:Swift.AnyType, referenceDate:double)` — the generator can't emit both `formatted` overloads when one parameter type resolves to `AnyType`. **(a) Correctly excluded** — the useful `formatted` overload is already skipped via `UnsupportedSignature` anyway.

### Prioritized generator unlocks

| # | API | Skip reason | Value | Effort |
|---|---|---|---|---|
| 1 | `VerificationResult<T>.payloadValue` / `unsafePayloadValue` | AnyTypeFallback | High — common ergonomic path | Medium — closed-specialization extension pattern already exists |
| 2 | `Product.SubscriptionInfo.Status.all` | UnsupportedAsyncStream | Medium — subscription snapshot use case | Medium — generator must handle array-element AsyncStream |
| 3 | Missing `Product.purchase` overload (UIWindowScene) | UnsupportedSignature | Low — UIViewController overload covers it | Low — needs wrapper Swift side |
| 4 | `Product.priceFormatStyle` / `subscriptionPeriodFormatStyle` | UnsupportedType | Low — string `DisplayPrice` fills the gap | Blocked on Foundation binding depth |

---

## 2. C# Quality

### AsyncSequence handling — **strong**

Five distinct `IAsyncEnumerable<T>` types emitted, each with a private `__SbAsyncSequenceImpl` async iterator and a `MakeAsyncIterator()` wired through a `@_cdecl` wrapper:

| C# type | Element type | Powers |
|---|---|---|
| `Transaction.Transactions` | `VerificationResult<Transaction>` | `Transaction.All`, `.CurrentEntitlements`, `.Updates`, `.Unfinished` |
| `Product.SubscriptionInfo.Status.Statuses` | `Product.SubscriptionInfo.Status` | `Status.Updates` |
| `PurchaseIntent.PurchaseIntents` | `PurchaseIntent` | `PurchaseIntent.Intents` |
| `Message.MessagesType` | `Message` | `Message.Messages` (iOS/Catalyst only) |
| `Storefront.Storefronts` | `Storefront` | `Storefront.Updates` |

Standard `await foreach` over any of these works. The iterator disposal chain (line ~1097: `if (iter is IDisposable __sbAsyncIterDisposable)`) handles the Swift iterator's native memory correctly.

### Naming stutter on raw-value types

`Transaction.OfferTypeTypeType` (line 8232) and `Transaction.OfferTypeType` (line 9042): the generator appends `Type` to raw-value container types, which produces triple "Type" stutter when the parent type name already ends in "Type". Concretely: `Transaction.OfferTypeTypeType.Introductory`, `.Promotional`, `.Code`. These are usable but unpleasant; a consumer reading the type name can't easily infer the Swift original (`Transaction.OfferType`). Same pattern creates `ReasonType` (reasonable) and `RevocationReasonType` (fine), but the triple-stutter case is a consumer UX problem.

### `@MainActor` / `PurchaseAsync` footgun — documented, but subtle

`PurchaseAsync` (lines 28562, 28740, 28924) carries `[global::Swift.Runtime.SwiftMainActor]` and calls `MainActorGuard.AssertMainThread()` before the async call site. A C# developer calling `await product.PurchaseAsync()` from a background `Task` will get a `SwiftMainActorException` (or equivalent) at runtime, not at compile time. The XML doc at line 28558 ("call on the platform main thread") is present — this is correctly documented, but the `[SwiftMainActor]` attribute has no compile-time analyzer enforcing it, making it an easy mistake.

### `VerificationResult.payloadValue` — navigable gap

Lines 4174–4175: both `payloadValue` and `unsafePayloadValue` are commented-out as unsupported at every specialization level. C# consumers get `TryGetVerified(out TSignedType value)` (line 4030) and `TryGetUnverified(out TSignedType value0, out VerificationError value1)` (line 4030). These work but require the out-parameter dance that Swift's `payloadValue` avoids. No data is unreachable — the gap is ergonomic.

### Constrained extension methods — slight ergonomic step-down

In Swift, `VerificationResult<Transaction>` exposes `.jwsRepresentation`, `.headerData`, etc. as direct properties. In C#, these become static extension methods: `VerificationResult.GetJwsRepresentation(self)` etc. (e.g. lines 4843, 4854, 4865, 4876). Minor step-down; C# `self.GetJwsRepresentation()` reads fine in practice.

### `SignedDate` epoch conversion

Line 3374: `new System.DateTimeOffset(2001, 1, 1, …).AddSeconds(SignedDate_Get())` — Apple's NSDate reference date (Jan 1, 2001 UTC) correctly converted. `System.DateTimeOffset` is the right C# type. ✓

### Nullability

`Transaction.OfferType?` (line 5889 — `StoreKit2.Transaction.OfferTypeType?`), `Transaction.RevocationReason` nullable, `AppTransaction.AppID: ulong?` — optionals correctly surfaced as nullable C#. No missing-nullable smell found in key transaction properties.

### IDisposable

All Swift struct/value types (e.g. `Transaction`, `Product`, `VerificationResult<T>`, all sequence types) implement `IDisposable` via `SwiftSafeHandle`. Safe handles have finalizers as backstops. ✓

### `Product.Price` type

Line 6640: `Product.Price` is `Foundation.NSDecimalNumber?` — correct for the high-precision monetary amount. `Product.DisplayPrice` (line 15698) is `string` — the pre-formatted locale-aware display string. Both present, correct types. ✓

---

## 3. Test Coverage

**30 distinct test cases** in `Tests.cs` (+ 5 `MetadataTest<T>` calls = 35 total assertions). Platform-conditional cases (#if IOS, #if !TVOS) are counted as present but excluded on non-qualifying platforms.

| Depth category | Count | What they probe |
|---|---|---|
| CaseTag / enum ordinal values | 5 | Correct tag integers for 5 enum types |
| Static singleton accessors | 8 | `StoreKitError.*`, `AppStore.Environment.*`, `AppStore.Platform.*`, `Transaction.ReasonType.*`, `Transaction.OwnershipTypeType.*`, `Product.ProductType.*`, `Transaction.OfferTypeTypeType.*`, `Transaction.RevocationReasonType.*`, `Product.SubscriptionOffer.OfferType.*` |
| AsyncSequence *construction only* | 7 | `Transaction.All/CurrentEntitlements/Unfinished/Updates`, `PurchaseIntent.Intents`, `Message.Messages`, `Storefront.Updates` — **no iteration** |
| Type metadata (handle non-null) | 5 | `AppTransaction`, `Product`, `Transaction`, `Storefront`, `Message`, `PurchaseIntent`, `SubscriptionInfo` |
| Async *dispatch only* (no await) | 2 | `Storefront.GetCurrentAsync`, `AppTransaction.GetSharedAsync` — checks `Task != null`, does not await |
| Error extension method shape | 3 | `RefundRequestError`, `PurchaseError`, `PaymentMethodBindingError` extension methods |
| Property reads | 2 | `AppStore.CanMakePayments`, `AppStore.DeviceVerificationID` |
| Reflection shape check | 1 | `VerificationResult<Transaction>.TryGetVerified/TryGetUnverified` parameter shapes |

**All 30 cases are weak depth** — they verify ABI metadata and construction, but none:
- Awaits a real async result (products, purchase, transaction iteration, sync)
- Enumerates a `Transaction.Transactions` sequence
- Accesses properties on a real `Transaction` or `Product` instance
- Calls `Transaction.FinishAsync()`
- Calls `AppStore.SyncAsync()`
- Exercises `VerificationResult<T>.TryGetVerified` or `TryGetUnverified` with a live value

**Note on the known-not-our-bug items:** `Product.ProductsAsync` returns 0 products in sandbox without ASC/sandbox config — this is confirmed to be ASC config, not a marshalling bug (proven on sim+device). `AppTransaction.GetSharedAsync` in the test dispatch also requires a signed app context to return a real result. These are legitimate reasons the tests don't await real results.

**Most important untested surface:**

1. **AsyncSequence iteration**: create `Transaction.CurrentEntitlements` and `await foreach` over it, asserting the loop runs (even with zero results). This is the single most important ABI path — the `__SbAsyncSequenceImpl` iterator chain is untested. Add at unit level: create the sequence, `await foreach (var _ in seq) { break; }` — confirms `MakeAsyncIterator()` + `NextAsync()` wiring doesn't crash.

2. **`VerificationResult<T>` case dispatch**: in a Sandbox environment, after a product fetch, call `TryGetVerified(out Transaction t)` and assert the bool + the non-null `t.ProductID`. Ties `ProductsAsync` → `PurchaseAsync` → `Transaction.CurrentEntitlements` → `TryGetVerified` → `FinishAsync` into one real flow.

3. **`AppStore.SyncAsync()`**: even if it returns an error in sandbox, asserting the task completes without exception covers the P/Invoke wiring.

4. **`Product.SubscriptionInfo.Status.Updates` enumeration**: sanity-iterate (break after first or on cancellation) to verify the iterator protocol works on the Statuses type.

---

## Action Items

| # | Dimension | Finding | Recommendation | Effort | Value |
|---|---|---|---|---|---|
| 1 | Coverage | `VerificationResult.payloadValue`/`unsafePayloadValue` AnyTypeFallback — no synchronous payload accessor | Add `GetPayloadValue()`/`GetUnsafePayloadValue()` C# extension methods per specialization, mirroring `GetJwsRepresentation()` pattern (lines 4843–4995) | Medium | High |
| 2 | Coverage | `Product.SubscriptionInfo.Status.all` (UnsupportedAsyncStream) — array-element AsyncStream not supported | Generator support for `AsyncStream<[T]>` → `IAsyncEnumerable<IReadOnlyList<T>>`; or a hand-authored Swift wrapper converting to `AsyncStream<T>` element-by-element | Medium | Medium |
| 3 | C# quality | `Transaction.OfferTypeTypeType` name stutter (line 8232) and `Transaction.OfferTypeType` (line 9042) — triple-"Type" names are confusing to consumers | Generator naming rule: when the raw-value-wrapper class would append `Type` to a parent name already ending in `Type`, strip the extra `Type` (or rename the suffix) | Medium | Medium |
| 4 | C# quality | Missing `purchase` overload (comment line 28794) — likely `purchase(confirmIn: UIWindowScene, options:)` | Add `@_cdecl` Swift wrapper; follow the `UIViewController` purchase overload pattern (line 28924) | Low | Low |
| 5 | Tests | Zero async flows awaited; no AsyncSequence iteration tested | Add BindingTests-layer test: `await foreach` over `Transaction.CurrentEntitlements` (break-on-first or CancellationToken timeout); assert no crash and correct element type | Low | High |
| 6 | Tests | `AppStore.SyncAsync()` never called | Add task-dispatch + await test (in sandbox it likely throws; assert the throw is a `SwiftError`, not a crash) | Low | Medium |
| 7 | Tests | `VerificationResult<T>` case dispatch never called with a live value | Long-term: sandbox StoreKit config + real product fetch in BindingTests; short-term: at minimum, reflection shape test for `GetJwsRepresentation()` extension signature | Low | Medium |
