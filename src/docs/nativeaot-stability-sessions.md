# NativeAOT Device Stability — Status & Remaining Work

> **Goal**: Zero crashes, all 15 validation libraries passing on NativeAOT device (iPhone 13).
> **Current**: 337 pass, 1 fail, 3 exit-crashes, 0 build-fail (2026-03-15).
> **Target**: 360+ pass, 0 fail, 0 crashes.
> **Validation repo**: `/Users/wojo/Dev/sim-validation/`

## Current Device Results (2026-03-15, commit 5ee25de7)

| Library | Status | Pass | Fail | Notes |
|---------|--------|------|------|-------|
| Kingfisher | success | 33 | 0 | Clean |
| SnapKit | success | 18 | 0 | Clean |
| KeychainAccess | success | 30 | 0 | Fixed: UTF-8 subscript + DateProjection Optional<Date> |
| DeviceKit | success | 26 | 0 | Clean |
| PhoneNumberKit | success | 30 | 0 | Clean |
| ObjectMapper | success | 18 | 0 | Clean |
| SwiftyBeaver | success | 28 | 0 | Clean |
| BonMot | success | 30 | 0 | Clean |
| **RxSwift** | **success** | **31** | **0** | Fixed: DateProjection + @_cdecl Double param |
| **Reachability** | **success** | **15** | **0** | **Fixed: test code → 2-param API** |
| CryptoSwift | failed | 19 | 1 | 1 remaining: IBlockMode protocol param (IExistentialBoxable pending) |
| Alamofire | exited | 28 | 0 | Exit crash after test 28 (SwiftExitGuard in place) |
| Starscream | exited | 10 | 0 | Exit crash after test 10 |
| Swinject | exited | 8 | 0 | Exit crash after test 8 |
| XMLCoder | failed | 13 | 17 | Known limitation: internal protocol types |

**Totals**: 337 pass, 18 fail, 10 success, 3 exit-crash, 1 test-fail, 1 known-limitation

## Completed Work

### Commit 103e8fed — @_cdecl parameter marshalling
- UTF-8 string params in enum case and subscript wrappers (Reachability, KeychainAccess)
- Collection pointer params via DangerousGetHandle (CryptoSwift)
- nint length params for UTF-8 string pointers

### Commits 780b5f84 + 5ee25de7 — DateProjection (Foundation.Date)
- New `DateProjection`: `DateTimeOffset ↔ double` at P/Invoke boundary
- FoundationDatabase.xml: Date maps to `double` (matching 8-byte ABI)
- @_cdecl wrapper: accept `Double`, reconstruct `Date(timeIntervalSinceReferenceDate:)` inside — avoids NSDate ObjC bridging crash
- Full parity: accessor visitors, enum case construction/unmarshalling, protocol proxy receivers, subscript/tuple elements, Optional<Date>
- **Result**: RxSwift 31/31, KeychainAccess 30/30, BlinkID fixed

### IExistentialBoxable — concrete types as protocol parameters

- New `IExistentialBoxable` interface: enables concrete types (e.g., ECB) to be boxed into existential containers when passed where protocol existentials are expected (e.g., `any BlockMode` parameter)
- `ExistentialContainerFactory.GetOrCreate<TProtocol>()`: handles both proxy types (fast path via `ISwiftExistentialConvertible`) and concrete types (runtime boxing via `IExistentialBoxable`)
- Generator emits `IExistentialBoxable` on all types with protocol conformances; `BoxAsExistential1<TProtocol>()` delegates to `ExistentialContainerFactory.Create<ConcreteType, TProtocol>(this)`
- Updated all 11 calling sites: `ExistentialProjection`, `MethodSignature`, `WrapperEmitter.Marshalling`, `EnumHandler.CaseConstruction`, `ClosureEmitter` (4 sites), `ClosureEmitter.StructParams` (2 sites)
- **Gating**: `GetOrCreate` is only used for **single-protocol existentials (EC1)** with a proxy class. Protocol compositions (EC2+), well-known types (AnyError/EC0), and unknown protocols (object) fall back to `ISwiftExistentialConvertible` cast — compositions need multiple witness tables that `GetOrCreate` can't produce, and AnyError is a value type incompatible with the `class` constraint.
- **Result**: Library validation: 0 regressions, 11 libraries improved. CryptoSwift `AES(byte[], ECB, Padding)` still fails at runtime (see Priority 1 below).

