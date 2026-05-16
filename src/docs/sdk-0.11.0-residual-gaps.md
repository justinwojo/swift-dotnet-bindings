# SDK 0.11.0 — residual gaps after the post-0.10.0 fix wave

> Generated 2026-05-16 from the SDK 0.11.0 validation pass against
> [swift-dotnet-packages](https://github.com/justinwojo/swift-dotnet-packages).
> Method: 9 parallel Grok subagents over the 48 docs in
> [`Resolved/`](Resolved/), followed by manual file-level spot-checks
> for every NOT_FIXED / PARTIALLY_FIXED claim. Every evidence cite
> below was opened directly and confirmed.

---

## Session plan — 4 Claude sessions to unblock 0.11.0

The 10 must-fix items in the priority table at the bottom of this doc
are grouped into 4 sessions by emitter area and investigative lens
(rather than by severity tier) so each session works one mental model.
Every session ships unit tests at the emitter level **and** BindingTests
coverage reproducing the shape (not the third-party library) in
`BindingTests/Sources/SwiftBindingsTestLib/`.

### Session 1 — Stripe surface restoration (S-1 + S-3)

Both 🔥 High; both are "code that should exist but doesn't."

- **S-1** `CrossModuleExtensionEmitter` — extend routing to cover path
  2-3 of 3: third-party-module-A → third-party-module-B extensions.
  The 0.10.0 fix only handled Apple/system-module extensions. Restores
  the `STPAPIClientStripePaymentsExtensions` body (createToken,
  createSource, confirmPaymentIntent, …).
- **S-3** Enum case payload extractor — emit
  `TryGetCompleted(out FinancialConnectionsSession value)` for
  `.completed(payload:)`-shape Swift enum cases. Currently emits
  `TryGetFailed` / `TryGetCanceled` / `TryGetUnknown` only.

**Tests**:
- Unit: emitter tests for `CrossModuleExtensionEmitter` covering
  module-A→module-B extension; enum-case extractor tests across
  `.completed(T)` / `.success(T)` / `.value(T)` payload shapes.
- BindingTests: Swift fixture under `SwiftBindingsTestLib` with two
  inter-dependent test modules (`ModuleA.PublicType` +
  `ModuleB extension ModuleA.PublicType { … }`); `Result`-shaped enum
  with `.completed(Payload)` to lock the extractor pattern.

### Session 2 — Apple-frameworks generic-class accessor surface (A-1 + A-2)

Both 🔥 High; **same fix unblocks both**.

Mirror the StoreKit2 `VerificationResult<SignedType>` wiring
(`EnumHandler` → `ConstrainedExtensionEmitter`) through `ClassHandler`
so closed-instantiation extension methods land on generic classes.
Unblocks ~13 MusicKit (`MusicLibraryRequest<T>` accessors) + ~18
WeatherKit (`DailyWeatherStatisticsQuery<T>` and siblings) endpoints
in one go.

**Tests**:
- Unit: `ConstrainedExtensionEmitter` tests covering
  `class Foo<T> where T: SomeProto` + closed-instantiation extension;
  assert `_MultiSpecialization` skip rows drop for class generics.
- BindingTests: `class Request<T>` + closed-extension fixture
  mirroring the MusicKit shape (not the actual MusicKit type) under
  the existing generics domain file.

### Session 3 — Closure proxy vtables + dedup keys (S-2 + N-1)

S-2 🔥 High, N-1 P1 Medium. Both are "emitter silently drops emission
paths."

- **S-2** Proxy/vtable emitter, closure-arg branch — emit
  `SetSTPAuthenticationContext_vtable`-style Swift trampolines for
  protocol methods that take closures, then call `InitializeVtable`
  from the proxy ctor. Affects `STPAuthenticationContextProxy`,
  `STPIssuingCardEphemeralKeyProviderProxy`,
  `STPCustomerEphemeralKeyProviderProxy`. Central blocker on Stripe
  "confirm payment."
- **N-1** Dedup-key emitter — when two methods collide because their
  projected C# signatures share an ObjC-bridge container shape (e.g.
  `IEnumerable<NSUrl>` vs `IEnumerable<ImageRequest>` both projecting
  through `NSArray`), suffix the non-canonical one (`Process` /
  `Process2`) the way non-constructor collisions already do.

**Tests**:
- Unit: emitter tests for `protocol P { func go(_ cb: @escaping (Int)->Void) }`
  round-trip (proxy registers vtable, C#→Swift dispatch hits the
  method); dedup-key tests for `func f([T])` overload collisions on
  bridged container element types.
- BindingTests: Swift protocol with closure-taking method + C#
  implementer + Swift caller in `protocols.swift`; overloaded methods
  colliding via ObjC-bridge container projection.

### Session 4 — Lifetime / memory cleanup (S-4 + S-5 + N-3 + A-4)

