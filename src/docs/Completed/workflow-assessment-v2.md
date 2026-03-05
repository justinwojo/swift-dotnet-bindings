# Workflow Assessment v2 — February 28, 2026

## Purpose

Tracks binary workflow completion for target libraries and prioritized architectural work to reduce remaining gaps. Replaces v1 (moved to `Completed/workflow-assessment-v1.md`).

## Current Status

**Compile gate: 53/53 passing. 8/9 target libraries usable. Sessions 1–4 complete.**

| Library | Lines | Types | Verdict | Key gap |
|---|---|---|---|---|
| **Lottie** | 36,915 | 72 | USABLE | SB0001 on SetValueProvider (NativeAOT only) |
| **Nuke** | 24,794 | 81 | USABLE | NukeExtensions not emitted |
| **BlinkID** | 52,952 | 116 | USABLE | CameraFrame constructor (CMSampleBuffer) |
| **Stripe** (3 modules) | 184,403 | 642 | USABLE | nint params on microdeposit verify |
| **MicroblinkPlatform** | 4,997 | 10 | USABLE (NativeAOT) | SB0001 on SDK constructor |
| **Mappedin** | 51,722 | 120 | USABLE | All SB0001 methods have async/no-callback alternatives |
| **SmartCardIO** | 5,162 | 17 | USABLE | `Transmit(apdu)` now dispatches; remaining: collection returns + existential property |
| **BlinkIDUX** | 12,055 | 57 | USABLE | ConstrainedExistentialBridge unblocked constructor |
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

### Capabilities gained in Session 3

