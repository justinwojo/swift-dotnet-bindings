# SDK 0.3.0 Validation Findings

Date: 2026-03-21

NuGet packages `SwiftBindings.Runtime`, `SwiftBindings.Sdk`, and `SwiftBindings.Templates` were built at version 0.3.0 and validated against two external test repositories.

---

## 1. swift-dotnet-packages

5 libraries tested: Nuke, Lottie, BlinkID, BlinkIDUX, Stripe (16 binding csproj files total).

### API Breaking Changes from 0.2.0

The nested type collision fix (commit `9ab694c5`) renames nested types that collide with properties. Properties that previously had a `Value` suffix to avoid collision lost the suffix:
- `pipeline.ConfigurationValue` → `pipeline.Configuration` (type: `ConfigurationType`)
- `request.OptionsValue` → `request.Options` (type: `OptionsType`)
- `pipeline.CacheValue` → `pipeline.Cache` (type: `CacheType`)

### Simulator Results (after skip audit)

| Library | Pass | Fail | Skip | Status |
|---------|------|------|------|--------|
| **Nuke** | 49 | 0 | 8 | PASS |
| **Lottie** | 64 | 0 | 4 | PASS |
| **BlinkID** | 14 | 0 | 0 | PASS |
| **BlinkIDUX** | 11 | 0 | 2 | PASS |
| **Stripe** | 5 | 0 | 5 | PASS |

**Improvements from skip audit:**
- Nuke: 45→49 pass, 15→8 skip (7 stale skips removed)
- Lottie: 57→64 pass, 9→4 skip (5 stale skips removed), crash eliminated
- BlinkIDUX: 1 failure reclassified as skip (wrapper not compiled)

### Skip Audit Results

Every skip was verified by checking the generated binding's calling convention (CallConvCdecl vs CallConvSwift) and testing it live.

#### Nuke — 8 remaining skips (all verified)

| Skip | Root Cause | Category |
|------|-----------|----------|
| ImageAsync(NSUrl) | `InvalidProgramException: non-blittable` — NSUrl param | NSUrl limitation |
| DataAsync(NSUrl) | Same non-blittable NSUrl issue | NSUrl limitation |
| DataCache.Path | Same non-blittable NSUrl return | NSUrl limitation |
| ImageTask(NSUrl) | Same non-blittable NSUrl param | NSUrl limitation |
| DataAsync(ImageRequest) | SIGSEGV in `dataOnComplete` callback marshalling NSUrlResponse | Runtime bug |
| Prefetcher.Priority | CallConvSwift mangled symbol (no @_cdecl wrapper) — SIGSEGV on Mono | Generator gap |
| ImageTask.Priority set | Same CallConvSwift pattern | Generator gap |
| ImageDecoders.Empty() | Constructor not emitted in SDK 0.3.0 | API removed |

**Previously skipped, now passing (7):**
- `ImageRequest.Priority` getter — uses @_cdecl wrapper, works fine
- `DataCache.StoreData` + `ContainsData` roundtrip — Data parameter marshalling works
- `DataCache.RemoveData` — works
- `DataCache.RemoveAll` — works (no dependency on StoreData)
- `DataCache.Url` — was duplicate of DataCache.Path test (already had try/catch)

#### Lottie — 4 remaining skips (all verified)

| Skip | Root Cause |
|------|-----------|
| LottieColor(r,g,b,a) constructor | CallConvSwift mangled symbol — SIGSEGV on Mono |
| LottieColor R/G/B/A properties | Depends on LottieColor constructor |
| ColorValueProvider | Depends on LottieColor constructor |
| SetValueProvider | `FloatValueProvider` conforms to `IAnyValueProvider` at runtime only (conformance factory), not at compile time |

**Previously skipped, now passing (8):**
- `LottieAnimation.From(data)` — uses @_cdecl wrapper
- `LottieAnimation.From(data, strategy)` — uses @_cdecl wrapper
- `AnimationView.BackgroundBehavior` set/get — uses @_cdecl wrapper
- `Play(fromProgress, toProgress)` — uses @_cdecl wrapper
- `CurrentPlaybackMode` getter — uses @_cdecl wrapper
- `AnimationKeypath.String` getter — uses @_cdecl wrapper
- `LottieAnimationLayer` construction + play/stop — consolidated test using config constructor
- Lottie crash eliminated (was hitting LottieColor constructor, now properly skipped)

