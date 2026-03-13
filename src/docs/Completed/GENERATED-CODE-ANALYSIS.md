# Generated Code Static Analysis — Local Generator (2026-03-13)

Analysis of generated Swift wrappers and C# bindings from the local generator across **10 libraries** (two rounds of 5). Checks for correctness issues independent of whether the wrapper compiles.

**Generator source**: `/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/` (main branch, commit 29662011)
**Reference**: Prior SDK 0.2.0 analysis at `/Users/wojo/Dev/swift-dotnet-packages/SDK-0.2.0-GENERATED-CODE-ANALYSIS.md`

---

## Libraries Analyzed

| Library | Tier | Swift Lines | C# Lines | P/Invokes | @_cdecl Wrappers | Wrapper Compiled |
|---------|------|------------|----------|-----------|-----------------|-----------------|
| Alamofire | 1 | 7,027 | 50,924 | 971 (719 wrapper / 251 direct) | 1,167 | No (46 errors) |
| CryptoSwift | 1 | 4,130 | 24,576 | 557 (463 wrapper / 94 direct) | 680 | No (83 errors) |
| RxSwift | 1 | 3,099 | 18,991 | 380 (309 wrapper / 71 direct) | 215 | No (31 errors) |
| DeviceKit | 2 | 927 | 6,137 | 105 (97 wrapper / 8 direct) | 196 | **Yes** |
| Swinject | 2 | 884 | 5,112 | 84 (74 wrapper / 10 direct) | 111 | No (9 errors) |

**Round 1 Totals**: 16,067 Swift lines, 105,740 C# lines, 2,097 P/Invokes, 2,369 @_cdecl wrappers

### Round 2

| Library | Tier | Swift Lines | C# Lines | P/Invokes | @_cdecl Wrappers | Wrapper Compiled |
|---------|------|------------|----------|-----------|-----------------|-----------------|
| Kingfisher | 1 | 9,280 | 55,535 | 919 (763 wrapper / 155 direct) | 1,281 | No (74 errors) |
| GRDB | 1 | 15,469 | 92,548 | 1,791 (1,468 wrapper / 316 direct) | 2,148 | No (246 errors) |
| PhoneNumberKit | 2 | 2,708 | 15,792 | 367 (239 wrapper / 121 direct) | 417 | No (16 errors) |
| XMLCoder | 2 | 2,803 | 22,196 | 482 (341 wrapper / 129 direct) | 577 | No (18 errors) |
| Parchment | 2 | 1,793 | 17,533 | 394 (153 wrapper / 232 direct) | 265 | No (7 errors) |

**Round 2 Totals**: 32,053 Swift lines, 203,604 C# lines, 3,953 P/Invokes, 4,688 @_cdecl wrappers

**Grand Totals**: 48,120 Swift lines, 309,344 C# lines, 6,050 P/Invokes, 7,057 @_cdecl wrappers across 10 libraries

---

## Summary of All Issues Found

| ID | Category | Severity | Libraries | Count | Known? | Generator Fix Location |
|----|----------|----------|-----------|-------|--------|----------------------|
| S1 | Swift keyword as parameter name | Critical | Alamofire | 3 | **NEW** | EnumCaseWrapperEmitter — keyword escaping |
| S2 | Bare `Type` for metatype parameters | Critical | Swinject | 9 | **NEW** | MethodWrapperEmitter — metatype projection |
| S3 | Malformed closure parameter in @_silgen_name | Critical | RxSwift, GRDB | 42 | **NEW** | ClosureHandler — generic closure signature |
| S4 | Internal member access in wrapper | High | CryptoSwift | 83+ | **NEW** | MemberEmissionValidator — access level gate |
| S5 | Orphaned `_dbw_` extension calls | High | Alamofire, Kingfisher, GRDB, PhoneNumberKit, XMLCoder | 50+ | Confirms H | DefaultParameterOverloadEmitter |
| S6 | `try!` force-try in closure adapters | Medium | RxSwift, GRDB | 119 | **NEW** | ClosureHandler — error propagation |
| S7 | Missing @MainActor on @_cdecl wrappers | Critical | Kingfisher | 53 | **NEW** | WrapperEmitter — actor annotation propagation |
| S8 | Enum case tuple → multi-arg mismatch | Critical | Parchment | 3 | **NEW** | EnumCaseWrapperEmitter — multi-param destructuring |
| S9 | `inout` parameter loaded as `let` | Critical | GRDB | 10 | **NEW** | MethodWrapperEmitter — mutability annotation |
| S10 | `Unmanaged` used on struct type | Critical | Kingfisher | 3 | **NEW** | PropertyHandler — struct vs class detection |
| S11 | Duplicate parameter name in function | Critical | Kingfisher | 2 | **NEW** | Async wrapper emitter — parameter dedup |
| S12 | Internal enum case access (`_convertTo*`) | High | XMLCoder | 8 | **NEW** | EnumHandler — underscore-prefix internal cases |
| S13 | Duplicate `_sbw_emptyBuffer` declaration | Low | Kingfisher | 1 | **NEW** | Utf8SliceEmitter — dedup guard |
| P1 | NativeMemory leak (empty finally) | High | All 10 | 167 | Confirms P | PropertyHandler / MethodHandler — missing Free |
| P2 | NativeMemory leak (no finally at all) | High | Alamofire | 19+ | Confirms P | MethodHandler — SwiftIndirectResult path |
| N1 | Double P/Invoke getter | High | Alamofire | 15 | Confirms N | PropertyHandler — ObjC optional path |
| K1 | GCHandle freed in finally (escaping closure) | Critical | Alamofire, Swinject, RxSwift, Kingfisher, GRDB, PhoneNumberKit, XMLCoder | 224+ | Confirms K | ClosureHandler — escaping lifetime |
| R1 | Orphaned C# proxy classes (no Swift EveryProtocol) | High | 8 of 10 libraries | 68 | Confirms R | ProtocolProxyEmitter — conformance gate |
| I1 | Stdlib type name collision | Medium | Alamofire | 2 | **NEW** | WrapperEmitter — fully-qualified type names |
| I2 | Scope-invisible free functions | Medium | CryptoSwift | 10 | **NEW** | MemberEmissionValidator — module-level scope |
| I3 | Constructor missing required params | Medium | PhoneNumberKit | 3 | **NEW** | ConstructorWrapperEmitter — param validation |
| I4 | Utf8Slice string buffer leak | Medium | DeviceKit | 1 | **NEW** | Utf8SliceEmitter — double alloc cleanup |
| I5 | Incomplete vtable initialization | High | Swinject | 1 | **NEW** | ProtocolProxyEmitter — empty vtable body |

