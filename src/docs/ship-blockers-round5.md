# Round 5 Blockers — 2026-04-22 (post Sessions 1–4)

**What was tested:** `SwiftBindings.Sdk 0.8.0` + `Runtime 0.8.0` + `Templates 0.8.0` + `Apple 26.2.0` (nupkgs dropped into `/Users/wojo/Dev/swift-dotnet-packages/local-packages/` at 2026-04-22 22:41). This is the post-Sessions-1–4 snapshot of the 2026-04-22 work plan (`ship-blockers-2026-04-22.md`).

**Validation flow (consumer-side, swift-dotnet-packages repo):**
1. Cleared NuGet cache, wiped all `obj/**/swift-binding/` + `bin/` across `apple-frameworks/` and `libraries/`.
2. Rebuilt 12 Apple framework packages (multi-TFM), all with 0 build errors.
3. Booted sim, ran `BuildTestApp` + `ValidateSim` for all 12 — **277 assertions PASS / 0 fail**.
4. Ran `BuildTestApp --device --aot` + `ValidateDevice --aot` on iPhone 13 — **277 assertions PASS / 0 fail**.
5. Ran a focused two-agent API-completeness audit (`Explore` subagents, Sonnet) over the four suspected-problem Apple packages. Results inline below.

**Headline.** Build and runtime are green across the board. But the audit found that **four Apple packages have primary-flow APIs missing from the generated C#**. Sim/device "PASS" reflects that the test assertions PASSed — but those assertions are largely metadata-only and skip the broken primary flow. Specifically:

- **StoreKit2** Session 2 Issue E.1 (`VerificationResult<T>.TryGetVerified`) did not land.
- **WeatherKit** Session 3 Issue E.2 cleared the build-level tombstone but did NOT project `Collection`/`Sequence` onto `Forecast<T>` — consumers still cannot iterate.
- **MusicKit** Session 1 Issue C (`DoesPairingSatisfyAssociatedTypeConstraints` relaxation) did not land — 4 SB0001 unchanged from Round 4.
- **CryptoKit** Session 1 Issue A landed (144 build errors → 0), but the 37 SB0001 on method-level-generics + mutating-self remain — correctly, per the Sessions 1–4 scope commitment (this was explicitly Round 5 work).

Plus: **ActivityKit** `Activity<TAttributes>` lifecycle (start/update/end) emits no methods — this is the "permanent limitation or needs new tooling" design investigation the 2026-04-22 plan flagged.

Consumer-side outputs per finding live under `/Users/wojo/Dev/swift-dotnet-packages/apple-frameworks/<Name>/obj/Debug/net10.0-ios26.2/swift-binding/`. Full validation logs at `/tmp/apple-validation-2026-04-22-pm/`. Ship-readiness audit at `/Users/wojo/Dev/swift-dotnet-packages/SHIP-READINESS.md`.

---

## Ship status snapshot

| Bucket | Count | Packages |
|---|---|---|
| **Clean SHIP** | 5 Apple + 11 Stripe + 6 third-party = **22** | Apple: LiveCommunicationKit, ProximityReader, RoomPlan, FamilyControls, Translation. Stripe: all 11 public products. Third-party: Nuke, Lottie, Kingfisher, BlinkID, BlinkIDUX, Mappedin. |
| **SHIP with README caveat** | 3 Apple | ActivityKit (permanent — user ActivityAttributes), TipKit (permanent — `@_alwaysEmitIntoClient` DSL), WorkoutKit (HealthKit writes deferred). |
| **HOLD — Round 5 SDK fixes required** | 4 Apple | StoreKit2, WeatherKit, MusicKit, CryptoKit. |

## Sessions 1–4 retrospective

### What landed