#### BlinkIDUX — 2 remaining skips

| Skip | Root Cause |
|------|-----------|
| BlinkIDTheme.Shared | Requires resource bundle not available in test app |
| MicroblinkColor_Cases | `DllNotFoundException` — BlinkIDUXSwiftBindings wrapper xcframework not compiled |

#### Stripe — 5 remaining skips (all verified)

| Skip | Root Cause |
|------|-----------|
| DownloadManager.SharedManager | `@_spi` type correctly suppressed (expected behavior) |
| STPImageLibrary card images | StripePaymentsUISwiftBindings wrapper xcframework not compiled |
| CardScanSheetResult.Canceled | StripeCardScanSwiftBindings wrapper xcframework not compiled |
| FinancialConnections.Result.Canceled | StripeFinancialConnectionsSwiftBindings wrapper xcframework not compiled |

Note: VerificationFlowResult.FlowCanceled is listed alongside skips in test output but actually passes.

---

## 2. sim-validation

15 libraries tested across the `swift-bindings/.libraries` validation set.

### Generator Bugs Found (2)

1. **Kingfisher — nested type rename: handle generic parameter not updated**
   Generated binding doesn't compile: `AnimatorType` renamed but `SwiftClassHandle<Animator>` generic parameter still references old name.

2. **Reachability — extension method fully qualified name: namespace/type collision**
   Generated binding doesn't compile: extension method references `global::Reachability.ConnectionType` but type is nested under `Reachability.Reachability.ConnectionType`.

**XMLCoder note**: The 4 `EntryPointNotFoundException` failures (`SBW_Get_XMLCoder_BoolBox_isNull`, etc.) are NOT a generator bug. The C# P/Invoke entry points and Swift wrapper `@_cdecl` names match exactly. The issue is the compiled wrapper dylib doesn't export the symbols — the wrapper needs recompilation after the 0.3.0 regeneration.

### Simulator Results (after skip audit)

| Library | Pass | Fail | Skip | Status | Notes |
|---------|------|------|------|--------|-------|
| Alamofire | 5 | 0 | 1 | CRASH | URLEncoding ctor CallConvSwift+SwiftIndirectResult |
| Kingfisher | — | — | — | BUILD_FAILED | Generator bug #1 |
| RxSwift | 6 | 0 | 0 | EXITED | Crash at test 7: BooleanDisposable(bool) CallConvSwift |
| SnapKit | 18 | 0 | 0 | PASS | |
| CryptoSwift | 5 | 0 | 0 | EXITED | Crash at test 6: MD5() CallConvSwift (no wrapper) |
| KeychainAccess | 11 | 0 | 0 | CRASH | Keychain() ctor CallConvSwift (no wrapper) |
| Starscream | 19 | 6 | 3 | FAIL | URLRequest non-blittable + CallConvSwift (Mono) |
| DeviceKit | 10 | 0 | 0 | CRASH | Device.Name: SwiftOptional marshalling crash |
| PhoneNumberKit | 24 | 0 | 0 | CRASH | MainCountry: optional string optbuf crash |
| Reachability | — | — | — | BUILD_FAILED | Generator bug #2 |
| Swinject | 25 | 0 | 3 | PASS | |
| ObjectMapper | 18 | 0 | 0 | PASS | |
| SwiftyBeaver | 28 | 0 | 0 | PASS | |
| XMLCoder | 16 | 4 | 10 | FAIL | EntryPointNotFoundException (wrapper not recompiled) |
| BonMot | 30 | 0 | 0 | PASS | |

### Device Results

