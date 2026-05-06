# Gap: `MultiSpecialization` skip drops the entire signed-data property surface of generic enum types

> SDK 0.10.0 generator feature gap. Discovered 2026-05-05 during a
> consumer-experience audit of
> [SwiftBindings.Apple.StoreKit2](https://github.com/justinwojo/swift-dotnet-packages)
> (Apple StoreKit framework, 26.2.2).

## Summary

Properties on a generic Swift enum that need specialization across multiple
realized type parameters are skipped wholesale with skip reason
`MultiSpecialization` (and `AnyTypeFallback`) in the binding report. For
StoreKit2's `VerificationResult<SignedType>`, this drops the entire
JWS-inspection surface — `jwsRepresentation`, `headerData`, `payloadData`,
`signatureData`, `signature`, `signedData`, `signedDate`,
`deviceVerification`, `deviceVerificationNonce`, `payloadValue`,
`unsafePayloadValue`, plus several minor helpers.

This blocks server-side App Store receipt verification end-to-end:
consumer apps cannot read `jwsRepresentation` to POST to their backend,
backend cannot use the App Store Server API to verify, the entire
attested-purchase flow is unavailable.

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.2
- .NET SDK 10.0.107, workload 10.0.107
- Xcode 26.3 / Swift 6.2.x
- macOS 26.x, arm64
- Source framework: system StoreKit (iOS 26.2.2)

## Repro

```bash
jq '.skipped[] | select(.reason | contains("MultiSpecialization"))' \
   apple-frameworks/StoreKit2/obj/Debug/net10.0-ios26.2/swift-binding/binding-report.json
```

Skip records (binding-report.json:35, :53):

```json
{
  "kind": "property",
  "name": "VerificationResult.jwsRepresentation",
  "reason": "MultiSpecialization",
  "details": "Multi-specialization required across SignedType ∈ {Transaction, AppTransaction, RenewalInfo}"
}
```

Same skip record for `headerData`, `payloadData`, `signatureData`,
`signature`, `signedData`, `signedDate`, `deviceVerification`,
`deviceVerificationNonce`. `payloadValue` / `unsafePayloadValue` skip
under `AnyTypeFallback` (the projected `SignedType` is type-erased).

## Native ground truth

```text
swiftinterface (StoreKit framework, line ~475-510):
  extension StoreKit.VerificationResult where SignedType == StoreKit.AppTransaction
                                            | StoreKit.Transaction
                                            | StoreKit.Product.SubscriptionInfo.RenewalInfo {
    public var jwsRepresentation: String { get }
    public var headerData: Data { get }
    public var payloadData: Data { get }
    public var signatureData: Data { get }
    public var signature: Data { get }
    public var signedData: Data { get }
    public var signedDate: Date { get }
    public var deviceVerification: Data { get }
    public var deviceVerificationNonce: UUID { get }
  }
  extension StoreKit.VerificationResult {
    public var payloadValue: SignedType { get }
    public var unsafePayloadValue: SignedType { get }
  }
```

These properties are *defined in extensions constrained on the realized
`SignedType`*. The Swift compiler emits one specialization per realized
type. The SDK's emitter sees the multi-specialized property and bails
with `MultiSpecialization`.

## Hypothesis

The emitter likely treats "property visible on N realized specializations"
as a generic-property-must-be-specialized-N-times problem and gives up.
The right shape is to emit the property *once* on the generic C# class
`VerificationResult<TSignedType>` with a runtime metadata-driven
PInvoke dispatch (the same way the existing accessors that DO work today
already do).

`payloadValue` / `unsafePayloadValue` skip under `AnyTypeFallback` is a
sub-shape: the projected return is the generic parameter `SignedType`. The
generator's existential lowering would need to pick a specialization at
the call site. The fallback today is to drop the property; a better
fallback would be `T payloadValue` where T is the closed generic.

## Impact

- **Server-side StoreKit verification is unavailable from C#.** This is
  the canonical Apple-recommended pattern: consumer reads
  `jwsRepresentation`, POSTs to backend, backend uses the App Store Server
  API to verify the JWS signature against Apple's keys, backend grants
  entitlement. Without `jwsRepresentation` there is no path.
- `VerificationResult<T>.payloadValue` is the type-erased "give me the
  payload regardless of verification status" entry point — also
  unreachable.
- Affects every StoreKit consumer. There is no workaround other than
  hand-writing a Swift shim that exposes `jwsRepresentation` via a
  `@_cdecl` function.

## Workaround

Consumer-side: no first-class workaround. The closest alternative is to
hand-write a Swift wrapper:

```swift
@_cdecl("MyApp_jwsRepresentation_for_Transaction")
public func myAppJwsForTransaction(_ result: UnsafeRawPointer) -> SwiftString.Buffer {
    let r = result.load(as: VerificationResult<Transaction>.self)
    return SwiftString.Buffer(r.jwsRepresentation)
}
```

…and PInvoke it from C#. Not portable; defeats the binding purpose.

## Severity

**Feature gap — High.** Production apps that verify receipts server-side
cannot use this binding. Distinct emitter from
`bug-0.10.0-safehandle-wraps-stack-pointer-in-generic-enum-extractor.md`
(Round 4 / M-1) — that one breaks the *extractor* lifecycle; this one
drops the *property surface* entirely. Both should be addressed in the
same SDK pass.

Cross-reference in
[SDK-0.10.0-BLOCKERS.md](../../swift-dotnet-packages/SDK-0.10.0-BLOCKERS.md)
under Round 4 / M-2.

## Round 5 — WeatherKit query-type tombstones (2026-05-05)

The cross-package audit of `SwiftBindings.Apple.WeatherKit`
(2026-05-05) confirms this gap recurs across **four iOS 18 statistics
query types**. All four are emitted with `[OpaqueSwiftType(2)]`
decoration and "no projectable public members" — the working static
factory APIs that Swift exposes are all dropped.

| Site | Type | Severity |
|------|------|----------|
| WeatherKit.cs:7020 | `DailyWeatherStatisticsQuery<T>` `[OpaqueSwiftType(2)]` | High |
| WeatherKit.cs:9796 | `MonthlyWeatherStatisticsQuery<T>` same | High |
| WeatherKit.cs:10458 | `DailyWeatherSummaryQuery<T>` same | High |
| WeatherKit.cs:18470 | `HourlyWeatherStatisticsQuery<T>` same | High |

Swift exposes the working factories at
swiftinterface:356 (daily `temperature`/`precipitation`),
swiftinterface:515 (monthly), swiftinterface:588 (daily summary),
swiftinterface:1204 (hourly). All four type bodies in C# tombstone-
out — even if the missing
`WeatherService.dailyStatistics`/`hourlyStatistics`/etc. methods
(see Family C / **closure-parameter-skip**) were exposed, consumers
still couldn't *construct* the query values needed to call them.

**Companion to O-10
[`bug-0.10.0-foundation-dimension-constraint-not-projected.md`](bug-0.10.0-foundation-dimension-constraint-not-projected.md)**
— the WeatherKit case adds a related defect: `Trend<Dimension>`,
`TrendBaseline<Dimension>`, and `Percentiles<Dimension>` are also
silently emitted as unsupported tombstones because the generator
can't project the `Foundation.Dimension` typedef constraint. (See
that doc for details.)

