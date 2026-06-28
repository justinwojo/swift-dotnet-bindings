# ProximityReader — Binding Audit

- **Package**: SwiftBindings.Apple.ProximityReader v26.2.8   **Mode**: apple   **TFM(s)**: net10.0-ios26.2
- **Native**: Apple ProximityReader.framework (iOS 15.4+, Mac Catalyst 17.0+)
- **Audited at**: main 1e8c27a, generated 2026-06-27T19:50:01Z

## Verdict

Clean binding with 100% type emission and only 3 explicitly skipped members (2 synthesized-Codable stubs and 1 generic-protocol-constrained method). The full Tap to Pay async flow — `PrepareAsync` → `ReadPaymentCardAsync` / `ReadVASAsync` / `CapturePINAsync` — surfaces as proper `Task<T>` with `CancellationToken` and a Swift `AsyncSequence` event stream mapped to `IAsyncEnumerable<Event>`. The only functional gap is `MobileDocumentReaderSession.requestDocument<T>`, which requires type erasure for protocols with associated types; the binding ships a working workaround (`MobileDocumentAnyOfDataRequest`). Tests are correctly scoped to what's reachable without Tap to Pay entitlements (metadata + enum), but struct construction/property-round-trip and error-discrimination tests can be added at zero infrastructure cost.

## 1. Coverage

### Counts

| Dimension | Count | Notes |
|---|---|---|
| TotalTypes | 104 | |
| EmittedTypes | 104 | 100% |
| SkippedTypes | 0 | |
| TotalMembers (Swift declarations) | 552 | |
| EmittedMembers | 365 | 66% of raw Swift declarations |
| SkippedMembers (explicit) | 3 | |
| SynthesizedMembers (generator-added) | 441 | `Dispose`, `Equals`, `GetHashCode`, `==`/`!=`, boxing helpers, conformance stubs |

### Reconciling 365/552 — this is NOT 187 missing APIs

`TotalMembers=552` counts every Swift declaration the generator ingests: property getter/setter stubs, init overloads, protocol witness requirement entries, and `@available`-guarded variants. `EmittedMembers=365` is the count of C# API surface entries that appear in the binding. The 184-member gap has three causes:

1. **Swift members collapsed at emit time**: init overloads on `PaymentCardTransactionRequest`, `PaymentCardReader.Token`, etc. map to one C# constructor rather than N overloads. The Swift interface counts each; the emitted binding counts one.
2. **Accessor stub counting**: Swift property `get`/`set` pairs are two declarations in `TotalMembers` but one C# `Property` in `EmittedMembersByKind`. With 272 emitted properties, ~272 "extra" Swift accessor stubs alone explain most of the gap.
3. **Protocol witness requirements** counted at the Swift declaration level but not re-emitted as standalone C# members — they surface as interface conformance, not additional API entries.

`SynthesizedMembers=441` are *generator-added* members with no Swift counterpart — they inflate the C# surface beyond what `TotalMembers` describes. The file has 466 `[SupportedOSPlatform]`/`[ObsoletedOSPlatform]` annotations, reflecting how many APIs are gated across the iOS 15.4→iOS 26.0 span.

### Skipped members — breakdown

| Reason | Count | Members | Classification |
|---|---|---|---|
| SynthesizedCodable | 2 | `StoreAndForwardBatch.encode`, `StoreAndForwardBatch.StoredPaymentCardReadResult.encode` | **(a) Correctly excluded** — synthesized `Encodable` stubs pruned by design |
| GenericProtocolConstraint | 1 | `MobileDocumentReaderSession.requestDocument<Request: MobileDocumentRequest>` | **(b) Real gap** — see below |

### Real gaps

**`MobileDocumentReaderSession.requestDocument<T: MobileDocumentRequest>`** (skip reason: `GenericProtocolConstraint`)

Swift signature: `func requestDocument<Request>(_ request: Request) async throws -> Request.Response where Request: MobileDocumentRequest`

The associated-type PAT constraint (`Request.Response`) requires type erasure to bind from C#. **Impact**: the typed response return is lost; consumers must use the type-erased `MobileDocumentAnyOfDataRequest` / `MobileDocumentAnyOfRawDataRequest` path (both emitted) and pattern-match on the returned data themselves. **Workaround quality**: adequate — Apple itself recommends `AnyOfDataRequest` for multi-element reads. **Generator fix value**: Medium. Requires the generator to emit a type-erased overload (e.g. `ReadDocumentAsync(IMobileDocumentDataRequest) → Task<MobileDocumentResponse>`) by introducing a wrapper Swift shim. Non-trivial but the `MobileDocumentRequest` protocol family is already fully emitted.

### Prioritized generator unlocks

| # | API | Reason | Value | Tractability |
|---|---|---|---|---|
| 1 | `MobileDocumentReaderSession.requestDocument<T>` | GenericProtocolConstraint — associated-type PAT | Medium | Medium — needs type-erased shim + new emit path |

---

## 2. C# Quality

### Naming and shape — clean

