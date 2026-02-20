# Binding Errors — Third-Party Library Validation

Last updated: 2026-02-20 | Baseline: 13/32 passed, 19 failed, 0 no output

## Validation Summary

| Library | Targets | Result | Error Count | Error Categories |
|---------|---------|--------|-------------|-----------------|
| Alamofire | 1 | **Fail** | 11 | Optional protocol, proxy receiver |
| BlinkID | 1 | **Pass** | 0 | — |
| BlinkIDUX | 1 | **Fail** | 2 | Optional protocol property, closure AnyType fallback |
| BRLMPrinterKit | 1 | **Pass** | 0 | — |
| CryptoSwift | 1 | **Pass** | 0 | — |
| GRDB | 1 | **Fail** | 8 | Optional protocol, proxy receiver |
| KeychainAccess | 1 | **Pass** | 0 | — |
| Kingfisher | 1 | **Fail** | 32 | Optional protocol, proxy receiver |
| Lottie | 1 | **Fail** | 3 | Optional protocol property (2), protocol setter PayloadBuffer (1) |
| Mappedin | 1 | **Fail** | 4 | Optional protocol property (4) |
| MicroblinkPlatform | 1 | **Pass** | 0 | — |
| Mixpanel | 1 | **Fail** | 9 | Optional protocol property (3), protocol setter PayloadBuffer (3), dictionary protocol value (1), AnyError optional (1), closure AnyType fallback (1) |
| Nuke | 1 | **Fail** | 5 | Optional protocol property (3), protocol setter PayloadBuffer (2) |
| RxSwift | 1 | **Fail** | 5 | Closure AnyType fallback |
| SkeletonView | 1 | **Fail** | 9 | Optional protocol property, closure AnyType fallback, proxy receiver |
| SmartCardIO | 1 | **Pass** | 0 | — |
| SnapKit | 1 | **Pass** | 0 | — |
| Starscream | 1 | **Fail** | 9 | Optional protocol property, closure AnyType fallback, proxy receiver |
| Stripe | 14 | **Mixed** | 80 | See Stripe breakdown below |
| **Total** | **32** | **13 pass** | **177** | |

### Stripe Breakdown

| Framework | Result | Errors | Primary Pattern |
|-----------|--------|--------|-----------------|
| Stripe | Pass | 0 | — |
| StripeApplePay | Fail | 6 | Optional protocol property + setter |
| StripeCameraCore | Fail | 2 | Optional protocol property + setter |
| StripeCardScan | Pass | 0 | — |
| StripeConnect | Fail | 14 | Optional protocol property + setter |
| StripeCore | Fail | 4 | Proxy receiver |
| StripeCryptoOnramp | **Pass** | 0 | — |
| StripeFinancialConnections | Pass | 0 | — |
| StripeIdentity | Pass | 0 | — |
| StripeIssuing | Pass | 0 | — |
| StripePayments | Fail | 4 | Optional protocol |
| StripePaymentSheet | Fail | 8 | Optional protocol |
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

### 4. Closure Parameter as `AnyType` in P/Invoke (CS1503) — FIXED (Session B)

**Affected:** Nuke (5), Mixpanel (1), Mappedin (4), StripeCryptoOnramp (1) — **11 errors total** (was)

**Fix:** Added unsupported closure parameter check to `MemberEmissionValidator.ShouldSkipMethodEmission` (before the constructor early-return, so both methods and constructors are covered) and `CanEmitMethod` (for conformance validation). Uses `ClosureHandler.IsSupportedClosure()` to detect unsupported closures and returns `SkipReason.UnsupportedClosure` to skip the method/constructor entirely.

**Result:** Methods/constructors with unmarshallable closure parameters are now cleanly skipped instead of emitting broken code. API surface reduction is expected — these closures require expanded Cdecl wrapper support to emit correctly. Skipped members are tracked in `binding-report.json` under `SkipReason.UnsupportedClosure`.

**Residual:** The skipped APIs are visible in the binding report. Future work to expand closure Cdecl wrapper support (beyond the current 8 constraints) would recover these APIs.

---

### 5. Optional\<Array\<T\>\> Property Projection Inconsistency (CS0266) — FIXED (Session B)

