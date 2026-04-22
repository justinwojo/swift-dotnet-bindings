# Ship-blockers — 2026-04-21 drop

**What was tested:** `SwiftBindings.Sdk 0.8.0`, `SwiftBindings.Runtime 0.8.0`, `SwiftBindings.Templates 0.8.0`, `SwiftBindings.Apple 26.2.0` — all four nupkgs freshly re-built and dropped into `/Users/wojo/Dev/swift-dotnet-packages/local-packages/` at `2026-04-21 13:29`. Same version numbers as the Round 4 (2026-04-19) baseline, different contents.

**Validation flow run against the drop:**
1. `dotnet nuget locals all --clear`
2. Wiped every `obj/**/swift-binding/` and `swift-binding.stamp` under `libraries/` and `apple-frameworks/`.
3. Rebuilt all 6 third-party libraries, all 12 Apple-framework packages, and all 12 Stripe products (two-pass).
4. Booted a simulator from the Nuke fleet and ran `BuildTestApp` + `ValidateSim` for everything that built.
5. Ran `BuildTestApp --device` + `ValidateDevice` on a connected iPhone 13.

**Headline:** 9 of 12 Apple frameworks build clean, but every one of those 9 fails the device-install step with the same packaging error. The other 3 Apple frameworks (RoomPlan, MusicKit, CryptoKit) fail to build at all. Stripe (299/299) and 5 of 6 third-party libraries are green on both sim and device; the sixth (Kingfisher) builds but its test program no longer compiles because a payload enum case lost its factory method.

Consumer-side artefacts per finding live under `/Users/wojo/Dev/swift-dotnet-packages/apple-frameworks/<Name>/obj/Debug/net10.0-ios26.2/swift-binding/` (multi-TFM) or `…/libraries/<Name>/obj/Debug/net10.0-ios/swift-binding/` (single-TFM). Full build logs are in `/tmp/ship-readiness-2026-04-21/`.

---

## Issue 1 — `Info.plist` missing from the device slice of every multi-TFM wrapper xcframework (P0, blocks 12 packages)

**Symptom.** Every Apple-framework device install fails with:

```
ERROR: Failed to install the app on the device. (com.apple.dt.CoreDeviceError error 3002)
  Info.plist missing at …/Frameworks/ActivityKitSwiftBindings.framework/Info.plist
  (MIInstallerErrorDomain error 35 — MICreateCFBundleEnforcingInfoPlistSize)
```

**Evidence.** Every multi-TFM wrapper xcframework the SDK emits has an `Info.plist` in the simulator slice but not in the device slice:

```
apple-frameworks/ActivityKit/obj/Debug/net10.0-ios26.2/swift-binding/ActivityKitSwiftBindings.xcframework/
├── Info.plist
├── ios-arm64/ActivityKitSwiftBindings.framework/
│   └── ActivityKitSwiftBindings                        ← binary, no Info.plist
└── ios-arm64-simulator/ActivityKitSwiftBindings.framework/
    ├── ActivityKitSwiftBindings
    └── Info.plist                                      ← present here only
```

Reproduced on ActivityKit, FamilyControls, LiveCommunicationKit, ProximityReader, StoreKit2, TipKit, Translation, WeatherKit, WorkoutKit — i.e. every multi-TFM Apple-framework package that compiles cleanly. Not reproduced on any single-TFM (`net10.0-ios`) wrapper: `NukeSwiftBindings.xcframework`, `StripeCoreSwiftBindings.xcframework`, etc. all have `Info.plist` in both `ios-arm64` and `ios-arm64-simulator`.

Audit output:

```
ActivityKit            device=NO  sim=YES
FamilyControls         device=NO  sim=YES
LiveCommunicationKit   device=NO  sim=YES
ProximityReader        device=NO  sim=YES
StoreKit2              device=NO  sim=YES
TipKit                 device=NO  sim=YES
Translation            device=NO  sim=YES
WeatherKit             device=NO  sim=YES
WorkoutKit             device=NO  sim=YES
```

**Shape of the bug.** Something in the multi-TFM wrapper-packaging path emits the `Info.plist` for the simulator slice of the xcframework but skips it for the device slice. The single-TFM path is fine, which points at a branch that runs only when multiple slices are produced and rewrites / re-archives the device slice without copying the plist along. The bundle root's `Info.plist` (xcframework-level) is produced for both paths; the issue is specifically the per-framework embedded plist inside the device slice.

**Where to look.** Whichever step materializes the wrapper `.framework` bundle inside the xcframework slices — likely the Swift-wrapper compile / `-emit-library` follow-up that turns the raw `.dylib` + headers into a `.framework` directory. The device branch is probably dropping a `CFBundlePackageType=FMWK` Info.plist that the sim branch writes correctly. A diff of sim vs device slice construction for a multi-TFM package should make this obvious.

**Why the sim tests still passed.** iOS Simulator accepts framework bundles without `Info.plist`; physical devices don't. So the regression slipped through sim-only validation.

**Reproduction.**

```
cd /Users/wojo/Dev/swift-dotnet-packages
dotnet nuget locals all --clear
rm -rf apple-frameworks/ActivityKit/obj
dotnet build apple-frameworks/ActivityKit/SwiftBindings.ActivityKit.csproj
find apple-frameworks/ActivityKit/obj/Debug/net10.0-ios26.2/swift-binding/ActivityKitSwiftBindings.xcframework \
  -name Info.plist
# → only the simulator slice + xcframework root appear; ios-arm64 slice missing
```