| Library | Pass | Fail | Skip | Status | Notes |
|---------|------|------|------|--------|-------|
| Alamofire | 5 | 0 | 0 | EXITED | All pass (CallConvSwift tests unreachable) |
| Kingfisher | — | — | — | BUILD_FAILED | Generator bug |
| RxSwift | 6 | 0 | 0 | EXITED | All pass (CallConvSwift tests unreachable) |
| SnapKit | 17 | 1 | 0 | FAIL | SwiftOptional metadata failure |
| CryptoSwift | 5 | 0 | 0 | EXITED | All pass (CallConvSwift tests unreachable) |
| KeychainAccess | 11 | 0 | 0 | EXITED | All pass (CallConvSwift tests unreachable) |
| Starscream | 19 | 0 | 9 | PASS | WebSocket tests skip (MarshalDirectiveException) |
| DeviceKit | 20 | 6 | 0 | FAIL | SwiftOptional (5) + SwiftArray (1) |
| PhoneNumberKit | 24 | 6 | 0 | FAIL | SwiftOptional (5) + SwiftArray (1) |
| Reachability | — | — | — | BUILD_FAILED | Generator bug |
| Swinject | 25 | 0 | 3 | PASS | |
| ObjectMapper | 18 | 0 | 0 | PASS | |
| SwiftyBeaver | 25 | 3 | 0 | FAIL | FileDestination TypeInitializationException |
| XMLCoder | 16 | 4 | 10 | FAIL | EntryPointNotFoundException (wrapper not recompiled) |
| BonMot | 30 | 0 | 0 | PASS | |

### Skip Audit Results

Every skip was verified by checking the generated binding's calling convention and entry point.

#### Alamofire — 1 skip (verified)

| Skip | Root Cause | Verified |
|------|-----------|----------|
| URLEncoding.Default | `CallConvSwift` + `SwiftIndirectResult` static property getter — crashes Mono JIT (same pattern as constructor). Skip note says "NativeAOT struct singleton crash (SIGBUS on second call)" — a separate issue on device. | Yes |

#### Starscream — 3 skips (2 verified, 1 potentially stale)

| Skip | Root Cause | Verified |
|------|-----------|----------|
| WebSocketEvent.Binary | Data type not projected — @_cdecl wrapper bridges to NSData | Yes |
| WebSocketEvent.ViabilityChanged | Skip says "NativeAOT enum case dispose crash" but P/Invoke uses `CallConvCdecl` + @_cdecl wrapper. May work on Mono sim. | **Needs live test** |
| WebSocketEvent.Ping | Optional Data payload not projected | Yes |

**Starscream 6 failures** — all `InvalidProgramException: Passing non-blittable types to a P/Invoke with the Swift calling convention`. Root cause: every failing test creates a `WebSocket` which requires `URLRequest.FromString()`. URLRequest internally uses `CallConvSwift` with non-blittable types. Tests not dependent on WebSocket (WSError, HTTPWSHeader, enums) pass fine.

#### Swinject — 3 skips (all verified)

| Skip | Root Cause |
|------|-----------|
| Container with closure | `SwiftArray<ExistentialContainer1>` not supported — confirmed by sim output error message |
| Assembler.Resolver | Property removed in 0.3.0 (existential return type no longer emitted) |
| Container.GetSynchronize() | Method removed in 0.3.0 (existential return type no longer emitted) |

#### XMLCoder — 10 skips (all verified)

All 10 are APIs no longer emitted in the 0.3.0 binding:

| Skip | Root Cause |
|------|-----------|
| StringBox("hello") constructor | Removed in 0.3.0 |
| XMLCoderElement("test-value") constructor | Removed in 0.3.0 |
| BoolBox.Unboxed property | Removed in 0.3.0 |
| StringBox.Unboxed property | Removed in 0.3.0 |
| BoolBox.Description property | Removed in 0.3.0 |
| XMLCoderElement.Key property | Removed in 0.3.0 |
| XMLCoderElement.StringValue property | Removed in 0.3.0 |
| XMLDecoder.DataDecodingStrategyValue | Removed in 0.3.0 |
| DoubleBox.Unboxed property | Removed in 0.3.0 |
| DoubleBox.Description property | Removed in 0.3.0 |

### Crash / Exit Root Cause Analysis

#### Alamofire — CRASH at test 6 (URLEncoding constructor)

