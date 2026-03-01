# Workflow Assessment v2 — February 28, 2026

## Purpose

Tracks binary workflow completion for target libraries and prioritized architectural work to reduce remaining gaps. Replaces v1 (moved to `Completed/workflow-assessment-v1.md`).

## Current Status

**Compile gate: 53/53 passing. 7/9 target libraries usable. Sessions 1–2 complete.**

| Library | Lines | Types | Verdict | Key gap |
|---|---|---|---|---|
| **Lottie** | 36,915 | 72 | USABLE | SB0001 on SetValueProvider (NativeAOT only) |
| **Nuke** | 24,794 | 81 | USABLE | NukeExtensions not emitted |
| **BlinkID** | 52,952 | 116 | USABLE | CameraFrame constructor (CMSampleBuffer) |
| **Stripe** (3 modules) | 184,403 | 642 | USABLE | nint params on microdeposit verify |
| **MicroblinkPlatform** | 4,997 | 10 | USABLE (NativeAOT) | SB0001 on SDK constructor |
| **Mappedin** | 51,722 | 120 | USABLE | All SB0001 methods have async/no-callback alternatives |
| **SmartCardIO** | 5,162 | 17 | USABLE | `Transmit(apdu)` now dispatches; remaining: collection returns + existential property |
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

### Capabilities gained in Session 2

| Capability | Libraries improved |
|---|---|
| **ClassReturn dispatch** (Swift class → `Unmanaged.passRetained` → `SwiftMarshal.MarshalFromSwift<T>`) | SmartCardIO (+1: `Transmit → ResponseAPDU`), StripePayments (-1 SB0003) |
| **StructReturn dispatch** (Non-frozen struct → `SwiftIndirectResult` buffer → `SwiftMarshal.MarshalFromSwift<T>`) | BlinkIDUX (-21: 22 Color/Font theme properties on IUXThemeProtocol) |

## SB0003 Analysis — 162 Non-Dispatchable Proxy Members (post-Session 2)

Originally 186 pre-Session 1. Session 1 dispatched ~24 (throwing + void). Session 2 dispatched 24 more (class/struct returns). 162 remaining.

| Category | Original | Dispatched | Remaining | Notes |
|---|---|---|---|---|
| **NonBlittableReturn** | 58 | 24 (S2) | 34 | Class/struct returns dispatched; byte[]/collection returns remain |
| **VoidNonBlitParams** | 33 | 0 | 33 | Session 3 |
| **InterfaceReturn** | 28 | 0 | 28 | Collection returns + optional existentials deferred |
| **ThrowingString** | 21 | 21 (S1) | 0 | ✅ All dispatched |
| **AnyTypeReturn** | 13 | 0 | 13 | Fundamentally opaque |
| **VoidDispatchable** | 11 | 5 (S1) | 6 | 5 were throwing-void, 4 async, 2 regen |
| **ClosureParams** | 10 | 0 | 10 | Complex — deferred |
| **ThrowingBlittable** | 6 | 6 (S1) | 0 | ✅ All dispatched |
| **BlittableNonBlitParams** | 3 | 0 | 3 | Rare, low ROI |
| **VoidThrowing** | 2 | 2 (S1) | 0 | ✅ All dispatched |

### Distribution across target libraries

| Library | Pre-S1 | Post-S1 | Post-S2 | Change |
|---|---|---|---|---|
| StripePayments | 18 | — | 17 | -1 (ClassReturn) |
| StripeCore | 29 | — | 29 | 0 |
| StripePaymentSheet | 23 | — | 23 | 0 |
| Mappedin | 9 | — | 9 | 0 |
| Nuke | 27 | — | 26 | -1 |
| Lottie | 21 | — | 21 | 0 |
| BlinkIDUX | 32 | — | 11 | **-21** (StructReturn theme properties) |
| SmartCardIO | 19 | 9 | 8 | **-1** (Transmit → ResponseAPDU) |
| BlinkID | 5 | — | 5 | 0 |
| MicroblinkPlatform | 3 | — | 3 | 0 |
| **Total** | **186** | — | **162** | **-24** |

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

### Session 2: Non-blittable returns + interface returns ✅ COMPLETE

**Result: 24 SB0003 dispatched. BlinkIDUX 32→11, SmartCardIO 9→8, StripePayments 18→17, Nuke 27→26.**

Two new `MethodDispatchKind` values added:

**2a: Concrete struct/class returns ✅**

