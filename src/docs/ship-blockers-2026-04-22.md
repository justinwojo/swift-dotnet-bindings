# Ship-blockers — 2026-04-22 drop

**What was tested:** `SwiftBindings.Sdk 0.8.0`, `SwiftBindings.Runtime 0.8.0`, `SwiftBindings.Templates 0.8.0`, `SwiftBindings.Apple 26.2.0` — all four nupkgs re-built and dropped into `/Users/wojo/Dev/swift-dotnet-packages/local-packages/` at `2026-04-22 00:23`. Version numbers unchanged from the 2026-04-21 drop, contents different (this round picks up Sessions 1–4 from the 2026-04-21 doc).

**Validation flow:**
1. `dotnet nuget locals all --clear`
2. Wiped every `obj/**/swift-binding/` and `swift-binding.stamp` under `libraries/` and `apple-frameworks/`.
3. Rebuilt 6 third-party libraries, 12 Apple-framework packages, 12 Stripe products (two-pass with Nuke `InjectProjectRefs`).
4. Booted a simulator from the Nuke fleet and ran `BuildTestApp` + `ValidateSim` for every package that built.
5. Ran `BuildTestApp --device` + `ValidateDevice` on a connected iPhone 13 (default Mono AOT — no `--aot`).
6. Per-package audit subagents analyzed the generated C#, wrapper Swift, emission reports, and test programs for every shipping package.

**Headline.** The 2026-04-21 blockers are mostly resolved — **Issue 1 (device Info.plist)**, **Issue 2 (RoomPlan simd)**, **Issue 3 (MusicKit Data)**, and **Issue 5 (Kingfisher payload factory)** are fixed. **Issue 4 (CryptoKit)** landed on the Swift wrapper side but the C# emitter ships two new defects that keep CryptoKit build-failed on all 4 TFMs. Round 4 functional gaps (StoreKit2 `VerificationResult` unwrap, WeatherKit `Forecast<T>`) are unchanged. **A multi-TFM device-slice packaging gap now surfaces** on 8 of 11 Apple frameworks: `_CompileAppleFrameworkSecondWrapperSlice` in `Sdk.targets` builds the device (ios-arm64) wrapper without linking the NativeThunk `.arm64.o` files, so thunk symbols are absent from the device binary — methods dispatched through those thunks hit missing symbols at runtime (sim links them correctly via `SwiftWrapperCompiler.InvokeSwiftCompiler`, so sim passes). Confirmed via `nm`; see Issue B Research notes for the full diagnosis. This gap is pre-existing — it wasn't visible before because Issue 1 blocked device install. Stripe (all 12 products) and every third-party library (6/6) pass both sim and device end-to-end.

**Ship status snapshot (29 shippable NuGet packages = 12 Apple + 11 Stripe + 6 third-party; StripeUICore is internal xcframework only, no NuGet):**
- **14 SHIP today** — 3 Apple (LiveCommunicationKit, ProximityReader, RoomPlan) + 7 Stripe (umbrella + StripeCore + StripePayments + StripePaymentsUI + StripeIdentity + StripeCardScan + StripeFinancialConnections) + 4 third-party (Nuke, Lottie, BlinkID, Mappedin — with documented SB0001 surface)
- **13 NEAR-SHIP** broken down by actual blocker:
  - **7 Apple, Issue B device block only** — ActivityKit, FamilyControls, MusicKit, StoreKit2, TipKit, Translation, WorkoutKit
  - **2 Stripe, Issue F (`async throws -> String`)** — StripeConnect (HARD: sole public ctor blocked), StripePaymentSheet (IntentConfiguration flow blocked; presentation flow works)
  - **2 Stripe, PassKit placeholder (NOT Issue F)** — StripeApplePay, StripeIssuing — separate gap from async-throws; needs PassKit existential projection
  - **2 third-party, surface gap** — Kingfisher (cache write-back inspection), BlinkIDUX (capture lifecycle + frame data)
- **2 HOLD** — CryptoKit (Issue A build-fail), WeatherKit (Issues E.2 + B)

Consumer-side artefacts per finding live under `/Users/wojo/Dev/swift-dotnet-packages/apple-frameworks/<Name>/obj/Debug/net10.0-ios26.2/swift-binding/` (multi-TFM) or `…/libraries/<Name>/obj/Debug/net10.0-ios/swift-binding/` (single-TFM). Full build + validate logs are in `/tmp/ship-readiness-2026-04-22/`.

---

## Session plan (active — supersedes any earlier draft)

**Goal:** ship everything in this drop — rock-solid across all 29 shippable NuGet packages. Estimated **4 sessions typical, 5 if Session 1 linker fix hits a quirk or Session 3's actor work expands.**

This is the post-research plan. Research was conducted by six parallel Explore agents; findings are appended inline to each issue section below as "Research notes (2026-04-22)".

**Biggest reveal from research: Issue B is NOT a Mono AOT runtime bug.** Root cause is a missing thunk-link step in `_CompileAppleFrameworkSecondWrapperSlice` (`Sdk.targets:521`) — the thunk `.arm64.o` files are built but never passed to the device-slice `swiftc` invocation. The canonical reference for correct behavior is `SwiftWrapperCompiler.cs:1313` (`InvokeSwiftCompiler`). That collapses Issue B from a dedicated session to a ~2-hour MSBuild fix. Every other blocker is confirmed bounded.

### Session 1 — Issue B + Issue A + Issue C (highest leverage)

**Scope:** exactly these three issues. No Kingfisher, no BlinkIDUX, no actor work. Keep the scope tight so the first device-green drop lands cleanly.

- **Issue B.** Confirm `nm` on `ActivityKit.arm64.o` shows defined thunk symbols (proving the `.o` is correct and the link is the gap). Then update `_CompileAppleFrameworkSecondWrapperSlice` in `src/Swift.Bindings.Sdk/Sdk/Sdk.targets` to match the full thunk-link behavior from `SwiftWrapperCompiler.cs:1313–1329` — both (a) append `.arm64.o` files to `swiftc` inputs, AND (b) add `-Xlinker -framework -Xlinker {originalModuleName}`. Parity on both pieces, not just the glob.
- **Issue A.** Two emitter defects in `ConcreteProtocolSpecializationEmitter.cs` (lines 908–910 return-type generic substitution; line 1608 receiver-type canonicalization via `NameProvider.ToPascalCaseForTypeName`). Unblocks CryptoKit on all 4 TFMs.
- **Issue C.** Relax `DoesPairingSatisfyAssociatedTypeConstraints` to accept pairings where `ConformanceTarget` names a parent-type generic param. Clears 4 MusicKit SB0001.

**Tests to add (permanent regression coverage, not just existing-test re-run):**
- Unit — `src/Swift.Bindings/tests/UnitTests/EmitterTests/ConcreteSpecializationEngineTests.cs`:
  - Assert `ResolvePublicCSharpType` on a bound-generic TypeSpec recurses into generic args (Issue A defect 1 — expect `HashedAuthenticationCode<SHA256>` in output for HMAC-shaped pairing).
  - Assert `BuildConcreteParentCsharpName` returns `HMAC<Sha3256>` (not `HMAC<SHA3_256>`) for SHA3_256 pairing (Issue A defect 2).
  - Assert `DoesPairingSatisfyAssociatedTypeConstraints` returns `true` when `ConformanceTarget.ModuleQualifiedName` matches a parent-type generic parameter name (Issue C).
- BindingTests fixtures (`BindingTests/Sources/SwiftBindingsTestLib/` + runtime tests in `BindingTests/RuntimeTestsApp/`):
  - HMAC-shape generic protocol + SCREAMING_CASE conformer fixture; round-trip an `AuthenticationCode`-style generic return + a `Sha3*`-identifier receiver.
  - Generic type conforming to Collection; exercise `index(_:)`/`formIndex(_:)`/`distance(from:to:)` round-trip.
- Issue B regression guard: integration check that runs `nm` on a multi-TFM Apple-framework device binary after packaging and asserts thunk symbols are present. Wire into `build/scripts/` (new) or as an assertion in `nuke validate` for the multi-TFM targets. Without this guard the link step could silently regress on any future Sdk.targets edit.
- Pre-commit grep (CLAUDE.md mandate): all `ResolvePublicCSharpType(...)` call sites passing un-substituted TypeSpec; all sites using `p.Conformer.CSharpType` directly as a C# identifier.

**Gate:**
- `nuke test` (unit) + `nuke validate` (compile gate)
- `nuke binding-tests` (Mono JIT sim) + `nuke runtime-tests-simulator` for the new fixtures
- Device revalidation on all 8 NativeThunk-affected Apple frameworks (Mono AOT via `--device`)
- **NativeAOT smoke gate — one framework** (e.g. ActivityKit) via `nuke runtime-tests-device --aot`. Issue B touches slice/link behavior that can look fixed under Mono AOT and still fail under NativeAOT. Catch it here, not in Session 4.
- CryptoKit build on 4 TFMs + MusicKit sim
- `.validation-baseline.json` updated in the same commit as the fix (zero-regression policy)

**Unblocks:** 9 packages (8 Apple device-shipping + CryptoKit all TFMs).

### Session 2 — E.1 + Issue F + Kingfisher tombstone

- **Issue E.1.** Add generic-type-parameter branch to `EmitPayloadMarshal` in `EnumHandler.Marshalling.cs`. Mirror into `EmitPayloadMarshalWithDeclaration` / `EmitPayloadMarshalWithOffset`. Unblocks StoreKit2 IAP primary flow.
- **Issue F — `async throws -> String`.** Widen `IsBaselineAsyncThrowingClosure` in `ClosureHandler.cs:877–892` to accept `Swift.String` returns + add String-return success-callback arm to `ClosureEmitter.Async.cs` (analogous to existing `isDataReturn`). Unblocks **2 Stripe packages** (StripeConnect + StripePaymentSheet IntentConfig). Does NOT touch StripeApplePay or StripeIssuing — those are PassKit-blocked (separate issue, out of scope).
- **Issue D.1 — Kingfisher `CacheStoreResult`.** Add `Swift.Result<(), _>` bypass in `HasNonSwiftObjectGenericArg`.

