# SDK 0.11.0 — residual gaps after the post-residual fix wave

> Generated 2026-05-16 from a re-validation pass against
> [swift-dotnet-packages](https://github.com/justinwojo/swift-dotnet-packages)
> after the 4-session fix wave planned in
> [`sdk-0.11.0-residual-gaps.md`](sdk-0.11.0-residual-gaps.md) landed.
> Method: file-level spot-check of each of the 10 prioritized must-fix
> items in the original doc against the freshly regenerated bindings
> (local-packages/ `SwiftBindings.Sdk.0.11.0.nupkg` stamped 2026-05-16
> 22:45; all `obj/.../swift-binding/` outputs regenerated 22:54–23:06).

---

## Summary

Of the 10 must-fix items in the original residual-gaps doc, status
after R2-1 (2026-05-17):

- **FIXED in original round-1 (3):** N-1, N-3, S-5
- **FIXED in R2-1 (2):** S-2 (NSObject-rooted proxy subsystem),
  S-3 (cross-module nested-type emission on struct and class receivers)
- **PARTIALLY FIXED (1):** A-2 (static factories landed round-1;
  instance members covered by Session 2 below)
- **NOT FIXED (4):** S-1 (Session 1), A-1 (Session 2),
  S-4 + A-4 (Session 3)

**Close-out plan:** 3 sessions close the remaining 5 items, followed
by an end-of-wave regression sweep. Detailed in the
[Session plan](#session-plan) section below. Sessions are numbered
1/2/3; "Pre-work" covers wrap-up of R2-1 itself.

---

## Confirmed FIXED in R2-1 (2026-05-17, no further action)

- **S-2.** New NSObject-rooted proxy subsystem (`EveryObjCProtocol:
  NSObject` in Swift.Runtime + branching in `EveryProtocolEmitter` /
  `ProtocolProxyEmitter`) allows the closure-taking Stripe `@objc`
  protocols (`STPAuthenticationContext`, `STPCustomerEphemeralKeyProvider`,
  `STPIssuingCardEphemeralKeyProvider`) to be C#-implementable and
  invokable from Swift via ObjC dispatch. The verbatim "No Set…_vtable
  Swift trampoline was emitted" carve-out is replaced by populated
  `InitializeVtable` bodies.
- **S-3.** Cross-module nested-type emission (`extension ForeignType {
  struct Nested {} }`) now mirrors the same partial-class wrapper for
  BOTH class receivers (round-1 `CrossModuleExtensionEmitter.cs`) and
  struct receivers (new in R2-1: `CrossModuleExtensionEmitter.Struct.cs`
  recurses nested types and emits a `public partial class
  {ForeignStruct}` host so consumers can reference
  `CurrentModule.ForeignStruct.Nested`). Unblocks Stripe's
  `extension StripeCore.DependencyPoint { struct HostedTag {} }`-shape
  patterns. Also: parser-side `InterfaceFactsAggregator` now emits
  `OuterTupleLabel` for `case foo(label: (a:, b:, ...))` enum-case
  shapes so the enum-case wrapper emission matches.

## Confirmed FIXED in original round-1 fix wave (no further action)

- **N-1.** `IEnumerable<ImageRequest>` overloads now emit alongside the
  `IEnumerable<NSUrl>` ones with distinct PInvokes:
  - `libraries/Nuke/obj/Debug/net10.0-ios/swift-binding/Nuke.cs:17155`
    `StartPrefetching(IEnumerable<Foundation.NSUrl> requests)`
  - `Nuke.cs:17189` `StartPrefetching(IEnumerable<Nuke.ImageRequest> requests)`
  - Matching `StopPrefetching` pair at `:17224` / `:17258`
- **N-3.** `ImagePipeline.LoadData(didReceiveData:)` migrated to
  `SwiftClosureMarshaller.TryAllocateBoxedContext` + paired
  `ReleaseBoxedContext` / `GCHandle.Free` in finally:
  - `libraries/Nuke/obj/Debug/net10.0-ios/swift-binding/Nuke.cs:11242-11269`
- **S-5.** `authenticationContextHeap` now wrapped in
  `ExistentialContainerHeap` and tracked in `_asyncCallHolder`; cleanup
  loop handles the entry on both success and cancel paths:
  - alloc + tracking at
    `libraries/Stripe/StripePayments/obj/Debug/net10.0-ios/swift-binding/StripePayments.cs:39747-39750`
  - cleanup at `StripePayments.cs:39719-39720`

---

## Still-open must-fix items (5)

S-2 and S-3 are fixed in R2-1 — see *Confirmed FIXED in R2-1* above.
The five remaining items below are: S-1, S-4, A-1, A-2 (partial), A-4.

### S-1. StripePayments → StripeCore cross-module extensions still dropped

**Severity:** High (entire extension surface removed; consumer-visible).
**Original source:** [Resolved/bug-0.10.0-cross-module-extensions-dropped.md](Resolved/bug-0.10.0-cross-module-extensions-dropped.md).
**Targeted by:** Session 1 (S-1) of the prior plan — `CrossModuleExtensionEmitter`
routing path 2-3 (third-party-module-A → third-party-module-B).

The class body is still empty in the 0.11.0 regen:

```csharp
// libraries/Stripe/StripePayments/obj/Debug/net10.0-ios/swift-binding/StripePayments.cs:58808-58810
public static partial class STPAPIClientStripePaymentsExtensions
{
}
```

Sibling `StripeAPIStripePaymentsExtensions` at `StripePayments.cs:58798`
is also empty. The 0.10.0 fix that covered Apple/system-module extensions
(path 1 of 3) still hasn't been extended to the third-party-A →
third-party-B path. Consumer impact unchanged from the prior doc:
`STPAPIClient.createToken / createSource / confirmPaymentIntent / …`
are all absent from the C# surface.

### S-4. StripeCardScan completion-wrapper heap still leaks

**Severity:** Medium (~120 bytes per scan; cumulative).
**Original source:** [Resolved/bug-0.10.0-swift-wrapper-payload-buffer-leak.md](Resolved/bug-0.10.0-swift-wrapper-payload-buffer-leak.md).
**Targeted by:** Session 4 (S-4) — wrapper-emitter completion-handler path.

Both allocation sites are unchanged; no `defer { __heap_0.deallocate() }`,
no managed-side free after `MarshalFromSwift`:

```swift
// libraries/Stripe/StripeCardScan/obj/Debug/net10.0-ios/swift-binding/StripeCardScan.Wrapper.swift:340-342
// (CardImageVerificationSheet.present completion adapter)
let __heap_0 = UnsafeMutableRawPointer.allocate(
    byteCount: MemoryLayout<StripeCardScan.CardImageVerificationSheetResult>.size,
    alignment: MemoryLayout<StripeCardScan.CardImageVerificationSheetResult>.alignment)
__heap_0.initializeMemory(as: StripeCardScan.CardImageVerificationSheetResult.self,
    repeating: p0, count: 1)
cdecl_completion(__heap_0, completionContext!)

// StripeCardScan.Wrapper.swift:435-437  (CardScanSheet.present, same shape)
```

No `.deallocate()` call anywhere in the file for `__heap_0`. The
managed-side `StripeCardScan.cs` shows no `NativeMemory.Free` /
`Marshal.FreeHGlobal` paired with these completion paths either.

### A-1. MusicKit `MusicLibraryRequest<T>` still a bare tombstone

**Severity:** High (request-builder API unreachable).
**Original source:** [Resolved/gap-0.10.0-multispecialization-drops-generic-property-accessors.md](Resolved/gap-0.10.0-multispecialization-drops-generic-property-accessors.md).
**Targeted by:** Session 2 (A-1) — `ConstrainedExtensionEmitter` wiring
through `ClassHandler` (mirror of the StoreKit2 `EnumHandler` fix).

The class still has only metadata + Dispose + marshalling:

```csharp
// apple-frameworks/MusicKit/obj/Debug/net10.0-ios26.2/swift-binding/MusicKit.cs:3628
public partial class MusicLibraryRequest<TMusicItemType>
    : ISwiftObject, ISwiftStruct, IDisposable
    where TMusicItemType : ISwiftObject
{
    static nuint _payloadSize = TypeMetadata.RegisterAndGetSize(…);
    SwiftSafeHandle<MusicLibraryRequest<TMusicItemType>> _payload = …;
    public SwiftSafeHandle<MusicLibraryRequest<TMusicItemType>> Payload => _payload;
    public void Dispose() { … }
    ~MusicLibraryRequest() { … }
    static TypeMetadata ISwiftObject.GetTypeMetadata() => …;
    // … MarshalToSwift, NewFromPayload, private ctor — that's it.
    // No .limit, .offset, .filter, .sort, .response()
}
```

The Session 2 plan was for the same fix to unblock both A-1 and A-2 by
routing closed-instantiation extension methods through `ClassHandler`.
A-2 partially landed (see next item), but the class itself still has
no operational members — implying the wiring change isn't reaching the
`MusicLibraryRequest<T>` extension blocks at all.

### A-2. WeatherKit Statistics/Summary Query`<T>` — partial fix

**Severity:** High (consumer surface partially restored, instance API
still unreachable).
**Original source:** [Resolved/gap-0.10.0-multispecialization-drops-generic-property-accessors.md](Resolved/gap-0.10.0-multispecialization-drops-generic-property-accessors.md).
**Status:** PARTIALLY FIXED.
**Targeted by:** Session 2 (A-2) — same wiring as A-1.

Static factory properties on the query types now emit:

```csharp
// apple-frameworks/WeatherKit/obj/Debug/net10.0-ios26.2/swift-binding/WeatherKit.cs:8280-8323
public static WeatherKit.DailyWeatherStatisticsQuery<WeatherKit.DayTemperatureStatistics>
    Temperature { get => Temperature_Get(); }
public static WeatherKit.DailyWeatherStatisticsQuery<WeatherKit.DayPrecipitationStatistics>
    Precipitation { get => Precipitation_Get(); }
// … and similar for the other stat axes
```

But the `DailyWeatherStatisticsQuery<T>` class body itself (and siblings
`MonthlyWeatherStatisticsQuery<T>`, `HourlyWeatherStatisticsQuery<T>`,
`DailyWeatherSummaryQuery<T>`) still has only the standard ISwiftStruct
members. So consumers can construct/obtain a query via the static
factory, but can't call any instance method on it to actually fetch
statistics. The `WeatherService.weather<T>(for:including:)` family
that consumes these queries is also still in the Family-C closure-skip
cascade, so the end-to-end statistics path remains unreachable.

### A-4. Nullable `Measurement<T>?` setters still missing value-side AddRef

**Severity:** Medium (GC race during property set).
**Original source:** [Resolved/bug-0.10.0-equals-and-setter-missing-dangerousaddref.md](Resolved/bug-0.10.0-equals-and-setter-missing-dangerousaddref.md).
**Targeted by:** Session 4 (A-4) — property-setter emitter.

The receiver-side bracketing (O-9) is in place, but the value parameter
is still extracted as a raw `IntPtr` outside any AddRef block:

```csharp
// apple-frameworks/WeatherKit/obj/Debug/net10.0-ios26.2/swift-binding/WeatherKit.cs:18960-18981
private void Gust_Set(IntPtr payload, bool hasValue) {
    unsafe {
        var success = false;
        _payload.DangerousAddRef(ref success);        // receiver only ✓
        try {
            PInvoke_gust_Set_2506D7D4(payload, hasValue, _payload.DangerousGetHandle());
            return;
        }
        finally {
            if (success) _payload.DangerousRelease(); // receiver only ✓
        }
    }
}
```

The `payload` parameter handed in from the property setter is unpinned
across the PInvoke. If GC runs between
`value?.Payload.DangerousGetHandle()` in the property body and the
PInvoke return, the value's `SafeHandle` can be collected. Same shape
across the other nullable `Measurement<T>?` setters in `Wind`,
`Pressure`, `Visibility`, `Humidity`, `UVIndex`.

---

## Open-item priority table (gates 0.11.0)

5 open items (4 NOT FIXED + 1 PARTIALLY FIXED), keeping the same numbering
as the original residual-gaps doc. The S-2 / S-3 rows are retained for
traceability but marked **FIXED in R2-1**:

| Priority | Item | Status | Emitter area | Session |
|---|---|---|---|---|
| 🔥 1 | **S-1** cross-module ext routing — completion-block + async/throws on class receivers | NOT FIXED | `CrossModuleExtensionEmitter` | **Session 1** |
| 🔥 2 | **S-3** cross-module nested type emission on struct receivers | **FIXED in R2-1** | `CrossModuleExtensionEmitter.Struct.cs` mirror partial-class wrapper + parser `OuterTupleLabel` | R2-1 (landed) |
| 🔥 3 | **S-2** proxy vtable emission for closure-taking ObjC protocol methods | **FIXED in R2-1** | new `EveryObjCProtocol: NSObject` subsystem in Swift.Runtime + `EveryProtocolEmitter` / `ProtocolProxyEmitter` branch | R2-1 (landed) |
| 🔥 4 | **A-1** `MultiSpecialization` accessor surface on class generics (wiring exists; blocked by PAT-constraint + async-in-generic-type gates) | NOT FIXED | PAT-constraint gate + per-instantiation harness infrastructure | **Session 2** |
| 🔥 5 | **A-2** same shape as A-1 (static factories landed in round-1; instance members blocked by same gates) | PARTIALLY FIXED | same as A-1 | **Session 2** |
| P3 | **S-4** Swift wrapper `.allocate` without paired `.deallocate` | NOT FIXED | wrapper-emitter completion-handler path | **Session 3** |
| P5 | **A-4** value-side `DangerousAddRef` on nullable struct setters | NOT FIXED | property-setter emitter | **Session 3** |

The four roadmap items (N-2/Roadmap-1, A-3/Roadmap-2, B-1/Roadmap-3,
L-1/Roadmap-4) carry forward unchanged — still not gating 0.11.0.

---

## Why round-1 missed (lessons for round-2)

Every round-1 session that missed shares one failure mode: a new
emitter path was added and gated to a synthetic fixture shape, then
`nuke test` + `nuke binding-tests` green was treated as proof of fix.
At no point was the cited consumer-library file regenerated and the
cited symbol grepped — so a change that fires on the fixture and on
*no other input* sailed through the gate. The commit messages
described the emitter change; nothing in the gate chain verified the
consumer-side output. Per-session forensics:

- **S-1 (Session 1):** added a frozen-trivial-struct receiver path.
  `STPAPIClient` is a class. `STPAPIClientStripePaymentsExtensions` body
  still empty.
- **S-3 (Session 1):** added a *cross-module* enum payload extractor.
  The FinancialConnections `Result` enums are same-module; the
  emitter never fires. Regression also broader than originally
  characterized — `TryGetCanceled` is missing too, not just
  `TryGetCompleted`.
- **A-1 / A-2 (Session 2):** implemented `ClosedStaticFactoryGate` —
  emits *static getters only*. The plan asked for instance accessors
  (`.limit/.offset/.filter/.sort/.response()`). A-2's static factories
  materialized; no instance surface anywhere.
- **S-2 (Session 3):** loosened `EveryProtocolEmitter.IsDispatchableClosureMethod`.
  Verbatim "No SetSTPAuthenticationContext_vtable trampoline was
  emitted" comment is still in the output, meaning a gate *upstream*
  of the one Session 3 touched is still rejecting these three Stripe
  protocols.
- **S-4 (Session 4):** commit asserts `allocate/deinitialize/deallocate`
  pairing in closure wrappers. `StripeCardScan.Wrapper.swift:340-342`
  and `:435-437` are byte-for-byte unchanged. The new pairing fires on
  a different emission path than the present-completion adapter.
- **A-4 (Session 4):** commit asserts value-side `DangerousAddRef`/`Release`
  bracketing in nullable struct setters. `WeatherKit.cs:18960-18981`
  (`Gust_Set`) is byte-for-byte unchanged. Same shape problem as S-4.

Round-2 inverts the gate order: **per-item regen-and-grep of the cited
consumer-library output is the primary close-out gate.** Unit tests and
binding tests are still required (and still where new BindingTests
fixtures land) but they're no longer sufficient for sign-off.

---

<a id="session-plan"></a>
## Session plan

3 sessions close the remaining 5 items, plus an end-of-wave regression
sweep. Each session has a hard ship list, hard validation gates, and a
binary exit criterion. No autonomous deferral inside a session — if
something genuinely can't close, ask the user.

### Pre-work (this session) — close out R2-1

**Ships:**
- Runtime gate green on both R2-1 work products (S-2 worktree + S-3 main).
- `/codex-review` + `/grok-cli-review` complete on both, findings addressed.
- `s-2-objc-proxy` worktree merged into main.
- R2-1 work committed to main (S-3 + parser infra + 2 BindingTests
  fixtures + the s-2 changes).

**Validation gates (all must pass before commit):**
1. `nuke test` green on main.
2. `nuke binding-tests --skip-regen` green on main (sim).
3. `nuke binding-tests --skip-regen --device` green on main — S-2
   touches ObjC dispatch / NSObject, both runtimes must clear.
4. Per-item regen-and-grep against swift-dotnet-packages:
   - **S-2:** `STPAuthenticationContextProxy.InitializeVtable`
     contains `SetSTPAuthenticationContext_vtable(...)`; the
     "No SetXxx_vtable trampoline" comment is gone; same for the two
     StripeIssuing proxies.
   - **S-3:** `DependencyPoint.HostedTag` /
     `DependencyService.HostedPayload` partials are reachable via
     `using SwiftBindingsTestLib;` (or the equivalent current-module
     namespace import) from a consumer .cs. Stripe-shape equivalent:
     `StripeFinancialConnections.StripeAPI.FinancialConnectionsSession`
     reachable via `using StripeFinancialConnections;` once that lib regens.
5. Diff vs pre-image: only intended changes.
6. Codex + Grok-CLI: zero High/Critical findings open.

**Exit:** main contains R2-1 commit(s) with all gates green;
s-2 worktree deleted.

### Session 1 — S-1 (Stripe cross-module extensions with closures + async/throws)

🔥 High. Closes S-1.

**Diagnosis** (refined during R2-1 investigation, supersedes the
original S-1 writeup): receiver shape is not the blocker — the parser
already routes class receivers through
`CrossModuleExtensionEmitter.Emit(ClassDecl, …)` (see
`src/Swift.Bindings/src/Parser/SwiftABIParser.cs:851`, which admits
both `Class` and `Struct` DeclKinds for cross-module-extension nodes).

`STPAPIClientStripePaymentsExtensions` and `StripeAPIStripePaymentsExtensions`
emit the wrapper `public static partial class` declaration at
`StripePayments.cs:58798` and `:58808` but have empty bodies because
the per-method emitter rejects every method in
`extension StripeCore.STPAPIClient { … }` and
`extension StripeCore.StripeAPI { … }`. From
`StripePayments.swiftmodule/…/arm64-apple-ios.swiftinterface`:

```swift
extension StripeCore.STPAPIClient {
  @objc(createTokenWithCard:completion:) dynamic public func createToken(
      withCard cardParams: StripePayments.STPCardParams,
      completion: @escaping StripePayments.STPTokenCompletionBlock)
  public func createToken(withCard cardParams: StripePayments.STPCardParams)
      async throws -> StripePayments.STPToken
  @objc(createTokenForCVCUpdate:completion:) dynamic public func createToken(
      forCVCUpdate cvc: Swift.String,
      completion: StripePayments.STPTokenCompletionBlock? = nil)
}
// …createSource, confirmPaymentIntent, createPaymentMethod,
//   retrievePaymentIntent, confirmSetupIntent, etc. — same shape
```

100% of the extension surface is one of:

1. `@objc … completion: @escaping BlockType` —
   `ExtensionMarshallingHelper.ClassifyParameterType`
   (`src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ExtensionMarshallingHelper.cs:103`)
   returns `null` for any `typeSpec is not NamedTypeSpec` (line 105-106);
   the method is rejected at `CrossModuleExtensionEmitter.cs:244`
   (`paramCategory == null → return false`).
2. `public func … async throws -> T` — `TryEmitMethodExtension` returns
   false on `method.IsAsync` at `CrossModuleExtensionEmitter.cs:205-206`
   and on `method.Throws` at `:209-210`.

Sibling `StripeAPI.paymentRequest(withMerchantIdentifier:)` is
`@objc class func` (no closure, no async, no throws) but rejected
because class-receiver static-method emission isn't wired.

**Ships:**
- `CrossModuleExtensionEmitter` accepts closure-parameter methods on
  class receivers (`@_cdecl` Swift wrapper trampolines plus
  `ClosureHandle.Escaping`-style C# marshalling).
- `CrossModuleExtensionEmitter` accepts `async throws` overloads on
  class receivers — emits both the completion-block variant and a
  `XxxAsync()` `Task<T>`-returning variant via `AsyncHarnessEmitter`.
- `@objc class func` static-method emission on class receivers
  (covers `StripeAPI.paymentRequest`).
- BindingTests fixture under
  `BindingTests/Sources/SwiftBindingsTestLib/CrossModule/`: two
  inter-dependent modules, class receiver, extension method with
  `@escaping` completion + sibling `async throws` overload, both
  invoked end-to-end. RuntimeTestsApp test class exercises both shapes.

**Wrapper-path decision (settle at start of session):**
- **A.** Wrapper-library `@_cdecl` trampolines per method (mirrors
  `WrapperEmitter.cs` in-module path; higher fidelity, more code).
- **B.** Direct CallConvSwift dispatch with `ClosureHandle.Escaping`
  manufactured context (current cross-module path already dispatches
  CallConvSwift directly — see `CrossModuleExtensionEmitter.cs:522-526`;
  lower code volume, more bespoke).

**Validation gates:**
1. `nuke test` green.
2. `nuke binding-tests --skip-regen` green (sim).
3. `nuke binding-tests --skip-regen --device` green (calling-conv
   change — NativeAOT must clear).
4. Per-item regen-and-grep:
   - `StripePayments.cs:58808` `STPAPIClientStripePaymentsExtensions`
     body contains `CreateToken`, `CreateSource`, `ConfirmPaymentIntent`
     AND `CreateTokenAsync`, `CreateSourceAsync`,
     `ConfirmPaymentIntentAsync`.
   - `StripePayments.cs:58798` `StripeAPIStripePaymentsExtensions`
     body contains `PaymentRequest`.
   - Diff vs pre-image: only intended additions; no unrelated
     regressions in StripePayments.cs.
5. Codex + Grok-CLI: zero High/Critical findings open.

**Exit:** S-1 commit on main, all gates green.

### Session 2 — A-1 + A-2 (PAT-constraint + async-in-generic-type-member gates)

🔥 High (A-1) / partial→full (A-2). Closes A-1 and the A-2 instance
surface. **Authorized as full lift** — both gates lifted, both
consumer surfaces materialize. No "smaller-win-only" exit.

**Diagnosis** (refined during R2-1 investigation):
`ClassHandler.cs:390-392` already calls
`ConstrainedExtensionEmitter.EmitConstrainedExtensions(...)`. The
empty `MusicLibraryRequest<TMusicItemType>` and
`DailyWeatherStatisticsQuery<T>` class bodies are correct scaffolds —
empty because per-member emission hits separate gates:

1. **PAT-constraint gate** — MusicKit `filter` and `sort` are
   tombstoned at `MusicKit.cs:3748-3757` with
   `"Method has constraints on protocols with associated types"`. The
   gate lives in `BoundGenericsHandler.ShouldSkipConstraint` / the
   broader `MemberValidationPipeline` Phase 4 chain. PAT-constrained
   generic methods need a witness table per closed instantiation —
   currently unsupported.
2. **Async-in-generic-type-member gate** — MusicKit `response()` is
   tombstoned with `"async in generic type member"`. Separate gate at
   the intersection of `AsyncHarnessEmitter` and generic
   instantiation; async instance members on generic types need a
   per-instantiation harness — currently unsupported.
3. **`limit` / `offset` are NOT on `MusicLibraryRequest<T>`** — the
   original residual-gaps doc misidentified the parent type. They
   live on `MusicLibrarySearchRequest` (unconstrained struct). Verify
   against the swiftinterface as part of diagnosis; if they belong
   elsewhere, scope them out of A-1.
4. **WeatherKit `DailyWeatherStatisticsQuery<T>` static factories**
   landed in round-1 at `WeatherKit.cs:8280-8323` — that part of A-2
   is genuinely fixed. Instance members face the same PAT-gate.

**Ships:**
- Per-instantiation witness-table infrastructure: walks
  `MultiSpecializationDatabase.xml` for closed instantiations, stamps
  out concrete instances of PAT-constrained generic methods per
  instantiation, routed through `ConstrainedExtensionEmitter`.
- Per-instantiation async-harness generation: emits an
  `AsyncHarness` per closed instantiation of an `async` generic-type
  instance member.
- PAT-constraint gate lifted in `BoundGenericsHandler` (or routed
  through the per-instantiation path).
- Async-in-generic-type-member gate lifted in `AsyncHarnessEmitter`.
- `MusicLibraryRequest<T>` instance surface (`filter`, `sort`,
  `response()` — plus `limit`/`offset` if confirmed on this type)
  materializes per its swiftinterface.
- WeatherKit `DailyWeatherStatisticsQuery<T>` + 3 siblings
  (`MonthlyWeatherStatisticsQuery<T>`, `HourlyWeatherStatisticsQuery<T>`,
  `DailyWeatherSummaryQuery<T>`) instance surface materializes.
- BindingTests fixture: `Bag<T: SomeProtocolWithAssoc>` with one
  PAT-constrained instance method + one `async` instance method;
  closed instantiation `Bag<ConcreteImpl>` registered in
  MultiSpecializationDatabase; both members invoked end-to-end.

**Validation gates:**
1. `nuke test` green.
2. `nuke binding-tests --skip-regen --sim --device` green
   (generic-instantiation marshalling — cross-runtime divergence risk).
3. **`nuke validate` green. `.validation-baseline.json` cs_compile +
   swift_compile ≥ baseline.** Non-negotiable — this is the gate the
   PAT lift specifically must clear.
4. Per-item regen-and-grep:
   - `MusicKit.cs:3628` `MusicLibraryRequest<TMusicItemType>` body
     contains instance accessors per the swiftinterface (`Filter`,
     `Sort`, `Response`, plus `Limit`/`Offset` if they belong here).
   - `WeatherKit.cs:8280-8323` and surrounding —
     `DailyWeatherStatisticsQuery<T>` and 3 sibling classes each have
     ≥1 instance method.
5. Diff vs pre-image: zero regression in any other consumer-library
   `obj/.../swift-binding/*.cs`.
6. Codex + Grok-CLI: zero High/Critical findings open.

**Exit:** A-1 + A-2 commit on main, all gates green including validate.

**Hard rule:** if the PAT lift breaks validate mid-implementation,
the fix is to find why and unbreak it — **not** to ship the
async-gate alone and defer PAT. The session does not close partial.

### Session 3 — S-4 + A-4 (lifetime/pin retargeting) + end-of-wave regression sweep

P-tier. Closes S-4, A-4, and ships 0.11.0.

Round-1 added emitter code for both but both still emit byte-for-byte
unchanged at the cited consumer-library file:line. Round-1's pairing
logic / AddRef bracketing landed on a different emission path than the
cited consumer-library shape.

**Ships:**
- **S-4:** `defer { __heap_0.deallocate() }` (or managed-side Free
  after `MarshalFromSwift<T>`) at the actual emission site producing
  `StripeCardScan.Wrapper.swift:340-342` and `:435-437`. Trace the
  emitter that produces those exact lines; add the pairing at the
  actual site.
- **A-4:** value-side `DangerousAddRef`/`Release` (or `fixed` pinning)
  on nullable `Measurement<T>?` setters at `WeatherKit.cs:18960-18981`
  (`Gust_Set`) and siblings (Wind, Pressure, Visibility, Humidity,
  UVIndex). Trace the property-setter emitter that produces the cited
  lines; add value-parameter pinning at the actual site.
- BindingTests fixtures:
  - StripeCardScan-shape closure wrapper with `allocate` → completion
    → expected `deallocate` paired; asserted via leak count or
    allocator-counter shim.
  - WeatherKit-shape nullable struct setter under GC pressure;
    asserts no use-after-free.
- SDK version stays `0.11.0`, Apple stays `26.2.3`.

**Validation gates:**
1. `nuke test` green.
2. `nuke binding-tests --skip-regen --sim --device` green
   (lifetime work crosses both runtimes — required).
3. Per-item regen-and-grep:
   - `StripeCardScan.Wrapper.swift:340-342` and `:435-437` —
     `__heap_0.deallocate()` present in both adapters.
   - `WeatherKit.cs` `Gust_Set` (+ siblings) bracket the `payload`
     parameter through the PInvoke.
4. **End-of-wave gates** (after S-4/A-4 land, before publishing):
   - `nuke validate` green; baseline updated and committed.
   - `/regression-validation --version 0.11.0 --apple-version 26.2.3`
     — full Mono JIT sim + NativeAOT device sweep against both
     `swift-dotnet-packages` and `internal-binding-testing`. **Zero
     non-pass results** per the no-expected-failures policy.
5. Codex + Grok-CLI: zero High/Critical findings open.

**Exit:** S-4 + A-4 commit on main, end-of-wave gates green, 0.11.0
SDK + 26.2.3 Apple-supplement ready to publish to NuGet (publish is
the user's action, not Claude's).

### Cross-session rules

1. Each session ends with a commit. No half-merged worktrees carrying
   across sessions except for explicit multi-session handoffs.
2. BindingTests fixture is part of the session's ship list, not a
   follow-up.
3. Per-item regen-and-grep is mandatory before commit.
4. No autonomous deferral inside a session. If something genuinely
   can't close, ask the user.
5. No scope expansion mid-session. Surprises go on the roadmap and
   the user is told — they don't expand the session.
6. Reviews (`/codex-review` + `/grok-cli-review`) run before commit,
   not after. High/Critical findings close, not defer.

---

## Verification routine (mandatory, per item)

This is the gate round-1 skipped. For **every** item, before declaring
done:

1. **Capture pre-image** of the cited consumer-library file:line.
   Round-2's evidence cites above are the authoritative cite list.
   For each item, before any code change:
   ```bash
   # one-time per session, picks the items relevant to that session
   mkdir -p /tmp/round2-preimage
   cp /Users/wojo/Dev/swift-dotnet-packages/libraries/Stripe/StripePayments/obj/Debug/net10.0-ios/swift-binding/StripePayments.cs                 /tmp/round2-preimage/StripePayments.cs.pre
   cp /Users/wojo/Dev/swift-dotnet-packages/libraries/Stripe/StripeIssuing/obj/Debug/net10.0-ios/swift-binding/StripeIssuing.cs                   /tmp/round2-preimage/StripeIssuing.cs.pre
   cp /Users/wojo/Dev/swift-dotnet-packages/libraries/Stripe/StripeFinancialConnections/obj/Debug/net10.0-ios/swift-binding/StripeFinancialConnections.cs /tmp/round2-preimage/StripeFinancialConnections.cs.pre
   cp /Users/wojo/Dev/swift-dotnet-packages/libraries/Stripe/StripeCardScan/obj/Debug/net10.0-ios/swift-binding/StripeCardScan.Wrapper.swift      /tmp/round2-preimage/StripeCardScan.Wrapper.swift.pre
   cp /Users/wojo/Dev/swift-dotnet-packages/apple-frameworks/MusicKit/obj/Debug/net10.0-ios26.2/swift-binding/MusicKit.cs                        /tmp/round2-preimage/MusicKit.cs.pre
   cp /Users/wojo/Dev/swift-dotnet-packages/apple-frameworks/WeatherKit/obj/Debug/net10.0-ios26.2/swift-binding/WeatherKit.cs                    /tmp/round2-preimage/WeatherKit.cs.pre
   ```

2. **Pack + deploy** (mechanics lifted verbatim from
   `/regression-validation` Step 1 — `VersionScope` auto-reverts the
   stamped csprojs in `swift-bindings`, no manual cleanup needed):
   ```bash
   cd /Users/wojo/Dev/swift-bindings
   rm -rf /tmp/swift-nuget
   set -o pipefail; dotnet nuke Pack --version $VERSION --apple-version $APPLE_VERSION --output-dir /tmp/swift-nuget 2>&1 | tee /tmp/round2-pack-$VERSION.log

   # Wipe stale same-version nupkgs so the new ones can't resolve from cache
   rm -f /Users/wojo/Dev/swift-dotnet-packages/local-packages/SwiftBindings.*.nupkg
   cp /tmp/swift-nuget/*.nupkg /Users/wojo/Dev/swift-dotnet-packages/local-packages/

   dotnet nuget locals all --clear
   ```
   Sanity-check the four nupkgs landed in `local-packages/` before continuing
   (`SwiftBindings.{Runtime,Sdk,Templates}.$VERSION.nupkg` +
   `SwiftBindings.Apple.$APPLE_VERSION.nupkg`).

3. **Stamp + regenerate the cited csproj only** (we don't need the
   full RegressionValidate matrix for per-item verification — just
   stamp the SDK version into the one csproj we care about and build
   it, which triggers the SwiftBindings.Sdk regen):
   ```bash
   cd /Users/wojo/Dev/swift-dotnet-packages
   # BumpSdkVersion exists as a standalone target on the Nuke build there
   # (RegressionValidate calls BumpSdkVersionInternal as its pre-flight).
   dotnet nuke BumpSdkVersion --version $VERSION

   # Then build just the cited csproj. Items → csproj mapping:
   #   S-1, S-2          → libraries/Stripe/StripePayments/StripePayments.csproj
   #   S-2 (proxies #2,3) → libraries/Stripe/StripeIssuing/StripeIssuing.csproj
   #   S-3               → libraries/Stripe/StripeFinancialConnections/StripeFinancialConnections.csproj
   #   S-4               → libraries/Stripe/StripeCardScan/StripeCardScan.csproj
   #   A-1               → apple-frameworks/MusicKit/MusicKit.csproj
   #   A-2, A-4          → apple-frameworks/WeatherKit/WeatherKit.csproj
   dotnet build <csproj-path> -c Debug
   ```

4. **Grep the regenerated file for the expected symbol** at or near
   the cited line:
   - **S-1**: `STPAPIClientStripePaymentsExtensions` body must contain `CreateToken`, `CreateSource`, `ConfirmPaymentIntent`. Sibling `StripeAPIStripePaymentsExtensions` non-empty.
   - **S-2**: `InitializeVtable` in `STPAuthenticationContextProxy` (and the two `StripeIssuing` proxies) must call `SetSTPAuthenticationContext_vtable(...)`. The "no trampoline emitted" comment must be gone.
   - **S-3**: `Result` and `TokenResult` partials must declare `TryGetCompleted(out FinancialConnectionsSession value)` / `TryGetCompleted(out StripeAPI.Token value)` and `TryGetCanceled()` (bool, no out).
   - **A-1**: `MusicLibraryRequest<TMusicItemType>` class body must declare instance accessors for `limit` / `offset` / `filter` / `sort` / `response()` (names per the Swift surface).
   - **A-2**: `DailyWeatherStatisticsQuery<T>` and siblings — instance member(s) present in class body.
   - **S-4**: `StripeCardScan.Wrapper.swift` must contain `__heap_0.deallocate()` (likely in a `defer`) within both `CardImageVerificationSheet.present` and `CardScanSheet.present` adapters.
   - **A-4**: `Gust_Set` and siblings in WeatherKit.cs must bracket the `payload` parameter with `DangerousAddRef`/`Release` (or pin via `fixed`) across the PInvoke.

5. **Diff against the pre-image** captured in step 1:
   ```bash
   diff /tmp/round2-preimage/<file>.pre /Users/wojo/Dev/swift-dotnet-packages/<file>
   ```
   The diff should show only the intended emission added. Unexpected
   diffs elsewhere are a regression — chase them before sign-off.

6. **Only then** are `nuke test` + `nuke binding-tests` gate-relevant
   inside swift-bindings.

If a per-item check fails, the fix is not done — do not move to the
next item, do not commit, do not declare partial-with-deferral.

After any session stamps SDK version into swift-dotnet-packages
csprojs, the working tree there will show modified
`Sdk="SwiftBindings.Sdk/X.Y.Z"` attrs across `libraries/` and
`apple-frameworks/`. Per the `/regression-validation` notes: commit
if the version is shipping, revert if it's a dry-run wave tag. We're
a dry-run wave tag until Session 3 lands the last fix — so revert at
the end of Sessions 1 and 2 unless the user says otherwise.

---

## End-of-wave operational notes

Per-session validation gates are listed inside each Session writeup
above. Two operational notes that apply to the end-of-wave
`/regression-validation` run in Session 3:

- Where a BindingTests fixture for the closeout shape doesn't already
  exist, add one under `BindingTests/Sources/SwiftBindingsTestLib/`
  so the regression is locked in for future SDK bumps.
- iPhone must be connected, unlocked, and trusted before invoking the
  skill (it bails on `xcrun devicectl list devices` returning no
  device).

---

## Wave parameters (confirmed)

- **`$VERSION = 0.11.0`** (SDK lane) — rebuilt-in-place across both
  sessions per the SDK-version-stability memory; stale same-version
  nupkgs wiped before each redeploy.
- **`$APPLE_VERSION = 26.2.3`** (Apple-supplement lane) — same train
  we've already been testing against.
- **`swift-dotnet-packages` clone**: `/Users/wojo/Dev/swift-dotnet-packages`.
- **No round-1 reverts.** N-1, N-3, S-5, and the partial A-2
  static-factory landing all stay.
- **At end of each session**: revert the
  `Sdk="SwiftBindings.Sdk/0.11.0"` attribute changes in
  swift-dotnet-packages (these are dry-run stamps until the full wave
  ships).