---

## Issue S1: Swift Keyword Used as Parameter Name (NEW)

**Severity**: Critical — Swift compilation error
**Libraries**: Alamofire (3 instances)
**Root cause**: Enum case labels that are Swift keywords (`in`, `for`) are emitted as bare parameter names in `@_cdecl` wrapper functions.

### Instances

**Instance 1** — `Alamofire.swift:1826` (keyword: `in`)
```swift
@_cdecl("SBW_Alamofire_MultipartEncodingFailureReason_bodyPartFilenameInvalid_55C2DDCB")
public func _sbw_case_bodyPartFilenameInvalid_F5FA74B1(_ in: UnsafeRawPointer, _ resultPtr: UnsafeMutableRawPointer) {
    let inVal = in.load(as: URL.self)  // ERROR: 'in' is a keyword
```

**Instance 2** — `Alamofire.swift:1862` (keyword: `for`)
```swift
public func _sbw_case_bodyPartInputStreamCreationFailed_7752F73A(_ for: UnsafeRawPointer, _ resultPtr: UnsafeMutableRawPointer) {
    let forVal = for.load(as: URL.self)  // ERROR: 'for' is a keyword
```

**Instance 3** — `Alamofire.swift:1871` (keyword: `for`)
```swift
public func _sbw_case_outputStreamCreationFailed_44C79595(_ for: UnsafeRawPointer, _ resultPtr: UnsafeMutableRawPointer) {
    let forVal = for.load(as: URL.self)  // ERROR: 'for' is a keyword
```

### Suggested Fix