| Issue (from ship-blockers-2026-04-22.md) | Status | Evidence |
|---|---|---|
| Issue 1 (device Info.plist) | ✅ landed | All 12 Apple frameworks install on iPhone 13; ValidateDevice passed for all 12 |
| Issue 2 (RoomPlan simd) | ✅ landed | `SIMD3<Float>` → `System.Numerics.Vector3`; RoomPlan 29/0 PASS sim + device |
| Issue 3 (MusicKit Data) | ✅ landed | MusicKit 36/0 PASS; iOS + tvOS compile clean |
| Issue 5 (Kingfisher payload enum-case factory) | ✅ landed | Kingfisher 249/0 PASS sim + device |
| **Issue A** — CryptoKit emitter generic-arity + SCREAMING_CASE | ✅ landed | CryptoKit 144 build errors → 0 across 4 TFMs; CryptoKit 33/0 PASS sim + device |
| **Issue B** — `_CompileAppleFrameworkSecondWrapperSlice` thunk-link | ✅ landed | 8 previously-failing Apple frameworks device-PASS under NativeAOT (ActivityKit, FamilyControls, MusicKit, StoreKit2, TipKit, Translation, WorkoutKit, + others) |
| **Issue F** — `async throws -> String` closure marshalling | ✅ landed | Stripe all 12 products sim + device PASS; StripeConnect + StripePaymentSheet IntentConfig flow usable |
| **Issue D.1** — Kingfisher `Result<(), _>` | ✅ landed | Kingfisher compiles clean, CacheStoreResult surface no longer opaque |
| **Issue D.2** — CoreMedia `CMSampleBuffer` in TypeDatabase | ✅ (needs verification on BlinkIDUX surface) | BlinkIDUX 147/0 PASS sim + device |
| **Issue D.3** — BlinkIDUX CaptureService actor shell-stub | ✅ partial (shell-stub, executor semantics deferred per Session 3 scope) | BlinkIDUX tests pass; full actor isolation semantics are post-release work |
| Tombstone cleanup at build level (Issue 7) | ✅ build-level | `Forecast<T>` and `MusicRelationshipProperty<,>` compile; but see WeatherKit below |

### What did NOT land (or landed incompletely)

Each entry below is a Round 5 P0/P1.

#### 🔴 P0 — Issue C (Session 1): MusicKit parent-generic-param relaxation

**Expected.** Relax `DoesPairingSatisfyAssociatedTypeConstraints` to accept pairings where `ConformanceTarget` names a parent-type generic param. Clears 4 MusicKit SB0001 (`index(_:)`, `formIndex(_:)`, `index(_:offsetBy:)`, `distance(from:to:)` on `MusicItemCollection<TMusicItemType>`).

**Actual.** `MusicKit.cs` still reports 4 SB0001 with skip reason `generic_parent`. Lines 11240, 11272, 11306, 11340 call `ProtocolWitnessTable.GetOrThrow<TMusicItemType, IMusicItem>()` inline — exactly the shape Issue C was supposed to unblock. The method-level-generics path the emitter takes here clearly didn't pick up the relaxation.

**Where to investigate.** `src/Swift.Bindings/src/Engines/ConcreteSpecializationEngine.cs:1237` (or wherever `DoesPairingSatisfyAssociatedTypeConstraints` actually lives — `ConcreteProtocolSpecializationEmitter.cs` per the Session 1 doc). The unit test planned for Session 1 — "assert `DoesPairingSatisfyAssociatedTypeConstraints` returns `true` when `ConformanceTarget.ModuleQualifiedName` matches a parent-type generic parameter name" — may have been skipped or the test passed but a different call site in the pipeline still rejects the pairing.

**Consumer verification.**
```bash
grep -c 'Obsolete.*SB0001' /Users/wojo/Dev/swift-dotnet-packages/apple-frameworks/MusicKit/obj/Debug/net10.0-ios26.2/swift-binding/MusicKit.cs
# Current: 4. Expected after fix: 0.
```

#### 🔴 P0 — Issue E.1 (Session 2): StoreKit2 `VerificationResult<T>.TryGetVerified` missing

