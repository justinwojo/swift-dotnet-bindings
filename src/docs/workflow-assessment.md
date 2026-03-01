# Workflow Assessment v2 — February 28, 2026

## Purpose

Tracks binary workflow completion for target libraries and prioritized architectural work to reduce remaining gaps. Replaces v1 (moved to `Completed/workflow-assessment-v1.md`).

## Current Status

**Compile gate: 53/53 passing. 7/9 target libraries usable. Session 1 complete.**

| Library | Lines | Types | Verdict | Key gap |
|---|---|---|---|---|
| **Lottie** | 36,915 | 72 | USABLE | SB0001 on SetValueProvider (NativeAOT only) |
| **Nuke** | 24,794 | 81 | USABLE | NukeExtensions not emitted |
| **BlinkID** | 52,952 | 116 | USABLE | CameraFrame constructor (CMSampleBuffer) |
| **Stripe** (3 modules) | 184,403 | 642 | USABLE | nint params on microdeposit verify |
| **MicroblinkPlatform** | 4,997 | 10 | USABLE (NativeAOT) | SB0001 on SDK constructor |
| **Mappedin** | 51,722 | 120 | USABLE | All SB0001 methods have async/no-callback alternatives |
| **SmartCardIO** | 5,162 | 17 | USABLE | `Transmit(apdu)` SB0003 (Session 2a); throwing methods now dispatch |
| **BlinkIDUX** | 11,794 | 57 | BLOCKED | Constructor takes constrained existential |
| **BRLMPrinterKit** | 43 | 0 | NOT APPLICABLE | 99% ObjC SDK, 1 Swift method with unsupported params |

### Corrections from v1

- **BRLMPrinterKit** is not "missing BUILD_LIBRARY_FOR_DISTRIBUTION." The xcframework has ABI JSON, swiftinterface, and 286 ObjC headers. The SDK is 99% Objective-C. The single Swift type (`BRLMPrinterDriver.printImage(withClosures:settings:)`) takes `[() -> Unmanaged<CGImage>?]` + `any BRLMPrintSettingsProtocol` — both unsupported. This library needs an ObjC binding generator, not ours.
- **BlinkIDUX** is not "missing public constructors." `BlinkIDUXModel` has `init(analyzer: any CameraFrameAnalyzer<CameraFrame, UIEvent>, uxSettings:, sessionNumber:)`. The blocker is `any CameraFrameAnalyzer<CameraFrame, UIEvent>` — a constrained existential with associated types.
- **SmartCardIO `IsCardPresent()`** was listed as "Works" in v1 but is actually SB0003 — it's a throwing method, which `BlittableOrString` dispatch rejects.

### Capabilities gained since v1

| Capability | Libraries improved |
|---|---|
| **DataProjection** (Foundation.Data → byte[]) | Lottie (+2 load methods), Nuke (+30 members), BlinkID (+5 raw data), Stripe (+DataProjection roundtrips) |
| **MethodClosureBridge** | Nuke (LoadImage no longer SB0001), Stripe (+12 MCB methods incl. PossibleBrands, FlowController.Create) |
| **Async Task methods** | Lottie (17), Nuke (4+3 IAsyncEnumerable), BlinkID (6), Stripe (67), Mappedin (16) |
| **nint→int overloads** | Lottie (2), Nuke (1), Mappedin (2) |
| **SwiftUI theme properties** | MicroblinkPlatform (+18 DocumentScan properties, 40 total) |

### Capabilities gained in Session 1

| Capability | Libraries improved |
|---|---|
| **ThrowingBlittableOrString dispatch** | SmartCardIO (19→9 SB0003), Stripe (ThrowingString properties dispatch), Lottie, BlinkID |
| **Optional nint→int overloads** | BlinkID (DateOfBirth.Day/Month/Year as `int?`), SmartCardIO (APDU Nc/Ne/Nr), Stripe (DateOfBirth fields) |

## SB0003 Analysis — 186 Non-Dispatchable Proxy Members (pre-Session 1)

These are methods on protocol proxy classes that throw `NotSupportedException` because the `WitnessDispatchEmitter` can't generate Swift wrapper dispatch for them.

