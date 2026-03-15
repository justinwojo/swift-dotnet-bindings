# NativeAOT Device Stability — Session Plan

> **Goal**: Zero crashes, all 15 validation libraries passing on NativeAOT device (iPhone 13).
> **Baseline**: 298 pass, 21 fail, 1 mid-run crash, 5 exit-crashes (commit dc06e216).
> **Target**: 360+ pass, 0 fail, 0 crashes.
> **Validation repo**: `/Users/wojo/Dev/sim-validation/`

## Context

We validated 15 real-world Swift libraries on a physical iPhone 13 via NativeAOT. Three root causes remain:
1. **CallConvSwift crashes** — methods/equality/marshaling that still use CallConvSwift instead of @_cdecl wrappers
2. **Constructor parameter marshaling** — Optional<URL>, complex types passed incorrectly to Swift
3. **ARC release deinit crashes** — `swift_release` triggers Swift deinitializers that crash during GC finalization

Each session below targets one category of failures. Sessions are independent and can be executed in any order (no dependencies between them), but each must be committed and validated before the next starts.

## Critical Constraints

- **BitwiseCopyable in Swift 6+**: `storeBytes(of:as:)` requires it. Classes: `Unmanaged.passRetained().toOpaque()`. Structs/enums: `initializeMemory(as:repeating:count:)`.
- **ModuleEmissionContext threading**: ALL code paths creating emitters MUST pass `context.GetEmissionContext()` to avoid dedup failures.
- **Validation cache**: Run `rm -rf /tmp/binding-validation` before `./validate-libraries.sh` when generator source has changed.
- **Pipe slow commands**: `./run-tests.sh 2>&1 | tee /tmp/session-N-tests.txt` — never re-run just to see output.
- **Never use git stash** — linter hooks detect reverted files.
- **Test files by domain** — tests go in their respective domain test files, not session-specific files.
- **sim-validation is NOT a git repo** — don't try `git checkout` there.

## Validation Workflow

After each session's generator changes:
```bash
# 1. Build + unit tests
./build.sh && ./run-tests.sh 2>&1 | tee /tmp/session-N-tests.txt

# 2. Library compile gate (90 targets)
rm -rf /tmp/binding-validation && ./validate-libraries.sh 2>&1 | tee /tmp/session-N-validation.txt

# 3. Regenerate sim-validation bindings
cd /Users/wojo/Dev/sim-validation && ./regenerate-all.sh 2>&1 | tee /tmp/session-N-regen.txt

# 4. Device run (requires connected iPhone, ~15 min)
./run-all-device.sh 2>&1 | tee /tmp/session-N-device.txt

# 5. Check specific library
./run-all-device.sh --filter LibraryName 2>&1 | tee /tmp/session-N-library.txt
```

---

## Session Plan

### Session 1: Class Equality @_cdecl Wrappers

**Status**: Not started
**Scope**: Emit @_cdecl equality wrappers for Swift **class** types (not just structs).
**Blocked tests**: SwiftyBeaver `BaseDestination equality` (1 failure), plus any class Equatable across all 15 libraries.

**Problem**: `ClassEqualityMethodsWriter` in `ClassHandler.cs` still uses `SwiftEquatable.Equals()` which calls `CallConvSwift` P/Invoke → crashes on NativeAOT. Struct equality was fixed in commit dc06e216, but class equality was deferred because `_handle` is a private field inaccessible from derived classes.

**Fix strategy**: Add a `protected internal` handle accessor method to generated root classes, then use it in equality operators. The accessor should be emitted by `ClassISwiftObjectMethodWriter` alongside the existing `GetTypeMetadata()` implementation.

**Deliverables**:
1. Emit `internal IntPtr GetSwiftHandle() => _handle.DangerousGetHandle();` on root classes (non-derived) in ClassHandler
2. Update `ClassEqualityMethodsWriter` to accept SwiftWriter + context (same pattern as struct equality)
3. Emit @_cdecl Swift equality wrapper for classes using `Unmanaged<T>.fromOpaque(ptr).takeUnretainedValue()`
4. In C# equality, use `left.GetSwiftHandle()` / `right.GetSwiftHandle()` instead of `_handle.DangerousGetHandle()`
5. For derived classes, also use `GetSwiftHandle()` (inherited from base, accessible via `internal`)