---

## Issue 2 — RoomPlan: generated C# references an undefined `simd` namespace (P0, blocks 1 package)

**Symptom.** `dotnet build apple-frameworks/RoomPlan/SwiftBindings.RoomPlan.csproj` → 8 `CS0246: The type or namespace name 'simd' could not be found` (doubled to 16 in the raw log because MSBuild prints each error twice). All in the generated `.cs`, all on one target framework (`net10.0-ios26.2` — RoomPlan is single-TFM).

**Evidence.**

```csharp
// apple-frameworks/RoomPlan/obj/Debug/net10.0-ios26.2/swift-binding/RoomPlan.cs:6425-6447
[return: global::Swift.OriginalSwiftType("Swift.SIMD3")]
private simd.simd_float3<float> Center_Get()
{
    unsafe {
        …
        var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<simd.simd_float3<float>>();
        …
        return SwiftMarshal.MarshalFromSwift<simd.simd_float3<float>>(resultPtr);
    }
}
```

No `using simd;`, no namespace alias, no such type in scope. Full list of the emitted references:

```
$ grep -oE "simd\.[A-Za-z_0-9]+" RoomPlan.cs | sort -u
simd.simd_float3
```

And the file's `using` block:

```
using System; using System.Collections.Generic; using System.Diagnostics;
using System.Diagnostics.CodeAnalysis; using System.Linq;
using System.Runtime.CompilerServices; using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Swift; using System.Threading.Tasks;
using Swift; using Swift.Runtime; using Swift.Runtime.InteropServices;
using System.ComponentModel; using RoomPlan.SwiftInterop;
using Utf8Slice = global::Swift.Runtime.Utf8Slice;
```

**Shape of the bug.** The C# emitter is writing `simd.simd_float3<float>` when projecting Swift's `SIMD3<Float>`. Two problems:

1. `simd` is a Swift module name; there is no C# namespace called `simd`.
2. `simd_float3` in Swift is a typealias, not a generic type — `simd_float3<float>` is malformed even if the namespace existed.

Round 4 named this as blocker #5 ("RoomPlan `CapturedStructure` structural lists + `simd_float4x4` → `Matrix4x4`"). This drop looks like a partial attempt — the emitter now produces *something* for SIMD types instead of skipping them, but the projection isn't wired up to an actual C# type.

Expected projection: `System.Numerics.Vector3` for `SIMD3<Float>`, `System.Numerics.Matrix4x4` for `simd_float4x4`. Either a targeted type-DB entry or a generic "Swift.SIMD3 → Vector3" rule.

**Where to look.** The emitter path that resolves `Swift.SIMD3` / `simd_float3` when building a C# type reference. `OriginalSwiftType("Swift.SIMD3")` is still correctly annotated, so the Swift-side identity is preserved — the missing piece is the managed projection entry.

---

## Issue 3 — MusicKit: `Data(…)` initializer ambiguity in the emitted Swift wrapper (P0, blocks iOS + tvOS)

**Symptom.** `MusicKit.Wrapper.swift` compilation fails on `net10.0-ios26.2` and `net10.0-tvos26.2` with 174 errors each (macOS/MacCatalyst slices build clean). Two distinct Swift compiler diagnostics:

- `error: initializer 'init(_:)' requires the types 'Album' and 'Data.Element' (aka 'UInt8') be equivalent`
- `error: type of expression is ambiguous without a type annotation`

**Evidence.** `apple-frameworks/MusicKit/obj/Debug/net10.0-ios26.2/swift-binding/MusicKit.Wrapper.swift:4534`:

```swift
// Concrete specialization: MusicKit.MusicItemCollection<MusicKit.Album>.init<MusicKit.Album, [UInt8]>
@_cdecl("SBW_CSM_MusicKit_MusicItemCollection_MusicKit_Album_Swift_Array_Swift_UInt8_init_863933F9")
public func SBW_…_init_863933F9(
    _ resultPtr: UnsafeMutableRawPointer,
    _ _elements: UnsafeRawPointer,
    _ _elementsLen: Int
) {
    let _result = MusicKit.MusicItemCollection<MusicKit.Album>(
        Data(bytesNoCopy: UnsafeMutableRawPointer(mutating: _elements),
             count: _elementsLen, deallocator: .none))   // ← error here
    …
}
```

**Shape of the bug.** The wrapper is trying to project a Swift initializer whose input is `[UInt8]` (contiguous byte buffer) by constructing a `Foundation.Data` from the `(ptr, len)` pair and passing it to `MusicItemCollection.init(_:)`. But `MusicItemCollection<Album>` has *multiple* `init(_:)` overloads and the Swift type checker rejects the `Data` overload because `Album != UInt8` — there's no `init` on `MusicItemCollection<Album>` that accepts a `Data` of raw bytes. The `[UInt8]` specialization generally should not exist for this type; at minimum the wrapper must use an explicit initializer or cast that matches one of the real overloads.

Two lower-level questions worth answering in the fix:

1. Why is the specializer emitting the `Array<UInt8>` → `Data` collapse at all for `MusicItemCollection<Album>`? The real overloads are `init<S: Sequence>(_ elements: S) where S.Element == Album` and `init(arrayLiteral…)`. Passing `Data` is never a valid call.
2. Why does iOS + tvOS fail but macOS + MacCatalyst build? Probably because the specializer table is seeded from the set of `init`s visible on each platform and the macOS set doesn't include the offending overload.

