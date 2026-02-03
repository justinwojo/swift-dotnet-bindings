# Codex Task Specifications - Phase 40+

Task specifications for the next phase of binding generator improvements. Tasks are ordered by priority and designed to be worked independently unless noted.

**Date**: February 2026
**Starting Point**: Phase 39 complete, 1009 unit tests passing
**Libraries**: Nuke (0 errors), BlinkID (0 errors), Lottie (11 errors)

---

## Status Summary

| Task | Description | Status | Priority |
|------|-------------|--------|----------|
| 1 | Skip Members with Unsatisfied Constraints | ✅ **COMPLETED** | P0 |
| 2 | Protocol Proxy Return Type Alignment | Not Started | P1 |
| 3 | Protocol Conformance Emission | Not Started | P2 |
| 4 | Namespace Mapping Configuration | Not Started | P2 |

**Target**: Lottie 0 errors (currently 1 - CS0738)

---

## Task 1: Skip Members with Unsatisfied Constraints (CS0311)

### Status: ✅ COMPLETED (February 2026)
### Priority: P0 (Critical - blocks Lottie clean compile)
### Effort: Medium (4-6 hours)
### Dependencies: None

### Completion Notes

**Implemented by**: Codex
**Unit Tests**: 1018 passed (up from 1009)

**Files Modified**:
- `src/Swift.Bindings/src/Marshaler/BoundGenericsHandler.cs` - Added `TryGetFirstUnsatisfiedConstraint()` with cross-module support
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` - Constraint checks for method arguments
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PropertyHandler.cs` - Preflight checks for accessor return types and parameters, fixed accessor context mismatch
- `src/Swift.Bindings/src/Reporting/BindingReport.cs` - Added `UnsatisfiedGenericConstraint` skip reason

**Key fixes**:
1. Constraint checking for local module types (LottieVector3D, etc.)
2. Fail-closed for external types (SwiftArray<double>, etc.)
3. Property preflight now sets `IsAccessor = true` and injects `PInvokeHelperContext` to match emit behavior
4. Return type constraint checking for getters (not just arguments)

**Results**: CS0311 errors eliminated in all test libraries

### Problem Statement

Members are emitted that use generic types with constraints that cannot be satisfied by the argument types. For example:

```csharp
// ValueProviderStorage<T0> where T0 : ISwiftAnyInterpolatable
// But LottieVector3D doesn't implement ISwiftAnyInterpolatable
public ValueProviderStorage<LottieVector3D> storage { get; }  // CS0311!
```

The generator emits members with bound generic types without verifying that the type arguments satisfy the generic constraints.

### Root Cause

When emitting a member that returns `ValueProviderStorage<LottieVector3D>`, the generator:
1. Resolves `ValueProviderStorage<T>` and sees it has constraint `T : AnyInterpolatable`
2. Resolves `LottieVector3D` as the type argument
3. Emits the member without checking if `LottieVector3D` conforms to `AnyInterpolatable`

### Files to Investigate

1. `src/Swift.Bindings/src/Marshaler/Conductor.cs` - Where member marshalling decisions are made
2. `src/Swift.Bindings/src/TypeDatabase/TypeDatabase.cs` - Type and conformance lookup
3. `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` - Member emission
4. `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PropertyHandler.cs` - Property emission

### Implementation Approach

**Option A: Skip at marshalling stage** (Recommended)
1. In `Conductor.cs`, when processing a member with bound generic return/param types:
   - Extract the generic type definition and its constraints
   - For each constraint, check if the type argument conforms to the protocol
   - If not, mark member as `UnsupportedSignature` with reason `UnsatisfiedConstraint`

**Option B: Skip at emission stage**
1. In type handlers, before emitting a member:
   - Check bound generic types for constraint satisfaction
   - Skip emission if constraints aren't met

### Implementation Steps

1. **Add constraint checking utility**:
   ```csharp
   // In TypeDatabase or new ConstraintChecker.cs
   public bool SatisfiesConstraint(TypeDecl typeArg, TypeDecl constraint)
   {
       // Check if typeArg conforms to constraint protocol
       // Use conformance information from ABI
   }
   ```

2. **Integrate into bound generic handling**:
   - When a `BoundGenericType` is encountered, validate each type argument against its constraint
   - If validation fails, skip the member with appropriate reason

3. **Add skip reason**:
   - Add `UnsatisfiedGenericConstraint` to the skip reasons enum
   - Include in binding report with details of which constraint failed

4. **Add `[UnsupportedSwiftType]` attribute**:
   - Mark skipped members with the reason for discoverability

### Acceptance Criteria

- [ ] Regenerate Lottie bindings: `cd BindingTesting/Lottie && ./regenerate-bindings.sh`
- [ ] CS0311 errors eliminated: `dotnet build ... 2>&1 | grep "CS0311"` returns empty
- [ ] Skipped members appear in `binding-report.json` with `UnsatisfiedGenericConstraint` reason
- [ ] All unit tests pass: `./run-tests.sh`
- [ ] Nuke and BlinkID still compile clean

### Test Validation

```bash
./run-tests.sh
cd BindingTesting/Lottie && ./regenerate-bindings.sh
dotnet build LottieTestApp/LottieTestApp.csproj 2>&1 | grep -c "error CS0311"
# Expected: 0
```

---

## Task 2: Protocol Proxy Return Type Alignment (CS0738)

### Status: Not Started
### Priority: P1 (High)
### Effort: Low (2-3 hours)
### Dependencies: None

### Problem Statement

Protocol proxy classes have properties with return types that don't match the interface:

```csharp
interface ISwiftAnyValueProvider {
    AnyType valueType { get; }  // Interface expects AnyType
}

class AnyValueProviderProxy : ISwiftAnyValueProvider {
    public SomeOtherType valueType { get; }  // CS0738!
}
```

### Root Cause

The proxy emitter and interface emitter use different logic to determine property return types. The interface uses the Swift protocol's declared type, but the proxy may resolve it differently.

### Files to Modify

1. `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolEmitter.cs` - Interface emission
2. `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.cs` - Proxy emission

### Implementation Steps

1. **Identify the mismatch**:
   - Check how `valueType` is emitted in `ISwiftAnyValueProvider`
   - Check how it's emitted in `AnyValueProviderProxy`
   - Determine which is correct (likely the interface)

2. **Align proxy to interface**:
   - Ensure proxy property types exactly match interface declarations
   - May need to share type resolution logic between emitters

3. **Add test case**:
   - Unit test that verifies proxy properties match interface types

### Acceptance Criteria

- [ ] CS0738 error eliminated in Lottie
- [ ] Proxy class compiles and implements interface correctly
- [ ] Unit tests pass

### Test Validation

```bash
cd BindingTesting/Lottie && ./regenerate-bindings.sh
dotnet build LottieTestApp/LottieTestApp.csproj 2>&1 | grep -c "error CS0738"
# Expected: 0
```

---

## Task 3: Protocol Conformance Emission

### Status: Not Started
### Priority: P2 (Medium - enhances API but not blocking)
### Effort: High (8-12 hours)
### Dependencies: Task 1 provides interim fix; this is the proper solution

### Problem Statement

When a Swift type conforms to a protocol, the C# projection should implement the corresponding interface. Currently:

```swift
// Swift
struct LottieVector3D: AnyInterpolatable { ... }
```

```csharp
// Generated (current)
public struct LottieVector3D : ISwiftObject { ... }

// Should be
public struct LottieVector3D : ISwiftObject, ISwiftAnyInterpolatable { ... }
```

### Root Cause

The emitter doesn't process protocol conformances from the Swift ABI and emit interface implementations.

### Files to Investigate

1. `src/Swift.Bindings/src/Parser/SwiftABIParser.cs` - Conformance parsing
2. `src/Swift.Bindings/src/Model/TypeDecl.cs` - Type model (does it store conformances?)
3. `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/*Handler.cs` - Type emission

### Implementation Steps

1. **Verify conformance data is parsed**:
   - Check if `TypeDecl` has conformance information from ABI
   - If not, enhance parser to extract conformances

2. **Map conformances to interfaces**:
   - For each conformance, look up the corresponding `ISwift*` interface
   - Verify the interface exists in the current module or imports

3. **Emit interface implementations**:
   - Add interface to type's inheritance list
   - Ensure all interface members are implemented (may already be via protocol method emission)

4. **Handle cross-module conformances**:
   - Type in module A conforming to protocol in module B

### Acceptance Criteria

- [ ] `LottieVector3D` implements `ISwiftAnyInterpolatable`
- [ ] Generic constraints are now satisfiable
- [ ] All tests pass

### Notes

This is the "proper" fix for CS0311 but is more complex. Task 1 provides an interim solution by skipping problematic members. This task adds the conformance emission that would make those members valid.

---

## Task 4: Namespace Mapping Configuration

### Status: Not Started
### Priority: P2 (Medium - DX improvement before stable release)
### Effort: Medium (4-6 hours)
### Dependencies: None

### Problem Statement

The current namespace mapping uses a hardcoded `Swift.{Module}` pattern:

```csharp
namespace Swift.Lottie { ... }
namespace Swift.StoreKit { ... }
```

This pattern needs to be:
1. Documented as the default
2. Configurable via CLI or config file
3. Consistent before shipping stable packages (breaking change risk)

### Implementation Steps

1. **Add configuration option**:
   - CLI flag: `--namespace-pattern "{Module}Bindings"` or similar
   - Config file option in a `.swiftbindings.json` or similar

2. **Implement pattern substitution**:
   - `{Module}` → Swift module name
   - `{Framework}` → Framework name if different
   - Default: `Swift.{Module}`

3. **Document the scheme**:
   - Add to README or user docs
   - Explain implications of changing after release

### Files to Modify

1. `src/Swift.Bindings/src/Program.cs` - CLI argument parsing
2. `src/Swift.Bindings/src/Parser/ModuleProcessor.cs` - Namespace registration
3. `src/Swift.Bindings/src/Emitter/StringEmitter/ModuleEmitter.cs` - Namespace emission

### Acceptance Criteria

- [ ] Default behavior unchanged (`Swift.{Module}`)
- [ ] CLI flag allows override
- [ ] Config file option available
- [ ] Unit tests for pattern substitution

---

## Future Tasks (Not Yet Specified)

These are identified gaps but not yet detailed:

| Feature | Notes |
|---------|-------|
| Property setters | Verify current state; may be partially done |
| Async properties | Properties with async getters |
| Actors | Swift actor type support |
| Unbound generic types | Generic type definitions (not just bound) |
| TypeGraph layer | Architectural improvement from emitter redesign |

---

## Testing Commands Reference

```bash
# Run all unit tests
./run-tests.sh

# Regenerate and test Lottie
cd BindingTesting/Lottie
./regenerate-bindings.sh
dotnet build LottieTestApp/LottieTestApp.csproj

# Regenerate and test Nuke (with runtime validation)
cd BindingTesting/Nuke
./build-all.sh && ./validate-sim.sh 15

# Count errors by type
dotnet build ... 2>&1 | grep "error CS" | sed 's/.*error \(CS[0-9]*\).*/\1/' | sort | uniq -c
```
