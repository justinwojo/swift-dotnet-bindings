# Architecture Retrospective Supplement: Implementation Inventories

This document supplements `architecture-retrospective-findings.md` with three concrete inventories needed before implementation of the proposed refactoring steps.

---

## 1. Complete Parameter.Type String Marker Inventory

Every `Parameter` in the signature system uses its `Type` field as a discriminated union encoded as a string. This inventory catalogs every marker, where it is constructed, where it is consumed, and what semantic information it carries.

### 1.1 Prefixed Markers (parsed via `StartsWith` / `Split(':')`)

| # | String Marker/Prefix | Constructed At | Consumed At (SignatureString) | Consumed At (PInvokeSignatureString) | Consumed At (GetCallArgumentString) | Semantic Information Encoded | Proposed MarshalledType Variant |
|---|---------------------|----------------|------------------------------|--------------------------------------|-------------------------------------|------------------------------|-------------------------------|
| 1 | `Existential:{containerType}:{publicType}` | `WrapperSignatureBuilder.HandleArguments` (MethodSignature.cs:493), `PInvokeSignatureBuilder.HandleArguments` (PInvokeEmitter.cs:288) | MethodSignature.cs:49 — `Split(':')[2]` (public type) | MethodSignature.cs:26 — `Split(':')[1]` (container type) | MethodSignature.cs:110-111 — `Split(':')[1]` for `ISwiftExistentialConvertible<>` cast | Container type (ExistentialContainer1), public interface type (IProtocol). P/Invoke uses container, wrapper uses interface, call site extracts container from interface. | `Existential(string ContainerType, string PublicType)` |
| 2 | `SimpleEnum:{underlyingType}:{enumTypeName}` | `PInvokeSignatureBuilder.HandleArguments` (PInvokeEmitter.cs:360) | MethodSignature.cs:46 — `Split(':')[1]` (underlying type) | (delegates to SignatureString) | MethodSignature.cs:107 — `Split(':')[1]` for cast expression | Underlying integer type (int, long), full C# enum type name. P/Invoke uses underlying type, call site casts. | `SimpleEnum(string UnderlyingType, string EnumTypeName)` |
| 3 | `ObjCBridged:{csharpTypeName}` | `PInvokeSignatureBuilder.HandleArguments` (PInvokeEmitter.cs:348) | MethodSignature.cs:41 — emits `IntPtr` | (delegates to SignatureString) | MethodSignature.cs:144 — returns `{name}Handle` | Full C# type name of the ObjC class (e.g., `UIKit.UIImage`). P/Invoke uses IntPtr, call site extracts `.Handle`. | `ObjCBridged(string CSharpTypeName)` |
| 4 | `CdeclClosureFuncPtr:{callbackName}:{sourceCsName}` | `PInvokeSignatureBuilder.HandleArguments` (PInvokeEmitter.cs:241) | MethodSignature.cs:59 — emits `IntPtr` | (delegates to SignatureString) | MethodSignature.cs:127-128 — `Split(':')[1]` (callback), `Split(':')[2]` (source) for Handle.IsAllocated guard | Callback function name for static delegate*, source C# parameter name for GCHandle. | `CdeclClosureFuncPtr(string CallbackName, string SourceCsName)` |
| 5 | `CdeclClosureContext:{sourceCsName}` | `PInvokeSignatureBuilder.HandleArguments` (PInvokeEmitter.cs:242) | MethodSignature.cs:60 — emits `IntPtr` | (delegates to SignatureString) | MethodSignature.cs:131-132 — `Split(':')[1]` for GCHandle guard | Source C# parameter name for GCHandle allocation. | `CdeclClosureContext(string SourceCsName)` |
| 6 | `AsyncThrowingContext:{paramName}` | `PInvokeSignatureBuilder.HandleArguments` (PInvokeEmitter.cs:231) | MethodSignature.cs:54 — emits `IntPtr` | (delegates to SignatureString) | MethodSignature.cs:134-135 — `Substring()` for `{param}ContextPtr` | Parameter name to construct context pointer variable name. | `AsyncThrowingContext(string ParamName)` |
| 7 | `AsyncThrowingStartFunc:{callbackName}` | `PInvokeSignatureBuilder.HandleArguments` (PInvokeEmitter.cs:232) | MethodSignature.cs:56-57 — emits `delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>` | (delegates to SignatureString) | MethodSignature.cs:138-139 — `Substring()` for `s_{name}_Start` | Callback function name to construct start function pointer variable. | `AsyncThrowingStartFunc(string CallbackName)` |
| 8 | `NativeRemapped:{swiftWrapperType}` | `PInvokeSignatureBuilder.HandleArguments` (PInvokeEmitter.cs:327) | MethodSignature.cs:52 — `Substring()` to extract type | (delegates to SignatureString) | MethodSignature.cs:147 — returns `{name}Swift` | Swift wrapper type name (e.g., `Swift.Data`). P/Invoke uses the actual type, call site uses converted variable. | `NativeRemappedFrozen(string SwiftWrapperType)` |