**Expected.** Emit `TryGetVerified(out T payload)` / `TryGetUnverified(out T payload)` accessors for the generic enum `VerificationResult<T>` (payload-carrying case whose associated value is the generic parameter). Mirror the emitter change into `EmitPayloadMarshalWithDeclaration` / `EmitPayloadMarshalWithOffset`.

**Actual.** `StoreKit2.cs:3459-3625` defines `VerificationResult<TSignedType>` with a `CaseTag` enum (Unverified=0, Verified=1). No `TryGetVerified` / `TryGetUnverified` / payload extractor anywhere in the file. Grep returns empty.

**Where to investigate.** `src/Swift.Bindings/src/Emitters/EnumHandler.Marshalling.cs` — `EmitPayloadMarshal` generic-type-parameter branch. Per Session 2's planned tests, this was `EnumCaseAssociatedValueTests.cs` — check whether that test was written and what it asserts. Also check BindingTests fixture `enum Holder<T> { case wrapped(T); case empty }` — if the fixture wasn't added or it doesn't exercise `TryGetWrapped(out T)`, the regression guard didn't exist and the fix was never behavior-validated.

**Consumer verification.**
```bash
grep -n "TryGetVerified\|TryGetUnverified" /Users/wojo/Dev/swift-dotnet-packages/apple-frameworks/StoreKit2/obj/Debug/net10.0-ios26.2/swift-binding/StoreKit2.cs
# Current: empty. Expected after fix: at least 2 lines per `VerificationResult<T>` instantiation.
```

#### 🔴 P0 — Issue E.2 (Session 3): WeatherKit `Forecast<T>` has no iterator surface

**Expected.** `Forecast<TElement>` should project Swift's `Collection`/`Sequence` conformance into an `IEnumerable<TElement>` / indexer / `.Hours` / `.Days` / `.Minutes` surface, so `foreach (var h in weather.HourlyForecast)` compiles. Clearing the 3 unresolved PWT constraints on `Forecast<TElement>` via adding `Equatable`/`Decodable`/`Encodable` to `SwiftDatabase.xml` was Session 3's stated approach.

**Actual.** `WeatherKit.cs:13793` defines:
```csharp
public partial struct Forecast<TElement>
{
    public Swift.String Summary { get; }
}
```
**That's it.** No `GetEnumerator`, no `IEnumerable<TElement>`, no indexer, no iteration surface. Session 3's PWT-constraint fix made the type compile (the Round 4 tombstone is gone), but the Collection/Sequence conformance projection never happened. The tombstone morphed into a near-empty type.

**Where to investigate.** Two pieces:
1. **SwiftDatabase.xml additions.** Did Equatable/Decodable/Encodable actually get registered? Grep: `grep -n "sSQMp\|sSEMp\|sSeMp" src/Swift.Runtime/src/Swift/SwiftDatabase.xml`.
2. **Collection/Sequence projection emitter path.** Even with the protocols registered, there's a separate step that decides whether to emit `IEnumerable<T>` + `GetEnumerator` for a Swift type conforming to `Collection`. Check `ConcreteSpecializationEngine` or wherever `Sequence`/`Collection` is recognized — possibly it isn't.

**Consumer verification.**
```bash
grep -n "IEnumerable\|GetEnumerator\|this\[" /Users/wojo/Dev/swift-dotnet-packages/apple-frameworks/WeatherKit/obj/Debug/net10.0-ios26.2/swift-binding/WeatherKit.cs | head -20
# Current: no hits on Forecast. Expected after fix: IEnumerable<TElement> on Forecast + GetEnumerator.
```

Also note: the BindingTests fixture Session 3 added (`struct Container<T: Equatable>` + `<T: Decodable & Encodable>`) only validates that the type round-trips as-is. It does NOT validate the `Collection` projection — so the regression guard didn't catch this either.

#### 🟡 P1 — CryptoKit: method-level generics + mutating-self (originally Round 5 scope)

**Expected (per 2026-04-22 plan).** Not in Session 1–4 scope. Listed as "same class as Round 3 §Root Causes #5 (`Product.products(for:)`)".