### Reachability test code fix

- Updated `sim-validation/Reachability/Program.cs`: `FailedToCreateWithHostname` now takes 2 separate params (string, int) instead of 1 tuple param.
- **Result**: Reachability 15/15 on device.

## Remaining Work

### Priority 1: Exit crashes — Alamofire, Swinject, Starscream (3 libraries, likely 1 root cause)

All three libraries pass every test, then crash during process exit. SwiftExitGuard is implemented (commit bbb552fb) but does NOT prevent these crashes. All three show the same pattern: `swift_release` or `swift_retain` call during GC finalization triggers a Swift deinit that crashes.

**Alamofire**: 28 pass, exit with signal 10 (SIGBUS). Last output: all tests pass, then `App terminated due to signal 10`.
**Swinject**: 8 pass, SIGTRAP during Arc.Release of Assembler.
**Starscream**: 10 pass, SIGSEGV after WebSocketEvent enum tests.

**Why SwiftExitGuard isn't helping**: The guard checks `AppDomain.ProcessExit` and skips `Arc.Release` during finalization. But these crashes may be happening from:
1. **Explicit `Dispose()` via `using var`** — exit guard skips only finalizer-triggered release, NOT explicit dispose. If the test code has `using var session = ...` and dispose runs during scope exit while Swift runtime is partially torn down, it bypasses the guard.
2. **The static constructor for `SwiftExitGuard` may not have fired** — if no `SwiftClassHandle<T>` was accessed before the crash path, the `ProcessExit` handler was never registered.
3. **The crash happens before `ProcessExit` fires** — e.g., during `Environment.Exit()` but before the event is raised.

**Investigation for next session**:
- Attach lldb to device, set breakpoint on `swift_release` / `swift_retain`, check call stack at crash
- Check if crash originates from `ReleaseHandle()` (finalizer path, should be guarded) or from application code (using/dispose path, not guarded)
- If from dispose: consider adding exit guard check to explicit dispose path too, or restructure test code to avoid `using var` for long-lived objects
- If static constructor issue: force-touch `SwiftExitGuard.IsProcessExiting` during app startup

### Priority 2: CryptoSwift protocol parameter (1 remaining failure)

**Status**: IExistentialBoxable fix deployed. Test 11 (`AES(byte[],ECB,Padding)`) fails with `TargetInvocationException`.

**Call chain**: `GetOrCreate<IBlockMode>(ecb)` → `IExistentialBoxable.BoxAsExistential1<IBlockMode>()` → `ExistentialContainerFactory.Create<ECB, IBlockMode>(this)` → `ProtocolWitnessTable.GetOrThrow<ECB, IBlockMode>()` → `ProtocolConformanceDescriptor.TryGet<ECB, IBlockMode>()` → `MakeGenericType(typeof(ProtocolConformanceDescriptorHelper<,>), typeof(ECB), typeof(IBlockMode))`.

**Root cause**: NativeAOT's `MakeGenericType` for `ProtocolConformanceDescriptorHelper<ECB, IBlockMode>` likely fails because this specific generic instantiation was never statically referenced, so NativeAOT didn't generate it. The proxy path (`ProtocolConformanceDescriptorHelper<BlockModeProxy, IBlockMode>`) works because proxy creation references it statically.