### 1.2 Direct String Markers (exact equality matching)

| # | String Marker | Constructed At | SignatureString Output | PInvokeSignatureString Output | GetCallArgumentString Output | Semantic Information | Proposed MarshalledType Variant |
|---|--------------|----------------|----------------------|-------------------------------|------------------------------|---------------------|-------------------------------|
| 9 | `AsyncCallback` | PInvokeEmitter.cs:188 | `void* {name}` | (delegates to SS) | `{name}` | Async method's result callback function pointer | `AsyncCallback` |
| 10 | `AsyncErrorCallback` | PInvokeEmitter.cs:189 | `void* {name}` | (delegates to SS) | `{name}` | Async method's error callback function pointer | `AsyncErrorCallback` |
| 11 | `AsyncContext` | (internal to PInvoke pattern) | `void* {name}` | (delegates to SS) | `null` | Unused context parameter (always null) | `AsyncContext` |
| 12 | `AsyncTask` | PInvokeEmitter.cs:190 | `IntPtr {name}` | (delegates to SS) | `GCHandle.ToIntPtr({name})` | GCHandle to the task holder object | `AsyncTask` |
| 13 | `IntPtrFromNonFrozen` | PInvokeEmitter.cs:363, 374 | `IntPtr {name}` | (delegates to SS) | `{name}Handle` | Non-frozen struct or complex enum in async context (SafeHandle extraction) | `NonFrozenIntPtr` |
| 14 | `EnumSafeHandle` | PInvokeEmitter.cs:365 | `IntPtr {name}` | (delegates to SS) | `{name}.Payload.DangerousGetHandle()` | Complex enum with SafeHandle payload | `EnumSafeHandle` |
| 15 | `NativeRemappedSafeHandle` | PInvokeEmitter.cs:322 | `SafeHandle {name}` | (delegates to SS) | `{name}Swift.Payload` | Non-frozen native-remapped type (URL) with SafeHandle | `NativeRemappedNonFrozen` |
| 16 | `SafeHandle` | PInvokeEmitter.cs:376 | `SafeHandle {name}` (via default) | (delegates to SS) | `{name}.Payload` | Non-frozen struct passed via SafeHandle | `NonFrozenSafeHandle` |
| 17 | `SwiftClosureData` | PInvokeEmitter.cs:247 | `SwiftClosureData {name}` (via default) | (delegates to SS) | `{name}Closure` | Legacy closure data (func ptr + context pair) | `SwiftClosureLegacy` |
| 18 | `bool` | (from TypeRecord) | `bool {name}` (via default) | `[MarshalAs(UnmanagedType.U1)] bool {name}` | `{name}` (via default) | Boolean requiring marshalling annotation | `Bool` |

### 1.3 Pattern-Matched Markers (suffix/structure matching)

| # | Pattern | Constructed At | Where Consumed | Semantic Information | Proposed MarshalledType Variant |
|---|---------|----------------|---------------|---------------------|-------------------------------|
| 19 | `{TypeName}.Buffer` | PInvokeEmitter.cs:381 | MethodSignature.cs:114 (`.EndsWith(".Buffer")` + ref modifier → `ref {name}Disposable.BufferRef`), MethodSignature.cs:115 (`.EndsWith(".Buffer")` → `{name}Disposable.Buffer`) | Frozen struct requiring memory management (ClassWithBufferStruct pattern) | `FrozenBuffer(string TypeName)` |
| 20 | `delegate* unmanaged...` | PInvokeEmitter.cs:253 (from ClosureHandler) | MethodSignature.cs:141 (`.StartsWith("delegate* unmanaged")` → append `FuncPtr` suffix) | @convention(c) closure as unmanaged function pointer | `ConventionCFuncPtr(string FuncPtrType)` |
| 21 | `SwiftSelf<{TypeName}>` / `SwiftSelf<{TypeName}.Buffer>` | PInvokeEmitter.cs:515-517 | (not consumed by pattern matching — passes through default) | Self parameter for frozen struct getter | (part of `SwiftSelfTyped(string InnerType)`) |
| 22 | `SwiftSelf` | PInvokeEmitter.cs:506, 518, 522 | (not consumed by pattern matching — passes through default) | Self parameter for generic struct/class methods | `SwiftSelfUntyped` |

