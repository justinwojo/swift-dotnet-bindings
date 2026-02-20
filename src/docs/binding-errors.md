# Binding Errors — Third-Party Library Validation

Last updated: 2026-02-20 | Baseline: 11/32 passed, 21 failed, 0 no output

## Validation Summary

| Library | Targets | Result | Error Count | Error Categories |
|---------|---------|--------|-------------|-----------------|
| Alamofire | 1 | **Fail** | 20 | Closure AnyType fallback, Optional protocol, proxy receiver |
| BlinkID | 1 | **Fail** | 1 | Optional\<Array\<T\>\> property projection |
| BlinkIDUX | 1 | **Fail** | 2 | Optional protocol property, closure AnyType fallback |
| BRLMPrinterKit | 1 | **Pass** | 0 | — |
| CryptoSwift | 1 | **Pass** | 0 | — |
| GRDB | 1 | **Fail** | 8 | Closure AnyType fallback, Optional protocol, proxy receiver |
| KeychainAccess | 1 | **Pass** | 0 | — |
| Kingfisher | 1 | **Fail** | 32 | Closure AnyType fallback, Optional protocol, proxy receiver |
| Lottie | 1 | **Fail** | 3 | Optional protocol property (2), protocol setter PayloadBuffer (1) |
| Mappedin | 1 | **Fail** | 8 | Optional protocol property (4), closure AnyType fallback (4) |
| MicroblinkPlatform | 1 | **Pass** | 0 | — |
| Mixpanel | 1 | **Fail** | 9 | Optional protocol property (3), protocol setter PayloadBuffer (3), closure AnyType fallback (1), dictionary protocol value (1), AnyError optional (1) |
| Nuke | 1 | **Fail** | 10 | Optional protocol property (3), protocol setter PayloadBuffer (2), closure AnyType fallback (5) |
| RxSwift | 1 | **Fail** | 6 | Closure AnyType fallback |
| SkeletonView | 1 | **Fail** | 9 | Optional protocol property, closure AnyType fallback, proxy receiver |
| SmartCardIO | 1 | **Pass** | 0 | — |
| SnapKit | 1 | **Pass** | 0 | — |
| Starscream | 1 | **Fail** | 9 | Optional protocol property, closure AnyType fallback, proxy receiver |
| Stripe | 14 | **Mixed** | 103 | See Stripe breakdown below |
| **Total** | **32** | **11 pass** | **220** | |

### Stripe Breakdown

| Framework | Result | Errors | Primary Pattern |
|-----------|--------|--------|-----------------|
| Stripe | Pass | 0 | — |
| StripeApplePay | Fail | 6 | Optional protocol property + setter |
| StripeCameraCore | Fail | 2 | Optional protocol property + setter |
| StripeCardScan | Pass | 0 | — |
| StripeConnect | Fail | 14 | Optional protocol property + setter |
| StripeCore | Fail | 4 | Closure AnyType fallback, proxy receiver |
| StripeCryptoOnramp | Fail | 1 | Closure Func\<\> not marshalled to ABI |
| StripeFinancialConnections | Pass | 0 | — |
| StripeIdentity | Pass | 0 | — |
| StripeIssuing | Pass | 0 | — |
| StripePayments | Fail | 4 | Closure AnyType fallback, Optional protocol |
| StripePaymentSheet | Fail | 10 | Closure AnyType fallback, Optional protocol |
| StripePaymentsUI | Fail | 8 | Optional protocol property + setter |
| StripeUICore | Fail | 34 | Optional protocol property + setter, proxy receiver mismatch |

---

## Error Categories

### 1. Generator Crash: Unqualified Type Names (`Self`, `repeat`) — FIXED (Session A)

**Affected:** Alamofire, GRDB, Kingfisher (crash on `Self`), StripeCore (crash on `repeat`)