**Key files**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ClassHandler.cs` — ClassEqualityMethodsWriter + ClassISwiftObjectMethodWriter
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/TypeHandlerHelpersTests.cs` — add class equality tests

**Validation**:
- `./run-tests.sh` — 0 failures
- `./validate-libraries.sh` — 90/90 pass, 0 regressions
- Device: SwiftyBeaver `BaseDestination equality` test passes (was FAIL)

---

### Session 2: Exit Crash Prevention (ARC Release Deinit Guard)

**Status**: Not started
**Scope**: Prevent process crashes during GC finalization when `swift_release` triggers a Swift deinitializer that crashes.
**Affected**: Alamofire, KeychainAccess, Starscream, Swinject, SwiftyBeaver — all crash during process exit after tests pass.

**Problem**: When a `SwiftClassHandle<T>` is collected by the GC finalizer, `ReleaseHandle()` calls `Arc.Release(handle)` → `swift_release` → Swift deinit runs → crashes (SIGSEGV/SIGTRAP/SIGBUS). The deinit code in third-party libraries (SwiftyBeaver's BaseDestination, Swinject's Container, etc.) accesses resources that are invalid during process teardown.

**Fix strategy**: In `SwiftClassHandle<T>.ReleaseHandle()`, detect whether we're in process exit (GC finalization on shutdown) and skip `swift_release` in that case. The memory will be freed when the process exits anyway. This is the standard pattern for COM release in .NET (see `Marshal.ReleaseComObject` behavior during AppDomain unload).

**Deliverables**:
1. Add a static `AppDomain.ProcessExit` handler in `SwiftClassHandle<T>` (or a shared helper) that sets a `volatile bool s_processExiting` flag
2. In `ReleaseHandle()`, check the flag: if process is exiting AND we're on the finalizer thread (not explicit Dispose), skip `swift_release` and just null out the handle
3. Explicit `Dispose()` calls (`using var`) should still release — only suppress during finalization on exit
4. Add unit test verifying the flag is set on ProcessExit

**Key files**:
- `src/Swift.Runtime/src/Swift/Runtime/SwiftClassHandle.cs` — ReleaseHandle, ProcessExit handler
- `src/Swift.Runtime/tests/` — add test for ProcessExit flag behavior

**Validation**:
- `./run-tests.sh` — 0 failures
- Device: Alamofire, KeychainAccess should exit cleanly (status "success" not "exited")
- Note: SwiftyBeaver/Swinject/Starscream may still "exit" due to mid-test crashes (not exit crashes)

---

### Session 3: Swinject & Starscream Constructor Crashes

**Status**: Not started
**Scope**: Investigate and fix the constructor crashes that block 38 tests across Swinject (20 blocked) and Starscream (18 blocked).
**Prerequisites**: Sessions 1-2 help but aren't required.

**Problem**: After the initial batch of tests pass, the next constructor call crashes the process. These are NOT Optional<URL> truncation (that was fixed in the large-Optional change). Need to identify what specific pattern causes each crash.

**Investigation steps** (do these FIRST before implementing fixes):
1. Read `/Users/wojo/Dev/sim-validation/Swinject/Program.cs` — identify exactly which test crashes (add diagnostic prints between tests if needed)
2. Read the generated bindings (`Swinject/Swinject.cs`) for the crashing constructor — check parameter types, marshaling, P/Invoke signature
3. Check if the Swift @_cdecl wrapper exists and is correct (`Swinject/Swinject.swift`)
4. Same for Starscream — read Program.cs, bindings, and wrapper
5. Compare with working constructors in the same library to identify the difference

**Common crash patterns to check**:
- Optional parameters with large inner types (should be caught by IsLargeOptionalParam now)
- Closure parameters in constructors
- Protocol existential parameters
- Default parameter values not being passed correctly
- Missing @_cdecl wrapper (fallback to CallConvSwift)

**Deliverables**:
1. Root cause analysis for each crash (document in commit message)
2. Generator fix for each pattern found
3. Tests reproducing the pattern
4. Both libraries should have significantly more tests passing

**Key files**: Generator files depend on root cause. Start by reading:
- `/Users/wojo/Dev/sim-validation/Swinject/Program.cs` — test code
- `/Users/wojo/Dev/sim-validation/Swinject/Swinject.cs` — generated bindings
- `/Users/wojo/Dev/sim-validation/Starscream/Program.cs`
- `/Users/wojo/Dev/sim-validation/Starscream/Starscream.cs`