**Tests to add:**
- Unit:
  - `src/Swift.Bindings/tests/UnitTests/EmitterTests/EnumCaseAssociatedValueTests.cs` (or `EnumExtractionTests.cs`) — assert `EmitPayloadMarshal` emits a `MarshalFromSwift<{csParamName}>` line when the associated-value TypeSpec is a bare generic type parameter (Issue E.1).
  - `ClosureHandler` / `ClosureEmitter.Async` unit test — assert `IsBaselineAsyncThrowingClosure` accepts `Swift.String` return + the emitter produces a String-return success-callback arm analogous to `isDataReturn` (Issue F).
  - `src/Swift.Bindings/tests/UnitTests/ThirdPartyValidationFixTestsV4.cs` — assert `HasNonSwiftObjectGenericArg(Swift.Result<(), MyError>)` returns `false` (Issue D.1).
- BindingTests fixtures (critical — these are the durable coverage):
  - Generic enum `enum Holder<T> { case wrapped(T); case empty }`. Runtime test calls `TryGetWrapped(out T)` for both a value-type T (e.g. `Int32`) and a class-type T. **Must run on both Mono JIT (sim) AND NativeAOT (device)** — generic-enum payload marshalling differs enough between runtimes that single-runtime coverage is insufficient.
  - Swift function taking `@escaping () async throws -> String` closure parameter. Round-trip: closure returns a String, C# side receives `Task<string>`, assert correctness for non-empty + empty + throwing cases. **Must run on both Mono JIT AND NativeAOT** — Swift.String continuation ABI (two-register inline vs heap UTF-8) is non-trivial.
  - Swift function returning `Result<(), MyError>`. Verify the C# projection compiles and the success/failure cases round-trip.
- Pre-commit grep: other `HasNonSwiftObjectGenericArg` bypass candidates beyond `Result<(), _>`; other async-throwing closure shapes that might regress.

**Gate:** `nuke test` + `nuke validate` + StoreKit2 sim + device (Mono AOT + NativeAOT), StripeConnect + StripePaymentSheet sim + device, Kingfisher sim + device. Baseline updated in commit.

**Unblocks:** StoreKit2 + StripeConnect + StripePaymentSheet + Kingfisher (4 packages).

### Session 3 — WeatherKit + BlinkIDUX (scope committed to shell-stub for actors)

- **Issue E.2.** Add protocol entries to `src/Swift.Runtime/src/Swift/SwiftDatabase.xml` — start with `Equatable` ($sSQMp) alone, verify count drops from 3 → 2 unresolved on `Forecast<TElement>`, then add `Decodable` + `Encodable`. Optional pass for `Hashable`/`Comparable`/`Collection`/`Sequence` if time. Also clears `Trend<D>` and `MusicRelationshipProperty<,>`.
- **Issue D.2 — BlinkIDUX `SampleBuffer`.** Add `CoreMedia.CMSampleBuffer` to `CoreMediaDatabase.xml` (opaque `CFTypeRef`-backed class).
- **Issue D.3 — BlinkIDUX `CaptureService` (COMMITTED SCOPE: shell-stub only).** Emit actor methods as `async Task<T>` stubs WITHOUT full actor-executor routing. The proper fix (routing through `unownedExecutor` for correct actor semantics) is larger than a session and is **explicitly deferred to a post-release follow-up.** In Session 3 we only surface the API shape so the type isn't fully opaque; correct isolation semantics are a known limitation to document.

**Tests to add:**
- Unit:
  - `PInvokeHelperEmitter` / `FlattenConformances` test — with `Equatable`/`Decodable`/`Encodable` registered, assert `UnresolvedPwtConstraints.Count == 0` for a `Forecast<TElement>`-shape type (Issue E.2). Run incrementally: add Equatable first, verify count drops 3 → 2; then Decodable → 2 → 1; then Encodable → 1 → 0.
  - `TypeDatabase` resolution test — `CoreMedia.CMSampleBuffer` resolves after `CoreMediaDatabase.xml` addition (Issue D.2).
  - Actor emitter test — assert shell-stub output shape for a Swift `actor` with async methods (Issue D.3). Assert API shape is present (methods emit as `async Task<T>`), not that executor semantics are correct.
- BindingTests fixtures:
  - Swift generic `struct Container<T: Equatable> { let value: T }`; verify `where T : ISwiftObject` constraint emits and the type round-trips. Add a second fixture with `<T: Decodable & Encodable>` to exercise the multi-protocol constraint path.
  - Swift `actor WorkItem { public func run() async throws -> Int; public func stop() async }`; verify shell-stub projection compiles. Document the known limitation (executor semantics deferred) in the fixture's comment so future readers don't think it's a full actor impl.
- Pre-commit grep: other framework types that would clear with the protocol DB additions (confirm positive side effects + no regression on existing working generic emission).

**Gate:** `nuke test` + `nuke validate` + WeatherKit sim + device (hourly/daily/minute forecast enumeration — the primary consumer flow), BlinkIDUX sim + device. Baseline updated.

**Unblocks:** WeatherKit primary flow + BlinkIDUX surface.

### Session 4 — Final regression + release drop

- `/regression-validation`: all 29 shippable NuGets × sim + device × Mono JIT + NativeAOT. Zero-regression policy — no expected failures.
- Fix any new issues surfaced during final regression (this round found Issue B only because device install finally worked — expect at least one surprise).
- Decide final publish version (0.8.0 / 0.9.0 / 1.0.0), rebuild nupkgs, publish.

**Tests to verify (not add — fixtures from Sessions 1–3 are the durable coverage):**
- `.validation-baseline.json` `cs_compile` and `swift_compile` pass counts ≥ prior baseline on ALL branches. Drop = regression, must fix before publish.
- BindingTests sim pass count ≥ baseline.
- BindingTests device pass count ≥ baseline (new fixtures from Sessions 1–3 included).
- Unit test pass count ≥ baseline.
- NativeAOT coverage: all 8 Issue-B-affected Apple frameworks run clean under `--aot`, not just the Session 1 smoke framework.
- Multi-TFM check: Issue B fix holds on macos-arm64 + tvos-arm64, not just iOS (same MSBuild target, should be uniform — confirm).

### Testing strategy

Per CLAUDE.md's **"BindingTests are REQUIRED for generator, emitter, or runtime changes"** rule, every issue in this drop gets durable regression coverage — not just a one-time validation pass. A unit test can pass while generated code crashes on device; runtime-only fixtures catch that.

**Per-issue coverage matrix (consolidated; per-session scope is embedded in each Session block above):**

| Issue | Unit test (regression guard) | BindingTests fixture (durable runtime coverage) | Validation baseline delta |
|---|---|---|---|
| **A** CryptoKit emitter | `ConcreteSpecializationEngineTests.cs` — `ResolvePublicCSharpType` bound-generic recursion + `BuildConcreteParentCsharpName` SCREAMING_CASE | Swift protocol with generic associated type + SHA3-style SCREAMING_CASE conformer; HMAC-shape round-trip | CryptoKit 144 err → 0; `cs_compile` up |
| **B** Sdk.targets thunk link | SDK integration guard — `nm` on multi-TFM device binary asserts thunk symbols present | Multi-TFM test target exercising NativeThunk (zero-arg class ctor + static singleton); added to validation suite | Device pass on 8 Apple frameworks: 0 → 8 |
| **C** MusicKit filter | `ConcreteSpecializationEngineTests.cs:1237` — parent-generic-param case returns true | Swift generic type conforming to Collection; `index`/`formIndex`/`distance` round-trip | MusicKit SB0001: 4 → 0 |
| **D.1** Kingfisher Result<(),_> | `ThirdPartyValidationFixTestsV4.cs` — `HasNonSwiftObjectGenericArg(Swift.Result<(), E>)` false | Swift function returning `Result<(), MyError>` | Kingfisher opaque count drops |
| **D.2** CMSampleBuffer DB | `TypeDatabase` resolution test for `CoreMedia.CMSampleBuffer` | Swift property typed `CMSampleBuffer` (may need CoreMedia import in fixture infra) | BlinkIDUX opaque count drops |
| **D.3** Actor shell-stub | Emitter output-shape test for Swift `actor` with async methods | Swift `actor` with async methods; shell stub compiles + exposes API | Baseline unchanged (surface grows) |
| **E.1** Generic enum payload | `EnumCaseAssociatedValueTests.cs` — generic-type-param marshal branch | `enum Holder<T> { case wrapped(T); case empty }`; `TryGetWrapped(out T)` for value-type + class-type T. **Mono JIT + NativeAOT.** | StoreKit2 surface expands |
| **E.2** Protocol DB entries | `FlattenConformances` — unresolved count 3 → 0 after Equatable/Decodable/Encodable register | Swift `struct Container<T: Equatable>` + `<T: Decodable & Encodable>` | WeatherKit + Trend<D> + MusicRelationshipProperty unblock |
| **F** Stripe async-throws String | `ClosureHandler` — `IsBaselineAsyncThrowingClosure` accepts `Swift.String` return + String success-callback arm emits | `@escaping () async throws -> String` closure parameter round-trip. **Mono JIT + NativeAOT.** | Stripe surface expands |