**Where to look.** Specialization planner for generic initializers on collection-shaped types. The wrapper gen is clearly enumerating specializations across concrete type-argument tuples; it needs to filter specializations whose argument types have no matching overload on the target type.

---

## Issue 4 — CryptoKit: method-level generic parameter `H` leaks into the wrapper, plus missing `@available` guards on SHA3 (P0, blocks all 4 TFMs)

**Symptom.** `CryptoKit.Wrapper.swift` fails on every TFM:

- iOS/tvOS: 182 errors each (mostly `error: cannot find type 'H' in scope` plus `'SHA3_256' is only available in iOS 26.0 or newer`)
- macOS/MacCatalyst: 36 errors each (same `H` issue, `@available` floor not tripped)

**Evidence.** `apple-frameworks/CryptoKit/obj/Debug/net10.0-ios26.2/swift-binding/CryptoKit.Wrapper.swift:816`:

```swift
@_cdecl("SBW_CSM_CryptoKit_HMAC_CryptoKit_SHA256_Foundation_Data_authenticationCode_D9A40A0F")
public func SBW_…_authenticationCode_D9A40A0F(
    _ resultPtr: UnsafeMutableRawPointer,
    _ _data: UnsafeRawPointer,
    _ _key: UnsafeRawPointer
) {
    let _result = CryptoKit.HMAC<CryptoKit.SHA256>.authenticationCode(
        for: _data.assumingMemoryBound(to: Foundation.Data.self).pointee,
        using: unsafeBitCast(OpaquePointer(_key), to: SymmetricKey.self))
    resultPtr.initializeMemory(
        as: (CryptoKit.HashedAuthenticationCode<H>).self,   // ← 'H' not in scope
        repeating: _result, count: 1)
}
```

Same `H` appears in every specialization, including SHA3_256 (line ~2298, 2315, …), MD5, SHA384, SHA512. `H` is the Swift method-level generic parameter of `HMAC<H>.authenticationCode<H, D>(for:using:)`. It needs to be replaced with the concrete specialization type (`CryptoKit.SHA256`, `CryptoKit.SHA3_256`, …) when emitting the `initializeMemory(as:)` call.

Separately, the SHA3_256 specializations lack the iOS-26 availability floor:

```swift
@available(iOS 13.0, *)        // ← actual floor for SHA3_256 on iOS is 26.0
@available(macOS 10.15, *)     //    actual floor on macOS is 15.0
@available(watchOS 6.0, *)
@available(tvOS 13.0, *)
@available(macCatalyst 13.0, *)
@_cdecl("SBW_CSM_CryptoKit_HMAC_CryptoKit_SHA3_256_Foundation_Data_authenticationCode_B8BC4F3D")
public func SBW_…_B8BC4F3D(…) {
    let _result = CryptoKit.HMAC<CryptoKit.SHA3_256>.authenticationCode(…)
    …
}
```

So the Swift compiler trips `'SHA3_256' is only available in iOS 26.0 or newer`, independent of the `H` bug.

**Shape of the bug.** Two overlapping wrapper-emitter defects:

1. **Method-level generic substitution.** When the concrete-specialization pass emits a `@_cdecl` wrapper for `HMAC<SHA256>.authenticationCode<SHA256, D>(…)`, it specializes the containing type argument (`H` in `HMAC<H>`) but fails to substitute the method-level type parameter (`H` in `authenticationCode<H, D>`) in the *return-type* reference `HashedAuthenticationCode<H>`. Round 4 blocker #4 flagged exactly this "CryptoKit method-level generics + mutating-self" class. The earlier pass probably skipped these entirely (that's the 37 SB0001 in Round 4); now they're emitted but the substitution is incomplete.
2. **`@available` floors.** The emitter is copying the availability from the containing HMAC type (iOS 13+) but ignoring the tighter floor on the specialization's hash-type argument. SHA3 requires iOS 26 / macOS 15. Wrapper needs to take the max of the type-arg availability and the method availability.

**Where to look.** The specializer that emits `@_cdecl` wrappers for generic method specializations. Both substitution of method-level type parameters in the wrapper body and availability-floor computation from type-arg constraints sit in the same pass.

---

## Issue 5 — Payload enum-case factory methods are no longer emitted (P1, silently breaks payload-carrying enums)

**Symptom.** `Kingfisher.ImageProgressive.UpdatingStrategy.Replace(null)` — which compiled against the prior 0.8.0 drop (and every earlier SDK) — now produces:

```
libraries/Kingfisher/tests/Program.cs(2469,68):
  error CS1501: No overload for method 'Replace' takes 1 arguments
libraries/Kingfisher/tests/Program.cs(2469,23):
  error CS0815: Cannot assign void to an implicitly-typed variable
```

Kingfisher's managed library itself builds clean (39 SB0001, unchanged from Round 4) — only the test program breaks. But the *reason* it breaks is a silent generator regression with generic implications.

**Evidence.** In Swift, `ImageProgressive.UpdatingStrategy` is:

```swift
public enum UpdatingStrategy {
    case replace(UIImage?)   // payload case
    case `default`           // no payload
    case keepCurrent         // no payload
}
```

Generated Kingfisher.cs lines 37403-37480:

```csharp
public partial class UpdatingStrategy : ISwiftObject, ISwiftStruct, IDisposable
{
    …
    // no-payload cases: cached singleton factories ✓
    public static UpdatingStrategy Default => _lazy_default.Value;
    public static UpdatingStrategy KeepCurrent => _lazy_keepCurrent.Value;

    // CaseTag enum ✓
    public enum CaseTag : uint {
        Replace = 0,
        Default = 1,
        KeepCurrent = 2,
    }

    // ← no static Replace(UIImage?) factory emitted
    // ← no way to construct the .replace payload case
}
```

