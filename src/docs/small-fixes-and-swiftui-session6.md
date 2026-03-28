# Small Fixes + SwiftUI Session 6

**Date**: March 28, 2026
**Sessions**: 2
**Status**: Complete

---

## Context

The roadmap's "Small Fixes" section listed three items:

| Item | Status |
|------|--------|
| Async frozen struct params | **Already implemented** — guard at `WrapperEmitter.Marshalling.cs:592`, heap allocation at `WrapperEmitter.Async.cs:143-159`, 4 tests in `AsyncSwiftWrapperTests.cs` |
| `[String: Any]` dictionary projection | **Pending** — this doc, Session 1 |
| SwiftString.Buffer ABI (4+ string params) | **Deferred** — only 1 test case affected (`KeywordTest` with 4 string fields), no validation library hits this. High risk for low impact. |

The SwiftUI roadmap has one remaining session (Session 6: Observable Binding + Corpus Tracking).

This doc covers the two sessions of work:
1. **Session 1**: `[String: Any]` dictionary projection
2. **Session 2**: SwiftUI Session 6 (Observable Binding + Corpus Tracking)

---

## Session 1: `[String: Any]` Dictionary Projection — COMPLETE (`a4a9348`)

### Problem

Swift's `[String: Any]` pattern is common in real-world libraries (Alamofire HTTP parameters, Mixpanel event properties). Currently, bare `Any` (0-protocol existential) falls through to `"object"` in `GetPublicExistentialType`, which causes the `IsValidExistentialForContainer` gate at `BoundGenericsHandler.cs:220` to reject it. The dictionary resolves to `SwiftDictionary<string, AnyType>` — an opaque marker type that's unusable from C#.

### Goal

`[String: Any]` → `IDictionary<string, object>` (parameter) / `IReadOnlyDictionary<string, object>` (return). Values are boxed/unboxed through `ExistentialContainer0` at the ABI boundary.

### Current Code Flow

1. `TypeProjectionFactory.cs:134` — `NamedTypeSpec.IsAny` routes to `ProjectExistential()`
2. `ProjectExistential()` calls `ExistentialHandler.GetPublicExistentialType()` → returns `"object"` for bare `Any` (0 effective protocols, line 353-354)
3. `ProjectExistential()` calls `ExistentialHandler.GetCSharpExistentialType()` → returns `"Swift.Runtime.ExistentialContainer0"` for 0 non-marker protocols
4. Since `publicType == "object"` and no proxy class exists, `ExistentialProjection` is created with `proxyClassName = null`
5. **But** when `Any` appears inside a container (`Dictionary<String, Any>`), `BoundGenericsHandler.TranslateTypeArgument()` at line 607 checks `IsExistential()`, then at line 613 rejects it because `GetPublicExistentialType() == "object"` → falls back to `AnyType`

The issue is a **gate rejection**, not a missing projection. The `ExistentialProjection` already handles bare `Any` correctly when used standalone — the `"object"` public type and `ExistentialContainer0` P/Invoke type are right. The gate in `BoundGenericsHandler` just needs to let bare `Any` through when used as a container element.

### Deliverables

#### 1. Lift the bare-Any gate in BoundGenericsHandler

**File**: `src/Swift.Bindings/src/Marshaler/BoundGenericsHandler.cs`