- **Crash site**: `Alamofire.URLEncoding:PInvoke_init_2DD33C28` → `jit-info.c:918`
- **P/Invoke**: `CallConvSwift` + `SwiftIndirectResult` + 3 enum params, mangled symbol `$s9Alamofire11URLEncodingV...`
- **Root cause**: Frozen struct constructor with `SwiftIndirectResult` — Mono JIT can't handle this calling convention
- **Impact**: 5 of 30+ tests pass (metadata only), 25+ tests unreachable
- **Device behavior**: Same 5 tests "exit" before completion (CallConvSwift tests not reached)

#### KeychainAccess — CRASH at test 12 (Keychain() constructor)

- **Crash site**: `Swift.Runtime.SwiftDisposeScope:.cctor` → `jit-info.c:918`
- **P/Invoke**: `Keychain()` parameterless constructor uses `CallConvSwift`, mangled symbol `$s14KeychainAccess0A0CACycfC`
- **Root cause**: Class allocating constructor without @_cdecl wrapper. Note: `Keychain(string)` uses `CallConvCdecl` + wrapper and would work, but `Keychain()` crashes first.
- **Impact**: 11 of 30 tests pass (metadata + enums), 19 tests unreachable
- **Device behavior**: Same 11 tests pass, remaining tests exit before completion

#### DeviceKit — CRASH at test 11 (Device.Name getter)

- **Crash site**: `SwiftOptional<T>:.ctor` → `wrapper_native_indirect` → `jit-info.c:918`
- **P/Invoke**: `CallConvCdecl` + @_cdecl wrapper (`SBW_DeviceKit_Device_name_Get_D5800277_optbuf`) — **calling convention is correct**
- **Root cause**: NOT a CallConvSwift issue. The crash is inside the runtime's `SwiftOptional<T>.NewFromPayload` during optional string marshalling. The P/Invoke succeeds, but creating the `SwiftOptional<SwiftString>` from the returned data triggers a Mono JIT assertion in the native helper.
- **Impact**: 10 of 26 tests pass, 16 tests unreachable
- **Device behavior**: 20 pass, 6 fail with managed `SwiftRuntimeException: Unable to get type metadata for type SwiftOptional<T>` (same root cause, different manifestation)

#### PhoneNumberKit — CRASH at test 25 (MainCountry)

- **Crash site**: `PhoneNumberUtility:PInvoke_mainCountry_5C39064F` → `jit-info.c:918`
- **P/Invoke**: `CallConvCdecl` + @_cdecl wrapper (`SBW_PhoneNumberKit_PhoneNumberUtility_mainCountry_E19E5D95_optbuf`) — **calling convention is correct**
- **Root cause**: Same as DeviceKit — optional string return via optbuf pattern triggers Mono JIT crash during native helper execution
- **Impact**: 24 of 30 tests pass, 6 tests unreachable
- **Device behavior**: 24 pass, 6 fail with managed SwiftRuntimeException/SwiftArray failures

#### RxSwift — EXITED after test 6

- **Crash site**: Silent crash at test 7 (`BooleanDisposable(isDisposed: true)`)
- **P/Invoke**: `CallConvSwift`, mangled symbol `$s7RxSwift17BooleanDisposableC10isDisposedACSb_tcfC`, takes `bool` param
- **Root cause**: Class allocating constructor with `MarshalAs(UnmanagedType.U1) bool` + `CallConvSwift`. Note: the parameterless `BooleanDisposable()` also uses `CallConvSwift` but passes (no parameters to marshal). Adding a `bool` parameter triggers the crash.
- **Impact**: 6 of 31 tests pass, 25 tests unreachable
- **Device behavior**: Same 6 tests pass, remaining exit before completion

#### CryptoSwift — EXITED after test 5

- **Crash site**: Silent crash at test 6 (`new MD5()`)
- **P/Invoke**: `CallConvSwift`, mangled symbol `$s11CryptoSwift3MD5CACycfC`, no parameters. Has `[Obsolete("Uses CallConvSwift P/Invoke...")]` warning.
- **Root cause**: Class allocating constructor without @_cdecl wrapper. Note: `SHA1()` (test 5) uses a @_cdecl wrapper and passes. `MD5()` lacks one and crashes.
- **Impact**: 5 of 20 tests pass, 15 tests unreachable
- **Device behavior**: Same 5 tests pass, remaining exit before completion