| Capability | Libraries improved |
|---|---|
| **Class/struct param dispatch** (C# `.Payload.DangerousGetHandle()` → Swift `Unmanaged<T>.fromOpaque` for classes, `assumingMemoryBound(to:).pointee` for structs) | Mappedin (**-7**: delegate callbacks), Nuke (**-10**: pipeline delegate + class return unlocks), StripePaymentSheet (**-3**), StripeCore (**-1**), Lottie (**-2**), SmartCardIO (**-1**) |

Session 3 scope was broader than "VoidNonBlitParams=33" — extended param dispatch across ALL dispatch kinds (BlittableOrString, ThrowingBlittableOrString, ExistentialReturn, ClassReturn, StructReturn). This unlocked previously-blocked ClassReturn/StructReturn methods that had class/struct params, accounting for ~10 bonus dispatches beyond the 24 pure VoidNonBlitParams target.

### Capabilities gained in Session 4

| Capability | Libraries improved |
|---|---|
| **ConstrainedExistentialBridge** (`@_silgen_name` wrapper for `any Protocol<ConcreteA, ConcreteB>` params) | BlinkIDUX (**BLOCKED → USABLE**: +2 constructors) |
| **ISwiftObject.SwiftHandle** (DIM for raw handle extraction) | All generated types (infrastructure) |

## SB0003 Analysis — 128 Non-Dispatchable Proxy Members (post-Session 3)

Originally 186 pre-Session 1. Session 1 dispatched ~24 (throwing + void). Session 2 dispatched 24 (class/struct returns). Session 3 dispatched 34 (class/struct params across all dispatch kinds — 24 VoidNonBlitParams + ~10 bonus from previously-blocked return methods). 128 remaining.

| Category | Original | Dispatched | Remaining | Notes |
|---|---|---|---|---|
| **NonBlittableReturn** | 58 | 24 (S2) + ~10 (S3 bonus) | ~24 | S3 unlocked class/struct returns that had class/struct params |
| **VoidNonBlitParams** | 33 | ~24 (S3) | ~9 | Remaining have enum/closure/other non-dispatchable params |
| **InterfaceReturn** | 28 | 0 | 28 | Collection returns + optional existentials deferred |
| **ThrowingString** | 21 | 21 (S1) | 0 | ✅ All dispatched |
| **AnyTypeReturn** | 13 | 0 | 13 | Fundamentally opaque |
| **VoidDispatchable** | 11 | 5 (S1) | 6 | 5 were throwing-void, 4 async, 2 regen |
| **ClosureParams** | 10 | 0 | 10 | Complex — deferred |
| **ThrowingBlittable** | 6 | 6 (S1) | 0 | ✅ All dispatched |
| **BlittableNonBlitParams** | 3 | 0 | 3 | Rare, low ROI |
| **VoidThrowing** | 2 | 2 (S1) | 0 | ✅ All dispatched |

### Distribution across target libraries

| Library | Pre-S1 | Post-S1 | Post-S2 | Post-S3 | Change (S3) |
|---|---|---|---|---|---|
| StripePayments | 18 | — | 17 | 17 | 0 |
| StripeCore | 29 | — | 29 | 28 | **-1** |
| StripePaymentSheet | 23 | — | 23 | 20 | **-3** |
| Mappedin | 9 | — | 9 | 2 | **-7** (delegate callbacks) |
| Nuke | 27 | — | 26 | 16 | **-10** (pipeline delegate + return unlocks) |
| Lottie | 21 | — | 21 | 19 | **-2** |
| BlinkIDUX | 32 | — | 11 | 11 | 0 |
| SmartCardIO | 19 | 9 | 8 | 7 | **-1** |
| BlinkID | 5 | — | 5 | 5 | 0 |
| MicroblinkPlatform | 3 | — | 3 | 3 | 0 |
| **Total** | **186** | — | **162** | **128** | **-34** |

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

### Session 3: Class/struct param dispatch ✅ COMPLETE

**Result: 34 SB0003 dispatched (24 VoidNonBlitParams + ~10 bonus from previously-blocked return methods). Mappedin 9→2, Nuke 26→16, StripePaymentSheet 23→20.**

Extended `IsTypeDispatchable` to accept Swift classes (`TypeRecordKind.Class`, non-ObjC, non-generic) and indirect structs (non-frozen, or frozen+RefFields). This unlocked ALL dispatch kinds for methods with class/struct params — not just VoidNonBlitParams.

**Core changes:**
- Extracted `IsSwiftClassType(TypeSpec?)` and `IsIndirectStructType(TypeSpec?)` from `IsClassReturn`/`IsStructReturn` for raw type identification without circular dependency
- C# marshalling: `.Payload.DangerousGetHandle()` extracts IntPtr from SafeHandle for both class and indirect struct params
- Swift class unmarshal: `Unmanaged<Module.ClassName>.fromOpaque(rawPtr).takeUnretainedValue()` (C# retains ownership)
- Swift struct unmarshal: `rawPtr.assumingMemoryBound(to: Module.StructName.self).pointee` (creates copy)
- Refactored 3 inline param loops (`EmitMethodAccessor`, `EmitThrowingMethodAccessor`, `EmitExistentialMethodAccessor`) to use shared `EmitParameterUnmarshal` helper
- Property dispatch regression prevention: class/struct getters route through ClassReturn/StructReturn path, not blittable
- Native-remapped type exclusion: `IsSwiftClassType`/`IsIndirectStructType` reject types with `NativeTypeName` (e.g., `Foundation.URL → NSUrl`) — these use different marshalling (FromX/ToX), not `.Payload`
- P/Invoke emission fix: `ProtocolProxyEmitter.SwiftObject.cs` property getter/setter branches exclude class/struct types (with `Swift.String` carve-out) so they fall through to ClassReturn/StructReturn P/Invoke emission

Key unlocks achieved:
- Mappedin: 7 `IMPIMapViewDelegate` callbacks (`OnMapChanged(MPIMap)`, etc.) ✅
- Nuke: 10 methods (pipeline delegate + class return methods unblocked by param fix) ✅
- StripePaymentSheet: 3 callbacks ✅
- StripeCore: 1 callback ✅
- Lottie: 2 callbacks ✅
- SmartCardIO: 1 callback ✅

Remaining 2 Mappedin SB0003: `OnStateChanged(MPIState)` (non-simple enum param) and `Matches → IReadOnlyList` (collection return).

**Tests**: 24 new tests (16 WitnessDispatchEmitter, 8 ProtocolProxyEmitter). Includes regression tests for property P/Invoke emission and native-remapped type exclusion.

---

### Session 4: Constrained existential parameters ✅ COMPLETE

**Result: BlinkIDUX BLOCKED → USABLE. 8/9 target libraries usable. +2 constructors bridged (BlinkIDUXModel + ScanningViewModel).**

`ConstrainedExistentialBridge` emitter generates `@_silgen_name` Swift wrappers that accept `UnsafeMutableRawPointer` for constrained existential params (e.g., `any CameraFrameAnalyzer<CameraFrame, UIEvent>`). Swift wrapper casts via `Unmanaged<AnyObject>.fromOpaque(...).takeUnretainedValue() as! any Protocol<A, B>`. Class return via `Unmanaged.passRetained(result).toOpaque()`.

**Core changes:**
- `ISwiftObject.SwiftHandle` — default interface member (DIM) on `ISwiftObject` for raw pointer extraction. Overridden by all heap-backed runtime types (17 files) and emitted by generators (ClassHandler, NonFrozenStructHandler, EnumHandler, FrozenStructHandler, ProtocolProxyEmitter, ModuleHandler)
- `IsConstrainedExistential(TypeSpec, ITypeDatabase)` — detects both `ProtocolListTypeSpec` and `NamedTypeSpec` forms (ABI JSON parses constrained existentials as NamedTypeSpec with generic params from printedName)
- `ClassBound` flag (`TypeRecordFlags.1<<7`) — serialized/deserialized in module database. Set from `ProtocolDecl.IsClassBound`. Infrastructure prepared but bridge does NOT gate on class-bound (ISwiftObject.SwiftHandle provides runtime safety for non-class-bound protocols)
- Demangling resilience — `demangler.Run()` wrapped in try-catch in `CreateMethodDecl` (constrained existential mangled names contain `AssociatedType` node kind that throws `NotImplementedException`)
- MethodHandler integration — bridge check inserted inside `hasExistentialArg` block, between ExistentialBypassEmitter and fallback skip. Also retained after the block for non-flagged paths

Key unlocks:
- BlinkIDUX: `BlinkIDUXModel(ISwiftObject analyzer, ScanningUXSettings uxSettings, nint sessionNumber)` ✅
- BlinkIDUX: `ScanningViewModel(ISwiftObject analyzer, ScanningUXSettings uxSettings, nint sessionNumber)` ✅

**Tests**: 23 new tests (6 IsConstrainedExistential, 2 ClassBound flag, 6 bridge emission, 3 RenderConstrainedExistentialSwiftType, 2 gate tests, 4 NamedTypeSpec form tests).

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
4. ~~**`Mappedin.IMPIMapViewDelegate.OnMapChanged(MPIMap)`**~~ — ✅ Session 3 (ClassParamDispatch)
5. ~~**`StripePaymentSheet.IVerifyKYCInfo.City/Country/Line1... → string?`** (×11)~~ — ✅ Session 1a (ThrowingString)
6. ~~**`SmartCardIO.ICardTerminal.WaitForCardPresent(timeout) → bool`**~~ — ✅ Session 1a (ThrowingBlittable)
7. ~~**`BlinkIDUX.IUXThemeProtocol` 22 Color/Font properties**~~ — ✅ Session 2a (StructReturn, 21 dispatched)
8. **`Nuke.IImagePipelineDelegate.DataLoader() → IDataLoading`** — Session 2b (InterfaceReturn)
9. **`StripeCore.IAnalytic.Params → IReadOnlyDictionary`** — Session 2b (InterfaceReturn)
10. **`BlinkIDUX.BlinkIDUXModel` constructor** — Session 4 (ConstrainedExistential)

## Projected End State

```
Session 1: Throwing dispatch + void fix + nint    [~24 SB0003, Medium]    ✅ COMPLETE — SmartCardIO 19→9
Session 2: Non-blittable + interface returns      [24 SB0003, Large]      ✅ COMPLETE — BlinkIDUX 32→11, SmartCardIO 9→8
Session 3: Class/struct param dispatch            [34 SB0003, Large]      ✅ COMPLETE — Mappedin 9→2, Nuke 26→16
Session 4: Constrained existential params         [BlinkIDUX, Large]      ✅ COMPLETE — 8/9 usable
```

After Sessions 1–3: **58/186 SB0003 eliminated (31%)**. After Session 4: **8/9 target libraries usable**. The 128 remaining are: collection/interface returns (28), non-blittable returns with remaining non-dispatchable params (~24), non-simple enum params (~9), closure-in-proxy (10), AnyType/opaque (13), async/regen edge cases (6), blittable+non-blit-params (3), and other (~35).