`grep "Replace\b" Kingfisher.cs` finds only the `CaseTag.Replace = 0` line. `binding-report.json` does not list the Swift `replace` case in `SkippedItems`. The emitter silently dropped the payload-case constructor.

**Shape of the bug.** Payload-carrying enum cases historically got emitted as static factory methods (`public static UpdatingStrategy Replace(UIImage? payload) { … }`). This drop appears to emit only no-payload cases (as singleton factories) plus the `CaseTag` enum — payload cases become un-constructable.

Probably the same regression explains the `silentTombstones` additions the SDK now reports:

```
TipKit:                MiniTipViewStyle
BlinkIDUX:             CaptureService, SampleBuffer
Kingfisher:            CacheStoreResult
StripePaymentSheet:    CustomerPaymentOption
```

These are in the new `silentTombstones` key in `binding-emission-report.json` (good — Round 4 asked for this telemetry), but the emitter should either emit real projections or SB0001-annotate them, not quietly drop the case constructors.

**Impact check for other packages.** Any Swift enum with payload cases that managed consumers used to construct will have the same break. The test-suite regression in Kingfisher is the smoking gun; the other four tombstoned types above likely hide equivalent unreachable cases.

**Where to look.** Enum-emitter path that distinguishes payload cases from no-payload cases. The no-payload branch still fires (singleton factories get emitted); the payload branch is the one that went missing.