The MusicKit half of this audit also surfaces matching cases:
`MusicLibraryRequest<T>` accessors at MusicKit.cs:3168, :3255,
:3281-3290 — 13 skips for `limit`, `offset`,
`includeOnlyDownloadedContent`, all filters/sort, and `response()`
on the same MultiSpecialization shape (covered there as Family C in
[`gap-0.10.0-closure-parameter-skip-renders-apis-unreachable.md`](gap-0.10.0-closure-parameter-skip-renders-apis-unreachable.md)).

Cross-reference in
[SDK-0.10.0-BLOCKERS.md](../../swift-dotnet-packages/SDK-0.10.0-BLOCKERS.md)
under Round 5 / M-2.

## Resolution

Partial fix in 0.10.0: the StoreKit2 canonical case
(`VerificationResult<SignedType>` with `where SignedType ==
{Transaction, AppTransaction, RenewalInfo}` extension properties) is
now lowered correctly. The fix has two parts.

### Part 1 — wire EnumHandler to the constrained-extension emitter

`VerificationResult<SignedType>` is a `@frozen public enum`. The
existing `ConstrainedExtensionEmitter` was already wired into
`ClassHandler`, `FrozenStructHandler`, and `NonFrozenStructHandler`,
but **not** into `EnumHandler` — so generic enums whose properties
are defined in `where T == Concrete` extensions were
validator-suppressed at the open-generic level (correctly: the
mangled symbol is bound to the closed instantiation) AND never
re-surfaced as closed-generic extension methods. Result: the entire
property surface vanished.