### 1.4 Name-Based Markers (Parameter.Name patterns in GetCallArgumentString)

These don't use `Parameter.Type` patterns but `Parameter.Name` patterns:

| # | Name Pattern | Where Constructed | GetCallArgumentString Output | Semantic Information |
|---|-------------|-------------------|------------------------------|---------------------|
| 23 | `_selfClass` | PInvokeEmitter.cs:458, 491 | `*(IntPtr*)_payload.DangerousGetHandle()` | Class instance self (needs pointer dereference) |
| 24 | `_selfFixed` | PInvokeEmitter.cs:467 | `(IntPtr)__self` | Frozen value-type self (via fixed block) |
| 25 | `_self` (with Type `IntPtr`) | PInvokeEmitter.cs:469, 474, 492 | `_payload.DangerousGetHandle()` | Non-frozen struct self |

### 1.5 Summary

**Total unique markers: 25** (8 prefixed, 10 direct string, 4 pattern-matched, 3 name-based)

All construction happens in two classes:
- `PInvokeSignatureBuilder` (PInvokeEmitter.cs) — 20 of 25 markers
- `WrapperSignatureBuilder` (MethodSignature.cs) — 1 marker (`Existential:` in wrapper path)
- Implicit from TypeRecord — 4 markers (`bool`, `{Type}.Buffer`, `SwiftSelf*`, `delegate*`)

All consumption happens in three methods within MethodSignature.cs:
- `Parameter.SignatureString()` — lines 33-62
- `Parameter.PInvokeSignatureString()` — lines 22-31
- `Signature.GetCallArgumentString()` — lines 100-157

---

## 2. Cross-Path Divergences for Real Types

For each Swift type, the four conversion paths are:
- **P1**: `TypeConversionHandler.GetIdiomaticCSharpType(typeSpec, isParameter, typeTranslator)`
- **P2**: `WrapperSignatureBuilder.TranslateTypeSpecForConversion(typeSpec)` (used as `typeTranslator` callback)
- **P3**: `BoundGenericsHandler.TranslateBoundGenericTypeToCSharp(typeSpec)` (via temporary PropertyDecl)
- **P4**: `ProtocolProxyEmitter.GetCSharpTypeName(typeSpec, forAbiMarshalling: false)`

"N/A" means the path is not the primary handler for that type category. "—" means returns null (no conversion).

### 2.1 Type Conversion Matrix

