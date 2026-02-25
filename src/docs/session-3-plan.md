# Session 3: Existential Default-Arg Bypass & Protocol Receiver Relaxation

**Created**: February 2026
**Revised**: February 2026 (post-Codex review — 5 findings addressed)
**Goal**: Unlock methods with existential-in-bound-generic params that have `hasDefaultArg: true`
**Primary unlock**: Mixpanel `track(event:properties:)` compiles (bypass omits `properties`)
**Secondary impact**: up to ~67 default-arg methods unblocked (void-only; upper bound), ~55 protocol interface methods recovered

---

## Codex Review Findings & Corrections

The original plan had 5 errors identified by Codex. This revision addresses each:

| # | Finding | Root Cause | Fix |
|---|---------|-----------|-----|
| 1 | Plan assumed `MemberEmissionValidator.CanEmitMethod` is the hook point for MethodHandler | MethodHandler performs its own existential gating inline (lines 478-537) and never calls `MemberEmissionValidator.CanEmitMethod` | **All bypass/relaxation changes target MethodHandler directly**, not MemberEmissionValidator |
| 2 | Plan overstated property support — PropertyHandler has its own unconditional existential skip | PropertyHandler line 228 calls `TryGetFirstExistentialTypeArgument` and returns immediately with no Array/Optional allowthrough | **Properties are explicitly out of scope** for Session 3. PropertyHandler needs its own relaxation pass (deferred) |
| 3 | Protocol receiver skip is in ProtocolHandler.cs, not MemberEmissionValidator | The "can't be marshalled in protocol receiver" message is at ProtocolHandler.cs:311 in the B9 gate | **3b targets ProtocolHandler.cs directly** for interface recovery |
| 4 | `IsSupportedExistential()` returns true for `Any` (0 protocols) — plan's allowthrough predicate was too permissive | Plan used `IsSupportedExistential()` alone, which passes `Any`. Contradicted its own "no Any" constraint | **All allowthrough predicates use `AllProtocolsHaveTypeRecords` + `GetPublicExistentialType != "object"`**, matching the existing Optional<any P> pattern |
| 5 | `IsSwiftDictionary()` already exists | `TypeConversionHandler.cs:82` already has `IsSwiftDictionary` | **Removed from task list** |

---

## Executive Summary

Session 3 addresses methods/properties using existential types inside bound generics — primarily `Dictionary<String, Any>` and `Dictionary<String, any Protocol>` — that are currently **completely skipped**. There are 447 UnsupportedExistential + 6 AnyTypeFallback = 453 members across 26 library targets.

**Revised scope**: After the Codex review, the realistic Session 3 scope is:
1. **3a**: Generalize `ExistentialBypassEmitter` from struct constructors to class/struct instance methods, wired into MethodHandler's inline existential gate
2. **3b**: Recover protocol interface methods currently blocked by ProtocolHandler's B9 existential receiver gate
3. **3c**: Audit skip count reduction

**Explicitly out of scope**: Full dictionary bridging (no `DictionaryExistentialProjection`), property support (PropertyHandler existential relaxation deferred), non-void return method bypass. Note: `Any`-value dictionary *parameters* are in scope for the default-arg bypass (the parameter is omitted entirely, so no bridging needed), but full `Any` dictionary *bridging* (passing values across the boundary) is out of scope.

---

## Swift ABI Analysis: Existential Containers & Dictionary Layout

### Existential Container Layout (64-bit)

```
+-------------------+  word 0
| payload[0]        |  <- For small types (<=3 words): inline value
| payload[1]        |  <- For large types: heap-allocated box pointer in [0]
| payload[2]        |
+-------------------+  word 3
| type metadata ptr |  <- Points to the concrete type's metadata
+-------------------+  word 4
| witness table 0   |  <- Protocol witness table (1 per conformance)
| witness table 1   |  <- (for multi-protocol compositions)
| ...               |
+-------------------+
```