### Root Cause Categories

#### 1. CallConvSwift without @_cdecl wrapper (simulator crashes/exits)

The generator emits @_cdecl wrappers for most operations, but some patterns lack wrappers:
- **Class allocating constructors** (e.g., `Keychain()`, `MD5()`, `BooleanDisposable(bool)`)
- **Struct constructors with SwiftIndirectResult** (e.g., `URLEncoding(dest, array, bool)`)
- **Class property getters/setters with SwiftSelf** (e.g., `ImagePrefetcher.Priority`)

These use `CallConvSwift` with mangled Swift symbols directly. On Mono, specific patterns crash:
- `CallConvSwift` + `SwiftSelf` → `jit-info.c:918` assertion
- `CallConvSwift` + `SwiftIndirectResult` → `jit-info.c:918` assertion
- `CallConvSwift` + `MarshalAs` parameters → crash (even without SwiftSelf/SwiftIndirectResult)
- `CallConvSwift` + no parameters → **works** (e.g., `BooleanDisposable()`, SHA1 metadata)

NativeAOT handles all these correctly but can't execute them without `PublishAot=true`.

**Affected libraries (sim)**: Alamofire (1 crash), KeychainAccess (1 crash), RxSwift (1 exit), CryptoSwift (1 exit)

**Fix**: Emit @_cdecl wrappers for all CallConvSwift patterns. The generator already does this for most operations — the remaining gaps are class allocating constructors and frozen struct constructors.

#### 2. SwiftOptional/SwiftArray marshalling on Mono (simulator crashes)

The runtime's `SwiftOptional<T>.NewFromPayload` and related marshalling helpers crash the Mono JIT when creating optional values from native payloads. These use correct `CallConvCdecl` + @_cdecl wrappers — the P/Invoke is fine, the crash is in the subsequent managed marshalling code.

On NativeAOT (device), the same code paths throw managed `SwiftRuntimeException` instead of crashing, allowing tests to continue.

**Affected libraries (sim)**: DeviceKit (crash at Device.Name), PhoneNumberKit (crash at MainCountry)
**Affected libraries (device)**: SnapKit (1 fail), DeviceKit (5 fails), PhoneNumberKit (5 fails)

**Note**: BindingTests proves `SwiftOptional<SwiftString>` and `SwiftArray<T>` work (14/14 pass). The library-specific failures may be related to missing type metadata registration for third-party generic instantiations.

#### 3. SwiftArray NewFromPayload on NativeAOT

`Failed to find NewFromPayload on SwiftArray<T>`

Affects: DeviceKit (1 fail), PhoneNumberKit (1 fail) on device only.

Same library-specific pattern as SwiftOptional — BindingTests work, third-party libraries fail.

#### 4. URLRequest non-blittable on Mono (Starscream)

`InvalidProgramException: Passing non-blittable types to a P/Invoke with the Swift calling convention`

All 6 Starscream failures trace to `WebSocket` construction requiring `URLRequest.FromString()`, which internally uses `CallConvSwift` with non-blittable types. Tests not dependent on WebSocket pass fine (19/28). On device, these become skips via `MarshalDirectiveException` catch.

#### 5. FileDestination TypeInitializationException (SwiftyBeaver device only)

`TypeInitializationException` on FileDestination constructor and dependent tests. Sim passes all 28 tests. Device fails 3 tests that construct/use `FileDestination`. Likely a NativeAOT type initialization issue specific to FileDestination's static fields.

#### 6. XMLCoder wrapper not recompiled

The 4 `EntryPointNotFoundException` failures (`BoolBox.IsNull`, `BoolBox.XmlString`, `BoolBox.TryCreate`, `NullBox.IsNull`) have matching entry point names between C# and Swift wrapper. The wrapper was not recompiled after 0.3.0 regeneration, so the dylib doesn't export the symbols.

#### 7. Wrapper compilation failures