- All public names PascalCase; no leaked Swift mangling. `PaymentCardReader`, `PaymentCardReaderSession`, `MobileDocumentReader`, `MobileDocumentReaderSession`, `ProximityReaderDiscovery` all render predictably.
- Nested types (`PaymentCardReader.Event`, `PaymentCardReader.Token`, `PaymentCardReader.UpdateEvent`, `PaymentCardReaderSession.Event`, `PaymentCardReaderSession.PINToken`) follow the Swift namespace nesting faithfully (`ProximityReader.cs:3561`, `4804`, `4069`, `10526`, `11646`).
- Inheritance is correct: `StoreAndForwardPaymentCardReaderSession : PaymentCardReaderSession` (`ProximityReader.cs:13891`).
- Extension enum: `MobileDocumentReaderErrorExtensions` (`ProximityReader.cs:15421`) carries `GetErrorDescription()` cleanly alongside the plain `MobileDocumentReaderError` int enum (`ProximityReader.cs:15406`).

### Async — excellent

Every async operation in the Tap to Pay flow surfaces as a proper `Task<T>` with a default `CancellationToken` parameter. All overloads are present:

| C# method | Line | Returns |
|---|---|---|
| `PaymentCardReader.PrepareAsync(Token, ct)` | 5711 | `Task<PaymentCardReaderSession>` |
| `PaymentCardReader.PrepareAsync(Token, Action<UpdateEvent>?, ct)` | 5901 | `Task<PaymentCardReaderSession>` |
| `PaymentCardReader.PrepareStoreAndForwardAsync(ct)` | 6091 | `Task<StoreAndForwardPaymentCardReaderSession>` |
| `PaymentCardReader.IsAccountLinkedAsync(Token, ct)` | 5197 | `Task<bool>` |
| `PaymentCardReader.LinkAccountAsync(Token, ct)` | 5367 | `Task` |
| `PaymentCardReader.RelinkAccountAsync(Token, ct)` | 5538 | `Task` |
| `PaymentCardReader.GetReaderIdentifierAsync(ct)` | 3345 | `Task<string>` |
| `PaymentCardReaderSession.ReadPaymentCardAsync(PaymentCardTransactionRequest, ct)` | 12186 | `Task<PaymentCardReadResult>` |
| `PaymentCardReaderSession.ReadPaymentCardAsync(PaymentCardTransactionRequest, Action<Event>?, ct)` | 12391 | `Task<PaymentCardReadResult>` |
| `PaymentCardReaderSession.ReadPaymentCardAsync(PaymentCardVerificationRequest, ct)` | 12594 | `Task<PaymentCardReadResult>` |
| `PaymentCardReaderSession.ReadPaymentCardAsync(PaymentCardVerificationRequest, Action<Event>?, ct)` | 12799 | `Task<PaymentCardReadResult>` |
| `PaymentCardReaderSession.ReadVASAsync(VASRequest, ct)` | 13002 | `Task<VASReadResult>` |
| `PaymentCardReaderSession.ReadVASAsync(VASRequest, Action<Event>?, ct)` | 13208 | `Task<VASReadResult>` |
| `PaymentCardReaderSession.ReadPaymentCardAsync(request, vasRequest, stopOnVAS, ct)` | 13395 | `Task<(PaymentCardReadResult?, VASReadResult?)>` |
| `PaymentCardReaderSession.ReadPaymentCardAsync(request, vasRequest, stopOnVAS, Action<Event>?, ct)` | 13592 | `Task<(PaymentCardReadResult?, VASReadResult?)>` |
| `PaymentCardReaderSession.CancelReadAsync(ct)` | 12006 | `Task<bool>` |
| `PaymentCardReaderSession.CapturePINAsync(PINToken, string, ct)` | 13804 | `Task<PaymentCardReadResult>` |
| `PaymentCardReaderSession.DeclineAsync(ct)` | 14102 | `Task` |
| `StoreAndForwardPaymentCardReaderSession.StatusAsync(ct)` | 14284 | `Task<StoreAndForwardStatus>` |

The combined payment+VAS read returns a C# value tuple `(PaymentCardReadResult?, VASReadResult?)` — correct and idiomatic.

**Swift `AsyncSequence` → `IAsyncEnumerable<T>`**: `PaymentCardReader.Events` (`ProximityReader.cs:3168`) returns `IAsyncEnumerable<PaymentCardReader.Event>` backed by a `SwiftAsyncStream` with producer-side cancellation wired through `SBW_CancelTask` — the Swift producer gets a task cancellation when the C# consumer disposes the enumerator. This is the correct pattern.

### Nullability — clean

File opens with `#nullable enable` (`ProximityReader.cs:1`). Swift optionals are mapped to nullable C# types throughout (`string?`, `System.DateTimeOffset?`, `Contacts.CNPostalAddress?`). No missing `?` annotations observed in spot checks.

### Lifetime — clean

All reference types implement `IDisposable` (`PaymentCardReader`, `PaymentCardReaderSession`, `MobileDocumentReader`, `MobileDocumentReaderSession`, `VASRequest`, `ProximityReaderDiscovery`, `PaymentCardReaderStore`). Value types use `SwiftSafeHandle<T>` + `IDisposable` (`PaymentCardReaderError`, `PaymentCardReadResult`, `PaymentCardTransactionRequest`, etc.). `SwiftDisposeScope.TryRegister` is called in `NewFromPayload` factories. Disposal doc-comment is consistent: *"Use a 'using' block or call Dispose(). Failure to dispose may leak native memory."*