`src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumHandler.cs`
now invokes `ConstrainedExtensionEmitter.EmitConstrainedExtensions`
after closing the enum class, mirroring the wiring in the three
other handlers.

### Part 2 — non-frozen-struct return shape in ConstrainedExtensionEmitter

`VerificationResult<Transaction>.signature` returns a non-frozen
`ECDSASignature` (resilient struct, indirect-result ABI). The
emitter previously handled only String and primitive returns. A new
`CEReturnShape` discriminator (`String` / `Primitive` /
`NonFrozenStruct`) drives `TryEmitPropertyExtension`:

- **NonFrozenStruct**: the C# extension method allocates a
  VWT-sized buffer via `SwiftObjectHelper<T>.GetTypeMetadata().Size`,
  PInvokes with `(SwiftIndirectResult indirectResult, IntPtr _self)`,
  then `SwiftMarshal.MarshalFromSwift<T>(buffer)`. The Swift `@_cdecl`
  wrapper takes `_ resultPtr: UnsafeMutableRawPointer` and writes the
  value with `resultPtr.initializeMemory(as: T.self, repeating:
  result, count: 1)`.
- **String** / **Primitive**: unchanged (UTF-8 slice helper for
  String, plain return for primitives).

`src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConstrainedExtensionEmitter.cs`.

### Layer A coverage

Two new emission tests in
`src/Swift.Bindings/tests/UnitTests/EmitterTests/ConstrainedExtensionEmitterTests.cs`:

- `EmitConstrainedExtensions_GenericEnum_StringProperty_EmitsExtensionMethodAndUtf8Slice`
  — generic enum + `where T == Concrete` extension property of type
  `String`, asserts UTF-8 slice helper + `(IntPtr resultPtr, IntPtr
  _self)` PInvoke.
- `EmitConstrainedExtensions_GenericEnum_NonFrozenStructProperty_UsesIndirectResult`
  — same shape, non-frozen struct return, asserts VWT buffer
  allocation, `SwiftIndirectResult` PInvoke arg, and
  `resultPtr.initializeMemory(as:repeating:count:)` Swift wrapper.

The eight pre-existing `ConstrainedExtensionEmitter` tests still
pass (10/10 total).

### Verified end-to-end

`nuke validate --filter StoreKit2` now emits three new extension
classes
`VerificationResult{AppTransaction|Transaction|RenewalInfo}Extensions`
on the closed instantiations, exposing `GetJwsRepresentation`
(string) and `GetSignature` (resilient struct returning
`ECDSASignature`) per concrete type. The Swift wrappers are emitted
into the companion swift file with the indirect-result shape
described above. `nuke validate --filter WeatherKit` confirms no
`Forecast<T>.Summary` regression.

### Remaining scope (deferred)

The fix covers String and non-frozen-struct return shapes — enough
to resolve the StoreKit2 `jwsRepresentation` + `signature` blocker
described in the original Impact section. The following sub-shapes
are tracked as follow-ups, not blockers for 0.10.0:

- **Foundation value types as return** (`Data`, `Date`, `UUID`):
  these are registered as `frozen="true"
  requiresMemoryManagement="false"`, so
  `ExtensionMarshallingHelper.ClassifyReturnType` returns null
  ("Frozen value-type struct not supported yet"). Affects
  `headerData`, `payloadData`, `signatureData`, `signedData`,
  `signedDate`, `deviceVerification`, `deviceVerificationNonce`.
  These remain skipped.
- **`payloadValue` / `unsafePayloadValue`**: skip under
  `AnyTypeFallback` because the projected return is the open
  generic parameter `SignedType`. The fix would be to substitute
  the concrete specialization at extension-method emit time. Out of
  scope for this pass; tracked separately.
- **WeatherKit query types** (`[OpaqueSwiftType(2)]` tombstones)
  and **MusicKit `MusicLibraryRequest<T>` accessors**: same
  `MultiSpecialization` shape, but the constrained extensions
  contain methods (not just properties) and additional return
  shapes (closure parameters, structured result types). Worth
  revisiting as a unified pass once Bundle 11/11b lands.

The single-switch design means future shapes (frozen value types,
`SignedType`-as-return, methods) extend
`ConstrainedExtensionEmitter` in the same place rather than
spreading across the four handler call-sites.