In `ConstructorWrapperEmitter.cs` or `EnumHandler.CaseConstruction.cs`, when the enum case label is a Swift keyword, either:
1. Backtick-escape it: `` `in` `` (Swift's keyword escaping mechanism), or
2. Use a generated name (e.g., `p_in`, `arg0`) as done for Issue E's fix.

The variable reference (`inVal = in.load(...)`) would also need backtick-escaping: `` `in`.load(as:) ``

---

## Issue S2: Bare `Type` for Metatype Parameters (NEW)

**Severity**: Critical — Swift compilation error ("cannot find type 'Type' in scope")
**Libraries**: Swinject (9 instances)
**Root cause**: Swift metatype parameters (`Any.Type` / `T.Type`) are projected as bare `Type`, which doesn't exist in Swift.

### Instances

**Instance 1** — `Swinject.swift:588`
```swift
public func _sbw_method_299012F8(_ objectScope: UnsafeRawPointer, _ serviceType: UnsafeRawPointer, _ self_: UnsafeMutableRawPointer) {
    let serviceTypeVal: Type = serviceType.load(as: Type.self)  // ERROR: cannot find type 'Type'
    let obj = Unmanaged<Swinject.Container>.fromOpaque(self_).takeUnretainedValue()
    obj.resetObjectScope(objectScopeVal, serviceType: serviceTypeVal)
```

Additional instances at lines 598, 839, 853 and in the corresponding property declarations.

### Suggested Fix

In the marshaler or wrapper emitter, metatype parameters (`Any.Type`, `T.Type`) should be projected as `Any.Type` or the appropriate generic metatype. The `UnsafeRawPointer` → `Any.Type` marshaling path needs to use `unsafeBitCast(serviceType.load(as: UnsafeMutableRawPointer.self), to: Any.Type.self)`.

---

## Issue S3: Malformed Closure Parameter in @_silgen_name Functions (NEW)

**Severity**: Critical — Swift compilation error (multiple parse errors)
**Libraries**: RxSwift (14 instances)
**Root cause**: When a method takes a closure parameter with generic types, the closure's type annotation gets spliced into the function parameter list as a raw string, breaking the parameter name/type separation.

### Instances

**Example** — `RxSwift.swift:2743`
```swift
@_silgen_name("SBW_AsyncSubject_map_closure")
public func SBW_AsyncSubject_map_closure<Element, Result>(
    _ self_: UnsafeMutableRawPointer,
    _ element) throws -> ResultFuncPtr: UnsafeMutableRawPointer,  // MALFORMED
    _ element) throws -> ResultContext: UnsafeMutableRawPointer?,  // MALFORMED
    _ __elementType: Element.Type,
    _ __resultType: Result.Type
) -> UnsafeMutableRawPointer {
```

**What it should be**: The closure function pointer and context should be separate parameters:
```swift
_ closureFuncPtr: UnsafeMutableRawPointer,
_ closureContext: UnsafeMutableRawPointer?,
```

The `) throws -> Result` type signature leaked into the parameter name. This affects all 7 `AsyncSubject` map/filter variants (14 total occurrences including body references).

### Suggested Fix

In `ClosureHandler` or the closure adapter emitter, the function type `(Element) throws -> Result` is being stringified and concatenated into the parameter name instead of being replaced with the decomposed (FuncPtr, Context) pair.

---

## Issue S4: Internal Member Access in Wrapper (NEW)

**Severity**: High — Swift compilation error ("inaccessible due to 'internal' protection level")
**Libraries**: CryptoSwift (83+ errors across 42 distinct members)
**Root cause**: Generator emits `@_cdecl` wrappers for properties/methods with `internal` access level, which aren't accessible from the wrapper module.

### Representative Instances

```
'accumulated' is inaccessible due to 'internal' protection level (16 errors)
'processedBytesTotalCount' is inaccessible due to 'internal' protection level (8 errors)
'blockSize' is inaccessible due to 'internal' protection level (6 errors)
'worker' is inaccessible due to 'internal' protection level (4 errors)
```

42 distinct internal members accessed, totaling 83+ errors.

### Suggested Fix

`MemberEmissionValidator` should gate on effective access level ≥ `public`. The ABI JSON may include internal members; the generator needs to check access control before emitting wrappers. This may already be partially gated but failing for protocol requirement members that are semantically internal.

---

## Issue S5: Orphaned `_dbw_` Extension Method Calls (Confirms Issue H)

**Severity**: High — Swift compilation error ("has no member '_dbw_...'")
**Libraries**: Alamofire (28 instances)
**Root cause**: `@_cdecl` wrappers call `_dbw_` extension methods (default parameter overloads), but the corresponding extension definitions are missing—likely stripped during an optimization pass or never emitted.

### Representative Instances

```
value of type 'Session' has no member '_dbw_upload_CFC2B21F_3'
value of type 'Session' has no member '_dbw_upload_CFC2B21F_2'
value of type 'DownloadRequest' has no member '_dbw_serializingData_37670BBA_4'
value of type 'DataRequest' has no member '_dbw_publishData_43CCDE55_4'
value of type 'DataStreamRequest' has no member '_dbw_publishData_B8E2EAC4_1'
value of type 'Request' has no member '_dbw_authenticate_AF347B9D_1'
```

Types affected: `Session` (upload/streamRequest/cancelAllRequests), `DownloadRequest`, `DataRequest`, `DataStreamRequest`, `Request`.

### Analysis

This confirms Issue H from the prior analysis (seen in Nuke). The default parameter overload emitter generates `@_cdecl` callers that reference `_dbw_` extension methods, but those extension blocks are either:
1. Not emitted when the method requires unsupported types in its full signature
2. Stripped during a post-emission pass

---

## Issue S6: `try!` Force-Try in Closure Adapters (NEW)

**Severity**: Medium — Runtime crash on error (no compilation error)
**Libraries**: RxSwift (63 instances)
**Root cause**: Closure adapters for methods that take throwing closures use `try!` instead of proper error propagation, causing a fatal error if the closure throws.

### Pattern

```swift
// In closure adapter for .filter(), .map(), etc.
let __closure: (Element) throws -> Bool = { __arg0 in
    // ... marshal to C, call cdecl callback ...
    return cdecl(__buf0, enabledContext)
}
let result = try! instance.filter(__closure)  // CRASH if closure throws
return Unmanaged.passRetained(result).toOpaque()
```

### Suggested Fix

The `@_cdecl` wrapper should propagate errors through the error-out parameter mechanism rather than force-trying. When a throwing closure's error reaches the `@_cdecl` boundary, it should be stored in an error out-pointer for the C# side to handle.

---

## Issue P1: NativeMemory Leak — Empty `finally` Block (Confirms Issue P)

**Severity**: High — Memory leak (unrecoverable in native interop)
**Libraries**: All 5 (Alamofire: 24, CryptoSwift: 3, RxSwift: 6, DeviceKit: 4, Swinject: 4 = **41 total**)
**Root cause**: `NativeMemory.Alloc` for indirect result buffers followed by a `try`/`finally` block where the `finally` is empty.

### Pattern

```csharp
// DeviceKit.cs:5634 — typical instance
var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<DeviceKit.Device.ApplePencilSupport>();
var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
var swiftIndirectResult = new SwiftIndirectResult(payload);

PInvoke_firstGeneration_Get_A4DDD0F1(swiftIndirectResult);

return SwiftMarshal.MarshalFromSwift<DeviceKit.Device.ApplePencilSupport>(
    new IntPtr(swiftIndirectResult.Value));

finally
{
    // EMPTY — payload is never freed!
}
```

### Instances by Library

| Library | Leaking Alloc+Empty Finally | Example Lines |
|---------|---------------------------|---------------|
| Alamofire | 24 | 1403, 3271, 12410, 12798, 15968, 17026, 18377, ... |
| CryptoSwift | 3 | 11885, 12643, 12687 |
| RxSwift | 6 | 11074 + 5 others |
| DeviceKit | 4 | 5634, 5667, 5700, 5733 |
| Swinject | 4 | 3514, 3734, 4087, 4336 |

### Suggested Fix

The `finally` block must include `NativeMemory.Free(payload)`. The generator's indirect result path in `PropertyHandler` and `MethodHandler` emits the `try`/`finally` scaffolding but doesn't populate the `finally` body for `SwiftIndirectResult` returns.

---

## Issue P2: NativeMemory Leak — Missing `finally` Block Entirely (Confirms Issue P)

**Severity**: High — Memory leak
**Libraries**: Alamofire (19+ instances)
**Root cause**: Methods with `SwiftIndirectResult` that also need `_payload.DangerousRelease()` have a `finally` that only releases the SafeHandle ref but forgets to free the result buffer.

### Pattern

```csharp
// Alamofire.cs:1403
var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);  // ALLOCATED
var swiftIndirectResult = new SwiftIndirectResult(payload);
// ... use swiftIndirectResult in P/Invoke ...
return ((Swift.URL)SwiftMarshal.MarshalFromSwift<Swift.URL>(
    new IntPtr(swiftIndirectResult.Value))).ToNSUrl();

finally
{
    if (success)
       _payload.DangerousRelease();  // Only releases SafeHandle ref
    // payload (NativeMemory) is NEVER freed!
}
```

---

## Issue N1: Double P/Invoke Getter (Confirms Issue N)

**Severity**: High — ARC reference leak (getter called twice, first return value leaked)
**Libraries**: Alamofire (15 instances)
**Root cause**: Optional ObjC property getters use the pattern `Getter() == IntPtr.Zero ? null : Getter()` which calls the P/Invoke twice.

### Pattern

```csharp
// Alamofire.cs:3479
public Foundation.NSHttpUrlResponse? Response
{
    get => (Response_Get() == IntPtr.Zero ? null :
            ObjCRuntime.Runtime.GetNSObject<Foundation.NSHttpUrlResponse>(Response_Get()));
    //       ^^^^^^^^^^^^^^ first call             ^^^^^^^^^^^^^^ second call — first return leaked!
}
```

### All 15 Instances in Alamofire

| Line | Property | Type |
|------|----------|------|
| 3479 | Response | NSHttpUrlResponse? |
| 3560 | Metrics | NSUrlSessionTaskMetrics? |
| 4043 | Response | NSHttpUrlResponse? |
| 4164 | Metrics | NSUrlSessionTaskMetrics? |
| 26196 | Credential | NSUrlCredential? |
| 26437 | Response | NSHttpUrlResponse? |
| 26473 | FirstTask | NSUrlSessionTask? |
| 26509 | LastTask | NSUrlSessionTask? |
| 26545 | Task | NSUrlSessionTask? |
| 26581 | FirstMetrics | NSUrlSessionTaskMetrics? |
| 26617 | LastMetrics | NSUrlSessionTaskMetrics? |
| 26653 | Metrics | NSUrlSessionTaskMetrics? |
| 32205 | Response | NSHttpUrlResponse? |
| 32241 | Metrics | NSUrlSessionTaskMetrics? |
| 32544 | (return value) | NSInputStream? |

### Suggested Fix

Call the getter once, store in a local, then check:
```csharp
get {
    var ptr = Response_Get();
    return ptr == IntPtr.Zero ? null : ObjCRuntime.Runtime.GetNSObject<Foundation.NSHttpUrlResponse>(ptr);
}
```

---

## Issue K1: GCHandle Freed Before Escaping Callback (Confirms Issue K)

**Severity**: Critical — Use-after-free crash when Swift calls the callback
**Libraries**: Alamofire (15+), Swinject (4), RxSwift (1) = **20+ instances**

### Pattern

```csharp
// Alamofire.cs:15442-15460
GCHandle completionHandle = default;
try
{
    completionHandle = GCHandle.Alloc(completion);  // ALLOCATED
    var completionClosure = new SwiftClosureData(
        (IntPtr)s_task_completion_Callback, GCHandle.ToIntPtr(completionHandle));
    PInvoke_task(taskHandle, request.Payload, responseHandle, completionClosure, self);
    return;
}
finally
{
    if (completionHandle.IsAllocated) completionHandle.Free();  // FREED — but Swift may call it later!
}
```

The closure parameter is passed to Swift as an escaping callback. If Swift stores the closure and calls it asynchronously (e.g., on network response), the `GCHandle` is already freed. The callback's context pointer becomes dangling.

### Representative Instances

**Alamofire**: `completionHandle` (lines 15460, 22421, 22835, 23965, 24190, 24255, 24316, 24480, 24569, 25261), `shouldCompressBodyDataHandle` (21438), `closureHandle` (27282, 27341), `encodingHandle` (18572, 18833)

**Swinject**: `registeringClosureHandle` (709), `storageFactoryHandle` (2786, 2824, 2864)

**RxSwift**: `disposeHandle` (12946)

### Suggested Fix

For escaping closure parameters, the `GCHandle` should be freed by the **callback itself** (on final invocation or via a separate dealloc callback), not in the calling method's `finally` block. The `ClosureHandler` needs to distinguish escaping vs non-escaping closures.

---

## Issue R1: Orphaned C# Protocol Proxy Classes (Confirms Issue R)

**Severity**: High — Runtime crash when attempting to use protocol proxies
**Libraries**: Alamofire (6), CryptoSwift (7), RxSwift (7), Swinject (3) = **23 orphaned proxies**

### Description

C# emits `*Proxy` classes with `ISwiftExistentialConvertible` implementations that reference vtable setup and witness table P/Invokes, but no corresponding `EveryProtocol` conformance exists in the Swift wrapper. At runtime, the vtable/witness-table calls would fail.

### Inventory

| Library | Paired | Orphaned | Orphaned Proxy Names |
|---------|--------|----------|---------------------|
| Alamofire | 14 | 6 | DataDecoder, EmptyResponse, Error, EventMonitor, ParameterEncoder, UploadConvertible |
| CryptoSwift | 13 | 7 | AEAD, BinaryFloatingPoint, Collection, CryptorAndUpdatable, FixedWidthInteger, StreamModeWorker, _UInt8Type |
| RxSwift | 2 | 7 | AsyncSequence, ConnectableObservableType, DataDecoder, ImmediateSchedulerType, InfallibleType, ObservableType, SchedulerType |
| Swinject | 4 | 3 | Behavior, Resolver, _Resolver |
| DeviceKit | 0 | 0 | (none) |

Note: Some orphans may be due to associated type requirements, generic constraints, or other protocol features that prevent EveryProtocol conformance. The C# side should not emit proxy classes when the Swift side cannot conform.

---

## Issue I1: Standard Library Type Name Collision (NEW)

**Severity**: Medium — Swift compilation error
**Libraries**: Alamofire (2 instances)
**Root cause**: The generator emits bare type names (`Empty`, `Sequence`) that collide with Swift standard library types.

### Instances

**`Alamofire.swift:1356`** — `Empty` (collides with `Swift.Empty`?)
```swift
resultPtr.initializeMemory(as: Empty.self, repeating: result, count: 1)
// Should be: Alamofire.Empty.self
```

**Wrapper compilation error** — `Sequence` type used where `Alamofire.Sequence` (or parameter) expected:
```
error: type 'Sequence' has no member 'load'
```

### Suggested Fix

Always use fully module-qualified type names in the Swift wrapper (e.g., `Alamofire.Empty`, `Alamofire.DecodableResponseSerializer`) to avoid collisions with stdlib types.

---

## Issue I2: Scope-Invisible Free Functions (NEW)

**Severity**: Medium — Swift compilation error ("cannot find '...' in scope")
**Libraries**: CryptoSwift (10 instances: `rotateLeft` ×4, `rotateRight` ×3, `strideCount` ×1, `reversed` ×2)
**Root cause**: Module-internal free functions are emitted as `@_cdecl` wrappers but aren't accessible from the wrapper's compilation unit.

### Pattern

```swift
// CryptoSwift.swift:971-973
@_cdecl("SBW_CryptoSwift_Free_strideCount_6E4686EF")
public func _sbw_method_335DC791(_ from: Int, _ to: Int, _ by: Int) -> Int {
    return strideCount(from: from, to: to, by: by)  // ERROR: cannot find 'strideCount' in scope
}
```

These functions are `internal` in CryptoSwift and not visible outside the module.

---

## Compilation Error Summary

### Alamofire (46 errors)

| Error Category | Count | Root Issue |
|---------------|-------|------------|
| Missing `_dbw_` extension methods | 28 | Issue S5/H |
| Swift keyword parameter names | 3 | Issue S1 |
| `expected initial value after '='` | 4 | Parse errors from keyword params |
| `Sequence` type collision | 2 | Issue I1 |
| Other parse errors | 9 | Various |

### CryptoSwift (83 errors)

| Error Category | Count | Root Issue |
|---------------|-------|------------|
| Internal member access | 83 | Issue S4 |
| Scope-invisible free functions | 10 | Issue I2 |
| `@_cdecl` parameter type incompatible | 4 | Generic type erasure |

### RxSwift (31 errors)

| Error Category | Count | Root Issue |
|---------------|-------|------------|
| Malformed closure parameters | 16 | Issue S3 |
| Parse errors from malformed params | 14 | Cascading from S3 |
| Non-conforming argument type | 1 | Protocol existential |

### Swinject (9 errors)

| Error Category | Count | Root Issue |
|---------------|-------|------------|
| Bare `Type` not in scope | 9 | Issue S2 |

### DeviceKit (0 errors) — Clean compilation

---

## Cross-Cutting Analysis

### NativeMemory Alloc/Free Balance

| Library | Alloc | Free | SafeHandle-owned | Leaking (empty finally) | Leaking (missing free in finally) |
|---------|-------|------|-----------------|----------------------|--------------------------------|
| Alamofire | 397 | 182 | ~172 | 24 | 19+ |
| CryptoSwift | 188 | 50 | ~115 | 3 | 20+ |
| RxSwift | 123 | 72 | ~35 | 6 | 10+ |
| DeviceKit | 126 | 23 | ~99 | 4 | 0 |
| Swinject | 44 | 14 | ~18 | 4 | 8+ |

Note: Many `Alloc` calls without explicit `Free` are for enum case construction where `SwiftSafeHandle` owns the memory — this is correct. The "Leaking" columns are confirmed cases with no ownership transfer.

### P/Invoke Library Routing

All 5 libraries route correctly:
- `SBW_*` entry points → `{Module}SwiftBindings` (wrapper library) ✅
- `$s*` mangled symbols → `{Module}` (original library) ✅
- `GetTypeMetadata` → wrapper library ✅
- No misrouted symbols found

### Bool Marshaling

All P/Invoke `bool` parameters have `[MarshalAs(UnmanagedType.U1)]` ✅

### `@MainActor` / Actor Isolation

No `@MainActor` annotations found in any of the 5 libraries analyzed. These libraries don't use actor isolation in their public APIs, so Issue F (custom actor) was not triggered in this sample.

---

## Comparison with Prior SDK 0.2.0 Analysis

| Issue | Prior Analysis (Nuke/Lottie/BlinkID/Stripe) | This Analysis (5 new libraries) | Status |
|-------|---------------------------------------------|--------------------------------|--------|
| E — `_ _:` parameter | Lottie (3) | **Not found** | Possibly fixed or not triggered |
| F — Custom actor missing | BlinkID (2) | Not triggered (no actor APIs) | Untestable |
| G — `@_spi` access | StripeFinConn (2) | Not found | Untestable (no @_spi APIs) |
| H — Orphaned `_dbw_` | Nuke (4+) | **Alamofire (28)** — Confirmed, widespread | Still present |
| K — GCHandle early free | Lottie (22+) | **Alamofire (15+), Swinject (4), RxSwift (1)** | Still present |
| L — AsyncStream wrong lib | Nuke (3) | Not found | Possibly fixed or not triggered |
| M — Async singleton | Nuke (4) | Not found | Possibly fixed or not triggered |
| N — Double P/Invoke getter | Nuke (3+) | **Alamofire (15)** | Still present |
| O — Sync calling async | BlinkID (1) | Not found | Untestable |
| P — NativeMemory leak | BlinkID (14) | **All 5 libraries (41+)** | Still present, widespread |
| Q — OptBuf ordering | StripeCore (1) | Not found | Possibly fixed |
| R — Missing proxy impls | StripeCore (5) | **4 libraries (23 proxies)** | Still present, widespread |

### New Issues Not in Prior Analysis

| ID | Description | Severity |
|----|-------------|----------|
| S1 | Swift keyword as parameter name | Critical |
| S2 | Bare `Type` for metatype | Critical |
| S3 | Malformed closure parameter signatures | Critical |
| S4 | Internal member access in wrapper | High |
| S6 | `try!` force-try in closure adapters | Medium |
| I1 | Stdlib type name collision | Medium |
| I2 | Scope-invisible free functions | Medium |

---

## Priority Ranking for Fixes

### Critical (compilation blockers affecting multiple libraries)

1. **S4 — Internal member access gate** (CryptoSwift: 83 errors) — Add access level check in `MemberEmissionValidator`. Highest error count.
2. **S5/H — Orphaned `_dbw_` calls** (Alamofire: 28 errors) — Ensure extension definitions are emitted when callers are emitted, or suppress callers when extensions are suppressed.
3. **S3 — Malformed closure parameter names** (RxSwift: 14 functions) — Fix closure type stringification in `ClosureHandler` for generic throwing closures.
4. **S2 — Bare `Type` metatype** (Swinject: 9 errors) — Project metatype parameters as `Any.Type` instead of bare `Type`.
5. **S1 — Swift keyword parameter names** (Alamofire: 3 errors) — Backtick-escape keywords in `EnumCaseWrapperEmitter`.

### High (runtime correctness)

6. **K1 — GCHandle early free** (20+ instances) — Critical runtime crash. Escaping closures must not free GCHandle in `finally`.
7. **P1/P2 — NativeMemory leaks** (41+ instances) — Add `NativeMemory.Free(payload)` to all `SwiftIndirectResult` finally blocks.
8. **N1 — Double P/Invoke getter** (15 instances) — Store getter result in local before null check.
9. **R1 — Orphaned proxy classes** (23 proxies) — Don't emit C# proxy when Swift EveryProtocol conformance can't be generated.

### Medium (correctness/quality)

10. **S6 — `try!` in closure adapters** (119 instances) — Propagate errors through error-out pointer.
11. **I1/I2 — Type name collisions and scope** (12 instances) — Use fully-qualified names; gate on access level for free functions.

---

# Round 2: Kingfisher, GRDB, PhoneNumberKit, XMLCoder, Parchment

## Issue S7: Missing @MainActor on @_cdecl Wrappers (NEW)

**Severity**: Critical — Swift concurrency error ("call to main actor-isolated instance method in a synchronous nonisolated context")
**Libraries**: Kingfisher (53 instances)
**Root cause**: The generator annotates `@MainActor` on some `@_cdecl`/`@_silgen_name` wrappers (9 found with annotation) but misses others (53 missing) for `AnimatedImageView`, `Animator`, `KFAnimatedImage`, and other `@MainActor`-isolated types.

### Pattern

```swift
// CORRECT (line 1940):
@MainActor
@_silgen_name("SBW_Get_Kingfisher_AnimatedImageView_image")
public func _sbw_get_image_3F4AE92B(_ self_: Kingfisher.AnimatedImageView) -> Optional<UIImage> { ... }

// MISSING (line 2005):
// No @MainActor annotation!
@_cdecl("SBW_Get_Kingfisher_AnimatedImageView_Animator_maxRepeatCount")
public func _sbw_get_maxRepeatCount_E10EC03A(...) { ... }
```

Types with missing annotations: `AnimatedImageView` (constructors, metadata, nested types), `AnimatedImageView.Animator` (all properties/methods), `KFAnimatedImage` (40+ option-setter methods), `SessionDelegate`.

### Suggested Fix

The actor annotation propagation in `WrapperEmitter` needs to apply `@MainActor` to ALL wrappers that access `@MainActor`-isolated types, not just property overrides for ObjC base classes. This was previously seen only in the BlinkID custom actor case (Issue F) but is now a systemic issue for UIKit-heavy libraries.

---

## Issue S8: Enum Case Tuple → Multi-Argument Mismatch (NEW)

**Severity**: Critical — Swift compilation error ("enum case expects N separate arguments")
**Libraries**: Parchment (3 instances)
**Root cause**: Enum cases with multiple labeled parameters (e.g., `.fixed(width: CGFloat, height: CGFloat)`) are deserialized as a single tuple parameter. The wrapper loads the entire tuple as one value and passes it as a single argument.

### Instances

**`Parchment.swift:1325-1327`**
```swift
public func _sbw_case_fixed_FC692AB0(_ width: UnsafeRawPointer, _ resultPtr: UnsafeMutableRawPointer) {
    let widthVal = width.load(as: (width: CGFloat, height: CGFloat).self)
    let result = Parchment.PagingMenuItemSize.fixed(width: widthVal)
    //          ERROR: enum case 'fixed' expects 2 separate arguments
```

**Should be**:
```swift
let result = Parchment.PagingMenuItemSize.fixed(width: widthVal.width, height: widthVal.height)
```

Also affects `.selfSizing(estimatedWidth:height:)` and `.sizeToFit(minWidth:height:)`.

### Suggested Fix

In `EnumHandler.CaseConstruction.cs`, when an enum case has multiple labeled parameters, destructure the loaded tuple into individual arguments. The current code passes the entire tuple as the first argument.

---

## Issue S9: `inout` Parameter Loaded as `let` (NEW)

**Severity**: Critical — Swift compilation error ("cannot pass immutable value as inout argument")
**Libraries**: GRDB (10 instances)
**Root cause**: When a method takes an `inout` parameter, the wrapper loads it with `let` binding, making it immutable. Swift requires `var` for `inout` arguments.

### Pattern

```swift
// GRDB.swift line ~803
let streamVal = stream.load(as: DumpStream.self)  // 'let' makes it immutable
self_.assumingMemoryBound(to: GRDB.ListDumpFormat.self)
    .pointee.writeRow(dbVal, statement: statementVal, to: streamVal)
    //  ERROR: cannot pass immutable value as inout argument: 'streamVal' is a 'let' constant
```

**Should be**: `var streamVal = stream.load(as: DumpStream.self)`

### Suggested Fix

In `MethodWrapperEmitter`, check if the original parameter is `inout` and emit `var` instead of `let` for the loaded value.

---

## Issue S10: `Unmanaged` Used on Struct Type (NEW)

**Severity**: Critical — Swift compilation error ("generic struct 'Unmanaged' requires that T be a class type")
**Libraries**: Kingfisher (3 instances)
**Root cause**: `Unmanaged.passRetained()` is used to return a `PHPickerResult`, which is a struct, not a class.

### Instance

```swift
// Kingfisher.swift:5797
let obj = self_.assumingMemoryBound(to: Kingfisher.PHPickerResultImageDataProvider.self).pointee
return Unmanaged.passRetained(obj.pickerResult).toOpaque()
// ERROR: generic struct 'Unmanaged' requires that 'PHPickerResult' be a class type
```

### Suggested Fix

The property handler's return marshaling should check whether the return type is a value type (struct) and use `initializeMemory` + indirect result pointer instead of `Unmanaged.passRetained()`.

---

## Issue S11: Duplicate Parameter Name in Function (NEW)

**Severity**: Critical — Swift compilation error ("invalid redeclaration")
**Libraries**: Kingfisher (2 instances)
**Root cause**: Async wrapper functions have both a task ID parameter and a task object parameter, both named `task`.

### Instance

```swift
// Kingfisher.swift:6114
public func PInvoke_urlSession_93141891(
    _ callback: ..., _ errorCallback: ...,
    _ task: Int64,           // task ID (async context)
    _ session: UnsafeMutableRawPointer,
    _ task: UnsafeMutableRawPointer,  // task object — ERROR: redeclaration!
    _ challenge: UnsafeMutableRawPointer,
    _ _self: UnsafeMutableRawPointer
) { ... }
```

### Suggested Fix

The async wrapper emitter should deduplicate parameter names by appending a suffix (e.g., `task_0`, `task_1` or `taskId`, `taskObj`).

---

## Issue S12: Internal Enum Case Access via Underscore Prefix (NEW)

**Severity**: High — Swift compilation error ("has no member")
**Libraries**: XMLCoder (8 instances)
**Root cause**: Enum cases prefixed with `_` (e.g., `_convertToSnakeCase`, `_convertFromCapitalized`) are internal by convention and not accessible from the wrapper module.

### Instances

```
type 'XMLEncoder.KeyEncodingStrategy' has no member '_convertToUppercased'
type 'XMLEncoder.KeyEncodingStrategy' has no member '_convertToSnakeCase'
type 'XMLEncoder.KeyEncodingStrategy' has no member '_convertToLowercased'
type 'XMLEncoder.KeyEncodingStrategy' has no member '_convertToKebabCase'
type 'XMLEncoder.KeyEncodingStrategy' has no member '_convertToCapitalized'
type 'XMLDecoder.KeyDecodingStrategy' has no member '_convertFromCapitalized'
type 'XMLDecoder.KeyDecodingStrategy' has no member '_convertFromKebabCase'
type 'XMLDecoder.KeyDecodingStrategy' has no member '_convertFromSnakeCase'
```

### Suggested Fix

Underscore-prefixed enum cases that are not public should be filtered by `MemberEmissionValidator`. Note: the existing underscore-prefix suppression rule has an exception for structurally-required types, but individual enum *cases* within a public type need separate handling.

---

## Issue I3: Constructor Missing Required Parameters (NEW)

**Severity**: Medium — Swift compilation error ("missing arguments for parameters")
**Libraries**: PhoneNumberKit (3 instances)
**Root cause**: Constructor wrappers emit calls with fewer arguments than the actual initializer requires. Default parameter overloads may not account for all required parameters.

### Instance

```swift
let result = PhoneNumberKit.CountryCodePickerViewController(style: styleVal)
// ERROR: missing arguments for parameters 'utility', 'options' in call
```

The actual initializer requires `style:`, `utility:`, and `options:` but the wrapper only passes `style:`.

---

## Round 2 Compilation Error Summary

### Kingfisher (74 errors)

| Error Category | Count | Root Issue |
|---------------|-------|------------|
| Malformed closure params (parse errors) | 33 | S3 variant |
| `Unmanaged` on struct type | 3 | S10 |
| Duplicate parameter name | 2 | S11 |
| `UInt64` vs `UInt` type mismatch | 2 | Type projection |
| Other | 34 | Various cascading |

### GRDB (246 errors)

| Error Category | Count | Root Issue |
|---------------|-------|------------|
| Malformed closure params (parse errors) | 133 | S3 |
| `inout` parameter as `let` | 10 | S9 |
| Missing `_dbw_` extension methods | 15+ | S5/H |
| Missing argument labels | 6 | Constructor param emission |
| Other | 82 | Various cascading |

### PhoneNumberKit (16 errors)

| Error Category | Count | Root Issue |
|---------------|-------|------------|
| Missing `_dbw_` extension methods | 6 | S5/H |
| Missing constructor params | 5 | I3 |
| `Int64` vs `Int` type mismatch | 2 | Type projection |
| Other | 3 | Various |

### XMLCoder (18 errors)

| Error Category | Count | Root Issue |
|---------------|-------|------------|
| Internal member access (`_convert*`) | 8 | S12 |
| Missing internal methods (`toXML`, `isEmpty`) | 4 | S4 variant |
| Other parse errors | 6 | Various |

### Parchment (7 errors)

| Error Category | Count | Root Issue |
|---------------|-------|------------|
| Enum case tuple → multi-arg | 3 | S8 |
| Tuple type → CGFloat conversion | 4 | S8 related (indicator options) |

---

## Round 2 Cross-Cutting Analysis

### NativeMemory Alloc/Free Balance (Round 2)

| Library | Alloc | Free | Delta | Leaking (empty finally) |
|---------|-------|------|-------|----------------------|
| Kingfisher | 430 | 292 | 138 | 13 |
| GRDB | 838 | 546 | 292 | 63 |
| PhoneNumberKit | 158 | 103 | 55 | 18 |
| XMLCoder | 205 | 94 | 111 | 11 |
| Parchment | 85 | 38 | 47 | 21 |

### P/Invoke Library Routing (Round 2)

All 5 libraries route correctly ✅ — no SBW_ symbols targeting the original library.

### GCHandle.Free in Finally (Round 2)

| Library | GCHandle.Free Calls |
|---------|-------------------|
| Kingfisher | 105 |
| GRDB | 97 |
| PhoneNumberKit | 1 |
| XMLCoder | 1 |
| Parchment | 0 |

### Protocol Proxy Pairing (Round 2)

| Library | C# Proxies | Swift Conformances | Orphaned |
|---------|-----------|-------------------|----------|
| Kingfisher | 25 | 20 | 5 (ImageFrameSource, KFOptionSetter, KingfisherCompatible, KingfisherCompatibleValue, OptionalProtocol) |
| GRDB | 46 | 22 | 24 (AggregatingRequest, AssociationToMany/One, Collection, DatabaseCursor/Reader/Writer, DerivableRequest, EncodableRecord, ...) |
| PhoneNumberKit | 4 | 2 | 2 (CountryCodePickerSectionHeaderViewProtocol, CountryCodePickerTableViewCellProtocol) |
| XMLCoder | 13 | 0 | 13 (AnyOptional, Box, DynamicNodeDecoding/Encoding, SimpleBox, StringProtocol, ...) |
| Parchment | 11 | 10 | 1 (View) |

### Double P/Invoke Getter (Round 2)

No double-getter instances found in round 2 libraries (all use intermediate variable pattern) ✅

---

## Combined Statistics (All 10 Libraries)

| Metric | Round 1 | Round 2 | Total |
|--------|---------|---------|-------|
| Swift wrapper lines | 16,067 | 32,053 | 48,120 |
| C# binding lines | 105,740 | 203,604 | 309,344 |
| P/Invoke declarations | 2,097 | 3,953 | 6,050 |
| @_cdecl wrappers | 2,369 | 4,688 | 7,057 |
| Wrapper compilation errors | 169 | 361 | 530 |
| Clean compilations | 1 (DeviceKit) | 0 | 1/10 |
| NativeMemory leaks (empty finally) | 41 | 126 | 167 |
| Orphaned proxy classes | 23 | 45 | 68 |
| GCHandle early-free instances | 20+ | 204+ | 224+ |

---

## Updated Priority Ranking (All 10 Libraries)

### Critical (compilation blockers, multiple libraries)

1. **S3 — Malformed closure parameters** (RxSwift: 14, GRDB: 28, Kingfisher: 33 = **75 functions**) — Most widespread critical issue. Fix closure type stringification in `ClosureHandler`.
2. **S5/H — Orphaned `_dbw_` calls** (Alamofire: 28, Kingfisher: 5+, GRDB: 15+, PhoneNumberKit: 6 = **54+ instances**) — Ensure extension definitions emitted with callers.
3. **S4 — Internal member access gate** (CryptoSwift: 83, XMLCoder: 12 = **95 errors**) — Add access level check in `MemberEmissionValidator`.
4. **S7 — Missing @MainActor on wrappers** (Kingfisher: 53) — Propagate actor annotation to all wrappers for actor-isolated types.
5. **S9 — `inout` loaded as `let`** (GRDB: 10) — Emit `var` for `inout` parameters.
6. **S8 — Enum tuple → multi-arg** (Parchment: 3) — Destructure tuple into individual arguments.
7. **S2 — Bare `Type` metatype** (Swinject: 9) — Project as `Any.Type`.
8. **S1 — Swift keyword params** (Alamofire: 3) — Backtick-escape keywords.
9. **S10 — Unmanaged on struct** (Kingfisher: 3) — Use indirect result for struct returns.
10. **S11 — Duplicate param names** (Kingfisher: 2) — Deduplicate in async wrapper.

### High (runtime correctness)

11. **K1 — GCHandle early free** (224+ instances across 7 libraries) — Most widespread runtime issue. Escaping closures need callback-based dealloc.
12. **P1/P2 — NativeMemory leaks** (167+ instances across all 10 libraries) — Add `NativeMemory.Free(payload)` to empty finally blocks.
13. **R1 — Orphaned proxy classes** (68 proxies across 8 libraries) — Don't emit proxy when EveryProtocol can't conform.
14. **N1 — Double P/Invoke getter** (Alamofire: 15) — Store result in local before null check.

### Medium

15. **S6 — `try!` in closure adapters** (RxSwift: 63, GRDB: 56 = 119) — Propagate errors through error-out.
16. **S12 — Internal enum cases** (XMLCoder: 8) — Filter underscore-prefix internal cases.
17. **I1/I2/I3 — Type collisions, scope, constructors** (15+ instances) — Various targeted fixes.
18. **I4 — Utf8Slice string buffer leak** (DeviceKit: 1) — Swift allocates buffer + slice but C# only frees slice.
19. **I5 — Incomplete vtable initialization** (Swinject: BehaviorProxy) — Empty vtable body with no callback receivers.
