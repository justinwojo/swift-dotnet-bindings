# CryptoSwift Binding — Generator Bugs Found

CryptoSwift is a complex, real-world Swift library (103 types, 501 members) that exposed many generator codegen bugs that simpler test libraries didn't surface. This document catalogs every codegen bug found (Bugs #1–#24), plus capability gaps from skipped members, organized by the generator component responsible.

> **Review status**: Validated by Codex (Feb 2026). Bugs 1-4, 7-20 confirmed as-is. Bug 3 root cause broadened. Bug 5 marked as already-filtered. Bug 6 confirmed as latent risk. Bug 19 attribution refined. Four additional bugs (#21-#24) added from Codex review.

## Runtime Test Results (iOS Simulator)

| Test | Result | Notes |
|------|--------|-------|
| Digest.Sha256 (static) | PASS | Static method on frozen type — fully functional |
| Digest.Md5 (static) | PASS | Static method on frozen type — fully functional |
| Digest.Sha1 (static) | PASS | Static method on frozen type — fully functional |
| Enum type access | PASS | SHA2.Variant, HMAC.Variant, Padding — all construct correctly |
| Property access | PARTIAL | AES.BlockSize (static IntPtr) = 16 works; SHA2 instance properties fail |
| SHA2 instance | FAIL | "non-blittable types" — SHA2.Variant enum passed via CallConvSwift |
| HMAC-SHA256 | CRASH | SIGSEGV in `swift_release_dealloc` during SafeHandle cleanup |
| ChaCha20 round-trip | NOT RUN | Process killed by HMAC crash |
| RSA encrypt/decrypt | NOT RUN | Process killed by HMAC crash |
| MD5 instance | NOT RUN | Process killed by HMAC crash |

**Summary**: 4 pass, 1 partial, 1 fail (caught exception), 1 crash (SIGSEGV), 3 not run.

---

## C# Codegen Bugs (Swift.CryptoSwift.cs)

These bugs originally produced C# code that won't compile. The current generated file compiles because 24 broken members are stubbed with `NotImplementedException` (e.g., Swift.CryptoSwift.cs:5430, :17866). Each entry below describes the generator's incorrect output before stubbing.

### Bug 1: Operators missing SwiftIndirectResult allocation — FIXED

**Component**: `OperatorHandler.cs`
**Impact**: 14 operators on BigUInt (6) and BigInt (8)
**Error**: `CS0103: The name 'swiftIndirectResult' does not exist in the current context`

When an operator returns a non-frozen class type (like BigUInt/BigInt), the generated code references `swiftIndirectResult` without declaring it. The operator body should allocate memory and create a `SwiftIndirectResult`, just like `Init` methods do.

**Generated (broken)**:
```csharp
public static BigUInt operator /(BigUInt arg0, BigUInt arg1)
{
    return PInvoke_op_Division(swiftIndirectResult, arg0.Payload, arg1.Payload);
}
```

**Expected (correct)**:
```csharp
public static BigUInt operator /(BigUInt arg0, BigUInt arg1)
{
    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<BigUInt>();
    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
    var swiftIndirectResult = new SwiftIndirectResult(payload);
    PInvoke_op_Division(swiftIndirectResult, arg0.Payload, arg1.Payload);
    return SwiftMarshal.MarshalFromSwift<BigUInt>(new IntPtr(swiftIndirectResult.Value));
}
```

**Affected operators**: `/`, `%`, `~`, `*`, `+`, `-` on BigUInt; `/`, `%`, `~`, `&`, `|`, `^`, `*`, `+`, `-` on BigInt.
**Operators that work**: `==`, `<`, `!=`, `>` (return `bool`, no indirect result needed).

**Root cause**: `OperatorHandler` emits the P/Invoke call as if it returns a value directly, but the P/Invoke signature returns `void` and writes through `SwiftIndirectResult`. The handler doesn't implement the indirect-result allocation pattern that `MethodHandler` uses.

---

### Bug 2: Tuple return values not marshalled — FIXED

**Status**: Fixed in Step 4 of FIX-ORDER.md

**Component**: `WrapperEmitter.Return.cs` (return emission logic)
**Impact**: 3 methods on AEADChaCha20Poly1305

Methods returning named tuples now emit per-element marshalling via `SwiftMarshal.MarshalFromSwift<T>()` instead of raw `return result;`. Each tuple element is individually marshalled from its P/Invoke representation to the C# type.

**Before** (broken):
```csharp
var result = PInvoke_encrypt(...); // returns ValueTuple<IntPtr, IntPtr>
return result; // ERROR: can't convert (nint, nint) to (SwiftArray<byte>, SwiftArray<byte>)
```

**After** (fixed):
```csharp
var result = PInvoke_encrypt(...); // returns ValueTuple<IntPtr, IntPtr>
var elem0 = SwiftMarshal.MarshalFromSwift<SwiftArray<byte>>(result.Item1);
var elem1 = SwiftMarshal.MarshalFromSwift<SwiftArray<byte>>(result.Item2);
return (elem0, elem1);
```

---

### Bug 3: Protocol proxy closure type mismatch

**Component**: `ProtocolProxyEmitter.cs`
**Impact**: 3 methods on UpdatableProxy
**Error**: `CS1503: cannot convert from 'Action<SwiftArray<byte>>' to 'AnyType'`

When a protocol method has a closure parameter, the interface declares it as `AnyType` (fallback), but the proxy's receive dispatch method marshals it as `Action<SwiftArray<byte>>`. The proxy then tries to call `_csharpImpl!.Finish(param0)` passing the `Action` where `AnyType` is expected.

**Related**: The proxy also emits a public `Finish(Action<SwiftArray<byte>>)` overload that doesn't exist on the interface.

**Root cause**: The interface and the proxy disagree on the projected C# type for closure parameters. The proxy is closer to correct (Action<>) but the interface fell back to AnyType, and they need to agree.

> **Codex note**: Root cause is broader than just closure fallback — proxy/interface/vtable method indexing and signature alignment are also broken. See also Bug #21 (vtable index desync).

---

### Bug 4: Shift operators with unresolved generic type parameter — FIXED

**Component**: `OperatorHandler.cs`
**Impact**: 4 operators on BigUInt, 4 on BigInt
**Error**: `CS0103: The name 'T0' does not exist in the current context`

Shift operators (`>>`, `<<`) use an unresolved generic type parameter `T0` for the second operand. The original Swift signature is generic (`<T: FixedWidthInteger>`), but the C# operator can't be generic.

**Generated (broken)**:
```csharp
public static BigUInt operator >>(BigUInt arg0, T0 arg1) // T0 undefined
```

**Root cause**: The operator handler doesn't detect that the second operand of a shift operator is generic and should either: (a) be skipped with a skip reason, or (b) be bound to a concrete type like `int`.

---

### Bug 5: Swift overflow operators emitted with `&` in C# identifier

> **Status: ALREADY FIXED** — `OperatorHandler.cs:123` now filters unsupported operators via `IsSupportedOperator()`. The `_csharpOperators` dictionary omits overflow operators (`&+`, `&-`, `&*`, `&<<`, `&>>`), and the check at line 129 correctly skips them. This bug was observed in an earlier generator run; the current codebase handles it.

**Component**: `OperatorHandler.cs`
**Impact**: 4 operators on BigUInt (`&<<`, `&>>`, `&<<=`, `&>>=`)
**Error**: `&` is not valid in a C# identifier/operator name

Swift has overflow shift operators (`&<<`, `&>>`) and their compound forms. These have no C# equivalent and should be filtered out, like `&&` and `||` already are.

**Root cause**: ~~The operator filter list in `OperatorHandler` doesn't include Swift overflow operators.~~ Now fixed — operators not in `_csharpOperators` are skipped.

---

### Bug 6: `void*` used as generic type argument in ValueTuple — FIXED

**Status**: Fixed in Step 4 of FIX-ORDER.md

**Component**: `TupleHandler.cs` and `WrapperEmitter.Return.cs`
**Impact**: 2 P/Invoke declarations

All code paths that returned `"void*"` for bound generic tuple elements now return `"IntPtr"` instead. Also fixed optional non-ObjC tuple elements where the marshal code used `new IntPtr(&itemName)` (pointer-to-pointer) instead of passing the IntPtr value directly. Fixed in:
- `TupleHandler.TranslateElementTypeToPInvoke()` — bound generic fallback and unsupported existential fallback
- `WrapperEmitter.Return.GetPInvokeTypeForTupleElement()` — bound generic fallback and end fallback
- `WrapperEmitter.Return.GetTupleElementMarshalCode()` — non-ObjC optional now uses `itemName` directly (no `&`)

---

### Bug 7: Generic type missing type argument on SwiftSafeHandle

**Component**: `MethodHandler.cs` (constructor emission for generic types)
**Impact**: BatchedCollection<T0> constructor
**Error**: `CS0305: Using the generic type 'BatchedCollection<T0>' requires 1 type arguments`

**Generated (broken)**:
```csharp
_payload = new SwiftSafeHandle<BatchedCollection>((IntPtr)NativeMemory.Alloc(_payloadSize));
```

**Expected**:
```csharp
_payload = new SwiftSafeHandle<BatchedCollection<T0>>((IntPtr)NativeMemory.Alloc(_payloadSize));
```

**Root cause**: When emitting a constructor for a generic type, the `SwiftSafeHandle<>` reference drops the type parameter.

---

### Bug 8: Non-frozen generic type marshalled as frozen (PayloadBuffer)

**Component**: `MethodHandler.cs` (argument marshalling)
**Impact**: BatchedCollection.Index method
**Error**: `CS1061: 'BatchedCollectionIndex<T0>' does not contain a definition for 'PayloadBuffer'`

The generated code tries to access `.PayloadBuffer` on `BatchedCollectionIndex<T0>`, but `BatchedCollectionIndex<T0>` is a non-frozen generic type that doesn't have a `PayloadBuffer` property (only frozen structs with Buffer inner types get PayloadBuffer).

**Root cause**: The argument marshalling path treats generic type parameters as frozen when they should be marshalled via `SwiftSelf`/handle pattern.

---

### Bug 9: Duplicate property (static + instance)

**Component**: `PropertyHandler.cs`
**Impact**: Rabbit.KeySize
**Error**: `CS0102: The type already contains a definition for 'KeySize'`

Both a static `KeySize` (from Cipher protocol conformance) and an instance `KeySize` are emitted, causing a duplicate member error.

**Root cause**: The property handler doesn't deduplicate when the same property name is emitted from both a protocol conformance and the type itself.

---

### Bug 10: Generic operator uses wrong type parameter name (T1 vs T0) — FIXED

**Component**: `OperatorHandler.cs`
**Impact**: 4 operators on BatchedCollectionIndex
**Error**: Type parameter mismatch

Operators on `BatchedCollectionIndex<T0>` use `T1` instead of `T0` for the generic type parameter in the operator signature.

**Root cause**: The operator handler uses the wrong index when resolving the type parameter from the enclosing generic type.

---

### Bug 11: Protocol proxy dispatches method not on interface

**Component**: `ProtocolProxyEmitter.cs`
**Impact**: CollectionProxy (ISwiftCollection)
**Error**: `CS1061: 'ISwiftCollection' does not contain a definition for 'Batched'`

The `CollectionProxy` has a `Receive_batched_2` dispatch method that calls `_csharpImpl!.Batched(param0)`, but `ISwiftCollection` doesn't declare a `Batched` method (it was previously removed from the interface due to Bug 12).

**Root cause**: The proxy emitter and interface emitter disagree about which protocol methods are emittable. The interface skipped `Batched` (returns generic type with AnyType constraint), but the proxy still emits a receiver for it.

---

### Bug 12: AnyType used as generic type argument with ISwiftCollection constraint

**Component**: `TypeDatabase` or type projection
**Impact**: ISwiftCollection interface and CollectionProxy
**Error**: `AnyType doesn't satisfy ISwiftCollection where constraint`

The `Batched` method returns `BatchedCollection<AnyType>`, but `BatchedCollection<T0>` has `where T0 : ISwiftCollection`, and `AnyType` doesn't implement `ISwiftCollection`.

**Root cause**: The type database projects an unknown generic parameter as `AnyType` instead of skipping the method entirely.

---

## Swift Wrapper Codegen Bugs (Swift.CryptoSwift.swift)

The generated Swift wrapper has compilation errors that prevent it from being used. A hand-written subset (`SwiftBindings.swift`) is used instead.

### Bug 13: EveryProtocol conformance — return type mismatch (Cryptors)

**Error**: `type 'EveryProtocol' does not conform to protocol 'Cryptors'`

The generated conformance declares `makeEncryptor() -> Any` and `makeDecryptor() -> Any`, but the actual protocol requires `makeEncryptor() throws -> any Cryptor & Updatable`.

**Issues**:
- Missing `throws` specifier
- Return type `Any` instead of `any Cryptor & Updatable` (protocol composition existential)

---

### Bug 14: EveryProtocol conformance to non-existent module types

**Errors**: `no type named 'Collection' in module 'CryptoSwift'`, `no type named 'FixedWidthInteger'`, `no type named 'BatchedCollection'`

CryptoSwift defines internal protocol extensions on Swift standard library types (`Collection`, `FixedWidthInteger`). The generator emits EveryProtocol conformances to `CryptoSwift.Collection` etc., but these types don't exist in the CryptoSwift module namespace — they're Swift standard library types with CryptoSwift extensions.

---

### Bug 15: Extension on non-existent types

**Errors**: `no type named 'BlockEncryptor'`, `'StreamEncryptor'`, `'StreamDecryptor'`

The generated Swift wrapper emits `extension CryptoSwift.BlockEncryptor { ... }` etc., but these types are internal to CryptoSwift and not accessible from external code.

---

### Bug 16: Wrong argument labels on method wrappers

The generated wrappers use incorrect argument labels. Example: `AES.encrypt(block:)` where the actual Swift method uses a different label or no label.

---

### Bug 17: Accessing internal members

The generated wrappers call `SHA2.process64(...)`, `SHA2.process32(...)`, `SHA3.process(...)` etc., which are `internal` methods, not `public`.

---

## Runtime Bugs (Mono/.NET Limitations)

### Bug 18: Non-blittable types with CallConvSwift

**Error**: "Passing non-blittable types to a P/Invoke with the Swift calling convention is unsupported"

Non-frozen enum types (like `SHA2.Variant`, which is a C# class with a SafeHandle payload) cannot be passed through P/Invoke with `CallConvSwift`. This affects:
- Any `Init` method that takes a non-frozen enum parameter
- Any instance method on a type that was constructed with non-frozen enum params
- Property getters on instances of non-frozen types

**Workaround**: Use `@_cdecl` Swift wrapper functions instead of direct P/Invoke with CallConvSwift.

---

### Bug 19: SIGSEGV in SwiftSafeHandle.ReleaseHandle

**Crash**: `Assertion at jit-info.c:918, condition '!ji->async' not met`

The Mono JIT crashes with a SIGSEGV during `SwiftSafeHandle.ReleaseHandle` when cleaning up intermediate objects created during P/Invoke calls with `CallConvSwift`. The crash occurs in `swift_release_dealloc` when the runtime tries to release a Swift object.

This is the same known Mono bug that affects closure P/Invoke and SwiftString.PInvoke_GetLength in the TestFramework.

> **Codex note**: Not purely a Mono bug — evidence also points to interop ownership/marshalling corruption during cleanup. The generator may be passing incorrect type metadata or mismanaging ARC ownership semantics (retain/release balance), which then triggers the Mono assertion. Root cause may be shared between Mono JIT and generator-side marshalling.

---

### Bug 20: Init projected as instance method (not constructor) — FIXED

**Status**: Fixed in Step 2 of FIX-ORDER.md

**Component**: `MethodHandler.cs` (constructor handling for non-frozen classes)
**Impact**: All non-frozen class types (SHA2, MD5, HMAC, ChaCha20, RSA, etc.)

Swift initializers on non-frozen classes were emitted as instance methods that don't use `self`, rather than as C# constructors. Fixed by extending `ConstructorHandlerFactory.Handles()` to include `ClassDecl` (in addition to `StructDecl`), with an exclusion for async constructors which must remain as factory methods.

**Before** (broken):
```csharp
// Only constructor is internal: SHA2(SwiftHandle handle)
// Init is an instance method:
public SHA2 Init(SHA2.Variant variant) { ... }  // doesn't use self
```

**After** (fixed):
```csharp
public unsafe SHA2( SHA2.Variant variant)  // proper C# constructor
{
    _payload = new SwiftSafeHandle<SHA2>((IntPtr)NativeMemory.Alloc(_payloadSize));
    var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
    PInvoke_init_58864E6D(swiftIndirectResult, variant.Payload.DangerousGetHandle());
}
```

---

## Additional Bugs Found by Codex Review

These bugs were found during automated review of the generator source code against the CryptoSwift output.

### Bug 21: EveryProtocol vtable/index desync — FIXED

**Status**: Fixed in Step 5 of FIX-ORDER.md

**Component**: `EveryProtocolEmitter.cs`
**Impact**: All EveryProtocol protocol conformances with deduplicated methods

When `EmitProtocolConformance()` skipped a method due to global signature deduplication, it `continue`d without incrementing the per-emission method index. But vtable struct fields are declared sequentially. This caused index drift — emitted method bodies referenced `func_finish_N` at a lower N than the vtable struct declared.

**Fix**: Moved the vtable index assignment (via `methodIndices` dictionary + `methodIndex++`) before the global signature deduplication check. The index now always advances in lockstep with the vtable struct, even when a method body is skipped due to global conflicts.

---

### Bug 22a: EveryProtocol `throws` not emitted

**Component**: `EveryProtocolEmitter.cs`
**Impact**: All protocol methods with `throws` specifier
**Severity**: Medium

The `EmitMethodImplementation()` at line 562 emits:
```swift
public func {method.Name}({parametersString}){returnDecl} {
```

The `throws` keyword is never appended to the method signature, even though `MethodDecl.Throws` is parsed and available (defined at `MethodDecl.cs:41`). The `method.Throws` property is never checked during EveryProtocol emission.

This is a generalized version of Bug #13's `throws` issue — it affects ALL protocol methods that throw, not just `Cryptors.makeEncryptor()`.

**Impact**:
- Swift protocol conformance check fails: method signature doesn't match protocol requirement
- C# side has no way to handle Swift errors from these methods

### Bug 22b: `rethrows` not representable in model

**Component**: `MethodDecl.cs` (model fidelity)
**Impact**: All protocol methods with `rethrows` specifier
**Severity**: Medium

The method model only has `bool Throws` (`MethodDecl.cs:41`), which is a boolean. Swift distinguishes between `throws` and `rethrows` — the latter means the method only throws if one of its closure parameters throws. The current model collapses both to `true`, losing the `rethrows` semantic.

**Fix requires two steps**:
1. Emission fix (Bug 22a): Check `method.Throws` in `EveryProtocolEmitter.cs:562` and append `throws`
2. Model fix: Change `bool Throws` to an enum (`None`/`Throws`/`Rethrows`) in `MethodDecl.cs`, update the parser to populate it, and emit `rethrows` where appropriate

---

### Bug 23: Function-type metatype rendering invalid in Swift wrappers

**Component**: Swift wrapper emitter (return type metatype generation)
**Impact**: Any wrapper method that returns a closure/function type
**Severity**: Critical (blocks Swift wrapper compilation)

**Generated (broken)** (Swift.CryptoSwift.swift:375):
```swift
return resultPtr.assumingMemoryBound(to: (Swift.ArraySlice<Swift.UInt8>) -> (Swift.Array<Swift.UInt8>)?.self).pointee
```

The `.self` metatype accessor is applied directly to a function type literal, which is syntactically invalid in Swift. Function types need parentheses around them before `.self`.

**Expected (correct)**:
```swift
return resultPtr.assumingMemoryBound(to: ((Swift.ArraySlice<Swift.UInt8>) -> (Swift.Array<Swift.UInt8>)?).self).pointee
```

**Root cause**: The `.self` metatype suffix is appended by template code in `EveryProtocolEmitter.cs:589`. The underlying helper `SwiftTypeNameHelper.cs:74` (`GetSwiftTypeNameForMetatype()`) fails to parenthesize function types before metatype use. Function types like `(A) -> B` need wrapping as `((A) -> B).self`, but the helper returns the bare function type string.

---

### Bug 24: Frozen enum parameters passed as managed types through CallConvSwift

> **Status: FIXED** — Enum parameters are now emitted as `IntPtr` (sync: `EnumSafeHandle` marker → `.Payload.DangerousGetHandle()`) or async copy-buffer (`IntPtrFromNonFrozen` marker → `{name}Handle`). Fixed in three locations: `PInvokeEmitter.cs` (moved enum check before frozen guard), `EnumHandler.CaseConstruction.cs` (added enum checks to `GetPInvokeType()`/`GetPInvokeArgument()`), and `WrapperEmitter.Async.cs` (extended `nonFrozenParams` filter to include enums). 9 unit tests added.

**Component**: `PInvokeEmitter.cs`
**Impact**: Any P/Invoke method taking an enum parameter (SHA2.Init, Digest.Sha2, etc.)
**Severity**: High (runtime failure)

Enum types like `SHA2.Variant` are projected as C# classes (with SafeHandle payload), making them non-blittable. When passed to a P/Invoke with `CallConvSwift`, the Mono runtime rejects them:

```
"Passing non-blittable types to a P/Invoke with the Swift calling convention is unsupported"
```

**Affected locations** (examples from Swift.CryptoSwift.cs):
- Line 7966: `SHA2.Init(SHA2.Variant variant)` — non-blittable enum parameter
- Line 26624: `Digest.Sha2(IEnumerable<byte>, SHA2.Variant variant)` — same

**Root cause**: `PInvokeEmitter.cs:314` has enum-special handling (`EnumSafeHandle` path at line 322-323), but it is nested under a `!IsTypeFrozen()` guard. Frozen enum cases bypass this path entirely and are emitted as their managed wrapper class type — which is non-blittable and rejected by CallConvSwift at runtime.

**Affected locations** (current generated output):
- `Swift.CryptoSwift.cs:7988` — SHA2.Init P/Invoke signature
- `Swift.CryptoSwift.cs:26646` — Digest.Sha2 P/Invoke signature

**Distinction from Bug #18**: Bug #18 describes the runtime symptom generically. Bug #24 pinpoints the generator component responsible — the PInvokeEmitter should be emitting `IntPtr` or the raw enum value type in the P/Invoke signature for frozen enums, not the managed wrapper class.

---

## Summary by Generator Component

| Component | Bugs | IDs |
|-----------|------|-----|
| `OperatorHandler.cs` | 3 | #1, #4, #10 |
| `MethodHandler.cs` / constructors | 3 | #7, #8, #20 (FIXED) |
| `WrapperEmitter.Return.cs` | 1 | #2 (FIXED) |
| `ProtocolProxyEmitter.cs` | 2 | #3, #11 |
| `PropertyHandler.cs` | 1 | #9 |
| `TupleHandler.cs` / pointer types | 1 | #6 (FIXED) |
| `TypeDatabase` / type projection | 1 | #12 |
| `EveryProtocolEmitter.cs` / `SwiftTypeNameHelper.cs` | 6 | #13, #14, #15, #21 (FIXED), #22, #23 |
| Swift wrapper extension emission | 2 | #16, #17 |
| `PInvokeEmitter.cs` | 1 | #24 (FIXED) |
| Mono runtime / interop | 2 | #18, #19 |
| Already fixed | 1 | #5 |

**Total**: 24 bugs cataloged (16 active, 6 fixed, 0 latent, 1 shared Mono/generator, 1 already fixed)

## Priority for Fixes

**High priority** (blocks runtime validation for many types):
- ~~Bug #20: Init as instance method — can't construct ANY non-frozen class type~~ **FIXED**
- Bug #1: Operator indirect result — 14 broken operators
- ~~Bug #21: EveryProtocol vtable/index desync — all protocol conformances broken~~ **FIXED**
- ~~Bug #24: Frozen enum P/Invoke — runtime crash on any enum parameter~~ **FIXED**
- Bug #23: Function-type metatype rendering — blocks Swift wrapper compilation

**Medium priority** (affects specific type patterns):
- ~~Bug #2: Tuple return marshalling~~ **FIXED**
- Bug #3, #11: Protocol proxy/interface mismatch (related to #21)
- Bug #7, #8: Generic type constructor/marshalling
- Bug #13, #22: EveryProtocol return types and throws specifiers
- Bug #14, #15: EveryProtocol conformance to non-existent/internal types

**Low priority** (edge cases or partially addressed):
- Bug #4, #10: Operator edge cases (generic shifts, T1/T0)
- Bug #5: ~~Overflow operators~~ (already fixed)
- ~~Bug #6: void* in ValueTuple~~ **FIXED**
- Bug #9: Duplicate property
- Bug #12: AnyType constraint satisfaction
- Bug #16, #17: Wrong argument labels, internal member access

**Runtime/shared** (requires Mono investigation + generator-side marshalling review):
- Bug #18: Non-blittable types with CallConvSwift
- Bug #19: SIGSEGV in SafeHandle cleanup (Mono + possible generator ownership issue)

---

## Capability Gaps (Skipped Members)

Beyond codegen bugs, 42 of 501 members (8.4%) are intentionally skipped by the generator due to capability gaps. These are not bugs — they represent known limitations of the binding framework. From `binding-report.json`:

### UnsupportedType: 20 members

All 20 are **compound-assignment operators** (`/=`, `%=`, `|=`, `&=`, `^=`, `*=`, `+=`, `-=`, `>>=`, `<<=`) on BigUInt (10) and BigInt (10). These have no C# `operator` equivalent — C# synthesizes compound assignment from the base operator automatically. **No fix needed**; these are correctly skipped.

### UnsupportedSignature: 14 members

Methods with unsupported placeholder types in their signature:
- 9 `worker()` methods on block cipher modes (CFB, GCM, ECB, CCM, CTR, OFB, OCB, PCBC, CBC) — closure parameter with unsupported type
- `SHA1.process()` — unsupported placeholder type
- `BigUInt.compare()` — unsupported placeholder type
- `ChaCha20.ChaChaEncryptor.update()` and `ChaCha20.ChaChaDecryptor.update()` — unsupported placeholder type
- Top-level `xor()` function — unsupported placeholder type

**Potential fix area**: `Marshaler/` handlers and `TypeDatabase/` type resolution for the specific placeholder types these methods use.

### AnyTypeFallback: 4 members

Properties whose types resolved to opaque `AnyType` instead of a concrete type:
- `AEADChaCha20Poly1305.ivRange` — likely `Range<Int>` or similar
- `SHA1.hashInitialValue` — likely `Array<UInt32>` or similar
- `SHA1.accumulatedHash` — likely `Array<UInt32>` or similar
- `BigInt.Words.indices` — likely `Range<Int>` or similar

**Potential fix area**: `TypeDatabase/TypeDatabaseExtensions.cs` — add type mappings for the concrete types these resolve from.

### StaticProtocolMember: 4 members

Members skipped because C# interfaces cannot declare static members (prior to C# 11 static abstract):
- `Cryptors.randomIV()` — static protocol method
- `AEAD.kLen` and `AEAD.ivRange` — static protocol properties
- `BinaryFloatingPoint.init` — protocol constructor requirement

**Potential fix area**: Could emit as static methods on a companion class, or use C# 11 static abstract interface members if targeting .NET 7+.