| # | Swift Type | P1 (Idiomatic) | P2 (Translate callback) | P3 (BoundGenerics) | P4 (ProxyHelper) | Diverges? |
|---|-----------|-----------------|------------------------|---------------------|------------------|-----------|
| 1 | `Swift.String` | `"string"` | `"Swift.SwiftString"` (TypeRecord lookup) | N/A (not bound generic) | `"string"` (via P1) | **INTENTIONAL**: P1 for public API, P2 for type resolution in generic containers |
| 2 | `Swift.Optional<Swift.String>` | `"string?"` | N/A (P1 catches first) | `"Swift.SwiftOptional<Swift.SwiftString>"` | `"string?"` (P1 catches, non-ABI) | **INTENTIONAL**: P3 returns ABI type, P1/P4 return idiomatic |
| 3 | `Swift.Optional<SomeConfig>` (non-frozen struct) | `"SomeConfig?"` | N/A (P1 catches first) | `"Swift.SwiftOptional<SomeConfig>"` | `"SomeConfig?"` (P1 catches) or `"Swift.SwiftOptional<SomeConfig>"` (if P1 returns null and P3 is used) | **INTENTIONAL**: P3 is for P/Invoke layer, P1 is for public API |
| 4 | `Swift.Array<Swift.String>` param | `"IEnumerable<string>"` | N/A (P1 catches first) | `"Swift.SwiftArray<Swift.SwiftString>"` | `"IEnumerable<string>"` (P1 catches) | **INTENTIONAL**: P3 returns ABI, P1/P4 return idiomatic |
| 5 | `Swift.Array<Swift.String>` return | `"IReadOnlyList<string>"` | N/A (P1 catches first) | `"Swift.SwiftArray<Swift.SwiftString>"` | `"IReadOnlyList<string>"` (P1 isParam=true gives IEnumerable) | **BUG RISK**: P4 always passes `isParameter: true` to P1, so array returns get `IEnumerable<string>` instead of `IReadOnlyList<string>` in proxy signatures. If ProtocolHandler uses `isParameter: false`, there's a CS0535 mismatch. |
| 6 | `Swift.Dictionary<Swift.String, Swift.Int>` param | `"IDictionary<string, nint>"` | N/A (P1 catches first) | `"Swift.SwiftDictionary<Swift.SwiftString, nint>"` | `"IDictionary<string, nint>"` (P1 catches) | **INTENTIONAL** |
| 7 | `Swift.Optional<Swift.Array<Swift.String>>` | `"IReadOnlyList<string>?"` (isParam=false) | N/A (P1 catches first) | `"Swift.SwiftOptional<Swift.SwiftArray<Swift.SwiftString>>"` | `"IEnumerable<string>?"` (P1 with isParam=true) | **BUG RISK**: P4 gets IEnumerable? for returns, ProtocolHandler gets IReadOnlyList? |
| 8 | `(Swift.String, Swift.Int)` (tuple) | `null` (not handled) | N/A (tuple, not NamedTypeSpec) | N/A (not bound generic) | `"(string, nint)"` (via GetTupleCSharpType) | **INTENTIONAL**: P1 doesn't handle tuples; TupleHandler in WrapperSignatureBuilder handles them |
| 9 | `any Describable` (existential, known protocol) | `null` (defers to ExistentialHandler) | `"IDescribable"` (via GetPublicExistentialType) | `"Swift.AnyType"` (existentials → AnyType in generic args) | `"IDescribable"` (via ExistentialHandler) | **DIVERGES**: P3 returns AnyType for existentials in generic arguments. This is **INTENTIONAL** — ExistentialContainer doesn't implement ISwiftObject, so it can't be used as a generic type argument. But when P3 is called directly on an existential outside a generic context (e.g., in EnumHandler.CaseConstruction), the AnyType fallback is incorrect. |
| 10 | `any ProtocolA & ProtocolB` (composition) | `null` (defers) | `"IProtocolAAndProtocolB"` (composition interface) | `"Swift.AnyType"` | `"IProtocolAAndProtocolB"` | Same as #9 |
| 11 | `Swift.Optional<any Describable>` | `null` (defers to ExistentialHandler) | N/A (handled by special optional-existential check) | `"Swift.SwiftOptional<Swift.AnyType>"` (existential→AnyType, then Optional wraps it) | `"IDescribable?"` (via special Optional<existential> + well-known check) | **DIVERGES**: P3 wraps AnyType in SwiftOptional. P4 correctly resolves to IDescribable?. When P3 output leaks into a public signature, CS0029 results. **BUG RISK** if callers use P3 directly. |
| 12 | `(Swift.String) -> Swift.Void` (closure) | `null` (not handled) | N/A (not NamedTypeSpec) | N/A (not bound generic) | `"Action<string>"` (via GetClosureCSharpType, which recurses through P4 for args) | **INTENTIONAL**: P1 doesn't handle closures; ClosureHandler in WrapperSignatureBuilder handles them |
| 13 | `Swift.Optional<(Swift.String) -> Swift.Void>` (optional closure) | `null` (Optional<Closure> deliberately excluded) | N/A | N/A (IsOptionalClosure → IsBoundGeneric returns false) | Depends — if not caught by ClosureHandler, falls to BoundGenericsHandler → `"Swift.SwiftOptional<object>"` (closure→object in TranslateTypeSpecToCSharp) | **INTENTIONAL**: ClosureHandler intercepts in WrapperSignatureBuilder before P1/P3 |
| 14 | `Swift.Array<any Describable>` (array of existential) | `null` (P1 returns null because typeTranslator on inner existential returns AnyType or interface, depends on P2 callback) | If inner existential resolves to `"IDescribable"`, P2 returns `"IDescribable"`. If "object" → AnyType placeholder (ContainsPlaceholder skips method). | `"Swift.SwiftArray<Swift.AnyType>"` (existential→AnyType in generic arg) | `"IEnumerable<IDescribable>"` (P1 with P4 as typeTranslator recursing into existential) | **DIVERGES**: P3 loses the protocol identity. P4 preserves it. P2 behavior depends on whether ExistentialHandler returns "object" (Any protocol → method skipped) or interface name. |
| 15 | `Swift.Array<SomeClass>` (class type arg) | P1: `"IEnumerable<SomeClass>"` (if typeTranslator resolves SomeClass) or `"IEnumerable<Swift.AnyType>"` (if SomeClass not in TypeDB) | Resolves SomeClass from TypeDatabase | `"Swift.SwiftArray<SomeClass>"` | `"IEnumerable<SomeClass>"` (via P1 with recursive P4) | **INTENTIONAL** for non-AnyType case. **BUG RISK** if SomeClass is ObjC-bridged: HasNonSwiftObjectGenericArg returns true and the method is skipped before reaching any conversion path. |
| 16 | Non-frozen struct as param (e.g., `MyModule.Config`) | `null` (not convertible) | Returns `"Config"` (from TypeRecord) | N/A (not generic) | `"Config"` (from TypeRecord) | **No divergence** — all paths agree via TypeRecord |
| 17 | Enum with associated values as return (e.g., `MyModule.Result`) | `null` | Returns `"Result"` (from TypeRecord) | N/A | `"Result"` (from TypeRecord) | **No divergence** |
| 18 | `Foundation.URL` (native remapped) | `null` (not idiomatic) | Returns `"Swift.URL"` (from TypeRecord; native remap is separate check in WrapperSignatureBuilder) | N/A | `"Swift.URL"` (from TypeRecord; native remap handled separately in ProtocolProxyEmitter.InterfaceImpl) | **BUG RISK**: WrapperSignatureBuilder applies native remap (`"Foundation.NSUrl"`); P4 does NOT apply native remap in GetCSharpTypeName (only in GetInterfaceCompatiblePropertyTypeName for properties, and in EmitMethodImplementation for explicit method return handling). If P4 is used for a method param, it may return `"Swift.URL"` while the interface declares `"Foundation.NSUrl"`. |