**Validation**:
- `./run-tests.sh` — 0 failures
- `./validate-libraries.sh` — 90/90 pass
- Device: Swinject and Starscream pass counts increase significantly

---

### Session 4: MarshalDirectiveException — @_cdecl Wrappers for DateTimeOffset & ValueTuple

**Status**: Not started
**Scope**: Fix 3 test failures from NativeAOT MarshalDirectiveException by routing affected methods through @_cdecl wrappers.
**Affected**: RxSwift (1 fail), Reachability (2 fails).

**Problem**: NativeAOT compiler rejects `DateTimeOffset` and `ValueTuple<nint,int>` parameters through `CallConvSwift`. Error: `MarshalDirectiveException: Method '...' requires marshalling that is not yet supported by this compiler`. These methods currently fall back to `LegacyCallConvSwift` because they don't have @_cdecl wrappers.

**Root cause**: The `MethodWrapperEmitter.ShouldEmitWrapper()` guard checks reject these methods for wrapping. Need to identify WHY they're rejected and fix the guard or add special handling.

**Investigation steps**:
1. Read the generated bindings for the failing methods:
   - RxSwift: `HistoricalScheduler(DateTimeOffset)` — find in `/Users/wojo/Dev/sim-validation/RxSwift/RxSwift.cs`
   - Reachability: `ReachabilityError.FailedToCreateWithHostname` — find in `/Users/wojo/Dev/sim-validation/Reachability/Reachability.cs`
2. Check if these methods have `[Obsolete(DiagnosticId = "SB0001")]` (which means no @_cdecl wrapper)
3. In `MethodWrapperEmitter.ShouldEmitWrapper()`, trace why these specific methods are rejected
4. Check if DateTimeOffset maps to a Swift type that can be handled in @_cdecl

**Deliverables**:
1. Fix wrapper emission for the specific patterns causing rejection
2. OR: if the types genuinely can't be wrapped, emit a safe fallback that avoids CallConvSwift (e.g., manual marshaling through IntPtr)
3. Tests for the fixed patterns