Added `ClassReturn` and `StructReturn` dispatch kinds. ClassReturn: Swift returns `UnsafeMutableRawPointer` via `Unmanaged.passRetained(result as AnyObject).toOpaque()`, C# marshals via `SwiftMarshal.MarshalFromSwift<T>()` with `NativeMemory.Alloc(sizeof(IntPtr))` wrapper. StructReturn: C# pre-allocates buffer via `NativeMemory.Alloc(metadata.Size)`, Swift writes via `resultBuf.assumingMemoryBound(to: T.self).initialize(to: result)`. Non-frozen structs use `try/catch` (SafeHandle takes buffer ownership). Frozen+ref-field structs (`ClassWithBufferStruct`) use `try/finally` because `NewFromPayload` copies to a new buffer — original must be freed on success. ClassReturn catch blocks include `Arc.Release(resultPtr)` to release the `+1` retain on marshalling failure. No free functions — SafeHandle takes ownership. Throwing variants follow established error-out-parameter patterns.

Key unlocks achieved:
- SmartCardIO: `Transmit(CommandAPDU) → ResponseAPDU` (the #2 most-wanted method) ✅
- BlinkIDUX: 21 SwiftUI.Color/Font theme properties on `IUXThemeProtocol` ✅
- StripePayments: 1 class return ✅
- Nuke: 1 class/struct return ✅

Lower-than-estimated impact (24 vs 58) because many "NonBlittableReturn" methods also have non-dispatchable params (class/struct params → Session 3 will unlock more).

**2b: Interface/existential returns — diagnosed, deferred**

The 28 InterfaceReturn SB0003 break down into:
- Collection returns (`IReadOnlyList<T>`, `IReadOnlyDictionary<K,V>`) — bound generic types, NOT existentials → need bound generic witness dispatch (complex, deferred)
- Optional existentials (`ICardTerminal?`) — need Optional unwrapping in witness dispatch (deferred)
- Existential property getters (`ICard`) — `ExistentialReturn` handles methods but not property getters in witness dispatch (deferred, could be a small follow-up)

**Tests**: 29 new tests (10 classification, 10 Swift emission, 9 C# body/P/Invoke emission).

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
2. ~~**`SmartCardIO.ICardChannel.Transmit(CommandAPDU) → ResponseAPDU`**~~ — ✅ Session 2a (StructReturn)
3. **`StripePaymentSheet.ICustomerAdapter.AttachPaymentMethodAsync(string)`** — Session 1b: async, stays deferred
4. **`Mappedin.IMPIMapViewDelegate.OnMapChanged(MPIMap)`** — Session 3 (VoidNonBlitParams)
5. ~~**`StripePaymentSheet.IVerifyKYCInfo.City/Country/Line1... → string?`** (×11)~~ — ✅ Session 1a (ThrowingString)
6. ~~**`SmartCardIO.ICardTerminal.WaitForCardPresent(timeout) → bool`**~~ — ✅ Session 1a (ThrowingBlittable)
7. ~~**`BlinkIDUX.IUXThemeProtocol` 22 Color/Font properties**~~ — ✅ Session 2a (StructReturn, 21 dispatched)
8. **`Nuke.IImagePipelineDelegate.DataLoader() → IDataLoading`** — Session 2b (InterfaceReturn)
9. **`StripeCore.IAnalytic.Params → IReadOnlyDictionary`** — Session 2b (InterfaceReturn)
10. **`BlinkIDUX.BlinkIDUXModel` constructor** — Session 4 (ConstrainedExistential)

## Projected End State

```
Session 1: Throwing dispatch + void fix + nint    [~34 SB0003, Medium]    ✅ COMPLETE — SmartCardIO 19→9
Session 2: Non-blittable + interface returns      [24 SB0003, Large]      ✅ COMPLETE — BlinkIDUX 32→11, SmartCardIO 9→8
Session 3: Void dispatch with struct params       [33 SB0003, Large]      → ~129 remaining
Session 4: Constrained existential params         [BlinkIDUX, Large]      → ~129 remaining, 8/9 usable
```

After all 4 sessions: **~57/186 SB0003 eliminated (31%) + 33 from Session 3 = ~90/186 (48%)**, **8/9 target libraries usable**. The ~96 remaining are interface/collection returns (28), closure-in-proxy (10), AnyType/opaque (13), blittable+non-blit-params (3), class-return-with-class-params (~34), and async/regen edge cases (~8). Many "NonBlittableReturn" methods have non-dispatchable params — Session 3 (struct params) will unlock additional class/struct returns that are currently blocked on params, not on the return type itself.