**Fix:** Added explicit `name is "Self" or "repeat"` guard in `TypeProjectionFactory.ProjectNamedType()` after the `IsGenericTypeParameter()` check. Returns `null` (unsupported projection). Placed before `IsAny` existential routing to avoid intercepting unqualified existential spellings.

**Result:** All 4 libraries now produce bindings (with remaining errors from other categories).

---

### 2. Optional\<any Protocol\> Property Getter/Setter (CS0266 + CS1061)

**Affected:** Lottie (2), Nuke (3), Mixpanel (6), Mappedin (4), StripeApplePay (4), StripeCameraCore (2), StripeConnect (14), StripePaymentsUI (8), StripeUICore (26) — **~69 errors total**

This is the **dominant error pattern**, accounting for ~60% of all compile errors. Getter and setter errors always appear in pairs on the same property.

#### Getter (CS0266)

**Symptom:** `Cannot implicitly convert type 'SwiftOptional<AnyType>' to 'ISomeProtocol?'`

**Example (Nuke):**
```csharp
public IDataCaching? DataCache
{
    get {
        // Returns SwiftOptional<AnyType> but property type is IDataCaching?
        return SwiftMarshal.MarshalFromSwift<SwiftOptional<AnyType>>(new IntPtr(&result));
    }
}
```

**Root cause:** When a class property has type `Optional<any SomeProtocol>`, the return type is correctly projected to `ISomeProtocol?` (via existential container elimination), but the getter body still marshals as `SwiftOptional<AnyType>` — there is no runtime conversion path from the existential container to the C# protocol interface.

#### Setter (CS1061)

**Symptom:** `'ISomeProtocol' does not contain a definition for 'PayloadBuffer'`

**Example (Nuke):**
```csharp
set {
    // Tries to call .PayloadBuffer on the interface type
    using PayloadBuffer<IntPtr> valueDisposable = value.PayloadBuffer;
}
```

**Root cause:** The setter parameter is typed as the projected interface (`ISomeProtocol?`), but the setter body assumes it's an `ExistentialContainer`-based type with a `.PayloadBuffer` property. C# interfaces don't have `PayloadBuffer`.

**Known limitation:** Documented in MEMORY.md as deferred: "Optional\<any Protocol\> in closures uses `SwiftOptional<ExistentialContainer{N}>` (deferred — `SwiftMarshal.MarshalFromSwift` doesn't support interfaces/object at runtime)." The same gap applies to property getters/setters.

**Fix direction:** Requires runtime support for marshalling between `ExistentialContainer` and C# protocol interfaces. The getter needs to extract the existential container's payload and wrap it in the protocol proxy class. The setter needs to construct an existential container from the protocol proxy.

---

### 3. Optional\<UnsupportedClosure\> Emits Bare `SwiftOptional` (CS0305) — FIXED (Session A)

**Affected:** SkeletonView (2), Starscream (1), StripePayments (2), StripePaymentSheet (10) — **15 errors total**

**Fix:** Added `IsBareGenericTypeName` guard in `WrapperSignatureBuilder.HandleArguments()` and `HandleReturnType()` type-record fallback paths in `MethodSignature.cs`. When the resolved type name is bare generic (e.g., `SwiftOptional` without `<T>`), emits `AnyType` instead. Preserves `inoutModifier` in the parameter path.

**Result:** Bare `SwiftOptional` CS0305 errors eliminated. Some previously-hidden errors now visible (closure AnyType fallback, Optional protocol) since methods are no longer blocked by the bare generic.

---

### 4. Closure Parameter as `AnyType` in P/Invoke (CS1503)

**Affected:** Nuke (5), Mixpanel (1), Mappedin (4), StripeCryptoOnramp (1) — **11 errors total**

**Symptom:** `Cannot convert from 'Func<X, Y>' to 'AnyType'`

**Example (Nuke — DataLoader constructor):**
```csharp
// Public signature:
public DataLoader(NSUrlSessionConfiguration config, Func<NSUrlResponse, AnyError?> validate)

// P/Invoke:
private static partial void PInvoke_init_C8DDD010(..., Swift.AnyType validate);

// Body passes Func directly to P/Invoke — type mismatch:
PInvoke_init_C8DDD010(swiftIndirectResult, configHandle, validate);
```