**Cross-cutting testing requirements (apply to every session's commit):**

1. **Pre-fix codebase grep (CLAUDE.md mandate).** When fixing a bug pattern, grep for ALL instances before finishing — the research notes pinpointed specific lines but the defect shape may repeat:
   - Issue A defect 1: all `ResolvePublicCSharpType(...)` call sites passing un-substituted TypeSpec.
   - Issue A defect 2: all sites using a conformer's `CSharpType` directly as a C# identifier without canonicalization.
   - Issue D.1: other `HasNonSwiftObjectGenericArg` bypass candidates beyond `Result<(), _>`.
   - Issue D.2: other CoreMedia/CoreVideo opaque types with the same DB gap (e.g. `CVPixelBuffer`, `CMTime`).

2. **Validation baseline commits.** Every session-fix commit writes the updated `.validation-baseline.json` alongside the code change. The zero-regression policy says `cs_compile` and `swift_compile` pass counts go UP or stay equal, never down.

3. **NativeAOT expansion.** Current device tests run Mono AOT only. Session 1 adds a NativeAOT smoke on one framework; Session 4 must verify NativeAOT on all 8 Issue-B-affected Apple frameworks before publish. Mono and NativeAOT have different bugs — single-runtime coverage is a false positive source.

4. **Multi-TFM verification for Issue B.** Apple-framework MSBuild target is shared across ios/tvos/macos/maccatalyst. Fix should apply uniformly — but confirm on macos-arm64 + tvos-arm64 in Session 4 at minimum.

5. **Cross-runtime coverage for E.1 + F.** Generic-enum payload marshalling (E.1) and async-throwing String continuation ABI (F) both differ enough between Mono JIT and NativeAOT that fixtures must run on BOTH, not just sim. Flagged explicitly in the session entries above.

6. **Test quality — behavior over implementation.** Assertions like "generated code compiles" or "`HashedAuthenticationCode<SHA256>` appears in output" beat exact-string match of emitter internals. Survives emitter refactors; strict-string tests don't.

7. **Permanent BindingTests coverage per bug.** Every bug fix in this drop gets a Swift source fixture in `BindingTests/Sources/SwiftBindingsTestLib/` + a C# runtime test in `BindingTests/RuntimeTestsApp/`. The fixtures added this drop are enumerated per-session above; the union covers all 9 issues (A / B / C / D.1 / D.2 / D.3 / E.1 / E.2 / F). This is the single biggest lever against future regressions — more valuable than any unit test, because BindingTests catches ABI mismatches unit tests cannot.

### Risks that grow session count to 5

- **Session 1 linker quirk.** If option 1 (MSBuild parity) hits a linker quirk we can't resolve in-session, fall back to option 2 (emitter migration of the specific shapes in `ShouldEmitThunk`). Typically stays within Session 1 but could slip.
- **Session 3 actor work expands.** If the shell-stub projection turns out to be untenable (e.g., breaks compile for consumers), actor-isolated method emission moves into its own session. The commitment above keeps this from silently consuming WeatherKit's slot.
- **Session 4 regression surfaces new blockers.** Zero-regression policy says we fix them before publish. Plan for it.

### Known gaps explicitly out of scope for this drop

- **Stripe PassKit placeholder (StripeApplePay + StripeIssuing).** Separate gap from Issue F. Needs PassKit existential projection. Not on the 4-session plan; post-release follow-up.
- **Actor execution semantics (BlinkIDUX `CaptureService`).** Shell-stub in Session 3 only; proper executor routing deferred.

### Open decisions

- **Final SDK version.** Hold at 0.8.0 during iterative work (per standing guidance). Decide publish version (0.8.0 / 0.9.0 / 1.0.0) before Session 4.

---

## Issue status vs 2026-04-21 drop

| 2026-04-21 Issue | 2026-04-22 status | Notes |
|---|---|---|
| **1** device-slice Info.plist | ✅ **RESOLVED** | All 12 multi-TFM Apple wrapper xcframeworks carry Info.plist on both `ios-arm64/` and `ios-arm64-simulator/` slices. Device installs succeed. |
| **2** RoomPlan `simd` namespace | ✅ **RESOLVED** | `SIMD3<Float>` → `System.Numerics.Vector3`, `simd_float4x4` → `Matrix4x4`. Round-trips on sim + device (TEST SUCCESS). |
| **3** MusicKit `Data(_:)` ambiguity | ✅ **RESOLVED** at build level | iOS + tvOS now compile. See new Issue C (MusicKit regressed to 4 SB0001 on collection-index methods). |
| **4** CryptoKit `H` leak + SHA3 `@available` | ⚠️ **PARTIAL** | Swift wrapper fixed and `@available` floors correct. C# emitter side ships two new bugs — see Issue A. |
| **5** Payload enum-case factory | ✅ **RESOLVED** for ObjC-bridged generic args | Kingfisher `UpdatingStrategy.Replace(UIImage?)` factory emits (verified `Kingfisher.cs:37719`). Kingfisher test program compiles clean. |
| **6** `spm-to-xcframework` Stripe headers | ⚠️ **Pre-existing, unchanged** | Documented workaround used — all 12 Stripe products build clean with 0 errors on both passes. Not a release blocker. |
| **7** Silent tombstones | ⚠️ **PARTIAL** — 1 of 5 cleared | Remaining: `TipKit.MiniTipViewStyle` (documented permanent), `BlinkIDUX.CaptureService`, `BlinkIDUX.SampleBuffer`, `Kingfisher.CacheStoreResult`. `StripePaymentSheet.CustomerPaymentOption` cleared. |

**New blockers surfaced this round:**
- **Issue A** (CryptoKit C# emitter) — P0, blocks CryptoKit on all TFMs
- **Issue B** (NativeThunk crashes on device) — P0, blocks 8 Apple frameworks on device
- **Issue C** (MusicKit collection-index SB0001 regression) — P1, minor surface loss
- **Issue D** (tombstones incompletely cleared) — P1
- **Issue E** (Round 4 blockers #2, #3 unaddressed) — P1/P0 depending on package

---

## Issue A — CryptoKit C# emitter: generic arity dropped + SHA3 type name not canonicalized (P0, blocks 4 TFMs)

**Symptom.** `dotnet build apple-frameworks/CryptoKit/SwiftBindings.CryptoKit.csproj` → **144 errors** (36 × 4 TFMs: `net10.0-ios26.2`, `tvos26.2`, `macos26.2`, `maccatalyst26.2`). Two distinct C# defects:

- **96 × CS0305**: `Using the generic type 'HashedAuthenticationCode<H>' requires 1 type arguments`.
- **48 × CS0246**: `The type or namespace name 'SHA3_256' (or SHA3_384 / SHA3_512) could not be found`.

**Evidence.** `apple-frameworks/CryptoKit/obj/Debug/net10.0-ios26.2/swift-binding/CryptoKit.cs` — CS0305 at lines 2179, 2195, 2670, 2686, 3161, 3177, 3662, 3688, 4221, 4247, 4780, 4806; CS0246 at lines 4173, 4193, 4732, 4752, 5291, 5311. Defects repeat across all 4 TFMs.

**Root cause (two co-located defects in the CSM extension-class emitter):**

1. **`HashedAuthenticationCode` emitted without its generic type argument.** Every `AuthenticationCode(...)` return site in `HMACSHA256CsmExtensions`, `HMACSHA384CsmExtensions`, `HMACSHA512CsmExtensions`, `HMACSHA3_256CsmExtensions`, `HMACSHA3_384CsmExtensions`, and `HMACSHA3_512CsmExtensions` writes:
   ```csharp
   public unsafe static CryptoKit.HashedAuthenticationCode AuthenticationCode(...)
       // ↑ should be HashedAuthenticationCode<SHA256> (or <SHA384>, <SHA3_256>, …)
   ```
   Same problem on the two generic method invocations (`GetSwiftTypeSize<HashedAuthenticationCode>`, `MarshalFromSwift<HashedAuthenticationCode>`). The containing-type generic (`H` in `HMAC<H>`) *is* substituted correctly; the emitter fails to thread the concrete type through the return-type reference.
2. **SHA3 hash types referenced by Swift name, not sanitized C# name.** The emitter renamed `SHA3_256` → `Sha3256`, `SHA3_384` → `Sha3384`, `SHA3_512` → `Sha3512` when declaring the C# classes (identifier rules), but the CSM extension pass writes the receiver parameter using the un-sanitized Swift name:
   ```csharp
   // line 4173 — SHA3_256 does not exist as a C# identifier; Sha3256 does
   public unsafe static void Update(this HMAC<SHA3_256> self, ...)
   ```
   `SHA256/384/512` (non-SHA3) work because the Swift name is already a valid C# identifier.

**Shape of the bug.** Session 2 (commits `717fc8dd` + `7e0b778a`) landed the Swift-wrapper-side fix for method-level `H` substitution — `CryptoKit.Wrapper.swift` compiles cleanly now. The equivalent substitution never landed in the C# emitter CSM pass. The `@available(iOS 26.0)` / `@available(macOS 15.0)` guards on the SHA3 extension classes DID land — the `@available` half of Round 4 blocker #4 is genuinely resolved.

**Where to look.** Emitter pass that writes the CSM extension-class method bodies. Two targeted fixes, same file/pass:
- Return-type generic-arity substitution: thread the concrete type argument (from the specialization binding) through every generic-type reference emitted inside the method body.
- Receiver-type name canonicalization: resolve the `this HMAC<X>` receiver's `X` through the same sanitization table used when declaring the C# class.

**Surface reachable once fixed.** HMAC over SHA2 + SHA3, HKDF, AES.GCM, ChaChaPoly, all digest types — previously tombstoned with 37 SB0001, now would be reachable. The 23-assertion test at `apple-frameworks/CryptoKit/tests/Tests.cs` is metadata/enum only — would pass on first try once the package compiles.

**Reproduction.**
```
cd /Users/wojo/Dev/swift-dotnet-packages
dotnet nuget locals all --clear
rm -rf apple-frameworks/CryptoKit/obj
dotnet build apple-frameworks/CryptoKit/SwiftBindings.CryptoKit.csproj 2>&1 | grep -c 'error CS'
# → 144
```

### Research notes (2026-04-22, post-triage)

**Both defects live in the same file: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.cs`.**

**Defect 1 — generic arity dropped.** Lines 908–910, inside `EmitCSharpMethod`'s `csReturnType` resolution. The `else` branch (return type *contains* a generic param as a bound arg) calls `ResolvePublicCSharpType(returnTypeSpec, typeDatabase)` on the un-substituted TypeSpec. `ResolvePublicCSharpType` at line 1552 only handles `NamedTypeSpec.Name`, strips generic args, never recurses into `named.GenericParameters`. Same defect also fires at line 985 (`GetSwiftTypeSize<...>`) and line 1029 (`MarshalFromSwift<...>`).

- **Fix:** route the return spec through `SubstitutePairingGenericsInTypeSpec(returnTypeSpec, pairing)` (already exists — used at line 684 for the Swift side), then extend `ResolvePublicCSharpType` to recurse into generic parameters: `$"{baseName}<{string.Join(", ", named.GenericParameters.Select(gp => ResolvePublicCSharpType(gp, typeDatabase)))}>"`.

**Defect 2 — receiver type not canonicalized.** Line 1608, `BuildConcreteParentCsharpName`. Uses `p.Conformer.CSharpType` directly from `src/Swift.Bindings/src/Data/specialization-hints.json` (`"csharpType": "SHA3_256"`). Class declarations route through `NameProvider.ToPascalCaseForTypeName` → `ScreamingCaseToPascalCase` which converts `SHA3_256` → `Sha3256`; the CSM receiver doesn't. `SHA256`/`384`/`512` happen to already be valid C# identifiers, so only `SHA3_*` variants fail.

- **Fix:** wrap with `NameProvider.ToPascalCaseForTypeName(p.Conformer.CSharpType)` at line 1608.
- Sanitization helpers: `src/Swift.Bindings/src/Marshaler/NameProvider.cs` — `ToPascalCaseForTypeName` (line 226), `ScreamingCaseToPascalCase` (line 207, private).

**Tests to extend.** `src/Swift.Bindings/tests/UnitTests/EmitterTests/ConcreteSpecializationEngineTests.cs` — existing Swift-side test `SubstitutePairingGenerics_NestedInBoundGeneric_ReplacesH` (~line 1205) covers the substitution but not the C# emitter path. Add:
- `EmitCSharpMethod`/`ResolvePublicCSharpType` test asserting `HashedAuthenticationCode<SHA256>` emitted for the HMAC pairing.
- `BuildConcreteParentCsharpName` test asserting `HMAC<Sha3256>` for the SHA3_256 pairing.

---

## Issue B — Device failures on 8 of 11 multi-TFM Apple frameworks: NativeThunk wrapper `.o` files not linked into device slice (P0)

> **Status (2026-04-22 post-triage):** original framing "NativeThunk-strategy wrappers crash on device AOT" was imprecise. Research confirmed the thunks are NOT crashing — they are ABSENT from the device binary because the MSBuild second-slice compile target doesn't link the `.arm64.o` files. See **Research notes** below for the authoritative root cause and fix. The "Commonality" / "Where to look" / "Pre-existing vs new" paragraphs in this section reflect pre-research framing and should be disregarded.

**Symptom.** Every multi-TFM Apple-framework test app installs and launches on the iPhone 13 (Issue 1 resolved), but 8 of 11 then report `TEST FAILED: N failures`:

| Framework | Sim | Device | Device failures |
|---|---|---|---|
| ActivityKit | ✅ PASS | ⛔ FAILED | 4 |
| FamilyControls | ✅ PASS | ⛔ FAILED | 2 |
| **LiveCommunicationKit** | ✅ PASS | ✅ PASS | 0 |
| MusicKit | ✅ PASS | ⛔ FAILED | 3 |
| **ProximityReader** | ✅ PASS | ✅ PASS | 0 |
| **RoomPlan** | ✅ PASS | ✅ PASS | 0 |
| StoreKit2 | ✅ PASS | ⛔ FAILED | 1 |
| TipKit | ✅ PASS | ⛔ FAILED | 2 |
| Translation | ✅ PASS | ⛔ FAILED | 1 |
| WeatherKit | ✅ PASS | ⛔ FAILED | 3 |
| WorkoutKit | ✅ PASS | ⛔ FAILED | 3 |

**Root cause.** Methods bound via the **NativeThunk** wrapper strategy (rather than `@_cdecl` or `ThunkAssisted`) fail when invoked on device. All device builds used the default `--device` Mono-AOT mode (no `--aot` flag).

**Specific failing thunks identified:**

| Framework | Failing thunk(s) | What crashes |
|---|---|---|
| ActivityKit | `thunk_ActivityKit_a4cc381f`, `_d1a2ad08`, `_e5adff86` | `ActivityAuthorizationInfo()` ctor + dependent property reads → 4 cascading failures |
| FamilyControls | `thunk_FamilyControls_0c23f69e` | `AuthorizationCenter.Shared` static accessor → 2 cascading failures |
| MusicKit | `thunk_MusicKit_*` on library / player singletons | `MusicLibrary.Shared`, `ApplicationMusicPlayer.Shared`, `SystemMusicPlayer.Shared` → 3 failures |
| Translation | `thunk_Translation_d4e7ae58` | `LanguageAvailability()` ctor → 1 failure |
| WorkoutKit | `thunk_WorkoutKit_*` | `WorkoutScheduler.Shared` + `IsSupported` + `MaxAllowedScheduledWorkoutCount` → 3 failures |
| StoreKit2 / TipKit / WeatherKit | NativeThunk pattern consistent; specific thunks not yet enumerated | 1 / 2 / 3 failures |

**Commonality.** Every failing framework uses NativeThunk for at least one test-exercised method. LCK / ProximityReader / RoomPlan pass because their test surface happens to avoid NativeThunk-bound APIs (LCK's generated C# has **14 NativeThunk entries** that are simply not reached by the test — confirming the crash is at the thunk-dispatch path, not the generation path).

**Pre-existing vs new.** This is **NOT a regression from 2026-04-21** — it's a pre-existing SDK limitation for the NativeThunk wrapper strategy on device AOT. Round 4 didn't run device tests (Issue 1 blocked device install), so failures are newly *observed*, not newly *introduced*. This is the first drop that validates Apple-framework bindings on physical hardware end-to-end.

**Where to look.** Runtime dispatch for the `NativeThunk` wrapper strategy on device AOT. Candidate hypotheses:
- NativeThunk calling convention mismatches what Mono AOT produces for iOS device.
- Thunks may have unresolved-symbol / codegen issues that the simulator's JIT masks.
- Consider moving `strategy: NativeThunk` entries to `@_cdecl` or `ThunkAssisted` for the specific patterns that crash (static singleton accessors, zero-arg ctors).

**Reproduction.**
```
dotnet nuke BuildTestApp --library ActivityKit --device
dotnet nuke ValidateDevice --library ActivityKit --timeout 45
# → ValidateDevice Failed: TEST FAILED (ActivityKit): 4 failures
```

**Blocking scope.** 8 of 12 Apple frameworks cannot ship to device consumers until this is resolved. LCK + ProxReader + RoomPlan are unaffected. A test-by-test audit could identify which exact methods (on which package) must be swapped away from NativeThunk.

### Research notes (2026-04-22, post-triage) — ROOT CAUSE FOUND

**This is NOT a Mono AOT bug, NOT a calling-convention mismatch, NOT JIT-vs-AOT.** The thunk symbols are missing from the device binary because `_CompileAppleFrameworkSecondWrapperSlice` in `src/Swift.Bindings.Sdk/Sdk/Sdk.targets:521` does not link the thunk `.arm64.o` files into the device slice.

**Evidence:**
- `nm` on simulator binary: all `_thunk_ActivityKit_*` symbols present → methods work
- `nm` on device binary: **zero** thunk symbols → methods crash when dispatched
- `.arm64.o` thunk objects DO exist in `obj/.../swift-binding/` — they are simply never passed to the second-slice `xcrun swiftc` invocation
- Simulator path: `SwiftWrapperCompiler.CompileAll()` correctly links `.arm64.o` via `InvokeSwiftCompiler(..., devThunkResult?.ObjectFiles, ...)`
- Device path: `_CompileAppleFrameworkSecondWrapperSlice` MSBuild target uses a raw `xcrun swiftc` that only picks up `$(_AFW_WrapperSwiftFiles)` — no `.o` enumeration, no `-framework <Module>` linker flag. Thunk symbols never make it into the link line.

**Strategy comparison (corrects the original doc — "ThunkAssisted" was imprecise; real strategies are `None`, `CdeclConstructor`, `CdeclProperty`, `CdeclMethod`, `NativeThunk`):**

| Dimension | `@_cdecl` (`Cdecl*`) | `NativeThunk` | `None` (CallConvSwift) |
|---|---|---|---|
| C# calling conv | `CallConvCdecl` | `CallConvCdecl` (same) | `CallConvSwift` |
| `LibraryImport` target | wrapper dylib | wrapper dylib | original framework |
| Swift source | `.Wrapper.swift` | **none** (pure ARM64 `.arm64.s`) | none |
| Self/metatype | wrapper function sets up | thunk moves to `x20` via `mov x20, x{N}` | `[SwiftSelf]` attr |
| Used for | non-blittable types, Optional, closures, generic ctors | class ctors (metatype in x20), static singletons, non-final dispatch (Tj) | direct Swift ABI |

**Key files:**
- `src/Swift.Bindings.Sdk/Sdk/Sdk.targets:521` — **broken MSBuild target (PRIMARY FIX POINT)**
- `src/Swift.Bindings/src/Emitter/ThunkEmitter/NativeThunkEmitter.cs` — strategy definition; `ShouldEmitThunk()` picker (ALTERNATIVE FIX POINT)
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` — strategy routing at lines 243, 771, 843 (ctors, static-accessor getters, dispatch thunks)
- `src/Swift.Bindings/src/Emitter/ThunkEmitter/ThunkAssemblyEmitter.cs` — emits ARM64 assembly (`TailCall` + `FullFrame` kinds)
- `src/Swift.Bindings/src/Configuration/NativeThunkCompiler.cs` — `.arm64.s` → `.arm64.o` via `xcrun clang -c`
- `src/Swift.Bindings/src/Configuration/SwiftWrapperCompiler.cs:304,419` — first-slice linking (correct)

**Two fix options:**
1. **MSBuild fix (preferred).** Make `_CompileAppleFrameworkSecondWrapperSlice` in `Sdk.targets` match the full thunk-link behavior of `SwiftWrapperCompiler.InvokeSwiftCompiler` (see `src/Swift.Bindings/src/Configuration/SwiftWrapperCompiler.cs:1313-1329` — the canonical reference). That path: (a) appends each thunk `.arm64.o` to the `swiftc` file args, and (b) adds `-Xlinker -framework -Xlinker {originalModuleName}` so the linker can resolve `bl` targets (Tj dispatch thunks, metadata accessors) referenced from thunk assembly. The MSBuild target needs parity on BOTH pieces — not just the `.o` glob. Verify against `SwiftWrapperCompiler.cs:1313` rather than inferring from `.arm64.o` enumeration alone. Addresses all current AND future NativeThunks including the 14 unexercised LCK thunks — shippable forever.
2. **Emitter migration (escape hatch).** Add the affected shapes (zero-arg class ctors, zero-param static-singleton getters returning class pointers) to `ShouldEmitThunk`'s rejection list. Routes them to `@_cdecl` wrappers in `.Wrapper.swift` (already compiled by both paths). Trade-off: adds Swift wrapper compile time; concedes that NativeThunk remains broken on device. Only use if option 1 bumps into a linker quirk.

**Confirmation steps before fixing:**
```bash
nm /Users/wojo/Dev/swift-dotnet-packages/apple-frameworks/ActivityKit/obj/Debug/net10.0-ios26.2/swift-binding/ActivityKit.arm64.o 2>/dev/null | grep thunk
# Should show defined thunk symbols — confirms .o is correct, proving link is the gap

otool -L .../SwiftBindings.ActivityKit.Tests.app/Frameworks/ActivityKitSwiftBindings.framework/ActivityKitSwiftBindings
# Check current framework linker deps — will need to add ActivityKit.framework for the -framework flag
```

**Session-plan impact.** If option 1 holds, Issue B collapses from a dedicated session to a ~2-hour fix + device revalidation on 8 frameworks. Session plan below has been revised accordingly.

---

## Issue C — MusicKit collection-index methods regressed to SB0001 (P1)

**Symptom.** `MusicKit.cs` shows **4 SB0001** where Round 4 had 0. Sim test PASSES, device test FAILS (3 failures — Issue B NativeThunk on `MusicLibrary.Shared` / `ApplicationMusicPlayer.Shared` / `SystemMusicPlayer.Shared`, not related to this SB0001 regression).

**Evidence.** Four sites on `MusicItemCollection<TMusicItemType>`:

```
// Unsupported: method 'init' — generic constraint could not be satisfied
// (Type argument 'MusicItemType' does not satisfy constraint 'MusicKit.MusicItem'
//  on 'MusicItemCollection'.)
[Obsolete("… SB0001")] public nint Index(nint i)

// Unsupported: method 'index' — C# signature collides with another member
[Obsolete("… SB0001")] public nint Index(nint i, nint distance)

// Unsupported: method 'index' — parameter or return type not yet supported
// (unsupported placeholder type)
[Obsolete("… SB0001")] public nint Distance(nint start, nint end)

[Obsolete("… SB0001")] public void FormIndex(nint i)
```

**Shape of the bug.** Collateral damage from the Issue 3 fix (Session 2 commit `717fc8dd` — filtered `MusicItemCollection.init<[UInt8]>` specializations that didn't match a real init). The filter now drops some legitimate collection-index / Collection-protocol conformance methods. A C# signature-collision guard also trips on one of them.

**Consumer impact.** Non-critical. `Collection.index(_:)` / `formIndex(_:)` / `distance(from:to:)` are standard-library Collection-protocol methods. Primary-flow API (search, library reads, subscription status) is unaffected. MusicKit still classified SHIP in Round 4 terms, downgraded to **NEAR-SHIP** here for the SB0001 regression.

**Fix scope.** Tighten the Issue 3 specialization filter to accept specializations that match ANY valid signature on the specialized type — including inherited protocol-conformance methods — rather than only public type-declared ones.

### Research notes (2026-04-22, post-triage)

**Location:** `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.cs` — `DoesPairingSatisfyAssociatedTypeConstraints` implementation at lines 1309–1346; call site in `TryAcceptMethodPairing` at lines 1282–1294.

**What the filter currently does.** Iterates every `(Param, Conformer)` in the pairing. For each, walks `param.GenericParam.AssosiatedTypeConformances`. Any entry with `Kind == ConcreteType` and a 2+-element `Path` (e.g. `S.Element`) must be satisfied by `conformer.AssociatedTypes["Element"] == expected`. Fails closed on any missing data. Correctly rejects `[UInt8]` conformers whose `Element = UInt8` doesn't match the parent's `MusicItem` constraint (the Issue 3 fix that this was added for).

**The bug.** `MusicItemCollection<TMusicItemType>.Element` is the open generic `TMusicItemType`, not a concrete type. When the filter compares `conformer.Element == "MusicKit.Album"` against the constraint target `"MusicItemType"` (the parent's own generic-param name, carried in `ConformanceTarget.ModuleQualifiedName`), every MusicKit conformer fails the equality check. No valid P/Invoke entry gets generated for `index(_:)`, `formIndex(_:)`, `distance(from:to:)` → method bodies reference non-existent symbols → 4 SB0001.

**Fix.** In `DoesPairingSatisfyAssociatedTypeConstraints`, before running the equality check, test whether `assoc.ConformanceTarget.ModuleQualifiedName` matches any name in `parentTypeDecl.GenericParameters`. If it does, the constraint is inherently satisfied for any conformer whose `Element` matches the specialized parent-type argument — return `true` for that entry rather than fail-closing. Admits Collection/Sequence conformance methods on generic types while still blocking concrete-type mismatches.

**Tests.** `src/Swift.Bindings/tests/UnitTests/EmitterTests/ConcreteSpecializationEngineTests.cs:1237` has 6 direct test cases covering `[UInt8]` rejection. Add a new test asserting the filter returns `true` when `ConformanceTarget` names a parent generic param.

**Shares no code with Issue D** — this is in the Swift-wrapper specialization pairing; D is in C# member-emission validation.

---

## Issue D — Silent tombstones incompletely cleared (P1, same shape as 2026-04-21 Issue 5)

**Status.** 2026-04-21 Session 1 narrowed `ContainsRemappedObjCTypeInGenericArgs` in `EnumHandler.CaseConstruction.cs` so payload-case factories emit even when the payload is ObjC-bridged. Verified: `UpdatingStrategy.Replace(UIKit.UIImage?)` now emits at `libraries/Kingfisher/obj/Debug/net10.0-ios/swift-binding/Kingfisher.cs:37719`. Kingfisher test program compiles + runs clean.

**Tombstones that SHOULD have cleared but did NOT (per Session 1 disposition in 2026-04-21 doc):**

| Package | Tombstone | Still tombstoned? | Consumer impact |
|---|---|---|---|
| Kingfisher | `Kingfisher.CacheStoreResult` | ⛔ yes | `Store(completion:)` callbacks receive an opaque handle — `.diskCacheResult` / `.memoryCacheResult` unreadable. Cache read path (`RetrieveImage`) unaffected. |
| BlinkIDUX | `BlinkIDUX.CaptureService` | ⛔ yes | Capture lifecycle can't be controlled from C#. High-risk for anyone trying to drive capture state programmatically; higher-level scanning UI flow still works. |
| BlinkIDUX | `BlinkIDUX.SampleBuffer` | ⛔ yes | Frame delivery type — opaque. `IAsyncEnumerable<SampleBuffer>` stream items round-trip but individual frame data (pixel buffer) unreachable. |
| StripePaymentSheet | `StripePaymentSheet.CustomerPaymentOption` | ✅ cleared | n/a |
| TipKit | `TipKit.MiniTipViewStyle` | ⛔ yes (documented permanent) | Session 1 accepted as permanent limitation of the PAT-existential projection. |

The Kingfisher + BlinkIDUX tombstones suggest a second, narrower skip path also fires for their payload/associated types that the Session 1 `EnumHandler` narrowing did not cover. `CacheStoreResult` and `CaptureService` / `SampleBuffer` are different emitter branches (not all enum payload cases — `CaptureService` is a class, for example).

**Fix scope.** Trace how each of the 3 remaining tombstones gets emitted and apply an analogous constraint-aware narrowing, or emit real projections. Don't leave them as silent empty types.

### Research notes (2026-04-22, post-triage)

**The three tombstones live in three DIFFERENT code paths — not the same branch as Issue 5. Each needs a separate fix.**

| Tombstone | Handler | Root skip |
|---|---|---|
| `Kingfisher.CacheStoreResult` | struct/property (`NonFrozenStructHandler` → `MemberEmissionValidator`) | `BoundGenericsHandler.HasNonSwiftObjectGenericArg` returns `true` for `Swift.Result<(), _>` (Void tuple as Success arg) |
| `BlinkIDUX.SampleBuffer` | class → `PropertyHandler` | Type-database miss for `CoreMedia.CMSampleBuffer` |
| `BlinkIDUX.CaptureService` | class (`IsActor=true`) | Explicit skip for actor-synthesized `unownedExecutor`; all real methods blocked by `WrapperValidation.IsActorIsolatedMember` |

**`Kingfisher.CacheStoreResult`.** Swift decl: `Swift.Result<(), Swift.Never>` and `Swift.Result<(), Kingfisher.KingfisherError>`. Trips `MemberEmissionValidator.cs:255` / `MemberGateEvaluator.cs:106` (Gate P6). **Fix:** add bypass in `HasNonSwiftObjectGenericArg` (where `Foundation.Measurement` + `ManagedSettings.Token` bypasses already exist) for `Swift.Result` when `Success == Swift.Void`, OR project `Result<(), E>` directly to a C# throwing-void idiom.

**`BlinkIDUX.SampleBuffer`.** Swift decl: `final public let buffer: CoreMedia.CMSampleBuffer`. `CoreMediaDatabase.xml` (referenced at `Program.cs:559`) lacks an entry for `CMSampleBuffer`. **Fix:** add it as an opaque class type (`CFTypeRef`-backed). Once resolvable, property projects as `IntPtr` or a registered CoreMedia wrapper.

**`BlinkIDUX.CaptureService`.** Swift decl: `public actor CaptureService`. The only ABI-visible member is the auto-synthesized `unownedExecutor`, which is explicitly skipped at `ClassHandler.cs:216–220` — that skip is *correct and tested* (`TypeHandlersOutputTests.cs:759–800` asserts it). Real methods (`start()`, `stop()`) are blocked by `WrapperValidation.IsActorIsolatedMember`. **Proper fix:** emit actor-isolated methods as `async Task<T>` routed through the actor's executor (larger change). **Cheaper fix:** shell emitter projecting actor methods as async stubs so the type isn't fully opaque.

**Tests.** `ThirdPartyValidationFixTestsV4.cs:133,147,161` and `ThirdPartyValidationFixTestsV3.cs:276,296` cover `HasNonSwiftObjectGenericArg` for various types — no test yet covers `Swift.Result<(), _>`. `MemberGateEvaluatorTests.cs:143,313` covers Gate P6. No test covers the `CMSampleBuffer` DB-miss path.

---

## Issue E — Round 4 functional gaps unresolved

### E.1 — StoreKit2 `VerificationResult<T>` unwrap — NEAR-SHIP

- `VerificationResult<T>` is now emitted as a real generic class (no longer a silent tombstone) with `Tag` (`Unverified=0, Verified=1`) and `DebugDescription` — progress vs Round 4.
- But NO `TryGetVerified(out T)` / `TryGetUnverified(out T)` / associated-value accessor of any kind. Caller can read the tag but cannot extract the `Transaction` / `AppTransaction` / `Product` payload.
- `Product.ProductsAsync` **does** emit cleanly (`Task<IReadOnlyList<Product>>` at line 24429) — **Round 4 blocker #5 is resolved**. The "Unsupported" tombstone comment at line 24317 is for a different generic overload.
- `Transaction.CurrentEntitlements` emits; end-to-end entitlement enumeration is unreachable only because of the unwrap gap.

**Consumer impact.** Any IAP flow that needs to act on a verified transaction (finish it, read `Transaction.productID`, etc.) is blocked.

#### Research notes (2026-04-22, post-triage)

**This is a 1-file fix, no Swift wrapper changes needed.**

**Emitter files (partial-class split):**
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumHandler.CaseConstruction.cs` — factories (`Verified(T)`, `Unverified(T, VerificationError)`)
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumHandler.CaseInspection.cs` — `TryGet{Case}` accessors (`EmitTryGetMethod` at line 102)
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumHandler.Marshalling.cs` — payload marshal helpers (`EmitPayloadMarshal` at line 307)

**How non-generic extraction works today.** `EmitTryGetMethod` stackallocs a buffer sized by `SwiftObjectHelper<Enum>.GetTypeMetadata().Size`, calls `DestructiveProjectEnumData` to strip tag bits, then `SwiftMarshal.MarshalFromSwift<T>(buf)` to deserialize the payload.

**Why generic payloads currently skip.** `EmitPayloadMarshal` has no branch for a *bare* generic type parameter (`τ_0_0` / `T`). It handles bound generics, existentials, and concrete types — never a raw type-parameter reference. The factory direction (`CaseConstruction.cs:447–453`) already handles this (stackalloc by `TypeMetadata.GetTypeMetadataOrThrow<T>()` size, then `SwiftMarshal.MarshalToSwift`). Extraction just needs the mirror.

**Fix — add branch in `EmitPayloadMarshal` before fallback:**
```csharp
if (typeSpec is NamedTypeSpec np &&
    TypeSpecHelpers.IsGenericTypeParameter(np.Name) &&
    TryGetGenericTypeParameterName(np.Name, out var csParamName, genericParams))
{
    csWriter.WriteLine($"{varName} = SwiftMarshal.MarshalFromSwift<{csParamName}>(new IntPtr({sourcePtr}));");
    return;
}
```
Mirror into `EmitPayloadMarshalWithDeclaration` / `EmitPayloadMarshalWithOffset` for tuple-element parity.

**Already correct (no change needed):** `EmitTryGetMethod` already passes `marshalGenericParams`; `SwiftObjectHelper<VerificationResult<T>>.GetTypeMetadata()` already works; `GetCSharpTypeNameForEnumCase` (CaseConstruction.cs:700–703) already maps `τ_0_0` → `TSignedType`.

**Pattern generalizes** beyond StoreKit2 — any generic enum with generic-typed associated values benefits.

**Tests.** Extend `src/Swift.Bindings/tests/UnitTests/EmitterTests/EnumHandlerOutputTests.cs`, `EnumCaseAssociatedValueTests.cs`, `EnumExtractionTests.cs` — assert `TryGetVerified` / `TryGetUnverified` emit for a generic enum; assert `GetCSharpTypeNameForEnumCase` handles `τ_0_0` with `genericParams` populated.

### E.2 — WeatherKit `Forecast<T>` — HOLD

- `Forecast<TElement>` remains a compile-time tombstone. Generated C# at `WeatherKit.cs:12471`:
  ```
  // Unsupported: type 'Forecast' — IndeterminatePwtShape
  // (TElement: Swift.Decodable, Swift.Encodable, Swift.Equatable —
  //  protocols not projected in the type database)
  ```
- `Weather` struct exposes only `CurrentWeather`, `WeatherAlerts`, `Availability` — `HourlyForecast`, `DailyForecast`, `MinuteForecast` (all typed `Forecast<...>`) are not emitted.
- `Trend<D>` also tombstoned (e.g. temperature trend).
- `WeatherQuery<T>` offers `Current`, `Alerts`, `Availability`, `Changes`, `HistoricalComparisons` but no hourly/daily/minute query variants.

**Consumer impact.** `foreach (var hour in weather.HourlyForecast)` does not compile — the primary consumer-facing flow is unreachable.

#### Research notes (2026-04-22, post-triage)

**Scope: medium.** Not a 1-file fix, but uses existing runtime-descriptor PWT infrastructure (already working for `Lottie.AnyInterpolatable`).

**Decision chain:**
- `src/Swift.Bindings/src/Emitter/StringEmitter/PInvokeHelperEmitter.cs` — `FlattenConformances()` at lines 239–249 is the gate. Iterates generic-param conformances; calls `typeDatabase.TryGetTypeRecord(target, out record)`. Miss or `record.Kind != TypeRecordKind.Protocol` → `unresolved` list with reason `"protocol not projected in the type database"`. `HasIndeterminatePwtShape = UnresolvedPwtConstraints.Count > 0`.
- `src/Swift.Bindings/src/Emitter/StringEmitter/TypeSkipPrePass.cs` — records skip to `ReportCollector`.
- `src/Swift.Bindings/src/Emitter/StringEmitter/TypeMetadataAccessorSkipGate.cs` — `ShouldSkip()` writes the tombstone comment.

**"PWT" = Protocol Witness Table.** "Indeterminate" because Swift's generic-type metadata accessor takes `(num_type_metadata + num_pwts)` args; if `num_pwts` is unknown, the emitter cannot choose between thin-mode (≤ 3 args) and buffer-mode (> 3 args) P/Invoke. Fails closed to avoid silent ABI mismatch.

**Root cause.** `Swift.Decodable`, `Swift.Encodable`, and `Swift.Equatable` are NOT registered in `src/Swift.Runtime/src/Swift/SwiftDatabase.xml`. The stdlib DB currently holds only primitives + `Array/Set/Dictionary/Optional/Result/Hasher/AnyHashable` — zero protocol entries.

**How Array/Set/Dictionary avoid this path entirely.** They're hand-written in `src/Swift.Runtime/src/Swift/SwiftArray.cs` etc., registered as `kind="struct"` (`managedTypeName="SwiftArray"`), and hardcoded into `BoundGenericsHandler.s_stdlibGenerics`. They go straight to the `IntPtr` buffer path, never through `CreateIfGeneric`. That bypass doesn't exist for user-framework generics like `Forecast<T>`.

**Fix — two parts:**
1. **Add protocol entries to `src/Swift.Runtime/src/Swift/SwiftDatabase.xml`** for `Swift.Decodable`, `Swift.Encodable`, `Swift.Equatable` (+ `Hashable`, `Comparable`, `Collection`, `Sequence` for full future coverage). Each needs `kind="protocol"`, `hasAssociatedTypes` / `hasSelfRequirement` flags, and `protocolDescriptorSymbol` for Self-requirement protocols (runtime-descriptor path).
   - `Equatable`: `hasSelfRequirement=true`, descriptor symbol `$sSQMp`
   - `Decodable` / `Encodable`: PAT-like (associated decoder/encoder context)
2. Buffer-mode accessor (`BuildBufferModeMetadataAccessorBlock`) already handles the 1 metadata + 3 PWT = 4-arg case. `GenericTypeEmitter.GetWhereClause` (lines 95–116) already filters PAT/Self constraints from the C# `where` clause. Verify `ISwiftEquatable` exists at `src/Swift.Runtime/src/Swift/SwiftEquatable.cs`.

**Also clears** (same root cause): `WeatherKit.Trend<D>`, `MusicKit.MusicRelationshipProperty<,>` (explicitly called out in `TypeSkipPrePass.cs` docstring as a `HasIndeterminatePwtShape` victim), and any future HealthKit/Apple-framework generic constrained only on stdlib protocols.

**Recommended first experiment.** Add just `Swift.Equatable` (simplest — single Self requirement, well-known descriptor). Regenerate WeatherKit; unresolved count should drop from 3 → 2. Then add `Decodable` + `Encodable`.

---

## Issue F — Stripe `@escaping () async throws -> T` closure gap (P1, blocks 4 Stripe products)

**Symptom.** 4 Stripe initializers are tombstoned for async-throwing closure parameters:
- `EmbeddedComponentManager.init(fetchClientSecret: @escaping () async throws -> String)` — StripeConnect HARD block, sole public ctor
- `StripeCustomerAdapter.init` — async-throwing closure
- `PaymentSheet.IntentConfiguration.init(confirmHandler: (STPConfirmationToken) async throws -> String)`
- `CustomerSheet.IntentConfiguration.init` — `(STPPaymentMethod, Bool) async throws -> String`

### Research notes (2026-04-22, post-triage)

**The gap is much narrower than it looks — almost all infrastructure exists.**

**Already built:**
- **Runtime.** `src/Swift.Runtime/src/Swift/Runtime/AsyncThrowingClosureState.cs` has state types for arities 0–4. `src/Swift.Runtime/src/Swift/Runtime/AsyncClosureHelper.cs` has `RunAsync<T>`, `CompleteWithResult<T>` — all generic over `T`. `ReportError()` encodes `ex.Message` as UTF-8 and passes to Swift's error callback (error bridging is already done).
- **Emitter.** `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.Async.cs` — `EmitAsyncThrowingClosureCallback` etc. handle throwing × non-throwing × return × void × arities 0–4.
- **Gate.** `src/Swift.Bindings/src/Marshaler/ClosureHandler.cs:873` — `IsBaselineAsyncThrowingClosure()`. `MaxAsyncThrowingClosureArity = 4`.

**The actual blocker.** `IsBaselineAsyncThrowingClosure` calls `CdeclParamMapper.IsBlittablePrimitiveSwiftType()` (`CdeclParamMapper.cs:662–674`) on the return type. That set includes only numeric primitives + `CGFloat`. **`Swift.String` is excluded.** `Foundation.Data` has a separate Session-D path. `Swift.String` has neither. All 4 Stripe sites return `String` → all 4 fail the gate.

**Sync error bridging (for reference).** Sync-throwing methods use `SwiftResult<TSuccess, TFailure>` where `TFailure = SwiftError` (existential `any Error`). See `src/Swift.Runtime/src/Swift/SwiftResult.cs` + `SwiftErrorException.cs`.

**Tombstone messages (exact text from generated C#):**
- `EmbeddedComponentManager.init` — `StripeConnect.cs:6577`: *"closure signature not yet supported (Parameter 'fetchClientSecret' has unsupported closure type that cannot be marshalled.)"*
- `StripeCustomerAdapter.init` — `StripePaymentSheet.cs:3194`: *"parameter or return type not yet supported (unbridgeable async-throwing closure)"*
- `PaymentSheet.IntentConfiguration.init(confirmHandler:)` — `StripePaymentSheet.cs:28695–28697` (two sites)
- `CustomerSheet` / `ExternalPaymentMethodConfiguration.init` — `StripePaymentSheet.cs:25305, 24824`

**Fix — ~30–50 lines:**
1. Add `Swift.String` arm to `IsBaselineAsyncThrowingClosure` in `ClosureHandler.cs:877–892`, alongside numeric-primitive branch.
2. Add a String-return success-callback branch in `ClosureEmitter.Async.cs` analogous to existing `isDataReturn` arm. Swift continuation for String passes `(continuationBox, utf8Ptr, utf8Len)` — same ABI as existing sync `async -> String` (e.g. `setupIntentClientSecretForCustomerAttach` already works).
3. Runtime `CompleteWithResult<T>` already generic — no change.
4. All 4 Stripe sites clear with same change. Class arg types (`STPConfirmationToken`, `STPPaymentMethod`) are already in `AsyncThrowingArgCategory.Class`.

**Expected C# shape:**
```csharp
public EmbeddedComponentManager(Func<Task<string>> fetchClientSecret);
public IntentConfiguration(Func<STPConfirmationToken, bool, Task<string>> confirmHandler, ...);
```

**Tests.** Add a String-return-type case to existing async-closure emitter tests; add a BindingTests fixture exercising `() async throws -> String` round-trip (`String` marshalling differs from `Data` — critical to cover).

---

## Per-package final classification

### Apple frameworks (12)

| Package | Build | Sim | Device | SB0001 | Status | Notes |
|---|---|---|---|---|---|---|
| ActivityKit | ✅ | ✅ | ⛔ 4 | 0 | **NEAR-SHIP** | Issue B: `ActivityAuthorizationInfo` NativeThunk. ActivityConfiguration user-type routing still gapped. |
| CryptoKit | ⛔ 144 err | — | — | 37 (blocked by build) | **HOLD** | Issue A. Swift wrapper OK, C# emitter bugs. |
| FamilyControls | ✅ | ✅ | ⛔ 2 | 0 | **NEAR-SHIP** | Issue B: `AuthorizationCenter.Shared` NativeThunk. |
| LiveCommunicationKit | ✅ | ✅ | ✅ | 0 | **SHIP** | 14 NativeThunk entries present but not test-exercised. |
| MusicKit | ✅ | ✅ | ⛔ 3 | 4 | **NEAR-SHIP** | Issue C (collection-index SB0001) + Issue B (player singleton NativeThunks). `MusicRelationshipProperty<,>` tombstone unchanged. |
| ProximityReader | ✅ | ✅ | ✅ | 0 | **SHIP** | 9 NativeThunk entries present but not test-exercised. |
| RoomPlan | ✅ | ✅ | ✅ | 0 | **SHIP** | Issue 2 resolved. `SIMD3<Float>` → `Vector3`, `simd_float4x4` → `Matrix4x4` round-trip on device. `CapturedStructure` list gap unchanged (not primary flow). |
| StoreKit2 | ✅ | ✅ | ⛔ 1 | 0 | **NEAR-SHIP** | Issue E.1 (VerificationResult unwrap) + Issue B. Product.ProductsAsync emits — Round 4 blocker #5 resolved. |
| TipKit | ✅ | ✅ | ⛔ 2 | 12 | **NEAR-SHIP** | Result-builder DSL permanent limitation. `MiniTipViewStyle` tombstone (documented permanent). + Issue B. |
| Translation | ✅ | ✅ | ⛔ 1 | 0 | **NEAR-SHIP** | Issue B: `LanguageAvailability()` ctor NativeThunk. Non-device surface clean. |
| WeatherKit | ✅ | ✅ | ⛔ 3 | 0 | **HOLD** | Issue E.2 (Forecast<T>) blocks primary flow + Issue B. |
| WorkoutKit | ✅ | ✅ | ⛔ 3 | 0 | **NEAR-SHIP** | Issue B: `WorkoutScheduler.Shared` / `IsSupported` / `MaxAllowedScheduledWorkoutCount` NativeThunks. HealthKit writes permanent limitation. |

### Stripe (12 products, 1 NuGet each + StripeUICore internal)

| Product | Build | Sim | Device | SB0001 | Status | Notes |
|---|---|---|---|---|---|---|
| Stripe (umbrella) | ✅ | ✅ | ✅ | 0 | **SHIP** | Async-closure gap does not apply to umbrella surface. |
| StripeCore | ✅ | ✅ | ✅ | 0 | **SHIP** | `STPAPIClient` emits; 82 test assertions. |
| StripePayments | ✅ | ✅ | ✅ | 0 | **SHIP** | `STPPaymentIntent`/`STPPaymentMethod` emit; 154 test assertions. |
| StripePaymentsUI | ✅ | ✅ | ✅ | 0 | **SHIP** | `STPCardFormView`/`STPPaymentCardTextField` emit; 79 test assertions. |
| StripeIdentity | ✅ | ✅ | ✅ | 0 | **SHIP** | `IdentityVerificationSheet` emits; 18 test assertions. |
| StripeCardScan | ✅ | ✅ | ✅ | 0 | **SHIP** | `CardScanSheet` emits; 19 test assertions. |
| StripeFinancialConnections | ✅ | ✅ | ✅ | 0 | **SHIP** | `FinancialConnectionsSheet` emits; 13 test assertions. |
| StripePaymentSheet | ✅ | ✅ | ✅ | 0 | **NEAR-SHIP** | `PaymentSheet.present(from:completion:)` bridged. But 3 init overloads still tombstoned for `@escaping () async throws -> …` — `StripeCustomerAdapter.init`, `CustomerSheet.IntentConfiguration.init`, `PaymentSheet.IntentConfiguration.init(confirmHandler:)`. Server-side-intent flow and custom CustomerAdapter flow blocked. |
| StripeApplePay | ✅ | ✅ | ✅ | 0 | **NEAR-SHIP** | No async-closure gap — `PresentApplePay(completion:)` emits. Named gap is actually `STPApplePayContext.paymentAuthorizationController(_:didSelectShippingContact:handler:)` — PassKit `PKPaymentAuthorizationController` placeholder, not async. |
| StripeConnect | ✅ | ✅ | ✅ | 0 | **NEAR-SHIP** | Hard-blocked: `EmbeddedComponentManager.init(fetchClientSecret: @escaping () async throws -> String)` is the sole public ctor — still tombstoned. **Entire embedded-components flow unreachable.** |
| StripeIssuing | ✅ | ✅ | ✅ | 0 | **NEAR-SHIP** | No async-closure gap. `STPPushProvisioningContext.addPaymentPassViewController(...)` tombstoned for PassKit placeholder. Simulator-safe flows clean. |
| StripeUICore (internal) | ✅ | — | — | — | — | xcframework only; no NuGet. |

### Third-party libraries (6)

| Library | Build | Sim | Device | SB0001 | Status | Notes |
|---|---|---|---|---|---|---|
| Nuke | ✅ | ✅ | ✅ | 5 | **NEAR-SHIP** (ship-with-docs) | SB0001 on `IImageDecodingDelegate.Decoder`, `IImageDataLoaderDelegate.LoadData`, `ImagePipeline(delegate:configuration:)` ctor. Custom pipelines blocked; `ImagePipeline.Shared` + `LoadImage(request:completion:)` fully reachable. 94 assertions pass. |
| Lottie | ✅ | ✅ | ✅ | 8 | **NEAR-SHIP** (ship-with-docs) | SB0001 all on `LottieAnimationButton` subclass (`SetLayer`, `SetPlayRange` callbacks). Main `LottieAnimationView.Play(completion:)` has 4 clean overloads. 115 assertions pass. |
| Kingfisher | ✅ | ✅ | ✅ | 39 (15 distinct methods) | **NEAR-SHIP** | Issue 5 resolved. `CacheStoreResult` tombstone (Issue D) impairs cache write-back inspection. Primary `KingfisherManager.Shared` + `RetrieveImage` flow reachable. 200 assertions pass. |
| BlinkID | ✅ | ✅ | ✅ | 1 | **NEAR-SHIP** (ship-with-docs) | Single SB0001 on `DateResult<TStringType>.ToString()` — cosmetic. `ProcessingActor.Shared` reachable. 113 assertions pass. |
| BlinkIDUX | ✅ | ✅ | ✅ | 0 | **NEAR-SHIP** | Downgraded from SHIP by `CaptureService` + `SampleBuffer` tombstones (Issue D). Higher-level SwiftUI scanning view works; direct frame control doesn't. 97 assertions pass. |
| Mappedin | ✅ | ✅ | ✅ | 10 | **NEAR-SHIP** (ship-with-docs) | SB0001 all on `Async`-suffixed Task wrappers (`SuggestAsync`, `SearchAsync`, etc.). Callback overloads are SB0001-free. 83 assertions pass. |

---

## Sim + device test totals

Counted by **test-app runs** (not shippable NuGets — one Stripe umbrella test app covers all 12 Stripe products in a single run).

- **Sim**: 18 test-app runs, **18/18 PASS** (11 Apple + 6 third-party + 1 Stripe umbrella run covering all 12 Stripe products)
- **Device**: 18 test-app runs, **10/18 PASS** (3 Apple + 6 third-party + 1 Stripe umbrella)
- **Device failures**: 8 Apple test apps, all Issue B (thunk `.o` files missing from device slice — see Issue B Research notes for the confirmed root cause)
- **Build-fail**: 1 Apple package (CryptoKit, Issue A)

Test assertion counts (rough, from per-package audits): Apple sim ~180 / Stripe sim 299 / third-party sim ~770 = **~1249 sim assertions PASS**. Device green totals are ~1020 (Apple device-green subset ~180 minus ~25 for NativeThunk-exercising tests + Stripe 299 + third-party ~770).

---

## Priority fix list for next SDK drop

1. **Issue A — CryptoKit C# emitter (2 bugs).** Return-type generic-arity substitution + SHA3 receiver-type name canonicalization in the CSM extension-class emitter. Unblocks CryptoKit on all 4 TFMs.

2. **Issue B — Thunk `.o` files not linked into device slice (MSBuild gap).** Make `_CompileAppleFrameworkSecondWrapperSlice` in `Sdk.targets` match the thunk-link behavior of `SwiftWrapperCompiler.InvokeSwiftCompiler` (`SwiftWrapperCompiler.cs:1313`) — append `.arm64.o` files AND add `-Xlinker -framework -Xlinker {originalModuleName}`. Alternatively, migrate affected shapes (zero-arg class ctors, static-singleton accessors) from NativeThunk to `@_cdecl` as an escape hatch. Unblocks 8 Apple frameworks on device. **Highest-leverage single fix** — more packages affected than any other open issue.

3. **Issue E.1 — StoreKit2 `VerificationResult<T>.TryGetVerified(out T)`.** Emit `TryGetVerified` / `TryGetUnverified` for any generic enum whose cases carry generic-parameter-typed associated values. Named Round 4 blocker #2, still open. Unblocks the IAP primary flow.

4. **Issue E.2 — WeatherKit `Forecast<TElement>` projection.** Named Round 4 blocker #3, still open. Requires the generic-container-with-metadata projection (also generalizes to `HKStatisticsCollection<Sample>`, etc.). Without it, WeatherKit stays HOLD.

5. **Issue F — Stripe `async throws -> String` closure gap (narrower than originally scoped).** The `@escaping () async -> Void` case already emits. What remains is specifically `String`-return async-throwing closures on 4 tombstoned initializers inside `StripeConnect` + `StripePaymentSheet`: `EmbeddedComponentManager.init(fetchClientSecret:)` (StripeConnect's sole public ctor — HARD block), `StripeCustomerAdapter.init`, `PaymentSheet.IntentConfiguration.init(confirmHandler:)`, `CustomerSheet.IntentConfiguration.init`. Unblocks **2 Stripe packages**: StripeConnect (fully) + StripePaymentSheet (IntentConfig flow). Does NOT unblock StripeApplePay or StripeIssuing — those are blocked by PassKit existential placeholders (separate gap).

6. **Issue C — MusicKit collection-index filter.** Tighten the Issue 3 specialization filter. 4 SB0001 → 0. Minor.

7. **Issue D — Silent tombstones for Kingfisher + BlinkIDUX.** Finish the Session 1 narrowing for non-enum / non-ObjC-payload tombstones. 3 remaining.

8. **Issue 6 (pre-existing)** — `spm-to-xcframework cafa869b74c8` Stripe mixed-framework headers. Tooling, not a release blocker.

---

## Shipping recommendation

**Ship today (14 packages):**
- Apple (3): LiveCommunicationKit, ProximityReader, RoomPlan — sim + device clean
- Stripe (7 SHIP): Stripe umbrella + StripeCore + StripePayments + StripePaymentsUI + StripeIdentity + StripeCardScan + StripeFinancialConnections — sim + device clean
- Third-party (4, ship with documented SB0001 surface): Nuke, Lottie, BlinkID, Mappedin — sim + device clean, SB0001 scoped to non-primary surface

**Hold on Issue B device-slice link fix (7 Apple packages):**
- ActivityKit, FamilyControls, MusicKit, StoreKit2, TipKit, Translation, WorkoutKit — sim clean; device blocked by Issue B only

**Hold on Issue D tombstone fixes (2 third-party packages):**
- Kingfisher — Issue D.1 (`Swift.Result<(), _>` bypass) blocks cache write-back inspection
- BlinkIDUX — Issues D.2 (`CMSampleBuffer` DB entry) + D.3 (actor shell-stub) block capture lifecycle + frame data

**Hold on multi-fix (2 packages):**
- WeatherKit — needs both Issue E.2 and Issue B resolved
- CryptoKit — needs Issue A (build-fail) fixed

**Hold on Issue F — async-throwing `String` closure fix (2 Stripe NEAR-SHIP):**
- StripeConnect, StripePaymentSheet — build + sim + device all green, primary-flow API blocked:
  - Connect: zero reachable ctor — HARD block
  - PaymentSheet: IntentConfiguration flow blocked (alternative: PaymentSheet presentation works)

**Hold on separate PassKit gap (2 Stripe NEAR-SHIP, NOT Issue F):**
- StripeApplePay: PassKit existential blocked (alternative: delegate-based flow works)
- StripeIssuing: PassKit existential blocked (alternative: non-physical-provisioning flow works)
- *Fix scope:* PassKit placeholder projection — not on the current 4-session plan; could be picked up post-release.

**Recommended approach.** Ship the 14 SHIP packages now. For the 13 NEAR-SHIP + 2 HOLD, bundle the next SDK drop around Issue A + Issue B fixes to unblock the biggest population (9 packages cleared once those two land), then work through E.1 / Issue F / D / E.2 per the revised session plan at the top of this doc.

---

## Environment notes

- **Primary repo:** `/Users/wojo/Dev/swift-dotnet-packages`
- **Generator repo:** `/Users/wojo/Dev/swift-bindings`
- **SDK drop:** `/Users/wojo/Dev/swift-dotnet-packages/local-packages/` (2026-04-22 00:23)
- **SDK versions:** `SwiftBindings.Sdk 0.8.0`, `SwiftBindings.Runtime 0.8.0`, `SwiftBindings.Templates 0.8.0`, `SwiftBindings.Apple 26.2.0`
- **.NET SDK:** 10.0.103 (pinned in `global.json`)
- **Device under test:** iPhone 13 (iPhone14,5), iOS 26.x, connected via Xcode Core Device
- **Device build mode:** Default `--device` (Mono AOT). `--aot` (NativeAOT) NOT tested this round.
- **Sim:** booted via `dotnet nuke BootSim` from the Nuke fleet

Full validation logs: `/tmp/ship-readiness-2026-04-22/` (thirdparty-build.log, apple-build.log, stripe-build.log, apple-sim-validate-v3.log, apple-device.log, thirdparty-stripe-device.log, plus SB0001 + tombstone + Info.plist audit files).

---

## Reproduction — full validation sequence

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

# 4. Stripe (spm-to-xcframework still fails; fast path since xcframeworks cached)
for p in StripeCore StripeUICore StripePayments StripePaymentsUI StripeApplePay \
         Stripe StripePaymentSheet StripeConnect StripeIdentity StripeIssuing \
         StripeCardScan StripeFinancialConnections; do
  csproj=$(find libraries/Stripe/$p -maxdepth 2 \
    \( -name 'SwiftBindings.Stripe.*.csproj' -o -name 'SwiftBindings.Stripe.csproj' \) | head -1)
  dotnet build "$csproj" -v q 2>&1 | tail -3
done
dotnet nuke InjectProjectRefs --library Stripe --all-products
# re-run the per-product build loop above for pass 2

# 5. Sim tests (run SERIALLY — concurrent Nuke invocations collide on .nuke/temp/build.log)
dotnet nuke BootSim
for fw in ActivityKit FamilyControls LiveCommunicationKit MusicKit ProximityReader \
          RoomPlan StoreKit2 TipKit Translation WeatherKit WorkoutKit; do
  dotnet nuke BuildTestApp --library $fw
  dotnet nuke ValidateSim --library $fw --timeout 30
done
for lib in Nuke Lottie Kingfisher BlinkID BlinkIDUX Mappedin; do
  dotnet nuke BuildTestApp --library $lib
  dotnet nuke ValidateSim --library $lib --timeout 30
done
dotnet nuke BuildTestApp --library Stripe
dotnet nuke ValidateSim --library Stripe --timeout 60

# 6. Device tests (serial — same lock concern)
for fw in ActivityKit FamilyControls LiveCommunicationKit MusicKit ProximityReader \
          RoomPlan StoreKit2 TipKit Translation WeatherKit WorkoutKit; do
  dotnet nuke BuildTestApp --library $fw --device
  dotnet nuke ValidateDevice --library $fw --timeout 45
done
for lib in Nuke Lottie Kingfisher BlinkID BlinkIDUX Mappedin; do
  dotnet nuke BuildTestApp --library $lib --device
  dotnet nuke ValidateDevice --library $lib --timeout 45
done
dotnet nuke BuildTestApp --library Stripe --device
dotnet nuke ValidateDevice --library Stripe --timeout 120
```

**Side note on tooling.** The Nuke CLI acquires an exclusive lock on `.nuke/temp/build.log` for every invocation; running two `dotnet nuke` commands concurrently produces "file is being used by another process" errors and causes the second invocation to fail silently (exit 0, no actual build). Keep sim+device test harnesses serial, or use a file-lock-tolerant wrapper.
