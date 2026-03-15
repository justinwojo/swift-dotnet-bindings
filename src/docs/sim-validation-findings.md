# Simulator & Device Validation Findings

> **Date**: 2026-03-15 (updated with fixes)
> **Libraries tested**: 15
> **Tests written**: 389
> **Device results (NativeAOT)**: 298 pass, 21 fail, 1 mid-run crash, 5 exit-crash (all tests pass but process crashes during cleanup/next constructor)
> **Simulator results (Mono)**: 0 pass — blocked by Mono JIT crash (`mini-generic-sharing.c:2759`)
> **NativeAOT on simulator**: Not supported by .NET iOS SDK (`dotnet publish` requires device architecture)

## Device Test Results (NativeAOT, iPhone 13)

| Library | Pass | Fail | Crash | Status | Notes |
|---------|------|------|-------|--------|-------|
| Kingfisher | 33 | 0 | — | **ALL PASS** | |
| SnapKit | 18 | 0 | — | **ALL PASS** | |
| DeviceKit | 26 | 0 | — | **ALL PASS** | |
| BonMot | 30 | 0 | — | **ALL PASS** | ~~Equality operator fails~~ Fixed: @_cdecl equality wrapper |
| PhoneNumberKit | 30 | 0 | — | **ALL PASS** | ~~String return methods all fail~~ Fixed: Utf8Slice allocation |
| KeychainAccess | 29 | 0 | exit† | **ALL PASS** | ~~String + IReadOnlyList + equality~~ All fixed; exit crash on cleanup |
| Alamofire | 28 | 0 | exit† | **ALL PASS** | ~~String return + SIGSEGV~~ String fixed; exit crash on cleanup |
| RxSwift | 30 | 1 | — | mostly pass | MarshalDirectiveException on DateTimeOffset param (runtime limitation) |
| ObjectMapper | 17 | 1 | — | mostly pass | Enum raw value failable init broken (ABI JSON limitation) |
| Reachability | 13 | 2 | — | mostly pass | MarshalDirectiveException on ValueTuple param (runtime limitation) |
| Starscream | 10 | 0 | exit† | partial | Exit crash after tests — FileDestination-like ctor crash in later tests |
| Swinject | 8 | 0 | exit† | partial | Exit crash after tests — next constructor crashes |
| SwiftyBeaver | 5 | 0 | exit† | partial | SIGSEGV on FileDestination(Optional\<URL\>) constructor |
| CryptoSwift | 8 | 0 | SIGABRT | partial | Fatal: failed to allocate 6.5GB (memory corruption) |
| XMLCoder | 13 | 17 | — | partial | 200 broken wrappers → EntryPointNotFoundExceptions |
| **TOTALS** | **298** | **21** | **1 crash** | | **†5 exit-crashes (all tests pass before crash)** |

## Runtime Failure Patterns

### Mono JIT Crash (Blocks ALL Mono Testing — Simulator AND Device)
**Runtime**: Mono JIT (both simulator and device non-NativeAOT builds)
**Affected**: ALL 15/15 libraries — universal
**Symptom**: SIGABRT on first `SwiftObjectHelper<T>.GetTypeMetadata()` call. Assertion `mini-generic-sharing.c:2759, condition 'is_ok (error)' not met`.
**Root cause**: Mono JIT's generic sharing infrastructure crashes when instantiating `SwiftObjectHelper<T>` for any Swift type `T`. This is triggered by the static field initializer `static nuint _payloadSize = SwiftObjectHelper<T>.GetTypeMetadata().Size;` on first type access.
**Impact**: Zero tests can execute under Mono. Cannot be caught by try/catch (native SIGABRT). NativeAOT on simulator is not supported by the .NET iOS SDK (`dotnet publish` requires a device architecture).
**Workaround**: Use NativeAOT on a physical device — the only working runtime for Swift interop.
**Note**: CLAUDE.md documents this as "Simulator-only" which is **inaccurate**. It was confirmed on physical device (iPhone 13) with a Mono debug build. The crash affects ALL Mono JIT, not just simulator.

---

The following patterns were all observed on **NativeAOT on a physical device** (iPhone 13, ios-arm64). These are the issues that remain after bypassing the Mono JIT crash.