**Root cause:** When a closure has non-primitive parameter types (non-frozen enums, protocol types, Optional returns), it doesn't qualify for the `_cdecl` closure wrapper path (which requires primitive args only — one of the 8 closure Cdecl wrapper constraints). The P/Invoke falls back to `AnyType`, but the public API correctly emits `Func<...>` or `Action<...>`. The generated method body passes the C# delegate directly to the P/Invoke with no marshalling bridge in between.

**Fix direction:** Either expand the Cdecl closure wrapper to support non-primitive types, or emit the parameter as `AnyType` in the public API as well (matching the P/Invoke), or skip the method entirely when the closure cannot be marshalled.

---

### 5. Optional\<Array\<T\>\> Property Projection Inconsistency (CS0266)

**Affected:** BlinkID (1)

**Symptom:** `Cannot implicitly convert type 'IReadOnlyList<VehicleClassInfo<T0>>' to 'SwiftOptional<SwiftArray<VehicleClassInfo<T0>>>'`

**Example:**
```csharp
public SwiftOptional<SwiftArray<VehicleClassInfo<T0>>> VehicleClassesInfo
{
    get {
        using var __ret = VehicleClassesInfo_Get();
        // Returns IReadOnlyList<T>? but property type is SwiftOptional<SwiftArray<T>>
        return (__ret.Case == SwiftOptionalCases.None
            ? (IReadOnlyList<VehicleClassInfo<T0>>?)null
            : __ret.Some);
    }
}
```

**Root cause:** The property's declared return type uses the raw ABI types (`SwiftOptional<SwiftArray<T>>`), but the getter body applies the idiomatic array projection (`SwiftArray` → `IReadOnlyList`). The two are inconsistent — either the property type should be `IReadOnlyList<T>?` (fully idiomatic) or the getter body should return the raw `SwiftOptional` without unwrapping.

**Fix direction:** The property return type should be projected to `IReadOnlyList<VehicleClassInfo<T0>>?` to match the getter body's idiomatic projection.

---

### 6. Generic Protocol Existential Missing Type Argument (CS0305) — FIXED (Session A)

**Affected:** BlinkIDUX (3)

**Fix:** Added guard in `ExistentialHandler.GetPublicExistentialType()` — when a protocol `NamedTypeSpec` has `GenericParameters.Count > 0`, returns `AnyType` instead of emitting bare `IProtocol`. Uses `AnyType` (not `"object"`) to preserve API surface and avoid triggering member pruning in `MemberEmissionValidator` and `MethodHandler`.

**Result:** BlinkIDUX dropped from 3 to 2 errors. The CS0305 bare `IEventStream` is gone; remaining errors are different categories.

---

### 7. Enum Case/Property Name Collision (CS0102) — FIXED (Session A)

**Affected:** RxSwift (1)

**Fix:** In `EnumHandler.cs`: (1) Removed `IsStatic` restriction on property collision check — instance properties now also skipped when they collide with case constructor names. (2) Expanded `propertyNames` set passed to `HandleBaseDecl` for method collision detection to include case constructor names, `CaseTag`, `Tag`, `TryGet{CaseName}`, and simple case property names.

**Result:** RxSwift CS0102 eliminated. Remaining 6 errors are closure AnyType fallback (Category 4).

---

### 8. Protocol Proxy Receiver Type Mismatches (CS1503)

**Affected:** StripeUICore (3), Mixpanel (1) — **4 errors total**

**Symptom:** Various `CS1503` errors where protocol proxy receiver code has mismatched types between the public interface and the ABI marshalling layer.

**Sub-patterns:**

#### 8a. Protocol Array in Proxy Getter (StripeUICore)
```csharp
// Proxy getter for `elements: [any Element]`
var swiftResult = SwiftArray<AnyType>.FromEnumerable(result);
// `result` is IReadOnlyList<IElement> but FromEnumerable expects IEnumerable<AnyType>
```