| Category | Count | Description |
|---|---|---|
| **NonBlittableReturn** | 58 | Returns class/struct/byte[] |
| **VoidNonBlitParams** | 33 | Void return, Swift struct/class params |
| **InterfaceReturn** | 28 | Returns protocol existential (`I*` type) |
| **ThrowingString** | 21 | Returns string but method throws |
| **AnyTypeReturn** | 13 | Returns AnyType/object/AnyHashable (opaque) |
| **VoidDispatchable** | 11 | Should already dispatch (void, simple params) — investigate |
| **ClosureParams** | 10 | Closure callback params in protocol method |
| **ThrowingBlittable** | 6 | Returns bool/byte/int but throws |
| **BlittableNonBlitParams** | 3 | Returns blittable but non-blittable param blocks |
| **VoidThrowing** | 2 | Void return, throws |

### Distribution across target libraries

| Library | SB0003 | SB0001 | SB0002 |
|---|---|---|---|
| StripePayments | 18 | 41 | 72 |
| StripeCore | 29 | 11 | 3 |
| StripePaymentSheet | 23 | 5 | 7 |
| Mappedin | 9 | 45 | 1 |
| Nuke | 27 | 5 | 0 |
| Lottie | 21 | 24 | 10 |
| BlinkIDUX | 32 | 0 | 0 |
| SmartCardIO | ~~19~~ 9 | 1 | 0 |
| BlinkID | 5 | 3 | 0 |
| MicroblinkPlatform | 3 | 1 | 0 |

---

## Prioritized Sessions

### Session 1: Throwing dispatch + void investigation + Optional nint ✅ COMPLETE

**Result: ~34 SB0003 → dispatched + ergonomic Optional nint overloads. SmartCardIO 19→9 SB0003.**

Three related items that all build on existing `WitnessDispatchEmitter` infrastructure:

**1a: Throwing blittable + string + void dispatch ✅**

Added `MethodDispatchKind.ThrowingBlittableOrString` (value 3). Modified `ClassifyMethodDispatch` to route throwing methods with blittable/string/void returns and dispatchable params. `EmitThrowingMethodAccessor` generates Swift `do/catch` with error-out-parameter (`UnsafeMutablePointer<UnsafeRawPointer?>`). Value-returning: Swift returns `UnsafeMutableRawPointer?` (nil = error), C# checks `resultPtr == IntPtr.Zero`. Void: C# checks `errorOut != IntPtr.Zero`. P/Invoke emission unified with `ExistentialReturn` block. Secondary C#-side projected-type validation gates prevent dispatch when TypeDatabase degrades types.

**1b: VoidDispatchable investigation ✅**

Root cause: 5 methods were throwing void (fixed by 1a), 4 were async (stays deferred), 2 need regen diagnosis. No additional code changes needed beyond 1a.

**1c: Optional nint→int overloads ✅**

Extended `NativeIntOverloadEmitter` to unwrap `Optional<Swift.Int>` → `int?` and `Optional<Swift.UInt>` → `uint?` parameters. Indexer overloads not extended (SubscriptHandler filters OptionalProjection before indexers).

**Tests**: 21 new tests (12 WitnessDispatchEmitter, 5 ProtocolProxyEmitter, 4 NativeIntOverloadEmitter).

---

### Session 2: Non-blittable returns + interface returns

**Impact: 86 SB0003 → dispatched (58 NonBlittableReturn + 28 InterfaceReturn)**

Two related problems — dispatch through witness table when the return type isn't a simple scalar:

**2a: Concrete struct/class returns (58 SB0003)**

Requires witness dispatch returning a Swift struct/class through the existential witness table. Two sub-paths:
- Class returns (reference type) — wrapper allocates and returns a pointer, C# wraps in SafeHandle. Closest pattern: `ExistentialReturn` already handles returning existential containers.
- Struct returns (non-frozen) — `SwiftIndirectResult` pattern through witness dispatch. Caller pre-allocates return buffer.