- `Any` (zero protocols): `ExistentialContainer0` = 4 words = 32 bytes
- `any Protocol` (one protocol): `ExistentialContainer1` = 5 words = 40 bytes
- `any P1 & P2`: `ExistentialContainer2` = 6 words = 48 bytes

### Why Dictionary<String, any Protocol> Can't Use SwiftDictionary<K,V> Directly

`SwiftDictionary<TKey, TValue>` in C# requires `TValue : ISwiftObject` (via `TypeMetadata.GetTypeMetadataOrThrow<TValue>()`). `ExistentialContainer1` is a blittable struct and does NOT implement `ISwiftObject`. A full dictionary bridge would require a new `DictionaryExistentialProjection` — deferred.

### Why Default-Arg Bypass Works

For Mixpanel's `track(event:properties:)`, the `properties: [String: any MixpanelType]?` parameter has `hasDefaultArg: true`. By generating a Swift wrapper that omits this parameter, Swift fills in the default (`nil`). The C# consumer gets a `Track(string? event)` method that works.

---

## Sub-task 3a: Default-Arg Bypass for Methods

### Problem Statement

MethodHandler performs existential gating inline at two levels:
1. **Line 478**: `TryGetFirstUnsupportedExistentialTypeArgument` — catches existentials not in the supported set (hard block)
2. **Line 496 (B6)**: `TryGetFirstExistentialTypeArgument` — catches *supported* existentials in non-Array/non-Optional positions (e.g., Dictionary, Set)

Both gates immediately `return` (skip the method). There is no bypass attempt for non-constructor methods — `ExistentialBypassEmitter.TryEmitConstructorBypass` is only called in the constructor path (line 204).

### Design

Generalize `ExistentialBypassEmitter` to handle **instance methods on classes and structs**, then wire it into MethodHandler's non-constructor existential skip paths.

**Key difference from constructor bypass**:
- Constructors: Swift wrapper creates an instance, returns `UnsafeMutableRawPointer`
- Instance methods: Swift wrapper takes `self` as first param, calls method, returns result (or void)
- The bypass pattern is simpler for instance methods — no allocation/deallocation needed for the result when void

### Files to Modify

#### 1. `ExistentialBypassEmitter.cs` — Add `TryEmitMethodBypass`

**File**: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ExistentialBypassEmitter.cs`

**Current limitation**: Only has `TryEmitConstructorBypass` which requires `env.ParentDecl is StructDecl` (line 28).

**New method**: `TryEmitMethodBypass` — handles instance methods on both classes and structs.

```csharp
public static bool TryEmitMethodBypass(
    CSharpWriter csWriter,
    SwiftWriter swiftWriter,
    MethodEnvironment env,
    ILogger logger)
{
    // Must be a class or struct parent
    if (env.ParentDecl is not TypeDecl parentTypeDecl)
        return false;
    bool isClass = parentTypeDecl is ClassDecl;
    bool isStruct = parentTypeDecl is StructDecl;
    if (!isClass && !isStruct)
        return false;

    var methodDecl = env.MethodDecl;

    // Only handle instance methods (not constructors, not static)
    if (methodDecl.IsConstructor || methodDecl.MethodType == MethodType.Static)
        return false;

    // Only void return for now — non-void returns need result marshalling
    var returnType = methodDecl.CSSignature.First();
    if (returnType.SwiftTypeSpec != TupleTypeSpec.Empty)
        return false;

    // Throwing methods produce different Swift return shapes
    if (methodDecl.Throws)
        return false;

    // Classify params into existential vs passthrough
    var allArgs = methodDecl.CSSignature.Skip(1).ToList();
    var existentialArgs = new List<ArgumentDecl>();
    var passthroughArgs = new List<ArgumentDecl>();

    foreach (var arg in allArgs)
    {
        bool isExistentialBoundGeneric =
            env.BoundGenericsHandler.IsBoundGeneric(arg) &&
            (env.BoundGenericsHandler.TryGetFirstExistentialTypeArgument(arg.SwiftTypeSpec, out _) ||
             env.BoundGenericsHandler.TryGetFirstUnsupportedExistentialTypeArgument(arg.SwiftTypeSpec, out _));

        if (isExistentialBoundGeneric)
            existentialArgs.Add(arg);
        else
            passthroughArgs.Add(arg);
    }

    if (existentialArgs.Count == 0)
        return false;

    // ALL existential args must have HasDefaultArg
    if (existentialArgs.Any(a => !a.HasDefaultArg))
    {
        logger.LogDebug("ExistentialBypassEmitter: method bypass - not all existential params have defaults.");
        return false;
    }

    // Reject passthrough args with generic type params
    if (passthroughArgs.Any(a => a.IsGeneric))
    {
        logger.LogDebug("ExistentialBypassEmitter: method bypass - passthrough param is generic.");
        return false;
    }

    // Build reduced MethodDecl and check marshallability
    // ... (same pattern as constructor bypass: build reduced sig, check placeholder, check parity)

    // Emit Swift wrapper: calls self.method(passthrough args, omitting existential defaults)
    // Emit C# method: P/Invoke to wrapper, public method with passthrough params only
}
```

**Swift wrapper pattern for class instance method**:
```swift
@_silgen_name("SBW_MixpanelInstance_track_HASH")
public func SBW_MixpanelInstance_track_HASH(_ __self: MixpanelInstance, _ event: String?) {
    __self.track(event: event)
    // properties: omitted — Swift fills default (nil)
}
```

**C# pattern for class instance method**:
```csharp
public void Track(string? @event)
{
    SBW_MixpanelInstance_track_HASH(SwiftHandle, eventSwift);
}