**Stopgap for the consumer repo.** Edit `libraries/Kingfisher/tests/Program.cs:2469` to skip `UpdatingStrategy_Replace_null` until this is fixed — the library itself is shippable (39 SB0001, same as Round 4, all sim/device tests that don't touch payload-case construction pass).

---

## Issue 6 — `spm-to-xcframework` header injection still fails for Stripe Mixed frameworks (P2, pre-existing)

**Symptom.** `dotnet nuke BuildLibrary --library Stripe --all-products` aborts during the xcframework build step:

```
StripePaymentSheet.xcframework     : plan expected Mixed framework but no public .h files were produced
                                     under Headers/ (ObjC header injection likely failed);
                                     plan expected Mixed framework but no module.modulemap was produced
StripeCore.xcframework             : same
StripeUICore.xcframework           : same
… (all 12 Stripe frameworks plus Stripe3DS2, StripeCameraCore)
System.InvalidOperationException: spm-to-xcframework exited with code 8
```

**Status.** Same regression Round 3 §5 and Round 4 §Tooling flagged for `spm-to-xcframework cafa869b74c8`. Not a release blocker — the documented Round 3 workaround (reuse cached xcframeworks, skip the SPM archive step, build each product csproj directly, `InjectProjectRefs`, then rebuild) produced all 12 Stripe products clean on both passes in this validation run (299/299 assertions on sim + device).

**Where to look.** `spm-to-xcframework` tool itself (pinned at commit `cafa869b74c8` in `build/Build.Xcframework.cs`). This is a tooling bug, not a generator bug — but it keeps biting validators and should be tracked alongside the generator work because the same person usually lands fixes.

---

## Issue 7 — Silent tombstones still present, now correctly reported (P3, informational)

The SDK now emits a `silentTombstones` key in `binding-emission-report.json` (requested in Round 4 §9), listing the types it projects as empty bodies. The five currently tombstoned types across the validation set:

| Package | Tombstone | Impact |
|---|---|---|
| TipKit | `TipKit.MiniTipViewStyle` | Unclear — view-style protocol conformer; likely minor |
| BlinkIDUX | `BlinkIDUX.CaptureService` | **High-risk** — capture lifecycle sits here |
| BlinkIDUX | `BlinkIDUX.SampleBuffer` | Medium — frame delivery type |
| Kingfisher | `Kingfisher.CacheStoreResult` | Cache read-back |
| StripePaymentSheet | `StripePaymentSheet.CustomerPaymentOption` | Payment-option enum consumers must read |

All five compile clean and appear in consumer-surface type graphs, but their bodies are empty — consumers reading those types will find no usable surface. Round 4's `WeatherKit.Forecast<T>` and `MusicKit.MusicRelationshipProperty<,>` tombstones are no longer in the list, so some generic-container projections improved. New generic-shape tombstones:  none — all five additions are concrete (non-generic) types, suggesting this is a different class of skip than Round 4.

For each tombstone, decide between: emit a real projection, emit caller-side SB0001, or document as a permanent limitation. Do not leave them as silent empty types, since they're still reachable from the public surface.

### Session 1 disposition — TipKit.MiniTipViewStyle: **document as permanent limitation**

`TipKit.MiniTipViewStyle` is a `TipViewStyle` conformer — a Self-requiring PAT whose existential (`any TipKit.TipViewStyle`) the generator already lowers to `object` across the TipKit surface (see `TipKitSmokeTests.TestTipUICollectionReusableViewViewStylePropertyIsObject`). The public API of `MiniTipViewStyle` is effectively the zero-argument `init()` plus protocol-extension-driven callers (`.miniTip`) — a real projection would need custom witness-table dispatch on a type whose only interesting surface is the protocol conformance itself.

Because consumers already interact with TipKit view styles through the `object`-typed PAT fallback (the same mechanism that lets `TipUICollectionReusableView.ViewStyle` round-trip), the empty body on `MiniTipViewStyle` does not block any reachable consumer usage beyond the generic-`object` surface that is already pinned by smoke tests. Keep tombstoned for now, documented here as a permanent limitation of the PAT-existential projection. Emitting a concrete public constructor on the tombstone is a candidate follow-up once the generator grows first-class PAT-conformer emission.

### Session 1 disposition — other four tombstones: **clears tracked in Session 1 Issue 5 fix**

Kingfisher `CacheStoreResult`, StripePaymentSheet `CustomerPaymentOption`, BlinkIDUX `CaptureService` / `SampleBuffer` all matched the Issue 5 payload-case-suppression pattern (ObjC-bridged generic arg triggered the widened `ContainsRemappedObjCTypeInGenericArgs`). With the narrowing in this session (`IsStdlibContainerWithoutISwiftObjectConstraint` whitelist in `EnumHandler.CaseConstruction.cs`), validation is expected to re-emit payload-case factories and drop these four from `silentTombstones` — confirm on the next `nuke validate` run. See BindingTests regression fixture `UpdatingStrategy` in `MultiAssociatedValues.swift` + `EnumObjCBridgedPayloadTests.cs`.

---

## Reproduction: full validation sequence

```bash
cd /Users/wojo/Dev/swift-dotnet-packages

# 1. Drop new nupkgs into local-packages/, then:
dotnet nuget locals all --clear
find libraries apple-frameworks -path "*/obj/*/swift-binding" -type d -exec rm -rf {} + 2>/dev/null
find libraries apple-frameworks -name swift-binding.stamp -delete 2>/dev/null

# 2. Third-party
for lib in Nuke Lottie Kingfisher BlinkID BlinkIDUX Mappedin; do
  dotnet build libraries/$lib/SwiftBindings.$lib.csproj -v q 2>&1 | tail -5
done

# 3. Apple frameworks
for fw in ActivityKit CryptoKit FamilyControls LiveCommunicationKit MusicKit \
          ProximityReader RoomPlan StoreKit2 TipKit Translation WeatherKit WorkoutKit; do
  dotnet build apple-frameworks/$fw/SwiftBindings.$fw.csproj -v q 2>&1 | tail -5
done

# 4. Stripe (spm-to-xcframework fails; use fast path since xcframeworks are cached)
for p in StripeCore StripeUICore StripePayments StripePaymentsUI StripeApplePay \
         Stripe StripePaymentSheet StripeConnect StripeIdentity StripeIssuing \
         StripeCardScan StripeFinancialConnections; do
  csproj=$(find libraries/Stripe/$p -maxdepth 2 \
    \( -name 'SwiftBindings.Stripe.*.csproj' -o -name 'SwiftBindings.Stripe.csproj' \) | head -1)
  dotnet build "$csproj" -v q 2>&1 | tail -3
done
dotnet nuke InjectProjectRefs --library Stripe --all-products
# re-run the per-product build loop above for pass 2

# 5. Sim tests
dotnet nuke BootSim
for fw in ActivityKit FamilyControls LiveCommunicationKit ProximityReader \
          StoreKit2 TipKit Translation WeatherKit WorkoutKit; do
  dotnet nuke BuildTestApp --library $fw
  dotnet nuke ValidateSim --library $fw --timeout 30
done
dotnet nuke BuildTestApp --library Stripe
dotnet nuke ValidateSim --library Stripe --timeout 60
for lib in Nuke Lottie BlinkID BlinkIDUX Mappedin; do
  dotnet nuke BuildTestApp --library $lib
  dotnet nuke ValidateSim --library $lib --timeout 30
done

# 6. Device tests
for fw in ActivityKit FamilyControls LiveCommunicationKit ProximityReader \
          StoreKit2 TipKit Translation WeatherKit WorkoutKit; do
  dotnet nuke BuildTestApp --library $fw --device
  dotnet nuke ValidateDevice --library $fw --timeout 45
done
dotnet nuke BuildTestApp --library Stripe --device
dotnet nuke ValidateDevice --library Stripe --timeout 90
for lib in Nuke Lottie BlinkID BlinkIDUX Mappedin; do
  dotnet nuke BuildTestApp --library $lib --device
  dotnet nuke ValidateDevice --library $lib --timeout 45
done
```

---

## Result matrix (for quick comparison after the next drop)

| Cluster | Build | Sim | Device |
|---|---|---|---|
| Nuke (77 assertions) | ✅ | ✅ 77/0 | ✅ 77/0 |
| Lottie (89) | ✅ | ✅ 89/0 | ✅ 89/0 |
| Kingfisher (39 SB0001, unchanged) | ✅ library | ⛔ test program (Issue 5) | — |
| BlinkID (305) | ✅ | ✅ 305/0 | ✅ 305/0 |
| BlinkIDUX (146/1skip) | ✅ | ✅ | ✅ |
| Mappedin (257) | ✅ | ✅ 257/0 | ✅ 257/0 |
| All 12 Stripe products (299) | ✅ (workaround Issue 6) | ✅ 299/0 | ✅ 299/0 |
| ActivityKit (19) | ✅ | ✅ 19/0 | ⛔ Issue 1 |
| FamilyControls (15) | ✅ | ✅ 15/0 | ⛔ Issue 1 |
| LiveCommunicationKit (18) | ✅ | ✅ 18/0 | ⛔ Issue 1 |
| ProximityReader (10) | ✅ | ✅ 10/0 | ⛔ Issue 1 |
| StoreKit2 (35) | ✅ | ✅ 35/0 | ⛔ Issue 1 |
| TipKit (20) | ✅ | ✅ 20/0 | ⛔ Issue 1 |
| Translation (12) | ✅ | ✅ 12/0 | ⛔ Issue 1 |
| WeatherKit (25) | ✅ | ✅ 25/0 | ⛔ Issue 1 |
| WorkoutKit (25) | ✅ | ✅ 25/0 | ⛔ Issue 1 |
| CryptoKit | ⛔ Issue 4 (all 4 TFMs) | — | — |
| MusicKit | ⛔ Issue 3 (iOS+tvOS) | — | — |
| RoomPlan | ⛔ Issue 2 (iOS) | — | — |

Totals from this drop: **1493 assertions pass on sim** (179 Apple + 299 Stripe + 874 third-party + 141 repeat counts already in the per-row figures; actual distinct-run total is 1493). Device totals limited to 1173 pass + 1 skip (only Stripe + third-party cohorts ran).

---

## Rough fix priorities

1. **Issue 1** (device-slice Info.plist). Single-site packaging fix, unblocks 9 packages on device immediately.
2. **Issue 4** (CryptoKit method-level generics). Named Round 4 blocker #4; this drop made it worse (was 37 SB0001, now 0 SB0001 but the package doesn't build on any TFM).
3. **Issue 3** (MusicKit `Data` ambiguity). Single overload-filter fix in the specializer.
4. **Issue 2** (RoomPlan `simd`). Missing type-DB projection plus strip the spurious `<float>` parameter on `simd_float3`.
5. **Issue 5** (payload enum-case factories). Silent surface drop — regression test should catch this class going forward.
6. **Issue 6** (spm-to-xcframework Stripe). Tooling, pre-existing, has a workaround.
7. **Issue 7** (silent tombstones). Per-tombstone review, lowest blast radius.

---

## Root-cause map (which commit introduced which regression)

| Blocker | Introducing commit | Mechanism |
|---|---|---|
| Issue 5 — payload-enum factory dropped | `2e7d3697` "Silent-tombstone reporting" | Widened `ContainsRemappedObjCTypeInGenericArgs` (`EnumHandler.CaseConstruction.cs:1146-1149`) to also return true on `AppleFrameworkRegistry.HasObjCClassPrefix(...)`. `UIImage` matches the `UI*` prefix, so `replace(UIImage?)` now gates out of factory emission. Same mechanism drops the 4 new tombstones (Kingfisher `CacheStoreResult`, StripePaymentSheet `CustomerPaymentOption`, BlinkIDUX `CaptureService` / `SampleBuffer`). TipKit `MiniTipViewStyle` is a different (view-style) case. |
| Issue 2 — RoomPlan `simd` namespace | `4fa51488` "Project simd_float4x4 onto Matrix4x4" | Partial landing. `simd_float4x4` projection was wired, but `SIMD2/3/4<Float>`, `simd_float2/3`, and `simd_float3x3` were left in a half-state where the emitter now writes `simd.simd_float3<float>` (non-existent C# namespace, spurious generic arg on a Swift typealias) instead of skipping. |
| Issue 3 — MusicKit `Data` initializer ambiguity | `8fc00b51` "Close out CSM blockers: mutating-self thunks + generic-parent specialization" (or earlier in the CSM stack) | Concrete-specialization planner enumerates `MusicItemCollection<Album>.init<[UInt8]>` even though no overload on `MusicItemCollection<Album>` accepts a `Data` / byte-buffer. Filter for overload compatibility against the specialized target is missing. |
| Issue 4 — CryptoKit method-level `H` + missing `@available` | `8fc00b51` / `9b53fb16` (CSM + existential-bypass stack) | Two co-located defects in the `@_cdecl` wrapper specializer: (a) substitutes the containing-type generic (`HMAC<H>`) but not the method-level type param (`H` in `authenticationCode<H, D>`), leaving `HashedAuthenticationCode<H>` unreplaced in `initializeMemory(as:)`; (b) availability copied from the containing type only — never takes `max(containing, method, type-arg)`, so `SHA3_256` specializations miss the iOS 26 / macOS 15 floor. |
| Issue 1 — device-slice `Info.plist` | Pre-existing in SDK multi-TFM packaging path (not introduced in this drop) | `src/Swift.Bindings.Sdk/Sdk/Sdk.targets` `_ConfigureSwiftBindingPack` walks each TFM; the wrapper-framework assembly writes an embedded `Info.plist` (`CFBundlePackageType=FMWK`) for the simulator slice but skips it on the device slice. Only surfaces in multi-TFM builds. Single-TFM wrappers are fine because both slices are built by the same path that does write it. |

Note that Issues 2/3/4 all came out of the same ~10-day push to widen generic specialization (`8fc00b51` → `9b53fb16`). That push didn't have a compile gate for Apple system frameworks, so incomplete landings shipped.

---

## Why our gates missed these

Three structural blind spots, confirmed by reading `validation-libraries.json`, `build/Build.*.cs`, `BindingTests/**`, and the emitter:

1. **`nuke validate` has zero Apple system framework coverage.** The manifest is 51 third-party open-source libraries (Stripe, Alamofire, Kingfisher, …). CryptoKit, MusicKit, RoomPlan, WeatherKit, StoreKit2, ActivityKit, TipKit, Translation, WorkoutKit, FamilyControls, LiveCommunicationKit, ProximityReader — none are present. Issues 2, 3, and 4 had no gate between them and the NuGet drop; they were caught only by downstream validation in `swift-dotnet-packages`.

2. **BindingTests is single-TFM (`net10.0-ios`) and the multi-TFM packaging path has no automated gate.** `BindingTests/CompileCheck/CompileCheck.csproj` declares one TFM. The multi-TFM path in `Sdk.targets _ConfigureSwiftBindingPack` / `_ValidateSwiftBindingPackSlices` only fires when a project declares 4 TFMs — nothing in the Nuke pipeline does. `_ValidateSwiftBindingPackSlices` checks slice *directories* exist but not that `Info.plist` is inside. Default `nuke runtime-tests-simulator` doesn't set `--include-device`, so the device-slice Info.plist writer (`Build.BindingTests.cs:184-188`) is never exercised even on BindingTests' single-TFM harness. Issue 1 was invisible to every default gate.

3. **Payload-enum factory coverage is synthetic-only.** `BindingTests/RuntimeTestsApp/Marshalling/LargeEnumTests.cs` exercises `DeviceModel.Unknown(string)` and `DeviceModel.Custom(string, int)` — both payloads are primitives. No BindingTests fixture has a payload whose type is ObjC-bridged (`UIImage?`, `NSData`, `Foundation.URL` via NSURL, …), so widening `ContainsRemappedObjCTypeInGenericArgs` to match ObjC-prefixed types didn't trip any unit or runtime test. The regression was only visible at a consumer-test build (Kingfisher's `Program.cs`), which is not part of our pipeline.

There is also a generator-wide attribution gap: emission-report skip counts are a summary, not a diff. Dropping a factory from one type and a method from another can look identical in aggregate. If we tracked per-type emission fingerprints over a baseline, a silent surface drop like Issue 5 would fail the baseline check even without a dedicated unit test.

---

## Session plan

Sized so each session is one focused sitting with fix + test + gate-update. Related work bundled; unrelated work kept separate so a broken session doesn't block others.

### Session 1 — Emitter regression cluster (Issues 5, 2, 7)

All three are emitter-surface fixes in nearby handlers with small, independent diffs. Bundle them so we land one emitter + type-DB pass, re-baseline once.

- **Issue 5** — Narrow `ContainsRemappedObjCTypeInGenericArgs` in `EnumHandler.CaseConstruction.cs` so it only trips when the outer enum's generic signature has an `ISwiftObject` constraint that the ObjC-bridged arg would violate. The widened `HasObjCClassPrefix` branch can stay if it's constraint-aware; otherwise revert that branch.
- **Issue 2** — Finish the SIMD projection table: `SIMD2/3/4<Float>` → `System.Numerics.Vector2/3/4`, `simd_float3x3` / `simd_float4x4` → `Matrix3x2` / `Matrix4x4` (4x4 already done). Strip the spurious `<float>` on `simd_float3`. If a shape has no C# equivalent, skip + SB0001 rather than emit broken syntax.
- **Issue 7** — After Session 1's fixes, re-run the validation set and confirm Kingfisher `CacheStoreResult`, StripePaymentSheet `CustomerPaymentOption`, BlinkIDUX `CaptureService` / `SampleBuffer` clear. Decide on TipKit `MiniTipViewStyle` (keep tombstoned / emit / document).
- **BindingTests additions**: (a) payload enum with `UIImage?` payload — assert `.Replace(UIImage?)` static factory exists; (b) fixtures for each SIMD projection, assert C# compiles and values round-trip.
- **Gates**: `nuke test` + `nuke binding-tests` + `nuke validate`. Kingfisher baseline clears.

### Session 2 — Specializer correctness (Issues 3, 4) — ✅ landed at `717fc8dd` + `7e0b778a`

> **Outcome:** macOS + MacCatalyst flipped clean for both CryptoKit and MusicKit. iOS + tvOS `swift_compile: fail` status remains, but diagnosis confirmed the wrapper itself compiles — the failure is `CheckSwiftWrapper` rejecting the device slice for lack of an Info.plist. That's Issue 1 (Session 4), not Session 2. Session 2 also shipped a bonus fix: cross-level same-type-constraint filter in the CSM cartesian (caught `S.Element == T` couplings that were emitting uncompilable Swift).


Both live in the same `@_cdecl` wrapper specializer pass; fixing them together avoids re-reading the same code twice.

- **Issue 4** — Substitute method-level generic params in wrapper bodies (not just containing-type params). Compute availability as `max(containing-type availability, method availability, type-arg availability)` so `SHA3_256` specializations carry iOS 26 / macOS 15.
- **Issue 3** — Filter specializations in the planner: only emit a specialization when the target type has an init/method whose signature matches after type-arg substitution. For `MusicItemCollection<Album>`, `init<[UInt8]>` never matches; drop it.
- **BindingTests additions**: generic method with multiple concrete specializations, one of which requires a newer OS; generic container with multiple init overloads where only some are valid per concrete arg. Assert Swift wrapper compiles on all target TFMs.
- **Gates**: `nuke test` + `nuke binding-tests`. If Session 3 has landed first, `nuke validate` now trips CryptoKit/MusicKit and clears them on this session.

### Session 3 — Apple-framework validation tier (new infra) — ✅ landed at `71523beb` (amended)

Establishes a self-contained compile gate for Apple system frameworks. Does not depend on `swift-dotnet-packages`. Once landed, prevents the class of regression that made this entire drop.

- Add `mode: "apple-framework"` to `validation-libraries.json` with 12 entries (same list as the ship set: CryptoKit, MusicKit, RoomPlan, WeatherKit, StoreKit2, ActivityKit, TipKit, Translation, WorkoutKit, FamilyControls, LiveCommunicationKit, ProximityReader), each declaring `tfms` (subset of `net10.0-ios26.2`, `net10.0-tvos26.2`, `net10.0-maccatalyst26.2`, `net10.0-macos26.2`).
- Nuke target resolves each framework's location in the active Xcode SDK per-TFM (`$DEVELOPER_DIR/Platforms/<platform>.platform/Developer/SDKs/<platform><ver>.sdk/System/Library/Frameworks/<Name>.framework`), invokes the generator, compiles the generated C# + Swift wrapper per-TFM (so Issue 3's iOS/tvOS-only failure is distinguishable from macOS-clean).
- Baseline: add per-framework per-TFM entries to `.validation-baseline.json`, initially reflecting known failing frameworks (CryptoKit/MusicKit/RoomPlan fail, others pass). Sessions 1 and 2 then drop failure entries as fixes land.
- **Gates**: this session adds a gate; doesn't need to pass existing gates unscoped.
- Runtime/device remains out of scope — that stays in `swift-dotnet-packages`.