P2–P5; all "allocate-but-no-release" or "raw handle without AddRef."
Share one investigative lens (audit cleanup paths in emitters).

- **S-5** (P2) Async-task wrapper — wrap `authenticationContextHeap`
  in a `CopyBufferWithType` and add to `_asyncCallHolder` so the
  success-path cleanup loop reaches it.
- **S-4** (P3) Wrapper emitter completion-handler path — pair every
  `UnsafeMutableRawPointer.allocate` with matching
  `.deinitialize/.deallocate` on the managed side after
  `MarshalFromSwift<TResult>` or `defer` in the Swift wrapper.
- **N-3** (P4) Callback-trampoline emitter — migrate the legacy raw
  `GCHandle.Alloc` + `SwiftClosureData` path used by
  `ImagePipeline.loadData(didReceiveData:)`-shape sites onto
  `ClosureHandle.Escaping` + Swift-side `_sbWrapClosureContext`
  deinit.
- **A-4** (P5) Property-setter emitter — bracket the value
  parameter's payload with `DangerousAddRef` / `DangerousRelease` on
  nullable `Measurement<T>?` setters (currently only the receiver is
  bracketed).

**Tests**:
- Unit: setter emitter tests asserting value-side `DangerousAddRef`
  for nullable struct payloads; async-wrapper emitter tests asserting
  every `NativeMemory.Alloc` is registered with `_asyncCallHolder`;
  wrapper-Swift emitter tests asserting every `.allocate` has a
  paired `.deallocate`.
- BindingTests: streaming-callback test for `progress(streaming,
  completion)` shape (asserts no managed delegate leak by holding
  weak refs and forcing GC after stream end); async-success-path test
  asserting `NativeMemory` doesn't grow per call; nullable struct
  setter under forced-GC pressure.
- Run `nuke binding-tests --device` in addition to sim for this
  session — lifetime bugs differ between Mono and NativeAOT.

### Cross-cutting notes for every session

- **Per-session gates**: `nuke test` + `nuke binding-tests` (sim).
  Add `--device` for Session 3 (calling-convention / marshalling
  change) and Session 4 (lifetime).
- **`nuke validate` is opt-in**, not per-session. Reserve it for the
  **final** post-Session-4 sweep before declaring 0.11.0 unblocked.
- **Roadmap-tracked items** (Roadmap-1..4 in the "Future roadmap"
  section, plus the CANNOT_VERIFY / DEFERRED_BY_DESIGN groups) do
  **not** gate 0.11.0. Leave them alone.
- **Don't conflate**: Session 2 (generic-class accessor wiring) and
  Session 3 (closure-arg vtable emission) both touch generic/proxy
  code but are different emitters; keep them in separate sessions to
  keep the mental model clean.

---

## Summary

Of the 48 SDK 0.10.0 bug/gap docs, 0.11.0 closed the systemically
dangerous ones: every audited GCHandle leak on the new
`ClosureHandle`/`SwiftClosureMarshaller` path, refcount underflow,
stack-pointer SafeHandle, retain-count drift, IEnumerable-as-IntPtr in
async, typed-exception lowering, AsyncSequence → IAsyncEnumerable,
CGImage/CGColor projection, Codable Phase 1, Foundation.Dimension
constraint, ConstrainedExtension property accessor surface, and the
namespace-facade-as-static-class shape are all FIXED.

**Decision:** 0.11.0 is held until every must-fix item below lands.
Residuals split into two buckets:

1. **Ship-blockers for 0.11.0** — surface drops, vtable no-ops,
   tombstoned types, and runtime memory issues that affect real
   consumer flows. Listed in "Must-fix" sections per library and
   summarized in the priority table at the bottom.
2. **Future-release roadmap** — cosmetic / ergonomic / reflection-only
   items that don't block any consumer flow. Carried as roadmap
   entries; not gating 0.11.0.

The full per-doc tally from the validation pass:
**FIXED=24, PARTIALLY_FIXED=6, NOT_FIXED=8, CANNOT_VERIFY=4, DEFERRED_BY_DESIGN=6**.

---

## Nuke — must-fix (2 items)

Sources: [Resolved/gap-0.10.0-duplicatesignature-disambiguation.md](Resolved/gap-0.10.0-duplicatesignature-disambiguation.md),
[Resolved/bug-0.10.0-callback-trampoline-gchandle-leak.md](Resolved/bug-0.10.0-callback-trampoline-gchandle-leak.md).
(N-2 callback-arg projection asymmetry moved to "Future roadmap" below.)

### N-1. `IEnumerable<ImageRequest>` overloads dropped via DuplicateSignature collision

**Severity:** Medium (consumer-visible API drop; NSUrl path still works).