[LibraryImport("SwiftBindings", EntryPoint = "SBW_MixpanelInstance_track_HASH")]
private static partial void SBW_MixpanelInstance_track_HASH(IntPtr self, /* passthrough params */);
```

**Self parameter handling**:
- Class: `IntPtr self` → Swift casts to typed class pointer
- Struct (non-frozen): `UnsafeMutableRawPointer` → typed load
- The `self` parameter uses the same pattern as existing async wrappers

#### 2. `MethodHandler.cs` — Wire bypass into BOTH existential skip paths

**File**: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs`

**Critical insight from Codex Finding #1**: MethodHandler has its own inline existential gates. The bypass must be wired into MethodHandler directly, NOT via MemberEmissionValidator.

**Two insertion points** (both in the non-accessor `foreach` loop over `methodEnv.MethodDecl.CSSignature`):

**Point A: Line 478-488** — `TryGetFirstUnsupportedExistentialTypeArgument` (hard block)

Instead of immediately returning, accumulate the existential arg and continue checking remaining args:
```csharp
// Before (line 478-488):
if (methodEnv.BoundGenericsHandler.TryGetFirstUnsupportedExistentialTypeArgument(argument.SwiftTypeSpec, out var existentialType))
{
    // ... skip immediately
    return;
}

// After: accumulate for bypass attempt
if (methodEnv.BoundGenericsHandler.TryGetFirstUnsupportedExistentialTypeArgument(argument.SwiftTypeSpec, out var existentialType))
{
    hasExistentialArg = true;
    firstExistentialType ??= existentialType.ToString();
    continue; // Check remaining args before deciding to skip
}
```

**Point B: Line 496-537** — `TryGetFirstExistentialTypeArgument` (B6 gate, supported existentials in non-Array/non-Optional)

Same accumulation pattern — when a Dictionary/Set existential is found and NOT array/optional, mark for bypass:
```csharp
if (!isArrayWithDirectExistentialElement && !isOptionalWithDirectExistentialElement)
{
    hasExistentialArg = true;
    firstExistentialType ??= supportedExistentialType.ToString();
    continue; // Don't return yet — try bypass after loop
}
```