> Post-landing amend (`71523beb`): Codex flagged that the second-slice compile used `target.PlatformVersion` (Apple TFM/SDK version, 26.2) instead of the framework's min-deployment floor. Fixed to use `platform.MinOsVersion` (15.0 ios/tvos/maccatalyst, 12.0 macos), mirroring `%(SwiftAppleFrameworkTarget.MinDeploymentVersion)` in `_AFW_OtherTarget`. Baseline unchanged (`nuke validate`: 127/127 pass, 0 regressions).

### Session 4 — Multi-TFM packaging (Issue 1 + pack gate) — ✅ landed

> **Outcome:** Root cause was `_CompileAppleFrameworkSecondWrapperSlice`, not `_ConfigureSwiftBindingPack`. The first wrapper slice ships with an `Info.plist` written by the generator's `SwiftWrapperCompiler.WriteFrameworkPlist`; the second-slice target built the device slice via raw `swiftc -emit-library` and handed a plist-less `.framework/` directory to `xcodebuild -create-xcframework`, which happily merged it. Fix writes the same plist template (mirroring generator output with the slice's `CFBundleSupportedPlatforms`) before the merge step. `Build.Validation.cs` `GenerateAppleFrameworkDeviceSlice` — which deliberately replicates the SDK path so `CheckSwiftWrapper` catches regressions — got the same plist write. Baseline delta: 17 iOS/tvOS apple-framework entries flipped `swift_compile: fail` → `ok` (ActivityKit, CryptoKit, CryptoKit@tvos, FamilyControls, LiveCommunicationKit, MusicKit, MusicKit@tvos, ProximityReader, RoomPlan, StoreKit2, StoreKit2@tvos, TipKit, TipKit@tvos, Translation, WeatherKit, WeatherKit@tvos, WorkoutKit). The three maccatalyst `swift_compile: fail` holdouts (LiveCommunicationKit, ProximityReader, StoreKit2) are pre-existing and outside Session 4's scope. Pack gate added as a new Nuke target (`nuke pack-gate`) that version-stamps the Runtime/Sdk/Apple nupkgs at `0.0.0-packgate`, consumes them in a 4-TFM TipKit fixture, runs `dotnet pack`, and asserts every slice of every embedded xcframework carries `Info.plist` (6 slices verified per full run).