**Actual.** 37 SB0001 across SHA256/HMAC/HKDF/AES.GCM/ChaChaPoly/P256/P384/P521/Curve25519, all skip reason `method_level_generics`. Clusters:

- **Signing verification:** `IsValidSignature<D>(Signature, D)`, `Signature<D>(D)` — 8 methods on public-key types (CryptoKit.cs:256, :7561, :9555 per P256/P384/P521/Curve25519).
- **ChaChaPoly AEAD:** `Seal<TPlaintext>(TPlaintext, SymmetricKey)`, `Seal<TPlaintext, TAuthenticatedData>(..., TAuthenticatedData)`, `Open<TAuthenticatedData>(SealedBox, SymmetricKey, TAuthenticatedData)`, `Unwrap<TWrappedKey>(TWrappedKey, SymmetricKey)` — 7 methods (CryptoKit.cs:14880, :14946, :15004).
- **AES.GCM AEAD:** `Seal<TPlaintext>`, `Open<TAuthenticatedData>` — 6 methods (CryptoKit.cs:15630, :15695, :15753).
- **SHA/HMAC/HKDF:** 16 hash/auth/derive methods with method-level type params.

**Consumer impact.** HMAC (`HMAC.AuthenticationCode(Data, SymmetricKey)`, non-generic at CryptoKit.cs:2179) works. AEAD and digital-signature verification are entirely blocked — no concrete `Seal(Data, SymmetricKey)` overload for AES.GCM, so the canonical `SHA256 → HMAC → AES.GCM` chain fails at the encrypt step.

**Where to investigate.** Existing Round 3 notes on `Product.products(for:)`. Emitter path for method-level generic parameters where the generic constraint is `DataProtocol` or `ContiguousBytes`. The fix should emit concrete overloads for common adopters (`Data`, `byte[]`, `ReadOnlySpan<byte>`) or project the generic into a C# `where T : ...` constraint that works with Swift's witness-table resolution.

**Consumer verification.**
```bash
grep -nE 'public .*Seal\s*\(' /Users/wojo/Dev/swift-dotnet-packages/apple-frameworks/CryptoKit/obj/Debug/net10.0-ios26.2/swift-binding/CryptoKit.cs | grep -v Obsolete | head
# Current: empty (all `Seal` overloads are SB0001). Expected after fix: at least one non-obsolete AES.GCM.Seal / ChaChaPoly.Seal.
```

#### 🟡 P1 — ActivityKit `Activity<TAttributes>` lifecycle routing (design, possibly permanent)

**Expected (per 2026-04-22 plan).** "One design investigation: **ActivityKit `ActivityConfiguration` user-type routing** — may end up 'permanent limitation' after investigation."

**Actual.** `ActivityKit.cs:41` defines `Activity<TAttributes>` with type metadata only — no `StartAsync`, `UpdateAsync`, `EndAsync`. The consumer README at `/Users/wojo/Dev/swift-dotnet-packages/apple-frameworks/ActivityKit/README.md` documents this as a permanent limitation requiring a C# source generator that emits a Swift companion target.

**Shipping posture.** Two paths, per the README:

- **Accept as permanent.** Ship with caveat. Done. No generator work needed.
- **Build the Swift-companion emitter.** A C# source generator that takes a user-declared `ActivityAttributes` conformer and emits a Swift target declaring the struct + `Codable`/`Hashable` conformance witnesses + `@_cdecl` entry points for `request/update/end`. Significant new subsystem — no precedent in the current generator (all existing emitters produce C#, not Swift).

**Decision needed.** Confirm with the user (team lead) whether Path 2 goes on the roadmap. If yes, it's a multi-session project, not a Round 5 drop item.

### Sessions 1–4 test-coverage retrospective

The 2026-04-22 plan required BindingTests fixtures as durable regression coverage per-issue. The Round 5 audit can't easily verify every fixture was added, but the observed emitter gaps suggest the following may need double-checking in `BindingTests/Sources/SwiftBindingsTestLib/`:

- **E.1 fixture:** `enum Holder<T> { case wrapped(T); case empty }` + runtime test calling `TryGetWrapped(out T)` on value-type and class-type `T`, both sim AND NativeAOT. If this fixture doesn't exist or doesn't call the accessor, that's why E.1 silently regressed.
- **E.2 fixture:** Generic type conforming to `Collection` with a `foreach` round-trip assertion. `struct Container<T: Equatable>` isn't sufficient — the fixture needs to verify iteration, not just type emission.
- **C unit test:** `DoesPairingSatisfyAssociatedTypeConstraints` returning `true` for parent-generic-param shape — if the unit test exists and passes, the bug is elsewhere in the pipeline (different call site). If the test doesn't exist, Session 1's Issue C was never test-gated.

## Out-of-scope for Round 5

Noted from 2026-04-22; not investigated in this validation. Flag for roadmap, not publish-blocking:

- **Stripe PassKit placeholder** (StripeApplePay + StripeIssuing). Separate from Issue F. Needs PassKit existential projection (`any PKPaymentAuthorizationControllerDelegate`, etc.). Runtime tests pass because the exercised surface doesn't hit PassKit existentials — but an API-completeness audit is owed before those two Stripe products publish as SHIP.
- **BlinkIDUX CaptureService actor executor semantics.** Session 3 landed a shell-stub (API shape exposed, `async Task<T>` stubs without full actor-isolation routing). Proper fix — routing through `unownedExecutor` — is post-release work per the explicit Session 3 scope commit.
- **Cross-TFM runtime validation.** macos-arm64 / tvos-arm64 / maccatalyst runtime not exercised in Round 5 — only iOS sim + iOS NativeAOT device. Compile is clean on all 4 TFMs (part of the rebuild step); runtime parity is unverified. Same MSBuild target as iOS so likely uniform, but the Session 4 multi-TFM check commitment should run on macOS/tvOS before a 1.0.0 publish.
- **`spm-to-xcframework cafa869b74c8` Stripe mixed-framework header validation.** Round 3 §5 tooling regression. Not retested in Round 5 because cached Stripe xcframeworks worked. Flag for tool owner; not a release blocker.

## Session 5 plan

Root-cause analysis (2026-04-23) found the Round 4 retrospective mis-identified the fix site for every P0. The actual fix sites are deeper in the pipeline than Sessions 1–4 patched. Four sessions, one per blocker.

### Session 1 — Issue E.1 (StoreKit2 `TryGetVerified`)

**Actual root cause.** `EnumHandler.Marshalling.cs` has the generic-payload extraction engine (landed `a56ba42b`, extended `b6308240`). But `EnumHandler.CaseInspection.cs:135–143` bails out with `AnyType` *before* reaching the engine. `GetCSharpTypeNameForEnumCase` can't resolve `τ_0_0` from Apple ABI JSON, returns `AnyType`, and `EmitTryGetMethod` skips. The `Holder<T>` BindingTests fixture passes because source-compiled ABI surfaces the sugared generic name, not `τ_0_0` — a fixture-shape gap that hid the regression.

**Fix.** Before the `AnyType` bail-out, detect generic type-parameter payloads via the same `IsGenericTypeParameter` / `TryGetGenericTypeParameterName` helpers Marshalling.cs already uses. Mirror the resolution path.

**Tests.** Unit test that `EmitTryGetMethod` for `Holder<T>.wrapped(T)` emits `TryGetWrapped(out T)`. BindingTests fixture extended to exercise Apple-framework-shape typespec (bare `τ_0_0`), not just source-compiled shape. Run sim + NativeAOT.

**Gate.** `grep -n "TryGetVerified\|TryGetUnverified" StoreKit2.cs` returns ≥2 hits per `VerificationResult<T>` instantiation.