**After the foreach loop**, add the bypass attempt (mirrors the constructor pattern at line 201-224):
```csharp
if (hasExistentialArg)
{
    if (ExistentialBypassEmitter.TryEmitMethodBypass(csWriter, swiftWriter, methodEnv, _logger))
    {
        ReportCollector.RecordMemberWrapped(
            BindingItemKind.Method,
            methodEnv.MethodDecl.Name,
            methodEnv.MethodDecl.MangledName,
            methodEnv.MethodDecl.ParentDecl,
            "ExistentialBypass",
            "Existential parameter(s) omitted; Swift defaults used.");
        return;
    }

    // Fallback: skip as before
    _logger.LogWarning($"Skipping method {methodEnv.MethodDecl.Name}: ...");
    ReportCollector.RecordMemberSkipped(...);
    return;
}
```

**Structural change**: The `foreach` loop currently returns immediately on existential detection. It needs to be refactored to **accumulate** existential status across all args, then decide after the loop. This is the same pattern used for the constructor path (lines 130-225) which already accumulates `hasExistentialArg`/`firstExistentialType`.

### Implementation Order for 3a

1. Add `TryEmitMethodBypass` to `ExistentialBypassEmitter.cs`
2. Refactor MethodHandler's non-constructor `foreach` loop to accumulate existential args instead of early-return
3. Wire bypass attempt after the loop
4. Unit tests for the new method bypass

### Estimated Complexity: 3a

Medium. The constructor bypass is a proven pattern. The main work is:
- Adapting Swift wrapper to call `self.method(...)` instead of constructing
- Handling `self` parameter correctly (class vs struct ABI)
- Refactoring MethodHandler loop from early-return to accumulate pattern
- ~200-300 lines of new code

---

## Sub-task 3b: Protocol Interface Method Recovery

### Problem Statement

55 methods are blocked by the B9 gate in **ProtocolHandler.cs** (line 301-313), NOT MemberEmissionValidator. The gate checks if any method parameter is an existential and skips it entirely from the protocol interface.

```csharp
// ProtocolHandler.cs:301-313
bool hasExistentialParam = methodDecl.CSSignature.Skip(1).Any(arg =>
    existentialHandlerB9.IsExistential(arg.SwiftTypeSpec) ||
    existentialHandlerB9.IsOptionalExistential(arg.SwiftTypeSpec));
if (hasExistentialParam)
{
    skippedMethodKeys.Add(methodKey);
    // ... skip
    continue;
}
```

This is too aggressive — it blocks methods from appearing in the protocol interface, which means concrete types implementing the protocol can't provide these methods. The proxy receiver can't marshal existential containers, but the interface itself should still declare the method.

### Design

Follow the **closure recovery pattern** (Q4b) already established in ProtocolHandler: emit the method in the interface for concrete type implementation, but give the proxy a `NotSupportedException` stub.

**Change**: Instead of `skippedMethodKeys.Add(methodKey); continue;`, fall through to emit in the interface and track the method for proxy stubbing:
```csharp
// ProtocolHandler.cs B9 gate — revised
bool hasExistentialParam = methodDecl.CSSignature.Skip(1).Any(arg =>
    existentialHandlerB9.IsExistential(arg.SwiftTypeSpec) ||
    existentialHandlerB9.IsOptionalExistential(arg.SwiftTypeSpec));
if (hasExistentialParam)
{
    skippedMethodKeys.Add(methodKey);
    _logger.LogDebug($"Method '{methodDecl.Name}' has existential param — proxy will use NotSupportedException stub.");
    // Fall through to emit in interface — concrete types can implement it
    // existentialSkippedMethodKeys populated below, after all remaining gates pass
}
```

This mirrors exactly how closure methods are handled (lines 315-330):
```csharp
// Existing closure pattern (for reference):
if (hasClosureParam)
{
    skippedMethodKeys.Add(methodKey);
    // Fall through to emit in interface — concrete types can implement it
}
```

**New tracking set**: `existentialSkippedMethodKeys` — populated after all gates pass (same pattern as `closureSkippedMethodKeys`). Used by ProtocolProxyEmitter to decide between `NotSupportedException` stub vs skip.

### Files to Modify

#### 1. `ProtocolHandler.cs` — Convert B9 from hard-skip to interface-recovery