**Change at line 213-229** (`IsValidExistentialForContainer`): Add a special case for bare `Any` (0 effective protocols). The existing `"object"` rejection makes sense for *unknown* protocols (where we can't generate a proxy), but bare `Any` is intentional — Swift explicitly declares it. Allow it through.

```csharp
// In IsValidExistentialForContainer():
// After the AllProtocolsHaveTypeRecords check, before the "object" rejection:
// Bare Any (0 protocols) is intentionally supported — it's not an unknown protocol,
// it's Swift's explicit "any value" type. ExistentialContainer0 is the correct ABI.
var effectiveProtocols = _existentialHandler.GetEffectiveProtocols(protocolList);
if (effectiveProtocols.Count == 0)
    return true;  // Bare Any — valid for containers
if (_existentialHandler.GetPublicExistentialType(protocolList) == "object")
    return false;  // Unknown protocol — still blocked
```

Also need to expose `GetEffectiveProtocols` as public (currently private) on `ExistentialHandler`, or add a `IsBareAny(ProtocolListTypeSpec)` helper method.

**Change at line 607-618** (`TranslateTypeArgument`): Same pattern — let bare `Any` through the "object" gate:

```csharp
if (_existentialHandler.IsExistential(typeSpec))
{
    var protocolList = _existentialHandler.ToProtocolListTypeSpec(typeSpec);
    if (protocolList != null &&
        _existentialHandler.IsSupportedExistential(protocolList) &&
        _existentialHandler.AllProtocolsHaveTypeRecords(protocolList))
    {
        var publicType = _existentialHandler.GetPublicExistentialType(protocolList);
        // Allow bare Any (publicType == "object" with 0 protocols) — it's intentional.
        // Block unknown protocols that resolve to "object" (can't generate proxy).
        if (publicType != "object" || _existentialHandler.IsBareAny(protocolList))
            return _existentialHandler.GetCSharpExistentialType(protocolList);
    }
    return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
}
```

#### 2. Add `IsBareAny` helper to ExistentialHandler

**File**: `src/Swift.Bindings/src/Marshaler/ExistentialHandler.cs`

```csharp
/// <summary>
/// Returns true if the protocol list represents bare 'Any' (0 effective protocols).
/// Bare Any is intentionally supported for container elements, unlike unknown protocols.
/// </summary>
public bool IsBareAny(ProtocolListTypeSpec protocolList)
{
    return GetEffectiveProtocols(protocolList).Count == 0;
}
```

Need to verify `GetEffectiveProtocols` is already callable (it's currently private — check access level; if private, make it internal or add the `IsBareAny` wrapper).

#### 3. Verify ExistentialProjection handles bare Any correctly

**File**: `src/Swift.Bindings/src/Marshaler/Projection/ExistentialProjection.cs`

The existing `ExistentialProjection` with `publicType = "object"` and `proxyClassName = null` should work:
- `GetParameterPlan()` line 47: falls through to `ISwiftExistentialConvertible<EC0>` cast — **this is the problem**. C# `object` doesn't implement `ISwiftExistentialConvertible`. Need a boxing path.
- `GetReturnPlan()` line 59: returns bare `resultName` for `"object"` — but `ExistentialContainer0` is a struct, not `object`. Need an unboxing path.
- `GetParameterElementConversion()` line 73: same issue — cast won't work on arbitrary `object`.
- `GetReturnElementConversion()` line 83: casts container to `(object)` — needs actual unboxing.

**This means we need a dedicated bare-Any path in ExistentialProjection**, or a separate `BareAnyProjection` class. The cleanest approach: add bare-Any handling directly in `ExistentialProjection` since the container type (`ExistentialContainer0`) and public type (`object`) are already correct — only the marshalling expressions need updating.

Add a `_isBareAny` flag to `ExistentialProjection`:

```csharp
public ExistentialProjection(string containerType, string publicType, string? proxyClassName, bool isBareAny = false)
```

Then in `GetParameterPlan()` / `GetParameterElementConversion()`:
```csharp
if (_isBareAny)
    return $"ExistentialContainer0.Box({paramName})";
```

And in `GetReturnPlan()` / `GetReturnElementConversion()`:
```csharp
if (_isBareAny)
    return $"ExistentialContainer0.Unbox({resultName})";
```

#### 4. Add `Box` and `Unbox` methods to ExistentialContainer0

**File**: `src/Swift.Runtime/src/Swift/Runtime/ExistentialContainer.cs` (or wherever `ExistentialContainer0` is defined)

Find the struct first — it likely has 3 payload words + 1 metadata pointer (no witness tables for 0 protocols).

```csharp
/// <summary>
/// Boxes a C# object into an ExistentialContainer0 for passing as Swift 'Any'.
/// Supports: string, primitive numeric types, bool, ISwiftObject (classes/structs).
/// </summary>
public static ExistentialContainer0 Box(object value)
{
    // Dispatch on type:
    // - string → SwiftString → store in payload words
    // - int/double/etc → store directly in payload words
    // - ISwiftObject → store handle in payload word 0, get metadata from type
    // Returns container with correct metadata pointer for each type.
}

/// <summary>
/// Unboxes an ExistentialContainer0 back to a C# object.
/// Uses the metadata pointer to determine the contained type.
/// </summary>
public static object Unbox(ExistentialContainer0 container)
{
    // Read metadata pointer → determine Swift type
    // Extract payload based on type
    // Return as appropriate C# object
}
```

**Important**: The boxing/unboxing only needs to handle the types we can actually project. For `[String: Any]` configs, the realistic values are: `String`, `Int`, `Double`, `Bool`, and possibly nested `[String: Any]` or `[Any]`. We don't need to handle arbitrary types — just the ones that cross our P/Invoke boundary.

**Practical scoping**: Start with `String`, `Int64`, `Double`, `Bool` boxing. These cover the vast majority of `[String: Any]` config patterns. Classes and nested containers can be added later if validation libraries need them.

#### 5. Wire bare-Any flag through TypeProjectionFactory

**File**: `src/Swift.Bindings/src/Marshaler/Projection/TypeProjectionFactory.cs`

In `ProjectExistential()` (line 415), pass the bare-Any flag:

```csharp
private ITypeProjection? ProjectExistential(ProtocolListTypeSpec protocolList, ProjectionContext context)
{
    var handler = new ExistentialHandler(context.TypeDatabase, context.CompositionCollector)
    { CurrentModuleName = context.CurrentModuleName };
    var containerType = handler.GetCSharpExistentialType(protocolList);
    var publicType = handler.GetPublicExistentialType(protocolList);
    bool isBareAny = handler.IsBareAny(protocolList);

    string? proxyClassName = null;
    if (!handler.TryGetWellKnownProtocolType(protocolList, out _) && publicType != "object")
        proxyClassName = handler.GetQualifiedProxyClassName(protocolList);

    return new ExistentialProjection(containerType, publicType, proxyClassName, isBareAny);
}
```

#### 6. Tests

**Unit tests** (in existing test files — find the BoundGenericsHandler test file and ExistentialProjection test file):

- `TranslateTypeArgument_DictionaryWithBareAny_ReturnsExistentialContainer0` — verify `Dictionary<String, Any>` resolves to `SwiftDictionary<string, ExistentialContainer0>` (not `AnyType`)
- `IsValidExistentialForContainer_BareAny_ReturnsTrue` — verify the gate allows bare Any
- `IsValidExistentialForContainer_UnknownProtocol_ReturnsFalse` — verify unknown protocols still blocked
- `ExistentialProjection_BareAny_ParameterPlan_UsesBox` — verify `ExistentialContainer0.Box()` expression
- `ExistentialProjection_BareAny_ReturnPlan_UsesUnbox` — verify `ExistentialContainer0.Unbox()` expression
- `DictionaryProjection_WithBareAnyValue_PublicType_IsIDictionaryStringObject` — verify end-to-end type

**Runtime tests** (in BindingTests):

- Add a Swift test type with a `[String: Any]` property or method parameter to `BindingTests/Sources/SwiftBindingsTestLib/`
- Add a runtime test that creates the dictionary in C#, passes it to Swift, and reads it back
- Validate string, int, double, bool values survive the round-trip

#### 7. Validation

After implementation:
- `./run-tests.sh` — all unit tests pass
- `./validate-libraries.sh` — verify Alamofire/Mixpanel dictionary patterns now compile (or at least don't regress)
- Check that the validation baseline improves or holds steady

### Files to Modify

| File | Change | Complexity |
|------|--------|-----------|
| `ExistentialHandler.cs` | Add `IsBareAny()` helper | Low |
| `BoundGenericsHandler.cs` | Lift bare-Any gate in 2 locations | Low |
| `ExistentialProjection.cs` | Add `_isBareAny` flag + Box/Unbox expressions | Medium |
| `TypeProjectionFactory.cs` | Pass `isBareAny` flag | Low |
| `ExistentialContainer.cs` (runtime) | Add `Box()`/`Unbox()` static methods | Medium |
| Test files (BoundGenericsHandler, ExistentialProjection) | 6+ unit tests | Medium |
| BindingTests Swift source + C# runtime test | Round-trip test | Medium |

### Risk Assessment

**Medium risk**. The gate change is surgical (2 locations), and the projection changes are additive (new flag, not modifying existing paths). The runtime `Box`/`Unbox` is the riskiest part — it touches the ABI boundary. Scope it to primitive types + String initially.

---

## Session 2: SwiftUI Session 6 — Observable Binding + Corpus Tracking

### Overview

Two independent sub-features:
1. **Observable Binding**: C# `INotifyPropertyChanged` → Swift `@Published` reactivity (C# property changes automatically update SwiftUI views)
2. **Corpus Tracking**: Measurement infrastructure for bridge coverage across real SwiftUI libraries

These are independent — corpus tracking doesn't depend on observable binding.

### Sub-Feature A: Observable Binding (C# → Swift Reactivity)

#### Current State

Session 4A established the State/Wrapper/Update pattern:
- `State` class: `ObservableObject` with `@Published` vars
- `Wrapper` view: `@ObservedObject var state` + closure `let` properties
- `Update{Param}` `@_cdecl` functions: C# calls → direct state property assignment → SwiftUI re-renders

Currently, updates are **imperative** — C# must explicitly call `session.UpdateFoo(newValue)` for each property change. Observable binding makes this **automatic** — a C# `INotifyPropertyChanged` object fires `PropertyChanged`, and the bridge auto-dispatches to the correct `Update{Param}` function.

#### Architecture

The observable binding is a **C#-side convenience layer** on top of the existing Update infrastructure. No Swift-side changes needed — the `@_cdecl` Update functions already exist.

```
C# ViewModel (INotifyPropertyChanged)
  │  PropertyChanged event fires
  ▼
C# Session.BindTo(viewModel) — subscribes to PropertyChanged
  │  Maps property name → Update{Param} method via reflection/delegate cache
  ▼
Existing Update{Param} P/Invoke
  │
  ▼
Swift @_cdecl Update function → state.{param} = newValue → SwiftUI re-renders
```

#### Deliverables

##### 1. Emit `BindTo<T>` method on Session class

**File**: `src/Swift.Bindings/src/Emitter/StringEmitter/SwiftUIBridgeEmitter.cs`

Add a new emission step after `EmitCSharpUpdateMethods()`. For each view with updatable params, emit:

```csharp
/// <summary>Binds a view model's properties to this session's updatable parameters.</summary>
/// <remarks>
/// Property names on the view model must match parameter names (case-insensitive).
/// Unmatched properties are silently ignored. Dispose the session to unsubscribe.
/// </remarks>
public void BindTo(System.ComponentModel.INotifyPropertyChanged viewModel)
{
    ObjectDisposedException.ThrowIf(_disposed, this);
    if (_boundViewModel != null)
        throw new InvalidOperationException("Already bound to a view model. Unbind first.");
    _boundViewModel = viewModel;
    _boundViewModel.PropertyChanged += OnBoundPropertyChanged;
}

public void Unbind()
{
    if (_boundViewModel != null)
    {
        _boundViewModel.PropertyChanged -= OnBoundPropertyChanged;
        _boundViewModel = null;
    }
}

private void OnBoundPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
{
    if (sender == null || e.PropertyName == null) return;
    // Switch on property name → call appropriate Update method
    // Use reflection to read the property value from sender
    switch (e.PropertyName.ToLowerInvariant())
    {
        case "count":
            var countProp = sender.GetType().GetProperty("Count");
            if (countProp != null) UpdateCount((int)countProp.GetValue(sender)!);
            break;
        case "label":
            var labelProp = sender.GetType().GetProperty("Label");
            if (labelProp != null) UpdateLabel((string)labelProp.GetValue(sender)!);
            break;
        // ... one case per updatable param
    }
}
```

**Optimization**: Instead of reflection on every property change, emit a one-time delegate cache:

```csharp
private Dictionary<string, Action<object>>? _propertyDispatchers;

private void EnsureDispatchers()
{
    _propertyDispatchers ??= new(StringComparer.OrdinalIgnoreCase)
    {
        ["Count"] = sender => UpdateCount((int)sender.GetType().GetProperty("Count")!.GetValue(sender)!),
        ["Label"] = sender => UpdateLabel((string)sender.GetType().GetProperty("Label")!.GetValue(sender)!),
    };
}
```

##### 2. Wire Unbind into Dispose/Free

The existing `Free()` or `Dispose()` method on the Session class must call `Unbind()` to prevent leaked event subscriptions. Find the existing dispose emission and add `Unbind()` before the native handle release.

##### 3. Emit IDisposable if not already

Check whether Session classes already implement `IDisposable`. If not, add it — the `BindTo` pattern requires deterministic cleanup.

##### 4. Tests

**Unit tests** (in `SwiftUIBridgeEmitterTests.cs`):
- `Session_WithUpdatableParams_EmitsBindToMethod` — verify BindTo method is emitted
- `Session_WithUpdatableParams_EmitsUnbindMethod` — verify Unbind is emitted
- `Session_WithUpdatableParams_EmitsPropertyChangedHandler` — verify switch dispatch
- `Session_ClosureOnlyView_DoesNotEmitBindTo` — verify non-updatable views skip it
- `Session_Dispose_CallsUnbind` — verify cleanup wiring

**Runtime tests** (in BindingTests):
- Create a simple C# class implementing `INotifyPropertyChanged`
- Bind it to `UpdatableCounterView` session
- Change a property on the VM → verify the state update reaches Swift
- Dispose session → verify no crash on subsequent VM property changes

### Sub-Feature B: Corpus Tracking

#### Goal

Automated measurement of SwiftUI bridge coverage across real libraries. Three-tier metrics: **Generated** → **Typechecked** → **Runtime-validated**.

#### Deliverables

##### 1. Add BridgeSummary to binding-report.json

**File**: `src/Swift.Bindings/src/Emitter/StringEmitter/SwiftUIBridgeEmitter.cs` (or wherever the report is emitted)

Find where `binding-report.json` is written. Add a `BridgeSummary` section:

```json
{
  "BridgeSummary": {
    "TotalViews": 7,
    "Generated": 4,
    "Typechecked": 4,
    "RuntimeValidated": 0,
    "Template": 2,
    "HintSkipped": 1,
    "GeneratedPercent": 57.1
  }
}
```

The emitter already tracks which views are generated vs template vs skipped — expose these counts in the report.

##### 2. Create `generate-bridge-coverage.sh` script

**File**: `BindingTests/generate-bridge-coverage.sh` (new)

For each library in a manifest:
1. Run the generator with `--xcframework`
2. Parse `binding-report.json` → extract `BridgeSummary`
3. Attempt `swiftc -typecheck` on generated bridge Swift file → record pass/fail
4. Aggregate into `bridge-corpus/coverage-report.json`

Libraries to track (from swiftui-roadmap.md Session 6):
- BlinkIDUX, Lottie, AlertToast, ConfettiSwiftUI (already in validation)
- SDWebImageSwiftUI, SwiftUICharts, Kingfisher (need bridge-specific tracking)

##### 3. Create corpus manifest

**File**: `BindingTests/bridge-corpus/manifest.json` (new)

```json
{
  "libraries": [
    {
      "name": "BridgeParamTest",
      "source": "BindingTests",
      "views": 19,
      "runtime_validated": true
    },
    {
      "name": "Lottie",
      "source": "validation",
      "views": 3
    }
  ]
}
```

##### 4. Tests

- Unit test: Verify `BridgeSummary` is populated in report JSON for a module with SwiftUI views
- Unit test: Verify `BridgeSummary` is absent/empty for a module with no views
- Integration: Run `generate-bridge-coverage.sh` on BindingTests library, verify output format

### Files to Modify/Create

| File | Change | Complexity |
|------|--------|-----------|
| `SwiftUIBridgeEmitter.cs` | Emit `BindTo`/`Unbind`/handler + BridgeSummary | High |
| `SwiftUIBridgeEmitterTests.cs` | 5+ unit tests for observable binding + 2 for corpus | Medium |
| BindingTests runtime tests | Observable binding round-trip test | Medium |
| Report emission (find file) | Add BridgeSummary to binding-report.json | Low |
| `generate-bridge-coverage.sh` (new) | Corpus tracking script | Medium |
| `bridge-corpus/manifest.json` (new) | Library manifest | Low |

### Risk Assessment

**Medium risk**. Observable binding is purely additive C# code on top of existing Update infrastructure — no Swift changes, no ABI changes. The main risk is getting the property name matching and type coercion right in the emitted switch statement. Corpus tracking is standalone tooling with no production code risk.

---

## Session Order & Dependencies

```
Session 1: [String: Any] Dictionary Projection
  │  Independent — no dependency on Session 2
  │
Session 2: SwiftUI Session 6 (Observable Binding + Corpus Tracking)
  │  Independent — no dependency on Session 1
```

Sessions are independent and could theoretically run in parallel, but sequential execution is safer for validation baseline management.

---

## Post-Completion

After both sessions:
1. Update `src/docs/roadmap.md`:
   - Move `[String: Any]` from Small Fixes to completed (or remove)
   - Note async frozen struct params already done
   - Mark SwiftUI Session 6 complete in `src/docs/swiftui-roadmap.md`
2. Run final validation gates per CLAUDE.md
3. The roadmap's "Pending Work" section will contain only SwiftString.Buffer ABI (deferred, low impact)