**Affected:** BlinkID (1)

**Fix:** Added `GetIdiomaticCSharpType` fallback in 3 property type projection sites when `TypeProjectionFactory.Project()` returns `null` (can't project user-defined generic types). The fallback uses the same `TranslateTypeSpecWithGenerics` helper as the getter/setter body conversion, ensuring the property declaration type matches.

- `PropertyHandler.Emit`: After factory returns null, tries `GetIdiomaticCSharpType` with `typeTranslator`
- `MemberEmissionValidator.CanEmitProperty`: Same idiomatic override after `TranslateBoundGenericTypeToCSharp`
- `ProtocolConformanceValidator.GetInterfacePropertyType`: Same override in bound generic branch

`TranslateTypeSpecWithGenerics` promoted from `private static` to `internal static` for shared access.

**Result:** BlinkID now compiles cleanly (1 → 0 errors). Also covers `Optional<Dictionary<K,V>>` with generic key/value types (same mechanism).

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
| 1 | Generator crash (Self/repeat) | **FIXED** (A) | 0 (was 4 libs blocked) | — |
| 2 | Optional\<any Protocol\> property get/set | Open | ~69 | 9+ |
| 3 | Optional\<UnsupportedClosure\> bare SwiftOptional | **FIXED** (A) | 0 (was 15) | — |
| 4 | Closure param AnyType fallback | **FIXED** (B) | 0 (was ~30+, skipped) | — |
| 5 | Optional\<Array\<T\>\> projection inconsistency | **FIXED** (B) | 0 (was 1) | — |
| 6 | Generic protocol existential missing type arg | **FIXED** (A) | 0 (was 3) | — |
| 7 | Enum case/property name collision | **FIXED** (A) | 0 (was 1) | — |
| 8 | Protocol proxy receiver type mismatch | Open | ~10+ | 4+ |

**Notes:**
- Session A unblocked 4 crashed libraries, revealing errors from Categories 2, 4, 8 (115 → 220 visible).
- Session B fixed Categories 4 and 5 (220 → 177 visible). Category 4 fix skips methods with unsupported closure params (API surface reduction — correct behavior, not a regression).

## Fix Complexity (Remaining)

| # | Category | Files | Runtime Changes? | Complexity | Dependencies |
|---|----------|-------|-----------------|------------|--------------|
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

### Session B: Projection Fixes — Optional Containers and Closure Marshalling — COMPLETE

**Categories:** 4, 5 | **Actual changes:** ~50 lines across 3 files

| Fix | What | Where |
|-----|------|-------|
| Cat 4 | Added unsupported closure param check to `ShouldSkipMethodEmission` (before ctor return) and `CanEmitMethod` (after bound generics) — skips methods/constructors with unmarshallable closure params | `MemberEmissionValidator.cs` |
| Cat 5 | Added `GetIdiomaticCSharpType` fallback when `TypeProjectionFactory.Project()` returns null for `Optional<Array<UserType<T>>>`. Applied consistently to 3 property type projection sites. Promoted `TranslateTypeSpecWithGenerics` to `internal static`. | `PropertyHandler.cs`, `MemberEmissionValidator.cs`, `ProtocolConformanceValidator.cs` |

**Result:** 220 → 177 total errors (43 eliminated). BlinkID and StripeCryptoOnramp now pass (11/32 → 13/32). Category 4 fix skips methods with unsupported closure params — API surface reduction is expected behavior (these closures need expanded Cdecl wrapper support to emit correctly). All tests green, golden files unchanged.

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
| B | 4, 5 | **COMPLETE** | 43 errors eliminated (220 → 177) | 13/32 |
| C | 2, 8 | Pending | ~80+ optional protocol + proxy receiver | TBD |

**Notes:**
- Session A unblocked 4 crashed libraries, inflating visible error count (115 → 220) as expected.
- Session B eliminated 43 errors and flipped 2 libraries to pass (BlinkID, StripeCryptoOnramp).
- Session C addresses the remaining ~177 errors. Categories 2 and 8 account for the vast majority — fixing them should bring pass rate to ~27/32.