**File**: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ProtocolHandler.cs`

Lines 301-313: Change from `continue` to fall-through with tracking.

Also need to mirror the same pattern for **existential properties** in the property loop if ProtocolHandler has an equivalent gate there.

#### 2. `ProtocolProxyEmitter.InterfaceImpl.cs` — Emit `NotSupportedException` stubs for existential methods

**File**: `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.InterfaceImpl.cs`

The stub-vs-dispatch decision is at line 68 (`_skippedMethodKeys.Contains(methodKey)`) and the existing `EmitNotSupportedMethodStub` is at line 762. The closure recovery pattern already established the exact code path:

```csharp
// InterfaceImpl.cs:68-78 (existing code, showing where existential stubs plug in)
if (_skippedMethodKeys.Contains(methodKey))
{
    // Closure-skipped methods → NotSupported stub
    if (_closureSkippedMethodKeys.Contains(methodKey))
    {
        // ... emit stub
    }
    // NEW: Existential-skipped methods → same pattern
    if (_existentialSkippedMethodKeys.Contains(methodKey))
    {
        var projectedKeySkipped = ProtocolSignatureHelper.GetProjectedCSharpMethodKey(method, _typeDatabase, protocolDecl);
        if (!emittedCSharpKeys.Add(projectedKeySkipped))
            continue;
        EmitNotSupportedMethodStub(writer, method);
    }
    continue;
}
```

`ProtocolProxyEmitter.Receivers.cs` handles receiver/witness plumbing and is NOT where interface method stubs are emitted.

### Estimated Complexity: 3b

Low-Medium. The pattern is established by Q4b closure recovery. The main work is:
- Adding the tracking set
- Wiring into proxy emission
- ~50-80 lines of changes

---

## Sub-task 3c: Skip Count Audit

After implementing 3a and 3b:

1. Regenerate all 32 validation library bindings
2. Run `validate-libraries.sh` → confirm 32/32 still pass
3. Collect binding reports and compare skip counts:
   - UnsupportedExistential: 447 → target <400 (conservative with scope narrowing)
   - Protocol receiver existential: 55 → ~0 (recovered to interface)
4. Categorize remaining skips by root cause

### Expected Outcome (Revised)

The original plan projected >30% UnsupportedExistential reduction. With properties explicitly deferred and no full dictionary bridge, the realistic impact is:

| Category | Before | After 3a | After 3a+3b |
|----------|:------:|:--------:|:-----------:|
| UnsupportedExistential (methods) | 447 | ~380-415 | ~380-415 |
| Protocol receiver existential (interface recovery) | 55 | 55 | ~0 |
| Net member recovery | — | ≤67 methods (upper bound) | ≤67 + ~55 interface |

**Important caveat on the ~67 estimate**: This is an upper bound assuming all default-arg existential methods have void return and non-throwing signatures. The 3a implementation restricts to void-return, non-throwing instance methods. Methods with non-void returns (e.g., returning `Self` or a bound generic) will still be skipped until the bypass is extended. The actual unlock count depends on how many of the ~67 candidates are void — to be measured during 3c audit.

The 3a bypass unlocks methods where ALL existential params have defaults — primarily Mixpanel (~15) and Stripe methods with `additionalAPIParameters` defaults (~52).

The 3b recovery doesn't reduce UnsupportedExistential count (different skip reason) but recovers ~55 protocol interface methods for concrete type implementation.

---

## File Modification Summary

### Files that MUST be modified

| File | Change | Scope |
|------|--------|-------|
| `ExistentialBypassEmitter.cs` | Add `TryEmitMethodBypass` for class/struct instance methods | New method ~150 lines |
| `MethodHandler.cs` (lines 431-540) | Refactor existential gates from early-return to accumulate; wire bypass after loop | Restructure ~50 lines |
| `ProtocolHandler.cs` (lines 301-313) | Convert B9 existential gate from hard-skip to interface-recovery | Modify ~15 lines |

### Files that MAY need modification

| File | Change | Condition |
|------|--------|-----------|
| `ProtocolProxyEmitter.InterfaceImpl.cs` (lines 68-78, 762) | Add `existentialSkippedMethodKeys` check alongside existing closure check; reuse `EmitNotSupportedMethodStub` | For 3b proxy completion |

### Files NOT modified (correcting original plan)

| File | Original plan said | Why not |
|------|-------------------|---------|
| `MemberEmissionValidator.cs` | "Relax existential gate" | MethodHandler has its own gates; MemberEmissionValidator is used by ProtocolConformanceValidator/IHandler, not MethodHandler |
| `PropertyHandler.cs` | "Mirror method gate changes" | Property existential support deferred — PropertyHandler has unconditional skip with no Array/Optional allowthrough |
| `TypeConversionHandler.cs` | "Add IsSwiftDictionary" | Already exists at line 82 |
| `TypeProjectionFactory.cs` | "Route dict+existential to new projection" | No DictionaryExistentialProjection in this session |

---

## Existential Predicate — Correct Guard

Every allowthrough predicate in this session must use the **full guard** matching the existing Optional<any P> pattern, NOT just `IsSupportedExistential()`:

```csharp
// CORRECT — blocks Any, blocks unknown protocols, blocks ObjC-filtered compositions
var protocolList = existentialHandler.ToProtocolListTypeSpec(innerTypeSpec);
bool isAllowed = protocolList != null &&
    existentialHandler.AllProtocolsHaveTypeRecords(protocolList) &&
    existentialHandler.GetPublicExistentialType(protocolList) != "object";