### Ergonomic notes

- **`PaymentCardReaderError` discrimination**: `TryGetInvalidReaderToken(out string?)`, `TryGetPrepareFailed(out string?)`, `TryGetDeviceBanned(out DateTimeOffset?)`, etc. (`ProximityReader.cs:2684`) follow the standard Swift enum case-extraction pattern. Payloadless cases are static `Lazy<T>` properties (e.g., `PaymentCardReaderError.NotAllowed`). Clean.
- **`Id` property deprecated correctly**: `[ObsoletedOSPlatform("ios16.0", "Use ObjectIdentifier instead")]` on both `PaymentCardReader.Id` and `PaymentCardReaderSession.Id` (`ProximityReader.cs:3118`, `10278`).
- **ObjC cross-framework bridge**: `CNPostalAddress` from `Contacts.framework` is correctly bridged via `ObjCRuntime.Runtime.GetINativeObject<Contacts.CNPostalAddress>` for `MobileDriversLicenseDataRequest.*` and `MobileNationalIDCardDataRequest.*` address properties (`ProximityReader.cs:17824`, `24257`). The `ObjCPrefixBridges` entry confirms generator awareness.

---

## 3. Test Coverage

### Counts and what's tested

**File**: `tests/Tests.cs` (101 lines). No `Skip()` calls. 11 distinct test cases:

| # | Case name | What it tests | Depth |
|---|---|---|---|
| 1–9 | `{Type} metadata` (9 types) | `SwiftObjectHelper<T>.GetTypeMetadata()` non-zero handle | Weak — metadata load only |
| 10 | `MobileDocumentReaderError values` | Enum int values (`Unknown==0`, `InvalidResponse==10`) | Weak — value equality only |
| 11 | `MobileDocumentReaderError.GetErrorDescription` | P/Invoke round-trip via `GetErrorDescription()` extension | Moderate — exercises the cdecl stub, nullable result |

### Depth verdict: Weak — appropriate given entitlement constraints

ProximityReader requires `com.apple.developer.proximity-reader.payment.acceptance` and an enrolled Tap to Pay merchant account — a live `PaymentCardReader` cannot be instantiated without device provisioning and Apple backend credentials. `MobileDocumentReader` (`requestDocument`) similarly requires identity-reader hardware. Metadata-load + enum tests are the correct pattern here; the existing test comment ("ProximityReader is permission + session heavy") accurately reflects this.

### Untested surface worth adding (zero additional infrastructure)

The following can be tested on the Simulator without any entitlement or live session:

| Proposed test | API | What to assert |
|---|---|---|
| `PaymentCardReader.IsSupported` | `PaymentCardReader.IsSupported` (static bool, `ProximityReader.cs:3070`) | Returns `false` on Simulator; doesn't throw |
| `PaymentCardTransactionRequest` round-trip | `new PaymentCardTransactionRequest(NSDecimalNumber.FromDouble(10.0), "USD", TransactionType.Purchase)` (`ProximityReader.cs:7664`) + `.Amount`, `.CurrencyCode`, `.Type` | Properties echo constructor args |
| `PaymentCardReaderError` case discrimination | `PaymentCardReaderError.InvalidReaderToken("tok123").TryGetInvalidReaderToken(out var s)` (`ProximityReader.cs:2684`) | `TryGet` returns `true`, `s == "tok123"` |
| `PaymentCardReaderError` payloadless case | `PaymentCardReaderError.NotAllowed` tag check | Static lazy initializer non-throw, `Tag == CaseTag.NotAllowed` |
| `VASRequest` construction | `new VASRequest(...)` + property access | No crash on Simulator metadata init path |

These would upgrade the test suite from pure metadata probes to lightweight ABI round-trips without requiring any session, entitlement, or device.

---

## Action Items

| # | Dimension | Finding | Recommendation | Effort | Value |
|---|---|---|---|---|---|
| 1 | Coverage | `MobileDocumentReaderSession.requestDocument<T: MobileDocumentRequest>` skipped (GenericProtocolConstraint) | Generator unlock: emit a type-erased overload via a Swift wrapper shim (`any MobileDocumentRequest` → `MobileDocumentResponse`); workaround (`AnyOfDataRequest`) already in place | Medium | Medium |
| 2 | Tests | No struct construction / property round-trip tests | Add `PaymentCardTransactionRequest` ctor + `Amount`/`CurrencyCode`/`Type` round-trip test | Low | Medium |
| 3 | Tests | `PaymentCardReaderError` case discrimination untested | Add `InvalidReaderToken("x").TryGetInvalidReaderToken(out var s)` test + payloadless `NotAllowed` tag check | Low | Medium |
| 4 | Tests | `PaymentCardReader.IsSupported` not covered | Add static bool check (expect `false` on Simulator) | Trivial | Low |