Several Stripe submodules and BlinkIDUX don't compile their Swift wrappers:
- StripeCardScan, StripeFinancialConnections, StripePaymentsUI — inter-module dependencies
- BlinkIDUX — MicroblinkColor enum case wrapper not exported

These result in `DllNotFoundException` or `EntryPointNotFoundException` at runtime.

---

## Summary

### Audit results

**12 stale skips recovered** (now passing): Nuke (4), Lottie (8). All were tests whose skip reasons no longer applied after recent generator improvements.

**17 skips audited across sim-validation**: 16 verified valid, 1 potentially stale (`Starscream.WebSocketEvent.ViabilityChanged` — P/Invoke uses CallConvCdecl + @_cdecl wrapper but skip says "NativeAOT enum case dispose crash"; needs live test).

**~106 tests behind crash barriers** across 6 libraries — unreachable because the app crashes/exits before reaching them:

| Library | Tests Reached | Tests Behind Barrier | Barrier Cause |
|---------|--------------|---------------------|---------------|
| Alamofire | 5 | ~25 | CallConvSwift constructor |
| KeychainAccess | 11 | ~19 | CallConvSwift constructor |
| RxSwift | 6 | ~25 | CallConvSwift constructor |
| CryptoSwift | 5 | ~15 | CallConvSwift constructor |
| DeviceKit | 10 | ~16 | SwiftOptional marshalling |
| PhoneNumberKit | 24 | ~6 | SwiftOptional marshalling |

---

## Action Plan

### Session 1: Generator Bug Fixes + Infrastructure

**Generator compilation bugs (2):**

1. **Kingfisher — nested type rename: generic parameter not updated.** Generated binding doesn't compile: `AnimatorType` renamed but `SwiftClassHandle<Animator>` generic parameter still references old name. Fix: when renaming a nested type, also update generic type arguments that reference it.

2. **Reachability — extension method fully qualified name: namespace/type collision.** Generated binding doesn't compile: extension method references `global::Reachability.ConnectionType` but type is nested under `Reachability.Reachability.ConnectionType`. Fix: namespace-aware fully-qualified name resolution for extension methods.

**Infrastructure cleanup:**

3. **XMLCoder wrapper recompilation.** The 4 `EntryPointNotFoundException` failures have matching C#/Swift entry point names — the wrapper just needs recompiling after 0.3.0 regeneration. Not a generator bug.

4. **Starscream ViabilityChanged live test.** One potentially stale skip — P/Invoke uses `CallConvCdecl` + @_cdecl wrapper but skip reason references a NativeAOT issue. Live test to confirm whether it passes on Mono sim.

5. **Test reordering recommendation** for downstream repos (swift-dotnet-packages, sim-validation): put known-crashing tests last so crash barriers don't hide passing tests.

**BindingTests:**

- **Nested type + generic param**: Existing `NestedEnumTests.cs` has 14 tests but none where a renamed nested type appears as a generic type argument (e.g., `SwiftClassHandle<NestedType>`). Add a Swift type with a nested type that collides with a property name AND is referenced as a generic parameter. Verify the generated binding compiles and the test passes at runtime.
- **Extension + namespace collision**: Existing `EnumExtensionTests.cs` has 11 tests but none where the Swift module name matches a type name. Add a Swift type whose name matches the module namespace, with an extension method that references a nested type. Verify the fully-qualified name resolves correctly in the generated binding.

**Validation**: `run-tests.sh` + `validate-libraries.sh` (Kingfisher, Reachability should go from BUILD_FAILED to PASS). `build-and-test.sh` for new BindingTests.

---

### Session 2: @_cdecl Wrapper Gap Closure (highest impact) — DONE

The generator already emits @_cdecl wrappers for most operations, but three patterns lack wrappers and crash on Mono. Closing these gaps unlocks **~85 tests behind crash barriers** across 4+ libraries.

All changes in `WrapperValidation.cs` (`RequiresCdeclForAbiSafety`):

1. **Class allocating constructors.** All class constructors now require @_cdecl wrappers. Swift's allocating init passes hidden `@thick Self.Type` metatype — Mono JIT crashes without wrapper. Previously only generic class constructors were caught.

   **Affected libraries (sim)**: KeychainAccess (~19 tests), RxSwift (~25 tests), CryptoSwift (~15 tests).