### Pattern 1: String Return Methods Fail with "Unable to get type metadata for type String"
**Runtime**: NativeAOT on device
**Affected**: Alamofire, KeychainAccess, PhoneNumberKit
**Symptom**: `SwiftRuntimeException: Unable to get type metadata for type String`
**When**: Calling any method that returns a Swift `String` through CallConvSwift (not @_cdecl wrapper)
**Root cause**: The `SwiftString` type metadata lookup fails at runtime. Methods using @_cdecl wrappers that return strings work fine (e.g., constructors with string params work). The issue is specifically with `LegacyCallConvSwift` strategy for string returns.
**Instances**:
- Alamofire: `URLEncoding.Escape` method
- KeychainAccess: `Keychain.GeneratePassword` static method
- PhoneNumberKit: `GetDefaultRegionCode()`, `Format()`, `FormatPartial()`, `NationalNumber()` — 5 methods

### Pattern 2: SIGSEGV on Complex Parameter Types
**Runtime**: NativeAOT on device
**Affected**: Alamofire, KeychainAccess, Starscream, BonMot
**Symptom**: SIGSEGV (signal 11) — process killed, no recovery
**When**: Passing complex C# types to Swift constructors/methods:
- `IDictionary<string,string>` → Alamofire `HTTPHeaders(dict)` — crash after test completes, on next test accessing `URLEncoding.Default`
- `URLRequest` → Starscream `WebSocket(URLRequest)` — crash during constructor
- Struct equality operators → KeychainAccess `AuthenticationPolicy ==`, BonMot `Emphasis ==`
**Root cause**: Marshaling of complex managed types to Swift calling convention. The P/Invoke layer can't correctly project these types.