### 2.2 Key Divergence Summary

| Divergence | Paths | Root Cause | Risk Level |
|-----------|-------|------------|------------|
| `isParameter` always true in P4 | P4 vs P1 (ProtocolHandler) | `GetCSharpTypeName` passes `isParameter: true` to `GetIdiomaticCSharpType`. For return types that should be `IReadOnlyList<T>`, proxy emits `IEnumerable<T>`. If the protocol interface uses `IReadOnlyList<T>` (via ProtocolHandler using `isParameter: false`), CS0535. | **HIGH** — affects Array<T> returns in protocol members |
| Existentials → AnyType in P3 | P3 vs P2/P4 | BoundGenericsHandler.TranslateTypeSpecToCSharp returns AnyType for all existentials because ExistentialContainer doesn't implement ISwiftObject (required generic constraint) | **MEDIUM** — intentional for generic args but dangerous when P3 is called directly outside generics |
| Native type remapping missing from P4 | P4 vs WrapperSignatureBuilder | ProtocolProxyEmitter.GetCSharpTypeName doesn't check HasNativeTypeRemapping; it handles it only in the explicit method/property emission methods | **MEDIUM** — affects URL/Data in protocol proxy method params |
| Optional<Array<T>> returns | P4 vs P1(return) | P4 calls P1 with isParameter=true, gets `IEnumerable<T>?` instead of `IReadOnlyList<T>?` | **HIGH** — same as row 1, but nested under Optional |

---

## 3. Hard Marshalling Cases for TypeProjectionFactory

These are the most complex marshalling sequences in the codebase, ordered by complexity. For each, I document what the code does and what `ITypeProjection` would need to capture.

### Case 1: `IDictionary<string, string>` parameter (String keys + String values)

**Swift type:** `[String: String]` (Dictionary<String, String>)

**Trigger:** `EmitTypeConversions` in WrapperEmitter.Marshalling.cs:333-384

**Emitted C# wrapper code:**
```csharp
var paramConverted = param.Select(kvp => new KeyValuePair<SwiftString, SwiftString>(
    new SwiftString(kvp.Key), new SwiftString(kvp.Value))).ToList();
SwiftDictionary<SwiftString, SwiftString> paramSwiftInner;
try { paramSwiftInner = SwiftDictionary<SwiftString, SwiftString>.FromDictionary(paramConverted); }
finally { foreach (var _item in paramConverted) { _item.Key.Dispose(); _item.Value.Dispose(); } }
using var paramSwift = paramSwiftInner;
using PayloadBuffer<IntPtr> paramDisposable = paramSwift.PayloadBuffer;
IntPtr paramBuf = paramDisposable.Buffer;
```