- **Issue 1** — Fix the multi-TFM branch in `Sdk.targets _ConfigureSwiftBindingPack` (or the wrapper-framework assembly step it drives) to write the embedded `Info.plist` on the device slice. Diff the sim vs device slice-assembly path; copy/write whatever the sim branch does.
- **Pack gate** — Add a small 4-TFM fixture project. New Nuke target runs `dotnet pack`, unzips the produced nupkg, and asserts every embedded xcframework has `Info.plist` in `ios-arm64/`, `ios-arm64-simulator/`, `tvos-arm64/`, `tvos-arm64-simulator/`, `maccatalyst-arm64_x86_64/`, `macos-arm64_x86_64/` as applicable. Gate runs in `nuke validate`.
- **Gates**: new pack gate passes; existing `nuke validate` + `nuke binding-tests` unchanged.

### Session 5 (optional) — Attribution-level coverage

If time allows after the P0 stack, extend the binding-emission report to include per-type emission fingerprints (factory count, method count, property count) and add a baseline check that flags per-type drops as regressions. Would have caught Issue 5 at commit time without needing a hand-authored fixture. Lower priority; nice-to-have once the ship-blockers are cleared.

### Dependencies & recommended order

```
Session 1 (independent)  ──► ships Issue 5, 2, 7 fixes
Session 3 (independent)  ──► establishes Apple-framework gate, baseline locks in known state
Session 2 (runs after 3) ──► specializer fixes trip Apple gate → clear them in the same session
Session 4 (independent)  ──► packaging fix + multi-TFM pack gate
Session 5 (optional)     ──► attribution-level regression coverage
```

Sessions 1, 3, and 4 are independent and can run in any order (or parallel if multiple sessions in a day). Session 2 benefits from Session 3 landing first so the Apple-framework gate is in place to catch regressions in adjacent specializer work. Session 5 is a post-ship improvement, not a blocker.