**Key files**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/MethodWrapperEmitter.cs` — ShouldEmitWrapper guards
- `src/Swift.Bindings/src/Emitter/StringEmitter/WrapperValidation.cs` — wrapper eligibility checks

**Validation**:
- `./run-tests.sh` — 0 failures
- `./validate-libraries.sh` — 90/90 pass
- Device: RxSwift 31/31, Reachability 15/15

---

### Session 5: CryptoSwift Memory Corruption

**Status**: Not started
**Scope**: Fix the SIGABRT crash in CryptoSwift caused by a 6.5GB allocation attempt (memory corruption).
**Affected**: CryptoSwift — 8 tests pass, then SIGABRT on next allocation.

**Problem**: After `ECB()` constructor test passes, the next test (HMAC) crashes with `Fatal error: failed to allocate 6576677088 bytes`. This suggests the ECB constructor overflows its allocated buffer (wrong `_payloadSize`), corrupting heap metadata, causing the next allocation to read a garbage size.

**Investigation steps**:
1. Read `/Users/wojo/Dev/sim-validation/CryptoSwift/CryptoSwift.cs` — find the `ECB` struct/class definition
2. Check `_payloadSize` initialization: `static nuint _payloadSize = SwiftObjectHelper<ECB>.GetTypeMetadata().Size;`
3. Check the metadata accessor wrapper: `SBW_GetMetadata_CryptoSwift_CryptoSwift_ECB_HASH` in the Swift wrapper
4. Compare metadata size with the actual Swift struct layout (check ABI JSON for field count/types)
5. Check if HMAC has similar issues or if it's just collateral damage from ECB overflow
6. Look at the test order in `/Users/wojo/Dev/sim-validation/CryptoSwift/Program.cs` — which test actually crashes

**Possible causes**:
- Metadata accessor returns wrong type metadata (e.g., for a different type)
- ECB is a protocol or generic type where metadata resolution gives wrong size
- The @_cdecl init wrapper writes more bytes than `_payloadSize` allocates
- Swift struct layout differs from what the metadata reports

**Deliverables**:
1. Root cause identified and documented
2. Generator fix if it's a code generation issue
3. If it's a library-specific ABI anomaly, add a guard to prevent the heap corruption (e.g., validate _payloadSize > 0 and < reasonable max)

**Key files**: Depends on root cause. Start by reading:
- `/Users/wojo/Dev/sim-validation/CryptoSwift/CryptoSwift.cs`
- `/Users/wojo/Dev/sim-validation/CryptoSwift/CryptoSwift.swift` (wrapper)
- `/Users/wojo/Dev/sim-validation/CryptoSwift/Program.cs`

**Validation**:
- Device: CryptoSwift passes more tests without SIGABRT

---

### Session 6: XMLCoder Broken Wrappers

**Status**: Not started
**Scope**: Fix 17 EntryPointNotFoundException failures in XMLCoder caused by 200 Swift wrappers that fail to compile.
**Affected**: XMLCoder — 13 pass, 17 fail.

**Problem**: The Swift wrapper file has ~500 @_cdecl functions, but ~200 fail to compile. The C# bindings reference these entry points, but they don't exist in the compiled wrapper. Likely caused by internal types or complex generic constraints in XMLCoder's API that the Swift compiler rejects.

**Investigation steps**:
1. Regenerate XMLCoder bindings and capture the wrapper compilation output:
   ```bash
   dotnet run --project src/Swift.Bindings/src -- --xcframework .libraries/XMLCoder/XMLCoder.xcframework -o /tmp/xmlcoder-check/ 2>&1 | tee /tmp/xmlcoder-gen.txt
   ```
2. Try to compile the Swift wrapper manually and capture ALL errors:
   ```bash
   # Check the wrapper compilation output in the validation log
   ./validate-libraries.sh --filter XMLCoder --verbose 2>&1 | tee /tmp/xmlcoder-verbose.txt
   ```
3. Categorize the wrapper compilation errors — are they all the same pattern?
4. Check if the failing wrappers reference internal types, generic constraints, or missing imports

**Possible fixes**:
- If wrappers reference internal types: add guards in MethodWrapperEmitter to skip these
- If it's missing imports: add import statements to the Swift wrapper preamble
- If it's generic constraints: adjust the wrapper emission to handle the constraints

**Deliverables**:
1. Categorize wrapper compilation errors
2. Fix the most common pattern(s) to reduce the 200 broken wrappers
3. Fewer EntryPointNotFoundExceptions in device testing

**Key files**: Depends on root cause. Start by examining:
- The Swift wrapper compilation errors (from validation --verbose output)
- `src/Swift.Bindings/src/Emitter/StringEmitter/MethodWrapperEmitter.cs` — wrapper guards

**Validation**:
- `./validate-libraries.sh --filter XMLCoder` — Swift wrapper passes or fewer failures
- Device: XMLCoder pass count increases

---

### Session 7: ObjectMapper Enum Raw Value

**Status**: Not started
**Scope**: Fix the `FromRawValue()` failure for `DateTransform.Unit` enum in ObjectMapper.
**Affected**: ObjectMapper — 17 pass, 1 fail.

**Problem**: `InvalidOperationException: Failed to create Unit.Seconds from raw value 0`. The generator creates `FromRawValue()` factory methods for enums, but string enum raw values use case names instead of actual raw values because ABI JSON doesn't include raw value data.

**Investigation steps**:
1. Read `/Users/wojo/Dev/sim-validation/ObjectMapper/ObjectMapper.cs` — find `DateTransform.Unit` enum
2. Check the `FromRawValue()` implementation — what raw value is it expecting?
3. Check the ABI JSON for the enum — confirm raw values are missing
4. Look at the `.swiftinterface` file for the library — do raw values appear there?
5. Check if the generator already parses swiftinterface files for any purpose

**Possible fixes**:
- Parse raw values from `.swiftinterface` files (they contain `case seconds = "seconds"` etc.)
- Use the Swift type's `init(rawValue:)` via @_cdecl wrapper instead of a generated lookup table
- If it's an integer enum (not string), the raw values might be inferrable from case order

**Deliverables**:
1. Root cause confirmed
2. Fix implemented (preferably the @_cdecl `init(rawValue:)` approach as it's most general)
3. ObjectMapper enum test passes

**Validation**:
- Device: ObjectMapper 18/18

---

## Final Validation

After all sessions complete:
```bash
cd /Users/wojo/Dev/swift-bindings
./run-tests.sh 2>&1 | tee /tmp/final-tests.txt
rm -rf /tmp/binding-validation && ./validate-libraries.sh 2>&1 | tee /tmp/final-validation.txt
cd /Users/wojo/Dev/sim-validation
./regenerate-all.sh 2>&1 | tee /tmp/final-regen.txt
./run-all-device.sh 2>&1 | tee /tmp/final-device.txt
```

**Target**: All 15 libraries show "success" status, 0 failures, 0 crashes.

---

## Session 4 Progress — @_cdecl Parameter Marshalling Fixes

**Baseline**: 329 pass, 7 clean, 8 failing (commit bbb552fb).

### Completed Fixes

**1. UTF-8 String params in @_cdecl enum case wrappers (Reachability)**
- Root cause: `EnumHandler.CaseConstruction.cs` passed `SwiftString.Buffer` (a struct) to @_cdecl P/Invoke. NativeAOT rejects non-blittable struct params in P/Invoke.
- Swift side: `GetCdeclParamMapping` used two-Int buffer halves (`unsafeBitCast`) which didn't match.
- Fix: Added `useUtf8Strings` flag to `GetCdeclParamMapping`. Enum case wrappers and subscript wrappers pass `true` → C# sends UTF-8 ptr+len, Swift reconstructs via `String(bytes:encoding:)`.
- Files: `ConstructorWrapperEmitter.cs`, `EnumHandler.CaseConstruction.cs`, `EnumCaseWrapperEmitter.cs`

**2. UTF-8 String params in @_cdecl subscript wrappers (KeychainAccess)**
- Root cause: `SubscriptWrapperEmitter` called `GetCdeclParamMapping` without `useUtf8Strings`. Swift wrapper expected two-Int but C# `SubscriptHandler` already sent UTF-8 ptr+len.
- Fix: Pass `useUtf8Strings: true` from `SubscriptWrapperEmitter`.
- Files: `SubscriptWrapperEmitter.cs`

### Investigated — Needs Implementation

**3. DateTimeOffset in @_cdecl constructor wrappers (RxSwift — 1 fail)**
- `HistoricalScheduler(DateTimeOffset)`: `System.DateTimeOffset` is not blittable, NativeAOT rejects it.
- Swift `Foundation.Date` is a `Double` wrapper. @_cdecl expects `Date` (= Double in registers).
- Fix needed: New `DateProjection` that converts DateTimeOffset → double (seconds since 2001-01-01).
- Complexity: Requires new ITypeProjection, PInvokeEmitter case, and body marshalling changes.

**4. Optional<Class> in @_cdecl constructor wrappers (Swinject — SIGTRAP)**
- `Assembler(Container?)`: C# creates `SwiftOptional<IntPtr>` buffer and passes the buffer pointer. But @_cdecl wrapper expects `UnsafeMutableRawPointer?` (nullable pointer, not buffer pointer).
- For None: buffer pointer is NON-null (it's a valid buffer), Swift interprets as Some → SIGTRAP.
- Fix needed: For @_cdecl + Optional<Class>, pass IntPtr directly (0 for nil, handle for value). Requires PInvokeEmitter + body marshalling changes.

**5. Wrong overload mapping (Alamofire — SIGSEGV)**
- `HTTPHeaders(IDictionary<string,string>)`: @_cdecl Swift wrapper loads param as `Array<HTTPHeader>` but C# passes `SwiftDictionary<SwiftString, SwiftString>`.
- The constructor wrapper mapped to the wrong overload (Array-based instead of Dictionary-based).
- Fix needed: Investigate how `ConstructorWrapperEmitter` resolves overloads for init methods.

**6. Metadata size corruption (CryptoSwift — SIGABRT 6.5GB alloc)**
- After ECB() constructor succeeds, next test triggers 6.5GB allocation. Wrong metadata size being used for a struct/enum.
- Fix needed: More investigation into which type's metadata is corrupted.

**7. SIGSEGV after enum tests (Starscream)**
- After WebSocketEvent.Cancelled succeeds, next constructor crashes.
- Fix needed: More investigation into the specific crashing constructor.

**8. Wrapper compilation failure (XMLCoder — EntryPointNotFoundException)**
- @_cdecl wrapper symbols not found in compiled wrapper. Internal protocol types (Box protocol) can't be accessed from wrapper module.
- This is a **known limitation** (same class as SkeletonView/Mixpanel internal types).

### Validation
- Unit tests: 7577/7577 pass, 1 skipped
- Library validation: 90/90 compile (no regressions)
- No runtime changes needed for the completed fixes