**P/Invoke signature:** `IntPtr paramBuf`

**ITypeProjection adequacy:** `string GetParameterConversion(paramName)` is **completely insufficient**. This case requires:
- Multiple statements with temporaries (`paramConverted`, `paramSwiftInner`)
- Try/finally for disposal of intermediate values
- `using` declarations for two separate levels of disposal
- A buffer extraction step producing the actual P/Invoke argument

**What ITypeProjection needs:**
```csharp
interface ITypeProjection {
    // Instead of a single string, need a structured marshalling plan:
    MarshalPlan GetParameterMarshalPlan(string paramName);
}

record MarshalPlan {
    List<string> SetupStatements;      // All statements before the P/Invoke call
    string PInvokeArgExpression;        // The expression passed to P/Invoke
    List<string> CleanupStatements;     // Finally-block statements
    List<string> DisposableNames;       // Variables needing 'using' scoping
}
```

### Case 2: `Optional<IDictionary<string, IReadOnlyList<string>>>?` parameter

**Swift type:** `[String: [String]]?`

**Trigger:** `EmitTypeConversions` in WrapperEmitter.Marshalling.cs:458-519 (Optional + Dictionary + nested Array with String elements)

**Emitted C# wrapper code:**
```csharp
SwiftOptional<SwiftDictionary<SwiftString, SwiftArray<SwiftString>>> paramSwiftInner;
if (param is {} paramValue)
{
    var paramConverted = paramValue.Select(kvp => new KeyValuePair<SwiftString, SwiftArray<SwiftString>>(
        new SwiftString(kvp.Key),
        SwiftArray<SwiftString>.FromEnumerable(kvp.Value.Select(e => new SwiftString(e))))).ToList();
    SwiftDictionary<SwiftString, SwiftArray<SwiftString>> paramDictInner;
    try { paramDictInner = SwiftDictionary<SwiftString, SwiftArray<SwiftString>>.FromDictionary(paramConverted); }
    finally { foreach (var _item in paramConverted) { _item.Key.Dispose(); _item.Value.Dispose(); } }
    try { paramSwiftInner = SwiftOptional<SwiftDictionary<SwiftString, SwiftArray<SwiftString>>>.NewSome(paramDictInner); }
    finally { paramDictInner.Dispose(); }
}
else { paramSwiftInner = SwiftOptional<SwiftDictionary<SwiftString, SwiftArray<SwiftString>>>.NewNone(); }
using var paramSwift = paramSwiftInner;
using PayloadBuffer<IntPtr> paramDisposable = paramSwift.PayloadBuffer;
IntPtr paramBuf = paramDisposable.Buffer;
```

**ITypeProjection adequacy:** A simple interface is **grossly insufficient**. This is 15+ lines of emitted code with 3 levels of nesting, 4 temporaries, 3 disposal scopes, conditional branching (Optional none/some), and nested try/finally blocks.

**What ITypeProjection needs:** Recursive composition. The Optional projection wraps a Dictionary projection, which wraps Key=String + Value=Array<String> projections. Each level needs its own setup/cleanup. The composition must:
1. Generate unique variable names per nesting level
2. Track which temporaries need disposal
3. Compose disposal scopes correctly (inner before outer)
4. Handle the Optional none/some branch

### Case 3: String return via indirect result

**Swift type:** `func getName() -> String` (non-frozen struct method)

**Trigger:** `EmitTypeConvertedIndirectReturn` in WrapperEmitter.Return.cs:490-496

**Emitted C# wrapper code:**
```csharp
var swiftResult = SwiftMarshal.MarshalFromSwift<SwiftString>(new IntPtr(swiftIndirectResult.Value));
return swiftResult.ToString();
```

**P/Invoke signature:** `void PInvoke_GetName(SwiftIndirectResult swiftIndirectResult, SwiftSelf self)`

**ITypeProjection adequacy:** `string GetReturnConversion(resultName)` would return `"SwiftMarshal.MarshalFromSwift<SwiftString>(new IntPtr(swiftIndirectResult.Value)).ToString()"` — but this ignores that the P/Invoke return type is `void` (indirect result), and the result variable is `swiftIndirectResult`, not `result`. The projection needs to know whether indirect result is in play.

**What ITypeProjection needs:** Awareness of the return strategy (direct vs indirect result vs buffer).

### Case 4: Optional existential return with proxy wrapping

**Swift type:** `func getProcessor() -> (any ImageProcessing)?`