The non-constructor disambiguation fix the doc claims shipped in 0.10.0
*didn't* cover collection-arg projection collisions where the Swift
element types differ but both project through ObjC bridge to NSArray:

```csharp
// libraries/Nuke/obj/Debug/net10.0-ios/swift-binding/Nuke.cs:16991
public void StartPrefetching(IEnumerable<Foundation.NSUrl> requests) { … }

// Nuke.cs:17022
// Unsupported: method 'startPrefetching' — C# signature collides with another member
//   (the IEnumerable<ImageRequest> overload should have been here, suffixed)

// Nuke.cs:17057
// Unsupported: method 'stopPrefetching' — C# signature collides with another member
```

Same shape in `binding-report.json:27-29` (`DataCache.filename(_for:Swift.SwiftString)`)
and 5 other `DuplicateSignature` rows. The doc's own resolution
section says `ModuleHandler.cs` now emits "`Process`, `Process2`, …"
suffixes for non-constructor collisions; that path apparently doesn't
fire when the colliding members share an ObjC-bridge container shape.

### N-3. Legacy `progress` / `didReceiveData` GCHandle path still allocates without ClosureHandle

**Severity:** Medium (resource leak per callback registration).

The new `ClosureHandle` + `SwiftClosureMarshaller` model that closed C2
across most callback sites is wired into `LoadImage`/`LoadData` callback
trampolines, but `ImagePipeline.loadData(didReceiveData:)` still uses
the older raw `GCHandle.Alloc` + `SwiftClosureData` pattern:

```csharp
// Nuke.cs:11193-11206 (LoadData with didReceiveData + completion)
GCHandle didReceiveDataHandle = default;
GCHandle completionHandle = default;
try
{
    didReceiveDataHandle = GCHandle.Alloc(didReceiveData);
    var didReceiveDataClosure = new SwiftClosureData(
        (IntPtr)s_loadData_didReceiveData_032C6097_Callback,
        GCHandle.ToIntPtr(didReceiveDataHandle));
    completionHandle = GCHandle.Alloc(completion);
    var completionClosure = new SwiftClosureData(
        (IntPtr)s_loadData_completion_032C6097_Callback,
        GCHandle.ToIntPtr(completionHandle));
    …
    var result = PInvoke_loadData_032C6097(requestHandle,
        didReceiveDataClosure, completionClosure, self);
    return new CancellableProxy(result);
}
finally { … }  // no didReceiveDataHandle.Free() / completionHandle.Free()
```

The `finally` block runs synchronously after the PInvoke returns the
`CancellableProxy`, but the Swift side keeps the callback pointers
alive for the duration of streaming — meaning `Free()` in the finally
would dangle the Swift-side reference, and *omitting* `Free()` leaks
the managed delegate for the lifetime of the process. The newer
`ClosureHandle.Escaping` model handles both halves (`MarkOwnership-
Transferred` + Swift-side `deinit`-driven release). This call site
hasn't been migrated.

---

## Stripe — must-fix (5 items)

Sources: [Resolved/bug-0.10.0-cross-module-extensions-dropped.md](Resolved/bug-0.10.0-cross-module-extensions-dropped.md),
[Resolved/bug-0.10.0-empty-proxy-vtables-for-closure-protocol-methods.md](Resolved/bug-0.10.0-empty-proxy-vtables-for-closure-protocol-methods.md),
[Resolved/bug-0.10.0-enum-case-payload-extractor-missing.md](Resolved/bug-0.10.0-enum-case-payload-extractor-missing.md),
[Resolved/bug-0.10.0-swift-wrapper-payload-buffer-leak.md](Resolved/bug-0.10.0-swift-wrapper-payload-buffer-leak.md),
[Resolved/bug-0.10.0-async-task-wrapper-leaks-existential-heap.md](Resolved/bug-0.10.0-async-task-wrapper-leaks-existential-heap.md).

### S-1. StripePayments → StripeCore cross-module extensions dropped wholesale

**Severity:** High (entire extension surface removed; consumer-visible).