**Fix options**:
1. **`[DynamicDependency]` annotations** on `BoxAsExistential1` to hint NativeAOT about needed instantiations — but we can't enumerate all (T, TProtocol) pairs at compile time.
2. **Avoid `MakeGenericType` entirely**: Change `ProtocolConformanceDescriptor.TryGet` to use a non-generic path. The generated type's `GetProtocolConformanceDescriptor<TProtocol>()` just does a dictionary lookup + `LoadFromSymbol`. We could call this directly via interface dispatch instead of reflection.
3. **Generate a static `GetExistentialContainer` per conformance**: Instead of generic `BoxAsExistential1<TProtocol>()`, generate specific methods like `BoxAsBlockMode()` that call `Create<ECB, IBlockMode>` — making the instantiation statically visible to NativeAOT.

Option 2 is cleanest. Add a non-static `TryGetConformanceDescriptor<TProtocol>()` instance method to `ISwiftObject` (or a new interface) that each type implements with its dictionary lookup, avoiding `MakeGenericType`.

### Priority 3: XMLCoder internal types (17 fails) — known limitation

**Status**: @_cdecl wrapper symbols can't compile because they reference `internal` protocol types (Box protocol) not visible outside the XMLCoder module. The 13 passing tests use APIs that don't touch internal types.
**Not fixable** without upstream library changes or falling back to CallConvSwift (which causes NativeAOT marshalling crashes). Same class of issue as SkeletonView and Mixpanel.

## Critical Constraints

- **BitwiseCopyable in Swift 6+**: `storeBytes(of:as:)` requires it. Classes: `Unmanaged.passRetained().toOpaque()`. Structs/enums: `initializeMemory(as:repeating:count:)`.
- **@_cdecl ObjC bridging**: Foundation types (`Date ↔ NSDate`, `String ↔ NSString`) are auto-bridged in @_cdecl. Must use raw types (Double, UnsafePointer<UInt8>+Int) and reconstruct inside wrapper.
- **GetOrCreate EC1-only**: `ExistentialContainerFactory.GetOrCreate` MUST only be used for single-protocol existentials (`ExistentialContainer1`). Compositions (EC2+) return the wrong container size. AnyError (EC0) is a value type incompatible with the `class` constraint. Gate on `containerType == "Swift.Runtime.ExistentialContainer1"` at all call sites. The fully-qualified name is required (not `"ExistentialContainer1"`).
- **NativeAOT `MakeGenericType` limitation**: `ProtocolConformanceDescriptor.TryGet<T,P>` uses `MakeGenericType` internally. On NativeAOT, generic instantiations must be statically reachable. The `IExistentialBoxable` path triggers `TryGet` with runtime types that may not have been AOT-compiled. This is the CryptoSwift Priority 2 blocker.
- **Validation cache**: Run `rm -rf /tmp/binding-validation` before `./validate-libraries.sh` when generator source has changed.
- **Pipe slow commands**: Always `2>&1 | tee /tmp/file.txt`.
- **sim-validation is NOT a git repo** — don't try `git checkout` there.
- **Local NuGet for device tests**: After runtime changes, rebuild the NuGet package (`dotnet pack src/Swift.Runtime/src/Swift.Runtime.csproj -c Release -o /tmp/swift-nuget/ -p:PackageVersion=0.2.0`), copy to `/Users/wojo/Dev/swift-dotnet-packages/local-packages/`, and clear NuGet cache (`dotnet nuget locals all --clear`) before regenerating sim-validation bindings.

## Validation Workflow

```bash
# 1. Build + unit tests
./build.sh && ./run-tests.sh 2>&1 | tee /tmp/test-results.txt

# 2. Library compile gate (90 targets)
rm -rf /tmp/binding-validation && ./validate-libraries.sh 2>&1 | tee /tmp/validation.txt

# 3. Regenerate sim-validation bindings
cd /Users/wojo/Dev/sim-validation && ./regenerate-all.sh 2>&1 | tee /tmp/regen.txt

# 4. Device run (requires connected iPhone, ~15 min)
./run-all-device.sh 2>&1 | tee /tmp/device.txt

# 5. Check specific library
./run-all-device.sh --filter LibraryName 2>&1 | tee /tmp/library-device.txt
```