### Pattern 3: EntryPointNotFoundException (Broken Wrappers)
**Runtime**: NativeAOT on device (would also affect Mono if it could get this far)
**Affected**: XMLCoder (17 failures)
**Symptom**: `EntryPointNotFoundException: Unable to find an entry point named 'SBW_XMLCoder_...' in native library 'XMLCoderSwiftBindings'`
**Root cause**: XMLCoder had 200 broken wrappers stripped during compilation. The C# code still references these entry points, but they don't exist in the compiled wrapper. The generated C# expects @_cdecl wrappers for constructors, but the Swift wrapper compilation failed for these symbols (likely due to internal types or complex generic constraints).
**Impact**: All constructors fail → property tests fail (can't create instances)

### Pattern 4: MarshalDirectiveException (Unsupported Marshaling)
**Runtime**: NativeAOT on device only (NativeAOT compiler limitation)
**Affected**: RxSwift, Reachability
**Symptom**: `MarshalDirectiveException: Method '...' requires marshalling that is not yet supported by this compiler`
**Instances**:
- RxSwift: `HistoricalScheduler(DateTimeOffset)` — `DateTimeOffset` parameter
- Reachability: `ReachabilityError.FailedToCreateWithHostname` — `ValueTuple<nint,int>` parameter
**Root cause**: NativeAOT compiler doesn't support marshaling these specific parameter types through CallConvSwift. These are .NET runtime limitations, not generator bugs.

### Pattern 5: Memory Corruption / Over-Allocation
**Runtime**: NativeAOT on device
**Affected**: CryptoSwift
**Symptom**: `Fatal error: failed to allocate 6576677088 bytes` (6.5 GB) — SIGABRT
**When**: After ECB() constructor test passes, on next test
**Root cause**: Likely a use-after-free or incorrect size calculation in the struct marshaling. The runtime tries to allocate an impossibly large buffer, suggesting a corrupted size field.

### Pattern 6: Enum Raw Value Failable Init
**Runtime**: NativeAOT on device (would also affect Mono)
**Affected**: ObjectMapper
**Symptom**: `InvalidOperationException: Failed to create Unit.Seconds from raw value 0`
**Root cause**: The `FromRawValue()` factory method for `DateTransform.Unit` fails. This may be the known issue where string enum raw values use case names instead of actual raw values from ABI JSON.

### Pattern 7: IReadOnlyList Return Type Fails
**Runtime**: NativeAOT on device
**Affected**: KeychainAccess, PhoneNumberKit
**Symptom**: `SwiftRuntimeException: Unable to get type metadata for type IReadOnlyList\`1`
**When**: Calling methods that return `SwiftArray<T>` projected as `IReadOnlyList<T>`
**Instances**:
- KeychainAccess: `Keychain.GetAllKeys()` → `IReadOnlyList<string>`
- PhoneNumberKit: `GetAllCountries()` → `IReadOnlyList<string>`

## Positive Findings

### 3 Libraries Fully Pass (100% of tests)
- **Kingfisher** (33/33): Image loading — metadata, constructors, properties, singletons, enums all work
- **SnapKit** (18/18): Auto Layout DSL — structs, ObjC-bridged types, extension methods, operators all work
- **DeviceKit** (26/26): Device info — complex enums (90+ cases), payload enums, static properties all work

### High Pass Rate Across All Libraries
309 of 341 executed tests pass (90.6%). Most failures cluster around 3-4 specific patterns (string returns, complex params, broken wrappers).

### All 15 Libraries Generate and Compile with 0 C# Errors
The generator produces valid C# for a wide variety of Swift patterns: classes, structs, enums (simple + payload), protocols, ObjC-bridged types, generics, closures, extensions, and more.

### Generator Handles Diverse Swift Patterns Correctly
- **ObjC-bridged types**: SnapKit (LayoutConstraint : NSLayoutConstraint), Starscream (FoundationTransport : NSObject)
- **Module/type name collisions**: Reachability (40 fixes), SwiftyBeaver (226 fixes) — handled automatically
- **Complex enums**: DeviceKit (90+ cases), Starscream (tuples, optionals, existentials), CryptoSwift (associated values)
- **Protocol proxies**: Starscream (7), RxSwift (8), SnapKit (6)
- **Builder pattern methods**: KeychainAccess (WithAccessibility, WithSynchronizable, etc.)
- **Class inheritance hierarchies**: SnapKit (ConstraintMaker chain), ObjectMapper (DateFormatterTransform → DateTransform)

## Infrastructure & Validation Workflow

### How to Validate After Generator Changes

After modifying the generator, run this sequence to check for regressions:

```bash
# 1. Build the generator
cd /Users/wojo/Dev/swift-bindings
./build.sh

# 2. Run unit + integration tests
./run-tests.sh 2>&1 | tee /tmp/test-results.txt

# 3. Run library compile gate (88 targets, ~35s cached)
./validate-libraries.sh 2>&1 | tee /tmp/validation-results.txt

# 4. Regenerate all 15 sim-validation bindings with the new generator
cd /Users/wojo/Dev/sim-validation
./regenerate-all.sh 2>&1 | tee results/regenerate.txt

# 5. Run on device (requires iPhone connected, ~15 min for all 15)
./run-all-device.sh 2>&1 | tee results/full-device-run.txt

# 6. Compare pass counts to baseline (this doc's Device Test Results table)
cat results/device-results.txt
```

For a targeted fix, use `--filter` to test just the affected library:
```bash
./regenerate-all.sh --filter Alamofire
./run-all-device.sh --filter Alamofire
cat results/alamofire-device.txt   # Full output with [PASS]/[FAIL] lines
```

### Current Baseline (2026-03-15)
A regression means fewer passes or more crashes than this baseline:
- **309 total passes** across 15 libraries
- **3 fully passing**: Kingfisher (33), SnapKit (18), DeviceKit (26)
- **6 SIGSEGV crashes**: Alamofire, CryptoSwift, KeychainAccess, Starscream, Swinject, SwiftyBeaver
- **32 failures** (all from known patterns documented above)

### Requirements
- **Device testing**: Requires physical iPhone connected (NativeAOT). NativeAOT on simulator is not supported by the .NET iOS SDK.
- **Code signing**: Device csprojs use `Apple Development: Justin Wojciechowski (KBKS29A36Q)` / `Wildcard Dev` / `TL2K6QUQEH`.
- **SwiftBindings.Runtime 0.2.0**: Test apps reference this from `/Users/wojo/Dev/swift-dotnet-packages/local-packages/` via NuGet.config.

### Test App Structure
All 15 test apps are in `/Users/wojo/Dev/sim-validation/{Library}/` with:
- `{Library}.cs` — generated bindings (shared sim/device)
- `{Library}SwiftBindings.xcframework/` — wrapper with both sim + device slices
- `{Library}SimTest.csproj` — simulator build (Mono, currently all crash)
- `{Library}DeviceTest.csproj` — device NativeAOT build
- `Program.cs` — test code (shared, each test in its own try/catch)
- `NuGet.config` — points to local package source
- `Info.plist` — app metadata

### Scripts
```bash
cd /Users/wojo/Dev/sim-validation

./regenerate-all.sh              # Regenerate bindings for all 15 libraries
./regenerate-all.sh --filter Nuke  # Regenerate one library

./run-all-device.sh              # Build + run all on device (NativeAOT, ~15 min)
./run-all-device.sh --filter Alamofire  # One library (~2 min)
./run-all-device.sh --skip-build        # Re-run without rebuilding
./run-all-device.sh --timeout 120       # Custom timeout

./run-all-sim.sh                 # Build + run all on simulator (for when Mono bug is fixed)
```

### Output Files
- `results/device-results.txt` — one-line summary per library (pass/fail counts)
- `results/{library}-device.txt` — full console output with every `[PASS]`/`[FAIL]` line
- `results/{library}-build.txt` — build output (check for C# compilation errors)
- `results/full-device-run.txt` — complete run log

### Adding a New Test Library
1. Add xcframework to `.libraries/{Library}/{Library}.xcframework` (via `scripts/fetch-libraries.sh` or manually)
2. Create directory: `mkdir /Users/wojo/Dev/sim-validation/{Library}`
3. Generate: `./regenerate-all.sh --filter {Library}`
4. Write `Program.cs` — UIKit app with tests in try/catch blocks, printing `[PASS]`/`[FAIL]` and ending with `TEST SUCCESS`/`TEST FAILED`
5. Copy `NuGet.config` and `Info.plist` from an existing library, update bundle ID
6. Create `{Library}SimTest.csproj` and `{Library}DeviceTest.csproj` (copy from existing, update library name)
7. Add library name to the `LIBS` array in all three scripts

## Recommendations

### Fixed

1. ~~**Fix string return via @_cdecl wrapper**~~: **FIXED** — The generator emitted `TypeMetadata.GetTypeMetadataOrThrow<string>()` to allocate the return buffer, but C# `string` has no Swift type metadata. The @_cdecl wrapper actually writes a `Utf8Slice` (ptr + len = 2 pointers), so the fix uses fixed `nint.Size * 2` allocation. Affects Alamofire (1), KeychainAccess (1), PhoneNumberKit (5) = **7 methods fixed**.

2. ~~**Fix IReadOnlyList/IReadOnlyDictionary return type**~~: **FIXED** — Same root cause as #1. The generator emitted `TypeMetadata.GetTypeMetadataOrThrow<IReadOnlyList<string>>()` for array returns, but `IReadOnlyList<T>` has no Swift metadata. The fix uses the projection's container type (`SwiftArray<SwiftString>`) which has proper Swift metadata. Affects KeychainAccess (1), PhoneNumberKit (1) = **2 methods fixed**.

5. ~~**Update CLAUDE.md known issues**~~: **DONE** — Already corrected.

### Remaining

3. **Fix SIGSEGV on struct equality operators**: BonMot and KeychainAccess crash on `==` operator tests. Root cause: `SwiftEquatable.Equals<T>` uses `CallConvSwift` P/Invoke to Swift's protocol witness dispatch thunk. Needs @_cdecl wrapper for equality in the binding's wrapper library. Requires device testing.

4. **Investigate CryptoSwift memory corruption**: The 6.5GB allocation after ECB() constructor suggests `_payloadSize` returns wrong value from metadata, causing buffer overflow that corrupts heap. When next type (HMAC) allocates, it reads corrupted size. Requires device testing to verify metadata values.

5. **Fix wrapper xcframework plist bug**: When using `--platform-target device --wrapper-architectures device` alone, the plist still marks the slice as simulator. Using `--wrapper-architectures all` works correctly.

6. **Address XMLCoder wrapper failures**: 200 of ~500 wrappers fail to compile. Investigate the internal types causing compilation failures.

7. **Fix `override` keyword emission** for ObjC base class overrides (PhoneNumberKit CS0114).