Key unlocks:
- SmartCardIO: `Transmit(CommandAPDU) → ResponseAPDU` (the #1 most-wanted method)
- BlinkIDUX: 22 SwiftUI.Color/Font theme properties on `IUXThemeProtocol`
- Nuke: 8 struct/class returns (ImageContainer, ImageResponse, etc.)
- BlinkID: 4 struct returns
- Stripe: 12 struct/class returns across 3 modules

**2b: Interface/existential returns in proxies (28 SB0003)**

Protocol methods returning other protocol existentials. The `ExistentialReturn` dispatch kind already handles this for concrete class methods, but proxy witness dispatch doesn't use it. Extending `ExistentialReturn` infrastructure to the proxy witness dispatch context.

Key unlocks:
- SmartCardIO: `Connect() → ICard`, `GetList() → IReadOnlyList<ICardTerminal>`, `Terminal(name) → ICardTerminal?`
- StripeCore: 10 analytics protocol methods returning `IReadOnlyDictionary<string, object>`
- Nuke: 5 delegate factory methods
- Stripe: 5 protocol methods across PaymentSheet/Payments

**Estimated effort**: Large.

---

### Session 3: Void dispatch with struct params

**Impact: 33 SB0003 → dispatched**

Delegate callback methods that receive Swift struct/class event data. The proxy needs to pass these through witness dispatch. Currently blocked because params contain non-blittable types. This is the reverse direction from Session 2 — instead of returning structs FROM Swift, this is passing structs TO Swift through witness dispatch.

Key unlocks:
- Mappedin: All 8 `IMPIMapViewDelegate` callbacks (OnMapChanged, OnBlueDotPositionUpdate, etc.)
- Nuke: 7 `IImagePipelineDelegate` callbacks
- MicroblinkPlatform: 3 `IMicroblinkPlatformSDKDelegate` callbacks
- Stripe: 11 callbacks across 3 modules
- BlinkIDUX: 2 camera model callbacks
- Lottie: 2 callbacks

**Estimated effort**: Large. Requires witness dispatch to marshal C# struct/class values into Swift ABI format for the witness call.

---

### Session 4: Constrained existential parameters

**Impact: Unblocks BlinkIDUX → 8/9 target libraries usable**

`BlinkIDUXModel.init(analyzer: any CameraFrameAnalyzer<CameraFrame, UIEvent>, ...)` requires supporting constrained existentials — protocol types with associated type constraints applied at the call site. May need type metadata for the constrained protocol witness table.

Key unlocks:
- BlinkIDUX: `BlinkIDUXModel` constructor (the only way to create the main scanning model)

**Estimated effort**: Large.

---

### Future / Deferred

| Item | Count | Why deferred |
|---|---|---|
| Closure params in proxy dispatch | 10 SB0003 | Requires closure marshalling through witness table — very complex |
| AnyType returns | 13 SB0003 | Type-erased returns are fundamentally opaque without Swift runtime introspection |
| BlittableNonBlitParams | 3 SB0003 | Rare pattern, low ROI |
| NukeExtensions.loadImage(into:) | 1 method | ObjC extension not in ABI JSON — would need swiftinterface extension parsing |

## Top 10 Most-Wanted Methods

1. ~~**`SmartCardIO.ICardTerminal.IsCardPresent() → bool`**~~ — ✅ Session 1a (ThrowingBlittable)
2. **`SmartCardIO.ICardChannel.Transmit(CommandAPDU) → ResponseAPDU`** — Session 2a (NonBlittableReturn)
3. **`StripePaymentSheet.ICustomerAdapter.AttachPaymentMethodAsync(string)`** — Session 1b: async, stays deferred
4. **`Mappedin.IMPIMapViewDelegate.OnMapChanged(MPIMap)`** — Session 3 (VoidNonBlitParams)
5. ~~**`StripePaymentSheet.IVerifyKYCInfo.City/Country/Line1... → string?`** (×11)~~ — ✅ Session 1a (ThrowingString)
6. ~~**`SmartCardIO.ICardTerminal.WaitForCardPresent(timeout) → bool`**~~ — ✅ Session 1a (ThrowingBlittable)
7. **`BlinkIDUX.IUXThemeProtocol` 22 Color/Font properties** — Session 2a (NonBlittableReturn)
8. **`Nuke.IImagePipelineDelegate.DataLoader() → IDataLoading`** — Session 2b (InterfaceReturn)
9. **`StripeCore.IAnalytic.Params → IReadOnlyDictionary`** — Session 2b (InterfaceReturn)
10. **`BlinkIDUX.BlinkIDUXModel` constructor** — Session 4 (ConstrainedExistential)

## Projected End State

```
Session 1: Throwing dispatch + void fix + nint    [~34 SB0003, Medium]    ✅ COMPLETE — SmartCardIO 19→9
Session 2: Non-blittable + interface returns      [86 SB0003, Large]      → ~60 remaining
Session 3: Void dispatch with struct params       [33 SB0003, Large]      → ~27 remaining
Session 4: Constrained existential params         [BlinkIDUX, Large]      → ~27 remaining, 8/9 usable
```

After all 4 sessions: **~159/186 SB0003 eliminated (86%)**, **8/9 target libraries usable**. The ~27 remaining are closure-in-proxy (10), AnyType/opaque (13), and edge cases (4) — all deferred.