**Trigger:** `EmitTypeConvertedReturn` in WrapperEmitter.Return.cs:401-438 → optional path → existential check

**Emitted C# wrapper code:**
```csharp
var swiftResult = SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Runtime.ExistentialContainer1>>(new IntPtr(&result));
if (swiftResult.Case == Swift.SwiftOptionalCases.None) return null;
return new ImageProcessingProxy(swiftResult.Some);
```

**P/Invoke return type:** `Swift.SwiftOptional<Swift.Runtime.ExistentialContainer1>.Buffer`

**Public return type:** `IImageProcessing?`

**ITypeProjection adequacy:** `string GetReturnConversion(result)` can't express this because:
1. The marshal type (`SwiftOptional<ExistentialContainer1>`) differs from both the P/Invoke type (Buffer) and the public type (IImageProcessing?)
2. The conversion requires checking a discriminant (`.Case`)
3. The proxy construction (`new ...Proxy()`) requires knowing the proxy class name
4. The `new IntPtr(&result)` address-of depends on whether it's a Buffer or IntPtr

### Case 5: Closure return with non-frozen struct parameters

**Swift type:** `func getTransform() -> (Config) -> Result` where Config is non-frozen

**Trigger:** `EmitReturnMethod` → `ClosureEmitter.EmitClosureReturnMarshallingWithNonFrozenParams`

**Emitted C# wrapper code (from ClosureEmitter.StructParams.cs):**
```csharp
if (result.Function == IntPtr.Zero) return null;
return (Config config) => {
    var metadata = TypeMetadata.GetTypeMetadataOrThrow<Config>();
    IntPtr configBuffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
    try {
        metadata.ValueWitnessTable->InitializeWithCopy((void*)configBuffer, (void*)config.Payload.DangerousGetHandle(), metadata);
        // ... call through function pointer with configBuffer ...
        metadata.ValueWitnessTable->Destroy((void*)configBuffer, metadata);
    }
    finally {
        NativeMemory.Free((void*)configBuffer);
    }
};
```

**ITypeProjection adequacy:** Completely insufficient. This generates a lambda body with native memory allocation, value witness table calls, and try/finally cleanup. The projection would need to generate an entire anonymous function body.

### Case 6: `IEnumerable<IDescribable>` parameter (array of existential)

**Swift type:** `[any Describable]`

**Trigger:** `EmitTypeConversions` in WrapperEmitter.Marshalling.cs:297-307

**Emitted C# wrapper code:**
```csharp
var paramContainers = param.Select(i =>
    ((Swift.Runtime.ISwiftExistentialConvertible<Swift.Runtime.ExistentialContainer1>)i).GetExistentialContainer());
using var paramSwift = SwiftArray<Swift.Runtime.ExistentialContainer1>.FromEnumerable(paramContainers);
using PayloadBuffer<IntPtr> paramDisposable = paramSwift.PayloadBuffer;
IntPtr paramBuf = paramDisposable.Buffer;
```

**ITypeProjection adequacy:** Multi-statement. Needs to express: existential extraction per element via Select(), then array conversion, then buffer extraction.

### Case 7: Async method return with tuple containing String

**Swift type:** `func fetch() async -> (String, Int)`

**Trigger:** `EmitAsync` in WrapperEmitter.Async.cs — generates Swift wrapper + C# callback

**Emitted Swift wrapper:**
```swift
@_silgen_name("SBW_fetch_abc123")
public func SBW_fetch_abc123(
    _ callback: @convention(c) (UnsafeRawPointer, UnsafeRawPointer) -> Void,
    _ errorCallback: @convention(c) (UnsafeRawPointer) -> Void,
    _ handle: IntPtr
) {
    Task {
        do {
            let result = try await self.fetch()
            let ptr = UnsafeMutablePointer<(String, Int)>.allocate(capacity: 1)
            ptr.initialize(to: result)
            callback(UnsafeMutableRawPointer(ptr), handle)
        } catch {
            errorCallback(handle)
        }
    }
}
```

**Emitted C# callback:**
```csharp
[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
static void Callback_fetch(IntPtr resultPtr, IntPtr holderHandle) {
    var holder = (object[])GCHandle.FromIntPtr(holderHandle).Target!;
    var tcs = (TaskCompletionSource<(string, nint)>)holder[0];
    try {
        var raw = *(ValueTuple<SwiftString.Buffer, nint>*)resultPtr;
        var elem0 = SwiftMarshal.MarshalFromSwift<SwiftString>(new IntPtr(&raw.Item1)).ToString();
        var elem1 = raw.Item2;
        tcs.SetResult((elem0, elem1));
    } finally {
        NativeMemory.Free((void*)resultPtr);
        // cleanup holder...
    }
}
```