if (isAllowed && protocolList != null)
{
    var filteredCount = protocolList.Protocols.Keys
        .Count(p => !TypeDatabaseExtensions.IsObjCModuleType(p));
    if (filteredCount != protocolList.Protocols.Count)
        isAllowed = false;
}
```

```csharp
// WRONG — IsSupportedExistential returns true for Any (0 protocols)
bool isAllowed = existentialHandler.IsSupportedExistential(protocolList);
```

This addresses Codex Finding #4.

---

## Test Strategy

### Unit Tests

1. **ExistentialBypassEmitter tests**:
   - `TryEmitMethodBypass_ClassInstanceMethod_WithDefaultDictParam_EmitsSwiftWrapper`
   - `TryEmitMethodBypass_ClassInstanceMethod_RequiredExistentialParam_ReturnsFalse`
   - `TryEmitMethodBypass_StructInstanceMethod_WithDefaultParam_Works`
   - `TryEmitMethodBypass_StaticMethod_ReturnsFalse` (not supported)
   - `TryEmitMethodBypass_NonVoidReturn_ReturnsFalse` (deferred)

2. **ProtocolHandler tests** (if test infrastructure exists):
   - Verify existential-param method appears in interface
   - Verify proxy gets NotSupportedException stub

### Integration Tests

- Regenerate Mixpanel bindings → verify `Track(string? event)` is emitted (bypass version)
- Regenerate StripePayments → verify methods with default `additionalAPIParameters` emit

### Validation Gate

- `./run-tests.sh` → no regressions
- `./validate-libraries.sh` → 32/32 passing
- Count UnsupportedExistential skips → document reduction

---

## Suggested Implementation Order

### Step 1: Generalize ExistentialBypassEmitter (~150 lines)

1. Add `TryEmitMethodBypass` for instance methods
2. Handle `self` parameter (class: `IntPtr` → typed cast; struct: pointer load)
3. Handle void return (non-void deferred)
4. Reuse existing `RenderSwiftTypeSpec`, passthrough/existential classification
5. Unit tests

### Step 2: Refactor MethodHandler existential loop (~50 lines restructure)

1. Add `hasExistentialArg` / `firstExistentialType` tracking variables (matching constructor pattern)
2. Change line 478-488 (`TryGetFirstUnsupportedExistentialTypeArgument`) from `return` to `continue` with tracking
3. Change line 527-537 (B6 non-Array/non-Optional) from `return` to `continue` with tracking
4. After the foreach loop, add bypass attempt → fallback skip
5. Integration test: regenerate Mixpanel

### Step 3: Protocol interface recovery (3b) (~50 lines)

1. In ProtocolHandler.cs, convert B9 from `continue` to fall-through
2. Add `existentialSkippedMethodKeys` tracking set (passed to ProtocolProxyEmitter alongside existing `closureSkippedMethodKeys`)
3. In ProtocolProxyEmitter.InterfaceImpl.cs (line 68-78), add `existentialSkippedMethodKeys` check parallel to the existing closure check, reusing `EmitNotSupportedMethodStub`
4. Integration test: check protocol interface includes existential-param methods

### Step 4: Validate & audit (3c)

1. `./run-tests.sh` → verify no regressions
2. `./validate-libraries.sh` → confirm 32/32
3. Collect skip count data
4. Document findings

---

## Complexity & Risk Summary

| Sub-task | Complexity | Risk | Confidence |
|----------|:----------:|:----:|:----------:|
| 3a (method bypass generalization) | Medium | Low | High — proven pattern from constructors |
| 3b (protocol interface recovery) | Low-Medium | Low | High — proven pattern from Q4b closures |
| 3c (audit) | Low | Low | High |

**Session scope**: 3a + 3b + 3c. All fit within 1 session.

**What's deferred**:
- Full dictionary bridge (`DictionaryExistentialProjection`) → Session 10 or future
- PropertyHandler existential relaxation → follow-up session
- Non-void return bypass → follow-up
- `MemberEmissionValidator.CanEmitMethod` existential relaxation → only needed when ProtocolConformanceValidator needs to match the new MethodHandler behavior

---

## Appendix: Key Data Points

### Mixpanel `track(event:properties:)` ABI

```json
{
  "name": "track",
  "printedName": "track(event:properties:)",
  "children": [
    { "name": "Void" },
    { "name": "Optional", "printedName": "Swift.String?" },
    { "name": "Optional", "printedName": "[Swift.String : any Mixpanel.MixpanelType]?",
      "hasDefaultArg": true }
  ],
  "mangledName": "$s8Mixpanel0A8InstanceC5track5event10propertiesySSSg_SDySSAA0A4Type_pGSgtF"
}
```

The `properties` parameter has `hasDefaultArg: true` — bypass is possible.

### Gating architecture (corrected)

| Gate Location | What It Gates | Used By |
|---------------|---------------|---------|
| MethodHandler.cs:478-488 | Unsupported existentials in bound generics (methods) | MethodHandler directly (inline) |
| MethodHandler.cs:496-537 | Supported existentials in non-Array/non-Optional (B6) | MethodHandler directly (inline) |
| MethodHandler.cs:167-198 | Existentials in constructor bound generics | ConstructorHandler path in MethodHandler |
| PropertyHandler.cs:228-233 | Any existential in bound generic property | PropertyHandler directly (unconditional skip) |
| ProtocolHandler.cs:301-313 | Existential params in protocol methods (B9) | ProtocolHandler directly |
| MemberEmissionValidator.CanEmitMethod | Method validation for conformance/dedup | ProtocolConformanceValidator, IHandler |
| MemberEmissionValidator.CanEmitProperty | Property validation | ClassHandler, FrozenStructHandler, NonFrozenStructHandler, EnumHandler |

### Top existential skip patterns

| Existential Type | Count | Libraries | Session 3 Approach |
|-----------------|:-----:|-----------|-------------------|
| `Any` (bare, in Dict/Set) | 328 | StripePayments (276), StripeCore (21), Lottie (5) | 3a bypass (if default arg) |
| Protocol receiver gate | 55 | Various | 3b — interface recovery |
| `any MixpanelType` | 15 | Mixpanel | 3a bypass (all have defaults) |
| `any StripeUICore.Element` | 8 | StripeUICore | 3a bypass (if default arg) |
| `any Swift.Error` | 6 | Various | Already handled by well-known type |
| Other specific protocols | ~35 | Various | Case-by-case |