#### 8b. Protocol/Closure Cross-Wiring in Proxy Callbacks (StripeUICore)
- `IElementDelegate` passed where `AnyType` expected
- `AnyType` received where `Action?` expected

#### 8c. Dictionary with Protocol Value Type (Mixpanel)
```csharp
// IMixpanelFlagDelegate.Track expects IReadOnlyDictionary<string, IMixpanelType>
// but proxy receiver produces IReadOnlyDictionary<string, AnyType>
var param1 = rawParam1.Some.AsProjected(k => k.ToString(), k => new SwiftString(k), v => v);
```

**Root cause:** Protocol proxy receivers marshal ABI types (`AnyType`, `ExistentialContainer`) but the public interface methods expect idiomatic C# types (`IProtocol`, `IReadOnlyList<IProtocol>`, `IReadOnlyDictionary<K, IProtocol>`). The conversion between ABI existential types and projected protocol interfaces is missing in the proxy receiver emission path.

**Fix direction:** Proxy receivers need conversion bridges that wrap `AnyType`/`ExistentialContainer` values in protocol proxy instances before passing them to the public interface implementation.

---

## Error Count by Category

| # | Category | Status | Errors | Libraries Affected |
|---|----------|--------|--------|-------------------|
| 1 | Generator crash (Self/repeat) | **FIXED** | 0 (was 4 libs blocked) | — |
| 2 | Optional\<any Protocol\> property get/set | Open | ~69 | 9+ |
| 3 | Optional\<UnsupportedClosure\> bare SwiftOptional | **FIXED** | 0 (was 15) | — |
| 4 | Closure param AnyType fallback | Open | ~30+ | 8+ |
| 5 | Optional\<Array\<T\>\> projection inconsistency | Open | 1 | 1 |
| 6 | Generic protocol existential missing type arg | **FIXED** | 0 (was 3) | — |
| 7 | Enum case/property name collision | **FIXED** | 0 (was 1) | — |
| 8 | Protocol proxy receiver type mismatch | Open | ~10+ | 4+ |

**Note:** Fixing Categories 1 and 3 unblocked 4 libraries and revealed previously-hidden errors in Categories 2, 4, and 8 — total visible error count increased from 115 to 220 even though the fixes are correct. This is expected: the crashed libraries (Alamofire, GRDB, Kingfisher, StripeCore) now produce output with errors from the remaining open categories.

## Fix Complexity (Remaining)

| # | Category | Files | Runtime Changes? | Complexity | Dependencies |
|---|----------|-------|-----------------|------------|--------------|
| 5 | Optional\<Array\<T\>\> projection | 1-2 | No | Moderate (20-60 lines) | None |
| 4 | Closure AnyType fallback | 1-2 | No | Moderate (20-50 lines) | None |
| 8 | Proxy receiver existential | 1-2 | No (emitter-only via ISwiftExistentialConvertible) | Moderate (30-60 lines) | Lays groundwork for Cat 2 |
| 2 | Optional\<any Protocol\> | 2-3 | Yes (SwiftMarshal) | Significant (60-100+ lines) | Benefits from Cat 8 |

## Suggested Fix Sessions

### Session A: Quick Wins — Guards, Gates, and Emission Fixes — COMPLETE

**Categories:** 1, 3, 6, 7 | **Actual changes:** ~40 lines across 4 files

| Fix | What | Where |
|-----|------|-------|
| Cat 1 | Explicit `name is "Self" or "repeat"` guard in `ProjectNamedType()` | `TypeProjectionFactory.cs` |
| Cat 3 | `IsBareGenericTypeName` guard in `HandleArguments()` and `HandleReturnType()` fallback | `MethodSignature.cs` |
| Cat 6 | `GenericParameters.Count > 0` guard returns `AnyType` in `GetPublicExistentialType()` | `ExistentialHandler.cs` |
| Cat 7 | Removed `IsStatic` restriction + expanded `propertyNames` with synthesized names | `EnumHandler.cs` |