2. **Frozen struct constructors with SwiftIndirectResult.** All frozen struct constructors now require @_cdecl wrappers. SwiftIndirectResult + CallConvSwift crashes Mono JIT.

   **Affected libraries (sim)**: Alamofire (~25 tests).

3. **Final class instance property getters/setters.** Broadened from non-final-only to ALL non-static class instance properties. Mono JIT can't handle CallConvSwift + SwiftSelf even on final classes.

   **Affected libraries**: Nuke (2 skips), Lottie (3 skips via LottieColor constructor).

**Known remaining gaps** (pre-existing `ShouldEmitWrapper` rejections, tracked in roadmap):
- ObjC-bridged optional setters (`UIViewController?`, `NSString?`) — `PropertyWrapperEmitter` rejects due to IntPtr reconstruction incompatibility.
- Optional-closure property setters (`((…) -> Void)?`) — `PropertyWrapperEmitter` rejects closure properties.

**BindingTests added:**

- **`MultiInitClass`** (Classes.swift): Class with 3 constructors (parameterless, bool param, string+int). 6 runtime tests — all pass.
- **`FrozenRect`** (Structs.swift): 32-byte frozen struct (4 Doubles, triggers SwiftIndirectResult). 4 runtime tests — all pass.
- **`FinalPropertyHolder`** (Classes.swift): Final class with Int32/Double/String/Bool read-write properties + computed summary. 6 runtime tests — all pass.

**Unit tests:** 7 new tests in AbiSafetyTests.cs + 1 updated in ConstructorHandlerOutputTests.cs. 3 existing tests updated to match new behavior.

**Validation results:**
- Unit tests: 8440 pass, 0 fail
- Validation: 90/90 pass, no regressions
- Runtime tests (sim): 757 pass, 32 skip (up from 742/27 — +16 new tests, +1 pre-existing crash skipped)

---

### Session 3: SwiftOptional Mono Marshalling (runtime investigation)

`SwiftOptional<T>.NewFromPayload` crashes Mono JIT inside `wrapper_native_indirect` even with correct `CallConvCdecl` + @_cdecl wrappers. The P/Invoke succeeds, but constructing the `SwiftOptional<SwiftString>` from the returned data triggers a JIT assertion in the native helper.

- **Sim**: DeviceKit crash at test 11 (`Device.Name`), PhoneNumberKit crash at test 25 (`MainCountry`)
- **Device**: Same root cause manifests as managed `SwiftRuntimeException` — SnapKit (1 fail), DeviceKit (5+1 fails), PhoneNumberKit (5+1 fails)
- **BindingTests**: `SwiftOptional<SwiftString>` and `SwiftArray<T>` work fine (14/14 pass) — so this may be related to missing type metadata registration for third-party generic instantiations, not a fundamental marshalling bug

**Approach**: Investigate the difference between BindingTests (works) and third-party libraries (crashes). Likely candidates: metadata registration gap, generic instantiation differences, or library-specific type layout.

**BindingTests:**

- **SwiftOptional cross-module**: Existing optional tests (14/14 pass) all use types from the test library itself. The third-party crashes may be related to missing metadata registration for generic instantiations across module boundaries. If root cause is identified, add a targeted regression test that reproduces the specific failure pattern. This may require a second Swift module in the test setup to simulate the cross-module boundary.

**Validation**: `run-tests.sh` + re-run DeviceKit and PhoneNumberKit in sim-validation to confirm fix. `build-and-test.sh` if new BindingTests added.

---

### Not actionable (known limitations)

- URLRequest/Foundation type non-blittable marshalling — needs Foundation type support
- `@_spi` type suppression — correct behavior
- Resource bundle dependencies (BlinkIDTheme) — deployment limitation
- Existential return types removed in 0.3.0 — correct generator behavior
- SwiftyBeaver FileDestination TypeInitializationException — NativeAOT-specific type init issue
- Stripe/BlinkIDUX wrapper compilation — inter-module dependencies / missing types
- Starscream WebSocket tests — all trace to URLRequest non-blittable marshalling