**ITypeProjection adequacy:** The async pattern requires generating TWO files (Swift + C#), a callback method, holder management, and per-element tuple marshalling. No simple interface can express this.

### Case 8: `(any Protocol)?` parameter with container extraction

**Swift type:** `func process(_ handler: (any ImageProcessing)?)`

**Trigger:** `EmitTypeConversions` in WrapperEmitter.Marshalling.cs:386-404

**Emitted C# wrapper code:**
```csharp
using var handlerSwift = handler is {} handlerValue
    ? SwiftOptional<ExistentialContainer1>.NewSome(
        ((Swift.Runtime.ISwiftExistentialConvertible<ExistentialContainer1>)handlerValue).GetExistentialContainer())
    : SwiftOptional<ExistentialContainer1>.NewNone();
using PayloadBuffer<IntPtr> handlerDisposable = handlerSwift.PayloadBuffer;
IntPtr handlerBuf = handlerDisposable.Buffer;
```

**ITypeProjection adequacy:** Multi-statement with conditional branch and interface cast. Needs to compose Optional wrapping with existential extraction.

### 3.1 ITypeProjection Interface Redesign

Based on the cases above, the simple `string GetParameterConversion(paramName)` / `string GetReturnConversion(resultName)` interface proposed in the findings document is **insufficient for approximately half the real-world cases**. The hard cases require:

1. **Multi-statement emission** with temporaries (Cases 1, 2, 6, 8)
2. **Disposal scope management** — `using` declarations, try/finally blocks (Cases 1, 2)
3. **Conditional branches** — Optional none/some patterns (Cases 2, 4, 8)
4. **Recursive composition** — nested containers (Case 2: Optional<Dict<String, Array<String>>>)
5. **Return strategy awareness** — direct return vs indirect result vs async callback (Cases 3, 7)
6. **Lambda body generation** — closure return marshalling (Case 5)
7. **Cross-file emission** — async requires Swift wrapper + C# callback (Case 7)

**Revised interface proposal:**

```csharp
/// A marshalling plan for a single parameter or return value.
/// Captures all the emission decisions without generating strings.
record MarshalPlan
{
    /// Statements emitted before the P/Invoke call (setup, conversion, disposal scope)
    List<MarshalStatement> SetupStatements { get; }

    /// The expression or variable name passed to/from P/Invoke
    string PInvokeExpression { get; }

    /// Statements emitted after the P/Invoke call (writeback, cleanup)
    List<MarshalStatement> CleanupStatements { get; }

    /// Variables that need 'using' scope (automatic disposal)
    List<(string Type, string Name)> UsingDeclarations { get; }

    /// Whether this plan requires an unsafe context
    bool RequiresUnsafe { get; }

    /// Whether this plan requires a fixed block
    bool RequiresFixed { get; }
}

abstract record MarshalStatement
{
    record Line(string Code) : MarshalStatement;
    record Block(string Header, List<MarshalStatement> Body) : MarshalStatement;  // if/else, try/finally
    record Using(string Type, string Name, string InitExpression) : MarshalStatement;
}

/// Produces marshal plans for a given Swift type.
interface ITypeProjection
{
    /// The C# type for public API signatures
    string PublicType { get; }

    /// The C# type for P/Invoke signatures
    string PInvokeType { get; }

    /// P/Invoke attributes (e.g., [MarshalAs(UnmanagedType.U1)])
    string? PInvokeAttribute { get; }

    /// Generate the marshalling plan for this type as a parameter
    MarshalPlan GetParameterPlan(string paramName);

    /// Generate the marshalling plan for this type as a return value
    MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy);

    /// Whether this type needs a Swift wrapper function
    bool RequiresSwiftWrapper { get; }

    /// Generate the Swift wrapper code (if needed)
    string? GetSwiftWrapperCode(SwiftWrapperContext context);
}

enum ReturnStrategy { Direct, IndirectResult, OutBuffer, AsyncCallback }
```

The key insight: **the projection must produce a plan (structured data), not a string**. The plan is then rendered to code by a separate emitter. This separation enables:
- Testing the plan without string comparison
- Composing plans for nested types
- Optimizing disposal scopes across multiple parameters
- Generating both C# and Swift code from the same plan