The cross-module extension emitter's 0.10.0 resolution handled
**Apple/system-module** extensions (path 1 of 3 in the doc's "Routing
gaps" section) but not **third-party-to-third-party**:

```csharp
// libraries/Stripe/StripePayments/obj/Debug/net10.0-ios/swift-binding/StripePayments.cs:68618
public static partial class STPAPIClientStripePaymentsExtensions
{
}
```

Empty body. Every method Stripe adds to `STPAPIClient` from
StripePayments (createToken, createSource, confirmPaymentIntent, etc.)
is gone from the consumer-visible surface. Consumers who migrate from
SwiftBindings.Stripe.Payments 0.9.0 → 25.x will find the surface they
called against is not there.

### S-2. Proxy classes for closure-taking Stripe protocols have no-op vtables

**Severity:** High (silently breaks C#→Swift dispatch for these protocols).

Three Stripe protocols whose methods take closures emit proxy classes
with empty `InitializeVtable`:

```csharp
// StripePayments.cs (STPAuthenticationContextProxy)
private static void InitializeVtable()
{
    lock (_vtableLock)
    {
        if (_vtableInitialized) return;
        // No SetSTPAuthenticationContext_vtable Swift trampoline was emitted
        // for this protocol; the proxy is read-only (Swift→C# wrap path only).
        // Skip vtable initialisation.
        _vtableInitialized = true;
    }
}
```

Same shape in `StripeIssuing.cs` for `STPIssuingCardEphemeralKey-
ProviderProxy` and `STPCustomerEphemeralKeyProviderProxy`. The
"read-only" carve-out is honest, but the proxy *is* given to consumer
code as the only way to implement these protocols; a C# class
implementing `ISTPAuthenticationContext` and passed to
`STPPaymentHandler.confirmPaymentIntent(...)` will never have its
methods called from Swift because the vtable was never registered.

Combined with **S-5** below, this is the central blocker on the
StripePayments "confirm payment" surface — consumers can't supply a
managed authentication context.

### S-3. StripeFinancialConnections enum `.completed` cases are unreachable

**Severity:** High (consumer can't extract the success payload).

`FinancialConnectionsSheet.Result` and `TokenResult` are Swift enums of
the shape:

```swift
public enum Result {
    case completed(session: FinancialConnectionsSession)
    case canceled
    case failed(Error)
}
```

The current emission generates `TryGetUnknown`, `TryGetFailed`, and
`TryGetCanceled` extractors but no `TryGetCompleted`:

```csharp
// libraries/Stripe/StripeFinancialConnections/obj/Debug/net10.0-ios/swift-binding/StripeFinancialConnections.cs:967
public bool TryGetFailed([MaybeNullWhen(false)] out Swift.Foundation.AnyError value)
```

No matching `TryGetCompleted(out FinancialConnectionsSession value)`.
Grep across the file: zero matches for `TryGetCompleted` or
`NewCompleted`. Consumers can see the operation finished but cannot
read the `session` payload.

### S-4. StripeCardScan present-wrapper `.allocate` without `.deallocate`

**Severity:** Medium (~120 byte leak per scan completion; not blocking
but cumulative).

The Swift completion wrapper allocates a heap buffer for the result
payload and hands the raw pointer to the cdecl C# callback. There's no
matching `.deinitialize() / .deallocate()`:

```swift
// libraries/Stripe/StripeCardScan/obj/Debug/net10.0-ios/swift-binding/StripeCardScan.Wrapper.swift:340-342
//   (CardImageVerificationSheet.present completion adapter)
let __heap_0 = UnsafeMutableRawPointer.allocate(
    byteCount: MemoryLayout<StripeCardScan.CardImageVerificationSheetResult>.size,
    alignment: MemoryLayout<StripeCardScan.CardImageVerificationSheetResult>.alignment)
__heap_0.initializeMemory(as: StripeCardScan.CardImageVerificationSheetResult.self,
    repeating: p0, count: 1)
cdecl_completion(__heap_0, completionContext!)

// Wrapper.swift:435-437 — same shape on CardScanSheet.present
```

C# side receives `__heap_0` and reads the value out, but neither side
ever calls `__heap_0.deallocate()`. Cleanup needs to happen on the
managed side after `MarshalFromSwift<TResult>` or in a `defer` block
in the Swift wrapper.

### S-5. StripePayments `STPPaymentHandler.ConfirmPaymentIntentAsync` existential heap leak in success path

**Severity:** Medium (one leak per confirm-payment call).

```csharp
// libraries/Stripe/StripePayments/obj/Debug/net10.0-ios/swift-binding/StripePayments.cs:46098-46150
public virtual Task<…> ConfirmPaymentIntentAsync(…, ISTPAuthenticationContext authenticationContext, …)
{
    unsafe
    {
        void* authenticationContextHeap = null;
        …
        object[] _asyncCallHolder = new object[] { _tcs, new RetainedSelfPtr(_selfPtr), (object)this, null! };
        …
        // success path:
        var authenticationContextContainer =
            Swift.Runtime.ExistentialContainerFactory.GetOrCreate<ISTPAuthenticationContext>(
                authenticationContext, static __v => new STPAuthenticationContextProxy(__v));
        authenticationContextHeap = NativeMemory.Alloc((nuint)Unsafe.SizeOf<Swift.Runtime.ExistentialContainer1>());
        Unsafe.Copy(authenticationContextHeap, ref authenticationContextContainer);
```

The cancel-path cleanup loop iterates `_asyncCallHolder` looking for
`CopyBufferWithType` entries to `NativeMemory.Free`. But
`authenticationContextHeap` is never wrapped in a `CopyBufferWithType`
and added to `_asyncCallHolder` — it's only held as a local `void*`.
On success completion the local goes out of scope without ever calling
`NativeMemory.Free(authenticationContextHeap)`.

(`FinancialConnectionsSheet.OnEvent` setter is correctly wired with
`DangerousAddRef` + transferred + Swift `_sbWrapClosureContext` deinit
upcall — that path is FIXED.)

---

## Apple frameworks — must-fix (3 items)

Sources: [Resolved/gap-0.10.0-multispecialization-drops-generic-property-accessors.md](Resolved/gap-0.10.0-multispecialization-drops-generic-property-accessors.md),
[Resolved/bug-0.10.0-equals-and-setter-missing-dangerousaddref.md](Resolved/bug-0.10.0-equals-and-setter-missing-dangerousaddref.md),
[Resolved/gap-0.10.0-closure-parameter-skip-renders-apis-unreachable.md](Resolved/gap-0.10.0-closure-parameter-skip-renders-apis-unreachable.md).
(A-3 MusicKit silent tombstones moved to "Future roadmap" below.)

### A-1. MusicKit `MusicLibraryRequest<T>` accessor surface still tombstoned

**Severity:** High (request-builder API unreachable).

Doc resolution closed the StoreKit2 canonical case
(`VerificationResult<SignedType>`) by wiring `EnumHandler` to the
constrained-extension emitter. The MusicKit case (a class, not an enum)
needs the same wiring through `ClassHandler` for closed-instantiation
extension methods:

```csharp
// apple-frameworks/MusicKit/obj/Debug/net10.0-ios26.2/swift-binding/MusicKit.cs:3592
public partial class MusicLibraryRequest<TMusicItemType> : ISwiftObject, ISwiftStruct, IDisposable
    where TMusicItemType : ISwiftObject
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    static nuint _payloadSize = TypeMetadata.RegisterAndGetSize(…);
    // …
    // no .limit / .offset / .filter / .sort / .response() accessors
}
```

13 `MultiSpecialization` skips in MusicKit's
`binding-emission-report.json`. The class is constructible but has no
operational surface — `MusicLibraryRequest<Album>` consumers can't
configure or execute the request.

### A-2. WeatherKit Statistics/Summary query types are bare tombstones

**Severity:** High (entire statistics surface unreachable).

```csharp
// apple-frameworks/WeatherKit/obj/Debug/net10.0-ios26.2/swift-binding/WeatherKit.cs:8259
[global::Swift.OpaqueSwiftType(2)]
public partial class DailyWeatherStatisticsQuery<T> : ISwiftObject, ISwiftStruct, IDisposable
    where T : ISwiftObject
{
    // metadata + Dispose only; no factories, no operational members
}

// MonthlyWeatherStatisticsQuery<T> at WeatherKit.cs:11405, same shape
// HourlyWeatherStatisticsQuery<T>, DailyWeatherSummaryQuery<T> — same shape
```

These are the entry points for WeatherKit's statistics/summary calls
on `WeatherService` (e.g.
`service.weather<T>(for:including:DailyWeatherStatisticsQuery<DayWeather>.stats)`).
With the query types tombstoned and the `WeatherService.weather<T>`
family in the Family-C closure-skip cascade, ~18 of WeatherKit's ~25
async data-fetch endpoints are unreachable from C#.

### A-4. WeatherKit `IEquatable<T>` setters missing AddRef on the value side

**Severity:** Medium (race on GC during property set).

Receiver-side `DangerousAddRef` brackets shipped for `Equals` and
setters (O-9 fix). But the value parameter to nullable
`Measurement<T>?` setters still passes a raw `DangerousGetHandle`
without bracketing on the value's payload:

```csharp
// WeatherKit.cs:18811
set => Gust_Set(value?.Payload.DangerousGetHandle() ?? IntPtr.Zero, value != null);

// Gust_Set body (18780-18794):
private void Gust_Set(IntPtr payload, bool hasValue) {
    unsafe {
        var success = false;
        _payload.DangerousAddRef(ref success);   // receiver bracketed ✓
        try {
            PInvoke_gust_Set_2506D7D4(payload, hasValue, _payload.DangerousGetHandle());
        }
        finally { if (success) _payload.DangerousRelease(); }
    }
}
```

If GC runs between `value?.Payload.DangerousGetHandle()` and the
PInvoke return, `value` can be collected and the handle freed while
Swift is still reading from it. Same pattern across all nullable
`Measurement<T>?` setters in Wind, Pressure, Visibility, Humidity,
UVIndex, etc.

---

## Future roadmap — minor items, not gating 0.11.0

These four are tracked but won't hold the release. None of them block
a real consumer flow: B-1 has a working concrete-class path, N-2 and
L-1 are pure ergonomics, A-3 affects reflection-cookie consumers only.

### Roadmap-1. Nuke `LoadData` vs `DataAsync` callback-arg projection asymmetry

**Severity:** Low (ergonomics, no correctness).
**Source:** [Resolved/bug-0.10.0-callback-arg-projection-asymmetry.md](Resolved/bug-0.10.0-callback-arg-projection-asymmetry.md).

Same Swift signature, two different C# projections depending on whether
the consumer takes the async-returning overload or the callback overload.
Async path lowers correctly; callback path leaks the raw bridge types:

```csharp
// Nuke.cs:16049
public Task<(byte[], Foundation.NSUrlResponse?)> DataAsync(
    Nuke.ImageRequest request, CancellationToken cancellationToken = default)

// Nuke.cs:16320
public unsafe Nuke.ImageTask LoadData(
    Nuke.ImageRequest request,
    Action<Swift.SwiftResult<(Swift.Foundation.Data data, Swift.SwiftOptional<IntPtr> response),
        Nuke.ImagePipeline.Error>> completion)
```

Consumers using `LoadData` have to unwrap `SwiftOptional<IntPtr>` and
re-marshal to `NSUrlResponse?` themselves. Fix: closure-parameter
argument projector should run the same projection rules the async
path does.

### Roadmap-2. MusicKit silent type tombstones referenced from metadata maps

**Severity:** Low (no crash, no normal-flow impact; only affects
reflection-based consumers of cookie maps).
**Source:** [Resolved/gap-0.10.0-silent-type-tombstones-referenced-by-metadata-maps.md](Resolved/gap-0.10.0-silent-type-tombstones-referenced-by-metadata-maps.md).

`binding-emission-report.json` lists 4 `silentTombstones`:

```json
["MusicKit.MusicAttributeProperty", "MusicKit.MusicExtendedAttributeProperty",
 "MusicKit.MusicRelationshipProperty", "MusicKit.PartialMusicProperty"]
```

These are referenced by name in 13+ metadata-cookie maps in MusicKit.cs
but the types themselves are not emitted. Fix: emit silent-tombstone
types or remove from metadata-cookie maps.

### Roadmap-3. BlinkIDUX `ICameraModel.SampleBuffer` collapses to `Swift.AnyType`

**Severity:** Low (concrete class works; only the protocol surface affected).
**Source:** [Resolved/gap-0.10.0-everyprotocol-and-existentials.md](Resolved/gap-0.10.0-everyprotocol-and-existentials.md).

The concrete `Camera` class correctly emits its `SampleBuffer`
property as `IAsyncEnumerable<SampleBuffer>` (AsyncSequence lowering
works). But the `ICameraModel` protocol interface declares it as
`Swift.AnyType`:

```csharp
// libraries/BlinkIDUX/obj/Debug/net10.0-ios/swift-binding/BlinkIDUX.cs:9607
public interface ICameraModel
{
    BlinkIDUX.CameraStatus Status { get; }
    IPreviewSource PreviewSource { get; }
    bool IsSwitchingModes { get; }
    Swift.Foundation.AnyError? Error { get; }
    // SampleBuffer is on the protocol but emits as Swift.AnyType, not IAsyncEnumerable<SampleBuffer>
}
```

Workaround for consumers: use the concrete `Camera` type rather than
the `ICameraModel` interface. Fix: existential-typed property
declarations on protocol interfaces should get the same lowering as
concrete class members.

### Roadmap-4. Lottie `value0` parameter-name leak on static factories

**Severity:** Low (cosmetic; works but ugly).
**Source:** [Resolved/gap-0.10.0-underscore-argument-labels-leak-as-parameter-names.md](Resolved/gap-0.10.0-underscore-argument-labels-leak-as-parameter-names.md).

Most underscore-labelled Swift parameters now lift to `@for` /
`value` / etc. when a fallback name is available. `LottiePlaybackMode`
static factories that take unlabelled primitives still emit `value0`:

```csharp
// libraries/Lottie/obj/Debug/net10.0-ios/swift-binding/Lottie.cs:6630, 6650, 6670, 6704
public static unsafe LottiePlaybackMode FromProgress(
    double? value0, double toProgress, Lottie.LottieLoopMode loopMode)
// and TryGet* extractors at :7026, 7030, 7103, 7107, 7180, …
```

Fix: PlaybackMode case-payload-extractor emitter should pick a
sensible fallback (`from`, `start`, etc.) for the unlabelled-`_`
Swift slot rather than `value0`.

---

## CANNOT_VERIFY group — surface changes during version bump (StoreKit2 / MusicKit)

Four docs flagged CANNOT_VERIFY because the cited 0.10.0 symbols no
longer exist in 0.11.0 output. Each appears to be a fix-via-removal
(the buggy emission path stopped firing because the symbol moved to a
different code path), but worth a one-line confirmation when next
running the validation:

- [Resolved/bug-0.10.0-generic-async-wrapper-symbol-missing.md](Resolved/bug-0.10.0-generic-async-wrapper-symbol-missing.md) —
  `MusicPlayer.Queue.InsertAsync<S>` generic + mangled `_async` symbol
  no longer emitted. Concrete specializations (Album/Playlist/Song/Track)
  now use valid `SBW_CSM_..._async` symbols. Likely O-1 fixed by
  routing through the concrete path.
- [Resolved/bug-0.10.0-some-protocol-generic-constraint-over-broad.md](Resolved/bug-0.10.0-some-protocol-generic-constraint-over-broad.md) —
  `Product.PurchaseAsync<T0>(T0 viewController, …)` generic form no
  longer emitted; non-generic UIViewController + IReadOnlySet overloads
  remain.
- [Resolved/bug-0.10.0-direct-callconvswift-pinvoke-for-skipped-wrapper.md](Resolved/bug-0.10.0-direct-callconvswift-pinvoke-for-skipped-wrapper.md) —
  same surface change as above (StoreKit2 generic UIScene overloads
  not in current emission).
- [Resolved/gap-0.10.0-generic-method-default-overload-missing.md](Resolved/gap-0.10.0-generic-method-default-overload-missing.md) —
  same surface change as above.

These four all collapse around the same StoreKit2 `purchase` symbol
family. Worth reading the 0.11.0 `binding-emission-report.json` for
the StoreKit2 csproj to confirm the symbols moved (vs. were skipped),
but no current consumer-visible defect.

---

## DEFERRED_BY_DESIGN — gaps explicitly closed by the SDK team

Six docs where 0.11.0 closure is a "won't fix" or "design decision" per
the doc's own resolution text, not a code change in the generated C#:

- [Resolved/gap-0.10.0-b06-hashable-predicate-composition.md](Resolved/gap-0.10.0-b06-hashable-predicate-composition.md) —
  Equatable-only types intentionally retain `return 0;` GetHashCode
  stub (safe stance). Only types with explicit Hashable conformance
  route through `SwiftHashable.GetHashCode`. **This means
  `bug-0.10.0-equatable-not-lowered.md` B-1 is FIXED, not
  PARTIALLY_FIXED** — every "still emits `return 0;`" site flagged
  during validation is on an `IEquatable<T>`-only type and is now
  documented intended behavior.
- [Resolved/gap-0.10.0-sendable-annotation-silently-dropped.md](Resolved/gap-0.10.0-sendable-annotation-silently-dropped.md) —
  no C# Sendable equivalent; not pursued.
- [Resolved/gap-0.10.0-closure-parameter-skip-renders-apis-unreachable.md](Resolved/gap-0.10.0-closure-parameter-skip-renders-apis-unreachable.md) —
  some Layer A tombstoning shipped (`[UnsupportedSwiftType]` +
  `[Obsolete(SB0005)]` + throw), others still in
  `UnsupportedClosure`/`UnsupportedSignature` carve-out.
- [Resolved/gap-0.10.0-codable-synthesis-dropped.md](Resolved/gap-0.10.0-codable-synthesis-dropped.md) —
  Phase 1 non-generic surface shipped (`EncodeToJson`/`DecodeFromJson`
  on 73 WeatherKit types); generics deferred to Phase 2.
- [Resolved/gap-0.10.0-everyprotocol-and-existentials.md](Resolved/gap-0.10.0-everyprotocol-and-existentials.md) —
  Case 1 (BlinkIDUX events) closed; protocol-iface case open (B-1
  above).
- [Resolved/bug-0.10.0-swiftui-bridge-free-deadlocks-finalizer-thread.md](Resolved/bug-0.10.0-swiftui-bridge-free-deadlocks-finalizer-thread.md) —
  closed in SDK Session 8 inside swift-bindings/BindingTests; not
  visible from consumer repo. Trust the doc.

---

## Docs Grok did not validate (orchestrator gap)

Six of 48 docs slipped past the parallel fan-out. Outcomes:

| Doc | Disposition |
|---|---|
| `bug-0.10.0-async-toplevel-objc-container-return-emits-pointer-call-on-managed-collection.md` | CANNOT_VERIFY (latent bug, no current consumer trigger) |
| `bug-0.10.0-callback-arg-projection-asymmetry.md` | Validated above as Nuke N-2 |
| `bug-0.10.0-protocol-proxy-class-doesnt-inherit-available.md` | Not validated; needs spot-check on proxy class declarations vs interface `[SupportedOSPlatform]` |
| `bug-0.10.0-swiftui-bridge-free-deadlocks-finalizer-thread.md` | DEFERRED_BY_DESIGN (closed in SDK BindingTests Session 8) |
| `gap-0.10.0-b06-hashable-predicate-composition.md` | DEFERRED_BY_DESIGN (closed via safe-stance) |
| `gap-0.10.0-sendable-annotation-silently-dropped.md` | DEFERRED_BY_DESIGN |

The only real follow-up here is the protocol-proxy `@available`
inheritance check on a few iOS-15-gated protocols (e.g. Stripe and
StoreKit2 protocols with platform attributes). One-off spot-check, not
a blocker.

---

## Out-of-corpus concerns (separate gap surface)

Two Apple-framework areas tracked in project memory but not part of
the 48-doc set. Worth confirming state in 0.11.0 separately:

- **CryptoKit AEAD round-trip** — memory's `project_cryptokit_f3.md`
  said the AEAD primary path was blocked pending a `SymmetricKey →
  AEAD-CSM` marshalling fix. Last commit `a46062c "Promote CryptoKit
  to SHIP, hold Stripe pending F4 (Round 7)"` suggests CryptoKit was
  unblocked, but the current Tests.cs surface only exercises enum-
  case extraction (`HPKE.AEAD.AesGcm128` value + `AllCases`), not
  actual encrypt/decrypt round-trips. Memory may be stale; needs
  confirmation against current SDK behavior.
- **RealityKit / RealityFoundation NativeAOT generic metadata** —
  memory's `project_realitykit_simd_marshalling.md`. Tests.cs still
  pins 7 sites (`MeshBuffer<Vector3>` + variants) with an empirical
  capability probe that distinguishes regression from documented gap.
  No new Pins in 0.11.0; no regression. Same gap, same status.

A 0.9.0 → 0.11.0 source-compat shift on caseless-enum namespaces also
affects already-shipped CryptoKit / BlinkID consumers — captured in
the Ship decision section below.

---

## Must-fix priority table (gates 0.11.0)

10 items, ordered by consumer blast radius. Each one blocks 0.11.0
shipping until landed; one-line dispatch hint per item.

| Priority | Item | Emitter area |
|---|---|---|
| 🔥 1 | S-1 cross-module ext routing for third-party-to-third-party (paths 2-3 in the resolution doc) | `CrossModuleExtensionEmitter` |
| 🔥 2 | S-3 missing `TryGetCompleted` extractor on `.completed(value:)` Swift enum cases | enum-case payload extractor emitter |
| 🔥 3 | S-2 proxy vtable emission for closure-taking protocol methods | proxy / vtable emitter (closure-arg branch) |
| 🔥 4 | A-1 `MultiSpecialization` for class generics (mirror EnumHandler wiring through ClassHandler) | `ConstrainedExtensionEmitter` wiring |
| 🔥 5 | A-2 same as A-1 for WeatherKit Statistics/Summary query<T> types | `ConstrainedExtensionEmitter` wiring |
| P1 | N-1 `DuplicateSignature` disambiguation when collision is via ObjC-bridge container shape | dedup-key emitter |
| P2 | S-5 `authenticationContextHeap` not added to `_asyncCallHolder` cleanup | async-task wrapper emitter |
| P3 | S-4 Swift wrapper `.allocate` without matching `.deinitialize/.deallocate` | wrapper-emitter completion-handler path |
| P4 | N-3 migrate legacy `progress` / `didReceiveData` to `ClosureHandle.Escaping` | callback-trampoline emitter |
| P5 | A-4 value-side `DangerousAddRef` on nullable struct setters | property-setter emitter |

Roadmap-only (not gating 0.11.0): Roadmap-1 (N-2 closure-arg projection
parity), Roadmap-2 (A-3 silent tombstones in cookie maps), Roadmap-3
(B-1 protocol-iface AsyncSequence parity), Roadmap-4 (L-1 `value0`
fallback name). Carry into a future-release tracker.

## Ship decision

0.11.0 is held for the full release wave until every must-fix item
above lands. No partial-wave shipping — the systemically dangerous
0.10.0 issues that 0.11.0 closes (GCHandle leaks, refcount underflow,
stack-pointer SafeHandle, retain-count drift, IEnumerable-as-IntPtr
in async, AsyncSequence lowering, etc.) are valuable to ship across
the whole library set together, not piecemeal while consumer-visible
surface drops linger on Stripe / MusicKit / WeatherKit.

The 0.11.0 → 0.9.0 source-compat shift to call out in release notes
when the wave does ship:

- **Caseless Swift enums emit as C# namespaces, not static types.**
  Same emitter change that fixed `bug-namespace-facade-as-static-class.md`.
  Consumers using `using CryptoKit; HPKE.AEAD.Foo` against the 0.9.0
  package will fail to compile against the 0.11.0 package; they need
  to add `using CryptoKit.HPKE;` or alias. Affects CryptoKit (HPKE,
  AES, Insecure, P256, P384, P521) and BlinkID (BlinkIDSDK).