**Outcome (2026-04-23).** Single-payload `TryGetVerified` ships — `EmitTryGetMethod` now resolves the Apple-shape sugared `SignedType` payload to `TSignedType` via an extended `TryGetGenericTypeParameterName` lookup against `GenericArgumentDecl.SugaredTypeName`. Codex P2 follow-up: `EnumHandler.CaseConstruction.cs` was leaking a stale `signedType.Payload.DangerousGetHandle()` argument into the `Verified` case factory because the case-factory argList and `GetPInvokeType` still gated on the restrictive `TypeSpecHelpers.IsGenericTypeParameter` shortlist. Both call sites now route sugared generic-param names through `TryGetGenericTypeParameterName`, and the case factory emits the proper `TypeMetadata.GetTypeMetadataOrThrow<T>()` + `stackalloc` + `SwiftMarshal.MarshalToSwift` path. The widened AnyType bail in `EnumHandler.CaseConstruction` was narrowed via a new `HasUnsupportedAnyTypeInPayload` helper that distinguishes plain-tuple AnyType (Lottie pattern — keep emitting via `Swift.AnyType.Payload`) from generic-bound nested-type AnyType (StoreKit2's `VerificationError<...>` shape — bail). Tuple-payload `TryGetUnverified((SignedType, VerificationError))` is **still bailed out** by the separate nested-type-on-generic-outer bug (out of E.1 scope; tracked in `roadmap.md` §"Lower Priority / Post-1.0" → "Nested-type-on-generic-outer arg mis-placement"). Gates: 9945 unit tests pass; `nuke validate` 127/127 with StoreKit2 fail(1)→ok(0) on all 4 platforms, no regressions; BindingTests sim 1553/0 + device/NativeAOT 1565/0 (+4 new `AppleHolder<TSignedType>` tests on each).

### Session 2 — Issue C (MusicKit parent-generic-param) [landed 2026-04-23 · `268dc708`]

**Actual root cause.** Session 1's fix landed at `ConcreteProtocolSpecializationEmitter.cs:1345–1355` (`DoesPairingSatisfyAssociatedTypeConstraints` + `IsParentGenericParamName`). But those four methods (`index(_:)`, `formIndex(_:)`, `index(_:offsetBy:)`, `distance(from:to:)` on `MusicItemCollection<TMusicItemType>`) are rejected *two gates earlier* at `GenericDispatchEmitter.cs:262–270` in `CanEmitStaticDispatch`: their signatures are pure `nint`-arithmetic and never reference `TMusicItemType`, so `signatureReferencesT` is `false` and the method returns `false` before the associated-type check runs. Unit tests passed — they tested the wrong chokepoint.

**Fix.** In `CanEmitStaticDispatch`, drop the `signatureReferencesT` hard-gate when the parent type has a `Collection`-family conformance. Route through protocol-cast dispatch using the parent's conformance (same path instance methods on generic classes already take).

**Tests.** Unit test targeting `CanEmitStaticDispatch` directly with a `nint`-only-signature method on a `MusicItemCollection<T>`-shape parent. Add the missing `AssociatedTypeConstraints_ParentGenericParam_Accepts` unit test that Session 1 intended. BindingTests fixture: generic struct conforming to `Collection` with an `index(_:) -> Int`-shape method.

**Gate.** MusicKit 4 SB0001 (skip reason `generic_parent`) → 0.

**Outcome (2026-04-23, commit `268dc708`).** Fix landed at `GenericDispatchEmitter.CanEmitStaticDispatch`: `signatureReferencesT` hard-gate relaxed when the struct parent conforms to Sequence / Collection / BidirectionalCollection / RandomAccessCollection; routes through existing `@_cdecl` static-dispatch wrappers (off Mono-Issue-1 CallConvSwift+2-metadata path). Parallel relaxation in `PropertyWrapperEmitter.CanEmitGenericClassPropertyWrapper` + direct-return parity fix in `EmitGenericStaticGetterWrapper` (Bool → `? 1 : 0`, SimpleEnum-with-rawValue, tag-only enum zero-init + `copyMemory`, Class/Optional<Class> → `Unmanaged.passRetained`). `CollectionProjectionEmitter.HasCollectionConformance(StructDecl)` promoted to `internal` so both emitters share the detector. BindingTests fixture `MusicItemBag<Item: CollectibleItem>: Collection` in `Generics/Constraints.swift` + 4 new runtime tests (StartIndex/EndIndex, Index(_:offsetBy:), Distance(from:to:), Index(after:)). Gates: `nuke test` 10,565/0/2, `nuke validate` MusicKit swift:fail → swift:ok × 4 TFMs (SB0001 4 → **1**; remaining `FormIndex(nint)` is a distinct inout-parameter issue, out of Issue C scope), runtime-tests-simulator 1557/0/53/0 (+4 vs baseline). Zero regressions.

### Session 3 — Issue E.2 (WeatherKit `Forecast<T>` Collection projection) [landed 2026-04-23 · `ad6e65d9`]

**Actual root cause.** `CollectionProjectionEmitter.TryFindBacking:107–130` only projects when a public `var x: [Element]` property exists on the type, to delegate `GetEnumerator`/indexer calls to. `Forecast<T>` has no such property — the Apple storage is opaque. The Session 3 PWT fix cleared the tombstone but the projection never fires. The BindingTests fixture (`IndexedSeries<Element>` with `public let items: [Element]`) hides the gap because it has the visible backing array the emitter requires.

**Fix.** Add a fallback path in `TryFindBacking` / emission: when Collection conformance is present but no `Array<T>` backing property is emittable, generate `IEnumerable<T>` + indexer that dispatches through Swift's `startIndex` / `endIndex` / `subscript(Int)` witnesses directly. Builds on the witness-dispatch infra Session 2 unblocks.

**Tests.** BindingTests fixture: generic struct with `Collection` conformance, private backing storage, only `startIndex`/`endIndex`/`subscript(Int)` public. `foreach` round-trip assertion — element-by-element, not just type emission.

**Gate.** `grep -nE "IEnumerable|GetEnumerator" WeatherKit.cs` hits `Forecast<TElement>`; consumer test iterates hourly forecast without casting.

### Session 4 — CryptoKit method-level generics [partial, 2026-04-23 · `2569ac21`]

**Actual root cause.** Two separate gates:
- `Seal<TPlaintext>` AEAD methods reach the `method_level_generics` gate at `WrapperValidation.cs:1086` via `HasMethodOwnGenericParameters`. The existing `ConcreteProtocolSpecializationEmitter` + `specialization-hints.json` (from `8fc00b51`) handles instance methods on generic types; AEAD `Seal` is a **static** method, and `CanEmitStaticDispatch:260` explicitly returns `false` for `MethodType.Static`.
- `isValidSignature<D>` reaches `HasUnsupportedProtocolConstraints` earlier in the pipeline and is tombstoned before `method_level_generics` runs.

**Fix.** (a) Extend `ConcreteProtocolSpecializationEmitter` to cover static methods with method-level `DataProtocol`/`ContiguousBytes` generics — emit concrete overloads (`Seal(byte[], SymmetricKey)`, `Seal(Data, SymmetricKey)`). (b) Targeted relaxation in `HasUnsupportedProtocolConstraints` for method-level generic constraints on `Self`-less protocols so `isValidSignature<D: DataProtocol>` reaches the specialization path.

**Tests.** BindingTests fixture: static method with `<T: DataProtocol>` constraint + signature-verification shape exercised on Mono + NativeAOT.

**Gate.** `grep -nE 'public .*Seal\s*\(' CryptoKit.cs | grep -v Obsolete` returns non-obsolete overloads for AES.GCM + ChaChaPoly. `IsValidSignature(Signature, Data)` emits on P256/P384/P521/Curve25519.

**Outcome (2026-04-23, commit `2569ac21`, partial).** Two pieces shipped, one deferred.

*Shipped:*
- `EnumHandler.EmitNamespaceEnum` now invokes `ConcreteProtocolSpecializationEmitter.EmitConcreteSpecializations`. Caseless Swift enums (AES, ChaChaPoly, HPKE, …) are projected as C# static partial classes but never ran CSM — they do now. This is the hook that unblocks AEAD `Seal<TPlaintext>` / `Open<TAuthenticatedData>` **once sync-throws CSM lands**.
- Latent ABI-width fix in CSM: `EmitCSharpMethod`'s `pinvokeReturn` was hardcoded to `IntPtr` for every non-bool direct return, producing CS0266 for any method returning `byte`/`short`/… and silently mis-sizing smaller-than-pointer returns even when the cast coincidentally worked. Now routes through `MethodClosureBridge.GetPInvokePrimitiveType`.
- BindingTests fixture `BytesNamespace` (caseless enum, two static `<D: DataProtocol>` methods — `countBytes → Int`, `firstByteOrZero → UInt8`). 7 runtime tests covering `byte[]` + `Foundation.Data` conformers and empty-input sentinels. Sim + NativeAOT device both green.

*Deferred (follow-up session required):* **sync-throws CSM.** The actual CryptoKit `Seal`/`Open` methods all throw. CSM currently rejects `!isAsync && Throws` at `ConcreteProtocolSpecializationEmitter.cs:107` and `:1801`. Lifting this requires (a) Swift-side `UnsafeMutablePointer<UnsafeMutableRawPointer?>` errorOut param + `do`/`catch` wrap + sentinel returns across 5 return-shape branches; (b) C#-side `out IntPtr errorPtr` P/Invoke param + `SwiftMarshal.ThrowSwiftError` after call + `ErrorDescriptionEmitter` helper wiring; (c) mirror at `:1801` in `EmitConcreteSpecializationsForGenericParent`. Natural next session.

*Design-doc revision:* the `isValidSignature<D: DataProtocol>` gate (Fix 2) was reported as **already passing pre-commit** — `IsValidSignature(Signature, Data)` overloads were already emitting on P256/P384/P521/Curve25519. Round 4 audit may have misclassified the symptom.

*Gates (zero regression):* `nuke compile` ok · `nuke test` 598/1/0 · `nuke validate` clean (no compile-status movement) · `nuke binding-tests --strict` ok · `runtime-tests-simulator` 1562 → **1569** (+7) · `runtime-tests-device` 1565 → **1580** (+15). `.validation-baseline.json` updated in commit.

*Gate status at end of Session 4:* `grep 'public .*Seal\s*\(' CryptoKit.cs | grep -v Obsolete` still empty (tombstoned until sync-throws CSM lands). `IsValidSignature(Signature, Data)` ✓.

### Sequencing

S1 and S2 are independent — can run parallel. S3 builds on S2's witness-dispatch wiring. S4 is independent; likely the longest session.

## ActivityKit decision (2026-04-23)

**Shelve.** User-facing value doesn't justify the investment. Live Activities are dominated by Swift-native / RN consumer apps; MAUI's audience (enterprise LOB, B2B, field services) has near-zero Live Activity demand. Even with a baked-in `DefaultActivityAttributes` companion in the package, consumers still need a SwiftUI WidgetKit Extension to render the activity — at which point the ~20-line Swift attributes struct isn't the blocker. Not shipping `SwiftBindings.ActivityKit` for 1.0; revisit post-ship if consumer demand surfaces.

## Re-validation after fixes (for consumer-side verification)

After a Session 5 drop lands in `/Users/wojo/Dev/swift-dotnet-packages/local-packages/`, run the workflow in `/Users/wojo/Dev/swift-dotnet-packages/SHIP-READINESS.md` §"How to re-evaluate after the next SDK drop". The §8 grep commands (one per HOLD item) give a fast pass/fail read on each specific fix before the heavier sim/device revalidation.