**Result:** 4 crashes eliminated (0 no-output), 19 original errors fixed. Unblocked libraries reveal previously-hidden errors from Categories 2, 4, 8 — total visible error count rose from 115 to 220. Validation: 11/32 passed, 21 failed, 0 no output. All tests green, golden files unchanged.

---

### Session B: Projection Fixes — Optional Containers and Closure Marshalling

**Categories:** 4, 5 | **Estimated complexity:** ~80 lines across 2-4 files

These are emitter-only fixes for type projection inconsistencies — the P/Invoke and public API types don't match.

| Fix | What | Where |
|-----|------|-------|
| Cat 5 | Fix `Optional<Array<T>>` property return type: the property signature uses raw `SwiftOptional<SwiftArray<T>>` but the getter body returns idiomatic `IReadOnlyList<T>?`. Align them. | `WrapperEmitter.Return.cs` (3 Optional return paths) |
| Cat 4 | Fix closure params where public API emits `Func<...>` but P/Invoke emits `AnyType`. Either tighten `IsSupportedClosureParameterType` to reject non-blittable types, or emit `AnyType` in the public API to match. | `ClosureHandler.cs` |

**Expected impact:** Fixes 12 compile errors (11 closure AnyType + 1 Optional\<Array\>). Libraries affected: BlinkID, Nuke, Mixpanel, Mappedin, StripeCryptoOnramp.

**Validation:** Same as Session A. Check that the tightened closure gate doesn't regress the TestFramework coverage report.

---

### Session C: Protocol Existential Round-Tripping

**Categories:** 2, 8 | **Estimated complexity:** ~120 lines across 3-5 files (emitter + runtime)

This is the hardest session — it addresses the dominant error pattern (60% of all errors). Categories 2 and 8 share a common root: no mechanism to round-trip between `ExistentialContainer` (ABI) and C# protocol interfaces. Fix 8 first as the simpler emitter-only half, then tackle 2 which needs runtime support.

| Fix | What | Where |
|-----|------|-------|
| Cat 8 | Proxy receiver getters need to extract `ExistentialContainer` from `IProtocol` via `ISwiftExistentialConvertible` before `MarshalToSwiftBuffer`. Proxy array/dictionary receivers need element-level conversion. | `ProtocolProxyEmitter.Receivers.cs`, `ProtocolProxyEmitter.Helpers.cs` |
| Cat 2 | Property getters returning `Optional<any Protocol>`: unmarshal `SwiftOptional<ExistentialContainer>` and wrap in protocol proxy class. Property setters: extract `ExistentialContainer` from interface before P/Invoke. May need `SwiftMarshal` runtime support. | `WrapperEmitter.Return.cs`, `PropertyHandler.cs`, `SwiftMarshal.cs` (runtime) |

**Expected impact:** Fixes ~73 compile errors across 9 libraries. This is the single biggest win — should bring validation from ~18/32 → ~27/32 passed (assuming Sessions A+B already applied).

**Validation:** Same as above plus `run-runtime-tests.sh` if runtime files change. Verify protocol proxy scenarios still work in TestFramework.

---

### Session Summary

| Session | Categories | Status | Errors Fixed | Pass Rate |
|---------|-----------|--------|-------------|-----------|
| A | 1, 3, 6, 7 | **COMPLETE** | 19 errors + 4 crashes unblocked | 11/32 (0 crashes) |
| B | 4, 5 | Pending | ~30+ closure + 1 Optional\<Array\> | TBD |
| C | 2, 8 | Pending | ~80+ optional protocol + proxy receiver | TBD |

**Note:** Session A unblocked 4 libraries that now show errors from Categories 2, 4, 8 — this inflated the visible error count but is expected. Sessions B and C address the now-visible errors across all 21 failing libraries.
